using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace Muses.Audio
{
    /// <summary>song-play-flow-r1.md §3.2。音源ファイルの読み込み。元は
    /// ChartEditorApp/PreviewSystem.cs にあったロジックをゲーム側と共有できるよう
    /// UnityEngine非依存ではないが Editor非依存の形でここへ移した（挙動は不変）。</summary>
    public enum AudioLoadResult
    {
        NotFound,
        Unsupported,
        DecodeFailed,
        Ok,
    }

    public static class AudioFileLoader
    {
        /// <summary>Oggコンテナの先頭ページ内に"OpusHead"シグネチャがあるかどうか。
        /// 数十バイト読むだけなので同期I/Oで十分。</summary>
        public static bool LooksLikeOpus(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var buf = new byte[64];
                int read = fs.Read(buf, 0, buf.Length);
                string header = System.Text.Encoding.ASCII.GetString(buf, 0, read);
                return header.Contains("OpusHead");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>拡張子からUnity標準のAudioTypeを決める。ogg(Vorbis)以外にwav/mp3も
        /// そのまま扱える。それ以外は既定でOGGVORBISを試す。</summary>
        public static AudioType AudioTypeFromExtension(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".wav" => AudioType.WAV,
                ".mp3" => AudioType.MPEG,
                _ => AudioType.OGGVORBIS,
            };
        }

        /// <summary>
        /// 音源を読み込む。ファイルが無ければ即座に NotFound、Opusコンテナなら即座に
        /// Unsupported をコールバックへ渡す（コルーチンには入らない）。
        /// host はコルーチンの実行元(MonoBehaviour)。
        /// stream: trueなら圧縮のまま保持しストリーミング再生する（全長PCM展開を避けメモリを
        /// 大幅に節約できる、perf-r1.md §3）。ただしストリーミングクリップは同時に1つの
        /// AudioSourceからしか再生できず、シーク応答も悪化しうる。ゲーム本体はBGM1本のみで
        /// シークもほぼ発生しないため true、譜面エディタのプレビューはスクラブを多用するため
        /// false（既定）にすること。
        /// </summary>
        public static IEnumerator Load(MonoBehaviour host, string path, Action<AudioLoadResult, AudioClip, string> onDone, bool stream = false)
        {
            if (!File.Exists(path))
            {
                onDone(AudioLoadResult.NotFound, null, $"見つかりません: {path}");
                yield break;
            }

            // UnityはOgg Vorbisしかデコードできず、Opusコンテナ(近年のffmpegがoutput.oggに
            // 既定で選ぶ)は読めても無音になる/失敗するだけで原因が分からない。
            // ヘッダ先頭を読んで先に弾き、はっきりした案内を出す。
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".ogg" && LooksLikeOpus(path))
            {
                onDone(AudioLoadResult.Unsupported,
                    null,
                    "Opus形式は再生できません。Vorbisに変換してください（例: ffmpeg -i 元ファイル -c:a libvorbis -q:a 6 出力ファイル）");
                yield break;
            }

            AudioType audioType = AudioTypeFromExtension(path);
            string uri = "file://" + path.Replace("\\", "/");
            using var www = UnityWebRequestMultimedia.GetAudioClip(uri, audioType);
            if (stream) ((DownloadHandlerAudioClip)www.downloadHandler).streamAudio = true;
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                onDone(AudioLoadResult.DecodeFailed, null, $"読み込みに失敗しました ({path}): {www.error}");
                yield break;
            }

            var clip = DownloadHandlerAudioClip.GetContent(www);
            onDone(AudioLoadResult.Ok, clip, path);
        }
    }
}
