using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

// 譜面エディタ(ChartEditor.unity)だけをスタンドアロン実行ファイルとしてビルドする。
// ゲーム本体(SampleScene)のビルド設定(EditorBuildSettings)には触れず、
// シーンを直接指定することで独立した出力物にしている。
public static class BuildChartEditor
{
    private const string SceneName = "Assets/Scenes/ChartEditor.unity";

    [MenuItem("Build/Build Chart Editor (macOS)")]
    public static void BuildMac()
    {
        // Intel(x64)版を同梱しない。配布対象は自分のApple Siliconマシンのみのため、
        // ユニバーサルバイナリ(x64+ARM64、約38MB増)は不要（容量調査 2026-08-03）。
        // PlayerSettings.SetArchitecture単体ではネイティブ側(UnityPlayer.dylib等)の
        // Universal/ARM64選択には反映されなかった（実測、EditorUserBuildSettings.asset内の
        // "OSXUniversal > Architecture" が実際の切替キーだったため、両方セットする）。
        PlayerSettings.SetArchitecture(NamedBuildTarget.Standalone, (int)OSArchitecture.ARM64);
        EditorUserBuildSettings.SetPlatformSettings("OSXUniversal", "Architecture", "ARM64");
        Build(BuildTarget.StandaloneOSX, "Builds/ChartEditorMac/MusesChartEditor.app");
    }

    [MenuItem("Build/Build Chart Editor (Windows)")]
    public static void BuildWindows()
    {
        Build(BuildTarget.StandaloneWindows64, "Builds/ChartEditorWin/MusesChartEditor.exe");
    }

    private static void Build(BuildTarget target, string locationPathName)
    {
        var options = new BuildPlayerOptions
        {
            scenes = new[] { SceneName },
            locationPathName = locationPathName,
            target = target,
            options = BuildOptions.None,
        };

        var report = BuildPipeline.BuildPlayer(options);
        var summary = report.summary;

        if (summary.result == BuildResult.Succeeded)
        {
            UnityEngine.Debug.Log($"Chart Editor build succeeded: {summary.outputPath} ({summary.totalSize} bytes)");
        }
        else
        {
            UnityEngine.Debug.LogError($"Chart Editor build failed: {summary.result}");
        }
    }
}
