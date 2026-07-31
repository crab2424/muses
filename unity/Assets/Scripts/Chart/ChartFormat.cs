using System;
using System.Collections.Generic;

namespace Muses.Chart
{
    /// <summary>note-spec.md §5.1。BPM変化イベント。tick は累積値（前ノーツからの差分ではない）。</summary>
    public struct BpmEvent
    {
        public int tick;
        public float bpm;
    }

    /// <summary>
    /// note-spec.md §5.5。ソフランイベント。group ごとに独立した speed 倍率を持つ。
    /// mul=0 で停止、mul&lt;0 で逆走を許す。durationTicks=0 は階段状（即座に変化）。
    /// 本セッションでは譜面フォーマットとしてのみ保持し、実際の速度積分（X(t)）は未実装（Phase 1後続項目）。
    /// </summary>
    public struct ScrollEvent
    {
        public int tick;
        public int group;
        public float mul;
        public Easing easing;
        public int durationTicks;
    }

    /// <summary>note-spec.md §5.1〜§5.5 の譜面データ本体。ノーツの Waypoint.tick はこの空間の値。</summary>
    public class ChartData
    {
        /// <summary>note-spec.md §5.2: 1〜10全てで割り切れ、64分音符まで整数になる値として確定。</summary>
        public const int TicksPerBeat = 5040;

        public List<BpmEvent> bpmEvents = new();
        public List<ScrollEvent> scrollEvents = new();
        public List<Note> notes = new();
    }

    /// <summary>
    /// 譜面フォーマット層。tick→秒変換をここに閉じ込め、ランタイム（NoteGeometry/Judge）は
    /// 変換後の秒（Waypoint.time）だけを読む（note-spec.md §5.1）。
    /// </summary>
    public static class ChartFormat
    {
        /// <summary>
        /// bpmEvents から tick→秒の区分線形変換関数を構築する。譜面ロード時に1回だけ呼ぶ想定。
        /// </summary>
        public static Func<int, float> BuildTickToSeconds(List<BpmEvent> bpmEvents)
        {
            var sorted = new List<BpmEvent>(bpmEvents);
            sorted.Sort((a, b) => a.tick.CompareTo(b.tick));
            if (sorted.Count == 0 || sorted[0].tick != 0)
                sorted.Insert(0, new BpmEvent { tick = 0, bpm = 120f });

            // 各イベント開始tickでの累積秒をあらかじめ求めておく
            var accSec = new float[sorted.Count];
            for (int i = 1; i < sorted.Count; i++)
            {
                int dTick = sorted[i].tick - sorted[i - 1].tick;
                float secPerTick = 60f / sorted[i - 1].bpm / ChartData.TicksPerBeat;
                accSec[i] = accSec[i - 1] + dTick * secPerTick;
            }

            return tick =>
            {
                int idx = 0;
                for (int i = 1; i < sorted.Count; i++)
                {
                    if (sorted[i].tick > tick) break;
                    idx = i;
                }
                float secPerTick = 60f / sorted[idx].bpm / ChartData.TicksPerBeat;
                return accSec[idx] + (tick - sorted[idx].tick) * secPerTick;
            };
        }

        /// <summary>譜面中の全 Waypoint.time を tick から埋める。ロード時に1回だけ呼ぶ。</summary>
        public static void ResolveTimes(ChartData chart)
        {
            var tickToSeconds = BuildTickToSeconds(chart.bpmEvents);
            foreach (var note in chart.notes)
            {
                for (int i = 0; i < note.points.Count; i++)
                {
                    var wp = note.points[i];
                    wp.time = tickToSeconds(wp.tick);
                    note.points[i] = wp;
                }
            }
        }
    }
}
