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

        /// <summary>gameplay-feel-r2.md §2/§6。EARLY/LATE の件数（PERFECT と GOOD のみ数える）。</summary>
        public int early;
        public int late;

        /// <summary>gameplay-feel-r2.md §6。リザルトの種別×層ごとの内訳（<see cref="ResultCategory"/> で引く）。</summary>
        public readonly CategoryStats[] byCategory = NewCategoryArray();

        private static CategoryStats[] NewCategoryArray()
        {
            var a = new CategoryStats[ResultCategories.Count];
            for (int i = 0; i < a.Length; i++) a[i] = new CategoryStats();
            return a;
        }

        /// <summary>判定1件を合計・内訳の両方へ加算する。ms は入力時刻−ノーツ時刻（正=遅い）、NaN なら EARLY/LATE を数えない。</summary>
        public void Add(ResultCategory cat, JudgeKind kind, float ms)
        {
            var c = byCategory[(int)cat];
            switch (kind)
            {
                case JudgeKind.PerfectPlus: perfectPlus++; c.perfectPlus++; break;
                case JudgeKind.Perfect: perfect++; c.perfect++; break;
                case JudgeKind.Good: good++; c.good++; break;
                default: miss++; c.miss++; break;
            }
            switch (EarlyLate.Of(kind, ms))
            {
                case -1: early++; c.early++; break;
                case 1: late++; c.late++; break;
            }
        }

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

    /// <summary>gameplay-feel-r2.md §6。リザルトの内訳の行（種別×層、Riser/Diver は方向別）。</summary>
    public enum ResultCategory
    {
        TapGround,
        TapSky,
        ExTapGround,
        ExTapSky,
        SlideGround,
        SlideSky,
        FlickGround,
        FlickSky,
        Riser,
        Diver,
    }

    public static class ResultCategories
    {
        public const int Count = 10;

        public static string Label(ResultCategory c) => c switch
        {
            ResultCategory.TapGround => "Tap 地上",
            ResultCategory.TapSky => "Tap 空中",
            ResultCategory.ExTapGround => "Ex Tap 地上",
            ResultCategory.ExTapSky => "Ex Tap 空中",
            ResultCategory.SlideGround => "Slide 地上",
            ResultCategory.SlideSky => "Slide 空中",
            ResultCategory.FlickGround => "Flick 地上",
            ResultCategory.FlickSky => "Flick 空中",
            ResultCategory.Riser => "Riser",
            _ => "Diver",
        };
    }

    public class CategoryStats
    {
        public int perfectPlus, perfect, good, miss, early, late;
        public int Total => perfectPlus + perfect + good + miss;
    }

    /// <summary>gameplay-feel-r2.md §2。EARLY/LATE を出すのは PERFECT と GOOD だけ（PERFECT+・MISS・ms=NaN は出さない）。</summary>
    public static class EarlyLate
    {
        /// <returns>-1=EARLY / +1=LATE / 0=表示しない</returns>
        public static int Of(JudgeKind kind, float ms)
        {
            if (kind != JudgeKind.Perfect && kind != JudgeKind.Good) return 0;
            if (float.IsNaN(ms) || ms == 0f) return 0;
            return ms < 0f ? -1 : 1;
        }
    }

    /// <summary>移植元: web-prototype/src/overlay.ts の HitFlash</summary>
    public struct HitFlash
    {
        public Layer layer;
        public int cell;
        /// <summary>gameplay-feel-r1.md §3/§4。ノーツ左端の連続値（判定名・ヒット演出をノーツ中央に置くため）。</summary>
        public float cellF;
        public float width;
        /// <summary>gameplay-feel-r2.md §5。演出を出す高さ（layerF、0=地上判定線/1=空中判定線、間は連続）。</summary>
        public float layerF;
        public float born;
        public JudgeKind kind;
        /// <summary>gameplay-feel-r2.md §2。入力時刻−ノーツ時刻(ms、正=遅い)。NaN は早い/遅いが意味を持たない判定。</summary>
        public float ms;
        /// <summary>gameplay-feel-r1.md §4。Slide の始点以外のコンボ点（演出を軽量版にする）。</summary>
        public bool slideTick;
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
