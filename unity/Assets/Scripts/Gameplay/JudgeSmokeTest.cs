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
            // gameplay-feel-r2.md
            TestSkyReachPicksNearestOneNote();
            TestSkyReachTiePrefersNativeLayer();
            TestRiserStartedAboveJudgeLine();
            TestRiserBestInWindow();
            TestRiserEarlyOnlyIsEarlyGood();
            TestRiserTouchedWithoutSwipeIsMiss();
            TestRiserNotCutByChain();
            TestRiserLateShift();
            TestResultCategoryAndEarlyLate();
            // note-feel-r3
            TestSlideStartNeedsEnter();
            TestSlideStartAllPerfect();
            TestDualLayerTapTakesOtherPanel();
            TestDualLayerChainCutsSkyTap();
            TestHandoffDoesNotTakeDualTap();
            TestRiserWindowExtend();

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

        /// <summary>note-feel-r3: Slide 始点は枠内更新駆動。始点の位置で OnEnter を1回呼ぶ。</summary>
        private static void TapSlideStart(Judge judge, Note slide, float at)
        {
            var wp = slide.points[0];
            judge.OnEnter(new EnterEvent
            {
                layer = wp.layerF > 0.5f ? Layer.Sky : Layer.Ground, cell = (int)MathF.Round(wp.cellF), fresh = true,
                at = at, cellF = wp.cellF + 0.5f, layerF = wp.layerF,
            }, at);
        }

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

            // note-feel-r3: 始点は枠内更新で叩き、以降は帯の内側に居続ける。
            var contacts = new List<Contact> { new() { cellF = 3f, layerF = 0f } };
            for (float t = 0.9f; t <= 2.2f; t += 0.05f)
            {
                if (MathF.Abs(t - 1.0f) < 0.025f) TapSlideStart(judge, slide, t);
                judge.Update(t, contacts);
            }

            Check("Slide 始点を叩いて押しっぱなし -> 始点+コンボ点2つが全てPERFECT+ (計3)",
                judge.Score.perfectPlus == 3 && rt.state == NoteState.Hit);
            Check("ComboPointCount(Slide) = comboTimes+始点 = 3", ChartMath.ComboPointCount(slide) == 3);
        }

        /// <summary>note-feel-r3（ユーザー判断で gameplay-feel-r1.md §2.1 を撤回）。前のSlideの終点と次のSlideの始点が
        /// 同じ位置でも、押し続けるだけでは次の始点は取れない（押し直しが必要）。</summary>
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
            {
                if (MathF.Abs(t - 1.0f) < 0.005f) TapSlideStart(judge, rt1.note, t);
                judge.Update(t, contacts);
            }

            Check("連続Slide: 押しっぱなしでは次の始点はMISS（PERFECT+ 3 / MISS 1）",
                judge.Score.perfectPlus == 3 && judge.Score.miss == 1);
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
                if (MathF.Abs(t - 1.0f) < 0.004f) TapSlideStart(judge, slide, t);
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
            judge.OnEnter(new EnterEvent { layer = Layer.Sky, cell = 4, fresh = true, at = 1.0f, cellF = 4f, layerF = 1.8f }, 1.0f);
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
            TapSlideStart(judge, slide, 1.0f);
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

            // note-feel-r3: 後続Slide始点は枠内更新駆動。Riser 成立時に合成される空中の EnterEvent（判定時刻 1.0）で
            // 始点(1.05)が −50ms・Ex Tap 扱いの PERFECT+ になる。
            for (float t = 1.01f; t <= 1.15f; t += 0.01f) judge.Update(t, contacts);
            Check("Riser handoff -> 後続Slide始点が合成EnterEventで成立",
                rtSlide.state == NoteState.Active && rtSlide.startResolved && judge.Score.perfectPlus == 2);
        }

        // ================= gameplay-feel-r2.md =================

        private static EnterEvent Enter(Layer layer, int cell, float at, bool skyReach = false) => new()
        {
            layer = layer, cell = cell, fresh = true, at = at, cellF = cell, layerF = layer == Layer.Sky ? 1f : 0f,
            skyReach = skyReach,
        };

        /// <summary>§3。重なり帯の接触は両層の候補から |dt| 最小の1つだけを取る（地上Tapは残る）。</summary>
        private void TestSkyReachPicksNearestOneNote()
        {
            var ground = new NoteRuntime { note = SingleWaypointNote(NoteKind.Tap, 1.0f, Layer.Ground, 3) };
            var sky = new NoteRuntime { note = SingleWaypointNote(NoteKind.Tap, 1.05f, Layer.Sky, 3) };
            var judge = new Judge(Cfg(), (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { ground, sky });

            judge.OnEnter(Enter(Layer.Ground, 3, 1.04f, skyReach: true), 1.04f);

            Check("重なり帯: 近い空中Tapだけ取る（1タッチ1ノーツ）",
                sky.state == NoteState.Hit && ground.state == NoteState.Pending && judge.Score.perfectPlus == 1);

            var judge2 = new Judge(Cfg(), (r, a) => { });
            var sky2 = new NoteRuntime { note = SingleWaypointNote(NoteKind.Tap, 1.0f, Layer.Sky, 3) };
            judge2.Prepare(new List<NoteRuntime> { sky2 });
            judge2.OnEnter(Enter(Layer.Ground, 3, 1.0f, skyReach: false), 1.0f);
            Check("重なり帯の外（skyReach=false）の地上接触では空中Tapは取れない", sky2.state == NoteState.Pending);
        }

        /// <summary>§3。|dt| が同じ（地上と空中の同時押し）なら接触の本来の層だけを取る。もう一方は2本目の指が要る。</summary>
        private void TestSkyReachTiePrefersNativeLayer()
        {
            var ground = new NoteRuntime { note = SingleWaypointNote(NoteKind.Tap, 1.0f, Layer.Ground, 3) };
            var sky = new NoteRuntime { note = SingleWaypointNote(NoteKind.Tap, 1.0f, Layer.Sky, 3) };
            var judge = new Judge(Cfg(), (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { sky, ground });

            judge.OnEnter(Enter(Layer.Ground, 3, 1.0f, skyReach: true), 1.0f);

            Check("重なり帯: 同時押しは本来の層(地上)だけ",
                ground.state == NoteState.Hit && sky.state == NoteState.Pending && judge.Score.perfectPlus == 1);
        }

        private static Note RiserNote(float time, float cell = 3f, bool up = true) => new()
        {
            kind = NoteKind.Riser,
            points = new List<Waypoint>
            {
                new() { time = time, layerF = up ? 0f : 1f, layerTo = up ? 1f : 0f, cellF = cell, width = 2f },
            },
        };

        /// <summary>画面座標(u,v)を連続座標(cellF,layerF)と整合させた接触。</summary>
        private static Contact ContactAt(StageConfig cfg, float cellF, float layerF) => new()
        {
            u = cellF * 2f * cfg.U / cfg.cells - cfg.U,
            v = cfg.vGroundJudge + layerF * (cfg.vSkyJudge - cfg.vGroundJudge),
            cellF = cellF,
            layerF = layerF,
        };

        private static void MoveTo(StageConfig cfg, Contact c, float layerF, float time)
        {
            c.v = cfg.vGroundJudge + layerF * (cfg.vSkyJudge - cfg.vGroundJudge);
            c.layerF = layerF;
            c.history.Add((c.u, c.v, time));
            while (c.history.Count > 0 && c.history[0].t < time - cfg.flickWindowMs / 1000f) c.history.RemoveAt(0);
        }

        /// <summary>1ストローク: 始点 fromLayerF から toLayerF まで dur 秒で等速に擦る（8ms刻み）。各フレームで judge.Update。</summary>
        private static void Stroke(Judge judge, StageConfig cfg, Contact c, float t0, float dur, float fromLayerF, float toLayerF)
        {
            for (float t = t0; t <= t0 + dur + 1e-4f; t += 0.008f)
            {
                MoveTo(cfg, c, fromLayerF + (toLayerF - fromLayerF) * Math.Clamp((t - t0) / dur, 0f, 1f), t);
                judge.Update(t, new List<Contact> { c });
            }
        }

        /// <summary>§4.1-1。判定線より上(layerF 0.2)から擦り始めても成立する（旧: 判定域を出てから閾値に届くので救済GOOD）。</summary>
        private void TestRiserStartedAboveJudgeLine()
        {
            var cfg = Cfg();
            var rt = new NoteRuntime { note = RiserNote(1.0f) };
            var judge = new Judge(cfg, (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { rt });

            var c = ContactAt(cfg, 3.5f, 0.2f);
            Stroke(judge, cfg, c, 0.96f, 0.06f, 0.2f, 1.0f); // 60msで layerF 0.2→1.0（閾値=0.5相当の移動は途中で満たす）
            for (float t = 1.03f; t <= 1.3f; t += 0.008f) { MoveTo(cfg, c, 1.0f, t); judge.Update(t, new List<Contact> { c }); }

            Check("Riser 判定線より上から擦り始め -> PERFECT+",
                rt.state == NoteState.Hit && judge.Score.perfectPlus == 1 && judge.Score.good == 0);
        }

        /// <summary>§4.2 窓内最良。早い反応(−80ms=GOOD相当)の後、ノーツ時刻付近で再度擦れば PERFECT+ を採る。</summary>
        private void TestRiserBestInWindow()
        {
            var cfg = Cfg();
            var rt = new NoteRuntime { note = RiserNote(1.0f) };
            var judge = new Judge(cfg, (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { rt });

            var c = ContactAt(cfg, 3.5f, 0f);
            Stroke(judge, cfg, c, 0.84f, 0.04f, 0f, 0.8f);                   // 反応 ≒ 0.865 (−135ms、ext 50 で GOOD)
            for (float t = 0.89f; t < 0.98f; t += 0.008f) { MoveTo(cfg, c, 0f, t); judge.Update(t, new List<Contact> { c }); } // 下へ戻す
            bool pendingAfterEarly = rt.state == NoteState.Pending;
            Stroke(judge, cfg, c, 0.98f, 0.03f, 0f, 0.8f);                   // 反応 ≒ 1.00
            for (float t = 1.02f; t <= 1.3f; t += 0.008f) judge.Update(t, new List<Contact> { c });

            Check("Riser 窓内最良: 早GOODの反応では確定せず、後のPERFECT+を採る",
                pendingAfterEarly && judge.Score.perfectPlus == 1 && judge.Score.good == 0);
        }

        /// <summary>§4.2。早く擦り切って、その後 PERFECT 窓で反応が無ければ早 GOOD（指を止めていても反応は伸びない）。</summary>
        private void TestRiserEarlyOnlyIsEarlyGood()
        {
            var cfg = Cfg();
            var rt = new NoteRuntime { note = RiserNote(1.0f) };
            var judge = new Judge(cfg, (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { rt });

            var c = ContactAt(cfg, 3.5f, 0f);
            Stroke(judge, cfg, c, 0.84f, 0.04f, 0f, 0.8f); // 反応 ≒ 0.865 (−135ms、ext 50 で GOOD)
            for (float t = 0.89f; t <= 1.3f; t += 0.008f) { MoveTo(cfg, c, 0.8f, t); judge.Update(t, new List<Contact> { c }); } // 止めたまま

            Check("Riser 早い反応のみ -> GOOD かつ EARLY",
                judge.Score.good == 1 && judge.Score.early == 1 && judge.Score.perfectPlus == 0);
        }

        /// <summary>§4.2。判定域に触れていても擦りが成立しなければ MISS（旧 §4.4 の救済GOODは Riser では廃止）。</summary>
        private void TestRiserTouchedWithoutSwipeIsMiss()
        {
            var cfg = Cfg();
            var rt = new NoteRuntime { note = RiserNote(1.0f) };
            var judge = new Judge(cfg, (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { rt });

            var c = ContactAt(cfg, 3.5f, 0f);
            for (float t = 0.9f; t <= 1.3f; t += 0.008f) { MoveTo(cfg, c, 0f, t); judge.Update(t, new List<Contact> { c }); }

            Check("Riser 触れただけ -> MISS（救済GOODなし）", judge.Score.miss == 1 && judge.Score.good == 0);
        }

        /// <summary>§4.2。同層・同セルの Tap が直後(+60ms)にあっても Riser の窓は削られない（旧: hi=+30msで切れた）。</summary>
        private void TestRiserNotCutByChain()
        {
            var cfg = Cfg();
            var riser = new NoteRuntime { note = RiserNote(1.0f) };
            var tap = new NoteRuntime { note = SingleWaypointNote(NoteKind.Tap, 1.06f, Layer.Ground, 3) };
            var judge = new Judge(cfg, (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { riser, tap });

            var c = ContactAt(cfg, 3.5f, 0f);
            Stroke(judge, cfg, c, 1.0f, 0.05f, 0f, 0.8f); // 反応 ≒ +50ms（旧実装なら縦連で窓の外）

            Check("Riser 直後に同セルTapがあっても窓が削られない", riser.state == NoteState.Hit && judge.Score.perfectPlus >= 1);
        }

        /// <summary>§4.2 + note-feel-r3。遅い側は riserLateShiftMs(50)＋riserWindowExtendMs(50) ずらす:
        /// +130ms は PERFECT+、+150ms は PERFECT(LATE)。</summary>
        private void TestRiserLateShift()
        {
            var cfg = Cfg();
            JudgeKind? Run(float reactAt)
            {
                var rt = new NoteRuntime { note = RiserNote(1.0f) };
                var judge = new Judge(cfg, (r, a) => { });
                judge.Prepare(new List<NoteRuntime> { rt });
                var c = ContactAt(cfg, 3.5f, 0f);
                for (float t = 0.9f; t < reactAt - 0.03f; t += 0.008f) { MoveTo(cfg, c, 0f, t); judge.Update(t, new List<Contact> { c }); }
                Stroke(judge, cfg, c, reactAt - 0.03f, 0.03f, 0f, 0.8f);
                for (float t = reactAt + 0.008f; t <= 1.3f; t += 0.008f) judge.Update(t, new List<Contact> { c });
                var s = judge.Score;
                return s.perfectPlus == 1 ? JudgeKind.PerfectPlus : s.perfect == 1 && s.late == 1 ? JudgeKind.Perfect
                    : s.good == 1 ? JudgeKind.Good : s.miss == 1 ? JudgeKind.Miss : null;
            }
            Check("Riser 遅い側延長: +130ms -> PERFECT+", Run(1.13f) == JudgeKind.PerfectPlus);
            Check("Riser 遅い側延長: +150ms -> PERFECT (LATE)", Run(1.15f) == JudgeKind.Perfect);
        }

        /// <summary>§6/§2。内訳は種別×層で数え、EARLY/LATE は PERFECT/GOOD のみ。</summary>
        private void TestResultCategoryAndEarlyLate()
        {
            var sky = new NoteRuntime { note = SingleWaypointNote(NoteKind.Tap, 1.0f, Layer.Sky, 3) };
            var ground = new NoteRuntime { note = SingleWaypointNote(NoteKind.Tap, 2.0f, Layer.Ground, 3) };
            var judge = new Judge(Cfg(), (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { sky, ground });

            judge.OnEnter(Enter(Layer.Sky, 3, 0.95f), 0.95f);   // −50ms: PERFECT / EARLY
            judge.OnEnter(Enter(Layer.Ground, 3, 2.01f), 2.01f); // +10ms: PERFECT+ （EARLY/LATEは数えない）

            var s = judge.Score;
            var tapSky = s.byCategory[(int)ResultCategory.TapSky];
            var tapGround = s.byCategory[(int)ResultCategory.TapGround];
            Check("内訳: Tap空中にPERFECT(EARLY)、Tap地上にPERFECT+",
                tapSky.perfect == 1 && tapSky.early == 1 && tapGround.perfectPlus == 1 &&
                tapGround.early + tapGround.late == 0 && s.early == 1 && s.late == 0);
        }

        // ================= note-feel-r3 =================

        private static Note StillSlide(float t0, float t1, Layer layer = Layer.Ground) => new()
        {
            kind = NoteKind.Slide,
            points = new List<Waypoint>
            {
                new() { time = t0, layerF = layer == Layer.Sky ? 1f : 0f, cellF = 3f, width = 2f },
                new() { time = t1, layerF = layer == Layer.Sky ? 1f : 0f, cellF = 3f, width = 2f },
            },
            comboTimes = new List<float> { t1 },
        };

        /// <summary>Slide 始点は押しっぱなしでは拾わない（枠内更新が無ければ始点は MISS、コンボ点は占有で取れる）。</summary>
        private void TestSlideStartNeedsEnter()
        {
            var rt = new NoteRuntime { note = StillSlide(1.0f, 1.5f) };
            var judge = new Judge(Cfg(), (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { rt });
            var contacts = new List<Contact> { new() { cellF = 4f, layerF = 0f } };
            for (float t = 0.8f; t <= 1.7f; t += 0.01f) judge.Update(t, contacts);
            Check("Slide始点: 押しっぱなしだけでは始点MISS・終点PERFECT+",
                judge.Score.miss == 1 && judge.Score.perfectPlus == 1);
        }

        /// <summary>Slide 始点は Ex Tap と同じ: ±100ms 内の枠内更新はすべて PERFECT+、EARLY/LATE は出ない。</summary>
        private void TestSlideStartAllPerfect()
        {
            var rt = new NoteRuntime { note = StillSlide(1.0f, 1.5f) };
            var judge = new Judge(Cfg(), (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { rt });
            judge.Update(0.95f, new List<Contact>());
            TapSlideStart(judge, rt.note, 1.08f); // +80ms
            Check("Slide始点: +80ms でも PERFECT+（Ex Tap と同じ）",
                rt.startResolved && judge.Score.perfectPlus == 1 && judge.Score.late == 0);
        }

        /// <summary>Riser と重なる地上 Tap は空中パネルの接触でも取れる。</summary>
        private void TestDualLayerTapTakesOtherPanel()
        {
            var tap = new NoteRuntime { note = SingleWaypointNote(NoteKind.Tap, 1.0f, Layer.Ground, 3) };
            var riser = new NoteRuntime { note = RiserNote(1.0f) };
            var judge = new Judge(Cfg(), (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { tap, riser });
            judge.OnEnter(Enter(Layer.Sky, 3, 1.0f), 1.0f);
            Check("Tap+Riser: 空中パネルの接触で地上Tapが取れる", tap.dualLayer && tap.state == NoteState.Hit);

            var lone = new NoteRuntime { note = SingleWaypointNote(NoteKind.Tap, 1.0f, Layer.Ground, 3) };
            var judge2 = new Judge(Cfg(), (r, a) => { });
            judge2.Prepare(new List<NoteRuntime> { lone });
            judge2.OnEnter(Enter(Layer.Sky, 3, 1.0f), 1.0f);
            Check("Riser の無い地上Tapは空中パネルでは取れない", !lone.dualLayer && lone.state == NoteState.Pending);
        }

        /// <summary>両層の Tap は直前の空中 Tap と縦連になり、空中 Tap の遅い側が中点で切られる。</summary>
        private void TestDualLayerChainCutsSkyTap()
        {
            var sky = new NoteRuntime { note = SingleWaypointNote(NoteKind.Tap, 0.9f, Layer.Sky, 3) };
            var tap = new NoteRuntime { note = SingleWaypointNote(NoteKind.Tap, 1.0f, Layer.Ground, 3) };
            var riser = new NoteRuntime { note = RiserNote(1.0f) };
            var judge = new Judge(Cfg(), (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { sky, tap, riser });
            judge.Update(0.9f, new List<Contact>());
            judge.OnEnter(Enter(Layer.Sky, 3, 0.96f), 0.96f); // 空中Tapの窓は中点0.95で切れている
            Check("Tap+Riser: 直前の空中Tapは縦連で切られ、+60msの空中の接触は地上Tapへ",
                tap.state == NoteState.Hit && sky.state == NoteState.Pending);
        }

        /// <summary>Riser 成立時の合成イベントでは両層 Tap を取らない（擦るだけで Tap まで取れないように）。</summary>
        private void TestHandoffDoesNotTakeDualTap()
        {
            var cfg = Cfg();
            var tap = new NoteRuntime { note = SingleWaypointNote(NoteKind.Tap, 1.0f, Layer.Ground, 3) };
            var riser = new NoteRuntime { note = RiserNote(1.0f) };
            var judge = new Judge(cfg, (r, a) => { });
            judge.Prepare(new List<NoteRuntime> { tap, riser });
            var c = ContactAt(cfg, 3.5f, 0f);
            Stroke(judge, cfg, c, 0.97f, 0.04f, 0f, 0.8f);
            for (float t = 1.02f; t <= 1.3f; t += 0.008f) judge.Update(t, new List<Contact> { c });
            Check("Tap+Riser: 擦るだけなら Riser は成立、Tap は MISS",
                riser.state == NoteState.Hit && tap.state == NoteState.Missed);
        }

        /// <summary>Riser の PERFECT+ 窓を ±50ms 広げた: −80ms は PERFECT+、−110ms は PERFECT(EARLY)。</summary>
        private void TestRiserWindowExtend()
        {
            var cfg = Cfg();
            JudgeKind? Run(float reactAt)
            {
                var rt = new NoteRuntime { note = RiserNote(1.0f) };
                var judge = new Judge(cfg, (r, a) => { });
                judge.Prepare(new List<NoteRuntime> { rt });
                var c = ContactAt(cfg, 3.5f, 0f);
                for (float t = 0.8f; t < reactAt - 0.016f; t += 0.008f) { MoveTo(cfg, c, 0f, t); judge.Update(t, new List<Contact> { c }); }
                // 2フレームで閾値を超える（反応時刻 ≒ reactAt）
                MoveTo(cfg, c, 0.3f, reactAt - 0.008f); judge.Update(reactAt - 0.008f, new List<Contact> { c });
                MoveTo(cfg, c, 0.8f, reactAt); judge.Update(reactAt, new List<Contact> { c });
                for (float t = reactAt + 0.008f; t <= 1.3f; t += 0.008f) { MoveTo(cfg, c, 0.8f, t); judge.Update(t, new List<Contact> { c }); }
                var s = judge.Score;
                return s.perfectPlus == 1 ? JudgeKind.PerfectPlus : s.perfect == 1 && s.early == 1 ? JudgeKind.Perfect
                    : s.good == 1 ? JudgeKind.Good : s.miss == 1 ? JudgeKind.Miss : null;
            }
            Check("Riser 窓拡張: −80ms -> PERFECT+", Run(0.92f) == JudgeKind.PerfectPlus);
            Check("Riser 窓拡張: −110ms -> PERFECT (EARLY)", Run(0.89f) == JudgeKind.Perfect);
            Check("Riser 窓拡張: −140ms -> GOOD", Run(0.86f) == JudgeKind.Good);
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
