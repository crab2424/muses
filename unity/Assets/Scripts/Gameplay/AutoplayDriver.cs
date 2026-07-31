using System;
using System.Collections.Generic;
using Muses.Chart;
using Muses.Notes;
using Muses.Stage;
using Muses.TouchInput;

namespace Muses.Gameplay
{
    /// <summary>
    /// editor-spec.md §5.2。実ゲームの <see cref="Judge"/> をそのまま走らせ、入力を自動生成して
    /// 全 PERFECT+ を狙う（判定系の回帰検知・演出確認が目的、note-spec.md §7 の理論値ちょうどが期待値）。
    ///
    /// Phase 3 で Judge が MonoBehaviour 非依存の純粋 C# クラスになっているため、
    /// Contact/EnterEvent の生成器を差し替えるだけで成立する（実装は容易、と editor-spec.md にある通り）。
    ///
    /// 生成規則（editor-spec.md §5.2 のとおり）:
    /// - Tap/ExTap/Slide始点は枠内更新(EnterEvent)が要るので、開始tickを跨いだフレームで
    ///   ちょうど Judge.OnEnter を1回呼ぶ。songTime にはノーツ自身の time をそのまま渡す
    ///   （フレームレートに関わらず dt=0 を保証し、理論値ちょうどを再現するため）。
    /// - Flick は Presence 駆動なので、開始time以降で毎フレーム「枠内に接触があり、
    ///   直近flickWindowMs以上の移動がある」ことを示す合成 Contact を供給する
    ///   （履歴を1件だけ manufactured すれば Judge.UpdateFlickPending が即座に移動成立と判定する）。
    /// - Slide 継続は各フレーム、ChartMath.At(note, songTime) の位置に合成 Contact を置き続ける
    ///   （Judge.UpdateSlide の帯占有サンプルがフレームごとに記録される。60fpsなら誤差は最大±8ms程度で
    ///   ティア窓(33.33ms〜)に対して実用上問題ない、というのは note-spec移植時に確立済みの許容範囲）。
    /// </summary>
    public static class AutoplayDriver
    {
        /// <summary>
        /// 1フレーム分の自動入力を生成する。Tap/ExTap/Slide始点は即座に judge.OnEnter を呼び、
        /// Slide継続・Flickの合成Contactは戻り値として返すので、呼び出し側はこれをそのまま
        /// judge.Update(curTime, contacts) に渡す。
        /// </summary>
        public static List<Contact> Step(Judge judge, StageConfig cfg, List<NoteRuntime> runtimes, float prevTime, float curTime)
        {
            var contacts = new List<Contact>();
            int syntheticId = -1;

            foreach (var rt in runtimes)
            {
                var note = rt.note;

                if (note.kind == NoteKind.Flick)
                {
                    if (rt.state != NoteState.Pending) continue;
                    var wp = note.points[0];
                    if (curTime < wp.time) continue;
                    contacts.Add(MakeFlickContact(cfg, wp, curTime, syntheticId--));
                    continue;
                }

                if (rt.state == NoteState.Pending)
                {
                    var wp = note.points[0];
                    if (prevTime < wp.time && curTime >= wp.time)
                    {
                        var layer = wp.layerF > 0.5f ? Layer.Sky : Layer.Ground;
                        var e = new EnterEvent
                        {
                            layer = layer,
                            cell = (int)MathF.Round(wp.cellF),
                            fresh = true,
                            at = wp.time,
                            cellF = wp.cellF,
                            layerF = wp.layerF,
                        };
                        // songTime にノーツ自身の time を渡すことで dt=0 を保証する（フレーム境界に依存しない）。
                        judge.OnEnter(e, wp.time);
                    }
                    continue;
                }

                if (note.kind == NoteKind.Slide && rt.state == NoteState.Active)
                {
                    var (layerF, cellF, _) = ChartMath.At(note, curTime);
                    contacts.Add(new Contact
                    {
                        id = syntheticId--,
                        layer = layerF > 0.5f ? Layer.Sky : Layer.Ground,
                        cell = (int)MathF.Round(cellF),
                        cellF = cellF,
                        layerF = layerF,
                        since = curTime,
                    });
                }
            }

            return contacts;
        }

        private static Contact MakeFlickContact(StageConfig cfg, Waypoint wp, float curTime, int id)
        {
            // Judge.InBand は c.cellF/c.layerF で枠内判定するため、その場に置くだけで枠内成立を満たす。
            // 移動量判定(c.u/c.v)は枠内判定と独立な軸なので、任意の原点+履歴差分で flickDistance 以上を作ればよい。
            float flickDistance = cfg.U / cfg.cells * 1.5f; // Judge.cs と同じ flickDistance(=U/cells) を確実に超える量
            var c = new Contact
            {
                id = id,
                layer = wp.layerF > 0.5f ? Layer.Sky : Layer.Ground,
                cell = (int)MathF.Round(wp.cellF),
                cellF = wp.cellF,
                layerF = wp.layerF,
                since = curTime,
                u = 0f,
                v = 0f,
            };
            c.history.Add((-flickDistance, 0f, curTime));
            return c;
        }
    }
}
