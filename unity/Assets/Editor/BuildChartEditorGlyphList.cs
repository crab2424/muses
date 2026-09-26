using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

// editor-ui-rework-r14.md §1.4。譜面エディタのUIに出てくる文字の一覧を作る。
// フォントアセットはDynamic方式(初めて表示する文字をその場でSDFに焼く)で、ビルドは毎回空から始まる
// (m_ClearDynamicDataOnBuild)。設定モーダルを初めて開いた瞬間などに新しい漢字がまとめて焼かれて
// UIが止まるのを避けるため、ChartEditorAppが起動時にこの一覧を先に焼く。
// ビルド時(BuildChartEditor)に自動で作り直す。手動で更新したいときはメニューから。
public static class BuildChartEditorGlyphList
{
    public const string OutputPath = "Assets/Resources/ChartEditorGlyphs.txt";

    private static readonly string[] SourceGlobs =
    {
        "Assets/Scripts/ChartEditorApp",
        "Assets/Scripts/Chart",
    };

    [MenuItem("Build/Update Chart Editor Glyph List")]
    public static void Generate()
    {
        var chars = new SortedSet<char>();
        foreach (var dir in SourceGlobs)
            foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
                CollectStringLiterals(File.ReadAllText(file), chars);
        foreach (var file in Directory.GetFiles("Assets/UI/ChartEditor", "*.uxml", SearchOption.AllDirectories))
            foreach (char c in File.ReadAllText(file)) chars.Add(c);

        // ASCIIの英数字・記号は取りこぼしが無いよう全部入れる(数値表示・入力で必ず使う)。
        for (char c = ' '; c <= '~'; c++) chars.Add(c);

        var text = new string(chars.Where(c => !char.IsControl(c) && !char.IsSurrogate(c)).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
        string previous = File.Exists(OutputPath) ? File.ReadAllText(OutputPath, Encoding.UTF8) : null;
        if (previous != text)
        {
            File.WriteAllText(OutputPath, text, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(OutputPath);
        }
        Debug.Log($"Chart Editor glyph list: {text.Length} 文字 → {OutputPath}");
    }

    // 文字列リテラル("..."・$"..."・@"...")の中身だけを拾う。コメント中の文字(設計メモ等)は
    // 画面に出ないので含めない。
    private static readonly Regex LineComment = new(@"//.*$", RegexOptions.Multiline);
    private static readonly Regex StringLiteral = new(@"""((?:[^""\\\n]|\\.)*)""");

    private static void CollectStringLiterals(string source, SortedSet<char> into)
    {
        // 行コメントは文字列より先に落とす(コメント中の"..."を拾わないため)。URL等"//"を含む
        // 文字列リテラルは多少欠けるが、一覧は先読み用なので欠けても実害は無い(その場で焼かれる)。
        source = LineComment.Replace(source, "");
        foreach (Match m in StringLiteral.Matches(source))
            foreach (char c in m.Groups[1].Value)
                into.Add(c);
    }
}
