// ステージ背景の共通関数。設計は memory/game/stage-bg-unity-port.md。
// 移植元: design-lab/stage-bg/shared/zone.js（暗部ゾーン）、shared/lines.js（画面上で一定幅の線）。
//
// グローバル uniform（StageBackground.cs が設定する）:
//   _MusesBgTime          実時間(秒)。音楽・一時停止に反応しない
//   _MusesBgAspect        画面の横/縦
//   _MusesBgZone          暗部ゾーン (u0, v0, u1, v1)。NDC、y 上向き
//   _MusesBgZoneFeather   暗部マスクの縁のぼかし幅（NDC）
//   _MusesBgCamPos / _MusesBgCamFwd / _MusesBgCamRight / _MusesBgCamUp   レイキャスト用のカメラ基底（ワールド、Unity 座標）
//   _MusesBgTanHalfFov    tan(垂直画角/2)
#ifndef MUSES_BACKGROUND_INCLUDED
#define MUSES_BACKGROUND_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

float _MusesBgTime;
float _MusesBgAspect;
float4 _MusesBgZone;
float _MusesBgZoneFeather;
float3 _MusesBgCamPos;
float3 _MusesBgCamFwd;
float3 _MusesBgCamRight;
float3 _MusesBgCamUp;
float _MusesBgTanHalfFov;

// iPad 11"（縦1668px）を基準にした px → NDC 縦単位（ラボの線幅・点の大きさの単位）
#define MUSES_BG_REF_HEIGHT 1668.0
float BgRefPx(float px) { return px * 2.0 / MUSES_BG_REF_HEIGHT; }

// ---------------------------------------------------------------- 画面座標
// ラボの museBaseNdc()。頂点で clip を varying に渡し（xyw）、フラグメントでこれを呼ぶ。y は上向き（プラットフォームの反転を戻す）
float2 BgNdc(float3 clipXYW)
{
    float2 ndc = clipXYW.xy / clipXYW.z;
    ndc.y *= _ProjectionParams.x;
    return ndc;
}

// スクリーン空間の板: 頂点の xy を NDC（y 上向き）としてそのまま出す。z は reversed-Z でも通常でも有効な 0.5
float4 BgScreenClip(float2 ndc)
{
    return float4(ndc.x, ndc.y * _ProjectionParams.x, 0.5, 1.0);
}

// 遠い物体を far 面の手前に押し込む（カメラの far は drawFar×1.5 しかないため）。前後関係は far より手前のものだけ保たれる
float4 BgClampFar(float4 clip)
{
#if UNITY_REVERSED_Z
    clip.z = max(clip.z, clip.w * 1e-5);
#else
    clip.z = min(clip.z, clip.w * (1.0 - 1e-5));
#endif
    return clip;
}

// ---------------------------------------------------------------- 暗部ゾーン（zone.js の SPAWN_GLSL と同じ式）
float museSpawnMask(float2 ndc)
{
    float2 c = 0.5 * (_MusesBgZone.xy + _MusesBgZone.zw);
    float2 h = 0.5 * (_MusesBgZone.zw - _MusesBgZone.xy);
    float2 q = abs(ndc - c) - h;
    float dist = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0);
    return 1.0 - smoothstep(0.0, _MusesBgZoneFeather, dist);
}

// 画面上の真円までの符号付き距離（縦単位、内側が負）。gate = (cx, cy, r, aspect)（zone.js の GATE_GLSL）
float museGateDist(float2 ndc, float4 gate)
{
    float2 q = (ndc - gate.xy) * float2(gate.w, 1.0);
    return length(q) - gate.z;
}

// ---------------------------------------------------------------- レイキャスト
// このピクセルの視線方向（ワールド、Unity 座標、正規化済み）
float3 BgRayDir(float2 ndc)
{
    float3 d = _MusesBgCamFwd
             + ndc.x * _MusesBgTanHalfFov * _MusesBgAspect * _MusesBgCamRight
             + ndc.y * _MusesBgTanHalfFov * _MusesBgCamUp;
    return normalize(d);
}
// Unity 座標 → ラボ座標（z 反転）。模様の計算をラボと同じにするため
float3 BgToLab(float3 p) { return float3(p.x, p.y, -p.z); }

// ---------------------------------------------------------------- 線（shared/lines.js）
// メッシュは BgLineMesh.cs が作る。線分1本 = 4頂点:
//   POSITION = 始点 A（オブジェクト空間） / TEXCOORD0.xyz = 終点 B / TEXCOORD1.xy = (始点0・終点1, 帯の左右 ±1)
//   TEXCOORD2.x = 線分ごとの任意値 p（ラボの vP） / COLOR = 色×強さ（linear、そのまま使う）
// 線幅 widthNdc は NDC 縦単位（BgRefPx(px)）。AA 用に実画面 0.5px ずつ外へ広げる。
struct BgLineVOut
{
    float4 clip;
    float side;     // 帯の左右 ±1（フラグメントで縁のぼかしに使う）
    float halfW;    // 帯の半幅（NDC）
    float margin;   // AA 余白（NDC）
};

// aWS / bWS はワールド座標（オブジェクト変換・アニメ済み）。s = TEXCOORD1
BgLineVOut BgLineExpand(float3 aWS, float3 bWS, float2 s, float widthNdc)
{
    float4 ca = TransformWorldToHClip(aWS);
    float4 cb = TransformWorldToHClip(bWS);
    float2 na = ca.xy / ca.w, nb = cb.xy / cb.w;
    float2 dir = (nb - na) * float2(_MusesBgAspect, 1.0);
    dir = length(dir) > 1e-6 ? normalize(dir) : float2(1.0, 0.0);
    float2 nrm = float2(-dir.y, dir.x);
    float4 c = s.x < 0.5 ? ca : cb;
    BgLineVOut o;
    o.halfW = widthNdc * 0.5;
    o.margin = 1.0 / max(_ScaledScreenParams.y, 1.0);
    float w = o.halfW + o.margin;
    c.xy += nrm * s.y * w * float2(1.0 / _MusesBgAspect, 1.0) * c.w;
    o.clip = BgClampFar(c);
    o.side = s.y;
    return o;
}

// 帯の縁の柔らかさ（中心1 → 外周0）。lines.js の edge と同じ
float BgLineEdge(float side, float halfW, float margin)
{
    float edge = halfW / (halfW + margin);
    return 1.0 - smoothstep(edge * 0.6, 1.0, abs(side));
}

#endif
