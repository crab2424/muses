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
    /// 速度積分 X(t) は <see cref="ScrollTimeline"/> が担う（<see cref="ChartFormat.BuildScrollTimelines"/> 参照）。
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

        private static List<BpmEvent> SortedBpmEvents(List<BpmEvent> bpmEvents)
        {
            var sorted = new List<BpmEvent>(bpmEvents);
            sorted.Sort((a, b) => a.tick.CompareTo(b.tick));
            if (sorted.Count == 0 || sorted[0].tick != 0)
                sorted.Insert(0, new BpmEvent { tick = 0, bpm = 120f });
            return sorted;
        }

        /// <summary>editor-spec.md §5.1。プレビューのメトロノーム用: 秒(songTime)時点のBPMを引く。</summary>
        public static float BpmAtTime(List<BpmEvent> bpmEvents, float songTime)
        {
            var sorted = SortedBpmEvents(bpmEvents);
            var toSeconds = BuildTickToSeconds(bpmEvents);
            float bpm = sorted[0].bpm;
            foreach (var e in sorted)
            {
                if (toSeconds(e.tick) > songTime) break;
                bpm = e.bpm;
            }
            return bpm;
        }

        private static float BpmAt(List<BpmEvent> sortedBpmEvents, int tick)
        {
            float bpm = sortedBpmEvents[0].bpm;
            foreach (var e in sortedBpmEvents)
            {
                if (e.tick > tick) break;
                bpm = e.bpm;
            }
            return bpm;
        }

        /// <summary>
        /// note-spec.md §2.3。刻みの実時間が0.125〜0.25秒(毎秒4〜8コンボ)に収まる音価を選ぶ表。
        /// 境界の60/120/240はこの帯の切れ目そのものなので、表外のBPMも実時間基準で自動的に決まる。
        /// </summary>
        private static int ComboStepTicksForBpm(float bpm)
        {
            if (bpm < 60f) return ChartData.TicksPerBeat / 8; // 32分
            if (bpm < 120f) return ChartData.TicksPerBeat / 4; // 16分
            if (bpm < 240f) return ChartData.TicksPerBeat / 2; // 8分
            if (bpm < 480f) return ChartData.TicksPerBeat; // 4分
            return ChartData.TicksPerBeat * 2; // 2分
        }

        /// <summary>
        /// note-spec.md §2.2/§2.3。Slide のコンボ点集合を生成し Note.comboTimes に秒で格納する。
        /// ResolveTimes() の後に呼ぶこと。
        ///
        /// 規則: 始点は無条件コンボ点だが Contact 駆動で Judge.OnEnter が別途解決するため
        /// comboTimes には含めない。終点は marker 不問で無条件に含む。Visible 中継点を含む。
        /// 始点を基準に comboStep 刻みで自動生成した点を、区間ごとに刻み直さず連続して積む
        /// （中継点は位相をリセットしない）。端数（最後の自動生成点〜終点）は捨てる。
        /// Waypoint.comboStep による上書きは、その tick 以降の自動生成刻みに適用する。
        /// </summary>
        public static void ResolveSlideComboPoints(ChartData chart)
        {
            var sortedBpm = SortedBpmEvents(chart.bpmEvents);
            var tickToSeconds = BuildTickToSeconds(chart.bpmEvents);

            foreach (var note in chart.notes)
            {
                if (note.kind != NoteKind.Slide)
                {
                    note.comboTimes = new List<float>();
                    continue;
                }

                var pts = note.points;
                int startTick = pts[0].tick;
                int endTick = pts[^1].tick;

                var overrides = new List<(int tick, int step)>();
                foreach (var wp in pts)
                    if (wp.comboStep.HasValue)
                        overrides.Add((wp.tick, wp.comboStep.Value));
                overrides.Sort((a, b) => a.tick.CompareTo(b.tick));

                int StepAt(int tick)
                {
                    int step = -1;
                    foreach (var o in overrides)
                    {
                        if (o.tick > tick) break;
                        step = o.step;
                    }
                    return step >= 0 ? step : ComboStepTicksForBpm(BpmAt(sortedBpm, tick));
                }

                var ticks = new SortedSet<int> { endTick };
                foreach (var wp in pts)
                    if (wp.marker == WaypointMarker.Visible)
                        ticks.Add(wp.tick);

                int cur = startTick;
                while (true)
                {
                    int step = Math.Max(1, StepAt(cur));
                    int next = cur + step;
                    if (next >= endTick) break;
                    ticks.Add(next);
                    cur = next;
                }

                ticks.Remove(startTick); // 始点はContact駆動で別解決するため含めない

                var times = new List<float>(ticks.Count);
                foreach (var t in ticks) times.Add(tickToSeconds(t));
                note.comboTimes = times;
            }
        }

        /// <summary>
        /// note-spec.md §5.5。scrollEvents をグループごとに束ね、各グループの X(t) を
        /// ScrollTimeline として構築する。ResolveTimes() 後どのタイミングで呼んでもよい
        /// （tick→秒変換を独自に再構築するため）。scrollEvents を持たないグループは
        /// 呼び出し側で ScrollTimeline.Identity を使うこと（このMapには含まれない）。
        /// </summary>
        public static Dictionary<int, ScrollTimeline> BuildScrollTimelines(ChartData chart)
        {
            var tickToSeconds = BuildTickToSeconds(chart.bpmEvents);
            var byGroup = new Dictionary<int, List<ScrollEvent>>();
            foreach (var ev in chart.scrollEvents)
            {
                if (!byGroup.TryGetValue(ev.group, out var list))
                    byGroup[ev.group] = list = new List<ScrollEvent>();
                list.Add(ev);
            }

            var result = new Dictionary<int, ScrollTimeline>();
            foreach (var kv in byGroup)
                result[kv.Key] = ScrollTimeline.Build(kv.Value, tickToSeconds);
            return result;
        }
    }
}
