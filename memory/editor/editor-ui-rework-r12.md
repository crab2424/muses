# 譜面エディタ UI 改修 r12 設計書

対象: `unity/Assets/Scripts/ChartEditorApp/`（`ChartEditorApp.cs` / `.UI.cs` / `.Commands.cs` /
`EditorSettings.cs` / `ImeBridge.cs`）、`unity/Assets/UI/ChartEditor/`（`.uxml` / `.uss`）、
`unity/Assets/Scripts/Chart/ChartSerializer.cs`。

前提: r11（`editor-ui-rework-r11.md`、コミット`919fdb2`）の実機確認で出た3テーマ。
r11 §2.3 で入れた IME ブリッジは**診断モードとして意図どおり動いた**（実機スクリーンショットで
`composition回数=48` / `textInput回数=52` / `最新char='お'` を確認）。今回はその結果を踏まえた
本実装と、判明した副作用の修正が中心になる。

方針の確認（ユーザー回答済み、2026-08-04）:
1. 右パネル → **インスペクタを4タブ目にする**（切替式）。
2. 自動保存の復元 → **バグ修正＋設定で挙動を選べる**。
3. IMEの未確定文字 → **入力欄の中にインライン表示**。
4. 日本語フォント → **フォントファイルの取得もこちらで行う**（実行前に改めて確認する）。

---

## §0 先に確定した3つの原因（推測ではなくコード上で確定）

### §0.1 ショートカット全滅は IME 機能ではなく「オーバーレイ層の常駐子要素」が原因

`ImeBridge` のコンストラクタが `compositionOverlay` と `debugLabel` を **`overlayLayer` へ常駐で
Add している**（`ImeBridge.cs:206,211`）。両方 `display: none` だが、要素としては常に子である。

一方ショートカットのディスパッチは、モーダル表示中に譜面コマンドを撃たせないための門番として
`overlayLayer.childCount > 0` を使っている（`ChartEditorApp.Commands.cs:134`、r6 §3.3(2)で追加）。

結果、**起動直後から `childCount == 2` で常に「モーダル表示中」と誤判定され、
`OnGlobalKeyDown` が全コマンドを撃つ前に return していた**。ユーザー報告の
「ショートカットキー全般が効かなくなった」と完全に一致する。IME の副作用ではあるが、
IME の仕組みそのものとは無関係の配線ミス。

**How to apply**: 「特定の要素が存在するか」を機能の状態フラグ代わりに使う設計
（`childCount > 0` で「モーダル中」を表す等）は、後から同じ親に別用途の要素が足された瞬間に壊れる。
状態は要素の有無ではなく明示的な状態として持つ（§3.1 の修正方針）。

### §0.2 起動直後の復元案内は「消されない untitled 自動保存」の遺物

`CheckUntitledAutosaveRestore()`（`ChartEditorApp.cs:962`）は
`persistentDataPath/untitled.muses.autosave` が**存在するだけで無条件に**案内を出す。
そしてこのファイルは:

- 復元しても削除されない（`RestoreFromAutosave` は消さない）
- 「無視する」でも削除されない（`showRestorePrompt=false` にするだけ、`ChartEditorApp.UI.cs:2666`）
- その後ちゃんと名前を付けて保存しても削除されない（`DoSaveChartToPath` は autosave に触れない）

ため、一度でも未保存の新規譜面を作った環境では**以後永久に毎起動で案内が出る**。
ユーザーが言う「開いた時のファイルが不明なパス（もう削除した）」の正体もこれで、
中身は当時の未保存譜面のスナップショットであって、現存する曲フォルダとは無関係。
起動時点でプロジェクトを開いていないのに案内が出るのも同じ理由（この経路は
「開いているファイル」と対応する概念を持たない）。

### §0.3 プロジェクトを開くたびの案内は「mtime だけを見て、消しも無効化もしない」から

`CheckAutosaveRestore(path)`（`ChartEditorApp.cs:946`）は
`autosave の mtime > 譜面ファイルの mtime` だけで案内を出す。ここに3つの穴が重なっている:

1. **自動保存ファイルは正規保存時に削除も無効化もされない**。編集 → 自動保存 → 保存せず終了、を
   一度でもやるとその曲は以後**毎回**案内が出る（無視しても状態が何も変わらないため）。
2. **r10 §3 の副作用**: `OpenChartFromPath` は基準イベントを補ったとき `dirty = true` にする
   （`ChartEditorApp.cs:711`）。`TickAutosave` は `dirty` だけを見るので、**ユーザーが何も
   編集していなくても**開いて5分放置すれば自動保存が走り、上記1の状態に入る。
3. **内容ではなく時刻でしか比較していない**。中身が正規ファイルと1バイトも違わなくても、
   mtime が新しければ案内が出る。

---

## §1 右パネル: インスペクタを4タブ目にする

### §1.1 現状

`ChartEditorRoot.uxml:39-68` の `right-panel` は縦2段構成:

```
right-panel (width:300px)
├─ right-tab-header      … 曲 / 表示 / 結果 の3ボタン
├─ right-tab-body        … flex-grow:1、3枚の ScrollView を display で出し分け
└─ right-inspector       … max-height:45% の常設枠（見出し + inspector-scroll）
```

`.right-inspector { max-height: 45% }`（`ChartEditorRoot.uss:327`）のせいで、Slide の点が多い
ノーツを選ぶとインスペクタが常に内部スクロールになり、上の45%も潰れて両方見づらい、というのが
今回の指摘。

### §1.2 変更後

インスペクタを4枚目のタブにして、`right-inspector` という独立枠は廃止する。

```
right-panel
├─ right-tab-header      … 曲 / 表示 / 結果 / インスペクタ の4ボタン
└─ right-tab-body        … 4枚の ScrollView を display で出し分け（各タブが全高を使える）
```

- UXML: `right-inspector` ブロックを削除し、`right-tab-body` の中へ
  `<ui:ScrollView name="right-tab-inspector" class="right-scroll">` を追加、その中に
  既存の `inspector-host` を移す。見出し `subsection-heading--pinned` は他タブと同じ
  `subsection-heading` に戻す（タブ名で自明なので見出し自体を省いてもよいが、他3タブが
  見出し持ちなので揃える）。
- USS: `.right-inspector` / `.right-inspector .right-scroll` / `--pinned` 系を削除。
- `ChartEditorApp.UI.cs`:
  - `RightTabInspector = 3` を追加、`rightTabInspectorButton` / `rightTabInspectorBody` を
    既存3枚と同じ書き方で追加（`BuildRightTabs` / `SelectRightTab` に1行ずつ）。
  - `SelectRightTab` は `foreach` ループ化してもよいが、既存の明示列挙のままでも4枚なら読める。
    **既存スタイル踏襲**で明示列挙のまま増やす。
- `ChartEditorApp.cs:485` の `rightTabIndex = Mathf.Clamp(ws.rightTabIndex, 0, 2)` を
  `0, 3` へ。r11 §3.3 で書いたとおり、**クランプを必ず通す設計にしてあるおかげで
  古い設定ファイル（0〜2 しか入っていない）からの復元も無変更で通る**（値域を広げる方向なので
  データ移行不要）。

### §1.3 選択時の自動切替

タブ化するとインスペクタが隠れうるので、「ノーツを選択したのに何も起きないように見える」
退行を避ける必要がある。

- **既定ON**: 選択が「空 → 非空」に変わった瞬間（およびイベントチップを選択した瞬間）に
  `SelectRightTab(RightTabInspector)` する。空→非空の**変化時のみ**で、選択中にドラッグする
  たびに切り替えはしない（他タブを見ながらの編集を邪魔しない）。
- 設定「一般」タブに `選択時にインスペクタへ切り替える`（`EditorSettings.autoFocusInspector`、
  既定true）を追加。ユーザー方針の「モーダルで明示的に選ぶ設定 / 操作の結果で変わる作業状態」
  の区別（r11 §3.2）では**前者＝設定**に属する。
- 検証結果の `SelectRightTab(RightTabResults)`（r11 §4で追加済み）はそのまま。
  検証実行は明示操作なので、自動切替と競合しない。

### §1.4 リフレッシュの最適化を復活させる

r11 §4 で `foldInspector.value`（折りたたみ中はリフレッシュしない）を「常に更新」へ置換した。
タブ化で再び「見えていない」状態が生まれるので、`RefreshInspectorValues()` 相当の毎フレーム
処理を `rightTabIndex == RightTabInspector` のときだけ走らせる形に戻す。
`RebuildInspector()`（構造の作り直し）側は、非表示タブでも選択状態の整合を保つため
従来どおり走らせる（コストは選択変化時のみ）。

---

## §2 自動保存の復元まわり

### §2.1 設計の芯: 「時刻」ではなく「内容」で判断し、ライフサイクルを明示する

3つの穴（§0.2 / §0.3）は、いずれも「自動保存ファイルがいつ意味を失うのか」が
どこにも書かれていないことに由来する。次の2点を土台に据える。

**(a) 内容比較を判断の基準にする**

`ChartSerializer.WriteChart` は `StringBuilder` を組んでから `File.WriteAllText` する構造
（`ChartSerializer.cs:264,413`）なので、次の分割を入れるだけで文字列比較ができる。

```csharp
public static string SerializeChart(ChartFileHeader header, ChartData chart, SongMeta song); // 新規（中身は現WriteChartの本体）
public static void WriteChart(string path, ...) => WriteText(path, SerializeChart(...));      // 既存シグネチャは維持
```

これで以下がすべて同じ物差しで書ける:

- 自動保存を書くか: 「現在の内容 != 最後にディスクへ書いた内容」なら書く。
- 復元を案内するか: 「自動保存の中身 != 正規ファイルの中身」なら案内する。

`ChartEditorApp` に `lastPersistedText`（読み込み時・保存時・自動保存時に更新）を1つ持つ。
これで §0.3-2（基準イベント補完由来の `dirty` で自動保存が走る）も自動的に消える
——補完済みの内容をそのまま書けば `lastPersistedText` と一致するため書かない…のではなく、
**補完によって内容は実際に変わっている**ので初回は1回だけ書かれる。ここは
`dirty` 判定を触るより、後述の (b) と §2.3 の「内容が異なる時のみ案内」で吸収するほうが
筋が良い（補完結果は保存されるべき変更ではあるので、自動保存に載ること自体は正しい）。

`dirty` フラグ（33箇所で立つ）には**手を入れない**。フラグを増やすと立て忘れの穴が
構造的に生まれるため、内容比較1本に寄せる（r11 §3.2 の作業状態永続化で採った
「差分比較で立て忘れを防ぐ」判断と同じ方針）。

**(b) 自動保存ファイルのライフサイクルを決める**

| いつ | 何をするか | 理由 |
| --- | --- | --- |
| 正規保存が成功した | 対応する autosave（新形式 `<曲>/autosave/*.autosave`・旧形式の真横・untitled）を**削除** | 保存した内容が正となり、autosave は定義上「保存されなかった作業」ではなくなる |
| 復元を実行した | 削除しない（直後 `dirty=true` なので次の自動保存で上書きされる） | ユーザーの取り消し余地を残す |
| 「無視する」を押した | **削除しない。設定に「無視済み」を記録する**（§2.2） | r10 の「ユーザーのデータを黙って消さない」方針を踏襲しつつ、二度と聞かれないようにする |
| 正常終了した（`OnDestroy`） | untitled autosave が残っていて内容が空でなければ**残す**、正常終了フラグを立てる | クラッシュ判定に使う（§2.4） |

### §2.2 「無視済み」の記録

`EditorSettings` に追加:

```csharp
[Serializable] public class DismissedAutosave { public string autosavePath; public string contentHash; }
public List<DismissedAutosave> dismissedAutosaves = new();
```

- `contentHash` は自動保存本文の安定ハッシュ（`System.Security.Cryptography.MD5` で十分。
  改竄検知ではなく同一性判定なので暗号強度は不要）。mtime ではなく**内容**で持つのが要点で、
  同じ内容の autosave なら二度と聞かれず、内容が変われば（＝新しい作業が入れば）また聞かれる。
- 上限20件のリングにして古い順に捨てる（設定ファイルが無限に育たないように）。
- 存在しないパスのエントリは `EditorSettingsStore.Load()` 時に掃除する。

### §2.3 案内の条件と設定

`CheckAutosaveRestore(path)` を次の順で判定する:

1. autosave ファイルが無ければ終わり（新形式 → 旧形式の順で探す、現状どおり）。
2. `restorePromptMode == Never` なら終わり。
3. autosave 本文を読む。空・パース不能なら終わり（壊れたファイルで案内しない）。
4. `restorePromptMode == WhenDifferent`（既定）なら、正規ファイル本文と**文字列一致するときは終わり**。
5. 「無視済み」に同じ contentHash があれば終わり。
6. ここまで来たら案内する。

設定「一般」タブに `自動保存の復元を確認`（`EditorSettings.restorePromptMode`）:

| 値 | 挙動 |
| --- | --- |
| `Always`(0) | 自動保存が存在し、mtime が正規ファイルより新しければ必ず聞く（＝現行に近い） |
| `WhenDifferent`(1、既定) | 上の判定どおり。内容が同じ／無視済みなら聞かない |
| `Never`(2) | 聞かない。代わりに復元可能なときステータスバーに `自動保存あり（ファイル > 自動保存から復元）` と出す |

`Never` を選んでも手が無くなると困るので、**ファイルメニューに「自動保存から復元…」を新設**する
（`restoreAutosavePath` が解決できるときだけ有効。`Never` 以外でも常に使える）。
これはコマンドテーブル（`CommandIds.FileRestoreAutosave`）にも載せる
（r5 §5 の「エディタができることは全部コマンドにする」方針に従う）。

### §2.4 起動時（untitled）の案内

`CheckUntitledAutosaveRestore()` を次のように厳しくする。**すべて満たすときだけ案内する**:

1. `untitled.muses.autosave` が存在し、内容が空でなくパースできる。
2. **前回セッションが正常終了していない**。`EditorSettings.cleanShutdown` を
   `Awake` の最後で `false` にして即保存し、`OnDestroy` で `true` にして保存する。
   起動時に読んだ値が `false` ならクラッシュ／強制終了とみなす。
3. `restorePromptMode != Never` かつ「無視済み」に入っていない。

正常終了時（`OnDestroy`）は、untitled autosave が残っていれば削除する
——正常終了とは「ユーザーがそのセッションを閉じる判断をした」ことであり、
未保存の新規譜面をそこで捨てる意思表示とみなす。**ただし黙って消えると事故なので、
未保存（`dirty` かつ `chartPath` 空、または `dirty`）のまま閉じようとしたときの
終了確認モーダルとセットで入れる**（§2.5）。この2つは必ず同時に実装する。

案内モーダル（`ShowRestoreModal`）の文面も直す。現状は untitled 経路でも
「自動保存ファイルの方が新しいです」と出て、**何のファイルの話なのか一切分からない**
（今回の「不明なパス」報告の直接原因）。表示するもの:

- 対象: 曲タイトル / 難易度（自動保存本文の `@TITLE` `@DIFFICULTY` から読む）
- 自動保存ファイルのフルパスと更新日時
- 正規ファイルのパスと更新日時（untitled 経路では「保存先未確定の新規譜面」と明記）
- ボタン: `復元する` / `無視する`（＝今後この内容では聞かない） / `自動保存を削除する`（明示的な破棄口）

### §2.5 終了時の未保存確認（§2.4 の前提としてセットで入れる）

現状 `Application.Quit()` は `ChartEditorApp.UI.cs:2860` から無条件に呼ばれ、
ウインドウの×で閉じた場合も何も聞かれない。

- `Application.wantsToQuit` にフックし、`dirty` なら
  `保存して終了 / 保存せず終了 / キャンセル` の確認モーダルを出して一旦終了をキャンセルする。
- 「保存せず終了」を選んだ場合のみ untitled autosave を削除する（§2.4）。
- macOS スタンドアロンで `wantsToQuit` が×ボタン経由でも呼ばれるかは実機確認が要る。
  呼ばれない場合は `OnApplicationQuit` でのフォールバック（確認は出せないので
  「untitled autosave は消さない」側に倒す）にする。**この分岐は実機で確かめてから確定する**。

---

## §3 IME 関連

### §3.1 ショートカット全滅の修正（最優先）

原因は §0.1。修正は2段構え:

1. **IME 用オーバーレイを `overlayLayer` から出す**。UXML に `ime-layer` を新設し
   （`overlay-layer` と同じく画面全面・`picking-mode: Ignore`、モーダルより下、他UIより上）、
   `ImeBridge` にはそれを渡す。IME 表示はモーダルの上に出す必要が無い
   （モーダル内の入力欄で変換するときは §3.2 のインライン表示なのでオーバーレイ自体を使わない）。
2. **`overlayLayer.childCount > 0` という判定をやめる**。`ChartEditorApp` に
   `modalDepth`（`ShowModal` で++、`CloseModal` で--）を持ち、`OnGlobalKeyDown` は
   `modalDepth > 0` を見る。自前メニューのポップアップ（`openMenuIndex >= 0`）は
   別途既に見ているのでそのまま。

1 だけでも今回の症状は消えるが、2 をやらないと同じ罠を次に踏む（§0.1 の How to apply）。
**両方入れる。**

回帰確認: 設定モーダル／ファイル参照モーダル／キー重複確認モーダル／自動保存復元モーダルの
表示中に Space・数字キー・Delete が譜面へ抜けないこと（r6 §3.3(2) で塞いだ穴の再発チェック）。

### §3.2 未確定文字のインライン表示

#### 実機で分かっていること

スクリーンショットの診断値より:

- `composition回数=48` … `Keyboard.onIMECompositionChange` は**発火している**。
- `textInput回数=52` `最新char='お'` … `Keyboard.onTextInput` も**発火している**。
- 入力欄には `あいうえ`、自前オーバーレイには `あいうえお` が出ている
  → **未確定のかな文字が、composition としてだけでなく textInput としても
  入力欄へ流し込まれている**（＝入力欄側にも文字が入ってしまっている）。

つまり現状は「未確定文字が二重に見えている」状態で、色・位置のずれ以前に
**確定時に文字が重複する危険がある**。ここがインライン化の本質的な動機になる。

#### 設計

`ImeBridge` を「表示するだけ」から「未確定文字列の所有者」に格上げする。
対象は **`TextField`（文字列）に限る**。`IntegerField` / `FloatField` は日本語入力の
必要が無いので、フォーカス時に `SetIMEEnabled(false)` にして OS の IME 自体を止める
（現状は数値欄でも IME が有効になり、変換窓が出て数値が打てない可能性がある）。

状態:

```csharp
TextField composingField;   // 変換中の対象（null なら非変換中）
string    baseText;         // 変換開始時点の確定済みテキスト
int       baseCursor;       // 変換開始時点のキャレット位置（baseText 内の index）
string    composition;      // 現在の未確定文字列
```

遷移:

| 契機 | 処理 |
| --- | --- |
| `onIMECompositionChange(s)` で `s` が非空、かつ `composingField == null` | 変換開始。`baseText`/`baseCursor` を現在の値から控える |
| `onIMECompositionChange(s)` で `s` が非空 | `composition = s`。表示テキスト = `baseText[..baseCursor] + s + baseText[baseCursor..]` を **`SetValueWithoutNotify`** で反映し、キャレットを `baseCursor + s.Length` に置く |
| `onIMECompositionChange("")` | 変換終了。表示を `baseText` に戻し（`SetValueWithoutNotify`）、確定文字列の到着を待つ（下記） |
| 確定文字列が到着 | `baseText` にそれを挿入して **`value =`（通知あり）** で確定。`composingField = null` |

`SetValueWithoutNotify` を使うのが重要な点で、現状のまま未確定文字が入力欄へ入ると
`song.artist` などのモデルが**未確定の途中経過で書き換わり `songMetaDirty` が立つ**
（`ChartEditorApp.UI.cs:806`）。確定時にだけ通知する形にすればこれも同時に直る。

#### 未確定文字が入力欄へ入るのを止める方法（要実機確認の分岐点）

UITK ランタイムパネルへ文字が届く経路が2つ考えられ、コードだけでは確定できない:

- **経路A**: `KeyDownEvent.character` として届く → `uiRoot` の TrickleDown ハンドラで
  変換中に `evt.StopPropagation()` すれば止められる（`ImeBridge` は既に TrickleDown で
  `KeyDownEvent` を購読しているので追加コストは無い）。
- **経路B**: InputForUI の `TextInputEvent` として届き、KeyDownEvent を経由しない
  → イベントを止められないので、`SetValueWithoutNotify` で**毎フレーム上書きし直す**
  （表示テキストは `ImeBridge` が唯一の権威、という扱いにする）方式に倒す。

**まず経路Aを実装し、診断オーバーレイに `KeyDown(character=..., keyCode=...)` の直近ログを
追加して実機で確認する**。止まらなければ経路Bへ切り替える。診断オーバーレイは
このために残す（トグルは既存の `IME診断表示`）。

#### 確定文字列の判定

`onIMECompositionChange("")` の後に `onTextInput` で確定文字が1文字ずつ来る想定だが、
順序（composition が空になるのが先か、textInput が先か）は実装依存。
**1フレーム分バッファして解決する**: `composition` が空になったフレームの
`onTextInput` 文字列を集め、`LateUpdate` 相当（`uiRoot.schedule` の1回実行）で
まとめて確定する。ESC で変換取消した場合は textInput が来ないので、
バッファ空 = 取消として `baseText` のまま確定処理を打ち切る。

#### 表示（位置・色・フォント）

インライン化により、位置・色・フォントは**入力欄自身のものになるので原理的にずれない**
（今回の指摘への直接の答え）。未確定であることの表示は下線が理想だが、
UITK のランタイムに文字単位の下線スタイルが無いことは r11 §2.3 で確認済み。代替:

- 未確定部分を `ITextSelection` の選択範囲（`cursorIndex`/`selectIndex`）にして
  **選択ハイライトで未確定を表す**。追加描画なしで実現でき、macOS の未確定表示（下線）とは
  見た目が違うが、確定済み/未確定の区別という目的は果たす。
- 変換候補窓（OS 側）の位置は引き続き `SetIMECursorPosition`。r11 で書いた
  「パネル座標 → デバイスピクセル → y反転」の換算式が正しいかは実機でしか分からないので、
  診断オーバーレイの `imeCursorScreenPos` と実際の候補窓の位置を見比べて詰める。
  **今回のスクリーンショットでは候補窓が写っていないため、まだ検証できていない。**

`.ime-composition-overlay` は使わなくなる（USS ごと削除）。`.ime-debug-overlay` は残す。

### §3.3 日本語フォント（Noto Sans JP）実装済み（2026-08-04、`dotnet build`成功確認・reflectionでAPI検証済み）

設計時点の想定（TMP_FontAsset.CreateFontAsset）は誤りで、正しくはUnity 6組み込みの
`UnityEngine.TextCore.Text.FontAsset.CreateFontAsset(Font, samplingPointSize, atlasPadding,
GlyphRenderMode, atlasWidth, atlasHeight, AtlasPopulationMode, enableMultiAtlasSupport)`
だった（TextMeshProパッケージ不要、確認済み: `Packages/manifest.json`にTMPro関連の記載は無い）。
実装前に`UnityEngine.TextCoreTextEngineModule.dll`をreflectionで走査し、`FontAsset`静的メソッド・
`PanelTextSettings`のプロパティ・`PanelSettings.textSettings`（publicフィールド、プロパティではない）
の実在をすべて確認してから書いた。

- フォント本体: Google Fontsの`google/fonts`リポジトリから`NotoSansJP[wght].ttf`
  （可変フォント、Regular〜Boldを1ファイルで内包、約9.6MB、OFLライセンス）を取得し
  `unity/Assets/UI/ChartEditor/Fonts/NotoSansJP-Variable.ttf`に配置。`LICENSE-OFL.txt`も同梱。
  設計時点で想定していた「Regular/Bold別ファイル」は、このフォントファミリーには静的個別ファイルが
  無かったため単一の可変フォントに変更（UnityのFont importerは既定の名前付きインスタンスを使う）。
- `unity/Assets/Editor/BuildJapaneseFontAsset.cs`（新規、`[MenuItem("Build/Setup Japanese Font
  (Chart Editor)")]`）: フォントから`FontAsset`（Dynamic方式、既存の容量削減方針を踏襲）を生成し、
  新規`PanelTextSettings`アセット(`ChartEditorTextSettings.asset`)へ`defaultFontAsset`/
  `fallbackFontAssets`として設定、`ChartEditorRoot`の`PanelSettings.asset`の`textSettings`
  フィールドへ配線する。冪等（既存アセットがあれば再利用）。
- `TextSettings.defaultFontAsset`はUnity 6.5.6f1時点で`Obsolete`（将来削除予定）マーク済みだが、
  代替プロパティがAPI上に存在せず、このバージョンでは機能する。`#pragma warning disable CS0618`で
  明示的に許容し、コメントで「将来のUnityアップデートでビルドが壊れたらAPIドキュメントを確認」と残した。
- PanelSettingsの`textSettings`はグローバル既定フォントとして効くため、USS側の個別ルール変更は
  不要と判断した（`--unity-font-definition`を個別要素に散らす必要がない）。

**未実行（次回ユーザーがUnity Editorで行う）**: メニュー`Build > Setup Japanese Font (Chart
Editor)`の実行。Unity Editorが実行中のプロジェクトへ`Unity -batchmode`を二重起動すると
プロジェクトロックで競合する（[[muses-unity-port-progress]]の既知の制約）ため、このセッションでは
ユーザーのEditorセッションが開いたままだったことを確認した上でheadless実行を見送った。
`dotnet build Assembly-CSharp-Editor.csproj`でのコンパイル確認のみ実施済み（`Compile Include`は
手動一時追加、`.csproj`はgitignore対象なのでコミットには含まれない、r11と同じ運用）。
実行後はChartEditorをPlayして日本語表示（字形・字間）を確認する。

### §3.3 旧設計メモ（実装は上記を参照）

現状: `PanelSettings.asset` は Unity 既定のランタイムテーマ（`UnityDefaultRuntimeTheme.tss`）を
参照しており、日本語グリフは OS フォールバックで出ている。字形・字間が macOS 標準に引きずられ、
`-unity-font-style: bold` などの指定も日本語には効かない。

導入手順（**Font Asset Creator の GUI 操作を使わず、エディタ拡張で自動化する**):

1. `unity/Assets/UI/ChartEditor/Fonts/NotoSansJP-Regular.ttf`（と Bold）を配置。
   OFL ライセンスなので `LICENSE-OFL.txt` も同じフォルダへ置く。
   **取得はダウンロードを伴うので、実行前に改めてユーザーへ確認する。**
2. `unity/Assets/Editor/BuildJapaneseFontAsset.cs`（新規、`[MenuItem]` 付き）で
   `TMP_FontAsset.CreateFontAsset(font, samplingPointSize:48, atlasPadding:5,
   GlyphRenderMode.SDFAA, atlasWidth:1024, atlasHeight:1024, AtlasPopulationMode.Dynamic)` を
   呼んで FontAsset を生成し `AssetDatabase.CreateAsset` する。
   **Dynamic 方式**を採るのは、日本語の全グリフを静的アトラスに焼くとサイズが跳ね上がるため
   （r5〜容量調査で 75MB まで落とした経緯があり、ここを膨らませたくない）。
3. 生成した FontAsset を `PanelSettings.textSettings`（`PanelTextSettings`）の
   `fallbackFontAssets` 先頭へ登録し、既定フォントとしても設定する。この差し替えも同スクリプトで行う。
4. USS 側は `--font-*` 変数を作って `.root` に当てる形にし、個別ルールで
   `-unity-font-definition` を散らさない。

**Dynamic 方式の注意**: アトラスは実行時に育つので、`AtlasPopulationMode.Dynamic` の FontAsset は
Play 中に生成されたグリフがアセットへ書き戻されて差分が出ることがある。
`Clear Dynamic Data on Build` 相当の設定を有効にしてビルド前に掃除する。

---

## §4 実装順序

依存関係が薄いので、影響の大きさ順に上から。

1. **§3.1 ショートカット修正**（`ime-layer` 新設 + `modalDepth` 化）。
   現状ほぼ全ショートカットが死んでいるので最優先。単独でコミット可能。
2. **§1 右パネル4タブ化**（UXML/USS + `SelectRightTab` + clamp + 自動切替設定）。
   1 と独立。
3. **§2 自動保存**（`SerializeChart` 分割 → 内容比較 → ライフサイクル → 設定 →
   モーダル文面 → 終了確認）。この中は上から順に依存する。
4. **§3.2 IME インライン化**。3 と独立だが、実機での試行錯誤が要るので後ろに置く。
5. **§3.3 フォント**。フォントファイル取得の確認待ちなので最後。

---

## §5 検証計画

`dotnet build Assembly-CSharp.csproj` でのコンパイル確認に加え、
純粋 C# 部分（`SerializeChart` の往復・内容比較・`DismissedAutosave` のハッシュ判定）は
スクラッチへ複製して `dotnet run` で検証する（既存手法どおり）。
新規 `.cs` を足すので、`.csproj` に `<Compile Include=...>` を手で足す必要が出る点も既知
（r11 の注記どおり、gitignore 対象なのでコミットには影響しない）。

実機（スタンドアロンビルド）で確認する項目:

1. **ショートカット**: 起動直後に数字キー・Space・Cmd+Z が効く。モーダル表示中は効かない。
   右パネルのテキスト欄編集中の Cmd+Z がテキスト側に行く（r5 で直した挙動の回帰確認）。
2. **右パネル**: 4タブが切り替わる／インスペクタが全高を使える／ノーツ選択で自動的に
   インスペクタタブへ来る／設定でOFFにすると来ない／再起動で最後のタブが復元される。
3. **自動保存(起動時)**: 既存の `untitled.muses.autosave` があっても、正常終了後の起動では
   案内が出ない。強制終了（アクティビティモニタで kill）→ 再起動では案内が出て、
   モーダルに曲名・難易度・日時・パスが出る。
4. **自動保存(プロジェクト)**: 開いただけ→5分放置→開き直しで案内が出ない。
   実際に編集→保存せず終了→開き直しで**出る**。「無視する」→ 開き直しで**出ない**。
   その後さらに編集して自動保存が更新されたら**また出る**。
5. **保存で消える**: 編集→自動保存→正規保存 のあと `<曲>/autosave/` が空になっている。
6. **終了確認**: 未保存で×ボタン → 確認が出る（出ない場合は §2.5 のフォールバック分岐へ）。
7. **IME**: アーティスト欄で `あいうえお` と入力 → 未確定文字が**入力欄の中に**出る／
   二重に出ない／確定するまで `songMetaDirty` が立たない（＝未保存マークが点かない）／
   ESC で取消できる／数値欄では IME が起動しない／候補窓の位置が実キャレットに近い。
   診断オーバーレイの `KeyDown(character=...)` ログで経路A/Bのどちらかを確定させる。
8. **フォント**: 日本語の字形が Noto Sans JP になる／ビルドサイズの増分を `du -sh` で計測。

---

## §6 未決事項（実機の結果を見てから決める）

1. **未確定文字の経路がA（KeyDownEvent）かB（TextInputEvent）か** → §3.2 の実装方式が変わる。
2. **`Application.wantsToQuit` が macOS の×ボタンで発火するか** → §2.5 のフォールバック要否。
3. **`SetIMECursorPosition` の座標換算式** → r11 から持ち越し。候補窓が実際にどこへ出るかを見て補正。
4. **未確定部分の見せ方**（選択ハイライト代用で許容できるか） → 実機の見た目で判断。
5. **`restorePromptMode` の既定値** を `WhenDifferent` で始めるが、実運用で
   「それでも多い／少ない」となれば見直す。
