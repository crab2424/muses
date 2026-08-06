using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Muses.ChartTool
{
    /// <summary>
    /// editor-ui-rework-r5.md §5.2(1)。エディタが提供する操作の一覧。文字列IDにしているのは、
    /// enumだと項目を挿入するたびに既存のキーバインド設定ファイルの割り当てがずれるため
    /// （JsonUtilityがenumをintとしてシリアライズするので、順序が意味を持ってしまう）。
    /// </summary>
    public static class CommandIds
    {
        public const string FileNew = "file.new";
        public const string FileOpen = "file.open";
        public const string FileSave = "file.save";
        public const string FileSaveAs = "file.saveAs";
        // editor-ui-rework-r12.md §2.3。
        public const string FileRestoreAutosave = "file.restoreAutosave";

        public const string EditUndo = "edit.undo";
        public const string EditRedo = "edit.redo";
        public const string EditCut = "edit.cut";
        public const string EditCopy = "edit.copy";
        public const string EditPaste = "edit.paste";
        public const string EditPasteFlip = "edit.pasteFlip";
        public const string EditPasteCancel = "edit.pasteCancel";
        public const string EditSelectAll = "edit.selectAll";
        public const string EditDelete = "edit.delete";
        public const string EditFlipSelected = "edit.flipSelected";

        public const string ToolSelect = "tool.select";
        public const string ToolTap = "tool.tap";
        public const string ToolExTap = "tool.extap";
        public const string ToolSlide = "tool.slide";
        public const string ToolFlick = "tool.flick";
        // riser-r2.md §4。
        public const string ToolLayerMove = "tool.layerMove";
        public const string ToolAddWaypoint = "tool.addWaypoint";
        public const string ToolDelete = "tool.delete";
        public const string ToolEvent = "tool.event";

        public const string PlayToggle = "play.toggle";
        public const string PlayStop = "play.stop";
        public const string PlayToStart = "play.toStart";
        public const string PlayToEnd = "play.toEnd";

        public const string CursorUp = "cursor.up";
        public const string CursorDown = "cursor.down";
        public const string ZoomIn = "zoom.in";
        public const string ZoomOut = "zoom.out";
        public const string SnapFiner = "snap.finer";
        public const string SnapCoarser = "snap.coarser";

        // editor-ui-rework-r6.md §1.5。
        public const string NoteWidthGrow = "note.widthGrow";
        public const string NoteWidthShrink = "note.widthShrink";
    }

    /// <summary>1つのキーの組み合わせ。primaryはmacOSではCmd・それ以外ではCtrlを指す
    /// （editor-ui-rework-r5.md §5.2(2): 既存コードが元々commandKey/ctrlKeyを同一視していたため、
    /// プラットフォームごとに別レコードを持たず1つに畳む）。</summary>
    [Serializable]
    public struct KeyChord
    {
        public KeyCode key;
        public bool primary;
        public bool shift;
        public bool alt;

        public KeyChord(KeyCode key, bool primary = false, bool shift = false, bool alt = false)
        {
            this.key = key;
            this.primary = primary;
            this.shift = shift;
            this.alt = alt;
        }

        public override string ToString()
        {
            string s = "";
            if (primary) s += Application.platform == RuntimePlatform.OSXPlayer || Application.platform == RuntimePlatform.OSXEditor ? "⌘" : "Ctrl+";
            if (alt) s += "Alt+";
            if (shift) s += "Shift+";
            s += KeyLabel(key);
            return s;
        }

        private static string KeyLabel(KeyCode k) => k switch
        {
            KeyCode.UpArrow => "↑",
            KeyCode.DownArrow => "↓",
            KeyCode.LeftArrow => "←",
            KeyCode.RightArrow => "→",
            KeyCode.Space => "Space",
            KeyCode.Delete => "Delete",
            KeyCode.Backspace => "Backspace",
            KeyCode.Escape => "Esc",
            KeyCode.Equals => "+",
            KeyCode.Minus => "-",
            KeyCode.LeftBracket => "[",
            KeyCode.RightBracket => "]",
            _ => k.ToString(),
        };
    }

    /// <summary>1コマンドに対する割り当て。複数のchordを持てる（ユーザー要望「複数登録できると良い」）。</summary>
    [Serializable]
    public class KeyBinding
    {
        public string commandId;
        public List<KeyChord> chords = new();

        public KeyBinding() { }
        public KeyBinding(string commandId, params KeyChord[] chords)
        {
            this.commandId = commandId;
            this.chords = new List<KeyChord>(chords);
        }
    }

    /// <summary>editor-ui-rework-r11.md §3。タイムライン倍率・スナップ・レーン表示・右パネルの
    /// 選択中タブなど、「設定画面で明示的に選ぶもの」ではなく「編集操作の結果として変わるもの」を
    /// まとめて持つ。UIから直接編集する項目が無いこと(コード側で書き換えるだけ)を前提にしている。</summary>
    [Serializable]
    public class WorkspaceState
    {
        public float pxPerBeat = 28f; // ChartEditorApp.ZoomBasePxPerBeatと同値
        public int snapIndex = 3; // 1/16 既定(ChartEditorApp.snapIndexの既定と同値)
        public bool showHeightLane = false;
        public bool showEventLane = true;
        public int rightTabIndex = 0;
    }

    /// <summary>editor-ui-rework-r12.md §2.2。「無視する」を押した自動保存ファイルを、
    /// 更新日時ではなく内容(contentHash)で記憶する。同じ内容の間は二度と案内せず、
    /// 新しい編集が自動保存されれば(=内容が変われば)また案内が出る。</summary>
    [Serializable]
    public class DismissedAutosave
    {
        public string autosavePath;
        public string contentHash;
    }

    /// <summary>editor-ui-rework-r12.md §2.3。自動保存の復元案内をどこまで出すか。</summary>
    public static class RestorePromptMode
    {
        public const int Always = 0;
        public const int WhenDifferent = 1;
        public const int Never = 2;
    }

    /// <summary>
    /// editor-ui-rework-r5.md §1。譜面エディタ(ChartEditorApp)の設定。ゲーム本体のプレイヤー設定
    /// （Stage/OffsetSettings.cs、judgeOffsetMs/visualOffsetMs）とは別物で混ぜない。
    /// </summary>
    [Serializable]
    public class EditorSettings
    {
        public int version = 1;

        // ---- 一般 ----
        public bool autosaveEnabled = true;
        public int autosaveMinutes = 5;
        /// <summary>0=VSync, 1=60fps, 2=120fps, 3=無制限。既定はVSync
        /// （[[muses-unity-port-progress]]の「Play中の発熱」の記録どおり、無制限は実害が出た実績がある）。</summary>
        public int frameRateMode = 0;
        /// <summary>PanelSettings.referenceResolutionを割る倍率。1.0で従来どおりの見た目。</summary>
        public float uiScale = 1f;
        /// <summary>editor-ui-rework-r13.md §7.5。fps計測表示。デバッグ用の一時的なトグルとして
        /// 導入した際は永続化せず既定ONだったが、常時表示は不要なため2026-08-06に永続化・既定OFFへ変更。</summary>
        public bool showPerfStats = false;
        /// <summary>editor-ui-rework-r12.md §1.3。ノーツ/イベント選択時に右パネルを
        /// インスペクタタブへ自動的に切り替えるか。「選ぶと消えたように見える」退行を防ぐための既定ON。</summary>
        public bool autoFocusInspector = true;
        /// <summary>editor-ui-rework-r12.md §2.3。RestorePromptMode.*。既定はWhenDifferent
        /// （内容が正規ファイルと同じ、または「無視済み」なら聞かない）。</summary>
        public int restorePromptMode = RestorePromptMode.WhenDifferent;
        /// <summary>editor-ui-rework-r12.md §2.4。前回セッションが正常終了したか。Awakeの最後にfalseへ
        /// 落として即保存し、OnDestroyでtrueに戻す。起動時にfalseのまま読めたらクラッシュ/強制終了とみなす。</summary>
        public bool cleanShutdown = true;
        /// <summary>editor-ui-rework-r12.md §2.2。「無視する」を押した自動保存の記録（内容一致で判定）。</summary>
        public List<DismissedAutosave> dismissedAutosaves = new();

        // ---- タイムライン ----
        public bool followPlayback = true;
        public bool pageScroll = false;
        public float judgeLineFrac = 1f;
        /// <summary>縦線分割数。12(Cells)の約数のみを許容する(1/2/3/4/6/12)。</summary>
        public int laneDivisions = 4;
        public bool invertScroll = false;
        public float laneWidthPx = 46f;
        /// <summary>editor-ui-rework-r8.md §5.2。プレビュー画面でのCmd/Ctrl+ホイールで変わる
        /// ノーツ速度倍率。譜面の属性ではなくエディタ側の設定(音量と同じ扱い)なのでsong.musesには入れない。</summary>
        public float hiSpeed = 1f;

        /// <summary>editor-ui-rework-r13.md §7.9。プレビューのノーツ奥行き厚み（NotePlacement.hlsl）。
        /// 既定値はユーザーが実機と見比べて確定した値（frac=0.06 / minFrac=0.01）。
        /// hiSpeedと同じくエディタ側の表示設定なので譜面ファイルには入れない。</summary>
        public float thicknessFrac = 0.06f;
        public float thicknessMinFrac = 0.01f;

        // ---- ショートカット ----
        public List<KeyBinding> keyBindings = new();

        // ---- 音源(editor-ui-rework-r6.md §4.3) ----
        // song.musesではなくこちらに持つ: 音量は譜面の属性ではなくエディタの設定なので、
        // 音量を変えただけで譜面ファイルがdirtyになるのは間違い。既定値は参照元
        // (MikuMikuWorld ScoreEditor.cpp:37-39)に合わせて0.8/1.0/1.0。
        public float masterVolume = 0.8f;
        public float bgmVolume = 1f;
        public float seVolume = 1f;

        // ---- ファイルダイアログ ----
        /// <summary>editor-ui-rework-r5.md §1 Q1: 従来PlayerPrefsだった最後に開いたフォルダも
        /// 設定ファイルへ統合する。</summary>
        public string browseDir = "";

        // ---- 作業状態(editor-ui-rework-r11.md §3) ----
        // 「設定」ではなく編集操作の副作用で変わる値。設定モーダルには一切出さない
        // （出す/出さないの境界は「モーダルで明示的に選ぶか」「操作の結果として変わるか」）。
        public WorkspaceState workspace = new();

        /// <summary>editor-ui-rework-r7.md §3.2。曲プロジェクト（song.muses・各難易度・音源・
        /// ジャケット等を1フォルダにまとめたもの、editor-spec.md §1.2の songs/&lt;song-id&gt;/ に相当）
        /// を置く親フォルダ。空文字なら DefaultSongsRoot() を使う。
        /// 従来はここが Application.persistentDataPath（macOSでは ~/Library/... 下、Finderの既定で
        /// 非表示）になっており「どこにあるか分からない」の直接の原因だった。</summary>
        public string songsRoot = "";

        /// <summary>Finderから素直に見える既定の置き場所。ユーザーがドキュメントフォルダ以下を
        /// 期待するのは自然な慣習なので、persistentDataPath（アプリ内部状態向け。editor-settings.json
        /// や自動保存はこのままそこに残す）とは意図的に分離する。
        /// editor-ui-rework-r9.md §2.1: Unix系(.NET/Mono)では SpecialFolder.MyDocuments が
        /// $HOME に縮退する（Documentsを指さない）ため、その場合だけ手で "Documents" を補う。
        /// Windowsではこの縮退は起きないため分岐なしでそのまま使う。</summary>
        public static string DefaultSongsRoot()
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(docs) || PathEquals(docs, home))
                docs = Path.Combine(home, "Documents");
            return Path.Combine(docs, "muses", "songs");
        }

        /// <summary>2つの絶対パスを、末尾区切り文字・大小の揺れを無視して比較する
        /// （editor-ui-rework-r9.md §2.1/§3.1で共通に使う）。</summary>
        public static bool PathEquals(string a, string b)
        {
            try
            {
                string na = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string nb = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(na, nb, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>editor-ui-rework-r5.md §5.3。参照元(MikuMikuWorld)の既定キー割り当てを土台に、
        /// muses の現状の割り当てを維持したもの。</summary>
        public static List<KeyBinding> DefaultKeyBindings() => new()
        {
            new KeyBinding(CommandIds.FileNew, new KeyChord(KeyCode.N, primary: true)),
            new KeyBinding(CommandIds.FileOpen, new KeyChord(KeyCode.O, primary: true)),
            new KeyBinding(CommandIds.FileSave, new KeyChord(KeyCode.S, primary: true)),
            new KeyBinding(CommandIds.FileSaveAs, new KeyChord(KeyCode.S, primary: true, shift: true)),

            new KeyBinding(CommandIds.EditUndo, new KeyChord(KeyCode.Z, primary: true)),
            new KeyBinding(CommandIds.EditRedo,
                new KeyChord(KeyCode.Z, primary: true, shift: true),
                new KeyChord(KeyCode.Y, primary: true)),
            new KeyBinding(CommandIds.EditCut, new KeyChord(KeyCode.X, primary: true)),
            new KeyBinding(CommandIds.EditCopy, new KeyChord(KeyCode.C, primary: true)),
            new KeyBinding(CommandIds.EditPaste, new KeyChord(KeyCode.V, primary: true)),
            new KeyBinding(CommandIds.EditPasteFlip, new KeyChord(KeyCode.V, primary: true, shift: true)),
            new KeyBinding(CommandIds.EditPasteCancel, new KeyChord(KeyCode.Escape)),
            new KeyBinding(CommandIds.EditSelectAll, new KeyChord(KeyCode.A, primary: true)),
            new KeyBinding(CommandIds.EditDelete,
                new KeyChord(KeyCode.Delete),
                new KeyChord(KeyCode.Backspace)),
            new KeyBinding(CommandIds.EditFlipSelected, new KeyChord(KeyCode.F, primary: true)),

            new KeyBinding(CommandIds.ToolSelect, new KeyChord(KeyCode.Alpha1)),
            new KeyBinding(CommandIds.ToolTap, new KeyChord(KeyCode.Alpha2)),
            new KeyBinding(CommandIds.ToolExTap, new KeyChord(KeyCode.Alpha3)),
            new KeyBinding(CommandIds.ToolSlide, new KeyChord(KeyCode.Alpha4)),
            new KeyBinding(CommandIds.ToolFlick, new KeyChord(KeyCode.Alpha5)),
            new KeyBinding(CommandIds.ToolAddWaypoint, new KeyChord(KeyCode.Alpha6)),
            new KeyBinding(CommandIds.ToolDelete, new KeyChord(KeyCode.Alpha7)),
            new KeyBinding(CommandIds.ToolEvent, new KeyChord(KeyCode.Alpha8)),
            // riser-r2.md §4。既存ツールの数字を詰め替えると混乱するため末尾のAlpha9に割り当てる。
            new KeyBinding(CommandIds.ToolLayerMove, new KeyChord(KeyCode.Alpha9)),

            new KeyBinding(CommandIds.PlayToggle, new KeyChord(KeyCode.Space)),
            // 参照元はBackspaceだが、muses ではEditDeleteと衝突するため変更する(editor-ui-rework-r5.md §5.3)。
            new KeyBinding(CommandIds.PlayStop, new KeyChord(KeyCode.Space, shift: true)),

            new KeyBinding(CommandIds.CursorUp, new KeyChord(KeyCode.UpArrow)),
            new KeyBinding(CommandIds.CursorDown, new KeyChord(KeyCode.DownArrow)),
            new KeyBinding(CommandIds.ZoomIn, new KeyChord(KeyCode.Equals, primary: true)),
            new KeyBinding(CommandIds.ZoomOut, new KeyChord(KeyCode.Minus, primary: true)),
            new KeyBinding(CommandIds.SnapFiner, new KeyChord(KeyCode.RightBracket)),
            new KeyBinding(CommandIds.SnapCoarser, new KeyChord(KeyCode.LeftBracket)),

            // editor-ui-rework-r6.md §1.5: 既定は拡大=←、縮小=→（ユーザー指定どおり）。
            new KeyBinding(CommandIds.NoteWidthGrow, new KeyChord(KeyCode.LeftArrow)),
            new KeyBinding(CommandIds.NoteWidthShrink, new KeyChord(KeyCode.RightArrow)),
        };
    }

    /// <summary>
    /// editor-ui-rework-r5.md §1.2。JSONファイルへの永続化。PlayerPrefsではなくファイルにする理由は
    /// 同ドキュメント参照（キーバインドが入れ子配列を持つ・中身を人が見て直せる・PlayerPrefsは
    /// macOSでは実質不可視、の3点）。
    /// </summary>
    public static class EditorSettingsStore
    {
        private const string FileName = "editor-settings.json";
        private static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

        /// <summary>欠けたフィールドは既定値のまま残す(JsonUtility.FromJsonOverwriteの挙動)。
        /// 参照元(ApplicationConfiguration::tryGetX)の「欠けたキーは既定値」と同じ狙い。</summary>
        public static EditorSettings Load()
        {
            var settings = new EditorSettings();
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    JsonUtility.FromJsonOverwrite(json, settings);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"EditorSettings: 読み込みに失敗したため既定値を使います: {ex.Message}");
            }

            settings.keyBindings ??= new List<KeyBinding>();
            settings.dismissedAutosaves ??= new List<DismissedAutosave>();
            // editor-ui-rework-r12.md §2.2: 参照先が既に無い(削除済み/移動済み)エントリは掃除する。
            // 20件のリング上限も併せて維持する(無限に育たないように)。
            settings.dismissedAutosaves.RemoveAll(d => string.IsNullOrEmpty(d.autosavePath) || !File.Exists(d.autosavePath));
            const int maxDismissed = 20;
            if (settings.dismissedAutosaves.Count > maxDismissed)
                settings.dismissedAutosaves.RemoveRange(0, settings.dismissedAutosaves.Count - maxDismissed);
            // editor-ui-rework-r6.md §1.5: JsonUtility.FromJsonOverwriteはkeyBindingsを丸ごと
            // 置き換えるため、後からコマンドを追加しても既存のeditor-settings.jsonには
            // その既定バインドが無く、そのままだと一切効かない（新コマンドが無音で死ぬ）。
            // 既存の割り当てを壊さず、まだ無いcommandIdの既定だけを補う。
            var existingIds = new HashSet<string>();
            foreach (var b in settings.keyBindings) existingIds.Add(b.commandId);
            foreach (var def in EditorSettings.DefaultKeyBindings())
                if (!existingIds.Contains(def.commandId))
                    settings.keyBindings.Add(def);

            return settings;
        }

        /// <summary>editor-ui-rework-r9.md §2.2。旧DefaultSongsRoot()（Unix系でHOMEに縮退していた
        /// バグの産物、$HOME/muses/songs）を指したまま保存されている設定ファイルを救済する。
        /// フォルダが空（＝実際には使われていない）のときだけ新しい既定値へ書き換える。
        /// 中身がある場合はユーザーが意図して置いた可能性があるため触らない。
        /// 呼び出し側(ChartEditorApp.Awake)がユーザーへ通知できるよう、書き換えたかを返す。</summary>
        public static bool RescueLegacySongsRoot(EditorSettings settings)
        {
            if (string.IsNullOrEmpty(settings.songsRoot)) return false;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string legacyDefault = Path.Combine(home, "muses", "songs");
            if (!EditorSettings.PathEquals(settings.songsRoot, legacyDefault)) return false;

            bool isEmpty = !Directory.Exists(settings.songsRoot)
                || (Directory.GetFiles(settings.songsRoot).Length == 0 && Directory.GetDirectories(settings.songsRoot).Length == 0);
            if (!isEmpty) return false;

            settings.songsRoot = EditorSettings.DefaultSongsRoot();
            return true;
        }

        public static void Save(EditorSettings settings)
        {
            try
            {
                string json = JsonUtility.ToJson(settings, prettyPrint: true);
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"EditorSettings: 保存に失敗しました: {ex.Message}");
            }
        }
    }
}
