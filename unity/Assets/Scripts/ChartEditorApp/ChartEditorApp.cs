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
        private bool wasPlayingLastFrame;

        // ---- §4 検証 ----
        private List<ValidationIssue> validationIssues = new();
        private bool validateOnSave = true;

        // ---- §6 Undo/Redo ----
        private struct UndoSnapshot
        {
            public ChartData chart;
            public ChartFileHeader header;
            public string label;
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

        // ---- §3 再生位置カーソル（橙色）----
        // editor-ui-rework-mmw.md §3。判定線(赤、スクロール追従の同期位置)とは別に、
        // 「再生を開始する時刻」を示す。参照元(ScoreEditor.h:76 currentTick)と同じく、
        // 停止中はこちらが真の値・再生中はpreview.SongTimeが真の値、と役割を切り替える
        // （EditorWindows.cpp:513-546のupdate()と同じ設計）。
        private int cursorTick;

        // ノーツシート左右の余白。左=小節番号の退避先、右=将来のイベントレーン(§7.3)用に確保。
        // editor-ui-redesign.md §7.2: 将来設定画面から変更できるようインスタンスフィールドにしている
        // （constにしない）。
        private float sheetMarginLeft = 44f;
        private float sheetMarginRight = 104f;

        // タイムライン追従: ノーツシート内で「現在時刻」を固定表示する高さ(0=上端,1=下端)。
        // scrollTickはこの位置に置かれるtickとして扱う（judgeLineFracが1.0なら従来どおり下端固定）。
        private bool followPlayback = true;
        private float judgeLineFrac = 1f;

        /// <summary>
        /// MikuMikuWorld移植候補: 再生追従の2モード（EditorWindows.cpp:531-540のScrollMode）。
        /// Smooth=毎フレーム追従（従来どおり）。Page=画面上端を超えるまでスクロールを止め、
        /// 超えた瞬間に1ページ分ジャンプする（高速再生時に読みやすい）。
        /// </summary>
        private enum ScrollFollowMode { Smooth, Page }
        private ScrollFollowMode scrollFollowMode = ScrollFollowMode.Smooth;
        private EditorTool currentTool = EditorTool.Select;

        // ---- §7.4-A 選択状態 ----
        // editor-ui-rework-mmw.md §5.2: 選択の粒度は「点」単位（NoteRef）。参照元(MikuMikuWorld)は
        // Slideの始点/中継点/終点がそれぞれ独立したNoteなので選択が最初から点単位になっている。
        // muses は Note.points が入れ子なので、選択側でその粒度を再現する。
        // 単発ノーツ(Tap/ExTap/Flick)は points.Count==1 なので NoteRef(note, 0) が「ノーツ全体」と一致する
        // （概念が増えるわけではない）。
        //
        // selectedNote は「単一選択時のインスペクタ/中継点追加等の対象」を表す後方互換フィールドで、
        // selection.Count==1のときだけその点が属するnoteと一致させる（それ以外はnull）。
        // 既存コードの大半（RebuildInspector、AddWaypoint、BuildChartInfoText等）は
        // selectedNoteだけを見ればよいようにこの同期を保つ。
        private readonly struct NoteRef : IEquatable<NoteRef>
        {
            public readonly Note note;
            public readonly int index;
            public NoteRef(Note note, int index) { this.note = note; this.index = index; }
            public bool Equals(NoteRef other) => ReferenceEquals(note, other.note) && index == other.index;
            public override bool Equals(object obj) => obj is NoteRef other && Equals(other);
            public override int GetHashCode() => System.HashCode.Combine(note, index);
        }

        private readonly List<NoteRef> selection = new();
        private Note selectedNote;
        private Note pendingSlideStart;

        private void SyncSelectedNoteFromSelection() => selectedNote = selection.Count == 1 ? selection[0].note : null;

        /// <summary>selectionが参照する重複無しのNote集合。高さレーン・コピー等、点ではなくノーツ単位で
        /// 動作すべき機能はこちらを使う（参照元Editing.cpp:31-59の「一部でも選択されていれば全体を対象にする」
        /// 挙動を踏襲）。</summary>
        private IEnumerable<Note> SelectedNotesDistinct()
        {
            var seen = new HashSet<Note>();
            foreach (var r in selection)
                if (seen.Add(r.note))
                    yield return r.note;
        }

        private static List<NoteRef> AllPointRefs(Note n)
        {
            var list = new List<NoteRef>(n.points.Count);
            for (int i = 0; i < n.points.Count; i++) list.Add(new NoteRef(n, i));
            return list;
        }

        private static List<NoteRef> AllPointRefsForNotes(IEnumerable<Note> notes)
        {
            var list = new List<NoteRef>();
            foreach (var n in notes) list.AddRange(AllPointRefs(n));
            return list;
        }

        private void SetSingleSelection(NoteRef? r)
        {
            selection.Clear();
            if (r.HasValue) selection.Add(r.Value);
            SyncSelectedNoteFromSelection();
            pendingSlideStart = null;
            draggingNote = false;
            resizingNote = null;
            heightDragNote = null;
            ClearEventSelection();
        }

        /// <summary>単発ノーツ(Tap/ExTap/Flick)や、配置直後のノーツ全体を選択状態にするための補助。</summary>
        private void SetSingleSelection(Note wholeNote) => SetSingleSelection(new NoteRef(wholeNote, 0));

        private void SetMultiSelection(List<NoteRef> refs)
        {
            selection.Clear();
            selection.AddRange(refs);
            SyncSelectedNoteFromSelection();
            pendingSlideStart = null;
            draggingNote = false;
            resizingNote = null;
            heightDragNote = null;
            ClearEventSelection();
        }

        private void ToggleSelectionMembership(NoteRef r)
        {
            if (!selection.Remove(r)) selection.Add(r);
            SyncSelectedNoteFromSelection();
            pendingSlideStart = null;
            ClearEventSelection();
        }

        private void ClearSelection()
        {
            selection.Clear();
            selectedNote = null;
            // 高さレーンのドラッグは選択中ノーツにしか掛からない。Undo等でchartごと差し替わったとき、
            // 消えたNoteへの参照を掴んだままにしないようここでも切る。
            heightDragNote = null;
            heightDragPointIndex = -1;
            heightEasingCycleCandidate = null;
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
            heightDragNote = null;
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
        // editor-ui-rework-r2.md §4: ガター対策でPaneAtの代わりにTryPaneAtを使うため、
        // ガター上に来たフレームは直前の有効な値を使い続ける（無いとカーソルが盤面中央へ飛ぶ）。
        private float dragLastValidCell;
        private float dragLastValidLayer;
        // editor-ui-rework-mmw.md §5.2-3: ドラッグは掴んだ「点」だけを動かす（ノーツ全体ではない）。
        private Dictionary<NoteRef, Waypoint> dragOriginByRef;

        // §5.3: クリック(ドラッグ無し)とドラッグを区別するための開始位置。
        private Vector2 dragStartScreenPos;
        // Slideツールで始点/中継点をクリック(ドラッグ無し)した場合にeasingを巡回する対象。
        // ドラッグと判定されたら何もしない。対象が無い(終点を掴んだ、Selectツールでの通常ドラッグ等)場合はnull。
        private NoteRef? easingCycleCandidate;

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

        // ---- MikuMikuWorld移植候補: パターンプリセット ----
        // PresetManager.h相当。ディスク永続化はまだ無く、アプリ実行中だけ保持する（次回増分候補）。
        private class NotePreset
        {
            public string name;
            public List<Note> notes;
        }
        private readonly List<NotePreset> presets = new();

        // ---- §7.5 高さレーン（Ground/Sky を跨ぐ Slide の layerF を専用軸で編集する） ----
        // 横軸1本に cellF と layerF が混ざる CombinedX では遷移中の高さが読めないため、
        // 高さ専用の軸を Sky とイベントレーンの間に立てる。既定は折りたたみ（幅0）で、
        // 「表示」メニューか右パネルの表示設定からトグルする。
        private bool showHeightLane;
        private float heightLaneWidth = 100f;
        private float HeightLaneW => showHeightLane ? heightLaneWidth : 0f;

        // 高さレーン上での waypoint ドラッグ（layerF の直接編集）
        private Note heightDragNote;
        private int heightDragPointIndex = -1;
        // editor-ui-rework-r2.md §6.3: 高さレーンでのクリック(ドラッグ無し)はeasingHを巡回する
        // （シート本体のeasingCycleCandidateの高さレーン版）。
        private Vector2 heightDragStartScreenPos;
        private NoteRef? heightEasingCycleCandidate;

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
            // §7.4の幅変更・§7.5の高さドラッグも同じくchart.notesを直接書き換えるので同列に扱う。
            bool draggingAnything = draggingNote || resizingNote != null || heightDragNote != null;
            if (dirty && !draggingAnything && Time.unscaledTime - lastPreviewRebuildRealtime > 0.3f)
            {
                preview.Rebuild(song, chart, Path.GetDirectoryName(songPath));
                lastPreviewRebuildRealtime = Time.unscaledTime;
            }

            if (followPlayback && preview.IsPlaying)
            {
                int followTick = Math.Max(0, ChartFormat.SecondsToTick(chart.bpmEvents, preview.SongTime));
                if (scrollFollowMode == ScrollFollowMode.Smooth)
                {
                    scrollTick = followTick;
                }
                else
                {
                    // Page: 画面上端(=最も未来のtick)を超えるまではスクロールを止め、
                    // 超えた瞬間に判定線位置へ揃え直す（1ページ分のジャンプ）。
                    var L = CurrentSheetLayout();
                    if (followTick > L.TopTick) scrollTick = followTick;
                }
            }

            // §3: 再生が止まった瞬間の時刻をカーソルへ書き戻す。参照元(EditorWindows.cpp:513-546)と
            // 同じく、再生中はpreview.SongTimeが真の値・停止中はcursorTickが真の値、と役割を切り替える。
            if (wasPlayingLastFrame && !preview.IsPlaying)
                cursorTick = Mathf.Max(0, ChartFormat.SecondsToTick(chart.bpmEvents, preview.SongTime));
            wasPlayingLastFrame = preview.IsPlaying;

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

        private UndoSnapshot CaptureSnapshot(string label) => new() { chart = CloneChart(chart), header = header, label = label };

        /// <summary>
        /// 変更を適用する直前に呼ぶ。coalesce=trueは「直前の記録から一定時間内なら1手にまとめる」
        /// （スライダー操作など、フレームごとに変更が飛んでくる編集向け）。
        /// MikuMikuWorld移植候補: labelはメニューの「元に戻す: {label}」に表示する説明文字列
        /// （参照元pushHistory(description, prev, curr)、ScoreEditor.cpp:327-336）。
        /// </summary>
        private void PushUndo(bool coalesce, string label = "編集")
        {
            float now = Time.unscaledTime;
            if (coalesce && undoStack.Count > 0 && now - lastUndoPushRealtime < UndoCoalesceSec)
            {
                lastUndoPushRealtime = now;
                return; // 直前の変更前スナップショットをそのまま使う(既に積んである)
            }
            undoStack.Add(CaptureSnapshot(label));
            if (undoStack.Count > UndoLimit) undoStack.RemoveAt(0);
            redoStack.Clear();
            lastUndoPushRealtime = now;
        }

        private void Undo()
        {
            if (undoStack.Count == 0) return;
            var snap = undoStack[^1];
            undoStack.RemoveAt(undoStack.Count - 1);
            // redo側のラベルは「undoが今回取り消す操作」を引き継ぐ（やり直すと同じ操作が再適用されるため）。
            redoStack.Add(CaptureSnapshot(snap.label));
            ApplySnapshot(snap);
        }

        private void Redo()
        {
            if (redoStack.Count == 0) return;
            var snap = redoStack[^1];
            redoStack.RemoveAt(redoStack.Count - 1);
            undoStack.Add(CaptureSnapshot(snap.label));
            ApplySnapshot(snap);
        }

        /// <summary>メニュー/ボタンの「元に戻す: {…}」表示用。無ければ空文字。</summary>
        private string PeekUndoLabel() => undoStack.Count > 0 ? undoStack[^1].label : "";
        private string PeekRedoLabel() => redoStack.Count > 0 ? redoStack[^1].label : "";

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

        // ---------- §3 再生位置カーソル ----------

        /// <summary>cursorTick(再生開始位置)を秒に変換する。BuildTickToSecondsは呼ぶたびに
        /// bpmEventsから区分線形関数を組み立てる軽い処理で、Update()のfollowPlayback処理と
        /// 同じ頻度・同じ許容コストで既に使われている（SecondsToTickの逆）。</summary>
        private float TickToSeconds(int tick) => ChartFormat.BuildTickToSeconds(chart.bpmEvents)(tick);

        /// <summary>MikuMikuWorld移植候補: 小節ジャンプ（ScoreEditor.cpp:409-416のgotoMeasure）。
        /// カーソルをその小節の頭へ移し、画面外なら追従してスクロールする。</summary>
        private void GotoMeasure(int measure)
        {
            if (measure < 0) return;
            cursorTick = SongAddr.ToTick(song.meters, measure, 1, 0);
            EnsureCursorVisible();
            statusMessage = $"#{measure} 小節目へ移動しました";
        }

        /// <summary>cursorTickが現在の表示範囲から外れたら、追従スクロールする。
        /// editor-ui-rework-r2.md §5: 旧実装は画面外に出た瞬間カーソルを判定線位置へ
        /// 持ってきており（scrollTick=cursorTick）、↑↓キーで1マスずつ動かしている最中に
        /// 画面が丸ごと飛んで読みにくかった。画面端に達したら1/4画面分だけ余裕を持って
        /// スクロールする（参照元のcenterCursor相当だが、判定線位置が可変なため中央寄せではなく
        /// 端からの余白確保にしている）。</summary>
        private void EnsureCursorVisible()
        {
            var L = CurrentSheetLayout();
            int visibleRange = Mathf.Max(1, L.TopTick - L.BottomTick);
            int margin = visibleRange / 4;
            if (cursorTick > L.TopTick) scrollTick = Mathf.Max(0, scrollTick + (cursorTick - L.TopTick) + margin);
            else if (cursorTick < L.BottomTick) scrollTick = Mathf.Max(0, scrollTick - (L.BottomTick - cursorTick) - margin);
        }

        /// <summary>ステータスバーの▶ボタン。停止中は必ずcursorTick位置から再生を始める
        /// （editor-ui-rework-mmw.md §3）。一時停止時の書き戻しはUpdate()側で行う。</summary>
        private void TogglePlayFromCursor()
        {
            if (preview.IsPlaying)
            {
                preview.Pause();
            }
            else
            {
                preview.Seek(TickToSeconds(cursorTick));
                preview.Play();
            }
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
            // （左=小節番号の退避先、右=イベントレーン §7.3）。heightLane は §7.5 の高さレーンで、
            // 折りたたみ時は幅0（＝存在しないのと同じ扱いになる）。
            // editor-ui-redesign.md §7.2 どおり、帯の大きさは今後設定画面から変更できるよう
            // ChartEditorApp側のフィールド(sheetMarginLeft/sheetMarginRight/heightLaneWidth)経由で渡す。
            public readonly Rect rect, leftMargin, ground, gutter, sky, heightLane, rightMargin;
            public readonly float pxPerTick, judgeLineY;
            private readonly int scrollTick;

            /// <summary>高さレーンの内側余白。layerF=0/1 の点が帯の縁で欠けないように左右を空ける。</summary>
            private const float HeightLanePad = 8f;

            public SheetLayout(Rect rect, float pxPerBeat, int scrollTick, float judgeLineFrac,
                float marginLeft, float marginRight, float heightLaneW)
            {
                this.rect = rect;
                this.scrollTick = scrollTick;

                const float gutterW = 26f;
                leftMargin = new Rect(rect.x, rect.y, marginLeft, rect.height);
                rightMargin = new Rect(rect.xMax - marginRight, rect.y, marginRight, rect.height);
                heightLane = new Rect(rightMargin.xMin - heightLaneW, rect.y, heightLaneW, rect.height);

                float lanesX = leftMargin.xMax;
                float lanesW = Mathf.Max(0f, heightLane.xMin - lanesX - gutterW);
                float paneW = lanesW * 0.5f;
                ground = new Rect(lanesX, rect.y, paneW, rect.height);
                gutter = new Rect(ground.xMax, rect.y, gutterW, rect.height);
                sky = new Rect(gutter.xMax, rect.y, paneW, rect.height);

                pxPerTick = pxPerBeat / ChartData.TicksPerBeat;
                judgeLineY = rect.y + rect.height * Mathf.Clamp01(judgeLineFrac);
            }

            /// <summary>§7.5 高さレーン: layerF(0=Ground, 1=Sky) → x。折りたたみ中は帯の左端を返す。</summary>
            public float LayerToX(float layerF) =>
                heightLane.x + HeightLanePad + Mathf.Clamp01(layerF) * Mathf.Max(0f, heightLane.width - HeightLanePad * 2f);

            public float XToLayer(float x)
            {
                float inner = Mathf.Max(0f, heightLane.width - HeightLanePad * 2f);
                if (inner <= 0f) return 0f;
                return Mathf.Clamp01((x - heightLane.x - HeightLanePad) / inner);
            }

            public float TickToY(int tick) => judgeLineY - (tick - scrollTick) * pxPerTick;
            public int YToTick(float y) => scrollTick + Mathf.RoundToInt((judgeLineY - y) / pxPerTick);

            // TickToYは下に行くほどtickが小さくなるため、上端が「大きいtick」・下端が「小さいtick」になる。
            public int TopTick => scrollTick + Mathf.CeilToInt((judgeLineY - rect.y) / pxPerTick);
            public int BottomTick => scrollTick - Mathf.CeilToInt((rect.yMax - judgeLineY) / pxPerTick);

            public static float CellX(Rect pane, float cellF) => pane.x + cellF / Cells * pane.width;

            /// <summary>
            /// editor-ui-rework-mmw.md §4: 横軸はcellFのみで決め、layerFで横に動かさない
            /// （旧CombinedXはlerpでGround/Sky間を横移動させており「skyのノーツも高さを変えると
            /// 場所が動く」というユーザー指摘の原因だった）。
            /// forceSky=true（=このノーツは高さ情報を持つ＝waypoint間でlayerFが変化するSlide）なら
            /// 常にSkyペインのcellFで位置を決める。layerFはHeightAlphaで濃淡として表現する。
            /// forceSky=false（単発ノーツ、または高さ変化の無いSlide）は従来どおりlayerF(常に0か1)で
            /// Ground/Skyどちらのペインかを決める（この場合は結果がCombinedXと完全に一致し回帰は無い）。
            /// </summary>
            public float NoteX(float layerF, float cellF, bool forceSky) =>
                forceSky ? CellX(sky, cellF) : CellX(layerF >= 0.5f ? sky : ground, cellF);

            /// <summary>x座標がGround/Skyどちらのペインか。ガター上なら中間値を返す（単発ノーツは置けない）。</summary>
            public (float layerF, float cellF) PaneAt(float x)
            {
                if (x >= ground.xMin && x <= ground.xMax)
                    return (0f, Mathf.Clamp((x - ground.x) / ground.width * Cells, 0f, Cells));
                if (x >= sky.xMin && x <= sky.xMax)
                    return (1f, Mathf.Clamp((x - sky.x) / sky.width * Cells, 0f, Cells));
                return (0.5f, Cells * 0.5f);
            }

            /// <summary>editor-ui-rework-r2.md §4: PaneAtと違い、ガター上ではnullを返す
            /// （PaneAtの(0.5, Cells*0.5)は「ガターの中間値」であって実在するペイン位置ではないため、
            /// ペースト/ドラッグの基準点としてそのまま使うとガターを跨いだ瞬間に座標が飛ぶ）。</summary>
            public (float layerF, float cellF)? TryPaneAt(float x)
            {
                if (x >= ground.xMin && x <= ground.xMax)
                    return (0f, Mathf.Clamp((x - ground.x) / ground.width * Cells, 0f, Cells));
                if (x >= sky.xMin && x <= sky.xMax)
                    return (1f, Mathf.Clamp((x - sky.x) / sky.width * Cells, 0f, Cells));
                return null;
            }
        }

        private SheetLayout CurrentSheetLayout()
        {
            var r = notesSheet.contentRect;
            return new SheetLayout(new Rect(0f, 0f, r.width, r.height), pxPerBeat, scrollTick, judgeLineFrac,
                sheetMarginLeft, sheetMarginRight, HeightLaneW);
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
        /// 任意角度の線分。他の描画はすべて軸並行なので FillRect で足りていたが、
        /// §7.5 の高さカーブだけは斜めの折れ線が要る。太さぶん法線方向にオフセットした
        /// 四角形として塗る（Painter2Dのstroke系APIを使わないのは、このファイルの他の描画と
        /// 同じ「塗りつぶしパスのみ」に揃えるため）。
        /// </summary>
        private static void FillLine(Painter2D p, Vector2 a, Vector2 b, Color c, float thickness)
        {
            var d = b - a;
            float len = d.magnitude;
            if (len < 0.0001f) return;
            var n = new Vector2(-d.y, d.x) / len * (thickness * 0.5f);
            p.fillColor = c;
            p.BeginPath();
            p.MoveTo(a + n);
            p.LineTo(b + n);
            p.LineTo(b - n);
            p.LineTo(a - n);
            p.ClosePath();
            p.Fill();
        }

        /// <summary>§3 再生位置カーソルの左余白つまみ用。3頂点を1パスで塗る。</summary>
        private static void FillTriangle(Painter2D p, Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            p.fillColor = color;
            p.BeginPath();
            p.MoveTo(a);
            p.LineTo(b);
            p.LineTo(c);
            p.ClosePath();
            p.Fill();
        }

        /// <summary>§5.4: 4頂点の任意四角形を1パスで塗る。Slideの帯の1区間（斜め区間でもがたつかない）。</summary>
        private static void FillQuad(Painter2D p, Vector2 a, Vector2 b, Vector2 c2, Vector2 d, Color color)
        {
            p.fillColor = color;
            p.BeginPath();
            p.MoveTo(a);
            p.LineTo(b);
            p.LineTo(c2);
            p.LineTo(d);
            p.ClosePath();
            p.Fill();
        }

        /// <summary>§5.4: 帯全体を1本の塗りつぶしパスにする（濃淡が一定のSlide用）。左端の頂点列を
        /// 順にたどり、右端の頂点列を逆順にたどって閉じることで台形の連なりを1つの多角形にする。</summary>
        private static void FillBand(Painter2D p, List<Vector2> left, List<Vector2> right, Color color)
        {
            p.fillColor = color;
            p.BeginPath();
            p.MoveTo(left[0]);
            for (int i = 1; i < left.Count; i++) p.LineTo(left[i]);
            for (int i = right.Count - 1; i >= 0; i--) p.LineTo(right[i]);
            p.ClosePath();
            p.Fill();
        }

        /// <summary>
        /// §5.1: Slideの始点・終点にTapと同じ見た目（幅の帯を高さ8pxの矩形で描く）の目印を描く。
        /// forceSky時はHeightAlphaで濃淡を付ける（layerFが0でも完全には透明にしない）。
        /// </summary>
        private static void DrawEndpointGlyph(Painter2D p, SheetLayout L, Waypoint wp, Color col, bool forceSky)
        {
            float y = L.TickToY(wp.tick);
            if (y < L.rect.y - 8 || y > L.rect.yMax + 8) return;
            float x0 = L.NoteX(wp.layerF, wp.cellF, forceSky);
            float x1 = L.NoteX(wp.layerF, wp.cellF + wp.width, forceSky);
            float alpha = forceSky ? HeightAlpha(wp.layerF) : 1f;
            FillRect(p, Rect.MinMaxRect(Mathf.Min(x0, x1), y - 4, Mathf.Max(x0, x1), y + 4),
                new Color(col.r, col.g, col.b, alpha));
        }

        /// <summary>editor-ui-rework-r2.md §1: 中継点は常に存在を示す（markerによる非表示をやめる）。
        /// Visible(コンボ点)は塗りつぶし、None/Invisible(コンボにならない)は輪郭のみで区別する。</summary>
        private static void DrawWaypointGlyph(Painter2D p, float x, float y, WaypointMarker marker, Color color)
        {
            var r = new Rect(x - 3, y - 3, 6, 6);
            if (marker == WaypointMarker.Visible) FillRect(p, r, color);
            else FillRectOutline(p, r, color, 1f);
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

            // §7.5 高さレーンの下地（折りたたみ中は幅0なので何も描かれない）
            if (L.heightLane.width > 0f)
            {
                FillRect(p, L.heightLane, new Color(0.13f, 0.13f, 0.16f));
                FillRect(p, new Rect(L.heightLane.x, rect.y, 1, rect.height), new Color(1, 1, 1, 0.08f));
                // 左端=Ground(0) / 中央(0.5) / 右端=Sky(1) の目盛り
                FillRect(p, new Rect(L.LayerToX(0f), rect.y, 1, rect.height), new Color(1, 1, 1, 0.16f));
                FillRect(p, new Rect(L.LayerToX(0.5f), rect.y, 1, rect.height), new Color(1, 1, 1, 0.06f));
                FillRect(p, new Rect(L.LayerToX(1f), rect.y, 1, rect.height), new Color(1, 1, 1, 0.16f));
            }

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
                bool isMeasure = addr.beat == 1 && addr.tick == 0;
                Color c;
                float thickness;
                if (isMeasure) { c = new Color(1, 1, 1, 0.5f); thickness = 2f; }
                else if (addr.tick == 0) { c = new Color(1, 1, 1, 0.28f); thickness = 1f; }
                else { c = new Color(1, 1, 1, 0.12f); thickness = 1f; }

                // ガターは横断しない。参照元(ScoreEditor.cpp:503-528,545)と同じく、小節線だけは
                // 左余白へ延長して小節番号ラベル(UpdateSheetLabels)と繋げる。
                if (isMeasure)
                    FillRect(p, new Rect(L.leftMargin.x, y, L.ground.xMax - L.leftMargin.x, thickness), c);
                else
                    FillRect(p, new Rect(L.ground.x, y, L.ground.width, thickness), c);
                FillRect(p, new Rect(L.sky.x, y, L.sky.width, thickness), c);
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
                    float x0 = L.NoteX(wp.layerF, wp.cellF, forceSky: false);
                    float x1 = L.NoteX(wp.layerF, wp.cellF + wp.width, forceSky: false);
                    FillRect(p, Rect.MinMaxRect(Mathf.Min(x0, x1), y - 4, Mathf.Max(x0, x1), y + 4), col);
                }
                else
                {
                    // §4: waypoint間でlayerFが変化する(=高さ情報を持つ)Slideは、Groundには一切描かず
                    // Skyペインのみに、layerFを濃淡(HeightAlpha)として表現して描く。
                    // 変化が無ければ従来どおり自分の層(Ground or Sky)だけに不透明で描く。
                    bool forceSky = HasHeightVariation(note);

                    if (forceSky)
                    {
                        // 濃淡がwaypointごとに変わるため、区間ごとにquadを塗る(§5.4: 矩形ではなく
                        // 実際の四隅を結ぶquadにして、斜め区間でもがたつきを抑える)。
                        int stepTicks = Mathf.Max(1, Mathf.RoundToInt(4f / L.pxPerTick));
                        Vector2? prevL = null, prevR = null;
                        float prevAlpha = 0f;
                        // tc は t を nEnd に丸めた値。t が nEnd をまたいでも最後の区間が
                        // 必ず真の終点まで届くよう、ループの継続条件ではなく break で終わらせる
                        // （旧実装の t2=Mathf.Min(t+stepTicks,nEnd) と同じ意図）。
                        for (int t = nStart; ; t += stepTicks)
                        {
                            int tc = Mathf.Min(t, nEnd);
                            var s = InterpAtTick(note, tc);
                            float y = L.TickToY(tc);
                            float x0 = L.NoteX(s.layerF, s.cellF, forceSky: true);
                            float x1 = L.NoteX(s.layerF, s.cellF + s.width, forceSky: true);
                            var curL = new Vector2(Mathf.Min(x0, x1), y);
                            var curR = new Vector2(Mathf.Max(x0, x1), y);
                            float curAlpha = 0.55f * HeightAlpha(s.layerF);

                            if (prevL.HasValue)
                            {
                                bool bothOffscreen = (y > rect.yMax + 8 && prevL.Value.y > rect.yMax + 8) ||
                                                     (y < rect.y - 8 && prevL.Value.y < rect.y - 8);
                                if (!bothOffscreen)
                                {
                                    float segAlpha = (prevAlpha + curAlpha) * 0.5f;
                                    FillQuad(p, prevL.Value, curL, curR, prevR.Value, new Color(col.r, col.g, col.b, segAlpha));
                                }
                            }
                            prevL = curL; prevR = curR; prevAlpha = curAlpha;
                            if (tc == nEnd) break;
                        }

                        for (int i = 1; i < note.points.Count - 1; i++)
                        {
                            var wp = note.points[i];
                            float y = L.TickToY(wp.tick);
                            float x = L.NoteX(wp.layerF, wp.cellF, forceSky: true);
                            DrawWaypointGlyph(p, x, y, wp.marker, new Color(1f, 1f, 1f, HeightAlpha(wp.layerF)));
                        }
                    }
                    else
                    {
                        // §5.4: 高さ変化が無い帯は濃淡が一定なので、1本の塗りつぶしパスにできる
                        // （斜め区間・easing区間もPainter2Dのアンチエイリアスでなめらかになる）。
                        int stepTicks = Mathf.Max(1, Mathf.RoundToInt(4f / L.pxPerTick));
                        var leftPts = new List<Vector2>();
                        var rightPts = new List<Vector2>();
                        for (int t = nStart; ; t += stepTicks)
                        {
                            int tc = Mathf.Min(t, nEnd);
                            var s = InterpAtTick(note, tc);
                            float y = L.TickToY(tc);
                            float x0 = L.NoteX(s.layerF, s.cellF, forceSky: false);
                            float x1 = L.NoteX(s.layerF, s.cellF + s.width, forceSky: false);
                            leftPts.Add(new Vector2(Mathf.Min(x0, x1), y));
                            rightPts.Add(new Vector2(Mathf.Max(x0, x1), y));
                            if (tc == nEnd) break;
                        }
                        if (leftPts.Count >= 2 && !(rightPts[^1].y > rect.yMax + 8 && leftPts[0].y < rect.y - 8))
                            FillBand(p, leftPts, rightPts, new Color(col.r, col.g, col.b, 0.55f));

                        for (int i = 1; i < note.points.Count - 1; i++)
                        {
                            var wp = note.points[i];
                            float y = L.TickToY(wp.tick);
                            float x = L.NoteX(wp.layerF, wp.cellF, forceSky: false);
                            DrawWaypointGlyph(p, x, y, wp.marker, Color.white);
                        }
                    }

                    // §5.1: Slideの両端には必ずTapと同じ見た目の始点・終点を描く
                    // （帯だけでは「押し始め/離す」位置が視覚的に分かりにくいため）。
                    var startWp = note.points[0];
                    var endWp = note.points[^1];
                    DrawEndpointGlyph(p, L, startWp, col, forceSky);
                    DrawEndpointGlyph(p, L, endWp, col, forceSky);
                }
            }

            // §5.2: 選択のハイライトは選択された「点」だけを囲む（帯や未選択の点は囲まない）。
            // 単発ノーツ・Slideの端点・中継点のいずれも同じ描き方で統一できる。
            foreach (var r in selection)
            {
                var note = r.note;
                if (r.index < 0 || r.index >= note.points.Count) continue;
                var wp = note.points[r.index];
                float y = L.TickToY(wp.tick);
                if (y < rect.y - 10 || y > rect.yMax + 10) continue;

                bool forceSky = note.points.Count > 1 && HasHeightVariation(note);
                float x0 = L.NoteX(wp.layerF, wp.cellF, forceSky);
                float x1 = L.NoteX(wp.layerF, wp.cellF + wp.width, forceSky);
                var box = Rect.MinMaxRect(Mathf.Min(x0, x1) - 3, y - 6, Mathf.Max(x0, x1) + 3, y + 6);
                FillRectOutline(p, box, Color.yellow);
            }

            DrawHeightLane(p, L);

            // 判定線(追従の同期位置)。judgeLineFracで高さを変更可能（右パネルの「表示設定」）
            // 高さレーンも同じ時間軸なので、判定線はそちらまで伸ばす。
            float judgeXMax = L.heightLane.width > 0f ? L.heightLane.xMax : lanesXMax;
            FillRect(p, new Rect(lanesXMin, L.judgeLineY - 1, judgeXMax - lanesXMin, 2), new Color(1f, 0.25f, 0.25f, 0.9f));

            // §3 再生位置カーソル(橙)。判定線と見た目は同じ太さだが色で区別し、時間軸上の
            // 「再生を開始する位置」を示す（判定線はスクロール追従の同期位置で意味が異なる）。
            // 参照元(ScoreEditor.cpp:565-584)にならい、左余白に小さな三角のつまみを添える。
            {
                float cy = L.TickToY(cursorTick);
                if (cy >= rect.y - 10f && cy <= rect.yMax + 10f)
                {
                    var cursorColor = new Color(1f, 0.62f, 0.12f, 0.95f);
                    FillRect(p, new Rect(lanesXMin, cy - 1, judgeXMax - lanesXMin, 2), cursorColor);
                    const float triH = 5f;
                    FillTriangle(p,
                        new Vector2(L.leftMargin.x + 2f, cy - triH),
                        new Vector2(L.leftMargin.x + 2f, cy + triH),
                        new Vector2(L.leftMargin.x + 2f + triH * 1.6f, cy),
                        cursorColor);
                }
            }

            // Slide配置中(1点目クリック済み・2点目待ち)の視覚フィードバック。マウスがシート外
            // （インスペクタ確認等）でも1点目クリック済みなことが分かるよう、ホバーの有無に関わらず出す。
            if (pendingSlideStart != null)
            {
                var wp0 = pendingSlideStart.points[0];
                float py = L.TickToY(wp0.tick);
                float px = L.NoteX(wp0.layerF, wp0.cellF, forceSky: false); // 単一点なのでまだ高さ情報は無い
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

        // ---------- §7.5 高さレーン ----------

        /// <summary>
        /// 縦=時間（ノーツシートと共有）、横=layerF（左端0=Ground、右端1=Sky）。
        /// 3Dステージを正面左から投影して90度回した図に相当する。
        ///
        /// editor-ui-rework-r2.md §2: 旧実装は選択中ノーツしか点を描かず掴めなかった
        /// （同時押しSlideの重なり対策として editor-ui-redesign.md §7.5 で導入した絞り込み）。
        /// 実機で「Skyのslideを高さレーンから選択できない」という逆転が起きたため、
        /// **全ノーツを常に描いてクリックで選択できる**方式に弱める。重なり対策は
        /// 「選択中を濃く・後に描く」＋HandleHeightLanePointerDownの2パス探索（選択中を優先して掴む）
        /// で代替する。単発ノーツ(points.Count==1)も点として描く（layerFは0/1にスナップされる）。
        /// </summary>
        private void DrawHeightLane(Painter2D p, SheetLayout L)
        {
            if (L.heightLane.width <= 0f) return;

            var selectedNotes = new HashSet<Note>(SelectedNotesDistinct());

            foreach (var note in chart.notes)
            {
                if (selectedNotes.Contains(note)) continue;
                DrawHeightCurve(p, L, note, new Color(1f, 1f, 1f, 0.28f), selected: false);
            }

            // 選択中を後に描くことで、重なっても選択中が手前に出る。
            foreach (var note in selectedNotes)
                DrawHeightCurve(p, L, note, NoteColor(note.kind), selected: true);
        }

        /// <summary>selectedがfalseのときは点を輪郭のみで描き（§1のmarker区別は選択中にのみ適用)、
        /// カーブ・点とも渡されたcol(既に非選択用の低alpha)をそのまま使う。</summary>
        private void DrawHeightCurve(Painter2D p, SheetLayout L, Note note, Color col, bool selected)
        {
            var pts = note.points;

            for (int i = 0; i < pts.Count - 1; i++)
            {
                int tickA = pts[i].tick, tickB = pts[i + 1].tick;
                float yA = L.TickToY(tickA), yB = L.TickToY(tickB);
                if (Mathf.Max(yA, yB) < L.rect.y - 8f || Mathf.Min(yA, yB) > L.rect.yMax + 8f) continue;

                // easingHによる曲線をそのまま見せたいので、区間を約6px刻みに割って折れ線で近似する
                // （layerFが両端で同じなら直線なので分割しない）。§6: 高さレーンが参照するのは
                // 高さ方向のeasingH（横方向のeasingではない。取り違えると横の設定が高さカーブに
                // 反映されてしまう）。
                bool curved = !Mathf.Approximately(pts[i].layerF, pts[i + 1].layerF)
                              && pts[i].easingH != Easing.Linear;
                int steps = curved ? Mathf.Clamp(Mathf.RoundToInt(Mathf.Abs(yA - yB) / 6f), 1, 64) : 1;

                for (int s = 0; s < steps; s++)
                {
                    int t0 = Mathf.RoundToInt(Mathf.Lerp(tickA, tickB, (float)s / steps));
                    int t1 = Mathf.RoundToInt(Mathf.Lerp(tickA, tickB, (float)(s + 1) / steps));
                    var a = new Vector2(L.LayerToX(InterpAtTick(note, t0).layerF), L.TickToY(t0));
                    var b = new Vector2(L.LayerToX(InterpAtTick(note, t1).layerF), L.TickToY(t1));
                    FillLine(p, a, b, col, 2f);
                }
            }

            for (int i = 0; i < pts.Count; i++)
            {
                float y = L.TickToY(pts[i].tick);
                if (y < L.rect.y - 8f || y > L.rect.yMax + 8f) continue;
                float x = L.LayerToX(pts[i].layerF);
                bool grabbed = ReferenceEquals(note, heightDragNote) && i == heightDragPointIndex;
                // §1: Visible(コンボ点)は塗りつぶし、None/Invisibleは輪郭のみで区別する。
                // 始点・終点はmarkerの意味がTap型マーカーの重ね描き可否なので常に塗りつぶし扱いにする。
                // ただし非選択(selected=false)は§2どおりmarkerに関わらず常に輪郭のみ（濃さで選択状態を表す）。
                bool solid = selected && (i == 0 || i == pts.Count - 1 || pts[i].marker == WaypointMarker.Visible);
                if (solid)
                {
                    FillRect(p, new Rect(x - 4, y - 4, 8, 8), grabbed ? Color.yellow : Color.white);
                    FillRect(p, new Rect(x - 2.5f, y - 2.5f, 5, 5), col);
                }
                else
                {
                    var outlineCol = grabbed ? Color.yellow : selected ? Color.white : col;
                    FillRectOutline(p, new Rect(x - 4, y - 4, 8, 8), outlineCol, 1f);
                }
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

            // §1: 貼り付けモード中は配置ツールのゴーストより優先。
            if (pasting) { DrawPasteGhost(p, L); return; }

            int snapTicks = SnapTicks;
            int tick = SnapTickTo(Mathf.Max(0, L.YToTick(pos.y)), snapTicks);

            // イベントレーンは現在のツールに関わらず常時クリックで追加できるので、
            // ホバー時のゴーストもツール非依存で出す。
            if (L.rightMargin.Contains(pos))
            {
                DrawEventGhost(p, L, pos, tick);
                return;
            }

            // §7.5 高さレーンは既存waypointの編集専用でノーツを配置しないため、ゴーストは出さない。
            if (L.heightLane.width > 0f && L.heightLane.Contains(pos)) return;

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
                            // §4: 始点と現在のホバー位置でlayerFが違えば、完成後は高さ情報を持つ
                            // Slideになる＝Skyペインのみに描かれる。プレビューもそれに合わせる。
                            bool previewForceSky = !Mathf.Approximately(wp0.layerF, layerF);
                            float y0 = L.TickToY(wp0.tick), y1 = L.TickToY(tick);
                            float x0 = L.NoteX(wp0.layerF, wp0.cellF + wp0.width * 0.5f, previewForceSky);
                            float x1 = L.NoteX(layerF, cellF + defaultWidthCells * 0.5f, previewForceSky);
                            var lineCol = new Color(col.r, col.g, col.b, 0.4f);
                            FillRect(p, Rect.MinMaxRect(Mathf.Min(x0, x1) - 2, Mathf.Min(y0, y1), Mathf.Max(x0, x1) + 2, Mathf.Max(y0, y1)), lineCol);
                            DrawGhostPoint(p, L, tick, layerF, cellF, defaultWidthCells, col, previewForceSky);
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
                            bool forceSky = HasHeightVariation(selectedNote);
                            // 高さ情報を持つSlideはSkyペインのみに描くため、マウスのペイン位置(layerF)は
                            // 意味を持たない。既存カーブを補間したlayerFを初期値にし、必要なら高さレーンで
                            // 調整してもらう（ResolveInsertLayerと同じ規則）。
                            float previewLayerF = forceSky ? InterpAtTick(selectedNote, tick).layerF : layerF;
                            DrawGhostPoint(p, L, tick, previewLayerF, cellF, width, Color.white, forceSky);
                        }
                    }
                    break;
                }
            }
        }

        private static void DrawGhostPoint(Painter2D p, SheetLayout L, int tick, float layerF, float cellF, float width, Color baseColor, bool forceSky = false)
        {
            float y = L.TickToY(tick);
            float x0 = L.NoteX(layerF, cellF, forceSky);
            float x1 = L.NoteX(layerF, cellF + width, forceSky);
            float alphaScale = forceSky ? HeightAlpha(layerF) : 1f;
            var fill = new Color(baseColor.r, baseColor.g, baseColor.b, 0.4f * alphaScale);
            var outline = new Color(baseColor.r, baseColor.g, baseColor.b, 0.85f * alphaScale);
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

            // §1: 貼り付けモード中は他のどの操作よりも優先。左クリックで確定、右クリックでキャンセル。
            if (pasting)
            {
                if (evt.button == 0) ConfirmPaste();
                else if (evt.button == 1) CancelPaste();
                evt.StopPropagation();
                return;
            }

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

            // §7.5 高さレーン: 選択中ノーツの waypoint を掴んで layerF をドラッグ編集する。
            // ノーツの配置ではなく既存の値の編集なので、どのツールを選んでいても同じ挙動にする。
            if (L.heightLane.width > 0f && L.heightLane.Contains(pos))
            {
                HandleHeightLanePointerDown(L, pos, evt);
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
                    PushUndo(coalesce: false, "ノーツ配置");
                    chart.notes.Add(note);
                    SetSingleSelection(note);
                    dirty = true;
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
                            float insertLayer = ResolveInsertLayer(selectedNote, tick, layerF);
                            PushUndo(coalesce: false, "中継点を追加");
                            selectedNote.points.Insert(insertAt, NewWaypoint(tick, insertLayer, cellF, width));
                            dirty = true;
                        }
                    }
                    break;
                }
                case EditorTool.Delete:
                {
                    var hit = HitTestPoint(L, pos);
                    if (hit.HasValue)
                    {
                        PushUndo(coalesce: false, "ノーツ削除");
                        RemovePoint(hit.Value);
                        dirty = true;
                    }
                    break;
                }
                case EditorTool.Slide:
                {
                    // §5.3: 既存Slideの始点/中継点/終点をクリックした場合は新規配置ではなく点の操作
                    // （ドラッグ=その点だけ移動、ドラッグせずクリック=easing巡回）。
                    // 他種別のノーツや空白では従来どおり配置フローへ進む。
                    var hitOnSlide = HitTestPoint(L, pos);
                    if (hitOnSlide.HasValue && hitOnSlide.Value.note.kind == NoteKind.Slide)
                    {
                        var hp = hitOnSlide.Value;
                        if (!selection.Contains(hp)) SetSingleSelection(hp);
                        BeginPointDrag(rawTick, rawCell, layerF, pos, evt);
                        // easingは始点/中継点(=次の区間を持つ点)にのみ意味がある。終点はドラッグのみ。
                        easingCycleCandidate = hp.index < hp.note.points.Count - 1 ? hp : (NoteRef?)null;
                        break;
                    }

                    float slideCellF = SnapCellTo(rawCell, 0.5f);
                    if (pendingSlideStart == null)
                    {
                        pendingSlideStart = new Note
                        {
                            kind = NoteKind.Slide,
                            points = new List<Waypoint> { NewWaypoint(tick, layerF, slideCellF, defaultWidthCells) },
                        };
                    }
                    else
                    {
                        int startTick = pendingSlideStart.points[0].tick;
                        if (tick > startTick)
                        {
                            var completed = pendingSlideStart;
                            completed.points.Add(NewWaypoint(tick, layerF, slideCellF, defaultWidthCells));
                            PushUndo(coalesce: false, "Slide配置");
                            chart.notes.Add(completed);
                            pendingSlideStart = null;
                            SetMultiSelection(AllPointRefs(completed));
                            dirty = true;
                            statusMessage = "Slideを配置しました";
                        }
                        else
                        {
                            statusMessage = "Slideの終点は始点より後ろの位置をクリックしてください（1点目は維持中）";
                        }
                    }
                    break;
                }
                case EditorTool.Select:
                default:
                {
                    var hit = HitTestPoint(L, pos);

                    if (hit.HasValue)
                    {
                        var hn = hit.Value;
                        // §7.4-D 端ドラッグでの幅変更（単発ノーツのみ、Shift併用時は選択トグル優先）
                        int edgeSign = hn.note.points.Count == 1 ? EdgeGrabSign(L, hn.note, pos) : 0;
                        if (edgeSign != 0 && !evt.shiftKey)
                        {
                            SetSingleSelection(hn);
                            PushUndo(coalesce: false, "幅変更");
                            resizingNote = hn.note;
                            resizingEdgeSign = edgeSign;
                            resizeOriginWp = hn.note.points[0];
                            notesSheet.CapturePointer(evt.pointerId);
                            evt.StopPropagation();
                            return;
                        }

                        if (evt.shiftKey)
                        {
                            ToggleSelectionMembership(hn);
                        }
                        else if (!selection.Contains(hn))
                        {
                            // 未選択の点をクリック→単一選択に切り替える。
                            // 既に選択済みグループの一員なら選択を維持し、グループごとドラッグできるようにする。
                            SetSingleSelection(hn);
                        }

                        if (selection.Contains(hn))
                            BeginPointDrag(rawTick, rawCell, layerF, pos, evt);
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

        /// <summary>選択中の点のドラッグを開始する（§5.2-3: 掴んだ点だけを動かす）。
        /// Select/Slide両ツールの点ドラッグ開始処理を共通化。</summary>
        private void BeginPointDrag(int rawTick, float rawCell, float layerF, Vector2 pos, PointerDownEvent evt)
        {
            PushUndo(coalesce: false, "移動"); // ドラッグ開始時点(変更前)を1手として記録する
            draggingNote = true;
            dragOriginRawTick = rawTick;
            dragOriginRawCell = rawCell;
            dragOriginRawLayer = layerF;
            dragLastValidCell = rawCell;
            dragLastValidLayer = layerF;
            dragStartScreenPos = pos;
            easingCycleCandidate = null;
            dragOriginByRef = new Dictionary<NoteRef, Waypoint>();
            foreach (var r in selection)
                dragOriginByRef[r] = r.note.points[r.index];
            notesSheet.CapturePointer(evt.pointerId);
        }

        /// <summary>§5.2-2: 始点/終点を削除するとSlide全体が消え、中継点の削除はその点だけが消える
        /// （参照元Editing.cpp:209-251の規則をそのまま踏襲）。単発ノーツはindex常に0なので全体削除。</summary>
        private void RemovePoint(NoteRef r)
        {
            var note = r.note;
            int last = note.points.Count - 1;
            if (note.points.Count == 1 || r.index == 0 || r.index == last)
            {
                chart.notes.Remove(note);
                selection.RemoveAll(x => ReferenceEquals(x.note, note));
            }
            else
            {
                note.points.RemoveAt(r.index);
                // 削除後にindexがずれるため、この点より後ろのindexを持つ選択参照を補正する
                for (int i = 0; i < selection.Count; i++)
                    if (ReferenceEquals(selection[i].note, note) && selection[i].index > r.index)
                        selection[i] = new NoteRef(note, selection[i].index - 1);
                selection.RemoveAll(x => ReferenceEquals(x.note, note) && x.index == r.index);
            }
            SyncSelectedNoteFromSelection();
        }

        /// <summary>
        /// editor-ui-rework-r2.md §7: コンテキストメニュー。参照元(EditorWindows.cpp:146-198の
        /// contextMenu())は位置に関わらずタイムライン全体に同じメニューを張り、実行できない項目は
        /// 無効表示にする（BeginPopupContextWindow）。旧実装は右クリック位置ごとに別内容を
        /// 出し分けており、空白での右クリックには何も出なかった。ここを「常設ブロック（削除／
        /// 切り取り／コピー／貼り付け／反転して貼り付け／選択を反転）＋文脈ブロック（点の上=種別変更、
        /// 帯の上=中継点追加）」の2段構成へ変える。常設項目は位置が常に一定なのでマッスルメモリで
        /// 操作できる（AddDisabledItemで無効表示、Unity 6000.5.6f1に存在を確認済み）。
        ///
        /// 右クリック対象が未選択なら単一選択に切り替えてから開く（既存の複数選択中に
        /// 右クリックした場合はそのグループを対象にする）。空白での右クリックは選択を変えない
        /// （貼り付け先を選ぶ用途で右クリックすることがあるため）。
        /// </summary>
        private void OnSheetRightClick(PointerDownEvent evt)
        {
            var L = CurrentSheetLayout();
            var pos = (Vector2)evt.localPosition;
            if (L.rightMargin.Contains(pos)) { evt.StopPropagation(); return; }
            if (L.heightLane.width > 0f && L.heightLane.Contains(pos)) { evt.StopPropagation(); return; }

            // §5.2: 選択反応は点にのみ。帯は「ここに中継点を追加」の対象だけを別途探す。
            var hit = HitTestPoint(L, pos);
            NoteRef? hp = null;
            if (hit.HasValue)
            {
                hp = hit.Value;
                if (!selection.Contains(hp.Value)) SetSingleSelection(hp.Value);
            }

            Note bandHit = null;
            int bandTick = 0;
            if (!hit.HasValue)
            {
                var band = HitTestSlideBand(L, pos);
                if (band != null)
                {
                    int snapTicks = SnapTicks;
                    int tick = SnapTickTo(Mathf.Max(0, L.YToTick(pos.y)), snapTicks);
                    if (tick > band.points[0].tick && tick < band.points[^1].tick)
                    {
                        bandHit = band;
                        bandTick = tick;
                    }
                }
            }

            var menu = new GenericDropdownMenu();
            int count = selection.Count;
            bool hasSelection = count > 0;
            bool hasClipboard = clipboard.Count > 0;

            // ---- 常設ブロック ----
            string deleteLabel = "削除\tDelete";
            if (hp.HasValue)
            {
                bool wholeNote = hp.Value.note.points.Count == 1 || hp.Value.index == 0 || hp.Value.index == hp.Value.note.points.Count - 1;
                deleteLabel = count > 1 ? $"選択した{count}件を削除\tDelete"
                    : wholeNote ? "このノーツを削除\tDelete" : "この中継点を削除\tDelete";
            }
            if (hasSelection) menu.AddItem(deleteLabel, false, DeleteSelection);
            else menu.AddDisabledItem(deleteLabel, false);

            if (hasSelection) menu.AddItem("切り取り\tCtrl+X", false, () => { CopySelectionToClipboard(); DeleteSelection(); });
            else menu.AddDisabledItem("切り取り\tCtrl+X", false);

            if (hasSelection) menu.AddItem("コピー\tCtrl+C", false, CopySelectionToClipboard);
            else menu.AddDisabledItem("コピー\tCtrl+C", false);

            if (hasClipboard) menu.AddItem("貼り付け\tCtrl+V", false, () => EnterPasteMode());
            else menu.AddDisabledItem("貼り付け\tCtrl+V", false);

            if (hasClipboard) menu.AddItem("反転して貼り付け", false, () => EnterPasteMode(flip: true));
            else menu.AddDisabledItem("反転して貼り付け", false);

            if (hasSelection) menu.AddItem("選択を反転", false, FlipSelected);
            else menu.AddDisabledItem("選択を反転", false);

            // ---- 文脈ブロック ----
            if (hp.HasValue && count == 1 && hp.Value.note.points.Count == 1)
            {
                var hpv = hp.Value;
                menu.AddSeparator("");
                if (hpv.note.kind != NoteKind.Tap) menu.AddItem("Tapに変更", false, () => ChangeNoteKind(hpv.note, NoteKind.Tap));
                if (hpv.note.kind != NoteKind.ExTap) menu.AddItem("Ex Tapに変更", false, () => ChangeNoteKind(hpv.note, NoteKind.ExTap));
                if (hpv.note.kind != NoteKind.Flick) menu.AddItem("Flickに変更", false, () => ChangeNoteKind(hpv.note, NoteKind.Flick));
            }
            else if (bandHit != null)
            {
                var band = bandHit;
                int tick = bandTick;
                menu.AddSeparator("");
                menu.AddItem("ここに中継点を追加", false, () => InsertWaypointInto(band, L, pos, tick));
            }

            var worldPos = notesSheet.LocalToWorld(pos);
            menu.DropDown(new Rect(worldPos, Vector2.zero), notesSheet, DropdownMenuSizeMode.Auto);
            evt.StopPropagation();
        }

        /// <summary>指定したノーツ群の中から、posに最も近いwaypointを探す。</summary>
        private static (Note note, int index, float dist) FindClosestHeightPoint(SheetLayout L, Vector2 pos, IEnumerable<Note> notes)
        {
            Note bestNote = null;
            int bestIndex = -1;
            float bestDist = float.MaxValue;
            foreach (var note in notes)
            {
                for (int i = 0; i < note.points.Count; i++)
                {
                    var wp = note.points[i];
                    float dist = Vector2.Distance(pos, new Vector2(L.LayerToX(wp.layerF), L.TickToY(wp.tick)));
                    if (dist >= bestDist) continue;
                    bestDist = dist;
                    bestNote = note;
                    bestIndex = i;
                }
            }
            return (bestNote, bestIndex, bestDist);
        }

        /// <summary>
        /// editor-ui-rework-r2.md §2: 高さレーンのクリック。全ノーツの waypoint を対象に掴めるが、
        /// **選択中のノーツを優先して探す**（見つからなければ全ノーツへ広げる）ことで、
        /// 同時押しSlideの高さカーブが重なっていても選択中を掴み続けられるようにする
        /// （editor-ui-redesign.md §7.5 の絞り込み理由を、クリック時の優先度として引き継ぐ）。
        /// クリックした点はシート本体と同じ規則で選択状態にもする（Shift=トグル、
        /// 既存グループの一員なら選択維持、外れたら単一選択）。
        /// </summary>
        private void HandleHeightLanePointerDown(SheetLayout L, Vector2 pos, PointerDownEvent evt)
        {
            const float grabRadius = 14f;

            var (note, index, dist) = FindClosestHeightPoint(L, pos, SelectedNotesDistinct());
            if (note == null || dist > grabRadius)
                (note, index, dist) = FindClosestHeightPoint(L, pos, chart.notes);

            if (note == null || dist > grabRadius)
            {
                if (!evt.shiftKey) ClearSelection();
                return;
            }

            var hit = new NoteRef(note, index);
            if (evt.shiftKey) ToggleSelectionMembership(hit);
            else if (!selection.Contains(hit)) SetSingleSelection(hit);

            if (!selection.Contains(hit)) return; // Shiftトグルで選択解除された場合はドラッグしない

            PushUndo(coalesce: false, "高さ編集"); // ドラッグ開始時点(変更前)を1手として記録する
            heightDragNote = note;
            heightDragPointIndex = index;
            heightDragStartScreenPos = pos;
            // §6.3: easingHは「この点から次の点まで」の意味を持つので、始点/中継点(次の区間を持つ点)
            // にのみ巡回対象を設定する。終点や単発ノーツの点はドラッグのみ。
            heightEasingCycleCandidate = index < note.points.Count - 1 ? hit : (NoteRef?)null;
            notesSheet.CapturePointer(evt.pointerId);
        }

        private void InsertWaypointInto(Note note, SheetLayout L, Vector2 pos, int tick)
        {
            var (layerF, rawCell) = L.PaneAt(pos.x);
            float cellF = SnapCellTo(rawCell, 0.5f);
            int insertAt = note.points.FindIndex(pt => pt.tick > tick);
            if (insertAt < 0) insertAt = note.points.Count;
            if (insertAt <= 0 || insertAt >= note.points.Count) return;
            float width = InterpAtTick(note, tick).width;
            float insertLayer = ResolveInsertLayer(note, tick, layerF);
            PushUndo(coalesce: false, "中継点を追加");
            note.points.Insert(insertAt, NewWaypoint(tick, insertLayer, cellF, width));
            dirty = true;
        }

        /// <summary>
        /// §4: 高さ情報を持つSlide(waypoint間でlayerFが変化する)へ新しい中継点を挿入するとき、
        /// マウスのペイン位置(layerF)は意味を持たない（見えているのはSkyペインだけなので）。
        /// 代わりに既存カーブをその時刻で補間した値を初期値にする（後で高さレーンから調整できる）。
        /// 高さ変化の無いSlideでは従来どおりマウス位置のlayerFをそのまま使う。
        /// </summary>
        private static float ResolveInsertLayer(Note note, int tick, float mouseLayerF) =>
            HasHeightVariation(note) ? InterpAtTick(note, tick).layerF : mouseLayerF;

        private void ChangeNoteKind(Note note, NoteKind kind)
        {
            if (note.points.Count != 1 || note.kind == kind) return;
            PushUndo(coalesce: false, "種別変更");
            note.kind = kind;
            dirty = true;
        }

        /// <summary>単発ノーツの左右端 ±4px を掴んでいるか。-1=左端, 0=対象外, +1=右端。</summary>
        private static int EdgeGrabSign(SheetLayout L, Note note, Vector2 pos)
        {
            if (note.points.Count != 1) return 0;
            var wp = note.points[0];
            float x0 = L.NoteX(wp.layerF, wp.cellF, forceSky: false);
            float x1 = L.NoteX(wp.layerF, wp.cellF + wp.width, forceSky: false);
            float left = Mathf.Min(x0, x1), right = Mathf.Max(x0, x1);
            const float grab = 4f;
            if (Mathf.Abs(pos.x - left) <= grab) return -1;
            if (Mathf.Abs(pos.x - right) <= grab) return 1;
            return 0;
        }

        /// <summary>§5.2: 矩形選択は点(waypoint)単位で当たる。Slideは帯ではなく個々のwaypointだけが対象。</summary>
        private List<NoteRef> HitTestPointsInRect(SheetLayout L, Rect rect)
        {
            var result = new List<NoteRef>();
            foreach (var note in chart.notes)
            {
                bool forceSky = HasHeightVariation(note);
                for (int i = 0; i < note.points.Count; i++)
                {
                    var wp = note.points[i];
                    float y = L.TickToY(wp.tick);
                    float x0 = L.NoteX(wp.layerF, wp.cellF, forceSky);
                    float x1 = L.NoteX(wp.layerF, wp.cellF + wp.width, forceSky);
                    var wpRect = Rect.MinMaxRect(Mathf.Min(x0, x1), y - 4f, Mathf.Max(x0, x1), y + 4f);
                    if (rect.Overlaps(wpRect))
                        result.Add(new NoteRef(note, i));
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

            // §7.5 高さレーンでの layerF ドラッグ。tickは動かさない（時間軸の編集はシート本体の担当）。
            if (heightDragNote != null)
            {
                var wp = heightDragNote.points[heightDragPointIndex];
                float layer = L.XToLayer(pos.x);
                // 単発ノーツ(Tap/Ex Tap/Flick)はGround/Skyのどちらかにしか存在できないため0/1にスナップする。
                // Slideの中継点は§7.5どおり連続値を許す（層を跨ぐ高さカーブを作るのが本レーンの目的）。
                wp.layerF = heightDragNote.points.Count == 1 ? Mathf.Round(layer) : layer;
                heightDragNote.points[heightDragPointIndex] = wp;
                dirty = true;
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

            if (!draggingNote || selection.Count == 0 || dragOriginByRef == null) return;

            int snapTicks = SnapTicks;
            int rawTick = L.YToTick(pos.y);
            // editor-ui-rework-r2.md §4: PaneAtはガター上で(0.5, Cells*0.5)という実在しない中間値を
            // 返すため、ドラッグ中にガターを通ると座標が飛ぶ。TryPaneAtで直前の有効値を保持する。
            var pane = L.TryPaneAt(pos.x);
            if (pane.HasValue) { dragLastValidLayer = pane.Value.layerF; dragLastValidCell = pane.Value.cellF; }

            int deltaTick = Mathf.RoundToInt((float)(rawTick - dragOriginRawTick) / snapTicks) * snapTicks;
            // 選択中にSlideの点が1つでもあれば0.5セル刻み、単発ノーツのみなら1セル刻み
            float cellStep = selection.Exists(r => r.note.kind == NoteKind.Slide) ? 0.5f : 1f;
            float rawDeltaCell = dragLastValidCell - dragOriginRawCell;
            // §7.4-B: ペインをまたいだら層(layerF)も更新する（従来はcellFの差分だけを見ており、
            // Ground⇔Skyへドラッグしても層が変わらないバグがあった）。
            float rawDeltaLayer = dragLastValidLayer - dragOriginRawLayer;

            // §4: スナップ＋盤面内クランプを点群全体に対して1回だけ適用する（ペーストと同じ規則）。
            float deltaCell = ResolveCellDelta(dragOriginByRef.Values, rawDeltaCell, cellStep);
            float deltaLayer = ResolveLayerDelta(dragOriginByRef.Values, rawDeltaLayer);

            // editor-ui-rework-mmw.md §5.2-3: 掴んだ「点」だけを動かす。ノーツ全体(他waypoint)は
            // 動かない — Slideの帯を一部分だけ調整できるようにするため。
            foreach (var r in selection)
            {
                if (!dragOriginByRef.TryGetValue(r, out var origin)) continue;
                var wp = origin;
                wp.tick = Mathf.Max(0, wp.tick + deltaTick);
                wp.cellF += deltaCell;
                wp.layerF += deltaLayer;
                r.note.points[r.index] = wp;
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
                    var hits = HitTestPointsInRect(L, rect);
                    if (rectAdditive)
                    {
                        foreach (var r in hits)
                            if (!selection.Contains(r)) selection.Add(r);
                        SyncSelectedNoteFromSelection();
                    }
                    else
                    {
                        SetMultiSelection(hits);
                    }
                }
                else if (!preview.IsPlaying)
                {
                    // §3: 空白のクリック(ドラッグ無し)＝再生位置カーソルの移動。参照元
                    // (ScoreEditor.cpp:565-584)と同じく再生中は動かさない。
                    int snapTicks = SnapTicks;
                    cursorTick = SnapTickTo(Mathf.Max(0, L.YToTick(rectStartPos.y)), snapTicks);
                }
                if (notesSheet.HasPointerCapture(evt.pointerId)) notesSheet.ReleasePointer(evt.pointerId);
                return;
            }

            if (heightDragNote != null)
            {
                // §6.3: 実質移動が無ければ「クリック」= easingH巡回。動いていればドラッグ確定。
                bool wasHeightClick = Vector2.Distance(heightDragStartScreenPos, (Vector2)evt.localPosition) < 3f;
                if (wasHeightClick && heightEasingCycleCandidate.HasValue)
                {
                    var r = heightEasingCycleCandidate.Value;
                    var wp = r.note.points[r.index];
                    wp.easingH = NextEasing(wp.easingH);
                    r.note.points[r.index] = wp;
                    dirty = true;
                }
                heightEasingCycleCandidate = null;
                heightDragNote = null;
                heightDragPointIndex = -1;
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

            // §5.3: 実質移動が無ければ「クリック」= easing巡回。動いていればドラッグ確定として扱う。
            bool wasClick = Vector2.Distance(dragStartScreenPos, (Vector2)evt.localPosition) < 3f;
            if (wasClick && easingCycleCandidate.HasValue)
            {
                var r = easingCycleCandidate.Value;
                var wp = r.note.points[r.index];
                wp.easing = NextEasing(wp.easing);
                r.note.points[r.index] = wp;
                dirty = true;
            }
            easingCycleCandidate = null;

            // §5.2-4: ドラッグ後は各ノーツのwaypointをtick順に並べ直す（始点/終点の入れ替わりや
            // 中継点の追い越しに対応。ドラッグ中は毎フレーム並べ替えるとindexがずれて壊れるため、
            // 確定後の1回だけ行う。参照元ScoreEditor.cpp:772-789の規則と同じ）。
            if (dragOriginByRef != null)
            {
                var touched = new HashSet<Note>();
                foreach (var r in dragOriginByRef.Keys) touched.Add(r.note);
                foreach (var note in touched) NormalizePointsOrder(note);
            }
            dragOriginByRef = null;
            if (notesSheet.HasPointerCapture(evt.pointerId)) notesSheet.ReleasePointer(evt.pointerId);
        }

        private static Easing NextEasing(Easing e)
        {
            var values = (Easing[])Enum.GetValues(typeof(Easing));
            int idx = Array.IndexOf(values, e);
            return values[(idx + 1) % values.Length];
        }

        /// <summary>ドラッグで始点/終点/中継点のtickが入れ替わった場合に備え、waypointをtick順へ
        /// 並べ直す。1点しかないノーツは対象外。</summary>
        private static void NormalizePointsOrder(Note note)
        {
            if (note.points.Count < 2) return;
            note.points.Sort((a, b) => a.tick.CompareTo(b.tick));
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
                // §1: 即挿入せずペーストモードに入る（カーソル追従→クリックで確定）。
                EnterPasteMode();
                evt.StopPropagation();
                return;
            }

            if (pasting && evt.keyCode == KeyCode.Escape)
            {
                CancelPaste();
                evt.StopPropagation();
                return;
            }

            // MikuMikuWorld移植候補: ↑↓キーでのカーソル移動＋自動スクロール
            // （ScoreEditor.cpp:292-304のnextTick/previousTick相当）。停止中のみ（再生中は
            // preview.SongTimeが真の値のため動かさない）。
            if (!pasting && !preview.IsPlaying && (evt.keyCode == KeyCode.UpArrow || evt.keyCode == KeyCode.DownArrow))
            {
                int snapTicks = SnapTicks;
                int baseTick = SnapTickTo(cursorTick, snapTicks);
                cursorTick = evt.keyCode == KeyCode.UpArrow ? baseTick + snapTicks : Mathf.Max(0, baseTick - snapTicks);
                EnsureCursorVisible();
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

        /// <summary>
        /// §5.2-2: 始点/終点(または単発ノーツ)を選んでいれば、そのノーツ全体を削除する。
        /// 中継点を選んでいれば、その点だけを削除する（参照元Editing.cpp:209-251の規則を踏襲）。
        /// </summary>
        private void DeleteSelection()
        {
            if (selection.Count == 0) return;
            PushUndo(coalesce: false, selection.Count > 1 ? "複数削除" : "ノーツ削除");

            var notesToRemove = new HashSet<Note>();
            var pointsByNote = new Dictionary<Note, List<int>>();
            foreach (var r in selection)
            {
                int last = r.note.points.Count - 1;
                if (r.note.points.Count == 1 || r.index == 0 || r.index == last)
                {
                    notesToRemove.Add(r.note);
                }
                else
                {
                    if (!pointsByNote.TryGetValue(r.note, out var list)) pointsByNote[r.note] = list = new List<int>();
                    list.Add(r.index);
                }
            }

            foreach (var note in notesToRemove) chart.notes.Remove(note);

            foreach (var kv in pointsByNote)
            {
                var note = kv.Key;
                if (notesToRemove.Contains(note)) continue; // 既にノーツごと削除済み
                var indices = kv.Value;
                indices.Sort();
                for (int i = indices.Count - 1; i >= 0; i--)
                {
                    int idx = indices[i];
                    if (idx > 0 && idx < note.points.Count - 1) note.points.RemoveAt(idx);
                }
            }

            ClearSelection();
            dirty = true;
        }

        /// <summary>選択された「点」ではなく、それが属する重複無しのノーツ全体を複製する
        /// （参照元Editing.cpp:31-59: holdの一部だけ選んでいても全体をコピーする挙動と同じ）。
        /// tickは最も早いノーツの開始tickを0とする相対値に正規化する（参照元Editing.cpp:61-63と同じ。
        /// 貼り付け時にホバー位置のtickへそのまま吸い付けられるようにするため）。
        /// コピー(CopySelectionToClipboard)とプリセット保存(SavePreset)の両方が使う共通ロジック。</summary>
        private List<Note> NormalizedClonesOfSelection()
        {
            var result = new List<Note>();
            var notes = new List<Note>(SelectedNotesDistinct());
            if (notes.Count == 0) return result;

            int minTick = int.MaxValue;
            foreach (var n in notes) minTick = Mathf.Min(minTick, n.points[0].tick);

            foreach (var n in notes)
            {
                var clone = CloneNote(n);
                for (int i = 0; i < clone.points.Count; i++)
                {
                    var wp = clone.points[i];
                    wp.tick -= minTick;
                    clone.points[i] = wp;
                }
                result.Add(clone);
            }
            return result;
        }

        private void CopySelectionToClipboard()
        {
            clipboard.Clear();
            clipboard.AddRange(NormalizedClonesOfSelection());
            statusMessage = $"{clipboard.Count}件コピーしました";
        }

        /// <summary>MikuMikuWorld移植候補: パターンプリセット。選択をtick=0正規化して名前付きで保存する。
        /// ディスクへは保存しない（アプリ実行中のみ、次回増分候補）。</summary>
        private void SavePreset(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                statusMessage = "プリセット名を入力してください";
                return;
            }
            var notes = NormalizedClonesOfSelection();
            if (notes.Count == 0)
            {
                statusMessage = "保存する選択がありません";
                return;
            }
            presets.RemoveAll(p => p.name == name);
            presets.Add(new NotePreset { name = name, notes = notes });
            statusMessage = $"プリセット「{name}」を保存しました";
        }

        /// <summary>プリセットの内容をクリップボードへ複製し、通常の貼り付けモード（§1）に入る。</summary>
        private void PastePreset(NotePreset preset)
        {
            clipboard.Clear();
            foreach (var n in preset.notes) clipboard.Add(CloneNote(n));
            EnterPasteMode();
        }

        private void DeletePreset(NotePreset preset) => presets.Remove(preset);

        /// <summary>
        /// §1: 貼り付けはカーソル追従モード。Cmd/Ctrl+Vの時点では挿入せず、以後ゴーストが
        /// マウスに追従し、クリックで確定する（参照元MikuMikuWorldのpaste/previewPaste/confirmPasteと
        /// 同じ設計、Editing.cpp:80-176）。
        /// tickはクリップボードが既に0正規化済みなので、確定時のホバーtickへそのまま足すだけで
        /// 先頭ノーツがカーソルに吸い付く。cellF/layerFは「Vを押した瞬間のホバー位置」との差分を
        /// 各ノーツの元の値に足す方式にしており、押した直後（マウスを動かす前）は元の横位置・高さの
        /// まま出る（参照元のpasteLane方式と同じ非対称: tickは絶対位置に吸着、cellF/layerFは相対移動）。
        /// </summary>
        private bool pasting;
        private bool pasteFlip;
        private float pasteAnchorCell;
        private float pasteAnchorLayer;
        // editor-ui-rework-r2.md §4: ガター上ではTryPaneAtがnullを返すため、直前の有効なペイン位置を
        // 保持しておく（保持しないとカーソルがガターを通った瞬間にゴーストが盤面中央へ飛ぶ）。
        private float pasteLastValidCell;
        private float pasteLastValidLayer;

        private void EnterPasteMode(bool flip = false)
        {
            if (clipboard.Count == 0 || pasting) return;
            if (!sheetHoverPos.HasValue)
            {
                statusMessage = "貼り付け先にマウスを合わせてから再度お試しください";
                return;
            }
            var L = CurrentSheetLayout();
            var (layerF, cellF) = L.PaneAt(sheetHoverPos.Value.x);
            pasteAnchorLayer = layerF;
            pasteAnchorCell = cellF;
            pasteLastValidLayer = layerF;
            pasteLastValidCell = cellF;
            pasting = true;
            pasteFlip = flip;
            statusMessage = "貼り付け先をクリックして確定（Escでキャンセル）";
        }

        /// <summary>editor-ui-rework-r2.md §4: ConfirmPasteとDrawPasteGhostで同じ変換を使うための
        /// 共通ヘルパー（両者がずれると「ゴーストの位置でクリックしたのに違う場所に貼られた」になる）。
        /// スナップ＋盤面内クランプを cellF/layerF それぞれ点群全体に対して1回だけ適用する。</summary>
        private (int hoverTick, float deltaCell, float deltaLayer) ComputePasteTransform(SheetLayout L)
        {
            var pos = sheetHoverPos.Value;
            int snapTicks = SnapTicks;
            int hoverTick = SnapTickTo(Mathf.Max(0, L.YToTick(pos.y)), snapTicks);

            var pane = L.TryPaneAt(pos.x);
            if (pane.HasValue) { pasteLastValidLayer = pane.Value.layerF; pasteLastValidCell = pane.Value.cellF; }

            float rawDeltaCell = pasteLastValidCell - pasteAnchorCell;
            float rawDeltaLayer = pasteLastValidLayer - pasteAnchorLayer;

            var allPts = new List<Waypoint>();
            foreach (var n in clipboard) allPts.AddRange(n.points);
            float cellStep = clipboard.Exists(n => n.kind == NoteKind.Slide) ? 0.5f : 1f;
            float deltaCell = ResolveCellDelta(allPts, rawDeltaCell, cellStep);
            float deltaLayer = ResolveLayerDelta(allPts, rawDeltaLayer);

            return (hoverTick, deltaCell, deltaLayer);
        }

        /// <summary>cellFを盤面中央で鏡像反転する。反転貼り付け(pasteFlip)と選択の反転(FlipSelected)で共用。</summary>
        private static float FlipCellF(float cellF, float width) => Cells - cellF - width;

        /// <summary>MikuMikuWorld移植候補: 選択の左右反転（Editing.cpp:503-526のflip相当）。
        /// layerF(Ground/Sky)やtickは変えず、cellFだけを盤面中央で鏡像反転する。</summary>
        private void FlipSelected()
        {
            var notes = new List<Note>(SelectedNotesDistinct());
            if (notes.Count == 0) return;
            PushUndo(coalesce: false, "反転");
            foreach (var note in notes)
            {
                for (int i = 0; i < note.points.Count; i++)
                {
                    var wp = note.points[i];
                    wp.cellF = FlipCellF(wp.cellF, wp.width);
                    note.points[i] = wp;
                }
            }
            dirty = true;
        }

        private void CancelPaste()
        {
            pasting = false;
            statusMessage = "貼り付けをキャンセルしました";
        }

        private void ConfirmPaste()
        {
            if (!sheetHoverPos.HasValue) return;
            var L = CurrentSheetLayout();
            var (hoverTick, deltaCell, deltaLayer) = ComputePasteTransform(L);

            PushUndo(coalesce: false, pasteFlip ? "反転貼り付け" : "貼り付け");
            var pasted = new List<Note>();
            foreach (var src in clipboard)
            {
                var n = CloneNote(src);
                for (int i = 0; i < n.points.Count; i++)
                {
                    var wp = n.points[i];
                    wp.tick = Mathf.Max(0, wp.tick + hoverTick);
                    wp.cellF += deltaCell;
                    wp.layerF += deltaLayer;
                    if (pasteFlip) wp.cellF = FlipCellF(wp.cellF, wp.width);
                    n.points[i] = wp;
                }
                chart.notes.Add(n);
                pasted.Add(n);
            }
            SetMultiSelection(AllPointRefsForNotes(pasted));
            dirty = true;
            pasting = false;
            statusMessage = $"{pasted.Count}件貼り付けました";
        }

        /// <summary>ペーストモード中のゴースト描画。ConfirmPasteと同じ変換式を使う
        /// （§7のDrawPlacementGhost同様、実際に確定する位置とゴーストを一致させるため）。</summary>
        private void DrawPasteGhost(Painter2D p, SheetLayout L)
        {
            var (hoverTick, deltaCell, deltaLayer) = ComputePasteTransform(L);

            foreach (var src in clipboard)
            {
                var col = NoteColor(src.kind);
                var pts = new List<Waypoint>(src.points.Count);
                foreach (var srcWp in src.points)
                {
                    var wp = srcWp;
                    wp.tick = Mathf.Max(0, wp.tick + hoverTick);
                    wp.cellF += deltaCell;
                    wp.layerF += deltaLayer;
                    if (pasteFlip) wp.cellF = FlipCellF(wp.cellF, wp.width);
                    pts.Add(wp);
                }

                if (pts.Count == 1)
                {
                    DrawGhostPoint(p, L, pts[0].tick, pts[0].layerF, pts[0].cellF, pts[0].width, col);
                    continue;
                }

                var ghostNote = new Note { kind = src.kind, points = pts };
                bool forceSky = HasHeightVariation(ghostNote);
                DrawGhostPoint(p, L, pts[0].tick, pts[0].layerF, pts[0].cellF, pts[0].width, col, forceSky);
                DrawGhostPoint(p, L, pts[^1].tick, pts[^1].layerF, pts[^1].cellF, pts[^1].width, col, forceSky);

                int nStart = pts[0].tick, nEnd = pts[^1].tick;
                int stepTicks = Mathf.Max(1, Mathf.RoundToInt(8f / L.pxPerTick));
                Vector2? prev = null;
                for (int t = nStart; ; t += stepTicks)
                {
                    int tc = Mathf.Min(t, nEnd);
                    var s = InterpAtTick(ghostNote, tc);
                    var cur = new Vector2(L.NoteX(s.layerF, s.cellF + s.width * 0.5f, forceSky), L.TickToY(tc));
                    if (prev.HasValue) FillLine(p, prev.Value, cur, new Color(col.r, col.g, col.b, 0.35f), 3f);
                    prev = cur;
                    if (tc == nEnd) break;
                }
            }
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
                PushUndo(coalesce: false, "ソフランイベント追加");
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
                    PushUndo(coalesce: false, "ソフランイベント削除");
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

        /// <summary>editor-ui-rework-r2.md §4: 点群をcellF方向へrawDeltaだけ動かすときの、
        /// スナップ済みかつ盤面(0〜Cells)に収まる実効deltaを返す。クランプは点群全体で1回だけ
        /// 行う（点ごとにクランプすると相対位置が崩れて形が壊れる）。ペーストとドラッグ移動の
        /// 両方から呼ぶ共通ヘルパー。</summary>
        private static float ResolveCellDelta(IEnumerable<Waypoint> pts, float rawDelta, float step)
        {
            float d = SnapCellTo(rawDelta, step);
            float minCell = float.MaxValue, maxEdge = float.MinValue;
            foreach (var w in pts)
            {
                minCell = Mathf.Min(minCell, w.cellF);
                maxEdge = Mathf.Max(maxEdge, w.cellF + w.width);
            }
            if (minCell > maxEdge) return 0f; // 点群が空
            d = Mathf.Max(d, -minCell);
            d = Mathf.Min(d, Cells - maxEdge);
            return d;
        }

        /// <summary>ResolveCellDeltaのlayerF版（0〜1にクランプ、スナップは無し）。</summary>
        private static float ResolveLayerDelta(IEnumerable<Waypoint> pts, float rawDelta)
        {
            float minLayer = float.MaxValue, maxLayer = float.MinValue;
            foreach (var w in pts)
            {
                minLayer = Mathf.Min(minLayer, w.layerF);
                maxLayer = Mathf.Max(maxLayer, w.layerF);
            }
            if (minLayer > maxLayer) return 0f;
            float d = rawDelta;
            d = Mathf.Max(d, -minLayer);
            d = Mathf.Min(d, 1f - maxLayer);
            return d;
        }

        /// <summary>
        /// §5.2: 選択・ドラッグ・削除の対象は「点」のみ。Slideの帯そのものは対象外
        /// （editor-ui-rework-mmw.md §5.2。参照元(MikuMikuWorld)はhold始点/中継点/終点が独立した
        /// Noteなので、この設計が最初から自然に成り立っている）。
        /// </summary>
        private NoteRef? HitTestPoint(SheetLayout L, Vector2 mouse)
        {
            for (int idx = chart.notes.Count - 1; idx >= 0; idx--)
            {
                var n = chart.notes[idx];
                bool forceSky = HasHeightVariation(n);
                for (int i = 0; i < n.points.Count; i++)
                {
                    var wp = n.points[i];
                    float y = L.TickToY(wp.tick);
                    if (Mathf.Abs(mouse.y - y) > 6f) continue;
                    float x0 = L.NoteX(wp.layerF, wp.cellF, forceSky);
                    float x1 = L.NoteX(wp.layerF, wp.cellF + wp.width, forceSky);
                    if (mouse.x >= Mathf.Min(x0, x1) - 3 && mouse.x <= Mathf.Max(x0, x1) + 3)
                        return new NoteRef(n, i);
                }
            }
            return null;
        }

        /// <summary>
        /// Slideの帯（waypoint間の補間経路）へのヒットテスト。§5.2により選択やドラッグの対象には
        /// ならないが、「帯をクリックして中継点を挿入する」機能（右クリックメニュー）だけはこれを使う
        /// （参照元のfindClosestHold相当、ScoreEditor.cpp:794-832。帯のヒットテストが生き残るのは
        /// ここだけ、という位置づけ）。
        /// </summary>
        private Note HitTestSlideBand(SheetLayout L, Vector2 mouse)
        {
            for (int idx = chart.notes.Count - 1; idx >= 0; idx--)
            {
                var n = chart.notes[idx];
                if (n.points.Count < 2) continue;
                bool forceSky = HasHeightVariation(n);
                int tick = L.YToTick(mouse.y);
                int nStart = n.points[0].tick, nEnd = n.points[^1].tick;
                if (tick < nStart - 4 || tick > nEnd + 4) continue;
                int clamped = Mathf.Clamp(tick, nStart, nEnd);
                var s = InterpAtTick(n, clamped);
                float x0 = L.NoteX(s.layerF, s.cellF, forceSky);
                float x1 = L.NoteX(s.layerF, s.cellF + s.width, forceSky);
                if (mouse.x >= Mathf.Min(x0, x1) - 4 && mouse.x <= Mathf.Max(x0, x1) + 4) return n;
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
            easingH = Easing.Linear,
            marker = WaypointMarker.None,
            comboStep = null,
        };

        /// <summary>ChartMath.At と同じ補間ロジックだが time(秒) ではなく tick を軸にする（エディタ描画専用）。
        /// §6: 横(cellF/width)はeasing、高さ(layerF)はeasingHで独立に補間する（ChartMath.Atと同じ規則）。</summary>
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
                    float eh = ChartMath.Ease(a.easingH, k);
                    return (
                        a.layerF + (b.layerF - a.layerF) * eh,
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

        // ---------- §4 layerFの濃淡表現 ----------

        /// <summary>
        /// editor-ui-rework-mmw.md §4: 「高さ情報を含むノーツ」＝waypoint間でlayerFが変化するSlide。
        /// これはSkyペインのみに記す（Groundには一切描かない）。単発ノーツ(points.Count==1)や
        /// layerFが一定のSlide(層をまたがない)はfalseになり、従来どおり自分の層のペインだけに描く。
        /// </summary>
        private static bool HasHeightVariation(Note note)
        {
            if (note.points.Count < 2) return false;
            float first = note.points[0].layerF;
            for (int i = 1; i < note.points.Count; i++)
                if (!Mathf.Approximately(note.points[i].layerF, first)) return true;
            return false;
        }

        // layerF=0(Ground寄り)でも完全透明にはしない、というユーザー指定の下限。
        private const float HeightAlphaFloor = 0.22f;

        /// <summary>高さ情報を持つSlideをSkyペインへ描くときの濃淡。layerF=1(Sky)で不透明、
        /// layerF=0でもHeightAlphaFloorまでしか下がらない。</summary>
        private static float HeightAlpha(float layerF) => Mathf.Lerp(HeightAlphaFloor, 1f, Mathf.Clamp01(layerF));
    }
}
