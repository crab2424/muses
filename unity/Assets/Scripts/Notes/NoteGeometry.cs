using System;
using System.Collections.Generic;
using Muses.Chart;
using Muses.Stage;
using UnityEngine;

namespace Muses.Notes
{
    /// <summary>r3（design-lab/note-skin）。ノーツの見た目一式。プレイヤーが設定で選ぶ（既定=ネオン）。
    /// Note.shader のキーワード（_SKIN_KEYCAP）と、Tap 系・∧ のメッシュの作り方が変わる。</summary>
    public enum NoteSkin
    {
        Neon = 0,
        Keycap = 1,
    }

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
        /// <summary>note-visual-r1.md §1.5/§8-3。フラグメントシェーダのSDF描画用（TEXCOORD3）。
        /// x = 横方向のローカル座標(0=左端/1=右端)、**y = 種別タグ**。
        ///
        /// y はプリミティブ内の全頂点で同じ値にすること（＝補間しても値が変わらないこと）が必須:
        /// - y=1  タップ系の薄い板（Tap/ExTap）。厚み方向の座標は side（-1..+1）から導く。カプセル形`( )`+輪郭線のSDF。
        /// - y=2  Flick。端を尖らせた `< >`（gameplay-feel-r1.md §5.1）。
        /// - y=3  Slide の Visible 中継点マーカー。角丸矩形。帯と一緒に判定線で食べる（§5.4）。
        /// - y=0  Slide帯。x のみ意味を持つ。輪郭線・中央線は x 方向のみに引く
        ///        （帯を時間方向に分割しても継ぎ目が出ないように）。
        /// - y=-1 Riser/Diver の壁。
        /// - y=4  Riser/Diver の ∧（∨）の腕（r3、スキン「ネオン」）。**x には腕の断面 -1（下辺）/+1（上辺）を入れる**
        ///        （side は頂点シェーダが厚みに使うので流用しない）。
        /// 形・色の描き分けは Note.shader（スキン「ネオン」）の frag を参照。
        ///
        /// 2026-08-07: 旧実装はタグを (side, localUv.y) の組で表現していたが、**side は頂点シェーダが
        /// 厚みを付けるための座標で面の内部では -1→+1 に補間される**ため、`abs(side)>0.5` は
        /// 「タップ系」ではなく「タップ系の厚みの外側半分」を意味してしまっていた。タップの中央50%が
        /// Slide帯の分岐へ落ち、中央に縦線・境目に横線が出て内部の色も別処理になっていた。
        /// タグは補間で不変な値でなければならない。</summary>
        public Vector2[] localUv;
        /// <summary>r3（design-lab/note-skin）。種別ごとの追加データ（TEXCOORD4）。
        /// - Tap/ExTap/Flick: (uL, uR, ExTapなら1, キーキャップの立体の縁距離 c)。中心・半長を頂点シェーダで求めるため、全頂点に両端の u を持たせる。
        ///   c は白リング用（天面だけ 0→1、それ以外 -1。ネオンでは 0）。
        /// - Riser/Diver の壁: (k=根元0→到達点1, span=層の移動量, 0, 0)。
        /// - ∧ の腕: (offset=先端からの層のずれ, span, dirSign=Riser+1/Diver-1, ノーツの実時刻)。
        ///   実時刻は ∧ の位相用（スクロールグループの X(t) を通さないので、停止・逆走中も一定の速さで流れる）。
        /// - その他: 0。</summary>
        public Vector4[] extra;
        /// <summary>r3。三角形の頂点インデックス。キーキャップの Tap の立体だけ頂点を共有し、それ以外は頂点を3つずつ順に使う。
        /// NoteView がサブメッシュ（地上帯/空中帯/その他）へ振り分ける。</summary>
        public int[] indices;
        /// <summary>r3。キーキャップの Tap の立体の法線（ラボのローカル座標: x=横, y=上, z=手前が+）。それ以外は 0。</summary>
        public Vector3[] normals;
        /// <summary>r3（TEXCOORD5）。キーキャップの Tap の立体の頂点 (A, B, zN, yN)。ラボのローカル座標で
        /// x = A*半長 + B*半奥行、z = zN*半奥行、y = yN*半奥行（半長・半奥行は頂点シェーダで求める）。それ以外は 0。</summary>
        public Vector4[] solid;
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
        private delegate void PushFn(float u, float y, float time, float layerF, Color c, float nearD, float localU, float localV, Vector4 extra);

        public static NoteMeshData Build(StageConfig cfg, in Derived d, List<Note> notes,
            Dictionary<int, Chart.ScrollTimeline> scrollTimelines = null, List<float> barTimes = null,
            NoteSkin skin = NoteSkin.Neon)
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
            var extraArr = new List<Vector4>();
            var idxArr = new List<int>();
            var normArr = new List<Vector3>();
            var solidArr = new List<Vector4>();
            var runtimes = new List<NoteRuntime>();

            // 2026-08-07: プロジェクトは Linear カラースペース(m_ActiveColorSpace:1)。
            // NoteColors の値は sRGB の16進(#4aa3ff 等)をそのまま Color にしたものだが、
            // **メッシュの頂点色は Unity が変換しない**ため、シェーダはこれをリニア値として
            // 受け取り、最終のリニア→sRGB変換で明るく出てしまう
            // （実測: 4aa3ff→93d1ff / ffd54a→ffec92 / ff4a4a→ff9393。これは正確に
            //   LinearToSRGB(値) の関係になっていた）。頂点色へ載せる直前でリニアへ変換する。
            // NoteColors 自体は変換しない: エディタの2Dピアノロール(UI Toolkit)は sRGB 空間で
            // 描くため、そちらは元の値のままが正しい（実際タイムラインの色は正しく見えていた）。
            Color ToVertexColor(Color c) =>
                QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c;

            void Push(float u, float y, float time, float layerF, Color c, float nearD, float localU, float localV, Vector4 extra)
            {
                pos.Add(new Vector3(u, y, time));
                col.Add(ToVertexColor(c));
                st.Add(1f);
                nearArr.Add(nearD);
                layerArr.Add(layerF);
                sideArr.Add(0f);
                uv3Arr.Add(new Vector2(localU, localV));
                extraArr.Add(extra);
                normArr.Add(Vector3.zero);
                solidArr.Add(Vector4.zero);
                idxArr.Add(pos.Count - 1); // 立体以外は頂点を3つずつ順に三角形にする
            }

            // タップ系ノーツ用: 奥行き方向に薄い板を、頂点シェーダ側で「現在の奥行きに
            // 比例した厚み」に広げてもらうための頂点を積む（全頂点を同じ中心時刻centerTimeで積み、
            // near/far側の判定を side (-1/+1) に持たせる）。
            // note-visual-r1.md §3-3/§8-3: ローカルUV(0..1, 0..1)も同時に積み、フラグメント側の
            // 角丸+輪郭線SDF（Note.shader）で使う。
            // r3: Tap 系は extra に (uL, uR, ExTap, 0) を入れる（NoteMeshData.extra 参照）。マーカーは 0。
            void QuadThin(float u0, float u1, float y, float centerTime, float layerF, Color c, float nearD, float shapeTag, Vector4 extra)
            {
                float[] uu = { u0, u1, u1, u0 };
                float[] su = { -1f, -1f, 1f, 1f };
                float[] lu = { 0f, 1f, 1f, 0f };
                int[] idx = { 0, 1, 2, 0, 2, 3 };
                var cv = ToVertexColor(c); // 上記コメント参照（頂点色はリニアで載せる）
                foreach (var i in idx)
                {
                    pos.Add(new Vector3(uu[i], y, centerTime));
                    col.Add(cv);
                    st.Add(1f);
                    nearArr.Add(nearD);
                    layerArr.Add(layerF);
                    sideArr.Add(su[i]);
                    // localUv.y は「種別タグ」なので4頂点とも同じ値にする（1/2/3、NoteMeshData.localUv 参照）。
                    // 厚み方向の座標は side から導く（side*0.5+0.5 は旧 localUv.y={0,0,1,1} と完全に同値）。
                    uv3Arr.Add(new Vector2(lu[i], shapeTag));
                    extraArr.Add(extra);
                    normArr.Add(Vector3.zero);
                    solidArr.Add(Vector4.zero);
                    idxArr.Add(pos.Count - 1);
                }
            }

            // r3: キーキャップの Tap 系の立体（KeycapSolid のひな形を1個ぶん積む）。頂点を共有してインデックスで三角形を張る。
            void PushKeycapSolid(float u0, float u1, float y, float centerTime, float layerF, Color c, float nearD,
                bool flick, bool exTap)
            {
                var t = KeycapSolid.Get(flick, layerF > 0.5f);
                int baseIdx = pos.Count;
                var cv = ToVertexColor(c);
                float uc = (u0 + u1) * 0.5f;
                for (int i = 0; i < t.solid.Length; i++)
                {
                    pos.Add(new Vector3(uc, y, centerTime));
                    col.Add(cv);
                    st.Add(1f);
                    nearArr.Add(nearD);
                    layerArr.Add(layerF);
                    sideArr.Add(0f);
                    uv3Arr.Add(new Vector2(0f, flick ? 2f : 1f));
                    extraArr.Add(new Vector4(u0, u1, exTap ? 1f : 0f, t.edge[i]));
                    normArr.Add(t.normals[i]);
                    solidArr.Add(t.solid[i]);
                }
                foreach (var k in t.indices) idxArr.Add(baseIdx + k);
            }

            // note-visual-r1.md §4: 色は NoteColors に一元化済み（旧: このファイル・エディタ・
            // StageColors の3箇所に別々のリテラルがありドリフトしていた）。
            var cTap = NoteColors.Tap; // note-visual-r1.md §4.2: Tapは「操作」の色、層で変えない
            var cEx = NoteColors.ExTap;
            var cFlick = NoteColors.Flick;
            var cRiser = NoteColors.Riser;
            var cDiver = NoteColors.Diver;

            // 2026-08-07: 重なり順を NoteDrawOrder（エディタのタイムラインと共有）に揃える。
            // 従来は notes リスト順（＝おおむね追加順）で積んでいたため、同じ譜面でも
            // タイムラインとプレビュー/ゲームで前後関係が食い違っていた（ユーザー報告）。
            // Note.shader は ZWrite Off / ZTest Always なので、メッシュ内の頂点順がそのまま
            // 前後関係になる（note-visual-r1.md §5.3）。
            //
            // ただし runtimes は Judge が「開始時刻順ソート済み」を前提に cursor を単調前進
            // させる（Judge.Prepare / Judge.Seek）ため、**入力の notes 順のまま**作る必要がある。
            // そこで頂点範囲だけ元のインデックスに退避しておき、runtimes は後段でまとめて作る。
            var vRange = new (int start, int count)[notes.Count];
            // ipad-test-findings-r1.md §④。Slide専用: comboTimesの添字ごとの頂点範囲（Slide以外はnull）。
            var comboRanges = new (int start, int count)[notes.Count][];
            // gameplay-feel-r1.md §5.4。Slide専用: comboTimesの添字ごとのVisible中継点マーカーの頂点範囲。
            var markerRanges = new (int start, int count)[notes.Count][];

            for (int pass = 0; pass < NoteDrawOrder.Count; pass++)
            for (int ni = 0; ni < notes.Count; ni++)
            {
                var n = notes[ni];
                if (NoteDrawOrder.Priority(n) != pass) continue;
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
                    if (skin == NoteSkin.Keycap)
                        PushKeycapSolid(u0, u1, y, timeline.XAt(wp.time), layerF, c, NearOf(layerF),
                            n.kind == NoteKind.Flick, n.kind == NoteKind.ExTap);
                    else
                        QuadThin(u0, u1, y, timeline.XAt(wp.time), layerF, c, NearOf(layerF),
                            n.kind == NoteKind.Flick ? 2f : 1f, // gameplay-feel-r1.md §5.1: Flickは `< >`、他は `( )`
                            new Vector4(u0, u1, n.kind == NoteKind.ExTap ? 1f : 0f, 0f));
                }
                else if (n.kind == NoteKind.Riser)
                {
                    // note-spec.md §4.6.6: 時刻を1つだけ持つ垂直な壁。layerF方向にスイープする
                    // （時間でスイープする PushSlideBand とは別の生成関数）。
                    var wp = n.points[0];
                    var cWall = wp.layerTo > wp.layerF ? cRiser : cDiver;
                    PushRiserWall(wp, dCopy, Push, NearOf, UAt, YAt, cWall, timeline.XAt(wp.time), ChevronDims(skin));
                }
                else // Slide（旧Hold+旧Arcの統合）: Waypoint列を通した1本の帯
                {
                    // note-visual-r1.md §5.1/§4.2: 帯の色はGround(不透明寄り)/Sky(透明)を layerF で
                    // 連続的に補間する（NoteColors.SlideColor）。Riser/Diverと違い、Slideは層を
                    // 跨いで連続的に高さが変わり得るため離散切り替えにしない。
                    comboRanges[ni] = PushSlideBand(n, dCopy, Push, NearOf, UAt, YAt, timeline.XAt, () => st.Count);

                    // note-spec.md §3: Visible中継点はTapと同じ形・別色で描く（コンボ点として扱われる、item11）。
                    // editor-ui-rework-r3.md §5: cellFは全種別で左端基準に統一（旧: Slideのみ中心基準）。
                    // note-visual-r1.md §7: マーカーは始点(帯)と同じ色相、alphaは層に依らず常に高く保つ。
                    // gameplay-feel-r1.md §5.4: マーカーは、その時刻をコンボ点とする区間（comboTimes[i]==wp.time の i、
                    // 始点は区間0）に属させ、区間と一緒に判定線で食べる。
                    var mr = new (int start, int count)[n.comboTimes.Count];
                    foreach (var wp in n.points)
                    {
                        if (wp.marker != WaypointMarker.Visible) continue;
                        int mStart = st.Count;
                        float y = YAt(wp.layerF, dCopy.skyHeight) + dCopy.zJudge * 0.012f; // 帯(0.01)より上にして隠れないようにする
                        float u0 = UAt(wp.cellF + 0.04f);
                        float u1 = UAt(wp.cellF + wp.width - 0.04f);
                        QuadThin(u0, u1, y, timeline.XAt(wp.time), wp.layerF, NoteColors.SlideMarkerColor(wp.layerF), NearOf(wp.layerF), 3f, Vector4.zero);

                        int seg = 0;
                        while (seg < n.comboTimes.Count - 1 && n.comboTimes[seg] < wp.time - 1e-4f) seg++;
                        if (seg >= mr.Length) continue;
                        // 始点マーカーと comboTimes[0] のマーカーは同じ区間0に入る。マーカーは連続して積むので
                        // 同じ区間の2個目は範囲を後ろへ伸ばすだけでよい。
                        mr[seg] = mr[seg].count == 0 ? (mStart, st.Count - mStart) : (mr[seg].start, st.Count - mr[seg].start);
                    }
                    markerRanges[ni] = mr;
                }

                // note-spec.md §5.5。グループはノーツ単位。生成した全頂点に同じインデックスを焼く。
                float gIdx = n.scrollGroup;
                while (groupArr.Count < st.Count) groupArr.Add(gIdx);

                vRange[ni] = (vStart, st.Count - vStart);
            }

            // Judge が前提とする「開始時刻順」を保つため、入力の notes 順で作る（上記コメント参照）。
            for (int ni = 0; ni < notes.Count; ni++)
            {
                runtimes.Add(new NoteRuntime
                {
                    note = notes[ni],
                    state = NoteState.Pending,
                    vStart = vRange[ni].start,
                    vCount = vRange[ni].count,
                    alpha = 1f,
                    comboSegmentVertexRanges = comboRanges[ni] ?? System.Array.Empty<(int, int)>(),
                    comboMarkerVertexRanges = markerRanges[ni] ?? System.Array.Empty<(int, int)>(),
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
                extra = extraArr.ToArray(),
                indices = idxArr.ToArray(),
                normals = normArr.ToArray(),
                solid = solidArr.ToArray(),
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
        ///
        /// ipad-test-findings-r1.md §④。区間の頂点範囲を comboTimes の境界に揃えて生成し、戻り値として
        /// 返す（Judge が各コンボ点の判定結果をこの範囲へ書き込み、判定線で「食べる/そのまま通り過ぎる」を
        /// 区間ごとに分岐させるため）。comboTimes は必ず末尾が t1 と一致する
        /// （ChartFormat.ResolveSlideComboPoints）。vertexCount は現在の総頂点数を返すコールバック
        /// （呼び出し側の頂点リストと連動させるためクロージャで渡す）。
        /// </summary>
        private static (int start, int count)[] PushSlideBand(
            Note slide, Derived d,
            PushFn push, Func<float, float> nearOf,
            Func<float, float> uAt, Func<float, float, float> yAt, Func<float, float> xOf,
            Func<int> vertexCount)
        {
            float t0 = ChartMath.NoteStart(slide);

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
                    NoteColors.SlideColor(p.layerF), nearOf(p.layerF), side < 0f ? 0f : 1f, 0f, Vector4.zero);

            var comboTimes = slide.comboTimes;
            var ranges = new (int start, int count)[comboTimes.Count];

            float segStart = t0;
            var prev = At(t0);
            int rangeStart = vertexCount();

            for (int seg = 0; seg < comboTimes.Count; seg++)
            {
                float segEnd = comboTimes[seg];
                // 元は帯全体で min 8 だったが、区間ごとに分けたのでここは区間の長さに応じた
                // 最小値にする（短い区間でも easing の曲がりが見える程度は残す）。
                int steps = Math.Max(2, (int)MathF.Ceiling((segEnd - segStart) / 0.03f));
                for (int i = 1; i <= steps; i++)
                {
                    var cur = At(segStart + (segEnd - segStart) * i / steps);
                    Emit(prev, -1f);
                    Emit(prev, 1f);
                    Emit(cur, 1f);
                    Emit(prev, -1f);
                    Emit(cur, 1f);
                    Emit(cur, -1f);
                    prev = cur;
                }
                int rangeEnd = vertexCount();
                ranges[seg] = (rangeStart, rangeEnd - rangeStart);
                rangeStart = rangeEnd;
                segStart = segEnd;
            }

            return ranges;
        }

        // ∧ の寸法（design-lab/note-skin/skins/shared/chevron.js の CHEVRON_DEFAULTS）。
        // 厚み th・傾き sl（層）はスキンごと。Include/NoteSkinNeon.hlsl / NoteSkinKeycap.hlsl の SKIN_CHEV_TH / SKIN_CHEV_SL と
        // 一致させること（周期の計算に使う）。sl は中央→端での下がり量。
        private static (float th, float sl) ChevronDims(NoteSkin skin) =>
            skin == NoteSkin.Keycap ? (0.28f, 0.26f) : (0.32f, 0.30f);

        /// <summary>
        /// note-spec.md §4.6.6（rev.7）。Riser/Diver の見た目（r3、スキン「ネオン」。移植元 design-lab/note-skin/skins/neon.js）。
        /// 全頂点が同じ時刻 centerTime を持つ（＝判定線に平行な垂直面）。
        ///
        /// 1. 壁: layerF ∈ [wp.layerF, wp.layerTo] を 12 分割した発光面（タグ -1、extra = (k, span)）。
        ///    左右の縁・到達点の白線・ハローはフラグメントで描く。
        /// 2. ∧（Diver は ∨）: 腕ごとのクアッド（タグ 4）。層位置は頂点シェーダが実時刻から決める
        ///    （Note.shader の ChevronPlace。腕の両端だけを置くので画面上で直線になる）。
        ///    ここでは基準層 = wp.layerF で積み、先端からの層のずれ offset を extra.x に焼く。
        ///    1周期に1枚だけ見え、判定時刻に先端が到達点へ届く。幅に依らず全幅で1つ（r3 §13）。
        /// </summary>
        private static void PushRiserWall(
            Waypoint wp, Derived d,
            PushFn push, Func<float, float> nearOf,
            Func<float, float> uAt, Func<float, float, float> yAt, Color wallColor, float centerTime,
            (float th, float sl) chev)
        {
            const int steps = 12;
            float u0 = uAt(wp.cellF);
            float u1 = uAt(wp.cellF + wp.width);
            float span = Mathf.Abs(wp.layerTo - wp.layerF);
            float yUp = d.zJudge * 0.01f;

            float LayerAt(float k) => wp.layerF + (wp.layerTo - wp.layerF) * k;
            void EmitWall(float u, float k, float localU)
            {
                float l = LayerAt(k);
                push(u, yAt(l, d.skyHeight) + yUp, centerTime, l, wallColor, nearOf(l), localU, -1f, new Vector4(k, span, 0f, 0f));
            }

            for (int i = 0; i < steps; i++)
            {
                float ka = (float)i / steps, kb = (float)(i + 1) / steps;
                EmitWall(u0, ka, 0f); EmitWall(u1, ka, 1f); EmitWall(u1, kb, 1f);
                EmitWall(u0, ka, 0f); EmitWall(u1, kb, 1f); EmitWall(u0, kb, 0f);
            }

            // ---- ∧ の腕（shared/chevron.js の buildChevrons）----
            float dirSign = wp.layerTo < wp.layerF ? -1f : 1f;
            // 幅に依らず ∧ は1つ（ノーツ全幅の大きい1本）。横に並べると大きいノーツと隣接した小さいノーツの集まりの区別がつかない（r3 §13）
            const int n = 1;
            float h = chev.th * 0.5f;
            float baseY = yAt(wp.layerF, d.skyHeight) + yUp;
            float baseNear = nearOf(wp.layerF);

            // 1頂点: u, 先端からの層のずれ, 腕の断面(-1/+1)
            void EmitChev(float u, float offset, float armSide) =>
                push(u, baseY, centerTime, wp.layerF, wallColor, baseNear, armSide, 4f,
                    new Vector4(offset, span, dirSign, wp.time));

            // fa/fb = 幅に対する位置(0..1)、xla/xlb = 先端からの横位置(0=先端/1=端)
            void Arm(float fa, float xla, float fb, float xlb)
            {
                float ua = uAt(wp.cellF + fa * wp.width), ub = uAt(wp.cellF + fb * wp.width);
                float oa = -chev.sl * xla, ob = -chev.sl * xlb;
                EmitChev(ua, oa - h, -1f); EmitChev(ub, ob - h, -1f); EmitChev(ub, ob + h, 1f);
                EmitChev(ua, oa - h, -1f); EmitChev(ub, ob + h, 1f); EmitChev(ua, oa + h, 1f);
            }

            for (int i = 0; i < n; i++)
            {
                float f0 = (float)i / n, f1 = (float)(i + 1) / n, fc = (f0 + f1) * 0.5f;
                Arm(f0, 1f, fc, 0f); // 左の腕（端 → 先端）
                Arm(fc, 0f, f1, 1f); // 右の腕（先端 → 端）
            }
        }
    }
}
