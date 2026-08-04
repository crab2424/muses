> **実装状況(2026-08-04)**: §1(難易度選択式)・§3(作業状態の記憶)・§4(右パネル再構成)・
> §2.1(キャレット点滅)・§2.3(IME診断モード)を実装済み、`dotnet build`成功確認済み。
> §2.4(日本語フォント)は Unity Editor でのGUI操作が必須のためユーザー作業として未着手。
> §2.3 の本実装(候補窓位置の補正等)は実機での診断結果待ち。詳細は
> [[muses-unity-port-progress]] のr11実装ログ参照。

# 譜面エディタ UI改修 第11弾 (r11) — 難易度の選択式化 / テキスト入力(キャレット・IME・フォント) / 作業状態の記憶 / 右パネルの整理

前提: `editor-ui-rework-r10.md`。r10 の実機確認と並行して出た4テーマに対応する。

このうち §2（テキスト入力）は**Unity 6000.5.6f1 の実アセンブリと Input System パッケージの
ソースを読んで原因を確定させた**。推測を含む箇所はその旨を明記している。

ユーザー確定事項（このセッションで確認済み）:
- 右パネルは**タブ3枚＋インスペクタ常設**（§4 案A）
- 日本語フォントは**OSフォント参照（Dynamic OS）**（§2.4 案A）
- IMEは**自前ブリッジで実用レベルまで**（§2.3 案A）

---

## §1 難易度を選択式にする

### 1.1 現状

`ChartEditorApp.UI.cs:799` — 情報セクションの難易度は自由入力の `TextField`:

```csharp
infoDifficulty = AddTextRow(infoHost, "難易度", v => { header.difficulty = v; dirty = true; });
```

一方で難易度の値は既に**4値に固定された概念**として使われている:

- `ChartEditorApp.UI.cs:2616` — `DifficultyChoices = { "LINE", "SQUARE", "CUBE", "TESSERACT" }`
  （新規曲ウィザードは既に `DropdownField`）
- `ChartEditorApp.cs:680` — 保存時に `header.difficulty.ToLowerInvariant() + ".muses"` を
  **ファイル名として使う**（r9 §5.3 のリネーム処理）

つまり自由入力のままだと、`Cube ` のような打ち間違いがそのままファイル名になり、
`cube .muses` のようなファイルが生まれる。選択式にするのはUIの好みではなく**データの
妥当性の問題**でもある。

### 1.2 変更

`AddTextRow` → 新設 `AddChoiceRow`（文字列の選択肢版。既存の `AddEnumRow` は enum 専用なので
そのままでは使えない）:

```csharp
private DropdownField AddChoiceRow(VisualElement parent, string label,
                                   IEnumerable<string> choices, Action<string> onChange)
```

- 選択肢は `DifficultyChoices` をそのまま使う（ウィザードと同じ定数を共有する）。
- **未知の値の扱い**: 既存ファイルの `@DIFFICULTY` が4値以外だった場合、
  `choices` の末尾にその値を1つだけ足して選択状態にする（黙って `CUBE` に化けさせない）。
  ファイルを開くたびに `SyncModelToUi` で判定するので、`RefreshChoiceIfUnfocused`
  （`RefreshEnumIfUnfocused` の文字列版）で「フォーカス中は触らない」既存作法に合わせる。
- `header.difficulty` の書き込みと `dirty = true` は現状のまま。

### 1.3 難易度を変えたときの副作用の明示

現状、難易度を変えると**次の保存で譜面ファイル名がリネームされる**（r9 §5.3）。
自由入力だった間は「そういうものだ」で済んでいたが、ドロップダウンになると
気軽に切り替えられるようになるため、選択肢の下に注記ラベルを1行置く:

> 保存時にファイル名が `<難易度>.muses` へ変わります。

（確認モーダルは既に「同名ファイルが既にある場合」に出るので、それ以上は増やさない。）

---

## §2 テキスト入力（キャレット・IME・フォント）

3つの症状は**それぞれ別の原因**なので、独立した3小節に分ける。

### 2.1 キャレットが点滅しない — UI Toolkit の仕様（バグではない）

`UnityEngine.UIElementsModule.dll` を走査した結果、**blink/点滅に相当するメンバは1つも
存在しない**（`TextElement.DrawCaret(MeshGenerationContext)` があるだけ）。
つまり UITK ランタイムのキャレットは常時実線で描かれる仕様で、点滅は自前で作るしかない。

幸い、必要なAPIは公開されている:

```csharp
UnityEngine.UIElements.TextElement.selection : ITextSelection   // public
ITextSelection.cursorColor { get; set; }                         // public
ITextSelection.cursorPosition { get; }                           // public (要素ローカル座標)
```

**実装**: `ImeAndCaretBridge`（§2.3 と同じ新規クラスに同居させる）で、

1. `uiRoot` のフォーカス変化（`FocusInEvent`/`FocusOutEvent` を root で捕捉）を監視し、
   フォーカス中の `TextField`/`IntegerField`/`FloatField` の内部 `TextElement` を保持する。
2. `panel.schedule.Execute(...).Every(530)` で `cursorColor` のアルファを 1↔0 でトグルし、
   `MarkDirtyRepaint()` を呼ぶ。530ms は macOS のキャレット点滅周期に合わせた値。
3. **キー入力があった直後は点滅をリセットして必ず表示状態に戻す**（打っている最中に
   キャレットが消えるのは実装として不快）。`KeyDownEvent`/文字入力で位相をリセットする。
4. フォーカスが外れたら元の色へ戻す（`--unity-cursor-color` 由来の値を退避しておく）。

**注意**: `cursorColor` を毎フレーム書き換えるのではなく530ms周期のスケジューラで書く。
UITKの再描画はダーティ駆動なので、毎フレーム `MarkDirtyRepaint` すると
入力欄がある間ずっと再描画が走る（フレームレート制限の設定を入れた r5 §2 の趣旨に反する）。

### 2.2 IME が効かない・かな候補窓が画面下部中央に出る — 原因確定

**原因は Unity 側の未実装**で、mus­es のコードの誤りではない。経路は次の通り:

```
OS(IME) → Input System(Keyboard) → UnityEngine.InputForUI.EventProvider
        → DefaultEventSystem.InputForUIProcessor.ProcessIMECompositionEvent
        → KeyboardTextEditorEventHandler.OnIMEInput → TextField
```

このうち **Input System パッケージの provider が IME を実装していない**:

`Library/PackageCache/com.unity.inputsystem@.../InputForUI/InputSystemProvider.cs:343-346`

```csharp
// TODO
case Event.Type.IMECompositionEvent:
default:
    return false;
```

対して**旧 Input Manager 側の provider には実装がある**
（`UnityEngine.InputForUI.InputManagerProvider` に `CheckIfIMEChanged` /
`ToIMECompositionEvent(DiscreteTime, string)` / `_compositionString` が存在する）。

本プロジェクトは `ProjectSettings.asset: activeInputHandler: 1`（New Input System 専用）なので、
**IMEイベントがUIへ届く経路が構造的に存在しない**。候補窓が画面下部中央に出るのも、
キャレット位置をOSへ知らせる `Keyboard.SetIMECursorPosition` を誰も呼んでいないため
（既定位置のまま）。

#### 採らない選択肢とその理由

| 案 | 却下理由 |
|---|---|
| `activeInputHandler` を「Input Manager (Old)」へ戻す | 唯一 Unity 標準経路でIMEが通る案だが、ゲーム側 `TouchInputManager` が EnhancedTouch 前提で全面書き直しになる。エディタのテキスト入力のために本編の入力層を壊すのは割に合わない |
| 「Both」にする | パッケージのコメントに *"Only if InputSystem is enabled in the PlayerSettings do we set it as the provider. **This includes situations where both InputManager and InputSystem are enabled.**"*（`InputSystemProvider.cs:61-67`）とあり、Both でも InputSystem 側の provider が選ばれる。直らない |
| `EventProvider` へ自前 provider を差す | `UnityEngine.InputForUI` の型は `IMECompositionEvent` を含め**すべて internal**（実アセンブリで確認済み）。リフレクション頼みになりUnityの更新で壊れる |

### 2.3 IME: 自前ブリッジ（採用）

UITK の内部IME経路は使わず、**Input System の公開IME APIとUITKの公開APIだけ**で組む。
新規 `Assets/Scripts/ChartEditorApp/ImeBridge.cs`。

使うAPI（すべて public、`com.unity.inputsystem` で確認済み）:

```csharp
Keyboard.SetIMEEnabled(bool)                       // Keyboard.cs:1227
Keyboard.SetIMECursorPosition(Vector2)             // Keyboard.cs:1202 付近
Keyboard.onIMECompositionChange += (IMECompositionString c) => ...   // Keyboard.cs:1181
Keyboard.imeSelected                               // ButtonControl。IME が選択中かの状態
```

#### 動作

1. **有効化**: テキスト系フィールドがフォーカスを得たら `SetIMEEnabled(true)`、
   外れたら `false`。エディタ全体で常時ONにはしない（タイムライン上でのショートカット
   （数字キーのツール切替等）がIMEに食われるのを避けるため）。
2. **候補窓の位置**: フォーカス中の `TextElement.selection.cursorPosition`（要素ローカル）を
   `element.LocalToWorld` → パネル座標 → **スクリーン座標（y上向き）** に変換して
   `SetIMECursorPosition` へ渡す。UITK はy下向き、Unityのスクリーン座標はy上向きなので
   `y = Screen.height - panelY` の反転が要る（`InputSystemProvider.ScreenBottomLeftToPanelPosition`
   と同じ変換の逆）。**UI Scale（r5 の `PanelSettings.referenceResolution` 操作）が入るので、
   パネル座標→スクリーン座標は `panel.visualTree` のスケールを経由して求める**
   （固定倍率をハードコードしない）。
3. **変換中の表示**: `onIMECompositionChange` で受けた文字列を、キャレット位置に重ねた
   専用のオーバーレイ `Label`（`overlay-layer` 上、`position: absolute`）に表示する。
   下線は `border-bottom-width: 1px` で表現する（UITKのテキストに下線スタイルは無いため）。
   変換文字列が空になったら Label を消す。
4. **確定文字**: `InputSystemProvider` は確定文字を `TextInputEvent` として通常どおり流すので、
   **確定した文字列はそのまま入力欄へ入るはず**。ここは Unity 側の実装依存なので
   §2.5 の診断モードで最初に確かめる。

#### 段階的に進める（重要）

macOS で `SetIMEEnabled` / `SetIMECursorPosition` が実際に効くかは Unity のネイティブ実装依存で、
コードを読んだだけでは確定できない。**本実装の前に診断モードを入れて実機で1回確かめる**:

`ImeBridge` に `debugOverlay` を持たせ、有効時はステータスバー付近に

- `Keyboard.imeSelected` の現在値
- `onIMECompositionChange` が発火した回数と最新の文字列
- `onTextInput` が発火した回数と最新の文字
- 直近に `SetIMECursorPosition` へ渡したスクリーン座標

を出す。設定モーダルの「一般」タブに `IME診断表示` トグルを1つ足す（既定OFF）。

この診断で分岐する:

| 実機の結果 | 進め方 |
|---|---|
| composition が飛んできて候補窓も動く | 上記1〜4をそのまま完成させる（狙いどおり） |
| composition は飛ぶが候補窓が動かない | 変換中文字列の自前表示だけで実用にする（候補窓は既定位置のまま） |
| composition が飛ばない | IMEは Unity 側で未サポートと判断。§2.6 の回避策に切り替え、この判断を記録して打ち切る |

**推測を実装で固定しない**ためにこの順序にする。

### 2.4 日本語フォント（Dynamic OS で用意）

現状 `PanelSettings.asset` の `textSettings: {fileID: 0}`、プロジェクトに `.ttf`/`.otf`/
FontAsset は**1つも無い**。今表示されている日本語は Unity のフォールバック任せで、
「標準でない感じ」の正体はこれ。

**方式**: OS搭載フォントを参照する FontAsset（Atlas Population Mode = **Dynamic OS**）を作る。
アプリ容量の増加はゼロ（`74aec45` の 115MB→75MB の成果を損なわない）。

手順（**Unity Editor のGUI操作が必要。ユーザー作業**）:

1. `Assets/UI/ChartEditor/Fonts/` を作る。
2. Window > TextMeshPro > Font Asset Creator、または Project で右クリック >
   Create > Text > Font Asset を作り、**Atlas Population Mode を `Dynamic OS`**、
   Source Font File 名に `Hiragino Sans`（macOSの標準日本語フォント）を指定する。
   名前は `JP-DynamicOS` とする。
3. `Create > UI Toolkit > Text Settings` で `ChartEditorTextSettings` を作り、
   `Fallback Font Assets` の先頭に 2 を入れる。
4. `PanelSettings.asset` の `Text Settings` に 3 を割り当てる。

USS 側は既存の `--font-sm` 等のサイズ指定に手を入れず、root に

```css
.root { -unity-font-definition: url("project://database/Assets/UI/ChartEditor/Fonts/JP-DynamicOS.asset"); }
```

を足すだけにする（個別セレクタでのフォント指定はしない。r4 §7 の教訓どおり、
テーマ側の指定に負ける／勝つの関係を増やさない）。

**将来のWindows対応**: `Dynamic OS` はフォント名がOS依存（Windowsなら `Yu Gothic UI` 等）。
Windows でエディタを動かす段になったら、FontAsset をもう1つ作って
`Application.platform` で `PanelSettings.textSettings` を差し替えるか、その時点で
Noto Sans JP 同梱へ切り替える。**今はやらない**（macOS単独ビルドなので不要な複雑さ）。

### 2.5 影響範囲

`ImeBridge` は `ChartEditorApp` からのみ生成し、`uiRoot` を渡す。ゲーム側
（`GameController` 等）には一切触れない。テキスト入力があるのはエディタだけなので、
本編のビルドに新しい依存を持ち込まない。

### 2.6 IMEが不可だった場合の回避策（フォールバック）

診断で「composition が飛ばない」と分かった場合に限り採る:

- 日本語が要る欄（タイトル／アーティスト／譜面制作者）に「クリップボードから貼り付け」
  ボタンを付ける（`GUIUtility.systemCopyBuffer` を読むだけ。Cmd+V が効くならボタンは不要）。
- その旨をラベルで明示する。

---

## §3 作業状態（タイムライン倍率・スナップ・レーン表示）の記憶

### 3.1 何を「設定」と区別するか

ユーザーの指摘どおり、これらは**設定画面に並べるものではない**。
性質が違う:

| | 設定（`設定...`モーダルに出る） | 作業状態（今回追加） |
|---|---|---|
| 変え方 | モーダルで明示的に選ぶ | 編集中の操作（ホイール・ドロップダウン・メニュー）の副作用で変わる |
| 頻度 | まれ | 常時 |
| 例 | オートセーブ間隔・キーバインド・UI倍率 | ズーム倍率・スナップ・レーン表示 |

置き場所は**同じ `editor-settings.json` の中に別グループ**として持つ。別ファイルにすると
読み書き経路と保存タイミングが二重になるだけで、得るものが無い。

```csharp
[Serializable]
public class WorkspaceState
{
    public float pxPerBeat = 28f;   // ZoomBasePxPerBeat と同値
    public int snapIndex = 3;       // 1/16
    public bool showHeightLane = false;
    public bool showEventLane = true;
}

// EditorSettings に追加
public WorkspaceState workspace = new();
```

**設定モーダルには一切出さない**。`EditorSettings` のこのグループには
「UIから編集しない、操作の結果だけが入る」ことをコメントで明記する。

### 3.2 保存タイミング

`SaveSettingsFromLiveFields()`（`ChartEditorApp.cs:505`）に4行足すだけで、
既存の呼び出し（設定モーダルを閉じたとき・`OnDestroy`）に相乗りできる。

ただし `OnDestroy` 頼みだと**異常終了で失う**。ズーム/スナップは操作頻度が高く、
「昨日いじった状態」が消えると意図が伝わりにくいので、`TickAutosave()` と同じ場所で
**変更があってから10秒後に1回だけ書く**遅延保存を足す:

```csharp
private bool workspaceDirty;   // pxPerBeat/snapIndex/showHeightLane/showEventLane を書き換える箇所で立てる
private float workspaceDirtySince;
```

`EditorSettingsStore.Save` は JSON 全体を書き直すだけの軽い処理なので、
この頻度なら実測を待たずに問題ない。

### 3.3 復元タイミング

`ChartEditorApp.cs:429` の `settings = EditorSettingsStore.Load();` の直後、
既存の `followPlayback = settings.followPlayback;` 等と同じ場所で読み戻す。

**クランプを必ず通す**: `pxPerBeat` は `Mathf.Clamp(v, ZoomMinPxPerBeat, ZoomMaxPxPerBeat)`、
`snapIndex` は `Mathf.Clamp(v, 0, SnapDenominators.Length - 1)`。
手で編集された／将来 `SnapDenominators` を変えた設定ファイルで壊れないようにする
（r6 §1.5 のキーバインド差分マージと同じ「古い設定ファイルでも壊れない」方針）。

### 3.4 記憶しないもの（意図的）

- `scrollTick` / `cursorTick`（再生位置）: 曲ごとに意味が変わる値なので、グローバルな
  設定ファイルに持つのは誤り。曲ごとに持つ案もあるが、今回のスコープ外。
- `hiSpeed` / 音量: 既に `EditorSettings` 直下にあり、設定画面にも出ている（現状維持）。

---

## §4 右パネルの整理（タブ3枚＋インスペクタ常設）

### 4.1 現状と問題

`ChartEditorRoot.uxml:37-57` — 7つの `Foldout` が縦1列のスクロールに並ぶ:

```
情報 / 音源 / 表示 / プリセット / 統計 / インスペクタ / 検証結果
```

問題は数そのものより**性質の違うものが同列に並んでいる**こと:

| セクション | 性質 | 触る頻度 |
|---|---|---|
| 情報・音源 | 曲プロジェクトのメタ。曲を作り始めるときに1回 | 低 |
| 表示・プリセット | 編集の道具立て | 中 |
| 統計・検証結果 | 結果の閲覧（編集不可） | 中（節目で見る） |
| **インスペクタ** | **選択中のノーツ/イベントの編集** | **常時** |

現状は一番使うインスペクタが6番目にあり、上の折りたたみを開けると押し出される。

### 4.2 採用する形

```
┌─ 右パネル (300px) ───────────┐
│ [曲] [表示] [結果]   ← タブヘッダ    │
├──────────────────────────┤
│ (選択中タブの中身。スクロール)          │
│   曲   : 情報 + 音源                │
│   表示 : 表示 + プリセット            │
│   結果 : 統計 + 検証結果             │
├══════════════════════════┤ ← ドラッグで高さ変更（将来）
│ インスペクタ（常設・タブに関係なく表示）    │
│ (選択中のノーツ/イベント。スクロール)      │
└──────────────────────────┘
```

- **インスペクタだけ常設**。タブを切り替えてもノーツの編集が中断されない。
- タブ内は**折りたたみを廃止してフラットに並べる**（1タブに2セクションなので、
  タブ＋折りたたみの二重の開閉は無駄）。見出しは `Foldout` ではなく単なる見出しラベルにする。
- 既定タブは「表示」。起動直後にいちばん触るのが再生速度・レーン表示のため。
  **選択中のタブは §3 の `WorkspaceState` に含める**（`rightTabIndex`）。

### 4.3 実装方針

- タブヘッダは**既存の USS クラス `.tab-header` / `.tab-header-btn` /
  `.tab-header-btn--selected` をそのまま再利用**する（メインキャンバスのタブ用に既にある。
  `ChartEditorRoot.uss:263-293`）。見た目の統一が無料で手に入る。
- `TabView` は使わない。メインキャンバス側で「content-container が確実に伸びる保証が
  コードから読めない」という理由で既に自前実装している（`ChartEditorApp.UI.cs:495`）ので、
  同じ理由・同じ作りに揃える。切替は `display: Flex/None` の付け替え。
- **上下の高さ配分**: 上（タブ側）を `flex-grow: 1`、インスペクタを
  `flex-shrink: 0` + `max-height: 45%` + 内部スクロール。インスペクタは選択内容で
  行数が大きく変わる（Slideの点／イベント種別）ため、固定高にはしない。
- UXMLは器の入れ子だけを持つ方針（ファイル冒頭のコメント）なので、
  `right-panel` の子を `right-tab-header` / `right-tab-body`（3つの子host）/
  `right-inspector` の3ブロックに書き換える。中身の流し込みは今までどおりコード側。

### 4.4 メニュー「表示」との関係

メニューバーの「表示」（`ChartEditorApp.UI.cs:232`）には既に
`高さレーン` / `イベントレーン` のトグルがある。右パネルの「表示」タブにも同じ項目があり
二重だが、**これは今回も残す**（片方はメニューからの素早い切替、もう片方は状態の一覧性）。
既存の相互同期（`viewHeightLane.SetValueWithoutNotify`）もそのまま。

右パネルのタブ切替をメニューへも足す（`表示 > 右パネル > 曲/表示/結果`）かは**やらない**。
タブは常に見えているので、メニュー経由の必要がない。

---

## §5 実装順序

依存関係が薄いので、影響範囲の小さい順に進める。

1. **§1 難易度の選択式化** — 独立。`AddChoiceRow` の追加だけ。
2. **§3 作業状態の記憶** — 独立。`EditorSettings` へのフィールド追加と読み書き。
3. **§4 右パネルの整理** — UXML/USS/`BuildRightPanel` の構造変更。§3 の
   `rightTabIndex` を使うので §3 の後。
4. **§2.4 フォント** — ユーザーの Unity Editor 操作が要る。コード側は USS 1行。
5. **§2.1 キャレット点滅** — `ImeBridge` の器を作り、まず点滅だけ入れる。
6. **§2.3 IME 診断モード** → 実機確認 → 結果に応じて本実装 or §2.6 の回避策。

1〜3 は `dotnet build` とスクラッチ検証で完結する。4〜6 は実機確認が前提。

---

## §6 実機で確認してもらう項目

1. 難易度がドロップダウンになり、変更→保存でファイル名が `<難易度>.muses` になること（§1）
2. ズーム倍率・スナップ・高さ/イベントレーンの表示が、アプリを終了→再起動しても
   直前の状態で開くこと（§3）
3. 設定モーダルに上記3項目が**出ていない**こと（§3の趣旨）
4. 右パネルがタブ3枚＋常設インスペクタになり、タブを切り替えても
   選択中ノーツの編集が続けられること（§4）
5. タブの選択状態が再起動後も保たれること（§3 + §4）
6. 日本語の表示が全体で同じフォントになること（§2.4）
7. 入力欄にフォーカスすると**キャレットが点滅**し、打鍵中は消えないこと（§2.1）
8. IME診断表示をONにして日本語を打ち、composition が飛ぶか／候補窓が
   キャレット位置へ来るか／確定文字が欄に入るかを報告してもらう（§2.3）

---

## §7 未決事項

- **§2.3 の最終形**は §6-8 の実機結果で決まる。ここで確定させない。
- インスペクタの高さ配分（45%）は仮値。実機で「タブ側が狭い」と感じたら
  境界のドラッグ変更を足す（今回はスコープ外）。
- Windows でエディタを動かす場合のフォント（§2.4 末尾）。macOS単独の間は不要。
