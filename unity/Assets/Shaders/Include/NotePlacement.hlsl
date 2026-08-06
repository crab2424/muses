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
    float _TanHalfPhi;
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
// そこで「共有奥行き d0」ではなく「地上レーンの画面上の進み具合」を層間の対応付けに使う:
// 地上ノーツが自レーンの p% を消化した瞬間、層 L のノーツも自レーンの p% の位置に置く。
// 判定線 (p=0) と最遠端 (p=1) はどちらも従来と同じ奥行き (_ZJudge / _Far) に落ちるので、
// **パネルの描画範囲・near/far のフェード・判定は一切変わらず、帯の内側での進み方だけが変わる**。
//
// 「画面上の進み具合」は StageDerive.cs の Psi/DepthAt と同じ v(depth)=tan(theta-atan(h/depth))/tanHalfPhi
// を使う（面の高さ h、depth と1対1対応する本物のスクリーン垂直位置）。
//
// **再マップするのは帯の内側 [_ZJudge, _Far] だけで、その外側は従来どおり d0 をそのまま使う。**
// 両端は d0=_ZJudge→depth=_ZJudge / d0=_Far→depth=_Far と厳密に一致するので継ぎ目は出ない
// （実測 2e-7、float32 の精度そのもの）。この帯の内側では v は [vj, vf] に収まり
// 地平線から十分離れているため、極が存在しない。
//
// 【2026-08-06 訂正1】初版は v(depth) の代わりに X収束用のzc比を流用した除算ベースの式
// （zc_L = zcJudge_L / (1 + laneConverge*(R-1)/c_L)）で実装したが、判定線通過の約0.1秒後に
// 極を持ち、depth が符号反転して画面外へ跳ぶ実害があった。
//
// 【2026-08-06 訂正2】訂正1で tan/atan ベースに変えた際、**帯の外側を再マップ対象から
// 外し忘れていた**。pg は帯の外では [0,1] を外れるため、v が地平線(vHorizon=0.982)を
// 越えた時点で tan(psi) の符号が反転し、**判定線の約2.4秒より先にある全ノーツが
// 大きな負の depth になる**。負の depth はフラグメント側の `depth > _Far` を通過して
// しまう（＝破棄されない）ため、それらの頂点がカメラ背後に落ちて三角形が画面全体へ
// 引き伸ばされた（ユーザー報告: slideやtapが一斉に表示されてよくわからなくなる）。
// 譜面のほぼ全ノーツが該当するので影響は極めて大きい。
// **教訓: 検証は「見えている範囲」だけでなく、メッシュに含まれる全ノーツ
// （＝曲の最初から最後まで、tau が数十〜数百秒）の範囲で行うこと。**
// 初版の検証が読み先(1.2秒)以内しか見ていなかったのがこの見落としの原因。
//
// 地上判定線・最遠端の v 値は uniform で渡さずここで計算する。そうすると pg は構成上
// 厳密に d0=_ZJudge で 0・d0=_Far で 1 になり、C#側の値とのズレが原理的に生じない
// （zFar は StageDerive で zJudge*200 のクランプが掛かる場合があり、その際 Derived.vFar とは
// 一致しなくなる）。layerF=0 の地上ノーツと拍線は分岐で恒等式に落ちるため無コスト。
float VAt(float h, float depth, float theta)
{
    float psi = theta - atan(h / depth);
    return tan(psi) / _TanHalfPhi;
}

float DepthFromV(float h, float v, float theta)
{
    float psi = theta - atan(v * _TanHalfPhi);
    return h / tan(psi);
}

float3 PlaceNote(float3 positionOS, float2 uv0, float2 uv1, float groupX, out float depthOut)
{
    // 地上基準の奥行き。タイミングの正であり、厚みもこの空間で足してから再マップする
    // （そうすると画面上の厚みも地上と揃う。空中だけ薄く見える問題も同時に解消する）。
    float d0 = _ZJudge + (positionOS.z - groupX) * _Speed;
    float halfThickness = max(_ZJudge * _ThicknessFrac, d0 * _ThicknessMinFrac);
    d0 += uv1.y * halfThickness;

    float layerF = uv1.x;
    float yPlane = layerF * _SkyHeight;
    float hL = _YCam - yPlane;

    // 地上(layerF=0、拍線を含む)は恒等。帯の外側も恒等（上のコメント「訂正2」参照）。
    float depth = d0;
    if (layerF > 1e-6 && d0 > _ZJudge && d0 < _Far)
    {
        float theta = atan2(_SinTheta, _CosTheta);
        float vgj = VAt(_YCam, _ZJudge, theta);
        float vgf = VAt(_YCam, _Far, theta);
        float pg = (VAt(_YCam, d0, theta) - vgj) / (vgf - vgj); // 構成上 [0,1]
        float vj = VAt(hL, _ZJudge, theta);
        float vf = VAt(hL, _Far, theta);
        depth = DepthFromV(hL, vj + pg * (vf - vj), theta);
    }

    float a = hL * _SinTheta;
    float zcJudge = a + _ZJudge * _CosTheta;
    float zcFar = a + _Far * _CosTheta;
    float c = clamp(_LaneConverge * (zcFar / _ZcFarGround), 0.0, 1.0);
    float zc = a + depth * _CosTheta;
    float zcMix = lerp(zc, zcJudge, c);
    float x = positionOS.x * _LaneK * zcMix;
    depthOut = depth;
    return float3(x, positionOS.y, depth); // Unity は +z が奥（StageGeometry参照）
}

#endif
