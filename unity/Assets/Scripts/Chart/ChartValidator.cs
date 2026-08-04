using System;
using System.Collections.Generic;

namespace Muses.Chart
{
    /// <summary>editor-spec.md §4。エラー/警告/情報の3段階。</summary>
    public enum ValidationSeverity { Info, Warning, Error }

    /// <summary>editor-spec.md §4。検証結果1件。行クリックでのジャンプ用に tick を持つ。</summary>
    public struct ValidationIssue
    {
        public string id; // "V1".."V11"
        public ValidationSeverity severity;
        public string message;
        public int tick;
    }

    /// <summary>
    /// editor-spec.md §4。譜面の検証（V1〜V11）。UnityEngine 非依存の純粋C#（実装場所の指示どおり）。
    /// 常時実行はせず、呼び出し側（ChartEditorApp の [検証] ボタン・保存時）がこの
    /// <see cref="Validate"/> を呼ぶ。ここでは何も自動的にトリガしない。
    ///
    /// ChartData は ChartFormat.ResolveTimes / ResolveSlideComboPoints 済みであることを前提とする
    /// （Waypoint.time・Note.comboTimes を読むため）。
    /// </summary>
    public static class ChartValidator
    {
        public static List<ValidationIssue> Validate(ChartData chart, int cells = 12, float audioLengthSec = -1f, float offsetSec = 0f)
        {
            var issues = new List<ValidationIssue>();

            ValidateStructure(chart, cells, issues); // V4, V5, V6, V7
            ValidateBpmCrossing(chart, issues); // V1
            ValidateComboOverride(chart, issues); // V2
            ValidateSimultaneousOverlap(chart, issues); // V3
            ValidateChainWindow(chart, issues); // V8
            ValidateScrollDuplicates(chart, issues); // V9
            ValidateAudioLength(chart, audioLengthSec, issues); // V10
            ValidateTotalCombo(chart, issues); // V11
            ValidateOffset(audioLengthSec, offsetSec, issues); // V12

            return issues;
        }

        private static void Add(List<ValidationIssue> issues, string id, ValidationSeverity sev, string message, int tick) =>
            issues.Add(new ValidationIssue { id = id, severity = sev, message = message, tick = tick });

        /// <summary>note-spec.md §1.1: Slide の points が2点未満、または Tap/ExTap/Flick が2点以上はエラー。
        /// あわせて Waypoint.tick の昇順チェック(V5)、cellF/width(V6)、layerF(V7)の範囲チェックも行う。</summary>
        private static void ValidateStructure(ChartData chart, int cells, List<ValidationIssue> issues)
        {
            foreach (var note in chart.notes)
            {
                int tick0 = note.points.Count > 0 ? note.points[0].tick : 0;

                if (note.kind == NoteKind.Slide)
                {
                    if (note.points.Count < 2)
                        Add(issues, "V4", ValidationSeverity.Error, "Slide の Waypoint が2点未満です", tick0);
                }
                else if (note.points.Count >= 2)
                {
                    Add(issues, "V4", ValidationSeverity.Error, $"{note.kind} の Waypoint が2点以上あります（1点のみのはず）", tick0);
                }

                for (int i = 0; i < note.points.Count; i++)
                {
                    var wp = note.points[i];

                    if (i > 0 && wp.tick <= note.points[i - 1].tick)
                        Add(issues, "V5", ValidationSeverity.Error, "Waypoint の tick が昇順ではありません", wp.tick);

                    // editor-ui-rework-r3.md §5: cellFは全種別で左端基準に統一（旧: Slideのみ中心基準）。
                    float lo = wp.cellF;
                    float hi = wp.cellF + wp.width;
                    if (lo < 0f || hi > cells)
                        Add(issues, "V6", ValidationSeverity.Warning, $"cellF範囲([{lo:0.##},{hi:0.##}]) が [0,{cells}] を外れています", wp.tick);

                    if (wp.layerF < 0f || wp.layerF > 1f)
                        Add(issues, "V7", ValidationSeverity.Warning, $"layerF({wp.layerF:0.##}) が [0,1] の範囲外です", wp.tick);
                }

                // note-spec.md §4.6.1（rev.7）: Riser は layerF != layerTo が本質（移動が無ければ意味を持たない）。
                if (note.kind == NoteKind.Riser)
                {
                    var wp0 = note.points[0];
                    if (wp0.layerTo < 0f || wp0.layerTo > 1f)
                        Add(issues, "V7", ValidationSeverity.Warning, $"layerTo({wp0.layerTo:0.##}) が [0,1] の範囲外です", wp0.tick);
                    if (MathF.Abs(wp0.layerTo - wp0.layerF) < 1e-4f)
                        Add(issues, "V13", ValidationSeverity.Warning, "Riser の layerTo が layerF と同じで、層移動がありません", wp0.tick);
                }
            }
        }

        /// <summary>note-spec.md §2.3: BPMが変化するtickをまたぐSlideに、その境界のVisible中継点が無い場合に警告。</summary>
        private static void ValidateBpmCrossing(ChartData chart, List<ValidationIssue> issues)
        {
            var changeTicksAfterZero = new List<int>();
            foreach (var e in chart.bpmEvents)
                if (e.tick > 0) changeTicksAfterZero.Add(e.tick);

            if (changeTicksAfterZero.Count == 0) return;

            foreach (var note in chart.notes)
            {
                if (note.kind != NoteKind.Slide) continue;
                int start = note.points[0].tick;
                int end = note.points[^1].tick;

                foreach (var bpmTick in changeTicksAfterZero)
                {
                    if (bpmTick <= start || bpmTick >= end) continue;

                    bool hasVisibleAtBoundary = false;
                    foreach (var wp in note.points)
                        if (wp.tick == bpmTick && wp.marker == WaypointMarker.Visible) { hasVisibleAtBoundary = true; break; }

                    if (!hasVisibleAtBoundary)
                        Add(issues, "V1", ValidationSeverity.Warning, "BPM変化点をまたぐSlideに、その境界のVisible中継点がありません", bpmTick);
                }
            }
        }

        /// <summary>note-spec.md §2.3: comboStep 上書きを持つ Waypoint の marker が Visible でない場合に警告。</summary>
        private static void ValidateComboOverride(ChartData chart, List<ValidationIssue> issues)
        {
            foreach (var note in chart.notes)
                foreach (var wp in note.points)
                    if (wp.comboStep.HasValue && wp.marker != WaypointMarker.Visible)
                        Add(issues, "V2", ValidationSeverity.Warning, "comboStep 上書きを持つ Waypoint の marker が Visible ではありません", wp.tick);
        }

        /// <summary>ノーツの (layer, cellFの範囲) を求める。先頭Waypointの値を使う。
        /// editor-ui-rework-r3.md §5: cellFは全種別で左端基準（NoteGeometry/Judgeと同じ規則）。</summary>
        private static (bool sky, float lo, float hi) FirstCellRange(Note n)
        {
            var wp = n.points[0];
            bool sky = wp.layerF > 0.5f;
            return (sky, wp.cellF, wp.cellF + wp.width);
        }

        private static bool RangesOverlap(float aLo, float aHi, float bLo, float bHi) => aLo < bHi && bLo < aHi;

        /// <summary>note-spec.md §6.4: 同一層でセル範囲が交差する同時刻ノーツをハイライト対象として情報表示する（禁止ではない）。</summary>
        private static void ValidateSimultaneousOverlap(ChartData chart, List<ValidationIssue> issues)
        {
            var notes = chart.notes;
            for (int i = 0; i < notes.Count; i++)
            {
                var a = notes[i];
                int aTick = a.points[0].tick;
                var (aSky, aLo, aHi) = FirstCellRange(a);

                for (int j = i + 1; j < notes.Count; j++)
                {
                    var b = notes[j];
                    if (b.points[0].tick != aTick) continue;
                    var (bSky, bLo, bHi) = FirstCellRange(b);
                    if (aSky != bSky) continue;
                    if (!RangesOverlap(aLo, aHi, bLo, bHi)) continue;

                    Add(issues, "V3", ValidationSeverity.Info, "同時刻・同一層でセル範囲が交差するノーツがあります", aTick);
                }
            }
        }

        /// <summary>
        /// note-spec.md §6.2。Judge.Prepare と同じ「対象集合T(Tap/ExTap/Flick/Slide始点)を同一層・
        /// セル範囲交差でprev/next探索」を行い、隣接する2点の実時間の間隔が66.7ms未満なら情報表示する
        /// （editor-spec.md V8: 間隔が狭すぎるとPERFECTティアが実質消滅するため）。
        /// </summary>
        private static void ValidateChainWindow(ChartData chart, List<ValidationIssue> issues)
        {
            var group = new List<Note>();
            foreach (var n in chart.notes)
                if (n.kind == NoteKind.Tap || n.kind == NoteKind.ExTap || n.kind == NoteKind.Flick ||
                    n.kind == NoteKind.Riser || n.kind == NoteKind.Slide)
                    group.Add(n);
            group.Sort((a, b) => a.points[0].time.CompareTo(b.points[0].time));

            const float thresholdSec = 0.0667f;

            for (int i = 0; i < group.Count - 1; i++)
            {
                var a = group[i];
                var (aSky, aLo, aHi) = FirstCellRange(a);
                float aTime = a.points[0].time;

                for (int j = i + 1; j < group.Count; j++)
                {
                    var b = group[j];
                    float bTime = b.points[0].time;
                    if (bTime - aTime >= thresholdSec + 1f) break; // 十分離れたら以降も探索不要（早期打ち切り）

                    var (bSky, bLo, bHi) = FirstCellRange(b);
                    if (aSky != bSky || !RangesOverlap(aLo, aHi, bLo, bHi)) continue;

                    if (bTime - aTime < thresholdSec)
                        Add(issues, "V8", ValidationSeverity.Info, $"縦連判定の実効窓が狭く(間隔{(bTime - aTime) * 1000f:0}ms)、PERFECTが出にくい箇所があります", a.points[0].tick);
                    break; // このaに対する直近nextは1つで十分
                }
            }
        }

        /// <summary>note-spec.md §5.5。同一(tick,group)にscrollEventが重複している場合に警告。</summary>
        private static void ValidateScrollDuplicates(ChartData chart, List<ValidationIssue> issues)
        {
            var seen = new HashSet<(int tick, int group)>();
            foreach (var ev in chart.scrollEvents)
            {
                var key = (ev.tick, ev.group);
                if (!seen.Add(key))
                    Add(issues, "V9", ValidationSeverity.Warning, $"scrollEvent が同一tick・グループ{ev.group}に重複しています", ev.tick);
            }
        }

        /// <summary>譜面末尾が音源長を超えている場合に情報表示する（audioLengthSec<=0なら未チェック扱い）。</summary>
        private static void ValidateAudioLength(ChartData chart, float audioLengthSec, List<ValidationIssue> issues)
        {
            if (audioLengthSec <= 0f) return;
            float end = 0f;
            int endTick = 0;
            foreach (var n in chart.notes)
            {
                float e = ChartMath.NoteEnd(n);
                if (e > end) { end = e; endTick = n.points[^1].tick; }
            }
            if (end > audioLengthSec)
                Add(issues, "V10", ValidationSeverity.Info, $"譜面末尾({end:0.0}s)が音源長({audioLengthSec:0.0}s)を超えています", endTick);
        }

        /// <summary>editor-ui-rework-r6.md §4.1(f) / §9 Q4。offsetSecが音源長を超えていると
        /// PreviewClock.Seekが終端へクランプされて常に無音になる(PreviewClock.cs:97)。
        /// 音源が無い(audioLengthSec&lt;=0)場合は判定できないので未チェック扱い。</summary>
        private static void ValidateOffset(float audioLengthSec, float offsetSec, List<ValidationIssue> issues)
        {
            if (audioLengthSec <= 0f) return;
            if (offsetSec > audioLengthSec)
                Add(issues, "V12", ValidationSeverity.Warning,
                    $"オフセット({offsetSec:0.0}s)が音源長({audioLengthSec:0.0}s)を超えています（このままでは無音になります）", 0);
        }

        /// <summary>note-spec.md §7。スコア式のN(総コンボ点数)を情報として表示する。</summary>
        private static void ValidateTotalCombo(ChartData chart, List<ValidationIssue> issues)
        {
            int n = 0;
            foreach (var note in chart.notes)
                n += note.kind == NoteKind.Slide ? note.comboTimes.Count : 1;
            Add(issues, "V11", ValidationSeverity.Info, $"総コンボ点数 N = {n}", 0);
        }
    }
}
