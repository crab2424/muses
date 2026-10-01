using System.Collections.Generic;
using UnityEngine;

namespace Muses.Stage.Background.Themes
{
    /// <summary>
    /// 神話の軌道都市（design-lab/stage-bg/themes/orbit-r3.js の移植）。設計は memory/game/stage-bg-unity-port.md。
    /// </summary>
    public sealed class OrbitCityBackground : BackgroundBuilder
    {
        public override Color ClearColor => StageTint.Hex("#03050d");

        public override StageTint? Tint => new StageTint
        {
            groundFill = StageTint.Hex("#0b1226"), groundFillAlpha = 1f,
            groundLine = StageTint.Hex("#7fa4cc"), groundLineAlpha = 0.28f,
            skyFill = StageTint.Hex("#2c4f80"), skyFillAlpha = 0.2f,
            skyLine = StageTint.Hex("#7fa4cc"), skyLineAlpha = 0.28f,
        };

        private readonly List<(GameObject go, float phase)> platforms = new List<(GameObject, float)>();

        public override void Tick(float t)
        {
            // 左右で逆位相に上下（周期約19秒）
            foreach (var p in platforms)
                if (p.go != null) p.go.transform.position = new Vector3(0f, 0.7f * Mathf.Sin(t * 0.33f + p.phase), 0f);
        }

        public override void Build(BackgroundContext ctx, BackgroundParts parts)
        {
            float aspect = ctx.aspect;
            // r1 のゲートの寸法: 中心 NDC(0, 0.9)、半径は画面横 0.23（= 縦単位で 0.23×aspect）。上部は画面外にはみ出す
            var gate = new Vector4(0f, 0.9f, 0.23f * aspect, aspect);

            // ---------- 都市（全画面板1枚） ----------
            const float CityY = -34f;
            // ゲート中心の視線が都市面 y=CityY に当たる点 → 大通りの中心（ラボ座標 xz で渡す）
            Vector2 cityCenterLab;
            {
                var ray = ctx.cam.ViewportPointToRay(new Vector3((gate.x + 1f) * 0.5f, (gate.y + 1f) * 0.5f, 0f));
                float tt = (CityY - ray.origin.y) / ray.direction.y;
                Vector3 hit = ray.origin + ray.direction * tt;
                cityCenterLab = new Vector2(hit.x, -hit.z);
            }
            var cityMesh = BgMeshUtil.ScreenQuad("OrbitCity", -1f, -1f, 1f, 1f);
            var cityMat = parts.NewMaterial("OrbitCity");
            cityMat.SetVector("_Gate", gate);
            cityMat.SetFloat("_CityY", CityY);
            cityMat.SetVector("_CityC", new Vector4(cityCenterLab.x, cityCenterLab.y, 0f, 0f));
            parts.Add("OrbitCity", cityMesh, cityMat, -100);

            // ---------- 虚空のゲート（スクリーン空間の板1枚） ----------
            const float ext = 1.55f; // 外周の時計盤まで覆う
            var gateMesh = BgMeshUtil.ScreenQuad("OrbitGate",
                gate.x - gate.z * ext / aspect, gate.y - gate.z * ext, gate.x + gate.z * ext / aspect, gate.y + gate.z * ext);
            var gateMat = parts.NewMaterial("OrbitGate");
            gateMat.SetVector("_Gate", gate);
            parts.Add("OrbitGate", gateMesh, gateMat, -90);

            var cyan = new Vector3(0.36f, 0.52f, 0.72f);
            var gold = new Vector3(0.70f, 0.60f, 0.38f);

            // ---------- 神殿（長手 = z 方向）。座標はラボのまま組み、BackgroundContext.Lab で z 反転 ----------
            var TK = new BgLineKit();
            foreach (float sgn in new[] { -1f, 1f })
            {
                Temple(TK, new Vector3(sgn * 24.7f, -10f, -26f), 1.1f, cyan, gold);
                Temple(TK, new Vector3(sgn * 27f, -12f, -66f), 1.3f, cyan, gold);
            }
            var templeMat = parts.NewMaterial("OrbitLines");
            templeMat.SetFloat("_WidthPx", 2.2f);
            templeMat.SetFloat("_Opacity", 0.9f);
            templeMat.SetFloat("_Hook", 1f);
            parts.Add("OrbitTemple", FlipZ(TK).BuildMesh("OrbitTemple"), templeMat, -70);

            // ---------- 浮遊足場（左右で逆位相に上下） ----------
            platforms.Clear();
            foreach (float sgn in new[] { -1f, 1f })
            {
                var K = new BgLineKit();
                void Plat(float x, float y, float z, float w, float dd)
                {
                    K.Box(new Vector3(sgn * x, y, z), new Vector3(w, 0.6f, dd), cyan, 0.7f);
                    K.Box(new Vector3(sgn * x, y - 0.75f, z), new Vector3(w * 0.6f, 0.9f, dd * 0.6f), cyan, 0.4f);
                    K.Box(new Vector3(sgn * x, y - 1.6f, z), new Vector3(w * 0.25f, 0.9f, dd * 0.25f), cyan, 0.25f);
                }
                Plat(14f, -6f, -20f, 6f, 5f);
                Plat(40f, -3f, -30f, 7f, 6f);
                Plat(15f, -2f, -44f, 6f, 5f);
                Plat(46f, -6f, -52f, 8f, 6f);
                var mat = parts.NewMaterial("OrbitLines");
                mat.SetFloat("_WidthPx", 1.6f);
                mat.SetFloat("_Opacity", 0.55f);
                mat.SetFloat("_Hook", 0f);
                var go = parts.Add(sgn > 0 ? "OrbitPlatformR" : "OrbitPlatformL", FlipZ(K).BuildMesh("OrbitPlatform"), mat, -72);
                platforms.Add((go, sgn > 0 ? 0f : Mathf.PI));
            }

            // ---------- 粒子（400点、1粒子 = 4頂点の板） ----------
            const int N = 400;
            var ppos = new Vector3[N * 4];
            var pcorner = new Vector2[N * 4];
            var pseed = new Vector4[N * 4];
            var pidx = new int[N * 6];
            uint rs = 12345;
            float Rnd() { rs = unchecked(rs * 1664525u + 1013904223u); return rs / 4294967296f; }
            Vector2[] corners = { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };
            for (int i = 0; i < N; i++)
            {
                float side = Rnd() < 0.5f ? -1f : 1f;
                float sx = side * (8f + Rnd() * 62f);
                float sph = Rnd();
                float sz = -(12f + Rnd() * 88f);
                float sp = 0.6f + Rnd() * 0.8f;
                for (int j = 0; j < 4; j++)
                {
                    pcorner[i * 4 + j] = corners[j];
                    pseed[i * 4 + j] = new Vector4(sx, sph, sz, sp);
                }
                int o = i * 6, q = i * 4;
                pidx[o] = q; pidx[o + 1] = q + 1; pidx[o + 2] = q + 2; pidx[o + 3] = q; pidx[o + 4] = q + 2; pidx[o + 5] = q + 3;
            }
            var pm = new Mesh { name = "OrbitParticles" };
            pm.SetVertices(ppos);
            pm.SetUVs(0, pcorner);
            pm.SetUVs(1, pseed);
            pm.SetTriangles(pidx, 0);
            pm.bounds = new Bounds(Vector3.zero, Vector3.one * 1e5f);
            var pmat = parts.NewMaterial("OrbitParticles");
            pmat.SetVector("_Gate", gate);
            parts.Add("OrbitParticles", pm, pmat, -60);
        }

        /// <summary>ラボ座標で組んだ線分を Unity 座標（z 反転）へ</summary>
        private static BgLineKit FlipZ(BgLineKit src)
        {
            var dst = new BgLineKit();
            foreach (var g in src.segs)
                dst.segs.Add(new BgLineKit.Seg { a = BackgroundContext.Lab(g.a), b = BackgroundContext.Lab(g.b), c = g.c, p = g.p });
            return dst;
        }

        /// <summary>神殿1棟（ラボの temple）。p: 1=列柱（光が下から上へ）、2=梁・棟（光が奥へ走る）、0=静的</summary>
        private static void Temple(BgLineKit K, Vector3 o, float S, Vector3 cyan, Vector3 gold)
        {
            float L = 24f * S, W = 14f * S;
            Vector3 At(float x, float y, float z) => new Vector3(o.x + x, o.y + y, o.z + z);
            // 台座（三段の基壇）
            K.Box(At(0, 0, 0), new Vector3(W + 4 * S, 0.9f * S, L + 4 * S), cyan, 0.85f);
            K.Box(At(0, 0.7f * S, 0), new Vector3(W + 2.4f * S, 0.5f * S, L + 2.4f * S), cyan, 0.6f);
            K.Box(At(0, 1.2f * S, 0), new Vector3(W + 0.8f * S, 0.5f * S, L + 0.8f * S), cyan, 0.45f);
            // 列柱
            float cx = W / 2 - 0.9f * S, cz = L / 2 - 1.3f * S, ch = 5.0f * S, cy = 1.45f * S + ch / 2;
            float cw = 1.0f * S; int nz = 6, nx = 4;
            var csz = new Vector3(cw, ch, cw);
            for (int i = 0; i < nz; i++)
            {
                float z = -cz + (2 * cz * i) / (nz - 1);
                K.Box(At(-cx, cy, z), csz, cyan, 0.8f, 1f);
                K.Box(At(cx, cy, z), csz, cyan, 0.8f, 1f);
            }
            for (int i = 1; i < nx - 1; i++)
            {
                float x = -cx + (2 * cx * i) / (nx - 1);
                K.Box(At(x, cy, -cz), csz, cyan, 0.5f, 1f);
                K.Box(At(x, cy, cz), csz, cyan, 0.8f, 1f);
            }
            // 梁
            float yb = cy + ch / 2 + 0.4f * S;
            K.Box(At(0, yb, 0), new Vector3(W, 0.8f * S, L), gold, 0.8f, 2f);
            // 切妻屋根
            float yt = yb + 0.4f * S, hgt = 3.6f * S, ya = yt + hgt;
            foreach (float z in new[] { L / 2, -L / 2 })
            {
                var a = At(-W / 2, yt, z); var b = At(W / 2, yt, z); var t = At(0, ya, z);
                float k = z > 0 ? 0.95f : 0.5f;
                K.SegLine(a, b, gold, k); K.SegLine(a, t, gold, k); K.SegLine(b, t, gold, k);
            }
            K.SegLine(At(0, ya, -L / 2), At(0, ya, L / 2), gold, 0.8f, 2f);
            int nr = 6;
            for (int i = 1; i < nr; i++)
            {
                float z = -L / 2 + (L * i) / nr;
                K.SegLine(At(0, ya, z), At(-W / 2, yt, z), gold, 0.32f);
                K.SegLine(At(0, ya, z), At(W / 2, yt, z), gold, 0.32f);
            }
        }
    }
}
