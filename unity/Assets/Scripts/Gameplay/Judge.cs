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
    /// **今回まだ未実装（次の増分）**: Slideの連続座標判定・コンボ点ごとの独立判定（§2.4対称窓、
    /// item7）、Flickの移動量判定本体（item8、現状はTapと同じ接触判定の暫定代用）、
    /// 入力のバンド分割（item6）、Slide始点のContact駆動統一（item16）。
    /// Slideは旧実装のまま: 開始時刻に自動追従開始、離れて0.2秒でブレイク、縦連判定の対象外。
    /// </summary>
    public class Judge
    {
        public Score Score { get; private set; } = new();
        public List<HitFlash> Flashes { get; } = new();

        private StageConfig cfg;
        private readonly NoteView noteView;
        private int cursor;

        /// <summary>note-spec.md §6.2。ノーツごとの実効窓（縦連判定で中点分割した後の [lo, hi] 秒）。
        /// Tap/ExTap/Flickのみが対象（Slide始点は次の増分でitem16と合わせて対応）。</summary>
        private readonly Dictionary<NoteRuntime, (float lo, float hi)> chainWindows = new();

        public Judge(StageConfig cfg, NoteView noteView)
        {
            this.cfg = cfg;
            this.noteView = noteView;
        }

        public void SetConfig(StageConfig cfg) => this.cfg = cfg;

        public void Reset()
        {
            Score = new Score();
            cursor = 0;
        }

        /// <summary>
        /// note-spec.md §6.2 の縦連判定（中点分割）をロード時に1回だけprecomputeする。
        /// NoteView.Build() の直後に呼ぶこと。
        /// </summary>
        public void Prepare(List<NoteRuntime> runtimes)
        {
            chainWindows.Clear();
            float w = JudgeTiers.All[^1].halfWidthMs / 1000f; // GOODの半幅=100ms（対象集合T全ノーツ共通）

            // 対象集合 T = Tap / ExTap / Flick（Slide始点は次の増分で追加）。
            // runtimes は既に開始時刻順ソート済みなので、そのままの相対順序で prev/next の最近傍探索ができる。
            var group = new List<NoteRuntime>();
            foreach (var rt in runtimes)
                if (rt.note.kind == NoteKind.Tap || rt.note.kind == NoteKind.ExTap || rt.note.kind == NoteKind.Flick)
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
                if (rt.note.kind == NoteKind.Flick) lo = float.NegativeInfinity; // Flick: 早い側だけ免除(§6.2)

                chainWindows[rt] = (MathF.Max(lo, t - w), MathF.Min(hi, t + w));
            }
        }

        private static bool CellOverlap(Waypoint a, Waypoint b) => a.cellF < b.cellF + b.width && b.cellF < a.cellF + a.width;

        private (Layer layer, int lo, int hi) CellRange(NoteRuntime rt, float songTime)
        {
            var n = rt.note;
            int max = cfg.cells - 1;
            if (n.kind == NoteKind.Slide)
            {
                var (layerF, cellF, width) = ChartMath.At(n, songTime);
                int lo = (int)MathF.Floor(cellF - width / 2f);
                int hi = (int)MathF.Ceiling(cellF + width / 2f) - 1;
                return (
                    layerF > 0.5f ? Layer.Sky : Layer.Ground,
                    Math.Max(0, Math.Min(max, lo)),
                    Math.Max(0, Math.Min(max, Math.Max(lo, hi)))
                );
            }

            var wp = n.points[0];
            int cell = (int)MathF.Round(wp.cellF);
            int w = Math.Max(1, (int)MathF.Round(wp.width));
            return (
                wp.layerF > 0.5f ? Layer.Sky : Layer.Ground,
                Math.Max(0, Math.Min(max, cell)),
                Math.Max(0, Math.Min(max, cell + w - 1))
            );
        }

        private bool AnyOccupied(TouchInputManager input, Layer layer, int lo, int hi)
        {
            for (int k = lo; k <= hi; k++)
                if (input.IsOccupied(layer, k)) return true;
            return false;
        }

        /// <summary>「入力範囲内に新規の接触点が検出された」= ヒット判定のトリガ</summary>
        public void OnEnter(EnterEvent e, float songTime)
        {
            var rts = noteView.Runtimes;
            NoteRuntime best = null;
            float bestDt = float.PositiveInfinity;
            float rawWin = JudgeTiers.All[^1].halfWidthMs / 1000f; // GOODの素の半幅。中点分割は窓を狭めるだけなのでこれを打ち切り境界に使える

            for (int i = cursor; i < rts.Count; i++)
            {
                var rt = rts[i];
                var n = rt.note;
                if (ChartMath.NoteStart(n) > songTime + rawWin) break; // これ以降は誰の実効窓にも入らない
                if (n.kind == NoteKind.Slide) continue; // Slideは接触トリガではなく追従型（次の増分でitem16対応）
                if (rt.state != NoteState.Pending) continue;
                if (!chainWindows.TryGetValue(rt, out var win)) continue;
                if (songTime < win.lo || songTime > win.hi) continue;
                var wp = n.points[0];
                var layer = wp.layerF > 0.5f ? Layer.Sky : Layer.Ground;
                if (layer != e.layer) continue;
                int cell = (int)MathF.Round(wp.cellF);
                int w = Math.Max(1, (int)MathF.Round(wp.width));
                if (e.cell < cell || e.cell >= cell + w) continue;
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
                if (n.kind == NoteKind.Slide) continue;
                if (rt.state != NoteState.Pending) continue;
                var wp = n.points[0];
                if (MathF.Abs(wp.time - bestTime) > 1e-4f) continue;
                var layer = wp.layerF > 0.5f ? Layer.Sky : Layer.Ground;
                if (layer != e.layer) continue;
                int cell = (int)MathF.Round(wp.cellF);
                int w = Math.Max(1, (int)MathF.Round(wp.width));
                if (e.cell < cell || e.cell >= cell + w) continue;

                ResolveHit(rt, wp, cell, layer, wp.time - songTime, songTime);
            }
        }

        /// <summary>note-spec.md §6.1。トレイト（judgeProfile）駆動でティアを決め、スコア/コンボ/演出を反映する。</summary>
        private void ResolveHit(NoteRuntime rt, Waypoint wp, int cell, Layer layer, float dt, float songTime)
        {
            var traits = NoteKindTraits.Of(rt.note.kind);
            float ms = -dt * 1000f; // 正 = 早押し
            float absMs = MathF.Abs(ms);

            JudgeKind kind;
            if (traits.judgeProfile == JudgeProfile.AllPerfect)
            {
                kind = JudgeKind.PerfectPlus; // 窓内(呼び出し元でchainWindow済み)は常にPERFECT+
            }
            else
            {
                var tier = JudgeTiers.TierFor(absMs);
                if (tier == null) return; // 理論上ここには来ない（chainWindowで既に窓内保証済み）が安全側に
                kind = tier.Value.kind;
            }

            switch (kind)
            {
                case JudgeKind.PerfectPlus: Score.perfectPlus++; break;
                case JudgeKind.Perfect: Score.perfect++; break;
                case JudgeKind.Good: Score.good++; break;
            }
            Score.combo++;
            Score.maxCombo = Math.Max(Score.maxCombo, Score.combo);
            Score.lastJudge = kind switch
            {
                JudgeKind.PerfectPlus => "PERFECT+",
                JudgeKind.Perfect => "PERFECT",
                JudgeKind.Good => "GOOD",
                _ => "",
            };
            Score.lastMs = ms;

            rt.state = NoteState.Hit;
            noteView.SetNoteAlpha(rt, 0f);

            Flashes.Add(new HitFlash { layer = layer, cell = cell, width = wp.width, born = songTime, kind = kind });
        }

        /// <summary>毎フレーム: 見逃し判定と、Slide の追従型判定</summary>
        public void Update(float songTime, TouchInputManager input)
        {
            var rts = noteView.Runtimes;

            while (cursor < rts.Count &&
                   (rts[cursor].state == NoteState.Hit || rts[cursor].state == NoteState.Missed))
                cursor++;

            for (int i = cursor; i < rts.Count; i++)
            {
                var rt = rts[i];
                var n = rt.note;
                float start = ChartMath.NoteStart(n);
                if (start > songTime) break; // 開始時刻順にソート済みなので以降は未開始
                float end = ChartMath.NoteEnd(n);

                if (rt.state == NoteState.Pending)
                {
                    if (n.kind == NoteKind.Slide)
                    {
                        // Slideは開始時刻に到達したら追従判定を開始する（接触が要らない）
                        rt.state = NoteState.Active;
                        rt.lastHeld = songTime;
                    }
                    else
                    {
                        // note-spec.md §6.2: MISSは実効窓（縦連判定で中点分割された窓）の上限を超えた時点で確定
                        float hi = chainWindows.TryGetValue(rt, out var win)
                            ? win.hi
                            : start + JudgeTiers.All[^1].halfWidthMs / 1000f;
                        if (songTime > hi)
                        {
                            rt.state = NoteState.Missed;
                            noteView.SetNoteAlpha(rt, 0.12f);
                            Score.miss++;
                            Score.combo = 0;
                            Score.lastJudge = "MISS";
                            var wp = n.points[0];
                            var layer = wp.layerF > 0.5f ? Layer.Sky : Layer.Ground;
                            int cell = (int)MathF.Round(wp.cellF);
                            Flashes.Add(new HitFlash
                            {
                                layer = layer, cell = cell, width = wp.width, born = songTime, kind = JudgeKind.Miss,
                            });
                        }
                    }
                    continue;
                }

                if (rt.state != NoteState.Active) continue;

                var (layer2, lo, hi2) = CellRange(rt, songTime);
                bool held = AnyOccupied(input, layer2, lo, hi2);
                if (held) rt.lastHeld = songTime;

                // 保持が0.2秒以上外れたら失敗
                if (!held && songTime - rt.lastHeld > 0.2f)
                {
                    BreakNote(rt);
                    continue;
                }

                noteView.SetNoteAlpha(rt, held ? 1f : 0.45f);

                if (songTime > end)
                {
                    // Slideの終点は離す必要なし。フレーム落ちで丸ごと飛んだ場合に誤って
                    // 成立しないよう、「最後に保持できていた時刻」が終端に十分近いことを条件にする
                    if (end - rt.lastHeld <= 0.2f)
                    {
                        rt.state = NoteState.Hit;
                        noteView.SetNoteAlpha(rt, 0f);
                        Score.perfect++;
                        Score.combo++;
                        Score.maxCombo = Math.Max(Score.maxCombo, Score.combo);
                    }
                    else
                    {
                        BreakNote(rt);
                    }
                }
            }
        }

        private void BreakNote(NoteRuntime rt)
        {
            rt.state = NoteState.Missed;
            noteView.SetNoteAlpha(rt, 0.12f);
            Score.miss++;
            Score.combo = 0;
            Score.lastJudge = "HOLD BREAK";
        }
    }
}
