using UnityEngine;
using UnityEngine.InputSystem;
using Muses.Audio;
using Muses.Chart;
using Muses.Gameplay;
using Muses.Notes;
using Muses.Overlay;
using Muses.Stage;
using Muses.TouchInput;

namespace Muses.Game
{
    /// <summary>
    /// main.ts 相当の統括役。Stage/Notes/Input/Judge/Clock を束ねてゲームループを回す。
    /// 「1曲をプレイする」責務に限定し、画面遷移（タイトル/ロード/結果）は AppController が持つ
    /// （song-play-flow-r1.md §2.3）。
    ///
    /// 簡略化した点（TS版との差分）:
    /// - リサイズ（アスペクト比変化）時に StageController は自動で再導出されるが、
    ///   NoteView 側は Rechart() を呼ぶまで追従しない（この点はTS版のresize連動より簡略）。
    /// </summary>
    // AppController.Awake()がApplySettingsToGame()経由でGameController.ApplyPlayerSettings()を
    // 呼ぶが、そこで参照するclockはGameController.Awake()で初めて生成される。同じAwakeタイミングの
    // 兄弟コンポーネント間で実行順は保証されないため、これが後に実行されるとNullReferenceExceptionになる
    // （2026-08-10、実機/Editor Playで発覚）。明示的にGameControllerを先に走らせて解決する。
    [DefaultExecutionOrder(-100)]
    public class GameController : MonoBehaviour
    {
        [SerializeField] private StageController stageController;
        [SerializeField] private NoteView noteView;
        [SerializeField] private TouchInputManager input;
        [SerializeField] private StageOverlay overlay;
        [SerializeField] private AudioSource metronomeSource;
        [SerializeField] private AudioSource musicSource;
        [SerializeField] private AudioSource seSource;
        /// <summary>AppController から一度も LoadChart() されないままの開発時セーフティネット用
        /// （エディタでこのシーンだけ単独Playしたときに何も表示されないと確認しづらいため）。</summary>
        [SerializeField] private float demoChartSeconds = 600f;

        private SongClock clock;
        private HitSePlayer hitSe;
        private Judge judge;
        private Chart.ChartData chart = new();
        private Chart.SongMeta song;
        private float fps = 60f;
        /// <summary>perf-r1.md §12の切り分け用。SongClock.AudioScheduleErrorSec は
        /// DSPブロック単位(約23ms)に量子化されて段々に動くため、fpsと同じ要領で均して表示する。</summary>
        private float audioErrorMs;

        private void Awake()
        {
            clock = new SongClock(metronomeSource);
            hitSe = new HitSePlayer(seSource);
        }

        private void Start()
        {
            // vSyncCount!=0だとtargetFrameRateは無視される（ChartEditorApp.csの表示設定と同じ排他関係）。
            // 実機(iPad)は120Hz(ProMotion)描画が可能なため、vSyncに委ねず明示的に120を要求する。
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 120;

            judge = new Judge(stageController.Config, noteView.SetNoteAlpha, hitSe.OnJudged,
                noteView.SetSlideSegmentEatable);
            if (overlay != null) overlay.Judge = judge;

            input.Init(() => clock.SongTime);
            input.OnEnter = e =>
            {
                if (clock.Running) judge.OnEnter(e, JudgeTime());
            };

            // song-play-flow-r1.md §2.3: 実際の開始はAppController.LoadSong→StartGame()が駆動する。
            // ここでは「まだ何も無い」状態にせず、開発時にこのシーンを単独Playしても
            // すぐ確認できるようデモ譜面だけ仕込んでおく（AppControllerが無ければ動かないよりまし）。
            if (chart.notes.Count == 0)
                chart = ChartBuilder.BuildDemoChart(stageController.Config.bpm, demoChartSeconds, stageController.Config.cells);
        }

        /// <summary>judgeOffsetMs を適用した、判定にだけ使う時刻。音と入力のズレ補正用</summary>
        private float JudgeTime() => clock.SongTime + stageController.Config.judgeOffsetMs / 1000f;

        /// <summary>visualOffsetMs を適用した、ノーツ描画位置にだけ使う時刻。音と描画のズレ補正用</summary>
        private float VisualTime() => clock.SongTime + stageController.Config.visualOffsetMs / 1000f;

        /// <summary>song-play-flow-r1.md §3.2。AppController がロード完了後に呼ぶ。musicClip が null
        /// なら無音のフリーランクロックになる（音源無し譜面・デモ譜面向け）。ここではエディタ値
        /// (song.offsetSec)だけを一時的に渡す。プレイヤー設定の楽曲オフセットを合算した最終値は
        /// 直後に呼ばれる ApplyPlayerSettings() が clock.UpdateOffset() で上書きする前提
        /// （AppController.LoadAndStart: LoadChart → ApplySettingsToGame → StartGame の順序に依存）。</summary>
        public void LoadChart(Chart.ChartData newChart, Chart.SongMeta newSong, AudioClip musicClip)
        {
            chart = newChart;
            song = newSong;
            clock.SetMusic(musicSource, musicClip, newSong?.offsetSec ?? 0f);
        }

        /// <summary>song-play-flow-r1.md §6.2追補(2026-08-09)。譜面エディタが設定した
        /// SongMeta.offsetSec に、プレイヤー設定の楽曲オフセット(ms)を加算した合計値。
        /// エディタ側の値を上書きせず「エディタ値 + 実機での微調整」として積み増す
        /// （ユーザー要望）。AudioEndTime()と ApplyPlayerSettings 双方で使うため保持する。</summary>
        private float totalSongOffsetSec;

        /// <summary>§6.2/§6.4。設定変更のたび（設定画面を閉じたとき・ロード完了直後）に呼ぶ。
        /// ステージ角度等の「頂点に焼き込まれる値」はここに含めない（§6.2でv1では設定に出さない判断）。</summary>
        public void ApplyPlayerSettings(PlayerSettings ps)
        {
            var cfg = stageController.Config;
            cfg.judgeOffsetMs = ps.judgeOffsetMs;
            cfg.visualOffsetMs = ps.visualOffsetMs;
            cfg.hiSpeed = ps.hiSpeed;
            cfg.metronome = ps.metronome;
            noteView.ThicknessFrac = ps.noteThickness;
            AudioListener.volume = ps.masterVolume;
            if (musicSource != null) musicSource.volume = ps.bgmVolume;
            if (seSource != null) seSource.volume = ps.seVolume;

            totalSongOffsetSec = (song?.offsetSec ?? 0f) + ps.songOffsetMs / 1000f;
            clock.UpdateOffset(totalSongOffsetSec);
        }

        /// <summary>main.ts の restart() 相当。ノーツを作り直して頭から再生する（implementation-roadmap.md 項目F）。</summary>
        public void StartGame()
        {
            paused = false;
            Rechart();
            clock.Start();
        }

        public bool Paused => paused;
        private bool paused;

        /// <summary>Result画面・エディタから読む用。判定成立コールバックの外から直接触らないこと。</summary>
        public Score Score => judge?.Score;

        /// <summary>song-play-flow-r1.md §8.1。終了条件の算出に使う。最終ノーツの終端時刻(秒)。
        /// ノーツが1つも無ければ0。</summary>
        public float LastNoteEndTime()
        {
            float end = 0f;
            foreach (var n in chart.notes) end = Mathf.Max(end, ChartMath.NoteEnd(n));
            return end;
        }

        /// <summary>音源終端の譜面時間(秒)。音源が無ければ null。</summary>
        public float? AudioEndTime()
        {
            if (musicSource == null || musicSource.clip == null) return null;
            return musicSource.clip.length - totalSongOffsetSec;
        }

        /// <summary>note-spec.md §7。Score.ComputeScoreの分母N。SlideはcomboTimesの数、それ以外は1
        /// （PreviewSystemの表示ラベル計算(PreviewSystem.cs:491-492)と同じ数え方）。</summary>
        public int TotalComboPoints()
        {
            int total = 0;
            foreach (var n in chart.notes) total += n.kind == NoteKind.Slide ? n.comboTimes.Count : 1;
            return total;
        }

        public float SongTime => clock.SongTime;

        public void Pause()
        {
            if (paused) return;
            paused = true;
            clock.Pause();
        }

        public void Resume()
        {
            if (!paused) return;
            paused = false;
            clock.Resume();
        }

        public void TogglePause()
        {
            if (paused) Resume(); else Pause();
        }

        /// <summary>即座リスタート（implementation-roadmap.md 項目F）。同じ譜面を頭から再生する。</summary>
        public void Retry() => StartGame();

        /// <summary>song-play-flow-r1.md §2.3。タイトルへ戻るときに呼ぶ。時計を完全に止める
        /// （Pause()と違い、次はStartGame()で頭から始まる想定）。</summary>
        public void StopToIdle()
        {
            clock.Stop();
            paused = false;
        }

        /// <summary>
        /// implementation-roadmap.md 項目D。任意の曲時刻へジャンプする。エディタのスクラブ操作の想定口。
        /// Clock と Judge の両方を同じ時刻に揃える必要があるため、必ずこのメソッド経由で行うこと。
        /// </summary>
        public void SeekTo(float songTime)
        {
            songTime = Mathf.Max(0f, songTime);
            clock.Seek(songTime);
            judge.Seek(songTime);
            noteView.FlushAlpha(); // editor-ui-rework-r13.md §7.3
        }

        public void SeekBy(float deltaSeconds) => SeekTo(clock.SongTime + deltaSeconds);

        /// <summary>
        /// 開発用のキーボードショートカット（Space=一時停止、R=リトライ、←/→=5秒シーク）。
        /// エディタUIができるまでの暫定確認手段。Editor確認用にマウスを拾うTouchInputManagerと同じ位置づけ。
        /// </summary>
        private void HandleDevInput()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.spaceKey.wasPressedThisFrame) TogglePause();
            if (kb.rKey.wasPressedThisFrame) Retry();
            if (kb.leftArrowKey.wasPressedThisFrame) SeekBy(-5f);
            if (kb.rightArrowKey.wasPressedThisFrame) SeekBy(5f);
        }

        /// <summary>song-play-flow-r1.md §8.2。同じ譜面でのリトライではメッシュ(約8万頂点)を
        /// 作り直さない。LoadChart()が新しい ChartData インスタンスを渡す前提で、参照が変わって
        /// いなければ「同じ譜面」とみなす（ステージ角度・アスペクト比はv1ではプレイ中に変わらない
        /// ため、参照比較だけで安全に判定できる）。</summary>
        private Chart.ChartData lastBuiltChart;

        private void Rechart()
        {
            if (!ReferenceEquals(chart, lastBuiltChart))
            {
                var scrollTimelines = Chart.ChartFormat.BuildScrollTimelines(chart); // note-spec.md §5.5
                noteView.Build(stageController.Config, stageController.Derived, chart.notes, scrollTimelines);
                lastBuiltChart = chart;
            }
            judge.SetConfig(stageController.Config);
            judge.Reset();
            judge.Prepare(noteView.Runtimes); // 縦連判定(中点分割)の実効窓をここで1回だけprecompute
            noteView.FlushAlpha(); // editor-ui-rework-r13.md §7.3
        }

        private void Update()
        {
            HandleDevInput();

            // ipad-build-issues-r1.md ②-B: dspTimeのDSPバッファ量子化(実機で実測23〜25Hz)を
            // 補間して滑らかにする。判定用JudgeTime()/描画用VisualTime()の両方がこの値を使う。
            clock.Advance(Time.unscaledDeltaTime);

            float t = clock.SongTime;
            float dt = Time.deltaTime;
            fps += (1f / Mathf.Max(dt, 1e-4f) - fps) * 0.08f;

            clock.TickMetronome(stageController.Config.bpm, stageController.Config.metronome);
            noteView.UpdateScroll(VisualTime(), stageController.Config.hiSpeed);
            if (clock.Running)
            {
                judge.Update(JudgeTime(), input.Contacts.Values);
                noteView.FlushAlpha(); // editor-ui-rework-r13.md §7.3: メッシュ全体転送を1フレーム1回に限定
            }

            audioErrorMs += (clock.AudioScheduleErrorSec * 1000f - audioErrorMs) * 0.05f;

            if (overlay != null) overlay.SetHudTime(t, fps, audioErrorMs);
        }
    }
}
