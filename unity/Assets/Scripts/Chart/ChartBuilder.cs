using System;
using System.Collections.Generic;
using Muses.Stage;

namespace Muses.Chart
{
    /// <summary>
    /// デモ譜面。移植元: web-prototype/src/chart.ts の buildDemoChart()。
    /// ステージUIの見え方を確認するのが目的の32拍ループ譜面（数式・構成はそのまま移植）。
    /// </summary>
    public static class ChartBuilder
    {
        public static List<Note> BuildDemoChart(float bpm, float durationSec, int cells)
        {
            float b = 60f / bpm;
            var notes = new List<Note>();
            const int loopBeats = 32;
            float loopSec = loopBeats * b;
            int loops = (int)MathF.Ceiling(durationSec / loopSec);
            float S = cells / 12f;

            int C(float c) => (int)MathF.Max(0, MathF.Min(cells - 1, MathF.Round(c * S)));
            int W(float w) => (int)MathF.Max(1, MathF.Min(cells, MathF.Round(w * S)));
            float F(float c) => c * S;

            for (int L = 0; L < loops; L++)
            {
                float t0 = 3f + L * loopSec;

                // 0-7拍: 地上の階段
                for (int i = 0; i < 8; i++)
                    notes.Add(Tap(t0 + i * b, Layer.Ground, C(1 + i), W(2)));

                // 8-11拍: 空中の8分刻み
                for (int i = 0; i < 8; i++)
                    notes.Add(Tap(t0 + (8 + i * 0.5f) * b, Layer.Sky, C(i % 2 == 0 ? 1 : 8), W(3)));

                // 12-15拍: 両層同時押し
                for (int i = 0; i < 4; i++)
                {
                    float t = t0 + (12 + i) * b;
                    notes.Add(Tap(t, Layer.Ground, C(i), W(2)));
                    notes.Add(Tap(t, Layer.Sky, C(11 - i), W(1)));
                }

                // 16-19拍: 地上の幅広ホールド + 空中の3連符
                notes.Add(new Note
                {
                    kind = NoteKind.Hold,
                    time = t0 + 16 * b,
                    endTime = t0 + 20 * b,
                    layer = Layer.Ground,
                    cell = C(4),
                    width = W(4),
                });
                for (int i = 0; i < 6; i++)
                    notes.Add(Tap(t0 + (16 + i * 4f / 6f) * b, Layer.Sky, C(i * 2), W(2)));

                // 20-27拍: 層をまたぐアーク2本
                notes.Add(new Note
                {
                    kind = NoteKind.Arc,
                    width = 1.2f * S,
                    points = new List<ArcPoint>
                    {
                        new() { time = t0 + 20 * b, layerF = 0f, cellF = F(1.5f) },
                        new() { time = t0 + 22 * b, layerF = 0f, cellF = F(4.5f) },
                        new() { time = t0 + 24 * b, layerF = 1f, cellF = F(7.5f) },
                        new() { time = t0 + 26 * b, layerF = 1f, cellF = F(9.5f) },
                        new() { time = t0 + 28 * b, layerF = 0f, cellF = F(6.5f) },
                    },
                });
                notes.Add(new Note
                {
                    kind = NoteKind.Arc,
                    width = 1.2f * S,
                    points = new List<ArcPoint>
                    {
                        new() { time = t0 + 21 * b, layerF = 1f, cellF = F(10.5f) },
                        new() { time = t0 + 24 * b, layerF = 0.5f, cellF = F(5.5f) },
                        new() { time = t0 + 27 * b, layerF = 0f, cellF = F(0.5f) },
                    },
                });

                // 28-31拍: 地上の16分の詰め
                for (int i = 0; i < 16; i++)
                    notes.Add(Tap(t0 + (28 + i * 0.25f) * b, Layer.Ground, C((i % 6) * 2), W(2)));
            }

            notes.Sort((a, c2) => ChartMath.NoteStart(a).CompareTo(ChartMath.NoteStart(c2)));
            return notes;
        }

        private static Note Tap(float time, Layer layer, int cell, float width) => new()
        {
            kind = NoteKind.Tap,
            time = time,
            layer = layer,
            cell = cell,
            width = width,
        };
    }
}
