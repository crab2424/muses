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
    /// 図形は UI Toolkit の generateVisualContent + Painter2D、セル番号・ラベル・タッチデバッグの
    /// 文字は OnGUI で描画する（テキストはPainter2Dで描けないため、こちらは従来どおり）。
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

        /// <summary>Judge はプレーンなC#クラス（MonoBehaviourではない）なので Inspector には出せない。
        /// GameController が生成後にコードから設定する。</summary>
        public Judge Judge { get; set; }

        private PanelSettings panelSettings;
        private UIDocument uiDocument;
        private VisualElement overlayRoot;
        private float hudSongTime;
        private float hudFps;

        /// <summary>main.ts の frame() 内 HUD 更新相当。GameController が毎フレーム呼ぶ。</summary>
        public void SetHudTime(float songTime, float fps)
        {
            hudSongTime = songTime;
            hudFps = fps;
        }

        private void Awake()
        {
            // PreviewSystem.cs と同じ方針: Inspector配線を前提にせず、コードから組み立てる。
            panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            panelSettings.scaleMode = PanelScaleMode.ConstantPixelSize;
            panelSettings.clearColor = false;

            uiDocument = gameObject.AddComponent<UIDocument>();
            uiDocument.panelSettings = panelSettings;
            uiDocument.rootVisualElement.pickingMode = PickingMode.Ignore;

            overlayRoot = new VisualElement { pickingMode = PickingMode.Ignore };
            overlayRoot.style.position = Position.Absolute;
            overlayRoot.style.left = 0;
            overlayRoot.style.top = 0;
            overlayRoot.style.right = 0;
            overlayRoot.style.bottom = 0;
            overlayRoot.generateVisualContent += GenerateOverlay;
            uiDocument.rootVisualElement.Add(overlayRoot);
        }

        private void OnDestroy()
        {
            if (panelSettings != null) Destroy(panelSettings);
        }

        private void Update()
        {
            // GL版で毎フレーム行っていたクリーンアップ（描画本体からは分離し、副作用を1箇所にまとめる）。
            float now = Time.time;
            if (Judge != null) Judge.Flashes.RemoveAll(f => now - f.born < 0f || now - f.born >= 0.45f);
            if (input != null) input.Ripples.RemoveAll(r => now - r.born < 0f || now - r.born >= 0.3f);
            overlayRoot.MarkDirtyRepaint();
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
            float now = Time.time;
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

        // ================= OnGUI: 文字（Painter2Dでは描けないため従来どおり） =================

        private static float PxX(StageConfig cfg, float u) => (u + 1f) / 2f * Screen.width;
        private static float PxY(float v) => (v + 1f) / 2f * Screen.height; // y-up（OnGUIのSpaceと合わせて後段でScreen.height-flipする）
        private static float CellU(StageConfig cfg, float cellIdx) => -cfg.U + 2f * cfg.U * cellIdx / cfg.cells;

        private void OnGUI()
        {
            if (stageController == null) return;
            var cfg = stageController.Config;
            var d = stageController.Derived;
            var style = new GUIStyle { fontSize = 10, normal = { textColor = Color.white } };

            if (showHud) DrawHud();

            if (cfg.showHorizon && d.vHorizon <= 1f)
                Label("horizon", PxX(cfg, cfg.U) - 60, Screen.height - PxY(d.vHorizon) - 14,
                    new Color(140 / 255f, 170 / 255f, 230 / 255f, 0.6f), style);

            if (cfg.showSplitLine)
                Label("y_split", 0, Screen.height - PxY(cfg.vSplit) - 14,
                    new Color(200 / 255f, 205 / 255f, 235 / 255f, 0.55f), style);

            if (cfg.showCellIndex)
            {
                DrawCellIndex(cfg, cfg.vSkyBot, style);
                DrawCellIndex(cfg, cfg.vGroundBot, style);
            }

            if (cfg.showTouchDebug && input != null)
            {
                foreach (var t in input.Contacts.Values)
                {
                    float x = PxX(cfg, t.u), y = PxY(t.v);
                    style.normal.textColor = Color.white;
                    GUI.Label(new Rect(x + 30, Screen.height - y - 6, 100, 20), $"L{(int)t.layer} C{t.cell}", style);
                }
            }
        }

        /// <summary>移植元: web-prototype/src/main.ts の frame() 内 HUD更新（#hud要素の innerHTML 相当）</summary>
        private void DrawHud()
        {
            if (Judge == null) return;
            var s = Judge.Score;

            GUI.Box(new Rect(8, 8, 190, 78), "");

            var line = new GUIStyle { fontSize = 12, normal = { textColor = Color.white } };
            var judgeLine = new GUIStyle(line) { normal = { textColor = new Color(0.91f, 0.94f, 1f) } };

            string msSuffix = "";
            if (s.lastJudge == "PERFECT+" || s.lastJudge == "PERFECT" || s.lastJudge == "GOOD")
                msSuffix = $" {(s.lastMs > 0 ? "+" : "")}{s.lastMs:F0}ms";

            GUI.Label(new Rect(16, 12, 180, 18), $"t {hudSongTime:F2}s   {hudFps:F0}fps", line);
            GUI.Label(new Rect(16, 30, 180, 18), $"COMBO {s.combo} (max {s.maxCombo})", line);
            GUI.Label(new Rect(16, 48, 180, 18), $"P+{s.perfectPlus} P{s.perfect} G{s.good} M{s.miss}", line);
            GUI.Label(new Rect(16, 66, 180, 18), $"{s.lastJudge}{msSuffix}", judgeLine);
        }

        private void DrawCellIndex(StageConfig cfg, float vBot, GUIStyle style)
        {
            float y = Mathf.Min(Screen.height - 3f, Mathf.Max(10f, Screen.height - PxY(vBot) - 4f));
            var centered = new GUIStyle(style) { alignment = TextAnchor.MiddleCenter };
            for (int k = 0; k < cfg.cells; k++)
            {
                float x = (PxX(cfg, CellU(cfg, k)) + PxX(cfg, CellU(cfg, k + 1))) / 2f;
                GUI.Label(new Rect(x - 10, y - 6, 20, 12), k.ToString(), centered);
            }
        }

        private static void Label(string text, float x, float y, Color color, GUIStyle style)
        {
            style.normal.textColor = color;
            GUI.Label(new Rect(x + 4, y, 100, 16), text, style);
        }
    }
}
