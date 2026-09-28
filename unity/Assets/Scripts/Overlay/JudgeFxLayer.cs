using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using Muses.Gameplay;

namespace Muses.Overlay
{
    /// <summary>
    /// gameplay-feel-r1.md §3・§4。判定名のポップアップ、コンボ表示、PERFECT以上のヒット演出（光の輪＋火花）。
    ///
    /// StageOverlay の overlayRoot（判定線と同じ座標系）の子として置く UI Toolkit の要素だけで描く。
    /// - 光・輪・火花は実行時に生成した小さなテクスチャ（放射グラデーション等）を貼った VisualElement。
    ///   Painter2D で描くと細かいパスの分割が毎フレーム CPU に乗るため、要素をプールして
    ///   translate / scale / opacity / tint だけを書き換える（レイアウト再計算を起こさない）。
    /// - Bloom は軽量化（perf-r1【A】）で切ってあるので、光の柔らかさはテクスチャのグラデーションで出す。
    /// - アニメーションは Time.unscaledTime 基準（songTime は判定オフセット分ずれるうえ、演出には不要）。
    ///
    /// 大きさは画面の高さ h に比例させる（PanelSettings が ConstantPixelSize のため、端末の解像度で
    /// 見た目の大きさが変わらないように）。
    /// </summary>
    public class JudgeFxLayer
    {
        // ---- 見た目の定数（gameplay-feel-r1.md §8: 実機で調整する値） ----
        private const float PopupDuration = 0.45f;
        private const float HitDuration = 0.38f;
        private const float TickDuration = 0.22f;
        private const float ComboAlpha = 0.6f;

        private static readonly Color PerfectPlusColor = Hex(0xffd54a);
        private static readonly Color PerfectColor = Hex(0xff9f43);
        private static readonly Color GoodColor = Hex(0x4ade80);
        private static readonly Color MissColor = Hex(0x9ca3af);
        /// <summary>gameplay-feel-r2.md §2。判定名の下の EARLY / LATE。</summary>
        private static readonly Color EarlyColor = Hex(0x60a5fa);
        private static readonly Color LateColor = Hex(0xf87171);
        /// <summary>光・輪・火花の色（判定名の色とは別。PERFECT+は金、PERFECTは白寄りの水色）。</summary>
        private static readonly Color FxGold = new(1f, 0.86f, 0.42f);
        private static readonly Color FxBlue = new(0.78f, 0.94f, 1f);

        private readonly VisualElement root;
        private readonly Texture2D glowTex, ringTex, sparkTex;
        private readonly System.Random rng = new(12345);

        private float h = 1000f;

        public JudgeFxLayer(VisualElement parent)
        {
            root = new VisualElement { pickingMode = PickingMode.Ignore };
            root.style.position = Position.Absolute;
            root.style.left = root.style.top = root.style.right = root.style.bottom = 0;
            parent.Add(root);

            glowTex = MakeTex(64, r => Mathf.Pow(Mathf.Clamp01(1f - r), 2.2f));
            // 輪: 半径0.78付近に細い帯＋内側にごく薄いにじみ
            ringTex = MakeTex(128, r =>
            {
                float band = Mathf.Exp(-Mathf.Pow((r - 0.78f) / 0.07f, 2f));
                float inner = 0.12f * Mathf.Clamp01(1f - r / 0.78f);
                return Mathf.Clamp01(band + inner) * (r > 1f ? 0f : 1f);
            });
            sparkTex = MakeTex(32, r => Mathf.Clamp01(Mathf.Pow(Mathf.Clamp01(1f - r), 1.6f) * 1.4f));

            BuildCombo();
        }

        /// <summary>画面サイズが変わったとき（初回レイアウト含む）に呼ぶ。コンボの位置もここで決める。</summary>
        public void Layout(float width, float height, float comboCenterY)
        {
            h = Mathf.Max(1f, height);
            comboBox.style.left = 0;
            comboBox.style.width = width;
            float numSize = Mathf.Round(h * 0.085f);
            float capSize = Mathf.Round(h * 0.022f);
            comboNumber.style.fontSize = numSize;
            comboCaption.style.fontSize = capSize;
            comboBox.style.top = comboCenterY - (numSize + capSize) * 0.55f;
        }

        // ================= 判定名のポップアップ =================

        private class Popup
        {
            public Label label;
            /// <summary>gameplay-feel-r2.md §2。判定名の下の EARLY / LATE。</summary>
            public Label sub;
            public bool active;
            public float layerF;
            public float cellCenter;
            public float x, y, born;
        }

        private readonly List<Popup> popups = new();

        /// <param name="earlyLate">-1=EARLY / +1=LATE / 0=出さない（<see cref="EarlyLate.Of"/>）</param>
        /// <param name="layerF">判定点の高さ（近い高さ・近い位置の表示中のものは使い回す）</param>
        /// <param name="cellCenter">ノーツ中央のセル座標（使い回し判定用）</param>
        public void SpawnPopup(JudgeKind kind, int earlyLate, float layerF, float cellCenter, float x, float yJudge, float now)
        {
            // Slide のコンボ点は毎秒4〜8回出るので、近い高さ・近い位置（0.75セル以内）に表示中のものがあれば
            // 積み重ねずにそれを使い回す（gameplay-feel-r1.md §3）。
            Popup p = null;
            foreach (var q in popups)
                if (q.active && Mathf.Abs(q.layerF - layerF) < 0.1f && Mathf.Abs(q.cellCenter - cellCenter) < 0.75f) { p = q; break; }
            if (p == null)
            {
                foreach (var q in popups)
                    if (!q.active) { p = q; break; }
            }
            if (p == null)
            {
                if (popups.Count >= 24)
                {
                    // 満杯なら一番古いものを奪う
                    p = popups[0];
                    foreach (var q in popups) if (q.born < p.born) p = q;
                }
                else
                {
                    p = new Popup { label = NewPopupLabel(240f, 60f), sub = NewPopupLabel(160f, 30f) };
                    popups.Add(p);
                }
            }

            p.active = true;
            p.layerF = layerF;
            p.cellCenter = cellCenter;
            p.x = x;
            p.y = yJudge - h * 0.045f; // 判定線の少し上
            p.born = now;
            var (text, color) = kind switch
            {
                JudgeKind.PerfectPlus => ("PERFECT+", PerfectPlusColor),
                JudgeKind.Perfect => ("PERFECT", PerfectColor),
                JudgeKind.Good => ("GOOD", GoodColor),
                _ => ("MISS", MissColor),
            };
            if (p.label.text != text) p.label.text = text;
            p.label.style.color = color;
            p.label.style.fontSize = Mathf.Round(h * 0.03f);
            p.label.style.visibility = Visibility.Visible;

            if (earlyLate != 0)
            {
                string subText = earlyLate < 0 ? "EARLY" : "LATE";
                if (p.sub.text != subText) p.sub.text = subText;
                p.sub.style.color = earlyLate < 0 ? EarlyColor : LateColor;
                p.sub.style.fontSize = Mathf.Round(h * 0.018f);
                p.sub.style.visibility = Visibility.Visible;
            }
            else p.sub.style.visibility = Visibility.Hidden;
        }

        private Label NewPopupLabel(float width, float height)
        {
            var l = new Label { pickingMode = PickingMode.Ignore };
            l.style.position = Position.Absolute;
            l.style.left = 0;
            l.style.top = 0;
            l.style.width = width;
            l.style.height = height;
            l.style.marginLeft = l.style.marginRight = l.style.marginTop = l.style.marginBottom = 0;
            l.style.paddingLeft = l.style.paddingRight = l.style.paddingTop = l.style.paddingBottom = 0;
            l.style.unityTextAlign = TextAnchor.MiddleCenter;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            // 背景が明るい(#a0b298)ので暗い縁取りで読ませる
            l.style.unityTextOutlineWidth = 1.2f;
            l.style.unityTextOutlineColor = new Color(0.08f, 0.06f, 0.14f, 0.9f);
            l.style.visibility = Visibility.Hidden;
            root.Add(l);
            return l;
        }

        private void TickPopups(float now)
        {
            foreach (var p in popups)
            {
                if (!p.active) continue;
                float k = (now - p.born) / PopupDuration;
                if (k >= 1f || k < 0f)
                {
                    p.active = false;
                    p.label.style.visibility = Visibility.Hidden;
                    p.sub.style.visibility = Visibility.Hidden;
                    continue;
                }
                float rise = h * 0.03f * EaseOut(k);
                float pop = k < 0.15f ? Mathf.Lerp(1.3f, 1f, k / 0.15f) : 1f;
                float opacity = k < 0.6f ? 1f : 1f - (k - 0.6f) / 0.4f;
                p.label.style.translate = new Translate(p.x - 120f, p.y - 30f - rise);
                p.label.style.scale = new Scale(new Vector2(pop, pop));
                p.label.style.opacity = opacity;
                // EARLY/LATE は判定名のすぐ下（判定名の文字高 ≒ h*0.03 の少し下）に一緒に流す
                p.sub.style.translate = new Translate(p.x - 80f, p.y - 15f - rise + h * 0.026f);
                p.sub.style.opacity = opacity;
            }
        }

        // ================= ヒット演出（光の輪＋火花） =================

        private class Sprite
        {
            public VisualElement ve;
            public bool active;
            public float born, duration;
            // 動き（火花のみ使用）
            public float x, y, vx, vy;
            public float baseW, baseH;
            public float scale0, scale1;
            public float alpha0;
            public int kind; // 0=glow 1=ring 2=spark
            public Texture2D tex;
        }

        private readonly List<Sprite> sprites = new();

        /// <param name="noteWidthPx">ノーツの画面上の幅（光の横幅を合わせる）</param>
        public void SpawnHit(JudgeKind kind, bool slideTick, float x, float y, float noteWidthPx, float now)
        {
            if (kind != JudgeKind.PerfectPlus && kind != JudgeKind.Perfect) return;
            bool pp = kind == JudgeKind.PerfectPlus;
            var col = pp ? FxGold : FxBlue;
            float big = pp ? 1.15f : 1f;

            if (slideTick)
            {
                // Slide のコンボ点は軽量版（小さいグローのみ）。毎秒8回、火花まで出すとうるさい。
                float gw = Mathf.Max(noteWidthPx * 0.7f, h * 0.05f);
                AddSprite(0, glowTex, col, x, y, gw, h * 0.035f, 0.9f, 1.15f, 0.7f, TickDuration, now);
                return;
            }

            // 光: ノーツ幅に合わせた横長のグロー
            float glowW = Mathf.Max(noteWidthPx * 1.35f, h * 0.09f) * big;
            AddSprite(0, glowTex, col, x, y, glowW, h * 0.075f * big, 0.8f, 1.25f, 1f, HitDuration, now);
            // 芯: 小さく白い強い光
            AddSprite(0, glowTex, Color.white, x, y, glowW * 0.45f, h * 0.03f * big, 1f, 1.1f, 0.9f, HitDuration * 0.6f, now);
            // 輪: 縦を潰した楕円（床に乗っている感じ）が広がる
            float ringW = Mathf.Max(noteWidthPx * 0.9f, h * 0.08f) * big;
            AddSprite(1, ringTex, col, x, y, ringW, ringW * 0.42f, 0.45f, 1.7f, 0.95f, HitDuration * 1.1f, now);

            // 火花: 上向きに散って、少し落ちながら消える
            int n = pp ? 8 : 6;
            for (int i = 0; i < n; i++)
            {
                float ang = Mathf.Deg2Rad * Mathf.Lerp(-160f, -20f, (i + (float)rng.NextDouble()) / n); // 画面座標はy下向き
                float speed = h * Mathf.Lerp(0.28f, 0.5f, (float)rng.NextDouble()) * big;
                float sz = h * Mathf.Lerp(0.009f, 0.015f, (float)rng.NextDouble()) * big;
                var s = AddSprite(2, sparkTex, i % 3 == 0 ? Color.white : col,
                    x + (float)(rng.NextDouble() - 0.5) * noteWidthPx * 0.6f, y, sz, sz, 1f, 0.6f, 1f,
                    Mathf.Lerp(0.3f, 0.48f, (float)rng.NextDouble()), now);
                s.vx = Mathf.Cos(ang) * speed;
                s.vy = Mathf.Sin(ang) * speed;
            }
        }

        private Sprite AddSprite(int kind, Texture2D tex, Color tint, float x, float y, float w, float hgt,
            float scale0, float scale1, float alpha0, float duration, float now)
        {
            Sprite s = null;
            foreach (var q in sprites)
                if (!q.active && q.kind == kind) { s = q; break; }
            if (s == null)
            {
                if (sprites.Count >= 160)
                {
                    s = sprites[0];
                    foreach (var q in sprites) if (q.born < s.born) s = q;
                }
                else
                {
                    var ve = new VisualElement { pickingMode = PickingMode.Ignore };
                    ve.style.position = Position.Absolute;
                    ve.style.left = 0;
                    ve.style.top = 0;
                    ve.style.visibility = Visibility.Hidden;
                    root.Insert(0, ve); // 判定名(ラベル)より奥に描く
                    s = new Sprite { ve = ve };
                    sprites.Add(s);
                }
            }

            if (s.tex != tex)
            {
                s.tex = tex;
                s.ve.style.backgroundImage = new StyleBackground(tex);
            }
            s.kind = kind;
            s.active = true;
            s.born = now;
            s.duration = duration;
            s.x = x;
            s.y = y;
            s.vx = s.vy = 0f;
            s.baseW = w;
            s.baseH = hgt;
            s.scale0 = scale0;
            s.scale1 = scale1;
            s.alpha0 = alpha0;
            s.ve.style.width = w;
            s.ve.style.height = hgt;
            s.ve.style.unityBackgroundImageTintColor = tint;
            s.ve.style.visibility = Visibility.Visible;
            return s;
        }

        private void TickSprites(float now)
        {
            float gravity = h * 1.1f;
            foreach (var s in sprites)
            {
                if (!s.active) continue;
                float age = now - s.born;
                float k = age / s.duration;
                if (k >= 1f || k < 0f)
                {
                    s.active = false;
                    s.ve.style.visibility = Visibility.Hidden;
                    continue;
                }
                float cx = s.x, cy = s.y;
                if (s.kind == 2)
                {
                    cx += s.vx * age;
                    cy += s.vy * age + 0.5f * gravity * age * age;
                }
                float sc = Mathf.Lerp(s.scale0, s.scale1, EaseOut(k));
                // 中心合わせは translate で行う（スケールの原点は要素中心なので、位置は拡縮に影響されない）
                s.ve.style.translate = new Translate(cx - s.baseW * 0.5f, cy - s.baseH * 0.5f);
                s.ve.style.scale = new Scale(new Vector2(sc, sc));
                float fade = s.kind == 1 ? (1f - k) * (1f - k) : 1f - k;
                s.ve.style.opacity = s.alpha0 * fade;
            }
        }

        // ================= コンボ =================

        private VisualElement comboBox;
        private Label comboNumber, comboCaption;
        private int shownCombo = -1;
        private float comboPulseAt = -10f;

        private void BuildCombo()
        {
            comboBox = new VisualElement { pickingMode = PickingMode.Ignore };
            comboBox.style.position = Position.Absolute;
            comboBox.style.flexDirection = FlexDirection.Column;
            comboBox.style.alignItems = Align.Center;
            comboBox.style.opacity = ComboAlpha;
            comboBox.style.visibility = Visibility.Hidden;

            Label L(bool bold)
            {
                var l = new Label { pickingMode = PickingMode.Ignore };
                l.style.marginLeft = l.style.marginRight = l.style.marginTop = l.style.marginBottom = 0;
                l.style.paddingLeft = l.style.paddingRight = l.style.paddingTop = l.style.paddingBottom = 0;
                l.style.unityTextAlign = TextAnchor.MiddleCenter;
                l.style.color = Color.white;
                if (bold) l.style.unityFontStyleAndWeight = FontStyle.Bold;
                l.style.unityTextOutlineWidth = 1.5f;
                l.style.unityTextOutlineColor = new Color(0.08f, 0.06f, 0.14f, 0.85f);
                comboBox.Add(l);
                return l;
            }
            comboNumber = L(true);
            comboCaption = L(false);
            comboCaption.text = "COMBO";
            root.Add(comboBox);
        }

        public void SetCombo(int combo, float now)
        {
            if (combo == shownCombo) return;
            if (combo > shownCombo && combo > 0) comboPulseAt = now;
            shownCombo = combo;
            // コンボ0（開始直後・MISS直後）は非表示
            comboBox.style.visibility = combo > 0 ? Visibility.Visible : Visibility.Hidden;
            if (combo > 0) comboNumber.text = combo.ToString();
        }

        private void TickCombo(float now)
        {
            float k = (now - comboPulseAt) / 0.12f;
            float sc = k >= 0f && k < 1f ? Mathf.Lerp(1.12f, 1f, k) : 1f;
            comboNumber.style.scale = new Scale(new Vector2(sc, sc));
        }

        // ================= 共通 =================

        public void Tick(float now)
        {
            TickSprites(now);
            TickPopups(now);
            TickCombo(now);
        }

        /// <summary>リトライ・タイトル戻りなど、演出を即座に消したいとき。</summary>
        public void Clear()
        {
            foreach (var p in popups)
            {
                p.active = false;
                p.label.style.visibility = Visibility.Hidden;
                p.sub.style.visibility = Visibility.Hidden;
            }
            foreach (var s in sprites) { s.active = false; s.ve.style.visibility = Visibility.Hidden; }
            SetCombo(0, 0f);
        }

        private static float EaseOut(float k) => 1f - (1f - k) * (1f - k);

        private static Color Hex(uint hex) =>
            new(((hex >> 16) & 0xff) / 255f, ((hex >> 8) & 0xff) / 255f, (hex & 0xff) / 255f, 1f);

        /// <summary>白地・放射状のアルファだけを持つテクスチャ。色は tint で付ける。r は中心0〜縁1。</summary>
        private static Texture2D MakeTex(int size, System.Func<float, float> alphaOfR)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };
            var px = new Color32[size * size];
            float c = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - c) / c, dy = (y - c) / c;
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(alphaOfR(r)) * 255f);
                px[y * size + x] = new Color32(255, 255, 255, a);
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }
    }
}
