using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Muses.Stage.Background.Themes
{
    /// <summary>
    /// 天上の聖域（design-lab/stage-bg/themes/sanctuary-r3.js の移植）。設計は memory/game/stage-bg-unity-port.md。
    /// 浮島・ガラス・金の輪は「全インスタンスを1メッシュに焼き」、頂点の uv0.y にインスタンス番号を持たせる。浮遊・自転はシェーダが
    /// マテリアルの配列（_InstM0/1/2 = 配置行列の行、_InstSeed = aSeed）と _MusesBgTime から計算する（SanctuaryCommon.hlsl）。
    /// メッシュの座標・法線は「ラボの局所座標」のまま焼き、シェーダ内でラボ座標のまま変換してから z を反転する。
    /// 乱数列（rs=12345 / rs2=777 の LCG）の呼び出し順はラボと同じ。
    /// </summary>
    public sealed class SanctuaryBackground : BackgroundBuilder
    {
        public override Color ClearColor => StageTint.Hex("#8fb0d8");

        public override StageTint? Tint => new StageTint
        {
            groundFill = StageTint.Hex("#232848"), groundFillAlpha = 1f,
            groundLine = StageTint.Hex("#e8e4ff"), groundLineAlpha = 0.38f,
            skyFill = StageTint.Hex("#3f4878"), skyFillAlpha = 0.22f,
            skyLine = StageTint.Hex("#e8e4ff"), skyLineAlpha = 0.34f,
        };

        private const float CloudY = -9f;

        // ---------------------------------------------------------------- 小道具
        /// <summary>ラボの rnd / rnd2（LCG）</summary>
        private sealed class Lcg
        {
            private uint s;
            public Lcg(uint seed) { s = seed; }
            public float Next() { s = unchecked(s * 1664525u + 1013904223u); return (float)(s / 4294967296.0); }
        }

        private struct Item { public float x, y, z, sx, sy, sz, rx, ry, rz; }

        private static Matrix4x4 Rot(float rx, float ry, float rz)
        {
            // three.js の Euler 'XYZ' = Rx * Ry * Rz（標準の右手系回転行列）
            float cx = Mathf.Cos(rx), sx = Mathf.Sin(rx), cy = Mathf.Cos(ry), sy = Mathf.Sin(ry), cz = Mathf.Cos(rz), sz = Mathf.Sin(rz);
            var X = Matrix4x4.identity; X[1, 1] = cx; X[1, 2] = -sx; X[2, 1] = sx; X[2, 2] = cx;
            var Y = Matrix4x4.identity; Y[0, 0] = cy; Y[0, 2] = sy; Y[2, 0] = -sy; Y[2, 2] = cy;
            var Z = Matrix4x4.identity; Z[0, 0] = cz; Z[0, 1] = -sz; Z[1, 0] = sz; Z[1, 1] = cz;
            return X * Y * Z;
        }

        /// <summary>ラボの Matrix4.compose(position, quaternion(Euler XYZ), scale) = T * Rx*Ry*Rz * S</summary>
        private static Matrix4x4 Compose(float x, float y, float z, float sx, float sy, float sz, float rx, float ry, float rz)
        {
            var m = Rot(rx, ry, rz);
            for (int r = 0; r < 3; r++) { m[r, 0] *= sx; m[r, 1] *= sy; m[r, 2] *= sz; }
            m[0, 3] = x; m[1, 3] = y; m[2, 3] = z;
            return m;
        }

        /// <summary>ラボの T(x,y,z, sx,sy,sz, ry, rz, rx)（島のパーツ配置用。引数の並びがラボと同じ）</summary>
        private static Matrix4x4 TM(float x, float y, float z, float sx = 1f, float sy = 1f, float sz = 1f, float ry = 0f, float rz = 0f, float rx = 0f)
            => Compose(x, y, z, sx, sy, sz, rx, ry, rz);

        private static Matrix4x4 InstMatrix(Item it) => Compose(it.x, it.y, it.z, it.sx, it.sy, it.sz, it.rx, it.ry, it.rz);

        private static void SetInstances(Material mat, List<Item> items, List<Vector4> seeds)
        {
            int n = items.Count;
            var r0 = new Vector4[n]; var r1 = new Vector4[n]; var r2 = new Vector4[n]; var sd = new Vector4[n];
            for (int i = 0; i < n; i++)
            {
                var m = InstMatrix(items[i]);
                r0[i] = new Vector4(m[0, 0], m[0, 1], m[0, 2], m[0, 3]);
                r1[i] = new Vector4(m[1, 0], m[1, 1], m[1, 2], m[1, 3]);
                r2[i] = new Vector4(m[2, 0], m[2, 1], m[2, 2], m[2, 3]);
                sd[i] = seeds[i];
            }
            mat.SetVectorArray("_InstM0", r0);
            mat.SetVectorArray("_InstM1", r1);
            mat.SetVectorArray("_InstM2", r2);
            mat.SetVectorArray("_InstSeed", sd);
        }

        /// <summary>ラボの inst() の乱数部分: aSeed = (rnd, rnd, (rnd-0.5)*2, rnd)</summary>
        private static Vector4 NextSeed(Lcg rnd)
        {
            float a = rnd.Next(), b = rnd.Next(), c = (rnd.Next() - 0.5f) * 2f, d = rnd.Next();
            return new Vector4(a, b, c, d);
        }

        /// <summary>焼く前の1インスタンス分のジオメトリ（ラボの局所座標。非インデックスの三角形リスト）</summary>
        private sealed class Surf
        {
            public readonly List<Vector3> P = new List<Vector3>();
            public readonly List<Vector3> N = new List<Vector3>();
            public readonly List<float> K = new List<float>();
            public readonly List<Vector3> Bary = new List<Vector3>();

            /// <summary>三角形を足す。refLocal（パーツ内部の点、局所）から見て外向きになるよう巻きを揃え、面法線を付ける（computeVertexNormals と同じ平面法）</summary>
            public void Tri(Vector3 a, Vector3 b, Vector3 c, Matrix4x4 m, Vector3 refLocal, float kind)
            {
                a = m.MultiplyPoint3x4(a); b = m.MultiplyPoint3x4(b); c = m.MultiplyPoint3x4(c);
                Vector3 rf = m.MultiplyPoint3x4(refLocal);
                Vector3 n = Vector3.Cross(b - a, c - a);
                if (Vector3.Dot(n, (a + b + c) / 3f - rf) < 0f) { var t = b; b = c; c = t; n = -n; }
                Emit(a, b, c, n.sqrMagnitude > 1e-20f ? n.normalized : Vector3.zero, kind);
            }

            /// <summary>巻きをそのまま使う三角形（旋盤状の岩・ガラスなど、ラボが巻きを決めているもの）</summary>
            public void TriRaw(Vector3 a, Vector3 b, Vector3 c, float kind)
            {
                Vector3 n = Vector3.Cross(b - a, c - a);
                Emit(a, b, c, n.sqrMagnitude > 1e-20f ? n.normalized : Vector3.zero, kind);
            }

            /// <summary>頂点法線つき（トーラス・光点）。頂点法線の平均に合わせて巻きを揃える</summary>
            public void TriSmooth(Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc, float kind)
            {
                if (Vector3.Dot(Vector3.Cross(b - a, c - a), na + nb + nc) < 0f) { var t = b; b = c; c = t; t = nb; nb = nc; nc = t; }
                P.Add(a); P.Add(b); P.Add(c); N.Add(na); N.Add(nb); N.Add(nc);
                K.Add(kind); K.Add(kind); K.Add(kind);
            }

            private void Emit(Vector3 a, Vector3 b, Vector3 c, Vector3 n, float kind)
            {
                P.Add(a); P.Add(b); P.Add(c);
                N.Add(n); N.Add(n); N.Add(n);
                K.Add(kind); K.Add(kind); K.Add(kind);
            }

            /// <summary>三角形の頂点順を保ったまま重心座標を付ける（ガラス用）</summary>
            public void TriBary(Vector3 a, Vector3 b, Vector3 c, Vector3 n)
            {
                P.Add(a); P.Add(b); P.Add(c); N.Add(n); N.Add(n); N.Add(n);
                K.Add(0); K.Add(0); K.Add(0);
                Bary.Add(new Vector3(1, 0, 0)); Bary.Add(new Vector3(0, 1, 0)); Bary.Add(new Vector3(0, 0, 1));
            }

            public void Box(float w, float h, float d, Matrix4x4 m, float kind)
            {
                float x = w / 2, y = h / 2, z = d / 2;
                var V = new[]
                {
                    new Vector3(-x, -y, -z), new Vector3(x, -y, -z), new Vector3(x, y, -z), new Vector3(-x, y, -z),
                    new Vector3(-x, -y, z), new Vector3(x, -y, z), new Vector3(x, y, z), new Vector3(-x, y, z),
                };
                int[][] F = { new[] { 0, 1, 2, 3 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 }, new[] { 3, 2, 6, 7 }, new[] { 0, 3, 7, 4 }, new[] { 1, 2, 6, 5 } };
                foreach (var f in F)
                {
                    Tri(V[f[0]], V[f[1]], V[f[2]], m, Vector3.zero, kind);
                    Tri(V[f[0]], V[f[2]], V[f[3]], m, Vector3.zero, kind);
                }
            }

            /// <summary>CylinderGeometry(rTop, rBot, h, n, 1)。頂点角は x=sin, z=cos（three.js と同じ）。open=false で上下の蓋つき</summary>
            public void Cyl(float rTop, float rBot, float h, int n, Matrix4x4 m, float kind, bool open = false)
            {
                Vector3 T(int k) { float th = (float)k / n * Mathf.PI * 2f; return new Vector3(rTop * Mathf.Sin(th), h / 2, rTop * Mathf.Cos(th)); }
                Vector3 B(int k) { float th = (float)k / n * Mathf.PI * 2f; return new Vector3(rBot * Mathf.Sin(th), -h / 2, rBot * Mathf.Cos(th)); }
                for (int k = 0; k < n; k++)
                {
                    int k2 = (k + 1) % n;
                    Tri(T(k), B(k), T(k2), m, Vector3.zero, kind);
                    Tri(B(k), B(k2), T(k2), m, Vector3.zero, kind);
                    if (!open)
                    {
                        Tri(new Vector3(0, h / 2, 0), T(k), T(k2), m, Vector3.zero, kind);
                        Tri(new Vector3(0, -h / 2, 0), B(k), B(k2), m, Vector3.zero, kind);
                    }
                }
            }

            /// <summary>ConeGeometry(radius, length, n, 1, openEnded=true).rotateX(PI): 先端が下（-length/2）、底の輪が上（+length/2）</summary>
            public void ConeDown(float radius, float length, int n, Matrix4x4 m, float kind)
            {
                var apex = new Vector3(0, -length / 2, 0);
                Vector3 Bs(int k) { float th = (float)k / n * Mathf.PI * 2f; return new Vector3(radius * Mathf.Sin(th), length / 2, -radius * Mathf.Cos(th)); }
                var inside = new Vector3(0, length / 4, 0);
                for (int k = 0; k < n; k++) Tri(apex, Bs(k), Bs((k + 1) % n), m, inside, kind);
            }

            /// <summary>
            /// 全インスタンス分を1メッシュに焼く。uv0 = (aKind, インスタンス番号)。withBary のとき uv1 = 重心座標。
            /// </summary>
            public Mesh Bake(string name, int instances, bool withBary)
            {
                int nv = P.Count;
                var pos = new List<Vector3>(nv * instances);
                var nor = new List<Vector3>(nv * instances);
                var uv0 = new List<Vector2>(nv * instances);
                List<Vector3> uv1 = withBary ? new List<Vector3>(nv * instances) : null;
                for (int i = 0; i < instances; i++)
                {
                    pos.AddRange(P); nor.AddRange(N);
                    for (int v = 0; v < nv; v++) uv0.Add(new Vector2(K[v], i));
                    if (withBary) uv1.AddRange(Bary);
                }
                int total = pos.Count;
                var idx = new int[total];
                for (int i = 0; i < total; i++) idx[i] = i;
                var mesh = new Mesh { name = name };
                if (total > 65535) mesh.indexFormat = IndexFormat.UInt32;
                mesh.SetVertices(pos);
                mesh.SetNormals(nor);
                mesh.SetUVs(0, uv0);
                if (withBary) mesh.SetUVs(1, uv1);
                mesh.SetIndices(idx, MeshTopology.Triangles, 0);
                mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f);
                return mesh;
            }
        }

        // ---------------------------------------------------------------- 岩の形（旋盤状）
        private const float RockTop = -0.08f, RockLen = 1.5f, RockR0 = 0.945f;

        private static double RockR(double a, double t)
        {
            double k = Math.Min(1.0, t * 5);
            double shelf = 0.05 * Math.Sin(t * 20 + Math.Sin(a * 2.0) * 1.5);
            double bump = 0.09 * Math.Sin(a * 3 + t * 4) + 0.05 * Math.Sin(a * 7 - t * 6) + 0.03 * Math.Sin(a * 13 + t * 9);
            return RockR0 * Math.Pow(1 - t, 0.85) * (1 + (shelf + bump) * k);
        }

        private static Vector3 RockPt(double a, double t, double o = 1.0)
        {
            double r = RockR(a, t) * o;
            return new Vector3((float)(r * Math.Cos(a) + 0.12 * t * t), (float)(RockTop - RockLen * t), (float)(r * Math.Sin(a)));
        }

        private static Vector3 CylPt(float cx, float cz, float r, float y, int k, int n)
        {
            float th = (float)k / n * Mathf.PI * 2f;
            return new Vector3(cx + r * Mathf.Sin(th), y, cz + r * Mathf.Cos(th));
        }

        // ---------------------------------------------------------------- Build
        public override void Build(BackgroundContext ctx, BackgroundParts parts)
        {
            var rnd = new Lcg(12345);
            var rnd2 = new Lcg(777);
            const float PI = Mathf.PI;

            // ステージ半幅の線形近似（地上）: half(depth) = a + b*depth
            float zA = ctx.d.zJudge, zB = ctx.d.zFar;
            float hA = ctx.LaneX(1f, 0f, zA), hB = ctx.LaneX(1f, 0f, zB);
            float hb = (hB - hA) / (zB - zA), ha = hA - hb * zA;

            // ======================= 浮島のジオメトリと線（乱数は rnd2 のみ。呼び出し順はラボと同じ）=======================
            var island = new Surf();
            var iLines = new BgLineKit();
            var C_DARK = new Vector3(0.05f, 0.04f, 0.17f);
            var C_INK = new Vector3(0.18f, 0.16f, 0.42f);
            var C_LITE = new Vector3(1.0f, 0.97f, 0.84f);
            var C_WARM = new Vector3(0.62f, 0.52f, 0.80f);
            var C_GRN = new Vector3(0.80f, 0.98f, 0.78f);
            void ln(Vector3 a, Vector3 b, Vector3 c, float alpha) => iLines.SegLine(a, b, c, 1f, alpha);
            void poly(IList<Vector3> pts, Vector3 c, float alpha, bool closed = false)
            {
                for (int i = 0; i < pts.Count - 1; i++) ln(pts[i], pts[i + 1], c, alpha);
                if (closed) ln(pts[pts.Count - 1], pts[0], c, alpha);
            }
            Vector3[] ring(float cx, float cz, float r, float y, int n)
            {
                var a = new Vector3[n];
                for (int k = 0; k < n; k++) a[k] = CylPt(cx, cz, r, y, k, n);
                return a;
            }
            void boxPart(float w, float h, float dd, float kind, Matrix4x4 m, float aT, float aO)
            {
                island.Box(w, h, dd, m, kind);
                float hx = w / 2, hy = h / 2, hz = dd / 2;
                var V = new[]
                {
                    new Vector3(-hx, -hy, -hz), new Vector3(hx, -hy, -hz), new Vector3(hx, hy, -hz), new Vector3(-hx, hy, -hz),
                    new Vector3(-hx, -hy, hz), new Vector3(hx, -hy, hz), new Vector3(hx, hy, hz), new Vector3(-hx, hy, hz),
                };
                for (int i = 0; i < V.Length; i++) V[i] = m.MultiplyPoint3x4(V[i]);
                int[,] top = { { 2, 3 }, { 6, 7 }, { 2, 6 }, { 3, 7 } };
                int[,] oth = { { 0, 1 }, { 4, 5 }, { 0, 4 }, { 1, 5 }, { 0, 3 }, { 1, 2 }, { 4, 7 }, { 5, 6 } };
                for (int i = 0; i < 4; i++) ln(V[top[i, 0]], V[top[i, 1]], C_LITE, aT);
                for (int i = 0; i < 8; i++) ln(V[oth[i, 0]], V[oth[i, 1]], C_INK, aO);
            }

            // --- 上面（円盤、28角）と縁の線
            const int NR = 28; const float y0 = 0.08f;
            island.Cyl(1.0f, 0.94f, 0.16f, NR, TM(0, 0, 0), 1);
            poly(ring(0, 0, 0.998f, y0 + 0.002f, NR), C_LITE, 0.85f, true);   // 縁の明るい輪郭
            poly(ring(0, 0, 0.90f, y0 + 0.002f, NR), C_GRN, 0.30f, true);     // 内側の2本目
            poly(ring(0, 0, 0.945f, -0.078f, NR), C_DARK, 0.55f, true);       // 縁の下の暗い線

            // --- 岩（旋盤状。a=方位, t=0(縁の直下)〜1(先端)）
            {
                const int nA = 24, nT = 14;
                Vector3 pt(int i, int j) => RockPt((double)i / nA * Math.PI * 2, (double)j / nT);
                for (int j = 0; j < nT; j++)
                    for (int i = 0; i < nA; i++)
                    {
                        Vector3 a = pt(i, j), b = pt(i + 1, j), c = pt(i, j + 1), e = pt(i + 1, j + 1);
                        island.TriRaw(a, b, c, 0); island.TriRaw(b, e, c, 0);
                    }
            }
            // 鍾乳石（岩の下部から垂れる細い錐）。[方位, t, 長さ, 半径]
            float[][] stal =
            {
                new[] { 0.9f, 0.52f, 0.55f, 0.11f }, new[] { 2.4f, 0.44f, 0.5f, 0.10f }, new[] { 3.6f, 0.60f, 0.6f, 0.09f },
                new[] { 5.0f, 0.48f, 0.45f, 0.10f }, new[] { 5.9f, 0.66f, 0.42f, 0.07f }, new[] { 1.6f, 0.34f, 0.36f, 0.08f }, new[] { 4.3f, 0.36f, 0.4f, 0.075f },
            };
            foreach (var s in stal)
            {
                float len = s[2], rad = s[3];
                Vector3 p = RockPt(s[0], s[1], 0.96);
                island.ConeDown(rad, len, 5, TM(p.x, p.y - len / 2 + 0.04f, p.z), 0);
            }
            island.ConeDown(0.22f, 0.85f, 6, TM(0.30f, -1.02f, 0.20f), 0);

            // --- 縁から垂れる草の蔓（細い緑の筋）
            for (int i = 0; i < 26; i++)
            {
                float a = (float)i / 26 * PI * 2 + (rnd2.Next() - 0.5f) * 0.18f, len = 0.04f + rnd2.Next() * 0.11f;
                Vector3 pt(float r, float y) => new Vector3(r * Mathf.Cos(a), y, r * Mathf.Sin(a));
                var pts = new List<Vector3> { pt(0.985f, 0.03f), pt(0.975f, -0.03f), pt(0.965f, -0.075f) };
                for (int s = 1; s <= 3; s++) pts.Add(RockPt(a + Math.Sin(s * 2.1 + i) * 0.02, len * (s / 3f), 1.012));
                poly(pts, new Vector3(0.28f, 0.52f, 0.34f), 0.36f);
            }
            // --- 岩の線: 地層の筋（途切れ途切れ）と縦の割れ目
            {
                float[] tl = { 0.12f, 0.26f, 0.42f, 0.60f, 0.76f };
                for (int li = 0; li < tl.Length; li++)
                {
                    float t = tl[li];
                    const int nS = 36; bool on = rnd2.Next() < 0.7f;
                    for (int i = 0; i < nS; i++)
                    {
                        if (rnd2.Next() < 0.16f) on = !on;
                        if (!on) continue;
                        double a0 = (double)i / nS * Math.PI * 2, a1 = (double)(i + 1) / nS * Math.PI * 2;
                        ln(RockPt(a0, t, 1.008), RockPt(a1, t, 1.008), C_DARK, 0.30f);
                        ln(RockPt(a0, t - 0.014, 1.008), RockPt(a1, t - 0.014, 1.008), C_WARM, li % 2 == 0 ? 0.24f : 0.14f);
                    }
                }
                for (int c = 0; c < 10; c++)
                {
                    double a = rnd2.Next() * Math.PI * 2, t = 0.03 + rnd2.Next() * 0.12;
                    double len = 0.22 + rnd2.Next() * 0.45; const int steps = 5;
                    Vector3 prev = RockPt(a, t, 1.01);
                    for (int s = 1; s <= steps; s++)
                    {
                        a += (rnd2.Next() - 0.5) * 0.11; t += len / steps;
                        Vector3 cur = RockPt(a, Math.Min(t, 0.95), 1.01);
                        ln(prev, cur, C_DARK, 0.75f * (1f - (float)s / (steps + 1)) + 0.15f);
                        prev = cur;
                    }
                }
            }

            // --- 遺構: 2段の基壇 / 柱 / 崩れたまぐさ / 破風の断片 / 倒れた柱の胴 / 瓦礫
            boxPart(1.16f, 0.05f, 0.52f, 3, TM(-0.02f, y0 + 0.025f, 0.02f), 0.55f, 0.45f);
            boxPart(1.02f, 0.05f, 0.40f, 3, TM(-0.02f, y0 + 0.075f, 0.02f), 0.6f, 0.5f);
            for (int i = -3; i <= 3; i++) ln(new Vector3(-0.02f + i * 0.145f, y0 + 0.1005f, -0.18f), new Vector3(-0.02f + i * 0.145f, y0 + 0.1005f, 0.22f), C_INK, 0.22f);   // 基壇の目地
            ln(new Vector3(-0.5f, y0 + 0.1005f, 0.02f), new Vector3(0.46f, y0 + 0.1005f, 0.02f), C_INK, 0.18f);
            float yc = y0 + 0.10f;
            // 柱: 土台（円錐台）・胴（10角、上へ細く）・柱頭（エキヌスと平板）
            float col(float x, float z, float h, float r = 0.05f, bool capital = true)
            {
                const int NC = 10;
                island.Cyl(r * 1.35f, r * 1.5f, 0.03f, NC, TM(x, yc + 0.015f, z), 2);
                island.Cyl(r * 0.82f, r, h, NC, TM(x, yc + 0.03f + h / 2, z), 2);
                float yBase = yc + 0.03f, yTopC = yBase + h;
                poly(ring(x, z, r * 1.35f, yc + 0.031f, NC), C_INK, 0.5f, true);   // 土台の上縁
                poly(ring(x, z, r * 1.5f, yc + 0.001f, NC), C_INK, 0.35f, true);   // 土台の下縁
                for (int k = 0; k < NC; k++) ln(CylPt(x, z, r * 1.002f, yBase + 0.005f, k, NC), CylPt(x, z, r * 0.83f, yTopC - 0.005f, k, NC), C_INK, 0.30f); // 縦溝（稜線）
                if (capital)
                {
                    island.Cyl(r * 1.32f, r * 0.85f, 0.03f, NC, TM(x, yTopC + 0.015f, z), 2);
                    poly(ring(x, z, r * 0.83f, yTopC + 0.001f, NC), C_DARK, 0.45f, true);   // 胴と柱頭の境
                    boxPart(r * 3.0f, 0.03f, r * 3.0f, 2, TM(x, yTopC + 0.045f, z), 0.7f, 0.45f);
                }
                else
                {
                    poly(ring(x, z, r * 0.83f, yTopC, NC), C_LITE, 0.6f, true);   // 折れた柱: 割れ口の明るい縁
                }
                return yTopC + 0.06f;
            }
            float yTop = col(-0.42f, 0.02f, 0.5f); col(-0.14f, 0.02f, 0.5f); col(0.14f, 0.02f, 0.5f);
            col(0.52f, -0.30f, 0.20f, 0.05f, false);
            // まぐさ: 左の1本は柱の上、右の1本は崩れて傾く
            boxPart(0.50f, 0.07f, 0.15f, 2, TM(-0.28f, yTop + 0.035f, 0.02f), 0.75f, 0.55f);
            boxPart(0.34f, 0.07f, 0.15f, 2, TM(0.10f, yTop + 0.03f, 0.02f, 1, 1, 1, 0, 0.15f), 0.75f, 0.55f);
            // 破風の断片: 三角柱（頂点が上）
            {
                const float R = 0.29f;
                var m = TM(-0.28f, yTop + 0.07f + 0.0653f, 0.02f, 1, 0.45f, 1);
                var tri = new[] { new Vector3(0, R, 0.075f), new Vector3(R * Mathf.Sqrt(3) / 2, -R / 2, 0.075f), new Vector3(-R * Mathf.Sqrt(3) / 2, -R / 2, 0.075f) };
                var tri2 = new Vector3[3];
                for (int i = 0; i < 3; i++) tri2[i] = new Vector3(tri[i].x, tri[i].y, -0.075f);
                // 面（側面3枚の四角＋前後の三角）
                for (int i = 0; i < 3; i++)
                {
                    int j = (i + 1) % 3;
                    island.Tri(tri[i], tri[j], tri2[j], m, Vector3.zero, 2);
                    island.Tri(tri[i], tri2[j], tri2[i], m, Vector3.zero, 2);
                }
                island.Tri(tri[0], tri[1], tri[2], m, Vector3.zero, 2);
                island.Tri(tri2[0], tri2[1], tri2[2], m, Vector3.zero, 2);
                Vector3 W(Vector3 p) => m.MultiplyPoint3x4(p);
                for (int i = 0; i < 3; i++)
                {
                    int j = (i + 1) % 3;
                    ln(W(tri[i]), W(tri[j]), i == 0 || i == 2 ? C_LITE : C_INK, i == 1 ? 0.5f : 0.75f);
                    ln(W(tri2[i]), W(tri2[j]), C_INK, 0.5f);
                    ln(W(tri[i]), W(tri2[i]), C_INK, 0.45f);
                }
            }
            // 倒れた柱の胴と瓦礫
            island.Cyl(0.048f, 0.05f, 0.22f, 10, TM(0.26f, y0 + 0.05f, 0.32f, 1, 1, 1, 0.6f, PI / 2), 2);
            boxPart(0.09f, 0.05f, 0.07f, 2, TM(-0.52f, y0 + 0.025f, 0.34f, 1, 1, 1, 0.4f), 0.6f, 0.45f);
            boxPart(0.06f, 0.04f, 0.06f, 2, TM(0.64f, y0 + 0.02f, 0.06f, 1, 1, 1, -0.5f), 0.6f, 0.45f);
            boxPart(0.07f, 0.035f, 0.05f, 2, TM(-0.10f, y0 + 0.0175f, -0.34f, 1, 1, 1, 0.9f), 0.55f, 0.4f);

            // ======================= 浮島の配置（rnd: ry）と、雲海に落ちる影 =======================
            var islandItems = new List<Item>();
            {
                float[][] src =
                {
                    new[] { -17f, -1f, -18f, 4.6f }, new[] { 19f, -3f, -26f, 5.4f },
                    new[] { -30f, 1.5f, -40f, 7.0f }, new[] { 27f, 2f, -54f, 6.4f },
                    new[] { -13.5f, 4f, -13f, 2.4f }, new[] { 34f, -2f, -14f, 3.2f },
                };
                foreach (var s in src)
                    islandItems.Add(new Item { x = s[0], y = s[1], z = s[2], sx = s[3], sy = s[3] * 0.85f, sz = s[3], ry = rnd.Next() * 6f });
            }
            var islVec = new Vector4[6];
            for (int i = 0; i < 6; i++)
            {
                var it = islandItems[i];
                float h = it.y - CloudY;
                float rr = it.sx * 0.95f + h * 0.05f;
                islVec[i] = new Vector4(it.x + 0.4375f * h, it.z + 0.5625f * h, rr * rr * 0.8f, 0f);
            }
            var islandSeeds = new List<Vector4>();
            for (int i = 0; i < islandItems.Count; i++) islandSeeds.Add(NextSeed(rnd));

            // ======================= ガラスの破片（rnd: z, s, x, y, rx, rz, ry の順）=======================
            var glassItems = new List<Item>();
            for (int i = 0; i < 22; i++)
            {
                float side = (i % 2 == 1) ? 1f : -1f, z = -(9f + rnd.Next() * 55f);
                float s = 0.7f + rnd.Next() * 1.3f;
                float x = side * (12f + rnd.Next() * 12f + (-z) * 0.28f);
                float y = -3f + rnd.Next() * 9f;
                float rx = (rnd.Next() - 0.5f) * 1.2f, rz = (rnd.Next() - 0.5f) * 1.2f, ry = rnd.Next() * 6f;
                glassItems.Add(new Item { x = x, y = y, z = z, sx = s * 0.55f, sy = s * 2.2f, sz = s * 0.4f, rx = rx, ry = ry, rz = rz });
            }
            var glassSeeds = new List<Vector4>();
            for (int i = 0; i < glassItems.Count; i++) glassSeeds.Add(NextSeed(rnd));
            var glass = new Surf();
            {
                const int n = 6;
                var top = new Vector3(0.05f, 1.0f, 0.0f); var bot = new Vector3(-0.05f, -1.0f, 0.03f);
                var A = new Vector3[n]; var B = new Vector3[n];
                for (int i = 0; i < n; i++)
                {
                    float a = (float)i / n * PI * 2 + 0.2f * Mathf.Sin(i * 2.3f);
                    float ra = 0.95f + 0.12f * Mathf.Sin(i * 1.7f + 0.5f), rb = 0.80f + 0.1f * Mathf.Cos(i * 2.1f);
                    A[i] = new Vector3(ra * Mathf.Cos(a), 0.30f, ra * Mathf.Sin(a));
                    B[i] = new Vector3(rb * Mathf.Cos(a + 0.15f), -0.22f, rb * Mathf.Sin(a + 0.15f));
                }
                void tri(Vector3 a, Vector3 b, Vector3 c)
                {
                    Vector3 nn = Vector3.Cross(b - a, c - a), cen = (a + b + c) / 3f;
                    if (Vector3.Dot(nn, cen) < 0f) { var t = b; b = c; c = t; nn = -nn; }
                    glass.TriBary(a, b, c, nn.normalized);
                }
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    tri(top, A[j], A[i]);
                    tri(A[i], A[j], B[j]); tri(A[i], B[j], B[i]);
                    tri(B[i], B[j], bot);
                }
            }

            // ======================= 金の光輪（rnd: seed のみ）=======================
            var ringItems = new List<Item>();
            {
                float[][] spec = { new[] { -26f, 4.5f, -22f, 8.5f }, new[] { 27f, 6f, -34f, 9.5f }, new[] { -33f, 8f, -50f, 12f }, new[] { 15.5f, 9f, -14f, 4.5f } };
                for (int i = 0; i < spec.Length; i++)
                {
                    var s = spec[i];
                    ringItems.Add(new Item { x = s[0], y = s[1], z = s[2], sx = s[3], sy = s[3], sz = s[3], ry = (s[0] < 0 ? 1f : -1f) * 0.5f, rx = -0.5f + i * 0.05f });
                }
            }
            var ringSeeds = new List<Vector4>();
            for (int i = 0; i < ringItems.Count; i++) ringSeeds.Add(NextSeed(rnd));
            var ringSurf = new Surf();
            {
                // TorusGeometry(1, 0.05, 8, 128)。輪は XY 平面（軸 Z）
                const int tub = 128, rad = 8; const float R = 1f, r = 0.05f;
                Vector3 V(int i, int j, out Vector3 nrm)
                {
                    float u = (float)i / tub * PI * 2, v = (float)j / rad * PI * 2;
                    var c = new Vector3(R * Mathf.Cos(u), R * Mathf.Sin(u), 0f);
                    var p = new Vector3((R + r * Mathf.Cos(v)) * Mathf.Cos(u), (R + r * Mathf.Cos(v)) * Mathf.Sin(u), r * Mathf.Sin(v));
                    nrm = (p - c).normalized;
                    return p;
                }
                for (int j = 0; j < rad; j++)
                    for (int i = 0; i < tub; i++)
                    {
                        var a = V(i, j, out var na); var b = V(i + 1, j, out var nb);
                        var c = V(i, j + 1, out var nc); var d = V(i + 1, j + 1, out var nd);
                        ringSurf.TriSmooth(a, b, c, na, nb, nc, 0);
                        ringSurf.TriSmooth(b, d, c, nb, nd, nc, 0);
                    }
                // 周回する光点3つ（半径0.06の球。ラボは IcosahedronGeometry(0.06, 2)）
                for (int d = 0; d < 3; d++)
                {
                    float ang = (float)d / 3 * PI * 2 + 0.4f;
                    var ctr = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0f);
                    const int st = 8, sl = 12; const float sr = 0.06f;
                    Vector3 S(int a, int b, out Vector3 nrm)
                    {
                        float ph = (float)a / st * PI, th = (float)b / sl * PI * 2;
                        nrm = new Vector3(Mathf.Sin(ph) * Mathf.Cos(th), Mathf.Cos(ph), Mathf.Sin(ph) * Mathf.Sin(th));
                        return ctr + nrm * sr;
                    }
                    for (int a = 0; a < st; a++)
                        for (int b = 0; b < sl; b++)
                        {
                            var p00 = S(a, b, out var n00); var p10 = S(a + 1, b, out var n10);
                            var p01 = S(a, b + 1, out var n01); var p11 = S(a + 1, b + 1, out var n11);
                            ringSurf.TriSmooth(p00, p10, p01, n00, n10, n01, 1);
                            ringSurf.TriSmooth(p10, p11, p01, n10, n11, n01, 1);
                        }
                }
            }
            // 輪の線（内側の細い2本目・外側の細い輪・目盛り・外側の破線環）。色は lines.js が色空間変換なしで出していた「表示値」のまま入れる（シェーダ側で 2.2 乗）
            var rLines = new BgLineKit();
            {
                var gold = new Vector3(0.96f, 0.66f, 0.20f); var goldD = new Vector3(0.90f, 0.58f, 0.18f);
                Vector3 pt(float r, float a) => new Vector3(r * Mathf.Cos(a), r * Mathf.Sin(a), 0f);
                const int NA = 96;
                for (int i = 0; i < NA; i++)
                {
                    float a0 = (float)i / NA * PI * 2, a1 = (float)(i + 1) / NA * PI * 2;
                    rLines.SegLine(pt(0.86f, a0), pt(0.86f, a1), gold, 0.75f, 0);
                    rLines.SegLine(pt(1.09f, a0), pt(1.09f, a1), goldD, 0.30f, 0);
                }
                for (int i = 0; i < 72; i++)
                {
                    float a = (float)i / 72 * PI * 2; bool lg = i % 6 == 0;
                    rLines.SegLine(pt(lg ? 0.855f : 0.90f, a), pt(0.945f, a), gold, lg ? 0.85f : 0.5f, 1);
                }
                for (int i = 0; i < 30; i++)
                {
                    float a0 = (float)i / 30 * PI * 2, a1 = a0 + (PI * 2 / 30) * 0.55f;
                    for (int s = 0; s < 3; s++) rLines.SegLine(pt(1.15f, a0 + (a1 - a0) * (s / 3f)), pt(1.15f, a0 + (a1 - a0) * ((s + 1) / 3f)), goldD, 0.6f, 2);
                    rLines.SegLine(pt(1.15f, a0), pt(1.19f, a0), goldD, 0.6f, 2);
                }
            }

            // ======================= 光芒（rnd: ray ごとの seed）=======================
            Mesh raysMesh;
            {
                var pos = new List<Vector3>(); var uv = new List<Vector2>(); var sd = new List<Vector2>(); var idx = new List<int>();
                const int rays = 8;
                for (int i = 0; i < rays; i++)
                {
                    float side = (i % 2 == 1) ? 1f : -1f; int k = i >> 1;
                    float sx = side * (6 + k * 7), ex = side * (26 + k * 13);
                    float[] S = { sx, 34f, -96f }, E = { ex, -8f, -22f - k * 4f };
                    float w0 = 1.5f, w1 = 4.5f + k * 1.2f;
                    int b = pos.Count;
                    pos.Add(BackgroundContext.Lab(S[0] - w0, S[1], S[2])); pos.Add(BackgroundContext.Lab(S[0] + w0, S[1], S[2]));
                    pos.Add(BackgroundContext.Lab(E[0] + w1, E[1], E[2])); pos.Add(BackgroundContext.Lab(E[0] - w1, E[1], E[2]));
                    uv.Add(new Vector2(-1, 0)); uv.Add(new Vector2(1, 0)); uv.Add(new Vector2(1, 1)); uv.Add(new Vector2(-1, 1));
                    float s = rnd.Next();
                    for (int v = 0; v < 4; v++) sd.Add(new Vector2(s, 0));
                    idx.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }
                raysMesh = new Mesh { name = "SanctuaryRays" };
                raysMesh.SetVertices(pos); raysMesh.SetUVs(0, uv); raysMesh.SetUVs(1, sd);
                raysMesh.SetTriangles(idx, 0);
            }

            // ======================= 雲海（全画面の板）order -100 =======================
            {
                var mat = parts.NewMaterial("SanctuaryCloud");
                mat.SetVector("_StageHalf", new Vector4(ha, hb, 0, 0));
                mat.SetVectorArray("_Isl", islVec);
                parts.Add("SanctuaryCloud", BgMeshUtil.ScreenQuad("SanctuaryCloud", -1f, -1f, 1f, 1f), mat, -100);
            }

            // ======================= 浮島（不透明）order -99。ラボは不透明パスで雲海の直後に描かれる =======================
            {
                var mat = parts.NewMaterial("SanctuaryIsland");
                mat.SetFloat("_Spin", 0.03f); mat.SetFloat("_Bob", 0.55f);
                SetInstances(mat, islandItems, islandSeeds);
                parts.Add("SanctuaryIsland", island.Bake("SanctuaryIsland", islandItems.Count, false), mat, -99);
            }

            // ======================= 島の線 order -30 =======================
            {
                var kit = new BgLineKit(); var extra = new List<Vector4>();
                for (int i = 0; i < islandItems.Count; i++)
                    foreach (var sg in iLines.segs) { kit.segs.Add(sg); extra.Add(new Vector4(i, 0, 0, 0)); }
                var mat = parts.NewMaterial("SanctuaryLines");
                mat.SetFloat("_WidthPx", 1.25f); mat.SetFloat("_Opacity", 1f); mat.SetFloat("_Kind", 0f);
                mat.SetFloat("_Spin", 0.03f); mat.SetFloat("_Bob", 0.55f); mat.SetFloat("_Bias", 0.03f);
                mat.SetFloat("_RotOn", 0f); mat.SetFloat("_RotSpd", 0f);
                SetInstances(mat, islandItems, islandSeeds);
                parts.Add("SanctuaryIslandLines", kit.BuildMesh("SanctuaryIslandLines", extra), mat, -30);
            }

            // ======================= ガラス order -40 =======================
            {
                var mat = parts.NewMaterial("SanctuaryGlass");
                mat.SetFloat("_Spin", 0.12f); mat.SetFloat("_Bob", 0.7f);
                SetInstances(mat, glassItems, glassSeeds);
                parts.Add("SanctuaryGlass", glass.Bake("SanctuaryGlass", glassItems.Count, true), mat, -40);
            }

            // ======================= 金の輪 order -45 / 輪の線 order -44 =======================
            {
                var mat = parts.NewMaterial("SanctuaryRing");
                mat.SetFloat("_Spin", 0.05f); mat.SetFloat("_Bob", 0.3f);
                SetInstances(mat, ringItems, ringSeeds);
                parts.Add("SanctuaryRing", ringSurf.Bake("SanctuaryRing", ringItems.Count, false), mat, -45);

                var kit = new BgLineKit(); var extra = new List<Vector4>();
                for (int i = 0; i < ringItems.Count; i++)
                    foreach (var sg in rLines.segs) { kit.segs.Add(sg); extra.Add(new Vector4(i, 0, 0, 0)); }
                var lm = parts.NewMaterial("SanctuaryLines");
                lm.SetFloat("_WidthPx", 1.2f); lm.SetFloat("_Opacity", 0.7f); lm.SetFloat("_Kind", 1f);
                lm.SetFloat("_Spin", 0.05f); lm.SetFloat("_Bob", 0.3f); lm.SetFloat("_Bias", 0f);
                lm.SetFloat("_RotOn", 1f); lm.SetFloat("_RotSpd", 0.035f);
                SetInstances(lm, ringItems, ringSeeds);
                parts.Add("SanctuaryRingLines", kit.BuildMesh("SanctuaryRingLines", extra), lm, -44);
            }

            // ======================= 光芒 order -50 =======================
            {
                var mat = parts.NewMaterial("SanctuaryRays");
                parts.Add("SanctuaryRays", raysMesh, mat, -50);
            }

            // ======================= モノリス order -60（距離150のワールドに置く）=======================
            {
                const float D = 150f;
                Vector3 W(Vector2 p) => ctx.NdcToWorldAtDistance(p, D);
                const float vApex = 0.5f, vTop = 1.1f;
                float k = 0.19f / (0.79f - vApex);
                float hw(float v) => k * (v - vApex);
                const float edge = 0.012f;
                var P = new List<Vector3>(); var CL = new List<Vector4>(); var FX = new List<Vector2>();
                void tri(Vector2 a, Vector2 b, Vector2 c, Vector4 cc, float fx)
                {
                    P.Add(W(a)); P.Add(W(b)); P.Add(W(c));
                    for (int i = 0; i < 3; i++) { CL.Add(cc); FX.Add(new Vector2(fx, 0)); }
                }
                void quad(Vector2 a, Vector2 b, Vector2 c, Vector2 e, Vector4 cc, float fx) { tri(a, b, c, cc, fx); tri(a, c, e, cc, fx); }
                // 縁から水平に o（+外側 / -内側）ずらした線: 上端 vTop から、u=0 で交わる頂点まで
                Vector2 edgePt(float o, float side, float v) => new Vector2(side * (hw(v) + o), v);
                float apexV(float o) => vApex - o / k;
                void band(float o0, float o1, Vector4 cc, float fx)
                {
                    foreach (float side in new[] { -1f, 1f })
                    {
                        Vector2 a0 = edgePt(o0, side, vTop), a1 = new Vector2(0, apexV(o0)), b0 = edgePt(o1, side, vTop), b1 = new Vector2(0, apexV(o1));
                        quad(a0, b0, b1, a1, cc, fx);
                    }
                }
                var glow = new Vector4(1.0f, 0.90f, 0.68f, 1f); var black = new Vector4(0.008f, 0.012f, 0.03f, 1f);
                const float inset = edge * 1.6f;
                { float o = -(inset - 0.002f); tri(edgePt(o, -1, vTop), edgePt(o, 1, vTop), new Vector2(0, apexV(o)), black, 0); }   // 内側の黒
                band(0f, -inset, glow, 1);                                                  // 主帯（琥珀）
                band(0.022f, 0.0155f, new Vector4(1.0f, 0.70f, 0.74f, 0.85f), 1);          // 外側の細線（桃）
                band(-inset - 0.018f, -inset - 0.0225f, new Vector4(0.86f, 0.82f, 1.0f, 0.55f), 2);   // 内側の細線（薄紫）
                float[] dds = { 0.075f, 0.13f, 0.185f };
                for (int i = 0; i < dds.Length; i++) band(-inset - dds[i], -inset - dds[i] - 0.0017f, new Vector4(0.16f, 0.18f, 0.38f, 0.62f - i * 0.1f), 2);   // 入れ子の山形
                void rung(float v, float insetX, Vector4 cc)
                {
                    float w = hw(v) - inset - insetX, t = 0.0008f;
                    quad(new Vector2(-w, v - t), new Vector2(w, v - t), new Vector2(w, v + t), new Vector2(-w, v + t), cc, 2);
                }
                float[] vs = { 0.83f, 0.90f, 0.97f, 1.04f };
                for (int i = 0; i < vs.Length; i++) rung(vs[i], 0.03f, new Vector4(0.16f, 0.18f, 0.38f, 0.55f - i * 0.06f));   // 横桟

                var mesh = new Mesh { name = "SanctuaryMonolith" };
                var idx = new int[P.Count];
                for (int i = 0; i < idx.Length; i++) idx[i] = i;
                mesh.SetVertices(P); mesh.SetUVs(0, CL); mesh.SetUVs(1, FX);
                mesh.SetTriangles(idx, 0);
                var mat = parts.NewMaterial("SanctuaryMonolith");
                parts.Add("SanctuaryMonolith", mesh, mat, -60);
            }
        }
    }
}
