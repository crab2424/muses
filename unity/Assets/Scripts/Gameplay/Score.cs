using Muses.Stage;

namespace Muses.Gameplay
{
    /// <summary>移植元: web-prototype/src/judge.ts の Score。ティア構成は note-spec.md §6.1 で4段に確定。</summary>
    public class Score
    {
        public int perfectPlus;
        public int perfect;
        public int good;
        public int miss;
        public int combo;
        public int maxCombo;
        public string lastJudge = "";
        public float lastMs;

        /// <summary>
        /// note-spec.md §7。NP = 1,000,000 / N（N=総コンボ点数）、丸めはノーツごとではなく最後に1回だけ。
        /// N には Slide の全コンボ点数（note-spec.md §2.2）も含める必要があるが、
        /// Slideのコンボ点判定自体が未実装のため、現状は呼び出し側でNの数え方に注意が必要
        /// （Tap/ExTap/Flickのみのチャートであれば notes.Count がそのままNになる）。
        /// </summary>
        public int ComputeScore(int totalComboPoints)
        {
            if (totalComboPoints <= 0) return 0;
            long numerator = (long)System.Math.Round(101 * (double)perfectPlus + 100 * (double)perfect + 50 * (double)good);
            return (int)System.Math.Round(1_000_000.0 * numerator / (100.0 * totalComboPoints));
        }
    }

    /// <summary>note-spec.md §6.1 のティア種別。HitFlashの見た目分岐にも使う。</summary>
    public enum JudgeKind
    {
        PerfectPlus,
        Perfect,
        Good,
        Miss,
    }

    /// <summary>移植元: web-prototype/src/overlay.ts の HitFlash</summary>
    public struct HitFlash
    {
        public Layer layer;
        public int cell;
        public float width;
        public float born;
        public JudgeKind kind;
    }

    /// <summary>note-spec.md §6.1。判定ティアを配列データとして持つ（列挙+switchの分岐にしない）。</summary>
    public struct JudgeTier
    {
        public JudgeKind kind;
        public float halfWidthMs;
        public float weight;
    }

    public static class JudgeTiers
    {
        /// <summary>半幅の狭い順（PERFECT+ → PERFECT → GOOD）。60fpsの2/4/6フレームに対応。</summary>
        public static readonly JudgeTier[] All =
        {
            new() { kind = JudgeKind.PerfectPlus, halfWidthMs = 33.33f, weight = 1.01f },
            new() { kind = JudgeKind.Perfect, halfWidthMs = 66.67f, weight = 1.00f },
            new() { kind = JudgeKind.Good, halfWidthMs = 100f, weight = 0.50f },
        };

        /// <summary>絶対値ms（|dt|）が全ティアの外なら null（MISS）を返す。</summary>
        public static JudgeTier? TierFor(float absMs)
        {
            foreach (var t in All)
                if (absMs <= t.halfWidthMs) return t;
            return null;
        }
    }
}
