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
        /// <summary>Slide: 最後に保持できていた時刻</summary>
        public float lastHeld = float.NegativeInfinity;
    }
}
