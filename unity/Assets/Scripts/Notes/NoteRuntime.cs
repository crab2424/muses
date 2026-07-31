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
    }
}
