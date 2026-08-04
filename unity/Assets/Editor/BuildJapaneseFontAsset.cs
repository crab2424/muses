using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

// editor-ui-rework-r12.md §3.3。譜面エディタの日本語フォント(Noto Sans JP)をUI Toolkit用の
// FontAssetへ変換し、ChartEditorのPanelSettingsへ配線する。Font Asset Creator等のGUI操作を
// 使わず、この1メニュー実行だけで完結させる(BuildChartEditor.csと同じ「エディタ拡張として
// Assets/Editor/に置く」方針)。TextMeshProパッケージには依存しない
// (UnityEngine.TextCore.Text.FontAssetはUnity 6のエンジン組み込みで、[[muses-unity-port-progress]]の
// 容量調査でTextMeshProパッケージ自体は削除済みだが、そちらとは別物でパッケージ不要)。
public static class BuildJapaneseFontAsset
{
    private const string FontPath = "Assets/UI/ChartEditor/Fonts/NotoSansJP-Regular.ttf";
    private const string FontAssetPath = "Assets/UI/ChartEditor/Fonts/NotoSansJP-Regular SDF.asset";
    private const string TextSettingsPath = "Assets/UI/ChartEditor/Fonts/ChartEditorTextSettings.asset";
    private const string PanelSettingsPath = "Assets/UI/ChartEditor/PanelSettings.asset";

    [MenuItem("Build/Setup Japanese Font (Chart Editor)")]
    public static void Setup()
    {
        var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
        if (font == null)
        {
            Debug.LogError($"フォントが見つかりません: {FontPath}");
            return;
        }

        var fontAsset = AssetDatabase.LoadAssetAtPath<FontAsset>(FontAssetPath);
        if (fontAsset == null)
        {
            // Dynamic方式(実行時に必要なグリフだけをアトラスへ都度焼く)を採る。日本語の全グリフを
            // 静的アトラスへ事前に焼くとサイズが跳ね上がるため(容量調査で75MBまで削減した経緯があり、
            // ここを膨らませたくない)。samplingPointSize/atlasサイズは初期値からの実測調整余地あり。
            fontAsset = FontAsset.CreateFontAsset(
                font, samplingPointSize: 48, atlasPadding: 5, GlyphRenderMode.SDFAA,
                atlasWidth: 1024, atlasHeight: 1024, AtlasPopulationMode.Dynamic, enableMultiAtlasSupport: true);
            if (fontAsset == null)
            {
                Debug.LogError("FontAssetの生成に失敗しました。");
                return;
            }
            fontAsset.name = "NotoSansJP SDF";
            AssetDatabase.CreateAsset(fontAsset, FontAssetPath);
            // material/atlasTexturesはFontAsset.CreateFontAssetが生成した子アセットだが、
            // サブアセットとしてファイルへ書き込まないとリロード時に参照切れ(m_Materialが
            // 「存在しなくなった」)を起こす。CreateAsset直後にAddObjectToAssetで明示的に追加する。
            if (fontAsset.material != null)
            {
                fontAsset.material.name = fontAsset.name + " Material";
                AssetDatabase.AddObjectToAsset(fontAsset.material, fontAsset);
            }
            if (fontAsset.atlasTextures != null)
            {
                foreach (var tex in fontAsset.atlasTextures)
                {
                    if (tex != null && AssetDatabase.GetAssetPath(tex) != FontAssetPath)
                        AssetDatabase.AddObjectToAsset(tex, fontAsset);
                }
            }
            Debug.Log("FontAssetを新規作成しました: " + FontAssetPath);
        }
        else
        {
            Debug.Log("既存のFontAssetを再利用します: " + FontAssetPath);
        }

        var textSettings = AssetDatabase.LoadAssetAtPath<PanelTextSettings>(TextSettingsPath);
        if (textSettings == null)
        {
            textSettings = ScriptableObject.CreateInstance<PanelTextSettings>();
            AssetDatabase.CreateAsset(textSettings, TextSettingsPath);
            Debug.Log("PanelTextSettingsを新規作成しました: " + TextSettingsPath);
        }
        // defaultFontAssetはUnity 6.5.6f1時点でObsolete("将来のバージョンで削除予定")マーク済みだが、
        // このバージョンではまだ機能し、かつ代替プロパティがAPI上に存在しない(fallbackFontAssetsだけでは
        // 「既定フォント」自体は指定できない)。将来のUnityアップデートでビルドが壊れたら、
        // その時点のAPIドキュメントで置き換え先を確認する。
#pragma warning disable CS0618
        textSettings.defaultFontAsset = fontAsset;
#pragma warning restore CS0618
        if (textSettings.fallbackFontAssets == null)
            textSettings.fallbackFontAssets = new System.Collections.Generic.List<FontAsset>();
        if (!textSettings.fallbackFontAssets.Contains(fontAsset))
            textSettings.fallbackFontAssets.Add(fontAsset);
        EditorUtility.SetDirty(textSettings);

        var panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
        if (panelSettings == null)
        {
            Debug.LogError($"PanelSettingsが見つかりません: {PanelSettingsPath}");
            return;
        }
        panelSettings.textSettings = textSettings;
        EditorUtility.SetDirty(panelSettings);

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("日本語フォントのセットアップが完了しました。ChartEditorシーンをPlayして表示を確認してください。");
    }
}
