using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using Muses.Chart;
using Muses.Gameplay;
using Muses.Notes;
using Muses.Stage;
using Muses.TouchInput;

namespace Muses.ChartTool
{
    /// <summary>
    /// editor-spec.md §5。譜面エディタのプレビュー再生（音源同期・オートプレイ）と §2.2 の
    /// RenderTexture埋め込み3Dプレビューをまとめて担当する。ChartEditorApp（MonoBehaviour）に
    /// 保持され、そのUnityライフサイクル（Awake/Update/OnGUI/OnDestroy相当）から明示的に呼ばれる
    /// プレーンなC#クラス（NoteView/StageViewが子GameObjectを自前管理するのと同じやり方）。
    ///
    /// 実ゲームのシーン（SampleScene）とは独立に、このクラス自身がオフスクリーンの
    /// Camera/StageView/StageController/NoteView 一式をコードから組み立てる。Inspector配線を
    /// 前提にしないぶん、シェーダ3種（Stage/Note/BeatLine）だけは ChartEditorApp の Inspector で
    /// 割り当ててもらう必要がある（既存のゲームシーンで既にユーザーがやっている操作と同じ）。
    /// </summary>
    public class PreviewSystem
    {
        private readonly MonoBehaviour host;
        private readonly Shader stageShader;
        private readonly Shader noteShader;
        private readonly Shader beatLineShader;
        private readonly StageConfig cfg = StageConfig.Default();

        // ---- rig ----
        private GameObject rigRoot;
        private Camera cam;
        private RenderTexture rt;
        private int rtW = -1, rtH = -1;
        private StageController stageController;
        private StageView stageView;
        private NoteView noteView;
        private AudioSource musicSource;
        private AudioSource seSource;
        private AudioClip seClip;
        private const int SePoolSize = 8;
        private AudioSource[] sePool;
        private int sePoolIndex;
        private const float AudioLookAheadSec = 0.1f;

        // ---- playback state ----
        private PreviewClock clock;
        private Judge judge;
        private List<NoteRuntime> runtimes = new();
        private ChartData chart = new();
        private SongMeta song = new();
        private float lastSongTime;
        private bool autoplay;
        private string lastLoadedAudioPath;
        private int seCoroutineToken;

        // ---- render throttle (editor-spec.md §2.2: 再生中のみ更新、停止中は差分があるときだけ) ----
        private bool sceneDirty = true;
        private float lastRenderRealtime = -999f;
        private const float RenderIntervalSec = 1f / 60f;

        public PreviewSystem(MonoBehaviour host, Shader stageShader, Shader noteShader, Shader beatLineShader)
        {
            this.host = host;
            this.stageShader = stageShader;
            this.noteShader = noteShader;
            this.beatLineShader = beatLineShader;
            BuildRig();
        }

        private void BuildRig()
        {
            rigRoot = new GameObject("PreviewRig") { hideFlags = HideFlags.DontSave };
            rigRoot.transform.SetParent(host.transform, false);

            var camGo = new GameObject("PreviewCamera") { hideFlags = HideFlags.DontSave };
            camGo.transform.SetParent(rigRoot.transform, false);
            cam = camGo.AddComponent<Camera>();
            cam.enabled = false; // 明示的に Render() を呼ぶ（自動レンダリングでの垂れ流しを防ぐ、§2.2）

            // PreviewCameraはenabled=falseでオフスクリーンRenderTextureにしか描かないため、
            // シーンに描画用カメラが1台も無くなり「Display 1 / No cameras rendering」の警告
            // オーバーレイが出る（editor-ui-redesign.md §5-2）。何も映さない最背面カメラを別途置いて解消する。
            var displayCamGo = new GameObject("DisplayFallbackCamera") { hideFlags = HideFlags.DontSave };
            displayCamGo.transform.SetParent(rigRoot.transform, false);
            var displayCam = displayCamGo.AddComponent<Camera>();
            displayCam.clearFlags = CameraClearFlags.SolidColor;
            displayCam.backgroundColor = Color.black;
            displayCam.cullingMask = 0;
            displayCam.depth = -100f;
            displayCam.orthographic = true;
            displayCam.orthographicSize = 1f;
            displayCam.nearClipPlane = 0.01f;
            displayCam.farClipPlane = 1f;
            // AudioListenerがシーンに1つも無いと、下で作る musicSource/seSource の音が一切鳴らない
            // （「There are no audio listeners in the scene」の警告の実害はこれ）。
            displayCamGo.AddComponent<AudioListener>();

            var stageGo = new GameObject("PreviewStage") { hideFlags = HideFlags.DontSave };
            stageGo.transform.SetParent(rigRoot.transform, false);
            stageView = stageGo.AddComponent<StageView>();
            stageView.ConfigureShader(stageShader);
            stageController = stageGo.AddComponent<StageController>();
            stageController.Configure(cam, stageView, cfg);

            var notesGo = new GameObject("PreviewNotes") { hideFlags = HideFlags.DontSave };
            notesGo.transform.SetParent(stageGo.transform, false);
            noteView = notesGo.AddComponent<NoteView>();
            noteView.ConfigureShaders(noteShader, beatLineShader);

            var musicGo = new GameObject("PreviewMusic") { hideFlags = HideFlags.DontSave };
            musicGo.transform.SetParent(rigRoot.transform, false);
            musicSource = musicGo.AddComponent<AudioSource>();
            musicSource.playOnAwake = false;
            musicSource.spatialBlend = 0f;

            var seGo = new GameObject("PreviewSe") { hideFlags = HideFlags.DontSave };
            seGo.transform.SetParent(rigRoot.transform, false);
            seSource = seGo.AddComponent<AudioSource>();
            seSource.playOnAwake = false;
            seSource.spatialBlend = 0f;
            seClip = BuildClickClip(1200f);

            // MikuMikuWorld移植候補: ノーツSEの先読みスケジュール(audioLookAhead方式、
            // ScoreEditor.cpp:418-485相当)。PlayOneShotは即時再生しかできないため、
            // AudioSource.PlayScheduledで鳴らせるプールを用意する（dspTime基準なので、
            // Tick()の呼び出し頻度=フレームレートに再生タイミングが縛られなくなる）。
            sePool = new AudioSource[SePoolSize];
            for (int i = 0; i < SePoolSize; i++)
            {
                var srcGo = new GameObject($"PreviewSeScheduled{i}") { hideFlags = HideFlags.DontSave };
                srcGo.transform.SetParent(rigRoot.transform, false);
                var src = srcGo.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 0f;
                sePool[i] = src;
            }

            clock = new PreviewClock(musicSource);
        }

        /// <summary>譜面/曲メタが変わるたび（読み込み・編集）に呼ぶ。tick→秒の再解決とNoteView/Judgeの再構築を行う。</summary>
        public void Rebuild(SongMeta newSong, ChartData newChart, string audioDir)
        {
            song = newSong;
            chart = newChart;

            // ChartSerializer.ReadChart と同じ規則: BPMは曲の属性なので、譜面側へ都度コピーしてから解決する
            // （エディタでの編集中は ChartSerializer を経由しないため、ここで明示的に合わせておく必要がある）。
            chart.bpmEvents = new List<BpmEvent>(song.bpmEvents);

            ChartFormat.ResolveTimes(chart);
            ChartFormat.ResolveSlideComboPoints(chart);
            var scrollTimelines = ChartFormat.BuildScrollTimelines(chart);

            stageController.EnsureBuilt();
            noteView.Build(cfg, stageController.Derived, chart.notes, scrollTimelines);
            runtimes = noteView.Runtimes;

            judge = new Judge(cfg, noteView.SetNoteAlpha);
            judge.Prepare(runtimes);
            judge.Reset();

            float t = clock.SongTime;
            noteView.UpdateScroll(t, cfg.hiSpeed);
            lastSongTime = t;

            TryLoadAudio(audioDir);
            MarkDirty();
        }

        private void TryLoadAudio(string audioDir)
        {
            if (string.IsNullOrEmpty(song.audio) || string.IsNullOrEmpty(audioDir)) return;
            string path = Path.Combine(audioDir, song.audio);
            if (path == lastLoadedAudioPath) return;
            lastLoadedAudioPath = path;
            if (!File.Exists(path))
            {
                musicSource.clip = null;
                return;
            }
            host.StartCoroutine(LoadAudioCoroutine(path));
        }

        private IEnumerator LoadAudioCoroutine(string path)
        {
            int myToken = ++seCoroutineToken;
            string uri = "file://" + path.Replace("\\", "/");
            using var www = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.OGGVORBIS);
            yield return www.SendWebRequest();
            if (myToken != seCoroutineToken) yield break; // 途中で別の曲に切り替わっていたら破棄

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"PreviewSystem: 音源の読み込みに失敗しました ({path}): {www.error}");
                yield break;
            }
            var clip = DownloadHandlerAudioClip.GetContent(www);
            bool wasRunning = clock.Running;
            float at = clock.SongTime;
            // Running中にclipを差し替えると「音源無し(silent)クロック」の Running=true のまま
            // AudioSource.Play() を呼ばずに Seek/Play() がガードで早期returnしてしまう
            // (Play()は「Running==trueなら何もしない」前提のため)。一度明示的に止めてから
            // clipを差し替え、Seek→Playの順で音源ベースのクロックとして正しく再始動する。
            if (wasRunning) clock.Pause();
            musicSource.clip = clip;
            clock.Seek(at);
            if (wasRunning) clock.Play();
            MarkDirty();
        }

        public void MarkDirty() => sceneDirty = true;

        /// <summary>editor-spec.md §4 V10。読み込み済み音源の長さ(秒)。未読み込みなら-1。</summary>
        public float AudioLengthSec => musicSource.clip != null ? musicSource.clip.length : -1f;
        public float SongTime => clock?.SongTime ?? 0f;
        public bool IsPlaying => clock?.Running ?? false;

        // ---------- UI(ChartEditorApp.UI.cs)から触る状態 ----------
        // 以前はこのクラス自身がIMGUIでトランスポートを描いていたが、UI Toolkit移行にあたって
        // 「状態を持つ・描かない」に整理した（editor-ui-redesign.md §1-C: トランスポートは
        // 最下部ステータスバーへ移設）。

        /// <summary>プレビュータブが表示されている間だけtrue。falseの間はRender()を呼ばない（§2.2の負荷対策）。</summary>
        public bool RenderEnabled { get; set; }

        public bool SePreview { get; set; } = true;
        public bool Metronome { get; set; }
        public bool Autoplay => autoplay;
        public RenderTexture Texture => rt;

        private float rate = 1f;
        public float Rate
        {
            get => rate;
            set
            {
                if (Mathf.Approximately(rate, value)) return;
                rate = value;
                clock.SetRate(rate);
            }
        }

        /// <summary>オートプレイ中のスコア表示（非オートプレイ時はnull）。</summary>
        public string AutoplaySummary
        {
            get
            {
                if (!autoplay || judge == null) return null;
                var s = judge.Score;
                int totalCombo = 0;
                foreach (var n in chart.notes) totalCombo += n.kind == NoteKind.Slide ? n.comboTimes.Count : 1;
                return $"P+{s.perfectPlus} P{s.perfect} G{s.good} M{s.miss}  combo{s.maxCombo}  score{s.ComputeScore(totalCombo)}";
            }
        }

        /// <summary>譜面の最後のノーツが終わる時刻(秒)。シークバーの上限に使う。</summary>
        public float ChartEndSec
        {
            get
            {
                float end = 0f;
                foreach (var n in chart.notes) end = Mathf.Max(end, ChartMath.NoteEnd(n));
                return end;
            }
        }

        // ---------- 毎フレーム駆動 ----------

        public void Tick()
        {
            float prev = lastSongTime;
            float cur = clock.SongTime;

            if (clock.Running)
            {
                noteView.UpdateScroll(cur, cfg.hiSpeed);

                if (autoplay && judge != null)
                {
                    var contacts = AutoplayDriver.Step(judge, cfg, runtimes, prev, cur);
                    judge.Update(cur, contacts);
                }

                if (SePreview) PlayNoteSe(prev, cur);
                if (Metronome) TickMetronome(prev, cur);

                MarkDirty();
            }

            lastSongTime = cur;
            MaybeRender();
        }

        /// <summary>
        /// MikuMikuWorld移植候補: ノーツ時刻の <see cref="AudioLookAheadSec"/> 秒前に検出し、
        /// dspTime基準でスケジュール再生する（参照元ScoreEditor.cpp:418-485のaudioLookAhead方式）。
        /// 旧実装は「時刻を跨いだフレームでPlayOneShot」だったため最大1フレーム分の遅れ・ジッタが
        /// あったが、スケジュール方式ならTick()の呼び出し頻度に関わらず狙った時刻ちょうどに鳴る。
        /// </summary>
        private void PlayNoteSe(float prev, float cur)
        {
            float prevOffset = prev - AudioLookAheadSec;
            float curOffset = cur - AudioLookAheadSec;
            foreach (var note in chart.notes)
            {
                float t = note.points[0].time;
                if (t > prevOffset && t <= curOffset) PlayScheduledSe(0.6f, t - cur);
                if (note.kind == NoteKind.Slide)
                    foreach (var ct in note.comboTimes)
                        if (ct > prevOffset && ct <= curOffset) PlayScheduledSe(0.25f, ct - cur);
            }
        }

        private void PlayScheduledSe(float volume, float delaySeconds)
        {
            var src = sePool[sePoolIndex];
            sePoolIndex = (sePoolIndex + 1) % SePoolSize;
            src.clip = seClip;
            src.volume = volume;
            src.PlayScheduled(AudioSettings.dspTime + Mathf.Max(0f, delaySeconds));
        }

        private float nextMetronomeBeat;

        private void TickMetronome(float prev, float cur)
        {
            if (cur < prev) nextMetronomeBeat = cur; // シーク後の巻き戻りに追従
            float bpm = Mathf.Max(1f, ChartFormat.BpmAtTime(song.bpmEvents, cur));
            float beatSec = 60f / bpm;
            if (nextMetronomeBeat < cur - beatSec * 2f) nextMetronomeBeat = cur; // 大きくシークしたら追いつかせる
            while (nextMetronomeBeat <= cur)
            {
                if (nextMetronomeBeat > prev - 1e-4f) seSource.PlayOneShot(seClip, 0.4f);
                nextMetronomeBeat += beatSec;
            }
        }

        private void MaybeRender()
        {
            if (!RenderEnabled || cam == null || rt == null) return;
            bool shouldRender = clock.Running
                ? Time.realtimeSinceStartup - lastRenderRealtime >= RenderIntervalSec
                : sceneDirty;
            if (!shouldRender) return;
            cam.targetTexture = rt;
            cam.Render();
            lastRenderRealtime = Time.realtimeSinceStartup;
            sceneDirty = false;
        }

        // ---------- 再生制御 ----------

        public void Play() { clock.Play(); MarkDirty(); }
        public void Pause() { clock.Pause(); MarkDirty(); }
        public void TogglePlay() { clock.TogglePlay(); MarkDirty(); }

        public void Seek(float t)
        {
            clock.Seek(t);
            lastSongTime = clock.SongTime;
            judge?.Seek(lastSongTime);
            noteView.UpdateScroll(lastSongTime, cfg.hiSpeed);
            MarkDirty();
        }

        public void SetAutoplay(bool on)
        {
            if (autoplay == on) return;
            autoplay = on;
            judge?.Seek(clock.SongTime);
            MarkDirty();
        }

        // ---------- 描画先 ----------

        /// <summary>
        /// プレビュータブの表示サイズに合わせてRenderTextureを張り替える。UI Toolkit側は
        /// 戻り値を <c>style.backgroundImage</c> に割り当てる（editor-ui-redesign.md §6 の
        /// 「RenderTexture埋め込みはImage要素のbackgroundImageでそのまま置き換えられる」）。
        /// </summary>
        public RenderTexture EnsureRenderTexture(int width, int height)
        {
            int w = Mathf.Clamp(width, 16, 1920);
            int h = Mathf.Clamp(height, 16, 1080);
            if (rt != null && w == rtW && h == rtH) return rt;

            if (rt != null) rt.Release();
            rt = new RenderTexture(w, h, 16) { name = "ChartEditorPreview" };
            rtW = w; rtH = h;
            MarkDirty();
            return rt;
        }

        /// <summary>プレビュータブから離れたときに呼ぶ。カメラに描画対象を持たせない（§2.2）。</summary>
        public void DetachTexture()
        {
            RenderEnabled = false;
            if (cam != null) cam.targetTexture = null;
        }

        // ---------- 破棄 ----------

        public void Dispose()
        {
            if (rt != null) rt.Release();
            if (rigRoot != null)
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(rigRoot);
                else UnityEngine.Object.DestroyImmediate(rigRoot);
            }
        }

        /// <summary>SongClock.BuildClickClip と同様、外部アセット無しで短いクリック音を実行時合成する。</summary>
        private static AudioClip BuildClickClip(float freq)
        {
            const int sampleRate = 44100;
            int length = (int)(sampleRate * 0.05f);
            var clip = AudioClip.Create($"PreviewClick{freq:F0}", length, 1, sampleRate, false);
            var data = new float[length];
            for (int i = 0; i < length; i++)
            {
                float t = i / (float)sampleRate;
                float env = Mathf.Exp(-t * 70f);
                data[i] = Mathf.Sin(2f * Mathf.PI * freq * t) * env * 0.5f;
            }
            clip.SetData(data, 0);
            return clip;
        }
    }
}
