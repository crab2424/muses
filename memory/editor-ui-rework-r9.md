# 譜面エディタ UI改修 第9弾 (r9) — プレビュー再生位置・曲フォルダ・ファイル形式

前提: `editor-spec.md` / `editor-ui-rework-r7.md` / `editor-ui-rework-r8.md`。
r8 の実機確認で出た3件（プレビューのスクロール位置と再生開始地点の乖離、曲フォルダが
自動生成されない、`.muses` が2つの役割を兼ねている）に対応する。

---

## §0 調査結果（実機の実状態と根拠）

設計に入る前に現物を確認し、3件のうち2件は**原因まで特定できた**。

### §0.1 `songsRoot` が意図した場所を指していない（r8までの潜在バグ）

`~/Library/Application Support/.../editor-settings.json` の実値:

```
"browseDir": "/Users/crab2424/Documents/muses/songs",
"songsRoot": "/Users/crab2424/muses/songs"
```

`~/muses/songs` は空フォルダとして実在する（`Directory.CreateDirectory(songsRoot)` が
起動時に作ったもの）。

**原因**: `EditorSettings.DefaultSongsRoot()`（`EditorSettings.cs:175`）が
`Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)` を使っているが、
**Unix系(.NET/Mono)ではこの値は `~/Documents` ではなく `$HOME` を返す**
（`Personal`/`MyDocuments` は Unix では HOME に縮退する仕様）。
そのため r7 で「`~/Documents/muses/songs` にする」と説明した既定値が、実際には
`~/muses/songs` になっていた。

### §0.2 曲フォルダの自動生成が発火しなかった理由

r8 §2.2 で入れた自動生成は `IsSongsRootItself(browseDir)`（保存先が **songsRoot そのもの**
のときだけ）が条件。§0.1 のズレにより
`browseDir`(`~/Documents/muses/songs`) ≠ `songsRoot`(`~/muses/songs`) となり、
条件が常に false だった。**実装漏れではなく、条件が厳しすぎたことによる不発**。

条件が songsRoot 一致のみである限り、既定値を直しても
「ユーザーが別の場所（デスクトップ等）へ保存した瞬間に散らかる」という穴は残る。§3 で一般化する。

### §0.3 生成された2ファイルの正体

```
songs/test.muses   @FORMAT muses-chart 1   … 譜面本体（@DIFFICULTY CUBE）
songs/song.muses   @FORMAT muses-song 1    … 曲メタ（BPM/拍子/音源/オフセット）
```

役割は設計どおり分離できているが、
- 拡張子が同じで見分けがつかない
- 譜面側のファイル名が難易度名（`cube.muses`）ではなく任意名（`test.muses`）

の2点が「違和感」の実体。§4・§5 で解消する。

### §0.4 移行対象データ

`.muses` は上記2ファイルのみ（`songs/` は `.gitignore` 済みでリポジトリには入らない）。
移行コストはほぼゼロなので、**自動マイグレーション処理は書かない**方針で進める（§4.4）。

---

## §1 プレビューのスクロール位置を再生開始地点に同期する

### §1.1 現状の時間軸の役割分担（r3 §8 以来の設計）

| 変数 | 意味 |
|---|---|
| `cursorTick` | **再生開始位置**（▶で必ずここから鳴る。青いカーソル線） |
| `scrollTick` | 判定線の位置＝**停止中のプレビュー表示時刻** |
| `preview.SongTime` | 再生中の真の時刻 |

停止中は `Update()` が `scrollTick → preview.Seek(...)` を毎フレーム同期している
（`ChartEditorApp.cs:553-557`）。プレビュー上のホイールは `OnPreviewWheel` →
`OnSheetWheel` で `scrollTick` を動かすので、**プレビューの絵とシークバーの表示位置は
既に追従している**（シークバーは `preview.SongTime` を表示: `ChartEditorApp.UI.cs:1569`）。

**足りていないのは `cursorTick` だけ**。プレビューをスクロールしてから ▶ を押すと、
`TogglePlayFromCursor` が `cursorTick` へ戻してから再生するので位置が飛ぶ。

### §1.2 変更内容

`OnPreviewWheel` の通常ホイール経路でのみ、停止中に `cursorTick` を `scrollTick` に
追従させる。

```
private void OnPreviewWheel(WheelEvent evt)
{
    if (evt.ctrlKey || evt.commandKey) { /* HiSpeed、従来どおり */ return; }
    OnSheetWheel(evt);
    // r9 §1: プレビューは「再生位置そのもの」を映す面なので、ここでのスクロールは
    // 表示位置だけでなく再生開始地点も動かす（シークバーでのスクラブと同じ扱い）。
    if (!preview.IsPlaying) cursorTick = scrollTick;
}
```

- スクラブバー側の `cursorTick = scrollTick = t` 代入（`ChartEditorApp.UI.cs:1469-1474`）と
  同じ意味づけになり、**「シークバーの再生開始地点と同期」というユーザー要望をそのまま満たす**。
- **タイムライン（ノーツシート）側のホイールは従来どおり `cursorTick` を動かさない**。
  譜面を眺めて回るスクロールで再生開始地点が動くのは編集作業では邪魔になるため
  （参照元 MikuMikuWorld も同じ役割分担）。ユーザーの指定も「プレビューにおいては」だった。
- 再生中は何もしない。再生中は `followPlayback` が逆方向（`preview.SongTime → scrollTick`）に
  動かしており、停止した瞬間に `cursorTick = stopTick` が入る既存処理
  （`ChartEditorApp.cs:564-569`）で辻褄が合う。

### §1.3 併せて確認する既存の導線

同じ「プレビュー上の操作で位置を決める」系として、プレビュー面のドラッグやクリックでの
シークは**今回は入れない**（プレビュー面は将来ノーツ素材や演出の確認に使うので、
クリックを移動操作に割り当てると後で衝突する）。ホイールのみに閉じる。

---

## §2 曲フォルダの既定地を直す（`MyDocuments` 問題）

### §2.1 `DefaultSongsRoot()` の修正

プラットフォーム分岐は書かず、**縮退の検出**で対応する（Windows では `MyDocuments` が
正しく効くので、そのまま活かしたい）。

```
public static string DefaultSongsRoot()
{
    string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    // Unix系(.NET/Mono)では MyDocuments が HOME に縮退する。その場合だけ Documents を補う。
    if (string.IsNullOrEmpty(docs) || PathEquals(docs, home))
        docs = Path.Combine(home, "Documents");
    return Path.Combine(docs, "muses", "songs");
}
```

`PathEquals` は末尾区切り文字・大小の揺れを無視する既存 `IsSongsRootItself` と同じ比較で、
共通ヘルパに切り出す。

### §2.2 既に保存されている設定の救済

`editor-settings.json` には旧既定値 `~/muses/songs` が焼き付いている。起動時
（`ChartEditorApp.cs:432-436` の初期化）に、以下の条件を**すべて**満たすときだけ
新既定へ更新する:

1. `settings.songsRoot` が旧既定パターン（`$HOME/muses/songs`）と一致する
2. そのフォルダが存在しない、または**空**である

空でなければ「ユーザーが意図してそこに曲を置いた」可能性があるので触らない。
更新したときはステータスバーに `曲フォルダの既定値を修正しました: <新パス>` を出す。

**Why**: サイレントに設定を書き換えるのは事故のもとだが、今回は「空フォルダを指したまま
使えない設定」であることがコードから確定できるケースに限れるため、限定条件つきで自動修正する。

### §2.3 副次的な効果

設定画面の「曲フォルダ」表示・「Finderで開く」・新規曲ウィザードの「保存先:」プレビューが、
すべて実際に使われる場所を指すようになる（現状は誰も見ない `~/muses/songs` を指していた）。

---

## §3 保存時の曲フォルダ自動生成（条件の一般化）

### §3.1 判定条件を「曲プロジェクトかどうか」に変える

`IsSongsRootItself(browseDir)` を廃止し、次の規則にする（ユーザー確定）:

| 保存先ディレクトリ | 挙動 |
|---|---|
| 曲メタファイル（§4 の `song.museproj`）が**ある** | そのまま直下に保存（＝既存の曲へ難易度を追加） |
| **ない** | 入力された名前でサブフォルダを作り、その中に保存 |

これで songsRoot の値に依存しなくなり、デスクトップや任意のフォルダを選んでも
「必ず曲プロジェクトフォルダの形」で保存される。§0.2 の穴が構造的に閉じる。

### §3.2 実装

```
string targetDir = browseDir;
if (!File.Exists(Path.Combine(browseDir, ChartSerializer.SongFileName)))
{
    targetDir = Path.Combine(browseDir, SanitizeFolderName(folderNameInput));
    Directory.CreateDirectory(targetDir);
}
```

`SanitizeFolderName` は既存のものを流用。空文字は弾く（既存のガードと同じ）。

---

## §4 ファイル拡張子を役割ごとに分ける

### §4.1 決定

| 役割 | ファイル名 | ヘッダ |
|---|---|---|
| 曲プロジェクト定義（BPM/拍子/音源/オフセット/ジャケット） | **`song.museproj`** | `@FORMAT muses-song 1`（据え置き） |
| 譜面（難易度ごと） | `line.muses` / `square.muses` / `cube.muses` / `tesseract.muses` | `@FORMAT muses-chart 1`（据え置き） |

**Why この分け方**:
- `.muses` は「譜面」に一本化される。エディタが開く対象・ユーザーが日常的に触る対象が
  `.muses` だけになり、拡張子の意味が1つに定まる。
- 曲メタは「そのフォルダが曲プロジェクトであることの目印」でもあるので、
  *proj* という語が役割を素直に表す（§3.1 の判定にも使う）。
- **`@FORMAT` 行は変更しない**。読み分けに使っているのはファイル名と関数の使い分けであり、
  FORMAT 名を変えても得るものがなく、既存ファイルが読めなくなる副作用だけが残るため。

### §4.2 影響箇所（すべて定数へ集約する）

現状 `"song.muses"` は5箇所以上に文字列リテラルで散っている。今回まとめて
`ChartSerializer` に定数を置き、全箇所をそこ経由にする:

```
public const string SongFileName = "song.museproj";
public const string SongExt      = ".museproj";
public const string ChartExt     = ".muses";
```

書き換え対象:
- `ChartEditorApp.cs:117` `header` 既定の `songFile`
- `ChartEditorApp.cs:592-597` `OpenChartFromPath` の曲メタ存在チェックとエラーメッセージ
- `ChartEditorApp.cs:646-661` `SaveChartToPath` の `songPath` 再確定
- `ChartEditorApp.UI.cs` `CreateNewSong`（新規曲ウィザード）
- ファイルブラウザの列挙フィルタ `*.muses`（→ `ChartExt`。結果として曲メタが一覧に
  出なくなり、「開く」ダイアログには譜面だけが並ぶ＝副次的な改善）

`@SONG` 行の値も新名で書くが、**読み込み時は従来どおりフォルダ内の固定名を見る**
（`@SONG` は表示用・将来の別名参照用のまま。今回この読み方は変えない）。

### §4.3 移行（後方互換）

`OpenChartFromPath` で `song.museproj` が無い場合に限り、同フォルダの `song.muses` を
フォールバックで読む。**旧ファイルの削除やリネームはしない**（保存時に新名で書かれ、
旧ファイルは残る。手で消してもらう）。

**Why**: 自動リネームは、旧ファイルを参照している別の何か（バックアップ・自動保存）と
食い違う余地がある。対象データが §0.4 のとおり1組しかないので、読めることだけ保証すれば足りる。

---

## §5 譜面ファイル名を難易度名に固定する

### §5.1 決定

譜面ファイル名は `@DIFFICULTY` から自動決定する（`difficulty.ToLowerInvariant() + ".muses"`）。
新規曲ウィザードは既にこの方式なので、**「別名で保存」だけがこの規則から外れていた**のを揃える。

### §5.2 「別名で保存」の UI 変更

- メニュー項目名: `別名で保存...` → **`曲フォルダを選んで保存...`**
- 入力欄のラベル: `ファイル名` → **`曲フォルダ名`**（初期値は曲タイトル、無ければ空）
- 保存先の表示ラベルを1行追加: `保存先: <targetDir>/<difficulty>.muses`（入力に追従して更新）
- 保存パス = `<§3で決めた targetDir>/<difficulty>.muses`

`.muses` 拡張子の自動付与ロジック（`if (!name.EndsWith(".muses")) name += ".muses"`）は
不要になるので削除する。

### §5.3 難易度を変更したときのファイル名追従

右パネルで `@DIFFICULTY` を変えると期待ファイル名が変わる。`SaveChartToPath` で:

1. `expectedPath = <songDir>/<difficulty>.muses` を計算
2. `expectedPath != chartPath` かつ `chartPath` のファイルが存在する場合は
   **リネーム扱い**（`File.Move(chartPath, expectedPath)` してから書く）
3. ただし `expectedPath` が既に存在する（＝別の譜面がある）場合は
   **確認モーダル**「`cube.muses` は既に存在します。上書きしますか？」を出し、
   キャンセルなら保存を中断する

**Why リネームにするか**: 「難易度を変えた」は同じ譜面の属性変更であって、譜面が増えたわけでは
ないため。コピーが残ると曲フォルダに同じ譜面の重複が溜まる。

自動保存ファイル（`<曲フォルダ>/autosave/<譜面名>.muses.autosave`）は追従させない
（古い名前のものが残るだけで実害がなく、リネーム漏れでクラッシュする方が損）。

---

## §6 実装順序

依存関係の順に:

1. **§4 定数化と拡張子分離**（他の項目が `SongFileName` を参照するため最初）
2. **§2 `DefaultSongsRoot()` 修正＋設定の救済**
3. **§3 フォルダ自動生成の条件一般化**（§4 の定数に依存）
4. **§5 譜面ファイル名の固定と「別名で保存」UI 改修**（§3 と同じ関数を触るので直後に）
5. **§1 プレビューのスクロール同期**（他と独立、最後でよい）

検証は `dotnet build Assembly-CSharp.csproj` でのコンパイル確認 →
ユーザーによる実機確認（standalone ビルド）。

---

## §7 実機で確認してもらう項目

1. 起動時に設定画面の「曲フォルダ」が `~/Documents/muses/songs` を指していること（§2）
2. 既存の `songs/test.muses` が開けること（§4.3 のフォールバック）
3. その状態で「曲フォルダを選んで保存」→ フォルダ名入力 →
   `songs/<入力名>/{song.museproj, cube.muses}` の形で生成されること（§3・§4・§5）
4. 曲プロジェクト内で保存すると、そこへ直に `cube.muses` が保存されること（サブフォルダを
   作らないこと）
5. 難易度を CUBE→SQUARE に変えて保存 → `square.muses` にリネームされること（§5.3）
6. 停止中にプレビューをホイールでスクロール → ▶ を押すと**その位置から**鳴ること、
   シークバーの位置と一致していること（§1）
7. タイムライン側のホイールでは再生開始地点が動かない（従来どおり）こと（§1.2）

---

## §8 未決事項

- なし（3件とも方針確定済み）。実装後の実機確認で挙動を詰める。
