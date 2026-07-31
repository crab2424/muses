using System;
using System.Collections.Generic;
using Muses.Chart;
using Muses.Notes;
using Muses.Stage;
using Muses.TouchInput;

namespace Muses.Gameplay
{
    /// <summary>
    /// 判定。移植元: web-prototype/src/judge.ts。判定ウィンドウ・スコアリングは
    /// 未設計項目のため暫定実装のまま移植している（TS版のコメント参照）。
    ///
    /// note-spec.md rev.4 のデータモデル（Waypoint[] + kind プリセット）に合わせて再構成したが、
    /// トレイト駆動の判定（judgeProfile/chainExempt/縦連判定/スコア式など、note-spec.md §8 item4〜16）
    /// はまだ未実装。挙動は旧実装のまま: Tap/ExTap は接触即判定、Slide（旧Hold+旧Arc統合）は
    /// 開始時刻に自動追従開始、離れて0.2秒でブレイク。Flick は本来 Presence 駆動＋移動量判定だが
    /// item8 未着手のため、暫定的に Tap と同じ接触判定で代用している（TODO: item8で置き換え）。
    /// </summary>
    public class Judge
    {
        public Score Score { get; private set; } = new();
        public List<HitFlash> Flashes { get; } = new();

        private StageConfig cfg;
        private readonly NoteView noteView;
        private int cursor;

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
            float win = cfg.windowGood / 1000f;
            var rts = noteView.Runtimes;
            NoteRuntime best = null;
            float bestDt = float.PositiveInfinity;

            for (int i = cursor; i < rts.Count; i++)
            {
                var rt = rts[i];
                var n = rt.note;
                if (ChartMath.NoteStart(n) > songTime + win) break; // これ以降は窓の外
                if (rt.state != NoteState.Pending || n.kind == NoteKind.Slide) continue; // Slideは接触トリガではなく追従型
                var wp = n.points[0];
                var layer = wp.layerF > 0.5f ? Layer.Sky : Layer.Ground;
                if (layer != e.layer) continue;
                int cell = (int)MathF.Round(wp.cellF);
                int w = Math.Max(1, (int)MathF.Round(wp.width));
                if (e.cell < cell || e.cell >= cell + w) continue;
                float dt = wp.time - songTime;
                if (MathF.Abs(dt) > win) continue;
                if (MathF.Abs(dt) < MathF.Abs(bestDt))
                {
                    best = rt;
                    bestDt = dt;
                }
            }

            if (best == null) return;
            var bwp = best.note.points[0];
            var bLayer = bwp.layerF > 0.5f ? Layer.Sky : Layer.Ground;
            int bCell = (int)MathF.Round(bwp.cellF);

            float ms = -bestDt * 1000f; // 正 = 早押し
            bool perfect = MathF.Abs(ms) <= cfg.windowPerfect;
            if (perfect) Score.perfect++; else Score.good++;
            Score.combo++;
            Score.maxCombo = Math.Max(Score.maxCombo, Score.combo);
            Score.lastJudge = perfect ? "PERFECT" : "GOOD";
            Score.lastMs = ms;

            best.state = NoteState.Hit;
            noteView.SetNoteAlpha(best, 0f);

            Flashes.Add(new HitFlash
            {
                layer = bLayer,
                cell = bCell,
                width = bwp.width,
                born = songTime,
                kind = perfect ? JudgeKind.Perfect : JudgeKind.Good,
            });
        }

        /// <summary>毎フレーム: 見逃し判定と、Slide の追従型判定</summary>
        public void Update(float songTime, TouchInputManager input)
        {
            float win = cfg.windowGood / 1000f;
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
                    else if (songTime - start > win)
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
                    continue;
                }

                if (rt.state != NoteState.Active) continue;

                var (layer2, lo, hi) = CellRange(rt, songTime);
                bool held = AnyOccupied(input, layer2, lo, hi);
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
