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

export interface DerivedReadout {
  'カメラ俯角 θ (deg)': string;
  '判定線の奥行き z_j': string;
  '空中面の高さ h': string;
  '地上面の幅 (1セル)': string;
  '空中面の幅 (1セル)': string;
  '地上帯の奥行き範囲': string;
  '空中帯の奥行き範囲': string;
  '地上の可視限界': string;
  '先読み (秒)': string;
  '1セルの実寸 (mm@11inch)': string;
  fps: string;
  'audio latency (ms)': string;
}

export function buildGui(cfg: StageConfig, hooks: GuiHooks): {
  gui: GUI;
  readout: DerivedReadout;
  refresh: (d: Derived, extra: { fps: number; latencyMs: number; aspect: number }) => void;
} {
  const gui = new GUI({ title: 'muses / stage params' });
  gui.close();

  const R = hooks.rebuild;

  const fScreen = gui.addFolder('スクリーン空間 (NDC) — これが正');
  fScreen.add(cfg, 'vHorizon', 0.3, 0.95, 0.01).name('地平線').onChange(R);
  fScreen.add(cfg, 'vSkyTop', -0.2, 0.9, 0.01).name('空中 帯 上端').onChange(R);
  fScreen.add(cfg, 'vSkyJudge', -0.2, 0.9, 0.01).name('空中 判定線').onChange(R);
  fScreen.add(cfg, 'vSkyBot', -0.4, 0.9, 0.01).name('空中 帯 下端').onChange(R);
  fScreen.add(cfg, 'vSplit', -0.6, 0.4, 0.01).name('y_split (層境界)').onChange(R);
  fScreen.add(cfg, 'vGroundTop', -0.9, 0.2, 0.01).name('地上 帯 上端').onChange(R);
  fScreen.add(cfg, 'vGroundJudge', -0.95, 0.2, 0.01).name('地上 判定線').onChange(R);
  fScreen.add(cfg, 'vGroundBot', -1, 0.1, 0.01).name('地上 帯 下端').onChange(R);
  fScreen.add(cfg, 'U', 0.5, 1.0, 0.01).name('帯の半幅 U').onChange(R);
  fScreen.add(cfg, 'cells', [6, 12, 24, 36]).name('セル数').onChange(R);
  fScreen.open();

  const fCam = gui.addFolder('カメラ (導出の入力)');
  fCam.add(cfg, 'phiDeg', 25, 80, 1).name('垂直画角 φ').onChange(R);
  fCam.add(cfg, 'yCam', 4, 20, 0.5).name('カメラ高さ (基準スケール)').onChange(R);
  fCam.add(cfg, 'drawFar', 20, 140, 5).name('描画最遠').onChange(R);

  const fPlay = gui.addFolder('ゲームプレイ');
  fPlay.add(cfg, 'scrollSpeed', 4, 40, 0.5).name('スクロール速度').onChange(hooks.rechart);
  fPlay.add(cfg, 'bpm', 60, 260, 1).name('BPM').onChange(hooks.rechart);
  fPlay.add(cfg, 'windowPerfect', 10, 120, 5).name('PERFECT窓 (ms)');
  fPlay.add(cfg, 'windowGood', 30, 250, 5).name('GOOD窓 (ms)');
  fPlay.add(cfg, 'splitHysteresis', 0, 0.2, 0.01).name('層ヒステリシス');
  fPlay.add(cfg, 'metronome').name('メトロノーム');
  fPlay.add({ restart: hooks.restart }, 'restart').name('▶ 最初から');

  const fDbg = gui.addFolder('デバッグ表示');
  fDbg.add(cfg, 'showHorizon').name('地平線');
  fDbg.add(cfg, 'showSplitLine').name('y_split 線');
  fDbg.add(cfg, 'showCellIndex').name('セル番号');
  fDbg.add(cfg, 'showTouchDebug').name('タッチ点');
  fDbg.add(cfg, 'showLaneFloor').name('床/空中面').onChange(R);

  const readout: DerivedReadout = {
    'カメラ俯角 θ (deg)': '',
    '判定線の奥行き z_j': '',
    '空中面の高さ h': '',
    '地上面の幅 (1セル)': '',
    '空中面の幅 (1セル)': '',
    '地上帯の奥行き範囲': '',
    '空中帯の奥行き範囲': '',
    '地上の可視限界': '',
    '先読み (秒)': '',
    '1セルの実寸 (mm@11inch)': '',
    fps: '',
    'audio latency (ms)': '',
  };
  const fOut = gui.addFolder('導出された 3D パラメータ (読み取り専用)');
  for (const k of Object.keys(readout) as (keyof DerivedReadout)[]) {
    fOut.add(readout, k).listen().disable();
  }
  fOut.open();

  const actions = {
    'デフォルトに戻す': () => {
      Object.assign(cfg, DEFAULT_CONFIG);
      gui.controllersRecursive().forEach((c) => c.updateDisplay());
      R();
    },
    'JSON をコピー': async () => {
      const { showHorizon, showSplitLine, showCellIndex, showTouchDebug, showLaneFloor, metronome, ...rest } = cfg;
      void showHorizon;
      void showSplitLine;
      void showCellIndex;
      void showTouchDebug;
      void showLaneFloor;
      void metronome;
      const text = JSON.stringify(rest, null, 2);
      try {
        await navigator.clipboard.writeText(text);
      } catch {
        console.log(text);
      }
    },
  };
  gui.add(actions, 'デフォルトに戻す');
  gui.add(actions, 'JSON をコピー');

  const refresh = (
    d: Derived,
    extra: { fps: number; latencyMs: number; aspect: number },
  ) => {
    const f = (n: number, p = 2) => n.toFixed(p);
    readout['カメラ俯角 θ (deg)'] = f((d.theta * 180) / Math.PI);
    readout['判定線の奥行き z_j'] = f(d.zJudge);
    readout['空中面の高さ h'] = f(d.skyHeight);
    readout['地上面の幅 (1セル)'] = `${f(d.groundWidth)} (${f(d.groundCellWidth)})`;
    readout['空中面の幅 (1セル)'] = `${f(d.skyWidth)} (${f(d.skyCellWidth)})`;
    readout['地上帯の奥行き範囲'] = `${f(d.groundBandDepth[0])} – ${f(d.groundBandDepth[1])}`;
    readout['空中帯の奥行き範囲'] = `${f(d.skyBandDepth[0])} – ${f(d.skyBandDepth[1])}`;
    readout['地上の可視限界'] = f(d.groundVisibleFar);
    readout['先読み (秒)'] = f(d.readAheadSec);
    // 11 インチタブレット (対角 11in = 279.4mm) の画面幅から 1 セルの実寸を出す
    const diagMm = 279.4;
    const a = extra.aspect;
    const widthMm = (diagMm * a) / Math.sqrt(a * a + 1);
    readout['1セルの実寸 (mm@11inch)'] = f((widthMm * (2 * cfg.U)) / cfg.cells, 1);
    readout.fps = f(extra.fps, 0);
    readout['audio latency (ms)'] = f(extra.latencyMs, 1);
  };

  return { gui, readout, refresh };
}
