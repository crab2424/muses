# iPad実機テスト(2026-08-09)で報告された5件の調査結果 r1

`cbf9c70`(曲の読み込み・再生フロー)以降、初めて自作譜面を通しで実機プレイした際の報告5件。
着手順序の検討 → 原因調査 → 一部修正まで。

| # | 症状 | 種別 | 状態 |
|---|------|------|------|
| ③ | 判定演出(HitFlash)が一切描画されない | **回帰** | 原因確定・修正済み |
| ② | 判定/描画オフセットの可変幅が狭い | UI改善 | 幅拡大済み(±150→±1000ms) |
| ④ | Ground slideノーツのみ判定線上で消えない(Skyは消える) | 仕様の副作用の疑い | 有力説明あり・実機再確認待ち |
| ⑤ | CPU72% / メモリ331MB / Energy High | 概ね妥当だが改善余地大 | 原因分析済み・判断待ち |
| ① | ビルド134MB / 「ステージエンジン」100MB超 | 誤帰属の疑い | 原因分析済み・判断待ち |

---

## ③ 判定演出が描画されない（回帰、原因確定・修正済み）

### 根本原因: `born` は songTime、比較相手は `Time.time`

`Judge.CommitJudgement`/`CommitMiss` は `Flashes.Add(new HitFlash { born = songTime, ... })` と
**曲時間**で記録する。`TouchInputManager.Emit` の `Ripples.Add((layer, cell, at))` も同じく
`at = clock.SongTime` 由来。ところが受け手の `StageOverlay` は

- `Update()`: `cleanupNow = Time.time;` → `flashExpired = f => cleanupNow - f.born >= 0.45f`
- `GenerateOverlay()`: `float now = Time.time;` → `k = (now - f.born) / 0.45f`

と**アプリ起動からの経過時間**で比較していた。単位が違う2つの時計を引き算していたことになる。

### なぜ今まで動いていたか

`0ad63ab` 時点の `GameController.Start()` は末尾で `clock.Start()` を呼んでおり、
シーン開始と同時に songTime が0から走り出していた。つまり `Time.time ≒ songTime` が
偶然成立していたため、この不整合は表に出なかった。

`cbf9c70` でタイトル画面(START押下)→ロード→プレイの状態機械(`AppController`)を導入した結果、
`clock.Start()` は**ユーザーがSTARTを押した瞬間**まで遅延するようになった。
タイトル/ロード画面に居た時間(実測で数秒〜数十秒)がそのまま `Time.time - songTime` の
差になり、`now - born` が常に 0.45 を大きく超える → **生成した瞬間に期限切れ扱いで
削除・描画スキップ**。「デモ譜面にあった判定演出が出ない」という報告と完全に一致する。

**副作用として、タッチのリップル演出(`input.Ripples`)も同じ理由で全く出ていない**
（こちらは未報告だが同一原因）。

### 修正

`StageOverlay` の2箇所を `hudSongTime`（`SetHudTime()` で `GameController` から毎フレーム
渡される `clock.SongTime`）基準に変更した。`born` と単位が揃う。

- ポーズ中は songTime が凍結するので演出も凍る。これは望ましい挙動。
- タイトル画面では songTime=0 かつ Flashes も空なので影響なし。

### 教訓

**`born`/`at` のような時刻フィールドは「どの時計の値か」を型か命名で表現すべき。**
`float` のまま2つの時計を混ぜたため、コンパイラも通り、実機で初めて症状として出た。
`HitFlash.born` のコメントに「songTime基準」と明記するだけでも再発は防げる。
同種の危険箇所: `Contact.layerHandoffUntil`(songTime基準)、`Contact.history[].t`(songTime基準)。
いずれも現状は正しく songTime 同士で比較されている。

---

## ② オフセット（訂正: ゲーム内は2種類のみ。可変幅を拡大）

### 訂正: 「楽曲オフセット」はゲーム内のどこにも表示されない

r1初版で「3種類ある」と説明したのは誤解を招く書き方だった。`SongMeta.offsetSec`
（音源先頭→譜面tick0のズレ）は**譜面エディタで譜面ごとに一度だけ設定する値**で、
プレイ画面の設定(ポーズ→設定)には一切出てこない。実機で触れるのは
**判定オフセット(`judgeOffsetMs`)と描画オフセット(`visualOffsetMs`)の2つだけ**で、
ユーザーの観測（「ゲーム側には2種類しか表示されていない」）が正しい。

コードを追った限りこの2つは正しく実装・反映されている。
`GameController.ApplyPlayerSettings` がスライダー変更のたび `cfg` へ書き戻し、
`JudgeTime()`/`VisualTime()` が毎フレーム参照する。

### 変更したこと

可変幅を **±150ms → ±1000ms** に拡大（`AppController`）。±150msでは端まで振っても
体感差が小さく、「効いていない」のか「効き幅が足りない」のか切り分けられなかった。
判定窓(GOOD半幅100ms)を大きく超える範囲を取れるので、意図的に極端な値を入れて
反映の有無を確認する用途にも使える。

### 未着手の検討事項

- 楽曲オフセットをプレイ中に微調整する口が無い（現状は譜面エディタで直すしかない）。
  プレイ画面から譜面の `offsetSec` を書き換えて保存する導線を作るかどうかは別途判断。
  なお `SongClock.Offset` は `ScheduleMusicAt`(Start/Resume/Seek)でしか反映されないので、
  プレイ中に動かすなら再スケジュールが要る。

---

## ④ Ground slideノーツのみ判定線上で消えない（Skyは消える）

### 追加報告（2026-08-09）で判明した重要な絞り込み

初回報告は「GOOD以上のノーツが判定線上で消えない」だったが、実際は
**「groundに存在するslideノーツのみ消えない。skyのノーツはMISSでも消えている」**。
これで2つのことが分かる: (a) Tap/ExTap/Flickは両層とも正しく消えている
（③の判定演出の見えなさと混同していただけ）、(b) 症状はSlide種別・Ground層限定。

### 有力な説明: 意図的な近距離フェード設定の非対称が原因の可能性が高い

`StageDerive.cs:194-196`:
```csharp
// 手前側の消える位置。空中は判定線で切ると見やすい（ユーザー要望）
float groundNear = gbNear;                                   // 帯の下端(vGroundBot)の奥行き
float skyNear = cfg.skyFloorFromJudge ? zJudge : sbNear;      // 既定true → 判定線の奥行きそのもの
```
`StageConfig.cs:163` で `skyFloorFromJudge = true` が既定値。これは**過去のユーザー要望で
意図的に入れた仕様**（コメントに明記）で、Skyのノーツは判定線を通過した瞬間、
**判定結果(GOOD/MISS/未判定を問わず)に関わらず幾何学的にフェードアウトして消える**。
一方Groundは帯の下端(カメラにかなり近い位置)まで消えない。

これと`Judge.UpdateSlide`の仕様「Slideの帯は1メッシュ1alphaで、**全コンボ点を消化し
終えるまで隠れない**（判定線を過ぎた分だけを個別に隠す仕組みは無い）」を組み合わせると、
観測結果は矛盾なく説明できる:

- Slideは判定線を通過した後も、未消化のコンボ点が残っていれば帯全体が表示され続ける
  （これは仕様通りの動作）。
- **Sky**はこの「まだ表示中」の状態でも判定線で強制フェードするため、ユーザーからは
  「（MISSでも）消えた」ように見える。
- **Ground**は同じ状態でもカメラ間近までフェードしないため、
  「判定線を過ぎても居座っている」ように見える。

**つまりGround/SkyでJudge側の判定ロジックに差は無く、見え方の非対称は
意図的な近距離フェード設定の副作用である可能性が高い。実バグではなく仕様の相互作用。**
Tap/ExTap/Flickは1回の判定で即座に`alpha=0`になり帯を持たないため、
この非対称は露呈しない（両層とも正しく消える、という(a)の観測と一致する）。

### 未確定な点・確認したいこと

上記は「まだ未消化のコンボ点が残っている間はGroundが判定線を過ぎても表示され続ける」
までは説明するが、**最後のコンボ点まで消化し終えた後もなお消えないなら別の実バグ**
（`rt.nextComboIndex`が`comboTimes.Count`に到達しない経路、Judge.cs側の未検出の不具合）
の可能性が残る。コードを読む限りこの完了条件自体は層に依存しないため、そちらの経路に
バグがあるならSky側でも本来同じことが起きているはずだが、近距離フェードに隠れて
気づいていないだけという可能性もある。

### 次にやること

実機で「GOOD以上が出たあと、そのSlideの最後のコンボ点を過ぎてから数秒待っても
Groundのノーツが消えないか」を確認する。
- **消えるなら**: 上記の説明で決着（バグではない、仕様上の見え方の差）。対応するなら
  `groundNear`を`skyFloorFromJudge`と同様に「判定線で切る」オプションへ寄せるか、
  Slideの帯を判定線通過分だけ隠すよう作り替えるか、のUI/設計判断になる。
- **消えないなら**: `UpdateSlide`の完了条件周りの実バグを疑う。次の一手は
  `ResolveSlideComboPoint`呼び出し前後で`rt.nextComboIndex`をログし、
  最後のコンボ点まで正しく到達しているかを実機ログで確認する。

---

## ⑤ CPU72% / メモリ331MB / Energy High は妥当か

### 結論: 「Highになるのは今の作りなら当然」だが、**下げ代は大きい**

#### 前提: これは Development Build + Xcodeデバッガ接続中の計測

Xcodeから実行してInstrumentsを当てている以上、開発ビルドである可能性が高い。
開発ビルドはプロファイラのフック・スタックトレース収集・デバッグシンボルを含み、
**CPUとバイナリサイズの両方を押し上げる**。リリースビルド(`Development Build`のチェックを
外す)での再計測が、あらゆる最適化より先に来る比較基準になる。

#### 高負荷の構造的な要因（大きい順）

**1. 120fps 固定描画（GPU 60.1% の主因）**

`GameController.Start()` で `QualitySettings.vSyncCount = 0; Application.targetFrameRate = 120;`。
ProMotionに追従させる意図だが、**エネルギー消費は素直に倍**になる。
音ゲーとして120fpsは価値があるので消す判断にはならないが、
「設定で60/120を切り替えられるようにする」のが妥当な落とし所。
（譜面エディタ側には既に `frameRateMode` があるので、ゲーム側にも同じものを出す）

**2. ノーツメッシュ全頂点を毎フレーム頂点シェーダへ通している**

`NoteView` は**曲1本分のノーツを1つのメッシュに一括生成**する（600秒/BPM150のデモ譜面で
約8万頂点、`IndexFormat.UInt32` が必須な規模）。位置は頂点シェーダ内で `time` と
`_GroupX` から計算されるため、**画面に映らない曲の最後の頂点も含めて毎フレーム全部が
頂点シェーダを通る**。8万頂点 × 120fps = **毎秒960万回の頂点シェーダ実行**。
`PlaceNote` は tan/atan を含む重い関数なので、これがGPU 60.1%の実体。

改善案（効果順）:
- **時間方向のチャンク分割**: 曲を例えば10秒ごとのサブメッシュに切り、現在時刻の
  前後だけ描画する。実装コストは中程度だが効果は桁で効く（8万→数千頂点）。
  `vStart/vCount` によるアルファ制御の仕組みはチャンク内オフセットに読み替えれば維持できる。
- 上記の前段として、`RecalculateBounds` に頼らずチャンクごとに手でboundsを与えれば
  カリングも効くようになる。

**3. `ZTest Always` + `Cull Off` + アルファブレンドによるオーバードロー**

デプス棄却が一切効かないため、重なったノーツは全部ラスタライズされる。
`ZTest Always` は地面とのZファイティング対策で入れた経緯（Note.shader冒頭のコメント）が
あるので簡単には外せない。ただし `Cull Off` は、ノーツが常に手前を向く板であれば
`Cull Back` にできる可能性があり、これは1行で効く。**要検証**。

**4. `StageOverlay` の毎フレーム全再構築（CPU側の主犯候補）**

`Update()` が無条件に `overlayRoot.MarkDirtyRepaint()` を呼ぶため、
**判定帯・セル区切り線(cells+1本 × 2層)・判定線・フラッシュ・リップルの
Painter2Dメッシュを120fps全部作り直している**。実際に変化するのはフラッシュ・リップル・
アクティブセルのハイライトだけで、帯と区切り線は静的。

改善案: 静的な部分（帯・区切り線・判定線・地平線）と動的な部分（フラッシュ・リップル・
ハイライト）を別の `VisualElement` に分け、動的側だけ `MarkDirtyRepaint()` する。
さらに「フラッシュもリップルも空 かつ 占有セルに変化なし」のフレームは
`MarkDirtyRepaint()` 自体を省ける。

**5. `OnGUI()` が毎フレーム走っている（GCアロケーションの主犯）**

IMGUIは1フレームに最低2回(Layout/Repaint)呼ばれる。現状 `OnGUI` は:
- `DrawHud()` が毎回 `$"..."` の文字列補間を4本 → **毎フレーム8個以上の文字列ゴミ**
- `DrawCellIndex()` が `new GUIStyle(style)` を毎回生成（`showCellIndex` 既定falseなので今は不発）

`showHud` を既定OFFにするか、HUDをUI Toolkit側(`AppController`のラベル)へ移して
`OnGUI` を完全に削除するのが筋。**IMGUIはUnityで最もCPUを食うUI経路**なので、
毎フレーム回すのは避けたい。`showTouchDebug`/`showCellIndex`/`showHorizon`/`showSplitLine`/
`showBand` は既定falseなので、残る負荷は実質HUDだけ。

### 妥当性の判定

- **Energy Impact "High"**: 120fps描画 + GPU60% + 毎フレームUI再構築なら**当然High**。
  異常ではないが、リリース品質としては下げるべき。
- **CPU 72%**: これは高すぎる。開発ビルドのオーバーヘッドを差し引いても、
  上記4(オーバーレイ再構築)と5(OnGUI)で説明が付く範囲。**60fps + オーバーレイの
  差分描画化 + OnGUI廃止で大幅に下がるはず**。
- **GPU 60.1% / CPU 39.9%（Component Utilization）**: GPU優勢は要因2の裏付け。

---

## ① ビルド134MB / メモリ100MB超は妥当か

### 「ステージエンジンが100MB超」は誤帰属の可能性が高い

`Assets/StreamingAssets` は空、`Resources` も無く、大きなテクスチャ・音声アセットは
存在しない。ステージとノーツは全て**手続き的に生成されるメッシュ**で、
最大のノーツメッシュでも:

```
8万頂点 × (位置12B + 色16B + UV0..3 32B) ≒ 60B/頂点 ≒ 4.8MB
```

CPU側コピーとGPU側を合わせても**10MB程度**にしかならない。100MB超の説明にはならない。

### 331MB の主犯候補: 非圧縮のまま常駐している AudioClip

`AudioFileLoader.Load` は `UnityWebRequestMultimedia.GetAudioClip` →
`DownloadHandlerAudioClip.GetContent(www)` で取得している。
**`DownloadHandlerAudioClip.streamAudio` は既定 false** なので、
Unityは音源を**全長デコード済みPCMとしてメモリに展開**する。

```
44100Hz × 2ch × 4B(float) × 210秒(3分30秒) ≒ 74MB
5分の曲なら           ≒ 106MB
```

**これが「100MB超」の正体である可能性が非常に高い**。Instrumentsのカテゴリ分けで
オーディオバッファがどこに計上されるかによっては「エンジン側」に見える。

#### 対策

`SendWebRequest()` の**前に** streamAudio を立てる:

```csharp
var www = UnityWebRequestMultimedia.GetAudioClip(uri, audioType);
((DownloadHandlerAudioClip)www.downloadHandler).streamAudio = true;
yield return www.SendWebRequest();
```

これで圧縮のまま保持しストリーミング再生になり、**数十MB〜100MB規模の削減**が見込める。

注意点（要確認）:
- ストリーミングクリップは**同時に1つのAudioSourceからしか再生できない**。
  ゲーム側はBGM1本なので問題ない。**譜面エディタのプレビュー(`PreviewSystem`)は
  スクラブ(シーク)を多用するため、ストリーミングだとシーク応答が悪化する可能性がある**。
  `AudioFileLoader.Load` は両者で共有しているので、**引数でストリーミング可否を
  切り替える**のが安全（ゲーム=true / エディタ=false）。
- `musicSource.time` の代入と `PlayScheduled` の組み合わせは維持できるはずだが、
  実機で「シーク直後に音が出るまでの遅延」を確認すること。

### ビルド134MB について

Unity 6.5 + URP + UI Toolkit + Input System + IL2CPP のiOSアプリとして、
134MBは**やや大きいが異常ではない**。内訳の大半はUnityランタイムとIL2CPPが生成した
ネイティブコードで、このプロジェクトのアセットはほぼ寄与していない。

削減の余地:
1. **Development Build を切る**（デバッグシンボル分が消える。効果大）
2. **Managed Stripping Level**: `ProjectSettings.asset` の `managedStrippingLevel: {}` は
   未設定＝既定(Low)。**Medium/High** に上げると未使用のマネージドコードが削られる。
   リフレクションを使っていないプロジェクトなので比較的安全だが、`JsonUtility` や
   UI Toolkit周りで剥がれすぎないか要確認（`link.xml` で保護できる）。
3. **IL2CPP Code Generation** を `Faster (smaller) builds` にする（サイズ優先）。
4. 対応アーキテクチャがARM64のみか確認する。

---

## 推奨する着手順序（更新版）

1. **③の修正を実機確認**（済: 修正投入済み、未検証）。
2. **④の実機確認**: Groundのslideノーツについて、最後のコンボ点を消化し終えてから
   数秒待っても消えないかを見る。消えるなら仕様の見え方の差として決着、
   消えないならUpdateSlideの完了条件の実バグとして追加調査する。
3. **①のstreamAudio化**。1行に近い変更で最大の効果（数十〜100MB）が見込める。
   エディタ側と切り分ける引数だけ足す。
4. **⑤のCPU削減**。効果/コスト比の順に (a) リリースビルドで再計測 →
   (b) `OnGUI` HUDの廃止 → (c) `StageOverlay` の静的/動的分離 → (d) 60/120fps設定の追加。
5. **⑤のGPU削減（ノーツメッシュのチャンク分割）**。効果は最大だが実装コストも最大。
   4まで終えて、まだEnergyがHighなら着手する。
