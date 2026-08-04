using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UIElements;

namespace Muses.ChartTool
{
    /// <summary>
    /// editor-ui-rework-r5.md §5。「エディタができること」の一覧(EditorCommand)と、
    /// キー入力からコマンドへのディスパッチ(OnGlobalKeyDown)。
    ///
    /// 旧実装はショートカットが2系統に分かれていた: (A) HandleUndoRedoShortcuts()の
    /// InputSystemポーリング(Undo/Redoのみ、フォーカスを見ない)、(B) notesSheet単体の
    /// KeyDownEvent(コピペ・Delete・↑↓、notesSheetにフォーカスが無いと届かない)。
    /// (A)はフォーカスを見ないため、右パネルのテキスト欄を編集中にCmd+Zを押すと
    /// テキストの取り消しではなく譜面のUndoが誤爆する不具合があった。
    /// 本ファイルは両方をuiRoot 1箇所のKeyDownEventへ統合し、フォーカス中の要素が
    /// テキスト入力かどうかで通す/奪うを切り分ける。
    /// </summary>
    public partial class ChartEditorApp
    {
        private readonly struct EditorCommand
        {
            public readonly string id;
            public readonly string category;
            public readonly string label;
            public readonly System.Action run;
            public readonly System.Func<bool> enabled;

            public EditorCommand(string id, string category, string label, System.Action run, System.Func<bool> enabled = null)
            {
                this.id = id;
                this.category = category;
                this.label = label;
                this.run = run;
                this.enabled = enabled;
            }
        }

        private List<EditorCommand> commands;

        /// <summary>入力欄編集中に奪うと編集操作と衝突するコマンド。§5.2(3)の「primary+C/V/X/A/Z」を
        /// undo/redo両方に対称に適用する（doc確定時は取り消し(Z)のみの言及だったが、やり直し
        /// (Shift+Z/Y)を除外すると同じ種類の誤爆がredo側に残ってしまうため、実装時に対称化した）。</summary>
        private static readonly HashSet<string> TextEditingConflictCommands = new()
        {
            CommandIds.EditUndo, CommandIds.EditRedo, CommandIds.EditCut,
            CommandIds.EditCopy, CommandIds.EditPaste, CommandIds.EditSelectAll,
        };

        private void BuildCommandTable()
        {
            commands = new List<EditorCommand>
            {
                new(CommandIds.FileNew, "ファイル", "新規", NewChart),
                new(CommandIds.FileOpen, "ファイル", "開く", () => ShowFileModal(saveMode: false)),
                new(CommandIds.FileSave, "ファイル", "保存", SaveChartToPath),
                new(CommandIds.FileSaveAs, "ファイル", "別名で保存", () => ShowFileModal(saveMode: true)),
                new(CommandIds.FileRestoreAutosave, "ファイル", "自動保存から復元", ShowRestoreModal,
                    () => !string.IsNullOrEmpty(restoreAutosavePath) && File.Exists(restoreAutosavePath)),

                new(CommandIds.EditUndo, "編集", "元に戻す", Undo, () => undoStack.Count > 0),
                new(CommandIds.EditRedo, "編集", "やり直す", Redo, () => redoStack.Count > 0),
                new(CommandIds.EditCut, "編集", "切り取り", () => { CopySelectionToClipboard(); DeleteSelection(); }, () => selection.Count > 0),
                new(CommandIds.EditCopy, "編集", "コピー", CopySelectionToClipboard, () => selection.Count > 0),
                new(CommandIds.EditPaste, "編集", "貼り付け", () => EnterPasteMode(), () => clipboard.Count > 0),
                new(CommandIds.EditPasteFlip, "編集", "反転して貼り付け", () => EnterPasteMode(flip: true), () => clipboard.Count > 0),
                new(CommandIds.EditPasteCancel, "編集", "貼り付け中止", CancelPaste, () => pasting),
                new(CommandIds.EditSelectAll, "編集", "すべて選択", SelectAllNotes, () => chart.notes.Count > 0),
                new(CommandIds.EditDelete, "編集", "削除", DeleteSelectionOrEvent,
                    () => selection.Count > 0 || selectedEventKind != EventKind.None),
                new(CommandIds.EditFlipSelected, "編集", "選択を反転", FlipSelected, () => selection.Count > 0),

                new(CommandIds.ToolSelect, "ツール", "選択", () => SelectTool(EditorTool.Select)),
                new(CommandIds.ToolTap, "ツール", "Tap", () => SelectTool(EditorTool.Tap)),
                new(CommandIds.ToolExTap, "ツール", "Ex Tap", () => SelectTool(EditorTool.ExTap)),
                new(CommandIds.ToolSlide, "ツール", "Slide", () => SelectTool(EditorTool.Slide)),
                new(CommandIds.ToolFlick, "ツール", "Flick", () => SelectTool(EditorTool.Flick)),
                new(CommandIds.ToolAddWaypoint, "ツール", "中継点", () => SelectTool(EditorTool.AddWaypoint)),
                new(CommandIds.ToolDelete, "ツール", "削除", () => SelectTool(EditorTool.Delete)),
                new(CommandIds.ToolEvent, "ツール", "イベント", () => SelectTool(EditorTool.Event)),

                new(CommandIds.PlayToggle, "再生", "再生・一時停止", TogglePlayFromCursor),
                new(CommandIds.PlayStop, "再生", "停止", StopAtCursor),
                new(CommandIds.PlayToStart, "再生", "先頭へ", GoToStart),
                new(CommandIds.PlayToEnd, "再生", "末尾へ", GoToEnd),

                new(CommandIds.CursorUp, "カーソル・表示", "カーソルを上へ", () => MoveCursorBySnap(+1),
                    () => !pasting && !preview.IsPlaying),
                new(CommandIds.CursorDown, "カーソル・表示", "カーソルを下へ", () => MoveCursorBySnap(-1),
                    () => !pasting && !preview.IsPlaying),
                new(CommandIds.ZoomIn, "カーソル・表示", "拡大", () => SetZoom(pxPerBeat + 8f)),
                new(CommandIds.ZoomOut, "カーソル・表示", "縮小", () => SetZoom(pxPerBeat - 8f)),
                new(CommandIds.SnapFiner, "カーソル・表示", "スナップを細かく",
                    () => snapIndex = Mathf.Min(snapIndex + 1, SnapDenominators.Length - 1)),
                new(CommandIds.SnapCoarser, "カーソル・表示", "スナップを粗く",
                    () => snapIndex = Mathf.Max(snapIndex - 1, 0)),

                new(CommandIds.NoteWidthGrow, "ノーツ", "幅を広げる", () => ChangeWidth(+1),
                    () => selection.Count > 0 || IsPlacementTool(currentTool)),
                new(CommandIds.NoteWidthShrink, "ノーツ", "幅を狭める", () => ChangeWidth(-1),
                    () => selection.Count > 0 || IsPlacementTool(currentTool)),
            };
        }

        private EditorCommand? FindCommand(string id)
        {
            if (commands == null) return null;
            foreach (var c in commands)
                if (c.id == id) return c;
            return null;
        }

        /// <summary>editor-ui-rework-r5.md §5.2: uiRootにTrickleDownで登録する唯一の入力口。
        /// keyBindingsを線形探索して一致するchordを持つコマンドを実行する。</summary>
        private void OnGlobalKeyDown(KeyDownEvent evt)
        {
            if (keyBindings == null || commands == null) return;
            // §5.4: ショートカット設定タブでキーをキャプチャ中は、既存の割り当て（例:
            // 数字キーのツール切替）が先に発火してキャプチャへイベントが届かなくなるのを防ぐため、
            // 通常のディスパッチを止めて素通りさせる（StopPropagationしない）。
            if (capturingCommandId != null) return;

            // §9: メニューが開いている間のEscapeは自前メニューを閉じる（他のEscape系コマンド
            // (貼り付け中止等)より優先）。
            if (openMenuIndex >= 0 && evt.keyCode == KeyCode.Escape)
            {
                CloseMenu();
                evt.StopPropagation();
                return;
            }

            // editor-ui-rework-r6.md §3.3(2): モーダル表示中(設定/ファイル参照/自動保存復元/
            // キー重複確認等)は譜面側のコマンドを一切発火させない。旧実装はこれが無く、
            // ファイル参照モーダルを開いた状態でSpaceを押すと再生が始まる、数字キーでツールが
            // 切り替わる、といった穴があった。
            // editor-ui-rework-r12.md §3.1: 判定はoverlayLayer.childCount>0からmodalDepthへ変更。
            // IME用オーバーレイ(ImeBridge)がoverlayLayerへ常駐の子要素を追加していたため、
            // 旧判定は起動直後から常に真になりショートカット全般が死んでいた
            // (IME側は別層ime-layerへ分離済みだが、判定自体も要素の有無に依存させないほうが
            // 同種の事故を再発させない)。
            if (modalDepth > 0) return;

            bool primary = evt.commandKey || evt.ctrlKey;
            bool textFocused = IsTextInputFocused();

            foreach (var binding in keyBindings)
            {
                KeyChord? matched = null;
                foreach (var chord in binding.chords)
                {
                    if (chord.key != evt.keyCode) continue;
                    if (chord.primary != primary) continue;
                    if (chord.shift != evt.shiftKey) continue;
                    if (chord.alt != evt.altKey) continue;
                    matched = chord;
                    break;
                }
                if (!matched.HasValue) continue;

                if (textFocused)
                {
                    // §5.2(3): 修飾キー無しの単キー(1〜8/Space/Delete等)と、入力欄自身の編集操作と
                    // 衝突するコマンド(コピペ・取り消し・全選択)は、入力欄へ委ねて何もしない。
                    var chord = matched.Value;
                    bool noModifiers = !chord.primary && !chord.shift && !chord.alt;
                    if (noModifiers || TextEditingConflictCommands.Contains(binding.commandId))
                        return;
                }

                var cmd = FindCommand(binding.commandId);
                if (!cmd.HasValue) return;
                if (cmd.Value.enabled != null && !cmd.Value.enabled()) return;

                cmd.Value.run();
                evt.StopPropagation();
                evt.PreventDefault();
                return;
            }
        }

        /// <summary>フォーカス中の要素（またはその祖先）がテキスト系入力欄かどうか。</summary>
        private bool IsTextInputFocused()
        {
            var el = uiRoot?.panel?.focusController?.focusedElement as VisualElement;
            while (el != null)
            {
                if (el is TextField or IntegerField or FloatField) return true;
                el = el.parent;
            }
            return false;
        }
    }
}
