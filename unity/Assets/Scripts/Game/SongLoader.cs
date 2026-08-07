using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Muses.Chart;

namespace Muses.Game
{
    /// <summary>song-play-flow-r1.md §3。曲プロジェクト(song.museproj + &lt;difficulty&gt;.muses + 音源)の
    /// 探索・読み込み。UnityEngine依存（Application.persistentDataPath等を読む）だが、
    /// パースそのものは UnityEngine非依存の Chart.ChartSerializer にすべて委譲する。</summary>
    public readonly struct SongDifficultyEntry
    {
        public readonly string difficulty;
        public readonly string chartPath;

        public SongDifficultyEntry(string difficulty, string chartPath)
        {
            this.difficulty = difficulty;
            this.chartPath = chartPath;
        }
    }

    public readonly struct SongEntry
    {
        /// <summary>フォルダ名をそのままIDとして使う（エディタの保存規約と同じ、editor-ui-rework-r7.md §3.2）。</summary>
        public readonly string songId;
        public readonly string dir;
        public readonly SongMeta meta;
        public readonly List<SongDifficultyEntry> difficulties;

        public SongEntry(string songId, string dir, SongMeta meta, List<SongDifficultyEntry> difficulties)
        {
            this.songId = songId;
            this.dir = dir;
            this.meta = meta;
            this.difficulties = difficulties;
        }
    }

    public static class SongLoader
    {
        /// <summary>探索する順（先に見つかった同名song-idを優先する）。
        /// 1. iOSで唯一の書き込み可能な実体で、Filesアプリからも見える(§3.1)。
        /// 2. デスクトップでの持ち込み先。エディタと同じ既定値(userOverrideRoot未指定時)。
        /// 3. ビルドに焼いた同梱曲（読み取り専用）。</summary>
        public static IEnumerable<string> SearchRoots(string userOverrideRoot = null)
        {
            yield return Path.Combine(Application.persistentDataPath, "songs");

            string desktopRoot = !string.IsNullOrEmpty(userOverrideRoot)
                ? userOverrideRoot
                : Muses.ChartTool.EditorSettings.DefaultSongsRoot();
            yield return desktopRoot;

            yield return Path.Combine(Application.streamingAssetsPath, "songs");
        }

        /// <summary>全探索パスを走査して曲リストを合成する。同じ song-id は最初に見つかったものが勝つ
        /// （SearchRootsの列挙順＝優先順位）。1曲でも見つけられなかった探索パスは黙ってスキップする
        /// （StreamingAssetsが存在しない・songsRootが未作成、はどちらも普通に起こるため）。</summary>
        public static List<SongEntry> Enumerate(string userOverrideRoot = null)
        {
            var result = new List<SongEntry>();
            var seenIds = new HashSet<string>();

            foreach (var root in SearchRoots(userOverrideRoot))
            {
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

                IEnumerable<string> dirs;
                try { dirs = Directory.GetDirectories(root).OrderBy(d => d); }
                catch { continue; }

                foreach (var dir in dirs)
                {
                    string songId = Path.GetFileName(dir);
                    if (!seenIds.Add(songId)) continue; // 優先順位の高い探索パスで既出

                    string songMetaPath = ResolveSongMetaPath(dir);
                    if (songMetaPath == null) continue; // song.museprojが無い＝曲フォルダではない

                    SongMeta meta;
                    try { meta = ChartSerializer.ReadSongMeta(songMetaPath); }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"SongLoader: {songMetaPath} の読み込みに失敗しました: {ex.Message}");
                        continue;
                    }

                    var difficulties = new List<SongDifficultyEntry>();
                    try
                    {
                        foreach (var chartPath in Directory.GetFiles(dir, "*" + ChartSerializer.ChartExt).OrderBy(f => f))
                            difficulties.Add(new SongDifficultyEntry(Path.GetFileNameWithoutExtension(chartPath), chartPath));
                    }
                    catch { /* 譜面ファイルが1つも読めなくても曲メタだけは一覧に出す */ }

                    result.Add(new SongEntry(songId, dir, meta, difficulties));
                }
            }

            return result;
        }

        /// <summary>editor-ui-rework-r9.md §4: 曲メタのファイル名は song.museproj が正、
        /// song.muses は旧ファイル名からのフォールバック（ChartSerializer.LegacySongFileNameと同じ規約）。</summary>
        private static string ResolveSongMetaPath(string dir)
        {
            string current = Path.Combine(dir, ChartSerializer.SongFileName);
            if (File.Exists(current)) return current;
            string legacy = Path.Combine(dir, ChartSerializer.LegacySongFileName);
            if (File.Exists(legacy)) return legacy;
            return null;
        }

        /// <summary>1曲・1難易度ぶんの譜面を読み込む。ChartSerializer.ReadChart が
        /// tick→秒解決・combo point解決まで済ませた状態で返す。</summary>
        public static (SongMeta song, ChartData chart) Load(SongEntry entry, string difficulty)
        {
            var target = entry.difficulties.FirstOrDefault(d => d.difficulty == difficulty);
            if (target.chartPath == null)
                throw new FileNotFoundException($"SongLoader: 難易度 '{difficulty}' が見つかりません（{entry.dir}）");

            var (_, chart) = ChartSerializer.ReadChart(target.chartPath, entry.meta);
            return (entry.meta, chart);
        }

        /// <summary>音源ファイルのフルパス。meta.audio が空なら null（音源無し譜面）。</summary>
        public static string ResolveAudioPath(SongEntry entry) =>
            string.IsNullOrEmpty(entry.meta.audio) ? null : Path.Combine(entry.dir, entry.meta.audio);
    }
}
