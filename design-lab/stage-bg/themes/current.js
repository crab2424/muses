// 基準: 現行の Unity（StageConfig.bgColor = #a0b298 の単色）。暗幕なし＝いまの見え方
export default {
  id: 'current', name: '現行（単色）', model: '-',
  concept: 'Unity の現状。比較の基準',
  palette: '#a0b298 単色', motion: 'なし', perf: 'クリアのみ', unityCost: '-',
  clearColor: '#a0b298',
  shade: { strength: 0 },
  create() { return {}; },
};
