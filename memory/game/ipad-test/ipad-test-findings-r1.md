# iPad実機テスト(2026-08-09)で報告された5件の調査結果 r1

`cbf9c70`(曲の読み込み・再生フロー)以降、初めて自作譜面を通しで実機プレイした際の報告5件。
着手順序の検討 → 原因調査 → 修正 → 実機確認まで完了。

| # | 症状 | 種別 | 状態 |
|---|------|------|------|
| ③ | 判定演出(HitFlash)が一切描画されない | **回帰** | 修正・**実機確認済み** |
| ② | 判定/描画オフセットの可変幅が狭い、楽曲オフセットが実機で調整できない | UI改善 | 実装・**実機確認済み** |
| ④ | Ground slideノーツのみ判定線上で消えない(Skyは消える) | 仕様の相互作用（実バグではなかった） | **決着。GOOD以上は判定線で食べる/MISSはそのまま通り過ぎる仕様に作り直し、実機/Editor確認済み** |
| ⑤ | CPU72% / メモリ331MB / Energy High | 概ね妥当だが改善余地大 | 原因分析済み。**次回セッションで深掘り** |
| ① | ビルド134MB / 「ステージエンジン」100MB超 | 誤帰属の疑い | 原因分析済み。**次回セッションで深掘り** |

**2026-08-09追記**: ③④を除く全項目（②の実装含む）はユーザーが実機で確認し、
「4のslide以外は全て反映されていた」と報告。当時は「仕様の相互作用による見え方の差」
という説明では足りず実バグ濃厚と判断した。

**2026-08-10追記（決着）**: ログ計測の結果、**Judge側は正常に完了しており実バグではなかった**
（当初の仮説が正しかった）。終点+0.1秒までGroundの帯が判定線の手前に居座るのが正体で、
ユーザー判断により**「GOOD以上は判定線で逐次食べる/MISSはそのまま通り過ぎる」仕様へ変更**した
（コンボ区間ごとに独立した頂点範囲・eatableフラグを持たせる作り、詳細は④節参照）。
初版はタイミングを誤り(コンボ点確定後に食べ始めていたため食べる過程が見えなかった)、
「押さえている間、通過中の区間をその場で食べる」方式へ修正して**ユーザーがUnity Editorで
確認・解決を確認済み**。①③④のうちの④はこれで完全に決着。
**残るは①⑤（メモリ/CPU/Energy・ビルドサイズ）のみ。**

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

### 追加要望を受けて実装: 楽曲オフセットを実機からも調整可能に（2026-08-09、実機確認済み）

「楽曲オフセットが実機に表示されないのは意図通りだが、エディタの値を保った上で実機からも
微調整したい（エディタ値+実機調整値の加算）」という要望を受けて実装した。

- `PlayerSettings.songOffsetMs`（新規、既定0）を追加し、設定画面に「楽曲オフセット(ms)」
  スライダー(±1000ms)を追加。
- `SongClock.UpdateOffset(float offsetSec)` を新設。**`Seek()`と違い`t0`/`smoothed`/
  `nextBeat`には触れない**ため、判定進行・コンボを一切巻き戻さずにOffsetだけ更新できる。
  再生中は即座に再スケジュール、一時停止中はOffsetを書き換えるだけで次の`Resume()`が
  自然に拾う。
- `GameController.ApplyPlayerSettings`が`totalSongOffsetSec = (song?.offsetSec ?? 0f) +
  ps.songOffsetMs / 1000f`を計算し`clock.UpdateOffset()`に渡す。`AudioEndTime()`も
  この合計値を参照するよう統一（終了条件のズレ防止）。
- `GameController.LoadChart`は引き続きエディタ値だけを一時的に`SetMusic`へ渡すが、
  直後に呼ばれる`ApplyPlayerSettings`が上書きする前提（`AppController.LoadAndStart`の
  呼び出し順: LoadChart→ApplySettingsToGame→StartGame に依存、コード内にコメントで明記）。

**ユーザーが実機で確認し、正しく反映されていることを確認済み。**

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

### 決着（2026-08-10）: 実バグではなく仕様の相互作用だった。シェーダ側で「食べる」実装へ

実機ログ計測（下記）を仕込んでUnity Editorで再現したところ、
`Slide完了: layer=Ground songTime=5.966 comboCount=2 vStart=300 vCount=60` が**出力された**。
つまり:

- **Judge側は正常**。`nextComboIndex`は最後まで到達し`setAlpha(rt, 0f)`まで呼ばれている。
- **描画側の経路も正常**。`vCount=60`は`PushSlideBand`の生成数と一致する
  （`steps = max(8, ceil((t1-t0)/0.03))`・1ステップ6頂点 → 60頂点＝10ステップ）。
  帯全体が`SetNoteAlpha`の書き込み対象に入っており、`FlushAlpha`も毎フレーム呼ばれている。
- **ユーザーが「終点を過ぎて0.1秒ほど待つと消える」ことを確認**。

よって当初仮説（`skyFloorFromJudge`による近距離フェードの非対称＋Slideの
「全コンボ点消化まで帯全体表示」仕様の相互作用）が**正しかった**。r1追記で
「仮説の分が悪くなった」と書いたのは誤りで、実際には仕様どおりの挙動だった。
終点のコンボ点は終端時刻`t1`にあり完了判定は`t1+0.1秒`。その間、Groundの帯は
判定線を通過した部分も`gbNear`(カメラ間近)まで消えずに描かれ続ける
（Skyは`skyNear=zJudge`なので通過した瞬間に消える）。**長いSlideほど居座る時間が長い**。

**ユーザー判断: 「帯全体がパッと消える」のではなく、判定が有効な最中の帯を
判定線で逐次食べる仕様にする**（音ゲー定番の見た目）。

#### 実装（`Note.shader` のSlide帯分岐、1行）

```hlsl
a *= saturate((IN.depth - _ZJudge) / max(fwidth(IN.depth), 1e-5));
if (a <= 0.003) discard;
```

- Slide帯は1メッシュ1アルファなので、Judge側は**ノーツ単位でしか隠せない**。
  「通過済みか」は断片ごとの`depth`で決まるため、**フラグメントシェーダが正しい層**。
- `depth < _ZJudge` が「判定線より手前(通過済み)」。これはSkyが判定線で消えていた
  理由（`skyNear=_ZJudge`により`aNear`がここで0になる）と**同じ位置**なので、
  Sky帯にとっては冗長だが無害で、結果としてGround/Skyの見え方が揃う。
- `fwidth(depth)`で割ることで遠近に依らず画面上1px幅のアンチエイリアスになる
  （note-visual-r1.md §2.2の輪郭線と同じ手法）。
- Slide帯分岐(`localUv.y`が(-0.5, 0.5]）の中だけなので、**Tap/ExTap/Flick・
  Riserの縁線/矢印には影響しない**。

#### 追加要望を受けての作り直し（2026-08-10）: 食べる/通り過ぎるをコンボ区間ごとに分岐

上記の初版実装は「帯全体を一律で判定線から食べる」だった。ユーザーから
**「MISS時は判定線で消えずに通り過ぎる仕様にしたい。判定によって通り過ぎる処理と
食べる処理を分岐させること」**という追加要望を受け、コンボ区間（`comboTimes`の要素ごと）
単位で食べる/通り過ぎるを分岐する作りに変更した。

**なぜ「全体で1フラグ」では成立しないか**: 頂点は時刻固定でスクロールに伴い
depthが単調に減っていくだけなので、一度cutされた区間はそのままcutされ続ける。
問題は逆方向——後から別のコンボ点がMISSになってノート全体のフラグを0に戻すと、
すでに食べられて消えた区間まで巻き添えで復活してしまう（同一ノートの前半と
後半で挙動が変わる継ぎ目ができる）。**区間ごとに独立した頂点範囲を持たせ、
区間の判定が確定した瞬間にその区間専用のフラグだけを書く**必要がある。

**実装**:
- `NoteGeometry.PushSlideBand`: 生成する頂点をコンボ区間の境界（`comboTimes[i]`）に
  厳密に揃えて分割し、区間ごとの頂点範囲`(start, count)[]`を返すよう変更
  （旧: 帯全体を一括で`min 8`ステップ生成 → 新: 区間ごとに`min 2`ステップ）。
- `NoteRuntime.comboSegmentVertexRanges`: 上記の範囲をノーツごとに保持する新フィールド。
- `NoteView`: `uv2.y`（従来未使用だったscrollGroupの相方）をSlide専用の
  eatableフラグとして転用。`SetSlideSegmentEatable(rt, comboIndex, eatable)`で
  対応区間だけ書き換え、`FlushAlpha()`が`uv0`と一緒に転送する。
- `Judge`: `setSegmentEatable`コールバック（省略可）を追加。**ノーツ完了時の
  強制`setAlpha(rt, 0f)`は削除**——Hit済み区間は既に判定線で食べられて見えなくなっており、
  Miss区間は帯として自然に近距離フェードまで通り過ぎ続けるのが正しい挙動になったため。
  `Seek()`でも全区間のフラグをfalse（通り過ぎる）へ一律リセットする
  （シークは「素直に見た状態」へ組み直す既存方針に合わせる）。

**タイミングの誤り（実機確認で発覚、修正済み）**: 初版は「コンボ点が確定した瞬間に
その区間のHit/Miss結果を書く」実装だったが、**確定は`comboTimes[i] + 0.1秒`＝区間iが
判定線を完全に通過し終えた後**なので、食べる過程が画面に一切出ず「通過済みの区間が
丸ごと消えるだけ」になっていた（ユーザー報告:「食べる処理が消えてしまった」）。
確定を待たず、**いま判定線を通過中の区間を、実際に押さえられていれば(`occ==true`)
その場でtrueへ倒す**方式に修正:

- 通過中の区間は`songTime`から直接求める（`nextComboIndex`は確定が0.1秒遅れる分だけ
  1つ前を指すことがあり、そのまま使うと食べ始めが0.1秒遅れて段差になる）。
- **sticky（一度trueにしたらfalseへ戻さない）**: 途中で手を離したときに、既に食べられて
  消えた部分が復活してしまうのを防ぐ。離した後の区間は`occ==false`でこの分岐を通らないので、
  そのまま流れて行く＝**MISSは通り過ぎる**が自動的に成立する。
- `ResolveSlideComboPoint`の戻り値は使わなくなったのでvoidへ戻した。
- 押さえられている間は毎フレーム呼ばれるため、`NoteView`側で**値が変わらないときは
  何もしない**ガードが必須（無いと毎フレーム全メッシュ転送になり、r13 §7.3で解決した
  fps問題が再発する）。区間内の頂点は必ず同じ値なので先頭1つを見れば現在値が分かる。

**ユーザーがUnity Editorで確認し、修正できたことを確認済み（2026-08-10）。④はこれで完全に決着。**
- `Note.shader`: `uv2.y`を`slideEatable`としてvarying経由でfragmentへ渡し、
  `depth < _ZJudge`の食べる処理を`lerp(1.0, eat, IN.slideEatable)`で選択的に適用。
  **`if`で分岐せず`lerp`にした**: `fwidth(depth)`は非一様な制御フロー内だと
  GPU実装依存の未定義動作になりうるため（早期discardを避けた理由と同じ注意）、
  常に計算した上で結果だけを条件で混ぜる。
- `GameController.cs`/`PreviewSystem.cs`: `noteView.SetSlideSegmentEatable`を
  Judgeのコンストラクタへ配線（ゲーム本体・エディタプレビュー両方）。

**ユーザーがUnity Editorで確認し、解決を確認済み（2026-08-10）。**

#### 残る派生論点（未対応、要判断）

- **Visible中継点マーカー**は`QuadThin`経由で`localUv.y=1`＝タップ形状の分岐に入るため
  食べられず、ノーツ完了まで判定線の手前に残る。帯だけ削れてマーカーが浮くのが
  気になる場合は、マーカー専用の`localUv.y`タグ値を新設して同じ切り方を適用する。
- **MISSしたTap/Flick**（alpha 0.12）は従来どおり判定線を過ぎて手前まで流れる（今回の
  変更はSlide帯のみが対象で、この挙動と自然に一致している）。

#### NullReferenceException、原因確定・修正済み（2026-08-10）

上記の確認中に別件のエラーが発生:
```
NullReferenceException: Object reference not set to an instance of an object
Muses.Game.GameController.ApplyPlayerSettings (Muses.Game.PlayerSettings ps)
Muses.Game.AppController.ApplySettingsToGame ()
Muses.Game.AppController.Awake ()
```
`AppController.Awake()`が`ApplySettingsToGame()`経由で`GameController.ApplyPlayerSettings()`を
呼ぶが、そこで参照する`clock`は`GameController.Awake()`で初めて生成される。同じAwakeタイミングの
兄弟コンポーネント間でUnityの実行順は保証されないため、`AppController`が先に走るとNREになる
（今回④の調査でPlayを繰り返す中で顕在化したが、④の変更とは無関係の既存の潜在バグ）。
`GameController`に`[DefaultExecutionOrder(-100)]`を付けて明示的に先へ走らせることで解決した。

---

### 調査の経緯（参考）

**コード調査の結果、`UpdateSlide`の完了ロジック自体はlayer非依存と確認済み**
（`Judge.cs`の`nextComboIndex`カウントアップは`songTime`が各コンボ点+0.1sを過ぎれば
無条件に進み、Ground/Skyを一切参照しない。`setAlpha(rt, 0f)`は`rt.vStart..vCount`の
全頂点に書き込むためGround/Sky問わず対象になるはず）。よって静的読解だけでは原因を
特定できず、**実機ログでの確認が必須**と判断した。

**実機ログ計測の仕込み（役目を終えたため撤去済み）**: `Judge.cs`に`logSlideComplete`
コールバック（省略可、既定null）を追加し、Slideが完了(`nextComboIndex >= comboTimes.Count`)
した瞬間に`layer`(Ground/Sky)・`songTime`・`comboCount`・`vStart`/`vCount`を1行ログする。
`GameController.cs`で`Debug.Log`へ配線済み（原因特定後に削除する前提の一時コード、
コメントに明記）。`Judge`はUnityEngineに依存しない設計を保つため、`Judge`本体は
`Debug.Log`を直接呼ばずコールバック注入にした（既存の`setAlpha`/`onJudged`と同じ形）。

→ **結果は「ログが出る」＝Judge側は正常**だった（上の「決着」節を参照）。

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

## 推奨する着手順序（2026-08-09時点、更新版）

**③②④とも原因確定・対応済み。残るは①⑤のみ。**

1. ~~**④**~~ → **2026-08-10に決着**。実バグではなく仕様の相互作用で、GOOD以上は判定線で
   逐次食べる/MISSはそのまま通り過ぎる実装に変更し、Unity Editorでユーザー確認済み
   （④節の「決着」参照）。
2. **①⑤（メモリ・エネルギー）**: ユーザーの意向により**次回セッションで詳しく掘り下げる**。
   本ファイルの①⑤節に既に分析と改善案（streamAudio化・OnGUI廃止・オーバーレイの
   静的/動的分離・ノーツメッシュのチャンク分割等）を優先度付きでまとめてあるので、
   次回はそこから着手する。
