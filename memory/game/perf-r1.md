# ゲームシステムの軽量化 調査 r1（2026-08-11）

[[ipad-test-findings-r1]] の①⑤（メモリ331MB / CPU72% / Energy High / ビルド134MB）の深掘り。
r1 の暫定分析はコードだけを見て「ノーツメッシュ8万頂点」を GPU 60% の主犯と推定していたが、
**プロジェクト設定・シーン・URPアセットまで調べたところ、それより大きい主因が3つ見つかった**。
以下、実測ではなく設定/コードから確定できた事実と、そこから導いた見積もり。

---

## 0. 結論サマリ（着手優先度順）

| # | 項目 | 種別 | 手数 | 効果見込み |
|---|------|------|------|-----------|
| A | **Bloom+Tonemapping+Vignette が全フレーム有効** | GPU | 設定1つ | **特大** |
| B | **HDR有効による中間RT+ブリット** | GPU帯域 | 設定2つ | 大 |
| C | **AudioClip を全長PCM展開** (`streamAudio`未設定) | メモリ | コード3行 | **特大**(数十〜100MB) |
| D | 影の設定（不要なshadowmapパス） | GPU | 設定1つ | 中 |
| E | `StageOverlay` の毎フレーム全再構築 | CPU | 中規模 | 中 |
| F | `OnGUI` HUD（毎フレーム文字列生成） | CPU/GC | 小 | 中 |
| G | ノーツメッシュ全頂点の毎フレーム頂点シェーダ | GPU | 大規模 | 中 |
| H | 日本語フォント7.3MBがゲームビルドに同梱 | サイズ | 中 | 中(-5〜7MB) |
| I | Managed Stripping が Low のまま | サイズ | 設定1つ | 中 |
| J | 120fps固定 | GPU/電力 | 設定1つ | 大（ただし品質トレードオフ） |

**まず A・B・C・D だけやる。設定変更が主体で、合計30分程度・回帰リスクも低いのに、
CPU/GPU/メモリの3つ全部に効く。** G（チャンク分割）は実装コストが大きいので、
A〜Dの後に再計測してから判断する。

---

## 1. 【A】ポストプロセスが全フレーム走っている ← r1が見落としていた最大の主因

### 事実

- `Assets/Scenes/SampleScene.unity` の Main Camera: `m_RenderPostProcessing: 1`、
  `m_VolumeLayerMask.m_Bits: 1`（Defaultレイヤ）。
- 同シーンに `Global Volume`（layer 0）があり、`sharedProfile` =
  `Assets/Settings/SampleSceneProfile.asset`。
- そのプロファイルの中身:
  - **Bloom `active: 1`** — `intensity 0.25` / `threshold 1` / `maxIterations 6` /
    `downscale 0`(=Half) / **`highQualityFiltering: 1`**
  - **Tonemapping `active: 1`**
  - **Vignette `active: 1`**
  - MotionBlur `active: 0`（これだけ無効）

つまり Unity の新規プロジェクトテンプレートに最初から入っている
`SampleSceneProfile` を**そのまま使い続けている**。muses は自前の
unlit シェーダで色を直接指定して詰めてきた（note-visual-r1.md の
Linear/sRGB 一致合わせ、`rgb*1.3` のオーバーブライト撤去など）ので、
**このポスト処理は意図して入れたものではない可能性が高い**。

### なぜ重いか

Bloom は「ダウンサンプル → ぼかし → アップサンプル」のピラミッドで、
`maxIterations 6` なら**フルスクリーンのブリットが往復で最大12パス**走る。
さらに `highQualityFiltering: 1` は 9タップのバイリニアアップサンプルになり、
**Unity 自身がモバイルでは切るよう案内している設定**。加えて Tonemapping/Vignette の
uber パスがもう1枚。iPad の実解像度（例 2360×1640 × renderScale 0.8 ≒ 1888×1312）で
これを**120fps**で回している。

タイル型GPU（Apple）はフルスクリーンのブリット往復が最も苦手な処理なので、
**GPU 60.1% の大半はノーツの頂点処理ではなくここだと考えるのが自然**。
根拠としてもう一つ: ノーツの頂点数（後述、実譜面で数万〜十数万）は
Apple GPU の頂点スループットから見て「重いが 60% を占めるほどではない」水準。

### 対処

1. **まず切って比べる。** `SampleSceneProfile` の Bloom/Tonemapping/Vignette を
   `active: 0` にする（またはカメラの `m_RenderPostProcessing` を 0 に）。
   これだけで GPU 使用率がどれだけ落ちるかが、以降の判断基準になる。
2. 見た目としてBloomを残したいなら、**モバイル向け設定に落とす**:
   `highQualityFiltering: false` / `downscale: Quarter` / `skipIterations` を上げる。
   ただし現状の `intensity 0.25` / `threshold 1` は控えめなので、
   **切っても見た目がほとんど変わらない可能性が高い**（要目視確認）。
3. Vignette と Tonemapping も、色を自前で決めている以上は
   「意図した効果か」をユーザーに確認したい。特に **Tonemapping は
   note-visual-r1.md で苦労して合わせた色をさらに変換している**ので、
   切るほうが設計意図に合うはず。

**確認事項（ユーザー判断）: Bloom/Tonemapping/Vignette は意図して入れたものか、
テンプレートの残りか。** 後者なら全部切るのが正解。

---

## 2. 【B】HDR が有効で中間レンダーターゲットが強制される

### 事実

- `Assets/Settings/Mobile_RPAsset.asset`: `m_SupportsHDR: 1`
- `SampleScene` の Main Camera: `m_HDR: 1`、`m_AllowMSAA: 1`
- MSAA 自体は `m_MSAA: 1`（=1サンプル＝無効）なので、こちらは問題なし。
- `m_RenderScale: 0.8` は既に下げてある（良い）。
- `m_RequireDepthTexture: 0` / `m_RequireOpaqueTexture: 0`（良い、既に切れている）。

### なぜ重いか

HDR を有効にすると URP はバックバッファへ直接描かず、
**浮動小数点フォーマット（R11G11B10 等）の中間RTへ描いて最後にブリット**する。
このプロジェクトは全部 unlit で、色は 0〜1 に収まる値しか作っていない
（note-visual-r1.md で `rgb*1.3` のオーバーブライトを撤去済み）ので、
**HDR で得られるものが何も無いのに、帯域とブリット1枚を毎フレーム払っている**。

なお A（ポスト処理）を切ると、URP は条件次第で中間RTを省略できるようになるため、
**AとBは合わせてやると効果が乗る**。

### 対処

`Mobile_RPAsset` の `m_SupportsHDR: 0`、カメラの `m_HDR: 0`。
色の見え方が変わらないことだけ目視確認する。

---

## 3. 【C】音源が全長PCMで常駐（メモリ331MBの主犯）— r1の推定を裏付け

### 事実（確認済み）

`Assets/Scripts/Audio/AudioFileLoader.cs:80-90`:

```csharp
using var www = UnityWebRequestMultimedia.GetAudioClip(uri, audioType);
yield return www.SendWebRequest();
...
var clip = DownloadHandlerAudioClip.GetContent(www);
```

`DownloadHandlerAudioClip.streamAudio` は**既定 false** で、どこでも設定していない。
よって音源は**全長デコード済みPCMとしてメモリに展開される**。

```
44100Hz × 2ch × 4B(float) × 210秒(3分30秒) ≒ 74MB
5分の曲なら                                ≒ 106MB
```

331MB のうち 70〜110MB がこれ。r1 の推定は正しかった。

### 対処

`SendWebRequest()` の**前に** `streamAudio` を立てる:

```csharp
var www = UnityWebRequestMultimedia.GetAudioClip(uri, audioType);
((DownloadHandlerAudioClip)www.downloadHandler).streamAudio = true;
yield return www.SendWebRequest();
```

**注意点（r1から引き継ぎ、まだ未検証）**:
- ストリーミングクリップは同時に1つの AudioSource からしか再生できない。
  ゲーム側は BGM 1本なので問題ない。
- **譜面エディタの `PreviewSystem` はスクラブ（シーク）を多用する**ため、
  ストリーミングだとシーク応答が悪化しうる。`AudioFileLoader.Load` は
  ゲーム/エディタで共有しているので、**引数 `bool stream` で切り替える**のが安全
  （ゲーム=true / エディタ=false）。
- `SongClock` は `AudioSource.time` を読まず dspTime を正としている
  （song-play-flow-r1.md §4.2）ので、時計側への影響は無いはず。
  ただし実機で「Seek/Resume 直後に音が出るまでの遅延」を確認すること。

---

## 4. 【D】使われない影のパス

### 事実

- `SampleScene` に Directional Light（`m_Type: 1`）があり `m_Shadows.m_Type: 2`（Soft）。
- カメラは `m_RenderShadows: 1`。
- `Mobile_RPAsset`: `m_MainLightShadowsSupported: 1`、`m_MainLightShadowmapResolution: 1024`、
  `m_ShadowCascadeCount: 1`、`m_ShadowDistance: 50`。

一方で**このゲームのシェーダはライトを一切使っていない**:
`Note.shader` / `NoteBeatLine.shader` / `StageDepth.shader` はどれも
`LightMode` タグ無しの単一パス（＝ShadowCaster パスを持たない）。
`NoteView`（NoteView.cs:288-289, 310-311）も `StageView`（StageView.cs:140-141）も
`shadowCastingMode = Off` / `receiveShadows = false` を設定済み。

つまり**影を落とす物も受ける物も一つも無いのに、URP は毎フレーム
1024×1024 のシャドウマップを確保してクリアし、影のパスを回している**。

### 対処

`Mobile_RPAsset` の `m_MainLightShadowsSupported: 0`（＋シーンの Directional Light の
影をオフ、あるいはライト自体を削除）。unlit しか無いのでライト自体が不要のはず。

---

## 5. 【E】`StageOverlay` の毎フレーム全再構築（CPU側の主犯候補）

### 事実

`StageOverlay.cs:81-96` の `Update()` が**無条件に** `overlayRoot.MarkDirtyRepaint()` を呼ぶ。
その結果 `GenerateOverlay()` が 120fps で全部作り直される。中身は:

- 地平線（`showHorizon` 既定 false → 出ない）
- `DrawBand()` を Sky/Ground の2回
  - 帯の矩形2枚（`showBand` はシーンで **0**（`SampleScene.unity:573`）→ 出ない）
  - **アクティブセルのハイライト: `cells`(=12) 回のループ**（`showBand` に関係なく毎回）
  - セル区切り線 `cells+1` 本 ×2層（`showBand: 0` なので現状は出ない）
  - **判定線（`showJudgeLine: 1` → 出る）**
  - リップル（あるときだけ）
- 判定フラッシュ（あるときだけ）

`showBand: 0` のおかげで区切り線26本は出ていないので、r1 の見積もりより
実際の描画量は少ない。それでも **Painter2D のメッシュ生成 + UI Toolkit の
再レイアウト/再描画が 120fps で無条件に走る**構造そのものが CPU を食う。

### 対処（効果順、実装コスト順でもある）

1. **「変化が無いフレームは `MarkDirtyRepaint()` を呼ばない」ガードを足す。**
   変化するのは①フラッシュの有無/経過②リップルの有無/経過③占有セルの集合、の3つだけ。
   前フレームの (フラッシュ数, リップル数, 占有セルのビットマスク) を保持して
   一致したらスキップする。**何も押していない静止フレームでは完全に0になる**。
   これは10〜20行で書ける割に効果が大きい。
2. さらに詰めるなら、静的な部分（帯・区切り線・判定線・地平線）と動的な部分
   （フラッシュ・リップル・ハイライト）を別 `VisualElement` に分け、動的側だけ
   dirty にする。1 をやった後に効果を見てから判断でよい。

---

## 6. 【F】`OnGUI` の HUD

`StageOverlay.cs:287-343`。IMGUI は 1フレームに最低2回（Layout/Repaint）呼ばれる。
`showHud` はシーンで **1**（`SampleScene.unity:320`）＝有効。

- `DrawHud()` が `$"..."` の文字列補間を4本 → **毎フレーム8個以上の文字列ゴミ**。
  GC.Alloc が積もって定期的な GC スパイクになる（音ゲーでは判定のジッタに直結）。
- `GUIStyle` はフィールドにキャッシュ済み（`cellStyle`/`hudLineStyle`/`hudJudgeLineStyle`）
  で、この点は既に対処済み。`DrawCellIndex` の `new GUIStyle(style)` は
  `showCellIndex` 既定 false なので現状不発。

### 対処

- 最短: `showHud` を既定 OFF にする（デバッグ表示なので、
  fps 計測表示と同じくエディタ側の `showPerfStats` に倣って設定項目化してもよい）。
- 本筋: HUD を `AppController` 側の UI Toolkit ラベルへ移し、
  **`OnGUI` メソッド自体を消す**（メソッドが存在するだけで IMGUI パスが有効になる）。
  更新も毎フレームではなく値が変わったときだけにすれば文字列生成も消える。

---

## 7. 【G】ノーツメッシュ全頂点の毎フレーム頂点シェーダ

r1 の分析どおりの構造だが、**A〜D を先にやってから再計測して判断すべき**。
以下は調べた結果わかった、より安い改善案。

### 現状の頂点数（コードから確認）

- **インデックスバッファを一切使っていない**。`NoteView.cs:140-142` が
  `tris[i] = i` の恒等インデックスを作る。つまり `QuadThin`（NoteGeometry.cs:109-129）は
  **1つの四角形に4頂点ではなく6頂点**を積んでいる。Tap/ExTap/Flick/中継点マーカーは全部これ。
- Slide 帯（`PushSlideBand`, NoteGeometry.cs:287-）は
  コンボ区間ごとに `steps = max(2, ceil(区間長/0.03秒))`、1ステップ6頂点。
  **1秒の帯でおよそ 200 頂点**。長いスライドが多い譜面ほど支配的になる。
- Riser の壁（`PushRiserWall`）は `steps = 12` 固定 + 矢印3つ。

### 安い改善案（実装コスト小 → 大）

1. **`theta` をCPUで計算して uniform で渡す**（1行 + C#1行）。
   `NotePlacement.hlsl:123` の `float theta = atan2(_SinTheta, _CosTheta);` は
   **uniform だけから決まる定数なのに空中ノーツの全頂点で計算している**。
   同様に `vgj = VAt(_YCam, _ZJudge, theta)` / `vgf = VAt(_YCam, _Far, theta)`
   （124-125行）も**完全に定数**（`atan`+`tan` を各2回）。
   この3つを C# 側で計算して `_Theta` / `_Vgj` / `_Vgf` として渡すだけで、
   **空中ノーツ1頂点あたり `atan2`×1 + `atan`×2 + `tan`×2 を削減できる**。
   `hL` に依存する `vj`/`vf` は layerF 依存なので残るが、
   **layerF は実質 0 か 1 しか取らない**（層跨ぎ Slide/Riser だけが中間値）ので、
   `_VjSky`/`_VfSky` を渡して `layerF > 0.999` の場合だけ即値を使う分岐も有効。
   → **回帰リスクほぼゼロで空中ノーツの頂点コストを大きく削れる。最初にやる価値が高い。**
2. **インデックスバッファを使う**。`QuadThin` を4頂点+6インデックスにすれば
   タップ系の頂点数が **1/1.5 に減る**。ただし `vStart`/`vCount` による
   アルファ制御と `comboSegmentVertexRanges`（ipad-test-findings-r1 §④）が
   頂点範囲前提なので、そこの読み替えが要る。中規模。
3. **Slide 帯の分割間隔 0.03秒を可変にする**。0.03秒は
   `PlaceNote` の奥行き再マップの非線形性を吸収するための値だが、
   **判定線から遠いほど画面上の距離が縮む**（最遠部で23倍圧縮、note-visual-r1.md §3.1）ので、
   遠方は粗くてよい。ただし頂点は時刻固定でスクロールするため
   「生成時の距離」で決められない＝**素直にはできない**。保留。
4. **時間方向のチャンク分割**（r1 の案）。効果は桁で効くが実装コスト大。
   `vStart`/`vCount`・`comboSegmentVertexRanges` を全部チャンク内オフセットへ
   読み替える必要があり、④で作り込んだ区間管理と干渉する。**最後の手段**。

### なお `FlushAlpha` は既に適切

`NoteView.cs:223-235` は `alphaDirty`/`eatableDirty` でガードされており、
変化が無いフレームは何も転送しない。ただし**変化があった1フレームでは
メッシュ全体（`SetUVs` で全頂点分）を再アップロードする**ので、
ノーツが密な区間では毎フレーム数MBの転送になりうる。
チャンク分割（案4）はこれも同時に解決する。

---

## 8. 【H】【I】ビルドサイズ134MB

### 【H】日本語フォント 7.3MB がゲームビルドに入っている（r1 の「アセットは寄与していない」は誤り）

参照チェーンを guid で追って確認した:

```
Assets/Scenes/SampleScene.unity                        （唯一のビルド対象シーン）
  → Assets/UI/Game/GameOverlayPanelSettings.asset      (guid 17cc7100…)
    → Assets/UI/ChartEditor/Fonts/ChartEditorTextSettings.asset (guid 8e694edc…)
      → NotoSansJP-Regular SDF.asset  (2.1MB, m_AtlasPopulationMode: 1 = Dynamic)
        → NotoSansJP-Regular.ttf      (5.2MB)  ← Dynamicなので実体が同梱される
```

`m_AtlasPopulationMode: 1`（Dynamic）の TMP フォントアセットは
**実行時にグリフを焼くためにソースTTFのバイナリをビルドに含める**。
よって **7.3MB がゲーム本体のビルドに入っている**。

- `NotoSansJP-Variable.ttf`（9.1MB）は**どこからも参照されていない**ので
  ビルドには入らない。ただしリポジトリ上の死蔵ファイルなので削除してよい。
- ゲーム側UIは日本語を使う（「一時停止」「設定」「読み込み中」等）ので
  フォント自体は必要。
- **対処案**: ①日本語サブセット化した TTF に差し替える（常用漢字＋かな＋英数で
  5.2MB → 1〜1.5MB）。曲名に任意の漢字が来るなら Dynamic のまま
  サブセットフォントを使う（未収録字は豆腐になるので要判断）。
  ②あるいはゲーム用に**静的（Static）フォントアセット**を別途作り、
  UI固定文言だけ焼き込む（数百KB）。曲名は別途 Dynamic にフォールバック。
- **そもそもゲーム用 PanelSettings がエディタ用 TextSettings を流用しているのが
  混線の元**。ゲーム用の TextSettings を分けるのが筋。

### 【I】ビルド設定（`ProjectSettings.asset` から確認）

| 設定 | 現在値 | 評価 |
|------|--------|------|
| `stripEngineCode` | `1` | 良い（既に有効） |
| `managedStrippingLevel` | `{}`（未設定＝**Low**） | **Medium/High に上げる余地あり** |
| `iPhoneStrippingLevel` | `0` | 上と重複する旧設定 |
| `il2cppCodeGeneration` | `{}`（未設定＝Faster runtime） | **`Faster (smaller) builds` にする余地あり** |
| `targetDevice` | `2`（iPhone+iPad） | iPad専用なら絞れる（効果小） |
| `apiCompatibilityLevel` | `6`（.NET Standard 2.1） | 良い |
| `useOnDemandResources` | `0` | 妥当 |

- **`managedStrippingLevel` を Medium へ**: このプロジェクトはリフレクションを
  ほぼ使っていない（`JsonUtility` は使うが、これは stripping を意識した実装）。
  ただし **UI Toolkit / Input System が内部でリフレクションを使う**ので、
  剥がれすぎたら `link.xml` で保護する。**必ず実機で一度通しプレイして確認すること。**
- **`ChartEditorApp.cs`(3852行) + `ChartEditorApp.UI.cs`(3156行) がゲームビルドにも
  コンパイルされている**。asmdef が無く全部 `Assembly-CSharp` に入っているため。
  シーンから参照されていなくても、Low stripping では残る。
  **`ChartEditorApp` 用の asmdef を切ってプラットフォーム/シーン単位で分けるか、
  stripping を上げるかのどちらか**でネイティブコードを削れる。
- **`Development Build` を切る**（r1 の指摘どおり。最優先の比較基準）。
  これは `EditorUserBuildSettings`（バージョン管理外）なので、ビルド時に手で外す。

### 134MB の内訳の見立て

Unity 6.5 + URP + UI Toolkit + Input System + IL2CPP の iOS アプリとして
100MB台は異常ではない。大半は Unity ランタイムと IL2CPP 生成コード。
アセット由来は**フォント7.3MBがほぼ唯一**。
よって削減の主戦場は【I】のビルド設定側で、
**Development Build を切る＋stripping を Medium にする**で数十MB規模の変化が期待できる。

---

## 9. 【J】120fps 固定

`GameController.cs:57-58`:
```csharp
QualitySettings.vSyncCount = 0;
Application.targetFrameRate = 120;
```

エネルギー消費は素直に倍。音ゲーとして 120fps は価値があるので消す判断にはならないが、
**設定で 60/120 を切り替えられるようにする**のが妥当な落としどころ
（譜面エディタ側には既に `frameRateMode` があるので、同じものをゲーム設定に出す）。
`PlayerSettings` に `frameRateMode` を足して `ApplyPlayerSettings` から
`Application.targetFrameRate` を書けばよく、実装は小さい。

**ただし A〜D を先にやること。** 120fps を維持したまま余裕が出る可能性が高く、
「品質を落とさずに済むならそのほうがよい」ため。

---

## 10. 補足: DSPバッファと dspTime 更新レートの関係（既知の症状の裏付け）

`ProjectSettings/AudioManager.asset`: `m_DSPBufferSize: 1024`（Best performance）。

```
1024 / 44100Hz = 23.2ms → 43Hz … ただしバッファ2枚分で実効 23〜25Hz
```

これは [[muses-unity-port-history-phase5-game]] / ipad-build-issues-r1 ②-B で観測された
**「実機で dspTime が 23〜25Hz でしか更新されない」と数値が完全に一致する**。
つまりあの症状は iOS 固有の不具合ではなく、**DSPバッファサイズの当然の帰結**だった。

`SongClock` のフレーム補間で既に対処済みなので変更は不要。
**むしろ 1024 は CPU 的に最も軽い設定なので、軽量化の文脈では現状維持でよい**
（512 に下げると入力〜発音のレイテンシは半減するが CPU 負荷は上がる。
これは軽量化ではなく判定精度側のトレードオフとして別途判断する）。

---

## 11. A・B・C・D 実装後の実機計測結果（2026-08-13）

| 指標 | 施策前(2026-08-09) | 施策後 | 差 |
|------|------|------|------|
| CPU | 72% | **61%** | -11pt |
| メモリ | 331MB | **290.8MB** | **-40MB** |
| Energy Impact | High | High | 変化なし |
| Component: GPU | 60.1% | **46%** | -14pt |
| Component: CPU | 39.9% | **54%** | +14pt |

### 読み取れること

- **【A】【B】【D】は効いた。** Component Utilization の GPU が 60.1%→46% へ落ち、
  **主役が GPU から CPU へ入れ替わった**。§1 の「GPU 60.1% の大半はノーツの頂点処理ではなく
  ポストプロセス」という推定を裏付ける結果。r1 が主犯と見ていたノーツメッシュが主因なら、
  ポスト処理を切っても GPU 側はこれほど落ちなかったはず。
- **【C】も効いた。** -40MB は、2分の曲の全長PCM展開分
  （44100×2ch×4B×120秒 ≒ 42.3MB）とほぼ一致する。`streamAudio` は期待どおり働いている。
- **Energy Impact は High のまま。** 120fps 固定（§9【J】）を維持している以上、
  ここは動かないと考えるのが妥当。**下げたいなら 60fps 化が最も直接的**。
- **残り 290.8MB の内訳は未調査。** Development Build のオーバーヘッド、Unityランタイム、
  IL2CPP、URP/UI Toolkit、日本語フォントのアトラス、ノーツメッシュなどの合計。
  **リリースビルドでの再計測が先**（§8末尾）。

### 次に効くのは CPU 側

主役が CPU に移ったので、**perf-r1 の §5【E】(StageOverlayの毎フレーム全再構築) と
§6【F】(OnGUIのHUD) が次の本命**になった。これは施策前から予測していた順序どおり。
【E】の「変化が無いフレームは `MarkDirtyRepaint()` を呼ばない」ガードが
最も費用対効果が高い。

---

## 12. 譜面と楽曲が約100msズレる（2026-08-13報告、調査中）

2分の曲を通しでプレイ中、譜面と楽曲が約100msズレて感じられるとの報告。
**コードを読んだだけでは断定できない**ため、切り分け用の計測をHUDに追加した。

### まず確定できたこと: これは「累積するドリフト」ではない

`SongClock.Advance()`（v2実装）は、毎フレーム `smoothed += unscaledDeltaTime` で前進させ、
dspTime が更新されたフレームだけズレの10%(`DriftCorrectionRate`)を足し込む一次遅れフィルタ。
これは **2つの時計のレート差を定常偏差ゼロで吸収する**構造になっている:

- 時定数は約 `1/(0.10 × 25Hz) = 0.4秒`。
- システムクロックとオーディオクロックのレート差が仮に 100ppm あっても、
  定常偏差は `100ppm × 0.4秒 = 0.04ms` にしかならない。

よって**2分かけて100msに育つ種類のズレは、この時計からは構造上出てこない**。
`t0`/`smoothed` は double、`musicSource.time` へ渡す float も120秒地点で精度0.014ms。
**ズレは最初から一定量あると考えるのが妥当**（ユーザーの「流している間ズレている」という
表現とも矛盾しない）。

### 候補は2つに絞られる

**仮説①: オーディオ出力レイテンシ（従来からある性質。streamAudio化とは無関係）**

`AudioSettings.dspTime` は「今ミキシングしている位置」であって「今耳に届いている位置」ではない。
出力バッファに積まれた分だけ、実際の発音は dspTime より遅れる。
`m_DSPBufferSize: 1024` は 1024/44100 = **23.2ms/バッファ**で、Unityは通常複数段を積む。
さらに iOS のハードウェア出力レイテンシが乗る。**合計で50〜100ms規模になり得る**。

音源も判定も同じ dspTime 軸に載っているので**音と譜面判定は内部的には整合している**が、
**画面の描画だけはこのレイテンシを経由しない**ため、視覚が聴覚に先行する。
これを吸収するのが `visualOffsetMs` / `judgeOffsetMs`（§10のとおり、
1024というバッファサイズ自体は ipad-build-issues-r1 ②-B の
「dspTimeが23〜25Hzでしか更新されない」の正体でもある）。

**仮説②: ストリーミングクリップの再生開始遅れ（streamAudio化による回帰）**

`SongClock.ScheduleMusicAt` は `musicSource.time` を設定してから
`PlayScheduled(dspAnchor + preRoll)` で予約する。`ScheduleLeadSec` は **50ms**。

全長PCM展開（従来）なら、データは既にメモリ上にあるので50msのリードで十分だった。
**ストリーミングだとファイルのオープン・シーク・デコードのプリロールが要る**ため、
50msでは予約時刻に間に合わない可能性がある。間に合わなければ音は遅れて鳴り始めるが、
dspTime基準の時計は構わず進むので、**曲全体を通して一定量、音が譜面に対して遅れる**。

### 切り分ける方法（実装済み、要実機確認）

**`AudioSource.time` はミキサ上のサンプル位置＝dspTime基準の値であり、
「実際に耳に届いている位置」ではない。**この性質のおかげで、上の2つを1つの値で切り分けられる。

`SongClock.AudioScheduleErrorSec`（新規）を追加し、
`musicSource.time - (songTime + Offset)` をHUDに `audio ±XXms` として表示するようにした
（DSPブロック単位に量子化されて段々に動くため、GameController側でfpsと同じ要領で均している）。

- **0付近** → 音源はスケジュールどおり鳴っている。**仮説①**。
  streamAudio化は無罪で、ズレの正体は出力レイテンシ。
  → 対処は `judgeOffsetMs` / `visualOffsetMs` でのキャリブレーション（機能は既にある）。
- **負に大きい（-100前後）** → 音源が予約より遅れて鳴り始めている。**仮説②**。
  → 対処は後述。

前奏区間(`audioTime<0`)・鳴り始め直後・末尾は値が意味を持たないので0を返すようガードしてある。

### 仮説②だった場合の対処案（未実装、確定後に着手）

1. **ストリーミング時だけ `ScheduleLeadSec` を大きくする**（例 200〜300ms）。
   最も小さい変更。ただし「どれだけ取れば十分か」が環境依存で、保証が無いのが弱点。
2. **鳴らす前にプリロールを完了させる**。`AppController` のロード画面で
   一度 `musicSource.Play()`→即 `Stop()` してデコーダを温める、あるいは
   `AudioClip.LoadAudioData()` の完了を待ってから `StartGame()` する。
   ロード画面という待ち時間が既にあるので、体感を悪化させずに入れられる。
3. **ゲーム本体だけ `streamAudio` を戻す**（メモリ-40MBを諦める）。
   音ゲーは音の正確さが最優先なので、1・2で確実に潰せないならこれが正しい判断になりうる。
   **§3で挙げた懸念（シーク応答）が現実になった、という位置づけ**。

**なお、そもそも「リズムゲームのBGMをストリーミングにする」のは
音の正確さとメモリのトレードオフである**という点は §3 の時点で認識していた。
仮説②が当たりなら、-40MB がその代償に見合うかはユーザー判断になる。

### 確認事項

- **この100msは、streamAudio化(コミット`ed17713`)より前から感じていたか?**
  前からなら仮説①がほぼ確定する（②は今回の変更が原因なので前には存在し得ない）。
- 判定オフセット・描画オフセットは現在どの値か（既定は両方0）。
  0のままなら、出力レイテンシが未補正のまま残っている＝仮説①と整合する。

---

## 13. 次にやること

1. ~~**A・B・D の設定変更**~~ → **2026-08-11実装・2026-08-13計測完了**（§11）。
   GPU 60.1%→46%、CPU 72%→61%。推定どおりポスト処理が主因だった。
2. ~~**C（`streamAudio`）**~~ → **実装済み。-40MBを確認**（§11）。
   ただし**§12のズレとの関係が未確定**。
3. **【最優先】§12 のズレの切り分け。** HUDの `audio ±XXms` を実機で読む。
   ここが決まらないと「メモリ-40MBを取るか、音の正確さを取るか」の判断ができない。
4. 主役が CPU に移ったので、次は **E（オーバーレイの dirty ガード）→ F（OnGUI廃止）**。
   Fは§12の診断表示を消してからにする（HUDごと消すため）。
5. GPU をさらに削るなら **G-1（`theta`/`vgj`/`vgf` の uniform 化）**。
   それでも足りなければ G-2（インデックスバッファ）、最後に G-4（チャンク分割）。
6. Energy Impact を High から下げたいなら **J（60fps 切り替えを設定に出す）**が最も直接的。
7. サイズは **H（フォントのサブセット化/TextSettings分離）+ I（stripping Medium）**。
   その前に **Development Build を外して再計測**すること（比較基準がまだ取れていない）。

**逆に G-4（チャンク分割）は ④ で作り込んだコンボ区間の頂点範囲管理と干渉するので、
本当に必要と確認できるまで着手しないこと。**
