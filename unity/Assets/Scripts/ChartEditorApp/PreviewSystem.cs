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
    /// <summary>editor-ui-rework-r6.md §4.1(b)。音源読み込みの結果状態。右パネルの音源セクションが
    /// これを見て状態ラベルを出す（旧実装はDebug.LogWarningのみでUIに何も出なかった）。</summary>
    public enum AudioLoadState
    {
        None,
        Loading,
        Ok,
        NotFound,
        DecodeFailed,
        Unsupported,
    }

    /// <summary>editor-ui-rework-r6.md §5.2 案A。ノーツ種別ごとのSE素材。ChartEditorAppの
    /// SerializeFieldとして持ち、PreviewSystemへそのまま渡す（既存のシェーダ3種と同じ配線
    /// パターン）。ゲーム本体からも同じアセットを参照できる（Assets/Audio/SE/に置く前提）。
    /// 未設定のクリップはnullのままでよい（PreviewSystem側でフォールバックする）。</summary>
    [Serializable]
    public class SeClipSet
    {
        public AudioClip tap;
        public AudioClip exTap;
        public AudioClip slide;
        public AudioClip flick;
        public AudioClip tick;
        public AudioClip metronome;
    }

    public class PreviewSystem
    {
        private readonly MonoBehaviour host;
        private readonly Shader stageShader;
        private readonly Shader noteShader;
        private readonly Shader beatLineShader;
        private readonly SeClipSet seClips;
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

        /// <summary>editor-ui-rework-r6.md §4.1。読み込み結果の状態とメッセージ(探したフルパス・
        /// エラー文)。UI側(音源セクション)がそのまま表示する。</summary>
        public AudioLoadState LoadState { get; private set; } = AudioLoadState.None;
        public string LoadMessage { get; private set; } = "";

        // ---- 音量(editor-ui-rework-r6.md §4.3) ----
        // AudioMixerは使わず素のAudioSource.volume/AudioListener.volumeを掛ける方式(設計どおり)。
        // 既定値は参照元(MikuMikuWorld ScoreEditor.cpp:37-39)に合わせて0.8/1.0/1.0。
        private float seVolume = 1f;

        /// <summary>アプリ全体の音量。AudioListener.volumeはグローバルだが、エディタは
        /// スタンドアロンの単独アプリなので副作用は無い。</summary>
        public float MasterVolume
        {
            get => AudioListener.volume;
            set => AudioListener.volume = Mathf.Clamp01(value);
        }

        public float BgmVolume
        {
            get => musicSource.volume;
            set => musicSource.volume = Mathf.Clamp01(value);
        }

        public float SeVolume
        {
            get => seVolume;
            set => seVolume = Mathf.Clamp01(value);
        }

        // ---- render throttle (editor-spec.md §2.2: 再生中のみ更新、停止中は差分があるときだけ) ----
        private bool sceneDirty = true;
        private float lastRenderRealtime = -999f;
        private const float RenderIntervalSec = 1f / 60f;

        public PreviewSystem(MonoBehaviour host, Shader stageShader, Shader noteShader, Shader beatLineShader, SeClipSet seClips = null)
        {
            this.host = host;
            this.stageShader = stageShader;
            this.noteShader = noteShader;
            this.beatLineShader = beatLineShader;
            this.seClips = seClips ?? new SeClipSet();
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
            // editor-ui-rework-r4.md §12: 音源先頭→譜面tick0のズレをPreviewClockへ反映する
            // （今まではSongMeta.offsetSecがどこからも読まれておらず、頭出しが常に「音源0秒=tick0」
            // 固定になっていた）。
            clock.Offset = song.offsetSec;

            // ChartSerializer.ReadChart と同じ規則: BPMは曲の属性なので、譜面側へ都度コピーしてから解決する
            // （エディタでの編集中は ChartSerializer を経由しないため、ここで明示的に合わせておく必要がある）。
            chart.bpmEvents = new List<BpmEvent>(song.bpmEvents);

            ChartFormat.ResolveTimes(chart);
            ChartFormat.ResolveSlideComboPoints(chart);
            var scrollTimelines = ChartFormat.BuildScrollTimelines(chart);
            var barTimes = BuildBarTimes(chart);

            stageController.EnsureBuilt();
            noteView.Build(cfg, stageController.Derived, chart.notes, scrollTimelines, barTimes);
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

        /// <summary>
        /// editor-ui-rework-r4.md §3: プレビューの小節線をエディタのタイムラインと同じ情報源
        /// (song.meters + chart.bpmEvents)から求める。旧実装はStageConfig.bpmから「4拍ごと」を
        /// 決め打ちしており、譜面のBPM・拍子と食い違うと必ずずれていた
        /// （新規譜面はbpmEventsが空→既定120BPM、cfg.bpm=150なので1.25倍ずれる）。
        /// </summary>
        private List<float> BuildBarTimes(ChartData c)
        {
            var tickToSeconds = ChartFormat.BuildTickToSeconds(c.bpmEvents);
            float end = 0f;
            foreach (var n in c.notes) end = Mathf.Max(end, ChartMath.NoteEnd(n));

            var result = new List<float>();
            int firstBar = SongAddr.Normalize(song.meters)[0].bar;
            int guard = 0;
            for (int bar = firstBar; guard < 100000; bar++, guard++)
            {
                int tick = SongAddr.ToTick(song.meters, bar, 1, 0);
                float t = tickToSeconds(tick);
                if (t > end + 4f) break;
                result.Add(t);
            }
            return result;
        }

        // editor-ui-rework-r6.md §4.1(a): 失敗(NotFound/Unsupported/DecodeFailed)はキャッシュしない
        // ので、chartが未保存(dirty)の間はUpdate()から0.3秒おきに呼ばれ続ける(ChartEditorApp.Update
        // の"dirty"はsave/loadでしかfalseに戻らない既存仕様)。失敗の再試行にクールダウンを設けて
        // 無駄なUnityWebRequestの発行を防ぐ。成功済み(LoadState==Ok)のパスは無条件にスキップする。
        private const float FailedLoadRetryCooldownSec = 1.5f;
        private float lastLoadAttemptRealtime = -999f;

        /// <summary>editor-ui-rework-r6.md §4.1(e)。「再読み込み」ボタンから呼ぶ。次のTryLoadAudioで
        /// 強制的に再読み込みさせる(成功済みキャッシュも含めて破棄する)。</summary>
        public void ForceReloadAudio()
        {
            lastLoadedAudioPath = null;
            lastLoadAttemptRealtime = -999f;
        }

        private void TryLoadAudio(string audioDir)
        {
            if (string.IsNullOrEmpty(song.audio) || string.IsNullOrEmpty(audioDir))
            {
                LoadState = AudioLoadState.None;
                LoadMessage = "";
                return;
            }
            string path = Path.Combine(audioDir, song.audio);
            if (path == lastLoadedAudioPath && LoadState == AudioLoadState.Ok) return;
            if (path != lastAttemptedPath) lastLoadAttemptRealtime = -999f; // パスが変われば即座に試す
            lastAttemptedPath = path;
            if (Time.unscaledTime - lastLoadAttemptRealtime < FailedLoadRetryCooldownSec) return;
            lastLoadAttemptRealtime = Time.unscaledTime;

            if (!File.Exists(path))
            {
                musicSource.clip = null;
                LoadState = AudioLoadState.NotFound;
                LoadMessage = $"見つかりません: {path}";
                return;
            }

            // editor-ui-rework-r6.md §0.1/§4.1(c): UnityはOgg Vorbisしかデコードできず、
            // Opusコンテナ(近年のffmpegがoutput.oggに既定で選ぶ)は読めても無音になる/失敗するだけで
            // 原因が分からない。ヘッダ先頭を読んで先に弾き、はっきりした案内を出す。
            if (LooksLikeOpus(path))
            {
                LoadState = AudioLoadState.Unsupported;
                LoadMessage = "Opus形式は再生できません。Vorbisに変換してください（例: ffmpeg -i 元ファイル -c:a libvorbis -q:a 6 出力ファイル）";
                return;
            }

            LoadState = AudioLoadState.Loading;
            LoadMessage = "";
            host.StartCoroutine(LoadAudioCoroutine(path));
        }

        private string lastAttemptedPath;

        /// <summary>Oggコンテナの先頭ページ内に"OpusHead"シグネチャがあるかどうか。
        /// 数十バイト読むだけなので同期I/Oで十分。</summary>
        private static bool LooksLikeOpus(string path)
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

        private IEnumerator LoadAudioCoroutine(string path)
        {
            int myToken = ++seCoroutineToken;
            string uri = "file://" + path.Replace("\\", "/");
            using var www = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.OGGVORBIS);
            yield return www.SendWebRequest();
            if (myToken != seCoroutineToken) yield break; // 途中で別の曲に切り替わっていたら破棄

            if (www.result != UnityWebRequest.Result.Success)
            {
                LoadState = AudioLoadState.DecodeFailed;
                LoadMessage = $"読み込みに失敗しました ({path}): {www.error}";
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
            // editor-ui-rework-r6.md §4.1(a): 成功後にのみキャッシュを確定する。
            lastLoadedAudioPath = path;
            LoadState = AudioLoadState.Ok;
            LoadMessage = path;
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

        /// <summary>editor-ui-rework-r3.md §6: プレビューへの判定線描画用。StageOverlayは
        /// Screen.width/height基準でオフスクリーンRenderTextureには使えないため、UI側が
        /// この設定値を直接読んでPainter2Dで描く。</summary>
        public StageConfig Config => cfg;

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
                if (t > prevOffset && t <= curOffset) PlayScheduledSe(ClipForNoteHead(note.kind), 0.6f, t - cur);
                if (note.kind == NoteKind.Slide)
                    foreach (var ct in note.comboTimes)
                        if (ct > prevOffset && ct <= curOffset) PlayScheduledSe(TickClip, 0.25f, ct - cur);
            }
        }

        /// <summary>editor-ui-rework-r6.md §5.3。ノーツ種別ごとの専用クリップが無ければ
        /// Tap用クリップへ、それも無ければ実行時合成のクリック音へフォールバックする。
        /// 素材が1つも無くても今までどおり動く。</summary>
        private AudioClip ClipForNoteHead(NoteKind kind) => kind switch
        {
            NoteKind.Tap => seClips.tap != null ? seClips.tap : seClip,
            NoteKind.ExTap => seClips.exTap != null ? seClips.exTap : TapOrSynth,
            NoteKind.Slide => seClips.slide != null ? seClips.slide : TapOrSynth,
            NoteKind.Flick => seClips.flick != null ? seClips.flick : TapOrSynth,
            _ => seClip,
        };

        private AudioClip TapOrSynth => seClips.tap != null ? seClips.tap : seClip;
        private AudioClip TickClip => seClips.tick != null ? seClips.tick : seClip;
        private AudioClip MetronomeClip => seClips.metronome != null ? seClips.metronome : seClip;

        private void PlayScheduledSe(AudioClip clip, float volume, float delaySeconds)
        {
            var src = sePool[sePoolIndex];
            sePoolIndex = (sePoolIndex + 1) % SePoolSize;
            src.clip = clip;
            src.volume = volume * seVolume;
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
                if (nextMetronomeBeat > prev - 1e-4f) seSource.PlayOneShot(MetronomeClip, 0.4f * seVolume);
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
            // editor-ui-rework-r4.md §8: StageController.Update()との実行順は保証されないため、
            // 描く直前に明示的にEnsureBuiltを呼んでおく(cam.aspectは既に固定済みなので
            // 呼んでも呼ばなくても同じ値になるはずだが、念のため確実にそのフレームの状態にする)。
            stageController.EnsureBuilt();
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
            // editor-ui-rework-r4.md §8: cam.aspectをRenderTextureのアスペクトに固定する。
            // 代入するとUnityは以後この値を使い続け(自動導出には戻らない)、DetachTexture()で
            // targetTexture=nullにしても画面全体のアスペクトへ戻らなくなる。固定しないと、
            // [ExecuteAlways]なStageController.Update()が「一瞬だけ画面アスペクトで作られた形」を
            // 描いてしまい、タブ切替のたびにステージが広がって見える不具合になる。
            cam.aspect = (float)w / h;
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
