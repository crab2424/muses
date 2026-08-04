using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using Muses.Chart;
using Muses.Stage;

namespace Muses.ChartTool
{
    /// <summary>
    /// editor-ui-redesign.md §1 の骨格A〜Dを UI Toolkit で組む部分。
    /// ChartEditorApp.cs（データモデル・キャンバス描画）と同じクラスの分割定義にしてあるのは、
    /// UI側から chart/selectedNote/undoStack といった内部状態へ素直に触れるようにするため
    /// （別クラスにすると privateフィールドを公開するだけの中継APIが大量に要る）。
    ///
    /// 骨格は Assets/UI/ChartEditor/ChartEditorRoot.uxml、見た目は同フォルダの .uss にある。
    /// このファイルが持つのは「器に中身を流し込む」ことと、毎フレームの表示同期だけ。
    ///
    /// ノーツシートは generateVisualContent + painter2D で描く（描画本体は ChartEditorApp.cs 側）。
    /// §6 が想定していた「まず IMGUIContainer で包む」段階移行は、IMGUIContainer が
    /// ランタイムパネルで使えないため採れない。
    /// </summary>
    public partial class ChartEditorApp
    {
        private static readonly (EditorTool tool, string label, string modifier)[] ToolButtons =
        {
            (EditorTool.Select, "選択", "neutral"),
            (EditorTool.Tap, "Tap", "tap"),
            (EditorTool.ExTap, "Ex Tap", "extap"),
            (EditorTool.Slide, "Slide", "slide"),
            (EditorTool.Flick, "Flick", "flick"),
            (EditorTool.AddWaypoint, "中継点", "neutral"),
            (EditorTool.Delete, "削除", "neutral"),
            (EditorTool.Event, "イベント", "event"),
        };

        private const int TabTimeline = 0;
        private const int TabPreview = 1;

        // ---- 要素の参照（BuildUIで一度だけ引く）----
        private VisualElement uiRoot;
        private VisualElement overlayLayer;
        private VisualElement notesSheet;
        private VisualElement previewSurface;
        private Label hiSpeedLabel;
        private Button timelineTabButton, previewTabButton;
        private int selectedTabIndex = TabTimeline;

        private const int MaxSheetLabels = 128;
        private readonly List<Label> sheetLabels = new();

        // §7.3 イベントレーン（BPM/拍子/ソフラン）のチップ。クリック選択・追加を受けるので
        // sheetLabelsと違い pickingMode.Ignore にしない。
        private const int MaxEventChips = 128;
        private readonly List<Label> eventChipLabels = new();

        private class EventChipRef
        {
            public EventKind kind;
            public int index;
        }

        private Label toolbarStatus;
        private Label toolbarDirty;
        private readonly Dictionary<EditorTool, Button> toolButtons = new();
        private Button undoButton, redoButton;

        private Button playButton;
        private Slider scrubSlider;
        private Slider zoomSlider;
        private Label zoomLabel;
        private DropdownField snapDropdown;
        private Label statusTime, statusChartInfo;
        private Button statusValidation;

        private TextField infoTitle, infoArtist, infoJacket, infoDifficulty, infoCharter;
        private IntegerField infoLevel;
        private TextField audioFile;
        private FloatField audioOffset;
        private Label audioStatusLabel;
        private Slider masterVolumeSlider, bgmVolumeSlider, seVolumeSlider;
        private FloatField widthField;
        private Slider viewRate;
        private Toggle viewHeightLane;
        private Toggle viewEventLane;
        private Label statsText;
        private VisualElement presetHost;
        private TextField presetNameField;
        private Foldout foldInspector, foldValidation;
        private VisualElement validationHost;
        private Toggle validateOnSaveToggle;

        // インスペクタは選択ノーツの中継点の数だけ行が変わるので、構造の作り直しと値の追従を分けて持つ
        private VisualElement inspectorHost;
        private Note inspectorNote;
        private int inspectorPointIndex = -1; // §3: どの点を表示中かも再構築判定に含める
        private int inspectorPointCount = -1;
        private int inspectorSelectionCount = -1; // §7.4-A 複数選択の件数変化を検知する
        private readonly List<Action> inspectorRefreshers = new();

        // §7.3: イベント選択時もインスペクタを共用するため、直近に組んだ選択状態を別途追跡する
        private EventKind inspectorEventKind = EventKind.None;
        private int inspectorEventIndex = -1;

        /// <summary>UI側からの書き込み中に同期処理が走って値が往復するのを防ぐ。</summary>
        private bool suppressUiCallbacks;
        private bool restorePromptShown;

        /// <summary>
        /// 情報/音源セクションの入力欄をモデルの値で上書きすべきタイミング（読み込み・Undo・新規）を表す。
        /// 毎フレーム上書きしてはいけない: 数値欄に「1.」まで打った時点でモデルは1になり、
        /// 書き戻しで入力途中の文字列が「1」に潰されて続きが打てなくなるため。
        /// </summary>
        private bool uiNeedsPropertyRefresh = true;

        /// <summary>統計・拍子/BPM表示のように毎フレーム作り直すと無駄が大きい表示の更新間隔。</summary>
        private const float UiSlowRefreshSec = 0.1f;
        private float lastSlowRefreshRealtime = -999f;

        private void Start()
        {
            var doc = GetComponent<UIDocument>();
            if (doc == null || doc.rootVisualElement == null)
            {
                Debug.LogError("ChartEditorApp: UIDocument が見つかりません（ChartEditor.unity の設定を確認してください）。");
                enabled = false;
                return;
            }

            uiRoot = doc.rootVisualElement.Q<VisualElement>("chart-editor-root");
            if (uiRoot == null)
            {
                Debug.LogError("ChartEditorApp: ChartEditorRoot.uxml の chart-editor-root が見つかりません。");
                enabled = false;
                return;
            }

            overlayLayer = uiRoot.Q<VisualElement>("overlay-layer");

            BuildMenuBar();
            BuildToolbar();
            BuildTabs();
            BuildRightPanel();
            BuildStatusBar();
            BuildCommandTable();

            // editor-ui-rework-r5.md §5.2: キー入力の受け口をuiRoot 1箇所へ統合する
            // （旧: HandleUndoRedoShortcutsのInputSystemポーリング + notesSheet単体のKeyDownEvent
            // の2系統に分かれており、右パネルの入力欄にフォーカスがある間はUndo/Redoがテキスト編集を
            // 無視して譜面側へ誤爆する不具合があった）。TrickleDownで登録するので、パネル内の
            // どの要素がフォーカスを持っていてもuiRootを経由する限り先にここへ届く。
            uiRoot.focusable = true;
            uiRoot.RegisterCallback<KeyDownEvent>(OnGlobalKeyDown, TrickleDown.TrickleDown);
            // editor-ui-rework-r2.md §5: 矢印キーはKeyDownEventとは別にNavigationMoveEventを
            // 発行し、既定でフォーカスを次のfocusable要素へ移してしまう。OnGlobalKeyDownの
            // StopPropagationはKeyDownEventしか止められないため、これも別途潰す
            // （旧実装はnotesSheet単体に登録していたが、受け口をuiRootへ統合したので同じ場所へ移した）。
            uiRoot.RegisterCallback<NavigationMoveEvent>(evt =>
            {
                evt.PreventDefault();
                evt.StopPropagation();
            }, TrickleDown.TrickleDown);
            // 起動直後、何もクリックしていない状態でもショートカットが効くようにする試み
            // （UI Toolkitのランタイムパネルでフォーカスが無い状態でKeyDownEventが配送されるかは
            // 実機で確認するまで信用しない。届かない場合はnotesSheet.Focus()等への切り戻しを検討）。
            uiRoot.Focus();

            SyncModelToUi();
            RefreshValidationList();
        }

        // ================= A: メニューバー(§2.1) =================

        private void BuildMenuBar()
        {
            var bar = uiRoot.Q<VisualElement>("menu-bar");
            bar.Clear();

            AddMenu(bar, "ファイル", menu =>
            {
                menu.AddItem("新規", false, NewChart);
                menu.AddItem("新規曲...", false, ShowNewSongWizard);
                menu.AddItem("開く...", false, () => ShowFileModal(saveMode: false));
                menu.AddSeparator("");
                menu.AddItem("保存", false, SaveChartToPath);
                menu.AddItem("曲フォルダを選んで保存...", false, () => ShowFileModal(saveMode: true));
                menu.AddSeparator("");
                menu.AddItem("終了", false, QuitApp);
            });

            AddMenu(bar, "編集", menu =>
            {
                if (undoStack.Count > 0) menu.AddItem($"元に戻す: {PeekUndoLabel()}", false, Undo);
                else menu.AddDisabledItem("元に戻す", false);
                if (redoStack.Count > 0) menu.AddItem($"やり直す: {PeekRedoLabel()}", false, Redo);
                else menu.AddDisabledItem("やり直す", false);
                menu.AddSeparator("");
                if (selection.Count > 0)
                    menu.AddItem(selection.Count > 1 ? $"選択した{selection.Count}件を削除" : "選択中のノーツを削除", false, DeleteSelection);
                else
                    menu.AddDisabledItem("選択中のノーツを削除", false);
                if (selection.Count > 0)
                {
                    menu.AddItem("コピー", false, CopySelectionToClipboard);
                    menu.AddItem("切り取り", false, () => { CopySelectionToClipboard(); DeleteSelection(); });
                }
                else
                {
                    menu.AddDisabledItem("コピー", false);
                    menu.AddDisabledItem("切り取り", false);
                }
                if (clipboard.Count > 0)
                {
                    menu.AddItem("貼り付け", false, () => EnterPasteMode());
                    menu.AddItem("反転して貼り付け", false, () => EnterPasteMode(flip: true));
                }
                else
                {
                    menu.AddDisabledItem("貼り付け", false);
                    menu.AddDisabledItem("反転して貼り付け", false);
                }
                menu.AddSeparator("");
                // MikuMikuWorld移植候補: 選択の左右反転（Editing.cpp:503-526）。
                if (selection.Count > 0)
                    menu.AddItem("選択を反転", false, FlipSelected);
                else
                    menu.AddDisabledItem("選択を反転", false);
            });

            AddMenu(bar, "表示", menu =>
            {
                menu.AddItem("タイムライン", selectedTabIndex == TabTimeline,
                    () => SelectTab(TabTimeline));
                menu.AddItem("譜面プレビュー", selectedTabIndex == TabPreview,
                    () => SelectTab(TabPreview));
                menu.AddSeparator("");
                // editor-ui-rework-r5.md §4.1: 「再生に追従」の設定コントロールは設定モーダルの
                // タイムラインタブへ移設したため、ここはfollowPlaybackを直接書き換えるだけでよい
                // （モーダルが開いていれば次に開いたときの表示に反映される。両方同時に開いて
                // 見比べる操作は想定していない）。
                menu.AddItem("再生に追従", followPlayback, () => followPlayback = !followPlayback);
                menu.AddItem("高さレーン", showHeightLane, () =>
                {
                    showHeightLane = !showHeightLane;
                    if (viewHeightLane != null) viewHeightLane.SetValueWithoutNotify(showHeightLane);
                });
                menu.AddItem("イベントレーン", showEventLane, () =>
                {
                    showEventLane = !showEventLane;
                    if (viewEventLane != null) viewEventLane.SetValueWithoutNotify(showEventLane);
                });
            });

            AddMenu(bar, "再生", menu =>
            {
                menu.AddItem(preview.IsPlaying ? "一時停止" : "再生", false, () => preview.TogglePlay());
                // editor-ui-rework-r3.md §8: 停止中にpreview.Seekを呼ぶのでscrollTick/cursorTickも合わせる。
                menu.AddItem("先頭へ戻る", false, () => { cursorTick = 0; scrollTick = 0; preview.Seek(0f); });
                menu.AddSeparator("");
                menu.AddItem("オートプレイ", preview.Autoplay, () => preview.SetAutoplay(!preview.Autoplay));
                menu.AddItem("ノーツSE", preview.SePreview, () => preview.SePreview = !preview.SePreview);
                menu.AddItem("メトロノーム", preview.Metronome, () => preview.Metronome = !preview.Metronome);
            });

            AddMenu(bar, "ツール", menu =>
            {
                menu.AddItem("譜面を検証", false, RunValidation);
                menu.AddItem("保存時に自動検証", validateOnSave, () =>
                {
                    validateOnSave = !validateOnSave;
                    validateOnSaveToggle?.SetValueWithoutNotify(validateOnSave);
                });
                menu.AddSeparator("");
                menu.AddItem("設定...", false, ShowSettingsModal);
            });

            AddMenu(bar, "ヘルプ", menu =>
            {
                // editor-ui-rework-r5.md §5: キーボードショートカットは変更可能になったため、
                // 個別の割り当てをここに書き並べるとすぐ古くなる。設定タブへの案内のみ残す。
                menu.AddItem("ショートカットキーの一覧・変更は「ツール > 設定」から", false, ShowSettingsModal);
                menu.AddSeparator("");
                menu.AddItem("Shift+ドラッグ = 追加選択 / Shift+クリック = 選択トグル", false, null);
                menu.AddItem("右クリック = コンテキストメニュー", false, null);
                menu.AddItem("高さレーン = 選択中ノーツの中継点を横ドラッグで層編集", false, null);
                menu.AddItem("ホイール = スクロール / Ctrl+ホイール = 拡大縮小", false, null);
                menu.AddSeparator("");
                menu.AddItem($"muses 譜面エディタ {Application.version}", false, null);
            });
        }

        // ================= 自前メニュー(editor-ui-rework-r5.md §9) =================
        //
        // 旧実装はボタン + 標準GenericDropdownMenuだった。GenericDropdownMenu.DropDownは
        // パネルルート直下に画面全体を覆うコンテナを作るため、その下にあるメニューバーの
        // ボタンにPointerEnterEventが届かず「一度クリックしたら他のタブはホバーだけで
        // 見られるようにしたい」が実現できなかった（公開APIに開いているメニューを閉じる
        // 手段も無い）。overlayLayer(モーダルと同じ層)に自前でポップアップを出す方式に置き換える。
        // AddItem/AddDisabledItem/AddSeparatorの呼び出し側(BuildMenuBar)はシグネチャを
        // 揃えているので無変更で移行できる。

        private class EditorMenuItem
        {
            public string label;
            public bool isChecked;
            public bool disabled;
            public bool isSeparator;
            public Action action;
        }

        private class EditorMenu
        {
            public readonly List<EditorMenuItem> items = new();
            public void AddItem(string label, bool isChecked, Action action) =>
                items.Add(new EditorMenuItem { label = label, isChecked = isChecked, action = action });
            public void AddDisabledItem(string label, bool isChecked) =>
                items.Add(new EditorMenuItem { label = label, isChecked = isChecked, disabled = true });
            public void AddSeparator(string _) => items.Add(new EditorMenuItem { isSeparator = true });
        }

        private readonly List<Button> menuBarButtons = new();
        private int openMenuIndex = -1;
        private VisualElement openMenuPopup;
        private VisualElement openMenuScrim;

        /// <summary>項目の活性状態は開くたびに変わるので、生成は毎回開くときに行う。</summary>
        private void AddMenu(VisualElement bar, string title, Action<EditorMenu> build)
        {
            int index = menuBarButtons.Count;
            var btn = new Button { text = title };
            btn.AddToClassList("menu-btn");
            btn.clicked += () =>
            {
                if (openMenuIndex == index) CloseMenu();
                else OpenMenu(index, btn, build);
            };
            // §9: 既にどれかのメニューが開いている間だけ、ホバーで即座に切り替える
            // （閉じている状態でのホバーでは開かない＝「一度クリックしたら」の意図）。
            btn.RegisterCallback<PointerEnterEvent>(_ =>
            {
                if (openMenuIndex >= 0 && openMenuIndex != index) OpenMenu(index, btn, build);
            });
            bar.Add(btn);
            menuBarButtons.Add(btn);
        }

        private void OpenMenu(int index, Button btn, Action<EditorMenu> build)
        {
            CloseMenu();
            openMenuIndex = index;
            foreach (var b in menuBarButtons) b.EnableInClassList("menu-btn--open", false);
            btn.EnableInClassList("menu-btn--open", true);

            var menu = new EditorMenu();
            build(menu);

            // §9: スクリム(「外側クリックで閉じる」用の当たり判定)とポップアップ本体は別要素にする。
            // ポップアップをスクリムの子にすると、ポップアップ自身へのPointerDownEventがスクリムまで
            // バブリングしてクリックの途中でメニューごと消え、項目のclickedが発火しなくなる
            // （Buttonのclickedはpointer up側の合成イベントのため、down時点で要素が消えると成立しない）。
            // 兄弟にして、ポップアップを後から追加することで重なり順を手前にする。
            //
            // 重要: スクリムはメニューバー行(btn.worldBound.yMaxより上)を覆わない。ここを画面全体で
            // 覆うと、GenericDropdownMenuで問題になったのと同じくメニューバーの他のボタンに
            // PointerEnterEventが届かなくなり、ホバー切替が実現できない。
            var bound = btn.worldBound;
            openMenuScrim = new VisualElement();
            openMenuScrim.style.position = Position.Absolute;
            openMenuScrim.style.left = 0;
            openMenuScrim.style.top = bound.yMax;
            openMenuScrim.style.right = 0;
            openMenuScrim.style.bottom = 0;
            openMenuScrim.RegisterCallback<PointerDownEvent>(_ => CloseMenu());
            overlayLayer.Add(openMenuScrim);

            var popup = new VisualElement();
            popup.AddToClassList("menu-popup");
            popup.style.position = Position.Absolute;
            popup.style.left = bound.x;
            popup.style.top = bound.yMax;
            // ポップアップ自身へのクリックがスクリムまで抜けて即クローズしないようにする。
            popup.RegisterCallback<PointerDownEvent>(evt => evt.StopPropagation());

            foreach (var item in menu.items)
            {
                if (item.isSeparator)
                {
                    var sep = new VisualElement();
                    sep.AddToClassList("menu-popup-sep");
                    popup.Add(sep);
                    continue;
                }

                var row = new Button(() =>
                {
                    if (!item.disabled) item.action?.Invoke();
                    CloseMenu();
                })
                { text = (item.isChecked ? "✓ " : "") + item.label };
                row.AddToClassList("menu-popup-item");
                row.SetEnabled(!item.disabled);
                popup.Add(row);
            }

            overlayLayer.Add(popup);
            openMenuPopup = popup;
        }

        private void CloseMenu()
        {
            openMenuIndex = -1;
            openMenuScrim?.RemoveFromHierarchy();
            openMenuScrim = null;
            // popupはscrimの子ではなくoverlayLayerの兄弟として追加している（§9のコメント参照:
            // ポップアップ自身へのクリックがスクリムまでバブリングして即クローズするのを防ぐため）。
            // そのためscrimを消すだけではpopupが消えず、ホバーで切り替えるたびに古いポップアップが
            // overlayLayerに積み重なって残ってしまっていた（バグ）。ここで明示的に取り除く。
            openMenuPopup?.RemoveFromHierarchy();
            openMenuPopup = null;
            foreach (var b in menuBarButtons) b.EnableInClassList("menu-btn--open", false);
        }

        // ================= A: ツールバー(§2.2) =================

        private void BuildToolbar()
        {
            var fileGroup = uiRoot.Q<VisualElement>("toolbar-file");
            fileGroup.Clear();
            fileGroup.Add(MakeToolbarButton("新規", NewChart));
            fileGroup.Add(MakeToolbarButton("新規曲", ShowNewSongWizard));
            fileGroup.Add(MakeToolbarButton("開く", () => ShowFileModal(saveMode: false)));
            fileGroup.Add(MakeToolbarButton("保存", SaveChartToPath));

            var editGroup = uiRoot.Q<VisualElement>("toolbar-edit");
            editGroup.Clear();
            undoButton = MakeToolbarButton("元に戻す", Undo);
            redoButton = MakeToolbarButton("やり直す", Redo);
            editGroup.Add(undoButton);
            editGroup.Add(redoButton);

            // §2.2: ノーツ種別は左パレットの縦積みではなくツールバーへ横並びにする
            var toolGroup = uiRoot.Q<VisualElement>("toolbar-tools");
            toolGroup.Clear();
            toolButtons.Clear();
            foreach (var (tool, label, modifier) in ToolButtons)
            {
                var btn = MakeToolbarButton(label, () => SelectTool(tool));
                btn.AddToClassList("tool-btn");
                btn.AddToClassList("tool-btn--" + modifier);
                toolGroup.Add(btn);
                toolButtons[tool] = btn;
            }

            var widthGroup = uiRoot.Q<VisualElement>("toolbar-width");
            widthGroup.Clear();
            var widthLabel = new Label("幅");
            widthLabel.AddToClassList("field-label");
            widthField = new FloatField { value = defaultWidthCells };
            widthField.AddToClassList("tb-field");
            widthField.RegisterValueChangedCallback(evt => defaultWidthCells = Mathf.Max(0.5f, evt.newValue));
            widthGroup.Add(widthLabel);
            widthGroup.Add(widthField);

            toolbarStatus = uiRoot.Q<Label>("toolbar-status");
            toolbarDirty = uiRoot.Q<Label>("toolbar-dirty");
        }

        private static Button MakeToolbarButton(string label, Action onClick)
        {
            var btn = new Button(onClick) { text = label };
            btn.AddToClassList("tb-btn");
            return btn;
        }

        private void SelectTool(EditorTool tool)
        {
            currentTool = tool;
            pendingSlideStart = null;
            // editor-ui-rework-r4.md §6: イベントレーンを畳んだままEventツールを選ぶと
            // 何もできず原因が分からないため、自動で表示に戻す。
            if (tool == EditorTool.Event && !showEventLane)
            {
                showEventLane = true;
                if (viewEventLane != null) viewEventLane.SetValueWithoutNotify(true);
            }
            foreach (var kv in toolButtons)
                kv.Value.EnableInClassList("tool-btn--selected", kv.Key == tool);
        }

        // ================= B: メインキャンバスのタブ(§2.3) =================

        /// <summary>
        /// 標準の TabView は使わない。中身のcontent-containerが確実に伸びる保証をコードから
        /// 検証できず(内部USSクラスに依存する作りで、実機で「中身が一切描画されない」不具合の
        /// 原因になった)、タブ切替という単純な要件に対してリスクが見合わないため、
        /// ボタン2つ + display切替の自前実装にしている。サイズは全て自分で持つVisualElementの
        /// flex-growだけで決まるので、挙動が読める。
        /// </summary>
        private void BuildTabs()
        {
            var host = uiRoot.Q<VisualElement>("tabs-host");
            host.Clear();
            host.style.flexDirection = FlexDirection.Column;

            var header = new VisualElement();
            header.AddToClassList("tab-header");
            timelineTabButton = new Button(() => SelectTab(TabTimeline)) { text = "タイムライン" };
            previewTabButton = new Button(() => SelectTab(TabPreview)) { text = "譜面プレビュー" };
            timelineTabButton.AddToClassList("tab-header-btn");
            previewTabButton.AddToClassList("tab-header-btn");
            header.Add(timelineTabButton);
            header.Add(previewTabButton);
            host.Add(header);

            var content = new VisualElement();
            content.AddToClassList("canvas-host");
            host.Add(content);

            // ランタイムパネルでは IMGUIContainer が使えない（"IMGUIContainer cannot be used in a
            // runtime panel"）ため、editor-ui-redesign.md §6 が「後から」としていた
            // generateVisualContent + painter2D 方式を最初から採る。
            notesSheet = new VisualElement { focusable = true };
            notesSheet.AddToClassList("notes-sheet");
            notesSheet.generateVisualContent += GenerateNotesSheet;
            notesSheet.RegisterCallback<PointerDownEvent>(OnSheetPointerDown);
            notesSheet.RegisterCallback<PointerMoveEvent>(OnSheetPointerMove);
            notesSheet.RegisterCallback<PointerUpEvent>(OnSheetPointerUp);
            notesSheet.RegisterCallback<PointerLeaveEvent>(OnSheetPointerLeave);
            notesSheet.RegisterCallback<WheelEvent>(OnSheetWheel);
            // editor-ui-rework-r5.md §5.2: キー入力の受け口はuiRoot側の1本(OnGlobalKeyDown)へ
            // 統合したため、notesSheet固有のKeyDownEventハンドラは無くなった（BuildUIExtras参照）。

            previewSurface = new VisualElement();
            previewSurface.AddToClassList("preview-surface");
            previewSurface.RegisterCallback<GeometryChangedEvent>(_ => UpdatePreviewTexture());
            // editor-ui-rework-r3.md §6: 判定線をRenderTexture(3D)の上にPainter2Dで重ねて描く。
            previewSurface.generateVisualContent += GeneratePreviewOverlay;
            // editor-ui-rework-r8.md §5: タイムラインと同じホイール操作を流用する。通常ホイールは
            // OnSheetWheel（scrollTick経由でプレビューも追従する既存配線）、Cmd/Ctrl+ホイールだけ
            // ここで横取りしてハイスピードに割り当てる（タイムライン側はズームに使っているため）。
            previewSurface.RegisterCallback<WheelEvent>(OnPreviewWheel);

            hiSpeedLabel = new Label("");
            hiSpeedLabel.AddToClassList("hi-speed-label");
            hiSpeedLabel.pickingMode = PickingMode.Ignore;
            previewSurface.Add(hiSpeedLabel);

            content.Add(notesSheet);
            content.Add(previewSurface);
            SelectTab(TabTimeline);
        }

        private void SelectTab(int index)
        {
            selectedTabIndex = index;
            notesSheet.style.display = index == TabTimeline ? DisplayStyle.Flex : DisplayStyle.None;
            previewSurface.style.display = index == TabPreview ? DisplayStyle.Flex : DisplayStyle.None;
            timelineTabButton.EnableInClassList("tab-header-btn--selected", index == TabTimeline);
            previewTabButton.EnableInClassList("tab-header-btn--selected", index == TabPreview);
        }

        /// <summary>
        /// painter2D では文字が描けないので、Ground/Sky の見出しと小節番号だけは絶対配置の
        /// Label要素として重ねる。要素の追加/削除を毎フレームやらずに済むよう使い回す
        /// （generateVisualContent の中で子要素をいじるのは不可なので、ここは描画とは別のタイミング）。
        /// </summary>
        private void UpdateSheetLabels()
        {
            var L = CurrentSheetLayout();
            if (L.rect.width < 2f || L.rect.height < 2f) return;

            int used = 0;

            PlaceSheetLabel(ref used, "Ground", L.ground.x + 4f, L.rect.y + 2f);
            PlaceSheetLabel(ref used, "Sky", L.sky.x + 4f, L.rect.y + 2f);

            // §7.5 高さレーン。editor-ui-rework-r5.md §8: 幅は常に予約されるため、
            // 見出しの表示可否はshowHeightLaneを直接見る。
            if (showHeightLane)
                PlaceSheetLabel(ref used, "高さ G→S", L.heightLane.x + 4f, L.rect.y + 2f);

            // editor-ui-rework-r5.md §8: 同上、showEventLaneを直接見る。
            if (showEventLane)
            {
                var (bpmCol, meterCol, scrollCol) = EventColumns(L.rightMargin);
                PlaceSheetLabel(ref used, "BPM", bpmCol.x + 2f, L.rect.y + 2f);
                PlaceSheetLabel(ref used, "拍子", meterCol.x + 2f, L.rect.y + 2f);
                PlaceSheetLabel(ref used, "速度", scrollCol.x + 2f, L.rect.y + 2f);
            }

            int snapTicks = SnapTicks;
            int barTicks = Mathf.Max(snapTicks, SongAddr.TicksPerBar(MeterAtBar(SongAddr.ToAddr(song.meters, scrollTick).bar)));
            int start = Mathf.Max(0, L.BottomTick - barTicks) / barTicks * barTicks;
            int end = L.TopTick + barTicks;

            for (int t = start; t <= end && used < MaxSheetLabels; t += barTicks)
            {
                var addr = SongAddr.ToAddr(song.meters, t);
                if (addr.beat != 1 || addr.tick != 0) continue;
                float y = L.TickToY(t);
                if (y < L.rect.y - 4f || y > L.rect.yMax + 4f) continue;
                // editor-ui-rework-r5.md §8: 左余白の幅はウィンドウ幅次第で変わる（中央揃えの
                // オフセットを含むため）ので、左詰めではなくレーンの左端(ground.xMin)に
                // 右詰めで貼り付ける。
                PlaceSheetLabelRight(ref used, addr.bar.ToString(), L.leftMargin.xMax - 4f, y - 7f);
            }

            for (int i = used; i < sheetLabels.Count; i++)
                sheetLabels[i].style.display = DisplayStyle.None;
        }

        private Label AcquireSheetLabel(ref int used)
        {
            if (used >= MaxSheetLabels) return null;
            while (sheetLabels.Count <= used)
            {
                var created = new Label();
                created.AddToClassList("sheet-label");
                created.style.position = Position.Absolute;
                created.pickingMode = PickingMode.Ignore;
                notesSheet.Add(created);
                sheetLabels.Add(created);
            }

            var lbl = sheetLabels[used++];
            lbl.style.display = DisplayStyle.Flex;
            return lbl;
        }

        private void PlaceSheetLabel(ref int used, string text, float x, float y)
        {
            var lbl = AcquireSheetLabel(ref used);
            if (lbl == null) return;
            if (lbl.text != text) lbl.text = text;
            lbl.style.left = x;
            lbl.style.right = StyleKeyword.Auto;
            lbl.style.top = y;
        }

        /// <summary>editor-ui-rework-r5.md §8.3: 右詰め版。左詰めのPlaceSheetLabelと同じプールを
        /// 使い回すため、leftとrightの両方を毎回明示的に設定し直す（前フレーム/前用途で
        /// 反対側のスタイルが残っていると、両方指定された状態でレイアウトが崩れるため）。</summary>
        private void PlaceSheetLabelRight(ref int used, string text, float rightEdgeX, float y)
        {
            var lbl = AcquireSheetLabel(ref used);
            if (lbl == null) return;
            if (lbl.text != text) lbl.text = text;
            lbl.style.left = StyleKeyword.Auto;
            lbl.style.right = Mathf.Max(0f, notesSheet.contentRect.width - rightEdgeX);
            lbl.style.top = y;
        }

        /// <summary>
        /// §7.3 イベントレーン本体。右余白(rightMargin)をBPM/拍子/ソフランの3列に分け、
        /// 各イベントをクリック可能なチップとして描く。painter2Dでは文字もクリックも扱えないため、
        /// sheetLabelsと同じプール方式のLabel要素を使うが、こちらは pickingMode を有効にして
        /// クリック選択を受ける（PointerDownで evt.StopPropagation() し、notesSheet側の
        /// 「空白クリックで新規追加」処理に流れないようにする）。
        /// </summary>
        private void UpdateEventChips()
        {
            var L = CurrentSheetLayout();
            if (L.rect.width < 2f || L.rect.height < 2f) return;
            // editor-ui-rework-r4.md §5: イベントレーンを畳んでいる間はチップを1つも出さない。
            // editor-ui-rework-r5.md §8: 幅は常に予約されるので、判定はshowEventLaneを直接見る。
            if (!showEventLane)
            {
                for (int i = 0; i < eventChipLabels.Count; i++) eventChipLabels[i].style.display = DisplayStyle.None;
                return;
            }

            var (bpmCol, meterCol, scrollCol) = EventColumns(L.rightMargin);
            int used = 0;

            for (int i = 0; i < song.bpmEvents.Count; i++)
            {
                var ev = song.bpmEvents[i];
                float y = L.TickToY(ev.tick);
                if (y < L.rect.y - 8f || y > L.rect.yMax + 8f) continue;
                PlaceEventChip(ref used, EventKind.Bpm, i, $"{ev.bpm:0.##}", $"{ev.bpm:0.##} BPM", bpmCol, y);
            }

            for (int i = 0; i < song.meters.Count; i++)
            {
                var m = song.meters[i];
                int tick = SongAddr.ToTick(song.meters, m.bar, 1, 0);
                float y = L.TickToY(tick);
                if (y < L.rect.y - 8f || y > L.rect.yMax + 8f) continue;
                PlaceEventChip(ref used, EventKind.Meter, i, $"{m.numerator}/{m.denominator}", $"{m.numerator}/{m.denominator}（{m.bar}小節目〜）", meterCol, y);
            }

            for (int i = 0; i < chart.scrollEvents.Count; i++)
            {
                var ev = chart.scrollEvents[i];
                float y = L.TickToY(ev.tick);
                if (y < L.rect.y - 8f || y > L.rect.yMax + 8f) continue;
                PlaceEventChip(ref used, EventKind.Scroll, i, $"{ev.mul:0.##}x",
                    $"群{ev.group} {ev.mul:0.##}x {ev.easing}", scrollCol, y);
            }

            for (int i = used; i < eventChipLabels.Count; i++)
                eventChipLabels[i].style.display = DisplayStyle.None;
        }

        private void PlaceEventChip(ref int used, EventKind kind, int index, string text, string tooltip, Rect col, float y)
        {
            if (used >= MaxEventChips) return;
            while (eventChipLabels.Count <= used)
            {
                var created = new Label();
                created.AddToClassList("event-chip");
                created.style.position = Position.Absolute;
                created.userData = new EventChipRef();
                created.RegisterCallback<PointerDownEvent>(evt =>
                {
                    var cr = (EventChipRef)created.userData;
                    SelectEvent(cr.kind, cr.index);
                    evt.StopPropagation();
                });
                notesSheet.Add(created);
                eventChipLabels.Add(created);
            }

            var lbl = eventChipLabels[used++];
            var chipRef = (EventChipRef)lbl.userData;
            chipRef.kind = kind;
            chipRef.index = index;

            lbl.style.display = DisplayStyle.Flex;
            if (lbl.text != text) lbl.text = text;
            lbl.tooltip = tooltip;
            lbl.style.left = col.x + 2f;
            lbl.style.width = col.width - 4f;
            lbl.style.top = y - 8f;

            lbl.RemoveFromClassList("event-chip--bpm");
            lbl.RemoveFromClassList("event-chip--meter");
            lbl.RemoveFromClassList("event-chip--scroll");
            lbl.AddToClassList(kind switch
            {
                EventKind.Bpm => "event-chip--bpm",
                EventKind.Meter => "event-chip--meter",
                _ => "event-chip--scroll",
            });

            lbl.EnableInClassList("event-chip--selected", selectedEventKind == kind && selectedEventIndex == index);
        }

        private void UpdatePreviewTexture()
        {
            var r = previewSurface.contentRect;
            if (r.width < 2f || r.height < 2f) return;
            var tex = preview.EnsureRenderTexture(Mathf.RoundToInt(r.width), Mathf.RoundToInt(r.height));
            previewSurface.style.backgroundImage = Background.FromRenderTexture(tex);
        }

        /// <summary>
        /// editor-ui-rework-r3.md §6: プレビューへの判定線描画。判定線はNDC(u,v)だけで決まる
        /// スクリーン空間の量なので3D投影の再計算は不要（Overlay/StageOverlay.DrawBandの
        /// 判定線描画と同じ式）。RenderTextureはcontentRectちょうどのサイズで引き伸ばし無しに
        /// 貼られる（UpdatePreviewTexture）ため、(u,v)→ローカル座標の写像だけで画素が一致する。
        /// StageOverlay自体はScreen.width/height基準でオフスクリーンRenderTextureには使えないため
        /// 流用せず、ここで簡略版を描く。
        /// </summary>
        private void GeneratePreviewOverlay(MeshGenerationContext mgc)
        {
            var r = previewSurface.contentRect;
            if (r.width < 2f || r.height < 2f) return;
            var cfg = preview.Config;
            var p = mgc.painter2D;

            float PxX(float u) => (u + 1f) / 2f * r.width;
            // UI Toolkitはy下向き（StageOverlay.PxYのy-upと符号が逆）。
            float PxY(float v) => (1f - v) / 2f * r.height;

            void JudgeLine(float vJudge, uint colorHex)
            {
                float y = PxY(vJudge);
                var col = StageGeometry.ColorFromHex(colorHex, 0.9f);
                float x0 = PxX(-cfg.U), x1 = PxX(cfg.U);
                FillRect(p, new Rect(Mathf.Min(x0, x1), y - 1f, Mathf.Abs(x1 - x0), 2f), col);
            }

            JudgeLine(cfg.vGroundJudge, StageColors.Ground);
            JudgeLine(cfg.vSkyJudge, StageColors.Sky);
        }

        // ================= D: 右パネル(§2.5) =================

        private void BuildRightPanel()
        {
            var infoHost = uiRoot.Q<VisualElement>("info-host");
            infoHost.Clear();
            infoTitle = AddTextRow(infoHost, "タイトル", v => { song.title = v; songMetaDirty = true; });
            infoArtist = AddTextRow(infoHost, "アーティスト", v => { song.artist = v; songMetaDirty = true; });
            infoJacket = AddTextRow(infoHost, "ジャケット", v => { song.jacket = v; songMetaDirty = true; });
            infoDifficulty = AddTextRow(infoHost, "難易度", v => { header.difficulty = v; dirty = true; });
            infoLevel = AddIntRow(infoHost, "レベル", v => { header.level = v; dirty = true; });
            infoCharter = AddTextRow(infoHost, "譜面制作者", v => { header.charter = v; dirty = true; });

            var audioHost = uiRoot.Q<VisualElement>("audio-host");
            audioHost.Clear();
            audioFile = AddTextRow(audioHost, "音源ファイル", v => { song.audio = v; songMetaDirty = true; MarkPreviewDirty(); });
            var audioPickBtn = new Button(PickAudioFile) { text = "…" };
            audioPickBtn.AddToClassList("tb-btn");
            audioFile.parent.Add(audioPickBtn);
            audioOffset = AddFloatRow(audioHost, "オフセット(秒)", v => { song.offsetSec = v; songMetaDirty = true; MarkPreviewDirty(); });
            var note = new Label("タイトル〜オフセットは song.museproj の内容です。保存時に譜面と一緒に書き戻します。");
            note.AddToClassList("prop-note");
            audioHost.Add(note);

            // editor-ui-rework-r6.md §4.1(d)(e): 読み込み結果の状態表示と再読み込みボタン。
            // 旧実装は失敗がDebug.LogWarningのみでUIに一切出ず、原因不明のまま「鳴らない」だけが見えていた。
            var statusRow = new VisualElement();
            statusRow.AddToClassList("prop-row");
            audioStatusLabel = new Label("");
            audioStatusLabel.AddToClassList("prop-note");
            statusRow.Add(audioStatusLabel);
            var reloadBtn = new Button(() => { preview.ForceReloadAudio(); MarkPreviewDirty(); }) { text = "再読み込み" };
            reloadBtn.AddToClassList("tb-btn");
            statusRow.Add(reloadBtn);
            // editor-ui-rework-r7.md §3.2: 曲フォルダがどこにあるか分からない、への直接対策。
            var openFolderBtn = new Button(() =>
            {
                string dir = string.IsNullOrEmpty(songPath) ? null : Path.GetDirectoryName(songPath);
                if (dir == null) { statusMessage = "先に譜面を保存してください（曲フォルダが決まっていません）"; return; }
                OpenInFinder(dir);
            })
            { text = "曲フォルダをFinderで開く" };
            openFolderBtn.AddToClassList("tb-btn");
            statusRow.Add(openFolderBtn);
            audioHost.Add(statusRow);

            // editor-ui-rework-r6.md §4.3: 全体/BGM/SEの3段音量。song.museprojではなくEditorSettings
            // (preview.MasterVolume等)に永続化する(音量は譜面の属性ではないため)。
            masterVolumeSlider = AddSliderRow(audioHost, "全体音量", 0f, 1f, v => preview.MasterVolume = v);
            bgmVolumeSlider = AddSliderRow(audioHost, "BGM音量", 0f, 1f, v => preview.BgmVolume = v);
            seVolumeSlider = AddSliderRow(audioHost, "SE音量", 0f, 1f, v => preview.SeVolume = v);

            // editor-ui-rework-r5.md §4.1: 「再生に追従」「ページ送りスクロール」「判定線位置」は
            // 設定モーダルのタイムラインタブへ移設した。ここに残すのは再生パラメータ（再生速度）・
            // 操作（小節へ移動）・高さ/イベントレーン（依頼で明示的に除外された2項目）。
            var viewHost = uiRoot.Q<VisualElement>("view-host");
            viewHost.Clear();
            viewRate = AddSliderRow(viewHost, "再生速度", 0.25f, 2f, v => preview.Rate = v);
            // §7.5 高さレーン。既定は折りたたみ（幅0）で、必要なときだけ開く。
            viewHeightLane = AddToggleRow(viewHost, "高さレーン", v => showHeightLane = v);
            var heightNote = new Label("高さレーンには選択中のノーツの中継点だけを表示します（点を横にドラッグで層を編集）。");
            heightNote.AddToClassList("prop-note");
            viewHost.Add(heightNote);
            viewEventLane = AddToggleRow(viewHost, "イベントレーン", v => showEventLane = v);

            // MikuMikuWorld移植候補: 小節ジャンプ（ScoreEditor.cpp:409-416のgotoMeasure）。
            var jumpRow = new VisualElement();
            jumpRow.AddToClassList("prop-row");
            var jumpLabel = new Label("小節へ移動");
            jumpLabel.AddToClassList("field-label");
            var jumpField = new IntegerField { value = 0 };
            var jumpBtn = new Button(() => GotoMeasure(jumpField.value)) { text = "移動" };
            jumpBtn.AddToClassList("tb-btn");
            jumpRow.Add(jumpLabel);
            jumpRow.Add(jumpField);
            jumpRow.Add(jumpBtn);
            viewHost.Add(jumpRow);

            var openSettingsBtn = new Button(ShowSettingsModal) { text = "設定を開く..." };
            openSettingsBtn.AddToClassList("tb-btn");
            viewHost.Add(openSettingsBtn);

            statsText = uiRoot.Q<Label>("stats-text");

            presetHost = uiRoot.Q<VisualElement>("preset-host");
            RebuildPresetList();

            foldInspector = uiRoot.Q<Foldout>("fold-inspector");
            inspectorHost = uiRoot.Q<VisualElement>("inspector-host");
            RebuildInspector();

            foldValidation = uiRoot.Q<Foldout>("fold-validation");
            validationHost = uiRoot.Q<VisualElement>("validation-host");
            validationHost.Clear();
            validateOnSaveToggle = AddToggleRow(validationHost, "保存時に自動実行", v => validateOnSave = v);
            validateOnSaveToggle.SetValueWithoutNotify(validateOnSave);
        }

        /// <summary>
        /// MikuMikuWorld移植候補: パターンプリセット（PresetManager.h相当）。今回の実装は
        /// アプリ実行中のみ保持するメモリ内リスト（ディスク永続化は未実装、次回増分候補）。
        /// </summary>
        private void RebuildPresetList()
        {
            if (presetHost == null) return;
            presetHost.Clear();

            var saveRow = new VisualElement();
            saveRow.AddToClassList("prop-row");
            presetNameField = new TextField { value = "" };
            var saveBtn = new Button(() => { SavePreset(presetNameField.value); RebuildPresetList(); }) { text = "選択を保存" };
            saveBtn.AddToClassList("tb-btn");
            saveRow.Add(presetNameField);
            saveRow.Add(saveBtn);
            presetHost.Add(saveRow);

            if (presets.Count == 0)
            {
                var empty = new Label("保存済みプリセットはありません。");
                empty.AddToClassList("prop-note");
                presetHost.Add(empty);
            }

            foreach (var preset in presets)
            {
                var row = new VisualElement();
                row.AddToClassList("prop-row");
                var label = new Label($"{preset.name} ({preset.notes.Count}件)");
                label.AddToClassList("field-label");
                var pasteBtn = new Button(() => PastePreset(preset)) { text = "貼り付け" };
                pasteBtn.AddToClassList("tb-btn");
                var deleteBtn = new Button(() => { DeletePreset(preset); RebuildPresetList(); }) { text = "削除" };
                deleteBtn.AddToClassList("tb-btn");
                row.Add(label);
                row.Add(pasteBtn);
                row.Add(deleteBtn);
                presetHost.Add(row);
            }

            var note = new Label("プリセットはアプリを再起動すると消えます（このセッション限定）。");
            note.AddToClassList("prop-note");
            presetHost.Add(note);
        }

        private VisualElement MakePropRow(VisualElement parent, string label, VisualElement field)
        {
            var row = new VisualElement();
            row.AddToClassList("prop-row");
            var lbl = new Label(label);
            lbl.AddToClassList("prop-label");
            field.AddToClassList("prop-value");
            row.Add(lbl);
            row.Add(field);
            parent.Add(row);
            return row;
        }

        private TextField AddTextRow(VisualElement parent, string label, Action<string> onChange)
        {
            var f = new TextField();
            f.RegisterValueChangedCallback(evt => { if (!suppressUiCallbacks) onChange(evt.newValue); });
            MakePropRow(parent, label, f);
            return f;
        }

        private IntegerField AddIntRow(VisualElement parent, string label, Action<int> onChange)
        {
            var f = new IntegerField();
            f.RegisterValueChangedCallback(evt => { if (!suppressUiCallbacks) onChange(evt.newValue); });
            MakePropRow(parent, label, f);
            return f;
        }

        private FloatField AddFloatRow(VisualElement parent, string label, Action<float> onChange)
        {
            var f = new FloatField();
            f.RegisterValueChangedCallback(evt => { if (!suppressUiCallbacks) onChange(evt.newValue); });
            MakePropRow(parent, label, f);
            return f;
        }

        private Toggle AddToggleRow(VisualElement parent, string label, Action<bool> onChange)
        {
            var f = new Toggle();
            f.RegisterValueChangedCallback(evt => { if (!suppressUiCallbacks) onChange(evt.newValue); });
            MakePropRow(parent, label, f);
            return f;
        }

        private Slider AddSliderRow(VisualElement parent, string label, float min, float max, Action<float> onChange)
        {
            var f = new Slider(min, max) { showInputField = true };
            f.RegisterValueChangedCallback(evt => { if (!suppressUiCallbacks) onChange(evt.newValue); });
            MakePropRow(parent, label, f);
            return f;
        }

        // ================= インスペクタ(§2.5) =================

        /// <summary>
        /// 選択中の1点だけを編集するUI（editor-ui-rework-r2.md §3）。
        /// 旧実装はSlideの全waypointをFoldoutで並べており、中継点が増えるとインスペクタが
        /// 圧迫された。選択の粒度は既にNoteRef(点単位)なので、Foldoutで包む代わりに
        /// 「選択中の1点」だけをフラットに表示する。単一選択はその点、複数選択は代表点
        /// （SelectionRepresentative）を対象にする。
        /// </summary>
        private void RebuildInspector()
        {
            inspectorHost.Clear();
            inspectorRefreshers.Clear();

            if (selectedEventKind != EventKind.None)
            {
                RebuildEventInspector();
                return;
            }

            var rep = SelectionRepresentative();
            if (rep == null)
            {
                var empty = new Label("ノーツを選択してください");
                empty.AddToClassList("prop-note");
                inspectorHost.Add(empty);
                return;
            }

            var (note, index) = (rep.Value.note, rep.Value.index);

            if (selection.Count > 1)
            {
                var countLabel = new Label($"{selection.Count}件選択 — 代表点（種別: {note.kind}）を編集中");
                countLabel.AddToClassList("prop-note");
                inspectorHost.Add(countLabel);

                var deleteAllBtn = new Button(DeleteSelection) { text = $"選択した{selection.Count}件を削除" };
                deleteAllBtn.AddToClassList("tb-btn");
                inspectorHost.Add(deleteAllBtn);

                bool allSinglePoint = selection.TrueForAll(r => r.note.points.Count == 1);
                if (allSinglePoint)
                {
                    var kindDropdown = new DropdownField { choices = new List<string> { "Tap", "Ex Tap", "Flick" }, index = 0 };
                    kindDropdown.RegisterValueChangedCallback(evt =>
                    {
                        if (suppressUiCallbacks) return;
                        NoteKind newKind = evt.newValue switch
                        {
                            "Tap" => NoteKind.Tap,
                            "Ex Tap" => NoteKind.ExTap,
                            _ => NoteKind.Flick,
                        };
                        PushUndo(coalesce: false);
                        foreach (var r in selection) r.note.kind = newKind;
                        dirty = true;
                    });
                    MakePropRow(inspectorHost, "種別に一括変更", kindDropdown);
                }
                else
                {
                    var note2 = new Label("種別の一括変更はTap/Ex Tap/Flickのみ対応（Slideを含む選択では非表示）");
                    note2.AddToClassList("prop-note");
                    inspectorHost.Add(note2);
                }

                inspectorHost.Add(new VisualElement { style = { height = 1, marginTop = 4, marginBottom = 4, backgroundColor = new Color(1, 1, 1, 0.15f) } });
            }
            else
            {
                var kindRow = new Label($"種別: {note.kind}");
                kindRow.AddToClassList("prop-note");
                inspectorHost.Add(kindRow);

                var groupField = AddIntRow(inspectorHost, "スクロール群", v =>
                {
                    PushUndo(coalesce: true);
                    note.scrollGroup = Mathf.Max(0, v);
                    dirty = true;
                });
                groupField.SetValueWithoutNotify(note.scrollGroup);
                inspectorRefreshers.Add(() => RefreshIfUnfocused(groupField, note.scrollGroup));
            }

            string role = note.points.Count == 1 ? ""
                : index == 0 ? "（始点）"
                : index == note.points.Count - 1 ? "（終点）" : "（中継点）";
            var pointRow = new Label($"点 {index + 1}/{note.points.Count}{role}");
            pointRow.AddToClassList("prop-note");
            inspectorHost.Add(pointRow);

            BuildWaypointRows(inspectorHost, note, index);

            if (note.points.Count > 1 && index != 0 && index != note.points.Count - 1)
            {
                var removeBtn = new Button(() =>
                {
                    PushUndo(coalesce: false, "中継点を削除");
                    note.points.RemoveAt(index);
                    dirty = true;
                })
                { text = "この中継点を削除" };
                removeBtn.AddToClassList("tb-btn");
                inspectorHost.Add(removeBtn);
            }

            if (selection.Count == 1)
            {
                var deleteBtn = new Button(DeleteSelection) { text = "このノーツを削除" };
                deleteBtn.AddToClassList("tb-btn");
                inspectorHost.Add(deleteBtn);
            }
        }

        /// <summary>editor-ui-rework-r2.md §3: 1点分の編集行（位置/layerF/cellF/width/easing/marker/
        /// comboStep）を組む。単一選択・複数選択時の代表点表示の両方から呼ぶ共通部分。</summary>
        private void BuildWaypointRows(VisualElement host, Note note, int index)
        {
            var addrField = AddTextRow(host, "位置", v =>
            {
                try
                {
                    var parsed = SongAddr.ParseAddr(v);
                    int newTick = SongAddr.ToTick(song.meters, parsed.bar, parsed.beat, parsed.tick);
                    var w = note.points[index];
                    if (w.tick == newTick) return;
                    PushUndo(coalesce: true);
                    w.tick = newTick;
                    note.points[index] = w;
                    dirty = true;
                }
                catch (FormatException)
                {
                    // 入力途中の無効な文字列は無視する（フォーカスが外れれば値で上書きされる）
                }
            });
            inspectorRefreshers.Add(() =>
                RefreshIfUnfocused(addrField, SongAddr.FormatAddr(SongAddr.ToAddr(song.meters, note.points[index].tick))));

            var layerField = AddSliderRow(host, "layerF", 0f, 1f, v =>
                MutateWaypoint(note, index, w => { w.layerF = Mathf.Clamp01(v); return w; }));
            inspectorRefreshers.Add(() => RefreshIfUnfocused(layerField, note.points[index].layerF));

            var cellField = AddFloatRow(host, "cellF", v =>
                MutateWaypoint(note, index, w => { w.cellF = v; return w; }));
            inspectorRefreshers.Add(() => RefreshIfUnfocused(cellField, note.points[index].cellF));

            var widthField = AddFloatRow(host, "width", v =>
                MutateWaypoint(note, index, w => { w.width = Mathf.Max(0.1f, v); return w; }));
            inspectorRefreshers.Add(() => RefreshIfUnfocused(widthField, note.points[index].width));

            var easingField = AddEnumRow(host, "easing(横)", note.points[index].easing, (Easing v) =>
                MutateWaypoint(note, index, w => { w.easing = v; return w; }));
            inspectorRefreshers.Add(() => RefreshEnumIfUnfocused(easingField, note.points[index].easing));

            // editor-ui-rework-r2.md §6: 横(cellF/width)と高さ(layerF)のeasingは独立に設定できる。
            var easingHField = AddEnumRow(host, "easing(高さ)", note.points[index].easingH, (Easing v) =>
                MutateWaypoint(note, index, w => { w.easingH = v; return w; }));
            inspectorRefreshers.Add(() => RefreshEnumIfUnfocused(easingHField, note.points[index].easingH));

            var markerField = AddEnumRow(host, "marker", note.points[index].marker, (WaypointMarker v) =>
                MutateWaypoint(note, index, w => { w.marker = v; return w; }));
            inspectorRefreshers.Add(() => RefreshEnumIfUnfocused(markerField, note.points[index].marker));

            var comboToggle = AddToggleRow(host, "comboStep上書き", v =>
                MutateWaypoint(note, index, w => { w.comboStep = v ? w.comboStep ?? 0 : null; return w; }));
            comboToggle.SetValueWithoutNotify(note.points[index].comboStep.HasValue);
            inspectorRefreshers.Add(() => comboToggle.SetValueWithoutNotify(note.points[index].comboStep.HasValue));

            var comboField = AddIntRow(host, "comboStep(tick)", v =>
                MutateWaypoint(note, index, w => { w.comboStep = v; return w; }));
            inspectorRefreshers.Add(() =>
            {
                var wp = note.points[index];
                comboField.parent.style.display = wp.comboStep.HasValue ? DisplayStyle.Flex : DisplayStyle.None;
                RefreshIfUnfocused(comboField, wp.comboStep ?? 0);
            });
        }

        /// <summary>
        /// editor-ui-rework-r2.md §3.2: 複数選択時の代表点を段階的な絞り込みで決める。
        /// 1) 始点(index==0)があれば絞る 2) 無ければ終点(index==最終)で絞る
        /// 3) 残った候補のうちcellFが最小(左)のもの 4) 同値ならtickが最小(早い)のもの。
        /// 単一選択時はそのまま選択中の1点を返す。
        /// </summary>
        private NoteRef? SelectionRepresentative()
        {
            if (selection.Count == 0) return null;
            if (selection.Count == 1) return selection[0];

            var candidates = selection.FindAll(r => r.index == 0);
            if (candidates.Count == 0) candidates = selection.FindAll(r => r.index == r.note.points.Count - 1);
            if (candidates.Count == 0) candidates = new List<NoteRef>(selection);

            float minCell = float.MaxValue;
            foreach (var r in candidates) minCell = Mathf.Min(minCell, r.note.points[r.index].cellF);
            candidates = candidates.FindAll(r => Mathf.Approximately(r.note.points[r.index].cellF, minCell));

            var best = candidates[0];
            int bestTick = best.note.points[best.index].tick;
            for (int i = 1; i < candidates.Count; i++)
            {
                int t = candidates[i].note.points[candidates[i].index].tick;
                if (t < bestTick) { best = candidates[i]; bestTick = t; }
            }
            return best;
        }

        /// <summary>
        /// §7.3: イベントレーンで選択したBPM/拍子/ソフランイベントの編集UI。ノーツと違い
        /// song.bpmEvents/song.metersはUndoスナップショット(chart+headerのみ)の対象外なので、
        /// 既存のタイトル等song.museproj系フィールドと同じくPushUndoを呼ばない
        /// （chart.scrollEventsはchartの一部なので通常どおりPushUndoする）。
        /// </summary>
        private void RebuildEventInspector()
        {
            switch (selectedEventKind)
            {
                case EventKind.Bpm:
                {
                    if (selectedEventIndex < 0 || selectedEventIndex >= song.bpmEvents.Count)
                    {
                        selectedEventKind = EventKind.None;
                        return;
                    }

                    var kindRow = new Label("種別: BPM変化");
                    kindRow.AddToClassList("prop-note");
                    inspectorHost.Add(kindRow);

                    var addrField = AddTextRow(inspectorHost, "位置", v =>
                    {
                        try
                        {
                            var parsed = SongAddr.ParseAddr(v);
                            int newTick = SongAddr.ToTick(song.meters, parsed.bar, parsed.beat, parsed.tick);
                            var e = song.bpmEvents[selectedEventIndex];
                            if (e.tick == newTick) return;
                            e.tick = newTick;
                            song.bpmEvents[selectedEventIndex] = e;
                            songMetaDirty = true;
                            MarkPreviewDirty();
                        }
                        catch (FormatException) { /* 入力途中は無視 */ }
                    });
                    inspectorRefreshers.Add(() => RefreshIfUnfocused(addrField,
                        SongAddr.FormatAddr(SongAddr.ToAddr(song.meters, song.bpmEvents[selectedEventIndex].tick))));

                    var bpmField = AddFloatRow(inspectorHost, "BPM", v =>
                    {
                        var e = song.bpmEvents[selectedEventIndex];
                        e.bpm = Mathf.Max(1f, v);
                        song.bpmEvents[selectedEventIndex] = e;
                        songMetaDirty = true;
                        MarkPreviewDirty();
                    });
                    inspectorRefreshers.Add(() => RefreshIfUnfocused(bpmField, song.bpmEvents[selectedEventIndex].bpm));

                    var delBtn = new Button(DeleteSelectedEvent) { text = "このBPMイベントを削除" };
                    delBtn.AddToClassList("tb-btn");
                    inspectorHost.Add(delBtn);
                    break;
                }
                case EventKind.Meter:
                {
                    if (selectedEventIndex < 0 || selectedEventIndex >= song.meters.Count)
                    {
                        selectedEventKind = EventKind.None;
                        return;
                    }

                    var kindRow = new Label("種別: 拍子変化");
                    kindRow.AddToClassList("prop-note");
                    inspectorHost.Add(kindRow);

                    var barField = AddIntRow(inspectorHost, "小節番号", v =>
                    {
                        // editor-ui-rework-r4.md §10: barは0始まり（アウフタクト用に0小節目を許可）。
                        var m = song.meters[selectedEventIndex];
                        m.bar = Mathf.Max(0, v);
                        song.meters[selectedEventIndex] = m;
                        songMetaDirty = true;
                        MarkPreviewDirty();
                    });
                    inspectorRefreshers.Add(() => RefreshIfUnfocused(barField, song.meters[selectedEventIndex].bar));

                    var numField = AddIntRow(inspectorHost, "分子", v =>
                    {
                        var m = song.meters[selectedEventIndex];
                        m.numerator = Mathf.Max(1, v);
                        song.meters[selectedEventIndex] = m;
                        songMetaDirty = true;
                        MarkPreviewDirty();
                    });
                    inspectorRefreshers.Add(() => RefreshIfUnfocused(numField, song.meters[selectedEventIndex].numerator));

                    var denField = AddIntRow(inspectorHost, "分母", v =>
                    {
                        var m = song.meters[selectedEventIndex];
                        m.denominator = Mathf.Max(1, v);
                        song.meters[selectedEventIndex] = m;
                        songMetaDirty = true;
                        MarkPreviewDirty();
                    });
                    inspectorRefreshers.Add(() => RefreshIfUnfocused(denField, song.meters[selectedEventIndex].denominator));

                    var delBtn = new Button(DeleteSelectedEvent) { text = "この拍子イベントを削除" };
                    delBtn.AddToClassList("tb-btn");
                    inspectorHost.Add(delBtn);
                    break;
                }
                case EventKind.Scroll:
                {
                    if (selectedEventIndex < 0 || selectedEventIndex >= chart.scrollEvents.Count)
                    {
                        selectedEventKind = EventKind.None;
                        return;
                    }

                    var kindRow = new Label("種別: ソフラン");
                    kindRow.AddToClassList("prop-note");
                    inspectorHost.Add(kindRow);

                    var addrField = AddTextRow(inspectorHost, "位置", v =>
                    {
                        try
                        {
                            var parsed = SongAddr.ParseAddr(v);
                            int newTick = SongAddr.ToTick(song.meters, parsed.bar, parsed.beat, parsed.tick);
                            var e = chart.scrollEvents[selectedEventIndex];
                            if (e.tick == newTick) return;
                            PushUndo(coalesce: true);
                            e.tick = newTick;
                            chart.scrollEvents[selectedEventIndex] = e;
                            dirty = true;
                        }
                        catch (FormatException) { /* 入力途中は無視 */ }
                    });
                    inspectorRefreshers.Add(() => RefreshIfUnfocused(addrField,
                        SongAddr.FormatAddr(SongAddr.ToAddr(song.meters, chart.scrollEvents[selectedEventIndex].tick))));

                    var groupField = AddIntRow(inspectorHost, "群", v =>
                    {
                        PushUndo(coalesce: true);
                        var e = chart.scrollEvents[selectedEventIndex];
                        e.group = Mathf.Max(0, v);
                        chart.scrollEvents[selectedEventIndex] = e;
                        dirty = true;
                    });
                    inspectorRefreshers.Add(() => RefreshIfUnfocused(groupField, chart.scrollEvents[selectedEventIndex].group));

                    var mulField = AddFloatRow(inspectorHost, "倍率", v =>
                    {
                        PushUndo(coalesce: true);
                        var e = chart.scrollEvents[selectedEventIndex];
                        e.mul = v;
                        chart.scrollEvents[selectedEventIndex] = e;
                        dirty = true;
                    });
                    inspectorRefreshers.Add(() => RefreshIfUnfocused(mulField, chart.scrollEvents[selectedEventIndex].mul));

                    var easingField = AddEnumRow(inspectorHost, "easing", chart.scrollEvents[selectedEventIndex].easing, (Easing v) =>
                    {
                        PushUndo(coalesce: true);
                        var e = chart.scrollEvents[selectedEventIndex];
                        e.easing = v;
                        chart.scrollEvents[selectedEventIndex] = e;
                        dirty = true;
                    });
                    inspectorRefreshers.Add(() => RefreshEnumIfUnfocused(easingField, chart.scrollEvents[selectedEventIndex].easing));

                    var durField = AddIntRow(inspectorHost, "変化にかかるtick", v =>
                    {
                        PushUndo(coalesce: true);
                        var e = chart.scrollEvents[selectedEventIndex];
                        e.durationTicks = Mathf.Max(0, v);
                        chart.scrollEvents[selectedEventIndex] = e;
                        dirty = true;
                    });
                    inspectorRefreshers.Add(() => RefreshIfUnfocused(durField, chart.scrollEvents[selectedEventIndex].durationTicks));

                    var delBtn2 = new Button(DeleteSelectedEvent) { text = "このソフランイベントを削除" };
                    delBtn2.AddToClassList("tb-btn");
                    inspectorHost.Add(delBtn2);
                    break;
                }
            }
        }

        /// <summary>Waypointは構造体なのでリストへ書き戻す。編集はスライダー等から連続で来るのでUndoはまとめる。</summary>
        private void MutateWaypoint(Note note, int index, Func<Waypoint, Waypoint> mutate)
        {
            if (suppressUiCallbacks) return;
            PushUndo(coalesce: true);
            note.points[index] = mutate(note.points[index]);
            dirty = true;
        }

        private static void RefreshIfUnfocused<T>(BaseField<T> field, T value)
        {
            if (field.focusController?.focusedElement == field) return;
            if (!EqualityComparer<T>.Default.Equals(field.value, value)) field.SetValueWithoutNotify(value);
        }

        private static void RefreshEnumIfUnfocused<T>(DropdownField field, T value) where T : struct, Enum
        {
            if (field.focusController?.focusedElement == field) return;
            string name = value.ToString();
            if (field.value != name) field.SetValueWithoutNotify(name);
        }

        private DropdownField AddEnumRow<T>(VisualElement parent, string label, T value, Action<T> onChange)
            where T : struct, Enum
        {
            var names = Enum.GetNames(typeof(T)).ToList();
            var f = new DropdownField { choices = names, value = value.ToString() };
            f.RegisterValueChangedCallback(evt =>
            {
                if (suppressUiCallbacks) return;
                if (Enum.TryParse<T>(evt.newValue, out var parsed)) onChange(parsed);
            });
            MakePropRow(parent, label, f);
            return f;
        }

        // ================= C: ステータスバー / トランスポート(§2.6) =================

        private void BuildStatusBar()
        {
            var transport = uiRoot.Q<VisualElement>("status-transport");
            transport.Clear();
            // §3: |◀ は曲頭へ（カーソルも0へ）、■ は停止してカーソル位置へ戻る（0ではない）、
            // ▶| は末尾へ（カーソルも末尾へ）。▶(再生/一時停止)はTogglePlayFromCursorが
            // 「停止中は必ずcursorTickから再生を始める」を担う。
            // editor-ui-rework-r3.md §8: 停止中にpreview.Seekを呼ぶ箇所はscrollTick(判定線)も
            // 合わせる。合わせないとUpdate()の停止中同期(scrollTick→preview.Seek)と引っ張り合う。
            transport.Add(MakeTransportButton("|◀", GoToStart));
            transport.Add(MakeTransportButton("■", StopAtCursor));
            playButton = MakeTransportButton("▶", TogglePlayFromCursor);
            transport.Add(playButton);
            transport.Add(MakeTransportButton("▶|", GoToEnd));

            // §2.6 / ユーザー指摘9: スナップは8個のボタン横並びをやめてドロップダウンに畳む
            var snapGroup = uiRoot.Q<VisualElement>("status-snap");
            snapGroup.Clear();
            var snapLabel = new Label("スナップ");
            snapLabel.AddToClassList("field-label");
            snapDropdown = new DropdownField
            {
                choices = SnapDenominators.Select(d => $"1/{d} 音符").ToList(),
                index = snapIndex,
            };
            snapDropdown.RegisterValueChangedCallback(_ =>
            {
                if (!suppressUiCallbacks) snapIndex = Mathf.Clamp(snapDropdown.index, 0, SnapDenominators.Length - 1);
            });
            snapGroup.Add(snapLabel);
            snapGroup.Add(snapDropdown);

            var zoomGroup = uiRoot.Q<VisualElement>("status-zoom");
            zoomGroup.Clear();
            zoomGroup.Add(MakeTransportButton("−", () => SetZoom(pxPerBeat - 8f)));
            zoomSlider = new Slider(ZoomMinPxPerBeat, ZoomMaxPxPerBeat) { value = pxPerBeat };
            zoomSlider.AddToClassList("zoom-slider");
            zoomSlider.RegisterValueChangedCallback(evt => { if (!suppressUiCallbacks) SetZoom(evt.newValue); });
            zoomGroup.Add(zoomSlider);
            zoomGroup.Add(MakeTransportButton("+", () => SetZoom(pxPerBeat + 8f)));
            zoomLabel = new Label();
            zoomLabel.AddToClassList("mono");
            zoomGroup.Add(zoomLabel);

            var scrubGroup = uiRoot.Q<VisualElement>("status-scrub");
            scrubGroup.Clear();
            scrubSlider = new Slider(0f, 10f);
            scrubSlider.RegisterValueChangedCallback(evt =>
            {
                if (suppressUiCallbacks) return;
                if (Mathf.Abs(evt.newValue - preview.SongTime) > 0.05f) preview.Seek(evt.newValue);
                // §3: 停止中にスクラブバーで動かした位置も「再生開始位置」として扱う。
                // editor-ui-rework-r3.md §8: scrollTick(判定線)も合わせる（合わせないとUpdate()の
                // 停止中同期がスクラブ直後に古いscrollTickへ引き戻してしまう）。
                if (!preview.IsPlaying)
                {
                    int t = Mathf.Max(0, ChartFormat.SecondsToTick(chart.bpmEvents, evt.newValue));
                    cursorTick = t;
                    scrollTick = t;
                }
            });
            scrubGroup.Add(scrubSlider);

            statusTime = uiRoot.Q<Label>("status-time");
            statusChartInfo = uiRoot.Q<Label>("status-chartinfo");
            statusValidation = uiRoot.Q<Button>("status-validation");
            statusValidation.clicked += () =>
            {
                if (foldValidation != null) foldValidation.value = true;
            };
        }

        private static Button MakeTransportButton(string label, Action onClick)
        {
            var btn = new Button(onClick) { text = label };
            btn.AddToClassList("tb-btn");
            btn.AddToClassList("transport-btn");
            return btn;
        }

        private void SetZoom(float value)
        {
            pxPerBeat = Mathf.Clamp(value, ZoomMinPxPerBeat, ZoomMaxPxPerBeat);
        }

        // ================= 毎フレームの表示同期 =================

        /// <summary>モデル→UIの一方向同期。Updateから呼ぶ（編集はUI側のコールバックがモデルを直接書く）。</summary>
        private void SyncModelToUi()
        {
            if (uiRoot == null) return;

            bool slowTick = Time.unscaledTime - lastSlowRefreshRealtime >= UiSlowRefreshSec;
            if (slowTick) lastSlowRefreshRealtime = Time.unscaledTime;

            suppressUiCallbacks = true;
            try
            {
                SelectToolVisual();

                undoButton.SetEnabled(undoStack.Count > 0);
                redoButton.SetEnabled(redoStack.Count > 0);
                undoButton.tooltip = undoStack.Count > 0 ? $"元に戻す: {PeekUndoLabel()}" : "";
                redoButton.tooltip = redoStack.Count > 0 ? $"やり直す: {PeekRedoLabel()}" : "";
                bool unsaved = dirty || songMetaDirty;
                toolbarStatus.text = statusMessage;
                toolbarDirty.text = unsaved ? "● 未保存" : "保存済み";
                toolbarDirty.EnableInClassList("dirty-badge--dirty", unsaved);
                toolbarDirty.EnableInClassList("dirty-badge--clean", !unsaved);

                if (uiNeedsPropertyRefresh)
                {
                    uiNeedsPropertyRefresh = false;
                    infoTitle.SetValueWithoutNotify(song.title ?? "");
                    infoArtist.SetValueWithoutNotify(song.artist ?? "");
                    infoJacket.SetValueWithoutNotify(song.jacket ?? "");
                    infoDifficulty.SetValueWithoutNotify(header.difficulty ?? "");
                    infoLevel.SetValueWithoutNotify(header.level);
                    infoCharter.SetValueWithoutNotify(header.charter ?? "");
                    audioFile.SetValueWithoutNotify(song.audio ?? "");
                    audioOffset.SetValueWithoutNotify(song.offsetSec);
                    masterVolumeSlider.SetValueWithoutNotify(preview.MasterVolume);
                    bgmVolumeSlider.SetValueWithoutNotify(preview.BgmVolume);
                    seVolumeSlider.SetValueWithoutNotify(preview.SeVolume);
                }

                // editor-ui-rework-r6.md §4.1: 読み込みは非同期(コルーチン)で終わるタイミングが
                // 読めないため、他のスライダー類と同じく毎フレーム追従させる。
                audioStatusLabel.text = AudioLoadStatusText();

                // スライダー/トグル/ドロップダウンは入力途中という状態が無いので毎フレーム追従させる。
                // ノーツシート側のCtrl+ホイール(拡大縮小)もここで反映される。
                // editor-ui-rework-r6.md §1.5: ←→ショートカットでdefaultWidthCellsが変わるので、
                // ツールバーの「幅」欄もスライダー類と同じく毎フレーム追従させる。
                widthField.SetValueWithoutNotify(defaultWidthCells);
                viewRate.SetValueWithoutNotify(preview.Rate);
                viewHeightLane.SetValueWithoutNotify(showHeightLane);
                viewEventLane.SetValueWithoutNotify(showEventLane);
                if (snapDropdown.index != snapIndex) snapDropdown.index = snapIndex;
                zoomSlider.SetValueWithoutNotify(pxPerBeat);
                zoomLabel.text = $"{pxPerBeat / ZoomBasePxPerBeat:0.00}x";
                playButton.text = preview.IsPlaying ? "❙❙" : "▶";
                // editor-ui-rework-r8.md §5.2: プレビュー左上のハイスピード表示。
                hiSpeedLabel.text = $"HS {preview.HiSpeed:0.00}x";

                // editor-ui-rework-r8.md §4: 音源が読み込まれている間はシークバーの長さを
                // 「音源長+10秒」に固定する（旧実装は譜面の最終ノーツ基準だったため、譜面を編集する
                // たびに目盛りが伸び縮みしていた）。音源未読み込みの間は従来相当のフォールバックを使う。
                float audioLen = preview.AudioLengthSec;
                float scrubMax = audioLen > 0f
                    ? audioLen + 10f
                    : Mathf.Max(10f, preview.ChartEndSec + 10f);
                scrubSlider.lowValue = 0f;
                scrubSlider.highValue = scrubMax;
                scrubSlider.SetValueWithoutNotify(Mathf.Clamp(preview.SongTime, 0f, scrubMax));
                statusTime.text = FormatTime(preview.SongTime);

                // 拍子/BPM表示と統計は毎フレーム作り直すとメーター正規化とノーツ全走査で
                // 無駄なアロケーションが出るので、目で追える程度に間引く。
                if (slowTick)
                {
                    statusChartInfo.text = BuildChartInfoText();
                    statsText.text = BuildStatsText();
                }
            }
            finally
            {
                suppressUiCallbacks = false;
            }

            // プレビュータブが手前にある間だけ3D描画する（§2.2の負荷対策）
            bool previewActive = selectedTabIndex == TabPreview;
            if (previewActive)
            {
                if (!preview.RenderEnabled)
                {
                    preview.RenderEnabled = true;
                    UpdatePreviewTexture();
                }
            }
            else if (preview.RenderEnabled)
            {
                preview.DetachTexture();
            }

            if (previewActive)
            {
                previewSurface.MarkDirtyRepaint();
            }
            else
            {
                UpdateSheetLabels();
                UpdateEventChips();
                notesSheet.MarkDirtyRepaint();
            }

            // 選択（どの点を表示するか）が変わった / 中継点が増減したときだけインスペクタを組み直し、
            // それ以外の間はキャンバス上のドラッグ結果を各フィールドへ反映するだけにする。
            // §3: 単一選択の点だけでなく複数選択の代表点(SelectionRepresentative)が変わった
            // 場合も再構築が要る（例: 矩形選択のドラッグで代表点になる点が入れ替わる）。
            var rep = SelectionRepresentative();
            Note repNote = rep?.note;
            int repIndex = rep?.index ?? -1;
            if (!ReferenceEquals(inspectorNote, repNote)
                || inspectorPointIndex != repIndex
                || (repNote != null && inspectorPointCount != repNote.points.Count)
                || inspectorEventKind != selectedEventKind || inspectorEventIndex != selectedEventIndex
                || inspectorSelectionCount != selection.Count)
            {
                inspectorNote = repNote;
                inspectorPointIndex = repIndex;
                inspectorPointCount = repNote?.points.Count ?? 0;
                inspectorEventKind = selectedEventKind;
                inspectorEventIndex = selectedEventIndex;
                inspectorSelectionCount = selection.Count;
                RebuildInspector();
            }
            else if (slowTick && foldInspector.value)
            {
                suppressUiCallbacks = true;
                try
                {
                    foreach (var refresh in inspectorRefreshers) refresh();
                }
                finally
                {
                    suppressUiCallbacks = false;
                }
            }

            if (showRestorePrompt && !restorePromptShown) ShowRestoreModal();
        }

        private void SelectToolVisual()
        {
            foreach (var kv in toolButtons)
                kv.Value.EnableInClassList("tool-btn--selected", kv.Key == currentTool);
        }

        private static string FormatTime(float t)
        {
            if (t < 0f) t = 0f;
            int minutes = (int)(t / 60f);
            float seconds = t - minutes * 60f;
            return $"{minutes:00}:{seconds:00.00}";
        }

        /// <summary>ステータスバー右側の「位置 / 拍子 / BPM」（§2.6: カーソル位置の拍子・BPMが見えない への対処）。</summary>
        private string BuildChartInfoText()
        {
            int tick = selectedNote != null ? selectedNote.points[0].tick : scrollTick;
            var addr = SongAddr.ToAddr(song.meters, tick);
            var meter = MeterAtBar(addr.bar);
            float bpm = song.bpmEvents.Count > 0
                ? ChartFormat.BpmAtTime(song.bpmEvents, preview.SongTime)
                : 0f;
            string bpmText = bpm > 0f ? $"{bpm:0.##} BPM" : "BPM -";
            return $"{SongAddr.FormatAddrPadded(addr)}  │  {meter.numerator}/{meter.denominator}  │  {bpmText}";
        }

        private MeterEvent MeterAtBar(int bar)
        {
            var sorted = SongAddr.Normalize(song.meters);
            var current = sorted[0];
            foreach (var m in sorted)
            {
                if (m.bar > bar) break;
                current = m;
            }
            return current;
        }

        /// <summary>§2.5「統計」セクション。参考画像と同じくノーツ種別ごとの本数と合計コンボ数を出す。</summary>
        private string BuildStatsText()
        {
            int tap = 0, exTap = 0, slide = 0, flick = 0, combo = 0;
            foreach (var n in chart.notes)
            {
                switch (n.kind)
                {
                    case NoteKind.Tap: tap++; break;
                    case NoteKind.ExTap: exTap++; break;
                    case NoteKind.Slide: slide++; break;
                    case NoteKind.Flick: flick++; break;
                }
                combo += n.kind == NoteKind.Slide ? n.comboTimes.Count : 1;
            }
            string autoplay = preview.AutoplaySummary;
            string body = $"Tap {tap}\nEx Tap {exTap}\nSlide {slide}\nFlick {flick}\n合計 {chart.notes.Count} ノーツ / {combo} コンボ";
            return autoplay == null ? body : body + "\n\n" + autoplay;
        }

        // ================= §4 検証結果の一覧 =================

        private void RefreshValidationList()
        {
            if (validationHost == null) return;

            // 先頭の「保存時に自動実行」トグル以外を作り直す
            while (validationHost.childCount > 1)
                validationHost.RemoveAt(validationHost.childCount - 1);

            int errors = 0, warnings = 0, infos = 0;
            foreach (var issue in validationIssues)
            {
                if (issue.severity == ValidationSeverity.Error) errors++;
                else if (issue.severity == ValidationSeverity.Warning) warnings++;
                else infos++;

                var captured = issue;
                var btn = new Button(() =>
                {
                    scrollTick = Mathf.Max(0, captured.tick - 2000);
                    SelectTab(TabTimeline);
                })
                {
                    text = $"[{issue.id}] {issue.message}",
                };
                btn.AddToClassList("validation-item");
                btn.AddToClassList(issue.severity switch
                {
                    ValidationSeverity.Error => "validation-item--error",
                    ValidationSeverity.Warning => "validation-item--warning",
                    _ => "validation-item--info",
                });
                validationHost.Add(btn);
            }

            if (validationIssues.Count == 0)
            {
                var empty = new Label("問題は見つかりませんでした。");
                empty.AddToClassList("prop-note");
                validationHost.Add(empty);
            }

            if (statusValidation != null)
                statusValidation.text = $"検証 E{errors} W{warnings} I{infos}";
        }

        /// <summary>editor-ui-rework-r7.md §3.3: 音源は必ずsong.museprojと同じフォルダから
        /// 相対パス(ファイル名のみ)で解決される（PreviewSystem.TryLoadAudio）ため、別フォルダの
        /// ファイルを選んだ場合は曲フォルダへコピーする（旧実装は「読み込めません」と警告して
        /// 終わるだけだった）。拡張子はogg/wav/mp3（editor-ui-rework-r7.md Q6でwav/mp3にも対応）。</summary>
        private void PickAudioFile()
        {
            string songDir = string.IsNullOrEmpty(songPath) ? null : Path.GetDirectoryName(songPath);
            if (songDir == null)
            {
                statusMessage = "先に譜面を保存してください（曲フォルダが決まっていません）";
                return;
            }

            ShowFilePickerModal("音源ファイルを選択", new[] { "*.ogg", "*.wav", "*.mp3" }, picked =>
            {
                string pickedDir = Path.GetDirectoryName(picked);
                if (string.Equals(pickedDir, songDir, StringComparison.OrdinalIgnoreCase))
                {
                    audioFile.value = Path.GetFileName(picked); // RegisterValueChangedCallback経由でsong.audioに反映される
                    return;
                }
                ImportAudioFile(picked, songDir);
            });
        }

        /// <summary>editor-ui-rework-r7.md §3.3。曲フォルダ外の音源を選んだときのコピー処理。
        /// Opus(拡張子.oggだが中身はOpus)はコピー前に弾く（今回ユーザーが踏んだ罠を、
        /// ファイル選択の時点で潰すため）。</summary>
        private void ImportAudioFile(string sourcePath, string songDir)
        {
            string ext = Path.GetExtension(sourcePath);
            if (string.Equals(ext, ".ogg", StringComparison.OrdinalIgnoreCase) && PreviewSystem.LooksLikeOpus(sourcePath))
            {
                statusMessage = "この音源はOpusです。Vorbisに変換してからインポートしてください（例: ffmpeg -i 元ファイル -c:a libvorbis -q:a 6 出力ファイル）";
                return;
            }

            string fileName = Path.GetFileName(sourcePath);
            string destPath = Path.Combine(songDir, fileName);

            void DoCopy()
            {
                try
                {
                    File.Copy(sourcePath, destPath, overwrite: true);
                    audioFile.value = fileName;
                    statusMessage = $"音源を曲フォルダへコピーしました: {fileName}";
                }
                catch (Exception ex)
                {
                    statusMessage = "音源のコピーに失敗しました: " + ex.Message;
                }
            }

            if (File.Exists(destPath))
            {
                ShowConfirmModal("音源の上書き",
                    $"曲フォルダに既に \"{fileName}\" があります。上書きしますか？\n（他の難易度譜面がこの音源を参照している場合、その内容も変わります）",
                    "上書きする", DoCopy);
            }
            else
            {
                ShowConfirmModal("音源のコピー",
                    $"\"{fileName}\" を曲フォルダ ({songDir}) にコピーしますか？",
                    "コピーする", DoCopy);
            }
        }

        /// <summary>editor-ui-rework-r7.md §3.3。汎用の確認モーダル。ShowKeyConflictModal
        /// （r6の重複確認）と同じ骨格を、任意のメッセージ/確定ボタン/コールバックへ一般化した。</summary>
        private void ShowConfirmModal(string title, string message, string confirmLabel, Action onConfirm)
        {
            var modal = ShowModal(title);

            var msg = new Label(message);
            msg.AddToClassList("prop-note");
            msg.style.whiteSpace = WhiteSpace.Normal;
            modal.Add(msg);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            var confirmBtn = new Button(() => { CloseModal(modal); onConfirm(); }) { text = confirmLabel };
            confirmBtn.AddToClassList("tb-btn");
            var cancelBtn = new Button(() => CloseModal(modal)) { text = "キャンセル" };
            cancelBtn.AddToClassList("tb-btn");
            row.Add(confirmBtn);
            row.Add(cancelBtn);
            modal.Add(row);
        }

        /// <summary>editor-ui-rework-r7.md §3.2。「曲フォルダ」設定用のフォルダ専用ブラウザ。
        /// ShowFilePickerModalと似た骨格だがファイル一覧を出さず、「このフォルダを選択」ボタンで
        /// 現在地を確定する。メインのファイルブラウザ(browseDir)とは独立した現在地を持つ
        /// （設定変更のためにファイルブラウザの最後の場所を潰さないため）。</summary>
        private void ShowFolderPickerModal(string title, string startDir, Action<string> onPick)
        {
            var modal = ShowModal(title);
            string current = !string.IsNullOrEmpty(startDir) && Directory.Exists(startDir) ? startDir : songsRoot;

            var pathLabel = new Label(current);
            pathLabel.AddToClassList("prop-note");
            modal.Add(pathLabel);

            var list = new ScrollView();
            list.AddToClassList("browse-list");
            modal.Add(list);

            var row = new VisualElement();
            row.AddToClassList("modal-row");
            modal.Add(row);

            void Rebuild()
            {
                pathLabel.text = current;
                list.Clear();

                var parent = Directory.GetParent(current);
                if (parent != null)
                {
                    var up = new Button(() => { current = parent.FullName; Rebuild(); }) { text = ".. (上へ)" };
                    up.AddToClassList("browse-item");
                    up.AddToClassList("browse-item--dir");
                    list.Add(up);
                }

                try
                {
                    foreach (var dir in Directory.GetDirectories(current).OrderBy(d => d))
                    {
                        string captured = dir;
                        var b = new Button(() => { current = captured; Rebuild(); }) { text = "[ " + Path.GetFileName(dir) + " ]" };
                        b.AddToClassList("browse-item");
                        b.AddToClassList("browse-item--dir");
                        list.Add(b);
                    }
                }
                catch (Exception ex)
                {
                    var err = new Label("読み取りエラー: " + ex.Message);
                    err.AddToClassList("prop-note");
                    list.Add(err);
                }
            }

            var selectBtn = new Button(() => { CloseModal(modal); onPick(current); }) { text = "このフォルダを選択" };
            selectBtn.AddToClassList("tb-btn");
            var cancelBtn = new Button(() => CloseModal(modal)) { text = "キャンセル" };
            cancelBtn.AddToClassList("tb-btn");
            row.Add(selectBtn);
            row.Add(cancelBtn);

            Rebuild();
        }

        /// <summary>editor-ui-rework-r7.md §3.2/§3.3。macOSのFinderでフォルダを開く。
        /// ベストエフォート（失敗しても実害はないのでtry-catchで握りつぶす）。</summary>
        private static void OpenInFinder(string path)
        {
            try
            {
                if (Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor)
                    System.Diagnostics.Process.Start("open", $"\"{path}\"");
                else
                    Application.OpenURL("file://" + path.Replace("\\", "/"));
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"OpenInFinder: 開けませんでした ({path}): {ex.Message}");
            }
        }

        // ================= モーダル(ファイル参照 / 自動保存の復元) =================

        private VisualElement ShowModal(string title)
        {
            var scrim = new VisualElement();
            scrim.AddToClassList("modal-scrim");
            var modal = new VisualElement();
            modal.AddToClassList("modal");
            var titleLabel = new Label(title);
            titleLabel.AddToClassList("modal-title");
            modal.Add(titleLabel);
            scrim.Add(modal);
            overlayLayer.Add(scrim);
            return modal;
        }

        private void CloseModal(VisualElement modal)
        {
            modal?.parent?.RemoveFromHierarchy();
        }

        // ================= 設定モーダル(editor-ui-rework-r5.md) =================

        private VisualElement settingsBody;
        private int settingsTabIndex;
        private Button settingsGeneralTabBtn, settingsTimelineTabBtn, settingsShortcutTabBtn;

        /// <summary>editor-ui-rework-r5.md §2.1: モーダル＋横タブ3枚。中身は「一般」「タイムライン」
        /// 「ショートカットキー」で、各コントロールは既存フィールド(followPlayback等)へ直接バインドする
        /// （設定専用のコピーを持たず、変更した瞬間に即時反映される。EditorSettingsは永続化専用）。</summary>
        private void ShowSettingsModal()
        {
            var modal = ShowModal("設定");
            modal.AddToClassList("settings-modal");

            var tabHeader = new VisualElement();
            tabHeader.AddToClassList("tab-header");
            settingsGeneralTabBtn = new Button(() => SelectSettingsTab(0)) { text = "一般" };
            settingsTimelineTabBtn = new Button(() => SelectSettingsTab(1)) { text = "タイムライン" };
            settingsShortcutTabBtn = new Button(() => SelectSettingsTab(2)) { text = "ショートカットキー" };
            settingsGeneralTabBtn.AddToClassList("tab-header-btn");
            settingsTimelineTabBtn.AddToClassList("tab-header-btn");
            settingsShortcutTabBtn.AddToClassList("tab-header-btn");
            tabHeader.Add(settingsGeneralTabBtn);
            tabHeader.Add(settingsTimelineTabBtn);
            tabHeader.Add(settingsShortcutTabBtn);
            modal.Add(tabHeader);

            settingsBody = new ScrollView();
            settingsBody.AddToClassList("settings-body");
            modal.Add(settingsBody);

            var footer = new VisualElement();
            footer.AddToClassList("modal-row");
            var resetBtn = new Button(ResetCurrentSettingsTab) { text = "このタブを既定に戻す" };
            resetBtn.AddToClassList("tb-btn");
            var closeBtn = new Button(() => { SaveSettingsFromLiveFields(); CloseModal(modal); }) { text = "閉じる" };
            closeBtn.AddToClassList("tb-btn");
            footer.Add(resetBtn);
            footer.Add(closeBtn);
            modal.Add(footer);

            SelectSettingsTab(0);
        }

        private void SelectSettingsTab(int index)
        {
            settingsTabIndex = index;
            settingsGeneralTabBtn.EnableInClassList("tab-header-btn--selected", index == 0);
            settingsTimelineTabBtn.EnableInClassList("tab-header-btn--selected", index == 1);
            settingsShortcutTabBtn.EnableInClassList("tab-header-btn--selected", index == 2);
            settingsBody.Clear();
            switch (index)
            {
                case 0: BuildGeneralSettingsTab(settingsBody); break;
                case 1: BuildTimelineSettingsTab(settingsBody); break;
                case 2: BuildShortcutSettingsTab(settingsBody); break;
            }
        }

        private void ResetCurrentSettingsTab()
        {
            switch (settingsTabIndex)
            {
                case 0:
                    autosaveEnabled = true;
                    autosaveMinutes = 5;
                    frameRateMode = 0;
                    uiScale = 1f;
                    ApplyFrameRateSetting();
                    ApplyUiScale();
                    break;
                case 1:
                    followPlayback = true;
                    scrollFollowMode = ScrollFollowMode.Smooth;
                    judgeLineFrac = 1f;
                    laneDivisions = 4;
                    invertScroll = false;
                    laneWidthPx = 46f;
                    break;
                case 2:
                    keyBindings = EditorSettings.DefaultKeyBindings();
                    break;
            }
            SelectSettingsTab(settingsTabIndex);
        }

        // ---- 一般タブ(§3) ----

        private void BuildGeneralSettingsTab(VisualElement parent)
        {
            var autosaveToggle = AddToggleRow(parent, "自動保存", v => autosaveEnabled = v);
            autosaveToggle.SetValueWithoutNotify(autosaveEnabled);

            var intervalField = AddIntRow(parent, "自動保存の間隔(分)", v => autosaveMinutes = Mathf.Clamp(v, 1, 60));
            intervalField.SetValueWithoutNotify(autosaveMinutes);

            var frameRateDropdown = new DropdownField
            {
                choices = new List<string> { "VSync", "60fps", "120fps", "無制限" },
                index = frameRateMode,
            };
            frameRateDropdown.RegisterValueChangedCallback(_ =>
            {
                frameRateMode = frameRateDropdown.index;
                ApplyFrameRateSetting();
            });
            MakePropRow(parent, "フレームレート制限", frameRateDropdown);

            var uiScaleSlider = AddSliderRow(parent, "エディタ画面倍率", 0.75f, 2f, v => { uiScale = v; ApplyUiScale(); });
            uiScaleSlider.SetValueWithoutNotify(uiScale);

            // editor-ui-rework-r7.md §3.2: 曲プロジェクト群の置き場所。既定はFinderから見える
            // ~/Documents/muses/songs/（従来はFinder既定で非表示の~/Library/...下だった）。
            var songsRootField = new VisualElement();
            songsRootField.style.flexDirection = FlexDirection.Row;
            var songsRootLabel = new Label(songsRoot);
            songsRootLabel.AddToClassList("prop-note");
            songsRootLabel.style.flexGrow = 1;
            songsRootField.Add(songsRootLabel);
            var changeBtn = new Button(() => ShowFolderPickerModal("曲フォルダを選択", songsRoot, picked =>
            {
                songsRoot = picked;
                songsRootLabel.text = songsRoot;
                Directory.CreateDirectory(songsRoot);
            }))
            { text = "変更..." };
            changeBtn.AddToClassList("tb-btn");
            songsRootField.Add(changeBtn);
            var openBtn = new Button(() => OpenInFinder(songsRoot)) { text = "Finderで開く" };
            openBtn.AddToClassList("tb-btn");
            songsRootField.Add(openBtn);
            MakePropRow(parent, "曲フォルダ", songsRootField);
        }

        // ---- タイムラインタブ(§4) ----

        private void BuildTimelineSettingsTab(VisualElement parent)
        {
            var followToggle = AddToggleRow(parent, "再生に追従", v => followPlayback = v);
            followToggle.SetValueWithoutNotify(followPlayback);

            var pageToggle = AddToggleRow(parent, "ページ送りスクロール",
                v => scrollFollowMode = v ? ScrollFollowMode.Page : ScrollFollowMode.Smooth);
            pageToggle.SetValueWithoutNotify(scrollFollowMode == ScrollFollowMode.Page);

            var judgeLineSlider = AddSliderRow(parent, "判定線位置", 0f, 1f, v => judgeLineFrac = v);
            judgeLineSlider.SetValueWithoutNotify(judgeLineFrac);

            // editor-ui-rework-r5.md §4.2: 12(Cells)の約数のみを許容する。非約数だと強調線が
            // セル境界からずれてしまう。
            var divisionChoices = new List<int> { 1, 2, 3, 4, 6, 12 };
            var divisionDropdown = new DropdownField
            {
                choices = divisionChoices.ConvertAll(d => d.ToString()),
                index = Mathf.Max(0, divisionChoices.IndexOf(laneDivisions)),
            };
            divisionDropdown.RegisterValueChangedCallback(_ =>
            {
                laneDivisions = divisionChoices[Mathf.Clamp(divisionDropdown.index, 0, divisionChoices.Count - 1)];
            });
            MakePropRow(parent, "縦線の分割数", divisionDropdown);

            var invertToggle = AddToggleRow(parent, "スクロール方向を反転", v => invertScroll = v);
            invertToggle.SetValueWithoutNotify(invertScroll);

            // editor-ui-rework-r8.md §5.2: プレビュー画面でのCmd/Ctrl+ホイールで変わる値を
            // 恒久的に確認・調整できる場所として設定画面にも置く。
            var hiSpeedSlider = AddSliderRow(parent, "ハイスピード(プレビュー)", 0.5f, 4f, v => preview.HiSpeed = v);
            hiSpeedSlider.SetValueWithoutNotify(preview.HiSpeed);

            var laneWidthField = AddFloatRow(parent, "レーン幅(px)", v => laneWidthPx = Mathf.Clamp(v, 20f, 100f));
            laneWidthField.SetValueWithoutNotify(laneWidthPx);
        }

        // ---- ショートカットキータブ(§5.4) ----

        /// <summary>キャプチャ中(「＋」を押して次のキー入力待ち)のコマンドID。nullなら非キャプチャ中。
        /// OnGlobalKeyDown(ChartEditorApp.Commands.cs)はこれが非nullの間、通常のディスパッチを
        /// 止めて素通りさせる（既存コマンドに割り当て済みのキーを新規登録しようとしたときに、
        /// 先にそのコマンドが実行されてキャプチャへ届かなくなるのを防ぐ）。</summary>
        private string capturingCommandId;

        private void BuildShortcutSettingsTab(VisualElement parent)
        {
            if (commands == null) BuildCommandTable();
            capturingCommandId = null;

            string lastCategory = null;
            foreach (var cmd in commands)
            {
                if (cmd.category != lastCategory)
                {
                    lastCategory = cmd.category;
                    var header = new Label(cmd.category);
                    header.AddToClassList("settings-section-title");
                    parent.Add(header);
                }
                BuildShortcutRow(parent, cmd.id, cmd.label);
            }
        }

        private void BuildShortcutRow(VisualElement parent, string commandId, string label)
        {
            var row = new VisualElement();
            row.AddToClassList("prop-row");
            var lbl = new Label(label);
            lbl.AddToClassList("prop-label");
            row.Add(lbl);

            var chipsHost = new VisualElement();
            chipsHost.style.flexDirection = FlexDirection.Row;
            chipsHost.style.flexWrap = Wrap.Wrap;
            chipsHost.style.flexGrow = 1;
            row.Add(chipsHost);

            var binding = keyBindings.Find(b => b.commandId == commandId);
            if (binding == null)
            {
                binding = new KeyBinding(commandId);
                keyBindings.Add(binding);
            }

            foreach (var chord in binding.chords)
            {
                // ユーザー要望「複数登録できると良い」: chip自体をクリックで削除できるようにする。
                var chip = new Button(() =>
                {
                    binding.chords.Remove(chord);
                    SelectSettingsTab(settingsTabIndex);
                })
                { text = chord + " ×" };
                chip.AddToClassList("tb-btn");
                // editor-ui-rework-r6.md §3.3(3)。通常は確認モーダルを経由するので重複は起きないが、
                // 手で編集したeditor-settings.jsonを読み込んだ場合には起こりうるため、気づける
                // 印だけ低コストで残しておく（色のみ、専用USSは追加しない）。
                if (IsChordDuplicated(commandId, chord)) chip.style.color = new Color(1f, 0.85f, 0.4f);
                chipsHost.Add(chip);
            }

            var addBtn = new Button(() => BeginCapture(commandId)) { text = "＋" };
            addBtn.AddToClassList("tb-btn");
            chipsHost.Add(addBtn);
            if (capturingCommandId == commandId) addBtn.text = "キーを押してください(Escで中止)";

            var resetBtn = new Button(() =>
            {
                var defaults = EditorSettings.DefaultKeyBindings().Find(b => b.commandId == commandId);
                binding.chords = defaults != null ? new List<KeyChord>(defaults.chords) : new List<KeyChord>();
                SelectSettingsTab(settingsTabIndex);
            })
            { text = "既定" };
            resetBtn.AddToClassList("tb-btn");
            row.Add(resetBtn);

            parent.Add(row);
        }

        private static bool ChordEquals(KeyChord a, KeyChord b) =>
            a.key == b.key && a.primary == b.primary && a.shift == b.shift && a.alt == b.alt;

        private bool IsChordDuplicated(string commandId, KeyChord chord)
        {
            foreach (var b in keyBindings)
            {
                if (b.commandId == commandId) continue;
                if (b.chords.Exists(c => ChordEquals(c, chord))) return true;
            }
            return false;
        }

        /// <summary>editor-ui-rework-r5.md §5.4 / editor-ui-rework-r6.md §3: 次のKeyDownEventを
        /// 1回だけ捕まえてchordとして登録する。競合(他コマンドが同じchordを既に持つ)がある場合は
        /// 黙って後勝ちにはせず、確認モーダルを挟んでから「割り当てる／やめる」を選ばせる
        /// （r5時点は後勝ちだったが、無言で外れると気づけないというユーザー指摘を受けて変更）。</summary>
        private void BeginCapture(string commandId)
        {
            capturingCommandId = commandId;

            void Handler(KeyDownEvent evt)
            {
                uiRoot.UnregisterCallback<KeyDownEvent>(Handler, TrickleDown.TrickleDown);
                capturingCommandId = null;
                evt.StopPropagation();
                evt.PreventDefault();

                if (evt.keyCode == KeyCode.Escape)
                {
                    SelectSettingsTab(settingsTabIndex);
                    return;
                }

                var chord = new KeyChord(evt.keyCode, evt.commandKey || evt.ctrlKey, evt.shiftKey, evt.altKey);
                var conflicts = new List<KeyBinding>();
                foreach (var b in keyBindings)
                {
                    if (b.commandId == commandId) continue;
                    if (b.chords.Exists(c => ChordEquals(c, chord))) conflicts.Add(b);
                }

                if (conflicts.Count == 0)
                {
                    AssignChord(commandId, chord);
                    SelectSettingsTab(settingsTabIndex);
                }
                else
                {
                    // §3.2: 「両方に残す」は提供しない（OnGlobalKeyDownは最初に一致した
                    // コマンドだけを実行するため、二重登録は片方だけが動く説明不能な状態になる）。
                    ShowKeyConflictModal(chord, commandId, conflicts);
                }
            }

            uiRoot.RegisterCallback<KeyDownEvent>(Handler, TrickleDown.TrickleDown);
        }

        private void AssignChord(string commandId, KeyChord chord)
        {
            foreach (var b in keyBindings)
            {
                if (b.commandId == commandId) continue;
                b.chords.RemoveAll(c => ChordEquals(c, chord));
            }
            var target = keyBindings.Find(b => b.commandId == commandId);
            if (target != null && !target.chords.Exists(c => ChordEquals(c, chord)))
                target.chords.Add(chord);
        }

        /// <summary>editor-ui-rework-r6.md §3.2。既存のShowModal/CloseModal基盤（overlayLayerの子）
        /// をそのまま使う。「やめる」を選んだ場合は何も変更しない。</summary>
        private void ShowKeyConflictModal(KeyChord chord, string commandId, List<KeyBinding> conflicts)
        {
            var modal = ShowModal("キーの重複");

            var msg = new Label($"{chord} は既に次のコマンドに割り当てられています:");
            msg.AddToClassList("prop-note");
            modal.Add(msg);

            foreach (var b in conflicts)
            {
                var cmd = FindCommand(b.commandId);
                var line = new Label(cmd.HasValue ? $"・{cmd.Value.category} > {cmd.Value.label}" : $"・{b.commandId}");
                line.AddToClassList("prop-note");
                modal.Add(line);
            }

            var note = new Label("「割り当てる」を選ぶと、上のコマンドからは外されます。");
            note.AddToClassList("prop-note");
            modal.Add(note);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            var assignBtn = new Button(() =>
            {
                AssignChord(commandId, chord);
                CloseModal(modal);
                SelectSettingsTab(settingsTabIndex);
            })
            { text = "割り当てる" };
            assignBtn.AddToClassList("tb-btn");
            var cancelBtn = new Button(() => CloseModal(modal)) { text = "やめる" };
            cancelBtn.AddToClassList("tb-btn");
            row.Add(assignBtn);
            row.Add(cancelBtn);
            modal.Add(row);
        }

        /// <summary>
        /// editor-ui-rework-r4.md §9: 音源ファイル等、ChartEditorApp内部の状態
        /// （chartFilePathBuffer・OpenChartFromPath等）に依存しない汎用のファイル選択モーダル。
        /// スタンドアロンビルドにはOSネイティブのファイル選択APIが無いため、ShowFileModal
        /// （.musesの開く/別名保存専用）と同じ自前ブラウザの骨格を、拡張子フィルタと
        /// コールバックだけを受け取る形に切り出した。
        /// </summary>
        private void ShowFilePickerModal(string title, string pattern, Action<string> onPick) =>
            ShowFilePickerModal(title, new[] { pattern }, onPick);

        /// <summary>editor-ui-rework-r7.md §3.3/Q6: 複数拡張子（*.ogg/*.wav/*.mp3等）を1つの
        /// 一覧にまとめて出すための多パターン版。</summary>
        private void ShowFilePickerModal(string title, string[] patterns, Action<string> onPick)
        {
            var modal = ShowModal(title);

            var pathLabel = new Label(browseDir);
            pathLabel.AddToClassList("prop-note");
            modal.Add(pathLabel);

            var list = new ScrollView();
            list.AddToClassList("browse-list");
            modal.Add(list);

            var row = new VisualElement();
            row.AddToClassList("modal-row");
            modal.Add(row);

            void Rebuild()
            {
                pathLabel.text = browseDir;
                list.Clear();

                var parent = Directory.GetParent(browseDir);
                if (parent != null)
                {
                    var up = new Button(() => { browseDir = parent.FullName; Rebuild(); }) { text = ".. (上へ)" };
                    up.AddToClassList("browse-item");
                    up.AddToClassList("browse-item--dir");
                    list.Add(up);
                }

                try
                {
                    foreach (var dir in Directory.GetDirectories(browseDir).OrderBy(d => d))
                    {
                        string captured = dir;
                        var b = new Button(() => { browseDir = captured; Rebuild(); }) { text = "[ " + Path.GetFileName(dir) + " ]" };
                        b.AddToClassList("browse-item");
                        b.AddToClassList("browse-item--dir");
                        list.Add(b);
                    }

                    var files = new List<string>();
                    foreach (var pat in patterns) files.AddRange(Directory.GetFiles(browseDir, pat));
                    foreach (var file in files.Distinct().OrderBy(f => f))
                    {
                        string captured = file;
                        var b = new Button(() =>
                        {
                            RememberBrowseDir();
                            CloseModal(modal);
                            onPick(captured);
                        })
                        { text = Path.GetFileName(file) };
                        b.AddToClassList("browse-item");
                        list.Add(b);
                    }
                }
                catch (Exception ex)
                {
                    var err = new Label("読み取りエラー: " + ex.Message);
                    err.AddToClassList("prop-note");
                    list.Add(err);
                }
            }

            var cancel = new Button(() => CloseModal(modal)) { text = "キャンセル" };
            cancel.AddToClassList("tb-btn");
            row.Add(cancel);

            Rebuild();
        }

        /// <summary>
        /// ネイティブのファイル選択ダイアログが無いスタンドアロン向けの代替（editor-spec.md の
        /// 簡易ファイルブラウザをUI Toolkitへ移植したもの）。saveMode時は曲フォルダ名入力欄が付く。
        /// editor-ui-rework-r9.md §5.2: 譜面ファイル名は自由入力ではなく@DIFFICULTYから自動決定
        /// するため、旧「ファイル名」入力は「曲フォルダ名」入力に置き換える。
        /// </summary>
        private void ShowFileModal(bool saveMode)
        {
            var modal = ShowModal(saveMode ? "曲フォルダを選んで保存" : "譜面ファイルを開く");

            var pathLabel = new Label(browseDir);
            pathLabel.AddToClassList("prop-note");
            modal.Add(pathLabel);

            var list = new ScrollView();
            list.AddToClassList("browse-list");
            modal.Add(list);

            // editor-ui-rework-r9.md §3.1: 曲フォルダ名の入力は「保存先に曲メタファイルが無い」
            // ときだけ使う（無ければこの名前でサブフォルダを作る、あれば直下にそのまま保存）。
            var nameField = new TextField("曲フォルダ名") { value = string.IsNullOrEmpty(song.title) ? "" : song.title };
            var destLabel = new Label("");
            destLabel.AddToClassList("prop-note");
            if (saveMode)
            {
                modal.Add(nameField);
                modal.Add(destLabel);
                nameField.RegisterValueChangedCallback(_ => UpdateDestLabel());
            }

            string ExpectedChartFileName() => header.difficulty.ToLowerInvariant() + ChartSerializer.ChartExt;

            bool HasSongMeta(string dir) => File.Exists(Path.Combine(dir, ChartSerializer.SongFileName));

            string ResolveTargetDir() =>
                HasSongMeta(browseDir) ? browseDir : Path.Combine(browseDir, SanitizeFolderName(nameField.value ?? ""));

            void UpdateDestLabel()
            {
                if (HasSongMeta(browseDir))
                {
                    nameField.SetEnabled(false);
                    destLabel.text = $"既存の曲フォルダに保存: {browseDir}/{ExpectedChartFileName()}";
                }
                else
                {
                    nameField.SetEnabled(true);
                    string folder = SanitizeFolderName(nameField.value ?? "");
                    destLabel.text = string.IsNullOrEmpty(folder)
                        ? "曲フォルダ名を入力してください"
                        : $"保存先: {browseDir}/{folder}/{ExpectedChartFileName()}";
                }
            }

            var row = new VisualElement();
            row.AddToClassList("modal-row");
            modal.Add(row);

            void Rebuild()
            {
                pathLabel.text = browseDir;
                list.Clear();
                if (saveMode) UpdateDestLabel();

                var parent = Directory.GetParent(browseDir);
                if (parent != null)
                {
                    var up = new Button(() => { browseDir = parent.FullName; Rebuild(); }) { text = ".. (上へ)" };
                    up.AddToClassList("browse-item");
                    up.AddToClassList("browse-item--dir");
                    list.Add(up);
                }

                try
                {
                    foreach (var dir in Directory.GetDirectories(browseDir).OrderBy(d => d))
                    {
                        string captured = dir;
                        var b = new Button(() => { browseDir = captured; Rebuild(); }) { text = "[ " + Path.GetFileName(dir) + " ]" };
                        b.AddToClassList("browse-item");
                        b.AddToClassList("browse-item--dir");
                        list.Add(b);
                    }

                    foreach (var file in Directory.GetFiles(browseDir, "*" + ChartSerializer.ChartExt).OrderBy(f => f))
                    {
                        string captured = file;
                        var b = new Button(() =>
                        {
                            if (saveMode) return; // r9 §5.2: 保存時はファイル名を選ばせない（難易度で自動決定するため）
                            chartFilePathBuffer = captured;
                            RememberBrowseDir();
                            CloseModal(modal);
                            OpenChartFromPath();
                            SyncAfterFileOperation();
                        })
                        { text = Path.GetFileName(file) };
                        b.AddToClassList("browse-item");
                        list.Add(b);
                    }
                }
                catch (Exception ex)
                {
                    var err = new Label("読み取りエラー: " + ex.Message);
                    err.AddToClassList("prop-note");
                    list.Add(err);
                }
            }

            if (saveMode)
            {
                var saveBtn = new Button(() =>
                {
                    string targetDir = ResolveTargetDir();
                    if (!HasSongMeta(browseDir) && string.IsNullOrWhiteSpace(nameField.value)) return;
                    Directory.CreateDirectory(targetDir);

                    chartFilePathBuffer = Path.Combine(targetDir, ExpectedChartFileName());
                    browseDir = targetDir;
                    RememberBrowseDir();
                    CloseModal(modal);
                    SaveChartToPath();
                    SyncAfterFileOperation();
                })
                { text = "保存" };
                saveBtn.AddToClassList("tb-btn");
                row.Add(saveBtn);
            }

            var cancel = new Button(() => CloseModal(modal)) { text = "キャンセル" };
            cancel.AddToClassList("tb-btn");
            row.Add(cancel);

            Rebuild();
        }

        private void RememberBrowseDir()
        {
            settings.browseDir = browseDir;
            EditorSettingsStore.Save(settings);
        }

        /// <summary>editor-ui-rework-r8.md §2.2。フォルダ名として使える形へ変換する
        /// （CreateNewSongのフォルダ名検証と揃え、禁止文字は_へ置換する）。</summary>
        private static string SanitizeFolderName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        private void ShowRestoreModal()
        {
            restorePromptShown = true;
            var modal = ShowModal("自動保存ファイルの復元");
            var body = new Label("自動保存ファイルの方が新しいです。復元しますか？");
            body.AddToClassList("prop-note");
            modal.Add(body);

            var row = new VisualElement();
            row.AddToClassList("modal-row");
            var restore = new Button(() =>
            {
                CloseModal(modal);
                restorePromptShown = false;
                RestoreFromAutosave();
                SyncAfterFileOperation();
            })
            { text = "復元する" };
            restore.AddToClassList("tb-btn");
            var ignore = new Button(() =>
            {
                CloseModal(modal);
                restorePromptShown = false;
                showRestorePrompt = false;
            })
            { text = "無視する" };
            ignore.AddToClassList("tb-btn");
            row.Add(restore);
            row.Add(ignore);
            modal.Add(row);
        }

        // ================= メニュー項目の実処理 =================

        private void NewChart()
        {
            PushUndo(coalesce: false);
            chart = new ChartData();
            ClearSelection();
            pendingSlideStart = null;
            draggingNote = false;
            dirty = true;
            uiNeedsPropertyRefresh = true;
            statusMessage = "新規譜面を作成しました";
            MarkPreviewDirty();
        }

        /// <summary>editor-ui-rework-r7.md §3.4。曲プロジェクト(songs/&lt;song-id&gt;/)を新規に
        /// 作るウィザード。既存の「新規」(NewChart)は chart を空にするだけで song/songPath/chartPath
        /// には触らない(＝同じ曲に別難易度を作る用の操作)ため意味が被らない。「新規曲」は
        /// フォルダ自体が無い状態から一直線に編集開始できることを目的にする
        /// （旧実装は保存を試みて初めて song.museproj が無いことに気づく、という段差があった）。
        /// </summary>
        private static readonly string[] DifficultyChoices = { "LINE", "SQUARE", "CUBE", "TESSERACT" };

        private void ShowNewSongWizard()
        {
            var modal = ShowModal("新規曲を作成");

            var idField = new TextField("フォルダ名") { value = "" };
            modal.Add(idField);
            var idNote = new Label($"保存先: {songsRoot}/<フォルダ名>/");
            idNote.AddToClassList("prop-note");
            modal.Add(idNote);
            idField.RegisterValueChangedCallback(evt => idNote.text = $"保存先: {songsRoot}/{evt.newValue}/");

            var titleField = new TextField("タイトル") { value = "" };
            modal.Add(titleField);

            var diffDropdown = new DropdownField { choices = new List<string>(DifficultyChoices), index = 2 }; // 既定CUBE
            MakePropRow(modal, "難易度", diffDropdown);

            var errorLabel = new Label("");
            errorLabel.AddToClassList("prop-note");
            modal.Add(errorLabel);

            var row = new VisualElement();
            row.AddToClassList("modal-row");
            modal.Add(row);

            var createBtn = new Button(() =>
            {
                string songId = idField.value?.Trim();
                if (string.IsNullOrEmpty(songId))
                {
                    errorLabel.text = "フォルダ名を入力してください";
                    return;
                }
                if (songId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    errorLabel.text = "フォルダ名に使えない文字が含まれています";
                    return;
                }

                string difficulty = DifficultyChoices[Mathf.Clamp(diffDropdown.index, 0, DifficultyChoices.Length - 1)];
                string error = CreateNewSong(songId, titleField.value, difficulty);
                if (error != null) { errorLabel.text = error; return; }
                CloseModal(modal);
            })
            { text = "作成" };
            createBtn.AddToClassList("tb-btn");
            var cancelBtn = new Button(() => CloseModal(modal)) { text = "キャンセル" };
            cancelBtn.AddToClassList("tb-btn");
            row.Add(createBtn);
            row.Add(cancelBtn);
        }

        /// <summary>戻り値がnullなら成功。エラーメッセージがあれば失敗（ウィザードのラベルに出す）。
        /// フォルダが既に存在する場合は既存の song.museproj を引き継ぎ、新しい難易度ファイルだけを
        /// 追加する（editor-ui-rework-r7.md §3.4「1曲に複数の譜面」を1つの曲プロジェクトフォルダ
        /// で管理する、というユーザー確定の設計どおり）。</summary>
        private string CreateNewSong(string songId, string title, string difficulty)
        {
            string dir = Path.Combine(songsRoot, songId);
            string newSongPath = Path.Combine(dir, ChartSerializer.SongFileName);
            string chartFileName = difficulty.ToLowerInvariant() + ChartSerializer.ChartExt;
            string newChartPath = Path.Combine(dir, chartFileName);

            if (File.Exists(newChartPath))
                return $"既に {chartFileName} が存在します（別の難易度を選ぶか、既存ファイルを開いてください）";

            try
            {
                Directory.CreateDirectory(dir);

                string legacySongPath = Path.Combine(dir, ChartSerializer.LegacySongFileName);
                SongMeta newSong;
                if (File.Exists(newSongPath))
                {
                    newSong = ChartSerializer.ReadSongMeta(newSongPath);
                }
                else if (File.Exists(legacySongPath))
                {
                    newSong = ChartSerializer.ReadSongMeta(legacySongPath);
                    ChartSerializer.WriteSongMeta(newSong, newSongPath); // r9 §4.3: 保存は常に新名で
                }
                else
                {
                    newSong = new SongMeta { title = string.IsNullOrEmpty(title) ? songId : title };
                    ChartSerializer.WriteSongMeta(newSong, newSongPath);
                }

                var newHeader = new ChartFileHeader { difficulty = difficulty, level = 1, charter = "", songFile = ChartSerializer.SongFileName };
                var newChart = new ChartData();
                ChartSerializer.WriteChart(newChartPath, newHeader, newChart, newSong);

                song = newSong;
                header = newHeader;
                chart = newChart;
                songPath = newSongPath;
                chartPath = newChartPath;
                chartFilePathBuffer = newChartPath;
                ClearSelection();
                pendingSlideStart = null;
                draggingNote = false;
                dirty = false;
                songMetaDirty = false;
                undoStack.Clear();
                redoStack.Clear();
                lastAutosaveRealtime = Time.unscaledTime;
                uiNeedsPropertyRefresh = true;
                browseDir = dir;
                settings.browseDir = dir;
                EditorSettingsStore.Save(settings);
                preview.Rebuild(song, chart, dir);
                lastPreviewRebuildRealtime = Time.unscaledTime;
                statusMessage = $"新規曲を作成しました: {dir}";
                SyncAfterFileOperation();
                return null;
            }
            catch (Exception ex)
            {
                return "作成エラー: " + ex.Message;
            }
        }

        private void MarkPreviewDirty()
        {
            // 譜面未読み込み(songPathがnull)でも「新規」から呼ばれうるので、音源ディレクトリは空で渡す
            string audioDir = string.IsNullOrEmpty(songPath) ? null : Path.GetDirectoryName(songPath);
            preview.Rebuild(song, chart, audioDir);
            lastPreviewRebuildRealtime = Time.unscaledTime;
        }

        /// <summary>editor-ui-rework-r6.md §4.1(d)。音源セクションの状態ラベル。</summary>
        private string AudioLoadStatusText() => preview.LoadState switch
        {
            AudioLoadState.None => "",
            AudioLoadState.Loading => "読み込み中…",
            AudioLoadState.Ok => "✓ 再生可能",
            AudioLoadState.NotFound => "⚠ " + preview.LoadMessage,
            AudioLoadState.Unsupported => "⚠ " + preview.LoadMessage,
            AudioLoadState.DecodeFailed => "⚠ " + preview.LoadMessage,
            _ => "",
        };

        private void SyncAfterFileOperation()
        {
            RefreshValidationList();
            SyncModelToUi();
        }

        private static void QuitApp()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
