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

        public override void Build(BackgroundContext ctx, BackgroundParts parts)
        {
            float aspect = ctx.aspect;
            // r1 のゲートの寸法: 中心 NDC(0, 0.9)、半径は画面横 0.23（= 縦単位で 0.23×aspect）。上部は画面外にはみ出す
            var gate = new Vector4(0f, 0.9f, 0.23f * aspect, aspect);

            // TODO: 都市（全画面板）order -100

            // ---------- 虚空のゲート（スクリーン空間の板1枚） ----------
            const float ext = 1.55f; // 外周の時計盤まで覆う
            var gateMesh = BgMeshUtil.ScreenQuad("OrbitGate",
                gate.x - gate.z * ext / aspect, gate.y - gate.z * ext, gate.x + gate.z * ext / aspect, gate.y + gate.z * ext);
            var gateMat = parts.NewMaterial("OrbitGate");
            gateMat.SetVector("_Gate", gate);
            parts.Add("OrbitGate", gateMesh, gateMat, -90);

            // TODO: 神殿（線）order -70、浮遊足場（線×2、上下動）order -72、粒子 order -60
        }
    }
}
