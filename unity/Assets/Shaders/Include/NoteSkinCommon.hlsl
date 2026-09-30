#ifndef MUSES_NOTE_SKIN_COMMON_INCLUDED
#define MUSES_NOTE_SKIN_COMMON_INCLUDED

// ノーツスキン（ネオン / キーキャップ）で共通の頂点入出力と部品。移植元: design-lab/note-skin（r3）。
// Note.shader が include し、スキンごとの部分は NoteSkinNeon.hlsl / NoteSkinKeycap.hlsl に置く。
#include "NotePlacement.hlsl"

// NoteView が毎フレーム/ビルド時に設定する（NotePlacement.hlsl の CBUFFER の外。既存の _GroupX と同じ扱い）
float _SongTime;   // 実時間（スクロールグループの X(t) を通さない）。∧ の位相に使う
float _GroundNear; // 層ごとの手前端（∧ の頂点は層が動くので、手前端をシェーダで求める）
float _SkyNear;
float _EdgeMinPx;
float _EdgeScale;

// ∧ の周期の余白と1周期の秒数（design-lab/note-skin/skins/shared/chevron.js の CHEVRON_DEFAULTS。両スキン共通）
#define CHEV_PAD 0.05
#define CHEV_CYCLE 0.5

// 種別タグ（localUv.y、NoteGeometry.NoteMeshData.localUv 参照）
// 0=Slide帯 / 1=Tap・ExTap / 2=Flick / 3=Slideマーカー / 4=∧の腕 / -1=Riser壁

struct Attributes
{
    float4 positionOS : POSITION;   // (u, y, X(ノーツ時刻))
    float3 normalOS : NORMAL;       // キーキャップの Tap の立体の法線（ラボのローカル座標、それ以外は 0）
    float4 color : COLOR;           // リニア
    float2 uv0 : TEXCOORD0;         // x = state, y = near
    float2 uv1 : TEXCOORD1;         // x = layerF, y = side
    float2 uv2 : TEXCOORD2;         // x = scrollGroup, y = slideEatable
    float2 uv3 : TEXCOORD3;         // x = 横のローカル座標(0..1)、∧は腕の断面(-1/+1) / y = 種別タグ
    float4 uv4 : TEXCOORD4;         // 種別ごとの追加データ（NoteGeometry.NoteMeshData.extra 参照）
    float4 uv5 : TEXCOORD5;         // キーキャップの Tap の立体の頂点（NoteMeshData.solid 参照）
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    float4 color : TEXCOORD0;
    float4 sdns : TEXCOORD1;   // state, depth, near, side
    float4 misc : TEXCOORD2;   // localX, tag, slideEatable, scale（見かけの倍率）
    float4 extra : TEXCOORD3;  // 帯・マーカー・壁: (uv4.x, uv4.y, layerF) 壁は (k, span) / ∧: (m, span) / ネオン Tap: (a, b, H, T)
    float3 tapA : TEXCOORD4;   // ネオン Tap: ローカル空間のカメラ位置 / キーキャップ Tap: 視線ベクトル V（ラボの向き）
    float3 tapB : TEXCOORD5;   // ネオン Tap: ローカル→ワールドの倍率 / キーキャップ Tap: 法線 N（ラボの向き）
    float4 tapC : TEXCOORD6;   // ネオン Tap: (板上の点 x, z, ExTap, 中心の奥行き) / キーキャップ Tap: (縁距離 c, 0, 0, 中心の奥行き)
};

// ラボの色（sRGB の16進）をリニアへ。定数なのでコンパイル時に畳まれる。
float3 SrgbToLinear3(float3 c)
{
#ifdef UNITY_COLORSPACE_GAMMA
    return c;
#else
    return lerp(pow((c + 0.055) / 1.055, 2.4), c / 12.92, step(c, (float3)0.04045));
#endif
}

// design-lab/note-skin/skins/shared/edge.js の museEdge。
// basePx = 判定線上（scale=1）での白線の幅(px)、scale = その地点の見かけの倍率。
// 戻り値 x = AA 用に 1px 以上に丸めた幅、y = 濃さ（実際の幅が 1px 未満ならその割合で薄くする）。
float2 MusesEdge(float basePx, float scale)
{
    float w = max(basePx * _EdgeScale * scale, _EdgeMinPx);
    return float2(max(w, 1.0), saturate(w));
}

float RoundedBoxSDF(float2 p, float2 b, float r)
{
    float2 q = abs(p) - b + r;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

// ---------------- 頂点: ∧ の腕（chevron.js の chevronTipM / chevronPlace）----------------
// th / sl（厚み・傾き、層）はスキンごと。NoteGeometry.cs の ChevronDims と一致させること。
float ChevronTipM(float noteT, float span, float th, float sl)
{
    float P = span + (th + sl) + CHEV_PAD;
    // 判定時刻（_SongTime == noteT）に先端が到達点（m = span）へ届く。GLSL の mod と同じ floor 基準で折り返す
    float x = (_SongTime - noteT) / CHEV_CYCLE * P + span + CHEV_PAD;
    return x - P * floor(x / P) - (th * 0.5 + CHEV_PAD);
}

// 全頂点で共通の基準層 Lc（先端中心の層を壁の範囲にクランプ）で壁上の位置と層方向の微分を取り、
// 頂点ごとの層差ぶんその方向へずらす（∧ 全体が一緒に動き、腕は画面上で直線のまま。r3 §10）。
float3 ChevronPlace(float3 pos, float baseLayer, float4 c, float groupX, float th, float sl,
    out float layer, out float m, out float depth, out float scale)
{
    float offsetM = c.x, span = c.y, dirSign = c.z, noteT = c.w;
    float yUp = pos.y - baseLayer * _SkyHeight;
    float tipM = ChevronTipM(noteT, span, th, sl);
    float Lc = baseLayer + dirSign * clamp(tipM, 0.0, span);
    m = tipM + offsetM;
    layer = baseLayer + dirSign * m;
    float d1, s1;
    float3 p0 = PlaceNoteCore(pos.x, Lc * _SkyHeight + yUp, pos.z, Lc, 0.0, groupX, depth, scale);
    float3 pL = PlaceNoteCore(pos.x, (Lc + 0.01) * _SkyHeight + yUp, pos.z, Lc + 0.01, 0.0, groupX, d1, s1);
    float3 p = p0 + (layer - Lc) * (pL - p0) / 0.01;
    depth = p.z;
    return p;
}

// ---------------- 頂点: Tap 系の中心と倍率（note-tap/stage.js の placeNote）----------------
// ラボはノーツ1個ずつ CPU で「中心へ平行移動＋(sx, sx, sz) 倍」していた。ここでは同じ中心と倍率を頂点シェーダで求める。
// uv4.xy = ノーツの左右端の u。戻り値 center はオブジェクト空間（z=奥行き）。
struct TapFrame
{
    float3 center;
    float sx, sz;
    float a, b;        // 半長・半奥行（判定線上・地上のワールド単位）
    float progress;    // 0=判定線, 1=最遠端（地上の進み具合、ラボの info.progress）
};

TapFrame MakeTapFrame(Attributes IN, float groupX)
{
    TapFrame f;
    float L = IN.uv1.x;
    float uL = IN.uv4.x, uR = IN.uv4.y;
    float d0 = _ZJudge + (IN.positionOS.z - groupX) * _Speed;
    float htNom = _ZJudge * _ThicknessFrac;
    float ht = MusesHalfThickness(d0, L);
    float dn = MusesRemapDepth(d0 - ht, L);
    float df = MusesRemapDepth(d0 + ht, L);
    float dc = 0.5 * (dn + df);
    float zcJG = MusesZcJudgeGround();
    float zcMix = MusesZcMix(L, dc);
    f.sx = zcMix / zcJG;
    f.sz = max((df - dn) / (2.0 * htNom), 1e-4);
    f.center = float3((uL + uR) * 0.5 * _LaneK * zcMix, IN.positionOS.y, dc);
    f.a = (uR - uL) * 0.5 * _LaneK * zcJG;
    f.b = htNom;
    f.progress = (d0 - _ZJudge) / (_Far - _ZJudge);
    return f;
}

#endif
