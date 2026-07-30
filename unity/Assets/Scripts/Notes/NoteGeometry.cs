using System;
using System.Collections.Generic;
using Muses.Chart;
using Muses.Stage;
using UnityEngine;

namespace Muses.Notes
{
    public struct NoteMeshData
    {
        public Vector3[] positions; // (u, y, ノーツ時刻)
        public Color[] colors;
        public float[] state;
        public float[] near;
        public float[] layerF;
        public List<NoteRuntime> runtimes;

        public Vector3[] beatPositions;
        public float[] beatNear;
        public float[] beatLayerF;
    }

    /// <summary>
    /// ノーツの頂点データ生成。移植元: web-prototype/src/notes.ts の NoteField.build()（THREE依存を除く）。
    /// x はワールド座標を焼き込まず (u, y, 時刻) を持たせ、頂点シェーダ（Note.shader）で毎フレーム配置する
    /// （stage.ts と同じ理由: laneConverge変更への追従、長時間譜面でのfloat精度劣化回避）。
    /// </summary>
    public static class NoteGeometry
    {
        private delegate void PushFn(float u, float y, float time, float layerF, Color c, float nearD);

        public static NoteMeshData Build(StageConfig cfg, in Derived d, List<Note> notes)
        {
            Derived dCopy = d; // in パラメータはローカル関数から直接キャプチャできない (CS1628)
            int cells = cfg.cells;

            float UAt(float cellF) => -1f + 2f * cellF / cells;
            float YAt(float layerF, float skyHeight) => layerF * skyHeight;
            float NearOf(float layerF) => dCopy.groundNear + (dCopy.skyNear - dCopy.groundNear) * layerF;

            var pos = new List<Vector3>();
            var col = new List<Color>();
            var st = new List<float>();
            var nearArr = new List<float>();
            var layerArr = new List<float>();
            var runtimes = new List<NoteRuntime>();

            void Push(float u, float y, float time, float layerF, Color c, float nearD)
            {
                pos.Add(new Vector3(u, y, time));
                col.Add(c);
                st.Add(1f);
                nearArr.Add(nearD);
                layerArr.Add(layerF);
            }

            void Quad(Vector3[] p, float layerF, Color c, float nearD)
            {
                int[] idx = { 0, 1, 2, 0, 2, 3 };
                foreach (var i in idx) Push(p[i].x, p[i].y, p[i].z, layerF, c, nearD);
            }

            var cG = StageGeometry.ColorFromHex(StageColors.Ground);
            var cS = StageGeometry.ColorFromHex(StageColors.Sky);
            // タップノーツの奥行き方向の厚み。判定線の奥行きに比例させ、画角を変えても見た目の厚みを保つ
            float thicknessWorld = dCopy.zJudge * 0.05f;

            foreach (var n in notes)
            {
                int vStart = st.Count;

                if (n.kind == NoteKind.Tap)
                {
                    float layerF = n.layer == Layer.Sky ? 1f : 0f;
                    float dt = thicknessWorld / dCopy.speed / 2f;
                    float y = YAt(layerF, dCopy.skyHeight) + dCopy.zJudge * 0.002f;
                    float u0 = UAt(n.cell + 0.04f);
                    float u1 = UAt(n.cell + n.width - 0.04f);
                    Quad(new[]
                    {
                        new Vector3(u0, y, n.time - dt),
                        new Vector3(u1, y, n.time - dt),
                        new Vector3(u1, y, n.time + dt),
                        new Vector3(u0, y, n.time + dt),
                    }, layerF, n.layer == Layer.Sky ? cS : cG, NearOf(layerF));
                }
                else if (n.kind == NoteKind.Hold)
                {
                    float layerF = n.layer == Layer.Sky ? 1f : 0f;
                    float dt = thicknessWorld / dCopy.speed / 2f;
                    float y = YAt(layerF, dCopy.skyHeight) + dCopy.zJudge * 0.002f;
                    var c = n.layer == Layer.Sky ? cS : cG;
                    float nr = NearOf(layerF);

                    float u0 = UAt(n.cell + 0.16f);
                    float u1 = UAt(n.cell + n.width - 0.16f);
                    Quad(new[]
                    {
                        new Vector3(u0, y, n.time),
                        new Vector3(u1, y, n.time),
                        new Vector3(u1, y, n.endTime),
                        new Vector3(u0, y, n.endTime),
                    }, layerF, Dim(c, 0.6f), nr);

                    float hu0 = UAt(n.cell + 0.04f);
                    float hu1 = UAt(n.cell + n.width - 0.04f);
                    float hy = y + dCopy.zJudge * 0.001f;
                    Quad(new[]
                    {
                        new Vector3(hu0, hy, n.time - dt),
                        new Vector3(hu1, hy, n.time - dt),
                        new Vector3(hu1, hy, n.time + dt),
                        new Vector3(hu0, hy, n.time + dt),
                    }, layerF, c, nr);
                }
                else
                {
                    PushArc(n, dCopy, Push, NearOf, UAt, YAt);
                }

                runtimes.Add(new NoteRuntime
                {
                    note = n,
                    state = NoteState.Pending,
                    vStart = vStart,
                    vCount = st.Count - vStart,
                    alpha = 1f,
                    lastHeld = float.NegativeInfinity,
                });
            }

            // ビートライン（地上のみ）
            float b = 60f / cfg.bpm;
            float last = notes.Count > 0 ? ChartMath.NoteEnd(notes[notes.Count - 1]) : 0f;
            var beatPos = new List<Vector3>();
            var beatNear = new List<float>();
            var beatLayer = new List<float>();
            for (float t = 0; t < last + 4f; t += b * 4f)
            {
                beatPos.Add(new Vector3(-1f, dCopy.zJudge * 0.0005f, t));
                beatPos.Add(new Vector3(1f, dCopy.zJudge * 0.0005f, t));
                beatNear.Add(dCopy.groundNear);
                beatNear.Add(dCopy.groundNear);
                beatLayer.Add(0f);
                beatLayer.Add(0f);
            }

            return new NoteMeshData
            {
                positions = pos.ToArray(),
                colors = col.ToArray(),
                state = st.ToArray(),
                near = nearArr.ToArray(),
                layerF = layerArr.ToArray(),
                runtimes = runtimes,
                beatPositions = beatPos.ToArray(),
                beatNear = beatNear.ToArray(),
                beatLayerF = beatLayer.ToArray(),
            };
        }

        private static Color Dim(Color c, float k) => new(c.r * k, c.g * k, c.b * k, c.a);

        /// <summary>
        /// アークは層をまたぐため、頂点ごとに自分の layerF に応じた手前端を持たせる。
        /// 幅もセル分数（cellF ± width/2）をuに変換して持たせ、ワールド単位に焼き込まない。
        /// </summary>
        private static void PushArc(
            Note arc, Derived d,
            PushFn push, Func<float, float> nearOf,
            Func<float, float> uAt, Func<float, float, float> yAt)
        {
            float t0 = ChartMath.NoteStart(arc);
            float t1 = ChartMath.NoteEnd(arc);
            int steps = Math.Max(8, (int)MathF.Ceiling((t1 - t0) / 0.03f));
            var c = new Color(0x35 / 255f, 0xe8 / 255f, 0xff / 255f);

            (float cellF, float y, float t, float layerF) At(float time)
            {
                var (layerF, cellF) = ChartMath.ArcAt(arc, time);
                return (cellF, yAt(layerF, d.skyHeight) + d.zJudge * 0.01f, time, layerF);
            }

            void Emit((float cellF, float y, float t, float layerF) p, float side) =>
                push(uAt(p.cellF + side * arc.width / 2f), p.y, p.t, p.layerF, c, nearOf(p.layerF));

            var prev = At(t0);
            for (int i = 1; i <= steps; i++)
            {
                var cur = At(t0 + (t1 - t0) * i / steps);
                Emit(prev, -1f);
                Emit(prev, 1f);
                Emit(cur, 1f);
                Emit(prev, -1f);
                Emit(cur, 1f);
                Emit(cur, -1f);
                prev = cur;
            }
        }
    }
}
