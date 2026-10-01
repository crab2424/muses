using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Muses.Stage.Background
{
    /// <summary>
    /// 画面上で一定幅の線（細い帯メッシュ）。移植元: design-lab/stage-bg/shared/lines.js の buildLines / lineKit。
    /// 線分1本 = 4頂点。頂点の並びと属性は MusesBackground.hlsl の BgLineExpand を参照。
    /// 座標はそのまま頂点に入る（ラボ座標から写すときは呼び出し側で BackgroundContext.Lab を通す）。
    /// </summary>
    public sealed class BgLineKit
    {
        public struct Seg
        {
            public Vector3 a, b;
            public Color c; // linear。強さ k は掛け済み
            public float p;
        }

        public readonly List<Seg> segs = new List<Seg>();

        /// <summary>ラボの seg(a, b, c, k, p)。色 c は linear の rgb（ラボの配列値そのまま）</summary>
        public void SegLine(Vector3 a, Vector3 b, Vector3 c, float k = 1f, float p = 0f)
        {
            segs.Add(new Seg { a = a, b = b, c = new Color(c.x * k, c.y * k, c.z * k, 1f), p = p });
        }

        /// <summary>ラボの box(cx, cy, cz, w, h, d, c, k, p)。中心と寸法は呼び出し側の座標系のまま（軸に平行な直方体の12辺）</summary>
        public void Box(Vector3 center, Vector3 size, Vector3 c, float k = 1f, float p = 0f)
        {
            Vector3 h = size * 0.5f;
            float x0 = center.x - h.x, x1 = center.x + h.x, y0 = center.y - h.y, y1 = center.y + h.y, z0 = center.z - h.z, z1 = center.z + h.z;
            var V = new[]
            {
                new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x0, y1, z0),
                new Vector3(x0, y0, z1), new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1),
            };
            int[] E = { 0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
            for (int i = 0; i < E.Length; i += 2) SegLine(V[E[i]], V[E[i + 1]], c, k, p);
        }

        /// <summary>ラボの circle(o, r, n, axis, c, k, p)。axis = 'x' | 'y' | 'z'</summary>
        public void Circle(Vector3 o, float r, int n, char axis, Vector3 c, float k = 1f, float p = 0f)
        {
            Vector3 Pt(float a)
            {
                float u = Mathf.Cos(a) * r, v = Mathf.Sin(a) * r;
                return axis == 'y' ? new Vector3(o.x + u, o.y, o.z + v)
                     : axis == 'x' ? new Vector3(o.x, o.y + u, o.z + v)
                     : new Vector3(o.x + u, o.y + v, o.z);
            }
            for (int i = 0; i < n; i++)
                SegLine(Pt(i * Mathf.PI * 2f / n), Pt((i + 1) * Mathf.PI * 2f / n), c, k, p);
        }

        /// <summary>帯メッシュを作る。extra は線分ごとの追加属性（TEXCOORD3、任意。テーマ側で島番号などに使う）</summary>
        public Mesh BuildMesh(string name, IReadOnlyList<Vector4> extra = null)
        {
            int n = segs.Count;
            var pos = new Vector3[n * 4];
            var b = new Vector3[n * 4];
            var s = new Vector2[n * 4];
            var p = new Vector2[n * 4];
            var col = new Color[n * 4];
            var ex = extra != null ? new Vector4[n * 4] : null;
            var idx = new int[n * 6];
            for (int i = 0; i < n; i++)
            {
                var g = segs[i];
                for (int j = 0; j < 4; j++)
                {
                    int v = i * 4 + j;
                    pos[v] = g.a; b[v] = g.b;
                    s[v] = new Vector2(j < 2 ? 0f : 1f, (j % 2) == 1 ? 1f : -1f);
                    p[v] = new Vector2(g.p, 0f);
                    col[v] = g.c;
                    if (ex != null) ex[v] = extra[i];
                }
                // 画面上で反時計回り（lines.js と同じ。Cull Off なので向きは問わないが揃えておく）
                int o = i * 6, q = i * 4;
                idx[o] = q; idx[o + 1] = q + 3; idx[o + 2] = q + 1; idx[o + 3] = q; idx[o + 4] = q + 2; idx[o + 5] = q + 3;
            }
            var mesh = new Mesh { name = name };
            if (n * 4 > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(pos);
            mesh.SetUVs(0, b);
            mesh.SetUVs(1, s);
            mesh.SetUVs(2, p);
            if (ex != null) mesh.SetUVs(3, ex);
            mesh.SetColors(col);
            mesh.SetTriangles(idx, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f); // 頂点はシェーダで動くので判定させない
            return mesh;
        }
    }

    public static class BgMeshUtil
    {
        /// <summary>スクリーン空間の板（頂点 xy = NDC、y 上向き）。BgScreenClip で描く</summary>
        public static Mesh ScreenQuad(string name, float x0, float y0, float x1, float y1)
        {
            var mesh = new Mesh { name = name };
            mesh.SetVertices(new[] { new Vector3(x0, y0, 0), new Vector3(x1, y0, 0), new Vector3(x1, y1, 0), new Vector3(x0, y1, 0) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f);
            return mesh;
        }

        /// <summary>sRGB の #rrggbb を linear の Vector3 へ（頂点色・シェーダ定数にラボと同じ値を入れるため）</summary>
        public static Vector3 HexLinear(string hex)
        {
            ColorUtility.TryParseHtmlString(hex, out var c);
            var l = c.linear;
            return new Vector3(l.r, l.g, l.b);
        }
    }
}
