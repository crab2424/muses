import { LAYER_GROUND, LAYER_SKY, type Layer } from './config';

export interface TapNote {
  kind: 'tap';
  time: number;
  layer: Layer;
  cell: number;
}

export interface HoldNote {
  kind: 'hold';
  time: number;
  endTime: number;
  layer: Layer;
  cell: number;
}

/** アーク制御点。layerF は 0=地上 / 1=空中 の連続値、cellF はセル番号の連続値 */
export interface ArcPoint {
  time: number;
  layerF: number;
  cellF: number;
}

export interface ArcNote {
  kind: 'arc';
  points: ArcPoint[];
}

export type Note = TapNote | HoldNote | ArcNote;

export function noteStart(n: Note): number {
  return n.kind === 'arc' ? n.points[0].time : n.time;
}

export function noteEnd(n: Note): number {
  if (n.kind === 'arc') return n.points[n.points.length - 1].time;
  if (n.kind === 'hold') return n.endTime;
  return n.time;
}

/** アークの時刻 t における位置を線形補間で求める */
export function arcAt(arc: ArcNote, t: number): { layerF: number; cellF: number } {
  const p = arc.points;
  if (t <= p[0].time) return { layerF: p[0].layerF, cellF: p[0].cellF };
  const last = p[p.length - 1];
  if (t >= last.time) return { layerF: last.layerF, cellF: last.cellF };
  for (let i = 0; i < p.length - 1; i++) {
    const a = p[i];
    const b = p[i + 1];
    if (t >= a.time && t <= b.time) {
      const k = b.time === a.time ? 0 : (t - a.time) / (b.time - a.time);
      // ease in-out（層間遷移をなめらかに）
      const e = k * k * (3 - 2 * k);
      return {
        layerF: a.layerF + (b.layerF - a.layerF) * e,
        cellF: a.cellF + (b.cellF - a.cellF) * e,
      };
    }
  }
  return { layerF: last.layerF, cellF: last.cellF };
}

/**
 * デモ譜面。ステージUIの見え方を確認するのが目的なので、
 * 「両層の同時押し」「層をまたぐアーク」「ホールド」を一通り含む 32 拍のループ。
 */
export function buildDemoChart(bpm: number, durationSec: number): Note[] {
  const b = 60 / bpm;
  const notes: Note[] = [];
  const loopBeats = 32;
  const loopSec = loopBeats * b;
  const loops = Math.ceil(durationSec / loopSec);

  for (let L = 0; L < loops; L++) {
    const t0 = 3 + L * loopSec; // 開始 3 秒の余白

    // 0–7 拍: 地上の階段
    for (let i = 0; i < 8; i++) {
      notes.push({ kind: 'tap', time: t0 + i * b, layer: LAYER_GROUND, cell: 2 + i });
    }
    // 8–11 拍: 空中の 8 分刻み（3 連ではなく 4 分割 → 12 マスの利点確認）
    for (let i = 0; i < 8; i++) {
      notes.push({
        kind: 'tap',
        time: t0 + (8 + i * 0.5) * b,
        layer: LAYER_SKY,
        cell: i % 2 === 0 ? 3 : 8,
      });
    }
    // 12–15 拍: 両層同時押し（左右対称）
    for (let i = 0; i < 4; i++) {
      const t = t0 + (12 + i) * b;
      notes.push({ kind: 'tap', time: t, layer: LAYER_GROUND, cell: 1 + i });
      notes.push({ kind: 'tap', time: t, layer: LAYER_SKY, cell: 10 - i });
    }
    // 16–19 拍: 地上ホールド + 空中の 3 連符（12 が 3 で割り切れる確認）
    notes.push({
      kind: 'hold',
      time: t0 + 16 * b,
      endTime: t0 + 20 * b,
      layer: LAYER_GROUND,
      cell: 5,
    });
    for (let i = 0; i < 6; i++) {
      notes.push({
        kind: 'tap',
        time: t0 + (16 + (i * 4) / 6) * b,
        layer: LAYER_SKY,
        cell: i * 2,
      });
    }
    // 20–27 拍: 層をまたぐアーク（地上 → 空中 → 地上）
    notes.push({
      kind: 'arc',
      points: [
        { time: t0 + 20 * b, layerF: 0, cellF: 1.5 },
        { time: t0 + 22 * b, layerF: 0, cellF: 4.5 },
        { time: t0 + 24 * b, layerF: 1, cellF: 7.5 },
        { time: t0 + 26 * b, layerF: 1, cellF: 9.5 },
        { time: t0 + 28 * b, layerF: 0, cellF: 6.5 },
      ],
    });
    // 20–27 拍: 反対側にもう1本（交差の見え方確認）
    notes.push({
      kind: 'arc',
      points: [
        { time: t0 + 21 * b, layerF: 1, cellF: 10.5 },
        { time: t0 + 24 * b, layerF: 0.5, cellF: 5.5 },
        { time: t0 + 27 * b, layerF: 0, cellF: 0.5 },
      ],
    });
    // 28–31 拍: 地上の 16 分の詰め（高速時の視認性確認）
    for (let i = 0; i < 16; i++) {
      notes.push({
        kind: 'tap',
        time: t0 + (28 + i * 0.25) * b,
        layer: LAYER_GROUND,
        cell: i % 12,
      });
    }
  }

  notes.sort((a, b2) => noteStart(a) - noteStart(b2));
  return notes;
}
