using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace Muses.ChartTool
{
    /// <summary>
    /// editor-ui-rework-r14.md §2。UIの文字をOSのフォントにする。ブラウザがOSのフォント
    /// (Mac=ヒラギノ、Win=Yu Gothic UI)で日本語を描くのに合わせ、同梱のNoto Sans JPより
    /// Webやほかのアプリに近い見た目にする。
    /// - フォントは実行時にOSから読むだけで同梱しない（配布物に含めないのでライセンス上の問題も無い）。
    /// - 太字(<c>-unity-font-style: bold</c>)は、これまでRegularを擬似的に太らせていた。
    ///   OSフォントでは本物の太字ウェイトを fontWeightTable[7] に登録する。
    /// - 同梱のNotoはフォールバックとして残す(OSフォントに無い文字・OSフォントが見つからない場合)。
    /// </summary>
    public static class UiFonts
    {
        private const int SamplingPointSize = 48; // 同梱Noto(BuildJapaneseFontAsset)と同じ
        private const int AtlasPadding = 5;

        private readonly struct Candidate
        {
            public readonly string family, regular, bold;
            public Candidate(string family, string regular, string bold)
            {
                this.family = family;
                this.regular = regular;
                this.bold = bold;
            }
        }

        private static Candidate[] CandidatesForPlatform()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor:
                    return new[]
                    {
                        new Candidate("Hiragino Sans", "W3", "W6"),
                        new Candidate("Hiragino Kaku Gothic ProN", "W3", "W6"),
                    };
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                    return new[]
                    {
                        new Candidate("Yu Gothic UI", "Regular", "Bold"),
                        new Candidate("Meiryo UI", "Regular", "Bold"),
                    };
                default:
                    return Array.Empty<Candidate>();
            }
        }

        /// <summary>panelSettingsのTextSettingsを複製し、既定フォントをOSフォントへ差し替える。
        /// 元のアセット(ChartEditorTextSettings.asset)は書き換えない（Editorで実行したときに
        /// アセットが汚れないよう、またゲーム側の参照に影響させないため）。
        /// OSフォントが見つからなければ何もしない(同梱フォントのまま)。</summary>
        public static bool ApplyOsFont(PanelSettings panelSettings)
        {
            var baseSettings = panelSettings.textSettings;
            if (baseSettings == null) return false;

            foreach (var c in CandidatesForPlatform())
            {
                var regular = TryCreate(c.family, c.regular);
                if (regular == null) continue;

                var bold = TryCreate(c.family, c.bold);
                var weights = regular.fontWeightTable;
                if (bold != null && weights != null && weights.Length > 7)
                    weights[7].regularTypeface = bold; // 700 = bold

                var ts = UnityEngine.Object.Instantiate(baseSettings);
                ts.name = baseSettings.name + " (OS)";
#pragma warning disable CS0618 // BuildJapaneseFontAsset.csと同じく、既定フォントの代替APIがまだ無い
                var previousDefault = ts.defaultFontAsset;
                ts.defaultFontAsset = regular;
#pragma warning restore CS0618
                var fallbacks = new List<FontAsset> { regular };
                if (ts.fallbackFontAssets != null)
                    foreach (var f in ts.fallbackFontAssets)
                        if (f != null && !fallbacks.Contains(f)) fallbacks.Add(f);
                if (previousDefault != null && !fallbacks.Contains(previousDefault)) fallbacks.Add(previousDefault);
                ts.fallbackFontAssets = fallbacks;

                panelSettings.textSettings = ts;
                Debug.Log($"ChartEditor: UIフォントにOSのフォントを使います: {c.family} {c.regular}" +
                          (bold != null ? $" / 太字 {c.bold}" : " / 太字は擬似太字"));
                return true;
            }

            Debug.LogWarning("ChartEditor: OSのフォントが見つからないため、同梱フォントを使います");
            return false;
        }

        private static FontAsset TryCreate(string family, string style)
        {
            try
            {
                var fa = FontAsset.CreateFontAsset(family, style, SamplingPointSize, AtlasPadding, GlyphRenderMode.SDFAA);
                if (fa != null)
                {
                    fa.name = $"{family} {style} (OS)";
                    fa.isMultiAtlasTexturesEnabled = true;
                }
                return fa;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ChartEditor: フォント {family} {style} を読み込めませんでした: {ex.Message}");
                return null;
            }
        }
    }
}
