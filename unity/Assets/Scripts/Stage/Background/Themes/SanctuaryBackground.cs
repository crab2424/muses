using UnityEngine;

namespace Muses.Stage.Background.Themes
{
    /// <summary>
    /// 天上の聖域（design-lab/stage-bg/themes/sanctuary-r3.js の移植）。設計は memory/game/stage-bg-unity-port.md。
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

        public override void Build(BackgroundContext ctx, BackgroundParts parts)
        {
            // TODO: sanctuary-r3.js を移植
        }
    }
}
