using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;
using Muses.Chart;

namespace Muses.ChartTool
{
    /// <summary>
    /// editor-spec.md §2,3。譜面エディタのデータモデルとノーツシート（主キャンバス）の描画・入力。
    ///
    /// **Unity Editor拡張ではなく、単独ビルドとして動くランタイムツール**として実装している
    /// （2026-07-31、ユーザーとの相談で方針転換。旧実装は Assets/Editor/ChartEditorWindow.cs
    /// だったが、Editorフォルダはプレイヤービルドから除外されるため書き直した）。空のGameObjectに
    /// アタッチしたシーンを作り、そのシーンだけをビルドすることでゲーム本体とは別の実行ファイルになる。
    ///
    /// 画面まわりは 2026-08-01 に OnGUI から **UI Toolkit** へ全面移行した
    /// （editor-ui-redesign.md §4.1）。バンド構成・メニュー・タブ・右パネル・ステータスバーは
    /// <c>ChartEditorApp.UI.cs</c>（同じクラスの分割定義）と Assets/UI/ChartEditor/ 配下の
    /// .uxml/.uss にある。このファイルが持つのはデータモデルと、ノーツシートの描画
    /// （<see cref="GenerateNotesSheet"/>）・入力処理。
    ///
    /// なお §6 は「ノーツシートはまず IMGUIContainer で包み、後から generateVisualContent へ」
    /// という段階移行を想定していたが、**IMGUIContainer はランタイムパネルでは使えない**
    /// （"IMGUIContainer cannot be used in a runtime panel"）ため、この案は成立しなかった。
    /// スタンドアロン実行が前提である以上、painter2D への書き直しは選択ではなく必須。
    ///
    /// §5（プレビュー: 音源同期再生・オートプレイ・RenderTexture 3Dプレビュー）は
    /// <see cref="PreviewSystem"/> に、§4（検証）は <see cref="Muses.Chart.ChartValidator"/> に、
    /// §6（Undo/Redo・自動保存）はこのクラス内に実装済み。波形+イベントレーンはまだ未実装。
    /// 現時点でのスコープ: ファイルの読み書き（OSネイティブのファイル選択ダイアログは無く、
    /// 自前の簡易ファイルブラウザ）、ノーツの配置/選択/平行移動/削除、Slideの中継点追加、
    /// インスペクタでの数値編集、プレビュー再生、検証、Undo/Redo、自動保存。
    /// 矩形選択・コピペ・一括変換・端のドラッグでの幅変更はまだ無い。
    /// </summary>
    public partial class ChartEditorApp : MonoBehaviour
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
        private bool validateOnSave = true;

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
        private string browseDir;
        private string chartPath;
        private string songPath;
        private SongMeta song = new();
        private ChartData chart = new();
        private ChartFileHeader header = new() { difficulty = "CUBE", level = 1, charter = "", songFile = "song.muses" };
        private bool dirty;
        /// <summary>SongMeta(song.muses)側だけの変更。chartのdirtyとは別に持ち、保存時に書き戻す。</summary>
        private bool songMetaDirty;
        private string statusMessage = "";

        // ---- 表示/編集状態 ----
        private int snapIndex = 3; // 1/16 既定
        private float defaultWidthCells = 1f;
        private float pxPerBeat = 28f;
        private int scrollTick;

        // ノーツシート左右の余白。左=小節番号の退避先、右=将来のイベントレーン(§7.3)用に確保。
        // editor-ui-redesign.md §7.2: 将来設定画面から変更できるようインスタンスフィールドにしている
        // （constにしない）。
        private float sheetMarginLeft = 44f;
        private float sheetMarginRight = 104f;

        // タイムライン追従: ノーツシート内で「現在時刻」を固定表示する高さ(0=上端,1=下端)。
        // scrollTickはこの位置に置かれるtickとして扱う（judgeLineFracが1.0なら従来どおり下端固定）。
        private bool followPlayback = true;
        private float judgeLineFrac = 1f;
        private EditorTool currentTool = EditorTool.Select;

        // ---- §7.4-A 選択状態 ----
        // selection が実体。selectedNote は「単一選択時のインスペクタ/中継点追加等の対象」を表す
        // 後方互換フィールドで、selection.Count==1のときだけselection[0]と一致させる（それ以外はnull）。
        // 既存コードの大半（RebuildInspector、AddWaypoint、BuildChartInfoText等）は
        // selectedNoteだけを見ればよいようにこの同期を保つ。
        private readonly List<Note> selection = new();
        private Note selectedNote;
        private Note pendingSlideStart;

        private void SyncSelectedNoteFromSelection() => selectedNote = selection.Count == 1 ? selection[0] : null;

        private void SetSingleSelection(Note note)
        {
            selection.Clear();
            if (note != null) selection.Add(note);
            SyncSelectedNoteFromSelection();
            pendingSlideStart = null;
            draggingNote = false;
            resizingNote = null;
            ClearEventSelection();
        }

        private void SetMultiSelection(List<Note> notes)
        {
            selection.Clear();
            selection.AddRange(notes);
            SyncSelectedNoteFromSelection();
            pendingSlideStart = null;
            draggingNote = false;
            resizingNote = null;
            ClearEventSelection();
        }

        private void ToggleSelectionMembership(Note note)
        {
            if (!selection.Remove(note)) selection.Add(note);
            SyncSelectedNoteFromSelection();
            pendingSlideStart = null;
            ClearEventSelection();
        }

        private void ClearSelection()
        {
            selection.Clear();
            selectedNote = null;
        }

        // ---- §7.3 イベントレーン（BPM/拍子/ソフラン）の選択状態 ----
        // ノーツとイベントは同時に1つしか選択できない（右パネルのインスペクタを共用するため）。
        private enum EventKind { None, Bpm, Meter, Scroll }
        private EventKind selectedEventKind = EventKind.None;
        private int selectedEventIndex = -1;

        private void SelectEvent(EventKind kind, int index)
        {
            ClearSelection();
            pendingSlideStart = null;
            draggingNote = false;
            resizingNote = null;
            selectedEventKind = kind;
            selectedEventIndex = index;
        }

        private void ClearEventSelection()
        {
            selectedEventKind = EventKind.None;
            selectedEventIndex = -1;
        }

        // ---- §7.4-A/B 複数選択ドラッグ ----
        private bool draggingNote;
        private int dragOriginRawTick;
        private float dragOriginRawCell;
        private float dragOriginRawLayer;
        private Dictionary<Note, List<Waypoint>> dragOriginByNote;

        // ---- §7.4-A 矩形選択 ----
        private bool rectSelecting;
        private bool rectAdditive;
        private Vector2 rectStartPos;
        private Vector2 rectCurrentPos;

        // ---- §7.4-D 端ドラッグでの幅変更（単発ノーツのみ） ----
        private Note resizingNote;
        private int resizingEdgeSign; // -1=左端, +1=右端
        private Waypoint resizeOriginWp;

        // ---- §7.4-C コピー/カット/ペースト（内部クリップボード） ----
        private readonly List<Note> clipboard = new();

        // ---- 配置ツールのゴースト表示（カーソル追従プレビュー） ----
        // シート内でのポインタ位置。範囲外/未取得時はnull（PointerLeaveEventで確実にクリアする。
        // ドラッグでキャプチャ中もPointerMoveEventは届くので、これ単体でホバー判定に使える）。
        private Vector2? sheetHoverPos;

        private void Awake()
        {
            preview = new PreviewSystem(this, stageShader, noteShader, beatLineShader);
            browseDir = PlayerPrefs.GetString("ChartEditor_LastDir", Application.persistentDataPath);
            if (!Directory.Exists(browseDir)) browseDir = Application.persistentDataPath;
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

            if (followPlayback && preview.IsPlaying)
            {
                scrollTick = Math.Max(0, ChartFormat.SecondsToTick(chart.bpmEvents, preview.SongTime));
            }

            HandleUndoRedoShortcuts();
            TickAutosave();
            SyncModelToUi();
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
                ClearSelection();
                pendingSlideStart = null;
                draggingNote = false;
                dirty = false;
                undoStack.Clear();
                redoStack.Clear();
                lastAutosaveRealtime = Time.unscaledTime;
                statusMessage = "読み込み完了";
                uiNeedsPropertyRefresh = true;
                browseDir = dir;
                PlayerPrefs.SetString("ChartEditor_LastDir", dir);
                PlayerPrefs.Save();
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
                // 右パネルの「情報」「音源」セクション(§2.5)は SongMeta を直接編集するので、
                // 譜面と一緒に song.muses も書き戻さないと編集内容が消える。
                if (songMetaDirty && !string.IsNullOrEmpty(songPath))
                {
                    ChartSerializer.WriteSongMeta(song, songPath);
                    songMetaDirty = false;
                }
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
            ClearSelection(); // 復元後は参照が切れるため選択解除
            pendingSlideStart = null;
            draggingNote = false;
            dirty = true;
            uiNeedsPropertyRefresh = true;
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

        private void RestoreFromAutosave()
        {
            try
            {
                var (loadedHeader, loadedChart) = ChartSerializer.ReadChart(restoreAutosavePath, song);
                header = loadedHeader;
                chart = loadedChart;
                undoStack.Clear();
                redoStack.Clear();
                ClearSelection();
                pendingSlideStart = null;
                dirty = true;
                statusMessage = "自動保存ファイルから復元しました";
                uiNeedsPropertyRefresh = true;
                preview.Rebuild(song, chart, Path.GetDirectoryName(chartPath));
            }
            catch (Exception ex)
            {
                statusMessage = "復元エラー: " + ex.Message;
            }
            showRestorePrompt = false;
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
            RefreshValidationList();
            if (foldValidation != null) foldValidation.value = true;
        }


        // ---------- ノーツシート（主キャンバス） ----------

        /// <summary>
        /// ノーツシートの座標変換。描画・小節番号ラベルの配置・入力判定の3か所で同じ計算が要るので、
        /// 現在の状態から都度組み立てて共有する（IMGUI版ではローカル関数をFuncで引き回していた）。
        /// </summary>
        private readonly struct SheetLayout
        {
            // rect: ノーツシート全体（背景塗りつぶし用）。leftMargin/rightMargin: レーン外の余白
            // （左=小節番号の退避先、右=将来のイベントレーン §7.3 用に確保のみ、当面は空）。
            // editor-ui-redesign.md §7.2 どおり、帯の大きさは今後設定画面から変更できるよう
            // ChartEditorApp側のフィールド(sheetMarginLeft/sheetMarginRight)経由で渡す。
            public readonly Rect rect, leftMargin, ground, gutter, sky, rightMargin;
            public readonly float pxPerTick, judgeLineY;
            private readonly int scrollTick;

            public SheetLayout(Rect rect, float pxPerBeat, int scrollTick, float judgeLineFrac, float marginLeft, float marginRight)
            {
                this.rect = rect;
                this.scrollTick = scrollTick;

                const float gutterW = 26f;
                leftMargin = new Rect(rect.x, rect.y, marginLeft, rect.height);
                rightMargin = new Rect(rect.xMax - marginRight, rect.y, marginRight, rect.height);

                float lanesX = leftMargin.xMax;
                float lanesW = Mathf.Max(0f, rightMargin.xMin - lanesX - gutterW);
                float paneW = lanesW * 0.5f;
                ground = new Rect(lanesX, rect.y, paneW, rect.height);
                gutter = new Rect(ground.xMax, rect.y, gutterW, rect.height);
                sky = new Rect(gutter.xMax, rect.y, paneW, rect.height);

                pxPerTick = pxPerBeat / ChartData.TicksPerBeat;
                judgeLineY = rect.y + rect.height * Mathf.Clamp01(judgeLineFrac);
            }

            public float TickToY(int tick) => judgeLineY - (tick - scrollTick) * pxPerTick;
            public int YToTick(float y) => scrollTick + Mathf.RoundToInt((judgeLineY - y) / pxPerTick);

            // TickToYは下に行くほどtickが小さくなるため、上端が「大きいtick」・下端が「小さいtick」になる。
            public int TopTick => scrollTick + Mathf.CeilToInt((judgeLineY - rect.y) / pxPerTick);
            public int BottomTick => scrollTick - Mathf.CeilToInt((rect.yMax - judgeLineY) / pxPerTick);

            public static float CellX(Rect pane, float cellF) => pane.x + cellF / Cells * pane.width;

            public float CombinedX(float layerF, float cellF) =>
                Mathf.Lerp(CellX(ground, cellF), CellX(sky, cellF), Mathf.Clamp01(layerF));

            /// <summary>x座標がGround/Skyどちらのペインか。ガター上なら中間値を返す（単発ノーツは置けない）。</summary>
            public (float layerF, float cellF) PaneAt(float x)
            {
                if (x >= ground.xMin && x <= ground.xMax)
                    return (0f, Mathf.Clamp((x - ground.x) / ground.width * Cells, 0f, Cells));
                if (x >= sky.xMin && x <= sky.xMax)
                    return (1f, Mathf.Clamp((x - sky.x) / sky.width * Cells, 0f, Cells));
                return (0.5f, Cells * 0.5f);
            }
        }

        private SheetLayout CurrentSheetLayout()
        {
            var r = notesSheet.contentRect;
            return new SheetLayout(new Rect(0f, 0f, r.width, r.height), pxPerBeat, scrollTick, judgeLineFrac, sheetMarginLeft, sheetMarginRight);
        }

        private int SnapTicks => Mathf.Max(1, SongAddr.TicksPerBeatUnit(SnapDenominators[snapIndex]));

        /// <summary>
        /// §7.3 イベントレーン: 右余白をBPM/拍子/ソフランの3列に分ける。列自体が種別を表すので、
        /// 参考画像のような「クリック後に種別を選ぶメニュー」は不要（列を選ぶことが種別選択を兼ねる）。
        /// </summary>
        private static (Rect bpm, Rect meter, Rect scroll) EventColumns(Rect rightMargin)
        {
            float w = rightMargin.width / 3f;
            var bpm = new Rect(rightMargin.x, rightMargin.y, w, rightMargin.height);
            var meter = new Rect(bpm.xMax, rightMargin.y, w, rightMargin.height);
            var scroll = new Rect(meter.xMax, rightMargin.y, w, rightMargin.height);
            return (bpm, meter, scroll);
        }

        // painter2D は塗りつぶしパスしか使わない。矩形1個 = パス1本。
        private static void FillRect(Painter2D p, Rect r, Color c)
        {
            p.fillColor = c;
            p.BeginPath();
            p.MoveTo(new Vector2(r.xMin, r.yMin));
            p.LineTo(new Vector2(r.xMax, r.yMin));
            p.LineTo(new Vector2(r.xMax, r.yMax));
            p.LineTo(new Vector2(r.xMin, r.yMax));
            p.ClosePath();
            p.Fill();
        }

        private static void FillRectOutline(Painter2D p, Rect r, Color c, float t = 2f)
        {
            FillRect(p, new Rect(r.x, r.y, r.width, t), c);
            FillRect(p, new Rect(r.x, r.yMax - t, r.width, t), c);
            FillRect(p, new Rect(r.x, r.y, t, r.height), c);
            FillRect(p, new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        /// <summary>
        /// ノーツシート本体の描画。UI Toolkitのランタイムパネルでは IMGUIContainer が使えないため
        /// （"IMGUIContainer cannot be used in a runtime panel"）、generateVisualContent から
        /// painter2D で直接描く。文字（Ground/Sky・小節番号）はここでは描けないので、
        /// <see cref="UpdateSheetLabels"/> が絶対配置のLabel要素として別に置いている。
        /// </summary>
        private void GenerateNotesSheet(MeshGenerationContext mgc)
        {
            var L = CurrentSheetLayout();
            if (L.rect.width < 2f || L.rect.height < 2f) return;

            var p = mgc.painter2D;
            var rect = L.rect;

            FillRect(p, rect, new Color(0.16f, 0.16f, 0.16f));
            FillRect(p, L.leftMargin, new Color(0.12f, 0.12f, 0.12f));
            FillRect(p, L.rightMargin, new Color(0.12f, 0.12f, 0.12f));
            FillRect(p, L.gutter, new Color(0.1f, 0.1f, 0.1f));

            // §7.3 イベントレーンの3列(BPM/拍子/ソフラン)の区切り線
            var (_, meterCol, scrollCol) = EventColumns(L.rightMargin);
            FillRect(p, new Rect(meterCol.x, rect.y, 1, rect.height), new Color(1, 1, 1, 0.08f));
            FillRect(p, new Rect(scrollCol.x, rect.y, 1, rect.height), new Color(1, 1, 1, 0.08f));

            float lanesXMin = L.ground.xMin, lanesXMax = L.sky.xMax;

            // セル境界線
            for (int c = 0; c <= Cells; c++)
            {
                FillRect(p, new Rect(SheetLayout.CellX(L.ground, c), rect.y, 1, rect.height), new Color(1, 1, 1, 0.08f));
                FillRect(p, new Rect(SheetLayout.CellX(L.sky, c), rect.y, 1, rect.height), new Color(1, 1, 1, 0.08f));
            }

            // 小節/拍/スナップ線
            int snapTicks = SnapTicks;
            int lineStart = Mathf.Max(0, L.BottomTick - snapTicks) / snapTicks * snapTicks;
            int lineEnd = L.TopTick + snapTicks;
            int guard = 0;
            for (int t = lineStart; t <= lineEnd && guard < 20000; t += snapTicks, guard++)
            {
                float y = L.TickToY(t);
                if (y < rect.y - 4 || y > rect.yMax + 4) continue;

                var addr = SongAddr.ToAddr(song.meters, t);
                Color c;
                float thickness;
                if (addr.beat == 1 && addr.tick == 0) { c = new Color(1, 1, 1, 0.5f); thickness = 2f; }
                else if (addr.tick == 0) { c = new Color(1, 1, 1, 0.28f); thickness = 1f; }
                else { c = new Color(1, 1, 1, 0.12f); thickness = 1f; }

                FillRect(p, new Rect(lanesXMin, y, lanesXMax - lanesXMin, thickness), c);
            }

            // ノーツ描画
            foreach (var note in chart.notes)
            {
                int nStart = note.points[0].tick;
                int nEnd = note.points[^1].tick;
                if (nEnd < L.BottomTick - snapTicks * 4 || nStart > L.TopTick + snapTicks * 4) continue;

                Color col = NoteColor(note.kind);
                if (note.points.Count == 1)
                {
                    var wp = note.points[0];
                    float y = L.TickToY(wp.tick);
                    float x0 = L.CombinedX(wp.layerF, wp.cellF);
                    float x1 = L.CombinedX(wp.layerF, wp.cellF + wp.width);
                    FillRect(p, Rect.MinMaxRect(Mathf.Min(x0, x1), y - 4, Mathf.Max(x0, x1), y + 4), col);
                }
                else
                {
                    int stepTicks = Mathf.Max(1, Mathf.RoundToInt(4f / L.pxPerTick));
                    for (int t = nStart; t < nEnd; t += stepTicks)
                    {
                        int t2 = Mathf.Min(t + stepTicks, nEnd);
                        float ya = L.TickToY(t), yb = L.TickToY(t2);
                        if (yb > rect.yMax + 8 || ya < rect.y - 8) continue;

                        var a = InterpAtTick(note, t);
                        float xa0 = L.CombinedX(a.layerF, a.cellF);
                        float xa1 = L.CombinedX(a.layerF, a.cellF + a.width);
                        FillRect(p,
                            Rect.MinMaxRect(Mathf.Min(xa0, xa1), Mathf.Min(ya, yb) - 1, Mathf.Max(xa0, xa1), Mathf.Max(ya, yb) + 1),
                            new Color(col.r, col.g, col.b, 0.55f));
                    }

                    foreach (var wp in note.points)
                    {
                        if (wp.marker != WaypointMarker.Visible) continue;
                        float y = L.TickToY(wp.tick);
                        float x = L.CombinedX(wp.layerF, wp.cellF);
                        FillRect(p, new Rect(x - 3, y - 3, 6, 6), Color.white);
                    }
                }

                if (selection.Contains(note))
                {
                    var startWp = note.points[0];
                    var endWp = note.points[^1];
                    float y0 = L.TickToY(nStart), y1 = L.TickToY(nEnd);
                    float sx0 = L.CombinedX(startWp.layerF, startWp.cellF);
                    float sx1 = L.CombinedX(endWp.layerF, endWp.cellF + endWp.width);
                    var box = Rect.MinMaxRect(Mathf.Min(sx0, sx1) - 3, Mathf.Min(y0, y1) - 6, Mathf.Max(sx0, sx1) + 3, Mathf.Max(y0, y1) + 6);
                    FillRectOutline(p, box, Color.yellow);
                }
            }

            // 判定線(追従の同期位置)。judgeLineFracで高さを変更可能（右パネルの「表示設定」）
            FillRect(p, new Rect(lanesXMin, L.judgeLineY - 1, lanesXMax - lanesXMin, 2), new Color(1f, 0.25f, 0.25f, 0.9f));

            // Slide配置中(1点目クリック済み・2点目待ち)の視覚フィードバック。マウスがシート外
            // （インスペクタ確認等）でも1点目クリック済みなことが分かるよう、ホバーの有無に関わらず出す。
            if (pendingSlideStart != null)
            {
                var wp0 = pendingSlideStart.points[0];
                float py = L.TickToY(wp0.tick);
                float px = L.CombinedX(wp0.layerF, wp0.cellF);
                FillRectOutline(p, new Rect(px - 5, py - 5, 10, 10), Color.white);
            }

            DrawPlacementGhost(p, L);

            // §7.4-A 矩形選択中のドラッグ矩形
            if (rectSelecting)
            {
                var box = Rect.MinMaxRect(
                    Mathf.Min(rectStartPos.x, rectCurrentPos.x), Mathf.Min(rectStartPos.y, rectCurrentPos.y),
                    Mathf.Max(rectStartPos.x, rectCurrentPos.x), Mathf.Max(rectStartPos.y, rectCurrentPos.y));
                FillRect(p, box, new Color(0.4f, 0.7f, 1f, 0.15f));
                FillRectOutline(p, box, new Color(0.4f, 0.7f, 1f, 0.8f), 1f);
            }
        }

        // ---------- 配置ツールのゴースト（カーソル追従プレビュー） ----------

        /// <summary>
        /// 「ノーツ選択時、カーソルに薄いノーツを追従させ、ここでクリックすると配置される」というUI。
        /// OnSheetPointerDownの配置ロジックと同じスナップ計算を使い、実際に置かれる位置とゴーストが
        /// 一致するようにしている（計算がずれると「ゴーストの位置でクリックしたのに違う場所に置かれた」
        /// という不整合になるため、ここは重複を許容してPointerDown側の分岐をなぞる）。
        /// </summary>
        private void DrawPlacementGhost(Painter2D p, SheetLayout L)
        {
            if (draggingNote || !sheetHoverPos.HasValue) return;
            var pos = sheetHoverPos.Value;
            if (!L.rect.Contains(pos)) return;

            int snapTicks = SnapTicks;
            int tick = SnapTickTo(Mathf.Max(0, L.YToTick(pos.y)), snapTicks);

            // イベントレーンは現在のツールに関わらず常時クリックで追加できるので、
            // ホバー時のゴーストもツール非依存で出す。
            if (L.rightMargin.Contains(pos))
            {
                DrawEventGhost(p, L, pos, tick);
                return;
            }

            var (layerF, rawCell) = L.PaneAt(pos.x);

            switch (currentTool)
            {
                case EditorTool.Tap:
                case EditorTool.ExTap:
                case EditorTool.Flick:
                {
                    if (layerF != 0f && layerF != 1f) return; // ガターには単発ノーツを置けない
                    float cellF = SnapCellTo(rawCell, 1f);
                    var kind = currentTool == EditorTool.Tap ? NoteKind.Tap
                        : currentTool == EditorTool.ExTap ? NoteKind.ExTap : NoteKind.Flick;
                    DrawGhostPoint(p, L, tick, layerF, cellF, defaultWidthCells, NoteColor(kind));
                    break;
                }
                case EditorTool.Slide:
                {
                    float cellF = SnapCellTo(rawCell, 0.5f);
                    var col = NoteColor(NoteKind.Slide);
                    if (pendingSlideStart == null)
                    {
                        DrawGhostPoint(p, L, tick, layerF, cellF, defaultWidthCells, col);
                    }
                    else
                    {
                        var wp0 = pendingSlideStart.points[0];
                        if (tick > wp0.tick)
                        {
                            float y0 = L.TickToY(wp0.tick), y1 = L.TickToY(tick);
                            float x0 = L.CombinedX(wp0.layerF, wp0.cellF + wp0.width * 0.5f);
                            float x1 = L.CombinedX(layerF, cellF + defaultWidthCells * 0.5f);
                            var lineCol = new Color(col.r, col.g, col.b, 0.4f);
                            FillRect(p, Rect.MinMaxRect(Mathf.Min(x0, x1) - 2, Mathf.Min(y0, y1), Mathf.Max(x0, x1) + 2, Mathf.Max(y0, y1)), lineCol);
                            DrawGhostPoint(p, L, tick, layerF, cellF, defaultWidthCells, col);
                        }
                    }
                    break;
                }
                case EditorTool.AddWaypoint:
                {
                    if (selectedNote is { kind: NoteKind.Slide })
                    {
                        int nStart = selectedNote.points[0].tick, nEnd = selectedNote.points[^1].tick;
                        if (tick > nStart && tick < nEnd)
                        {
                            float cellF = SnapCellTo(rawCell, 0.5f);
                            float width = InterpAtTick(selectedNote, tick).width;
                            DrawGhostPoint(p, L, tick, layerF, cellF, width, Color.white);
                        }
                    }
                    break;
                }
            }
        }

        private static void DrawGhostPoint(Painter2D p, SheetLayout L, int tick, float layerF, float cellF, float width, Color baseColor)
        {
            float y = L.TickToY(tick);
            float x0 = L.CombinedX(layerF, cellF);
            float x1 = L.CombinedX(layerF, cellF + width);
            var fill = new Color(baseColor.r, baseColor.g, baseColor.b, 0.4f);
            var outline = new Color(baseColor.r, baseColor.g, baseColor.b, 0.85f);
            FillRect(p, Rect.MinMaxRect(Mathf.Min(x0, x1), y - 4, Mathf.Max(x0, x1), y + 4), fill);
            FillRectOutline(p, Rect.MinMaxRect(Mathf.Min(x0, x1) - 1, y - 5, Mathf.Max(x0, x1) + 1, y + 5), outline, 1f);
        }

        /// <summary>
        /// イベントレーン(§7.3)のゴースト。列の位置がそのまま種別を表すので、列ごとに
        /// HandleEventLaneClickと同じtickの丸め方（BPM/ソフランはグリッドスナップ、拍子は小節頭）
        /// を再現する。
        /// </summary>
        private void DrawEventGhost(Painter2D p, SheetLayout L, Vector2 pos, int snappedTick)
        {
            var (bpmCol, meterCol, scrollCol) = EventColumns(L.rightMargin);
            Rect col;
            Color baseColor;
            int tick;

            if (pos.x < bpmCol.xMax)
            {
                col = bpmCol;
                baseColor = new Color(130f / 255f, 214f / 255f, 120f / 255f);
                tick = snappedTick;
            }
            else if (pos.x < meterCol.xMax)
            {
                col = meterCol;
                baseColor = new Color(230f / 255f, 200f / 255f, 90f / 255f);
                var addr = SongAddr.ToAddr(song.meters, Mathf.Max(0, L.YToTick(pos.y)));
                tick = SongAddr.ToTick(song.meters, addr.bar, 1, 0);
            }
            else
            {
                col = scrollCol;
                baseColor = new Color(190f / 255f, 140f / 255f, 230f / 255f);
                tick = snappedTick;
            }

            float y = L.TickToY(tick);
            FillRect(p, new Rect(col.x + 2f, y - 8f, col.width - 4f, 16f), new Color(baseColor.r, baseColor.g, baseColor.b, 0.45f));
            FillRectOutline(p, new Rect(col.x + 2f, y - 8f, col.width - 4f, 16f), new Color(baseColor.r, baseColor.g, baseColor.b, 0.9f), 1f);
        }

        // ---------- ノーツシートの入力（UI Toolkitのポインタ/ホイール/キーイベント） ----------

        private float sheetScrollAccum;

        private void OnSheetPointerDown(PointerDownEvent evt)
        {
            notesSheet.Focus(); // KeyDown（Deleteでの削除）を受け取れるようにする

            // §7.4-E 右クリック→コンテキストメニュー。UI ToolkitのPointerEventBaseはUnity既定の
            // マウスボタン番号（0=左,1=右,2=中）を使う（W3C PointerEventのDOM番号とは異なる）。
            if (evt.button == 1)
            {
                OnSheetRightClick(evt);
                return;
            }
            if (evt.button != 0) return;

            var L = CurrentSheetLayout();
            var pos = (Vector2)evt.localPosition;
            int snapTicks = SnapTicks;

            int rawTick = Mathf.Max(0, L.YToTick(pos.y));
            int tick = SnapTickTo(rawTick, snapTicks);

            // §7.3 イベントレーン: 空白をクリックしたら新規追加（既存チップのクリックは
            // UpdateEventChipsが作るLabel要素自体が拾いStopPropagationするので、ここには来ない）。
            if (L.rightMargin.Contains(pos))
            {
                HandleEventLaneClick(L, pos, tick);
                evt.StopPropagation();
                return;
            }

            var (layerF, rawCell) = L.PaneAt(pos.x);

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
                    SetSingleSelection(note);
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
                            var completed = pendingSlideStart;
                            completed.points.Add(NewWaypoint(tick, layerF, cellF, defaultWidthCells));
                            PushUndo(coalesce: false);
                            chart.notes.Add(completed);
                            pendingSlideStart = null;
                            SetSingleSelection(completed);
                            dirty = true;
                            statusMessage = "Slideを配置しました";
                        }
                        else
                        {
                            // 終点が始点と同tick以前だと配置できない（スナップが粗いと起きやすい）。
                            // 1点目は維持し、やり直せるようにする。
                            statusMessage = "Slideの終点は始点より後ろの位置をクリックしてください（1点目は維持中）";
                        }
                    }
                    break;
                }
                case EditorTool.AddWaypoint:
                {
                    if (selectedNote is { kind: NoteKind.Slide })
                    {
                        float cellF = SnapCellTo(rawCell, 0.5f);
                        int insertAt = selectedNote.points.FindIndex(pt => pt.tick > tick);
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
                    var hit = HitTestNote(L, pos);
                    if (hit != null)
                    {
                        PushUndo(coalesce: false);
                        chart.notes.Remove(hit);
                        selection.Remove(hit);
                        SyncSelectedNoteFromSelection();
                        dirty = true;
                    }
                    break;
                }
                case EditorTool.Select:
                default:
                {
                    var hit = HitTestNote(L, pos);

                    if (hit != null)
                    {
                        // §7.4-D 端ドラッグでの幅変更（単発ノーツのみ、Shift併用時は選択トグル優先）
                        int edgeSign = EdgeGrabSign(L, hit, pos);
                        if (edgeSign != 0 && !evt.shiftKey)
                        {
                            SetSingleSelection(hit);
                            PushUndo(coalesce: false);
                            resizingNote = hit;
                            resizingEdgeSign = edgeSign;
                            resizeOriginWp = hit.points[0];
                            notesSheet.CapturePointer(evt.pointerId);
                            evt.StopPropagation();
                            return;
                        }

                        if (evt.shiftKey)
                        {
                            ToggleSelectionMembership(hit);
                        }
                        else if (!selection.Contains(hit))
                        {
                            // 未選択のノーツをクリック→単一選択に切り替える。
                            // 既に選択済みグループの一員なら選択を維持し、グループごとドラッグできるようにする。
                            SetSingleSelection(hit);
                        }

                        if (selection.Contains(hit))
                        {
                            PushUndo(coalesce: false); // ドラッグ開始時点(変更前)を1手として記録する
                            draggingNote = true;
                            dragOriginRawTick = rawTick;
                            dragOriginRawCell = rawCell;
                            dragOriginRawLayer = layerF;
                            dragOriginByNote = new Dictionary<Note, List<Waypoint>>();
                            foreach (var n in selection)
                                dragOriginByNote[n] = new List<Waypoint>(n.points);
                            notesSheet.CapturePointer(evt.pointerId);
                        }
                    }
                    else
                    {
                        // §7.4-A 空白ドラッグ→矩形選択。Shiftなしなら既存選択をクリアしてから開始する。
                        if (!evt.shiftKey) ClearSelection();
                        ClearEventSelection();
                        rectSelecting = true;
                        rectAdditive = evt.shiftKey;
                        rectStartPos = pos;
                        rectCurrentPos = pos;
                        notesSheet.CapturePointer(evt.pointerId);
                    }
                    break;
                }
            }
            evt.StopPropagation();
        }

        /// <summary>
        /// §7.4-E コンテキストメニュー。右クリック対象が未選択なら単一選択に切り替えてから開く
        /// （既存の複数選択中に右クリックした場合はそのグループを対象にする）。
        /// </summary>
        private void OnSheetRightClick(PointerDownEvent evt)
        {
            var L = CurrentSheetLayout();
            var pos = (Vector2)evt.localPosition;
            if (L.rightMargin.Contains(pos)) { evt.StopPropagation(); return; }

            var hit = HitTestNote(L, pos);
            if (hit == null) { evt.StopPropagation(); return; }
            if (!selection.Contains(hit)) SetSingleSelection(hit);

            var menu = new GenericDropdownMenu();
            int count = selection.Count;
            menu.AddItem(count > 1 ? $"選択した{count}件を削除" : "このノーツを削除", false, DeleteSelection);

            if (count == 1 && hit.points.Count == 1)
            {
                menu.AddSeparator("");
                if (hit.kind != NoteKind.Tap) menu.AddItem("Tapに変更", false, () => ChangeNoteKind(hit, NoteKind.Tap));
                if (hit.kind != NoteKind.ExTap) menu.AddItem("Ex Tapに変更", false, () => ChangeNoteKind(hit, NoteKind.ExTap));
                if (hit.kind != NoteKind.Flick) menu.AddItem("Flickに変更", false, () => ChangeNoteKind(hit, NoteKind.Flick));
            }

            if (count == 1 && hit.kind == NoteKind.Slide)
            {
                int snapTicks = SnapTicks;
                int tick = SnapTickTo(Mathf.Max(0, L.YToTick(pos.y)), snapTicks);
                if (tick > hit.points[0].tick && tick < hit.points[^1].tick)
                {
                    menu.AddSeparator("");
                    menu.AddItem("ここに中継点を追加", false, () => InsertWaypointInto(hit, L, pos, tick));
                }
            }

            var worldPos = notesSheet.LocalToWorld(pos);
            menu.DropDown(new Rect(worldPos, Vector2.zero), notesSheet, DropdownMenuSizeMode.Auto);
            evt.StopPropagation();
        }

        private void InsertWaypointInto(Note note, SheetLayout L, Vector2 pos, int tick)
        {
            var (layerF, rawCell) = L.PaneAt(pos.x);
            float cellF = SnapCellTo(rawCell, 0.5f);
            int insertAt = note.points.FindIndex(pt => pt.tick > tick);
            if (insertAt < 0) insertAt = note.points.Count;
            if (insertAt <= 0 || insertAt >= note.points.Count) return;
            float width = InterpAtTick(note, tick).width;
            PushUndo(coalesce: false);
            note.points.Insert(insertAt, NewWaypoint(tick, layerF, cellF, width));
            dirty = true;
        }

        private void ChangeNoteKind(Note note, NoteKind kind)
        {
            if (note.points.Count != 1 || note.kind == kind) return;
            PushUndo(coalesce: false);
            note.kind = kind;
            dirty = true;
        }

        /// <summary>単発ノーツの左右端 ±4px を掴んでいるか。-1=左端, 0=対象外, +1=右端。</summary>
        private static int EdgeGrabSign(SheetLayout L, Note note, Vector2 pos)
        {
            if (note.points.Count != 1) return 0;
            var wp = note.points[0];
            float x0 = L.CombinedX(wp.layerF, wp.cellF);
            float x1 = L.CombinedX(wp.layerF, wp.cellF + wp.width);
            float left = Mathf.Min(x0, x1), right = Mathf.Max(x0, x1);
            const float grab = 4f;
            if (Mathf.Abs(pos.x - left) <= grab) return -1;
            if (Mathf.Abs(pos.x - right) <= grab) return 1;
            return 0;
        }

        private List<Note> HitTestNotesInRect(SheetLayout L, Rect rect)
        {
            var result = new List<Note>();
            foreach (var note in chart.notes)
            {
                foreach (var wp in note.points)
                {
                    float y = L.TickToY(wp.tick);
                    float x0 = L.CombinedX(wp.layerF, wp.cellF);
                    float x1 = L.CombinedX(wp.layerF, wp.cellF + wp.width);
                    var wpRect = Rect.MinMaxRect(Mathf.Min(x0, x1), y - 4f, Mathf.Max(x0, x1), y + 4f);
                    if (rect.Overlaps(wpRect))
                    {
                        result.Add(note);
                        break;
                    }
                }
            }
            return result;
        }

        private void OnSheetPointerMove(PointerMoveEvent evt)
        {
            sheetHoverPos = (Vector2)evt.localPosition;
            var pos = (Vector2)evt.localPosition;
            var L = CurrentSheetLayout();

            if (rectSelecting)
            {
                rectCurrentPos = pos;
                evt.StopPropagation();
                return;
            }

            if (resizingNote != null)
            {
                var (_, rawCellR) = L.PaneAt(pos.x);
                float cellF = SnapCellTo(rawCellR, 1f);
                var wp = resizeOriginWp;
                if (resizingEdgeSign > 0)
                {
                    wp.width = Mathf.Max(0.1f, cellF - wp.cellF);
                }
                else
                {
                    float rightEdge = resizeOriginWp.cellF + resizeOriginWp.width;
                    float newCellF = Mathf.Min(cellF, rightEdge - 0.1f);
                    wp.cellF = newCellF;
                    wp.width = Mathf.Max(0.1f, rightEdge - newCellF);
                }
                resizingNote.points[0] = wp;
                dirty = true;
                evt.StopPropagation();
                return;
            }

            if (!draggingNote || selection.Count == 0 || dragOriginByNote == null) return;

            int snapTicks = SnapTicks;
            int rawTick = L.YToTick(pos.y);
            var (currentLayerF, rawCell) = L.PaneAt(pos.x);

            int deltaTick = Mathf.RoundToInt((float)(rawTick - dragOriginRawTick) / snapTicks) * snapTicks;
            // 選択中にSlideが1つでもあれば0.5セル刻み、単発ノーツのみなら1セル刻み
            float cellStep = selection.Exists(n => n.kind == NoteKind.Slide) ? 0.5f : 1f;
            float deltaCell = SnapCellTo(rawCell - dragOriginRawCell, cellStep);
            // §7.4-B: ペインをまたいだら層(layerF)も更新する（従来はcellFの差分だけを見ており、
            // Ground⇔Skyへドラッグしても層が変わらないバグがあった）。
            float deltaLayer = currentLayerF - dragOriginRawLayer;

            foreach (var note in selection)
            {
                if (!dragOriginByNote.TryGetValue(note, out var origin)) continue;
                for (int i = 0; i < note.points.Count; i++)
                {
                    var wp = origin[i];
                    wp.tick = Mathf.Max(0, wp.tick + deltaTick);
                    wp.cellF += deltaCell;
                    wp.layerF = Mathf.Clamp01(wp.layerF + deltaLayer);
                    note.points[i] = wp;
                }
            }
            dirty = true;
            evt.StopPropagation();
        }

        private void OnSheetPointerUp(PointerUpEvent evt)
        {
            if (rectSelecting)
            {
                rectSelecting = false;
                var L = CurrentSheetLayout();
                var rect = Rect.MinMaxRect(
                    Mathf.Min(rectStartPos.x, rectCurrentPos.x), Mathf.Min(rectStartPos.y, rectCurrentPos.y),
                    Mathf.Max(rectStartPos.x, rectCurrentPos.x), Mathf.Max(rectStartPos.y, rectCurrentPos.y));
                // 実質移動なしのクリックは矩形選択として扱わない（既にPointerDownで選択解除済み）
                if (rect.width > 2f || rect.height > 2f)
                {
                    var hits = HitTestNotesInRect(L, rect);
                    if (rectAdditive)
                    {
                        foreach (var n in hits)
                            if (!selection.Contains(n)) selection.Add(n);
                        SyncSelectedNoteFromSelection();
                    }
                    else
                    {
                        SetMultiSelection(hits);
                    }
                }
                if (notesSheet.HasPointerCapture(evt.pointerId)) notesSheet.ReleasePointer(evt.pointerId);
                return;
            }

            if (resizingNote != null)
            {
                resizingNote = null;
                if (notesSheet.HasPointerCapture(evt.pointerId)) notesSheet.ReleasePointer(evt.pointerId);
                return;
            }

            if (!draggingNote) return;
            draggingNote = false;
            dragOriginByNote = null;
            if (notesSheet.HasPointerCapture(evt.pointerId)) notesSheet.ReleasePointer(evt.pointerId);
        }

        private void OnSheetPointerLeave(PointerLeaveEvent evt) => sheetHoverPos = null;

        private void OnSheetWheel(WheelEvent evt)
        {
            if (evt.ctrlKey || evt.commandKey)
            {
                pxPerBeat = Mathf.Clamp(pxPerBeat - evt.delta.y * 2f, 8f, 240f);
            }
            else
            {
                // トラックパッドでは delta が小数で連続的に来るため、端数を持ち越して1スナップ単位に量子化する
                sheetScrollAccum += evt.delta.y;
                int steps = (int)sheetScrollAccum;
                sheetScrollAccum -= steps;
                if (steps != 0) scrollTick = Mathf.Max(0, scrollTick + steps * SnapTicks);
            }
            evt.StopPropagation();
        }

        private void OnSheetKeyDown(KeyDownEvent evt)
        {
            // §7.4-C コピー/カット/ペースト（OS クリップボード連携はせず内部クリップボードのみ）
            bool cmdOrCtrl = evt.commandKey || evt.ctrlKey;
            if (cmdOrCtrl && evt.keyCode == KeyCode.C && selection.Count > 0)
            {
                CopySelectionToClipboard();
                evt.StopPropagation();
                return;
            }
            if (cmdOrCtrl && evt.keyCode == KeyCode.X && selection.Count > 0)
            {
                CopySelectionToClipboard();
                DeleteSelection();
                evt.StopPropagation();
                return;
            }
            if (cmdOrCtrl && evt.keyCode == KeyCode.V && clipboard.Count > 0)
            {
                PasteClipboard();
                evt.StopPropagation();
                return;
            }

            if (evt.keyCode != KeyCode.Delete && evt.keyCode != KeyCode.Backspace) return;

            if (selection.Count > 0)
            {
                DeleteSelection();
                evt.StopPropagation();
                return;
            }

            if (selectedEventKind != EventKind.None)
            {
                DeleteSelectedEvent();
                evt.StopPropagation();
            }
        }

        // ---------- §7.4-A/C 選択の削除・複製 ----------

        private static Note CloneNote(Note n) => new()
        {
            kind = n.kind,
            scrollGroup = n.scrollGroup,
            points = new List<Waypoint>(n.points),
            comboTimes = new List<float>(n.comboTimes),
        };

        private void DeleteSelection()
        {
            if (selection.Count == 0) return;
            PushUndo(coalesce: false);
            foreach (var n in selection) chart.notes.Remove(n);
            ClearSelection();
            dirty = true;
        }

        private void CopySelectionToClipboard()
        {
            clipboard.Clear();
            foreach (var n in selection) clipboard.Add(CloneNote(n));
            statusMessage = $"{clipboard.Count}件コピーしました";
        }

        /// <summary>貼り付け基準は判定線位置(=scrollTick、TickToY(scrollTick)==judgeLineYより)。
        /// クリップボード内の最も早いノーツの開始tickをそこへ揃え、ノーツ間の相対位置は保つ。</summary>
        private void PasteClipboard()
        {
            if (clipboard.Count == 0) return;
            int minTick = int.MaxValue;
            foreach (var n in clipboard) minTick = Mathf.Min(minTick, n.points[0].tick);
            int delta = scrollTick - minTick;

            PushUndo(coalesce: false);
            var pasted = new List<Note>();
            foreach (var src in clipboard)
            {
                var n = CloneNote(src);
                for (int i = 0; i < n.points.Count; i++)
                {
                    var wp = n.points[i];
                    wp.tick = Mathf.Max(0, wp.tick + delta);
                    n.points[i] = wp;
                }
                chart.notes.Add(n);
                pasted.Add(n);
            }
            SetMultiSelection(pasted);
            dirty = true;
            statusMessage = $"{pasted.Count}件貼り付けました";
        }

        // ---------- §7.3 イベントレーンの追加/削除 ----------

        /// <summary>指定tick時点で有効なBPM（新規追加時の初期値用）。無ければ既定120。</summary>
        private float CurrentBpmAtTick(int tick)
        {
            float bpm = 120f;
            var sorted = new List<BpmEvent>(song.bpmEvents);
            sorted.Sort((a, b) => a.tick.CompareTo(b.tick));
            foreach (var e in sorted)
            {
                if (e.tick > tick) break;
                bpm = e.bpm;
            }
            return bpm;
        }

        private void HandleEventLaneClick(SheetLayout L, Vector2 pos, int snappedTick)
        {
            var (bpmCol, meterCol, _) = EventColumns(L.rightMargin);

            if (pos.x < bpmCol.xMax)
            {
                // song.bpmEvents は SongMeta 側の値で、Undoスナップショット(chart+headerのみ)の対象外
                // （既存のタイトル/アーティスト等の編集項目と同じ扱い、PushUndoは呼ばない）。
                song.bpmEvents.Add(new BpmEvent { tick = snappedTick, bpm = CurrentBpmAtTick(snappedTick) });
                songMetaDirty = true;
                MarkPreviewDirty();
                SelectEvent(EventKind.Bpm, song.bpmEvents.Count - 1);
                statusMessage = "BPMイベントを追加しました";
            }
            else if (pos.x < meterCol.xMax)
            {
                var addr = SongAddr.ToAddr(song.meters, Mathf.Max(0, L.YToTick(pos.y)));
                int bar = addr.bar;
                int existing = song.meters.FindIndex(m => m.bar == bar);
                if (existing >= 0)
                {
                    SelectEvent(EventKind.Meter, existing);
                    statusMessage = "既存の拍子イベントを選択しました";
                }
                else
                {
                    var current = MeterAtBar(bar);
                    song.meters.Add(new MeterEvent { bar = bar, numerator = current.numerator, denominator = current.denominator });
                    songMetaDirty = true;
                    MarkPreviewDirty();
                    SelectEvent(EventKind.Meter, song.meters.Count - 1);
                    statusMessage = "拍子イベントを追加しました";
                }
            }
            else
            {
                PushUndo(coalesce: false);
                chart.scrollEvents.Add(new ScrollEvent { tick = snappedTick, group = 0, mul = 1f, easing = Easing.Linear, durationTicks = 0 });
                dirty = true;
                SelectEvent(EventKind.Scroll, chart.scrollEvents.Count - 1);
                statusMessage = "ソフランイベントを追加しました";
            }
        }

        private void DeleteSelectedEvent()
        {
            switch (selectedEventKind)
            {
                case EventKind.Bpm:
                    if (selectedEventIndex < 0 || selectedEventIndex >= song.bpmEvents.Count) break;
                    song.bpmEvents.RemoveAt(selectedEventIndex);
                    songMetaDirty = true;
                    MarkPreviewDirty();
                    break;
                case EventKind.Meter:
                    if (selectedEventIndex < 0 || selectedEventIndex >= song.meters.Count) break;
                    song.meters.RemoveAt(selectedEventIndex);
                    songMetaDirty = true;
                    MarkPreviewDirty();
                    break;
                case EventKind.Scroll:
                    if (selectedEventIndex < 0 || selectedEventIndex >= chart.scrollEvents.Count) break;
                    PushUndo(coalesce: false);
                    chart.scrollEvents.RemoveAt(selectedEventIndex);
                    dirty = true;
                    break;
            }
            ClearEventSelection();
        }

        private static int SnapTickTo(int rawTick, int snapTicks) =>
            Mathf.RoundToInt((float)rawTick / snapTicks) * snapTicks;

        private static float SnapCellTo(float rawCell, float step) =>
            Mathf.Round(rawCell / step) * step;

        private Note HitTestNote(SheetLayout L, Vector2 mouse)
        {
            for (int idx = chart.notes.Count - 1; idx >= 0; idx--)
            {
                var n = chart.notes[idx];
                if (n.points.Count == 1)
                {
                    var wp = n.points[0];
                    float y = L.TickToY(wp.tick);
                    if (Mathf.Abs(mouse.y - y) > 6f) continue;
                    float x0 = L.CombinedX(wp.layerF, wp.cellF);
                    float x1 = L.CombinedX(wp.layerF, wp.cellF + wp.width);
                    if (mouse.x >= Mathf.Min(x0, x1) - 2 && mouse.x <= Mathf.Max(x0, x1) + 2) return n;
                }
                else
                {
                    int tick = L.YToTick(mouse.y);
                    int nStart = n.points[0].tick, nEnd = n.points[^1].tick;
                    if (tick < nStart - 4 || tick > nEnd + 4) continue;
                    int clamped = Mathf.Clamp(tick, nStart, nEnd);
                    var s = InterpAtTick(n, clamped);
                    float x0 = L.CombinedX(s.layerF, s.cellF);
                    float x1 = L.CombinedX(s.layerF, s.cellF + s.width);
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
    }
}
