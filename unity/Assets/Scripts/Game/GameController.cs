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
    ///
    /// 簡略化した点（TS版との差分）:
    /// - Web版は AudioContext のユーザー操作要件があるため「Startボタン」を挟むが、
    ///   Unity にはその制約が無いので Start() で自動的にゲームを開始する。
    /// - リサイズ（アスペクト比変化）時に StageController は自動で再導出されるが、
    ///   NoteView 側は Rechart() を呼ぶまで追従しない（この点はTS版のresize連動より簡略）。
    /// </summary>
    public class GameController : MonoBehaviour
    {
        [SerializeField] private StageController stageController;
        [SerializeField] private NoteView noteView;
        [SerializeField] private TouchInputManager input;
        [SerializeField] private StageOverlay overlay;
        [SerializeField] private AudioSource metronomeSource;
        [SerializeField] private float chartSeconds = 600f;

        private SongClock clock;
        private Judge judge;
        private Chart.ChartData chart = new();
        private float fps = 60f;

        private void Awake()
        {
            clock = new SongClock(metronomeSource);
        }

        private void Start()
        {
            // vSyncCount!=0だとtargetFrameRateは無視される（ChartEditorApp.csの表示設定と同じ排他関係）。
            // 実機(iPad)は120Hz(ProMotion)描画が可能なため、vSyncに委ねず明示的に120を要求する。
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 120;

            OffsetSettings.Load(stageController.Config);

            judge = new Judge(stageController.Config, noteView.SetNoteAlpha);
            if (overlay != null) overlay.Judge = judge;

            input.Init(() => clock.SongTime);
            input.OnEnter = e =>
            {
                if (clock.Running) judge.OnEnter(e, JudgeTime());
            };

            StartGame();
        }

        /// <summary>judgeOffsetMs を適用した、判定にだけ使う時刻。音と入力のズレ補正用</summary>
        private float JudgeTime() => clock.SongTime + stageController.Config.judgeOffsetMs / 1000f;

        /// <summary>visualOffsetMs を適用した、ノーツ描画位置にだけ使う時刻。音と描画のズレ補正用</summary>
        private float VisualTime() => clock.SongTime + stageController.Config.visualOffsetMs / 1000f;

        /// <summary>main.ts の restart() 相当。譜面を作り直して頭から再生する（implementation-roadmap.md 項目F）。</summary>
        public void StartGame()
        {
            paused = false;
            Rechart();
            clock.Start();
        }

        public bool Paused => paused;
        private bool paused;

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

        /// <summary>即座リスタート（implementation-roadmap.md 項目F）。譜面を作り直して頭から再生する。</summary>
        public void Retry() => StartGame();

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

        private void Rechart()
        {
            chart = ChartBuilder.BuildDemoChart(stageController.Config.bpm, chartSeconds, stageController.Config.cells);
            var scrollTimelines = Chart.ChartFormat.BuildScrollTimelines(chart); // note-spec.md §5.5
            noteView.Build(stageController.Config, stageController.Derived, chart.notes, scrollTimelines);
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

            if (overlay != null) overlay.SetHudTime(t, fps);
        }
    }
}
