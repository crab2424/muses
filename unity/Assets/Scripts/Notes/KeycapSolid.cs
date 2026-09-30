using System.Collections.Generic;
using UnityEngine;

namespace Muses.Notes
{
    /// <summary>
    /// スキン「キーキャップ」の Tap 系の立体のひな形（r3。移植元: design-lab/note-skin/skins/keycap.js の
    /// loopStadium / loopFlick / buildSolid / RINGS）。平面の輪郭を断面リングで積み上げた押し出しソリッド（壁→面取り→天面）。
    ///
    /// 頂点はノーツの寸法に依らない形で持つ: ラボのローカル座標（x=横, y=上, z=手前が+）で
    /// x = A*半長 + B*半奥行、z = zN*半奥行、y = yN*半奥行（<see cref="NoteMeshData.solid"/>）。
    /// 半長はノーツ幅、半奥行は NoteView の thicknessFrac から頂点シェーダ（NoteSkinKeycap.hlsl）が求める。
    /// これが成り立つのは「半長 ≥ 半奥行×1.25（Flick の尖り）」のときで、幅1セル（半長 ≒ 半奥行×2.6）以上なら満たす。
    /// </summary>
    public sealed class KeycapSolid
    {
        public Vector4[] solid;
        public Vector3[] normals;
        /// <summary>縁距離 c（天面の縁=0 → 中心=1、天面以外は -1）。白リング用。</summary>
        public float[] edge;
        public int[] indices;

        private const int StadiumSeg = 10;   // 半円の分割数
        private const float FlickTipK = 1.25f; // Flick の尖りの長さ（半奥行比）
        private const float ZFat = 1.25f;    // 奥行き方向を太らせる（見た目のみ。ネオンの縦の大きさに合わせる、r3 §8）
        private const float HeightThin = 0.7f; // 地上: 薄型（半奥行比）
        private const float HeightAir = 1.3f;  // 空中: 立体（遠方ではシェーダで高さ→0）

        // 壁 → 面取り → 天面（天面リングは複製して c=0 から始める）
        private static readonly (float e, float y, float nh, float ny, float c)[] Rings =
        {
            (0.00f, 0.00f, 1.0f, 0.0f, -1f),
            (0.00f, 0.72f, 1.0f, 0.0f, -1f),
            (0.05f, 0.90f, 0.85f, 0.55f, -1f),
            (0.14f, 1.00f, 0.5f, 0.87f, -1f),
            (0.14f, 1.00f, 0.0f, 1.0f, 0.0f),
        };

        // 輪郭の1点: 位置 (A, B, z)（z は半奥行単位）、外向き法線 n、内側への縮み方向 m（ミター補正込み）
        private struct LoopPt
        {
            public float A, B, z;
            public Vector2 n, m;
        }

        private static readonly Dictionary<(bool flick, bool air), KeycapSolid> Cache = new();

        public static KeycapSolid Get(bool flick, bool air)
        {
            if (!Cache.TryGetValue((flick, air), out var s))
            {
                s = Build(flick ? LoopFlick() : LoopStadium(), air ? HeightAir : HeightThin);
                Cache[(flick, air)] = s;
            }
            return s;
        }

        // ( ) : 左右端が半円。中心 ±(半長-半奥行)
        private static List<LoopPt> LoopStadium()
        {
            var L = new List<LoopPt>();
            for (int i = 0; i <= StadiumSeg; i++)
            {
                float f = -Mathf.PI / 2 + Mathf.PI * i / StadiumSeg;
                var d = new Vector2(Mathf.Cos(f), Mathf.Sin(f));
                L.Add(new LoopPt { A = 1f, B = d.x - 1f, z = d.y, n = d, m = d });
            }
            for (int i = 0; i <= StadiumSeg; i++)
            {
                float f = Mathf.PI / 2 + Mathf.PI * i / StadiumSeg;
                var d = new Vector2(Mathf.Cos(f), Mathf.Sin(f));
                L.Add(new LoopPt { A = -1f, B = d.x + 1f, z = d.y, n = d, m = d });
            }
            return L;
        }

        // < > : 両端の三角の先端が外へ突き出す
        private static List<LoopPt> LoopFlick()
        {
            static Vector2 Mit(Vector2 n1, Vector2 n2)
            {
                float k = 1f + Vector2.Dot(n1, n2);
                return (n1 + n2) / k;
            }
            float tip = FlickTipK;
            float len = Mathf.Sqrt(1f + tip * tip);
            var nwf = new Vector2(1f / len, -tip / len); // 右側の斜辺（奥）の外向き法線
            var nwn = new Vector2(1f / len, tip / len);  // 右側の斜辺（手前）
            var nFar = new Vector2(0f, -1f);
            var nNear = new Vector2(0f, 1f);
            Vector2 Fx(Vector2 v) => new Vector2(-v.x, v.y);

            var L = new List<LoopPt>();
            void P(float A, float B, float z, Vector2 n, Vector2 m) => L.Add(new LoopPt { A = A, B = B, z = z, n = n, m = m });
            // 右: 奥角 → 先端 → 手前角（角の x = 半長 - 尖り長、先端の x = 半長）
            var m = Mit(nFar, nwf); P(1f, -tip, -1f, nFar, m); P(1f, -tip, -1f, nwf, m);
            m = Mit(nwf, nwn); P(1f, 0f, 0f, nwf, m); P(1f, 0f, 0f, nwn, m);
            m = Mit(nwn, nNear); P(1f, -tip, 1f, nwn, m); P(1f, -tip, 1f, nNear, m);
            // 左: 手前角 → 先端 → 奥角（x 反転）
            m = Mit(nNear, Fx(nwn)); P(-1f, tip, 1f, nNear, m); P(-1f, tip, 1f, Fx(nwn), m);
            m = Mit(Fx(nwn), Fx(nwf)); P(-1f, 0f, 0f, Fx(nwn), m); P(-1f, 0f, 0f, Fx(nwf), m);
            m = Mit(Fx(nwf), nFar); P(-1f, tip, -1f, Fx(nwf), m); P(-1f, tip, -1f, nFar, m);
            return L;
        }

        private static KeycapSolid Build(List<LoopPt> loop, float height)
        {
            int P = loop.Count;
            var solid = new List<Vector4>();
            var nor = new List<Vector3>();
            var cc = new List<float>();
            var idx = new List<int>();
            foreach (var rg in Rings)
            {
                foreach (var v in loop)
                {
                    // ラボ: 位置 = p - m*e*半奥行（z は ZFat 倍）、法線 = (nh*n.x, ny, nh*n.z) を z 方向 ZFat 倍で変形
                    solid.Add(new Vector4(v.A, v.B - v.m.x * rg.e, (v.z - v.m.y * rg.e) * ZFat, rg.y * height));
                    nor.Add(new Vector3(rg.nh * v.n.x, rg.ny, rg.nh * v.n.y / ZFat).normalized);
                    cc.Add(rg.c);
                }
            }
            for (int k = 0; k + 1 < Rings.Length; k++)
            {
                for (int i = 0; i < P; i++)
                {
                    int j = (i + 1) % P;
                    int lo0 = k * P + i, lo1 = k * P + j, hi0 = (k + 1) * P + i, hi1 = (k + 1) * P + j;
                    idx.Add(lo0); idx.Add(hi0); idx.Add(lo1);
                    idx.Add(lo1); idx.Add(hi0); idx.Add(hi1);
                }
            }
            int ci = solid.Count, tb = (Rings.Length - 1) * P;
            solid.Add(new Vector4(0f, 0f, 0f, height));
            nor.Add(Vector3.up);
            cc.Add(1f);
            for (int i = 0; i < P; i++)
            {
                idx.Add(tb + i); idx.Add(ci); idx.Add(tb + (i + 1) % P);
            }
            return new KeycapSolid { solid = solid.ToArray(), normals = nor.ToArray(), edge = cc.ToArray(), indices = idx.ToArray() };
        }
    }
}
