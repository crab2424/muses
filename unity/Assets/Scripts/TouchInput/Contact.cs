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
        /// <summary>note-spec.md §0.2。連続座標の cellF（u の線形写像）。Slide/Flick の包含判定に使う。</summary>
        public float cellF;
        /// <summary>note-spec.md §0.2。連続座標の layerF（vGroundJudge/vSkyJudge の線形逆変換）。同上。</summary>
        public float layerF;
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
        /// <summary>note-spec.md §0.2。このイベントを発生させた接触点の連続座標（Slide始点の包含判定用、item16）</summary>
        public float cellF;
        public float layerF;
    }
}
