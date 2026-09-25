using UnityEngine;
using UnityEngine.UIElements;
using Muses.Stage;
using Muses.TouchInput;
using Muses.Gameplay;

namespace Muses.Overlay
{
    /// <summary>
    /// 判定帯のスクリーン空間オーバーレイ。移植元: web-prototype/src/overlay.ts。
    ///
    /// 設計メモの帰結どおり、判定帯はワールド空間のポリゴンではなくスクリーン空間で描く。
    /// 図形は UI Toolkit の generateVisualContent + Painter2D、HUD・セル番号・ラベル・タッチデバッグの
    /// 文字は同じ UIDocument 上の Label で描く（テキストは Painter2D で描けないため。旧 OnGUI、perf-r1.md §6）。
    ///
    /// 簡略化した点（TS版との差分）:
    /// - 地平線の破線は実線で近似している。
    /// - 判定帯背景の3色グラデーションは、判定線を境に上下2枚の単色矩形で近似している。
    /// - 判定線のシャドウブラー（発光っぽい見た目）は省略している。
    ///
    /// 描画方式の変遷: 当初 OnRenderObject → RenderPipelineManager.endCameraRendering フックの
    /// GL immediate mode で実装していたが、実機（iPad, Metal）ビルドで判定線が描画されないことが
    /// 判明した（Unity Editor Play では問題なかった）。Metalではカメラの最終出力がバックバッファへ
    /// blitされるタイミングが Editor と異なり、endCameraRendering 時点のGL描画がその後のblitで
    /// 上書きされていたと考えられる。UI Toolkit の generateVisualContent は ChartEditorApp の
    /// スタンドアロンビルドで実機動作が既に実証済みの方式のため、これに統一した。
    /// </summary>
    public class StageOverlay : MonoBehaviour
    {
        [SerializeField] private StageController stageController;
        [SerializeField] private TouchInputManager input;
        [SerializeField] private bool showHud = true;

        /// <summary>実行時生成(ScriptableObject.CreateInstance)は既定シェーダ/テーマの参照が
        /// 埋まらずプレイヤービルドで描画できない(ipad-build-issues-r1.md ①)。
        /// 必ずアセット(Assets/UI/Game/GameOverlayPanelSettings.asset)をInspectorで配線すること。</summary>
        [SerializeField] private PanelSettings panelSettingsAsset;

        /// <summary>Judge はプレーンなC#クラス（MonoBehaviourではない）なので Inspector には出せない。
        /// GameController が生成後にコードから設定する。</summary>
        public Judge Judge { get; set; }

        private UIDocument uiDocument;
        private VisualElement overlayRoot;
        private float hudSongTime;
        private float hudFps;
        /// <summary>perf-r1.md §12の切り分け用。SongClock.AudioScheduleErrorSec を ms 単位で均した値。</summary>
        private float hudAudioErrorMs;

        // Update()の毎フレームRemoveAllで使う述語。ラムダをフィールドに固定して
        // 「thisだけをキャプチャする閉包」にすることで、毎フレームのデリゲート確保を避ける。
        private float cleanupNow;
        private System.Predicate<HitFlash> flashExpired;
        private System.Predicate<(Layer layer, int cell, float born)> rippleExpired;

        // 前回 MarkDirtyRepaint した時点の描画内容の要約（perf-r1.md §5【E】）。
        // 変わっていなければ再生成しない（何も押していない静止フレームでは描画コスト0）。
        private int lastStageVersion = -1;
        private ulong lastOccupiedMask;
        private int lastFlashCount = -1;
        private int lastRippleCount = -1;
        private float lastAnimTime = float.NaN;

        /// <summary>main.ts の frame() 内 HUD 更新相当。GameController が毎フレーム呼ぶ。</summary>
        public void SetHudTime(float songTime, float fps, float audioErrorMs = 0f)
        {
            hudSongTime = songTime;
            hudFps = fps;
            hudAudioErrorMs = audioErrorMs;
        }

        private void Awake()
        {
            uiDocument = gameObject.AddComponent<UIDocument>();
            uiDocument.panelSettings = panelSettingsAsset;
            uiDocument.rootVisualElement.pickingMode = PickingMode.Ignore;

            overlayRoot = new VisualElement { pickingMode = PickingMode.Ignore };
            overlayRoot.style.position = Position.Absolute;
            overlayRoot.style.left = 0;
            overlayRoot.style.top = 0;
            overlayRoot.style.right = 0;
            overlayRoot.style.bottom = 0;
            overlayRoot.generateVisualContent += GenerateOverlay;
            // 画面サイズ変化で座標が変わる。NeedsRepaint() の要約には含めないので、ここで明示的に描き直す。
            overlayRoot.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                overlayRoot.MarkDirtyRepaint();
                staticLabelsDirty = true;
            });
            uiDocument.rootVisualElement.Add(overlayRoot);

            // 文字は overlayRoot の子に置く（座標系を GenerateOverlay と共有するため）。
            staticLabelsRoot = NewLayer();
            touchLabelsRoot = NewLayer();
            if (showHud) BuildHud();

            // cleanupNow（thisのフィールド）だけをキャプチャする閉包として1回だけ生成する。
            flashExpired = f => cleanupNow - f.born < 0f || cleanupNow - f.born >= 0.45f;
            rippleExpired = r => cleanupNow - r.born < 0f || cleanupNow - r.born >= 0.3f;
        }

        private void Update()
        {
            // GL版で毎フレーム行っていたクリーンアップ（描画本体からは分離し、副作用を1箇所にまとめる）。
            //
            // 時刻の基準は songTime（SetHudTimeで毎フレーム受け取る値）でなければならない:
            // Judge.Flashes.born も TouchInputManager.Ripples.born も songTime で記録される
            // （Judge.CommitJudgement / TouchInputManager.Emit、いずれも clock.SongTime 由来）。
            // cbf9c70 で clock.Start() が「シーン開始時」から「タイトル画面のSTART押下時」へ
            // 移ったため、Time.time と songTime が「タイトル画面に居た時間」だけ乖離するようになり、
            // now - born が常に 0.45 を超えて**判定演出・リップルが一切描画されなくなっていた**
            // （それ以前はどちらもほぼ0始まりだったので偶然一致していた）。
            cleanupNow = hudSongTime;
            if (Judge != null) Judge.Flashes.RemoveAll(flashExpired);
            if (input != null) input.Ripples.RemoveAll(rippleExpired);
            if (NeedsRepaint()) overlayRoot.MarkDirtyRepaint();

            UpdateStaticLabels();
            UpdateTouchLabels();
            if (showHud) UpdateHud();
        }

        /// <summary>
        /// 描画内容が前回から変わりうるかを判定し、変わるなら要約を更新して true を返す。
        /// GenerateOverlay の出力を決めるのは (ステージ形状, 占有セル, フラッシュ, リップル, 経過時刻) だけで、
        /// フラッシュ/リップルが1つも無ければ経過時刻には依存しない。
        /// </summary>
        private bool NeedsRepaint()
        {
            if (stageController == null || input == null) return false;
            var cfg = stageController.Config;

            // タッチデバッグ表示は接触点の座標(u,v)に追従するので要約できない。デバッグ用途なので毎フレーム描く。
            if (cfg.showTouchDebug && input.Contacts.Count > 0) return true;

            int stageVersion = stageController.Version;
            ulong mask = OccupiedMask(cfg.cells, out bool maskValid);
            int flashCount = Judge != null ? Judge.Flashes.Count : 0;
            int rippleCount = input.Ripples.Count;
            // アニメーション中のものが無ければ時刻は描画に効かないので、要約上は固定値にする
            float animTime = flashCount > 0 || rippleCount > 0 ? hudSongTime : 0f;

            bool changed = !maskValid
                || stageVersion != lastStageVersion
                || mask != lastOccupiedMask
                || flashCount != lastFlashCount
                || rippleCount != lastRippleCount
                || !animTime.Equals(lastAnimTime);
            if (!changed) return false;

            lastStageVersion = stageVersion;
            lastOccupiedMask = mask;
            lastFlashCount = flashCount;
            lastRippleCount = rippleCount;
            lastAnimTime = animTime;
            return true;
        }

        /// <summary>占有セルを Ground=下位32bit / Sky=上位32bit のビットマスクにする。
        /// 1層32セルを超える設定では要約できないので valid=false（＝毎フレーム描き直す従来動作）。</summary>
        private ulong OccupiedMask(int cells, out bool valid)
        {
            valid = cells <= 32;
            if (!valid) return 0;
            ulong mask = 0;
            for (int k = 0; k < cells; k++)
            {
                if (input.IsOccupied(Layer.Ground, k)) mask |= 1UL << k;
                if (input.IsOccupied(Layer.Sky, k)) mask |= 1UL << (32 + k);
            }
            return mask;
        }

        // ================= UI Toolkit / Painter2D 描画（旧 GL immediate mode 相当） =================

        private static float OvX(float w, float u) => (u + 1f) / 2f * w;
        private static float OvY(float h, float v) => (1f - v) / 2f * h; // UI Toolkitはy下向き

        private static void FillRectP(Painter2D p, Rect r, Color c)
        {
            p.fillColor = c;
            p.BeginPath();
            p.MoveTo(new Vector2(r.xMin, r.yMin));
            p.LineTo(new Vector2(r.xMax, r.yMin));
            p.LineTo(new Vector2(r.xMax, r.yMax));
            p.LineTo(new Vector2(r.xMin, r.yMax));
            p.ClosePath();
            p.Fill();
        }

        // ChartEditorApp.cs の FillRect/FillLine と同じ「塗りつぶしパスのみ」方式に揃える
        // （Painter2D.Stroke()系はこのプロジェクトのどのビルドでも実機検証済みの実績が無いため、
        // 判定線の描画を確実に直すこの変更では使わない）。

        private static void FillLineP(Painter2D p, Vector2 a, Vector2 b, Color c, float thickness)
        {
            var d = b - a;
            float len = d.magnitude;
            if (len < 0.0001f) return;
            var n = new Vector2(-d.y, d.x) / len * (thickness * 0.5f);
            p.fillColor = c;
            p.BeginPath();
            p.MoveTo(a + n);
            p.LineTo(b + n);
            p.LineTo(b - n);
            p.LineTo(a - n);
            p.ClosePath();
            p.Fill();
        }

        private static void FillRectOutlineP(Painter2D p, Rect r, Color c, float t = 2f)
        {
            FillRectP(p, new Rect(r.x, r.y, r.width, t), c);
            FillRectP(p, new Rect(r.x, r.yMax - t, r.width, t), c);
            FillRectP(p, new Rect(r.x, r.y, t, r.height), c);
            FillRectP(p, new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        private static void FillCircleOutlineP(Painter2D p, Vector2 center, float r, Color c, float thickness = 2f)
        {
            const int seg = 24;
            for (int i = 0; i < seg; i++)
            {
                float a0 = i / (float)seg * Mathf.PI * 2f;
                float a1 = (i + 1) / (float)seg * Mathf.PI * 2f;
                var p0 = center + new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)) * r;
                var p1 = center + new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)) * r;
                FillLineP(p, p0, p1, c, thickness);
            }
        }

        private void GenerateOverlay(MeshGenerationContext mgc)
        {
            if (stageController == null || input == null) return;
            float w = overlayRoot.contentRect.width, h = overlayRoot.contentRect.height;
            if (w < 2f || h < 2f) return;

            var cfg = stageController.Config;
            var d = stageController.Derived;
            float now = hudSongTime; // Update()のcleanupNowと同じ理由でsongTime基準（born と単位を揃える）
            var p = mgc.painter2D;

            float PxX(float u) => OvX(w, u);
            float PxY(float v) => OvY(h, v);
            float CellU(float cellIdx) => -cfg.U + 2f * cfg.U * cellIdx / cfg.cells;

            if (cfg.showHorizon && d.vHorizon <= 1f)
                FillLineP(p, new Vector2(PxX(-1f), PxY(d.vHorizon)), new Vector2(PxX(1f), PxY(d.vHorizon)),
                    new Color(120 / 255f, 150 / 255f, 220 / 255f, 0.35f), 1f);

            DrawBand(p, cfg, Layer.Sky, cfg.vSkyTop, cfg.vSkyBot, cfg.vSkyJudge,
                StageGeometry.ColorFromHex(StageColors.Sky), new Color(255 / 255f, 62 / 255f, 165 / 255f), now, PxX, PxY, CellU);
            DrawBand(p, cfg, Layer.Ground, cfg.vGroundTop, cfg.vGroundBot, cfg.vGroundJudge,
                StageGeometry.ColorFromHex(StageColors.Ground), new Color(139 / 255f, 92 / 255f, 246 / 255f), now, PxX, PxY, CellU);

            if (cfg.showSplitLine)
                FillLineP(p, new Vector2(PxX(-1f), PxY(cfg.vSplit)), new Vector2(PxX(1f), PxY(cfg.vSplit)),
                    new Color(220 / 255f, 220 / 255f, 255 / 255f, 0.30f), 1f);

            if (cfg.showTouchDebug)
            {
                foreach (var t in input.Contacts.Values)
                {
                    var c = t.layer == Layer.Sky
                        ? StageGeometry.ColorFromHex(StageColors.Sky)
                        : StageGeometry.ColorFromHex(StageColors.Ground);
                    FillCircleOutlineP(p, new Vector2(PxX(t.u), PxY(t.v)), 26f, c, 1f);
                }
            }

            if (Judge != null)
            {
                foreach (var f in Judge.Flashes)
                {
                    float k = Mathf.Clamp01((now - f.born) / 0.45f);
                    float vJ = f.layer == Layer.Sky ? cfg.vSkyJudge : cfg.vGroundJudge;
                    float x0 = PxX(CellU(f.cell));
                    float x1 = PxX(CellU(f.cell + f.width));
                    float y = PxY(vJ);
                    float r = 6f + 26f * k;
                    Color col = f.kind switch
                    {
                        JudgeKind.PerfectPlus => Color.white,
                        JudgeKind.Perfect => new Color(220 / 255f, 230 / 255f, 255 / 255f),
                        JudgeKind.Good => new Color(120 / 255f, 220 / 255f, 255 / 255f),
                        _ => new Color(1f, 80 / 255f, 80 / 255f), // Miss
                    };
                    col.a = (1f - k) * 0.55f;
                    FillRectP(p, Rect.MinMaxRect(x0 + 2f, y - r / 2f, x1 - 2f, y + r / 2f), col);
                }
            }
        }

        private delegate float PxFunc(float v);
        private delegate float CellFunc(float cellIdx);

        private void DrawBand(Painter2D p, StageConfig cfg, Layer layer, float vTop, float vBot, float vJudge,
            Color css, Color rgb, float now, PxFunc PxX, PxFunc PxY, CellFunc CellU)
        {
            float yT = PxY(vTop), yB = PxY(vBot), yJ = PxY(vJudge);
            float xL = PxX(-cfg.U), xR = PxX(cfg.U);

            if (cfg.showBand)
            {
                // TS版は3ストップのグラデーションだが、判定線を境にした上下2枚の単色矩形で近似する
                var top = rgb; top.a = 0.02f;
                var mid = rgb; mid.a = 0.13f;
                FillRectP(p, Rect.MinMaxRect(xL, Mathf.Min(yT, yJ), xR, Mathf.Max(yT, yJ)), top);
                FillRectP(p, Rect.MinMaxRect(xL, Mathf.Min(yJ, yB), xR, Mathf.Max(yJ, yB)), mid);
            }

            // アクティブセルのハイライト（帯を消していても押した位置は出す）
            for (int k = 0; k < cfg.cells; k++)
            {
                if (!input.IsOccupied(layer, k)) continue;
                float a = PxX(CellU(k));
                float bx = PxX(CellU(k + 1));
                var c = rgb; c.a = 0.30f;
                FillRectP(p, Rect.MinMaxRect(a, Mathf.Min(yT, yB), bx, Mathf.Max(yT, yB)), c);
            }

            if (cfg.showBand)
            {
                var c = rgb; c.a = 0.38f;
                for (int k = 0; k <= cfg.cells; k++)
                {
                    float x = PxX(CellU(k));
                    FillLineP(p, new Vector2(x, yT), new Vector2(x, yB), c, 1f);
                }
                var edge = rgb; edge.a = 0.5f;
                FillLineP(p, new Vector2(xL, yT), new Vector2(xR, yT), edge, 1f);
                FillLineP(p, new Vector2(xL, yB), new Vector2(xR, yB), edge, 1f);
            }

            if (cfg.showJudgeLine)
                FillLineP(p, new Vector2(xL, yJ), new Vector2(xR, yJ), css, 2f);

            // 新規接触のリップル
            foreach (var r in input.Ripples)
            {
                if (r.layer != layer) continue;
                float k = (now - r.born) / 0.3f;
                if (k < 0f || k > 1f) continue;
                float x0 = PxX(CellU(r.cell));
                float x1 = PxX(CellU(r.cell + 1));
                float inset = k * 6f;
                var c = Color.white; c.a = (1f - k) * 0.8f;
                FillRectOutlineP(p, Rect.MinMaxRect(x0 + inset, Mathf.Min(yT, yB) + inset, x1 - inset, Mathf.Max(yT, yB) - inset), c, 1f);
            }
        }

        // ================= 文字: UI Toolkit の Label（perf-r1.md §6【F】） =================
        //
        // 以前は OnGUI(IMGUI) で描いていた。OnGUI はメソッドが存在するだけで IMGUI パスが毎フレーム
        // (Layout/Repaint の最低2回) 走り、HUD の文字列補間が毎フレーム GC ゴミを出していた。
        // Label は保持型なので、表示内容が変わったときだけ text を書き換える。

        private VisualElement staticLabelsRoot;
        private VisualElement touchLabelsRoot;

        private VisualElement NewLayer()
        {
            var layer = new VisualElement { pickingMode = PickingMode.Ignore };
            layer.style.position = Position.Absolute;
            layer.style.left = 0;
            layer.style.top = 0;
            layer.style.right = 0;
            layer.style.bottom = 0;
            overlayRoot.Add(layer);
            return layer;
        }

        private static Label NewLabel(string text, int fontSize, Color color)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.style.position = Position.Absolute;
            l.style.fontSize = fontSize;
            l.style.color = color;
            l.style.marginLeft = l.style.marginRight = l.style.marginTop = l.style.marginBottom = 0;
            l.style.paddingLeft = l.style.paddingRight = l.style.paddingTop = l.style.paddingBottom = 0;
            return l;
        }

        // ---- 地平線・分割線・セル番号（ステージ形状か画面サイズが変わったときだけ作り直す） ----

        private bool staticLabelsDirty = true;
        private int staticLabelsVersion = -1;

        private void UpdateStaticLabels()
        {
            if (stageController == null) return;
            if (!staticLabelsDirty && staticLabelsVersion == stageController.Version) return;

            float w = overlayRoot.contentRect.width, h = overlayRoot.contentRect.height;
            if (float.IsNaN(w) || w < 2f || h < 2f) return; // 初回レイアウト前。GeometryChangedEvent で再度来る
            staticLabelsDirty = false;
            staticLabelsVersion = stageController.Version;
            staticLabelsRoot.Clear();

            var cfg = stageController.Config;
            var d = stageController.Derived;
            float CellX(float cellIdx) => OvX(w, -cfg.U + 2f * cfg.U * cellIdx / cfg.cells);

            if (cfg.showHorizon && d.vHorizon <= 1f)
                AddStaticLabel("horizon", OvX(w, cfg.U) - 56f, OvY(h, d.vHorizon) - 14f,
                    new Color(140 / 255f, 170 / 255f, 230 / 255f, 0.6f));

            if (cfg.showSplitLine)
                AddStaticLabel("y_split", 4f, OvY(h, cfg.vSplit) - 14f,
                    new Color(200 / 255f, 205 / 255f, 235 / 255f, 0.55f));

            if (cfg.showCellIndex)
            {
                foreach (float vBot in new[] { cfg.vSkyBot, cfg.vGroundBot })
                {
                    float y = Mathf.Min(h - 3f, Mathf.Max(10f, OvY(h, vBot) - 4f));
                    for (int k = 0; k < cfg.cells; k++)
                    {
                        var l = AddStaticLabel(k.ToString(), (CellX(k) + CellX(k + 1)) / 2f - 10f, y - 6f, Color.white);
                        l.style.width = 20;
                        l.style.height = 12;
                        l.style.unityTextAlign = TextAnchor.MiddleCenter;
                    }
                }
            }
        }

        private Label AddStaticLabel(string text, float x, float y, Color color)
        {
            var l = NewLabel(text, 10, color);
            l.style.left = x;
            l.style.top = y;
            staticLabelsRoot.Add(l);
            return l;
        }

        // ---- タッチデバッグ（接触点ごとの "L{layer} C{cell}"。ラベルはプールして使い回す） ----

        private readonly System.Collections.Generic.List<Label> touchLabels = new();
        private readonly System.Collections.Generic.List<int> touchLabelKeys = new(); // layer*1000+cell、text更新判定用

        private void UpdateTouchLabels()
        {
            int used = 0;
            if (stageController != null && input != null && stageController.Config.showTouchDebug)
            {
                float w = overlayRoot.contentRect.width, h = overlayRoot.contentRect.height;
                foreach (var t in input.Contacts.Values)
                {
                    if (used == touchLabels.Count)
                    {
                        var nl = NewLabel("", 10, Color.white);
                        touchLabelsRoot.Add(nl);
                        touchLabels.Add(nl);
                        touchLabelKeys.Add(int.MinValue);
                    }
                    var l = touchLabels[used];
                    int key = (int)t.layer * 1000 + t.cell;
                    if (touchLabelKeys[used] != key)
                    {
                        touchLabelKeys[used] = key;
                        l.text = $"L{(int)t.layer} C{t.cell}";
                    }
                    l.style.left = OvX(w, t.u) + 30f;
                    l.style.top = OvY(h, t.v) - 6f;
                    l.style.display = DisplayStyle.Flex;
                    used++;
                }
            }
            for (int i = used; i < touchLabels.Count; i++)
                if (touchLabels[i].style.display != DisplayStyle.None)
                    touchLabels[i].style.display = DisplayStyle.None;
        }

        // ---- HUD（移植元: web-prototype/src/main.ts の frame() 内 HUD 更新） ----

        /// <summary>時刻・fps・音源誤差の行は毎フレーム値が変わるので、この間隔でだけ書き換える。</summary>
        private const float HudTickerIntervalSec = 0.1f;

        private Label hudTimeLabel, hudComboLabel, hudCountsLabel, hudJudgeLabel, hudAudioLabel;
        private float hudTickerNextAt;
        private int hudCombo = -1, hudMaxCombo = -1, hudPp = -1, hudP = -1, hudG = -1, hudM = -1;
        private string hudLastJudge;
        private int hudLastMs = int.MinValue;

        private void BuildHud()
        {
            var box = new VisualElement { pickingMode = PickingMode.Ignore };
            box.style.position = Position.Absolute;
            // 一時停止ボタン(AppController: left16/top16/48x48、前面)と重ならないよう右隣に置く
            box.style.left = 72;
            box.style.top = 8;
            box.style.width = 190;
            box.style.height = 96;
            box.style.backgroundColor = new Color(0f, 0f, 0f, 0.45f);
            box.style.borderTopLeftRadius = box.style.borderTopRightRadius =
                box.style.borderBottomLeftRadius = box.style.borderBottomRightRadius = 4;
            uiDocument.rootVisualElement.Add(box);

            Label Line(int row, Color c)
            {
                var l = NewLabel("", 12, c);
                l.style.left = 8;
                l.style.top = 4 + 18 * row;
                box.Add(l);
                return l;
            }
            hudTimeLabel = Line(0, Color.white);
            hudComboLabel = Line(1, Color.white);
            hudCountsLabel = Line(2, Color.white);
            hudJudgeLabel = Line(3, new Color(0.91f, 0.94f, 1f));
            // perf-r1.md §12: 音源がスケジュールどおり鳴っているかの診断。
            // 0付近＝スケジュールどおり（ズレの正体は出力レイテンシ）／
            // 負に大きい＝音源が遅れて鳴り始めている（streamAudio化の回帰）。
            hudAudioLabel = Line(4, Color.white);
        }

        private void UpdateHud()
        {
            if (Time.unscaledTime >= hudTickerNextAt)
            {
                hudTickerNextAt = Time.unscaledTime + HudTickerIntervalSec;
                hudTimeLabel.text = $"t {hudSongTime:F2}s   {hudFps:F0}fps";
                hudAudioLabel.text = $"audio {(hudAudioErrorMs > 0 ? "+" : "")}{hudAudioErrorMs:F0}ms";
            }

            if (Judge == null) return;
            var s = Judge.Score;

            if (s.combo != hudCombo || s.maxCombo != hudMaxCombo)
            {
                hudCombo = s.combo;
                hudMaxCombo = s.maxCombo;
                hudComboLabel.text = $"COMBO {s.combo} (max {s.maxCombo})";
            }
            if (s.perfectPlus != hudPp || s.perfect != hudP || s.good != hudG || s.miss != hudM)
            {
                hudPp = s.perfectPlus;
                hudP = s.perfect;
                hudG = s.good;
                hudM = s.miss;
                hudCountsLabel.text = $"P+{s.perfectPlus} P{s.perfect} G{s.good} M{s.miss}";
            }
            // 同じ判定・同じmsが連続しても表示は同じなので、(lastJudge, 丸めたms) の変化だけ見ればよい
            int ms = Mathf.RoundToInt(s.lastMs);
            if (s.lastJudge != hudLastJudge || ms != hudLastMs)
            {
                hudLastJudge = s.lastJudge;
                hudLastMs = ms;
                bool showMs = s.lastJudge == "PERFECT+" || s.lastJudge == "PERFECT" || s.lastJudge == "GOOD";
                hudJudgeLabel.text = showMs ? $"{s.lastJudge} {(ms > 0 ? "+" : "")}{ms}ms" : s.lastJudge;
            }
        }
    }
}
