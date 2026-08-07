using System.Collections.Generic;
using UnityEngine;

namespace Muses.Stage
{
    /// <summary>
    /// 層1つ分の頂点データ。<see cref="StageView"/> がそのまま Mesh に流し込む。
    /// 移植元: web-prototype/src/stage.ts の Stage.build() 内、層ごとのループ本体。
    /// </summary>
    public struct LayerGeometry
    {
        public float y;
        public float near;
        public float far;
        public bool hardFarEdge;

        public bool hasPlane;
        public Vector3[] planeVertices;
        public int[] planeTriangles;
        public Color planeColor;
        public float planeAlpha;

        public bool hasLines;
        public Vector3[] lineVertices;
        public int[] lineIndices;
        public Color lineColor;
        public float lineAlpha;
    }

    public static class StageGeometry
    {
        private struct LayerParams
        {
            public float y;
            public float layerF;
            public uint color;
            public uint grid;
            public float near;
            public float fillAlpha;
            public float step;
        }

        private static LayerParams GetParams(StageConfig cfg, in Derived d, Layer layer)
        {
            if (layer == Layer.Ground)
            {
                return new LayerParams
                {
                    y = 0f,
                    layerF = 0f,
                    color = StageColors.Ground,
                    grid = StageColors.GridGround,
                    near = d.groundNear,
                    fillAlpha = cfg.groundFillAlpha,
                    step = cfg.laneLineStepGround,
                };
            }
            return new LayerParams
            {
                y = d.skyHeight,
                layerF = 1f,
                color = StageColors.Sky,
                grid = StageColors.GridSky,
                near = d.skyNear,
                fillAlpha = cfg.skyFillAlpha,
                step = cfg.laneLineStepSky,
            };
        }

        /// <summary>0xRRGGBB (sRGB) から Unity の Color へ。Material.SetColor 経由で Linear へ変換される前提。
        ///
        /// **注意（2026-08-07）**: この「SetColor が変換してくれる」前提が成り立つのは
        /// マテリアルの Color プロパティ経由の場合だけ。**メッシュの頂点色は Unity が変換しない**ので、
        /// 頂点色に載せる場合は呼び出し側で .linear へ変換すること
        /// （ノーツがこれを取りこぼしており、全ノーツが 4aa3ff→93d1ff 相当に明るく出ていた。
        ///   NoteGeometry.Build の ToVertexColor を参照）。</summary>
        public static Color ColorFromHex(uint hex, float alpha = 1f)
        {
            float r = ((hex >> 16) & 0xFF) / 255f;
            float g = ((hex >> 8) & 0xFF) / 255f;
            float b = (hex & 0xFF) / 255f;
            return new Color(r, g, b, alpha);
        }

        /// <summary>
        /// 奥行きの符号は Three (-z 奥) と Unity (+z 奥) で反転する（座標系の差はこれだけ）。
        /// LaneX 自体は奥行きを正の値として受け取るので、stage.ts の xAt(u, z) 呼び出しと同一。
        /// </summary>
        public static bool Build(StageConfig cfg, in Derived d, Layer layer, out LayerGeometry geo)
        {
            geo = default;
            var L = GetParams(cfg, d, layer);

            float n0 = Mathf.Max(d.zJudge * 0.02f, L.near);
            float f0 = d.zFar;
            if (f0 <= n0) return false;

            // ローカル関数は in パラメータを直接キャプチャできない (CS1628) ため値コピーを介す
            Derived dCopy = d;
            float XAt(float u, float z) => StageDerive.LaneX(cfg, dCopy, u, L.layerF, z);

            float xLeftNear = XAt(-1f, n0);
            float xRightNear = XAt(1f, n0);
            float xLeftFar = XAt(-1f, f0);
            float xRightFar = XAt(1f, f0);

            geo.y = L.y;
            geo.near = n0;
            geo.far = f0;
            geo.hardFarEdge = cfg.hardFarEdge;

            if (L.fillAlpha > 0.001f)
            {
                geo.hasPlane = true;
                geo.planeVertices = new[]
                {
                    new Vector3(xLeftNear, L.y, n0),
                    new Vector3(xRightNear, L.y, n0),
                    new Vector3(xRightFar, L.y, f0),
                    new Vector3(xLeftFar, L.y, f0),
                };
                geo.planeTriangles = new[] { 0, 1, 2, 0, 2, 3 };
                geo.planeColor = ColorFromHex(L.grid);
                geo.planeAlpha = L.fillAlpha;
            }

            int step = Mathf.Max(1, Mathf.RoundToInt(L.step));
            var pts = new List<Vector3>();
            for (int k = 0; k <= cfg.cells; k++)
            {
                if (k % step != 0 && k != cfg.cells) continue;
                float u = -1f + (2f * k) / cfg.cells;
                pts.Add(new Vector3(XAt(u, n0), L.y, n0));
                pts.Add(new Vector3(XAt(u, f0), L.y, f0));
            }
            if (cfg.hardFarEdge)
            {
                pts.Add(new Vector3(xLeftFar, L.y, f0));
                pts.Add(new Vector3(xRightFar, L.y, f0));
            }

            if (pts.Count > 0)
            {
                geo.hasLines = true;
                geo.lineVertices = pts.ToArray();
                geo.lineIndices = new int[geo.lineVertices.Length];
                for (int i = 0; i < geo.lineIndices.Length; i++) geo.lineIndices[i] = i;
                geo.lineColor = ColorFromHex(L.color);
                geo.lineAlpha = 0.45f;
            }

            return true;
        }
    }
}
