// 比較用の譜面パターン（Tapのみ）。t は秒、cell は左端(0..11)、width はセル数、layer は 0=地上/1=空中。
// 各パターンは period 秒でループする。BPM150 基準（8分=0.2s, 16分=0.1s）。

function rng(seed) {
  let s = seed >>> 0;
  return () => ((s = (s * 1664525 + 1013904223) >>> 0) / 4294967296);
}

function mixed() {
  const r = rng(7), notes = [];
  for (let i = 0; i < 40; i++) {
    const width = 2 + Math.floor(r() * 3);
    notes.push({ t: i * 0.2, cell: Math.floor(r() * (13 - width)), width, layer: r() < 0.45 ? 1 : 0 });
  }
  return { notes, period: 8 };
}

// 隣接: 同時刻に横に並ぶノーツ（「1つの大きいノーツに見える」問題の確認）＋階段
function adjacent() {
  const notes = [];
  let t = 0;
  for (let rep = 0; rep < 2; rep++) {
    for (const layer of [0, 1]) {
      // 幅3×4個を隙間なく並べる
      for (let k = 0; k < 4; k++) notes.push({ t, cell: k * 3, width: 3, layer });
      t += 0.4;
      // 幅2×2個 + 幅4
      notes.push({ t, cell: 2, width: 2, layer }, { t, cell: 4, width: 2, layer }, { t, cell: 6, width: 4, layer });
      t += 0.4;
      // 16分の階段（右へ）
      for (let k = 0; k < 8; k++) notes.push({ t: t + k * 0.1, cell: k, width: 3, layer });
      t += 1.2;
    }
  }
  return { notes, period: Math.ceil(t - 1e-6) };
}

// 連打: 同じ位置の16分・24分（厚み÷間隔、特に空中の詰まり具合の確認）
function stream() {
  const notes = [];
  let t = 0;
  for (const layer of [0, 1]) {
    for (let k = 0; k < 16; k++) notes.push({ t: t + k * 0.1, cell: 4, width: 4, layer });
    t += 2.0;
    for (let k = 0; k < 12; k++) notes.push({ t: t + k * (0.4 / 6), cell: 1 + (k % 2) * 6, width: 4, layer });
    t += 1.4;
  }
  // 地上＋空中の同時押し
  for (let k = 0; k < 8; k++) {
    notes.push({ t: t + k * 0.2, cell: 1, width: 4, layer: 0 }, { t: t + k * 0.2, cell: 7, width: 4, layer: 1 });
  }
  t += 2.0;
  return { notes, period: t };
}

// 幅の極端: 1セル・12セル
function widths() {
  const notes = [];
  let t = 0;
  for (const layer of [0, 1]) {
    for (let k = 0; k < 6; k++) notes.push({ t: t + k * 0.2, cell: k * 2 + (k % 2), width: 1, layer });
    t += 1.4;
    notes.push({ t, cell: 0, width: 12, layer }); t += 0.4;
    notes.push({ t, cell: 0, width: 6, layer }, { t, cell: 6, width: 6, layer }); t += 0.4;
    notes.push({ t, cell: 0, width: 12, layer }); t += 0.8;
  }
  return { notes, period: t };
}

export const PATTERNS = {
  mixed: { label: '標準（混在）', ...mixed() },
  adjacent: { label: '隣接・階段', ...adjacent() },
  stream: { label: '連打・同時押し', ...stream() },
  widths: { label: '幅 1 / 12', ...widths() },
};
