# 譜面エディタ r14 — フォント・記号・メニュー・プレビュー解像度

2026-09-26 のUI調査（フォント・Webとの見た目差・アニメーション・入力欄幅・コンテキストメニュー・
プレビュー解像度の6項目）の結果と、そのうち実装する範囲の設計。

出典: [[muses-unity-port-progress]]。前提: r12 §3.3（日本語フォント導入）、r5 §9（自前メニュー）、
r13 §7（プレビュー）。

---

## 0. 確定事項（ユーザー決定、2026-09-26）

| # | 項目 | 決定 |
|---|---|---|
| 1 | 今回実装する範囲 | **§1（記号の表記崩れ＋フォントの停止）・§5（コンテキストメニュー）・§6（プレビュー解像度）** |
| 2 | コンテキストメニューのOS標準化 | **しない**。自前ポップアップへ統一する（§5.2） |
| 3 | §2（Web風の文字）・§3（アニメーション）・§4（入力欄幅） | 調査結果のみ記録し、今回は実装しない |

「フォントの停止」はユーザーへ意味を確認中のため、**「初めて表示する文字でUIが一瞬止まる」**と解釈して
§1.4を実装する。解釈が違えば§1.4を差し替える。

---

## 1. 記号の表記崩れとフォントの停止

### 1.1 調査結果: フォントに無い記号

UI文字列に出てくる記号をすべて `NotoSansJP-Regular.ttf` のcmapと照合した結果、欠けているのは2つだけ。

| 記号 | 用途 | 場所 |
|---|---|---|
| `❙` U+2759 | 再生中の一時停止ボタン `❙❙` | `ChartEditorApp.UI.cs` SyncModelToUi |
| `⇕` U+21D5 | ツール名「層移動⇕」 | `ChartEditorApp.UI.cs` toolDefs |

フォールバック先が同じNoto 1本だけなので、この2つは空白か四角になる。

### 1.2 調査結果: 全角幅の記号

Noto Sans JP では `←↑→↓▶◀■●` がすべて**幅1000（全角）**。さらに形も揃っていない。

- ▶ の縦の範囲は -93〜847（940）、■ は -20〜780（800）で、▶ だけ一回り大きい。
- ▶ は左右の余白が 122/64、◀ は 64/122 で非対称。`|◀` `▶|` のように細い `|`（幅270）と
  並べると左右で釣り合わない。
- `⌘↑` のようなショートカット表記は、矢印が全角なので間延びする。

### 1.3 対策: トランスポートのボタンは文字をやめて図形で描く

`|◀ ■ ▶ ❙❙ ▶|` を `TransportIcon`（`generateVisualContent` + `Painter2D` で描く `VisualElement`）に
置き換える。色はボタンの文字色（`resolvedStyle.color`）を使うので、無効時の見た目もUSSに従う。
画像アセットは増やさない。

「層移動⇕」は他のツール名と同じく記号なしの「層移動」にする（色クラス `riser` で区別できている）。

### 1.4 フォントの停止: 使う文字を起動時に先に焼く

**原因**: フォントアセットは Dynamic 方式で、初めて表示する文字をその場でSDFに焼く。
`m_ClearDynamicDataOnBuild: 1` なので、ビルドは毎回空の状態で起動する。
設定モーダルを初めて開いた瞬間など、新しい漢字が一度に大量に出るとメインスレッドで焼く分だけ止まる。

**対策**:
1. ビルド前（`BuildChartEditor`）とメニュー `Build/Update Chart Editor Glyph List` で、
   エディタのソース（`Assets/Scripts/ChartEditorApp/*.cs`・`Assets/Scripts/Chart/*.cs`・
   `Assets/UI/ChartEditor/*.uxml`）の文字列リテラルに出てくる文字を集め、
   `Assets/Resources/ChartEditorGlyphs.txt` へ書き出す。
2. 起動時（`ChartEditorApp.Awake`）にそれを読み、PanelSettingsのTextSettingsに登録された
   Dynamicフォントへ `TryAddCharacters` で先に焼く。かかった時間はログへ出す。

- 起動時に1回だけ待つ代わりに、操作中は止まらなくなる。
- 譜面の曲名など、ソースに無い文字は従来どおりその場で焼く（数文字なので問題にならない）。
- フォントアセットの初期状態は変えない。ゲーム側（課題H）のサイズにも影響しない。
- Resources のテキストは数KBなので、ゲームのビルドに入っても問題ない。

---

## 2. Webと見た目が違う理由（調査のみ）

| 原因 | 現状 | 対策の候補 |
|---|---|---|
| フォント | Noto Sans JP を同梱 | OSフォント（Mac=ヒラギノ、Win=Yu Gothic UI）を実行時に読む。Unity 6.5 の `FontAsset` に `CreateFontAssetWithOSFallbackList` / `GetOSFallbacks` がある |
| 太字 | Regular 1本を擬似太字化 | OSフォントなら本物の太字が使える |
| 足りない記号 | フォールバック無し | Advanced Text Generator（ATG）はOSフォントへのフォールバックを持つ（USS `-unity-text-generator`） |
| 拡大縮小 | PanelSettings が ScaleWithScreenSize（基準1600×900、match 0） | 小数倍率で線と文字がにじむ。ウィンドウ幅で文字の大きさも変わる。ブラウザ同様の「dpi基準の固定倍率」にするとくっきりし、大きさも一定になる |

描画方式（SDF対OSのラスタライザ）が違うので、完全に同じにはならない。
OSフォント化すれば同梱フォントが不要になり、課題Hにも効く。未使用の `NotoSansJP-Variable.ttf`（9.5MB）が残っている。

## 3. アニメーション（調査のみ）

- USSの `transition` で、色・透明度・`translate`・`scale` を変えるのは軽い。幅・高さ・位置は
  レイアウトの再計算が走るので避ける。
- **向いている**: ボタンのホバー色（80〜120ms）、メニュー・モーダルのフェードイン（約120ms）、トースト。
- **向かない**: 譜面シートやプレビュー。毎フレーム描き直しており効果がなく、時刻表示が遅れて見えるだけ。
- 要素を追加した直後のフレームではtransitionが発火しない。クラスの付与を1フレーム遅らせる必要がある。
- 「アニメーションを減らす」設定を付けておく。

## 4. 入力欄の横幅（調査のみ、ユーザーに該当箇所を確認中）

- `.prop-label` は `width: 88px; flex-shrink: 0`。設定モーダルの長いラベルは88pxを超えてはみ出す。
- 値の側は、FloatField/TextFieldは行いっぱいに伸びる。一方、Slider内の数値欄はUnityの既定幅、
  Toggleは最小幅のままなので揃わない。
- インスペクタ・設定モーダル・新規曲ウィザードで同じ88pxを共有している。
- 対策の候補: 画面ごとにラベル列の幅を変数化し、値の側の幅規則を種類ごとに揃える。

---

## 5. コンテキストメニュー

### 5.1 現状の問題

- 右クリックメニューだけ `GenericDropdownMenu` のまま。メニューバーは r5 §9 で自前ポップアップへ移行済み。
- `"削除\tDelete"` のようにタブで右列を作ろうとしているが、`GenericDropdownMenu` は右揃えの列を持たないので崩れる。
- ショートカットが `Ctrl+X` 固定で、macOS（⌘）とも、ユーザーが変更したキー割り当てとも一致しない。

### 5.2 OS標準メニューにしない理由

- Windows は `TrackPopupMenuEx` をP/Invokeで呼べば実装できる。macOS は `NSMenu` が必要で、
  osascriptでは出せない。Objective-Cのネイティブプラグインを作ってビルドする必要があり、
  「ネイティブバイナリを持たない」方針（ファイル選択と同じ判断）が崩れる。
- OSメニューはOSのライト/ダークに従い、エディタのダークテーマと合わない。
- 性能面の問題は無い。表示中はUnityのメインループが止まるが実害は無い。問題は保守コストと見た目。

### 5.3 設計

- ポップアップの生成（`BuildMenuPopup`）をメニューバーと共通化し、右クリック用に
  `ShowContextMenu(EditorMenu, Vector2 worldPos)` を足す。
  - スクリムは画面全体を覆う。メニューバーと違い、ホバーで切り替える必要が無いため。
  - 表示後に `GeometryChangedEvent` でサイズを測り、画面外にはみ出す場合は左/上へずらす。
- 1行は **チェック列（固定幅）｜項目名（伸縮）｜ショートカット（右揃え・薄色）** の3列。
- `EditorMenuItem` に `commandId` を持たせる。ショートカットの表記は、キー割り当て
  （`keyBindings`）の先頭の組み合わせを `KeyChord.ToString()` した値で、開くたびに作る。
  キー割り当てを変えても表記が古くならない。
- メニューバーの項目にも、対応するコマンドがあるものは同じ方法でショートカットを出す。

---

## 6. プレビューの解像度

### 6.1 原因

1. `UpdatePreviewTexture` が `contentRect`（パネル座標、論理ピクセル）の値で RenderTexture を作っている。
   PanelSettings の倍率とRetina（2倍）の分だけ解像度が足りず、引き伸ばして表示している。
2. MSAA無効（`PC_RPAsset` の `m_MSAA: 1`）。プレビューのカメラにも後処理のアンチエイリアスが無く、ノーツの縁がギザギザになる。
3. 上限が 1920×1080。

### 6.2 設計

- RenderTexture の実寸を `contentRect × previewSurface.scaledPixelsPerPoint × previewRenderScale` にする。
  上限は 3840×2160 に上げる。
- アンチエイリアスはプレビューのカメラだけに後処理のAA（`UniversalAdditionalCameraData.antialiasing`）を付ける。
  URPアセットのMSAAはゲーム（PC）とも共有しているので触らない。
  - ボリューム（ブルーム等）が効かないよう `volumeLayerMask = 0` にする。
  - 既定は SMAA（High）。
- 設定モーダルのタイムラインタブに2項目を追加し、`EditorSettings` に保存する。
  - `previewRenderScale`: 0.5 / 0.75 / 1.0（既定 1.0）
  - `previewAntialiasing`: なし / FXAA / SMAA（既定 SMAA）
- GPU負荷は最大で約4倍になるが、プレビューは再生中とダーティ時しか描かない（r13 §7.7）。重ければ倍率を下げる。
- 判定線のオーバーレイは `contentRect` 座標で描いているので影響しない。
- ノーツ自体の解像度（テクスチャ・厚み）はゲーム側と共通なので、ゲーム側で検討する。

---

## 7. 未決事項

- 「フォントの停止」の解釈（§1.4）。
- §4 の該当箇所。
- §2（ATG・OSフォント・PanelSettingsの倍率方式）を試すかどうか。試すなら課題Hと同時に進める。

---

## 8. 実装記録（2026-09-26）

- §1.3: `TransportIcon.cs`（新規）。ズームの `−` `＋` は崩れないので文字のまま。
- §1.4: `Assets/Editor/BuildChartEditorGlyphList.cs`（新規、ビルド時に自動実行）→
  `Assets/Resources/ChartEditorGlyphs.txt`（初版は485字、うちCJK 376字）。`ChartEditorApp.PrewarmUiGlyphs`
  が起動時に焼き、所要時間を `Debug.Log` に出す。
- §5.3: `BuildMenuPopup` / `ShowContextMenu` / `ShortcutText`（`ChartEditorApp.UI.cs`）。右クリックメニューは
  `GenericDropdownMenu` をやめた。メニューバーの項目にもショートカットの表記が付く。
- §6.2: `PreviewSystem.RenderScale` / `Antialiasing`。設定のタイムラインタブに「プレビュー解像度」
  「プレビューのアンチエイリアス」を追加。
- 確認済み: `dotnet build`（ランタイム・Editor両アセンブリ）でエラー0。**Unityでの表示確認はまだ**。

---

## 9. 追加の決定と実装（2026-09-26、2回目）

ユーザーの回答:
- §1.4 の解釈（初回表示時の一瞬の停止）で**合っていた**。
- §4 の対象は**設定モーダルの一般・タイムラインタブの項目全般**。問題は3つ: 横幅が必要以上に長い／幅が統一されていない／項目名が入力欄に被る。
- §2 は**OSフォントにする**。§3（アニメーション）は今回は見送り。

### 9.1 設定モーダルの行（§4）

`.settings-body` の下だけに規則を足した。インスペクタ等の88px規則は変えていない。
- ラベル列は 200px、折り返し可（`white-space: normal`）。項目名が入力欄に被らない。
- 値の側は種類を問わず 220px 固定（`flex-grow: 0`）。例外は次の2つ。
  - チェックボックス（`width: auto`）
  - 曲フォルダの行（`.prop-value--wide`、パスは長さが決まらないため残り全幅）
- スライダー横の数値欄は 52px に固定（`.unity-base-slider__text-field`）。

### 9.2 OSフォント（§2）

`UiFonts.cs`（新規）。`ChartEditorApp.Awake` で、PanelSettingsをUIDocumentへ割り当てる前に適用する。
- `FontAsset.CreateFontAsset(family, style, 48, 5, SDFAA)` でOSのフォントを実行時に読む。
  - Mac: Hiragino Sans W3（太字W6）。候補2番目は Hiragino Kaku Gothic ProN。
  - Win: Yu Gothic UI Regular/Bold。候補2番目は Meiryo UI。
- 太字ウェイトを `fontWeightTable[7]` に登録し、擬似太字をやめる。
- `ChartEditorTextSettings.asset` は書き換えず、`Instantiate` した複製の既定フォントを差し替える。
  同梱Notoはフォールバックとして残す。
- 設定の一般タブに「文字のフォント(再起動後)」を追加した（OS／同梱Noto）。`EditorSettings.uiFontMode`、既定はOS。
- 先読み（§1.4）は既定フォントだけに絞った。フォールバックまで焼くと起動が遅くなるだけのため。
- UIで使う485字は、すべてHiragino Sans W3に含まれることを確認済み。Yu Gothic UIは手元に無く未確認。
- ATG（`-unity-text-generator: advanced`）は今回入れていない。OSフォントで記号の欠けが無いため、必要になってから試す。
- 同梱Notoは参照が残るのでビルドには入ったまま。削るならゲーム側の課題Hと合わせて行う。
