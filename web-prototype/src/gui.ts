import GUI from 'lil-gui';
import { DEFAULT_CONFIG, type StageConfig } from './config';
import type { Derived } from './derive';

export interface GuiHooks {
  /** ジオメトリ再構築が必要な変更 */
  rebuild: () => void;
  /** 譜面再生成が必要な変更 */
  rechart: () => void;
  restart: () => void;
}

export interface Extra {
  fps: number;
  fpsPeak: number;
  latencyMs: number;
  eventRate: number;
  aspect: number;
}

const READOUT_KEYS = [
  'θ の有効域 (deg)',
  '地平線 v_horizon',
  '最遠端 v_far 地上/空中',
  '判定線の奥行き z_j',
  '最遠端の奥行き z_far (共通)',
  '空中面の高さ h',
  '地上面の幅 (1セル)',
  '空中面の幅 (1セル)',
  '先読み (秒) / 速度',
  '1セルの実寸 (mm@11inch)',
  'fps (実測 / ピーク)',
  '入力イベント (Hz)',
  'audio latency (ms)',
] as const;

export type DerivedReadout = Record<(typeof READOUT_KEYS)[number], string>;

export function buildGui(
  cfg: StageConfig,
  hooks: GuiHooks,
): {
  gui: GUI;
  refresh: (d: Derived, extra: Extra) => void;
} {
  const gui = new GUI({ title: 'muses / stage params' });

  const R = hooks.rebuild;

  const fScreen = gui.addFolder('スクリーン空間 (NDC) — これが正');
  fScreen.add(cfg, 'vSkyTop', -0.5, 1.0, 0.01).name('空中 帯 上端').onChange(R);
  fScreen.add(cfg, 'vSkyJudge', -0.5, 1.0, 0.01).name('空中 判定線').onChange(R);
  fScreen.add(cfg, 'vSkyBot', -1.0, 1.0, 0.01).name('空中 帯 下端').onChange(R);
  fScreen.add(cfg, 'vSplit', -1.0, 1.0, 0.01).name('y_split (層境界)').onChange(R);
  fScreen.add(cfg, 'vGroundTop', -1.0, 1.0, 0.01).name('地上 帯 上端').onChange(R);
  fScreen.add(cfg, 'vGroundJudge', -1.0, 0.5, 0.01).name('地上 判定線').onChange(R);
  fScreen.add(cfg, 'vGroundBot', -1.0, 0.5, 0.01).name('地上 帯 下端').onChange(R);
  fScreen.add(cfg, 'U', 0.5, 1.0, 0.01).name('帯の半幅 U').onChange(R);
  fScreen.add(cfg, 'cells', [6, 12, 24, 36]).name('セル数').onChange(hooks.rechart);

  const fCam = gui.addFolder('カメラ');
  // 0=地上面と平行（ステージが潰れる） / 90=真上（層の区別が消える）。
  // 有効域は判定線2本と φ から決まり、範囲外は derive() がクランプする（読み取り欄に表示）。
  fCam.add(cfg, 'thetaDeg', 0, 90, 0.5).name('視点 θ (俯角 deg)').onChange(R);
  fCam.add(cfg, 'farFrac', 0.02, 0.995, 0.005).name('最遠端 (判定線→地平線 の比)').onChange(R);
  fCam
    .add(cfg, 'phiDeg', 25, 110, 1)
    .name('垂直画角 φ (遠近の付き方)')
    .onChange(R);
  fCam
    .add(cfg, 'yCam', 4, 20, 0.5)
    .name('カメラ高さ (見た目不変)')
    .onChange(R);

  const fPlay = gui.addFolder('ゲームプレイ');
  fPlay.add(cfg, 'readAheadSec', 0.4, 5, 0.05).name('先読み時間 (秒)').onChange(R);
  fPlay.add(cfg, 'bpm', 60, 260, 1).name('BPM').onChange(hooks.rechart);
  fPlay.add(cfg, 'windowPerfect', 10, 120, 5).name('PERFECT窓 (ms)');
  fPlay.add(cfg, 'windowGood', 30, 250, 5).name('GOOD窓 (ms)');
  fPlay.add(cfg, 'splitHysteresis', 0, 0.2, 0.01).name('層ヒステリシス');
  fPlay.add(cfg, 'metronome').name('メトロノーム');
  fPlay.add({ restart: hooks.restart }, 'restart').name('▶ 最初から');

  const fLook = gui.addFolder('見た目');
  fLook.add(cfg, 'hardFarEdge').name('最遠端をはっきり切る').onChange(R);
  fLook
    .add(cfg, 'laneConverge', 0, 1, 0.01)
    .name('レーン収束 (0=長方形 / 1=台形)')
    .onChange(R);
  fLook.add(cfg, 'skyFloorFromJudge').name('空中面を判定線で切る').onChange(R);
  fLook.addColor(cfg, 'bgColor').name('背景色').onChange(R);
  fLook.add(cfg, 'groundFillAlpha', 0, 1, 0.01).name('地上面の不透明度').onChange(R);
  fLook.add(cfg, 'skyFillAlpha', 0, 1, 0.01).name('空中面の不透明度').onChange(R);
  fLook
    .add(cfg, 'laneLineStepGround', [1, 2, 3, 4, 6, 12])
    .name('地上 レーン線の間引き')
    .onChange(R);
  fLook
    .add(cfg, 'laneLineStepSky', [1, 2, 3, 4, 6, 12])
    .name('空中 レーン線の間引き')
    .onChange(R);

  const fDbg = gui.addFolder('デバッグ表示');
  fDbg.add(cfg, 'showBand').name('判定帯（枠・セル境界）');
  fDbg.add(cfg, 'showJudgeLine').name('判定線');
  fDbg.add(cfg, 'showHorizon').name('地平線');
  fDbg.add(cfg, 'showSplitLine').name('y_split 線');
  fDbg.add(cfg, 'showCellIndex').name('セル番号');
  fDbg.add(cfg, 'showTouchDebug').name('タッチ点');
  fDbg.add(cfg, 'showLaneFloor').name('床/空中面').onChange(R);

  const readout = Object.fromEntries(READOUT_KEYS.map((k) => [k, ''])) as DerivedReadout;
  const fOut = gui.addFolder('導出値・実測値 (読み取り専用)');
  for (const k of READOUT_KEYS) fOut.add(readout, k).listen().disable();
  fOut.open();

  const actions = {
    'デフォルトに戻す': () => {
      Object.assign(cfg, DEFAULT_CONFIG);
      gui.controllersRecursive().forEach((c) => c.updateDisplay());
      hooks.rechart();
    },
    'JSON をコピー': async () => {
      const text = JSON.stringify(cfg, null, 2);
      try {
        await navigator.clipboard.writeText(text);
      } catch {
        console.log(text);
      }
    },
  };
  gui.add(actions, 'デフォルトに戻す');
  gui.add(actions, 'JSON をコピー');

  const refresh = (d: Derived, extra: Extra) => {
    const f = (n: number, p = 2) => (Number.isFinite(n) ? n.toFixed(p) : '∞');
    readout['θ の有効域 (deg)'] =
      `${f(d.thetaMinDeg, 1)} 〜 ${f(d.thetaMaxDeg, 1)}` +
      (d.thetaClamped ? `  ⚠ ${f((d.theta * 180) / Math.PI, 1)} にクランプ中` : '');
    readout['地平線 v_horizon'] = f(d.vHorizon) + (d.vHorizon > 1 ? ' (画面外)' : '');
    readout['最遠端 v_far 地上/空中'] = `${f(d.vFar)} / ${f(d.vFarSky)}`;
    readout['判定線の奥行き z_j'] = f(d.zJudge);
    readout['最遠端の奥行き z_far (共通)'] = f(d.zFar, 1);
    readout['空中面の高さ h'] = f(d.skyHeight);
    readout['地上面の幅 (1セル)'] = `${f(d.groundWidth)} (${f(d.groundCellWidth)})`;
    readout['空中面の幅 (1セル)'] = `${f(d.skyWidth)} (${f(d.skyCellWidth)})`;
    readout['先読み (秒) / 速度'] = `${f(d.readAhead)} / ${f(d.speed, 1)}`;
    // 11 インチタブレット (対角 11in = 279.4mm) の画面幅から 1 セルの実寸を出す
    const diagMm = 279.4;
    const a = extra.aspect;
    const widthMm = (diagMm * a) / Math.sqrt(a * a + 1);
    readout['1セルの実寸 (mm@11inch)'] = f((widthMm * (2 * cfg.U)) / cfg.cells, 1);
    readout['fps (実測 / ピーク)'] = `${f(extra.fps, 0)} / ${f(extra.fpsPeak, 0)}`;
    readout['入力イベント (Hz)'] = f(extra.eventRate, 0);
    readout['audio latency (ms)'] = f(extra.latencyMs, 1);
  };

  return { gui, refresh };
}
