using UnityEngine;
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
            OffsetSettings.Load(stageController.Config);

            judge = new Judge(stageController.Config, noteView);
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

        /// <summary>main.ts の restart() 相当</summary>
        public void StartGame()
        {
            Rechart();
            clock.Start();
        }

        private void Rechart()
        {
            chart = ChartBuilder.BuildDemoChart(stageController.Config.bpm, chartSeconds, stageController.Config.cells);
            noteView.Build(stageController.Config, stageController.Derived, chart.notes);
            judge.SetConfig(stageController.Config);
            judge.Reset();
        }

        private void Update()
        {
            float t = clock.SongTime;
            float dt = Time.deltaTime;
            fps += (1f / Mathf.Max(dt, 1e-4f) - fps) * 0.08f;

            clock.TickMetronome(stageController.Config.bpm, stageController.Config.metronome);
            noteView.SetSongTime(VisualTime());
            if (clock.Running) judge.Update(JudgeTime(), input);

            if (overlay != null) overlay.SetHudTime(t, fps);
        }
    }
}
