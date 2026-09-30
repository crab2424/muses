#ifndef MUSES_NOTE_SKIN_NEON_INCLUDED
#define MUSES_NOTE_SKIN_NEON_INCLUDED

// スキン「ネオン」（既定）。移植元: design-lab/note-skin/skins/neon.js（r3 §7〜§13）。
//   - Tap / Ex Tap / Flick: 板1枚のインポスターで描くネオンドーム。カメラをノーツのローカル空間へ移し、
//     フラグメントでレイ vs 楕円柱＋楕円体（Flick は楕円錐）を解く。
//   - Slide: 白い芯＋色ハローの縁を持つ発光帯（刻み・パルスなし）。始点・Visible 中継点はドーム状のマーカー。
//   - Riser / Diver: 発光壁＋分厚い ∧（∨）1枚。
#include "NoteSkinCommon.hlsl"

// ∧ の寸法（層）。NoteGeometry.cs の ChevronDims(Neon) と一致させること
#define SKIN_CHEV_TH 0.32
#define SKIN_CHEV_SL 0.30

#define TAP_HR 1.5        // 高さ/半奥行
#define TAP_EXT_NEAR 0.15 // 手前側へ伸ばす量

// ---------------- 頂点: Tap 系インポスター（neon.js の TAP_VERT）----------------
// ローカル座標はラボと同じ: x = 横、y = 上、z = 手前が +（Unity のワールド z とは逆向き）。
// Tap は QuadThin の4頂点（uv3.x=左0/右1、side=-1手前/+1奥）。
float3 SkinTapPlace(Attributes IN, float groupX, inout Varyings OUT, out float depth, out float scale)
{
    TapFrame f = MakeTapFrame(IN, groupX);
    float a = f.a, b = f.b, H = b * TAP_HR;
    OUT.extra = float4(a, b, H, min(b * 2.4, a));   // w = Flick の尖り長

    float3 S = float3(f.sx, f.sx, f.sz);
    float3 cam = TransformWorldToObject(_WorldSpaceCameraPos);
    float3 ro = float3((cam.x - f.center.x) / f.sx, (cam.y - f.center.y) / f.sx, -(cam.z - f.center.z) / f.sz);

    // 板は奥側（side=+1）へ伸ばし、横にも広げて、ドームの側面・天面が板からはみ出さないようにする
    float lx = (IN.uv3.x * 2.0 - 1.0) * a;
    float lz;
    if (IN.uv1.y > 0.0)
    {
        float den = max(ro.y - H, 0.05 * b);
        float ext = max(ro.z, 0.0) * H / den;
        lz = -min(max(ext * 1.08 + 0.1 * b, b * 1.05), b * 6.0);
        lx = ro.x + (lx - ro.x) * (1.0 + 1.1 * H / den);
    }
    else
    {
        lz = b * (1.0 + TAP_EXT_NEAR);
    }
    OUT.tapA = ro;
    OUT.tapB = S;
    OUT.tapC = float4(lx, lz, IN.uv4.z, f.center.z);
    scale = f.sx;
    depth = f.center.z - lz * f.sz;
    return float3(f.center.x + lx * f.sx, f.center.y, depth);
}

// ---------------- フラグメント: Tap 系（neon.js の TAP_FRAG）----------------
float HitEllipsoid(float3 ro, float3 rd, float3 c, float3 r)
{
    float3 o = (ro - c) / r, d = rd / r;
    float A = dot(d, d), B = dot(o, d), C = dot(o, o) - 1.0;
    float D = B * B - A * C;
    if (D < 0.0) return -1.0;
    return (-B - sqrt(D)) / A;
}

// 端の尖り: 楕円錐 (z/b)^2+(y/H)^2 = ((T-xs)/T)^2, xs=端の始まりからの距離(0..T), y>=0。先端は地面の1点。
float HitCone(float3 o, float3 d, float T, float b, float H)
{
    float b2 = b * b, h2 = H * H, t2 = T * T;
    float A = d.z * d.z / b2 + d.y * d.y / h2 - d.x * d.x / t2;
    float B = o.z * d.z / b2 + o.y * d.y / h2 + (T - o.x) * d.x / t2;
    float C = o.z * o.z / b2 + o.y * o.y / h2 - (T - o.x) * (T - o.x) / t2;
    if (abs(A) < 1e-9) return -1.0;
    float D = B * B - A * C;
    if (D < 0.0) return -1.0;
    float s = sqrt(D);
    float t1 = (-B - s) / A, t2r = (-B + s) / A;
    if (t1 > t2r) { float tmp = t1; t1 = t2r; t2r = tmp; }
    float3 p1 = o + d * t1, p2 = o + d * t2r;
    if (t1 > 0.0 && p1.x >= 0.0 && p1.x <= T && p1.y >= 0.0) return t1;
    if (t2r > 0.0 && p2.x >= 0.0 && p2.x <= T && p2.y >= 0.0) return t2r;
    return -1.0;
}

// ドームの断面（角度 0,22,45,66,82 度＋天頂）の法線。neon.js の RINGS（HR=1.5）を展開した値。
static const float kRingD[6] = { 1.00000, 0.92718, 0.70711, 0.40674, 0.13917, 0.00000 };
static const float kRingNH[6] = { 1.00000, 0.96559, 0.83205, 0.55538, 0.20628, 0.00000 };
static const float kRingNY[6] = { 0.00000, 0.26008, 0.55470, 0.83160, 0.97849, 1.00000 };

// 戻り値 rgb、hit = 形に当たったか
float3 FragTap(Varyings IN, out bool hit)
{
    float uA = IN.extra.x, uB = IN.extra.y, uH = IN.extra.z;
    bool fl = IN.misc.y > 1.5;
    bool ex = IN.tapC.z > 0.5;
    float3 uColor = IN.color.rgb;
    float3 uDeep, uCore, uRim;
    if (fl)      { uDeep = SrgbToLinear3(float3(0x8f, 0x14, 0x24) / 255.0); uCore = SrgbToLinear3(float3(0xff, 0xd6, 0xd6) / 255.0); uRim = float3(1.0, 0.62, 0.66); }
    else if (ex) { uDeep = SrgbToLinear3(float3(0x8a, 0x5d, 0x05) / 255.0); uCore = SrgbToLinear3(float3(0xff, 0xf3, 0xc2) / 255.0); uRim = float3(1.0, 0.88, 0.5); }
    else         { uDeep = SrgbToLinear3(float3(0x0d, 0x3a, 0x8f) / 255.0); uCore = SrgbToLinear3(float3(0xd6, 0xf0, 0xff) / 255.0); uRim = float3(0.55, 0.8, 1.0); }

    float T = min(IN.extra.w, uA);
    float cx = fl ? max(uA - T, 0.0) : max(uA - uB, 0.0);
    float3 ro = IN.tapA;
    float3 qp = float3(IN.tapC.x, 0.0, IN.tapC.y);
    float3 rd = normalize(qp - ro);

    float tBest = 1e9;
    {
        float2 o = float2(ro.z / uB, ro.y / uH), d = float2(rd.z / uB, rd.y / uH);
        float A = dot(d, d), B = dot(o, d), C = dot(o, o) - 1.0;
        float D = B * B - A * C;
        if (D >= 0.0)
        {
            float t = (-B - sqrt(D)) / A;
            float3 p = ro + rd * t;
            if (abs(p.x) <= cx && p.y >= 0.0 && t > 0.0) tBest = t;
        }
    }
    [unroll] for (int i = 0; i < 2; i++)
    {
        float sg = i == 0 ? 1.0 : -1.0;
        float t = fl
            ? HitCone(float3(ro.x * sg - cx, ro.y, ro.z), float3(rd.x * sg, rd.y, rd.z), T, uB, uH)
            : HitEllipsoid(ro, rd, float3(sg * cx, 0.0, 0.0), float3(uB, uH, uB));
        if (t > 0.0)
        {
            float3 p = ro + rd * t;
            if (p.x * sg >= cx - 1e-5 && p.y >= 0.0 && t < tBest) tBest = t;
        }
    }
    hit = tBest < 1e8;
    float3 p = ro + rd * (hit ? tBest : 0.0);

    float vC; float3 nl;
    if (fl)
    {
        float xs = max(abs(p.x) - cx, 0.0);
        float g = clamp((T - xs) / T, 0.02, 1.0);
        vC = hit ? saturate(1.0 - abs(p.z) / (uB * g)) : 0.0;
        if (xs > 0.0) nl = float3(sign(p.x) * 2.0 * (T - xs) / (T * T), 2.0 * p.y / (uH * uH), 2.0 * p.z / (uB * uB));
        else nl = float3(0.0, p.y / (uH * uH), p.z / (uB * uB));
        nl = normalize(nl);
    }
    else
    {
        float exx = p.x - clamp(p.x, -cx, cx);
        float2 hz = float2(exx, p.z);
        float dist = length(hz);
        vC = hit ? saturate(1.0 - dist / uB) : 0.0;
        float2 hdir = dist > 1e-5 ? hz / dist : float2(1.0, 0.0);
        float dn = saturate(dist / uB);
        float nh = kRingNH[5], ny = kRingNY[5];
        [unroll] for (int k = 0; k < 5; k++)
        {
            if (dn <= kRingD[k] && dn >= kRingD[k + 1])
            {
                float f = (dn - kRingD[k + 1]) / (kRingD[k] - kRingD[k + 1]);
                nh = lerp(kRingNH[k + 1], kRingNH[k], f);
                ny = lerp(kRingNY[k + 1], kRingNY[k], f);
            }
        }
        nl = normalize(float3(nh * hdir.x, ny, nh * hdir.y));
    }
    float3 vS = IN.tapB;
    float3 N = normalize(nl / (vS * vS));
    float3 V = normalize((ro - p) * vS);

    float3 Lt = normalize(float3(-0.4, 0.8, 0.45));
    float ndv = saturate(dot(N, V));
    float fres = pow(1.0 - ndv, 2.2);
    // 遠方（ノーツの画面上の高さが小さい）ほど、白い縁・芯・リム・ハイライトを弱めて本体色を主にする
    float fw = max(fwidth(vC), 1e-5);
    float dPx = vC / fw, bPx = 1.0 / fw;   // bPx = 半奥行の画面上ピクセル数
    float nearF = smoothstep(4.0, 14.0, bPx);
    // r3 §13: 手前の縁（深色）が強く全体に暗い、の指摘で深色を本体色へ寄せ・縁の幅を狭め・陰影の下限を上げた
    float body = smoothstep(0.0, 0.45, vC);
    float3 rgb = lerp(lerp(uDeep, uColor, 0.45), uColor, body);
    rgb *= 0.92 + 0.25 * max(dot(N, Lt), 0.0);
    float core = smoothstep(0.62, 1.0, vC);
    rgb = lerp(rgb, uCore, core * 0.85 * lerp(0.25, 1.0, nearF));
    rgb += uRim * fres * 0.55 * lerp(0.3, 1.0, nearF);
    float3 Hh = normalize(Lt + V);
    rgb += pow(max(dot(N, Hh), 0.0), 60.0) * 0.6 * nearF;
    // 遠方はドームの陰影をやめ、本体色（やや明るめ）でほぼ均一に塗る
    rgb = lerp(min(uColor * 1.12 + 0.03, (float3)1.0), rgb, nearF);
    float2 e = MusesEdge(1.3, vS.x);   // 輪郭: 判定線上 1.3px × 見かけの倍率
    float ol = (1.0 - smoothstep(e.x - 0.5, e.x + 0.5, dPx)) * e.y;
    // 縁は近くでは白、遠くでは本体色の明るい版へ寄せる（細く・弱く）
    float3 olC = lerp(min(uColor * 1.25 + 0.08, (float3)1.0), float3(0.95, 0.98, 1.0), nearF);
    rgb = lerp(rgb, olC, ol * lerp(0.5, 1.0, nearF));
    return rgb;
}

// 出力 = (rgb * (alpha + add), alpha)。ラボの museOut と同じ。範囲外は discard する。
void SkinFrag(Varyings IN, bool isFront, out float3 rgb, out float alpha, out float add)
{
    float side = IN.sdns.w;
    float localX = IN.misc.x, tag = IN.misc.y, scale = IN.misc.w;
    float3 base = IN.color.rgb;

    if (tag > 0.5 && tag < 2.5)
    {
        bool hit;
        rgb = FragTap(IN, hit);
        if (!hit) discard;
        alpha = 1.0; add = 0.0;
    }
    else if (tag > 3.5)
    {
        // ---- ∧ の腕: 範囲外（根元より下・到達点より上）は捨てる ----
        float m = IN.extra.x, span = max(IN.extra.y, 0.05);
        float fm = max(fwidth(m), 1e-5);
        float inR = smoothstep(0.0, fm, m) * smoothstep(0.0, fm, span - m);
        if (inR <= 0.003) discard;
        float armSide = localX; // ∧ は localUv.x に腕の断面(-1/+1)を入れている
        float fs = max(fwidth(armSide), 1e-5);
        float2 eA = MusesEdge(2.0, scale);
        float edge = smoothstep(1.0 - eA.x * fs, 1.0, abs(armSide)) * eA.y; // 腕の上下の縁だけ白く
        rgb = lerp(base, (float3)1.0, 0.12 + 0.6 * edge);
        alpha = 0.75 * inR; add = 0.30 * inR;
    }
    else if (tag > 2.5)
    {
        // ---- Slide マーカー: ドーム状の発光（Tap と同素材・帯の色）----
        float2 uv = float2(localX, side * 0.5 + 0.5);
        float2 p = uv - 0.5;
        float2 duv = float2(max(fwidth(uv.x), 1e-5), max(fwidth(uv.y), 1e-5));
        float2 pPx = p / duv, bPx = 0.5 / duv;
        float rPx = min(0.5 / max(duv.x, duv.y) * 0.5, min(bPx.x, bPx.y) * 0.98);
        float dist = RoundedBoxSDF(pPx, bPx, rPx);
        float shapeA = 1.0 - smoothstep(0.0, 1.0, dist);
        if (shapeA <= 0.003) discard;
        float2 eM = MusesEdge(1.5, scale);
        float w = min(eM.x, max(0.0, bPx.y - 0.75) * 0.5);
        float outline = smoothstep(-w - 0.5, -w + 0.5, dist) * eM.y;
        float depthPx = saturate(-dist / max(bPx.y, 1.0));
        float dome = sqrt(1.0 - (1.0 - depthPx) * (1.0 - depthPx));
        float rim = pow(1.0 - dome, 2.5);
        rgb = base * (0.55 + 0.7 * dome);
        rgb = lerp(rgb, (float3)1.0, smoothstep(0.75, 1.0, dome) * 0.55);
        rgb += base * rim * 0.6;
        rgb = lerp(rgb, (float3)1.0, outline);
        alpha = 0.95 * shapeA; add = 0.25 * shapeA;
    }
    else if (tag > -0.5)
    {
        // ---- Slide 帯（横線の刻みなし）: 左右の白い芯＋色ハロー、中央の芯線 ----
        float du = max(fwidth(localX), 1e-5);
        float xe = min(localX, 1.0 - localX);
        float haloPx = 6.0 * scale;
        float haloW = min(haloPx * du, 0.3);
        float halo = exp(-xe / max(haloW, 1e-4) * 2.2) * saturate(haloPx);
        float2 eE = MusesEdge(1.4, scale);
        float core = (1.0 - smoothstep(eE.x - 0.5, eE.x + 0.5, xe / du)) * eE.y;
        float xc = abs(localX - 0.5);
        float2 eC = MusesEdge(1.0, scale);
        float cCore = (1.0 - smoothstep(eC.x - 0.5, eC.x + 0.5, xc / du)) * eC.y;
        float cHaloPx = 3.0 * scale;
        float cHalo = exp(-xc / max(min(cHaloPx * du, 0.1), 1e-4) * 2.0) * 0.35 * saturate(cHaloPx);
        float glow = 0.10 + halo * 0.55 + cHalo;
        rgb = lerp(base, (float3)1.0, saturate(core * 0.9 + cCore * 0.85));
        alpha = 0.10;
        add = clamp(glow + core * 0.6 + cCore * 0.6, 0.0, 1.6) * 0.9;
    }
    else
    {
        // ---- Riser / Diver の壁: 薄い発光面＋左右の白い芯とハロー＋到達点の白線 ----
        float k = IN.extra.x;
        float du = max(fwidth(localX), 1e-5);
        float xe = min(localX, 1.0 - localX);
        float haloPx = 7.0 * scale;
        float haloW = min(haloPx * du, 0.3);
        float halo = exp(-xe / max(haloW, 1e-4) * 2.2) * saturate(haloPx);
        float2 eE = MusesEdge(1.5, scale);
        float core = (1.0 - smoothstep(eE.x - 0.5, eE.x + 0.5, xe / du)) * eE.y;
        float dk = max(fwidth(k), 1e-5);
        float2 eG = MusesEdge(1.6, scale);
        float goal = (1.0 - smoothstep(eG.x - 0.5, eG.x + 0.5, (1.0 - k) / dk)) * eG.y;
        float gHaloPx = 8.0 * scale;
        float goalHalo = exp(-(1.0 - k) / max(min(gHaloPx * dk, 0.25), 1e-4) * 2.2) * 0.5 * saturate(gHaloPx);
        float wall = 0.10 * lerp(0.55, 1.0, k);
        rgb = lerp(base, (float3)1.0, saturate(core * 0.9 + goal * 0.9));
        alpha = 0.05;
        add = clamp(wall + halo * 0.55 + goalHalo + core * 0.6 + goal * 0.8, 0.0, 1.6);
    }
}

#endif
