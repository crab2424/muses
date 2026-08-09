using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;
using Muses.Audio;
using Muses.Chart;

namespace Muses.Game
{
    /// <summary>
    /// song-play-flow-r1.md §2。タイトル/ロード/プレイ/結果の画面遷移を1シーン内の状態機械として持つ。
    /// GameController は「1曲をプレイする」責務に限定し、こちらが曲の探索・読み込み・開始/終了を駆動する
    /// （§2.3: シーンは増やさない。Inspector配線のやり直し・PanelSettings実行時生成の実機不具合実績
    /// (ipad-build-issues-r1.md ①)を避けるため）。
    /// </summary>
    public class AppController : MonoBehaviour
    {
        private enum AppState { Title, Loading, Playing, Result }

        /// <summary>実行時生成では実機で描画できない(ipad-build-issues-r1.md ①)。
        /// StageOverlayと同じ Assets/UI/Game/GameOverlayPanelSettings.asset を流用する
        /// （song-play-flow-r1.md §2.3）。</summary>
        [SerializeField] private PanelSettings panelSettingsAsset;
        [SerializeField] private GameController gameController;

        private UIDocument uiDocument;
        private VisualElement root;

        private AppState state = AppState.Title;
        private AppState screenBeforeSettings = AppState.Title;

        private PlayerSettings settings;
        private List<SongEntry> songs = new();

        // ---- 終了条件(§8.1) ----
        private float endTime;

        // ---- screens ----
        private VisualElement titleScreen;
        private Button startButton;
        private Label titleErrorLabel;

        private VisualElement loadingScreen;
        private Label loadingLabel;

        private VisualElement pauseButton;
        private VisualElement pauseScreen;

        private VisualElement settingsScreen;
        private DropdownField songDropdown;
        private DropdownField difficultyDropdown;

        private VisualElement resultScreen;
        private Label resultSummaryLabel;

        private void Awake()
        {
            settings = PlayerSettingsStore.Load();

            uiDocument = gameObject.AddComponent<UIDocument>();
            uiDocument.panelSettings = panelSettingsAsset;
            uiDocument.sortingOrder = 10; // StageOverlay(既定0)より前面

            root = uiDocument.rootVisualElement;
            root.style.flexGrow = 1;

            RefreshSongList();

            BuildTitleScreen();
            BuildLoadingScreen();
            BuildPauseUi();
            BuildSettingsScreen(); // 中身(曲/難易度リスト等)はOpenSettings()のたび更新する
            BuildResultScreen();

            ApplySettingsToGame();
            ShowTitle();
        }

        // ---------------------------------------------------------------
        // 曲探索
        // ---------------------------------------------------------------

        private void RefreshSongList()
        {
            string userRoot = string.IsNullOrEmpty(settings.songsRoot) ? null : settings.songsRoot;
            songs = SongLoader.Enumerate(userRoot);

            if (!songs.Any(s => s.songId == settings.songId) && songs.Count > 0)
            {
                settings.songId = songs[0].songId;
                settings.difficulty = songs[0].difficulties.Count > 0 ? songs[0].difficulties[0].difficulty : "";
            }
        }

        private SongEntry? FindSelectedSong() =>
            songs.Where(s => s.songId == settings.songId).Select(s => (SongEntry?)s).FirstOrDefault();

        // ---------------------------------------------------------------
        // Title
        // ---------------------------------------------------------------

        private void BuildTitleScreen()
        {
            titleScreen = FullScreenOverlay(new Color(0.06f, 0.06f, 0.08f, 0.92f));

            var panel = new VisualElement();
            panel.style.alignSelf = Align.Center;
            panel.style.marginTop = 120;
            panel.style.alignItems = Align.Center;
            titleScreen.Add(panel);

            var titleLabel = new Label("muses");
            titleLabel.style.fontSize = 48;
            titleLabel.style.color = Color.white;
            titleLabel.style.marginBottom = 48;
            panel.Add(titleLabel);

            startButton = MakeMenuButton("START", StartPressed);
            panel.Add(startButton);

            var settingsButton = MakeMenuButton("設定", () => OpenSettings(AppState.Title));
            panel.Add(settingsButton);

            titleErrorLabel = new Label("");
            titleErrorLabel.style.color = new Color(1f, 0.6f, 0.5f);
            titleErrorLabel.style.marginTop = 24;
            titleErrorLabel.style.maxWidth = 640;
            titleErrorLabel.style.whiteSpace = WhiteSpace.Normal;
            titleErrorLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            panel.Add(titleErrorLabel);

            root.Add(titleScreen);
        }

        private void RefreshTitleState()
        {
            bool hasSongs = songs.Count > 0;
            startButton.SetEnabled(hasSongs);
            if (hasSongs)
            {
                titleErrorLabel.text = "";
                return;
            }

            var paths = string.Join("\n", SongLoader.SearchRoots(
                string.IsNullOrEmpty(settings.songsRoot) ? null : settings.songsRoot));
            titleErrorLabel.text =
                "曲が見つかりません。以下のいずれかに曲フォルダ(song.museproj を含む)を置いてください" +
                "（iPadなら Files アプリの muses フォルダ）:\n" + paths;
        }

        private void ShowTitle()
        {
            state = AppState.Title;
            RefreshSongList();
            RefreshTitleState();
            SetVisible(titleScreen, true);
            SetVisible(loadingScreen, false);
            SetVisible(pauseButton, false);
            SetVisible(pauseScreen, false);
            SetVisible(resultScreen, false);
        }

        private void StartPressed()
        {
            var entry = FindSelectedSong();
            if (entry == null) return;
            StartCoroutine(LoadAndStart(entry.Value, settings.difficulty));
        }

        // ---------------------------------------------------------------
        // Loading (§3.3: ロードは非同期(音源デコード)+同期(メッシュ生成)の混在で
        // 正確な%は出せないため、段階ラベル+不定形インジケータにする)
        // ---------------------------------------------------------------

        private void BuildLoadingScreen()
        {
            loadingScreen = FullScreenOverlay(new Color(0.06f, 0.06f, 0.08f, 0.92f));
            loadingLabel = new Label("読み込み中");
            loadingLabel.style.alignSelf = Align.Center;
            loadingLabel.style.marginTop = 200;
            loadingLabel.style.fontSize = 24;
            loadingLabel.style.color = Color.white;
            loadingScreen.Add(loadingLabel);
            SetVisible(loadingScreen, false);
            root.Add(loadingScreen);
        }

        private void ShowLoading(string label)
        {
            state = AppState.Loading;
            loadingLabel.text = label;
            SetVisible(titleScreen, false);
            SetVisible(loadingScreen, true);
        }

        private IEnumerator LoadAndStart(SongEntry entry, string difficulty)
        {
            ShowLoading("譜面を読み込み中…");

            Chart.SongMeta song;
            Chart.ChartData chart;
            try
            {
                (song, chart) = SongLoader.Load(entry, difficulty);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"AppController: 譜面の読み込みに失敗しました: {ex.Message}");
                ShowTitle();
                titleErrorLabel.text = $"譜面の読み込みに失敗しました: {ex.Message}";
                yield break;
            }

            AudioClip clip = null;
            string audioPath = SongLoader.ResolveAudioPath(entry);
            if (!string.IsNullOrEmpty(audioPath))
            {
                loadingLabel.text = "音源を展開中…";
                bool done = false;
                yield return AudioFileLoader.Load(this, audioPath, (result, loadedClip, message) =>
                {
                    done = true;
                    if (result == AudioLoadResult.Ok) clip = loadedClip;
                    else Debug.LogWarning($"AppController: 音源の読み込みに失敗しました({result}): {message}");
                });
                if (!done) yield break; // 念のため（AudioFileLoader.Loadは必ずコールバックを呼ぶ設計）
            }

            loadingLabel.text = "準備中…";
            yield return null; // ラベルを1フレーム表示してからメッシュ生成(重い同期処理)に入る

            gameController.LoadChart(chart, song, clip);
            ApplySettingsToGame();
            gameController.StartGame();

            endTime = Mathf.Max(gameController.LastNoteEndTime(), gameController.AudioEndTime() ?? 0f) + 2f;

            ShowPlaying();
        }

        // ---------------------------------------------------------------
        // Playing / Pause (§5.3)
        // ---------------------------------------------------------------

        private void BuildPauseUi()
        {
            pauseButton = new VisualElement();
            pauseButton.style.position = Position.Absolute;
            pauseButton.style.left = 16;
            pauseButton.style.top = 16;
            pauseButton.style.width = 48;
            pauseButton.style.height = 48;
            pauseButton.style.borderTopLeftRadius = pauseButton.style.borderTopRightRadius =
                pauseButton.style.borderBottomLeftRadius = pauseButton.style.borderBottomRightRadius = 8;
            pauseButton.style.backgroundColor = new Color(0f, 0f, 0f, 0.4f);
            var pauseIcon = new Label("II");
            pauseIcon.style.color = Color.white;
            pauseIcon.style.fontSize = 18;
            pauseIcon.style.unityTextAlign = TextAnchor.MiddleCenter;
            pauseIcon.style.flexGrow = 1;
            pauseButton.Add(pauseIcon);
            pauseButton.RegisterCallback<PointerDownEvent>(_ => OnPausePressed());
            SetVisible(pauseButton, false);
            root.Add(pauseButton);

            pauseScreen = FullScreenOverlay(new Color(0f, 0f, 0f, 0.6f));
            var panel = new VisualElement();
            panel.style.alignSelf = Align.Center;
            panel.style.marginTop = 160;
            panel.style.alignItems = Align.Center;
            pauseScreen.Add(panel);

            var pausedLabel = new Label("一時停止");
            pausedLabel.style.fontSize = 28;
            pausedLabel.style.color = Color.white;
            pausedLabel.style.marginBottom = 32;
            panel.Add(pausedLabel);

            panel.Add(MakeMenuButton("再開", OnResumePressed));
            panel.Add(MakeMenuButton("はじめから", OnRetryFromPausePressed));
            panel.Add(MakeMenuButton("設定", () => OpenSettings(AppState.Playing)));
            panel.Add(MakeMenuButton("タイトルへ戻る", OnQuitToTitlePressed));

            SetVisible(pauseScreen, false);
            root.Add(pauseScreen);
        }

        private void ShowPlaying()
        {
            state = AppState.Playing;
            SetVisible(titleScreen, false);
            SetVisible(loadingScreen, false);
            SetVisible(resultScreen, false);
            SetVisible(pauseScreen, false);
            SetVisible(pauseButton, true);
        }

        private void OnPausePressed()
        {
            if (state != AppState.Playing) return;
            gameController.Pause();
            SetVisible(pauseScreen, true);
        }

        private void OnResumePressed()
        {
            gameController.Resume();
            SetVisible(pauseScreen, false);
        }

        private void OnRetryFromPausePressed()
        {
            SetVisible(pauseScreen, false);
            gameController.Retry();
            endTime = Mathf.Max(gameController.LastNoteEndTime(), gameController.AudioEndTime() ?? 0f) + 2f;
        }

        private void OnQuitToTitlePressed()
        {
            gameController.StopToIdle();
            SetVisible(pauseScreen, false);
            ShowTitle();
        }

        // ---------------------------------------------------------------
        // Settings (§6): ポーズメニュー or タイトルから開く。開いた場所へ閉じたら戻る。
        // ---------------------------------------------------------------

        private void BuildSettingsScreen()
        {
            settingsScreen = FullScreenOverlay(new Color(0.06f, 0.06f, 0.08f, 0.95f));

            var panel = new VisualElement();
            panel.style.alignSelf = Align.Center;
            panel.style.marginTop = 48;
            panel.style.width = 560;
            panel.style.maxHeight = new Length(85, LengthUnit.Percent);
            settingsScreen.Add(panel);

            var scroll = new ScrollView();
            scroll.style.flexGrow = 1;
            panel.Add(scroll);

            var title = new Label("設定");
            title.style.fontSize = 22;
            title.style.color = Color.white;
            title.style.marginBottom = 16;
            scroll.Add(title);

            songDropdown = new DropdownField("曲");
            songDropdown.RegisterValueChangedCallback(evt =>
            {
                settings.songId = evt.newValue; // choicesはsongIdそのものを積んでいる(OpenSettings参照)
                RefreshDifficultyDropdown();
            });
            scroll.Add(songDropdown);

            difficultyDropdown = new DropdownField("難易度");
            difficultyDropdown.RegisterValueChangedCallback(evt => settings.difficulty = evt.newValue);
            scroll.Add(difficultyDropdown);

            // 楽曲オフセット(2026-08-09追加): 譜面エディタで設定したSongMeta.offsetSecを
            // 上書きするのではなく、実機での微調整分をここで加算する
            // （GameController.ApplyPlayerSettings: totalSongOffsetSec = song.offsetSec + これ）。
            scroll.Add(MakeSliderRow("楽曲オフセット(ms)", -1000f, 1000f, () => settings.songOffsetMs,
                v => settings.songOffsetMs = v, v => $"{v:F0} ms"));
            // 可変幅は±1000ms（ユーザー要望、2026-08-09）。±150msでは端まで振っても体感差が
            // 分かりにくく、そもそも効いているかの切り分けができなかったため。判定窓(GOOD半幅100ms)
            // より十分広い範囲を取れるので、意図的に大きくずらして確認する用途にも使える。
            scroll.Add(MakeSliderRow("判定オフセット(ms)", -1000f, 1000f, () => settings.judgeOffsetMs,
                v => settings.judgeOffsetMs = v, v => $"{v:F0} ms"));
            scroll.Add(MakeSliderRow("描画オフセット(ms)", -1000f, 1000f, () => settings.visualOffsetMs,
                v => settings.visualOffsetMs = v, v => $"{v:F0} ms"));
            scroll.Add(MakeSliderRow("マスター音量", 0f, 1f, () => settings.masterVolume,
                v => settings.masterVolume = v, v => $"{v * 100f:F0}%"));
            scroll.Add(MakeSliderRow("BGM音量", 0f, 1f, () => settings.bgmVolume,
                v => settings.bgmVolume = v, v => $"{v * 100f:F0}%"));
            scroll.Add(MakeSliderRow("SE音量", 0f, 1f, () => settings.seVolume,
                v => settings.seVolume = v, v => $"{v * 100f:F0}%"));
            scroll.Add(MakeSliderRow("ハイスピード", 0.5f, 3f, () => settings.hiSpeed,
                v => settings.hiSpeed = v, v => $"{v:F2}x"));
            scroll.Add(MakeSliderRow("ノーツの厚み", 0.01f, 0.15f, () => settings.noteThickness,
                v => settings.noteThickness = v, v => $"{v:F3}"));

            var metronomeToggle = new Toggle("メトロノーム");
            metronomeToggle.value = settings.metronome;
            metronomeToggle.RegisterValueChangedCallback(evt => settings.metronome = evt.newValue);
            scroll.Add(metronomeToggle);

            var closeButton = MakeMenuButton("閉じる（保存して戻る）", CloseSettings);
            closeButton.style.marginTop = 24;
            panel.Add(closeButton);

            SetVisible(settingsScreen, false);
            root.Add(settingsScreen);
        }

        private void RefreshDifficultyDropdown()
        {
            var entry = FindSelectedSong();
            var diffs = entry?.difficulties.Select(d => d.difficulty).ToList() ?? new List<string>();
            difficultyDropdown.choices = diffs;
            if (diffs.Count == 0) return;
            if (!diffs.Contains(settings.difficulty)) settings.difficulty = diffs[0];
            difficultyDropdown.SetValueWithoutNotify(settings.difficulty);
        }

        private void OpenSettings(AppState previous)
        {
            RefreshSongList();
            songDropdown.choices = songs.Select(s => s.songId).ToList();
            if (songs.Count > 0)
            {
                if (!songs.Any(s => s.songId == settings.songId)) settings.songId = songs[0].songId;
                songDropdown.SetValueWithoutNotify(settings.songId);
            }
            RefreshDifficultyDropdown();

            screenBeforeSettings = previous;
            SetVisible(titleScreen, false);
            SetVisible(pauseScreen, false);
            SetVisible(settingsScreen, true);
        }

        private void CloseSettings()
        {
            PlayerSettingsStore.Save(settings);
            ApplySettingsToGame();
            SetVisible(settingsScreen, false);

            if (screenBeforeSettings == AppState.Title)
            {
                ShowTitle();
            }
            else
            {
                SetVisible(pauseScreen, true);
            }
        }

        private void ApplySettingsToGame() => gameController.ApplyPlayerSettings(settings);

        // ---------------------------------------------------------------
        // Result (§5.4/§8)
        // ---------------------------------------------------------------

        private void BuildResultScreen()
        {
            resultScreen = FullScreenOverlay(new Color(0.06f, 0.06f, 0.08f, 0.95f));

            var panel = new VisualElement();
            panel.style.alignSelf = Align.Center;
            panel.style.marginTop = 120;
            panel.style.alignItems = Align.Center;
            resultScreen.Add(panel);

            var title = new Label("RESULT");
            title.style.fontSize = 32;
            title.style.color = Color.white;
            title.style.marginBottom = 24;
            panel.Add(title);

            resultSummaryLabel = new Label("");
            resultSummaryLabel.style.fontSize = 18;
            resultSummaryLabel.style.color = Color.white;
            resultSummaryLabel.style.marginBottom = 32;
            resultSummaryLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            resultSummaryLabel.style.whiteSpace = WhiteSpace.Normal;
            panel.Add(resultSummaryLabel);

            panel.Add(MakeMenuButton("もう一度", OnRetryFromResultPressed));
            panel.Add(MakeMenuButton("タイトルへ戻る", OnQuitToTitleFromResultPressed));

            SetVisible(resultScreen, false);
            root.Add(resultScreen);
        }

        private void ShowResult()
        {
            state = AppState.Result;
            var score = gameController.Score;
            int total = gameController.TotalComboPoints();
            int computed = score?.ComputeScore(total) ?? 0;
            resultSummaryLabel.text =
                $"SCORE {computed}\n" +
                $"MAX COMBO {score?.maxCombo ?? 0}\n" +
                $"PERFECT+ {score?.perfectPlus ?? 0}  PERFECT {score?.perfect ?? 0}  " +
                $"GOOD {score?.good ?? 0}  MISS {score?.miss ?? 0}";

            SetVisible(pauseButton, false);
            SetVisible(pauseScreen, false);
            SetVisible(resultScreen, true);
        }

        private void OnRetryFromResultPressed()
        {
            gameController.Retry();
            endTime = Mathf.Max(gameController.LastNoteEndTime(), gameController.AudioEndTime() ?? 0f) + 2f;
            ShowPlaying();
        }

        private void OnQuitToTitleFromResultPressed()
        {
            gameController.StopToIdle();
            ShowTitle();
        }

        // ---------------------------------------------------------------
        // 終了条件監視(§8.1)
        // ---------------------------------------------------------------

        private void Update()
        {
            if (state != AppState.Playing) return;
            if (gameController.Paused) return;
            if (gameController.SongTime >= endTime) ShowResult();
        }

        // ---------------------------------------------------------------
        // UI組み立てヘルパー
        // ---------------------------------------------------------------

        private VisualElement FullScreenOverlay(Color background)
        {
            var e = new VisualElement();
            e.style.position = Position.Absolute;
            e.style.left = 0;
            e.style.top = 0;
            e.style.right = 0;
            e.style.bottom = 0;
            e.style.backgroundColor = background;
            return e;
        }

        private static void SetVisible(VisualElement e, bool visible) =>
            e.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;

        private Button MakeMenuButton(string text, Action onClick)
        {
            var b = new Button(onClick) { text = text };
            b.style.width = 280;
            b.style.height = 44;
            b.style.marginBottom = 12;
            b.style.fontSize = 16;
            return b;
        }

        private VisualElement MakeSliderRow(string label, float min, float max, Func<float> get, Action<float> set, Func<float, string> format)
        {
            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginBottom = 8;
            row.style.alignItems = Align.Center;

            var slider = new Slider(min, max) { value = get(), showInputField = true };
            slider.style.flexGrow = 1;
            slider.label = label;

            var valueLabel = new Label(format(get()));
            valueLabel.style.width = 70;
            valueLabel.style.color = Color.white;
            valueLabel.style.unityTextAlign = TextAnchor.MiddleRight;

            slider.RegisterValueChangedCallback(evt =>
            {
                set(evt.newValue);
                valueLabel.text = format(evt.newValue);
                ApplySettingsToGame(); // §6.3: ポーズ中にその場で効果を確認できるよう即時反映する
            });

            row.Add(slider);
            row.Add(valueLabel);
            return row;
        }
    }
}
