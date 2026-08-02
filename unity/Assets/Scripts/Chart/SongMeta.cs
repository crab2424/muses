using System;
using System.Collections.Generic;
using System.Globalization;

namespace Muses.Chart
{
    /// <summary>
    /// editor-spec.md §1.4。note-spec.md には無い、エディタ層だけの概念。ChartData には入らず、
    /// tick↔bar:beat:tick 表記の変換にのみ使う。
    /// </summary>
    public struct MeterEvent
    {
        public int bar;
        public int numerator;
        public int denominator;
    }

    /// <summary>editor-spec.md §1.5。song.muses 1本ぶんの内容（曲メタ）。</summary>
    public class SongMeta
    {
        public string title = "";
        public string artist = "";
        public string audio = "";
        public string jacket = "";
        public float previewStart;
        public float previewEnd;
        /// <summary>音源先頭 → 譜面tick0のズレ(秒)。StageConfigのjudgeOffsetMs/visualOffsetMsとは別物。</summary>
        public float offsetSec;
        public List<MeterEvent> meters = new();
        public List<BpmEvent> bpmEvents = new();
    }

    /// <summary>
    /// editor-spec.md §1.4。bar:beat:tick ↔ 累積tick の変換。
    /// beat の単位はその区間の拍子の分母音符（4/4ならbeat=四分音符、6/8ならbeat=八分音符）。
    /// editor-ui-rework-r4.md §10: barは0始まり（beatは1始まり、tickは0始まり）。
    /// アウフタクト（弱起）を0小節目に置けるようにするため、参照元(MikuMikuWorld)と同じ
    /// 0始まりに揃えた（旧: bar=1始まり、0小節目にあたる場所が無くアウフタクトを表現できなかった）。
    /// meters が空、または先頭barが0でない場合は「bar=0から4/4」を補って扱う
    /// （song.musesの[METER]省略時の既定と同じ）。
    /// </summary>
    public static class SongAddr
    {
        public static int TicksPerBeatUnit(int denominator) =>
            ChartData.TicksPerBeat * 4 / denominator;

        public static int TicksPerBar(MeterEvent m) => m.numerator * TicksPerBeatUnit(m.denominator);

        public static List<MeterEvent> Normalize(List<MeterEvent> meters)
        {
            var sorted = new List<MeterEvent>(meters);
            sorted.Sort((a, b) => a.bar.CompareTo(b.bar));
            if (sorted.Count == 0 || sorted[0].bar != 0)
                sorted.Insert(0, new MeterEvent { bar = 0, numerator = 4, denominator = 4 });
            return sorted;
        }

        public static int ToTick(List<MeterEvent> meters, int bar, int beat, int tick)
        {
            var sorted = Normalize(meters);
            int accTick = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                int segStartBar = sorted[i].bar;
                int segEndBar = i + 1 < sorted.Count ? sorted[i + 1].bar : int.MaxValue;
                int ticksPerBar = TicksPerBar(sorted[i]);
                if (bar < segEndBar)
                {
                    return accTick
                           + (bar - segStartBar) * ticksPerBar
                           + (beat - 1) * TicksPerBeatUnit(sorted[i].denominator)
                           + tick;
                }
                accTick += (segEndBar - segStartBar) * ticksPerBar;
            }
            throw new InvalidOperationException("unreachable: last meter segment has no end");
        }

        public static (int bar, int beat, int tick) ToAddr(List<MeterEvent> meters, int absTick)
        {
            var sorted = Normalize(meters);
            int accTick = 0;
            for (int i = 0; i < sorted.Count; i++)
            {
                int segStartBar = sorted[i].bar;
                bool hasNext = i + 1 < sorted.Count;
                int ticksPerBar = TicksPerBar(sorted[i]);
                int? segTicks = hasNext ? (sorted[i + 1].bar - segStartBar) * ticksPerBar : (int?)null;

                if (!segTicks.HasValue || absTick < accTick + segTicks.Value)
                {
                    int rel = absTick - accTick;
                    int barsIn = rel / ticksPerBar;
                    int remInBar = rel % ticksPerBar;
                    int beatUnit = TicksPerBeatUnit(sorted[i].denominator);
                    int beatsIn = remInBar / beatUnit;
                    int tickIn = remInBar % beatUnit;
                    return (segStartBar + barsIn, beatsIn + 1, tickIn);
                }
                accTick += segTicks.Value;
            }
            throw new InvalidOperationException("unreachable");
        }

        /// <summary>"5:3:1260" 形式の文字列を解析する。</summary>
        public static (int bar, int beat, int tick) ParseAddr(string s)
        {
            var parts = s.Split(':');
            if (parts.Length != 3)
                throw new FormatException($"invalid addr '{s}' (expected bar:beat:tick)");
            return (
                int.Parse(parts[0], CultureInfo.InvariantCulture),
                int.Parse(parts[1], CultureInfo.InvariantCulture),
                int.Parse(parts[2], CultureInfo.InvariantCulture)
            );
        }

        public static string FormatAddr((int bar, int beat, int tick) addr) =>
            $"{addr.bar}:{addr.beat}:{addr.tick}";

        /// <summary>editor-ui-rework-r4.md §11: ステータスバー表示専用。tickを4桁ゼロ埋めする
        /// (TicksPerBeat=5040なので1拍内のtickは最大4桁)。.musesファイルやインスペクタの
        /// 「位置」欄はFormatAddrのまま(ゼロ埋めはステータスバーだけで良いとユーザー確定済み)。</summary>
        public static string FormatAddrPadded((int bar, int beat, int tick) addr) =>
            $"{addr.bar}:{addr.beat}:{addr.tick:0000}";
    }
}
