#ifndef MUSES_NOTE_SKIN_KEYCAP_INCLUDED
#define MUSES_NOTE_SKIN_KEYCAP_INCLUDED

// スキン「キーキャップ」。移植元: design-lab/note-skin/skins/keycap.js（r3 §7〜§10）。
// 全ノーツを不透明マットのキーキャップ素材（面取りの明暗＋白リング＋塗り）で統一する。加算発光は使わない。
//   - Tap / Ex Tap / Flick: 断面リングを積んだ押し出しソリッド（NoteGeometry.PushKeycapSolid）。地上は薄型、
//     空中は本物の立体で遠方ほど高さ→0（二重見え対策）。凸形なので背面を捨てれば ZTest Always のまま重なりが崩れない。
//   - Slide: マット板＋縁レール＋中央芯線（枕木なし）。始点・Visible 中継点は Tap と同じ陰影の平面キーキャップ。
//   - Riser / Diver: 薄い塗りの壁＋左右レール・根元/到達点バー＋白縁つきの分厚い ∧（∨）1枚。
#include "NoteSkinCommon.hlsl"

// ∧ の寸法（層）。NoteGeometry.cs の ChevronDims(Keycap) と一致させること
#define SKIN_CHEV_TH 0.28
#define SKIN_CHEV_SL 0.26

// 空中の Tap の高さを遠方で 0 に近づける係数（keycap.js の farFactor。progress: 0=判定線 1=最遠端）
float KeycapFarFactor(float p)
{
    float t = saturate((p - 0.12) / (0.6 - 0.12));
    return 1.0 - (1.0 - 0.06) * t * t * (3.0 - 2.0 * t);
}

// ---------------- 頂点: Tap 系の立体 ----------------
// uv5 = (A, B, zN, yN): ラボのローカル座標で x = A*半長 + B*半奥行、z = zN*半奥行（手前が +）、y = yN*半奥行。
// 半長・半奥行は頂点シェーダで求めるので、厚みの設定（NoteView の thicknessFrac）を変えてもメッシュの作り直しは要らない。
// normalOS はラボのローカル座標の法線、uv4.w は縁距離 c（天面のみ 0→1、それ以外 -1。白リング用）。
float3 SkinTapPlace(Attributes IN, float groupX, inout Varyings OUT, out float depth, out float scale)
{
    TapFrame f = MakeTapFrame(IN, groupX);
    float hf = IN.uv1.x > 0.5 ? KeycapFarFactor(f.progress) : 1.0; // 遠方で高さを縮めるのは空中だけ
    float lx = IN.uv5.x * f.a + IN.uv5.y * f.b;
    float lz = IN.uv5.z * f.b;
    float ly = IN.uv5.w * f.b * hf;
    float3 S = float3(f.sx, f.sx * hf, f.sz);
    float3 os = float3(f.center.x + lx * f.sx, f.center.y + ly * f.sx, f.center.z - lz * f.sz);

    float3 cam = TransformWorldToObject(_WorldSpaceCameraPos);
    float3 V = cam - os;
    V.z = -V.z;                          // ラボの向き（手前が +z）に揃える
    OUT.tapA = V;
    OUT.tapB = IN.normalOS / max(S, 1e-4); // ラボ: mat3(model) * (normal / s^2)
    OUT.tapC = float4(IN.uv4.w, 0, 0, f.center.z);
    scale = f.sx;
    depth = os.z;
    return os;
}

// キーキャップの陰影（Tap の立体とマーカーで共通。keycap.js の keycapShade）
float3 KeycapShade(float3 col, float3 N, float3 V)
{
    float3 L = normalize(float3(-0.35, 0.85, 0.5));
    float hemi = lerp(0.42, 0.78, N.y * 0.5 + 0.5);
    float diff = max(dot(N, L), 0.0) * 0.32;
    float3 rgb = col * (hemi + diff);
    float3 H = normalize(L + V);
    rgb += pow(max(dot(N, H), 0.0), 36.0) * 0.18 * (1.0 - step(0.98, N.y));
    rgb *= lerp(0.78, 1.0, smoothstep(0.0, 0.9, N.y));
    return rgb;
}

// 面取りプロファイル: e = 縁からの深さ(奥行き半幅比)。Tap の立体のリング補間と同じ (nh, ny)
float2 KeycapBevel(float e)
{
    if (e < 0.05) return lerp(float2(1.0, 0.0), float2(0.85, 0.55), e / 0.05);
    if (e < 0.14) return lerp(float2(0.85, 0.55), float2(0.5, 0.87), (e - 0.05) / 0.09);
    return float2(0.0, 1.0);
}

// 出力 = (rgb * (alpha + add), alpha)。範囲外は discard する。
void SkinFrag(Varyings IN, bool isFront, out float3 rgb, out float alpha, out float add)
{
    float side = IN.sdns.w;
    float localX = IN.misc.x, tag = IN.misc.y, scale = IN.misc.w;
    float3 base = IN.color.rgb;
    add = 0.0;

    if (tag > 0.5 && tag < 2.5)
    {
        // ---- Tap 系の立体: 背面は捨てる（凸形なので前向きの面は各ピクセル1枚だけ）----
        float c = IN.tapC.x;
        float fw = max(fwidth(c), 1e-5);
        if (!isFront) discard;
        rgb = KeycapShade(base, normalize(IN.tapB), normalize(IN.tapA));
        if (c >= 0.0)
        {
            // 白リング: 天面の縁から内側へ（遠方で細く）
            float dPx = c / fw, bPx = 1.0 / fw;
            float2 e = MusesEdge(1.5, scale);
            float w = min(e.x, max(0.0, bPx - 0.75) * 0.5);
            float ol = (1.0 - smoothstep(w - 0.5, w + 0.5, dPx)) * smoothstep(0.0, 0.4, w) * e.y;
            rgb = lerp(rgb, (float3)1.0, ol);
        }
        alpha = 1.0;
    }
    else if (tag > 3.5)
    {
        // ---- ∧ の腕: 範囲外（根元より下・到達点より上）は捨てる。白縁＋マット塗り ----
        float m = IN.extra.x, span = max(IN.extra.y, 0.05);
        float fm = max(fwidth(m), 1e-5);
        float inR = smoothstep(0.0, fm, m) * smoothstep(0.0, fm, span - m);
        if (inR <= 0.003) discard;
        float armSide = localX; // ∧ は localUv.x に腕の断面(-1/+1)を入れている
        float fs = max(fwidth(armSide), 1e-5);
        float2 eA = MusesEdge(3.0, scale);                          // 白縁: 判定線上で約3px、遠方で細く
        float edgeW = eA.x * fs;
        float edge = smoothstep(1.0 - edgeW - fs, 1.0 - edgeW + fs, abs(armSide)) * eA.y;
        // 切られた端（壁の出入り口）付近は白縁を出さない: 入ってきた矢印の先端が白い三角に見えるのを防ぐ
        float inside = min(m, span - m);
        edge *= smoothstep(0.12 * SKIN_CHEV_TH, 0.5 * SKIN_CHEV_TH, inside) * smoothstep(eA.x, eA.x + 2.0, inside / fm);
        float lit = 0.86 + 0.24 * (armSide * 0.5 + 0.5);            // 進行側ほど少し明るいマット
        rgb = lerp(base * lit, (float3)1.0, edge);
        alpha = inR;
    }
    else if (tag > 2.5)
    {
        // ---- Slide マーカー: Tap と同系統の陰影のキーキャップ板（平面にシェーダで陰影を描く）----
        float2 uv = float2(localX, side * 0.5 + 0.5);
        float2 p = uv - 0.5;
        float2 duv = float2(max(fwidth(uv.x), 1e-5), max(fwidth(uv.y), 1e-5));
        float2 pPx = p / duv, bPx = 0.5 / duv;
        float rPx = bPx.y * 0.999;                                  // ( ) カプセル
        float dist = RoundedBoxSDF(pPx, bPx, rPx);
        float shapeA = 1.0 - smoothstep(0.0, 1.0, dist);
        if (shapeA <= 0.003) discard;
        float inset = max(-dist, 0.0) / max(bPx.y, 1.0);            // 縁からの深さ（奥行き半幅比）
        float2 q = pPx - float2(clamp(pPx.x, -(bPx.x - rPx), bPx.x - rPx), 0.0);
        float2 dir = normalize(q + float2(1e-4, 0.0));
        float2 pf = KeycapBevel(inset);
        float3 N = normalize(float3(pf.x * dir.x, pf.y, -pf.x * dir.y));
        rgb = KeycapShade(base, N, normalize(float3(0.0, 0.8, 0.6)));
        // 白リング: 天面の縁（inset=0.14）から内側へ
        float2 eM = MusesEdge(1.5, scale);
        float w = min(eM.x, max(0.0, bPx.y - 0.75) * 0.5);
        float dPx = max(-dist - 0.14 * bPx.y, 0.0);
        float ol = eM.y * (1.0 - smoothstep(w - 0.5, w + 0.5, dPx)) * smoothstep(0.0, 0.4, w) * step(0.14 * bPx.y - 0.5, -dist);
        rgb = lerp(rgb, (float3)1.0, ol);
        alpha = shapeA;
    }
    else if (tag > -0.5)
    {
        // ---- Slide 帯: マット板 + 縁レール + 中央の芯線（横線・枕木なし）----
        float L = saturate(IN.extra.z); // 帯の層（vert が extra.z に入れる）。透明度を層で変える
        float du = max(fwidth(localX), 1e-5);
        float xe = min(localX, 1.0 - localX);
        float2 eR = MusesEdge(3.0, scale), eH = MusesEdge(1.2, scale), eC = MusesEdge(1.0, scale);
        float railW = min(eR.x * du, 0.25);
        float rail = (1.0 - smoothstep(railW - du, railW, xe)) * eR.y;
        float railHi = (1.0 - smoothstep(0.0, eH.x * du, xe)) * eH.y;
        float center = (1.0 - smoothstep(0.0, eC.x * du, abs(localX - 0.5))) * eC.y;
        rgb = base * 0.55;
        rgb = lerp(rgb, base, rail);
        rgb = lerp(rgb, (float3)1.0, saturate(railHi * 0.85 + center * 0.9));
        float a = lerp(0.52, 0.45, L);
        a = max(a, rail * 0.95);
        a = max(a, max(center, railHi) * 0.95);
        alpha = a;
    }
    else
    {
        // ---- Riser / Diver 壁（∧ は描かない）: 薄い塗り＋左右レール＋根元/到達点のバー ----
        float span = max(IN.extra.y, 0.05);
        float s = IN.extra.x * span;                                // 根元からの層距離
        float du = max(fwidth(localX), 1e-5);
        float xe = min(localX, 1.0 - localX);
        float ps = max(fwidth(s), 1e-5);
        rgb = base * 0.55;
        float a = 0.16;
        float dG = (span - s) / ps, dB = s / ps;
        float2 eG = MusesEdge(4.0, scale), eGW = MusesEdge(2.0, scale);
        float goalC = (1.0 - smoothstep(eG.x - 0.5, eG.x + 0.5, dG)) * eG.y;
        float goalW = (1.0 - smoothstep(eGW.x - 0.5, eGW.x + 0.5, dG)) * eGW.y;
        float baseC = (1.0 - smoothstep(eGW.x - 0.5, eGW.x + 0.5, dB)) * eGW.y;
        rgb = lerp(rgb, base, max(goalC, baseC)); a = max(a, max(goalC, baseC));
        rgb = lerp(rgb, (float3)1.0, goalW * 0.95);
        float2 eR = MusesEdge(3.4, scale), eH = MusesEdge(1.3, scale);
        float railW = min(eR.x * du, 0.2);
        float rail = (1.0 - smoothstep(railW - du, railW, xe)) * eR.y;
        float railHi = (1.0 - smoothstep(0.0, eH.x * du, xe)) * eH.y;
        rgb = lerp(rgb, base, rail); a = max(a, rail);
        rgb = lerp(rgb, (float3)1.0, railHi * 0.95); a = max(a, railHi);
        alpha = a;
    }
}

#endif
