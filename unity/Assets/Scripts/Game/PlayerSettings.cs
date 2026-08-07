using System;
using System.IO;
using UnityEngine;

namespace Muses.Game
{
    /// <summary>
    /// song-play-flow-r1.md §6。ゲーム本体のプレイヤー設定。
    /// エディタ側の EditorSettings（ChartEditorApp/EditorSettings.cs）とは別物で混ぜない。
    /// 曲・難易度の選択もここに持つ（v1では曲選択画面を作らない代わりに、
    /// ここのドロップダウンで選ぶ、§5.2）。
    /// </summary>
    [Serializable]
    public class PlayerSettings
    {
        public int version = 1;

        // ---- 曲・難易度(§5.2) ----
        public string songId = "";
        public string difficulty = "";

        // ---- オフセット(§6.2)。実機キャリブレーション用の最重要項目 ----
        public float judgeOffsetMs;
        public float visualOffsetMs;

        // ---- 音量(§6.2) ----
        public float masterVolume = 1f;
        public float bgmVolume = 1f;
        public float seVolume = 1f;

        // ---- ハイスピード・ノーツ厚み(§6.1: 毎フレームuniformに渡すだけなのでライブ調整可) ----
        public float hiSpeed = 1f;
        /// <summary>NoteView.ThicknessFrac に対応。既定値はシーンの調整値(NoteView.thicknessFrac)と同じ。</summary>
        public float noteThickness = 0.06f;

        // ---- その他 ----
        public bool metronome;

        // ---- デスクトップでの曲フォルダの親(§3.1)。空ならエディタと同じ既定値を使う ----
        public string songsRoot = "";
    }

    /// <summary>
    /// JSONファイルへの永続化。EditorSettingsStore(ChartEditorApp/EditorSettings.cs)と
    /// 同じパターン（欠けたフィールドは既定値のまま残る・macOS/iOSでPlayerPrefsより
    /// 中身が見える、の2点が理由。§6.4）。
    /// </summary>
    public static class PlayerSettingsStore
    {
        private const string FileName = "player-settings.json";
        private static string FilePath => Path.Combine(Application.persistentDataPath, FileName);

        private const string LegacyJudgeOffsetKey = "muses.judgeOffsetMs";
        private const string LegacyVisualOffsetKey = "muses.visualOffsetMs";

        public static PlayerSettings Load()
        {
            var settings = new PlayerSettings();
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    JsonUtility.FromJsonOverwrite(json, settings);
                }
                else
                {
                    MigrateFromPlayerPrefs(settings);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"PlayerSettings: 読み込みに失敗したため既定値を使います: {ex.Message}");
            }
            return settings;
        }

        /// <summary>旧 Stage/OffsetSettings.cs (PlayerPrefsベース)からの一度きりの移行。
        /// 値は judgeOffsetMs/visualOffsetMs の2つだけなので、JSONファイルが無いときに
        /// 読めれば引き継ぎ、以後は JSON が正になる。</summary>
        private static void MigrateFromPlayerPrefs(PlayerSettings settings)
        {
            if (PlayerPrefs.HasKey(LegacyJudgeOffsetKey))
                settings.judgeOffsetMs = PlayerPrefs.GetFloat(LegacyJudgeOffsetKey);
            if (PlayerPrefs.HasKey(LegacyVisualOffsetKey))
                settings.visualOffsetMs = PlayerPrefs.GetFloat(LegacyVisualOffsetKey);
        }

        public static void Save(PlayerSettings settings)
        {
            try
            {
                string json = JsonUtility.ToJson(settings, prettyPrint: true);
                File.WriteAllText(FilePath, json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"PlayerSettings: 保存に失敗しました: {ex.Message}");
            }
        }
    }
}
