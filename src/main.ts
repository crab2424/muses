import * as THREE from 'three';
import { DEFAULT_CONFIG, type StageConfig } from './config';
import { derive, type Derived } from './derive';
import { Stage } from './stage';
import { NoteField } from './notes';
import { buildDemoChart, type Note } from './chart';
import { InputManager } from './input';
import { Overlay } from './overlay';
import { Judge } from './judge';
import { Clock } from './audio';
import { buildGui } from './gui';

const CHART_SECONDS = 600;

const cfg: StageConfig = { ...DEFAULT_CONFIG };

const glCanvas = document.getElementById('gl') as HTMLCanvasElement;
const overlayCanvas = document.getElementById('overlay') as HTMLCanvasElement;
const hud = document.getElementById('hud') as HTMLDivElement;
const startScreen = document.getElementById('start') as HTMLDivElement;
const startBtn = document.getElementById('startBtn') as HTMLButtonElement;

const renderer = new THREE.WebGLRenderer({
  canvas: glCanvas,
  antialias: true,
  alpha: false,
  powerPreference: 'high-performance',
});
renderer.setClearColor(0x05060c, 1);

const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(cfg.phiDeg, 1, 0.1, 500);
camera.rotation.order = 'YXZ';

const stage = new Stage();
const noteField = new NoteField();
scene.add(stage.root, noteField.root);

const clock = new Clock();
const overlay = new Overlay(overlayCanvas);
const input = new InputManager(overlayCanvas, cfg, () => clock.songTime);
const judge = new Judge(cfg, noteField, overlay);

let d: Derived = derive(cfg, 1);
let chart: Note[] = [];

function aspect(): number {
  return window.innerWidth / window.innerHeight;
}

function rebuild(): void {
  d = derive(cfg, aspect());
  camera.fov = cfg.phiDeg;
  camera.aspect = aspect();
  camera.near = 0.1;
  camera.far = cfg.drawFar * 2;
  camera.position.set(0, cfg.yCam, 0);
  camera.rotation.set(-d.theta, 0, 0);
  camera.updateProjectionMatrix();

  stage.build(cfg, d);
  noteField.build(cfg, d, chart);
  input.setConfig(cfg);
  judge.setConfig(cfg);
  // 進行中の判定状態はジオメトリ再構築で失われるので、スコアも合わせてリセットする
  judge.reset();
}

function rechart(): void {
  chart = buildDemoChart(cfg.bpm, CHART_SECONDS);
  rebuild();
}

function restart(): void {
  rechart();
  void clock.start(cfg.bpm);
}

function resize(): void {
  const w = window.innerWidth;
  const h = window.innerHeight;
  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  renderer.setPixelRatio(dpr);
  renderer.setSize(w, h, false);
  overlay.resize(w, h, dpr);
  rebuild();
}

const { refresh } = buildGui(cfg, { rebuild, rechart, restart });

input.onEnter = (e) => {
  if (clock.running) judge.onEnter(e, clock.songTime);
};

window.addEventListener('resize', resize);
window.addEventListener('orientationchange', () => setTimeout(resize, 250));

chart = buildDemoChart(cfg.bpm, CHART_SECONDS);
resize();

// ---- ループ ----
let fps = 60;
let lastFrame = performance.now();
let hudAcc = 0;

function frame(): void {
  requestAnimationFrame(frame);

  const now = performance.now();
  const dt = (now - lastFrame) / 1000;
  lastFrame = now;
  fps += (1 / Math.max(dt, 1e-4) - fps) * 0.08;

  const t = clock.songTime;
  clock.tickMetronome(cfg.bpm, cfg.metronome);
  noteField.update(t);
  if (clock.running) judge.update(t, input);

  renderer.render(scene, camera);
  overlay.draw(cfg, input, t);

  hudAcc += dt;
  if (hudAcc > 0.1) {
    hudAcc = 0;
    const s = judge.score;
    hud.innerHTML =
      `t ${t.toFixed(2)}s &nbsp; ${fps.toFixed(0)}fps<br>` +
      `COMBO ${s.combo} (max ${s.maxCombo})<br>` +
      `P ${s.perfect} / G ${s.good} / M ${s.miss}<br>` +
      `<span style="color:#e8f0ff">${s.lastJudge}` +
      (s.lastJudge === 'PERFECT' || s.lastJudge === 'GOOD'
        ? ` ${s.lastMs > 0 ? '+' : ''}${s.lastMs.toFixed(0)}ms`
        : '') +
      `</span>`;
    refresh(d, { fps, latencyMs: clock.outputLatencyMs, aspect: aspect() });
  }
}

startBtn.addEventListener('click', async () => {
  startScreen.style.display = 'none';
  await clock.start(cfg.bpm);
  rechart();
});

// デバッグ用フック（コンソールから中身を覗ける）
(window as unknown as Record<string, unknown>).__muses = {
  cfg,
  scene,
  camera,
  input,
  judge,
  clock,
  get derived() {
    return d;
  },
  get chart() {
    return chart;
  },
};

frame();
