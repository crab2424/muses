# muses ステージ背景 — Unity 移植設計（2026-10-01）

採用: **神話の軌道都市 r3**（`design-lab/stage-bg/themes/orbit-r3.js`）と **天上の聖域 r3**（`sanctuary-r3.js`）。
経緯は `stage-bg-r1.md`。ノーツ側（キーキャップ・スキン切替）は移植済みなので着手（ユーザー指示 2026-10-01）。

## 1. 構成

| 役割 | ファイル |
|---|---|
| テーマの種類 | `Scripts/Stage/Background/BackgroundTheme.cs`（enum: None / OrbitCity / Sanctuary） |
| 背景の管理（生成・破棄・毎フレームの時刻） | `Scripts/Stage/Background/StageBackground.cs`（MonoBehaviour, ExecuteAlways） |
| テーマ1つ分の実装の基底 | `Scripts/Stage/Background/BackgroundBuilder.cs`（`Build(ctx, parts)` / `Tick(t)`） |
| 文脈（cfg・Derived・カメラ・暗部ゾーン・座標変換） | `Scripts/Stage/Background/BackgroundContext.cs` |
| 画面上で一定幅の線（lab の shared/lines.js） | `Scripts/Stage/Background/BgLineMesh.cs` ＋ `Shaders/Include/MusesBackground.hlsl` |
| テーマ本体 | `Scripts/Stage/Background/Themes/OrbitCityBackground.cs`, `SanctuaryBackground.cs` |
| テーマのシェーダ | `Resources/StageBackground/*.shader`（`Resources.Load<Shader>` で読む＝シーン配線不要・ビルドに必ず入る） |

- **StageController** が `BackgroundTheme` を持ち（既定 None＝従来の単色。譜面エディタのプレビューは None のまま）、
  Rebuild の最後（カメラ姿勢を決めた後）に `StageBackground.Rebuild` を呼ぶ。テーマ変更は即時反映（ノーツと違い譜面に依存しない）。
- **PlayerSettings.stageBackground**（既定 OrbitCity）→ GameController.ApplyPlayerSettings → StageController.BackgroundTheme。
  設定画面に「ステージ背景」ドロップダウン。
- テーマがあるとき、カメラのクリア色はテーマの clearColor、**ステージの面・線の色はテーマの tint で上書き**（StageView.ApplyTint）。
  ラボの groundJudge/skyJudge（判定線の色）は Unity では StageOverlay 側なので今回は対象外。

## 2. 描画順・深度

- 背景の各パーツは renderQueue **2900 + order**（order はラボの renderOrder をそのまま使う: −100〜−40 → 2800〜2860）。
  ステージ(3000〜)・ノーツ(3008〜)より必ず先。全て Transparent キュー（URP の半透明パスで queue 順に描かれる）。
- ラボは「背景 → 深度クリア → ステージ」だったが、Unity では深度クリアしない。ノーツ・拍線は ZTest Always、ステージ面は
  ZTest LEqual だが、背景の物体はステージの左右・下・奥にあり、視線上でステージ面より手前に来ないので結果は同じ。
  原則 **ZWrite Off / ZTest Always / Cull Off**。浮島のように自分自身の前後関係が要る不透明メッシュだけ ZWrite On・ZTest LEqual。
- **遠クリップ**: カメラの far は drawFar×1.5（≈135）しかないので、遠い物体は頂点シェーダで `BgClampFar` して far 面の手前に押し込む。

## 3. 座標と画面の約束（ラボ → Unity）

- **ラボは −z が奥、Unity は +z が奥**。`BackgroundContext.Lab(x, y, z)` = (x, y, −z) で座標をそのまま写す。
  z を反転すると左右手系が変わるので、Y 軸まわりの回転の向きは逆になる（回転の符号に注意）。カリングは Off にして巻き順の問題を避ける。
- ラボの `museBaseNdc()` = Unity では **頂点で clip を求め、フラグメントで xy/w、y に `_ProjectionParams.x` を掛ける**（`BgNdc`）。
  スクリーン空間の板は頂点の xy をそのまま NDC として `BgScreenClip` で出す（ラボの uBaseToClip / uFragToBase は不要）。
- レイキャスト（都市・雲海）: カメラの前・右・上ベクトルと tan(φ/2)・aspect をグローバル uniform で渡し `BgRayDir(ndc)`。
  当たった点はラボと同じ模様にするため `p = float2(hit.x, -hit.z)`（ラボの xz）で模様を計算する。
- 暗部ゾーン（ラボの zone.js）は C# で最遠端の断面を投影して求め、グローバル `_MusesBgZone` / `_MusesBgZoneFeather`。
  HLSL の `museSpawnMask(ndc)` はラボと同じ式。
- 時刻: グローバル `_MusesBgTime` = 実時間（`Time.realtimeSinceStartup`）。音楽・一時停止に反応しない（ラボと同じ）。
- 線の幅はラボと同じ「iPad 11"（縦1668px）換算の px」→ NDC 縦単位 `px*2/1668`。AA 余白は実画面の 0.5px。

## 4. 確認

- Unity Editor が開いている間は batchmode が使えないので、**コンパイル・シェーダエラーは `~/Library/Logs/Unity/Editor.log` で確認**する
  （Editor がファイル変更を検知して再コンパイルする）。見た目の確認はユーザーに依頼（Play して設定で背景を切替）。

## 5. 実装結果（2026-10-01、Unity 未確認）

- 軌道都市（Sonnet）: `OrbitCity.shader`（都市の全画面板）/ `OrbitGate.shader`（Opus、見本）/ `OrbitLines.shader`（神殿・足場の線）/
  `OrbitParticles.shader`（1粒子4頂点の板）。足場の上下は Tick で Transform。draw 6。
- 天上の聖域（Sonnet）: `Sanctuary{Cloud,Island,Glass,Ring,Lines,Rays,Monolith}.shader` ＋ `Shaders/Include/SanctuaryCommon.hlsl`。
  インスタンスは1メッシュに焼き、頂点のインスタンス番号＋マテリアルの float4 配列（CBUFFER 外）で配置・自転・上下を頂点シェーダで計算。
  メッシュはラボ座標のまま焼き、変換後に z だけ反転（回転の向き・光の向きの符号問題を避けるため）。浮島 ZWrite On、ガラス・輪・線は ZTest LEqual。draw 8。
- **色空間の注意（重要）**: ラボの shared/lines.js と軌道都市の粒子は `colorspace_fragment` を通しておらず、**出力値＝表示値（sRGB）**のまま
  加算していた（都市・ゲート・雲海などは linear → 変換あり）。Unity（linear 出力）では、軌道都市の線・粒子は暗い背景の上でラボと同じ見え方に
  なるよう「表示値 col·a² を pow 2.2 で linear にして Blend One One」で加算する。聖域の輪の線も pow 2.2（Sonnet 判断）、
  聖域の島の線はラボ側が pow 0.4545 で表示値化していたので Unity では外して linear のまま。
- Unity で最初に見る点: シェーダのコンパイル（Console / Logs/Editor.log）、ピンク（Resources.Load 失敗）、暗部マスクの上下、
  聖域のガラス・輪の表裏（Cull Front の推論）、島の線の深度バイアス、軌道都市の神殿の光の流れる向き。
