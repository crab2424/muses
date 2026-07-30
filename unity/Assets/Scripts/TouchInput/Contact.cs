using Muses.Stage;

namespace Muses.TouchInput
{
    /// <summary>移植元: web-prototype/src/input.ts の Contact</summary>
    public class Contact
    {
        public int id;
        public float u;
        public float v;
        public Layer layer;
        public int cell;
        /// <summary>押下開始時刻（Clock.SongTime 基準の秒）</summary>
        public float since;
    }

    /// <summary>移植元: web-prototype/src/input.ts の EnterEvent</summary>
    public struct EnterEvent
    {
        public Layer layer;
        public int cell;
        /// <summary>新規接触か、移動によるセル更新か</summary>
        public bool fresh;
        /// <summary>イベント発生時刻（songTime 基準の秒）</summary>
        public float at;
    }
}
