using System.Collections.Generic;

namespace Muses.Notes
{
    public enum NoteState
    {
        Pending,
        Active,
        Hit,
        Missed,
    }

    /// <summary>移植元: web-prototype/src/notes.ts の NoteRuntime</summary>
    public class NoteRuntime
    {
        public Chart.Note note;
        public NoteState state = NoteState.Pending;
        /// <summary>Mesh の頂点配列中でこのノーツが占める範囲 [vStart, vStart+vCount)</summary>
        public int vStart;
        public int vCount;
        /// <summary>現在の表示アルファ。値が変わらないときは頂点更新をスキップする</summary>
        public float alpha = 1f;

        /// <summary>
        /// note-spec.md §2.2。Slide専用: note.comboTimes のうち、まだ判定確定していない
        /// 先頭のインデックス（始点は含まないので0は「comboTimesの最初の点」）。
        /// </summary>
        public int nextComboIndex;

        /// <summary>
        /// note-spec.md §2.4。Slide専用: 直近フレームの帯占有サンプル (songTime, occupied)。
        /// 未確定コンボ点の判定窓([t_p-100ms, t_p+100ms])より古いものは間引く。
        /// </summary>
        public List<(float time, bool occupied)> slideSamples = new();

        /// <summary>
        /// note-spec.md §4.4。Flick専用: 判定窓内に枠内へ接触があったか（移動が閾値未満で
        /// 不成立に終わった場合のフォールバック判定に使う。移動なし・枠内更新ありならGOOD）。
        /// </summary>
        public bool flickEnterSeen;

        /// <summary>
        /// note-spec.md §6.4「Ex Tap 巻き込みルール」（rev.7）。Tap/Slide始点専用:
        /// 譜面上で同時刻・同一層・セル範囲が交差する Ex Tap が存在するか（ロード時にprecompute）。
        /// true なら実際の入力に関わらず常に PERFECT+ とみなす。chainExempt は引き継がない
        /// （実効窓自体は縦連判定で通常どおり削られる。窓の中に入力があった場合のティアだけが変わる）。
        /// </summary>
        public bool exBoosted;

        /// <summary>
        /// ipad-test-findings-r1.md §④。Slide専用: comboTimes[i] と同じ添字で、その添字が表す
        /// 区間（1つ前のコンボ点〜comboTimes[i]）の頂点範囲。NoteGeometry.PushSlideBand が
        /// コンボ点の境界と頂点範囲の境界を一致させて生成する。Judge が各コンボ点を解決するたび、
        /// この範囲に対して「判定線で食べる(Hit)/そのまま通り過ぎる(Miss)」を書き込む
        /// （NoteView.SetSlideSegmentEatable）。Slide以外は空配列。
        /// </summary>
        public (int start, int count)[] comboSegmentVertexRanges = System.Array.Empty<(int, int)>();
    }
}
