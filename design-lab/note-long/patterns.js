// Slide / Riser 比較用の譜面。時刻は秒（BPM150: 1拍=0.4s）。cellF は左端(0..12)、width はセル数、layerF 0=地上/1=空中。
// kind: 'tap'（文脈用。現行 Tap で描く） / 'slide' / 'riser'。
// Slide の points[i].marker: 'visible' のとき中継点マーカーを描く（始点は常に visible 扱い）。
// Riser は layerF(=0) → layerTo(=1 など)。Diver（下降）は今回対象外。

const wp = (t, cellF, width, layerF, extra = {}) => ({ t, cellF, width, layerF, ...extra });

// 1. 直線・ホールド系（地上/空中、同時押し、重なり）
function straight() {
  const n = [];
  n.push({ kind: 'slide', points: [wp(0.0, 1, 3, 0), wp(1.6, 1, 3, 0)] });
  n.push({ kind: 'slide', points: [wp(0.4, 7, 4, 1), wp(2.4, 7, 4, 1)] });
  n.push({ kind: 'slide', points: [wp(2.0, 4, 4, 0), wp(3.2, 4, 4, 0, { marker: 'visible' }), wp(4.4, 4, 4, 0)] });
  // 地上で重なる2本（重なり表示の確認）
  n.push({ kind: 'slide', points: [wp(4.8, 2, 5, 0), wp(6.4, 2, 5, 0)] });
  n.push({ kind: 'slide', points: [wp(5.2, 5, 5, 0), wp(6.8, 5, 5, 0)] });
  // 空中で重なる2本
  n.push({ kind: 'slide', points: [wp(4.8, 1, 5, 1), wp(6.0, 1, 5, 1)] });
  n.push({ kind: 'slide', points: [wp(5.2, 4, 5, 1), wp(6.8, 4, 5, 1)] });
  n.push({ kind: 'tap', t: 3.6, cellF: 8, width: 3, layer: 0 });
  n.push({ kind: 'tap', t: 4.0, cellF: 8, width: 3, layer: 0 });
  return { notes: n, period: 8 };
}

// 2. 曲線・層跨ぎ（なぞり）
function curves() {
  const n = [];
  n.push({ kind: 'slide', points: [
    wp(0.0, 0, 3, 0, { easing: 'sineInOut' }),
    wp(0.8, 9, 3, 0, { easing: 'sineInOut', marker: 'visible' }),
    wp(1.6, 0, 3, 0, { easing: 'sineInOut', marker: 'visible' }),
    wp(2.4, 9, 3, 0),
  ] });
  // 地上→空中へ徐々に上がる Slide
  n.push({ kind: 'slide', points: [
    wp(2.8, 2, 4, 0, { easingH: 'sineInOut' }),
    wp(4.0, 6, 4, 1, { marker: 'visible' }),
    wp(4.8, 6, 4, 1),
  ] });
  // 空中の S 字、幅が変わる
  n.push({ kind: 'slide', points: [
    wp(5.2, 1, 2, 1, { easing: 'smooth' }),
    wp(6.0, 5, 5, 1, { easing: 'smooth', marker: 'visible' }),
    wp(6.8, 9, 3, 1),
  ] });
  n.push({ kind: 'slide', points: [wp(5.2, 7, 3, 0), wp(7.2, 7, 3, 0)] });
  return { notes: n, period: 8 };
}

// 3. Riser 連携: 地上 Tap → 同地点 Riser → 空中 Slide（想定フロー、note-spec §4.6.1）
function riserFlow() {
  const n = [];
  // 基本形
  n.push({ kind: 'tap', t: 0.4, cellF: 2, width: 3, layer: 0 });
  n.push({ kind: 'riser', t: 0.8, cellF: 2, width: 3, layerF: 0, layerTo: 1 });
  n.push({ kind: 'slide', points: [wp(0.8, 2, 3, 1), wp(2.0, 2, 3, 1)] });
  // 単体 Riser を左右に
  n.push({ kind: 'riser', t: 2.4, cellF: 7, width: 3, layerF: 0, layerTo: 1 });
  n.push({ kind: 'riser', t: 2.8, cellF: 1, width: 3, layerF: 0, layerTo: 1 });
  // 部分移動（0 → 0.5）
  n.push({ kind: 'riser', t: 3.4, cellF: 4, width: 4, layerF: 0, layerTo: 0.5 });
  // 地上 Slide の終点から Riser → 空中 Slide（横移動つき）
  n.push({ kind: 'slide', points: [wp(4.0, 6, 3, 0, { easing: 'sineInOut' }), wp(5.2, 3, 3, 0)] });
  n.push({ kind: 'riser', t: 5.2, cellF: 3, width: 3, layerF: 0, layerTo: 1 });
  n.push({ kind: 'slide', points: [wp(5.2, 3, 3, 1, { easing: 'sineInOut' }), wp(6.4, 8, 3, 1)] });
  // 広い Riser
  n.push({ kind: 'riser', t: 7.0, cellF: 0, width: 12, layerF: 0, layerTo: 1 });
  return { notes: n, period: 8 };
}

// 4. 密集: 16分の Tap の上を Slide が通る / Riser の連続
function dense() {
  const n = [];
  n.push({ kind: 'slide', points: [wp(0.0, 0, 4, 0), wp(3.2, 0, 4, 0)] });
  for (let k = 0; k < 16; k++) n.push({ kind: 'tap', t: 0.2 + k * 0.2, cellF: 5 + (k % 2) * 3, width: 3, layer: 0 });
  n.push({ kind: 'slide', points: [wp(0.4, 6, 4, 1, { easing: 'sineInOut' }), wp(2.0, 2, 4, 1, { easing: 'sineInOut' }), wp(3.6, 6, 4, 1)] });
  for (let k = 0; k < 6; k++) n.push({ kind: 'riser', t: 4.2 + k * 0.4, cellF: (k % 3) * 4, width: 4, layerF: 0, layerTo: 1 });
  return { notes: n, period: 8 };
}

export const PATTERNS = {
  straight: { label: '直線・重なり', ...straight() },
  curves: { label: '曲線・層跨ぎ', ...curves() },
  riserFlow: { label: 'Riser 連携', ...riserFlow() },
  dense: { label: '密集', ...dense() },
};

/** ノーツの時間範囲（生成・破棄の判定用） */
export function noteSpan(n) {
  if (n.kind === 'slide') return [n.points[0].t, n.points[n.points.length - 1].t];
  return [n.t, n.t];
}
