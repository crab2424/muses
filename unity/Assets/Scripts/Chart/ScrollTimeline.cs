using System;
using System.Collections.Generic;

namespace Muses.Chart
{
    /// <summary>
    /// note-spec.md §5.5。スクロールグループ1本ぶんの表示位置関数 X(t) = ∫ scrollMul(τ) dτ。
    ///
    /// 譜面ロード時に、区間ごとの積分値(<see cref="Segment.accAtStart"/>)を前もって積んだ
    /// 区分関数として構築する（イージング区間は ChartMath.EaseAntiderivative01 の閉形式で厳密に積分、
    /// 数値積分は行わない）。実行時は songTime から対象区間を探して評価するだけ。
    ///
    /// scrollEvents を持たないグループは常に mul=1 の恒等写像（X(t)=t）になり、
    /// ソフラン未使用の譜面では従来どおり「表示位置＝時刻」のまま変わらない。
    /// </summary>
    public class ScrollTimeline
    {
        private struct Segment
        {
            public float startSec;
            public float endSec; // 必ず > startSec（瞬時遷移は区間を作らないため）
            public float mulFrom;
            public float mulTo;
            public Easing easing;
            public float accAtStart;
        }

        /// <summary>scrollEvents が空のグループ用の恒等写像。X(t) = t。</summary>
        public static readonly ScrollTimeline Identity = new(new List<Segment>(), 1f, 0f, 0f);

        private readonly List<Segment> segments;
        private readonly float tailMul;
        private readonly float tailAcc;
        private readonly float tailStart;

        private ScrollTimeline(List<Segment> segments, float tailMul, float tailAcc, float tailStart)
        {
            this.segments = segments;
            this.tailMul = tailMul;
            this.tailAcc = tailAcc;
            this.tailStart = tailStart;
        }

        /// <summary>秒 t における表示位置 X(t)。区間外(前後)は端の倍率で線形に外挿する。</summary>
        public float XAt(float t)
        {
            if (segments.Count == 0) return t;

            var first = segments[0];
            if (t <= first.startSec) return first.accAtStart + first.mulFrom * (t - first.startSec);

            foreach (var s in segments)
            {
                if (t > s.endSec) continue;
                float dur = s.endSec - s.startSec;
                float k = MathF.Min(1f, MathF.Max(0f, (t - s.startSec) / dur));
                return s.accAtStart + dur * (s.mulFrom * k + (s.mulTo - s.mulFrom) * ChartMath.EaseAntiderivative01(s.easing, k));
            }

            return tailAcc + tailMul * (t - tailStart);
        }

        /// <summary>
        /// 1グループぶんの scrollEvents（tick 昇順である必要はない、内部でソートする）から構築する。
        /// tickToSeconds は ChartFormat.BuildTickToSeconds が返す変換関数を渡す。
        /// </summary>
        public static ScrollTimeline Build(List<ScrollEvent> eventsForGroup, Func<int, float> tickToSeconds)
        {
            var sorted = new List<ScrollEvent>(eventsForGroup);
            sorted.Sort((a, b) => a.tick.CompareTo(b.tick));

            var segs = new List<Segment>();
            float prevMul = 1f; // イベント前は倍率1固定（ソフラン未使用時と同じ挙動）
            float prevEndSec = 0f;
            float acc = 0f;

            foreach (var ev in sorted)
            {
                float startSec = tickToSeconds(ev.tick);
                if (startSec < prevEndSec) continue; // tick順序異常(重複tick等)は先勝ちで無視

                if (startSec > prevEndSec)
                {
                    // 直前イベントから今回イベントまでの定常区間
                    segs.Add(new Segment
                    {
                        startSec = prevEndSec, endSec = startSec,
                        mulFrom = prevMul, mulTo = prevMul, easing = Easing.Linear,
                        accAtStart = acc,
                    });
                    acc += prevMul * (startSec - prevEndSec);
                }

                float endSec = ev.durationTicks > 0 ? tickToSeconds(ev.tick + ev.durationTicks) : startSec;
                if (endSec > startSec)
                {
                    segs.Add(new Segment
                    {
                        startSec = startSec, endSec = endSec,
                        mulFrom = prevMul, mulTo = ev.mul, easing = ev.easing,
                        accAtStart = acc,
                    });
                    float dur = endSec - startSec;
                    acc += dur * (prevMul + (ev.mul - prevMul) * ChartMath.EaseIntegral01(ev.easing));
                    prevEndSec = endSec;
                }
                else
                {
                    prevEndSec = startSec; // 階段状（durationTicks=0）: 区間長ゼロ、位置は変わらない
                }

                prevMul = ev.mul;
            }

            return new ScrollTimeline(segs, prevMul, acc, prevEndSec);
        }
    }
}
