// 天上の聖域の共通部分（浮島・ガラス・金の輪・それぞれの線で共有）。移植元: design-lab/stage-bg/themes/sanctuary-r3.js の mkVs / instLines の xf。
// ラボは InstancedMesh、Unity では全インスタンスを1メッシュに焼き、頂点の「インスタンス番号」で下の配列を引く。
// 変換は **ラボ座標（-z が奥）のまま** 行い、最後に z を反転して Unity のワールドへ出す（自転の向き・法線・光の向きをラボと完全に同じにするため）。
// 配列はマテリアルごとに SetVectorArray で渡す（CBUFFER の外）。
#ifndef MUSES_SANCTUARY_INCLUDED
#define MUSES_SANCTUARY_INCLUDED

#include "Assets/Shaders/Include/MusesBackground.hlsl"

#define SANC_MAX_INST 24
float4 _InstM0[SANC_MAX_INST];   // 配置行列（ラボ座標、T*Rx*Ry*Rz*S）の1行目 (m00 m01 m02 tx)
float4 _InstM1[SANC_MAX_INST];
float4 _InstM2[SANC_MAX_INST];
float4 _InstSeed[SANC_MAX_INST]; // ラボの aSeed: x=位相 y=周期係数 z=自転係数 w=明るさ

float3 SancLabToWorld(float3 p) { return float3(p.x, p.y, -p.z); }
float3 SancCamLab() { return float3(_MusesBgCamPos.x, _MusesBgCamPos.y, -_MusesBgCamPos.z); }

// 局所座標 p・法線 n（ラボ）→ 自転 → 配置行列 → 上下・横ゆれ。ラボのワールド座標を返し、法線（正規化前）を nW へ
float3 SancInstance(int id, float3 p, float3 n, float spin, float bob, out float3 nW)
{
    float4 seed = _InstSeed[id];
    float t = _MusesBgTime;
    float ph = seed.x * 6.2831 + t * (0.30 + seed.y * 0.22);
    float a = t * spin * seed.z + seed.x * 6.2831;
    float ca = cos(a), sa = sin(a);
    p = float3(ca * p.x + sa * p.z, p.y, -sa * p.x + ca * p.z);
    n = float3(ca * n.x + sa * n.z, n.y, -sa * n.x + ca * n.z);
    float4 r0 = _InstM0[id], r1 = _InstM1[id], r2 = _InstM2[id];
    float3 wp = float3(dot(r0.xyz, p) + r0.w, dot(r1.xyz, p) + r1.w, dot(r2.xyz, p) + r2.w);
    nW = float3(dot(r0.xyz, n), dot(r1.xyz, n), dot(r2.xyz, n));
    wp.y += sin(ph) * bob;
    wp.x += cos(ph * 0.7) * 0.25;
    return wp;
}

#endif
