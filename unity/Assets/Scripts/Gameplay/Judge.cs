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
        private List<NoteRuntime> runtimes = new();
        private int cursor;

        /// <summary>note-spec.md §6.2。ノーツごとの実効窓（縦連判定で中点分割した後の [lo, hi] 秒）。
        /// 対象集合 T = Tap/ExTap/Flick/Slide始点（§6.2）。</summary>
        private readonly Dictionary<NoteRuntime, (float lo, float hi)> chainWindows = new();

        /// <param name="setAlpha">ノーツの表示アルファを設定するコールバック（通常は NoteView.SetNoteAlpha）</param>
        /// <param name="onJudged">判定成立(MISS以外)のたび呼ぶヒットSEコールバック（省略可）</param>
        public Judge(StageConfig cfg, Action<NoteRuntime, float> setAlpha, Action<JudgeKind> onJudged = null)
        {
            this.cfg = cfg;
            this.setAlpha = setAlpha;
            this.onJudged = onJudged;
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
                rt.flickEnterSeen = false;

                if (ChartMath.NoteEnd(n) < songTime)
                {
                    rt.state = NoteState.Hit; // 過去に飛ばした分。スコアには反映しない
                    setAlpha(rt, 0f);
                }
                else if (n.kind == NoteKind.Slide && ChartMath.NoteStart(n) < songTime)
                {
                    rt.state = NoteState.Active;
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

            // 対象集合 T = Tap / ExTap / Flick / Riser / Slide始点（§6.2、rev.7でRiser追加）。
            // runtimes は既に開始時刻順ソート済みなので、そのままの相対順序で prev/next の最近傍探索ができる。
            var group = new List<NoteRuntime>();
            foreach (var rt in runtimes)
                if (rt.note.kind == NoteKind.Tap || rt.note.kind == NoteKind.ExTap ||
                    rt.note.kind == NoteKind.Flick || rt.note.kind == NoteKind.Riser || rt.note.kind == NoteKind.Slide)
                    group.Add(rt);

            for (int i = 0; i < group.Count; i++)
            {
                var rt = group[i];
                var wp = rt.note.points[0];
                var layer = wp.layerF > 0.5f ? Layer.Sky : Layer.Ground;
                float t = wp.time;

                float lo = float.NegativeInfinity;
                float hi = float.PositiveInfinity;

                for (int j = i - 1; j >= 0; j--)
                {
                    var pj = group[j].note.points[0];
                    if (pj.time >= t) continue; // 厳密不等号: 同時刻グループはprev/nextにならない(§6.4)
                    var pLayer = pj.layerF > 0.5f ? Layer.Sky : Layer.Ground;
                    if (pLayer != layer || !CellOverlap(pj, wp)) continue;
                    lo = (pj.time + t) / 2f;
                    break;
                }
                for (int j = i + 1; j < group.Count; j++)
                {
                    var nj = group[j].note.points[0];
                    if (nj.time <= t) continue;
                    var nLayer = nj.layerF > 0.5f ? Layer.Sky : Layer.Ground;
                    if (nLayer != layer || !CellOverlap(nj, wp)) continue;
                    hi = (t + nj.time) / 2f;
                    break;
                }

                var traits = NoteKindTraits.Of(rt.note.kind);
                if (traits.chainExempt) { lo = float.NegativeInfinity; hi = float.PositiveInfinity; } // Ex Tap: 自身は切られない
                if (rt.note.kind == NoteKind.Flick || rt.note.kind == NoteKind.Riser)
                    lo = float.NegativeInfinity; // Flick/Riser: 早い側だけ免除(§6.2、§4.6.5)

                chainWindows[rt] = (MathF.Max(lo, t - w), MathF.Min(hi, t + w));
            }

            PrepareExBoost(group);
        }

        /// <summary>
        /// note-spec.md §6.4「Ex Tap 巻き込みルール」（rev.7）。同時刻・同一層でセル範囲が交差する
        /// Ex Tap を持つ Tap / Slide始点を exBoosted=true にする。実行時にその入力が実際に
        /// Ex Tap のセル範囲へ触れたかは問わない（譜面上で交差していれば常に、が仕様）。
        /// chainWindows と同様ロード時に一度だけ計算する（実行時コストはゼロ）。
        /// </summary>
        private void PrepareExBoost(List<NoteRuntime> group)
        {
            var exNotes = new List<NoteRuntime>();
            foreach (var rt in group)
                if (rt.note.kind == NoteKind.ExTap) exNotes.Add(rt);

            foreach (var rt in group)
            {
                rt.exBoosted = false;
                if (rt.note.kind != NoteKind.Tap && rt.note.kind != NoteKind.Slide) continue;
                var wp = rt.note.points[0];
                float t = wp.time;

                foreach (var ex in exNotes)
                {
                    var ewp = ex.note.points[0];
                    if (MathF.Abs(ewp.time - t) >= 1e-4f) continue; // 同時刻のみ
                    var eLayer = ewp.layerF > 0.5f ? Layer.Sky : Layer.Ground;

                    // note-spec.md §6.4.3: Tapは離散層、Slide始点は連続座標(layerJudgeRadius)で層一致を見る
                    bool sameLayer = rt.note.kind == NoteKind.Slide
                        ? MathF.Abs(wp.layerF - (eLayer == Layer.Sky ? 1f : 0f)) <= cfg.layerJudgeRadius
                        : (wp.layerF > 0.5f ? Layer.Sky : Layer.Ground) == eLayer;
                    if (!sameLayer) continue;
                    if (!CellOverlap(ewp, wp)) continue;
                    rt.exBoosted = true;
                    break;
                }
            }
        }

        // editor-ui-rework-r3.md §5: cellFは全種別で左端基準（旧: Slideのみ中心基準）。
        private static bool CellOverlap(Waypoint a, Waypoint b) => a.cellF < b.cellF + b.width && b.cellF < a.cellF + a.width;

        /// <summary>note-spec.md §0.2。Slideの現在位置(帯)に接触点が包含されているかを連続座標で判定する。</summary>
        private bool AnyContactInBand(IEnumerable<Contact> contacts, Note n, float t)
        {
            var (layerF, cellF, width) = ChartMath.At(n, t);
            foreach (var c in contacts)
                if (InBand(c, layerF, cellF, width, t))
                    return true;
            return false;
        }

        /// <summary>
        /// note-spec.md §4.6.4（rev.7）。handoff が有効な間（songTime &lt;= layerHandoffUntil）は
        /// 実際の layerF ではなく layerHandoffTo を返す。Riser 成立後、指がまだ物理的に終端層へ
        /// 到達していなくても後続 Slide 始点などの包含判定を一定時間だけ通すための読み替え。
        /// </summary>
        private static float EffectiveLayerF(Contact c, float songTime) =>
            songTime <= c.layerHandoffUntil ? c.layerHandoffTo : c.layerF;

        private bool InBand(Contact c, float layerF, float cellF, float width, float songTime) =>
            MathF.Abs(EffectiveLayerF(c, songTime) - layerF) <= cfg.layerJudgeRadius &&
            c.cellF >= cellF && c.cellF <= cellF + width;

        /// <summary>
        /// EnterEvent がノーツ N の包含判定を満たすか。Tap/ExTap は離散セル(§0.2)、
        /// Slide 始点は連続座標(cellF/layerF、§0.2)で判定する（駆動は共通してこの枠内更新イベント）。
        /// Flick は Presence 駆動（item8、<see cref="UpdateFlickPending"/>）のためここでは扱わない。
        /// </summary>
        private bool Contains(Note n, Waypoint wp, EnterEvent e)
        {
            if (n.kind == NoteKind.Slide)
            {
                return MathF.Abs(e.layerF - wp.layerF) <= cfg.layerJudgeRadius &&
                       e.cellF >= wp.cellF && e.cellF <= wp.cellF + wp.width;
            }
            var layer = wp.layerF > 0.5f ? Layer.Sky : Layer.Ground;
            if (layer != e.layer) return false;
            int cell = (int)MathF.Round(wp.cellF);
            int w = Math.Max(1, (int)MathF.Round(wp.width));
            return e.cell >= cell && e.cell < cell + w;
        }

        /// <summary>「入力範囲内に新規の接触点が検出された」= ヒット判定のトリガ（Tap/ExTap/Slide始点）</summary>
        public void OnEnter(EnterEvent e, float songTime)
        {
            var rts = runtimes;
            NoteRuntime best = null;
            float bestDt = float.PositiveInfinity;
            float rawWin = JudgeTiers.All[^1].halfWidthMs / 1000f; // GOODの素の半幅。中点分割は窓を狭めるだけなのでこれを打ち切り境界に使える

            for (int i = cursor; i < rts.Count; i++)
            {
                var rt = rts[i];
                var n = rt.note;
                if (ChartMath.NoteStart(n) > songTime + rawWin) break; // これ以降は誰の実効窓にも入らない
                if (n.kind == NoteKind.Flick || n.kind == NoteKind.Riser) continue; // Presence駆動でUpdate()側が扱う（§4/§4.6）
                if (rt.state != NoteState.Pending) continue;
                if (!chainWindows.TryGetValue(rt, out var win)) continue;
                if (songTime < win.lo || songTime > win.hi) continue;
                var wp = n.points[0];
                if (!Contains(n, wp, e)) continue;
                float dt = wp.time - songTime;
                if (MathF.Abs(dt) < MathF.Abs(bestDt))
                {
                    best = rt;
                    bestDt = dt;
                }
            }

            if (best == null) return;
            float bestTime = best.note.points[0].time;

            // note-spec.md §6.4: 同時刻グループのうち、入力位置が包含判定を満たすノーツを全て
            // 同じ入力イベント(同じ dt)でまとめて解決する。ティアはノーツごとに個別に決まる。
            for (int i = cursor; i < rts.Count; i++)
            {
                var rt = rts[i];
                var n = rt.note;
                if (ChartMath.NoteStart(n) > bestTime + 1e-4f) break; // 開始時刻順ソート済みなので同時刻グループを過ぎたら終了
                if (n.kind == NoteKind.Flick || n.kind == NoteKind.Riser) continue;
                if (rt.state != NoteState.Pending) continue;
                var wp = n.points[0];
                if (MathF.Abs(wp.time - bestTime) > 1e-4f) continue;
                if (!Contains(n, wp, e)) continue;

                float dt = wp.time - songTime;
                var layer = wp.layerF > 0.5f ? Layer.Sky : Layer.Ground;
                if (n.kind == NoteKind.Slide) ResolveSlideStart(rt, wp, layer, dt, songTime);
                else
                {
                    int cell = (int)MathF.Round(wp.cellF);
                    ResolveHit(rt, wp, cell, layer, dt, songTime);
                }
            }
        }

        /// <summary>note-spec.md §6.1。トレイト（judgeProfile）駆動でティアを決める。理論上ここに来ない場合は null（呼び出し元がchainWindowで既に窓内を保証している）。
        /// exBoosted は §6.4「Ex Tap 巻き込みルール」（rev.7）: 同時刻・セル交差する Ex Tap があれば常に PERFECT+。</summary>
        private JudgeKind? TierFor(NoteKind kind, float absMs, bool exBoosted = false)
        {
            var traits = NoteKindTraits.Of(kind);
            if (traits.judgeProfile == JudgeProfile.AllPerfect || exBoosted) return JudgeKind.PerfectPlus;
            return JudgeTiers.TierFor(absMs)?.kind;
        }

        /// <summary>判定結果をスコア/コンボ/演出に反映する（MISS以外）。</summary>
        private void CommitJudgement(JudgeKind judged, Layer layer, int cell, float width, float songTime, float ms)
        {
            switch (judged)
            {
                case JudgeKind.PerfectPlus: Score.perfectPlus++; break;
                case JudgeKind.Perfect: Score.perfect++; break;
                case JudgeKind.Good: Score.good++; break;
            }
            Score.combo++;
            Score.maxCombo = Math.Max(Score.maxCombo, Score.combo);
            Score.lastJudge = judged switch
            {
                JudgeKind.PerfectPlus => "PERFECT+",
                JudgeKind.Perfect => "PERFECT",
                JudgeKind.Good => "GOOD",
                _ => "",
            };
            Score.lastMs = ms;
            Flashes.Add(new HitFlash { layer = layer, cell = cell, width = width, born = songTime, kind = judged });
            onJudged?.Invoke(judged);
        }

        private void CommitMiss(Layer layer, int cell, float width, float songTime)
        {
            Score.miss++;
            Score.combo = 0;
            Score.lastJudge = "MISS";
            Flashes.Add(new HitFlash { layer = layer, cell = cell, width = width, born = songTime, kind = JudgeKind.Miss });
        }

        /// <summary>
        /// note-spec.md §6.1。トレイト駆動でティアを決め、スコア/コンボ/演出を反映する。
        /// 有効なティアが無い場合（chainWindow の外＝理論上到達しない）は null を返し、呼び出し側は状態を変えない。
        /// </summary>
        private JudgeKind? ApplyJudgement(NoteKind kind, float dt, Layer layer, int cell, float width, float songTime, bool exBoosted = false)
        {
            float ms = -dt * 1000f; // 正 = 早押し
            var judged = TierFor(kind, MathF.Abs(ms), exBoosted);
            if (judged == null) return null;
            CommitJudgement(judged.Value, layer, cell, width, songTime, ms);
            return judged;
        }

        /// <summary>Tap/ExTap: 接触即ヒット確定。</summary>
        private void ResolveHit(NoteRuntime rt, Waypoint wp, int cell, Layer layer, float dt, float songTime)
        {
            var judged = ApplyJudgement(rt.note.kind, dt, layer, cell, wp.width, songTime, rt.exBoosted);
            if (judged == null) return;
            rt.state = NoteState.Hit;
            setAlpha(rt, 0f);
        }

        /// <summary>
        /// note-spec.md §0.2/§2.1。Slide 始点は Tap と同じ枠内更新で駆動されるが、
        /// ヒット確定はせず Active へ遷移する（以降のコンボ点は Update() で独立判定、item7）。
        /// </summary>
        private void ResolveSlideStart(NoteRuntime rt, Waypoint wp, Layer layer, float dt, float songTime)
        {
            int cell = (int)MathF.Round(wp.cellF);
            var judged = ApplyJudgement(NoteKind.Slide, dt, layer, cell, wp.width, songTime, rt.exBoosted);
            if (judged == null) return;
            rt.state = NoteState.Active;
            rt.nextComboIndex = 0;
        }

        /// <summary>毎フレーム: 見逃し判定、Slide のコンボ点独立判定（item7）、Flick の移動量判定（item8）</summary>
        public void Update(float songTime, IEnumerable<Contact> contacts)
        {
            var rts = runtimes;
            float rawWin = JudgeTiers.All[^1].halfWidthMs / 1000f; // GOODの素の半幅(=100ms)。Flickは早い側もこの分だけ窓が開く

            while (cursor < rts.Count &&
                   (rts[cursor].state == NoteState.Hit || rts[cursor].state == NoteState.Missed))
                cursor++;

            for (int i = cursor; i < rts.Count; i++)
            {
                var rt = rts[i];
                var n = rt.note;
                float start = ChartMath.NoteStart(n);
                // note-spec.md §4.3: Flickは早い側もPERFECT+まで拾うため、窓は start-rawWin から開く。
                // 開始時刻順ソート済みなので、これより先の全ノーツも同様に未到達。
                if (start - rawWin > songTime) break;

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
                    if (start > songTime) continue; // Flick/Riser以外はまだ開始前なら何もしない

                    // note-spec.md §6.2/§0.2: Tap/ExTap/Slide始点は同じ実効窓
                    // （縦連判定で中点分割された窓）の上限を超えた時点でMISSが確定する。
                    float hi = chainWindows.TryGetValue(rt, out var win) ? win.hi : start + rawWin;
                    if (songTime > hi)
                    {
                        var wp = n.points[0];
                        var layer = wp.layerF > 0.5f ? Layer.Sky : Layer.Ground;
                        int cell = (int)MathF.Round(wp.cellF);
                        CommitMiss(layer, cell, wp.width, songTime);

                        if (n.kind == NoteKind.Slide)
                        {
                            // note-spec.md §2.1: 始点を逃してMISSになっても、残りのコンボ点は
                            // 押し直せば独立に成立しうる。以降の判定を続けるためActiveへ遷移する
                            // （Hit/Missedにすると Update() の巡回対象から外れてしまう）。
                            rt.state = NoteState.Active;
                            rt.nextComboIndex = 0;
                            setAlpha(rt, 0.45f);
                        }
                        else
                        {
                            rt.state = NoteState.Missed;
                            setAlpha(rt, 0.12f);
                        }
                    }
                    continue;
                }

                if (n.kind != NoteKind.Slide || rt.state != NoteState.Active) continue;
                UpdateSlide(rt, n, songTime, contacts);
            }
        }

        /// <summary>
        /// note-spec.md §2.1/§2.4。コンボ点を独立に判定する。旧 HOLD BREAK（0.2秒離れたら丸ごと失敗）は廃止:
        /// 一度逃しても、以降のコンボ点は押し直せば成立しうる。
        /// </summary>
        private void UpdateSlide(NoteRuntime rt, Note n, float songTime, IEnumerable<Contact> contacts)
        {
            bool occ = AnyContactInBand(contacts, n, songTime);
            rt.slideSamples.Add((songTime, occ));
            setAlpha(rt, occ ? 1f : 0.45f);

            var comboTimes = n.comboTimes;
            while (rt.nextComboIndex < comboTimes.Count && songTime >= comboTimes[rt.nextComboIndex] + 0.1f)
            {
                ResolveSlideComboPoint(rt, n, comboTimes[rt.nextComboIndex], songTime);
                rt.nextComboIndex++;
            }

            // 次の未確定コンボ点の判定窓より前のサンプルはもう不要
            float horizon = (rt.nextComboIndex < comboTimes.Count ? comboTimes[rt.nextComboIndex] : songTime) - 0.15f;
            rt.slideSamples.RemoveAll(s => s.time < horizon);

            if (rt.nextComboIndex >= comboTimes.Count)
            {
                rt.state = NoteState.Hit;
                setAlpha(rt, 0f);
            }
        }

        /// <summary>
        /// note-spec.md §2.4。コンボ点 t_p について、[t_p-100ms, t_p+100ms] 内で帯を占有していた
        /// サンプルのうち最も近いものとの dt でティアを決める。占有サンプルが無ければ MISS。
        /// </summary>
        private void ResolveSlideComboPoint(NoteRuntime rt, Note n, float tp, float songTime)
        {
            float lo = tp - 0.1f, hi = tp + 0.1f;
            bool found = false;
            float bestDiff = 0f; // s.time - tp（符号付き、|bestDiff| が最小のもの）
            foreach (var s in rt.slideSamples)
            {
                if (!s.occupied || s.time < lo || s.time > hi) continue;
                float diff = s.time - tp;
                if (!found || MathF.Abs(diff) < MathF.Abs(bestDiff)) { bestDiff = diff; found = true; }
            }

            var (layerF, cellF, width) = ChartMath.At(n, tp);
            var layer = layerF > 0.5f ? Layer.Sky : Layer.Ground;
            int cell = (int)MathF.Round(cellF);

            if (!found)
            {
                CommitMiss(layer, cell, width, songTime);
                return;
            }

            // dt = ノーツ時刻 - 入力時刻（ResolveHit/ResolveSlideStart と同じ符号規則）
            ApplyJudgement(NoteKind.Slide, -bestDiff, layer, cell, width, songTime);
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
            var layer = wp.layerF > 0.5f ? Layer.Sky : Layer.Ground;
            int cell = (int)MathF.Round(wp.cellF);
            float flickDistance = cfg.U / cfg.cells; // note-spec.md §4.2: 0.5セル幅

            if (songTime <= win.hi)
            {
                foreach (var c in contacts)
                {
                    if (!InBand(c, wp.layerF, wp.cellF, wp.width, songTime)) continue;
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
                        CommitMiss(layer, cell, wp.width, songTime);
                        rt.state = NoteState.Missed;
                        setAlpha(rt, 0.12f);
                    }
                    else
                    {
                        CommitJudgement(judged, layer, cell, wp.width, songTime, ms);
                        rt.state = NoteState.Hit;
                        setAlpha(rt, 0f);
                    }
                    c.history.Clear(); // note-spec.md §4.1: 成立後はこの接触のリングバッファをリセットする
                    return;
                }
                return;
            }

            // note-spec.md §4.4: 判定窓を過ぎても移動が確認できなかった場合
            if (rt.flickEnterSeen)
            {
                CommitJudgement(JudgeKind.Good, layer, cell, wp.width, songTime, (songTime - wp.time) * 1000f);
                rt.state = NoteState.Hit;
                setAlpha(rt, 0f);
            }
            else
            {
                CommitMiss(layer, cell, wp.width, songTime);
                rt.state = NoteState.Missed;
                setAlpha(rt, 0.12f);
            }
        }

        /// <summary>
        /// note-spec.md §4.6（rev.7）。Riser は Flick と同じ Presence 駆動だが、方向制約
        /// （layerTo方向のΔvのみを見る）と閾値の測り方（Δv単独、§4.6.2）が異なる。
        /// 成立時は接触に handoff を記録し（§4.6.4）、終端層での EnterEvent を1回合成して
        /// 発火することで、後続 Slide 始点などが Judge の構造を変えずに引き継げるようにする。
        /// フォールバック（窓を過ぎても移動なし、§4.4 と同じ表）は UpdateFlickPending と共通の規則。
        /// </summary>
        private void UpdateRiserPending(NoteRuntime rt, Note n, float songTime, IEnumerable<Contact> contacts)
        {
            if (!chainWindows.TryGetValue(rt, out var win)) return;
            var wp = n.points[0];
            var layer = wp.layerF > 0.5f ? Layer.Sky : Layer.Ground;
            int cell = (int)MathF.Round(wp.cellF);
            float dir = MathF.Sign(wp.layerTo - wp.layerF); // +1: 上向き(Riser) / -1: 下向き(Diver)
            if (dir == 0f) return; // layerTo==layerF は不正データ(ChartValidator V13)。判定不能として無視する

            // note-spec.md §4.6.2: 絶対layerF 0.5への到達を基準1.0とする倍率。layerTo自体には依らない。
            float riserDistanceV = 0.5f * MathF.Abs(cfg.vSkyJudge - cfg.vGroundJudge) * cfg.riserReachFrac;

            if (songTime <= win.hi)
            {
                foreach (var c in contacts)
                {
                    if (!InBand(c, wp.layerF, wp.cellF, wp.width, songTime)) continue;
                    rt.flickEnterSeen = true; // §4.4フォールバック用（Flickと同じ規則を再利用）

                    if (c.history.Count == 0) continue;
                    var oldest = c.history[0];
                    float dv = (c.v - oldest.v) * dir; // 指定方向への符号付き移動量（逆方向は負になり不成立）
                    if (dv < riserDistanceV) continue;

                    // note-spec.md §4.3と同じ非対称窓（Flickと共有）
                    float ms = (songTime - wp.time) * 1000f;
                    JudgeKind judged = ms <= 33.33f ? JudgeKind.PerfectPlus
                        : JudgeTiers.TierFor(MathF.Abs(ms))?.kind ?? JudgeKind.Miss;

                    if (judged == JudgeKind.Miss)
                    {
                        CommitMiss(layer, cell, wp.width, songTime);
                        rt.state = NoteState.Missed;
                        setAlpha(rt, 0.12f);
                    }
                    else
                    {
                        CommitJudgement(judged, layer, cell, wp.width, songTime, ms);
                        rt.state = NoteState.Hit;
                        setAlpha(rt, 0f);

                        // note-spec.md §4.6.4: handoffを記録し、終端層でのEnterEventを1回合成して発火する。
                        c.layerHandoffTo = wp.layerTo;
                        c.layerHandoffUntil = songTime + cfg.handoffWindowMs / 1000f;
                        var targetLayer = wp.layerTo > 0.5f ? Layer.Sky : Layer.Ground;
                        OnEnter(new EnterEvent
                        {
                            layer = targetLayer,
                            cell = (int)MathF.Round(c.cellF),
                            fresh = true,
                            at = songTime,
                            cellF = c.cellF,
                            layerF = wp.layerTo,
                        }, songTime);
                    }
                    c.history.Clear();
                    return;
                }
                return;
            }

            // note-spec.md §4.4と同じフォールバック（Flickと共通）
            if (rt.flickEnterSeen)
            {
                CommitJudgement(JudgeKind.Good, layer, cell, wp.width, songTime, (songTime - wp.time) * 1000f);
                rt.state = NoteState.Hit;
                setAlpha(rt, 0f);
            }
            else
            {
                CommitMiss(layer, cell, wp.width, songTime);
                rt.state = NoteState.Missed;
                setAlpha(rt, 0.12f);
            }
        }
    }
}
