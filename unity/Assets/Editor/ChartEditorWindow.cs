using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Muses.Chart;

namespace Muses.Editor
{
    /// <summary>
    /// editor-spec.md §2,3。譜面エディタのノーツシート（主キャンバス）・ツールパレット・インスペクタ。
    /// 3Dプレビュー・波形+イベントレーン・検証結果リスト・Undo/自動保存は未実装（§5,4,6で別途着手）。
    /// 現時点でのスコープ: ファイルの読み書き、ノーツの配置/選択/平行移動/削除、
    /// Slideの中継点追加、インスペクタでの数値編集。矩形選択・コピペ・一括変換・端のドラッグでの
    /// 幅変更はまだ無い。
    /// </summary>
    public class ChartEditorWindow : EditorWindow
    {
        [MenuItem("Window/muses/Chart Editor")]
        public static void Open()
        {
            var win = GetWindow<ChartEditorWindow>("Chart Editor");
            win.Show();
        }

        private enum EditorTool { Select, Tap, ExTap, Slide, Flick, AddWaypoint, Delete }

        private const int Cells = 12;
        private static readonly int[] SnapDenominators = { 4, 8, 12, 16, 24, 32, 48, 64 };

        // ---- ファイル状態 ----
        private string chartPath;
        private string songPath;
        private SongMeta song = new();
        private ChartData chart = new();
        private ChartFileHeader header = new() { difficulty = "CUBE", level = 1, charter = "", songFile = "song.muses" };
        private bool dirty;

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

        private void OnGUI()
        {
            const float toolbarH = 22f;
            const float paletteW = 130f;
            const float inspectorW = 260f;

            var toolbarRect = new Rect(0, 0, position.width, toolbarH);
            var paletteRect = new Rect(0, toolbarH, paletteW, position.height - toolbarH);
            var inspectorRect = new Rect(position.width - inspectorW, toolbarH, inspectorW, position.height - toolbarH);
            var sheetRect = new Rect(paletteW, toolbarH, Mathf.Max(0, position.width - paletteW - inspectorW), position.height - toolbarH);

            DrawToolbar(toolbarRect);
            DrawPalette(paletteRect);
            DrawNotesSheet(sheetRect);
            DrawInspector(inspectorRect);
        }

        // ---------- ツールバー ----------

        private void DrawToolbar(Rect rect)
        {
            GUILayout.BeginArea(rect);
            GUILayout.BeginHorizontal(EditorStyles.toolbar);

            if (GUILayout.Button("開く", EditorStyles.toolbarButton, GUILayout.Width(50))) OpenChartDialog();
            if (GUILayout.Button("保存", EditorStyles.toolbarButton, GUILayout.Width(50))) SaveChart();
            if (GUILayout.Button("名前を付けて保存", EditorStyles.toolbarButton, GUILayout.Width(110))) SaveChartAs();

            GUILayout.Space(10);
            GUILayout.Label("snap", GUILayout.Width(30));
            snapIndex = EditorGUILayout.Popup(snapIndex, SnapDenominators.Select(d => $"1/{d}").ToArray(), EditorStyles.toolbarPopup, GUILayout.Width(60));

            GUILayout.Space(10);
            GUILayout.Label("幅", GUILayout.Width(16));
            defaultWidthCells = Mathf.Max(0.5f, EditorGUILayout.FloatField(defaultWidthCells, GUILayout.Width(40)));

            GUILayout.Space(10);
            GUILayout.Label("倍率", GUILayout.Width(26));
            pxPerBeat = EditorGUILayout.Slider(pxPerBeat, 8f, 240f, GUILayout.Width(100));

            GUILayout.FlexibleSpace();

            string posLabel = selectedNote != null
                ? SongAddr.FormatAddr(SongAddr.ToAddr(song.meters, selectedNote.points[0].tick))
                : "-";
            GUILayout.Label($"位置 {posLabel}", GUILayout.Width(140));

            GUI.enabled = false;
            GUILayout.Button("[検証]", EditorStyles.toolbarButton, GUILayout.Width(60)); // §4で実装予定
            GUI.enabled = true;

            GUILayout.Label(dirty ? "● 未保存" : "保存済み", GUILayout.Width(70));

            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        private void OpenChartDialog()
        {
            string path = EditorUtility.OpenFilePanel("譜面ファイルを開く (line/square/cube/tesseract.muses)", "", "muses");
            if (string.IsNullOrEmpty(path)) return;

            string dir = Path.GetDirectoryName(path);
            string songFilePath = Path.Combine(dir!, "song.muses");
            if (!File.Exists(songFilePath))
            {
                EditorUtility.DisplayDialog("エラー", $"同じフォルダに song.muses が見つかりません:\n{songFilePath}", "OK");
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
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("読み込みエラー", ex.Message, "OK");
            }
        }

        private void SaveChart()
        {
            if (string.IsNullOrEmpty(chartPath)) { SaveChartAs(); return; }
            try
            {
                ChartSerializer.WriteChart(chartPath, header, chart, song);
                dirty = false;
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("保存エラー", ex.Message, "OK");
            }
        }

        private void SaveChartAs()
        {
            string defaultName = string.IsNullOrEmpty(header.difficulty) ? "chart" : header.difficulty.ToLowerInvariant();
            string defaultDir = string.IsNullOrEmpty(chartPath) ? "" : Path.GetDirectoryName(chartPath);
            string path = EditorUtility.SaveFilePanel("譜面ファイルを保存", defaultDir, defaultName, "muses");
            if (string.IsNullOrEmpty(path)) return;
            chartPath = path;
            SaveChart();
        }

        // ---------- ツールパレット ----------

        private void DrawPalette(Rect rect)
        {
            GUILayout.BeginArea(rect, EditorStyles.helpBox);
            EditorGUILayout.LabelField("ツール", EditorStyles.boldLabel);
            DrawToolButton(EditorTool.Select, "選択");
            DrawToolButton(EditorTool.Tap, "Tap");
            DrawToolButton(EditorTool.ExTap, "Ex Tap");
            DrawToolButton(EditorTool.Slide, "Slide");
            DrawToolButton(EditorTool.Flick, "Flick");
            DrawToolButton(EditorTool.AddWaypoint, "Waypoint追加");
            DrawToolButton(EditorTool.Delete, "削除");

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("譜面情報", EditorStyles.boldLabel);
            header.difficulty = EditorGUILayout.TextField(header.difficulty ?? "");
            header.level = EditorGUILayout.IntField(header.level);
            header.charter = EditorGUILayout.TextField(header.charter ?? "");

            GUILayout.EndArea();
        }

        private void DrawToolButton(EditorTool tool, string label)
        {
            bool selected = currentTool == tool;
            GUI.backgroundColor = selected ? new Color(0.5f, 0.7f, 1f) : Color.white;
            if (GUILayout.Button(label))
            {
                currentTool = tool;
                pendingSlideStart = null;
            }
            GUI.backgroundColor = Color.white;
        }

        // ---------- インスペクタ ----------

        private void DrawInspector(Rect rect)
        {
            GUILayout.BeginArea(rect, EditorStyles.helpBox);
            EditorGUILayout.LabelField("インスペクタ", EditorStyles.boldLabel);

            if (selectedNote == null)
            {
                EditorGUILayout.HelpBox("ノーツを選択してください", MessageType.Info);
                GUILayout.EndArea();
                return;
            }

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.LabelField("kind", selectedNote.kind.ToString());
            int newGroup = EditorGUILayout.IntField("scrollGroup", selectedNote.scrollGroup);
            if (EditorGUI.EndChangeCheck())
            {
                selectedNote.scrollGroup = Mathf.Max(0, newGroup);
                dirty = true;
            }

            for (int i = 0; i < selectedNote.points.Count; i++)
            {
                EditorGUILayout.Space();
                string role = i == 0 ? " (始点)" : i == selectedNote.points.Count - 1 ? " (終点)" : "";
                EditorGUILayout.LabelField($"Waypoint {i}{role}", EditorStyles.boldLabel);

                var wp = selectedNote.points[i];
                EditorGUI.BeginChangeCheck();
                var addr = SongAddr.ToAddr(song.meters, wp.tick);
                string addrStr = EditorGUILayout.TextField("addr", SongAddr.FormatAddr(addr));
                float layerF = EditorGUILayout.Slider("layerF", wp.layerF, 0f, 1f);
                float cellF = EditorGUILayout.FloatField("cellF", wp.cellF);
                float width = EditorGUILayout.FloatField("width", wp.width);
                var easing = (Easing)EditorGUILayout.EnumPopup("easing", wp.easing);
                var marker = (WaypointMarker)EditorGUILayout.EnumPopup("marker", wp.marker);
                bool hasCombo = EditorGUILayout.Toggle("comboStep上書き", wp.comboStep.HasValue);
                int comboVal = wp.comboStep ?? 0;
                if (hasCombo) comboVal = EditorGUILayout.IntField("comboStep(tick)", comboVal);

                if (EditorGUI.EndChangeCheck())
                {
                    wp.layerF = Mathf.Clamp01(layerF);
                    wp.cellF = cellF;
                    wp.width = Mathf.Max(0.1f, width);
                    wp.easing = easing;
                    wp.marker = marker;
                    wp.comboStep = hasCombo ? comboVal : null;
                    try
                    {
                        var parsed = SongAddr.ParseAddr(addrStr);
                        wp.tick = SongAddr.ToTick(song.meters, parsed.bar, parsed.beat, parsed.tick);
                    }
                    catch (FormatException)
                    {
                        // 無効なaddr文字列はtickの変更を無視する（他フィールドの編集は反映する）
                    }
                    selectedNote.points[i] = wp;
                    dirty = true;
                }

                if (selectedNote.kind == NoteKind.Slide && selectedNote.points.Count > 2)
                {
                    if (GUILayout.Button("この中継点を削除"))
                    {
                        selectedNote.points.RemoveAt(i);
                        dirty = true;
                        GUIUtility.ExitGUI();
                    }
                }
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("このノーツを削除"))
            {
                chart.notes.Remove(selectedNote);
                selectedNote = null;
                dirty = true;
                GUIUtility.ExitGUI();
            }

            GUILayout.EndArea();
        }

        // ---------- ノーツシート（主キャンバス） ----------

        private void DrawNotesSheet(Rect rect)
        {
            EditorGUI.DrawRect(rect, new Color(0.16f, 0.16f, 0.16f));
            if (rect.width <= 1f || rect.height <= 1f) return;

            const float gutterW = 26f;
            float paneW = (rect.width - gutterW) * 0.5f;
            var groundRect = new Rect(rect.x, rect.y, paneW, rect.height);
            var gutterRect = new Rect(groundRect.xMax, rect.y, gutterW, rect.height);
            var skyRect = new Rect(gutterRect.xMax, rect.y, paneW, rect.height);

            EditorGUI.DrawRect(gutterRect, new Color(0.1f, 0.1f, 0.1f));
            GUI.Label(new Rect(groundRect.x, rect.y, groundRect.width, 16), "Ground", EditorStyles.centeredGreyMiniLabel);
            GUI.Label(new Rect(skyRect.x, rect.y, skyRect.width, 16), "Sky", EditorStyles.centeredGreyMiniLabel);

            float pxPerTick = pxPerBeat / ChartData.TicksPerBeat;
            int visibleTicks = Mathf.CeilToInt(rect.height / pxPerTick);

            float TickToY(int tick) => rect.yMax - (tick - scrollTick) * pxPerTick;
            int YToTick(float y) => scrollTick + Mathf.RoundToInt((rect.yMax - y) / pxPerTick);
            float CellX(Rect pane, float cellF) => pane.x + cellF / Cells * pane.width;
            float CombinedX(float layerF, float cellF) => Mathf.Lerp(CellX(groundRect, cellF), CellX(skyRect, cellF), Mathf.Clamp01(layerF));

            // セル境界線
            for (int c = 0; c <= Cells; c++)
            {
                EditorGUI.DrawRect(new Rect(CellX(groundRect, c), rect.y, 1, rect.height), new Color(1, 1, 1, 0.08f));
                EditorGUI.DrawRect(new Rect(CellX(skyRect, c), rect.y, 1, rect.height), new Color(1, 1, 1, 0.08f));
            }

            // 小節/拍/スナップ線
            int snapTicks = Mathf.Max(1, SongAddr.TicksPerBeatUnit(SnapDenominators[snapIndex]));
            int lineStart = ((Mathf.Max(0, scrollTick - snapTicks)) / snapTicks) * snapTicks;
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

                EditorGUI.DrawRect(new Rect(rect.x, y, rect.width, thickness), c);
                if (addr.beat == 1 && addr.tick == 0)
                    GUI.Label(new Rect(rect.x + 2, y - 14, 60, 14), addr.bar.ToString(), EditorStyles.whiteMiniLabel);
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
                    EditorGUI.DrawRect(Rect.MinMaxRect(Mathf.Min(x0, x1), y - 4, Mathf.Max(x0, x1), y + 4), col);
                }
                else
                {
                    int stepTicks = Mathf.Max(1, Mathf.RoundToInt(4f / pxPerTick));
                    for (int t = nStart; t < nEnd; t += stepTicks)
                    {
                        int t2 = Mathf.Min(t + stepTicks, nEnd);
                        float ya = TickToY(t), yb = TickToY(t2);
                        if (yb > rect.yMax + 8 || ya < rect.y - 8) { continue; }

                        var a = InterpAtTick(note, t);
                        float xa0 = CombinedX(a.layerF, a.cellF);
                        float xa1 = CombinedX(a.layerF, a.cellF + a.width);
                        EditorGUI.DrawRect(
                            Rect.MinMaxRect(Mathf.Min(xa0, xa1), Mathf.Min(ya, yb) - 1, Mathf.Max(xa0, xa1), Mathf.Max(ya, yb) + 1),
                            new Color(col.r, col.g, col.b, 0.55f));
                    }

                    foreach (var wp in note.points)
                    {
                        if (wp.marker != WaypointMarker.Visible) continue;
                        float y = TickToY(wp.tick);
                        float x = CombinedX(wp.layerF, wp.cellF);
                        EditorGUI.DrawRect(new Rect(x - 3, y - 3, 6, 6), Color.white);
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

            HandleSheetInput(rect, groundRect, skyRect, pxPerTick, TickToY, YToTick, CombinedX, snapTicks);
        }

        private void HandleSheetInput(
            Rect rect, Rect groundRect, Rect skyRect, float pxPerTick,
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
                Repaint();
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
                            draggingNote = true;
                            dragOriginRawTick = rawTick;
                            dragOriginRawCell = rawCell;
                            dragOriginPoints = new List<Waypoint>(hit.points);
                        }
                        break;
                    }
                }
                e.Use();
                Repaint();
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
                    wp.cellF = wp.cellF + deltaCell;
                    selectedNote.points[i] = wp;
                }
                dirty = true;
                e.Use();
                Repaint();
            }
            else if (e.type == EventType.MouseUp && e.button == 0)
            {
                draggingNote = false;
            }
            else if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace) && selectedNote != null)
            {
                chart.notes.Remove(selectedNote);
                selectedNote = null;
                dirty = true;
                e.Use();
                Repaint();
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

        private static void DrawRectOutline(Rect r, Color c)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, 2), c);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - 2, r.width, 2), c);
            EditorGUI.DrawRect(new Rect(r.x, r.y, 2, r.height), c);
            EditorGUI.DrawRect(new Rect(r.xMax - 2, r.y, 2, r.height), c);
        }
    }
}
