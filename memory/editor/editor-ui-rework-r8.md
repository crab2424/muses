**実装済み（2026-08-03、同セッション内、`dotnet build`成功確認済み）**: 本書§1〜§5を
ユーザー確認どおりの方針（曲フォルダ名はファイル名欄から自動生成／音源外は無音で時間だけ進む／
プレビューのCmd+ホイールはハイスピード）で全項目実装した。詳細は本ファイル末尾の「実装ログ」参照。

# 譜面エディタ 第8弾（r8）設計（2026-08-03）

`memory/editor-ui-rework-r7.md` の実装後、ユーザーが実機で音源(Vorbis)再生を確認した状態で
出た5項目に対する設計。実装は本書のユーザー確認後に行う。

**r7時点までの検証結果（ユーザー報告、2026-08-03）**:
- 音源(Vorbis)を用意済み・**再生されることを確認済み**。r6の残件だった「音源が鳴らない」は解消。
- **`@OFFSET` の符号の向きは「n秒前倒し」**。r4 §12から延期されていた検証がこれで完了した
  （`PreviewClock` の `audioTime = songTime + Offset` / `SongTime = AudioTime - Offset` が
  意図どおりに効いている、という確認）。

---

## 0. 全体像

| # | 項目 | 主な変更先 | 依存 |
|---|---|---|---|
| §1 | 負のオフセットでも譜面を0秒から再生する | `PreviewClock.cs` | — |
| §2 | 曲プロジェクトフォルダの強制と自動保存の格納先 | `ChartEditorApp.cs` / `.UI.cs` | — |
| §3 | 中継点ツールの暴発回避（既存ノーツのクリックで選択へ横取り） | `ChartEditorApp.cs` | — |
| §4 | シークバーの長さを「音源長+10秒」に固定 | `ChartEditorApp.UI.cs` | §1 |
| §5 | プレビュー画面でもホイールスクロール可に | `ChartEditorApp.UI.cs` / `PreviewSystem.cs` | — |

**§4 が §1 に依存する理由**: 「音源長+10秒」にすると、音源が存在しない末尾10秒へシークできる
ようになる。ユーザー確定で**そこは無音で時間だけ進む**挙動にするが、これは §1 の
「負のオフセットの前奏区間を無音で進める」のと**同一の仕組み**（音源の外側では dspTime 基準の
クロックへ切り替える）で実現できる。したがって §1 を先に実装し、§4 はその上に載せる。

---

## 1. 負のオフセットでも譜面を0秒から再生する

### 1.1 症状と原因

**症状（ユーザー報告）**: オフセットが負のとき、譜面の先頭部分が再生できない。
例: オフセット `-1` 秒 → シークバーが1秒地点から始まり、譜面の0〜1秒が再生されない。

**原因**: `PreviewClock`（`unity/Assets/Scripts/ChartEditorApp/PreviewClock.cs`）が
**音源上の位置 `audioTime` を唯一の時間軸として持ち、それを負にできない**こと。

```csharp
public float SongTime => AudioTime - Offset;              // :55
float audioTime = Mathf.Max(0f, songTime + Offset);       // :99  Seek() 内のクランプ
```

`Offset = -1` のとき `songTime = 0` は `audioTime = -1` に対応するが、`AudioSource.time` は
負を取れないため 0 にクランプされる。その結果 `SongTime` の下限が `0 - Offset = +1` になり、
**譜面の 0〜1 秒は原理的に到達できない領域になっていた**。シークバーが1秒から始まって見えるのも
同じ理由（`scrubSlider` の値は `preview.SongTime` を表示している）。

### 1.2 方針: dspTime 基準のアンカーを常設し、音源の内側でだけ `AudioSource.time` を使う

`PreviewClock` に「アンカー対」を持たせ、**音源が実際に鳴っていない区間（前奏区間・末尾区間）は
dspTime から時刻を導出する**。音源が鳴っている区間だけは従来どおり `AudioSource.time` を真の値と
して使う（pitch 変更時のドリフト回避という既存の設計意図をそのまま残すため）。

```csharp
private double anchorDsp;      // このdspTimeのとき audioTime == anchorAudio だった
private double anchorAudio;    // 音源上の位置(秒)。負・clip.lengthより大 もありうる
private double DspAudioTime => anchorAudio + (AudioSettings.dspTime - anchorDsp) * Rate;
```

`AudioTime` の決定則:

| 状態 | 使う値 |
|---|---|
| 停止中 | `pausedAt`（負・clip.length超 を許容するよう変更） |
| 再生中・音源が実際に鳴っている | `source.time`（従来どおり） |
| 再生中・前奏区間（`DspAudioTime < 0`） | `DspAudioTime` |
| 再生中・末尾区間（`DspAudioTime >= clip.length`）または音源なし | `DspAudioTime` |

「音源が実際に鳴っている」の判定は `HasClip && source.isPlaying && 0 <= DspAudioTime < clip.length`。
両者は同じスケジュールから導出されるので**切り替わりの瞬間に値が飛ばない**（ミックスバッファ1個分の
誤差は残るが、既存の実装が許容している精度と同等）。

### 1.3 `Play()` の変更（前奏区間のスケジュール）

`AudioSource.PlayScheduled` は**未来の dspTime を指定できる**ので、前奏区間は
「音源の再生開始をその秒数だけ先に予約する」だけで正確に実現できる。

```csharp
public void Play()
{
    if (Running) return;
    double a0 = pausedAt;                       // 音源上の位置。負なら前奏区間の途中
    anchorDsp   = AudioSettings.dspTime + ScheduleLeadSec;
    anchorAudio = a0;
    if (HasClip && a0 < clipLength)
    {
        source.time  = (float)Mathf.Clamp((float)a0, 0f, clipLength - 0.001f);
        source.pitch = Rate;
        // a0 < 0 なら、音源の先頭(audioTime=0)に到達する時刻へ予約する
        double startDsp = anchorDsp + (a0 < 0 ? (-a0) / Rate : 0.0);
        source.PlayScheduled(startDsp);
    }
    // a0 >= clipLength（末尾区間）は音源を鳴らさない。時刻だけがアンカーから進む
    Running = true;
}
```

**副次的に直るもの**: 現状 `Play()` は `Running = true` を即座に立てるのに音は
`ScheduleLeadSec`(0.05秒) 後に鳴り始めるため、その50msの間 `source.time` は 0 のままで
`SongTime` が一瞬巻き戻って見える。アンカー方式では前奏として連続に扱われるのでこの段差も消える。

### 1.4 `Seek()` / `Pause()` / `SetRate()` の変更

- **`Seek(songTime)`**: `songTime` の 0 クランプ（譜面が負の時刻を持たないため）は維持。
  `audioTime` 側の 0 クランプ（`:99`）を**廃止**し、負の値をそのまま `pausedAt` に入れる。
  `Running` 中なら `source.Stop()` してから `Play()` と同じ予約をやり直す。
- **`Pause()`**: 前奏区間・末尾区間では `pausedAt = DspAudioTime`（負のまま保存する）。
  音源が予約済みでまだ鳴り始めていない状態で `AudioSource.Pause()` を呼ぶ挙動は不定なので、
  **予約中は `Stop()` を使う**（次の `Play()` で必ず予約し直すので副作用は無い）。
- **`SetRate()`**: 前奏区間・末尾区間ではアンカーを組み直す（`anchorAudio = DspAudioTime;
  anchorDsp = dspTime;`）。前奏区間で速度を変えたときは予約も取り直す。

### 1.5 波及と非影響

- **`ChartEditorApp` 側の変更は不要**。`preview.SongTime` / `preview.Seek(t)` の意味（譜面時間）は
  変わらず、負の `SongTime` を返すこともない（`Seek` が0でクランプ、再生も0から始まる）。
- **`ChartValidator`** は `song.offsetSec` と `AudioLengthSec` を受け取っているが、
  時間軸の解釈は変えないので影響なし。
- **オフセット正の側（前奏付き音源）は完全に従来どおり**（`a0 >= 0` の経路が既存と同じ）。

---

## 2. 曲プロジェクトフォルダの強制と自動保存の格納先

### 2.1 症状と原因

**症状（ユーザー報告）**: 保存したら `songs/` の直下にファイルが置かれた。複数曲を扱うと散らかる。

**原因**: r7 で追加した「新規曲…」ウィザード（`ChartEditorApp.UI.cs:2529`〜）は
`songsRoot/<songId>/` を作って正しく配置するが、**通常の「保存」「別名で保存」経路はその規約を
知らない**。`ShowFileModal(saveMode: true)` は `Path.Combine(browseDir, name)` をそのまま
保存先にするため（`:2457`）、`browseDir` が `songsRoot` のままだと直下に置かれる。

`SaveChartToPath()` 側は r7 で「保存先フォルダに `song.muses` を必ず書く」ようになっている
（`ChartEditorApp.cs:643-661`）ため、**`songs/` 直下に `song.muses` まで作られてしまう**。
これは「`songs/` 全体が1つの曲プロジェクトである」という誤った状態で、
他の曲フォルダを兄弟として置くと `OpenChartFromPath` の「同じフォルダの song.muses を読む」規約
（`ChartEditorApp.cs:590`）と噛み合わなくなる。

### 2.2 方針（ユーザー確定）: ファイル名欄の入力をフォルダ名にも流用して自動生成する

> 「現在別名（新規）で保存する際，名前を入力する欄があると思います（.muses用）．
> それをそのままフォルダ名にも引用し，自動でフォルダを作成して下さい．」

`ShowFileModal` の保存側に、**保存先が `songsRoot` 直下だったときだけ**働く分岐を入れる。

```
入力名 "mysong"（or "mysong.muses"）／ browseDir == songsRoot のとき
  → songsRoot/mysong/mysong.muses へ保存し、browseDir も songsRoot/mysong へ移す
browseDir が songsRoot 以外（＝既に曲フォルダの中にいる）のとき
  → 従来どおり browseDir/<入力名>.muses（難易度追加はこちら）
```

- 判定は `Path.GetFullPath` で正規化してから比較する（末尾スラッシュ・大小の揺れ対策）。
- フォルダ名は入力名から `.muses` を除き、`Path.GetInvalidFileNameChars()` を `_` に置換した
  ものを使う（ウィザード側の songId 検証と同じ規則に揃える）。
- 既に同名フォルダがある場合は**そのフォルダを使う**（＝そこへ難易度を足す扱い。
  上書き確認は既存の保存と同じ挙動に任せる）。
- 保存後は `browseDir` をその曲フォルダへ更新して `RememberBrowseDir()`。
  **次回以降の「別名で保存」は自動的に曲フォルダの中から始まる**ので、
  この分岐が繰り返し発動して入れ子フォルダを作ることは無い。

**残る注意点（実装時にコメントで明記する）**: `editor-spec.md §1.2` の想定では譜面ファイル名は
難易度名（`line` / `square` / `cube` / `tesseract`）。この経路では曲名がそのまま譜面ファイル名に
なるため、難易度を意識した命名にしたい場合は「新規曲…」ウィザードを使うか、
曲フォルダへ入ってから別名保存する。**規約違反ではない**（`OpenChartFromPath` はファイル名を
見ておらず、同フォルダの `song.muses` の有無だけを見ている）ので機能上の破綻は無い。

### 2.3 自動保存の格納先

**現状**: `chartPath + ".autosave"`（`ChartEditorApp.cs:772`）＝譜面ファイルの真横。
ユーザー要望「できればオートセーブはさらにフォルダに格納」。

**変更**: `<曲フォルダ>/autosave/<譜面ファイル名>.autosave` にする。

- フォルダ名は `autosave`（先頭ドットにしない）。r7 で `~/Library/...`（Finder既定で非表示）を
  やめた経緯と同じ理由で、**隠しフォルダは使わない**。
- 書き込み前に `Directory.CreateDirectory` する。
- `CheckAutosaveRestore(path)`（`:785`）は新しい場所を見るように変更し、
  **無ければ旧パス（`path + ".autosave"`）もフォールバックで探す**（r7以前に作られた
  自動保存ファイルを取りこぼさないため。実際にユーザー環境に存在しうる）。
- 保存先を持たない新規譜面用の `UntitledAutosavePath`（`persistentDataPath/untitled.muses.autosave`）は
  **そのまま**。曲フォルダが未確定なので置き場所が無く、`persistentDataPath` は
  「アプリ内部状態」として `editor-settings.json` と同じ扱いで良い（r7 の分離方針どおり）。
- 簡易ファイルブラウザは `*.muses` しか列挙しないため、`autosave` フォルダは
  ディレクトリとしては見えるがファイルは混ざらない。実害なしと判断する。

---

## 3. 中継点ツールの暴発回避

**症状（ユーザー報告）**: 中継点（AddWaypoint）ツールにだけ、他モードにあるノーツ選択・
入力暴発回避の処理が無い。

**現状**: r3 §7 で Tap/ExTap/Flick に、r3・r7 で Slide に入れた
「配置ツールでも既存の点をクリックしたら選択に横取りする」分岐が、
`EditorTool.AddWaypoint` の case（`ChartEditorApp.cs:1773-1793`）にだけ無い。
そのため中継点ツール中は既存ノーツの点をクリックしても選択できず、
帯に当たっていれば無関係な中継点が挿入される。

**修正**: Tap/ExTap/Flick の case（`:1743-1753`）と**同じブロックを先頭に置く**。

```csharp
case EditorTool.AddWaypoint:
{
    var hitExisting = HitTestPoint(L, pos);
    if (hitExisting.HasValue)
    {
        var hp = hitExisting.Value;
        if (evt.shiftKey) ToggleSelectionMembership(hp);
        else if (!selection.Contains(hp)) SetSingleSelection(hp);
        if (selection.Contains(hp)) BeginPointDrag(rawTick, rawCell, layerF, pos, evt);
        break;
    }
    // 以降は従来どおり HitTestSlideBand で帯を探して挿入
    ...
}
```

- **順序の根拠**: `HitTestPoint`（点のみ）→ `HitTestSlideBand`（帯のみ）の順は
  `editor-ui-rework-mmw.md §5.2` で分割した2つのヒットテストの役割そのもの。
  既存の点の真上に中継点を挿入しても同 tick で退化するだけなので、点を優先して損は無い。
- ドラッグ（`BeginPointDrag`）まで含めるのも他ツールと揃える。中継点ツールのまま
  掴んだ点を微調整できるほうが自然。
- **ゴースト側も揃える**: `DrawPlacementGhost` の AddWaypoint 分岐（`:1595-1599`）は
  帯ヒットのときだけゴーストを出す。点の上にカーソルがあるときはゴーストを出さないよう、
  同じ `HitTestPoint` 判定を入れて**ゴーストと実際の挙動を一致させる**
  （§7 のゴースト実装以来守っている「PointerDown 側の計算をなぞる」規則）。

---

## 4. シークバーの長さを「音源長+10秒」に固定

**現状**: `ChartEditorApp.UI.cs:1548`

```csharp
float scrubMax = Mathf.Max(10f, preview.ChartEndSec + 2f);
```

譜面の最終ノーツ基準なので、**譜面を編集するたびにシークバーの目盛りが伸び縮みする**。

**変更**:

```csharp
float audioLen = preview.AudioLengthSec;                 // 未読み込みなら -1
float scrubMax = audioLen > 0f
    ? audioLen + 10f
    : Mathf.Max(10f, preview.ChartEndSec + 10f);         // 音源が無い間の従来相当
```

- 音源が読み込まれている限り**長さが固定**になる（ユーザー要望の主眼）。
- 末尾10秒は音源の外側なので、§1 で入れた「音源の外は無音で時間だけ進む」挙動が効く
  （ユーザー確定）。譜面の末尾ノーツが音源の終端付近にある場合の確認に使える。
- **オフセットとの関係（既知の近似）**: 譜面時間の定義域は厳密には
  `[-Offset, clipLength - Offset]`。ここでは `Offset` を足し引きせず `audioLen + 10` を
  そのまま上限にする。正のオフセット（前奏あり）では実際の譜面終端より最大 `Offset` 秒だけ
  余分に見える＝余白が増えるだけで実害が無く、ユーザーの指定（「音源長+10秒」）どおりの
  分かりやすい値になるため。この判断は実装時にコメントとして残す。

---

## 5. プレビュー画面でのホイールスクロール

**現状**: `previewSurface`（`ChartEditorApp.UI.cs:534`）は `RenderTexture` を背景に描くだけで、
ポインタ・ホイールのイベントを一切受けていない。プレビュータブを開いている間は時間移動の
手段がスクラブスライダーしか無い。

### 5.1 通常のホイール → タイムラインと同じ時間スクロール

`OnSheetWheel`（`ChartEditorApp.cs:2432`）を**そのまま再利用**して `previewSurface` に登録する。

```csharp
previewSurface.RegisterCallback<WheelEvent>(OnSheetWheel);
```

これだけで意図どおり動く根拠:

- `OnSheetWheel` の非ズーム経路は `scrollTick` を動かすだけで、`notesSheet` の
  レイアウトやマウス座標に依存していない。
- **停止中は `scrollTick` → `preview.Seek` の同期が既に `Update()` にある**
  （`ChartEditorApp.cs:551-555`、r3 §8）。つまり `scrollTick` を動かせばプレビューの
  時刻が追従するという配線が既に完成している。
- 再生中は `followPlayback` が逆方向に駆動するので、タイムライン上のホイールと同じく
  「再生中はスクロールしても引き戻される」。挙動が2つのタブで一致する。
- `invertScroll` 設定・Shift+ホイールの4倍速もそのまま共有される。

### 5.2 Ctrl(Cmd)+ホイール → ハイスピード（ユーザー確定）

タイムライン上では Ctrl+ホイールが `pxPerBeat`（タイムラインの拡大率）だが、
プレビューでは見た目に何も起きない。ユーザー確定でここは**ハイスピード（ノーツ速度）**に割り当てる。

- `PreviewSystem` に `HiSpeed` プロパティを追加する。実体は
  `StageConfig.hiSpeed`（`cfg` は `PreviewSystem` の private readonly）。
  setter で `noteView.UpdateScroll(SongTime, cfg.hiSpeed)` と `MarkDirty()` を呼び、
  停止中でも即座に見た目へ反映する。
- 刻みと範囲: `0.1` 刻み、`0.5`〜`4.0` でクランプ（`hiSpeed` は
  `note-spec.md §5.5` の「実効速度 = baseSpeed × hiSpeed × scrollMul」の中間項で、
  プレイヤー操作用の倍率という位置づけ）。
- **どこに表示するか**: 現状ハイスピードを触る UI がどこにも無いため、
  変えたことが分かる手段が要る。**プレビュータブの左上にオーバーレイで
  `x1.0` を短時間表示**（`previewSurface` の `generateVisualContent` は既に
  判定線描画で使っているので、そこにラベルを1つ足すだけ）＋
  設定画面の「タイムライン」タブにスライダーを追加して恒久的に見えるようにする。
- **永続化**: `EditorSettings.hiSpeed`（既定 1.0）を追加する。音量（r6 §4.3）と同じく
  「譜面の属性ではなくエディタ側の設定」なので `song.muses` には入れない。

### 5.3 やらないこと

- プレビュー上でのドラッグ・クリックによるノーツ編集は**入れない**（3D投影の逆変換が必要で、
  スコープが大きく変わる）。今回はホイールのみ。

---

## 6. 実装順

依存と切り分けやすさで並べる。

1. **§3 中継点ツールの暴発回避**（数行・独立・回帰の心配が最も小さい）
2. **§1 負のオフセットの前奏区間**（`PreviewClock` に閉じる。オフセット0/正で回帰が無いことを
   ここで確認しておく）
3. **§4 シークバーの長さ**（§1 の末尾区間が動いている前提で確認できる）
4. **§2 曲フォルダと自動保存の格納先**（ファイルI/O。他項目と干渉しない）
5. **§5 プレビューのホイール**（§1〜§4 が動いた状態で最後に足す）

---

## 7. 実装時に確認すること（実機）

- §1: オフセット `-1` で先頭から再生できること／オフセット `+1`・`0` で従来どおりであること／
  前奏区間の途中で一時停止→再開しても時刻が飛ばないこと。
- §2: `songs/` 直下で「別名で保存」→ フォルダが自動生成されること／
  そのフォルダの中で再度「別名で保存」しても入れ子にならないこと／
  自動保存が `autosave/` に入り、復元プロンプトが正しく出ること。
- §4: 音源の末尾より後ろへスクラブして再生しても止まらないこと。
- §5: プレビュータブでホイールを回すとステージが時間方向に動くこと／
  Cmd+ホイールでノーツ速度が変わり、値が表示されること。

## 実装ログ（2026-08-03、`dotnet build`成功確認済み・Unity Editor未検証）

設計どおり §3→§1→§4→§2→§5 の順で実装した。

- **§3（`ChartEditorApp.cs`）**: `EditorTool.AddWaypoint` の `OnSheetPointerDown` に
  Tap/ExTap/Flick と同じ「既存の点をクリックしたら選択へ横取り」ブロックを追加。
  `DrawPlacementGhost` のAddWaypoint分岐にも同じ`HitTestPoint`判定を追加し、点の上では
  中継点ゴーストを出さないようにした（実挙動とゴーストの不一致を防ぐ）。
- **§1（`PreviewClock.cs`、全面書き換え）**: `silentT0`単独のフィールドを廃し、
  `anchorDsp`/`anchorAudio`のアンカー対に統一。`AudioTimeD`は「Running中・HasClip・
  仮想時刻(`VirtualAudioTime`)が`[0, clipLength)`の範囲内」のときだけ`source.time`を
  真の値として使い、それ以外（前奏区間の途中・末尾区間・音源無し・停止中）は仮想クロック
  （`pausedAt`または`anchorAudio + (dspTime-anchorDsp)*Rate`）を真の値にする。
  `Play()`は前奏区間ぶんの遅延を`PlayScheduled`の予約時刻に足すだけで実現し、`Seek()`の
  0クランプを撤去、`SetRate()`は前奏区間・末尾区間でアンカーと予約を組み直すようにした。
  `PreviewSystem`・`ChartEditorApp`側は無変更（設計どおりこの層に閉じ込まった）。
- **§4（`ChartEditorApp.UI.cs`）**: `scrubMax`の計算を`preview.AudioLengthSec + 10f`
  （音源未読み込み時は`ChartEndSec + 10f`にフォールバック）に変更。
- **§2（`ChartEditorApp.UI.cs`/`ChartEditorApp.cs`）**:
  - `ShowFileModal`の保存ボタンに、保存先が`songsRoot`直下そのものと判定されたときだけ
    ファイル名(拡張子除く)をフォルダ名として流用し`Directory.CreateDirectory`する分岐を追加。
    判定用`IsSongsRootItself`・サニタイズ用`SanitizeFolderName`を新設（後者はr7の
    `CreateNewSong`のフォルダ名検証と同じ禁止文字置換規則）。保存後`browseDir`もそのフォルダへ
    更新するため、同じフォルダ内での再度の「別名保存」では発動しない（入れ子にならない）。
  - 自動保存の格納先を`chartPath + ".autosave"`から`<曲フォルダ>/autosave/<ファイル名>.autosave`
    （`AutosavePathFor`ヘルパー）へ変更。`TickAutosave`で`Directory.CreateDirectory`する。
    `CheckAutosaveRestore`は新パスを優先し、無ければr7以前の置き場所（ファイルの真横）へ
    フォールバックする（既存環境の自動保存を取りこぼさないため）。
- **§5（`PreviewSystem.cs`/`ChartEditorApp.cs`/`ChartEditorApp.UI.cs`/`EditorSettings.cs`/
  `ChartEditorRoot.uss`）**:
  - `PreviewSystem.HiSpeed`（`StageConfig.hiSpeed`のラッパー、0.5〜4.0にクランプ）を新設。
  - `previewSurface`に`OnPreviewWheel`を登録。Cmd/Ctrl+ホイールは`HiSpeed`を増減、
    それ以外は既存の`OnSheetWheel`をそのまま呼ぶ（`scrollTick`経由で停止中の`preview.Seek`が
    追従する既存配線をタイムラインと共有）。
  - プレビュー左上に`hiSpeedLabel`（絶対配置のLabel、`.hi-speed-label`）を追加し、
    `SyncModelToUi`で`HS {value}x`を毎フレーム表示。設定画面「タイムライン」タブにも
    恒久調整用のスライダーを追加。
  - `EditorSettings.hiSpeed`（既定1.0）を新設し、音量と同じ扱いで`Awake`/
    `SaveSettingsFromLiveFields`に配線（`song.muses`には入れない）。

**未検証のまま残した点**: Unity Editorでの実機確認一式（特にオフセット負での0秒再生、
音源末尾より後ろへのシーク、songsRoot直下保存でのフォルダ自動生成、プレビューのホイール操作）。

## 関連

- `memory/editor-ui-rework-r7.md` — 直前の増分（配置時の選択挙動・song.muses未生成バグ・
  曲フォルダの既定地・音源インポート・新規曲ウィザード）。§2・§3 はここの続き。
- `memory/editor-ui-rework-r4.md` §12 — `@OFFSET` の符号の向き。本書 §1 でその検証が完了した。
- `memory/editor-ui-rework-r6.md` §4 — 音源読み込み・音量。§5.2 の設定の置き場所の根拠。
- `memory/editor-spec.md` §1.2 — `songs/<song-id>/` のフォルダ規約。§2 の根拠。
- `memory/note-spec.md` §5.5 — `hiSpeed` の位置づけ。§5.2 の根拠。
