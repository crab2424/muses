# 譜面エディタ 設定画面の設計 ＋ 実機フィードバック4件（第5弾、2026-08-02 rev.1）

`memory/editor-ui-rework-r4.md` の §1〜§11 が実機確認済み（コミット `c5f484b` / `525bdeb`）になり、
残件だった **`editor-ui-redesign.md` §2.7「設定画面」** に着手する回。あわせてユーザーが実機で
気づいた UI 4 件を扱う。

**このドキュメントは実装計画。ユーザーの確認後に実装へ入る。**

- 現行実装: `unity/Assets/Scripts/ChartEditorApp/ChartEditorApp.cs`（2761行）、
  `ChartEditorApp.UI.cs`（1845行）、`PreviewSystem.cs`（481行）、`PreviewClock.cs`（133行）、
  `unity/Assets/UI/ChartEditor/ChartEditorRoot.uxml`（81行）/ `.uss`（493行）。
- Unity 6000.5.6f1。参照元は `memory/reference/MikuMikuWorld-master/`（C++ / Dear ImGui / OpenGL）。
- **今回は §5（ショートカット）が最大の変更**。入力経路の一本化とコマンドのデータ化を伴い、
  メニューバー・ツールバーの組み立て方にも波及する。

---

## 0. 参照元の調査結果（着手前に共有しておくべき事実）

ユーザーの依頼文にある「参考元のように、画面内をさらにジャンルごとにタブ分け」について、
**参照元の実際の設定画面はタブではない**ことが分かった。事実関係を先に整理しておく。

| # | 調べたこと | 参照元の実態 | 出典 |
|---|---|---|---|
| 1 | 設定画面の構造 | **モーダルダイアログ（550×500 固定）＋ 折りたたみヘッダ3つ**（Accent Color / Timeline / Video）。タブではない | `Dialogs.cpp:123-234` |
| 2 | 設定画面の中身 | アクセントカラー、Lane Width / Notes Height、VSync の**計4項目だけ** | 同上 |
| 3 | キーバインド設定 | **存在しない。**全ショートカットが `processInput()` に直書き（ツール切替の 1〜8 も `for (k = GLFW_KEY_1; ...)` でハードコード） | `Application.cpp:145-203`（特に `:186-188`） |
| 4 | オートセーブ | `autoSaveEnabled` / `autoSaveInterval`（分）と `autoSave()` が**宣言されているだけで呼び出し側が無い**（grep 済み、完全な死にコード） | `Application.h:32-33,43,87`、`Application.cpp:219-223` |
| 5 | VSync | 設定画面とメニュー「Window > VSync」の**両方**から触れる。実体は `glfwSwapInterval((int)vsync)` | `Dialogs.cpp:223-224`、`Application.cpp:279-280`、`OpenGlLoader.cpp:115` |
| 6 | 縦線（レーン区切り） | `drawLanes()` が `boldLane = !(l & 1)`（**2レーンごと**）で色を出し分ける。`NUM_LANES = 12`（muses の `Cells = 12` と同じ）。ただし **`thickness` を計算しておきながら `AddLine` には常に `secondaryLineThickness` を渡しており、太さの出し分けは効いていない（参照元のバグ）** | `ScoreEditor.cpp:552-563` |
| 7 | レーンの配置 | **レーン幅は固定px設定**（`timelineWidth = NUM_LANES * laneWidth`）で、**キャンバス中央に配置**（`laneOffset = canvasSize.x*0.5 - timelineWidth*0.5`）。余った左右がそのまま余白 | `EditorWindows.cpp:65-67`、設定側は `Dialogs.cpp:205-217` |
| 8 | ホイールスクロール | `timelineOffset += mouseWheelDelta * (Shift ? 200 : 50)`。**方向の反転設定は無い**。Shift で高速スクロールできる（muses には無い） | `EditorWindows.cpp:75-79` |
| 9 | 設定を開く場所 | メニュー「Edit > Settings」 | `Application.cpp:271-272` |
| 10 | 永続化 | `app_config.json` に JSON で書き出す。読み込みは `tryGetX(js, key, default)` で**欠けたキーは既定値**にフォールバック | `ApplicationConfiguration.cpp:72-146` |

**結論**:
- **タブ分けは muses 独自の改善**として進める（依頼どおり実装する）。「参照元と同じ」という根拠は無い、
  という点だけ明示しておく。項目数が参照元の4個に対して muses は20個以上になるので、
  折りたたみ3つよりタブのほうが妥当という判断自体は支持できる。
- **ショートカット設定タブは全面的に muses 独自設計**。参照元から取れるのは「既定のキー割り当て表」だけ。
- **オートセーブは参照元を参考にできない**（死にコード）。muses 側の既存実装（`TickAutosave`）を拡張する。
- **§8（中央揃え）はむしろ参照元の既定挙動**。muses が独自に「余りを全部レーンへ配る」方式を採っている。

---

## 1. 設定の永続化層（新規 `EditorSettings.cs`）

### 1.1 現状

エディタが永続化しているのは `browseDir`（`PlayerPrefs("ChartEditor_LastDir")`、
`ChartEditorApp.cs:324`・`:445`・`UI.cs:1771`）だけ。他はすべてフィールドの初期値。

`OffsetSettings.cs`（`Stage/`）も PlayerPrefs を使っているが、あちらは
**ゲーム本体のプレイヤー設定（judgeOffsetMs / visualOffsetMs）**で、エディタ設定とは別物。混ぜない。

### 1.2 方針: JSON ファイル

`Application.persistentDataPath/editor-settings.json` に `JsonUtility` で読み書きする。

**PlayerPrefs ではなく JSON ファイルにする理由**:
- キーバインドは「1コマンドに複数のキー組み合わせ」＝入れ子の配列。PlayerPrefs は string/int/float しか
  持てないので、結局 JSON 文字列を1キーに詰めることになり**ファイルにするのと実質同じ**。
  それならファイルのほうが素直。
- 参照元も JSON ファイル（§0-10）。
- ユーザーが中身を見て手で直せる。macOS の PlayerPrefs は
  `~/Library/Preferences/unity.DefaultCompany.muses.plist` で実質不可視。
- 設定が壊れたときにファイルを消すだけで初期化できる。

**`JsonUtility` の制約と対処**:
- `Dictionary` は扱えない → キーバインドは `List<KeyBinding>` で持つ。
- enum は int にシリアライズされる → **コマンドIDは enum ではなく文字列**で保存する
  （enum に項目を挿入すると既存の設定ファイルの割り当てが全部ずれるため）。
- 欠けたフィールドは補われない → **既定値入りのインスタンスを作ってから `JsonUtility.FromJsonOverwrite`**
  を掛ける。これで未知バージョンのファイルでも欠けた項目は既定のまま残る（参照元の
  `tryGetX(js, key, def)` と同じ狙いを、Unity の API で実現する形）。

```csharp
[Serializable]
public class EditorSettings
{
    public int version = 1;

    // 一般
    public bool  autosaveEnabled  = true;
    public int   autosaveMinutes  = 5;
    public int   frameRateMode    = 0;   // 0=VSync, 1=60, 2=120, 3=無制限

    // タイムライン
    public bool  followPlayback   = true;
    public bool  pageScroll       = false;
    public float judgeLineFrac    = 1f;
    public int   laneDivisions    = 4;     // §4.2
    public bool  invertScroll     = false; // §4.3
    public float laneWidthPx      = 46f;   // §8（案Bを採る場合）

    // ショートカット
    public List<KeyBinding> keyBindings = new();
}
```

**保存タイミング**: 設定モーダルを閉じたとき ＋ `OnDestroy`。
**反映タイミング**: 値を変えた瞬間に即時（プレビューしながら調整できるように）。
参照元も「値は即時反映、ファイル書き出しは終了時（`Application.cpp:510`）」で同じ。

`browseDir` もこちらへ移すかは §11 Q1 で確認したい（移すのが自然だが、既存の PlayerPrefs 経路を
消すと前回のフォルダが1回だけ忘れられる）。

---

## 2. 設定画面のシェル（モーダル ＋ 横タブ）

### 2.1 出し方

**モーダル**にする。既存の `ShowModal(title)`（`ChartEditorApp.UI.cs:1571-1583`）と
`.modal-scrim` / `.modal`（`.uss:444-467`）をそのまま流用できる。

「タイムライン / 譜面プレビュー」と並ぶ3つ目のメインタブにする案は採らない。
あのタブ列は**作業対象**（何を編集しているか）の切り替えで、設定は種類が違う。参照元もモーダル。

**開く場所**: メニュー「ツール > 設定...」。参照元は Edit 配下だが、muses は既に「ツール」メニューに
検証系（譜面を検証 / 保存時に自動検証）を置いているのでそちらに寄せる。

### 2.2 内側のタブ

3つだけなので**上に横タブ**。既存の `.tab-header` / `.tab-header-btn` / `.tab-header-btn--selected`
（`.uss:207-237`）を**そのまま再利用**でき、新規 USS がほぼ要らない
（メインタブと同じ実装＝`Button` ＋ `display` 切替。`BuildTabs` の作りをそのまま小さくしたもの）。

```
┌ 設定 ────────────────────────────────┐
│ ⟨一般⟩ ⟨タイムライン⟩ ⟨ショートカットキー⟩          │
├──────────────────────────────────────┤
│  (選択中タブの中身。prop-row の縦積み)              │
│                                                     │
├──────────────────────────────────────┤
│                     [既定に戻す]   [閉じる]         │
└──────────────────────────────────────┘
```

- サイズは `min-width: 560px; height: 460px`（参照元の 550×500 に相当）。
  ショートカットタブは項目が多いので中身は `ScrollView`。
- 「既定に戻す」は**表示中のタブのぶんだけ**戻す（全部消えると事故なので）。

---

## 3. 一般タブ

| 行 | コントロール | 既定 | 反映先 |
|---|---|---|---|
| 自動保存 | Toggle | ON | `TickAutosave` の早期 return |
| 自動保存の間隔（分） | IntegerField（1〜60でクランプ） | 5 | `AutosaveIntervalSec` |
| フレームレート制限 | DropdownField | VSync | `QualitySettings` / `Application` |
| エディタ画面倍率 | Slider（0.75〜2.0、0.05刻み） | 1.0 | `PanelSettings.referenceResolution`（§3.4、**Q2 で今回入れると確定**） |

### 3.1 オートセーブ

現状（`ChartEditorApp.cs:74-75, 570-583`）:

```csharp
private const float AutosaveIntervalSec = 5f * 60f;   // ← const。設定不可
...
if (!dirty || string.IsNullOrEmpty(chartPath)) return;  // ← 未保存の新規譜面は対象外
```

- `const` を捨てて `settings.autosaveMinutes * 60f` を見るようにする。
- **`chartPath` が空（＝一度も保存していない新規譜面）だと自動保存が一切走らない**という穴が
  ついでに見つかった。参照元も `autoSave()` が `editor->save()` を呼ぶだけなので同じ構造（かつ死にコード）。
  → `persistentDataPath/untitled.muses.autosave` へ書く案を提案するが、今回入れるかは §11 Q4 で確認したい。
- 間隔を短くしたときに即座に効くよう、`lastAutosaveRealtime` は据え置きでよい
  （次の判定から新しい間隔で比較される）。

### 3.2 VSync / フレームレート

ユーザーの「vsync（120fps個別でも可）」に対し、**1つのドロップダウンで両方を表現**する:

| 選択肢 | 実装 |
|---|---|
| VSync（画面のリフレッシュレートに同期） | `QualitySettings.vSyncCount = 1; Application.targetFrameRate = -1;` |
| 60 fps | `vSyncCount = 0; targetFrameRate = 60;` |
| 120 fps | `vSyncCount = 0; targetFrameRate = 120;` |
| 無制限 | `vSyncCount = 0; targetFrameRate = -1;` |

**なぜ両方を触るか**: `vSyncCount != 0` のとき `targetFrameRate` は無視される（プラットフォーム依存）。
排他に設定しないと「120 を選んだのに VSync が効いたまま」になる。

**既定は VSync**。[[muses-unity-port-progress]] の「Play 中の発熱」の記録どおり、上限なしで回すと
実害（約1300fps → 発熱）が出ることが実測で分かっている。
`ProjectSettings/QualitySettings.asset` は PC レベルが `vSyncCount: 1` だが、**実行時に上書きするので
設定値が常に勝つ**（＝ビルドのクオリティレベルに依存しなくなる）。

### 3.3 テーマ・装飾

ユーザー指示どおり**今回は見送り**（参照元の Accent Color 相当）。
色は既に `.uss` 冒頭のカスタムプロパティ（`--accent` / `--bg-*` / `--note-*` / `--event-*`）に
集約済みなので、後から「アクセント色」1項目を足すだけで済む状態にはなっている。

### 3.4 エディタ画面倍率（Q2 で今回入れると確定）

`editor-ui-redesign.md` §2.7 の項目。ユーザーの初回フィードバック
「メニューバーが小さすぎる／変更できない」（同 §3 の指摘3）への回答でもある。

`PanelSettings.asset` は現在 `m_ScaleMode: 2`（= `ScaleWithScreenSize`）、
`m_ReferenceResolution: {x:1600, y:900}`、`m_ScreenMatchMode: 0`（MatchWidthOrHeight）、`m_Match: 0`（幅基準）。

- `PanelSettings.scale` は **`ConstantPixelSize` のときしか効かない**ので、今の設定では使えない。
- **採用: `PanelSettings.referenceResolution` を `(1600, 900) / uiScale` に書き換える方式。**
  参照解像度を小さくすると UI が大きくなる。全体が確実に等倍でスケールする。
- 不採用: `.root` の `--font-*` / `--band-h` を倍率で書き換える方式（`.uss` 冒頭のコメントが
  もともと想定していたもの）。USS 変数を参照していない直書き箇所（`style.width = 54` 等）が
  置いていかれるため、倍率を上げるほど崩れが増える。
  なお §6 でこの直書きを USS へ寄せるが、全部は追い切れない前提で考える。

**注意点（実装時に効いてくる）**:
- 倍率を変えると `notesSheet.contentRect` の**論理サイズが変わる**（＝1論理pxあたりの実pxが変わる）。
  `SheetLayout` は `contentRect` を素で使っているので、レーン幅を固定pxにする §8（案B）の
  「固定px」は**論理px基準**になる。倍率を上げるとレーンは画面上で大きくなる ＝ 期待どおりの挙動。
- `PanelSettings` はアセットなので、実行時に書き換えると**エディタ上ではアセットが汚れる**。
  起動時に元の値を控えて `OnDestroy` で戻すか、`PanelSettings` を `Instantiate` して
  `UIDocument.panelSettings` に差し替える。**後者が安全**（アセットに一切触らない）。

---

## 4. タイムラインタブ

### 4.1 現行「表示設定」からの移設

現在の右パネル「表示設定」（`ChartEditorApp.UI.cs:648-673`）の中身と、移設するかどうか:

| 現在の項目 | 移設 | 理由 |
|---|---|---|
| 再生に追従 | **設定へ** | 一度決めたら変えない設定 |
| ページ送りスクロール | **設定へ** | 同上 |
| 判定線位置 | **設定へ** | 同上 |
| 再生速度 | **残す** | 設定ではなく再生パラメータ（曲ごと・場面ごとに触る）。→ §11 Q3 |
| 高さレーン | **残す** | ユーザー明示（「高さ，イベントレーン除く」） |
| イベントレーン | **残す** | 同上 |
| 小節へ移動 | **残す** | 設定ではなく操作。→ §11 Q3 |

移設後も「表示」メニュー（`UI.cs:207-229`）のトグルは残す
（参照元も VSync をメニューと設定の両方に置いている、§0-5）。
`viewFollow` 等のフィールドは設定モーダル側の Toggle を指すようになるが、
メニュー側の `SetValueWithoutNotify` 呼び出しは **モーダルが閉じている間 null になる**ので
null チェックが要る（現状も `if (viewFollow != null)` と書いてあるのでそのまま動く）。

移設後の「表示設定」セクションは項目が3つに減るので、**セクション名を「表示」に変え、
末尾に「設定を開く...」ボタン**を置くのがよい（設定画面への導線）。

### 4.2 縦線の分割（新規）

**現状**（`ChartEditorApp.cs:947-952`）:

```csharp
for (int c = 0; c <= Cells; c++)   // Cells = 12
{
    FillRect(p, new Rect(SheetLayout.CellX(L.ground, c), rect.y, 1, rect.height), new Color(1,1,1,0.08f));
    FillRect(p, new Rect(SheetLayout.CellX(L.sky,    c), rect.y, 1, rect.height), new Color(1,1,1,0.08f));
}
```

13本すべてが `alpha 0.08` の 1px。**どこが何セル目か数えないと分からない**のがユーザー指摘の中身。

**変更後**: 設定 `laneDivisions`（N）に応じて、`c % (Cells / N) == 0` の位置だけを強調する。

| 線 | 太さ | 色 |
|---|---|---|
| ペイン外周（c = 0, c = Cells） | 2px | `rgba(1,1,1,0.30)` |
| 分割位置（c が `Cells/N` の倍数） | 2px | `rgba(1,1,1,0.18)` |
| それ以外のセル境界 | 1px | `rgba(1,1,1,0.08)`（現状のまま） |

- **N は 12 の約数に限定**（1 / 2 / 3 / 4 / 6 / 12）→ DropdownField。
  非約数を許すと強調線がセル境界に乗らず、「ノーツを置ける位置」と食い違う線が出てしまう。
- **既定は 4**（3セルごと）。参照元は 2レーンごと＝6等分（§0-6）だが、12セルで6本の強調線は
  細かすぎる。4等分なら Ground/Sky それぞれが 3セル×4ブロックで数えやすい。
- 参照元は太さの出し分けが**バグで効いていない**（§0-6）ので、「少し太くする」は muses 独自。
  Painter2D は塗りつぶし矩形しか使っていない（`FillRect`）ので、太さは Rect の幅を変えるだけ。
- 高さレーンの目盛り（`:940-942`、0 / 0.5 / 1）は**この設定の対象外**（層は 0/1 の2値なので分割の概念が無い）。

### 4.3 スクロール方向の反転（新規）

**現状**（`ChartEditorApp.cs:2139-2154`）:

```csharp
sheetScrollAccum += evt.delta.y;
...
if (steps != 0) scrollTick = Mathf.Max(0, scrollTick + steps * SnapTicks);
```

`invertScroll` が true なら `evt.delta.y` の符号を反転するだけ（1行）。

- **Ctrl+ホイールのズーム方向（`:2143`）は別扱い**にする。スクロール方向の好みとズーム方向の好みは
  独立（macOS のナチュラルスクロールでも、ズームは「上で拡大」を好む人が多い）。→ §11 Q5。
- **おまけ候補**: 参照元の `Shift+ホイール = 高速スクロール`（`EditorWindows.cpp:79`、通常50px に対し 200px）。
  muses には無い。設定不要な純粋な追加なのでついでに入れたい（4倍＝`steps * 4`）。

### 4.4 レーン幅（§8 で案Bを採る場合のみ）

参照元の `Lane Width`（`Dialogs.cpp:209`、MIN/MAX でクランプしたスライダー）と同じ項目を置く。
セル1つの横幅を px で指定する。詳細は §8。

---

## 5. ショートカットキータブ

今回の最大の変更。**キーの割り当てを変えられるようにする前に、そもそも入力経路が2つに割れている
のを直す必要がある。**

### 5.1 現状の調査結果

ショートカットは**2系統**に分かれている。

| 系統 | 場所 | 仕組み | 割り当て |
|---|---|---|---|
| A | `HandleUndoRedoShortcuts()`（`ChartEditorApp.cs:386-402`） | `Update()` で `UnityEngine.InputSystem.Keyboard.current` をポーリング | Cmd/Ctrl+Z、Cmd/Ctrl+Shift+Z、Cmd/Ctrl+Y |
| B | `OnSheetKeyDown()`（`:2156-2215`） | UI Toolkit の `KeyDownEvent`。**`notesSheet` に登録**（`UI.cs:391`） | Cmd/Ctrl+C / X / V、Escape、↑↓、Delete / Backspace |

**この分裂から2つの不具合が出ている**:

1. **系統Aはフォーカスを見ない。**右パネルのテキスト欄を編集中に Cmd+Z を押すと、
   テキストの取り消しではなく**譜面の Undo が走る**（打ち間違いを戻そうとして直前のノーツ配置が消える）。
   r4 §7 で「入力欄がやっと見えるようになった」ばかりなので、これから顕在化するはず。
2. **系統Bは `notesSheet` にフォーカスが無いと届かない。**右パネルを触った直後は Delete も
   コピペも効かない。`NavigationMoveEvent` を潰す対処（`UI.cs:396-400`、r2 §5）は
   この構造から来た対症療法だった。

さらに、**ツール切替のショートカットが無い**（参照元は 1〜8、`Application.cpp:186-188`）。
ユーザー要望「タイムラインのノーツや選択モードなど全て（参照元は数字キー）」はここ。

### 5.2 方針: コマンドのデータ化 ＋ ディスパッチの一本化

#### (1) コマンドテーブル

「エディタができること」を1本のテーブルに集約する。

```csharp
private readonly struct EditorCommand
{
    public readonly string   id;        // 保存キー。文字列（enum の順序変更に強い）
    public readonly string   category;  // 設定画面のグループ見出し
    public readonly string   label;     // メニュー・設定画面の表示名
    public readonly Action   run;
    public readonly Func<bool> enabled; // null なら常時有効
}
```

**メニューバー・ツールバー・ショートカットの3つが同じテーブルを引く**ようにする。
今はメニュー項目が `BuildMenuBar()`（`UI.cs:152-265`）にベタ書きで、活性判定
（`if (selection.Count > 0) menu.AddItem(...) else menu.AddDisabledItem(...)`）も各所に重複している。
テーブル化するとここが1箇所にまとまり、**メニューにショートカット表記を出せる**ようになる
（参照元も `MenuItem("Save", "Ctrl + S")` と併記している）。

コマンド候補（カテゴリ = 設定画面の見出し）:

| カテゴリ | コマンド |
|---|---|
| ファイル | 新規 / 開く / 保存 / 別名で保存 |
| 編集 | 元に戻す / やり直す / 切り取り / コピー / 貼り付け / **反転して貼り付け** / 削除 / **すべて選択** / 選択を反転 / ペースト中止 |
| ツール | 選択 / Tap / Ex Tap / Slide / Flick / 中継点 / 削除 / イベント（＝`ToolButtons` の8個） |
| 再生 | 再生・一時停止 / 停止（カーソルへ）/ 先頭へ / 末尾へ / オートプレイ / ノーツSE / メトロノーム |
| カーソル・表示 | カーソルを上へ / 下へ / 拡大 / 縮小 / スナップを細かく / 粗く / 高さレーン / イベントレーン / タブ切替 |
| ツール（検証） | 譜面を検証 |

**「すべて選択」は muses に未実装**（参照元は `Ctrl+A`、`Application.cpp:178-179`）。
コマンド表に載せるついでに実装する（`SetMultiSelection(AllPointRefsForNotes(chart.notes))` で足りる）。

#### (2) キーの表現

```csharp
[Serializable] public struct KeyChord
{
    public KeyCode key;
    public bool primary;   // macOS: Cmd / それ以外: Ctrl
    public bool shift;
    public bool alt;
}

[Serializable] public class KeyBinding
{
    public string commandId;
    public List<KeyChord> chords = new();   // ユーザー要望「複数登録できると良い」
}
```

**Cmd と Ctrl を分けず「主修飾キー（primary）」1つに畳む**ことを推奨する。
既存コードが既に `evt.commandKey || evt.ctrlKey`（`:2159`）と同一視しており、分けると
「macOS では Cmd+Z、Windows では Ctrl+Z」を2レコードで持つ羽目になる。→ §11 Q6。

#### (3) ディスパッチ

- `KeyDownEvent` を **`uiRoot` に `TrickleDown` で登録**して一本化する
  （`notesSheet` だけでなく画面全体で効く）。系統A（InputSystem ポーリング）は**廃止**。
- **入力欄にフォーカスがある間は横取りしない**:
  `uiRoot.panel.focusController.focusedElement` が `TextField` / `IntegerField` / `FloatField` の
  内部要素なら、
  - 修飾キー無しの単キー（1〜8 / Space / Delete）は**無視**、
  - `primary+C/V/X/A/Z` も**無視**（入力欄自身のコピペ・取り消しに任せる）、
  - それ以外（例 `primary+S`）は通す。
  → これで §5.1 の不具合1が直る。
- `notesSheet` 側の `OnSheetKeyDown` は**シート固有の操作だけ**を残す（今のところ無し）か、
  全部 `uiRoot` 側へ移す。`NavigationMoveEvent` の潰し（r2 §5）は ↑↓ を `uiRoot` で
  受けるようになっても必要なので残す。

**実機で確認が要る点**: ランタイムパネルでフォーカスが何も無いとき（起動直後など）に
`uiRoot` へ `KeyDownEvent` が届くか。`uiRoot.focusable = true` にして起動時に `Focus()` する
対処を入れる想定だが、**UI Toolkit の前提は実機で確認するまで信用しない**
（TabView・IMGUIContainer で2度踏んだ教訓）。届かなければ InputSystem 側に一本化する
（そちらでも `focusController.focusedElement` を見れば同じ切り分けができる）。

### 5.3 既定のキーバインド

参照元（`Application.cpp:145-202`）を土台に、muses の現状の割り当てを維持する。

| コマンド | 既定 | 出典・備考 |
|---|---|---|
| 新規 / 開く / 保存 / 別名で保存 | `⌘N` / `⌘O` / `⌘S` / `⌘⇧S` | 参照元（muses は未割り当て → 新規） |
| 元に戻す | `⌘Z` | 現状 |
| やり直す | `⌘⇧Z`, `⌘Y` | 現状。**複数登録の実例** |
| 切り取り / コピー / 貼り付け | `⌘X` / `⌘C` / `⌘V` | 現状 |
| 反転して貼り付け | `⌘⇧V` | 参照元（`flipPaste`、`:167-168`） |
| すべて選択 | `⌘A` | 参照元。**muses は機能自体が未実装** |
| 選択を反転 | `⌘F` | 参照元（`:176-177`） |
| 削除 | `Delete`, `Backspace` | 現状 |
| ペースト中止 | `Escape` | 現状 |
| ツール切替（8個） | `1`〜`8` | 参照元（`:186-188`）。muses のツール順 = `ToolButtons` の並び |
| 再生・一時停止 | `Space` | 参照元（`:194-195`） |
| 停止 | `⇧Space` | 参照元は `Backspace` だが **muses では削除と衝突する**ので変更 |
| カーソル上 / 下 | `↑` / `↓` | 現状。参照元は `KEY_DOWN → previousTick` で**向きが逆**だが、muses は tick が増える向き＝画面上なので現状のほうが直感的。変更しない |
| 拡大 / 縮小 | `⌘+` / `⌘-` | 新規 |
| スナップを細かく / 粗く | `]` / `[` | 新規 |

### 5.4 設定UI

```
┌ ショートカットキー ──────────────────┐
│ ▾ 編集                                                │
│   元に戻す        [⌘Z]                      [＋]      │
│   やり直す        [⌘⇧Z ×] [⌘Y ×]           [＋]      │
│   すべて選択      [⌘A ×]                    [＋]      │
│ ▾ ツール                                              │
│   Tap             [2 ×]                     [＋]      │
│   ...                                                 │
└───────────────────────────────────┘
```

- `[＋]` を押すと「キーを押してください…」状態になり、次の `KeyDownEvent` を1つ捕まえて追加する
  （`Escape` で中止）。既存チップの `×` で削除。
- **競合検出**: 追加しようとした chord が別コマンドに既にあれば、
  **後勝ち（前の割り当てを自動で外す）＋ どこから外したかを表示**する。
  黙って二重登録させると「押しても片方しか動かない」原因不明の不具合になる。→ §11 Q7。
- 各行に「既定に戻す」、タブ下部に「全部を既定に戻す」。

---

## 6. UI指摘: テキスト入力欄「幅」が直っていない

### 6.1 対象（Q8 で確定）

**ツールバーの「幅」FloatField**（`ChartEditorApp.UI.cs:317-323`）。

```csharp
var widthField = new FloatField { value = defaultWidthCells };
widthField.style.width = 54;
```

r4 §7 続報でユーザーが挙げた「`入力枠が被っていて崩れている箇所もある`（次回対応の別件）」が
これに当たる。あのとき直したのは `.prop-row`（右パネル）だけで、**バンド側は手つかずだった**。

### 6.2 原因と修正

r4 §7 続報で特定した原因と**同型**。**§7 の「拡大倍率・シークバーが縦にずれる」も同じ原因**で、
Q9 の回答（「縦位置がずれる」）がその裏付けになった — ツールバーとステータスバーは
同じ `.band` を使っており、症状が両方で同時に出ているのはこの共通クラスが原因であることを示す。

> `.prop-row { height: 20px }` が固定値だったため、ランタイム既定テーマのコントロールの
> 実際に必要な高さに足りず、はみ出した分が次の行の不透明な背景に隠れていた。

ツールバーも `.toolbar { height: var(--band-h) }` = **30px 固定**（`.uss:116-118`、`.band` は
`align-items: center`）。コントロールの実測高が 30px を超えると上下にはみ出し、
上のメニューバー（`height: 24px`）や下のタブ見出しに被る。

**修正**: `.band` 系の `height` を **`min-height` に変える**（r4 §7 続報の "How to apply" の適用）。

```css
.toolbar   { min-height: var(--band-h); }
.status-bar{ min-height: var(--band-h); }
.menu-bar  { min-height: 24px; }
```

あわせて `widthField` の `style.width = 54` の直書きを USS クラス（`.tb-field`）へ移す
（「見た目に関する指定は USS に寄せる」という `.uss` 冒頭の既存方針）。

---

## 7. UI指摘: 拡大倍率・シークバーがずれている

ステータスバーの `status-zoom`（`−` / スライダー / `+` / `1.00x`）と `status-scrub`（シークバー）。

**Q9 の回答は「縦位置がずれる」＝ §6 と同一原因**（`.status-bar { height: var(--band-h) }` 固定）。
`.band` の `min-height` 化で解消するはず。**§6 と §7 は1つの修正でまとめて片づく。**

念のため、同時に入れておく2点（どちらも独立・害なし。r4 §7 で「色だけ直して1往復増やした」教訓の適用）:

| # | 内容 | 修正 |
|---|---|---|
| (b) | ランタイム既定テーマの Slider は `#unity-dragger` を絶対配置しており、要素高が想定と違うとつまみが溝の中心からずれる。バンド高が変わる以上、道連れで動く可能性がある | `.zoom-slider` / `.status-scrub > .unity-slider` に `height` を明示して揃える |
| (c) | `zoomLabel.text = $"{pxPerBeat / 28f:0.00}x"`（`UI.cs:1361`）の `28f` はマジックナンバー（`pxPerBeat` の初期値、`ChartEditorApp.cs:95`）。**さらに `OnSheetWheel`（`:2143`）がクランプ値 `8f, 240f` を直書きで重複させており `SetZoom` を通っていない** | 基準値と範囲を名前付き定数（`ZoomBasePxPerBeat` / `ZoomMin` / `ZoomMax`）に切り出し、`SetZoom`・`OnSheetWheel`・スライダー生成（`UI.cs:1264`）の3箇所で共有する |

---

## 8. UI指摘: 高さ／イベントレーンを畳んだときも右に余白を残す（中央揃え）

### 8.1 現状

`SheetLayout` のコンストラクタ（`ChartEditorApp.cs:705-725`）:

```csharp
leftMargin  = new Rect(rect.x, rect.y, marginLeft, rect.height);          // 常に 44px
rightMargin = new Rect(rect.xMax - marginRight, ...);                     // 畳むと 0px
heightLane  = new Rect(rightMargin.xMin - heightLaneW, ...);              // 畳むと 0px
float lanesW = Mathf.Max(0f, heightLane.xMin - lanesX - gutterW);         // 余りを全部レーンへ
```

畳むとその幅がそのまま Ground/Sky に配られるので、**トグルするたびにセル幅とノーツの
見かけの位置が変わる**。これがユーザー指摘の本質。

参照元は逆で、**レーン幅は固定px・レーン群はキャンバス中央**（§0-7）。

### 8.2 案（Q10 で **案B に確定**）

| | 案A（最小、不採用） | **案B（参照元方式、採用）** |
|---|---|---|
| やること | 畳んだぶんの幅を**空白として予約**する。`SheetLayout` に渡すのは常に `sheetMarginRight` / `heightLaneWidth` にし、「中身を描くか」だけを別フラグで切る | セル幅を**固定px設定**にし、レーン群（左余白＋Ground＋ガター＋Sky）をキャンバス**中央**へ置く。左右の余りが余白になる |
| 中央揃えになるか | **ならない**（左44px / 右204px で非対称のまま） | **なる**（ユーザー要求そのもの） |
| ウィンドウを広げたとき | セルが間延びする（現状どおり） | セル幅は一定で余白が増える |
| 変更量 | `CurrentSheetLayout()` と描画の分岐、数行 | `SheetLayout` のコンストラクタ（Rect の決め方）。`CellX` / `PaneAt` / `NoteX` / `LayerToX` は**すべて Rect ベースなので中身は無変更** |
| 追加で要るもの | なし | 設定「レーン幅」（§4.4）。ウィンドウが狭くて収まらないときは従来どおり伸縮に落とすフォールバック |

### 8.3 案B の設計

新しい帯の決め方（`SheetLayout` のコンストラクタを置き換える）:

```
[ 左の余り ][ 小節番号 44px ][ Ground 12*w ][ ガター 26px ][ Sky 12*w ][ 高さ 100px ][ イベント 104px ][ 右の余り ]
                            └────────── contentW（固定） ──────────┘
```

- `contentW = sheetMarginLeft + Cells*laneWidthPx*2 + gutterW + heightLaneWidth + sheetMarginRight`
  を**常に**（畳んでいても）確保する。
- `offsetX = (rect.width - contentW) * 0.5f` で中央へ寄せる。左右の余りが余白になる。
- **`showHeightLane` / `showEventLane` は「幅を0にする」のをやめ、「中身を描くかどうか」だけを切る。**
  → 畳んでもレーンの位置が1pxも動かない（ユーザー指摘への直接の回答）。
  → `SheetLayout.heightLane` / `rightMargin` は常に実寸を持つので、
    `UpdateEventChips`（`UI.cs:502`）や `UpdateSheetLabels`（`:438,442`）が使っている
    **`width > 0` による非表示判定は成立しなくなる**。`showEventLane` / `showHeightLane` を
    直接見るように書き換える（r4 §5 で入れた早期 return の条件だけ差し替え）。
  → 同じ理由で `L.rightMargin.Contains(pos)`（`ChartEditorApp.cs:1406` 付近）も
    畳んだ状態で true になってしまう。**イベントレーンのクリック処理は `showEventLane` を
    前提条件に加える**（r4 §6 でツール限定にしたガードの隣に足す）。高さレーンも同様。
- **フォールバック**: `rect.width < contentW` なら `offsetX = 0` にし、レーン幅を
  収まるところまで縮める（従来どおりの伸縮に落とす）。ウィンドウを狭くしても破綻させない。
- `laneWidthPx` の既定は **46px**（現状ウィンドウ幅1290pxでの実測セル幅 46.5px に合わせる＝
  既定では見た目が変わらない）。設定範囲は 20〜100px。

**追随が要る箇所**:
- 小節番号（`UpdateSheetLabels`、`UI.cs:463`）は `L.leftMargin.x + 4f` に置いている。
  中央揃えにすると左余白がウィンドウ幅次第で広がるので、**右詰め**（レーンの左端に寄せる）に変える。
- 小節線の左延長（`ChartEditorApp.cs:974-975`）も `L.leftMargin.x` 起点なので同様。
- 背景の塗り（`:924-927`）は `L.rect` 全面 → 左右の余りも同じ色になる。
  余白部分は**キャンバス色（`--bg-canvas` 相当の暗い色）**に落として、レーン領域の範囲を
  視覚的に示す（`editor-ui-redesign.md` §2.4「判定領域の範囲が視覚的に不明瞭」への回答も兼ねる）。

---

## 9. UI指摘: メニューバーをホバーで切り替えたい

### 9.1 現状と、なぜ今の作りでは無理か

`AddMenu`（`ChartEditorApp.UI.cs:272-283`）は標準の `GenericDropdownMenu` を使っている。

```csharp
btn.clicked += () => {
    var menu = new GenericDropdownMenu();
    build(menu);
    menu.DropDown(btn.worldBound, btn, DropdownMenuSizeMode.Auto);
};
```

`GenericDropdownMenu.DropDown` は**パネルのルート直下**に `.unity-base-dropdown` の
コンテナを追加する（Unity 同梱の `RuntimeDebugWindow.uss:42` が
`PanelRootElement > .unity-base-dropdown` というセレクタで上書きしていることから確認できる）。
このコンテナは外側クリックで閉じるために**画面全体を覆う**ので、
**その下にあるメニューバーのボタンには `PointerEnterEvent` が届かない**。
加えて公開 API に「開いているメニューを閉じる」手段が無い。

→ **ホバー切替は `GenericDropdownMenu` のままでは実装できない。自前のドロップダウンに置き換える。**

### 9.2 方針

既存のモーダル基盤と同じ `overlayLayer` に、`position: absolute` のポップアップを1つ出す方式。

- 状態は `openMenuIndex`（-1 = 閉じている）1つ。
- 各メニューボタンに `PointerEnterEvent` を登録し、**`openMenuIndex >= 0` のときだけ**開き直す
  （閉じているときのホバーでは開かない。これが「一度クリックしたら」というユーザー要望の意味）。
- 閉じる: 項目クリック / 同じボタンの再クリック / 透明スクリムのクリック / `Escape`。
- **`GenericDropdownMenu` と同じ形（`AddItem(label, isChecked, action)` / `AddDisabledItem` /
  `AddSeparator`）のシムを作れば、`BuildMenuBar()` の本体（`UI.cs:157-264`）は書き換えずに済む。**
  ただし §5 でコマンドテーブル化するなら、**そのときに一緒に組み直すほうが手戻りが無い**
  （ショートカット表記を項目に併記する = `AddItem(label, shortcut, ...)` にシグネチャが変わるため）。

→ **§5 と同じ増分で実装する**（§10 の順序に反映）。

---

## 10. 実装順

独立性と「壊れたときの切り分けやすさ」で並べる。

1. **§6 / §7** バンド高さ・スライダー・ズーム定数（USS ＋ 小さなリファクタ、完全に独立）
2. **§8** レーンの中央揃え（`SheetLayout` に閉じる）
3. **§1** `EditorSettings`（永続化層。器だけ作って既存の値を通す）
4. **§2** 設定モーダル ＋ 横タブ（空のタブ3つ）
5. **§3** 一般タブ（オートセーブ / フレームレート）
6. **§4** タイムラインタブ（移設 ＋ 縦線分割 ＋ スクロール反転）
7. **§5** コマンドテーブル化 → キーバインド → ショートカットタブ（最大）
8. **§9** 自前メニュー ＋ ホバー切替（§5 のコマンドテーブルに乗せる）

1〜2 は今回のフィードバックへの直接の回答なので先に出す。3〜6 で設定の器が完成し、
7〜8 が一番大きい塊。**7 の途中で切るなら「コマンドテーブル化まで（既定バインドは固定）」で
一度動く状態を作れる**ので、そこを中間コミットの候補にする。

**順序上の依存**:
- **§3.4（画面倍率）は §8 の後**にやる。倍率を変えると `contentRect` の論理サイズが変わるので、
  §8 のフォールバック（狭いときに伸縮へ落とす）が先に入っていないと、倍率を上げた瞬間に
  レーンがはみ出して原因の切り分けが難しくなる。順序 5 の中で最後に回す。
- **§9 は §5 の後**。コマンドテーブルができてからでないと、メニュー項目のシグネチャを2回変えることになる。

---

## 11. 確認事項

### 11.1 確定済み（2026-08-02、着手前にユーザー確認）

| # | 節 | 決定 |
|---|---|---|
| Q2 | §3.4 | 「エディタ画面倍率」は**今回入れる**（一般タブ、`PanelSettings.referenceResolution` 方式） |
| Q8 | §6 | 「テキストの入力欄，幅の部分」は**ツールバーの「幅」入力欄**のこと |
| Q9 | §7 | 「拡大倍率・シークバーがずれている」は**縦位置のずれ** → §6 と同一原因（`.band` の固定 `height`）と確定。修正は1つで足りる |
| Q10 | §8 | レーン配置は**案B（レーン幅を固定px設定にして中央配置、参照元方式）** |

### 11.2 未確認（推奨があるので、異論が無ければそのまま進める）

| # | 節 | 質問 | 推奨 |
|---|---|---|---|
| Q1 | §1 | 設定の永続化は JSON ファイル（`persistentDataPath/editor-settings.json`）でよいか。`browseDir`（現在 PlayerPrefs）もそちらへ移すか | JSON。`browseDir` も移す |
| Q3 | §4.1 | 「再生速度」「小節へ移動」は右パネルに残す解釈でよいか | 残す |
| Q4 | §3.1 | 未保存の新規譜面が自動保存の対象外になっている穴も今回直すか | 直す |
| Q5 | §4.3 | Ctrl+ホイールのズーム方向もスクロール反転に連動させるか、別設定にするか | 別（今回は反転しない） |
| Q6 | §5.2 | Cmd と Ctrl を「主修飾キー」1つに畳んでよいか | 畳む |
| Q7 | §5.4 | キーが競合したときは「後勝ち（前を自動で外す）」でよいか | 後勝ち |

### 11.3 実装後の実機確認で特に見てほしい点

- **§5**: 入力欄を編集中に `Cmd+Z` を押して、**譜面の Undo ではなくテキストの取り消しになる**こと。
  また `notesSheet` にフォーカスが無い状態（右パネルを触った直後など）でも Delete / コピペが効くこと。
- **§5**: 起動直後（どこもクリックしていない状態）でショートカットが効くこと
  （ランタイムパネルのフォーカス挙動は実機で確認するまで信用しない）。
- **§6 / §7**: ツールバーの「幅」欄が正しく見え、ステータスバーのスライダー類の縦位置が揃うこと。
- **§8**: 高さレーン／イベントレーンをトグルしても**ノーツの見かけの位置が1pxも動かない**こと。
  ウィンドウを狭くしたときにレーンがはみ出さないこと。
- **§3.4**: 画面倍率を変えたときに、レーン・ノーツ・小節番号がすべて同じ倍率で追従すること
  （USS 変数を経由していない直書きが残っていると、そこだけ取り残される）。
- **§3.2**: フレームレート制限を切り替えて実際に fps が変わること（発熱の再発チェック）。

---

## 実装ログ（2026-08-02、同セッション内）

§10の実装順どおり §6/§7 → §8 → §1 → §2 → §3 → §4 → §5 → §9 の順で全項目実装した。
`dotnet build Assembly-CSharp.csproj` でコンパイル成功を確認済み（警告14件、うち2件は
今回追加した`PreventDefault()`呼び出しに対する既存パターンと同型の非推奨警告で実害なし、
残り12件は既存分）。**Unity Editor上でのPlay確認は次回。**

- **§6/§7**: `.menu-bar`/`.toolbar`/`.status-bar`の固定`height`を`min-height`へ変更
  （r4 §7続報と同じ修正パターン）。ツールバーの「幅」欄の`style.width`直書きを`.tb-field`
  USSクラスへ移動。ズームの基準値・クランプ範囲(`ZoomBasePxPerBeat`/`Min`/`Max`)を定数化し、
  `SetZoom`/`OnSheetWheel`/スライダー生成の3箇所の重複を解消。
  Q9の回答（縦位置のずれ）どおり、§7の症状は§6と同一原因だったため単一の修正で両方解消した。
- **§8**: `SheetLayout`のコンストラクタを全面書き換え。`leftMargin`〜`rightMargin`の合計幅
  (`contentW`)を表示/非表示に関わらず常に確保し、収まる場合はキャンバス中央へオフセット、
  収まらない場合は`laneWidthPx`を縮めて全体を収める（従来どおりの伸縮フォールバック）。
  `showEventLane`/`showHeightLane`は「幅を0にする」役目から「中身(チップ・区切り線・背景)を
  描くかどうか」だけの役目に変わったため、`GenerateNotesSheet`・`DrawPlacementGhost`・
  `OnSheetPointerDown`・`OnSheetRightClick`・`UpdateEventChips`・`UpdateSheetLabels`の
  該当ガードをすべて`L.rightMargin.width > 0f`等の幅判定から`showEventLane`/`showHeightLane`の
  直接参照へ書き換えた（設計どおり、r4 §5で入れた早期returnの条件が幅0前提だったため全滅した）。
  小節番号ラベルは左詰め(`style.left`)から右詰め(`style.right`、レーンの左端基準)に変更
  （左余白の幅が中央揃えのオフセット込みで動くため）。背景色を「余白(letterbox、暗)」と
  「レーン一式の実寸(content、従来の明るさ)」の2層に分けた。
- **§1**: `EditorSettings.cs`（新規）に永続化層を実装。JSON（`persistentDataPath/editor-settings.json`）
  + `JsonUtility.FromJsonOverwrite`で欠けたフィールドは既定値のまま残す方式。`browseDir`も
  PlayerPrefsからこちらへ移行（Q1どおり）。§3.4の画面倍率は`PanelSettings`を`Instantiate`して
  `UIDocument.panelSettings`に差し替え、`referenceResolution`を倍率で割る方式で実装
  （アセット自体は汚さない）。**Q4どおり、未保存の新規譜面（`chartPath`が空）も自動保存の対象に
  した**（`persistentDataPath/untitled.muses.autosave`固定ファイル名。起動時に
  `CheckUntitledAutosaveRestore()`で復元プロンプトを出す）。
- **§2**: 設定モーダルを`ShowModal`基盤の上に構築。内側は横タブ3枚（一般/タイムライン/
  ショートカットキー）で、既存の`.tab-header`/`.tab-header-btn`をそのまま流用。
  各コントロールは設定専用のコピーを持たず、`ChartEditorApp`の既存フィールド
  （`followPlayback`等）へ直接バインドして値変更を即時反映する設計にした
  （`EditorSettings`は永続化専用、`SaveSettingsFromLiveFields()`が書き出し時に同期する）。
- **§3**: オートセーブのON/OFF・間隔（`autosaveMinutes`、旧`AutosaveIntervalSec`定数を置き換え）、
  フレームレート制限（VSync/60/120/無制限、`vSyncCount`と`targetFrameRate`は排他なので
  選択肢ごとに両方を明示）、画面倍率スライダーを実装。
- **§4**: 「再生に追従」「ページ送りスクロール」「判定線位置」を右パネルから設定タブへ移設
  （右パネルの「表示設定」Foldoutは「表示」に改名し、「設定を開く...」ボタンを追加）。
  縦線分割（`laneDivisions`、12の約数のみ許容）をセル境界線の描画に反映（外周2px・分割線2px・
  通常1px）。スクロール反転（`invertScroll`）を`OnSheetWheel`に反映。**Ctrl+ホイールのズーム
  方向は反転設定と独立のまま**（Q5どおり）。**おまけでShift+ホイールの4倍速スクロールも追加**
  （参照元`EditorWindows.cpp:79`、設定不要な純粋な追加）。
- **§5**: 最大の変更。`ChartEditorApp.Commands.cs`（新規）にコマンドテーブル(`EditorCommand`)と
  ディスパッチャ(`OnGlobalKeyDown`)を実装。旧`HandleUndoRedoShortcuts()`（InputSystemポーリング、
  フォーカスを見ない）と旧`OnSheetKeyDown`（notesSheet単体のKeyDownEvent）を両方廃止し、
  `uiRoot`にTrickleDownで登録する1系統へ統合した。入力欄にフォーカスがある間は
  「修飾キー無しの単キー」と「primary+C/V/X/A/Z/Shift+Z/Y（コピペ・取り消し・やり直し・
  全選択）」を奪わないようにした。**設計時点の記述は「取り消し(Z)のみ」を挙げていたが、
  実装時に「やり直し」（Shift+Z・Y）も対称的に保護するよう広げた**——そうしないと
  入力欄編集中にShift+Zを押したときだけ同じ種類の誤爆（テキストの再入力ではなく譜面の
  Redoが走る）が残ってしまうため。既定キーバインドは`EditorSettings.DefaultKeyBindings()`に
  集約し、参照元の数字キー(1〜8)ツール切替、`Cmd+A`（すべて選択、**muses未実装だったため新規実装**）、
  `Cmd+F`（選択反転）等を追加。停止コマンドは参照元のBackspaceだと`EditDelete`と衝突するため
  `Shift+Space`に変更（設計どおり）。ショートカット設定タブは「＋」でのキーキャプチャ、
  chipクリックでの削除、コマンド単位の「既定」ボタンを実装。競合は後勝ち（他コマンドから
  自動で外す）。キャプチャ中は`OnGlobalKeyDown`側で通常ディスパッチを止める
  （既存の数字キー等の割り当てが先に発火してキャプチャへ届かなくなるのを防ぐため）。
- **§9**: `GenericDropdownMenu`を廃し、`EditorMenu`/`EditorMenuItem`という薄いシムクラス＋
  `overlayLayer`上の自前ポップアップに置き換えた（`BuildMenuBar`の`menu.AddItem(...)`呼び出し
  自体はシグネチャを保っているため無変更）。**実装時に気づいた点**: ポップアップをスクリムの
  子にすると、ポップアップ自身へのクリックがスクリムまでバブリングしてクリック途中でメニューが
  消え、項目の`clicked`（pointer-up側の合成イベント）が成立しなくなる。スクリムとポップアップを
  兄弟にし、ポップアップ側で`PointerDownEvent`を`StopPropagation`することで回避した。
  **もう1点**: スクリムを画面全体で覆うと、GenericDropdownMenuで問題になったのと同じ理由で
  メニューバーの他のボタンへの`PointerEnterEvent`が届かなくなりホバー切替が機能しない。
  スクリムの`top`をメニューボタンの`worldBound.yMax`（＝ポップアップの開始位置）にすることで
  メニューバー行そのものはスクリムに覆われないようにし、これを回避した。

**設計からの逸脱（実装時の判断、いずれも上記に記載済みだが一覧化）**:
1. §5.2(3): undo/redoのテキスト編集競合ガードをZだけでなくShift+Z/Yにも対称適用。
2. §3.1: 未保存新規譜面の自動保存対応（Q4で「直す」と確定済みなので逸脱ではなく実装確認）。

## §9 バグ修正: ホバー切替でポップアップが閉じない（2026-08-02、ユーザー実機報告）

**症状**: メニューを1つ開いた後、別のメニューへホバーで切り替えると、古いポップアップが
消えずに残ったまま新しいポップアップが重なって表示され続ける。

**原因**: `CloseMenu()`が`openMenuPopup = null`とするだけで`RemoveFromHierarchy()`を
呼んでいなかった。popupは（§9本文に記載の理由により）scrimの子ではなく`overlayLayer`の
兄弟として追加しているため、scrimを消してもpopupは残ったままになる。ホバーで`OpenMenu`が
呼ばれるたびに`CloseMenu`→新規`popup`生成が繰り返され、古いpopupが積み重なっていた。

**修正**: `CloseMenu()`に`openMenuPopup?.RemoveFromHierarchy();`を追加。

**検証状況**: コンパイル成功のみ確認。Unity Editorでの実機確認は次回。

---

**次回セッション最優先事項**: Unity Editorでの実機確認。特に
- §5: 右パネルの入力欄編集中に`Cmd+Z`を押してテキストの取り消しになること（譜面のUndoが
  誤爆しないこと）。`notesSheet`にフォーカスが無い状態でもDelete/コピペ/ツール切替が効くこと。
  起動直後（どこもクリックしていない状態）でショートカットが効くこと（届かない場合は
  `uiRoot.Focus()`だけでは不十分な可能性があり、`notesSheet`等への切り戻しを検討）。
- §9: メニューを1つクリックした後、他のメニューボタンへホバーするだけで切り替わること。
  ポップアップ自身をクリックしても正しく項目が実行されること（スクリムに食われないこと）。
- §8: 高さレーン／イベントレーンをトグルしてもノーツの見かけの位置が動かないこと。
  ウィンドウを狭くしたときにレーンがはみ出さず縮小フォールバックが効くこと。
- §3.4: 画面倍率を変えたときレーン・ノーツ・小節番号・設定モーダル自体が揃って倍率に追従すること。
- §3.2: フレームレート制限の切り替えで実際にfpsが変わること。
- §6/§7: ツールバーの「幅」欄が正しく表示され、ステータスバーのスライダー類の縦位置が揃うこと。

---

## 関連

- `memory/editor-ui-rework-r4.md` — 前段。§7 続報の「固定 `height` は使わず `min-height`」が
  本書 §6 / §7(a) の根拠。実機確認済み。
- `memory/editor-ui-rework-r3.md` / `r2.md` / `mmw.md` — その前段。
- `memory/editor-ui-redesign.md` — **§2.7「設定画面（画面倍率・キーバインド）」が本書の出発点**
  （3項目とも本書で実装対象になった: 画面倍率=§3.4、キーバインド=§5、ノーツ選択ショートカット=§5.3）。
  §7.2 の帯構成が本書 §8、§4.1 の「IMGUI にはメニュー部品が無い」が本書 §9 の背景、
  §3 の指摘3「メニューバーが小さすぎる」が本書 §3.4 の動機。
- `memory/editor-spec.md` — Phase 4 機能仕様 rev.3。設定画面は仕様に無い項目なので、
  実装確定後に §2 へ追記する。
- `memory/reference/MikuMikuWorld-master/` — 参照元。本書 §0 の表に出典行を全て記載。
- [[muses-unity-port-progress]] — 「Play 中の発熱」の記録が本書 §3.2 の既定値（VSync）の根拠。
- [[feedback-editor-ui-polish-deferred]] — 見た目の指摘を溜めてまとめて対応する方針。
</content>
</invoke>
