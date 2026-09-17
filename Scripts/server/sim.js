// The authoritative simulation (SDD §3.3). One Sim per room. Everything
// that matters — health, score, multiplier, collisions, drafts — resolves
// here at 30 Hz. Clients only ever render what this file decides.

import {
  ARENA_W, ARENA_H, WALL_PAD, PLAYER, MULT, WAVE, PHASE, PS, EK, ZK,
  PILOTS, REVIVE_COST_PER_WAVE, TICK_DT, ORBITAL, PICKUP,
  AURA, TURRET, PYLON, TEAM_CORE_SHARE, ENEMY_HP_PER_WAVE, clamp,
} from "../shared/constants.js";
import { shopFor, shopItemById } from "../shared/shop.js";
import { CK, rollConsumable } from "../shared/consumables.js";
import { stepPlayerMovement, startDash } from "../shared/movement.js";
import { spawnPattern, PT } from "../shared/patterns.js";
import { ENEMIES } from "../shared/enemies.js";
import { computeStats, draftOffer, modById, classModsFor } from "../shared/mods.js";
import { mulberry32 } from "../shared/rng.js";
import { BTN, PF, EF } from "../shared/protocol.js";
import { makeWave, bossHp } from "./waves.js";

const PATTERN_IDS = { RING: PT.RING, FAN: PT.FAN, SPOKES: PT.SPOKES, ORB: PT.ORB };
const E_BULLET_CAP = 1200;

export class Sim {
  constructor() {
    // All non-wave randomness flows through these two streams so a run can
    // be replayed bit-for-bit (fixture generation, cross-runtime parity).
    // Live rooms leave them as Math.random and behave exactly as before.
    this.rng = Math.random;
    this.rngDrop = Math.random;   // injectable for tests
    this.phase = PHASE.LOBBY;
    this.tick = 0;
    this.wave = 0;
    this.phaseT = 0;
    this.players = new Map();
    this.enemies = new Map();
    this.pBullets = [];
    this.eBullets = [];
    this.zones = [];
    this.events = [];
    this.script = null;
    this.scriptT = 0;
    this.pending = [];
    this.mult = 1;
    this.sinceKill = 999;
    this.unbanked = 0;
    this.banked = 0;
    this.bestMult = 1;
    this.eid = 1;
    this.bid = 1;
    this.offers = new Map();
    this.dailySeed = null;   // set for Daily Dark / challenge rooms — waves become deterministic
    this.runSeed = (this.rng() * 0xffffffff) >>> 0;
    this._wardens = [];
    this.pickups = new Map();
    this.pkid = 1;
    this.stasisT = 0;
    this.turrets = [];          // RIGG's deployables (visuals ride ZK.TURRET zones)
    this.shopOpen = false;      // post-boss intermissions open the Core Shop
  }

  // Pin every random stream from one seed. Used by the fixture generator
  // and the C# parity harness; live rooms never call this.
  seedAll(seed) {
    this.rng = mulberry32(seed >>> 0);
    this.rngDrop = mulberry32((seed ^ 0x9e3779b9) >>> 0);
  }

  hpMax(p) { return Math.max(1, PLAYER.MAX_HP + (p.stats.maxHp | 0)); }

  // ---------- roster ----------
  addPlayer(id, name, pilot) {
    pilot = clamp(pilot | 0, 0, PILOTS.length - 1);
    const p = {
      id, name, pilot,
      weapon: PILOTS[pilot].weapon,
      x: ARENA_W / 2 + (id - 2) * 60, y: ARENA_H / 2, vx: 0, vy: 0,
      aim: 0, hp: PLAYER.MAX_HP, state: this.phase === PHASE.WAVE ? PS.SPECTATING : PS.ALIVE,
      mods: [], stats: computeStats(PILOTS[pilot], []),
      cores: 0, coreShare: 0, beacon: null, auraP: 0, shopDone: true,
      dashT: 0, dashCd: 0, dashStock: 1, fireCd: 0, abilCd: 0,
      iframesT: 0, bombs: PLAYER.START_BOMBS, lives: PLAYER.SOLO_LIVES,
      downedT: 0, reviveP: 0, respawnT: 0, beingRevived: false,
      dashDmgT: 0, rageT: 0, staticT: 2.2, lastCause: "",
      wrapT: 0, charge: null, // BROODMOTHER web / THADDIUS polarity
      cons: [], frenzyT: 0,
      buttonsPrev: 0, input: { seq: 0, mx: 0, my: 0, ax: 0, ay: 0, buttons: 0 },
      kills: 0, picked: true,
    };
    p.hp = this.hpMax(p); // DAVE starts with his tank HP
    this.players.set(id, p);
    return p;
  }
  removePlayer(id) {
    this.players.delete(id);
    // The squad may have been waiting only on the player who just left.
    if (this.phase === PHASE.INTERMISSION && this.players.size > 0) this.maybeShortenIntermission();
  }

  activeCount() {
    let n = 0;
    for (const p of this.players.values()) if (p.state !== PS.SPECTATING) n++;
    return n;
  }
  isSolo() { return this.players.size === 1; }

  // ---------- run control ----------
  startRun() {
    this.runSeed = this.dailySeed ?? ((this.rng() * 0xffffffff) >>> 0);
    this.wave = 0;
    this.mult = 1; this.bestMult = 1; this.sinceKill = 999;
    this.unbanked = 0; this.banked = 0;
    this.enemies.clear(); this.pBullets.length = 0; this.eBullets.length = 0;
    this.zones.length = 0; this.pending.length = 0;
    this.pickups.clear(); this.stasisT = 0;
    let i = 0;
    for (const p of this.players.values()) {
      p.state = PS.ALIVE; p.mods = [];
      p.stats = computeStats(PILOTS[p.pilot], []);
      p.hp = this.hpMax(p);
      p.bombs = PLAYER.START_BOMBS; p.lives = PLAYER.SOLO_LIVES; p.kills = 0;
      p.x = ARENA_W / 2 + (i++ - this.players.size / 2) * 70; p.y = ARENA_H / 2;
      p.vx = p.vy = 0; p.dashCd = 0; p.dashStock = p.stats.dashCharges; p.abilCd = 0; p.iframesT = 2;
      p.dashDmgT = 0; p.rageT = 0; p.cons = []; p.frenzyT = 0;
      p.wrapT = 0; p.charge = null;
      p.cores = 0; p.coreShare = 0; p.beacon = null; p.auraP = 0; p.shopDone = true;
      p.weapon = PILOTS[p.pilot].weapon; // pilot may have changed in the lobby
    }
    this.turrets.length = 0;
    this.shopOpen = false;
    this.startWave(1);
  }

  waveRng(n) {
    // every run is seeded so any finished run can be re-issued as a
    // challenge with identical wave recipes; Daily Dark pins the seed
    return mulberry32(((this.runSeed >>> 0) ^ Math.imul(n, 2654435761)) >>> 0);
  }

  startWave(n) {
    this.wave = n;
    this.phase = PHASE.WAVE;
    this.shopOpen = false;
    this.script = makeWave(n, Math.max(1, this.activeCount()), this.waveRng(n));
    this.scriptT = 0;
    this.offers.clear();
    for (const p of this.players.values()) {
      if (p.state === PS.OUT || p.state === PS.SPECTATING || p.state === PS.DOWNED) {
        p.state = PS.ALIVE; p.hp = Math.min(2, this.hpMax(p)); p.iframesT = 2;
        p.x = ARENA_W / 2; p.y = ARENA_H / 2;
      }
      p.charge = null; p.wrapT = 0; // boss mechanics never leak into the next wave
      p.picked = true;
    }
    if (this.script.boss) {
      this.spawnEnemy(this.script.boss, ARENA_W / 2, ARENA_H * 0.25, true);
      this.emit({ t: "boss", kind: this.script.boss, name: ENEMIES[this.script.boss].name });
    }
    this.emit({ t: "wave_start", wave: n });
  }

  beginIntermission() {
    this.phase = PHASE.INTERMISSION;
    const postBoss = this.wave % WAVE.BOSS_EVERY === 0;
    this.shopOpen = postBoss;                       // the Core Shop opens after bosses
    this.phaseT = postBoss ? WAVE.SHOP_WAIT_S : WAVE.INTERMISSION_S;
    this.eBullets.length = 0;
    if (postBoss) {
      for (const p of this.players.values()) {
        if (p.state === PS.ALIVE) p.hp = Math.min(this.hpMax(p), p.hp + 1);
        p.bombs = Math.min(PLAYER.MAX_BOMBS, p.bombs + 1);
      }
    }
    for (const p of this.players.values()) {
      if (p.state === PS.SPECTATING) { p.shopDone = true; continue; }
      // wave-clear stipend BEFORE offers, so the storefront shows real balance
      p.cores = Math.min(65535, p.cores + 5 + this.wave);
      p.shopDone = !this.shopOpen;
      if (this.shopOpen) {
        this.emit({
          t: "shop_offer", to: p.id,
          items: shopFor(p.pilot).map(it => it.id),
          cores: p.cores, wave: this.wave,
        });
      }
      // free random pilot-signature upgrade, on top of the draft (stacks)
      const pool = classModsFor(p.pilot);
      if (pool.length) {
        const grant = pool[Math.floor(this.rng() * pool.length)];
        p.mods.push(grant.id);
        p.stats = computeStats(PILOTS[p.pilot], p.mods);
        p.hp = Math.max(1, Math.min(this.hpMax(p), p.hp));
        this.emit({ t: "class_grant", to: p.id, mod: grant.id, name: grant.name, desc: grant.desc });
      }
      p.picked = false;
      const offer = draftOffer(this.rng, this.wave);
      this.offers.set(p.id, offer);
      this.emit({ t: "draft_offer", to: p.id, offer, wave: this.wave });
    }
    this.emit({ t: "intermission", wave: this.wave, seconds: this.phaseT });
  }

  // ---------- actions from clients ----------
  action(p, a) {
    if (a.t === "start" && this.phase === PHASE.LOBBY) { this.startRun(); return; }
    if (a.t === "again" && (this.phase === PHASE.GAMEOVER || this.phase === PHASE.VICTORY)) { this.startRun(); return; }
    if (a.t === "bank" && this.phase === PHASE.INTERMISSION && this.unbanked > 0) {
      const bonus = Math.round(this.unbanked * 0.05 * (p.stats.interest || 0));
      this.banked += this.unbanked + bonus;
      this.emit({ t: "bank", amount: this.unbanked, bonus, by: p.id });
      this.unbanked = 0;
      return;
    }
    if (a.t === "pick" && this.phase === PHASE.INTERMISSION && !p.picked) {
      const offer = this.offers.get(p.id) ?? [];
      if (offer.includes(a.id)) {
        p.mods.push(a.id);
        p.stats = computeStats(PILOTS[p.pilot], p.mods);
        const mod = modById(a.id);
        if (mod?.heal) p.hp += mod.heal;
        p.hp = Math.max(1, Math.min(this.hpMax(p), p.hp));
        p.dashStock = Math.min(p.stats.dashCharges, p.dashStock + (p.stats.dashCharges > 1 ? 1 : 0));
        p.picked = true;
        this.emit({ t: "picked", who: p.id, mod: a.id });
        this.maybeShortenIntermission();
      }
      return;
    }
    // ---- Core Shop (post-boss intermissions only) ----
    if (a.t === "buy" && this.phase === PHASE.INTERMISSION && this.shopOpen) {
      const item = shopItemById(a.id);
      if (!item) return;
      if (item.pilot !== null && item.pilot !== p.pilot) {
        this.emit({ t: "shop_err", to: p.id, why: "wrong_class" }); return;
      }
      if (item.once && p.mods.includes(item.id)) {
        this.emit({ t: "shop_err", to: p.id, why: "owned" }); return;
      }
      // Rank pricing: the Nth copy of a stackable costs N× base (its effect
      // also diminishes to 1/N via computeStats). `flat` consumables exempt.
      const rank = item.once || item.flat ? 1 : p.mods.filter(m => m === item.id).length + 1;
      const price = item.price * rank;
      if (p.cores < price) {
        this.emit({ t: "shop_err", to: p.id, why: "poor" }); return;
      }
      p.cores -= price;
      p.mods.push(item.id);
      p.stats = computeStats(PILOTS[p.pilot], p.mods);
      if (item.heal) p.hp += item.heal;
      if (item.bombs) p.bombs = Math.min(PLAYER.MAX_BOMBS, p.bombs + item.bombs);
      p.hp = Math.max(1, Math.min(this.hpMax(p), p.hp));
      this.emit({ t: "bought", who: p.id, mod: item.id, cores: p.cores, rank, price });
      return;
    }
    if (a.t === "shop_done" && this.phase === PHASE.INTERMISSION) {
      p.shopDone = true;
      this.maybeShortenIntermission();
    }
  }

  maybeShortenIntermission() {
    for (const q of this.players.values()) {
      if (q.state !== PS.SPECTATING && (!q.picked || !q.shopDone)) return;
    }
    if (this.phaseT <= WAVE.READY_COUNTDOWN_S) return;
    this.phaseT = WAVE.READY_COUNTDOWN_S;
    // Clients size the timer bar from `seconds`; without this the countdown
    // would be drawn as 3s of a 120s bar, which is nothing at all.
    this.emit({ t: "intermission", wave: this.wave, seconds: this.phaseT, ready: true });
  }

  // The killer is paid in full and every teammate still in the run gets
  // TEAM_CORE_SHARE of it, carried fractionally so a 1-core kill still counts.
  payCores(killer, value) {
    if (!(value > 0)) return;
    killer.cores = Math.min(65535, killer.cores + value);
    for (const q of this.players.values()) {
      if (q === killer || q.state === PS.SPECTATING) continue;
      q.coreShare = (q.coreShare || 0) + value * TEAM_CORE_SHARE;
      const whole = Math.floor(q.coreShare + 1e-9);
      if (whole > 0) {
        q.coreShare -= whole;
        q.cores = Math.min(65535, q.cores + whole);
      }
    }
  }

  // ---------- main step ----------
  step(dt) {
    this.tick++;
    if (this.phase === PHASE.LOBBY || this.phase === PHASE.GAMEOVER || this.phase === PHASE.VICTORY) {
      this.stepPlayers(dt, false);
      return;
    }
    if (this.phase === PHASE.INTERMISSION) {
      this.stepPlayers(dt, false);
      this.stepZones(dt);
      this.stepPickups(dt); // leftovers stay collectable between waves
      this.phaseT -= dt;
      if (this.phaseT <= 0) {
        for (const p of this.players.values()) {
          if (p.state !== PS.SPECTATING && !p.picked) {
            const offer = this.offers.get(p.id) ?? [];
            if (offer.length) this.action(p, { t: "pick", id: offer[Math.floor(this.rng() * offer.length)] });
          }
        }
        this.startWave(this.wave + 1); // endless — the dark always has more
      }
      return;
    }
    // PHASE.WAVE
    this.stasisT = Math.max(0, this.stasisT - dt);
    this.stepPlayers(dt, true);
    this.stepSpawner(dt);
    this.stepEnemies(dt);
    this.stepEnemyBullets(dt);
    this.stepPlayerBullets(dt);
    this.stepOrbitals(dt);
    this.stepTurrets(dt);
    this.stepZones(dt);
    this.stepPickups(dt);
    this.stepReviveAndRespawn(dt);
    this.sinceKill += dt;
    if (this.sinceKill > MULT.DECAY_GRACE && this.mult > 1) {
      this.mult = Math.max(1, this.mult - MULT.DECAY_PER_S * dt);
    }
    if (this.scriptDone() && this.enemies.size === 0 && this.pending.length === 0) {
      this.emit({ t: "wave_end", wave: this.wave });
      this.beginIntermission(); // endless — no victory wave
      return;
    }
    let anyAlive = false;
    for (const p of this.players.values()) if (p.state === PS.ALIVE) anyAlive = true;
    if (!anyAlive && this.activeCount() > 0) this.endRun(false);
  }

  endRun(victory) {
    this.phase = victory ? PHASE.VICTORY : PHASE.GAMEOVER;
    const lost = victory ? 0 : this.unbanked;
    if (victory) { this.banked += this.unbanked; this.unbanked = 0; }
    const roster = [...this.players.values()].map(p => ({ id: p.id, name: p.name, kills: p.kills }));
    this.emit({
      t: victory ? "victory" : "gameover",
      score: this.banked, lost, wave: this.wave, bestMult: Math.round(this.bestMult * 10) / 10,
      roster, daily: this.dailySeed != null, seed: this.runSeed >>> 0,
    });
  }

  // ---------- players ----------
  dashCooldown(p) {
    return Math.max(0.6, PLAYER.DASH_CD + (p.stats.dashPenalty ?? 0) - (p.stats.dashBonus ?? 0));
  }

  stepPlayers(dt, combat) {
    for (const p of this.players.values()) {
      if (p.state !== PS.ALIVE) continue;
      if (p.wrapT > 0) { // BROODMOTHER's web: rooted and silenced, but the
        p.wrapT -= dt;   // cocoon shields — the threat is the lost DPS
        p.vx = p.vy = 0;
        p.iframesT = Math.max(p.iframesT, 0.3);
        if (p.wrapT <= 0) this.freeWrap(p);
        continue;
      }
      const inp = p.input;
      const btn = inp.buttons, prev = p.buttonsPrev;
      if (Math.hypot(inp.ax, inp.ay) > 0.25) p.aim = Math.atan2(inp.ay, inp.ax);
      // dash charges: cooldown refills the stock (SDD §2.2 + Twin Dash mod)
      if (p.dashStock < p.stats.dashCharges) {
        p.dashCd -= dt;
        if (p.dashCd <= 0) {
          p.dashStock++;
          p.dashCd = p.dashStock < p.stats.dashCharges ? this.dashCooldown(p) : 0;
        }
      }
      if ((btn & BTN.DASH) && !(prev & BTN.DASH) && p.dashStock > 0 && p.dashT <= 0) {
        startDash(p, { mx: inp.mx, my: inp.my });
        if (p.dashStock === p.stats.dashCharges) p.dashCd = this.dashCooldown(p);
        p.dashStock--;
        p.iframesT = Math.max(p.iframesT, PLAYER.DASH_IFRAMES);
        p.dashDmgT = 1;
        if (p.stats.nova > 0 && combat) this.novaBurst(p);
        if (p.stats.napalm > 0 && combat) { // EMBER's Napalm Trail class mod
          this.zones.push({ kind: ZK.FLAME, x: p.x, y: p.y, r: 45, ttl: 2 * p.stats.napalm, owner: p.id });
        }
        this.emit({ t: "dash", who: p.id });
      }
      stepPlayerMovement(p, { mx: inp.mx, my: inp.my }, p.stats, dt);
      p.iframesT = Math.max(0, p.iframesT - dt);
      p.fireCd = Math.max(0, p.fireCd - dt);
      p.abilCd = Math.max(0, p.abilCd - dt);
      p.dashDmgT = Math.max(0, p.dashDmgT - dt);
      p.rageT = Math.max(0, p.rageT - dt);
      p.frenzyT = Math.max(0, p.frenzyT - dt);
      if ((btn & BTN.FIRE) && p.fireCd <= 0) {
        this.fire(p);
        const rate = p.stats.fire * (p.rageT > 0 ? 1 + 0.5 * p.stats.rage : 1) * (p.frenzyT > 0 ? 2 : 1);
        p.fireCd = p.weapon.cd / rate; // the class weapon owns the cadence
      }
      // use consumable (edge) — works in intermission too, hence outside combat
      if ((btn & BTN.USE) && !(prev & BTN.USE) && p.cons.length > 0) {
        const kind = p.cons.shift();
        this.applyConsumable(p, kind);
        this.emit({ t: "consumed", who: p.id, kind });
      }
      if (combat) {
        if ((btn & BTN.BOMB) && !(prev & BTN.BOMB) && p.bombs > 0) this.smartBomb(p);
        if ((btn & BTN.ABILITY) && !(prev & BTN.ABILITY) && p.abilCd <= 0) this.ability(p);
        // Static Coil: periodic zap on the nearest enemy
        if (p.stats.static > 0) {
          p.staticT -= dt;
          if (p.staticT <= 0) {
            p.staticT = 2.2;
            let tgt = null, td = 180;
            for (const e of this.enemies.values()) {
              const d = Math.hypot(e.x - p.x, e.y - p.y);
              if (d < td) { td = d; tgt = e; }
            }
            if (tgt) {
              this.zones.push({ kind: ZK.BLAST, x: tgt.x, y: tgt.y, r: 26, ttl: 0.2 });
              this.damageEnemy(tgt, 2 * p.stats.static, p);
            }
          }
        }
      }
      p.buttonsPrev = btn;
    }
    this.stepAuras(dt, combat);
  }

  // AMBER's healing aura — moves with her, works between waves too.
  stepAuras(dt, combat) {
    for (const amber of this.players.values()) {
      if (amber.pilot !== 2 || amber.state !== PS.ALIVE) continue;
      const s = amber.stats;
      const r = AURA.R * s.auraR;
      for (const q of this.players.values()) {
        if (q.state !== PS.ALIVE) continue;
        if (Math.hypot(q.x - amber.x, q.y - amber.y) > r) continue;
        const period = q === amber && !s.selfAura ? AURA.SELF_S : AURA.ALLY_S;
        q.auraP += (dt / period) * s.auraRate;
        if (q.auraP >= 1) {
          q.auraP = 0;
          if (q.hp < this.hpMax(q)) {
            q.hp++;
            this.emit({ t: "aura_heal", who: q.id, by: amber.id });
          }
        }
      }
      if (combat && s.radiantAura > 0) { // shop: the light burns
        for (const e of this.enemies.values()) {
          if (Math.hypot(e.x - amber.x, e.y - amber.y) < r) this.damageEnemy(e, 1 * s.radiantAura * dt, amber);
        }
      }
    }
  }

  // RIGG's auto-turrets: real aimbots, owner-credited bullets.
  stepTurrets(dt) {
    for (let i = this.turrets.length - 1; i >= 0; i--) {
      const t = this.turrets[i];
      t.ttl -= dt;
      if (t.ttl <= 0) { this.turrets.splice(i, 1); continue; }
      const owner = this.players.get(t.owner);
      if (!owner) { this.turrets.splice(i, 1); continue; }
      t.fireCd -= dt;
      if (t.fireCd > 0) continue;
      let tgt = null, td = TURRET.RANGE;
      for (const e of this.enemies.values()) {
        if (e.phased) continue;
        const d = Math.hypot(e.x - t.x, e.y - t.y);
        if (d < td) { td = d; tgt = e; }
      }
      if (!tgt) continue;
      t.fireCd = TURRET.CD / owner.stats.turretFireMul;
      const a = Math.atan2(tgt.y - t.y, tgt.x - t.x);
      this.pBullets.push({
        id: this.bid = (this.bid % 65000) + 1,
        x: t.x + Math.cos(a) * 14, y: t.y + Math.sin(a) * 14,
        vx: Math.cos(a) * 640, vy: Math.sin(a) * 640,
        dmg: TURRET.DMG * owner.stats.dmg * owner.stats.turretDmgMul,
        pierce: 0, ricochet: 0, life: 1.2, owner: t.owner,
        chill: 0, chain: 0, burn: 0,
      });
    }
  }

  // Class-weapon dispatch (SDD §2.2/§2.8): every class attacks differently.
  fire(p) {
    const w = p.weapon, s = p.stats;
    const dmgMul = p.dashDmgT > 0 && s.dashDmg > 0 ? 1 + 0.3 * s.dashDmg : 1;
    if (w.kind === "cleave") { this.cleave(p, dmgMul); return; }

    let count = 1 + s.split;
    let spread = count > 1 ? 0.14 : 0;      // split-mod fan spacing
    let dmg = w.dmg * s.dmg * dmgMul;
    let lifeMul = 1;
    if (w.kind === "shotgun") {
      if (s.slugShot) { count = 1; spread = 0; dmg = 5 * s.dmg * dmgMul; lifeMul = 2.5; }
      else {
        count = w.pellets + s.split;
        spread = (w.spread * s.chokeMul * 2) / Math.max(1, count - 1);
      }
    }
    const jitter = (w.jitter ?? 0) * s.jitterMul;
    const speed = PLAYER.BULLET_SPEED * (w.speedMul ?? 1) * s.bulletSpeed;
    const life = (w.life ?? PLAYER.BULLET_LIFE) * s.bulletLife * lifeMul;
    const angles = [];
    for (let i = 0; i < count; i++) {
      const off = count === 1 ? 0 : (i - (count - 1) / 2) * spread;
      const a = p.aim + off + (jitter ? (this.rng() - 0.5) * 2 * jitter : 0);
      angles.push(a);
      this.pBullets.push({
        id: this.bid = (this.bid % 65000) + 1,
        x: p.x + Math.cos(a) * 18, y: p.y + Math.sin(a) * 18,
        vx: Math.cos(a) * speed, vy: Math.sin(a) * speed,
        dmg, pierce: (w.pierce ?? 0) + s.pierce, ricochet: s.ricochet,
        life, owner: p.id,
        chill: w.chill ? w.chill * s.chillDurMul : 0,
        chain: w.chain ? w.chain + s.chainHops : 0, chainR: w.chainR ?? 140,
        burn: w.kind === "shotgun" ? s.burn : 0,
      });
    }
    // Rails outrun the 15Hz snapshots — one can spawn and die between two of
    // them and never reach a client. The spawn event is the visual contract:
    // clients integrate the tracer themselves (like pattern bullets).
    if (w.kind === "rail") this.railEvent(p, angles, speed, life, s.ricochet);
  }

  railEvent(p, angles, speed, life, rc, ability) {
    const ev = {
      t: "rail", who: p.id, x: Math.round(p.x), y: Math.round(p.y),
      a: angles.map(v => Math.round(v * 1000) / 1000),
      sp: Math.round(speed), tl: Math.round(life * 100) / 100, rc,
    };
    if (ability) ev.ab = 1; // Triple Rail — never client-predicted, never deduped
    this.emit(ev);
  }

  // DAVE's melee arc — no projectile; draft dmg/fire/Echo mods still apply.
  cleave(p, dmgMul) {
    const w = p.weapon, s = p.stats;
    const dmg = w.dmg * s.dmg * dmgMul;
    const full = s.cleave360 > 0;
    const arcHalf = full ? Math.PI : Math.min(Math.PI, w.arcHalf * s.cleaveArcMul);
    for (const e of this.enemies.values()) {
      if (e.phased) continue;
      const d = Math.hypot(e.x - p.x, e.y - p.y);
      if (d > w.arcR + e.def.radius) continue;
      if (!full) {
        let da = Math.atan2(e.y - p.y, e.x - p.x) - p.aim;
        da = Math.atan2(Math.sin(da), Math.cos(da)); // wrap to [-π, π]
        if (Math.abs(da) > arcHalf) continue;
      }
      const nx = (e.x - p.x) / (d || 1), ny = (e.y - p.y) / (d || 1);
      const died = this.damageEnemy(e, dmg, p);
      if (!died && this.enemies.has(e.id)) {
        e.x = clamp(e.x + nx * w.knockback, WALL_PAD, ARENA_W - WALL_PAD);
        e.y = clamp(e.y + ny * w.knockback, WALL_PAD, ARENA_H - WALL_PAD);
      }
    }
    if (s.aftershock > 0) { // shop: cleave launches a shockwave slug
      this.pBullets.push({
        id: this.bid = (this.bid % 65000) + 1,
        x: p.x + Math.cos(p.aim) * 20, y: p.y + Math.sin(p.aim) * 20,
        vx: Math.cos(p.aim) * 500, vy: Math.sin(p.aim) * 500,
        dmg: 2 * s.dmg, pierce: 2, ricochet: 0, life: 0.9, owner: p.id,
        chill: 0, chain: 0, burn: 0,
      });
    }
    this.emit({ t: "cleave", who: p.id, x: Math.round(p.x), y: Math.round(p.y), aim: Math.round(p.aim * 100) / 100, r: w.arcR, full });
  }

  smartBomb(p) {
    p.bombs--;
    this.eBullets.length = 0;
    for (const e of this.enemies.values()) {
      this.damageEnemy(e, (e.def.boss ? 4 : 2) + (p.stats.bombPower | 0), p);
    }
    this.emit({ t: "bomb", who: p.id, x: p.x, y: p.y });
  }

  ability(p) {
    const s = p.stats;
    let cd = Math.max(2, PILOTS[p.pilot].abilityCd - s.abilityCdr);
    const aim = p.aim;
    if (p.pilot === 0) { // BINK — Blink Volley
      if (s.blinkNova > 0) { // Echo Blink: detonate the departure point
        this.zones.push({ kind: ZK.BLAST, x: p.x, y: p.y, r: 100, ttl: 0.25 });
        for (const e of this.enemies.values()) {
          if (Math.hypot(e.x - p.x, e.y - p.y) < 100) this.damageEnemy(e, 2 * s.blinkNova, p);
        }
      }
      p.x = clamp(p.x + Math.cos(aim) * 180 * s.blinkDist, WALL_PAD, ARENA_W - WALL_PAD);
      p.y = clamp(p.y + Math.sin(aim) * 180 * s.blinkDist, WALL_PAD, ARENA_H - WALL_PAD);
      p.iframesT = Math.max(p.iframesT, 0.3 + s.blinkIframe);
      const shots = 12 + s.blinkShots;
      for (let i = 0; i < shots; i++) {
        const a = (i / shots) * Math.PI * 2;
        this.pBullets.push({
          id: this.bid = (this.bid % 65000) + 1,
          x: p.x, y: p.y, vx: Math.cos(a) * 560, vy: Math.sin(a) * 560,
          dmg: s.dmg, pierce: 0, ricochet: 0, life: 0.8, owner: p.id,
        });
      }
    } else if (p.pilot === 1) { // EMBER — Flame Zone
      this.zones.push({
        kind: ZK.FLAME, x: p.x + Math.cos(aim) * 120, y: p.y + Math.sin(aim) * 120,
        r: 90 * s.flameR, ttl: 4 * s.flameDur, owner: p.id,
      });
    } else if (p.pilot === 2) { // AMBER — Beacon Warp (two-phase)
      if (!p.beacon) { // phase 1: drop the beacon, tiny re-arm
        p.beacon = { x: p.x, y: p.y };
        this.zones.push({ kind: ZK.BEACON, x: p.x, y: p.y, r: 30, ttl: 25, owner: p.id, healT: 4 });
        cd = 0.25;
        this.emit({ t: "ability", who: p.id, pilot: p.pilot, phase: 1 });
        p.abilCd = cd;
        return;
      }
      // phase 2: warp home
      const fx = p.x, fy = p.y;
      p.x = p.beacon.x; p.y = p.beacon.y; p.vx = p.vy = 0;
      p.iframesT = Math.max(p.iframesT, 0.3);
      if (s.beaconBlast > 0) { // shop: detonate the departure point
        this.zones.push({ kind: ZK.BLAST, x: fx, y: fy, r: 120, ttl: 0.25 });
        for (const e of this.enemies.values()) {
          if (Math.hypot(e.x - fx, e.y - fy) < 120) this.damageEnemy(e, 3, p);
        }
      }
      p.beacon = null;
      this.zones = this.zones.filter(z => !(z.kind === ZK.BEACON && z.owner === p.id));
      this.emit({ t: "ability", who: p.id, pilot: p.pilot, phase: 2 });
      p.abilCd = cd;
      return;
    } else if (p.pilot === 3) { // DAVE — Gravity Well
      this.zones.push({
        kind: ZK.WELL, x: clamp(p.x + Math.cos(aim) * 250, WALL_PAD, ARENA_W - WALL_PAD),
        y: clamp(p.y + Math.sin(aim) * 250, WALL_PAD, ARENA_H - WALL_PAD),
        r: 130 * s.wellR, ttl: 2.5 * s.wellDur, owner: p.id,
      });
    } else if (p.pilot === 4) { // SPARKS — Tesla Pylon(s)
      const n = 1 + s.pylonCount;
      for (let i = 0; i < n; i++) {
        const a = (i / n) * Math.PI * 2;
        this.zones.push({
          kind: ZK.PYLON,
          x: clamp(p.x + (n > 1 ? Math.cos(a) * 70 : 0), WALL_PAD, ARENA_W - WALL_PAD),
          y: clamp(p.y + (n > 1 ? Math.sin(a) * 70 : 0), WALL_PAD, ARENA_H - WALL_PAD),
          r: PYLON.R, ttl: PYLON.TTL + s.pylonTtlBonus, owner: p.id, zapT: 0.3,
        });
      }
    } else if (p.pilot === 5) { // RIGG — Auto-Turret(s)
      const n = 1 + s.turretCount;
      for (let i = 0; i < n; i++) {
        const tx = clamp(p.x + (i - (n - 1) / 2) * 50, WALL_PAD, ARENA_W - WALL_PAD);
        const ty = clamp(p.y + 26, WALL_PAD, ARENA_H - WALL_PAD);
        const ttl = TURRET.TTL * s.turretTtlMul;
        this.turrets.push({ owner: p.id, x: tx, y: ty, ttl, fireCd: 0.3 });
        this.zones.push({ kind: ZK.TURRET, x: tx, y: ty, r: 16, ttl, owner: p.id });
      }
    } else if (p.pilot === 6) { // KELVIN — Frost Nova
      const r = 220 * s.frostRMul;
      this.zones.push({ kind: ZK.BLAST, x: p.x, y: p.y, r, ttl: 0.3 });
      for (const e of this.enemies.values()) {
        if (Math.hypot(e.x - p.x, e.y - p.y) < r) {
          e.stunT = Math.max(e.stunT, 1.5 + s.frostDurBonus);
          e.slowT = Math.max(e.slowT, 3);
          e.slowF = Math.min(e.slowF ?? 1, s.chillSlow);
        }
      }
    } else if (p.pilot === 7) { // HAWK — Triple Rail
      const shots = 3 + s.railShots;
      const w = p.weapon;
      const speed = PLAYER.BULLET_SPEED * (w.speedMul ?? 1) * s.bulletSpeed;
      const life = PLAYER.BULLET_LIFE * s.bulletLife;
      const angles = [];
      for (let i = 0; i < shots; i++) {
        const a = aim + (i - (shots - 1) / 2) * 0.105;
        angles.push(a);
        this.pBullets.push({
          id: this.bid = (this.bid % 65000) + 1,
          x: p.x + Math.cos(a) * 20, y: p.y + Math.sin(a) * 20,
          vx: Math.cos(a) * speed, vy: Math.sin(a) * speed,
          dmg: w.dmg * s.dmg, pierce: (w.pierce ?? 0) + s.pierce, ricochet: 0,
          life, owner: p.id,
          chill: 0, chain: 0, burn: 0,
        });
      }
      this.railEvent(p, angles, speed, life, 0, true);
    }
    p.abilCd = cd;
    this.emit({ t: "ability", who: p.id, pilot: p.pilot });
  }

  // ---------- consumables ----------
  applyConsumable(p, kind) {
    if (kind === CK.REPAIR) p.hp = Math.min(this.hpMax(p), p.hp + 1);
    else if (kind === CK.SHIELD) p.iframesT = Math.max(p.iframesT, 3);
    else if (kind === CK.FRENZY) p.frenzyT = 6;
    else if (kind === CK.STASIS) this.stasisT = Math.max(this.stasisT, 5);
    else if (kind === CK.BOMB) p.bombs = Math.min(PLAYER.MAX_BOMBS, p.bombs + 1);
  }

  spawnPickup(kind, x, y) {
    if (this.pickups.size >= PICKUP.MAX_LIVE) {
      const oldest = this.pickups.keys().next().value;
      this.pickups.delete(oldest);
    }
    const id = this.pkid = (this.pkid % 65000) + 1;
    this.pickups.set(id, {
      id, kind,
      x: clamp(x, WALL_PAD, ARENA_W - WALL_PAD),
      y: clamp(y, WALL_PAD, ARENA_H - WALL_PAD),
      ttl: PICKUP.TTL,
    });
  }

  stepPickups(dt) {
    for (const [id, k] of this.pickups) {
      k.ttl -= dt;
      if (k.ttl <= 0) { this.pickups.delete(id); continue; }
      for (const p of this.players.values()) {
        if (p.state !== PS.ALIVE) continue;
        if (p.cons.length >= PICKUP.MAX_CARRY) continue; // full — leave it for a teammate
        if (Math.hypot(p.x - k.x, p.y - k.y) < PLAYER.RADIUS + PICKUP.RADIUS) {
          p.cons.push(k.kind);
          this.pickups.delete(id);
          this.emit({ t: "pickup_got", who: p.id, kind: k.kind });
          break;
        }
      }
    }
  }

  novaBurst(p) {
    for (const e of this.enemies.values()) {
      const d = Math.hypot(e.x - p.x, e.y - p.y);
      if (d < 140) this.damageEnemy(e, p.stats.nova, p);
    }
    this.emit({ t: "nova", x: p.x, y: p.y });
  }

  applyPlayerHit(p, dmg, cause = "CONTACT") {
    if (p.state !== PS.ALIVE || p.iframesT > 0) return;
    let shielded = false;
    for (const z of this.zones) {
      if (z.kind === ZK.AEGIS && Math.hypot(p.x - z.x, p.y - z.y) < z.r) { shielded = true; break; }
    }
    if (!shielded) this.mult = Math.max(1, this.mult * p.stats.hitFactor);
    p.hp -= dmg;
    p.iframesT = PLAYER.HIT_IFRAMES * p.stats.iframeBonus;
    if (p.stats.rage > 0) p.rageT = 3;
    p.lastCause = cause;
    this.emit({ t: "hurt", who: p.id, hp: p.hp });
    if (p.hp <= 0) this.downPlayer(p);
  }

  downPlayer(p) {
    if (this.isSolo() || this.activeCount() === 1) {
      p.lives--;
      if (p.lives >= 0) {
        p.state = PS.DOWNED; p.respawnT = 2; p.downedT = 0;
        this.emit({ t: "downed", who: p.id, solo: true, lives: p.lives, cause: p.lastCause });
      } else {
        p.state = PS.OUT;
        this.emit({ t: "downed", who: p.id, solo: true, lives: 0, cause: p.lastCause });
      }
    } else {
      p.state = PS.DOWNED; p.downedT = PLAYER.DOWNED_TIMEOUT; p.reviveP = 0;
      this.emit({ t: "downed", who: p.id, solo: false, cause: p.lastCause });
    }
  }

  stepReviveAndRespawn(dt) {
    for (const p of this.players.values()) {
      if (p.state !== PS.DOWNED) continue;
      if (p.respawnT > 0) {
        p.respawnT -= dt;
        if (p.respawnT <= 0) {
          p.state = PS.ALIVE; p.hp = this.hpMax(p); p.iframesT = 2;
          p.x = ARENA_W / 2; p.y = ARENA_H / 2; p.vx = p.vy = 0;
          this.emit({ t: "revived", who: p.id, solo: true });
        }
        continue;
      }
      let reviver = null;
      for (const q of this.players.values()) {
        if (q.state === PS.ALIVE &&
            Math.hypot(q.x - p.x, q.y - p.y) < PLAYER.REVIVE_RANGE * q.stats.reviveRange) {
          if (!reviver || q.stats.reviveSpeed > reviver.stats.reviveSpeed) reviver = q;
        }
      }
      p.beingRevived = !!reviver;
      if (reviver) {
        p.reviveP += dt * reviver.stats.reviveSpeed / PLAYER.REVIVE_TIME;
        if (p.reviveP >= 1) {
          const cost = Math.round(Math.min(this.banked,
            REVIVE_COST_PER_WAVE * this.wave) * reviver.stats.reviveDiscount);
          this.banked -= cost;
          p.state = PS.ALIVE; p.hp = Math.min(2, this.hpMax(p)); p.iframesT = 2; p.reviveP = 0;
          this.emit({ t: "revived", who: p.id, by: reviver.id, cost });
        }
      } else {
        p.reviveP = Math.max(0, p.reviveP - dt * 0.5);
        p.downedT -= dt;
        if (p.downedT <= 0) { p.state = PS.OUT; this.emit({ t: "out", who: p.id }); }
      }
    }
  }

  // ---------- spawning ----------
  scriptDone() { return this.script && this.scriptT > (this.script.entries.at(-1)?.t ?? 0) && this.script.entries.every(e => e.done); }

  stepSpawner(dt) {
    this.scriptT += dt;
    for (const e of this.script.entries) {
      if (!e.done && e.t <= this.scriptT) {
        e.done = true;
        for (let i = 0; i < e.count; i++) this.spawnAtEdge(e.kind);
      }
    }
    for (let i = this.pending.length - 1; i >= 0; i--) {
      const s = this.pending[i];
      s.t -= dt;
      if (s.t <= 0) { this.pending.splice(i, 1); this.spawnEnemy(s.kind, s.x, s.y, false); }
    }
  }

  spawnAtEdge(kind) {
    let best = null, bestD = -1;
    for (let tries = 0; tries < 8; tries++) {
      const side = Math.floor(this.rng() * 4);
      const x = side === 0 ? WALL_PAD : side === 1 ? ARENA_W - WALL_PAD : WALL_PAD + this.rng() * (ARENA_W - WALL_PAD * 2);
      const y = side < 2 ? WALL_PAD + this.rng() * (ARENA_H - WALL_PAD * 2) : side === 2 ? WALL_PAD : ARENA_H - WALL_PAD;
      let d = Infinity;
      for (const p of this.players.values()) if (p.state === PS.ALIVE) d = Math.min(d, Math.hypot(p.x - x, p.y - y));
      if (d >= WAVE.SPAWN_MIN_DIST) { best = { x, y }; break; }
      if (d > bestD) { bestD = d; best = { x, y }; }
    }
    this.pendingAt(kind, best.x, best.y);
  }

  pendingAt(kind, x, y) {
    x = clamp(x, WALL_PAD, ARENA_W - WALL_PAD);
    y = clamp(y, WALL_PAD, ARENA_H - WALL_PAD);
    this.pending.push({ kind, x, y, t: WAVE.WARP_IN_S });
    this.zones.push({ kind: ZK.WARP, x, y, r: ENEMIES[kind].radius + 10, ttl: WAVE.WARP_IN_S });
  }

  spawnEnemy(kind, x, y, isBoss) {
    const def = ENEMIES[kind];
    // depth bites: regular enemies gain HP every wave (bosses use bossHp)
    const hp = isBoss ? bossHp(kind, Math.max(1, this.activeCount()), this.wave)
                      : def.hp * (1 + ENEMY_HP_PER_WAVE * Math.max(0, this.wave - 1));
    const e = {
      id: this.eid = (this.eid % 65000) + 1,
      kind, def, x, y, vx: 0, vy: 0,
      hp, maxHp: hp,
      speed: def.speed * (1 + this.wave * 0.03),
      fireT: (def.fireEvery ?? 0) * (0.5 + this.rng() * 0.5),
      wanderT: 0, wanderA: this.rng() * Math.PI * 2,
      eaten: 0, wrapOwner: 0, // black hole feeding / cocoon → wrapped player link
      hatefulT: (def.hatefulEvery ?? 0) * 0.7, charge: null, aliveT: 0,
      polT: 4, wrapTimer: (def.wrapEvery ?? 0) * 0.5, venomT: (def.venomEvery ?? 0) * 0.6,
      shockT: 2,
      sinePhase: this.rng() * Math.PI * 2, t: 0,
      enraged: false, spokeAngle: this.rng() * Math.PI * 2,
      contactCd: 0,
      slowT: 0, stunT: 0, slowF: 1, burnT: 0, burnStacks: 0, burnBy: 0,
      // sniper
      aiming: false, aimT: 0, lockX: 0, lockY: 0,
      // ghost
      phased: def.ai === "ghost", phaseT: (def.phaseTime ?? 0) * (0.5 + this.rng()), windowT: 0,
      // forge / foundry
      spawnT: def.spawnEvery ?? 0, doorOpen: false, doorT: def.doorClosed ?? 0,
      // shepherd / ultra
      darkT: def.darkEvery ?? 0, fireMode: 0,
    };
    this.enemies.set(e.id, e);
  }

  // ---------- enemies ----------
  nearestAlive(x, y) {
    let best = null, bd = Infinity;
    for (const p of this.players.values()) {
      if (p.state !== PS.ALIVE) continue;
      const d = Math.hypot(p.x - x, p.y - y);
      if (d < bd) { bd = d; best = p; }
    }
    return best;
  }

  seekVel(e, target, mul = 1) {
    const d = Math.hypot(target.x - e.x, target.y - e.y) || 1;
    const sp = e.speed * mul * (e.enraged ? 1.8 : 1);
    e.vx = ((target.x - e.x) / d) * sp; e.vy = ((target.y - e.y) / d) * sp;
  }

  stepEnemies(dt) {
    this._wardens.length = 0;
    for (const e of this.enemies.values()) if (e.def.ai === "warden") this._wardens.push(e);

    const slow = this.stasisT > 0 ? 0.45 : 1; // Stasis Charge consumable
    for (const e of this.enemies.values()) {
      e.t += dt;
      e.contactCd = Math.max(0, e.contactCd - dt);
      // per-enemy statuses (KELVIN chill/freeze, BLAZE burn)
      e.slowT = Math.max(0, e.slowT - dt);
      e.stunT = Math.max(0, e.stunT - dt);
      if (e.burnT > 0) {
        e.burnT -= dt;
        this.damageEnemy(e, 0.8 * e.burnStacks * dt, this.players.get(e.burnBy));
        if (!this.enemies.has(e.id)) continue; // burned to death mid-loop
      }
      const eslow = slow * (e.stunT > 0 ? 0 : e.slowT > 0 ? e.slowF : 1);
      const def = e.def;
      const target = this.nearestAlive(e.x, e.y);
      const ai = def.ai;

      if (ai === "seek" || ai === "boss_brute" || ai === "leech") {
        if (target) this.seekVel(e, target);
      } else if (ai === "weave") {
        if (target) {
          const d = Math.hypot(target.x - e.x, target.y - e.y) || 1;
          const px = -(target.y - e.y) / d, py = (target.x - e.x) / d;
          const s = Math.sin(e.t * 3 + e.sinePhase) * 90;
          e.vx = ((target.x - e.x) / d) * e.speed + px * s;
          e.vy = ((target.y - e.y) / d) * e.speed + py * s;
        }
      } else if (ai === "wander") {
        e.wanderT -= dt;
        if (e.wanderT <= 0) { e.wanderT = 1 + this.rng() * 1.5; e.wanderA = this.rng() * Math.PI * 2; }
        e.vx = Math.cos(e.wanderA) * e.speed; e.vy = Math.sin(e.wanderA) * e.speed;
      } else if (ai === "mortar") {
        const cx = ARENA_W / 2, cy = ARENA_H / 2;
        e.vx = (cx - e.x) * 0.02; e.vy = (cy - e.y) * 0.02;
      } else if (ai === "sniper") {
        // hold a standoff band; freeze while aiming
        if (e.aiming) { e.vx = e.vy = 0; }
        else if (target) {
          const d = Math.hypot(target.x - e.x, target.y - e.y);
          if (d < 480) this.seekVel(e, target, -1);
          else if (d > 780) this.seekVel(e, target, 0.6);
          else { e.vx *= 0.9; e.vy *= 0.9; }
        }
      } else if (ai === "warden") {
        // shepherd the nearest non-warden enemy; drift centre-ward alone
        let ward = null, wd = Infinity;
        for (const o of this.enemies.values()) {
          if (o === e || o.def.ai === "warden" || o.def.boss) continue;
          const d = Math.hypot(o.x - e.x, o.y - e.y);
          if (d < wd) { wd = d; ward = o; }
        }
        const goal = ward ?? { x: ARENA_W / 2, y: ARENA_H / 2 };
        this.seekVel(e, goal, 0.9);
      } else if (ai === "forge") {
        e.vx = e.vy = 0;
        e.spawnT -= dt;
        if (e.spawnT <= 0) {
          e.spawnT = def.spawnEvery;
          const kinds = this.wave >= 8 ? [EK.MITE, EK.DRONE, EK.WEAVER] : [EK.MITE, EK.DRONE];
          for (let i = 0; i < 2; i++) {
            const a = this.rng() * Math.PI * 2;
            this.pendingAt(kinds[Math.floor(this.rng() * kinds.length)],
              e.x + Math.cos(a) * 70, e.y + Math.sin(a) * 70);
          }
        }
      } else if (ai === "ghost") {
        if (e.phased) {
          e.phaseT -= dt;
          if (target) this.seekVel(e, target, 1.2);
          if (e.phaseT <= 0) {
            e.phased = false; e.windowT = def.windowTime;
            if (target) {
              const a = Math.atan2(target.y - e.y, target.x - e.x);
              this.emitPattern(PT.FAN, e.x, e.y, a, def.name);
            }
          }
        } else {
          e.windowT -= dt;
          e.vx *= 0.9; e.vy *= 0.9;
          if (e.windowT <= 0) { e.phased = true; e.phaseT = def.phaseTime; }
        }
      } else if (ai === "wanderer") {
        // Geometry Wars drift: constant heading, bounce off the walls
        if (e.vx === 0 && e.vy === 0) {
          e.vx = Math.cos(e.wanderA) * e.speed; e.vy = Math.sin(e.wanderA) * e.speed;
        }
        if (e.x <= WALL_PAD + 1 || e.x >= ARENA_W - WALL_PAD - 1) e.vx = -e.vx;
        if (e.y <= WALL_PAD + 1 || e.y >= ARENA_H - WALL_PAD - 1) e.vy = -e.vy;
      } else if (ai === "grunt") {
        // pulsing lunge-seeker: creeps, then surges
        if (target) this.seekVel(e, target, 0.55 + 0.9 * Math.abs(Math.sin(e.t * 2.2 + e.sinePhase)));
      } else if (ai === "dodger") {
        // the green weaver: seeks, but sidesteps incoming fire
        if (target) {
          this.seekVel(e, target);
          for (const b of this.pBullets) {
            const dx = e.x - b.x, dy = e.y - b.y;
            const d = Math.hypot(dx, dy);
            if (d > 170) continue;
            const bl = Math.hypot(b.vx, b.vy) || 1;
            const toward = (b.vx * dx + b.vy * dy) / (bl * d || 1);
            if (toward > 0.9) { // heading straight at us — juke perpendicular
              const side = (b.id & 1) ? 1 : -1;
              e.vx += (-b.vy / bl) * 300 * side;
              e.vy += (b.vx / bl) * 300 * side;
              break;
            }
          }
        }
      } else if (ai === "blackhole") {
        e.vx = e.vy = 0;
        // drags players in…
        for (const p of this.players.values()) {
          if (p.state !== PS.ALIVE) continue;
          const d = Math.hypot(p.x - e.x, p.y - e.y);
          if (d < def.pullR && d > 40) {
            p.x += ((e.x - p.x) / d) * 60 * dt;
            p.y += ((e.y - p.y) / d) * 60 * dt;
          }
        }
        // …and enemies, which it EATS; overfeed it and it bursts
        for (const o of this.enemies.values()) {
          if (o === e || o.def.boss || o.def.ai === "blackhole" || o.def.ai === "cocoon") continue;
          const d = Math.hypot(o.x - e.x, o.y - e.y);
          if (d < def.pullR * 0.7 && d > 4) {
            o.x += ((e.x - o.x) / d) * 90 * dt;
            o.y += ((e.y - o.y) / d) * 90 * dt;
          }
          if (d < 26) {
            this.enemies.delete(o.id); // swallowed — no kill, no cores
            e.hp += 2; e.maxHp += 2; e.eaten++;
          }
        }
        if (e.eaten >= 6) { // burst: bullets + a mite spray, hole collapses
          this.enemies.delete(e.id);
          this.emitPattern(PT.RING, e.x, e.y, 0, def.name);
          for (let i = 0; i < 3; i++) {
            const a = (i / 3) * Math.PI * 2;
            this.pendingAt(EK.MITE, e.x + Math.cos(a) * 50, e.y + Math.sin(a) * 50);
          }
          this.zones.push({ kind: ZK.BLAST, x: e.x, y: e.y, r: 120, ttl: 0.3 });
          this.emit({ t: "hole_burst", x: Math.round(e.x), y: Math.round(e.y) });
          continue;
        }
      } else if (ai === "cocoon") {
        e.vx = e.vy = 0; // inert — exists to be shot free
      } else if (ai === "boss_patch") {
        // THE PATCHWORK: melee sprinter. Hateful Charge locks the FARTHEST
        // pilot, telegraphs, then rushes the locked point. Hard-enrages on a
        // timer — kill it before the clock does the raid math for you.
        e.aliveT += dt;
        if (!e.enraged && e.aliveT >= def.hardEnrage) {
          e.enraged = true;
          this.emit({ t: "berserk", id: e.id, name: def.name });
        }
        if (e.charge) {
          e.charge.t -= dt;
          const d = Math.hypot(e.charge.tx - e.x, e.charge.ty - e.y);
          if (e.charge.t <= 0 || d < 30) { e.charge = null; }
          else {
            e.vx = ((e.charge.tx - e.x) / d) * e.speed * 6.5;
            e.vy = ((e.charge.ty - e.y) / d) * e.speed * 6.5;
          }
        } else {
          if (target) this.seekVel(e, target, 1.4);
          e.hatefulT -= dt;
          if (e.hatefulT <= 0) {
            e.hatefulT = def.hatefulEvery * (e.enraged ? 0.6 : 1);
            let far = null, fd = -1;
            for (const p of this.players.values()) {
              if (p.state !== PS.ALIVE) continue;
              const d = Math.hypot(p.x - e.x, p.y - e.y);
              if (d > fd) { fd = d; far = p; }
            }
            if (far) {
              e.charge = { tx: far.x, ty: far.y, t: 1.6 };
              this.emit({ t: "hateful", id: e.id, sx: Math.round(e.x), sy: Math.round(e.y), tx: Math.round(far.x), ty: Math.round(far.y), who: far.id });
            }
          }
        }
      } else if (ai === "boss_thad") {
        // THADDIUS PRIME: polarity. Everyone gets a charge; opposite charges
        // standing together get shocked. Spread by sign, or bleed.
        if (target) this.seekVel(e, target, 0.5);
        e.polT -= dt;
        if (e.polT <= 0) {
          e.polT = def.polarityEvery;
          const alive = [...this.players.values()].filter(p => p.state === PS.ALIVE);
          const charges = {};
          alive.forEach((p, i) => { p.charge = (i + (this.rng() < 0.5 ? 0 : 1)) % 2 ? 1 : -1; charges[p.id] = p.charge; });
          this.emit({ t: "charge", charges });
        }
        e.shockT -= dt;
        if (e.shockT <= 0) {
          e.shockT = 2;
          const alive = [...this.players.values()].filter(p => p.state === PS.ALIVE && p.charge);
          for (let i = 0; i < alive.length; i++) {
            for (let j = i + 1; j < alive.length; j++) {
              const a = alive[i], b = alive[j];
              if (a.charge !== b.charge && Math.hypot(a.x - b.x, a.y - b.y) < 220) {
                this.applyPlayerHit(a, 2, "POLARITY");
                this.applyPlayerHit(b, 2, "POLARITY");
                this.emit({ t: "shock", a: a.id, b: b.id });
              }
            }
          }
        }
      } else if (ai === "boss_brood") {
        // THE BROODMOTHER: web-wraps a pilot (spawns a shootable cocoon),
        // seeds venom pools, births spiderlings.
        if (target) this.seekVel(e, target, 0.8);
        e.wrapTimer -= dt;
        if (e.wrapTimer <= 0) {
          e.wrapTimer = def.wrapEvery * (e.enraged ? 0.7 : 1);
          const candidates = [...this.players.values()].filter(p => p.state === PS.ALIVE && p.wrapT <= 0);
          if (candidates.length >= 2) { // never wrap a lone pilot into a softlock
            const victim = candidates[Math.floor(this.rng() * candidates.length)];
            victim.wrapT = 6;
            this.spawnEnemy(EK.COCOON, victim.x, victim.y, false);
            const cocoon = [...this.enemies.values()].at(-1);
            cocoon.wrapOwner = victim.id;
            for (let i = 0; i < 6; i++) {
              const a = (i / 6) * Math.PI * 2;
              this.pendingAt(EK.MITE, victim.x + Math.cos(a) * 90, victim.y + Math.sin(a) * 90);
            }
            this.emit({ t: "wrapped", who: victim.id });
          }
        }
        e.venomT -= dt;
        if (e.venomT <= 0) {
          e.venomT = def.venomEvery;
          const alive = [...this.players.values()].filter(p => p.state === PS.ALIVE);
          if (alive.length) {
            const v = alive[Math.floor(this.rng() * alive.length)];
            this.zones.push({
              kind: ZK.VENOM,
              x: clamp(v.x + (this.rng() - 0.5) * 200, WALL_PAD, ARENA_W - WALL_PAD),
              y: clamp(v.y + (this.rng() - 0.5) * 200, WALL_PAD, ARENA_H - WALL_PAD),
              r: 150, ttl: 8, grace: 0.8, tickT: 0,
            });
          }
        }
      } else if (ai === "magnet") {
        if (target) this.seekVel(e, target, 0.8);
        for (const p of this.players.values()) {
          if (p.state !== PS.ALIVE) continue;
          const d = Math.hypot(p.x - e.x, p.y - e.y);
          if (d < def.pullR && d > 40) {
            p.x += ((e.x - p.x) / d) * def.pull * dt;
            p.y += ((e.y - p.y) / d) * def.pull * dt;
          }
        }
      } else if (ai === "boss_hex") {
        const cx = ARENA_W / 2, cy = ARENA_H / 2;
        e.vx = (cx - e.x) * 0.05; e.vy = (cy - e.y) * 0.05;
      } else if (ai === "boss_foundry") {
        const cx = ARENA_W / 2, cy = ARENA_H * 0.3;
        e.vx = (cx - e.x) * 0.02; e.vy = (cy - e.y) * 0.02;
        e.doorT -= dt;
        if (!e.doorOpen && e.doorT <= 0) {
          e.doorOpen = true; e.doorT = def.doorOpen;
          this.emit({ t: "doors", id: e.id, open: true });
          const spawnN = def.spawnCount + Math.max(0, this.activeCount() - 1); // adds scale with the squad
          for (let i = 0; i < spawnN; i++) {
            const a = (i / spawnN) * Math.PI * 2;
            const kinds = [EK.MITE, EK.DRONE, EK.WEAVER];
            this.pendingAt(kinds[i % kinds.length], e.x + Math.cos(a) * 110, e.y + Math.sin(a) * 110);
          }
        } else if (e.doorOpen && e.doorT <= 0) {
          e.doorOpen = false; e.doorT = def.doorClosed * (e.enraged ? 0.55 : 1);
          this.emit({ t: "doors", id: e.id, open: false });
        }
      } else if (ai === "boss_shepherd") {
        if (target) this.seekVel(e, target, 0.7);
        e.darkT -= dt;
        if (e.darkT <= 0) {
          e.darkT = def.darkEvery * (e.enraged ? 0.6 : 1);
          const victims = [...this.players.values()].filter(p => p.state === PS.ALIVE);
          if (victims.length) {
            const v = victims[Math.floor(this.rng() * victims.length)];
            this.zones.push({
              kind: ZK.DARK,
              x: clamp(v.x + (this.rng() - 0.5) * 240, WALL_PAD, ARENA_W - WALL_PAD),
              y: clamp(v.y + (this.rng() - 0.5) * 240, WALL_PAD, ARENA_H - WALL_PAD),
              r: 240, ttl: 6, grace: 1.5, tickT: 0,
            });
          }
        }
      } else if (ai === "boss_ultra") {
        if (target) this.seekVel(e, target, 0.6);
        if (Math.floor(e.t / 7) > Math.floor((e.t - dt) / 7)) this.spawnAtEdge(EK.GHOST);
      }

      e.x = clamp(e.x + e.vx * eslow * dt, WALL_PAD, ARENA_W - WALL_PAD);
      e.y = clamp(e.y + e.vy * eslow * dt, WALL_PAD, ARENA_H - WALL_PAD);

      // sniper aim/fire cycle (outside movement so freezing works)
      if (ai === "sniper" && target && e.stunT <= 0) {
        if (!e.aiming) {
          e.fireT -= dt * eslow;
          if (e.fireT <= 0) {
            e.aiming = true; e.aimT = def.aimTime;
            e.lockX = target.x; e.lockY = target.y;
            this.emit({ t: "laser_warn", id: e.id, sx: Math.round(e.x), sy: Math.round(e.y), tx: Math.round(e.lockX), ty: Math.round(e.lockY), s: def.aimTime });
          }
        } else {
          e.aimT -= dt;
          if (e.aimT <= 0) {
            e.aiming = false; e.fireT = def.fireEvery;
            this.fireLaser(e);
          }
        }
      }

      // generic pattern firing (frozen enemies don't fire)
      if (def.fireEvery && target && ai !== "sniper" && ai !== "ghost" && e.stunT <= 0) {
        e.fireT -= dt * eslow;
        if (e.fireT <= 0) {
          e.fireT = def.fireEvery * (e.enraged ? 0.55 : 1);
          const aimA = Math.atan2(target.y - e.y, target.x - e.x);
          if (ai === "weave" || (ai === "boss_brute" && e.enraged) || ai === "boss_shepherd") {
            this.emitPattern(PT.FAN, e.x, e.y, aimA, def.name);
          } else if (ai === "mortar") {
            this.zones.push({ kind: ZK.MORTAR_TELE, x: target.x, y: target.y, r: def.shellRadius, ttl: def.shellDelay });
          } else if (ai === "boss_brute") {
            this.emitPattern(PT.RING, e.x, e.y, 0, def.name);
          } else if (ai === "boss_hex") {
            e.spokeAngle += e.enraged ? 0.52 : 0.35;
            this.emitPattern(PT.SPOKES, e.x, e.y, e.spokeAngle, def.name);
            if (Math.floor(e.t / 6) > Math.floor((e.t - dt) / 6)) {
              this.spawnAtEdge(EK.WEAVER); this.spawnAtEdge(EK.WEAVER);
            }
          } else if (ai === "boss_ultra") {
            const mode = e.fireMode++ % 3;
            if (mode === 0) { e.spokeAngle += 0.4; this.emitPattern(PT.SPOKES, e.x, e.y, e.spokeAngle, def.name); }
            else if (mode === 1) this.emitPattern(PT.FAN, e.x, e.y, aimA, def.name);
            else this.emitPattern(PT.RING, e.x, e.y, 0, def.name);
          }
        }
      }
      if (def.boss && !e.enraged && e.hp <= e.maxHp * 0.5) {
        e.enraged = true;
        this.emit({ t: "enrage", id: e.id, kind: e.kind });
      }

      // contact
      for (const p of this.players.values()) {
        if (p.state !== PS.ALIVE) continue;
        const d = Math.hypot(p.x - e.x, p.y - e.y);
        if (d < def.radius + PLAYER.RADIUS) {
          if (ai === "cocoon") {
            // a web cocoon never hurts the pilots trying to free their friend
          } else if (ai === "leech") {
            if (e.contactCd <= 0) {
              e.contactCd = 1;
              this.mult = Math.max(1, this.mult - def.drain);
              this.emit({ t: "leech", who: p.id });
            }
          } else if (!(ai === "ghost" && e.phased)) {
            this.applyPlayerHit(p, 1, def.name.toUpperCase());
          }
          if (p.stats.thorns > 0) this.damageEnemy(e, p.stats.thorns * 2, p);
          const push = 60 / (d || 1);
          e.x -= (p.x - e.x) * push * 0.2; e.y -= (p.y - e.y) * push * 0.2;
        }
      }
    }
  }

  fireLaser(e) {
    // instant beam from the sniper through its locked point, arena-length
    const dx = e.lockX - e.x, dy = e.lockY - e.y;
    const l = Math.hypot(dx, dy) || 1;
    const ex = e.x + (dx / l) * 2600, ey = e.y + (dy / l) * 2600;
    for (const p of this.players.values()) {
      if (p.state !== PS.ALIVE) continue;
      if (distToSegment(p.x, p.y, e.x, e.y, ex, ey) < PLAYER.RADIUS + 6) {
        this.applyPlayerHit(p, 1, "SNIPER");
      }
    }
    this.emit({ t: "laser_fire", id: e.id, sx: Math.round(e.x), sy: Math.round(e.y), tx: Math.round(ex), ty: Math.round(ey) });
  }

  isShielded(e) {
    if (e.def.ai === "warden" || e.def.boss) return false;
    for (const w of this._wardens) {
      if (Math.hypot(e.x - w.x, e.y - w.y) < w.def.shieldR) return true;
    }
    return false;
  }

  emitPattern(pid, x, y, angle, srcName = "") {
    const seed = (this.rng() * 0xffffffff) >>> 0;
    const bullets = spawnPattern(pid, seed, x, y, angle);
    for (const b of bullets) { b.cause = srcName; this.eBullets.push(b); }
    while (this.eBullets.length > E_BULLET_CAP) this.eBullets.shift();
    this.emit({ t: "pattern", pid, seed, x: Math.round(x), y: Math.round(y), angle: Math.round(angle * 1000) / 1000 });
  }

  stepEnemyBullets(dt) {
    for (let i = this.eBullets.length - 1; i >= 0; i--) {
      const b = this.eBullets[i];
      b.x += b.vx * dt; b.y += b.vy * dt;
      if (b.x < 0 || b.x > ARENA_W || b.y < 0 || b.y > ARENA_H) { this.eBullets.splice(i, 1); continue; }
      for (const p of this.players.values()) {
        if (p.state !== PS.ALIVE || p.iframesT > 0) continue;
        if (Math.hypot(p.x - b.x, p.y - b.y) < PLAYER.RADIUS + b.r) {
          this.applyPlayerHit(p, 1, b.cause ? b.cause.toUpperCase() : "BULLET");
          this.eBullets.splice(i, 1);
          break;
        }
      }
    }
  }
  stepPlayerBullets(dt) {
    const aoeQueue = [];

    for (let i = this.pBullets.length - 1; i >= 0; i--) {
      const b = this.pBullets[i];
      const oldX = b.x;
      const oldY = b.y;

      b.x += b.vx * dt;
      b.y += b.vy * dt;
      b.life -= dt;

      if (b.life <= 0) {
        this.pBullets.splice(i, 1);
        continue;
      }

      const hits = [];

      for (const e of this.enemies.values()) {
        if (e.phased || b.hitIds?.has(e.id)) continue;

        const t = segmentCircleHitT(
          e.x, e.y, e.def.radius + 5,
          oldX, oldY, b.x, b.y,
        );

        if (t !== null) hits.push({ e, t });
      }

      hits.sort((a, z) => a.t - z.t);

      let consumed = false;
      const killer = this.players.get(b.owner);
      b.hitIds ??= new Set();

      for (const { e, t } of hits) {
        if (!this.enemies.has(e.id)) continue;

        if (
          (e.def.ai === "boss_foundry" && !e.doorOpen) ||
          this.isShielded(e)
        ) {
          consumed = true;
          break;
        }

        const impactX = oldX + (b.x - oldX) * t;
        const impactY = oldY + (b.y - oldY) * t;

        this.damageEnemy(e, b.dmg, killer, aoeQueue);
        b.hitIds.add(e.id);

        if (b.chill > 0 && this.enemies.has(e.id)) {
          e.slowT = Math.max(e.slowT, b.chill);
          e.slowF = Math.min(e.slowF, killer?.stats.chillSlow ?? 0.55);
        }

        if (b.burn > 0 && this.enemies.has(e.id)) {
          e.burnT = 2;
          e.burnStacks = b.burn;
          e.burnBy = b.owner;
        }

        if (b.chain > 0) {
          let hops = b.chain;
          const chainDamage = b.dmg * (0.5 + (killer?.stats.chainDmgBonus ?? 0));

          for (const other of this.enemies.values()) {
            if (hops <= 0) break;
            if (other === e || other.phased) continue;

            if (Math.hypot(other.x - impactX, other.y - impactY) < (b.chainR ?? 140)) {
              hops--;
              this.zones.push({ kind: ZK.BLAST, x: other.x, y: other.y, r: 18, ttl: 0.15 });
              this.damageEnemy(other, chainDamage, killer, aoeQueue);
            }
          }
        }

        // A kill does not consume a piercing rail.
        if (b.pierce <= 0) {
          consumed = true;
          break;
        }

        b.pierce--;
      }

      if (consumed) {
        this.pBullets.splice(i, 1);
        continue;
      }

      if (b.x < 4 || b.x > ARENA_W - 4) {
        if (b.ricochet > 0) {
          b.ricochet--;
          b.vx = -b.vx;
          b.x = clamp(b.x, 4, ARENA_W - 4);
        } else {
          this.pBullets.splice(i, 1);
          continue;
        }
      }

      if (b.y < 4 || b.y > ARENA_H - 4) {
        if (b.ricochet > 0) {
          b.ricochet--;
          b.vy = -b.vy;
          b.y = clamp(b.y, 4, ARENA_H - 4);
        } else {
          this.pBullets.splice(i, 1);
        }
      }
    }

    while (aoeQueue.length) {
      const { x, y, dmg, killer } = aoeQueue.pop();

      for (const e of this.enemies.values()) {
        if (Math.hypot(e.x - x, e.y - y) < 80) {
          this.damageEnemy(e, dmg, killer, aoeQueue);
        }
      }
    }
  }

  damageEnemy(e, dmg, killer, aoeQueue) {
    if (!this.enemies.has(e.id)) return false;
    if (e.phased) return false;
    if (e.def.ai === "boss_foundry" && !e.doorOpen) return false;
    if (this.isShielded(e)) return false;
    if (killer) { // shop damage modifiers
      if (killer.stats.headhunter > 0 && (e.def.boss || e.def.hp >= 4)) dmg *= 1.6;
      if (killer.stats.shatter > 0 && (e.slowT > 0 || e.stunT > 0)) dmg *= 1.75;
    }
    e.hp -= dmg;
    if (e.hp > 0) return false;
    this.enemies.delete(e.id);
    const def = e.def;
    if (e.kind === EK.COCOON) { // shot a teammate free — no score, no mult
      const owner = this.players.get(e.wrapOwner);
      if (owner && owner.wrapT > 0) {
        owner.wrapT = 0;
        this.emit({ t: "unwrapped", who: owner.id, by: killer?.id ?? 0 });
      }
      this.zones.push({ kind: ZK.BLAST, x: e.x, y: e.y, r: 40, ttl: 0.2 });
      return true;
    }
    const bounty = killer?.stats.bounty ?? 1;
    const overdrive = this.mult >= MULT.MAX - 0.05;
    const points = Math.round(def.score * this.mult * bounty * (overdrive ? 2 : 1));
    this.unbanked += points;
    this.mult = Math.min(MULT.MAX, this.mult + MULT.PER_KILL);
    this.bestMult = Math.max(this.bestMult, this.mult);
    this.sinceKill = 0;
    if (killer) {
      killer.kills++;
      this.payCores(killer, def.coreValue ?? 0);
      if (killer.stats.bloodrush > 0) killer.dashCd = Math.max(0, killer.dashCd - 0.15 * killer.stats.bloodrush);
      if (killer.stats.streakBomb > 0 && killer.kills % 25 === 0) {
        killer.bombs = Math.min(PLAYER.MAX_BOMBS, killer.bombs + killer.stats.streakBomb);
        this.emit({ t: "streak", who: killer.id });
      }
    }
    this.emit({ t: "kill", kind: e.kind, x: Math.round(e.x), y: Math.round(e.y), points, who: killer?.id ?? 0 });
    const luck = 1 + (killer?.stats.dropLuck ?? 0); // Lucky Charm stacks
    if (def.dropChance && this.rngDrop() < Math.min(1, def.dropChance * luck)) {
      this.spawnPickup(rollConsumable(this.rngDrop), e.x, e.y);
    }
    if (def.onDeath?.pattern) this.emitPattern(PATTERN_IDS[def.onDeath.pattern], e.x, e.y, 0, def.name);
    if (def.onDeath?.split) {
      for (let i = 0; i < def.onDeath.count; i++) {
        const a = (i / def.onDeath.count) * Math.PI * 2;
        this.spawnEnemy(def.onDeath.split, clamp(e.x + Math.cos(a) * 30, WALL_PAD, ARENA_W - WALL_PAD), clamp(e.y + Math.sin(a) * 30, WALL_PAD, ARENA_H - WALL_PAD), false);
      }
    }
    if (killer?.stats.blast > 0 && aoeQueue) aoeQueue.push({ x: e.x, y: e.y, dmg: killer.stats.blast, killer });
    if (def.boss) {
      for (const p of this.players.values()) {
        p.bombs = Math.min(PLAYER.MAX_BOMBS, p.bombs + 1);
        p.charge = null;                       // polarity ends with the boss
        if (p.wrapT > 0) this.freeWrap(p);
      }
      for (const [cid, o] of this.enemies) {
        if (o.kind === EK.COCOON) this.enemies.delete(cid);
      }
      this.zones = this.zones.filter(z => z.kind !== ZK.VENOM && z.kind !== ZK.DARK);
      this.emit({ t: "charge", charges: {} }); // clear the ± markers
      this.emit({ t: "boss_down", kind: e.kind });
    }
    return true;
  }

  freeWrap(p) {
    p.wrapT = 0;
    for (const [cid, o] of this.enemies) {
      if (o.kind === EK.COCOON && o.wrapOwner === p.id) this.enemies.delete(cid);
    }
    this.emit({ t: "unwrapped", who: p.id, by: 0 });
  }

  stepOrbitals(dt) {
    for (const p of this.players.values()) {
      if (p.state !== PS.ALIVE || p.stats.orbitals <= 0) continue;
      const k = p.stats.orbitals;
      for (let i = 0; i < k; i++) {
        // client renders blades from the same tick-based formula (ORBITAL const)
        const a = this.tick * TICK_DT * ORBITAL.ROT + (i / k) * Math.PI * 2;
        const ox = p.x + Math.cos(a) * ORBITAL.R, oy = p.y + Math.sin(a) * ORBITAL.R;
        for (const e of this.enemies.values()) {
          if (Math.hypot(e.x - ox, e.y - oy) < e.def.radius + ORBITAL.BLADE) {
            this.damageEnemy(e, ORBITAL.DPS * dt, p);
          }
        }
      }
    }
  }

  stepZones(dt) {
    for (let i = this.zones.length - 1; i >= 0; i--) {
      const z = this.zones[i];
      z.ttl -= dt;
      if (z.kind === ZK.FLAME) {
        const owner = this.players.get(z.owner);
        const dps = 2 * (owner?.stats.flameDps ?? 1);
        for (const e of this.enemies.values()) {
          if (Math.hypot(e.x - z.x, e.y - z.y) < z.r + e.def.radius) this.damageEnemy(e, dps * dt, owner);
        }
      } else if (z.kind === ZK.WELL) {
        for (const e of this.enemies.values()) {
          const d = Math.hypot(e.x - z.x, e.y - z.y);
          if (d < z.r * 2 && d > 4) {
            e.x += ((z.x - e.x) / d) * 140 * dt;
            e.y += ((z.y - e.y) / d) * 140 * dt;
          }
        }
      } else if (z.kind === ZK.PYLON) {
        // SPARKS' tesla pylon: zap the nearest enemy in range on a cadence
        z.zapT -= dt;
        if (z.zapT <= 0) {
          z.zapT = PYLON.CD;
          const owner = this.players.get(z.owner);
          let tgt = null, td = z.r;
          for (const e of this.enemies.values()) {
            if (e.phased) continue;
            const d = Math.hypot(e.x - z.x, e.y - z.y);
            if (d < td) { td = d; tgt = e; }
          }
          if (tgt) {
            this.zones.push({ kind: ZK.BLAST, x: tgt.x, y: tgt.y, r: 20, ttl: 0.15 });
            this.damageEnemy(tgt, PYLON.DMG + (owner?.stats.pylonDmgBonus ?? 0), owner);
          }
        }
      } else if (z.kind === ZK.BEACON) {
        // AMBER's Sanctuary (shop): the beacon site mends allies near it
        const owner = this.players.get(z.owner);
        if (owner?.stats.sanctuary > 0) {
          z.healT -= dt;
          if (z.healT <= 0) {
            z.healT = 4;
            for (const q of this.players.values()) {
              if (q.state === PS.ALIVE && Math.hypot(q.x - z.x, q.y - z.y) < 60) {
                q.hp = Math.min(this.hpMax(q), q.hp + 1);
              }
            }
          }
        }
      } else if (z.kind === ZK.DARK || z.kind === ZK.VENOM) {
        // herding dark / venom pool: linger inside and it bites
        z.grace -= dt;
        if (z.grace <= 0) {
          z.tickT -= dt;
          if (z.tickT <= 0) {
            z.tickT = 1.25;
            for (const p of this.players.values()) {
              if (p.state === PS.ALIVE && Math.hypot(p.x - z.x, p.y - z.y) < z.r) {
                this.applyPlayerHit(p, 1, z.kind === ZK.VENOM ? "VENOM" : "THE DARK");
              }
            }
          }
        }
      }
      if (z.ttl <= 0) {
        if (z.kind === ZK.BEACON) {
          // expired unused beacon — clear the owner's warp anchor
          const owner = this.players.get(z.owner);
          if (owner) owner.beacon = null;
        } else if (z.kind === ZK.MORTAR_TELE) {
          for (const p of this.players.values()) {
            if (p.state === PS.ALIVE && Math.hypot(p.x - z.x, p.y - z.y) < z.r) this.applyPlayerHit(p, 1, "MORTAR");
          }
          this.zones.push({ kind: ZK.BLAST, x: z.x, y: z.y, r: z.r, ttl: 0.25 });
        } else if (z.kind === ZK.WELL) {
          const owner = this.players.get(z.owner);
          const dmg = 3 + (owner?.stats.wellDmg ?? 0);
          for (const e of this.enemies.values()) {
            if (Math.hypot(e.x - z.x, e.y - z.y) < z.r) this.damageEnemy(e, dmg, owner);
          }
          this.zones.push({ kind: ZK.BLAST, x: z.x, y: z.y, r: z.r, ttl: 0.25 });
        }
        this.zones.splice(i, 1);
      }
    }
  }

  // ---------- output ----------
  emit(ev) { this.events.push(ev); }

  buildSnapshot() {
    const players = [];
    for (const p of this.players.values()) {
      let flags = 0;
      if (p.dashT > 0) flags |= PF.DASHING;
      if (p.iframesT > 0) flags |= PF.IFRAMES;
      if (p.beingRevived) flags |= PF.REVIVING;
      if (p.wrapT > 0) flags |= PF.WRAPPED;
      if (this.mult >= MULT.MAX - 0.05) flags |= PF.OVERDRIVE;
      players.push({
        id: p.id, pilot: p.pilot, state: p.state, x: p.x, y: p.y, aim: p.aim,
        hp: Math.max(0, p.hp), dashCd: p.dashStock > 0 ? 0 : p.dashCd, abilCd: Math.ceil(p.abilCd),
        bombs: p.bombs, flags,
        orbitals: Math.min(255, p.stats.orbitals | 0),
        cons: p.cons,
        cores: p.cores,
      });
    }
    const enemies = [];
    for (const e of this.enemies.values()) {
      let flags = 0;
      if (e.enraged) flags |= EF.ENRAGED;
      if (e.phased) flags |= EF.PHASED;
      if (e.doorOpen) flags |= EF.OPEN;
      if (e.slowT > 0 || e.stunT > 0) flags |= EF.CHILLED;
      enemies.push({
        id: e.id, kind: e.kind, x: e.x, y: e.y,
        hpPct: Math.max(1, Math.ceil((e.hp / e.maxHp) * 100)), flags,
      });
    }
    const bullets = this.pBullets.map(b => ({ id: b.id, x: b.x, y: b.y, owner: b.owner }));
    const zones = this.zones.map(z => ({ kind: z.kind, x: z.x, y: z.y, r: z.r, ttl: z.ttl }));
    const pickups = [...this.pickups.values()];
    return {
      tick: this.tick, phase: this.phase, wave: this.wave,
      phaseT: Math.max(0, Math.round(this.phaseT * 30)),
      mult: this.mult, unbanked: Math.round(this.unbanked), banked: Math.round(this.banked),
      enemiesLeft: this.enemies.size + this.pending.length,
      stasis: this.stasisT,
      players, enemies, bullets, zones, pickups,
    };
  }
}

function distToSegment(px, py, x1, y1, x2, y2) {
  const dx = x2 - x1, dy = y2 - y1;
  const len2 = dx * dx + dy * dy || 1;
  const t = clamp(((px - x1) * dx + (py - y1) * dy) / len2, 0, 1);
  return Math.hypot(px - (x1 + t * dx), py - (y1 + t * dy));
}
function segmentCircleHitT(cx, cy, r, x1, y1, x2, y2) {
  const dx = x2 - x1;
  const dy = y2 - y1;
  const fx = x1 - cx;
  const fy = y1 - cy;
  const a = dx * dx + dy * dy;
  const c = fx * fx + fy * fy - r * r;

  if (a < 1e-9) return c <= 0 ? 0 : null;
  if (c <= 0) return 0;

  const b = 2 * (fx * dx + fy * dy);
  const discriminant = b * b - 4 * a * c;
  if (discriminant < 0) return null;

  const root = Math.sqrt(discriminant);
  const t = (-b - root) / (2 * a);

  return t >= 0 && t <= 1 ? t : null;
}