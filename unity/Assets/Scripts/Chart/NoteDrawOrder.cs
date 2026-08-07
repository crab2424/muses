namespace Muses.Chart
{
    /// <summary>
    /// ノーツが重なったときの前後関係の唯一の定義。
    ///
    /// editor-ui-rework-r13.md §3 でエディタ（2Dピアノロール）向けに決めたユーザー確定順
    /// slide→tap→flick→extap→riser/diver を、**プレビュー/ゲーム本体（3D）にも共有する**。
    /// 元は ChartEditorApp.DrawPriority にしか無く、NoteGeometry.Build は譜面リスト順
    /// （＝おおむね追加順）でメッシュへ積んでいたため、同じ譜面がタイムラインとプレビューで
    /// 違う重なり方に見えていた（2026-08-07 ユーザー報告）。
    ///
    /// 3D側でこれが効くのは Note.shader が ZWrite Off / ZTest Always で、描画順が
    /// renderQueue とメッシュ内の頂点順だけで決まるため（note-visual-r1.md §5.3）。
    /// Ground の Slide 帯をほぼ不透明にした結果、この順序が「どちらが完全に隠れるか」を
    /// 直接決めるようになっている。
    /// </summary>
    public static class NoteDrawOrder
    {
        /// <summary>大きいほど手前。描画は昇順、ヒットテストは降順でこの1つの関数から導く
        /// （片方だけ直すと「見えているのに掴めない」がずれるため）。</summary>
        public static int Priority(Note n) => n.kind switch
        {
            NoteKind.Slide => 0,
            NoteKind.Tap => 1,
            NoteKind.Flick => 2,
            NoteKind.ExTap => 3,
            NoteKind.Riser => 4,
            _ => 1,
        };

        public const int Count = 5;
    }
}
