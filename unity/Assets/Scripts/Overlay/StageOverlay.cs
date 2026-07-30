using UnityEngine;
using Muses.Stage;
using Muses.TouchInput;
using Muses.Gameplay;

namespace Muses.Overlay
{
    /// <summary>
    /// 判定帯のスクリーン空間オーバーレイ。移植元: web-prototype/src/overlay.ts。
    ///
    /// 設計メモの帰結どおり、判定帯はワールド空間のポリゴンではなくスクリーン空間で描く。
    /// 図形は GL immediate mode（OnRenderObject）、セル番号・ラベル・タッチデバッグの文字は
    /// OnGUI で描画する。
    ///
    /// 簡略化した点（TS版との差分）:
    /// - 地平線の破線は実線で近似している。
    /// - 判定帯背景の3色グラデーションは、判定線を境に上下2枚の単色矩形で近似している。
    /// - 判定線のシャドウブラー（発光っぽい見た目）は省略している。
    /// GL immediate mode の material は "Hidden/Internal-Colored" を使う、Unityのデバッグ描画で
    /// 広く使われる標準パターン。URPでも動作するはずだが、映らない場合は要確認。
    /// </summary>
    [RequireComponent(typeof(Camera))]
    public class StageOverlay : MonoBehaviour
    {
        [SerializeField] private StageController stageController;
        [SerializeField] private TouchInputManager input;

        /// <summary>Judge はプレーンなC#クラス（MonoBehaviourではない）なので Inspector には出せない。
        /// GameController が生成後にコードから設定する。</summary>
        public Judge Judge { get; set; }

        private Camera cam;
        private static Material lineMaterial;

        private void Awake() => cam = GetComponent<Camera>();

        private static void EnsureMaterial()
        {
            if (lineMaterial != null) return;
            var shader = Shader.Find("Hidden/Internal-Colored");
            lineMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
            lineMaterial.SetInt("_ZWrite", 0);
        }

        private static float PxX(float u) => (u + 1f) / 2f * Screen.width;
        private static float PxY(float v) => (v + 1f) / 2f * Screen.height; // y-up（GL.LoadPixelMatrixに合わせる）
        private static float CellU(StageConfig cfg, float cellIdx) => -cfg.U + 2f * cfg.U * cellIdx / cfg.cells;

        private void OnRenderObject()
        {
            if (Camera.current != cam || stageController == null || input == null) return;
            var cfg = stageController.Config;
            var d = stageController.Derived;
            float now = Time.time;

            EnsureMaterial();
            lineMaterial.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, Screen.width, 0, Screen.height);

            if (cfg.showHorizon && d.vHorizon <= 1f)
            {
                GL.Begin(GL.LINES);
                GL.Color(new Color(120 / 255f, 150 / 255f, 220 / 255f, 0.35f));
                Line(PxX(-1f), PxY(d.vHorizon), PxX(1f), PxY(d.vHorizon));
                GL.End();
            }

            DrawBand(cfg, Layer.Sky, cfg.vSkyTop, cfg.vSkyBot, cfg.vSkyJudge,
                StageGeometry.ColorFromHex(StageColors.Sky), new Color(255 / 255f, 62 / 255f, 165 / 255f), now);
            DrawBand(cfg, Layer.Ground, cfg.vGroundTop, cfg.vGroundBot, cfg.vGroundJudge,
                StageGeometry.ColorFromHex(StageColors.Ground), new Color(139 / 255f, 92 / 255f, 246 / 255f), now);

            if (cfg.showSplitLine)
            {
                GL.Begin(GL.LINES);
                GL.Color(new Color(220 / 255f, 220 / 255f, 255 / 255f, 0.30f));
                Line(PxX(-1f), PxY(cfg.vSplit), PxX(1f), PxY(cfg.vSplit));
                GL.End();
            }

            if (cfg.showTouchDebug) DrawTouches();

            if (Judge != null)
            {
                Judge.Flashes.RemoveAll(f => now - f.born < 0f || now - f.born >= 0.45f);
                GL.Begin(GL.QUADS);
                foreach (var f in Judge.Flashes)
                {
                    float k = Mathf.Clamp01((now - f.born) / 0.45f);
                    float vJ = f.layer == Layer.Sky ? cfg.vSkyJudge : cfg.vGroundJudge;
                    float x0 = PxX(CellU(cfg, f.cell));
                    float x1 = PxX(CellU(cfg, f.cell + f.width));
                    float y = PxY(vJ);
                    float r = 6f + 26f * k;
                    Color col = f.kind == JudgeKind.Perfect ? Color.white
                        : f.kind == JudgeKind.Good ? new Color(120 / 255f, 220 / 255f, 255 / 255f)
                        : new Color(1f, 80 / 255f, 80 / 255f);
                    col.a = (1f - k) * 0.55f;
                    GL.Color(col);
                    Quad(x0 + 2f, y - r / 2f, x1 - 2f, y + r / 2f);
                }
                GL.End();
            }

            GL.PopMatrix();
        }

        private void DrawBand(StageConfig cfg, Layer layer, float vTop, float vBot, float vJudge, Color css, Color rgb, float now)
        {
            float yT = PxY(vTop), yB = PxY(vBot), yJ = PxY(vJudge);
            float xL = PxX(-cfg.U), xR = PxX(cfg.U);

            if (cfg.showBand)
            {
                // TS版は3ストップのグラデーションだが、判定線を境にした上下2枚の単色矩形で近似する
                GL.Begin(GL.QUADS);
                var top = rgb; top.a = 0.02f;
                var mid = rgb; mid.a = 0.13f;
                GL.Color(top);
                Quad(xL, Mathf.Min(yT, yJ), xR, Mathf.Max(yT, yJ));
                GL.Color(mid);
                Quad(xL, Mathf.Min(yJ, yB), xR, Mathf.Max(yJ, yB));
                GL.End();
            }

            // アクティブセルのハイライト（帯を消していても押した位置は出す）
            GL.Begin(GL.QUADS);
            for (int k = 0; k < cfg.cells; k++)
            {
                if (!input.IsOccupied(layer, k)) continue;
                float a = PxX(CellU(cfg, k));
                float bx = PxX(CellU(cfg, k + 1));
                var c = rgb; c.a = 0.30f;
                GL.Color(c);
                Quad(a, Mathf.Min(yT, yB), bx, Mathf.Max(yT, yB));
            }
            GL.End();

            if (cfg.showBand)
            {
                GL.Begin(GL.LINES);
                var c = rgb; c.a = 0.38f;
                GL.Color(c);
                for (int k = 0; k <= cfg.cells; k++)
                {
                    float x = PxX(CellU(cfg, k));
                    Line(x, yT, x, yB);
                }
                var edge = rgb; edge.a = 0.5f;
                GL.Color(edge);
                Line(xL, yT, xR, yT);
                Line(xL, yB, xR, yB);
                GL.End();
            }

            if (cfg.showJudgeLine)
            {
                GL.Begin(GL.LINES);
                GL.Color(css);
                Line(xL, yJ, xR, yJ);
                GL.End();
            }

            // 新規接触のリップル
            GL.Begin(GL.LINES);
            foreach (var r in input.Ripples)
            {
                if (r.layer != layer) continue;
                float k = (now - r.born) / 0.3f;
                if (k < 0f || k > 1f) continue;
                float x0 = PxX(CellU(cfg, r.cell));
                float x1 = PxX(CellU(cfg, r.cell + 1));
                float inset = k * 6f;
                var c = Color.white; c.a = (1f - k) * 0.8f;
                GL.Color(c);
                RectOutline(x0 + inset, Mathf.Min(yT, yB) + inset, x1 - inset, Mathf.Max(yT, yB) - inset);
            }
            GL.End();
            input.Ripples.RemoveAll(r => now - r.born < 0f || now - r.born >= 0.3f);
        }

        private void DrawTouches()
        {
            GL.Begin(GL.LINES);
            foreach (var t in input.Contacts.Values)
            {
                float x = PxX(t.u), y = PxY(t.v);
                var c = t.layer == Layer.Sky
                    ? StageGeometry.ColorFromHex(StageColors.Sky)
                    : StageGeometry.ColorFromHex(StageColors.Ground);
                GL.Color(c);
                Circle(x, y, 26f);
            }
            GL.End();
        }

        private void OnGUI()
        {
            if (stageController == null) return;
            var cfg = stageController.Config;
            var d = stageController.Derived;
            var style = new GUIStyle { fontSize = 10, normal = { textColor = Color.white } };

            if (cfg.showHorizon && d.vHorizon <= 1f)
                Label("horizon", PxX(cfg.U) - 60, Screen.height - PxY(d.vHorizon) - 14,
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
                    float x = PxX(t.u), y = PxY(t.v);
                    style.normal.textColor = Color.white;
                    GUI.Label(new Rect(x + 30, Screen.height - y - 6, 100, 20), $"L{(int)t.layer} C{t.cell}", style);
                }
            }
        }

        private void DrawCellIndex(StageConfig cfg, float vBot, GUIStyle style)
        {
            float y = Mathf.Min(Screen.height - 3f, Mathf.Max(10f, Screen.height - PxY(vBot) - 4f));
            var centered = new GUIStyle(style) { alignment = TextAnchor.MiddleCenter };
            for (int k = 0; k < cfg.cells; k++)
            {
                float x = (PxX(CellU(cfg, k)) + PxX(CellU(cfg, k + 1))) / 2f;
                GUI.Label(new Rect(x - 10, y - 6, 20, 12), k.ToString(), centered);
            }
        }

        private static void Label(string text, float x, float y, Color color, GUIStyle style)
        {
            style.normal.textColor = color;
            GUI.Label(new Rect(x + 4, y, 100, 16), text, style);
        }

        private static void Line(float x0, float y0, float x1, float y1)
        {
            GL.Vertex3(x0, y0, 0); GL.Vertex3(x1, y1, 0);
        }

        private static void Quad(float x0, float y0, float x1, float y1)
        {
            GL.Vertex3(x0, y0, 0); GL.Vertex3(x1, y0, 0);
            GL.Vertex3(x1, y1, 0); GL.Vertex3(x0, y1, 0);
        }

        private static void RectOutline(float x0, float y0, float x1, float y1)
        {
            GL.Vertex3(x0, y0, 0); GL.Vertex3(x1, y0, 0);
            GL.Vertex3(x1, y0, 0); GL.Vertex3(x1, y1, 0);
            GL.Vertex3(x1, y1, 0); GL.Vertex3(x0, y1, 0);
            GL.Vertex3(x0, y1, 0); GL.Vertex3(x0, y0, 0);
        }

        private static void Circle(float cx, float cy, float r)
        {
            const int seg = 24;
            for (int i = 0; i < seg; i++)
            {
                float a0 = i / (float)seg * Mathf.PI * 2f;
                float a1 = (i + 1) / (float)seg * Mathf.PI * 2f;
                GL.Vertex3(cx + Mathf.Cos(a0) * r, cy + Mathf.Sin(a0) * r, 0);
                GL.Vertex3(cx + Mathf.Cos(a1) * r, cy + Mathf.Sin(a1) * r, 0);
            }
        }
    }
}
