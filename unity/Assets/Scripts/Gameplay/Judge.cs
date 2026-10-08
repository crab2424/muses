using System;
using System.Collections.Generic;
using Muses.Chart;
using Muses.Notes;
using Muses.Stage;
using Muses.TouchInput;

namespace Muses.Gameplay
{
    /// <summary>
    /// 判定。移植元: web-prototype/src/judge.ts。
    ///
    /// note-spec.md rev.4 のデータモデルに合わせて再構成した後、§8 item4/9/10/13/15 を実装:
    /// Tap/ExTap/Flick はトレイト駆動（judgeProfile=AllPerfectならExTapは窓内すべてPERFECT+）、
    /// 判定ティアは4段の配列データ(<see cref="JudgeTiers"/>)を参照、縦連判定（中点分割）を
    /// ロード時にprecompute、同時刻ノーツはまとめて解決する（§6.4）。
    ///
    /// 続けて item5/7/16 を実装: 連続座標(cellF/layerF)による Slide/Flick の包含判定（§0.2）、
    /// Slide 始点を Tap と同じ Contact 駆動・連続座標包含に統一（§0.2/§2.1）、
    /// Slide のコンボ点を時間対称窓で独立判定し HOLD BREAK を廃止（§2.4）。
    ///
    /// 続けて item8 を実装: Flick を「枠内の移動量」で判定する本実装に置き換えた
    /// （Presence駆動・早い側繰り上げの非対称窓・移動なしのフォールバック、§4）。
    /// item6（入力のバンド分割）は TouchInputManager 側、item11（Visible中継点描画）は
    /// NoteGeometry 側で対応済み。item14（ソフラン）は ScrollTimeline/NoteView 側で対応済み
    /// （判定は songTime をそのまま使い続けるため無変更）。
    ///
    /// implementation-roadmap.md 項目D/H対応: MonoBehaviour（NoteView/TouchInputManager）に
    /// 直接依存せず、ノーツ列(List&lt;NoteRuntime&gt;)・接触点列(IEnumerable&lt;Contact&gt;)・
    /// アルファ設定コールバックだけを外から注入する形にしてある。時刻と入力を注入すれば
    /// 動く純粋なC#クラスなので、Unityのシーン無しでユニットテストできる（項目H）。
    /// シーク(<see cref="Seek"/>)にも対応済み: 任意のsongTimeへ全ノーツの状態を組み直せる。
    ///
    /// gameplay-feel-r1.md（2026-09-27）: Slide 始点を枠内更新駆動から他のコンボ点と同じ占有駆動へ変更（§2）、
    /// Slide 帯の包含判定に静的余白と時間ずれ許容を追加（§1.2）、連続座標の包含判定で接触の layerF を
    /// [0,1] にクランプ（§1.3、空中パネル上部が判定外だった穴の修正）、コンボ点の早期確定（§2.5）。
    ///
    /// gameplay-feel-r2.md（2026-09-28）: 重なり帯の接触は空中 Tap の候補にもなる（1タッチ1ノーツ、§3）、
    /// Riser/Diver を縦連なし・窓内最良・遅い側延長・救済GOOD廃止に作り直し（§4）、
    /// 判定ごとに種別×層の内訳と EARLY/LATE を Score へ記録（§2・§6）、演出に判定点の layerF を渡す（§5）。
    ///
    /// note-feel-r3（2026-10-08）: Slide 始点を Ex Tap と同じ枠内更新駆動・窓内全 PERFECT+ に戻した（押しっぱなしでは拾わない）、
    /// Riser と重なる Tap は両層で取れる（縦連も両層が相手）、Riser の PERFECT+ 窓を ±riserWindowExtendMs 広げた。
    /// </summary>
    public class Judge
    {
        public Score Score { get; private set; } = new();
        public List<HitFlash> Flashes { get; } = new();

        private StageConfig cfg;
        private readonly Action<NoteRuntime, float> setAlpha;
        /// <summary>song-play-flow-r1.md §7。判定成立(MISS以外)のたび呼ぶヒットSE用コールバック。
        /// Seek()中の過去ノーツの読み飛ばしでは呼ばれない(CommitJudgementはUpdate()経由の
        /// 実際の判定成立時にしか呼ばれないため)。</summary>
        private readonly Action<JudgeKind> onJudged;
        /// <summary>ipad-test-findings-r1.md §④。Slideの1コンボ区間の見た目を切り替える
        /// （通常は NoteView.SetSlideSegmentEatable）。true=判定線で食べる/false=そのまま通り過ぎる(既定)。
        /// 押さえられている間 UpdateSlide が毎フレーム呼ぶので、実装側は値が変わらないときに
        /// 何もしないこと。省略可（nullなら常に「通り過ぎる」＝この機能の導入前と同じ見た目）。</summary>
        private readonly Action<NoteRuntime, int, bool> setSegmentEatable;
        private List<NoteRuntime> runtimes = new();
        private int cursor;

        /// <summary>note-spec.md §6.2。ノーツごとの実効窓（縦連判定で中点分割した後の [lo, hi] 秒）。
        /// 対象集合 T = Tap/ExTap/Flick/Riser（§6.2、gameplay-feel-r1.md §2.3 で Slide始点を除外）。</summary>
        private readonly Dictionary<NoteRuntime, (float lo, float hi)> chainWindows = new();

        /// <param name="setAlpha">ノーツの表示アルファを設定するコールバック（通常は NoteView.SetNoteAlpha）</param>
        /// <param name="onJudged">判定成立(MISS以外)のたび呼ぶヒットSEコールバック（省略可）</param>
        /// <param name="setSegmentEatable">Slideの区間ごとの食べる/通り過ぎる分岐コールバック（省略可）</param>
        public Judge(StageConfig cfg, Action<NoteRuntime, float> setAlpha, Action<JudgeKind> onJudged = null,
            Action<NoteRuntime, int, bool> setSegmentEatable = null)
        {
            this.cfg = cfg;
            this.setAlpha = setAlpha;
            this.onJudged = onJudged;
            this.setSegmentEatable = setSegmentEatable;
        }

        public void SetConfig(StageConfig cfg) => this.cfg = cfg;

        /// <summary>譜面の頭からやり直す。Seek(0) と同じ。</summary>
        public void Reset() => Seek(0f);

        /// <summary>
        /// implementation-roadmap.md 項目D。任意の songTime へジャンプし、全ノーツの状態を
        /// 「実際にそこまでプレイした」結果ではなく「その時刻から素直に見た状態」として組み直す。
        /// エディタでのシーク・ゲーム側のリトライ/巻き戻しの両方から呼ばれる想定。
        ///
        /// 過去に完全に終わったノーツは判定を課さず（スコア/コンボへの加算はしない）Hit扱いで隠すだけ。
        /// Slide の途中に着地した場合は Active に戻し、songTime より前のコンボ点だけ読み飛ばす
        /// （読み飛ばした分もスコアには入れない。飛ばした地点から素直に続きを判定する）。
        /// </summary>
        public void Seek(float songTime)
        {
            Score = new Score();

            foreach (var rt in runtimes)
            {
                var n = rt.note;
                rt.slideSamples.Clear();
                rt.nextComboIndex = 0;
                rt.startResolved = false;
                rt.flickEnterSeen = false;
                rt.riserReactions.Clear();
                rt.riserReacting.Clear();

                // ipad-test-findings-r1.md §④: シークは各区間を「実際にプレイした」結果ではなく
                // 素直に見た状態へ組み直すため、食べる/通り過ぎるの判定履歴も一律リセットする
                // （以下の3分岐いずれでも、まだ判定していない区間は「通り過ぎる」が正しい既定）。
                if (n.kind == NoteKind.Slide && setSegmentEatable != null)
                    for (int i = 0; i < n.comboTimes.Count; i++) setSegmentEatable(rt, i, false);

                if (ChartMath.NoteEnd(n) < songTime)
                {
                    rt.state = NoteState.Hit; // 過去に飛ばした分。スコアには反映しない
                    setAlpha(rt, 0f);
                }
                else if (n.kind == NoteKind.Slide && ChartMath.NoteStart(n) < songTime)
                {
                    rt.state = NoteState.Active;
                    rt.startResolved = true; // 始点は通り過ぎた（読み飛ばし、スコアには入れない）
                    while (rt.nextComboIndex < n.comboTimes.Count && n.comboTimes[rt.nextComboIndex] < songTime)
                        rt.nextComboIndex++;
                    setAlpha(rt, 0.45f);
                }
                else
                {
                    rt.state = NoteState.Pending;
                    setAlpha(rt, 1f);
                }
            }

            cursor = 0;
            while (cursor < runtimes.Count &&
                   (runtimes[cursor].state == NoteState.Hit || runtimes[cursor].state == NoteState.Missed))
                cursor++;
        }

        /// <summary>
        /// note-spec.md §6.2 の縦連判定（中点分割）をロード時に1回だけprecomputeする。
        /// 以後の Judge はこの呼び出しで渡されたノーツ列を保持し続ける（NoteView.Build() の直後に呼ぶこと）。
        /// </summary>
        public void Prepare(List<NoteRuntime> runtimes)
        {
            this.runtimes = runtimes;

            chainWindows.Clear();
            float w = JudgeTiers.All[^1].halfWidthMs / 1000f; // GOODの半幅=100ms（対象集合T全ノーツ共通）

            // 対象集合 T = Tap / ExTap / Flick / Riser（§6.2、rev.7でRiser追加）。
            // gameplay-feel-r1.md §2.3: Slide始点は占有駆動になったので T から外した
            // （時間対称の占有判定には中点分割の窓が意味を持たない）。
            // runtimes は既に開始時刻順ソート済みなので、そのままの相対順序で prev/next の最近傍探索ができる。
            var group = new List<NoteRuntime>();
            foreach (var rt in runtimes)
                if (rt.note.kind == NoteKind.Tap || rt.note.kind == NoteKind.ExTap ||
                    rt.note.kind == NoteKind.Flick || rt.note.kind == NoteKind.Riser)
                    group.Add(rt);

            PrepareDualLayer(group);

            for (int i = 0; i < group.Count; i++)
            {
                var rt = group[i];
                var wp = rt.note.points[0];
                int mask = LayerMask(rt);
                float t = wp.time;

                float lo = float.NegativeInfinity;
                float hi = float.PositiveInfinity;

                for (int j = i - 1; j >= 0; j--)
                {
                    var pj = group[j].note.points[0];
                    if (pj.time >= t) continue; // 厳密不等号: 同時刻グループはprev/nextにならない(§6.4)
                    if ((LayerMask(group[j]) & mask) == 0 || !CellOverlap(pj, wp)) continue;
                    lo = (pj.time + t) / 2f;
                    break;
                }
                for (int j = i + 1; j < group.Count; j++)
                {
                    var nj = group[j].note.points[0];
                    if (nj.time <= t) continue;
                    if ((LayerMask(group[j]) & mask) == 0 || !CellOverlap(nj, wp)) continue;
                    hi = (t + nj.time) / 2f;
                    break;
                }

                var traits = NoteKindTraits.Of(rt.note.kind);
                // Ex Tap / Riser(gameplay-feel-r2.md §4.2): 自身は切られない（他ノーツの境界にはなる）。
                // Riser は窓も独自（UpdateRiserPending）なので、ここでの値は参照されない。
                if (traits.chainExempt) { lo = float.NegativeInfinity; hi = float.PositiveInfinity; }
                if (rt.note.kind == NoteKind.Flick)
                    lo = float.NegativeInfinity; // Flick: 早い側だけ免除(§6.2)

                chainWindows[rt] = (MathF.Max(lo, t - w), MathF.Min(hi, t + w));
            }

            PrepareExBoost(group);
        }

        /// <summary>縦連判定で使う層の集合（bit0=地上, bit1=空中）。dualLayer の Tap は両層。</summary>
        private static int LayerMask(NoteRuntime rt) =>
            rt.dualLayer ? 3 : rt.note.points[0].layerF > 0.5f ? 2 : 1;

        /// <summary>
        /// note-feel-r3（2026-10-08）。同時刻・同一層・セル範囲が交差する Riser/Diver の始点に重なる Tap/ExTap を
        /// dualLayer=true にする（地上・空中どちらのパネルの接触でも取れる）。ロード時に一度だけ計算する。
        /// </summary>
        private static void PrepareDualLayer(List<NoteRuntime> group)
        {
            foreach (var rt in group) rt.dualLayer = false;
            foreach (var rt in group)
            {
                if (rt.note.kind != NoteKind.Tap && rt.note.kind != NoteKind.ExTap) continue;
                var wp = rt.note.points[0];
                bool sky = wp.layerF > 0.5f;
                foreach (var r in group)
                {
                    if (r.note.kind != NoteKind.Riser) continue;
                    var rp = r.note.points[0];
                    if (MathF.Abs(rp.time - wp.time) >= 1e-4f) continue;
                    if ((rp.layerF > 0.5f) != sky || !CellOverlap(rp, wp)) continue;
                    rt.dualLayer = true;
                    break;
                }
            }
        }

        /// <summary>
        /// note-spec.md §6.4「Ex Tap 巻き込みルール」（rev.7）。同時刻・同一層でセル範囲が交差する
        /// Ex Tap を持つ Tap を exBoosted=true にする。実行時にその入力が実際に
        /// Ex Tap のセル範囲へ触れたかは問わない（譜面上で交差していれば常に、が仕様）。
        /// chainWindows と同様ロード時に一度だけ計算する（実行時コストはゼロ）。
        /// gameplay-feel-r1.md §2.3: Slide始点は枠内更新駆動でなくなったので対象から外した
        /// （Flick が対象外なのと同じ理由）。
        /// </summary>
        private void PrepareExBoost(List<NoteRuntime> group)
        {
            var exNotes = new List<NoteRuntime>();
            foreach (var rt in group)
                if (rt.note.kind == NoteKind.ExTap) exNotes.Add(rt);

            foreach (var rt in runtimes) rt.exBoosted = false;
            foreach (var rt in group)
            {
                if (rt.note.kind != NoteKind.Tap) continue;
                var wp = rt.note.points[0];
                float t = wp.time;

                foreach (var ex in exNotes)
                {
                    var ewp = ex.note.points[0];
                    if (MathF.Abs(ewp.time - t) >= 1e-4f) continue; // 同時刻のみ
                    var eLayer = ewp.layerF > 0.5f ? Layer.Sky : Layer.Ground;
                    if ((wp.layerF > 0.5f ? Layer.Sky : Layer.Ground) != eLayer) continue;
                    if (!CellOverlap(ewp, wp)) continue;
                    rt.exBoosted = true;
                    break;
                }
            }
        }

        // editor-ui-rework-r3.md §5: cellFは全種別で左端基準（旧: Slideのみ中心基準）。
        private static bool CellOverlap(Waypoint a, Waypoint b) => a.cellF < b.cellF + b.width && b.cellF < a.cellF + a.width;

        /// <summary>
        /// note-spec.md §0.2。Slideの帯に接触点が包含されているかを連続座標で判定する。
        /// gameplay-feel-r1.md §1.2: 帯の「時刻tの位置」ではなく、[t - slideTrailMs, t + slideLeadMs] に
        /// 帯が通過した範囲（掃引範囲）＋静的余白 slideMarginCells と比べる。静止した帯では範囲が
        /// 広がらず、横（縦）に動く帯ほど進行方向にだけ判定が伸びる（指の追従遅れを吸収する）。
        /// </summary>
        private bool AnyContactInBand(IEnumerable<Contact> contacts, Note n, float t)
        {
            float ta = t - cfg.slideTrailMs / 1000f, tb = t + cfg.slideLeadMs / 1000f;
            float left = float.PositiveInfinity, right = float.NegativeInfinity;
            float lo = float.PositiveInfinity, hi = float.NegativeInfinity;

            void Include(float time)
            {
                var (layerF, cellF, width) = ChartMath.At(n, time); // 範囲外の時刻は端点にクランプされる
                left = MathF.Min(left, cellF);
                right = MathF.Max(right, cellF + width);
                lo = MathF.Min(lo, layerF);
                hi = MathF.Max(hi, layerF);
            }

            Include(ta);
            Include(t);
            Include(tb);
            // 区間の中の折れ点（Waypoint）も含めれば、直線区間の掃引範囲は厳密になる。
            // easing区間は端と中点で近似（左右の極値は区間端に来るので実用上十分）。
            foreach (var p in n.points)
                if (p.time > ta && p.time < tb) Include(p.time);

            float m = cfg.slideMarginCells;
            foreach (var c in contacts)
            {
                float lf = JudgeLayerF(c, t);
                if (lf < lo - cfg.layerJudgeRadius || lf > hi + cfg.layerJudgeRadius) continue;
                if (c.cellF < left - m || c.cellF > right + m) continue;
                return true;
            }
            return false;
        }

        /// <summary>
        /// gameplay-feel-r1.md §1.3。連続座標の包含判定に使う接触の layerF。[0,1] にクランプし、
        /// 「各層の判定線より画面の外側」を判定線上と同じ扱いにする。クランプしないと空中ノーツ
        /// (layerF=1, radius 0.5)の許容が v≦0.6 で切れ、空中パネル上部（画面上端から約20%）が
        /// Slide/Flick/Riser だけ判定外になっていた（Tap は離散パネルなので取れていた）。
        /// </summary>
        private static float JudgeLayerF(Contact c, float songTime) =>
            Math.Clamp(EffectiveLayerF(c, songTime), 0f, 1f);

        /// <summary>
        /// note-spec.md §4.6.4（rev.7）。handoff が有効な間（songTime &lt;= layerHandoffUntil）は
        /// 実際の layerF ではなく layerHandoffTo を返す。Riser 成立後、指がまだ物理的に終端層へ
        /// 到達していなくても後続 Slide 始点などの包含判定を一定時間だけ通すための読み替え。
        /// </summary>
        private static float EffectiveLayerF(Contact c, float songTime) =>
            songTime <= c.layerHandoffUntil ? c.layerHandoffTo : c.layerF;

        /// <summary>Flick/Riser の包含判定（1点のノーツ用）。marginCells は gameplay-feel-r1.md §1.4 の左右余白。</summary>
        private bool InBand(Contact c, float layerF, float cellF, float width, float songTime, float marginCells) =>
            MathF.Abs(JudgeLayerF(c, songTime) - layerF) <= cfg.layerJudgeRadius &&
            c.cellF >= cellF - marginCells && c.cellF <= cellF + width + marginCells;

        /// <summary>
        /// EnterEvent がノーツ N の包含判定を満たすか。Tap/ExTap は離散セル(§0.2)。
        /// Slide（gameplay-feel-r1.md §2 で占有駆動に変更）と Flick/Riser（Presence 駆動）はここでは扱わない。
        /// </summary>
        private static bool Contains(Waypoint wp, Layer layer, int eCell)
        {
            var noteLayer = wp.layerF > 0.5f ? Layer.Sky : Layer.Ground;
            return noteLayer == layer && ContainsCell(wp, eCell);
        }

        private static bool ContainsCell(Waypoint wp, int eCell)
        {
            int cell = (int)MathF.Round(wp.cellF);
            int w = Math.Max(1, (int)MathF.Round(wp.width));
            return eCell >= cell && eCell < cell + w;
        }

        /// <summary>
        /// gameplay-feel-r2.md §3。EnterEvent がノーツの候補になるなら、そのノーツの層を返す。
        /// 重なり帯（e.skyReach）の接触は、本来の層（e.layer）に加えて空中ノーツの候補にもなる。
        /// </summary>
        private static Layer? CandidateLayer(NoteRuntime rt, EnterEvent e)
        {
            var wp = rt.note.points[0];
            // note-feel-r3: Riser と重なる Tap は層を問わない（Riser 成立時の合成イベントは除く）
            if (rt.dualLayer && !e.fromHandoff && ContainsCell(wp, e.cell)) return e.layer;
            if (Contains(wp, e.layer, e.cell)) return e.layer;
            if (e.skyReach && e.layer != Layer.Sky && Contains(wp, Layer.Sky, e.cell)) return Layer.Sky;
            return null;
        }

        /// <summary>「入力範囲内に新規の接触点が検出された」= ヒット判定のトリガ（Tap/ExTap）</summary>
        public void OnEnter(EnterEvent e, float songTime)
        {
            var rts = runtimes;
            NoteRuntime best = null;
            float bestDt = float.PositiveInfinity;
            Layer bestLayer = e.layer;
            float rawWin = JudgeTiers.All[^1].halfWidthMs / 1000f; // GOODの素の半幅。中点分割は窓を狭めるだけなのでこれを打ち切り境界に使える

            for (int i = cursor; i < rts.Count; i++)
            {
                var rt = rts[i];
                var n = rt.note;
                if (ChartMath.NoteStart(n) > songTime + rawWin) break; // これ以降は誰の実効窓にも入らない
                var wp = n.points[0];
                Layer? layer;
                if (n.kind == NoteKind.Slide)
                {
                    // note-feel-r3: Slide 始点は Ex Tap と同じ枠内更新駆動（包含は連続座標、窓は素の ±100ms）
                    if (!SlideStartPending(rt)) continue;
                    if (MathF.Abs(songTime - wp.time) > rawWin) continue;
                    if (!SlideStartContains(n, e)) continue;
                    layer = e.layer;
                }
                else
                {
                    if (!IsContactDriven(n.kind)) continue; // Flick/RiserはUpdate()側が扱う（§4/§4.6）
                    if (rt.state != NoteState.Pending) continue;
                    if (!chainWindows.TryGetValue(rt, out var win)) continue;
                    if (songTime < win.lo || songTime > win.hi) continue;
                    layer = CandidateLayer(rt, e);
                    if (layer == null) continue;
                }
                float dt = wp.time - songTime;
                // gameplay-feel-r2.md §3「1タッチ1ノーツ」: 両層の候補から |dt| 最小を1つ。
                // |dt| が同じなら、その接触が本来いる層（e.layer）を優先する。
                bool closer = MathF.Abs(dt) < MathF.Abs(bestDt) - 1e-6f;
                bool tieNative = MathF.Abs(MathF.Abs(dt) - MathF.Abs(bestDt)) <= 1e-6f &&
                                 layer.Value == e.layer && bestLayer != e.layer;
                if (closer || tieNative)
                {
                    best = rt;
                    bestDt = dt;
                    bestLayer = layer.Value;
                }
            }

            if (best == null) return;
            float bestTime = best.note.points[0].time;

            // note-spec.md §6.4: 同時刻グループのうち、入力位置が包含判定を満たすノーツを全て
            // 同じ入力イベント(同じ dt)でまとめて解決する。ティアはノーツごとに個別に決まる。
            // gameplay-feel-r2.md §3: まとめ解決は best の層の中だけ（重なり帯でも両層は同時に取らない）。
            for (int i = cursor; i < rts.Count; i++)
            {
                var rt = rts[i];
                var n = rt.note;
                if (ChartMath.NoteStart(n) > bestTime + 1e-4f) break; // 開始時刻順ソート済みなので同時刻グループを過ぎたら終了
                var wp = n.points[0];
                if (MathF.Abs(wp.time - bestTime) > 1e-4f) continue;
                if (n.kind == NoteKind.Slide)
                {
                    if (SlideStartPending(rt) && SlideStartContains(n, e)) ResolveSlideStart(rt, songTime);
                    continue;
                }
                if (!IsContactDriven(n.kind)) continue;
                if (rt.state != NoteState.Pending) continue;
                bool dual = rt.dualLayer && !e.fromHandoff;
                if (dual ? !ContainsCell(wp, e.cell) : !Contains(wp, bestLayer, e.cell)) continue;

                float dt = wp.time - songTime;
                ResolveHit(rt, wp, dt, songTime);
            }
        }

        /// <summary>枠内更新(EnterEvent)で駆動されるのは Tap / ExTap だけ（gameplay-feel-r1.md §2 で Slide 始点が外れた）。</summary>
        private static bool IsContactDriven(NoteKind kind) => kind == NoteKind.Tap || kind == NoteKind.ExTap;

        /// <summary>note-feel-r3。Slide 始点がまだ判定されていないか（Update が窓を開く前の Pending も含む）。</summary>
        private static bool SlideStartPending(NoteRuntime rt) =>
            !rt.startResolved && (rt.state == NoteState.Pending || rt.state == NoteState.Active);

        /// <summary>note-feel-r3。Slide 始点の包含判定（連続座標、note-spec §0.2）。層は layerF を [0,1] に
        /// クランプして layerJudgeRadius、横は始点のセル範囲＋slideMarginCells。</summary>
        private bool SlideStartContains(Note n, EnterEvent e)
        {
            var wp = n.points[0];
            if (MathF.Abs(Math.Clamp(e.layerF, 0f, 1f) - wp.layerF) > cfg.layerJudgeRadius) return false;
            float m = cfg.slideMarginCells;
            return e.cellF >= wp.cellF - m && e.cellF <= wp.cellF + wp.width + m;
        }

        /// <summary>note-feel-r3（2026-10-08、ユーザー判断）。Slide 始点は Ex Tap と同じ: 窓内の枠内更新なら常に PERFECT+。
        /// 押しっぱなしでは拾わない（gameplay-feel-r1.md §2 の占有駆動を撤回）。</summary>
        private void ResolveSlideStart(NoteRuntime rt, float songTime)
        {
            var n = rt.note;
            var wp = n.points[0];
            CommitJudgement(JudgeKind.PerfectPlus, n, wp.layerF, wp.cellF, wp.width, songTime, (songTime - wp.time) * 1000f);
            if (rt.state == NoteState.Pending)
            {
                rt.state = NoteState.Active;
                rt.nextComboIndex = 0;
            }
            rt.startResolved = true;
        }

        /// <summary>note-spec.md §6.1。トレイト（judgeProfile）駆動でティアを決める。理論上ここに来ない場合は null（呼び出し元がchainWindowで既に窓内を保証している）。
        /// exBoosted は §6.4「Ex Tap 巻き込みルール」（rev.7）: 同時刻・セル交差する Ex Tap があれば常に PERFECT+。</summary>
        private JudgeKind? TierFor(NoteKind kind, float absMs, bool exBoosted = false)
        {
            var traits = NoteKindTraits.Of(kind);
            if (traits.judgeProfile == JudgeProfile.AllPerfect || exBoosted) return JudgeKind.PerfectPlus;
            return JudgeTiers.TierFor(absMs)?.kind;
        }

        /// <summary>Tap/ExTap/Flick は層にスナップして描いている（NoteGeometry）ので、演出・内訳も 0/1 にスナップする。</summary>
        private static float SnapLayerF(Waypoint wp) => wp.layerF > 0.5f ? 1f : 0f;

        /// <summary>gameplay-feel-r2.md §6。リザルトの内訳の行。layerF は判定点の高さ（Slide はコンボ点ごと）。</summary>
        private static ResultCategory CategoryOf(Note n, float layerF)
        {
            bool sky = layerF > 0.5f;
            return n.kind switch
            {
                NoteKind.Tap => sky ? ResultCategory.TapSky : ResultCategory.TapGround,
                NoteKind.ExTap => sky ? ResultCategory.ExTapSky : ResultCategory.ExTapGround,
                NoteKind.Slide => sky ? ResultCategory.SlideSky : ResultCategory.SlideGround,
                NoteKind.Flick => sky ? ResultCategory.FlickSky : ResultCategory.FlickGround,
                _ => n.points[0].layerTo > n.points[0].layerF ? ResultCategory.Riser : ResultCategory.Diver,
            };
        }

        /// <summary>判定結果をスコア/コンボ/演出に反映する（MISS以外）。
        /// layerF・cellF は演出（判定名・ヒット演出）の位置（cellF は連続値の左端）。slideTick は Slide の始点以外の
        /// コンボ点（演出を軽量版にするため、gameplay-feel-r1.md §4）。ms は入力時刻−ノーツ時刻（正=遅い）、
        /// NaN は早い/遅いが意味を持たない判定（gameplay-feel-r2.md §2）。</summary>
        private void CommitJudgement(JudgeKind judged, Note n, float layerF, float cellF, float width, float songTime, float ms,
            bool slideTick = false)
        {
            Score.Add(CategoryOf(n, layerF), judged, ms);
            Score.combo++;
            Score.maxCombo = Math.Max(Score.maxCombo, Score.combo);
            Score.lastJudge = judged switch
            {
                JudgeKind.PerfectPlus => "PERFECT+",
                JudgeKind.Perfect => "PERFECT",
                JudgeKind.Good => "GOOD",
                _ => "",
            };
            Score.lastMs = float.IsNaN(ms) ? 0f : ms;
            AddFlash(layerF, cellF, width, songTime, judged, ms, slideTick);
            onJudged?.Invoke(judged);
        }

        private void CommitMiss(Note n, float layerF, float cellF, float width, float songTime, bool slideTick = false)
        {
            Score.Add(CategoryOf(n, layerF), JudgeKind.Miss, float.NaN);
            Score.combo = 0;
            Score.lastJudge = "MISS";
            AddFlash(layerF, cellF, width, songTime, JudgeKind.Miss, float.NaN, slideTick);
        }

        /// <summary>Flashes は StageOverlay が毎フレーム取り込んで空にする。取り込む側が居ない
        /// （譜面エディタのプレビュー等）と溜まり続けるので、古いものから捨てて上限を設ける。</summary>
        private const int MaxPendingFlashes = 64;

        private void AddFlash(float layerF, float cellF, float width, float songTime, JudgeKind kind, float ms, bool slideTick)
        {
            if (Flashes.Count >= MaxPendingFlashes) Flashes.RemoveAt(0);
            Flashes.Add(new HitFlash
            {
                layer = layerF > 0.5f ? Layer.Sky : Layer.Ground, cell = (int)MathF.Round(cellF), cellF = cellF, width = width,
                layerF = layerF, born = songTime, kind = kind, ms = ms, slideTick = slideTick,
            });
        }

        /// <summary>
        /// note-spec.md §6.1。トレイト駆動でティアを決め、スコア/コンボ/演出を反映する。
        /// 有効なティアが無い場合（chainWindow の外＝理論上到達しない）は null を返し、呼び出し側は状態を変えない。
        /// </summary>
        private JudgeKind? ApplyJudgement(Note n, float dt, float layerF, float cellF, float width, float songTime,
            bool exBoosted = false, bool slideTick = false)
        {
            // dt = ノーツ時刻 - 入力時刻 なので ms = 入力時刻 - ノーツ時刻。
            // **正 = 遅押し / 負 = 早押し**（HUDの "PERFECT +45ms" もこの符号で出る）。
            // 2026-08-13訂正: ここは以前「正 = 早押し」と書かれていたが逆だった。
            // オフセット校正はこの値の符号を見て行う（perf-r1.md §12）ため、実害のある誤りだった。
            // 他の算出箇所(TryResolveSlidePoint等の `(songTime - wp.time) * 1000f`)は同じ符号規則。
            float ms = -dt * 1000f;
            var judged = TierFor(n.kind, MathF.Abs(ms), exBoosted);
            if (judged == null) return null;
            CommitJudgement(judged.Value, n, layerF, cellF, width, songTime, ms, slideTick);
            return judged;
        }

        /// <summary>Tap/ExTap: 接触即ヒット確定。</summary>
        private void ResolveHit(NoteRuntime rt, Waypoint wp, float dt, float songTime)
        {
            var judged = ApplyJudgement(rt.note, dt, SnapLayerF(wp), wp.cellF, wp.width, songTime, rt.exBoosted);
            if (judged == null) return;
            rt.state = NoteState.Hit;
            setAlpha(rt, 0f);
        }

        /// <summary>毎フレーム: 見逃し判定、Slide のコンボ点独立判定（item7）、Flick の移動量判定（item8）</summary>
        public void Update(float songTime, IEnumerable<Contact> contacts)
        {
            var rts = runtimes;
            float rawWin = JudgeTiers.All[^1].halfWidthMs / 1000f; // GOODの素の半幅(=100ms)。Flick・Slide始点は早い側もこの分だけ窓が開く
            float lookAhead = rawWin + MathF.Max(0f, cfg.riserWindowExtendMs / 1000f); // Riser は早い側が ext だけ広い

            while (cursor < rts.Count &&
                   (rts[cursor].state == NoteState.Hit || rts[cursor].state == NoteState.Missed))
                cursor++;

            for (int i = cursor; i < rts.Count; i++)
            {
                var rt = rts[i];
                var n = rt.note;
                float start = ChartMath.NoteStart(n);
                // note-spec.md §4.3: Flickは早い側もPERFECT+まで拾うため、窓は start-rawWin から開く。
                // Slide始点（占有駆動、gameplay-feel-r1.md §2）も時間対称なので同じ時刻から見始める。
                // 開始時刻順ソート済みなので、これより先の全ノーツも同様に未到達。
                if (start - lookAhead > songTime) break;
                if (n.kind != NoteKind.Riser && start - rawWin > songTime) continue; // Riser 以外の窓は従来どおり rawWin から

                if (rt.state == NoteState.Pending)
                {
                    if (n.kind == NoteKind.Flick)
                    {
                        UpdateFlickPending(rt, n, songTime, contacts);
                        continue;
                    }
                    if (n.kind == NoteKind.Riser)
                    {
                        UpdateRiserPending(rt, n, songTime, contacts);
                        continue;
                    }
                    if (n.kind == NoteKind.Slide)
                    {
                        // gameplay-feel-r1.md §2: 始点も占有駆動。窓が開いた時点で Active にし、
                        // 以降は UpdateSlide が始点→各コンボ点の順に判定する。
                        rt.state = NoteState.Active;
                        rt.startResolved = false;
                        rt.nextComboIndex = 0;
                    }
                    else
                    {
                        if (start > songTime) continue; // Tap/ExTap はまだ開始前なら何もしない

                        // note-spec.md §6.2/§0.2: Tap/ExTap は実効窓（縦連判定で中点分割された窓）の
                        // 上限を超えた時点でMISSが確定する。
                        float hi = chainWindows.TryGetValue(rt, out var win) ? win.hi : start + rawWin;
                        if (songTime > hi)
                        {
                            var wp = n.points[0];
                            CommitMiss(n, SnapLayerF(wp), wp.cellF, wp.width, songTime);
                            rt.state = NoteState.Missed;
                            setAlpha(rt, 0.12f);
                        }
                        continue;
                    }
                }

                if (n.kind != NoteKind.Slide || rt.state != NoteState.Active) continue;
                UpdateSlide(rt, n, songTime, contacts);
            }
        }

        /// <summary>
        /// note-spec.md §2.1/§2.4。コンボ点を独立に判定する。旧 HOLD BREAK（0.2秒離れたら丸ごと失敗）は廃止:
        /// 一度逃しても、以降のコンボ点は押し直せば成立しうる。
        /// gameplay-feel-r1.md §2: 始点(points[0])も同じ占有駆動のコンボ点として、comboTimes より先に判定する。
        /// </summary>
        private void UpdateSlide(NoteRuntime rt, Note n, float songTime, IEnumerable<Contact> contacts)
        {
            float t0 = ChartMath.NoteStart(n);
            bool occ = AnyContactInBand(contacts, n, songTime);
            rt.slideSamples.Add((songTime, occ));
            // 始点が判定線に届く前（窓が開いてから t0 まで）は薄くしない（押す前に暗くなって見えるため）
            if (songTime >= t0) setAlpha(rt, occ ? 1f : 0.45f);

            var comboTimes = n.comboTimes;

            // ipad-test-findings-r1.md §④。いま判定線を通過中の区間を、実際に押さえられていれば
            // その場で「食べる」側へ倒す。**コンボ点の確定を待ってはいけない**:
            // 確定時点で区間iは既に judgment line を通過し終えていることがあり、食べる過程が
            // 画面に出ず「通過済みの区間が丸ごと消える」だけになる（2026-08-10のユーザー報告）。
            if (occ && songTime >= t0)
            {
                // 通過中の区間は songTime から直接求める。nextComboIndex は確定が遅れる分
                // 1つ前を指していることがあり、そのまま使うと食べ始めが遅れて段差になる。
                int seg = rt.nextComboIndex;
                while (seg < comboTimes.Count && songTime >= comboTimes[seg]) seg++;
                // 一度trueにした区間はfalseへ戻さない（sticky）: 途中で手を離したときに、
                // 既に食べられて消えた部分が復活してしまうのを防ぐため。離した後の区間は
                // occ==false でここを通らないので、そのまま流れて行く（＝MISSは通り過ぎる）。
                if (seg < comboTimes.Count) setSegmentEatable?.Invoke(rt, seg, true);
            }

            // note-feel-r3: 始点は OnEnter（枠内更新）で PERFECT+ 確定。窓（t0+100ms）を過ぎても来なければ MISS。
            if (!rt.startResolved && songTime > t0 + JudgeTiers.All[^1].halfWidthMs / 1000f)
            {
                var wp0 = n.points[0];
                CommitMiss(n, wp0.layerF, wp0.cellF, wp0.width, songTime);
                rt.startResolved = true;
            }
            if (rt.startResolved)
            {
                while (rt.nextComboIndex < comboTimes.Count &&
                       TryResolveSlidePoint(rt, n, comboTimes[rt.nextComboIndex], songTime, slideTick: true))
                    rt.nextComboIndex++;
            }

            // 次の未確定コンボ点の判定窓より前のサンプルはもう不要
            float nextTp = !rt.startResolved ? t0
                : rt.nextComboIndex < comboTimes.Count ? comboTimes[rt.nextComboIndex] : songTime;
            float horizon = nextTp - 0.15f;
            rt.slideSamples.RemoveAll(s => s.time < horizon);

            if (rt.startResolved && rt.nextComboIndex >= comboTimes.Count)
            {
                // ipad-test-findings-r1.md §④: スコア/カーソル進行のためHit状態にはするが、
                // 強制的にalpha=0で隠すのはやめた。Hitした区間は既に判定線で食べられて見えなく
                // なっており、Missした区間は帯として自然に通り過ぎ続ける（近距離フェードで消える）。
                rt.state = NoteState.Hit;
            }
        }

        /// <summary>
        /// note-spec.md §2.4。コンボ点 t_p について、[t_p-100ms, t_p+100ms] 内で帯を占有していた
        /// サンプルのうち最も近いものとの dt でティアを決める。占有サンプルが無ければ MISS。
        /// 確定したら true を返す。
        ///
        /// gameplay-feel-r1.md §2.5（早期確定）: 以前は常に t_p+100ms まで待っていた（note-spec §9-4 の
        /// 表示遅れ）。今 now ≥ t_p で、既知の最良 |diff| が now − t_p 以下なら、これから来るサンプル
        /// （時刻 &gt; now）はどれも |diff| &gt; now − t_p なので最良を更新できない。よってその場で確定しても
        /// 待った場合と結果は完全に同じ。押しっぱなしなら t_p 直後のフレームで確定する。
        /// </summary>
        private bool TryResolveSlidePoint(NoteRuntime rt, Note n, float tp, float songTime, bool slideTick)
        {
            if (songTime < tp) return false;
            float lo = tp - 0.1f, hi = tp + 0.1f;
            bool found = false;
            float bestDiff = 0f; // s.time - tp（符号付き、|bestDiff| が最小のもの）
            foreach (var s in rt.slideSamples)
            {
                if (!s.occupied || s.time < lo || s.time > hi) continue;
                float diff = s.time - tp;
                if (!found || MathF.Abs(diff) < MathF.Abs(bestDiff)) { bestDiff = diff; found = true; }
            }

            bool final = songTime >= hi || (found && MathF.Abs(bestDiff) <= songTime - tp);
            if (!final) return false;

            var (layerF, cellF, width) = ChartMath.At(n, tp);
            if (!found) CommitMiss(n, layerF, cellF, width, songTime, slideTick);
            else ApplyJudgement(n, -bestDiff, layerF, cellF, width, songTime, slideTick: slideTick); // dt = ノーツ時刻 - 入力時刻
            return true;
        }

        /// <summary>
        /// note-spec.md §4。Flick は Presence 駆動: 枠内に接触点が存在し、直近 flickWindowMs の
        /// 移動量が flickDistance 以上になった瞬間に成立する。窓を過ぎても移動が無ければ §4.4 のフォールバック
        /// （枠内更新があれば GOOD、無ければ MISS）で確定する。
        /// </summary>
        private void UpdateFlickPending(NoteRuntime rt, Note n, float songTime, IEnumerable<Contact> contacts)
        {
            if (!chainWindows.TryGetValue(rt, out var win)) return;
            var wp = n.points[0];
            float lf = SnapLayerF(wp);
            float flickDistance = cfg.U / cfg.cells; // note-spec.md §4.2: 0.5セル幅

            if (songTime <= win.hi)
            {
                foreach (var c in contacts)
                {
                    if (!InBand(c, wp.layerF, wp.cellF, wp.width, songTime, cfg.flickMarginCells)) continue;
                    rt.flickEnterSeen = true; // §4.4フォールバック用: 枠内に接触があったことを記録

                    if (c.history.Count == 0) continue;
                    var oldest = c.history[0];
                    float du = c.u - oldest.u, dv = c.v - oldest.v;
                    if (MathF.Sqrt(du * du + dv * dv) < flickDistance) continue;

                    // note-spec.md §4.3: 早い側(dt<=+33.33ms)はPERFECT+に繰り上げ、以遠は通常ティア表と同じ
                    float ms = (songTime - wp.time) * 1000f; // 入力時刻 - ノーツ時刻
                    JudgeKind judged = ms <= 33.33f ? JudgeKind.PerfectPlus
                        : JudgeTiers.TierFor(MathF.Abs(ms))?.kind ?? JudgeKind.Miss;

                    if (judged == JudgeKind.Miss)
                    {
                        CommitMiss(n, lf, wp.cellF, wp.width, songTime);
                        rt.state = NoteState.Missed;
                        setAlpha(rt, 0.12f);
                    }
                    else
                    {
                        CommitJudgement(judged, n, lf, wp.cellF, wp.width, songTime, ms);
                        rt.state = NoteState.Hit;
                        setAlpha(rt, 0f);
                    }
                    c.history.Clear(); // note-spec.md §4.1: 成立後はこの接触のリングバッファをリセットする
                    return;
                }
                return;
            }

            // note-spec.md §4.4: 判定窓を過ぎても移動が確認できなかった場合。
            // 早い/遅いは意味を持たないので ms=NaN（EARLY/LATE を出さない、gameplay-feel-r2.md §2）。
            if (rt.flickEnterSeen)
            {
                CommitJudgement(JudgeKind.Good, n, lf, wp.cellF, wp.width, songTime, float.NaN);
                rt.state = NoteState.Hit;
                setAlpha(rt, 0f);
            }
            else
            {
                CommitMiss(n, lf, wp.cellF, wp.width, songTime);
                rt.state = NoteState.Missed;
                setAlpha(rt, 0.12f);
            }
        }

        /// <summary>
        /// note-spec.md §4.6（rev.7）。Riser は Flick と同じ Presence 駆動だが、方向制約
        /// （layerTo方向のΔvのみを見る）と閾値の測り方（Δv単独、§4.6.2）が異なる。
        /// 成立時は接触に handoff を記録し（§4.6.4）、終端層での EnterEvent を1回合成して
        /// 発火することで、後続の Tap が Judge の構造を変えずに引き継げるようにする。
        /// 後続 Slide（gameplay-feel-r1.md §2 で占有駆動）へは、handoff 中の実効 layerF の読み替え
        /// （EffectiveLayerF）で引き継がれる。
        ///
        /// gameplay-feel-r2.md §4.2 で判定を作り直した（ユーザー判断）:
        /// - 縦連判定を受けない（前後とも。窓は [t−100ms, t+100ms+riserLateShiftMs]）。
        /// - 毎フレーム「反応」(<see cref="RiserReacted"/>)の有無をサンプルし、窓内で最も良い反応でティアを決める
        ///   （Slide コンボ点と同じ時間対称のサンプル方式。早 GOOD の反応があっても、後で PERFECT+ の反応があればそちら）。
        /// - ティアは早い側が通常どおり、遅い側は全境界を riserLateShiftMs 後ろへずらす（擦り切るまでの時間を吸収）。
        ///   実装は実効ずれ <see cref="RiserEff"/> の絶対値で通常ティア表を引く。
        /// - 旧 §4.4 の救済 GOOD は廃止。窓の中で一度も反応しなければ MISS。
        /// </summary>
        private void UpdateRiserPending(NoteRuntime rt, Note n, float songTime, IEnumerable<Contact> contacts)
        {
            var wp = n.points[0];
            float dir = MathF.Sign(wp.layerTo - wp.layerF); // +1: 上向き(Riser) / -1: 下向き(Diver)
            if (dir == 0f) return; // layerTo==layerF は不正データ(ChartValidator V13)。判定不能として無視する

            float t = wp.time;
            float shift = cfg.riserLateShiftMs / 1000f;
            float ext = cfg.riserWindowExtendMs / 1000f;
            float w = JudgeTiers.All[^1].halfWidthMs / 1000f + ext;
            float lo = t - w, hi = t + w + shift;

            // note-spec.md §4.6.2: 絶対layerF 0.5への到達を基準1.0とする倍率。layerTo自体には依らない。
            float riserDistanceV = 0.5f * MathF.Abs(cfg.vSkyJudge - cfg.vGroundJudge) * cfg.riserReachFrac;

            // 反応は「条件を満たした瞬間」（接触ごとの立ち上がり）だけを記録する。満たしている間を毎フレーム数えると、
            // 擦り切った後に指を止めていても履歴（flickWindowMs）が残る間は反応し続け、早く擦っても後ろの
            // PERFECT+ 窓まで反応が伸びて早 GOOD が実質出なくなる（ユーザー仕様: 早く反応し PERFECT 窓で反応していなければ GOOD）。
            if (songTime >= lo && songTime <= hi)
            {
                foreach (var c in contacts)
                {
                    if (RiserReacted(c, wp, dir, riserDistanceV, songTime))
                    {
                        if (rt.riserReacting.Add(c.id)) rt.riserReactions.Add((songTime, c.id));
                    }
                    else rt.riserReacting.Remove(c.id);
                }
            }

            if (songTime < t) return; // ノーツ時刻より前は、後からもっと良い反応が来うるので確定しない

            bool found = false;
            float bestEff = 0f;
            int bestContact = 0;
            foreach (var (time, id) in rt.riserReactions)
            {
                if (time < lo || time > hi) continue;
                float eff = RiserEff(time - t, shift, ext);
                if (!found || MathF.Abs(eff) < MathF.Abs(bestEff)) { bestEff = eff; bestContact = id; found = true; }
            }

            // 早期確定（Slide §2.5 と同じ理屈）: これから来るサンプルの実効ずれは RiserEff(now − t) 以上なので、
            // 既知の最良のティアがそれで取りうるティア以上なら、待っても判定は変わらない
            // （ずれの大小ではなくティアで比べる。PERFECT+ の反応なら t を過ぎた時点で確定できる）。
            bool final = songTime > hi ||
                         (found && TierRank(bestEff) <= TierRank(RiserEff(songTime - t, shift, ext)));
            if (!final) return;

            if (!found)
            {
                CommitMiss(n, wp.layerF, wp.cellF, wp.width, songTime);
                rt.state = NoteState.Missed;
                setAlpha(rt, 0.12f);
                return;
            }

            // ms は実効ずれで記録する（ティアと EARLY/LATE の表示が食い違わないように）。
            float ms = bestEff * 1000f;
            var judged = JudgeTiers.TierFor(MathF.Abs(ms))?.kind ?? JudgeKind.Good; // 窓内なので理論上 null にならない
            CommitJudgement(judged, n, wp.layerF, wp.cellF, wp.width, songTime, ms);
            rt.state = NoteState.Hit;
            setAlpha(rt, 0f);

            Contact hitContact = null;
            foreach (var c in contacts)
                if (c.id == bestContact) { hitContact = c; break; }
            if (hitContact == null) return; // 指が既に離れている（handoff の対象が無い）

            // note-spec.md §4.6.4: handoffを記録し、終端層でのEnterEventを1回合成して発火する。
            // gameplay-feel-r2.md §4.2: 合成イベントの判定時刻は t + 実効ずれ。擦り切るまでの時間ぶん、
            // Riser と同時刻の行き先層 Tap が遅押し扱いになるのを防ぐ。
            hitContact.layerHandoffTo = wp.layerTo;
            hitContact.layerHandoffUntil = songTime + cfg.handoffWindowMs / 1000f;
            float enterTime = t + bestEff;
            OnEnter(new EnterEvent
            {
                layer = wp.layerTo > 0.5f ? Layer.Sky : Layer.Ground,
                cell = (int)MathF.Round(hitContact.cellF),
                fresh = true,
                at = enterTime,
                cellF = hitContact.cellF,
                layerF = wp.layerTo,
                fromHandoff = true,
            }, enterTime);
            hitContact.history.Clear(); // 1回の擦りで成立させられる Riser は1つまで
        }

        /// <summary>gameplay-feel-r2.md §4.2。Riser の実効ずれ（秒）。早い側はそのまま、遅い側は shift を差し引いて 0 で止める。
        /// note-feel-r3: さらに両側から ext を差し引く（PERFECT+ 窓を ±ext 広げ、下位ティアの境界も同じだけ外側へずらす）。
        /// 戻り値の絶対値で通常のティア表を引けば、そのまま広げた窓のティアになる。</summary>
        private static float RiserEff(float diff, float shift, float ext)
        {
            float d = diff <= 0f ? diff : MathF.Max(0f, diff - shift);
            return MathF.Sign(d) * MathF.Max(0f, MathF.Abs(d) - ext);
        }

        /// <summary>ずれ（秒）のティアの順位。0=PERFECT+ / 1=PERFECT / 2=GOOD / 3=窓外。</summary>
        private static int TierRank(float diffSec)
        {
            float absMs = MathF.Abs(diffSec) * 1000f;
            for (int i = 0; i < JudgeTiers.All.Length; i++)
                if (absMs <= JudgeTiers.All[i].halfWidthMs) return i;
            return JudgeTiers.All.Length;
        }

        /// <summary>
        /// gameplay-feel-r2.md §4.2。接触 c が今フレーム Riser に「反応」しているか:
        /// 直近 flickWindowMs の履歴で、指定方向への最大変位（履歴中で一番後ろの点から現在まで）が閾値以上、かつ
        /// 履歴のどこか（現在位置を含む）で元の層の判定域の中を通ったこと。
        /// 旧実装は「いまの位置が判定域の中」を要求していたが、閾値の到達点が判定域の端（layerF 0.5）と一致するため、
        /// 判定線より上から擦り始めると成立しないまま窓が閉じ、救済の GOOD になっていた（§4.1）。
        /// </summary>
        private bool RiserReacted(Contact c, Waypoint wp, float dir, float distanceV, float songTime)
        {
            if (c.history.Count == 0) return false;
            float back = dir * c.v;
            bool passedBand = InRiserColumn(c.cellF, wp);
            foreach (var (u, v, time) in c.history)
            {
                back = MathF.Min(back, dir * v);
                if (!passedBand && SampleInBand(c, u, v, time, wp)) passedBand = true;
            }
            return passedBand && dir * c.v - back >= distanceV;
        }

        /// <summary>履歴の1点 (u, v, time) が Riser の判定域の中か（両層、横余白のみ）。</summary>
        private bool SampleInBand(Contact c, float u, float v, float time, Waypoint wp)
        {
            float cellF = (u + cfg.U) * cfg.cells / (2f * cfg.U);
            return InRiserColumn(cellF, wp);
        }

        /// <summary>note-feel-r3。Riser/Diver の判定域は両層（層は問わず、横だけ見る）。layerF は [0,1] にクランプされ、
        /// 地上・空中の両判定域（各 ±layerJudgeRadius）で全体が覆われるので、層の条件を外すのと同じ。
        /// 指定方向への垂直移動量の閾値が残るので、縦に擦らない限り成立しない。
        /// 空中側から触れてそのまま上へ擦る Tap+Riser の配置でも Riser が成立する。</summary>
        private bool InRiserColumn(float cellF, Waypoint wp) =>
            cellF >= wp.cellF - cfg.riserMarginCells && cellF <= wp.cellF + wp.width + cfg.riserMarginCells;
    }
}
