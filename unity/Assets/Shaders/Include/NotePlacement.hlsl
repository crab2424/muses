#ifndef MUSES_NOTE_PLACEMENT_INCLUDED
#define MUSES_NOTE_PLACEMENT_INCLUDED

// ノーツの頂点配置ロジック。移植元: web-prototype/src/notes.ts の vertexCommon (placeNote)。
// Note.shader と NoteBeatLine.shader の両方から include される。
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

CBUFFER_START(UnityPerMaterial)
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

// note-spec.md §5.5。スクロールグループごとの現在の表示位置 X(songTime)。配列で cbuffer の外に置く
// （cbuffer 内の float 配列は要素ごとに16byte境界へパディングされ無駄が出るため）。
// 要素数は NoteView.MaxScrollGroups と一致させること。
#define MUSES_MAX_SCROLL_GROUPS 16
float _GroupX[MUSES_MAX_SCROLL_GROUPS];

// positionOS = (u, y, ノーツの表示位置X(noteTime))。uv0.y = aNear, uv1.x = aLayerF, uv1.y = aSide。
// 戻り値はオブジェクト空間の座標（呼び出し側で TransformObjectToWorld する）。depthOut に奥行きを返す。
// groupX は呼び出し側があらかじめ _GroupX[] から引いた、このノーツのグループの現在の表示位置 X(songTime)
// （note-spec.md §5.5: 頂点に焼く値を「時刻」から「表示位置」に、uniformを「songTime」から「X(songTime)」に変える）。
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
// ---- 層ごとの奥行き再マップ（2026-08-06、game-rework-r1.md §1 の積み残しへの対処）----
//
// 従来は全層が同じワールド奥行き d0 を共有していた（判定タイミングを層非依存にするための設計）。
// しかし画面への写り方は面の高さで決まる: 空中面はカメラとの高低差が 1.88（地上は 8.0）しかなく、
// 同じ奥行き範囲が画面上の狭い帯に強く圧縮される。その結果、
//   1. 空中ノーツは自レーン長で正規化すると地上の 1.93 倍速く見え、奥でゆっくり→手前で急加速する
//      （game-rework-r1.md §1.2 の実測。当時は「構造的特性、ratio 1.0 は原理的に不可能」と結論した）
//   2. 同時刻の地上端と空中端を結ぶ Riser の壁が、画面上で最大 27.8% も横にずれて台形に傾く
// という2つの症状が出ていた。**どちらも同一の原因**で、d0 は「タイミングの正」ではあっても
// 「画面上の進み具合の正」ではない、という一点に尽きる。
//
// 一方でステージのパネル側は既に `zcFarGround` による層別の収束補正が入っており、
// **各層の台形は画面 x に関して互いに厳密な射影像**になっている（game-rework-r1.md §1.5-2 が
// 最遠端についてのみ述べていた性質は、実は帯の全域で成り立つ）。
// つまりズレていたのはステージ構造ではなくノーツの置き方だけだった。
//
// そこで「共有奥行き d0」ではなく「地上レーンの画面上の進み具合」を層間の対応付けに使う:
// 地上ノーツが自レーンの p% を消化した瞬間、層 L のノーツも自レーンの p% の位置に置く。
// 判定線 (p=0) と最遠端 (p=1) はどちらも従来と同じ奥行き (_ZJudge / _Far) に落ちるので、
// **パネルの描画範囲・near/far のフェード・判定は一切変わらず、帯の内側での進み方だけが変わる**。
//
// 実装は画面 v を経由せずに済む。求めたいのは「地上と同じ画面 x になる zc」で、
// x_ndc = U * zcMix / zc = U * (1 + c * (zcJudge/zc - 1)) を層間で等しいと置けば
//   zc_L = zcJudge_L / (1 + _LaneConverge * (R - 1) / c_L),  R = zcJudge_ground / zc_ground(d0)
// という四則演算だけの式になる（tan/atan 版と 3.6e-13 まで一致、Python で全層検証済み）。
// これで画面 x のズレは全層・全奥行きで厳密に 0 になり、正規化速度比も厳密に 1.0 になる。
// 地上 (layerF=0) は c_L = _LaneConverge となり R が約分されて d0 に戻る（＝恒等、拍線も無変更）。
float3 PlaceNote(float3 positionOS, float2 uv0, float2 uv1, float groupX, out float depthOut)
{
    // 地上基準の奥行き。タイミングの正であり、厚みもこの空間で足してから再マップする
    // （そうすると画面上の厚みも地上と揃う。空中だけ薄く見える問題も同時に解消する）。
    float d0 = _ZJudge + (positionOS.z - groupX) * _Speed;
    float halfThickness = max(_ZJudge * _ThicknessFrac, d0 * _ThicknessMinFrac);
    d0 += uv1.y * halfThickness;

    float yPlane = uv1.x * _SkyHeight;
    float a = (_YCam - yPlane) * _SinTheta;
    float zcJudge = a + _ZJudge * _CosTheta;
    float zcFar = a + _Far * _CosTheta;
    float c = clamp(_LaneConverge * (zcFar / _ZcFarGround), 0.0, 1.0);

    float aG = _YCam * _SinTheta;
    float R = (aG + _ZJudge * _CosTheta) / (aG + d0 * _CosTheta); // 地上の zcJudge/zc
    // _LaneConverge=0（レーンを画面上の長方形にする設定）では x が奥行きに依存しなくなり
    // この対応付け自体が定義できない。その場合だけ従来どおり d0 をそのまま使う。
    // denom の下限は、hiSpeed が大きく d0 が _Far を大きく超えたときに符号が反転して
    // ノーツがカメラ手前へ回り込むのを防ぐため（クランプ時は depth が _Far を大きく超え、
    // フラグメント側の `depth > _Far` で従来どおり破棄される）。
    float denom = max(1.0 + _LaneConverge * (R - 1.0) / max(c, 1e-4), 1e-3);
    float zc = (c > 1e-4) ? (zcJudge / denom) : (a + d0 * _CosTheta);
    float depth = (zc - a) / _CosTheta;

    float zcMix = lerp(zc, zcJudge, c);
    float x = positionOS.x * _LaneK * zcMix;
    depthOut = depth;
    return float3(x, positionOS.y, depth); // Unity は +z が奥（StageGeometry参照）
}

#endif
