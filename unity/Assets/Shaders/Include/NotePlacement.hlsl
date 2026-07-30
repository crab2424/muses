#ifndef MUSES_NOTE_PLACEMENT_INCLUDED
#define MUSES_NOTE_PLACEMENT_INCLUDED

// ノーツの頂点配置ロジック。移植元: web-prototype/src/notes.ts の vertexCommon (placeNote)。
// Note.shader と NoteBeatLine.shader の両方から include される。
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

CBUFFER_START(UnityPerMaterial)
    float _SongTime;
    float _ZJudge;
    float _Speed;
    float _Far;
    float _HardFar;
    float _YCam;
    float _SkyHeight;
    float _SinTheta;
    float _CosTheta;
    float _LaneK;
    float _LaneConverge;
    float _ZcFarGround;
    float _ThicknessFrac;
CBUFFER_END

// positionOS = (u, y, ノーツ時刻)。uv0.y = aNear, uv1.x = aLayerF, uv1.y = aSide。
// 戻り値はオブジェクト空間の座標（呼び出し側で TransformObjectToWorld する）。depthOut に奥行きを返す。
//
// aSide (-1/近い側, 0/無関係, +1/遠い側): タップ/ホールド始点は奥行き方向に薄い板で、
// ワールド単位で固定の厚みだと遠方ではパース(遠近法)で画面上の見かけの厚みが縮み、
// 1ピクセル未満になって描画が欠落し点滅して見える問題があった。
// aSide!=0の頂点だけ、中心の奥行き(depth)に比例した厚みを足し引きすることで、
// 距離によらず画面上の見かけの厚みをほぼ一定に保つ（近距離では従来と同じ見た目になるよう
// _ThicknessFrac は元の固定厚み thicknessWorld = zJudge*0.05 の半分の割合 (0.025) を既定値にしている）。
float3 PlaceNote(float3 positionOS, float2 uv0, float2 uv1, out float depthOut)
{
    float depth = _ZJudge + (positionOS.z - _SongTime) * _Speed;
    depth += uv1.y * depth * _ThicknessFrac;
    float yPlane = uv1.x * _SkyHeight;
    float a = (_YCam - yPlane) * _SinTheta;
    float zc = a + depth * _CosTheta;
    float zcJudge = a + _ZJudge * _CosTheta;
    float zcFar = a + _Far * _CosTheta;
    float c = clamp(_LaneConverge * (zcFar / _ZcFarGround), 0.0, 1.0);
    float zcMix = lerp(zc, zcJudge, c);
    float x = positionOS.x * _LaneK * zcMix;
    depthOut = depth;
    return float3(x, positionOS.y, depth); // Unity は +z が奥（StageGeometry参照）
}

#endif
