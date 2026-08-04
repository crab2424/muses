using System;
using System.Collections.Generic;
using UnityEngine;
using Muses.Chart;
using Muses.Notes;
using Muses.Stage;
using Muses.TouchInput;

namespace Muses.Gameplay
{
    /// <summary>
    /// implementation-roadmap.md 項目H。Judge の判定ロジック確認用スモークテスト。
    /// 空の GameObject にアタッチして Play すると、各ケースの OK/FAIL を Console に出力する
    /// （StageDeriveSmokeTest 等と同じ「アタッチして Play」方式）。
    ///
    /// Judge は NoteView/TouchInputManager（MonoBehaviour）に依存しない純粋な C# クラスなので、
    /// シーンや実機入力を用意せずに時刻と接触点を直接注入してテストできる。
    /// </summary>
    public class JudgeSmokeTest : MonoBehaviour
    {
        private int pass;
        private int fail;

        private void Start()
        {
            pass = 0;
            fail = 0;

            TestTapPerfectPlus();
            TestTapMissByTimeout();
            TestExTapAllPerfectWithinGoodWindow();
            TestSlideComboResolution();
            TestFlickHit();
            TestSeekSkipsPastNotesWithoutScoring();
            TestExBoostAppliesToOverlappingTap();
            TestRiserHandsOffToSlideStart();

            Debug.Log(fail == 0
                ? $"JudgeSmokeTest: ALL PASS ({pass})"
                : $"JudgeSmokeTest: {fail} FAIL / {pass + fail}");
        }

        private void Check(string label, bool ok)
        {
            if (ok) { pass++; Debug.Log($"OK   {label}"); }
            else { fail++; Debug.LogError($"FAIL {label}"); }
        }

        private static StageConfig Cfg() => StageConfig.Default();

        private static Note SingleWaypointNote(NoteKind kind, float time, Layer layer, float cell, float width = 2f) => new()
        {
            kind = kind,
            points = new List<Waypoint> { new() { time = time, layerF = layer == Layer.Sky ? 1f : 0f, cellF = cell, width = width } },
        };

        private void TestTapPerfectPlus()
        {
            var n = SingleWaypointNote(NoteKind.Tap, 1.0f, Layer.Ground, 3);
            var rt = new NoteRuntime { note = n };
            var judge = new Judge(Cfg(), (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { rt });

            judge.OnEnter(new EnterEvent { layer = Layer.Ground, cell = 3, fresh = true, at = 1.0f, cellF = 3f, layerF = 0f }, 1.0f);

            Check("Tap ちょうど押下 -> PERFECT+", judge.Score.perfectPlus == 1 && rt.state == NoteState.Hit);
        }

        private void TestTapMissByTimeout()
        {
            var n = SingleWaypointNote(NoteKind.Tap, 1.0f, Layer.Ground, 3);
            var rt = new NoteRuntime { note = n };
            var judge = new Judge(Cfg(), (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { rt });

            // 判定窓(±100ms)を過ぎるまで進める。入力は一切与えない。
            judge.Update(1.3f, new List<Contact>());

            Check("Tap 未入力タイムアウト -> MISS", judge.Score.miss == 1 && rt.state == NoteState.Missed);
        }

        private void TestExTapAllPerfectWithinGoodWindow()
        {
            var n = SingleWaypointNote(NoteKind.ExTap, 1.0f, Layer.Ground, 3);
            var rt = new NoteRuntime { note = n };
            var judge = new Judge(Cfg(), (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { rt });

            // 83ms遅れ(通常TapならGOOD相当)でも Ex Tap は judgeProfile=AllPerfect なので PERFECT+ になる
            judge.OnEnter(new EnterEvent { layer = Layer.Ground, cell = 3, fresh = true, at = 1.083f, cellF = 3f, layerF = 0f }, 1.083f);

            Check("ExTap 83ms遅れ -> PERFECT+ (AllPerfect)", judge.Score.perfectPlus == 1 && judge.Score.good == 0);
        }

        private void TestSlideComboResolution()
        {
            // 静止したSlide(旧Hold相当)。comboTimesはChartFormat.ResolveSlideComboPointsを介さず直接与える
            // （ここではJudge側のコンボ点消化ロジックだけを確認する）。
            var slide = new Note
            {
                kind = NoteKind.Slide,
                points = new List<Waypoint>
                {
                    new() { time = 1.0f, layerF = 0f, cellF = 3f, width = 2f },
                    new() { time = 2.0f, layerF = 0f, cellF = 3f, width = 2f },
                },
                comboTimes = new List<float> { 1.5f, 2.0f },
            };
            var rt = new NoteRuntime { note = slide };
            var judge = new Judge(Cfg(), (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { rt });

            // 始点はTapと同じ枠内更新で駆動(§0.2)
            judge.OnEnter(new EnterEvent { layer = Layer.Ground, cell = 3, fresh = true, at = 1.0f, cellF = 3f, layerF = 0f }, 1.0f);
            Check("Slide始点 -> Active", rt.state == NoteState.Active);

            // 押しっぱなし: 帯の内側(cellF=3, layerF=0)を維持したままUpdateを回す
            var contacts = new List<Contact> { new() { cellF = 3f, layerF = 0f } };
            for (float t = 1.0f; t <= 2.2f; t += 0.05f)
                judge.Update(t, contacts);

            Check("Slide 押しっぱなし -> 始点+コンボ点2つが全てPERFECT+ (計3)",
                judge.Score.perfectPlus == 3 && rt.state == NoteState.Hit);
        }

        private void TestFlickHit()
        {
            var n = SingleWaypointNote(NoteKind.Flick, 1.0f, Layer.Ground, 3);
            var rt = new NoteRuntime { note = n };
            var judge = new Judge(Cfg(), (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { rt });

            var cfg = Cfg();
            float flickDistance = cfg.U / cfg.cells; // note-spec.md §4.2
            var contact = new Contact { cellF = 3f, layerF = 0f, u = flickDistance * 1.5f, v = 0f };
            contact.history.Add((0f, 0f, 0.9f)); // 0.1s前は原点 -> 閾値を超える移動

            judge.Update(1.0f, new List<Contact> { contact });

            Check("Flick 閾値超過移動 -> PERFECT+ (即着地)", judge.Score.perfectPlus == 1 && rt.state == NoteState.Hit);
        }

        /// <summary>note-spec.md §6.4「Ex Tap 巻き込みルール」（rev.7）。同時刻・セル範囲が交差する
        /// Ex Tap があれば、実際にはそのExへ触れていない入力でも交差する Tap は PERFECT+ になる。</summary>
        private void TestExBoostAppliesToOverlappingTap()
        {
            var tap = SingleWaypointNote(NoteKind.Tap, 1.0f, Layer.Ground, 3, width: 2f); // [3,5)
            var ex = SingleWaypointNote(NoteKind.ExTap, 1.0f, Layer.Ground, 4, width: 2f); // [4,6): 3とだけ交差
            var rtTap = new NoteRuntime { note = tap };
            var rtEx = new NoteRuntime { note = ex };
            var judge = new Judge(Cfg(), (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { rtTap, rtEx });

            // 83ms遅れ(通常TapならGOOD相当)。cell=3はTapのみに触れ、Exの範囲[4,6)には触れていない。
            judge.OnEnter(new EnterEvent { layer = Layer.Ground, cell = 3, fresh = true, at = 1.083f, cellF = 3f, layerF = 0f }, 1.083f);

            Check("Ex巻き込み: 実際にExへ触れなくても交差するTapはPERFECT+",
                judge.Score.perfectPlus == 1 && judge.Score.good == 0 && rtTap.state == NoteState.Hit);
        }

        /// <summary>
        /// note-spec.md §4.6（rev.7）。「地上Tap → 同地点でRiser → 空中Slide」の想定フロー。
        /// Riser成立でhandoffが記録され、終端層(空中)でのEnterEventが合成発火されて
        /// 後続Slide始点がJudgeの構造を変えずに引き継げることを確認する。
        /// </summary>
        private void TestRiserHandsOffToSlideStart()
        {
            var cfg = Cfg();
            var riser = new Note
            {
                kind = NoteKind.Riser,
                points = new List<Waypoint> { new() { time = 1.0f, layerF = 0f, layerTo = 1f, cellF = 3f, width = 2f } },
            };
            // Riser成立直後(50ms後)に始まる空中Slide。handoffWindowMs(仮200ms)の中に収まる。
            var slide = new Note
            {
                kind = NoteKind.Slide,
                points = new List<Waypoint>
                {
                    new() { time = 1.05f, layerF = 1f, cellF = 3f, width = 2f },
                    new() { time = 1.55f, layerF = 1f, cellF = 3f, width = 2f },
                },
                comboTimes = new List<float> { 1.55f },
            };
            var rtRiser = new NoteRuntime { note = riser };
            var rtSlide = new NoteRuntime { note = slide };
            var judge = new Judge(cfg, (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { rtRiser, rtSlide });

            // note-spec.md §4.6.2: 閾値は絶対layerF 0.5相当のΔv(既定riserReachFrac=1.0)。
            // 指はRiserの枠内(layerF=0付近)から、閾値をわずかに超える分だけ上向きにvを動かす。
            float threshold = 0.5f * MathF.Abs(cfg.vSkyJudge - cfg.vGroundJudge) * cfg.riserReachFrac;
            var contact = new Contact { cellF = 3f, layerF = 0f, u = 0f, v = cfg.vGroundJudge };
            contact.history.Add((0f, cfg.vGroundJudge - (threshold + 0.01f), 0.9f));

            judge.Update(1.0f, new List<Contact> { contact });

            Check("Riser 閾値超過移動 -> PERFECT+ (即着地)、handoff記録",
                judge.Score.perfectPlus >= 1 && rtRiser.state == NoteState.Hit &&
                contact.layerHandoffUntil > 1.0f && contact.layerHandoffTo == 1f);
            Check("Riser handoff -> 後続Slide始点がEnterEvent合成でActiveへ引き継がれる",
                rtSlide.state == NoteState.Active);
        }

        private void TestSeekSkipsPastNotesWithoutScoring()
        {
            var n1 = SingleWaypointNote(NoteKind.Tap, 1.0f, Layer.Ground, 3);
            var n2 = SingleWaypointNote(NoteKind.Tap, 5.0f, Layer.Ground, 3);
            var rt1 = new NoteRuntime { note = n1 };
            var rt2 = new NoteRuntime { note = n2 };
            var judge = new Judge(Cfg(), (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { rt1, rt2 });

            judge.Seek(3.0f); // n1(t=1.0)は通り過ぎた地点へジャンプ、n2(t=5.0)はまだ先

            Check("Seek: 通り過ぎたノーツはHit扱い・スコア加算なし",
                rt1.state == NoteState.Hit && judge.Score.perfectPlus == 0 && judge.Score.miss == 0);
            Check("Seek: 未到達のノーツはPendingのまま", rt2.state == NoteState.Pending);
        }
    }
}
