using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using Muses.Chart;

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
        };

        private const int TabTimeline = 0;
        private const int TabPreview = 1;

        // ---- 要素の参照（BuildUIで一度だけ引く）----
        private VisualElement uiRoot;
        private VisualElement overlayLayer;
        private VisualElement notesSheet;
        private VisualElement previewSurface;
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
        private Toggle viewFollow;
        private Slider viewJudgeLine;
        private Slider viewRate;
        private Toggle viewHeightLane;
        private Label statsText;
        private VisualElement presetHost;
        private TextField presetNameField;
        private Foldout foldInspector, foldValidation;
        private VisualElement validationHost;
        private Toggle validateOnSaveToggle;

        // インスペクタは選択ノーツの中継点の数だけ行が変わるので、構造の作り直しと値の追従を分けて持つ
        private VisualElement inspectorHost;
        private Note inspectorNote;
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
                menu.AddItem("開く...", false, () => ShowFileModal(saveMode: false));
                menu.AddSeparator("");
                menu.AddItem("保存", false, SaveChartToPath);
                menu.AddItem("別名で保存...", false, () => ShowFileModal(saveMode: true));
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
                menu.AddItem("再生に追従", followPlayback, () =>
                {
                    followPlayback = !followPlayback;
                    if (viewFollow != null) viewFollow.SetValueWithoutNotify(followPlayback);
                });
                menu.AddItem("高さレーン", showHeightLane, () =>
                {
                    showHeightLane = !showHeightLane;
                    if (viewHeightLane != null) viewHeightLane.SetValueWithoutNotify(showHeightLane);
                });
            });

            AddMenu(bar, "再生", menu =>
            {
                menu.AddItem(preview.IsPlaying ? "一時停止" : "再生", false, () => preview.TogglePlay());
                menu.AddItem("先頭へ戻る", false, () => preview.Seek(0f));
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
            });

            AddMenu(bar, "ヘルプ", menu =>
            {
                menu.AddItem("ショートカット: Cmd/Ctrl+Z = 元に戻す", false, null);
                menu.AddItem("ショートカット: Cmd/Ctrl+Shift+Z / Y = やり直す", false, null);
                menu.AddItem("ショートカット: Delete = 選択ノーツを削除", false, null);
                menu.AddItem("ショートカット: Cmd/Ctrl+C/X/V = コピー/切り取り/貼り付け", false, null);
                menu.AddItem("Shift+ドラッグ = 追加選択 / Shift+クリック = 選択トグル", false, null);
                menu.AddItem("右クリック = コンテキストメニュー", false, null);
                menu.AddItem("高さレーン = 選択中ノーツの中継点を横ドラッグで層編集", false, null);
                menu.AddItem("ホイール = スクロール / Ctrl+ホイール = 拡大縮小", false, null);
                menu.AddSeparator("");
                menu.AddItem($"muses 譜面エディタ {Application.version}", false, null);
            });
        }

        /// <summary>
        /// IMGUIにもUI Toolkitにもメニューバー部品は無いため、ボタン + GenericDropdownMenu で自前実装する
        /// （§4.1 の表にあるとおり。GenericDropdownMenu はランタイムでも使える標準のポップアップ）。
        /// 項目の活性状態は開くたびに変わるので、生成は毎回クリック時に行う。
        /// </summary>
        private void AddMenu(VisualElement bar, string title, Action<GenericDropdownMenu> build)
        {
            var btn = new Button { text = title };
            btn.AddToClassList("menu-btn");
            btn.clicked += () =>
            {
                var menu = new GenericDropdownMenu();
                build(menu);
                menu.DropDown(btn.worldBound, btn, DropdownMenuSizeMode.Auto);
            };
            bar.Add(btn);
        }

        // ================= A: ツールバー(§2.2) =================

        private void BuildToolbar()
        {
            var fileGroup = uiRoot.Q<VisualElement>("toolbar-file");
            fileGroup.Clear();
            fileGroup.Add(MakeToolbarButton("新規", NewChart));
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
            var widthField = new FloatField { value = defaultWidthCells };
            widthField.style.width = 54;
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
            notesSheet.style.flexGrow = 1;
            notesSheet.generateVisualContent += GenerateNotesSheet;
            notesSheet.RegisterCallback<PointerDownEvent>(OnSheetPointerDown);
            notesSheet.RegisterCallback<PointerMoveEvent>(OnSheetPointerMove);
            notesSheet.RegisterCallback<PointerUpEvent>(OnSheetPointerUp);
            notesSheet.RegisterCallback<PointerLeaveEvent>(OnSheetPointerLeave);
            notesSheet.RegisterCallback<WheelEvent>(OnSheetWheel);
            notesSheet.RegisterCallback<KeyDownEvent>(OnSheetKeyDown);

            previewSurface = new VisualElement();
            previewSurface.style.flexGrow = 1;
            previewSurface.RegisterCallback<GeometryChangedEvent>(_ => UpdatePreviewTexture());

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

            // §7.5 高さレーン（折りたたみ中は幅0なので見出しも出さない）
            if (L.heightLane.width > 0f)
                PlaceSheetLabel(ref used, "高さ G→S", L.heightLane.x + 4f, L.rect.y + 2f);

            var (bpmCol, meterCol, scrollCol) = EventColumns(L.rightMargin);
            PlaceSheetLabel(ref used, "BPM", bpmCol.x + 2f, L.rect.y + 2f);
            PlaceSheetLabel(ref used, "拍子", meterCol.x + 2f, L.rect.y + 2f);
            PlaceSheetLabel(ref used, "速度", scrollCol.x + 2f, L.rect.y + 2f);

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
                // 余白レーン(§7.2)に退避したので、小節線と同じ高さに縦中央揃えで置ける
                // （旧実装はGroundレーン内に重ねていたためノーツと被らないよう線の上に逃がしていた）。
                PlaceSheetLabel(ref used, addr.bar.ToString(), L.leftMargin.x + 4f, y - 7f);
            }

            for (int i = used; i < sheetLabels.Count; i++)
                sheetLabels[i].style.display = DisplayStyle.None;
        }

        private void PlaceSheetLabel(ref int used, string text, float x, float y)
        {
            if (used >= MaxSheetLabels) return;
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
            if (lbl.text != text) lbl.text = text;
            lbl.style.left = x;
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
            audioOffset = AddFloatRow(audioHost, "オフセット(秒)", v => { song.offsetSec = v; songMetaDirty = true; MarkPreviewDirty(); });
            var note = new Label("タイトル〜オフセットは song.muses の内容です。保存時に譜面と一緒に書き戻します。");
            note.AddToClassList("prop-note");
            audioHost.Add(note);

            var viewHost = uiRoot.Q<VisualElement>("view-host");
            viewHost.Clear();
            viewFollow = AddToggleRow(viewHost, "再生に追従", v => followPlayback = v);
            // MikuMikuWorld移植候補: 再生追従のPage/Smooth切替（EditorWindows.cpp:531-540のScrollMode）。
            AddToggleRow(viewHost, "ページ送りスクロール", v => scrollFollowMode = v ? ScrollFollowMode.Page : ScrollFollowMode.Smooth);
            viewJudgeLine = AddSliderRow(viewHost, "判定線位置", 0f, 1f, v => judgeLineFrac = v);
            viewRate = AddSliderRow(viewHost, "再生速度", 0.25f, 2f, v => preview.Rate = v);
            // §7.5 高さレーン。既定は折りたたみ（幅0）で、必要なときだけ開く。
            viewHeightLane = AddToggleRow(viewHost, "高さレーン", v => showHeightLane = v);
            var heightNote = new Label("高さレーンには選択中のノーツの中継点だけを表示します（点を横にドラッグで層を編集）。");
            heightNote.AddToClassList("prop-note");
            viewHost.Add(heightNote);

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
        /// 選択中ノーツの編集UI。中継点の増減で行数が変わるため、選択や点数が変わったときだけ作り直す。
        /// 作り直しの間、値の追従（キャンバス上でのドラッグなど）は inspectorRefreshers が担当する。
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

            if (selection.Count > 1)
            {
                RebuildMultiSelectInspector();
                return;
            }

            if (selectedNote == null)
            {
                var empty = new Label("ノーツを選択してください");
                empty.AddToClassList("prop-note");
                inspectorHost.Add(empty);
                return;
            }

            var note = selectedNote;

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

            for (int i = 0; i < note.points.Count; i++)
            {
                int index = i; // クロージャ用に確定させる
                string role = index == 0 ? "（始点）"
                    : index == note.points.Count - 1 ? "（終点）" : "";
                var section = new Foldout { text = $"中継点 {index}{role}", value = index == 0 };
                section.AddToClassList("section");
                inspectorHost.Add(section);

                var addrField = AddTextRow(section, "位置", v =>
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

                var layerField = AddSliderRow(section, "layerF", 0f, 1f, v =>
                    MutateWaypoint(note, index, w => { w.layerF = Mathf.Clamp01(v); return w; }));
                inspectorRefreshers.Add(() => RefreshIfUnfocused(layerField, note.points[index].layerF));

                var cellField = AddFloatRow(section, "cellF", v =>
                    MutateWaypoint(note, index, w => { w.cellF = v; return w; }));
                inspectorRefreshers.Add(() => RefreshIfUnfocused(cellField, note.points[index].cellF));

                var widthField = AddFloatRow(section, "width", v =>
                    MutateWaypoint(note, index, w => { w.width = Mathf.Max(0.1f, v); return w; }));
                inspectorRefreshers.Add(() => RefreshIfUnfocused(widthField, note.points[index].width));

                var easingField = AddEnumRow(section, "easing", note.points[index].easing, (Easing v) =>
                    MutateWaypoint(note, index, w => { w.easing = v; return w; }));
                inspectorRefreshers.Add(() => RefreshEnumIfUnfocused(easingField, note.points[index].easing));

                var markerField = AddEnumRow(section, "marker", note.points[index].marker, (WaypointMarker v) =>
                    MutateWaypoint(note, index, w => { w.marker = v; return w; }));
                inspectorRefreshers.Add(() => RefreshEnumIfUnfocused(markerField, note.points[index].marker));

                var comboToggle = AddToggleRow(section, "comboStep上書き", v =>
                    MutateWaypoint(note, index, w => { w.comboStep = v ? w.comboStep ?? 0 : null; return w; }));
                comboToggle.SetValueWithoutNotify(note.points[index].comboStep.HasValue);
                inspectorRefreshers.Add(() => comboToggle.SetValueWithoutNotify(note.points[index].comboStep.HasValue));

                var comboField = AddIntRow(section, "comboStep(tick)", v =>
                    MutateWaypoint(note, index, w => { w.comboStep = v; return w; }));
                inspectorRefreshers.Add(() =>
                {
                    var wp = note.points[index];
                    comboField.parent.style.display = wp.comboStep.HasValue ? DisplayStyle.Flex : DisplayStyle.None;
                    RefreshIfUnfocused(comboField, wp.comboStep ?? 0);
                });

                if (note.kind == NoteKind.Slide && note.points.Count > 2)
                {
                    var removeBtn = new Button(() =>
                    {
                        PushUndo(coalesce: false);
                        note.points.RemoveAt(index);
                        dirty = true;
                    })
                    { text = "この中継点を削除" };
                    removeBtn.AddToClassList("tb-btn");
                    section.Add(removeBtn);
                }
            }

            var deleteBtn = new Button(DeleteSelection) { text = "このノーツを削除" };
            deleteBtn.AddToClassList("tb-btn");
            inspectorHost.Add(deleteBtn);
        }

        /// <summary>
        /// §7.4-A: 複数選択時のインスペクタ。個別編集はせず一括操作（削除／単発ノーツのみ種別変更）に留める
        /// （editor-ui-redesign.md §7.4-Aどおり）。
        /// </summary>
        private void RebuildMultiSelectInspector()
        {
            var countLabel = new Label($"{selection.Count}件選択");
            countLabel.AddToClassList("prop-note");
            inspectorHost.Add(countLabel);

            var deleteBtn = new Button(DeleteSelection) { text = $"選択した{selection.Count}件を削除" };
            deleteBtn.AddToClassList("tb-btn");
            inspectorHost.Add(deleteBtn);

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
                var note = new Label("種別の一括変更はTap/Ex Tap/Flickのみ対応（Slideを含む選択では非表示）");
                note.AddToClassList("prop-note");
                inspectorHost.Add(note);
            }
        }

        /// <summary>
        /// §7.3: イベントレーンで選択したBPM/拍子/ソフランイベントの編集UI。ノーツと違い
        /// song.bpmEvents/song.metersはUndoスナップショット(chart+headerのみ)の対象外なので、
        /// 既存のタイトル等song.muses系フィールドと同じくPushUndoを呼ばない
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
                        var m = song.meters[selectedEventIndex];
                        m.bar = Mathf.Max(1, v);
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
            transport.Add(MakeTransportButton("|◀", () => { cursorTick = 0; preview.Seek(0f); }));
            transport.Add(MakeTransportButton("■", () => { preview.Pause(); preview.Seek(TickToSeconds(cursorTick)); }));
            playButton = MakeTransportButton("▶", TogglePlayFromCursor);
            transport.Add(playButton);
            transport.Add(MakeTransportButton("▶|", () =>
            {
                preview.Seek(preview.ChartEndSec);
                cursorTick = Mathf.Max(0, ChartFormat.SecondsToTick(chart.bpmEvents, preview.ChartEndSec));
            }));

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
            zoomSlider = new Slider(8f, 240f) { value = pxPerBeat };
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
                if (!preview.IsPlaying)
                    cursorTick = Mathf.Max(0, ChartFormat.SecondsToTick(chart.bpmEvents, evt.newValue));
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
            pxPerBeat = Mathf.Clamp(value, 8f, 240f);
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
                }

                // スライダー/トグル/ドロップダウンは入力途中という状態が無いので毎フレーム追従させる。
                // ノーツシート側のCtrl+ホイール(拡大縮小)やメニューの「再生に追従」もここで反映される。
                viewFollow.SetValueWithoutNotify(followPlayback);
                viewJudgeLine.SetValueWithoutNotify(judgeLineFrac);
                viewRate.SetValueWithoutNotify(preview.Rate);
                viewHeightLane.SetValueWithoutNotify(showHeightLane);
                if (snapDropdown.index != snapIndex) snapDropdown.index = snapIndex;
                zoomSlider.SetValueWithoutNotify(pxPerBeat);
                zoomLabel.text = $"{pxPerBeat / 28f:0.00}x";
                playButton.text = preview.IsPlaying ? "❙❙" : "▶";

                float scrubMax = Mathf.Max(10f, preview.ChartEndSec + 2f);
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

            // 選択が変わった / 中継点が増減したときだけインスペクタを組み直し、
            // それ以外の間はキャンバス上のドラッグ結果を各フィールドへ反映するだけにする。
            // selectedNoteは複数選択中は常にnullなので、selection.Countも見ないと
            // 「1件選択→矩形選択で別の2件」のように中身が変わっても再構築されない場合がある。
            if (!ReferenceEquals(inspectorNote, selectedNote)
                || (selectedNote != null && inspectorPointCount != selectedNote.points.Count)
                || inspectorEventKind != selectedEventKind || inspectorEventIndex != selectedEventIndex
                || inspectorSelectionCount != selection.Count)
            {
                inspectorNote = selectedNote;
                inspectorPointCount = selectedNote?.points.Count ?? 0;
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
            return $"{SongAddr.FormatAddr(addr)}  │  {meter.numerator}/{meter.denominator}  │  {bpmText}";
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

        /// <summary>
        /// ネイティブのファイル選択ダイアログが無いスタンドアロン向けの代替（editor-spec.md の
        /// 簡易ファイルブラウザをUI Toolkitへ移植したもの）。saveMode時はファイル名入力欄が付く。
        /// </summary>
        private void ShowFileModal(bool saveMode)
        {
            var modal = ShowModal(saveMode ? "別名で保存" : "譜面ファイルを開く");

            var pathLabel = new Label(browseDir);
            pathLabel.AddToClassList("prop-note");
            modal.Add(pathLabel);

            var list = new ScrollView();
            list.AddToClassList("browse-list");
            modal.Add(list);

            var nameField = new TextField("ファイル名") { value = Path.GetFileName(chartFilePathBuffer ?? "") };
            if (saveMode) modal.Add(nameField);

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

                    foreach (var file in Directory.GetFiles(browseDir, "*.muses").OrderBy(f => f))
                    {
                        string captured = file;
                        var b = new Button(() =>
                        {
                            if (saveMode)
                            {
                                nameField.value = Path.GetFileName(captured);
                                return;
                            }
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
                    string name = nameField.value;
                    if (string.IsNullOrWhiteSpace(name)) return;
                    if (!name.EndsWith(".muses", StringComparison.OrdinalIgnoreCase)) name += ".muses";
                    chartFilePathBuffer = Path.Combine(browseDir, name);
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
            PlayerPrefs.SetString("ChartEditor_LastDir", browseDir);
            PlayerPrefs.Save();
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

        private void MarkPreviewDirty()
        {
            // 譜面未読み込み(songPathがnull)でも「新規」から呼ばれうるので、音源ディレクトリは空で渡す
            string audioDir = string.IsNullOrEmpty(songPath) ? null : Path.GetDirectoryName(songPath);
            preview.Rebuild(song, chart, audioDir);
            lastPreviewRebuildRealtime = Time.unscaledTime;
        }

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
