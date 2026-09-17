// Player movement integration — THE shared code path for server sim and
// client own-ship prediction. If these ever diverge, prediction rubber-bands.

import { ARENA_W, ARENA_H, WALL_PAD, PLAYER, clamp } from "./constants.js";

// p: {x,y,vx,vy,dashT}  input: {mx,my (-1..1), dash:boolean}  stats: {speed}
export function stepPlayerMovement(p, input, stats, dt) {
  const speed = PLAYER.SPEED * (stats?.speed ?? 1);
  if (p.dashT > 0) {
    p.dashT = Math.max(0, p.dashT - dt);
    // dash keeps its launch velocity; no steering mid-dash
  } else {
    const len = Math.hypot(input.mx, input.my) || 1;
    const nx = len > 1 ? input.mx / len : input.mx;
    const ny = len > 1 ? input.my / len : input.my;
    const tvx = nx * speed, tvy = ny * speed;
    const ax = (Math.abs(nx) > 0.01 || Math.abs(ny) > 0.01) ? PLAYER.ACCEL : PLAYER.FRICTION;
    p.vx = approach(p.vx, tvx, ax * dt);
    p.vy = approach(p.vy, tvy, ax * dt);
  }
  p.x = clamp(p.x + p.vx * dt, WALL_PAD, ARENA_W - WALL_PAD);
  p.y = clamp(p.y + p.vy * dt, WALL_PAD, ARENA_H - WALL_PAD);
}

export function startDash(p, input) {
  let dx = input.mx, dy = input.my;
  if (Math.abs(dx) < 0.01 && Math.abs(dy) < 0.01) { dx = Math.cos(p.aim ?? 0); dy = Math.sin(p.aim ?? 0); }
  const l = Math.hypot(dx, dy) || 1;
  p.vx = (dx / l) * PLAYER.DASH_SPEED;
  p.vy = (dy / l) * PLAYER.DASH_SPEED;
  p.dashT = PLAYER.DASH_TIME;
}

function approach(v, target, step) {
  if (v < target) return Math.min(target, v + step);
  if (v > target) return Math.max(target, v - step);
  return v;
}
