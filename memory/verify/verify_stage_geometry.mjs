// stage.ts と同じロジックを JS で再実装し、Unity 側の StageGeometrySmokeTest と数値照合するための参照値を出す。
// 移植元: web-prototype/src/stage.ts の Stage.build()。THREE 依存部分を除いた頂点生成のみ抜き出した。
import { readFileSync } from 'node:fs';

const cfg = JSON.parse(readFileSync('/Users/crab2424/Documents/muses/memory/settings.json', 'utf8'));

const deg = (d) => (d * Math.PI) / 180;
const rad = (r) => (r * 180) / Math.PI;

function psi(theta, tanHalfPhi, v) {
  return theta - Math.atan(v * tanHalfPhi);
}
function depthAt(yCam, yPlane, psiRad) {
  if (psiRad <= 1e-6) return Infinity;
  if (psiRad >= Math.PI / 2 - 1e-6) return 0;
  return (yCam - yPlane) / Math.tan(psiRad);
}
function viewDist(yCam, theta, yPlane, depth) {
  return (yCam - yPlane) * Math.sin(theta) + depth * Math.cos(theta);
}
function halfWidthAt(zc, aspect, tanHalfPhi) {
  return zc * aspect * tanHalfPhi;
}
function laneX(cfg, d, u, layerF, z) {
  const yPlane = layerF * d.skyHeight;
  const a = (cfg.yCam - yPlane) * d.sinTheta;
  const zc = a + z * d.cosTheta;
  const zcJudge = a + d.zJudge * d.cosTheta;
  const zcFar = a + d.zFar * d.cosTheta;
  const c = Math.min(1, Math.max(0, cfg.laneConverge * (zcFar / d.zcFarGround)));
  const zcMix = zc + (zcJudge - zc) * c;
  return u * d.laneK * zcMix;
}
function thetaRange(cfg, tanHalfPhi) {
  const vTop = Math.max(cfg.vSkyJudge, cfg.vGroundJudge);
  const vBot = Math.min(cfg.vSkyJudge, cfg.vGroundJudge);
  return [rad(Math.atan(vTop * tanHalfPhi) + deg(1.5)), rad(Math.atan(vBot * tanHalfPhi) + deg(88.5))];
}
function derive(cfg, aspectIn) {
  const aspect = Number.isFinite(aspectIn) && aspectIn > 0 ? aspectIn : 1;
  const tanHalfPhi = Math.tan(deg(cfg.phiDeg) / 2);
  const [thetaMinDeg, thetaMaxDeg] = thetaRange(cfg, tanHalfPhi);
  const thetaDeg = Math.min(thetaMaxDeg, Math.max(thetaMinDeg, cfg.thetaDeg));
  const theta = deg(thetaDeg);
  const P = (v) => psi(theta, tanHalfPhi, v);
  const vHorizon = Math.tan(theta) / tanHalfPhi;
  const zJudge = depthAt(cfg.yCam, 0, P(cfg.vGroundJudge));
  const skyHeight = cfg.yCam - zJudge * Math.tan(P(cfg.vSkyJudge));
  const gbNear = depthAt(cfg.yCam, 0, P(cfg.vGroundBot));
  const sbNear = depthAt(cfg.yCam, skyHeight, P(cfg.vSkyBot));
  const zcGround = viewDist(cfg.yCam, theta, 0, zJudge);
  const zcSky = viewDist(cfg.yCam, theta, skyHeight, zJudge);
  const sinTheta = Math.sin(theta);
  const cosTheta = Math.cos(theta);
  const laneK = cfg.U * aspect * tanHalfPhi;
  const vCeil = Math.min(vHorizon - 0.02, 1);
  const frac = Math.min(0.995, Math.max(0.02, cfg.farFrac));
  const vFar = cfg.vGroundJudge + frac * (vCeil - cfg.vGroundJudge);
  const zFar = Math.min(depthAt(cfg.yCam, 0, P(vFar)), zJudge * 200);
  const zcFarGround = viewDist(cfg.yCam, theta, 0, zFar);
  const groundNear = gbNear;
  const skyNear = cfg.skyFloorFromJudge ? zJudge : sbNear;
  const readAhead = Math.max(cfg.readAheadSec, 0.05);
  const speed = (zFar - zJudge) / readAhead;
  return { aspect, tanHalfPhi, theta, sinTheta, cosTheta, laneK, zJudge, skyHeight, zcFarGround, zFar, groundNear, skyNear, speed, readAhead, drawFar: zFar * 1.15 };
}

function buildLayer(cfg, d, layer) {
  const L =
    layer === 'ground'
      ? { y: 0, layerF: 0, near: d.groundNear, fillAlpha: cfg.groundFillAlpha, step: cfg.laneLineStepGround }
      : { y: d.skyHeight, layerF: 1, near: d.skyNear, fillAlpha: cfg.skyFillAlpha, step: cfg.laneLineStepSky };

  const n0 = Math.max(d.zJudge * 0.02, L.near);
  const f0 = d.zFar;
  if (f0 <= n0) return { visible: false };

  const xAt = (u, z) => laneX(cfg, d, u, L.layerF, z);
  const xLeftNear = xAt(-1, n0);
  const xRightNear = xAt(1, n0);
  const xLeftFar = xAt(-1, f0);
  const xRightFar = xAt(1, f0);

  const out = { visible: true, near: n0, far: f0 };

  if (L.fillAlpha > 0.001) {
    // Web (Three, -z 奥) の頂点。Unity 側は z の符号だけ反転して比較する
    out.plane = [
      [xLeftNear, L.y, -n0],
      [xRightNear, L.y, -n0],
      [xRightFar, L.y, -f0],
      [xLeftFar, L.y, -f0],
    ];
  }

  const step = Math.max(1, Math.round(L.step));
  const pts = [];
  for (let k = 0; k <= cfg.cells; k++) {
    if (k % step !== 0 && k !== cfg.cells) continue;
    const u = -1 + (2 * k) / cfg.cells;
    pts.push([xAt(u, n0), L.y, -n0], [xAt(u, f0), L.y, -f0]);
  }
  if (cfg.hardFarEdge) pts.push([xLeftFar, L.y, -f0], [xRightFar, L.y, -f0]);
  out.lines = pts;
  out.lineCount = pts.length / 2;

  return out;
}

const aspect = 16 / 9;
const d = derive(cfg, aspect);
const ground = buildLayer(cfg, d, 'ground');
const sky = buildLayer(cfg, d, 'sky');

const fmt = (v) => Number(v.toFixed(4));
const fmtPts = (pts) => pts.map((p) => p.map(fmt));

console.log('aspect =', aspect);
console.log('zJudge =', fmt(d.zJudge), 'zFar =', fmt(d.zFar), 'skyHeight =', fmt(d.skyHeight));
console.log('\n-- ground --');
console.log('near/far =', fmt(ground.near), fmt(ground.far));
console.log('plane =', ground.plane ? fmtPts(ground.plane) : null);
console.log('lineCount =', ground.lineCount);
console.log('first line =', fmtPts(ground.lines.slice(0, 2)));
console.log('last line =', fmtPts(ground.lines.slice(-2)));

console.log('\n-- sky --');
console.log('near/far =', fmt(sky.near), fmt(sky.far));
console.log('plane =', sky.plane ? fmtPts(sky.plane) : null);
console.log('lineCount =', sky.lineCount);
console.log('first line =', fmtPts(sky.lines.slice(0, 2)));
console.log('last line =', fmtPts(sky.lines.slice(-2)));
