using UnityEngine;

namespace Muses.Stage.Background
{
    /// <summary>
    /// 暗部ゾーン（ノーツ出現位置付近）。NDC（y 上向き）。移植元: design-lab/stage-bg/shared/zone.js の makeZone。
    /// 最遠端の断面（地上の奥の辺〜空中の奥の辺）に余白を足した長方形。
    /// </summary>
    public struct SpawnZone
    {
        public float u0, v0, u1, v1;
        public float feather;
        /// <summary>最遠端の断面: 地上の奥の辺 / 空中の奥の辺の右端 u と v</summary>
        public float groundFarU, groundFarV, skyFarU, skyFarV;

        public const float MarginSide = 0.06f, MarginTop = 0.08f, MarginBottom = 0.02f;
        public const float Feather = 0.14f;
    }

    /// <summary>
    /// テーマの Build に渡す文脈。座標の約束（ラボ −z 奥 → Unity +z 奥）は stage-bg-unity-port.md §3。
    /// </summary>
    public sealed class BackgroundContext
    {
        public StageConfig cfg;
        public Derived d;
        public Camera cam;
        public float aspect;
        public SpawnZone zone;

        /// <summary>ラボの座標 (x, y, z) を Unity のワールド座標へ（z 反転）</summary>
        public static Vector3 Lab(float x, float y, float z) => new Vector3(x, y, -z);
        public static Vector3 Lab(Vector3 p) => new Vector3(p.x, p.y, -p.z);

        /// <summary>ラボの ctx.laneX(u, layerF, z)。z は奥行き（正）</summary>
        public float LaneX(float u, float layerF, float depth) => StageDerive.LaneX(cfg, d, u, layerF, depth);

        /// <summary>iPad 11"（縦1668px）基準の px → NDC 縦単位（ラボの線幅の単位）</summary>
        public static float RefPx(float px) => px * 2f / 1668f;

        /// <summary>ワールド座標 → NDC（y 上向き）。カメラ姿勢を確定させた後に呼ぶ</summary>
        public Vector2 WorldToNdc(Vector3 world)
        {
            var vp = cam.WorldToViewportPoint(world);
            return new Vector2(vp.x * 2f - 1f, vp.y * 2f - 1f);
        }

        /// <summary>NDC（y 上向き）の点を通る視線が、カメラからの距離 dist の点（ラボの unproject 相当）</summary>
        public Vector3 NdcToWorldAtDistance(Vector2 ndc, float dist)
        {
            var ray = cam.ViewportPointToRay(new Vector3((ndc.x + 1f) * 0.5f, (ndc.y + 1f) * 0.5f, 0f));
            return ray.origin + ray.direction * dist;
        }

        internal static SpawnZone ComputeZone(BackgroundContext ctx)
        {
            var d = ctx.d;
            Vector2 gL = ctx.WorldToNdc(new Vector3(ctx.LaneX(-1f, 0f, d.zFar), 0f, d.zFar));
            Vector2 gR = ctx.WorldToNdc(new Vector3(ctx.LaneX(1f, 0f, d.zFar), 0f, d.zFar));
            Vector2 sL = ctx.WorldToNdc(new Vector3(ctx.LaneX(-1f, 1f, d.zFar), d.skyHeight, d.zFar));
            Vector2 sR = ctx.WorldToNdc(new Vector3(ctx.LaneX(1f, 1f, d.zFar), d.skyHeight, d.zFar));
            return new SpawnZone
            {
                u0 = Mathf.Min(gL.x, sL.x) - SpawnZone.MarginSide,
                u1 = Mathf.Max(gR.x, sR.x) + SpawnZone.MarginSide,
                v0 = Mathf.Min(gL.y, sL.y) - SpawnZone.MarginBottom,
                v1 = Mathf.Min(1f, Mathf.Max(gL.y, sL.y) + SpawnZone.MarginTop),
                feather = SpawnZone.Feather,
                groundFarU = gR.x, groundFarV = gL.y, skyFarU = sR.x, skyFarV = sL.y,
            };
        }
    }
}
