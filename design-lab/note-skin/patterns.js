// スキン比較用の譜面。時刻は秒（BPM150: 1拍=0.4s, 8分=0.2s, 16分=0.1s）。cellF は左端(0..12)、width はセル数。
// kind:
//   'tap' / 'extap' / 'flick' … { t, cellF, width, layer(0=地上/1=空中) }（CPU 配置、Tap ラボと同じ）
//   'slide'                   … { points: [{ t, cellF, width, layerF, easing, easingH, marker }] }（note-long と同じ）
//   'riser'                   … { t, cellF, width, layerF, layerTo }。layerTo < layerF なら Diver（下降、dir=-1）
// 各パターンは period 秒でループする。

const wp = (t, cellF, width, layerF, extra = {}) => ({ t, cellF, width, layerF, ...extra });
const tap = (kind, t, cellF, width, layer) => ({ kind, t, cellF, width, layer });

// 1. 全種類: 一通り順番に流れる（まずこれで一式の統一感を見る）
function all() {
  const n = [];
  // 地上: Tap → ExTap → Flick、空中: 同じ並び
  n.push(tap('tap', 0.4, 1, 3, 0), tap('extap', 0.8, 5, 3, 0), tap('flick', 1.2, 8, 3, 0));
  n.push(tap('tap', 1.6, 8, 3, 1), tap('extap', 2.0, 4, 3, 1), tap('flick', 2.4, 1, 3, 1));
  // 地上 Slide（中継点あり）→ Riser → 空中 Slide
  n.push({ kind: 'slide', points: [
    wp(2.8, 2, 3, 0, { easing: 'sineInOut' }), wp(3.6, 6, 3, 0, { marker: 'visible' }), wp(4.4, 6, 3, 0),
  ] });
  n.push({ kind: 'riser', t: 4.4, cellF: 6, width: 3, layerF: 0, layerTo: 1 });
  n.push({ kind: 'slide', points: [wp(4.4, 6, 3, 1, { easing: 'sineInOut' }), wp(5.4, 2, 3, 1)] });
  // Diver で地上へ戻って Tap
  n.push({ kind: 'riser', t: 5.8, cellF: 2, width: 3, layerF: 1, layerTo: 0 });
  n.push(tap('tap', 6.2, 2, 3, 0), tap('flick', 6.2, 7, 3, 0));
  // 同時押し: 地上 ExTap + 空中 Tap
  n.push(tap('extap', 6.8, 1, 4, 0), tap('tap', 6.8, 7, 4, 1));
  return { notes: n, period: 8 };
}

// 2. Riser / Diver: 全移動・部分移動・幅違い・連続
function riserDiver() {
  const n = [];
  n.push({ kind: 'riser', t: 0.4, cellF: 1, width: 3, layerF: 0, layerTo: 1 });
  n.push({ kind: 'riser', t: 0.8, cellF: 8, width: 3, layerF: 1, layerTo: 0 });
  n.push({ kind: 'riser', t: 1.6, cellF: 4, width: 4, layerF: 0, layerTo: 0.5 });
  n.push({ kind: 'riser', t: 2.2, cellF: 4, width: 4, layerF: 1, layerTo: 0.5 });
  n.push({ kind: 'riser', t: 3.0, cellF: 0, width: 12, layerF: 0, layerTo: 1 });
  n.push({ kind: 'riser', t: 3.8, cellF: 0, width: 12, layerF: 1, layerTo: 0 });
  // 幅1・幅2
  n.push({ kind: 'riser', t: 4.6, cellF: 2, width: 1, layerF: 0, layerTo: 1 });
  n.push({ kind: 'riser', t: 4.6, cellF: 8, width: 2, layerF: 1, layerTo: 0 });
  // 連続（上げ下げ）
  for (let k = 0; k < 6; k++) {
    const up = k % 2 === 0;
    n.push({ kind: 'riser', t: 5.4 + k * 0.4, cellF: (k % 3) * 4, width: 4, layerF: up ? 0 : 1, layerTo: up ? 1 : 0 });
  }
  return { notes: n, period: 8.4 };
}

// 3. Tap 系の隣接・同時押し（ExTap/Flick と Tap の見分け、1本に融合しないか）
function adjacent() {
  const n = [];
  let t = 0.4;
  for (const layer of [0, 1]) {
    n.push(tap('tap', t, 0, 3, layer), tap('extap', t, 3, 3, layer), tap('flick', t, 6, 3, layer), tap('tap', t, 9, 3, layer));
    t += 0.4;
    n.push(tap('flick', t, 2, 2, layer), tap('flick', t, 4, 2, layer), tap('extap', t, 6, 4, layer));
    t += 0.4;
    // 16分の階段（Tap / Flick 交互）
    for (let k = 0; k < 8; k++) n.push(tap(k % 2 ? 'flick' : 'tap', t + k * 0.1, k, 3, layer));
    t += 1.2;
    // 幅1・幅12
    n.push(tap('extap', t, 5, 1, layer)); t += 0.4;
    n.push(tap('tap', t, 0, 12, layer)); t += 0.6;
  }
  return { notes: n, period: Math.ceil(t) };
}

// 4. 密集: 16分の Tap/ExTap/Flick の上を Slide が通る、空中 Slide と空中 Tap の重なり
function dense() {
  const n = [];
  n.push({ kind: 'slide', points: [wp(0.0, 0, 4, 0), wp(3.2, 0, 4, 0)] });
  const kinds = ['tap', 'tap', 'extap', 'tap', 'flick', 'tap', 'tap', 'extap'];
  for (let k = 0; k < 16; k++) n.push(tap(kinds[k % 8], 0.2 + k * 0.2, 1 + (k % 3) * 3, 3, 0));
  n.push({ kind: 'slide', points: [
    wp(0.4, 6, 4, 1, { easing: 'sineInOut' }), wp(2.0, 2, 4, 1, { easing: 'sineInOut', marker: 'visible' }), wp(3.6, 6, 4, 1),
  ] });
  for (let k = 0; k < 8; k++) n.push(tap(k % 4 === 3 ? 'flick' : 'tap', 0.5 + k * 0.4, 7 - (k % 2) * 5, 3, 1));
  // 層を跨ぐ Slide と Diver
  n.push({ kind: 'slide', points: [wp(4.0, 1, 3, 0, { easingH: 'sineInOut' }), wp(5.2, 5, 3, 1, { marker: 'visible' }), wp(6.0, 5, 3, 1)] });
  n.push({ kind: 'riser', t: 6.0, cellF: 5, width: 3, layerF: 1, layerTo: 0 });
  n.push({ kind: 'slide', points: [wp(6.0, 5, 3, 0), wp(7.2, 9, 3, 0)] });
  return { notes: n, period: 8 };
}

export const PATTERNS = {
  all: { label: '全種類', ...all() },
  riserDiver: { label: 'Riser / Diver', ...riserDiver() },
  adjacent: { label: 'Tap系 隣接', ...adjacent() },
  dense: { label: '密集', ...dense() },
};

export const TAP_KINDS = new Set(['tap', 'extap', 'flick']);

/** ノーツの時間範囲（生成・破棄の判定用） */
export function noteSpan(n) {
  if (n.kind === 'slide') return [n.points[0].t, n.points[n.points.length - 1].t];
  return [n.t, n.t];
}
