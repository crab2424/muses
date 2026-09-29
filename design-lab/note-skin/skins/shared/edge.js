// 白線（縁・レール・輪郭）の幅を「判定線上での幅 × 見かけの倍率」で決め、下限を uEdgeMinPx（ヘッダの「白線の下限」）で
// 切り替えるための共有 GLSL（ネオン・キーキャップ共通）。
//
// 背景: これまでの白線はスクリーン空間で一定幅（1.2〜1.5px）だったため、遠方でノーツ本体が数 px に縮んでも白線だけ
//   同じ太さで残り、白の主張が強すぎた（ユーザー指摘 r3 §9）。遠方ではノーツと一緒に細くし、下限 0 なら消えるようにする。
//
// ## 使い方（フラグメントシェーダ）
//   ${EDGE_GLSL} を差し込み（uniform uEdgeMinPx を宣言する。マテリアルの uniforms に ctx.uniforms を展開しておくこと）、
//     vec2 e = museEdge(basePx, scale);
//     float line = (1.0 - smoothstep(e.x - 0.5, e.x + 0.5, distPx)) * e.y;
//   basePx : 判定線上（scale=1）での白線の幅（px）。従来の定数幅をそのまま入れる
//   scale  : その地点の見かけの倍率（判定線上=1、遠方ほど小さい）。
//            頂点配置のもの（Slide/Riser）は musePlace の out scale を varying で渡す。
//            CPU 配置の Tap 系は updateTap の info.sx（横の倍率）や、画面上の半奥行 px ÷ 判定線上の半奥行 px など。
//   戻り値 e.x = AA 用に 1px 以上に丸めた幅（px）、e.y = 濃さ（実際の幅が 1px 未満ならその割合で薄くする）
//
// uEdgeScale      : 白線の太さの全体倍率（ヘッダの「白線の太さ」、既定 1）。近距離の主張の強さもここで調整する
// uEdgeMinPx = 0   : 遠方では幅が 1px を切ったところから薄くなり、消える
// uEdgeMinPx = 1.5 : 従来とほぼ同じ（常に 1.5px 以上）
export const EDGE_GLSL = /* glsl */ `
uniform float uEdgeMinPx, uEdgeScale;
vec2 museEdge(float basePx, float scale) {
  float w = max(basePx * uEdgeScale * scale, uEdgeMinPx);
  return vec2(max(w, 1.0), clamp(w, 0.0, 1.0));
}
`;
