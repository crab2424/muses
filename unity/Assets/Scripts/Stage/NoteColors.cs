using UnityEngine;

namespace Muses.Stage
{
    /// <summary>
    /// ノーツ種別ごとの色の一元管理。note-visual-r1.md §4/§9（2026-08-07確定）の配色。
    /// ゲーム側(Notes/NoteGeometry.cs)・エディタ側(ChartEditorApp.cs)の双方から参照する
    /// （StageColorsと同じ Muses.Stage アセンブリに置くことでエディタからも参照できる）。
    /// 従来3箇所（NoteGeometry.cs / ChartEditorApp.cs / StageColors）に別々のリテラルがあり
    /// ドリフトしていた（note-visual-r1.md §4.3）ため、ここへ一本化する。
    ///
    /// 色の役割分担（note-visual-r1.md §4.2）: Tap は「操作」の色で層に依らず固定。
    /// それ以外（Slide/Riser/Diver）は追加で「場所（層）」の役割を兼ねるため層で変える。
    /// Sky側のSlideとRiserは意図的に同色（緑）に揃えている。
    /// </summary>
    public static class NoteColors
    {
        public static readonly Color Tap = StageGeometry.ColorFromHex(0x4aa3ff);
        public static readonly Color ExTap = StageGeometry.ColorFromHex(0xffd54a);
        public static readonly Color Flick = StageGeometry.ColorFromHex(0xff4a4a);

        public static readonly Color SlideGround = StageGeometry.ColorFromHex(0x35e8ff);
        /// <summary>Riserと同色（意図的、note-visual-r1.md §4.2）。</summary>
        public static readonly Color SlideSky = StageGeometry.ColorFromHex(0x4affa0);

        /// <summary>Ground(不透明寄り)/Sky(透明)。note-visual-r1.md §5.1: 「見た目は不透明」だが
        /// ごく僅かにblend capの余地を残す値で確定（alpha=1にはしない）。</summary>
        public const float SlideGroundAlpha = 0.94f;
        /// <summary>Riserの壁と同じ透明度（0.35）に揃える。</summary>
        public const float SlideSkyAlpha = 0.35f;

        /// <summary>中継点(Visible)マーカー・始点/終点のalpha。層に依らず常に高く保ち、
        /// Sky側の透明な帯の上でも視認できるようにする（note-visual-r1.md §7）。</summary>
        public const float SlideMarkerAlpha = 0.95f;

        public static readonly Color Riser = SlideSky;
        public static readonly Color Diver = StageGeometry.ColorFromHex(0xc86aff);

        /// <summary>Slideの帯・マーカーの色。layerFで Ground/Sky を連続的に補間する
        /// （Slideは層を跨いで連続的に高さが変わり得るため、離散切り替えにしない）。</summary>
        public static Color SlideColor(float layerF)
        {
            float t = Mathf.Clamp01(layerF);
            var c = Color.Lerp(SlideGround, SlideSky, t);
            c.a = Mathf.Lerp(SlideGroundAlpha, SlideSkyAlpha, t);
            return c;
        }

        /// <summary>中継点(Visible)マーカー用。色相はSlideColorと同じだが、alphaは層に依らず
        /// 常に高く保つ（note-visual-r1.md §7: 「始点と同じ色、視認性は白い輪郭線と高alphaで担保」）。</summary>
        public static Color SlideMarkerColor(float layerF)
        {
            var c = SlideColor(layerF);
            c.a = SlideMarkerAlpha;
            return c;
        }
    }
}
