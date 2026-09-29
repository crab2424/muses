// ステージ再現（Unity SampleScene の StageConfig 値 + NotePlacement.hlsl の配置ロジック）。
// 移植元: web-prototype/src/derive.ts, stage.ts / unity/Assets/Shaders/Include/NotePlacement.hlsl
import * as THREE from 'three';

// SampleScene.unity の cfg（2026-09-29 時点）
export const CFG = {
  phiDeg: 82, yCam: 10, thetaDeg: 38,
  vSkyJudge: 0.18, vSkyTop: 1, vSkyBot: -0.1,
  vGroundTop: -0.1, vGroundBot: -1, vGroundJudge: -0.55,
  farFrac: 0.9, U: 0.87, cells: 12, readAheadSec: 1.2,
  laneConverge: 1, laneLineStepGround: 3, laneLineStepSky: 12,
  groundFillAlpha: 1, skyFillAlpha: 0.2, bgColor: '#a0b298',
  // NoteView（SampleScene）
  thicknessFrac: 0.06, thicknessMinFrac: 0.01, skyThicknessMul: 1.96,
  // NoteGeometry.cs: 左右に ±0.04 セルの余白
  gapCells: 0.04,
};

export const COLORS = {
  tap: '#4aa3ff', exTap: '#ffd54a', flick: '#ff4a4a',
  ground: '#8b5cf6', sky: '#ff3ea5', gridGround: '#3a2f6b', gridSky: '#6b2a55',
};

const deg = (d) => (d * Math.PI) / 180;

export function derive(cfg, aspect) {
  const tanHalfPhi = Math.tan(deg(cfg.phiDeg) / 2);
  const theta = deg(cfg.thetaDeg);
  const P = (v) => theta - Math.atan(v * tanHalfPhi);
  const depthAt = (yPlane, psi) => {
    if (psi <= 1e-6) return Infinity;
    if (psi >= Math.PI / 2 - 1e-6) return 0;
    return (cfg.yCam - yPlane) / Math.tan(psi);
  };
  const vHorizon = Math.tan(theta) / tanHalfPhi;
  const zJudge = depthAt(0, P(cfg.vGroundJudge));
  const skyHeight = cfg.yCam - zJudge * Math.tan(P(cfg.vSkyJudge));
  const gbNear = depthAt(0, P(cfg.vGroundBot));
  const vCeil = Math.min(vHorizon - 0.02, 1);
  const vFar = cfg.vGroundJudge + cfg.farFrac * (vCeil - cfg.vGroundJudge);
  const zFar = Math.min(depthAt(0, P(vFar)), zJudge * 200);
  const sinTheta = Math.sin(theta), cosTheta = Math.cos(theta);
  const zcFarGround = cfg.yCam * sinTheta + zFar * cosTheta;
  const speed = (zFar - zJudge) / cfg.readAheadSec;
  return {
    aspect, tanHalfPhi, theta, sinTheta, cosTheta, zJudge, skyHeight, zFar, zcFarGround, speed,
    laneK: cfg.U * aspect * tanHalfPhi,
    groundNear: gbNear, skyNear: zJudge,
  };
}

export function laneX(cfg, d, u, layerF, z) {
  const a = (cfg.yCam - layerF * d.skyHeight) * d.sinTheta;
  const zc = a + z * d.cosTheta;
  const zcJudge = a + d.zJudge * d.cosTheta;
  const zcFar = a + d.zFar * d.cosTheta;
  const c = Math.min(1, Math.max(0, cfg.laneConverge * (zcFar / d.zcFarGround)));
  return u * d.laneK * (zc + (zcJudge - zc) * c);
}

// NotePlacement.hlsl の層ごとの奥行き再マップ（空中ノーツの進み具合を地上に揃える）
function vAt(d, h, depth) {
  return Math.tan(d.theta - Math.atan(h / depth)) / d.tanHalfPhi;
}
function depthFromV(d, h, v) {
  return h / Math.tan(d.theta - Math.atan(v * d.tanHalfPhi));
}
export function remapDepth(cfg, d, d0, layerF) {
  if (layerF < 1e-6 || d0 <= d.zJudge || d0 >= d.zFar) return d0;
  const hL = cfg.yCam - layerF * d.skyHeight;
  const vgj = vAt(d, cfg.yCam, d.zJudge), vgf = vAt(d, cfg.yCam, d.zFar);
  const pg = (vAt(d, cfg.yCam, d0) - vgj) / (vgf - vgj);
  const vj = vAt(d, hL, d.zJudge), vf = vAt(d, hL, d.zFar);
  return depthFromV(d, hL, vj + pg * (vf - vj));
}

/**
 * ノーツ1個の配置。モデルは「地上・判定線上」の寸法で作られている前提で、
 * 現在の奥行き・層に合わせた中心位置とスケールを返す。
 *   sx: 横（と高さ）の倍率。空中はカメラに近いぶんワールド上は小さくなる
 *   sz: 奥行き方向の倍率。厚みの下限（点滅防止）＋空中の厚み係数＋奥行き再マップの伸縮
 */
export function placeNote(cfg, d, note, songTime, out) {
  const d0 = d.zJudge + (note.t - songTime) * d.speed;
  const L = note.layer;
  const htNom = d.zJudge * cfg.thicknessFrac;
  const ht = Math.max(htNom, d0 * cfg.thicknessMinFrac) * (1 + (cfg.skyThicknessMul - 1) * L);
  const dn = remapDepth(cfg, d, d0 - ht, L);
  const df = remapDepth(cfg, d, d0 + ht, L);
  const depth = (dn + df) * 0.5;
  const uL = -1 + (2 * (note.cell + cfg.gapCells)) / cfg.cells;
  const uR = -1 + (2 * (note.cell + note.width - cfg.gapCells)) / cfg.cells;
  const xL = laneX(cfg, d, uL, L, depth), xR = laneX(cfg, d, uR, L, depth);
  const wNom = laneX(cfg, d, uR, 0, d.zJudge) - laneX(cfg, d, uL, 0, d.zJudge);
  out.x = (xL + xR) * 0.5;
  out.y = L * d.skyHeight;
  out.z = -depth;
  out.sx = (xR - xL) / wNom;
  out.sz = (df - dn) / (2 * htNom);
  out.d0 = d0;
  out.depth = depth;
  // 0=判定線, 1=最遠端（地上の画面上の進み具合）
  out.progress = (d0 - d.zJudge) / (d.zFar - d.zJudge);
  return out;
}

/** 判定線上・地上での寸法（variant の makeNote に渡す） */
export function nominalDims(cfg, d, widthCells) {
  const u0 = -1, u1 = -1 + (2 * (widthCells - 2 * cfg.gapCells)) / cfg.cells;
  return {
    wWorld: laneX(cfg, d, u1, 0, d.zJudge) - laneX(cfg, d, u0, 0, d.zJudge),
    halfT: d.zJudge * cfg.thicknessFrac,
    cellWorld: laneX(cfg, d, 2 / cfg.cells - 1, 0, d.zJudge) - laneX(cfg, d, -1, 0, d.zJudge),
  };
}

function stageMaterial(color, alpha, near, far) {
  return new THREE.ShaderMaterial({
    uniforms: {
      uColor: { value: new THREE.Color(color) },
      uAlpha: { value: alpha }, uNear: { value: near }, uFar: { value: far },
    },
    vertexShader: /* glsl */ `
      varying float vDepth;
      void main() {
        vec4 wp = modelMatrix * vec4(position, 1.0);
        vDepth = -wp.z;
        gl_Position = projectionMatrix * viewMatrix * wp;
      }`,
    fragmentShader: /* glsl */ `
      uniform vec3 uColor; uniform float uAlpha, uNear, uFar;
      varying float vDepth;
      void main() {
        if (vDepth > uFar || vDepth < uNear) discard;
        gl_FragColor = vec4(uColor, uAlpha);
        #include <colorspace_fragment>
      }`,
    transparent: true, depthWrite: false, side: THREE.DoubleSide,
  });
}

export function buildStage(cfg, d) {
  const root = new THREE.Group();
  const layers = [
    { y: 0, L: 0, color: COLORS.ground, grid: COLORS.gridGround, near: d.groundNear, alpha: cfg.groundFillAlpha, step: cfg.laneLineStepGround },
    { y: d.skyHeight, L: 1, color: COLORS.sky, grid: COLORS.gridSky, near: d.skyNear, alpha: cfg.skyFillAlpha, step: cfg.laneLineStepSky },
  ];
  for (const Ly of layers) {
    const n0 = Math.max(d.zJudge * 0.02, Ly.near), f0 = d.zFar;
    const xAt = (u, z) => laneX(cfg, d, u, Ly.L, z);
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute([
      xAt(-1, n0), Ly.y, -n0, xAt(1, n0), Ly.y, -n0, xAt(1, f0), Ly.y, -f0, xAt(-1, f0), Ly.y, -f0,
    ], 3));
    g.setIndex([0, 1, 2, 0, 2, 3]);
    const plane = new THREE.Mesh(g, stageMaterial(Ly.grid, Ly.alpha, n0, f0));
    plane.renderOrder = Ly.L === 0 ? -20 : -10;
    root.add(plane);

    const pts = [];
    for (let k = 0; k <= cfg.cells; k++) {
      if (k % Ly.step !== 0 && k !== cfg.cells) continue;
      const u = -1 + (2 * k) / cfg.cells;
      pts.push(xAt(u, n0), Ly.y, -n0, xAt(u, f0), Ly.y, -f0);
    }
    pts.push(xAt(-1, f0), Ly.y, -f0, xAt(1, f0), Ly.y, -f0);
    const lg = new THREE.BufferGeometry();
    lg.setAttribute('position', new THREE.Float32BufferAttribute(pts, 3));
    const lines = new THREE.LineSegments(lg, stageMaterial(Ly.color, 0.45, n0, f0));
    lines.renderOrder = Ly.L === 0 ? -19 : -9;
    root.add(lines);

    // 判定線（細い帯）
    const jw = d.zJudge * 0.012;
    const jg = new THREE.PlaneGeometry(1, 1);
    const judge = new THREE.Mesh(jg, new THREE.MeshBasicMaterial({
      color: Ly.color, transparent: true, opacity: 0.9, depthWrite: false,
    }));
    judge.rotation.x = -Math.PI / 2;
    judge.scale.set(xAt(1, d.zJudge) - xAt(-1, d.zJudge), jw * (Ly.L ? 0.5 : 1), 1);
    judge.position.set(0, Ly.y + 0.001, -d.zJudge);
    judge.renderOrder = Ly.L === 0 ? -18 : -8;
    root.add(judge);
  }
  return root;
}
