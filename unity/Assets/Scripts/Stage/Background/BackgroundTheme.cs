namespace Muses.Stage.Background
{
    /// <summary>ステージ背景の種類（memory/game/stage-bg-unity-port.md）。並びは設定画面のドロップダウンの順。</summary>
    public enum BackgroundTheme
    {
        /// <summary>背景なし（StageConfig.bgColor の単色）。譜面エディタのプレビュー等</summary>
        None = 0,
        /// <summary>神話の軌道都市（design-lab/stage-bg/themes/orbit-r3.js）</summary>
        OrbitCity = 1,
        /// <summary>天上の聖域（design-lab/stage-bg/themes/sanctuary-r3.js）</summary>
        Sanctuary = 2,
    }
}
