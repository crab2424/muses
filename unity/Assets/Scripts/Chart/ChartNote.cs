using System.Collections.Generic;
using Muses.Stage;

namespace Muses.Chart
{
    /// <summary>移植元: web-prototype/src/chart.ts の Note 判別共用体（tap/hold/arc）</summary>
    public enum NoteKind
    {
        Tap,
        Hold,
        Arc,
    }

    /// <summary>アーク制御点。layerF は 0=地上/1=空中 の連続値、cellF はセル境界インデックスの連続値</summary>
    public struct ArcPoint
    {
        public float time;
        public float layerF;
        public float cellF;
    }

    /// <summary>
    /// tap/hold/arc をまとめた1クラス。TS側は判別共用体で kind ごとに持つフィールドが違うが、
    /// C# では簡略化して1クラスに全フィールドを持たせている（kind に応じて使わないフィールドは既定値のまま）。
    /// </summary>
    public class Note
    {
        public NoteKind kind;

        // tap / hold
        public float time;
        public float endTime; // hold のみ
        public Layer layer;
        public int cell;
        /// <summary>tap/hold: 占有セル数（整数値）。arc: 帯の幅（セル数、小数可）</summary>
        public float width;

        // arc
        public List<ArcPoint> points;
    }

    public static class ChartMath
    {
        public static float NoteStart(Note n) => n.kind == NoteKind.Arc ? n.points[0].time : n.time;

        public static float NoteEnd(Note n)
        {
            if (n.kind == NoteKind.Arc) return n.points[n.points.Count - 1].time;
            if (n.kind == NoteKind.Hold) return n.endTime;
            return n.time;
        }

        /// <summary>アークの時刻 t における位置を線形補間（ease in-out）で求める</summary>
        public static (float layerF, float cellF) ArcAt(Note arc, float t)
        {
            var p = arc.points;
            if (t <= p[0].time) return (p[0].layerF, p[0].cellF);
            var last = p[p.Count - 1];
            if (t >= last.time) return (last.layerF, last.cellF);

            for (int i = 0; i < p.Count - 1; i++)
            {
                var a = p[i];
                var b = p[i + 1];
                if (t >= a.time && t <= b.time)
                {
                    float k = b.time == a.time ? 0f : (t - a.time) / (b.time - a.time);
                    float e = k * k * (3f - 2f * k);
                    return (a.layerF + (b.layerF - a.layerF) * e, a.cellF + (b.cellF - a.cellF) * e);
                }
            }
            return (last.layerF, last.cellF);
        }
    }
}
