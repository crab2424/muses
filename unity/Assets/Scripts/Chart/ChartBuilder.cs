using System;
using System.Collections.Generic;
using Muses.Stage;

namespace Muses.Chart
{
    /// <summary>
    /// デモ譜面。移植元: web-prototype/src/chart.ts の buildDemoChart()。
    /// ステージUIの見え方を確認するのが目的の32拍ループ譜面（数式・構成はそのまま移植）。
    /// note-spec.md rev.4 のデータモデル（Waypoint[] + kind プリセット、tick 空間）に合わせて再構成。
    /// 旧 Hold/Arc は Slide に統合。ExTap を1箇所デモとして追加した。
    /// </summary>
    public static class ChartBuilder
    {
        public static ChartData BuildDemoChart(float bpm, float durationSec, int cells)
        {
            const int loopBeats = 32;
            float b = 60f / bpm; // 秒/拍（ループ回数の計算にのみ使う）
            float loopSec = loopBeats * b;
            int loops = (int)MathF.Ceiling(durationSec / loopSec);
            float S = cells / 12f;

            var chart = new ChartData();
            chart.bpmEvents.Add(new BpmEvent { tick = 0, bpm = bpm });

            float C(float c) => MathF.Max(0, MathF.Min(cells - 1, MathF.Round(c * S)));
            float W(float w) => MathF.Max(1, MathF.Min(cells, MathF.Round(w * S)));
            float F(float c) => c * S;
            int T(float beats) => (int)MathF.Round(beats * ChartData.TicksPerBeat);

            Waypoint WP(float beats, float layerF, float cellF, float width, Easing easing = Easing.Linear) => new()
            {
                tick = T(beats),
                layerF = layerF,
                cellF = cellF,
                width = width,
                easing = easing,
                marker = WaypointMarker.None,
                comboStep = null,
            };

            Note Tap1(float beats, Layer layer, float cell, float width, bool ex = false) => new()
            {
                kind = ex ? NoteKind.ExTap : NoteKind.Tap,
                points = new List<Waypoint> { WP(beats, layer == Layer.Sky ? 1f : 0f, cell, width) },
            };

            for (int L = 0; L < loops; L++)
            {
                // 旧実装の「3秒のリードイン」を拍数に換算（3秒 = 3 * bpm / 60 拍）
                float t0Beat = 3f * bpm / 60f + L * loopBeats;

                // 0-7拍: 地上の階段
                for (int i = 0; i < 8; i++)
                    chart.notes.Add(Tap1(t0Beat + i, Layer.Ground, C(1 + i), W(2)));

                // 8-11拍: 空中の8分刻み
                for (int i = 0; i < 8; i++)
                    chart.notes.Add(Tap1(t0Beat + 8 + i * 0.5f, Layer.Sky, C(i % 2 == 0 ? 1 : 8), W(3)));

                // 12-15拍: 両層同時押し。空中側は ExTap の見た目確認を兼ねる
                for (int i = 0; i < 4; i++)
                {
                    float beat = t0Beat + 12 + i;
                    chart.notes.Add(Tap1(beat, Layer.Ground, C(i), W(2)));
                    chart.notes.Add(Tap1(beat, Layer.Sky, C(11 - i), W(1), ex: true));
                }

                // 16-19拍: 地上の幅広 Slide（旧Hold相当）+ 空中の3連符
                chart.notes.Add(new Note
                {
                    kind = NoteKind.Slide,
                    points = new List<Waypoint>
                    {
                        WP(t0Beat + 16, 0f, C(4), W(4)),
                        WP(t0Beat + 20, 0f, C(4), W(4)),
                    },
                });
                for (int i = 0; i < 6; i++)
                    chart.notes.Add(Tap1(t0Beat + 16 + i * 4f / 6f, Layer.Sky, C(i * 2), W(2)));

                // 20-27拍: 層をまたぐ Slide（旧Arc相当）2本。旧実装は smoothstep 固定だったので easing=Smooth で見た目を維持
                chart.notes.Add(new Note
                {
                    kind = NoteKind.Slide,
                    points = new List<Waypoint>
                    {
                        WP(t0Beat + 20, 0f, F(1.5f), 1.2f * S, Easing.Smooth),
                        WP(t0Beat + 22, 0f, F(4.5f), 1.2f * S, Easing.Smooth),
                        WP(t0Beat + 24, 1f, F(7.5f), 1.2f * S, Easing.Smooth),
                        WP(t0Beat + 26, 1f, F(9.5f), 1.2f * S, Easing.Smooth),
                        WP(t0Beat + 28, 0f, F(6.5f), 1.2f * S, Easing.Smooth),
                    },
                });
                chart.notes.Add(new Note
                {
                    kind = NoteKind.Slide,
                    points = new List<Waypoint>
                    {
                        WP(t0Beat + 21, 1f, F(10.5f), 1.2f * S, Easing.Smooth),
                        WP(t0Beat + 24, 0.5f, F(5.5f), 1.2f * S, Easing.Smooth),
                        WP(t0Beat + 27, 0f, F(0.5f), 1.2f * S, Easing.Smooth),
                    },
                });

                // 28-31拍: 地上の16分の詰め
                for (int i = 0; i < 16; i++)
                    chart.notes.Add(Tap1(t0Beat + 28 + i * 0.25f, Layer.Ground, C((i % 6) * 2), W(2)));
            }

            chart.notes.Sort((a, c2) => a.points[0].tick.CompareTo(c2.points[0].tick));
            ChartFormat.ResolveTimes(chart);
            ChartFormat.ResolveSlideComboPoints(chart);
            return chart;
        }
    }
}
