using UnityEditor;
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
