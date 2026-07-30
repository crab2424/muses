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
    float _ThicknessMinFrac;
CBUFFER_END

// positionOS = (u, y, ノーツ時刻)。uv0.y = aNear, uv1.x = aLayerF, uv1.y = aSide。
// 戻り値はオブジェクト空間の座標（呼び出し側で TransformObjectToWorld する）。depthOut に奥行きを返す。
//
// aSide (-1/近い側, 0/無関係, +1/遠い側): タップ/ホールド始点は奥行き方向に薄い板。
// 厚みの決め方には2つの要求があり、両立させるために max() を取っている:
//   1. 遠近感: ワールド単位で固定の厚み(_ThicknessFrac * zJudge、元の thicknessWorld = zJudge*0.05 相当)。
//      奥のノーツほど画面上で薄く、手前に来るほど厚く見え、奥行き感が出る。基本はこちら。
//   2. 点滅防止: 1.だけだと遠方(zFar/zJudge≈25倍)で見かけの厚みがサブピクセルになり、
//      ラスタライズが欠落してフレームごとに丸ごと消えて点滅する。そこで
//      「奥行きに比例した厚み(_ThicknessMinFrac * depth)」を下限として与える。
//      これは画面上で一定の厚みに相当するので、最遠部でも必ず一定ピクセル数を確保できる。
// _ThicknessMinFrac は _ThicknessFrac よりずっと小さく、遠方でのみ下限として効く
// （depth > zJudge * _ThicknessFrac / _ThicknessMinFrac の範囲だけ 2. が勝つ）。
float3 PlaceNote(float3 positionOS, float2 uv0, float2 uv1, out float depthOut)
{
    float depth = _ZJudge + (positionOS.z - _SongTime) * _Speed;
    float halfThickness = max(_ZJudge * _ThicknessFrac, depth * _ThicknessMinFrac);
    depth += uv1.y * halfThickness;
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
