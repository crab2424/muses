import { LAYER_GROUND, LAYER_SKY, type Layer } from './config';

/**
 * ノーツは `cell` から始まり `width` セル分の幅を占める。
 * 判定は「[cell, cell + width) のいずれかのセルに新規接触が入った」で成立する。
 */
export interface TapNote {
  kind: 'tap';
  time: number;
  layer: Layer;
  cell: number;
  /** 占有するセル数 (整数, 1 以上) */
  width: number;
}

export interface HoldNote {
  kind: 'hold';
  time: number;
  endTime: number;
  layer: Layer;
  cell: number;
  width: number;
}

/** アーク制御点。layerF は 0=地上 / 1=空中 の連続値、cellF はセル境界インデックスの連続値 */
export interface ArcPoint {
  time: number;
  layerF: number;
  cellF: number;
}

export interface ArcNote {
  kind: 'arc';
  points: ArcPoint[];
  /** 帯の幅（セル数単位、小数可） */
  width: number;
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
 * 「両層の同時押し」「層をまたぐアーク」「ホールド」「ノーツ幅の違い」を一通り含む 32 拍のループ。
 */
export function buildDemoChart(bpm: number, durationSec: number, cells: number): Note[] {
  const b = 60 / bpm;
  const notes: Note[] = [];
  const loopBeats = 32;
  const loopSec = loopBeats * b;
  const loops = Math.ceil(durationSec / loopSec);
  // 12 セル基準で書いた譜面を任意のセル数へスケールする
  const S = cells / 12;
  const C = (c: number) => Math.max(0, Math.min(cells - 1, Math.round(c * S)));
  const W = (w: number) => Math.max(1, Math.min(cells, Math.round(w * S)));
  const F = (c: number) => c * S; // アーク用（小数のまま）

  for (let L = 0; L < loops; L++) {
    const t0 = 3 + L * loopSec; // 開始 3 秒の余白

    // 0–7 拍: 地上の階段。幅 2 セル
    for (let i = 0; i < 8; i++) {
      notes.push({ kind: 'tap', time: t0 + i * b, layer: LAYER_GROUND, cell: C(1 + i), width: W(2) });
    }
    // 8–11 拍: 空中の 8 分刻み。幅 3 セルの太いノーツ
    for (let i = 0; i < 8; i++) {
      notes.push({
        kind: 'tap',
        time: t0 + (8 + i * 0.5) * b,
        layer: LAYER_SKY,
        cell: C(i % 2 === 0 ? 1 : 8),
        width: W(3),
      });
    }
    // 12–15 拍: 両層同時押し（左右対称）。片方は幅 1 の細ノーツ
    for (let i = 0; i < 4; i++) {
      const t = t0 + (12 + i) * b;
      notes.push({ kind: 'tap', time: t, layer: LAYER_GROUND, cell: C(i), width: W(2) });
      notes.push({ kind: 'tap', time: t, layer: LAYER_SKY, cell: C(11 - i), width: W(1) });
    }
    // 16–19 拍: 地上の幅広ホールド + 空中の 3 連符
    notes.push({
      kind: 'hold',
      time: t0 + 16 * b,
      endTime: t0 + 20 * b,
      layer: LAYER_GROUND,
      cell: C(4),
      width: W(4),
    });
    for (let i = 0; i < 6; i++) {
      notes.push({
        kind: 'tap',
        time: t0 + (16 + (i * 4) / 6) * b,
        layer: LAYER_SKY,
        cell: C(i * 2),
        width: W(2),
      });
    }
    // 20–27 拍: 層をまたぐアーク（地上 → 空中 → 地上）
    notes.push({
      kind: 'arc',
      width: 1.2 * S,
      points: [
        { time: t0 + 20 * b, layerF: 0, cellF: F(1.5) },
        { time: t0 + 22 * b, layerF: 0, cellF: F(4.5) },
        { time: t0 + 24 * b, layerF: 1, cellF: F(7.5) },
        { time: t0 + 26 * b, layerF: 1, cellF: F(9.5) },
        { time: t0 + 28 * b, layerF: 0, cellF: F(6.5) },
      ],
    });
    // 20–27 拍: 反対側にもう1本（交差の見え方確認）
    notes.push({
      kind: 'arc',
      width: 1.2 * S,
      points: [
        { time: t0 + 21 * b, layerF: 1, cellF: F(10.5) },
        { time: t0 + 24 * b, layerF: 0.5, cellF: F(5.5) },
        { time: t0 + 27 * b, layerF: 0, cellF: F(0.5) },
      ],
    });
    // 28–31 拍: 地上の 16 分の詰め（高速時の視認性確認）
    for (let i = 0; i < 16; i++) {
      notes.push({
        kind: 'tap',
        time: t0 + (28 + i * 0.25) * b,
        layer: LAYER_GROUND,
        cell: C((i % 6) * 2),
        width: W(2),
      });
    }
  }

  notes.sort((a, b2) => noteStart(a) - noteStart(b2));
  return notes;
}
