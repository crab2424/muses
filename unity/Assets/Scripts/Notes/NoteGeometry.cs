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
        /// <summary>タップ系ノーツの厚み方向の頂点符号（-1=近い側 / +1=遠い側 / 0=無関係）。
        /// シェーダ側で現在の奥行きに比例した厚みを付けるために使う（<see cref="NoteGeometry"/>のコメント参照）。</summary>
        public float[] side;
        public List<NoteRuntime> runtimes;

        public Vector3[] beatPositions;
        public float[] beatNear;
        public float[] beatLayerF;
    }

    /// <summary>
    /// ノーツの頂点データ生成。移植元: web-prototype/src/notes.ts の NoteField.build()（THREE依存を除く）。
    /// x はワールド座標を焼き込まず (u, y, 時刻) を持たせ、頂点シェーダ（Note.shader）で毎フレーム配置する
    /// （stage.ts と同じ理由: laneConverge変更への追従、長時間譜面でのfloat精度劣化回避）。
    ///
    /// note-spec.md rev.4 のデータモデルに合わせ、Tap/ExTap/Flick は単一Waypointの薄い板、
    /// Slide（旧Hold+旧Arcの統合）は Waypoint 列を通した1本の帯として描く（§2.1）。
    /// 中継点(marker)の個別描画・幅のスナップ表示は Phase 1 後続項目（§8 item11）で未実装。
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
            var sideArr = new List<float>();
            var runtimes = new List<NoteRuntime>();

            void Push(float u, float y, float time, float layerF, Color c, float nearD)
            {
                pos.Add(new Vector3(u, y, time));
                col.Add(c);
                st.Add(1f);
                nearArr.Add(nearD);
                layerArr.Add(layerF);
                sideArr.Add(0f);
            }

            // タップ系ノーツ用: 奥行き方向に薄い板を、頂点シェーダ側で「現在の奥行きに
            // 比例した厚み」に広げてもらうための頂点を積む（全頂点を同じ中心時刻centerTimeで積み、
            // near/far側の判定を side (-1/+1) に持たせる）。
            void QuadThin(float u0, float u1, float y, float centerTime, float layerF, Color c, float nearD)
            {
                float[] uu = { u0, u1, u1, u0 };
                float[] su = { -1f, -1f, 1f, 1f };
                int[] idx = { 0, 1, 2, 0, 2, 3 };
                foreach (var i in idx)
                {
                    pos.Add(new Vector3(uu[i], y, centerTime));
                    col.Add(c);
                    st.Add(1f);
                    nearArr.Add(nearD);
                    layerArr.Add(layerF);
                    sideArr.Add(su[i]);
                }
            }

            var cG = StageGeometry.ColorFromHex(StageColors.Ground);
            var cS = StageGeometry.ColorFromHex(StageColors.Sky);
            var cEx = new Color(0xff / 255f, 0xd5 / 255f, 0x4a / 255f); // Ex Tap: 通常Tapと区別する専用色
            var cFlick = new Color(0xff / 255f, 0x4a / 255f, 0xc8 / 255f); // Flick: 仮の専用色（判定はPhase 1後続項目）
            var cSlide = new Color(0x35 / 255f, 0xe8 / 255f, 0xff / 255f);

            foreach (var n in notes)
            {
                int vStart = st.Count;

                if (n.kind == NoteKind.Tap || n.kind == NoteKind.ExTap || n.kind == NoteKind.Flick)
                {
                    var wp = n.points[0];
                    float layerF = wp.layerF > 0.5f ? 1f : 0f;
                    float y = YAt(layerF, dCopy.skyHeight) + dCopy.zJudge * 0.002f;
                    float u0 = UAt(wp.cellF + 0.04f);
                    float u1 = UAt(wp.cellF + wp.width - 0.04f);
                    var c = n.kind == NoteKind.ExTap ? cEx
                        : n.kind == NoteKind.Flick ? cFlick
                        : (layerF > 0.5f ? cS : cG);
                    QuadThin(u0, u1, y, wp.time, layerF, c, NearOf(layerF));
                }
                else // Slide（旧Hold+旧Arcの統合）: Waypoint列を通した1本の帯
                {
                    PushSlideBand(n, dCopy, Push, NearOf, UAt, YAt, cSlide);
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
                side = sideArr.ToArray(),
                runtimes = runtimes,
                beatPositions = beatPos.ToArray(),
                beatNear = beatNear.ToArray(),
                beatLayerF = beatLayer.ToArray(),
            };
        }

        /// <summary>
        /// Slide は層をまたぐため、頂点ごとに自分の layerF に応じた手前端を持たせる。
        /// 幅もセル分数（cellF ± width/2）をuに変換して持たせ、ワールド単位に焼き込まない。
        /// points.Length==2 の直線区間（旧Holdに相当）も同じコードパスで描ける。
        /// </summary>
        private static void PushSlideBand(
            Note slide, Derived d,
            PushFn push, Func<float, float> nearOf,
            Func<float, float> uAt, Func<float, float, float> yAt, Color c)
        {
            float t0 = ChartMath.NoteStart(slide);
            float t1 = ChartMath.NoteEnd(slide);
            int steps = Math.Max(8, (int)MathF.Ceiling((t1 - t0) / 0.03f));

            (float cellF, float y, float t, float layerF, float width) At(float time)
            {
                var (layerF, cellF, width) = ChartMath.At(slide, time);
                return (cellF, yAt(layerF, d.skyHeight) + d.zJudge * 0.01f, time, layerF, width);
            }

            void Emit((float cellF, float y, float t, float layerF, float width) p, float side) =>
                push(uAt(p.cellF + side * p.width / 2f), p.y, p.t, p.layerF, c, nearOf(p.layerF));

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
