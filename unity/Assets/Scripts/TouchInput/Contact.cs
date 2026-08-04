using System.Collections.Generic;
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
        /// <summary>note-spec.md §0.1。層内のvバンド（0/1固定、bandsPerLayer=2確定）。判定側は無視し、
        /// 枠内更新を発生させるためだけに使う。</summary>
        public int band;
        /// <summary>note-spec.md §0.2。連続座標の cellF（u の線形写像）。Slide/Flick の包含判定に使う。</summary>
        public float cellF;
        /// <summary>note-spec.md §0.2。連続座標の layerF（vGroundJudge/vSkyJudge の線形逆変換）。同上。</summary>
        public float layerF;
        /// <summary>押下開始時刻（Clock.SongTime 基準の秒）</summary>
        public float since;
        /// <summary>note-spec.md §4.1。直近flickWindowMs分の(u,v,time)リングバッファ。Flickの移動量判定に使う。</summary>
        public List<(float u, float v, float t)> history = new();

        /// <summary>
        /// note-spec.md §4.6.4（rev.7）。Riser成立時に書き込む handoff: この時刻(songTime基準)まで、
        /// 包含判定・EnterEvent生成側は実際の layerF ではなく <see cref="layerHandoffTo"/> を使う。
        /// 既定は無効（負の無限大なので songTime &lt;= これは常に false）。
        /// </summary>
        public float layerHandoffUntil = float.NegativeInfinity;
        /// <summary>note-spec.md §4.6.4。handoff が有効な間、layerF の代わりに読まれる値（Riser の layerTo）。</summary>
        public float layerHandoffTo;
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
