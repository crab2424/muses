#if UNITY_IOS
using System.IO;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;

// song-play-flow-r1.md §3.1。iPadへ曲データ(song.museproj/.muses/音源)を持ち込む手段として
// Files アプリ経由を選んだ（実機実績のあるビルド後処理パターン、signing手作業の簡略化と同じ発想、
// [[muses-unity-port-progress]]参照）。この2キーが無いと Application.persistentDataPath(=Documents/)
// が Files アプリから見えず、ビルドし直さずに譜面を差し替えられない。
public static class IosInfoPlistPostprocess
{
    [PostProcessBuild(1)]
    public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
    {
        if (target != BuildTarget.iOS) return;

        string plistPath = Path.Combine(pathToBuiltProject, "Info.plist");
        var plist = new PlistDocument();
        plist.ReadFromFile(plistPath);

        plist.root.SetBoolean("UIFileSharingEnabled", true);
        plist.root.SetBoolean("LSSupportsOpeningDocumentsInPlace", true);

        plist.WriteToFile(plistPath);
    }
}
#endif
