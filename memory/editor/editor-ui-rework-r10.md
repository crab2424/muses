# 譜面エディタ UI改修 第10弾 (r10) — r9の実機フィードバック3件

前提: `editor-ui-rework-r9.md`。r9をユーザーが実機確認して出た3件の不具合に対応する。
3件とも**原因を特定できた**（推測ではなく、実ファイル・実設定・コードの突き合わせで確定）。

---

## §0 調査時点の実状態

```
~/Documents/muses/songs/
  song.museproj        ← songsRoot直下に落ちている（本来は曲フォルダの中にあるべき）
  cube.muses           ← r9のリネームで test.muses から改名された
  autosave/test.muses.autosave
```

`editor-settings.json`:
```
"songsRoot": "/Users/crab2424/Documents/muses/songs"   ← r9 §2の救済が効いて正しくなった
"browseDir": "/Users/crab2424/Documents/muses/songs"
```

`song.museproj` の中身は `[METER]` `[BPM]` とも**空**、`cube.muses` の `[SCROLL]` も**空**。
また `cube.muses` の `@SONG` は `song.muses`（旧名）のまま残っていた。

r9 §2（既定パスの修正）自体は成功している。以下の3件が残課題。

---

## §1 曲フォルダが生成されない（指摘1）

### 1.1 原因

r9 §3.1 で導入した判定:

```csharp
bool HasSongMeta(string dir) => File.Exists(Path.Combine(dir, ChartSerializer.SongFileName));
```

これは「保存先に曲メタがあれば既存の曲フォルダ」という規則だが、**songsRoot 自身がこの条件を
満たしてしまう**。今回まさにそうなっていた:

1. r9適用前に一度 songs 直下へ保存 → `songs/song.museproj` が生成される
2. 以降 `HasSongMeta(songsRoot)` が **true** を返す
3. → songsRoot が「既存の曲フォルダ」と誤判定され、サブフォルダを作らず直下に保存し続ける

r8 の旧実装には `IsSongsRootItself()` による songsRoot の除外があったが、r9 で条件を
一般化したときに**この除外ごと落としてしまった**のが直接の原因。一度でも直下に保存すると
自己強化的に固定化するため、ユーザーからは「まったく効いていない」ように見える。

### 1.2 修正

songsRoot は「曲フォルダを並べる親」であって曲フォルダそのものではない、という不変条件を
判定に明示する:

```csharp
bool IsSongProjectDir(string dir) =>
    !EditorSettings.PathEquals(dir, songsRoot)
    && File.Exists(Path.Combine(dir, ChartSerializer.SongFileName));
```

r9 で導入した `EditorSettings.PathEquals`（末尾区切り・大小の揺れを無視）を再利用する。
これで r8 の songsRoot 除外と r9 の一般化の**両方**が同時に成立する。

### 1.3 既存データの後始末（ユーザー作業）

`songs/song.museproj` と `songs/cube.muses` は songsRoot 直下に残ったままになる。
修正後は保存時にサブフォルダが作られるので、以下のどちらかで整理する:
- Finder で2ファイルを `songs/<曲名>/` へ手で移す（`autosave/` も同様）
- または「曲フォルダを選んで保存」でフォルダ名を入力して保存し直し、直下の旧ファイルを消す

**自動移動はしない**: ユーザーのデータを黙って動かすほうが事故が大きいため。

---

## §2 曲フォルダ名の入力欄が見えない（指摘2）

### 2.1 原因

r9 §5.2 の `UpdateDestLabel()` が、既存の曲フォルダにいるときに入力欄を**無効化**していた:

```csharp
nameField.SetEnabled(false);
```

UI Toolkit の `SetEnabled(false)` は `.unity-disabled` を付けて **`opacity: 0.5`** にする。
muses の入力欄は既に「暗い背景（`--bg-control`）＋明るい文字」なので、半透明になると
文字も枠も背景に溶けて**消えたようにしか見えない**。

しかも §1 の誤判定により、ユーザーの環境では**常にこの無効化パスに入っていた**。
つまり指摘1と指摘2は同じ誤判定から派生した同一原因の2つの症状で、
「フォルダ名を入力しても効かない」のは入力欄がそもそも無効だったため。

なお r4 §7 で直した「テキストが見えない」（`.prop-row` の固定height／
`.unity-text-element` への color 明示）は**今回は再発していない**。
`.modal .unity-base-field__input .unity-text-element` のルールは効いており、
新規曲ウィザードの同型の `TextField` は正常に見えている。原因は別物。

### 2.2 修正

使えない状態のコントロールを薄く見せるのではなく、**畳む**:

```csharp
nameField.style.display = inSongProject ? DisplayStyle.None : DisplayStyle.Flex;
```

既存の曲フォルダにいるときは入力欄自体が消え、下の説明ラベルが
`既存の曲フォルダに保存: <dir>/cube.muses` を示す。入力が必要な場面では通常表示になる。

**How to apply**: 今後 UI Toolkit で「今は使えない入力欄」を表現するときは、
暗色テーマ上で `SetEnabled(false)` に頼らない（opacity 0.5 は暗い背景では消滅に近い）。
不要なら `display: none` で畳み、必要なら文言で理由を示す。

---

## §3 イベント3種が譜面データに反映されない（指摘3）

### 3.1 原因

BPM・拍子・ソフラン倍率は、**どれも「無ければ既定値へ黙って落ちる」実装**になっていた:

| 種別 | 保持場所 | 空のときの既定 | 落ちる場所 |
|---|---|---|---|
| BPM | `SongMeta.bpmEvents` | 120 | `ChartFormat.BuildTickToSeconds:117` |
| 拍子 | `SongMeta.meters` | 4/4 | `SongAddr.Normalize:53` |
| ソフラン | `ChartData.scrollEvents` | 恒等写像 X(t)=t | `ScrollTimeline.Identity` |

そのため**動作はする**（BPM120・4/4・等速で正しく動く）が、その状態は
**譜面ファイルに一切現れない**。新規作成した曲は `SongMeta`/`ChartData` が空のまま保存され、
`[BPM]` `[METER]` `[SCROLL]` が空のファイルになる。実際、今回の `song.museproj` が
まさにその状態だった。

イベントレーンからの追加操作（`HandleEventLaneClick`）や `songMetaDirty` の設定自体は
正しく動いており、**壊れていたのは「基準値が実データとして存在しない」ことだけ**。
基準値が無いので、イベントレーンには編集の起点になる行が1つも無く、
ファイルを単体で見てもテンポが分からない。

### 3.2 修正

暗黙の既定値を**実データとして必ず持たせる**。`ChartFormat` に追加:

```csharp
public const float DefaultBpm = 120f;
public static bool EnsureBaseSongEvents(SongMeta song);   // BPM@tick0, METER@bar0
public static bool EnsureBaseChartEvents(ChartData chart); // SCROLL@tick0/group0/mul1
```

- 戻り値は「追加が発生したか」。呼び出し側が dirty フラグを立てるのに使う。
- **冪等**（2回目以降は何もしない）。読み込み直後に再度呼んでも dirty にならない。
- BPM の基準値は、途中変化だけを持つファイルでは**最も早いイベントの値を引き継ぐ**
  （無関係な120が先頭に挿し込まれるのを避ける）。空なら `DefaultBpm`。
- 既定値の重複を避けるため、`BuildTickToSeconds` / `SortedBpmEvents` の
  `120f` リテラルも `DefaultBpm` 経由に統一した。

呼び出し箇所:

| 箇所 | 呼ぶもの | 備考 |
|---|---|---|
| `OpenChartFromPath` | 両方 | `EnsureBaseSongEvents` は **`ReadChart` より前**（`ReadChart` が `song.bpmEvents` を `chart.bpmEvents` へ複製するため）。補った場合は `dirty`/`songMetaDirty` を立て、次の保存で確実に永続化する |
| `CreateNewSong` | 両方 | ファイルへ書く前 |
| `NewChart` | scroll のみ | BPM/拍子は SongMeta 側なので引き継ぐ |

さらに、基準イベントは**削除不可**にした（`DeleteSelectedEvent`）。
既存の「0小節目の拍子は削除できません」と同じ理由 — 消しても既定値が黙って補うため、
ユーザーが設定した値だけが気づかれずに消える結果になる。

### 3.3 回帰しないことの確認

基準ソフランイベント（tick0 / group0 / mul1）の追加が既存譜面の見た目・速度を
変えないことを、`ScrollTimeline` の構築ロジックで確認済み:
`startSec == prevEndSec` かつ `durationTicks == 0` なので区間が1つも生成されず、
`segments.Count == 0` → `XAt(t)` は `return t`（＝`Identity` と完全に同一）。

`memory/verify/` 方式のスクラッチ検証（`dotnet run`、pure C#部分のみ複製）でも
14項目＋往復11項目を全て確認済み。特に:
- `X(t)` が基準イベントの有無で完全一致（maxDiff = 0）
- 後続のソフラン（4拍目で mul=2）が従来どおり効く
- 明示 `bar0 4/4` と暗黙 4/4 で `SongAddr.ToTick` の結果が一致
- BPM変化・拍子変化・ソフラン変化（easing/duration付き）がファイル往復で保存される

---

## §4 副次的に見つかった不具合: `@SONG` が旧名のまま

`cube.muses` の `@SONG` が `song.muses` のまま残っていた。`header.songFile` は
読み込み時の値を持ち回るだけで、保存時に更新されていなかったため。
実際に書き出す曲メタのファイル名と食い違うのは誤りなので、`DoSaveChartToPath` で
保存のたびに `header.songFile = ChartSerializer.SongFileName` へ揃える。

（読み込み側は従来どおりフォルダ内の固定名を見るので、この値がズレていても
実害は無かった。ファイルを人が読んだときに嘘になる、という問題。）

---

## §5 実機で確認してもらう項目

1. songs 直下で「曲フォルダを選んで保存」→ **フォルダ名の入力欄が見えること**（§2）
2. フォルダ名を入れて保存 → `songs/<入力名>/{song.museproj, cube.muses}` になること（§1）
3. その曲フォルダの中で再度「曲フォルダを選んで保存」→ 入力欄が消え、
   サブフォルダを作らず直下に保存されること（§1・§2）
4. 保存した `song.museproj` に `[BPM] 0:1:0 120` と `[METER] 0 4/4`、
   `cube.muses` に `[SCROLL] 0:1:0 0 1` が入っていること（§3）
5. イベントレーンに基準の BPM／拍子／倍率が行として見え、値を変更できること（§3）
6. 基準イベントを削除しようとすると「削除できません」と出ること（§3）
7. 既存 `cube.muses` を開くと BPM/拍子/ソフランが補われ、保存すると反映されること（§3）
8. `@SONG` が `song.museproj` になること（§4）
