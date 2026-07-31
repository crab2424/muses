using System;
using System.Collections.Generic;

namespace Muses.Chart
{
    /// <summary>移植元: memory/note-spec.md §1.4。プリセットの選択肢のみで、判定/描画の分岐は増やさない。</summary>
    public enum NoteKind
    {
        Tap,
        ExTap,
        Slide,
        Flick,
    }

    /// <summary>note-spec.md §1.2。区間補間の種別。任意の数式は許さず列挙型に限定する。</summary>
    public enum Easing
    {
        Linear,
        Smooth,
        SineIn,
        SineOut,
        SineInOut,
        QuadIn,
        QuadOut,
    }

    /// <summary>note-spec.md §3。Slide の中継点の見た目/コンボ関与。</summary>
    public enum WaypointMarker
    {
        None,
        Visible,
        Invisible,
    }

    // --- note-spec.md §1.3 判定トレイト（直交する属性の組） ---

    public enum StartTrigger { Contact, Auto, Presence }
    public enum Sustain { None, Ticks }
    public enum Motion { None, Flick }
    public enum Resolution { Cell, Continuous }
    public enum JudgeProfile { Normal, AllPerfect }

    public struct NoteTraits
    {
        public StartTrigger startTrigger;
        public Sustain sustain;
        public Motion motion;
        public Resolution resolution;
        public JudgeProfile judgeProfile;
        public bool chainExempt;
    }

    /// <summary>
    /// note-spec.md §1.4 のプリセット表。新種別を足す手順は、NoteKind に値を足しこの表に1行足すだけ。
    /// </summary>
    public static class NoteKindTraits
    {
        private static readonly Dictionary<NoteKind, NoteTraits> Table = new()
        {
            [NoteKind.Tap] = new NoteTraits
            {
                startTrigger = StartTrigger.Contact, sustain = Sustain.None, motion = Motion.None,
                resolution = Resolution.Cell, judgeProfile = JudgeProfile.Normal, chainExempt = false,
            },
            [NoteKind.ExTap] = new NoteTraits
            {
                startTrigger = StartTrigger.Contact, sustain = Sustain.None, motion = Motion.None,
                resolution = Resolution.Cell, judgeProfile = JudgeProfile.AllPerfect, chainExempt = true,
            },
            [NoteKind.Slide] = new NoteTraits
            {
                startTrigger = StartTrigger.Contact, sustain = Sustain.Ticks, motion = Motion.None,
                resolution = Resolution.Continuous, judgeProfile = JudgeProfile.Normal, chainExempt = false,
            },
            [NoteKind.Flick] = new NoteTraits
            {
                // chainExempt は表上 false だが、note-spec.md §6.2 により早い側だけ免除される片側例外を持つ
                // （両側とも縦連対象、という意味の false ではない。実際の判定はJudge側で個別に扱う）。
                startTrigger = StartTrigger.Presence, sustain = Sustain.None, motion = Motion.Flick,
                resolution = Resolution.Continuous, judgeProfile = JudgeProfile.Normal, chainExempt = false,
            },
        };

        public static NoteTraits Of(NoteKind kind) => Table[kind];
    }

    /// <summary>
    /// note-spec.md §1.1。すべてのノーツの形状は Waypoint の列。
    /// tick は譜面フォーマット層の値（<see cref="ChartFormat"/>参照）。
    /// time はロード時の tick→秒変換で埋められる、ランタイムが実際に読む値。
    /// </summary>
    public struct Waypoint
    {
        public int tick;
        public float layerF;
        public float cellF;
        public float width;
        public Easing easing;
        public WaypointMarker marker;
        /// <summary>この点から次の点までのコンボ刻み幅（tick）の上書き。null なら自動決定（note-spec.md §2.3）</summary>
        public int? comboStep;

        /// <summary>ChartFormat.ResolveTimes() が tick から計算して埋める秒。描画/判定はこちらを読む。</summary>
        public float time;
    }

    /// <summary>
    /// note-spec.md §1.1。Tap/ExTap/Flick は points.Length==1 の退化形、Slide は points.Length>=2。
    /// </summary>
    public class Note
    {
        public NoteKind kind;
        /// <summary>ノーツ単位（Waypoint単位ではない）。既定 0。note-spec.md §5.5。</summary>
        public int scrollGroup;
        public List<Waypoint> points;

        /// <summary>
        /// note-spec.md §2.2。Slide のコンボ点時刻（秒、ロード時に <see cref="ChartFormat.ResolveSlideComboPoints"/>
        /// が生成）。始点(points[0])はContact駆動でJudge.OnEnterが別途解決するため含まない。
        /// Slide以外のkindでは空リストのまま未使用。
        /// </summary>
        public List<float> comboTimes = new();
    }

    public static class ChartMath
    {
        public static float NoteStart(Note n) => n.points[0].time;
        public static float NoteEnd(Note n) => n.points[^1].time;

        /// <summary>note-spec.md §1.2 の easing 種別を [0,1] の補間係数 k に適用する。</summary>
        public static float Ease(Easing e, float k)
        {
            switch (e)
            {
                case Easing.Smooth: return k * k * (3f - 2f * k);
                case Easing.SineIn: return 1f - MathF.Cos(k * MathF.PI / 2f);
                case Easing.SineOut: return MathF.Sin(k * MathF.PI / 2f);
                case Easing.SineInOut: return -(MathF.Cos(MathF.PI * k) - 1f) / 2f;
                case Easing.QuadIn: return k * k;
                case Easing.QuadOut: return 1f - (1f - k) * (1f - k);
                default: return k; // Linear
            }
        }

        /// <summary>
        /// ノーツ上の時刻 t における (layerF, cellF, width) を区間補間で求める。
        /// points.Length==1 の Tap/ExTap/Flick にもそのまま使える（常に始点の値を返す）。
        /// 区間の easing は区間開始側 Waypoint の easing を使う（note-spec.md §1.1）。
        /// </summary>
        public static (float layerF, float cellF, float width) At(Note n, float t)
        {
            var p = n.points;
            if (p.Count == 1 || t <= p[0].time) return (p[0].layerF, p[0].cellF, p[0].width);
            var last = p[^1];
            if (t >= last.time) return (last.layerF, last.cellF, last.width);

            for (int i = 0; i < p.Count - 1; i++)
            {
                var a = p[i];
                var b = p[i + 1];
                if (t >= a.time && t <= b.time)
                {
                    float k = b.time == a.time ? 0f : (t - a.time) / (b.time - a.time);
                    float e = Ease(a.easing, k);
                    return (
                        a.layerF + (b.layerF - a.layerF) * e,
                        a.cellF + (b.cellF - a.cellF) * e,
                        a.width + (b.width - a.width) * e
                    );
                }
            }
            return (last.layerF, last.cellF, last.width);
        }
    }
}
