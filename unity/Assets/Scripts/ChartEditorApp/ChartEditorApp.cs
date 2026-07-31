using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using Muses.Chart;

namespace Muses.ChartTool
{
    /// <summary>
    /// editor-spec.md §2,3。譜面エディタのノーツシート（主キャンバス）・ツールパレット・インスペクタ。
    ///
    /// **Unity Editor拡張ではなく、単独ビルドとして動くランタイムツール**として実装している
    /// （2026-07-31、ユーザーとの相談で方針転換。旧実装は Assets/Editor/ChartEditorWindow.cs
    /// だったが、Editorフォルダはプレイヤービルドから除外されるため、この MonoBehaviour + OnGUI
    /// 方式に書き直した）。空のGameObjectにアタッチしたシーンを作り、そのシーンだけをビルドする
    /// ことでゲーム本体とは別の実行ファイルになる。
    ///
    /// §5（プレビュー: 音源同期再生・オートプレイ・RenderTexture 3Dプレビュー）は
    /// <see cref="PreviewSystem"/> に、§4（検証）は <see cref="Muses.Chart.ChartValidator"/> に、
    /// §6（Undo/Redo・自動保存）はこのクラス内に実装済み。波形+イベントレーンはまだ未実装。
    /// 現時点でのスコープ: ファイルの読み書き（OSネイティブのファイル選択ダイアログは無く、
    /// パスを直接テキスト入力する簡易UI）、ノーツの配置/選択/平行移動/削除、Slideの中継点追加、
    /// インスペクタでの数値編集、プレビュー再生、検証、Undo/Redo、自動保存。
    /// 矩形選択・コピペ・一括変換・端のドラッグでの幅変更はまだ無い。
    /// </summary>
    public class ChartEditorApp : MonoBehaviour
    {
        private enum EditorTool { Select, Tap, ExTap, Slide, Flick, AddWaypoint, Delete }

        private const int Cells = 12;
        private static readonly int[] SnapDenominators = { 4, 8, 12, 16, 24, 32, 48, 64 };

        [Header("§5 プレビュー用（ゲーム本体シーンと同じシェーダ資産を割り当てる）")]
        [SerializeField] private Shader stageShader;
        [SerializeField] private Shader noteShader;
        [SerializeField] private Shader beatLineShader;

        private PreviewSystem preview;
        private float lastPreviewRebuildRealtime = -999f;

        // ---- §4 検証 ----
        private List<ValidationIssue> validationIssues = new();
        private bool validationPanelOpen;
        private bool validateOnSave = true;
        private Vector2 validationScroll;

        // ---- §6 Undo/Redo ----
        private struct UndoSnapshot
        {
            public ChartData chart;
            public ChartFileHeader header;
        }
        private readonly List<UndoSnapshot> undoStack = new();
        private readonly List<UndoSnapshot> redoStack = new();
        private const int UndoLimit = 80;
        private const float UndoCoalesceSec = 0.5f; // この秒数以内の連続編集は1手にまとめる（スライダー操作等）
        private float lastUndoPushRealtime = -999f;

        // ---- §6 自動保存 ----
        private const float AutosaveIntervalSec = 5f * 60f;
        private float lastAutosaveRealtime = -999f;
        private bool showRestorePrompt;
        private string restoreAutosavePath;

        // ---- ファイル状態 ----
        private string chartFilePathBuffer = "";
        private string chartPath;
        private string songPath;
        private SongMeta song = new();
        private ChartData chart = new();
        private ChartFileHeader header = new() { difficulty = "CUBE", level = 1, charter = "", songFile = "song.muses" };
        private bool dirty;
        private string statusMessage = "";

        // ---- 表示/編集状態 ----
        private int snapIndex = 3; // 1/16 既定
        private float defaultWidthCells = 1f;
        private float pxPerBeat = 28f;
        private int scrollTick;
        private EditorTool currentTool = EditorTool.Select;
        private Note selectedNote;
        private Note pendingSlideStart;

        private bool draggingNote;
        private int dragOriginRawTick;
        private float dragOriginRawCell;
        private List<Waypoint> dragOriginPoints;

        // ---- ランタイムGUI用の下地 ----
        private Texture2D whiteTex;
        private GUIStyle boldStyle;
        private GUIStyle panelStyle;
        private readonly Dictionary<string, string> textBuffers = new();

        private void Awake()
        {
            preview = new PreviewSystem(this, stageShader, noteShader, beatLineShader);
        }

        private void Update()
        {
            preview.Tick();

            // 編集中は毎フレーム再構築しない。ドラッグ終了後・一定間隔をおいて反映する
            // （chart.notes を直接ドラッグしている最中にtick→秒の再解決やNoteView再構築を挟むと重い上、無駄）。
            if (dirty && !draggingNote && Time.unscaledTime - lastPreviewRebuildRealtime > 0.3f)
            {
                preview.Rebuild(song, chart, Path.GetDirectoryName(songPath));
                lastPreviewRebuildRealtime = Time.unscaledTime;
            }

            HandleUndoRedoShortcuts();
            TickAutosave();
        }

        private void HandleUndoRedoShortcuts()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb == null) return;
            bool cmdOrCtrl = kb.leftCommandKey.isPressed || kb.rightCommandKey.isPressed
                              || kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
            if (!cmdOrCtrl) return;
            bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            if (kb.zKey.wasPressedThisFrame)
            {
                if (shift) Redo(); else Undo();
            }
            else if (kb.yKey.wasPressedThisFrame)
            {
                Redo();
            }
        }

        private void OnDestroy()
        {
            preview?.Dispose();
        }

        private void EnsureStyles()
        {
            if (whiteTex != null) return;
            whiteTex = new Texture2D(1, 1);
            whiteTex.SetPixel(0, 0, Color.white);
            whiteTex.Apply();

            boldStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            panelStyle = new GUIStyle(GUI.skin.box);
        }

        private void DrawRect(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, whiteTex);
            GUI.color = old;
        }

        private void OnGUI()
        {
            EnsureStyles();

            const float toolbarH = 44f;
            const float paletteW = 130f;
            const float inspectorW = 280f;
            const float previewH = 260f; // editor-spec.md §2: 右側は上に3Dプレビュー、下にインスペクタ
            const float validationHeaderH = 24f;
            const float validationOpenH = 160f;

            float validationH = validationPanelOpen ? validationOpenH : validationHeaderH;
            float bodyH = Mathf.Max(0, Screen.height - toolbarH - validationH);

            var toolbarRect = new Rect(0, 0, Screen.width, toolbarH);
            var paletteRect = new Rect(0, toolbarH, paletteW, bodyH);
            var rightColRect = new Rect(Screen.width - inspectorW, toolbarH, inspectorW, bodyH);
            var previewRect = new Rect(rightColRect.x, rightColRect.y, rightColRect.width, Mathf.Min(previewH, rightColRect.height));
            var inspectorRect = new Rect(rightColRect.x, previewRect.yMax, rightColRect.width, Mathf.Max(0, rightColRect.height - previewRect.height));
            var sheetRect = new Rect(paletteW, toolbarH, Mathf.Max(0, Screen.width - paletteW - inspectorW), bodyH);
            var validationRect = new Rect(0, toolbarH + bodyH, Screen.width, validationH);

            DrawToolbar(toolbarRect);
            DrawPalette(paletteRect);
            DrawNotesSheet(sheetRect);
            preview.Draw(previewRect);
            DrawInspector(inspectorRect);
            DrawValidationPanel(validationRect);
            DrawRestorePrompt();
        }

        // ---------- ツールバー ----------

        private void DrawToolbar(Rect rect)
        {
            DrawRect(rect, new Color(0.22f, 0.22f, 0.22f));
            GUILayout.BeginArea(rect);
            GUILayout.BeginHorizontal();

            GUILayout.Label("ファイル", GUILayout.Width(45));
            chartFilePathBuffer = BufferedTextFieldFixedWidth("chartPathField", chartFilePathBuffer, 320);

            if (GUILayout.Button("読み込む", GUILayout.Width(70))) OpenChartFromPath();
            if (GUILayout.Button("保存", GUILayout.Width(50))) SaveChartToPath();
            if (GUILayout.Button("検証", GUILayout.Width(50))) RunValidation();

            GUILayout.Space(6);
            GUI.enabled = undoStack.Count > 0;
            if (GUILayout.Button("元に戻す", GUILayout.Width(60))) Undo();
            GUI.enabled = redoStack.Count > 0;
            if (GUILayout.Button("やり直す", GUILayout.Width(60))) Redo();
            GUI.enabled = true;

            GUILayout.Space(10);
            GUILayout.Label("snap", GUILayout.Width(30));
            var snapLabels = new string[SnapDenominators.Length];
            for (int i = 0; i < SnapDenominators.Length; i++) snapLabels[i] = $"1/{SnapDenominators[i]}";
            snapIndex = GUILayout.SelectionGrid(snapIndex, snapLabels, snapLabels.Length, GUILayout.Width(320));

            GUILayout.Space(10);
            GUILayout.Label("倍率", GUILayout.Width(26));
            pxPerBeat = GUILayout.HorizontalSlider(pxPerBeat, 8f, 240f, GUILayout.Width(100));

            GUILayout.FlexibleSpace();

            string posLabel = selectedNote != null
                ? SongAddr.FormatAddr(SongAddr.ToAddr(song.meters, selectedNote.points[0].tick))
                : "-";
            GUILayout.Label($"位置 {posLabel}", GUILayout.Width(140));
            GUILayout.Label(dirty ? "● 未保存" : "保存済み", GUILayout.Width(70));

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("幅", GUILayout.Width(16));
            defaultWidthCells = Mathf.Max(0.5f, ParseFloatField("defaultWidth", defaultWidthCells, 60));
            GUILayout.Label(statusMessage);
            GUILayout.EndHorizontal();

            GUILayout.EndArea();
        }

        private void OpenChartFromPath()
        {
            string path = chartFilePathBuffer;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                statusMessage = $"ファイルが見つかりません: {path}";
                return;
            }

            string dir = Path.GetDirectoryName(path);
            string songFilePath = Path.Combine(dir ?? "", "song.muses");
            if (!File.Exists(songFilePath))
            {
                statusMessage = $"同じフォルダに song.muses がありません: {songFilePath}";
                return;
            }

            try
            {
                var loadedSong = ChartSerializer.ReadSongMeta(songFilePath);
                var (loadedHeader, loadedChart) = ChartSerializer.ReadChart(path, loadedSong);
                song = loadedSong;
                header = loadedHeader;
                chart = loadedChart;
                songPath = songFilePath;
                chartPath = path;
                selectedNote = null;
                pendingSlideStart = null;
                draggingNote = false;
                dirty = false;
                undoStack.Clear();
                redoStack.Clear();
                lastAutosaveRealtime = Time.unscaledTime;
                statusMessage = "読み込み完了";
                preview.Rebuild(song, chart, dir);
                lastPreviewRebuildRealtime = Time.unscaledTime;
                CheckAutosaveRestore(path);
            }
            catch (Exception ex)
            {
                statusMessage = "読み込みエラー: " + ex.Message;
            }
        }

        private void SaveChartToPath()
        {
            string path = chartFilePathBuffer;
            if (string.IsNullOrEmpty(path))
            {
                statusMessage = "保存先パスを入力してください";
                return;
            }
            try
            {
                ChartSerializer.WriteChart(path, header, chart, song);
                chartPath = path;
                dirty = false;
                statusMessage = "保存完了";
                if (validateOnSave) RunValidation();
            }
            catch (Exception ex)
            {
                statusMessage = "保存エラー: " + ex.Message;
            }
        }

        // ---------- §6 Undo/Redo ----------

        /// <summary>
        /// editor-spec.md §6.1はテキストシリアライザでのスナップショットを提案しているが、
        /// ChartData はList&lt;Note&gt;+List&lt;Waypoint&gt;(構造体)という単純なグラフなので、
        /// ファイルI/Oを挟まないインメモリのディープコピーの方が高速かつ往復精度の
        /// 心配（tick↔bar:beat:tick変換等）が無く優れている。二重実装は数行で済むため許容。
        /// </summary>
        private static ChartData CloneChart(ChartData src)
        {
            var c = new ChartData
            {
                bpmEvents = new List<BpmEvent>(src.bpmEvents),
                scrollEvents = new List<ScrollEvent>(src.scrollEvents),
            };
            foreach (var n in src.notes)
                c.notes.Add(new Note
                {
                    kind = n.kind,
                    scrollGroup = n.scrollGroup,
                    points = new List<Waypoint>(n.points),
                    comboTimes = new List<float>(n.comboTimes),
                });
            return c;
        }

        private UndoSnapshot CaptureSnapshot() => new() { chart = CloneChart(chart), header = header };

        /// <summary>変更を適用する直前に呼ぶ。coalesce=trueは「直前の記録から一定時間内なら1手にまとめる」
        /// （スライダー操作など、フレームごとに変更が飛んでくる編集向け）。</summary>
        private void PushUndo(bool coalesce)
        {
            float now = Time.unscaledTime;
            if (coalesce && undoStack.Count > 0 && now - lastUndoPushRealtime < UndoCoalesceSec)
            {
                lastUndoPushRealtime = now;
                return; // 直前の変更前スナップショットをそのまま使う(既に積んである)
            }
            undoStack.Add(CaptureSnapshot());
            if (undoStack.Count > UndoLimit) undoStack.RemoveAt(0);
            redoStack.Clear();
            lastUndoPushRealtime = now;
        }

        private void Undo()
        {
            if (undoStack.Count == 0) return;
            redoStack.Add(CaptureSnapshot());
            var snap = undoStack[^1];
            undoStack.RemoveAt(undoStack.Count - 1);
            ApplySnapshot(snap);
        }

        private void Redo()
        {
            if (redoStack.Count == 0) return;
            undoStack.Add(CaptureSnapshot());
            var snap = redoStack[^1];
            redoStack.RemoveAt(redoStack.Count - 1);
            ApplySnapshot(snap);
        }

        private void ApplySnapshot(UndoSnapshot snap)
        {
            chart = snap.chart;
            header = snap.header;
            selectedNote = null; // 復元後は参照が切れるため選択解除
            pendingSlideStart = null;
            draggingNote = false;
            dirty = true;
        }

        // ---------- §6 自動保存 ----------

        private void TickAutosave()
        {
            if (!dirty || string.IsNullOrEmpty(chartPath)) return;
            if (Time.unscaledTime - lastAutosaveRealtime < AutosaveIntervalSec) return;
            try
            {
                ChartSerializer.WriteChart(chartPath + ".autosave", header, chart, song);
                lastAutosaveRealtime = Time.unscaledTime;
            }
            catch (Exception ex)
            {
                statusMessage = "自動保存エラー: " + ex.Message;
            }
        }

        /// <summary>読み込み直後に呼ぶ。autosaveの方が正規ファイルより新しければ復元を提案する。</summary>
        private void CheckAutosaveRestore(string path)
        {
            string autosavePath = path + ".autosave";
            if (!File.Exists(autosavePath)) return;
            if (File.GetLastWriteTimeUtc(autosavePath) <= File.GetLastWriteTimeUtc(path)) return;
            showRestorePrompt = true;
            restoreAutosavePath = autosavePath;
        }

        private void DrawRestorePrompt()
        {
            if (!showRestorePrompt) return;
            const float w = 480f, h = 60f;
            var rect = new Rect((Screen.width - w) / 2f, 60f, w, h);
            DrawRect(rect, new Color(0.05f, 0.05f, 0.05f, 0.95f));
            GUILayout.BeginArea(rect);
            GUILayout.Label("自動保存ファイルの方が新しいです。復元しますか？", boldStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("復元する", GUILayout.Width(100)))
            {
                try
                {
                    var (loadedHeader, loadedChart) = ChartSerializer.ReadChart(restoreAutosavePath, song);
                    header = loadedHeader;
                    chart = loadedChart;
                    undoStack.Clear();
                    redoStack.Clear();
                    selectedNote = null;
                    pendingSlideStart = null;
                    dirty = true;
                    statusMessage = "自動保存ファイルから復元しました";
                    preview.Rebuild(song, chart, Path.GetDirectoryName(chartPath));
                }
                catch (Exception ex)
                {
                    statusMessage = "復元エラー: " + ex.Message;
                }
                showRestorePrompt = false;
            }
            if (GUILayout.Button("無視する", GUILayout.Width(100))) showRestorePrompt = false;
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // ---------- §4 検証 ----------

        /// <summary>editor-spec.md §4。常時実行はしない。[検証]ボタン・保存時にのみ呼ぶ。</summary>
        private void RunValidation()
        {
            // ChartValidator は chart.bpmEvents(V1) と Waypoint.time/Note.comboTimes を読むため、
            // 検証前に必ず再解決する（エディタでの編集はtickのみを直接書き換え、
            // timeの再計算はプレビューの再構築時にしか走らないため）。
            // ChartSerializer.ReadChart / PreviewSystem.Rebuild と同じ規則: BPMは曲の属性なので
            // 都度 song 側からコピーする（元に戻さず、以後も song と同期した状態を維持する）。
            chart.bpmEvents = new List<BpmEvent>(song.bpmEvents);
            ChartFormat.ResolveTimes(chart);
            ChartFormat.ResolveSlideComboPoints(chart);

            validationIssues = ChartValidator.Validate(chart, Cells, preview.AudioLengthSec);
            validationPanelOpen = true;
        }

        private void DrawValidationPanel(Rect rect)
        {
            DrawRect(rect, new Color(0.14f, 0.14f, 0.14f));
            var header = new Rect(rect.x, rect.y, rect.width, 24);
            GUI.Box(header, "");
            if (GUI.Button(new Rect(header.x + 2, header.y + 1, 20, 22), validationPanelOpen ? "▼" : "▶"))
                validationPanelOpen = !validationPanelOpen;

            int errors = 0, warnings = 0, infos = 0;
            foreach (var iss in validationIssues)
            {
                if (iss.severity == ValidationSeverity.Error) errors++;
                else if (iss.severity == ValidationSeverity.Warning) warnings++;
                else infos++;
            }
            GUI.Label(new Rect(header.x + 26, header.y + 3, 300, 20),
                $"検証結果: エラー{errors} 警告{warnings} 情報{infos}");

            if (!validationPanelOpen) return;

            var bodyRect = new Rect(rect.x, header.yMax, rect.width, rect.height - header.height);
            GUILayout.BeginArea(bodyRect);
            GUILayout.BeginHorizontal();
            bool newValidateOnSave = GUILayout.Toggle(validateOnSave, "保存時に自動実行", GUILayout.Width(120));
            if (newValidateOnSave != validateOnSave) validateOnSave = newValidateOnSave;
            GUILayout.EndHorizontal();

            validationScroll = GUILayout.BeginScrollView(validationScroll);
            foreach (var iss in validationIssues)
            {
                var color = iss.severity switch
                {
                    ValidationSeverity.Error => new Color(1f, 0.5f, 0.5f),
                    ValidationSeverity.Warning => new Color(1f, 0.85f, 0.4f),
                    _ => Color.white,
                };
                var old = GUI.color;
                GUI.color = color;
                if (GUILayout.Button($"[{iss.id}] {iss.message}", GUI.skin.label))
                {
                    scrollTick = Mathf.Max(0, iss.tick - 2000);
                }
                GUI.color = old;
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // ---------- ツールパレット ----------

        private void DrawPalette(Rect rect)
        {
            DrawRect(rect, new Color(0.18f, 0.18f, 0.18f));
            GUILayout.BeginArea(rect);
            GUILayout.Label("ツール", boldStyle);
            DrawToolButton(EditorTool.Select, "選択");
            DrawToolButton(EditorTool.Tap, "Tap");
            DrawToolButton(EditorTool.ExTap, "Ex Tap");
            DrawToolButton(EditorTool.Slide, "Slide");
            DrawToolButton(EditorTool.Flick, "Flick");
            DrawToolButton(EditorTool.AddWaypoint, "Waypoint追加");
            DrawToolButton(EditorTool.Delete, "削除");

            GUILayout.Space(12);
            GUILayout.Label("譜面情報", boldStyle);
            GUILayout.Label("難易度");
            header.difficulty = GUILayout.TextField(header.difficulty ?? "");
            GUILayout.Label("レベル");
            header.level = Mathf.RoundToInt(ParseFloatField("headerLevel", header.level, 100));
            GUILayout.Label("譜面制作者");
            header.charter = GUILayout.TextField(header.charter ?? "");

            GUILayout.EndArea();
        }

        private void DrawToolButton(EditorTool tool, string label)
        {
            bool selected = currentTool == tool;
            var old = GUI.backgroundColor;
            GUI.backgroundColor = selected ? new Color(0.5f, 0.7f, 1f) : Color.white;
            if (GUILayout.Button(label))
            {
                currentTool = tool;
                pendingSlideStart = null;
            }
            GUI.backgroundColor = old;
        }

        // ---------- インスペクタ ----------

        private void DrawInspector(Rect rect)
        {
            DrawRect(rect, new Color(0.18f, 0.18f, 0.18f));
            GUILayout.BeginArea(rect);
            GUILayout.Label("インスペクタ", boldStyle);

            if (selectedNote == null)
            {
                GUILayout.Label("ノーツを選択してください");
                GUILayout.EndArea();
                return;
            }

            GUILayout.Label($"kind: {selectedNote.kind}");
            int newGroup = Mathf.RoundToInt(ParseFloatField("scrollGroup", selectedNote.scrollGroup, 200));
            if (newGroup != selectedNote.scrollGroup)
            {
                PushUndo(coalesce: true);
                selectedNote.scrollGroup = Mathf.Max(0, newGroup);
                dirty = true;
            }

            for (int i = 0; i < selectedNote.points.Count; i++)
            {
                GUILayout.Space(6);
                string role = i == 0 ? " (始点)" : i == selectedNote.points.Count - 1 ? " (終点)" : "";
                GUILayout.Label($"Waypoint {i}{role}", boldStyle);

                var wp = selectedNote.points[i];
                string idPrefix = $"wp{i}_";

                var addr = SongAddr.ToAddr(song.meters, wp.tick);
                string addrText = BufferedTextField(idPrefix + "addr", SongAddr.FormatAddr(addr));

                GUILayout.Label("layerF");
                float layerF = GUILayout.HorizontalSlider(wp.layerF, 0f, 1f);

                GUILayout.Label("cellF");
                float cellF = ParseFloatField(idPrefix + "cellF", wp.cellF, 200);
                GUILayout.Label("width");
                float width = ParseFloatField(idPrefix + "width", wp.width, 200);

                var easing = EnumCycleButton("easing", wp.easing);
                var marker = EnumCycleButton("marker", wp.marker);

                bool hasCombo = GUILayout.Toggle(wp.comboStep.HasValue, "comboStep上書き");
                int comboVal = wp.comboStep ?? 0;
                if (hasCombo)
                {
                    GUILayout.Label("comboStep(tick)");
                    comboVal = Mathf.RoundToInt(ParseFloatField(idPrefix + "combo", comboVal, 200));
                }

                bool changed = !Mathf.Approximately(layerF, wp.layerF)
                               || !Mathf.Approximately(cellF, wp.cellF)
                               || !Mathf.Approximately(width, wp.width)
                               || easing != wp.easing
                               || marker != wp.marker
                               || hasCombo != wp.comboStep.HasValue
                               || (hasCombo && comboVal != (wp.comboStep ?? int.MinValue));

                int newTick = wp.tick;
                bool addrValid = true;
                try
                {
                    var parsed = SongAddr.ParseAddr(addrText);
                    newTick = SongAddr.ToTick(song.meters, parsed.bar, parsed.beat, parsed.tick);
                }
                catch (FormatException)
                {
                    addrValid = false; // 無効なaddr文字列はtickの変更を無視する
                }
                if (addrValid && newTick != wp.tick) changed = true;

                if (changed)
                {
                    PushUndo(coalesce: true); // スライダー/テキスト編集はフレームごとに来るので一定時間内はまとめる
                    wp.tick = addrValid ? newTick : wp.tick;
                    wp.layerF = Mathf.Clamp01(layerF);
                    wp.cellF = cellF;
                    wp.width = Mathf.Max(0.1f, width);
                    wp.easing = easing;
                    wp.marker = marker;
                    wp.comboStep = hasCombo ? comboVal : null;
                    selectedNote.points[i] = wp;
                    dirty = true;
                }

                if (selectedNote.kind == NoteKind.Slide && selectedNote.points.Count > 2)
                {
                    if (GUILayout.Button("この中継点を削除"))
                    {
                        PushUndo(coalesce: false);
                        selectedNote.points.RemoveAt(i);
                        dirty = true;
                        break;
                    }
                }
            }

            GUILayout.Space(8);
            if (GUILayout.Button("このノーツを削除"))
            {
                PushUndo(coalesce: false);
                chart.notes.Remove(selectedNote);
                selectedNote = null;
                dirty = true;
            }

            GUILayout.EndArea();
        }

        /// <summary>フォーカス中は上書きせず、フォーカスが外れているときだけ model の値で同期するテキストフィールド。</summary>
        private string BufferedTextField(string controlName, string modelValue)
        {
            bool focused = GUI.GetNameOfFocusedControl() == controlName;
            if (!focused) textBuffers[controlName] = modelValue;
            else if (!textBuffers.ContainsKey(controlName)) textBuffers[controlName] = modelValue;

            GUI.SetNextControlName(controlName);
            textBuffers[controlName] = GUILayout.TextField(textBuffers[controlName], GUILayout.Width(140));
            return textBuffers[controlName];
        }

        private float ParseFloatField(string controlName, float modelValue, float width)
        {
            string text = BufferedTextFieldFixedWidth(controlName, modelValue.ToString("0.###", CultureInfo.InvariantCulture), width);
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : modelValue;
        }

        private string BufferedTextFieldFixedWidth(string controlName, string modelValue, float width)
        {
            bool focused = GUI.GetNameOfFocusedControl() == controlName;
            if (!focused) textBuffers[controlName] = modelValue;
            else if (!textBuffers.ContainsKey(controlName)) textBuffers[controlName] = modelValue;

            GUI.SetNextControlName(controlName);
            textBuffers[controlName] = GUILayout.TextField(textBuffers[controlName], GUILayout.Width(width));
            return textBuffers[controlName];
        }

        private static T EnumCycleButton<T>(string label, T current) where T : struct, Enum
        {
            var values = (T[])Enum.GetValues(typeof(T));
            int idx = Array.IndexOf(values, current);
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(70));
            if (GUILayout.Button(current.ToString(), GUILayout.Width(100)))
                idx = (idx + 1) % values.Length;
            GUILayout.EndHorizontal();
            return values[idx];
        }

        // ---------- ノーツシート（主キャンバス） ----------

        private void DrawNotesSheet(Rect rect)
        {
            DrawRect(rect, new Color(0.16f, 0.16f, 0.16f));
            if (rect.width <= 1f || rect.height <= 1f) return;

            const float gutterW = 26f;
            float paneW = (rect.width - gutterW) * 0.5f;
            var groundRect = new Rect(rect.x, rect.y, paneW, rect.height);
            var gutterRect = new Rect(groundRect.xMax, rect.y, gutterW, rect.height);
            var skyRect = new Rect(gutterRect.xMax, rect.y, paneW, rect.height);

            DrawRect(gutterRect, new Color(0.1f, 0.1f, 0.1f));
            GUI.Label(new Rect(groundRect.x, rect.y, groundRect.width, 16), "Ground");
            GUI.Label(new Rect(skyRect.x, rect.y, skyRect.width, 16), "Sky");

            float pxPerTick = pxPerBeat / ChartData.TicksPerBeat;
            int visibleTicks = Mathf.CeilToInt(rect.height / pxPerTick);

            float TickToY(int tick) => rect.yMax - (tick - scrollTick) * pxPerTick;
            int YToTick(float y) => scrollTick + Mathf.RoundToInt((rect.yMax - y) / pxPerTick);
            float CellX(Rect pane, float cellF) => pane.x + cellF / Cells * pane.width;
            float CombinedX(float layerF, float cellF) => Mathf.Lerp(CellX(groundRect, cellF), CellX(skyRect, cellF), Mathf.Clamp01(layerF));

            // セル境界線
            for (int c = 0; c <= Cells; c++)
            {
                DrawRect(new Rect(CellX(groundRect, c), rect.y, 1, rect.height), new Color(1, 1, 1, 0.08f));
                DrawRect(new Rect(CellX(skyRect, c), rect.y, 1, rect.height), new Color(1, 1, 1, 0.08f));
            }

            // 小節/拍/スナップ線
            int snapTicks = Mathf.Max(1, SongAddr.TicksPerBeatUnit(SnapDenominators[snapIndex]));
            int lineStart = Mathf.Max(0, scrollTick - snapTicks) / snapTicks * snapTicks;
            int lineEnd = scrollTick + visibleTicks + snapTicks;
            int guard = 0;
            for (int t = lineStart; t <= lineEnd && guard < 20000; t += snapTicks, guard++)
            {
                float y = TickToY(t);
                if (y < rect.y - 4 || y > rect.yMax + 4) continue;

                var addr = SongAddr.ToAddr(song.meters, t);
                Color c;
                float thickness;
                if (addr.beat == 1 && addr.tick == 0) { c = new Color(1, 1, 1, 0.5f); thickness = 2f; }
                else if (addr.tick == 0) { c = new Color(1, 1, 1, 0.28f); thickness = 1f; }
                else { c = new Color(1, 1, 1, 0.12f); thickness = 1f; }

                DrawRect(new Rect(rect.x, y, rect.width, thickness), c);
                if (addr.beat == 1 && addr.tick == 0)
                    GUI.Label(new Rect(rect.x + 2, y - 14, 60, 14), addr.bar.ToString());
            }

            // ノーツ描画
            foreach (var note in chart.notes)
            {
                int nStart = note.points[0].tick;
                int nEnd = note.points[^1].tick;
                if (nEnd < scrollTick - snapTicks * 4 || nStart > scrollTick + visibleTicks + snapTicks * 4) continue;

                Color col = NoteColor(note.kind);
                if (note.points.Count == 1)
                {
                    var wp = note.points[0];
                    float y = TickToY(wp.tick);
                    float x0 = CombinedX(wp.layerF, wp.cellF);
                    float x1 = CombinedX(wp.layerF, wp.cellF + wp.width);
                    DrawRect(Rect.MinMaxRect(Mathf.Min(x0, x1), y - 4, Mathf.Max(x0, x1), y + 4), col);
                }
                else
                {
                    int stepTicks = Mathf.Max(1, Mathf.RoundToInt(4f / pxPerTick));
                    for (int t = nStart; t < nEnd; t += stepTicks)
                    {
                        int t2 = Mathf.Min(t + stepTicks, nEnd);
                        float ya = TickToY(t), yb = TickToY(t2);
                        if (yb > rect.yMax + 8 || ya < rect.y - 8) continue;

                        var a = InterpAtTick(note, t);
                        float xa0 = CombinedX(a.layerF, a.cellF);
                        float xa1 = CombinedX(a.layerF, a.cellF + a.width);
                        DrawRect(
                            Rect.MinMaxRect(Mathf.Min(xa0, xa1), Mathf.Min(ya, yb) - 1, Mathf.Max(xa0, xa1), Mathf.Max(ya, yb) + 1),
                            new Color(col.r, col.g, col.b, 0.55f));
                    }

                    foreach (var wp in note.points)
                    {
                        if (wp.marker != WaypointMarker.Visible) continue;
                        float y = TickToY(wp.tick);
                        float x = CombinedX(wp.layerF, wp.cellF);
                        DrawRect(new Rect(x - 3, y - 3, 6, 6), Color.white);
                    }
                }

                if (ReferenceEquals(note, selectedNote))
                {
                    var startWp = note.points[0];
                    var endWp = note.points[^1];
                    float y0 = TickToY(nStart), y1 = TickToY(nEnd);
                    float sx0 = CombinedX(startWp.layerF, startWp.cellF);
                    float sx1 = CombinedX(endWp.layerF, endWp.cellF + endWp.width);
                    var box = Rect.MinMaxRect(Mathf.Min(sx0, sx1) - 3, Mathf.Min(y0, y1) - 6, Mathf.Max(sx0, sx1) + 3, Mathf.Max(y0, y1) + 6);
                    DrawRectOutline(box, Color.yellow);
                }
            }

            HandleSheetInput(rect, groundRect, skyRect, TickToY, YToTick, CombinedX, snapTicks);
        }

        private void HandleSheetInput(
            Rect rect, Rect groundRect, Rect skyRect,
            Func<int, float> tickToY, Func<float, int> yToTick, Func<float, float, float> combinedX, int snapTicks)
        {
            var e = Event.current;
            bool overSheet = rect.Contains(e.mousePosition);

            if (e.type == EventType.ScrollWheel && overSheet)
            {
                if (e.control)
                {
                    pxPerBeat = Mathf.Clamp(pxPerBeat - e.delta.y * 2f, 8f, 240f);
                }
                else
                {
                    scrollTick = Mathf.Max(0, scrollTick + Mathf.RoundToInt(e.delta.y) * snapTicks);
                }
                e.Use();
                return;
            }

            (float layerF, float cellF) PaneAt(float x)
            {
                if (x >= groundRect.xMin && x <= groundRect.xMax)
                    return (0f, Mathf.Clamp((x - groundRect.x) / groundRect.width * Cells, 0f, Cells));
                if (x >= skyRect.xMin && x <= skyRect.xMax)
                    return (1f, Mathf.Clamp((x - skyRect.x) / skyRect.width * Cells, 0f, Cells));
                return (0.5f, Cells * 0.5f);
            }

            int SnapTickTo(int rawTick) => Mathf.RoundToInt((float)rawTick / snapTicks) * snapTicks;
            float SnapCellTo(float rawCell, float step) => Mathf.Round(rawCell / step) * step;

            if (e.type == EventType.MouseDown && e.button == 0 && overSheet)
            {
                int rawTick = Mathf.Max(0, yToTick(e.mousePosition.y));
                int tick = SnapTickTo(rawTick);
                var (layerF, rawCell) = PaneAt(e.mousePosition.x);

                switch (currentTool)
                {
                    case EditorTool.Tap:
                    case EditorTool.ExTap:
                    case EditorTool.Flick:
                    {
                        if (layerF != 0f && layerF != 1f) break; // ガターには単発ノーツを置かない
                        float cellF = SnapCellTo(rawCell, 1f);
                        var kind = currentTool == EditorTool.Tap ? NoteKind.Tap
                            : currentTool == EditorTool.ExTap ? NoteKind.ExTap : NoteKind.Flick;
                        var note = new Note
                        {
                            kind = kind,
                            points = new List<Waypoint> { NewWaypoint(tick, layerF, cellF, defaultWidthCells) },
                        };
                        PushUndo(coalesce: false);
                        chart.notes.Add(note);
                        selectedNote = note;
                        dirty = true;
                        break;
                    }
                    case EditorTool.Slide:
                    {
                        float cellF = SnapCellTo(rawCell, 0.5f);
                        if (pendingSlideStart == null)
                        {
                            pendingSlideStart = new Note
                            {
                                kind = NoteKind.Slide,
                                points = new List<Waypoint> { NewWaypoint(tick, layerF, cellF, defaultWidthCells) },
                            };
                        }
                        else
                        {
                            int startTick = pendingSlideStart.points[0].tick;
                            if (tick > startTick)
                            {
                                pendingSlideStart.points.Add(NewWaypoint(tick, layerF, cellF, defaultWidthCells));
                                PushUndo(coalesce: false);
                                chart.notes.Add(pendingSlideStart);
                                selectedNote = pendingSlideStart;
                                dirty = true;
                            }
                            pendingSlideStart = null;
                        }
                        break;
                    }
                    case EditorTool.AddWaypoint:
                    {
                        if (selectedNote is { kind: NoteKind.Slide })
                        {
                            float cellF = SnapCellTo(rawCell, 0.5f);
                            int insertAt = selectedNote.points.FindIndex(p => p.tick > tick);
                            if (insertAt < 0) insertAt = selectedNote.points.Count;
                            if (insertAt > 0 && insertAt < selectedNote.points.Count)
                            {
                                float width = InterpAtTick(selectedNote, tick).width;
                                PushUndo(coalesce: false);
                                selectedNote.points.Insert(insertAt, NewWaypoint(tick, layerF, cellF, width));
                                dirty = true;
                            }
                        }
                        break;
                    }
                    case EditorTool.Delete:
                    {
                        var hit = HitTestNote(e.mousePosition, tickToY, yToTick, combinedX);
                        if (hit != null)
                        {
                            PushUndo(coalesce: false);
                            chart.notes.Remove(hit);
                            if (ReferenceEquals(selectedNote, hit)) selectedNote = null;
                            dirty = true;
                        }
                        break;
                    }
                    case EditorTool.Select:
                    default:
                    {
                        var hit = HitTestNote(e.mousePosition, tickToY, yToTick, combinedX);
                        selectedNote = hit;
                        if (hit != null)
                        {
                            PushUndo(coalesce: false); // ドラッグ開始時点(変更前)を1手として記録する
                            draggingNote = true;
                            dragOriginRawTick = rawTick;
                            dragOriginRawCell = rawCell;
                            dragOriginPoints = new List<Waypoint>(hit.points);
                        }
                        break;
                    }
                }
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && e.button == 0 && draggingNote && selectedNote != null)
            {
                int rawTick = yToTick(e.mousePosition.y);
                var (_, rawCell) = PaneAt(e.mousePosition.x);

                int deltaTickRaw = rawTick - dragOriginRawTick;
                int deltaTick = Mathf.RoundToInt((float)deltaTickRaw / snapTicks) * snapTicks;
                float cellStep = selectedNote.kind == NoteKind.Slide ? 0.5f : 1f;
                float deltaCell = SnapCellTo(rawCell - dragOriginRawCell, cellStep);

                for (int i = 0; i < selectedNote.points.Count; i++)
                {
                    var wp = dragOriginPoints[i];
                    wp.tick = Mathf.Max(0, wp.tick + deltaTick);
                    wp.cellF += deltaCell;
                    selectedNote.points[i] = wp;
                }
                dirty = true;
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0)
            {
                draggingNote = false;
            }
            else if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace) && selectedNote != null)
            {
                PushUndo(coalesce: false);
                chart.notes.Remove(selectedNote);
                selectedNote = null;
                dirty = true;
                e.Use();
            }
        }

        private Note HitTestNote(Vector2 mouse, Func<int, float> tickToY, Func<float, int> yToTick, Func<float, float, float> combinedX)
        {
            for (int idx = chart.notes.Count - 1; idx >= 0; idx--)
            {
                var n = chart.notes[idx];
                if (n.points.Count == 1)
                {
                    var wp = n.points[0];
                    float y = tickToY(wp.tick);
                    if (Mathf.Abs(mouse.y - y) > 6f) continue;
                    float x0 = combinedX(wp.layerF, wp.cellF);
                    float x1 = combinedX(wp.layerF, wp.cellF + wp.width);
                    if (mouse.x >= Mathf.Min(x0, x1) - 2 && mouse.x <= Mathf.Max(x0, x1) + 2) return n;
                }
                else
                {
                    int tick = yToTick(mouse.y);
                    int nStart = n.points[0].tick, nEnd = n.points[^1].tick;
                    if (tick < nStart - 4 || tick > nEnd + 4) continue;
                    int clamped = Mathf.Clamp(tick, nStart, nEnd);
                    var s = InterpAtTick(n, clamped);
                    float x0 = combinedX(s.layerF, s.cellF);
                    float x1 = combinedX(s.layerF, s.cellF + s.width);
                    if (mouse.x >= Mathf.Min(x0, x1) - 4 && mouse.x <= Mathf.Max(x0, x1) + 4) return n;
                }
            }
            return null;
        }

        private static Waypoint NewWaypoint(int tick, float layerF, float cellF, float width) => new()
        {
            tick = tick,
            layerF = layerF,
            cellF = cellF,
            width = width,
            easing = Easing.Linear,
            marker = WaypointMarker.None,
            comboStep = null,
        };

        /// <summary>ChartMath.At と同じ補間ロジックだが time(秒) ではなく tick を軸にする（エディタ描画専用）。</summary>
        private static (float layerF, float cellF, float width) InterpAtTick(Note n, int tick)
        {
            var p = n.points;
            if (p.Count == 1 || tick <= p[0].tick) return (p[0].layerF, p[0].cellF, p[0].width);
            var last = p[^1];
            if (tick >= last.tick) return (last.layerF, last.cellF, last.width);

            for (int i = 0; i < p.Count - 1; i++)
            {
                var a = p[i];
                var b = p[i + 1];
                if (tick >= a.tick && tick <= b.tick)
                {
                    float k = b.tick == a.tick ? 0f : (float)(tick - a.tick) / (b.tick - a.tick);
                    float e = ChartMath.Ease(a.easing, k);
                    return (
                        a.layerF + (b.layerF - a.layerF) * e,
                        a.cellF + (b.cellF - a.cellF) * e,
                        a.width + (b.width - a.width) * e
                    );
                }
            }
            return (last.layerF, last.cellF, last.width);
        }

        private static Color NoteColor(NoteKind k) => k switch
        {
            NoteKind.Tap => new Color(0.3f, 0.8f, 0.9f),
            NoteKind.ExTap => new Color(0.95f, 0.8f, 0.25f),
            NoteKind.Slide => new Color(0.4f, 0.9f, 0.6f),
            NoteKind.Flick => new Color(0.95f, 0.45f, 0.3f),
            _ => Color.white,
        };

        private void DrawRectOutline(Rect r, Color c)
        {
            DrawRect(new Rect(r.x, r.y, r.width, 2), c);
            DrawRect(new Rect(r.x, r.yMax - 2, r.width, 2), c);
            DrawRect(new Rect(r.x, r.y, 2, r.height), c);
            DrawRect(new Rect(r.xMax - 2, r.y, 2, r.height), c);
        }
    }
}
