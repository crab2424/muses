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
            TestSlideStartByHoldingFromPreviousSlide();
            TestSlideLateralLagIsTolerated();
            TestSkyTopRegionCountsForSlide();
            TestSlideComboPointConfirmsEarly();

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

            // gameplay-feel-r1.md §2: 始点も占有駆動。枠内更新(OnEnter)は不要で、帯の内側に居続ければよい。
            var contacts = new List<Contact> { new() { cellF = 3f, layerF = 0f } };
            for (float t = 0.9f; t <= 2.2f; t += 0.05f)
                judge.Update(t, contacts);

            Check("Slide 押しっぱなし -> 始点+コンボ点2つが全てPERFECT+ (計3)",
                judge.Score.perfectPlus == 3 && rt.state == NoteState.Hit);
            Check("ComboPointCount(Slide) = comboTimes+始点 = 3", ChartMath.ComboPointCount(slide) == 3);
        }

        /// <summary>gameplay-feel-r1.md §2.1。前のSlideの終点と次のSlideの始点が同じ位置のとき、
        /// 指を動かさずに押し続けても次の始点が成立する（旧仕様では枠内更新が無いためMISSだった）。</summary>
        private void TestSlideStartByHoldingFromPreviousSlide()
        {
            Note Hold(float t0, float t1) => new()
            {
                kind = NoteKind.Slide,
                points = new List<Waypoint>
                {
                    new() { time = t0, layerF = 0f, cellF = 3f, width = 2f },
                    new() { time = t1, layerF = 0f, cellF = 3f, width = 2f },
                },
                comboTimes = new List<float> { t1 },
            };
            var rt1 = new NoteRuntime { note = Hold(1.0f, 1.5f) };
            var rt2 = new NoteRuntime { note = Hold(1.5f, 2.0f) };
            var judge = new Judge(Cfg(), (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { rt1, rt2 });

            var contacts = new List<Contact> { new() { cellF = 4f, layerF = 0f } };
            for (float t = 0.9f; t <= 2.2f; t += 0.01f)
                judge.Update(t, contacts);

            Check("連続Slide: 押しっぱなしで次の始点も成立 (4点全てPERFECT+)",
                judge.Score.perfectPlus == 4 && judge.Score.miss == 0);
        }

        /// <summary>gameplay-feel-r1.md §1.2。横に速く動くSlideを、指が80ms遅れて追いかけても落ちない。</summary>
        private void TestSlideLateralLagIsTolerated()
        {
            // 0.5秒で cellF 0→8 (16セル/秒)。幅1。指は帯の80ms前の位置にいる（=1.28セル遅れ、帯の外）。
            var slide = new Note
            {
                kind = NoteKind.Slide,
                points = new List<Waypoint>
                {
                    new() { time = 1.0f, layerF = 0f, cellF = 0f, width = 1f },
                    new() { time = 1.5f, layerF = 0f, cellF = 8f, width = 1f },
                },
                comboTimes = new List<float> { 1.25f, 1.5f },
            };
            var rt = new NoteRuntime { note = slide };
            var judge = new Judge(Cfg(), (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { rt });

            var c = new Contact { layerF = 0f };
            var contacts = new List<Contact> { c };
            for (float t = 0.9f; t <= 1.7f; t += 0.008f)
            {
                c.cellF = ChartMath.At(slide, t - 0.08f).cellF + 0.5f; // 80ms前の帯の中央
                judge.Update(t, contacts);
            }

            Check("横移動Slideを80ms遅れで追従 -> MISSなし",
                judge.Score.miss == 0 && judge.Score.perfectPlus == 3);
        }

        /// <summary>gameplay-feel-r1.md §1.3。空中パネル上部（layerF&gt;1.5相当、v&gt;0.6）でも空中Slideが取れる。</summary>
        private void TestSkyTopRegionCountsForSlide()
        {
            var slide = new Note
            {
                kind = NoteKind.Slide,
                points = new List<Waypoint>
                {
                    new() { time = 1.0f, layerF = 1f, cellF = 3f, width = 2f },
                    new() { time = 1.5f, layerF = 1f, cellF = 3f, width = 2f },
                },
                comboTimes = new List<float> { 1.5f },
            };
            var rt = new NoteRuntime { note = slide };
            var judge = new Judge(Cfg(), (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { rt });

            var contacts = new List<Contact> { new() { cellF = 4f, layerF = 1.8f } }; // 画面上端付近
            for (float t = 0.9f; t <= 1.7f; t += 0.01f)
                judge.Update(t, contacts);

            Check("空中パネル上部でも空中Slideが成立", judge.Score.perfectPlus == 2 && judge.Score.miss == 0);
        }

        /// <summary>gameplay-feel-r1.md §2.5。押しっぱなしならコンボ点は t_p 直後に確定する（+100ms待たない）。</summary>
        private void TestSlideComboPointConfirmsEarly()
        {
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

            var contacts = new List<Contact> { new() { cellF = 3f, layerF = 0f } };
            float t = 0.9f;
            for (; t <= 1.51f; t += 0.01f) judge.Update(t, contacts);
            // この時点で t≈1.51。始点(1.0)とコンボ点(1.5)の2つが確定済みのはず（旧実装では1.6まで待った）
            Check("コンボ点の早期確定: t_p直後に確定", judge.Score.perfectPlus == 2);
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

            var contacts = new List<Contact> { contact };
            judge.Update(1.0f, contacts);

            Check("Riser 閾値超過移動 -> PERFECT+ (即着地)、handoff記録",
                judge.Score.perfectPlus >= 1 && rtRiser.state == NoteState.Hit &&
                contact.layerHandoffUntil > 1.0f && contact.layerHandoffTo == 1f);

            // gameplay-feel-r1.md §2.3: 後続Slide始点は占有駆動。指の実layerFは0のままでも、
            // handoff中(〜1.2s)は実効layerFが1とみなされるので始点(1.05)が成立する。
            for (float t = 1.01f; t <= 1.15f; t += 0.01f) judge.Update(t, contacts);
            Check("Riser handoff -> 後続Slide始点が実効layerFの読み替えで成立",
                rtSlide.state == NoteState.Active && rtSlide.startResolved && judge.Score.perfectPlus == 2);
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
