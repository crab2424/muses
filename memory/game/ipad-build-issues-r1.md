# iPad実機ビルドの2件の不具合: 原因調査と対処設計 (r1)

対象コミット: `2d02f6a`（判定線オーバーレイのUI Toolkit化・120fps明示指定）時点。
症状は [[muses-unity-port-progress]] 末尾「iPad実機ビルド初回成功・2件の不具合は未解決」を参照。

- ① 判定線・オーバーレイ演出が実機で描画されない（Unity Editor Playでは出る）
- ② 見た目が30fps相当。HUDのfps表示は120と出ているのに動きが滑らかでない

**結論を先に**: ①と②は無関係な別々の原因で、どちらも「Unity Editorでは動くが
プレイヤービルドでは成立しない前提」を踏んでいる。両方ともコード側で直せる
（②のうち1点だけ Player Settings のチェックボックスが必要）。

---

## ① 判定線が描画されない

### 根本原因: 実行時生成した `PanelSettings` はプレイヤービルドで描画できない

`StageOverlay.Awake()`（`Assets/Scripts/Overlay/StageOverlay.cs:54`）は

```csharp
panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
```

で `PanelSettings` を**実行時に生成**している。一方、実機実績のある `ChartEditorApp` は
プロジェクト内の**アセット** `Assets/UI/ChartEditor/PanelSettings.asset` を使っている。
この差が決定的。

`PanelSettings.asset` の中身を見ると、UI Toolkitのレンダリングに必須の参照が
**アセットにシリアライズされている**：

```yaml
themeUss: {fileID: -4733365628477956816, guid: 1ea89cec6bdea43e796bd4ff72c7b1e3, type: 3}
m_AtlasBlitShader:          {fileID: 9101,  guid: 0000000000000000f000000000000000}
m_DefaultShader:            {fileID: 9100,  guid: 0000000000000000f000000000000000}
m_RuntimeGaussianBlurShader:{fileID: 20300, guid: 0000000000000000f000000000000000}
m_RuntimeColorEffectShader: {fileID: 20301, guid: 0000000000000000f000000000000000}
m_SDFShader:                {fileID: 19011, guid: 0000000000000000f000000000000000}
m_BitmapShader:             {fileID: 9001,  guid: 0000000000000000f000000000000000}
m_SpriteShader:             {fileID: 19012, guid: 0000000000000000f000000000000000}
m_ICUDataAsset:             {fileID: 20204, guid: 0000000000000000f000000000000000}
textSettings: {fileID: 11400000, guid: 8e694edc18b164bfb88c7be90e129b98, type: 2}
```

これらを**誰が埋めるのか**を、インストール済みUnity 6000.5.6f1のアセンブリの
シンボルで確認した:

| シンボル | 所在 |
|---|---|
| `PanelSettingsCreator` / `CreatePanelSettings` / `k_PanelSettingsAssetPath` | **UnityEditor**.UIElementsModule.dll |
| `defaultRuntimeTheme` / `s_BuiltInDefaultRuntimeTheme` / `BuiltInDefaultRuntimeThemeName` | **UnityEditor**.UIElementsModule.dll |
| `InitializeShaders`（割当済みShaderからMaterialを作るだけ） | UnityEngine.UIElementsModule.dll |

つまり **既定シェーダと既定テーマの解決処理はEditor側にしか存在しない**。
ランタイムモジュール側には `Shader.Find` によるフォールバックも既定テーマ取得もない
（`Hidden/Internal-UIR…` のようなシェーダ名文字列がランタイムdll内に一切存在しないことも確認済み）。

したがって:

- **Unity Editor Play**: `CreateInstance` がEditor側の初期化経路を通るため各フィールドが埋まり、描画される。
- **iOSプレイヤービルド**: Editorモジュールが存在しないため `m_DefaultShader` 等が **すべて null**、
  `themeUss` も null。パネルは描画に使うマテリアルを持てず、**何も描かれない**。
  さらに、これらの組込みシェーダはビルドに含めるべきと判断される参照が無いため
  **ビルドから除外(strip)されている**可能性も高く、仮に実行時に参照を差し込もうとしても取れない。

これは「Editorでは出るが実機では出ない」「同じUI Toolkit方式でもエディタアプリ側は実機で動く」という
観測結果の両方を、追加の仮定なしに説明する。

### 補強された切り分け（前セッションの宿題1はもう答えが出ている）

申し送りの「OnGUIのHUD文字が実機で見えているか」は、**見えている**。
②の報告「HUDには120fpsと表示されている」がその証拠で、この文字列は
`StageOverlay.DrawHud()`（OnGUI/IMGUI）が描いている。
よって **OnGUIは実機で正常に機能しており、問題はUI Toolkitパネルに限局している**。
これも上記の根本原因と整合する。

### 対処設計

**方針: 実行時生成をやめ、ゲーム用の `PanelSettings` アセットを作って参照する。**
`ChartEditorApp` で実機実証済みの経路に完全に合わせる。

1. `Assets/UI/Game/GameOverlayPanelSettings.asset` を新規作成
   （Project ウィンドウ > Create > UI Toolkit > Panel Settings Asset。
   この作成経路が上記のEditor側コードで、シェーダ/テーマ参照を焼き込んでくれる）。
   - `Theme Style Sheet` に既定の `UnityDefaultRuntimeTheme` が入っていることを必ず確認する
     （空だとテキスト系が壊れる。既存の `Assets/UI/ChartEditor/UnityDefaultRuntimeTheme.tss` を
     流用してもよい）。
   - `Clear Color` = off、`Scale Mode` = Constant Pixel Size
     （現行コードが `panelSettings.clearColor = false; scaleMode = ConstantPixelSize;` で
     設定しているのと同じ値をアセット側に持たせる）。
   - `Sorting Order` は既定の0のままでよい（ゲームシーンに他のUIDocumentは無い）。
2. `StageOverlay` に `[SerializeField] private PanelSettings panelSettingsAsset;` を追加し、
   `Awake()` の `CreateInstance` を削除。`uiDocument.panelSettings = panelSettingsAsset;` にする。
   `OnDestroy()` の `Destroy(panelSettings)` も削除する（アセットを破棄してはいけない）。
   - **アセットを共有インスタンスのまま使ってよいか**: このシーンでUIDocumentは1つだけなので
     共有で問題ない。将来 `referenceResolution` 等を実行時に変えたくなったら、
     `ChartEditorApp` と同じく `Instantiate(panelSettingsAsset)` したコピーに差し替える
     （コピーはシリアライズ済みフィールドを引き継ぐので、この不具合は再発しない）。
3. `SampleScene` の Main Camera の `StageOverlay` の Inspector で、新アセットをドラッグして配線する。
   **これによりアセットがビルドに含まれ、参照している組込みシェーダもstripされなくなる**
   ——ステップ2と3はセットで初めて意味を持つ。

**How to apply（一般則）**: `ScriptableObject.CreateInstance<T>()` は、Tが
「Editorのアセット作成時に既定参照を焼き込む」型のとき、プレイヤービルドで壊れる。
`PanelSettings` はまさにそれ。**UI Toolkitのランタイムパネルは必ずアセット経由で用意する。**

### 上記で直らなかった場合の次の切り分け（優先順）

1. `overlayRoot` に `style.backgroundColor = Color.red` を一時的に指定して全画面赤が出るか
   （出れば `generateVisualContent`/Painter2D だけの問題、出なければパネル自体がまだ死んでいる）。
2. Xcodeのデバイスコンソールで UI Toolkit 系の警告
   （"has no theme style sheet assigned" 等）が出ていないか確認する。
3. `Painter2D` の塗りつぶしパス自体は `ChartEditorApp` で実機実証済みなので疑う優先度は低い。

---

## ② 見た目が30fps

**2つの独立した原因が重なっている。** ②-Aは初回ビルド時の症状（HUDも30だった）を説明し、
`2d02f6a` の `targetFrameRate = 120` で既に解消している。**現在残っているのは ②-B。**
②-Cは「本当に120Hz出す」ために別途必要な設定。

### ②-A（解決済み）: iOSの既定 `targetFrameRate` は30

Unityのモバイルプラットフォームでは `Application.targetFrameRate` の既定が**30**。
`2d02f6a` 以前は何も指定していなかったため、ロジックも描画も30Hzで回っていた
（＝初回報告の「見た目・HUD表示の両方で30fps」と完全に一致）。
`GameController.Start()` での明示指定で解消済み。HUDが120を示すようになったのがその証拠。

### ②-B（未解決・本命）: ノーツの動きが `dspTime` のDSPバッファ粒度に量子化されている

**ここがHUD 120fps と 見た目30fps の食い違いの正体。**

ゲーム画面で動いているものは実質**ノーツだけ**である（ステージは静止、
ノーツは頂点シェーダが `_GroupX` uniform で毎フレーム配置する）。
そのuniformの供給源はこの1本道:

```
GameController.Update()
  → noteView.UpdateScroll(VisualTime(), hiSpeed)      // NoteView.cs:117
      → groupXBuffer[g] = tl.XAt(songTime)            // NoteView.cs:122
VisualTime() = clock.SongTime + visualOffsetMs/1000    // GameController.cs:66
SongClock.SongTime = AudioSettings.dspTime - t0        // SongClock.cs:73
```

**`AudioSettings.dspTime` はオーディオコールバック単位でしか進まない**
（連続時間ではなく、DSPバッファ1個ぶんずつ階段状に更新される）。
`ProjectSettings/AudioManager.asset` は:

```
m_SampleRate: 0          # = プラットフォーム既定
m_DSPBufferSize: 1024
m_RequestedDSPBufferSize: 0   # = Best performance
```

バッファ1024サンプルなので、dspTimeの更新間隔と、そこから決まる**ノーツの実効アニメーション周波数**は:

| 出力サンプルレート | 更新間隔 | 実効fps |
|---|---|---|
| 48000 Hz | 21.3 ms | **約47 Hz** |
| 24000 Hz（iOSでUnityが選びうる既定） | 42.7 ms | **約23 Hz** |

つまり描画は120Hzで回っていても、**ノーツの位置は毎フレーム同じ値のまま数フレーム据え置かれ、
数十msごとにまとめてジャンプする**。これが「30fpsに見える」の実体。
120Hzで表示するほどこの段差は目立つ（表示レートとdsp更新レートが約数関係にないため
不規則なコマ落ちにも見える）。Mac Editorでは表示が60Hz前後だったため相対的に目立たなかった。

**この仮説を実機で1分で検証する方法（実装前にやる価値が高い）**:
HUDに `AudioSettings.outputSampleRate` と `AudioSettings.GetDSPBufferSize(out int len, out int num)` の
値を出す。加えて **タッチのリップル演出（`Time.time`基準で滑らか）とノーツ（dspTime基準）を
見比べる**——リップルだけ滑らかならこの原因で確定する。

#### 対処設計: `SongClock` にフレーム補間を入れる（dspTimeは基準としては維持）

音との同期精度を落とさず、コマ送り感だけを消す標準的なやり方。
`dspTime` を**真の基準**として保持したまま、コールバックが来ない間は
`Time.unscaledDeltaTime` で前に進め、`dspTime` が更新されたときにドリフトを吸収する。

```csharp
// SongClock 内（概念コード）
private double lastDsp = -1;      // 最後に観測したdspTime
private double smoothed;          // 外へ返す滑らかな曲時刻
private const double MaxDrift = 0.05; // これを超えたら即スナップ（シーク/大きなハング後）

public void Advance(float unscaledDeltaTime)   // GameController.Update() の先頭で毎フレーム呼ぶ
{
    if (!Running) return;
    double dsp = AudioSettings.dspTime;
    if (dsp != lastDsp) {                       // オーディオコールバックが進んだフレーム
        lastDsp = dsp;
        double authoritative = dsp - t0;
        double drift = authoritative - smoothed;
        if (Math.Abs(drift) > MaxDrift) smoothed = authoritative;  // スナップ
        else smoothed += drift * 0.10;          // 徐々に寄せる（音ズレを蓄積させない）
    } else {
        smoothed += unscaledDeltaTime;          // コールバック間はフレーム時間で前進
    }
}
```

設計上の要点:

- **`SongTime`（＝描画用）だけを滑らかにし、判定用は分けるかどうか**を決める必要がある。
  推奨は**両方この滑らかな時刻を使う**こと。判定は入力イベント発生フレームの時刻と比較するので、
  むしろ補間したほうが実時間に近く精度が上がる（現状は最大±21ms/±43msの量子化誤差が
  判定にもそのまま乗っている——PERFECT窓33.33msに対して無視できない大きさである点に注意）。
  ただし [[muses-note-spec]] の判定窓を実機で詰める前に変えると評価が混ざるため、
  **①の修正・②の描画改善を先に入れて、判定への適用は独立した変更として分ける**のがよい。
- `Pause`/`Resume`/`Seek` は `smoothed` と `lastDsp` も一緒にリセットすること
  （`Seek` 直後は補間せず即スナップ）。
- 実際に曲音源を鳴らすようになったら、基準を `dspTime` ではなく
  `AudioSource.timeSamples`（同じく量子化されるので同様の補間が必要）に切り替える検討をする。

#### 併せて検討: DSPバッファを縮める

`Project Settings > Audio > DSP Buffer Size` を **Best latency**（256サンプル）にすると
更新間隔が 5.3ms（48kHz）/ 10.7ms（24kHz）になり、量子化そのものが目立たなくなる。
音ゲーとしては入力〜発音のレイテンシ低減の面でも望ましい。
ただしiOSではバッファを詰めるとオーディオドロップアウトのリスクが上がるため、
**補間実装（本命）を入れた上での追加改善**という位置づけにする。
`Output Sample Rate` も 48000 を明示指定しておくと挙動が予測可能になる。

### ②-C: `appleEnableProMotion: 0` のため、実機の表示は60Hzで頭打ち

`ProjectSettings/ProjectSettings.asset:262` が `appleEnableProMotion: 0`。
この設定がオフだと Unity は生成する Info.plist に
`CADisableMinimumFrameDuration = YES` を書き込まない。**このキーが無い限り
iOSは60fpsを超える表示を許可しない**ため、`Application.targetFrameRate = 120` は
表示側では満たされない（メインスレッドのループだけが120で回り、HUDはそれを見て120と表示する
——報告された食い違いのもう半分の説明になる）。

**対処**: `Player Settings > iOS > Other Settings > ProMotion Support` をオン。
（プロジェクトファイル直編集ではなくUnity EditorのGUIで行うこと。ビルド時にXcodeプロジェクトの
Info.plistへ反映される。）

**注意**: ②-Cを直しても②-Bは直らない。むしろ表示が本当に120Hzになると
dspTime量子化の段差は**より目立つ**。**②-B → ②-C の順で対処すること。**

### ②-D（優先度低・ついで）: 毎フレームのGCアロケーション

30fpsの主因ではないが、実機で断続的なヒッチを生むので気づいた点を記録しておく。

- `StageOverlay.OnGUI()` が毎フレーム `new GUIStyle` を複数生成している
  （`OnGUI` は1フレームにLayout/Repaintで2回以上呼ばれるため、実際はその倍）。
  `DrawHud()`・`DrawCellIndex()` も同様。**staticフィールドにキャッシュすれば消せる。**
- `StageOverlay.Update()` の `RemoveAll(f => ...)` はラムダがクロージャ（`now` をキャプチャ）なので
  毎フレームデリゲートを確保する。`now` をフィールドにするだけで消える。
- `overlayRoot.MarkDirtyRepaint()` を毎フレーム呼んでいるため、オーバーレイのメッシュを
  毎フレーム作り直している。判定フラッシュ/リップルが存在しないフレームはスキップできる。

### ②-E（要確認・ビルド衛生）: ビルドシーンにSmokeTestが残っている

`Assets/Scenes/SampleScene.unity` に `SmokeTest` GameObject が残っており、
2つのスクリプト（`StageDeriveSmokeTest`/`StageGeometrySmokeTest`/`JudgeSmokeTest` のいずれか）が
アタッチされたまま実機ビルドに含まれている。Start時のログ出力だけなら実害は小さいが、
実機ビルドではオフにしておくのが望ましい（Consoleログはデバイス上でも相応にコストがかかる）。

---

## 作業順序（推奨）

1. **②-Bの検証**（HUDに `outputSampleRate`/`GetDSPBufferSize` を出す＋リップルとノーツの見比べ）。
   コード数行で確定でき、以降の判断の前提になる。
2. **①の修正**（PanelSettingsアセット化＋Inspector配線）。演出が見えないと以降の確認全般が不便。
3. **②-Bの修正**（`SongClock` へのフレーム補間）。
4. **②-Cの設定変更**（ProMotion Support オン）、その上で改めて実機の見た目を確認。
5. 必要なら DSP Buffer Size = Best latency / Output Sample Rate = 48000、②-Dのアロケーション整理。
6. 判定用時刻にも補間を適用するか（②-Bの設計メモ参照）は独立した変更として後で判断する。
