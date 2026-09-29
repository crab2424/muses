// 仮組み: 既存のネオン系バリアントをそのまま束ねただけ（ハーネス確認用）。Sonnet 5.5 が r3 仕様で作り直す。
import tapV from '../../note-tap/variants/sonnet-b-imp.js';
import longV from '../../note-long/variants/sonnet-b.js';

export default {
  id: 'neon', name: 'ネオン（仮組み）', model: 'Sonnet 5.5',
  concept: 'Tap=ネオンドーム（インポスター）、Slide/Riser=ネオン系 をそのまま束ねた仮組み。',
  unityCost: '—',
  options: [],
  create(ctx) {
    const t = tapV.create(ctx), l = longV.create(ctx);
    return {
      makeTap: (spec) => t.makeNote(spec), updateTap: t.update, releaseTap: t.release,
      makeSlide: l.makeSlide, makeRiser: l.makeRiser,
      beforeRender(a) { t.beforeRender?.(a); l.beforeRender?.(a); },
    };
  },
};
