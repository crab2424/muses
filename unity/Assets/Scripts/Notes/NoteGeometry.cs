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
        /// <summary>note-spec.md §5.5。頂点が属するスクロールグループ（NoteView が _GroupX[] を引くためのインデックス）</summary>
        public float[] group;
        /// <summary>note-visual-r1.md §1.5/§8-3。フラグメントシェーダのSDF描画用ローカルUV（TEXCOORD3）。
        /// Note.shaderのモード判定は (aSide, localUv.y) の組で行う（NoteGeometryのPush呼び出し規約）:
        /// - side!=0（タップ系の薄い板）: 矩形内の単位正方形座標(0..1, 0..1)で角丸+輪郭線に使う。
        /// - side==0 かつ localUv.y==0（Slide帯）: xのみ意味を持つ（0=左端/1=右端）。
        ///   輪郭線・中央線は端/中央(x)方向のみ（帯を時間方向に分割しても継ぎ目が出ないように）。
        /// - side==0 かつ localUv.y&lt;0（Riserの縁線・矢印など）: SDF処理をせず頂点色をそのまま使う。</summary>
        public Vector2[] localUv;
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
    /// §8 item11: Visible中継点はTapと同じ形のマーカーを帯の上に重ねて描く
    /// （note-visual-r1.md §7: 色は帯(始点)と同じ色相、alphaのみ常時高く保つ）。
    /// 幅/easingの区間補間は既にPushSlideBand（ChartMath.At経由）で対応済み。
    /// </summary>
    public static class NoteGeometry
    {
        private delegate void PushFn(float u, float y, float time, float layerF, Color c, float nearD, float localU, float localV);

        public static NoteMeshData Build(StageConfig cfg, in Derived d, List<Note> notes,
            Dictionary<int, Chart.ScrollTimeline> scrollTimelines = null, List<float> barTimes = null)
        {
            Derived dCopy = d; // in パラメータはローカル関数から直接キャプチャできない (CS1628)
            int cells = cfg.cells;

            float UAt(float cellF) => -1f + 2f * cellF / cells;
            float YAt(float layerF, float skyHeight) => layerF * skyHeight;
            float NearOf(float layerF) => dCopy.groundNear + (dCopy.skyNear - dCopy.groundNear) * layerF;

            // note-spec.md §5.5。グループごとの X(t)。scrollEvents を持たないグループは恒等写像(X(t)=t)。
            Chart.ScrollTimeline TimelineFor(int group) =>
                scrollTimelines != null && scrollTimelines.TryGetValue(group, out var tl) ? tl : Chart.ScrollTimeline.Identity;

            var pos = new List<Vector3>();
            var col = new List<Color>();
            var st = new List<float>();
            var nearArr = new List<float>();
            var layerArr = new List<float>();
            var sideArr = new List<float>();
            var groupArr = new List<float>();
            var uv3Arr = new List<Vector2>();
            var runtimes = new List<NoteRuntime>();

            void Push(float u, float y, float time, float layerF, Color c, float nearD, float localU, float localV)
            {
                pos.Add(new Vector3(u, y, time));
                col.Add(c);
                st.Add(1f);
                nearArr.Add(nearD);
                layerArr.Add(layerF);
                sideArr.Add(0f);
                uv3Arr.Add(new Vector2(localU, localV));
            }

            // タップ系ノーツ用: 奥行き方向に薄い板を、頂点シェーダ側で「現在の奥行きに
            // 比例した厚み」に広げてもらうための頂点を積む（全頂点を同じ中心時刻centerTimeで積み、
            // near/far側の判定を side (-1/+1) に持たせる）。
            // note-visual-r1.md §3-3/§8-3: ローカルUV(0..1, 0..1)も同時に積み、フラグメント側の
            // 角丸+輪郭線SDF（Note.shader）で使う。
            void QuadThin(float u0, float u1, float y, float centerTime, float layerF, Color c, float nearD)
            {
                float[] uu = { u0, u1, u1, u0 };
                float[] su = { -1f, -1f, 1f, 1f };
                float[] lu = { 0f, 1f, 1f, 0f };
                float[] lv = { 0f, 0f, 1f, 1f };
                int[] idx = { 0, 1, 2, 0, 2, 3 };
                foreach (var i in idx)
                {
                    pos.Add(new Vector3(uu[i], y, centerTime));
                    col.Add(c);
                    st.Add(1f);
                    nearArr.Add(nearD);
                    layerArr.Add(layerF);
                    sideArr.Add(su[i]);
                    uv3Arr.Add(new Vector2(lu[i], lv[i]));
                }
            }

            // note-visual-r1.md §4: 色は NoteColors に一元化済み（旧: このファイル・エディタ・
            // StageColors の3箇所に別々のリテラルがありドリフトしていた）。
            var cTap = NoteColors.Tap; // note-visual-r1.md §4.2: Tapは「操作」の色、層で変えない
            var cEx = NoteColors.ExTap;
            var cFlick = NoteColors.Flick;
            // note-visual-r1.md §6: 壁の塗りを撤去し、左右の細い縁線＋矢印だけにしたため、
            // 大面積の半透明(旧alpha0.35)ではなく縁線として視認できるalphaへ上げる。
            var cRiser = new Color(NoteColors.Riser.r, NoteColors.Riser.g, NoteColors.Riser.b, 0.9f);
            var cDiver = new Color(NoteColors.Diver.r, NoteColors.Diver.g, NoteColors.Diver.b, 0.9f);
            var cRiserArrow = new Color(1f, 1f, 1f, 0.9f); // riser-r2.md §3.2 由来。矢印は白のまま

            foreach (var n in notes)
            {
                int vStart = st.Count;
                var timeline = TimelineFor(n.scrollGroup);

                if (n.kind == NoteKind.Tap || n.kind == NoteKind.ExTap || n.kind == NoteKind.Flick)
                {
                    var wp = n.points[0];
                    float layerF = wp.layerF > 0.5f ? 1f : 0f;
                    float y = YAt(layerF, dCopy.skyHeight) + dCopy.zJudge * 0.002f;
                    float u0 = UAt(wp.cellF + 0.04f);
                    float u1 = UAt(wp.cellF + wp.width - 0.04f);
                    // note-visual-r1.md §4.2: Tapは層で色を変えない（「操作」の色のため）。
                    var c = n.kind == NoteKind.ExTap ? cEx
                        : n.kind == NoteKind.Flick ? cFlick
                        : cTap;
                    QuadThin(u0, u1, y, timeline.XAt(wp.time), layerF, c, NearOf(layerF));
                }
                else if (n.kind == NoteKind.Riser)
                {
                    // note-spec.md §4.6.6: 時刻を1つだけ持つ垂直な壁。layerF方向にスイープする
                    // （時間でスイープする PushSlideBand とは別の生成関数）。
                    var wp = n.points[0];
                    var cEdge = wp.layerTo > wp.layerF ? cRiser : cDiver;
                    PushRiserWall(wp, dCopy, Push, NearOf, UAt, YAt, cEdge, cRiserArrow, timeline.XAt(wp.time));
                }
                else // Slide（旧Hold+旧Arcの統合）: Waypoint列を通した1本の帯
                {
                    // note-visual-r1.md §5.1/§4.2: 帯の色はGround(不透明寄り)/Sky(透明)を layerF で
                    // 連続的に補間する（NoteColors.SlideColor）。Riser/Diverと違い、Slideは層を
                    // 跨いで連続的に高さが変わり得るため離散切り替えにしない。
                    PushSlideBand(n, dCopy, Push, NearOf, UAt, YAt, timeline.XAt);

                    // note-spec.md §3: Visible中継点はTapと同じ形・別色で描く（コンボ点として扱われる、item11）。
                    // editor-ui-rework-r3.md §5: cellFは全種別で左端基準に統一（旧: Slideのみ中心基準）。
                    // note-visual-r1.md §7: マーカーは始点(帯)と同じ色相、alphaは層に依らず常に高く保つ。
                    foreach (var wp in n.points)
                    {
                        if (wp.marker != WaypointMarker.Visible) continue;
                        float y = YAt(wp.layerF, dCopy.skyHeight) + dCopy.zJudge * 0.012f; // 帯(0.01)より上にして隠れないようにする
                        float u0 = UAt(wp.cellF + 0.04f);
                        float u1 = UAt(wp.cellF + wp.width - 0.04f);
                        QuadThin(u0, u1, y, timeline.XAt(wp.time), wp.layerF, NoteColors.SlideMarkerColor(wp.layerF), NearOf(wp.layerF));
                    }
                }

                // note-spec.md §5.5。グループはノーツ単位。生成した全頂点に同じインデックスを焼く。
                float gIdx = n.scrollGroup;
                while (groupArr.Count < st.Count) groupArr.Add(gIdx);

                runtimes.Add(new NoteRuntime
                {
                    note = n,
                    state = NoteState.Pending,
                    vStart = vStart,
                    vCount = st.Count - vStart,
                    alpha = 1f,
                });
            }

            // ビートライン（地上のみ）。note-spec.md §5.5: グループ0のX(t)に乗せる（複数グループには対応しない簡略化）。
            // editor-ui-rework-r4.md §3: barTimesが渡されればそちら(=song.meters＋chart.bpmEventsから
            // 求めた実際の小節頭の時刻)を使う。渡されなければ従来どおりcfg.bpmから4拍間隔で引く
            // （GameControllerのデモ譜面は単一BPM・4/4なので回帰なし）。
            var beatTimeline = TimelineFor(0);
            float last = notes.Count > 0 ? ChartMath.NoteEnd(notes[notes.Count - 1]) : 0f;
            var beatPos = new List<Vector3>();
            var beatNear = new List<float>();
            var beatLayer = new List<float>();

            void PushBeatLine(float time)
            {
                float x = beatTimeline.XAt(time);
                beatPos.Add(new Vector3(-1f, dCopy.zJudge * 0.0005f, x));
                beatPos.Add(new Vector3(1f, dCopy.zJudge * 0.0005f, x));
                beatNear.Add(dCopy.groundNear);
                beatNear.Add(dCopy.groundNear);
                beatLayer.Add(0f);
                beatLayer.Add(0f);
            }

            if (barTimes != null)
            {
                foreach (var t in barTimes) PushBeatLine(t);
            }
            else
            {
                float b = 60f / cfg.bpm;
                for (float t = 0; t < last + 4f; t += b * 4f) PushBeatLine(t);
            }

            return new NoteMeshData
            {
                positions = pos.ToArray(),
                colors = col.ToArray(),
                state = st.ToArray(),
                near = nearArr.ToArray(),
                layerF = layerArr.ToArray(),
                side = sideArr.ToArray(),
                group = groupArr.ToArray(),
                localUv = uv3Arr.ToArray(),
                runtimes = runtimes,
                beatPositions = beatPos.ToArray(),
                beatNear = beatNear.ToArray(),
                beatLayerF = beatLayer.ToArray(),
            };
        }

        /// <summary>
        /// Slide は層をまたぐため、頂点ごとに自分の layerF に応じた手前端を持たせる。
        /// 幅もセル分数（cellF～cellF+width、editor-ui-rework-r3.md §5: 左端基準）をuに変換して
        /// 持たせ、ワールド単位に焼き込まない。
        /// points.Length==2 の直線区間（旧Holdに相当）も同じコードパスで描ける。
        /// xOf は note-spec.md §5.5 の X(t)（このSlideが属するスクロールグループの表示位置関数）。
        /// 形状(cellF/layerF/width)の補間は実時間 time のまま行い、頂点に焼く「時刻」座標だけを xOf(time) に変える。
        /// </summary>
        private static void PushSlideBand(
            Note slide, Derived d,
            PushFn push, Func<float, float> nearOf,
            Func<float, float> uAt, Func<float, float, float> yAt, Func<float, float> xOf)
        {
            float t0 = ChartMath.NoteStart(slide);
            float t1 = ChartMath.NoteEnd(slide);
            int steps = Math.Max(8, (int)MathF.Ceiling((t1 - t0) / 0.03f));

            (float cellF, float y, float t, float layerF, float width) At(float time)
            {
                var (layerF, cellF, width) = ChartMath.At(slide, time);
                return (cellF, yAt(layerF, d.skyHeight) + d.zJudge * 0.01f, xOf(time), layerF, width);
            }

            // side<0=左端(cellF) / side>0=右端(cellF+width)。editor-ui-rework-r3.md §5: 左端基準に統一。
            // note-visual-r1.md §5.1: 色は頂点ごとの layerF から連続的に補間する（層を跨ぐSlideに対応）。
            // note-visual-r1.md §5.2: localU=0(左端)/1(右端)。帯を時間方向に分割しても、この値は
            // 常に真の左右端に対応するので継ぎ目が出ない（yは未使用、0固定）。
            void Emit((float cellF, float y, float t, float layerF, float width) p, float side) =>
                push(uAt(side < 0f ? p.cellF : p.cellF + p.width), p.y, p.t, p.layerF,
                    NoteColors.SlideColor(p.layerF), nearOf(p.layerF), side < 0f ? 0f : 1f, 0f);

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

        /// <summary>
        /// note-spec.md §4.6.6（rev.7）。Riser（層跨ぎ）の見た目。全頂点が同じ時刻
        /// centerTime を持つ（＝ワールド空間で判定線に平行な垂直面）点が PushSlideBand との違い。
        /// layerF ∈ [wp.layerF, wp.layerTo] を分割してスイープする。直線移動なので分割数は固定8〜16で足りる。
        /// u（左右端）は各分割点で同じ cellF/width から求めるが、シェーダ側で頂点ごとの layerF 属性
        /// （uv1.x）に応じて LaneX の収束補正が変わるため、ワールド空間では厳密な垂直線にならない
        /// （unity-stage-port-design.md の「最遠端でワールド x は層ごとに違う」と同じ理由。正しい挙動）。
        ///
        /// note-visual-r1.md §6: 旧実装は12分割×2三角の大面積な半透明の壁だった
        /// （`ZTest Always`で早期棄却も効かずオーバードローが重い上、視認性の面でも「矢印だけの方がいい」
        /// というユーザー判断）。**壁の塗りは撤去し、左右端の細い縁線2本（レーン幅の手掛かり）＋
        /// 進行方向に並べた矢印3つ（層のつながりと方向感の手掛かり）に置き換える。**
        /// 縁線・矢印とも同じ(u, layerF)空間の座標で指定するだけでよい（頂点シェーダのPlaceNoteが
        /// 層ごとのレーン収束補正を含めて正しく変形してくれるため）。
        /// </summary>
        private static void PushRiserWall(
            Waypoint wp, Derived d,
            PushFn push, Func<float, float> nearOf,
            Func<float, float> uAt, Func<float, float, float> yAt, Color edgeColor, Color arrowColor, float centerTime)
        {
            const int steps = 12;
            float u0 = uAt(wp.cellF);
            float u1 = uAt(wp.cellF + wp.width);

            (float y, float near) At(float layerF) =>
                (yAt(layerF, d.skyHeight) + d.zJudge * 0.01f, nearOf(layerF));

            // note-visual-r1.md §6.2: レーン幅の手掛かりとして左右端に細い縁線を残す。
            // ワールド固定の小さい半幅で近似する（本格的なスクリーン空間一定幅のSDF化は
            // §4のQuadThin/帯と違い、この程度の装飾要素まではやり過ぎと判断し見送った）。
            const float edgeHalfWidth = 0.006f;
            void EmitEdge(float uCenter, float layerA, float layerB)
            {
                var a = At(layerA);
                var b = At(layerB);
                float ul = uCenter - edgeHalfWidth, ur = uCenter + edgeHalfWidth;
                push(ul, a.y, centerTime, layerA, edgeColor, a.near, 0f, -1f);
                push(ur, a.y, centerTime, layerA, edgeColor, a.near, 1f, -1f);
                push(ur, b.y, centerTime, layerB, edgeColor, b.near, 1f, -1f);
                push(ul, a.y, centerTime, layerA, edgeColor, a.near, 0f, -1f);
                push(ur, b.y, centerTime, layerB, edgeColor, b.near, 1f, -1f);
                push(ul, b.y, centerTime, layerB, edgeColor, b.near, 0f, -1f);
            }

            for (int i = 0; i < steps; i++)
            {
                float lA = wp.layerF + (wp.layerTo - wp.layerF) * i / steps;
                float lB = wp.layerF + (wp.layerTo - wp.layerF) * (i + 1) / steps;
                EmitEdge(u0, lA, lB);
                EmitEdge(u1, lA, lB);
            }

            // ---- 矢印（進行方向に3つ並べ、単発の大矢印より「動き」を読めるようにする） ----
            float uc = (u0 + u1) * 0.5f;
            float halfW = (u1 - u0) * 0.30f;
            float dir = Mathf.Sign(wp.layerTo - wp.layerF);
            float span = Mathf.Abs(wp.layerTo - wp.layerF);
            float armH = span * 0.16f;
            float thick = span * 0.08f;

            // 左腕・右腕とも4隅がu/layerFとも異なる四角形なので、EmitEdgeの矩形前提には乗らない。
            // 頂点1つずつ座標を指定する汎用の四角形をここだけ別に組む。
            void EmitFreeQuad((float u, float l) p0, (float u, float l) p1, (float u, float l) p2, (float u, float l) p3)
            {
                var a = At(p0.l); var b = At(p1.l); var c2 = At(p2.l); var e = At(p3.l);
                push(p0.u, a.y, centerTime, p0.l, arrowColor, a.near, 0f, -1f);
                push(p1.u, b.y, centerTime, p1.l, arrowColor, b.near, 0f, -1f);
                push(p2.u, c2.y, centerTime, p2.l, arrowColor, c2.near, 0f, -1f);
                push(p0.u, a.y, centerTime, p0.l, arrowColor, a.near, 0f, -1f);
                push(p2.u, c2.y, centerTime, p2.l, arrowColor, c2.near, 0f, -1f);
                push(p3.u, e.y, centerTime, p3.l, arrowColor, e.near, 0f, -1f);
            }

            float[] fracs = { 0.22f, 0.5f, 0.78f };
            foreach (var frac in fracs)
            {
                float lMid = wp.layerF + (wp.layerTo - wp.layerF) * frac;
                float lTip = lMid + dir * armH * 0.5f;
                float lBase = lMid - dir * armH * 0.5f;
                EmitFreeQuad((uc - halfW, lBase), (uc - halfW, lBase + dir * thick), (uc, lTip + dir * thick), (uc, lTip));
                EmitFreeQuad((uc + halfW, lBase), (uc + halfW, lBase + dir * thick), (uc, lTip + dir * thick), (uc, lTip));
            }
        }
    }
}
