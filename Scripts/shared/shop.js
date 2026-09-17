// The post-boss Core Shop catalog. Items use s_* ids and resolve through
// modById() in mods.js, so a purchase is just an id pushed into p.mods —
// every stat-recompute site (server and client) applies it for free, and
// buying the same stackable twice simply applies it twice.
//
// Design rule: `once: true` items are SIGNATURE playstyle flips (few, pricey).
// Everything else is `once: false` — weak alone, bought again and again.
// RANK BALANCE: for stackables the Nth copy costs price×N and continuous
// effects apply at 1/N strength (computeStats passes k=1/N to apply).
// Discrete effects (+1 pellet, +1 max HP…) stay whole but still pay ×N.
// `flat: true` = utility consumables (heal/bombs): flat price, full effect.
// Every class has an ability-cooldown line and ability-magnitude lines.
// `pilot: null` = any class. `heal`/`bombs` are immediate effects at purchase.
// IMPORTANT: shop.js must not import from mods.js (mods.js imports us).

export const SHOP_ITEMS = [
  // ================ BINK ✦ — SMG & Blink Volley ================
  { id: "s_b_twin",   pilot: 0, price: 70, once: true,  name: "Twin SMG",      desc: "+1 bullet stream, −15% damage.",            apply: (s, k = 1) => { s.split += 1; s.dmg *= 0.85; } },
  { id: "s_b_rifle",  pilot: 0, price: 30, once: false, name: "Rifling",       desc: "+8% SMG damage.",                           apply: (s, k = 1) => { s.dmg *= 1 + 0.08 * k; } },
  { id: "s_b_steady", pilot: 0, price: 25, once: false, name: "Steady Barrel", desc: "SMG jitter −20%.",                          apply: (s, k = 1) => { s.jitterMul *= 1 - 0.2 * k; } },
  { id: "s_b_feather",pilot: 0, price: 30, once: false, name: "Featherlight",  desc: "+4% move speed.",                           apply: (s, k = 1) => { s.speed *= 1 + 0.04 * k; } },
  { id: "s_b_volley", pilot: 0, price: 30, once: false, name: "Volley Rounds", desc: "Blink volley fires +3 bullets.",            apply: (s, k = 1) => { s.blinkShots += 3; } },
  { id: "s_b_cdr",    pilot: 0, price: 35, once: false, name: "Coil Tuning",   desc: "Blink cooldown −1s.",                       apply: (s, k = 1) => { s.abilityCdr += 1 * k; } },
  // ================ BLAZE ▲ — Scattergun & Flame Zone ================
  { id: "s_z_slug",   pilot: 1, price: 80, once: true,  name: "Slug Shot",     desc: "Pellets fuse into ONE slug: dmg 5, range ×2.5.", apply: (s, k = 1) => { s.slugShot = 1; } },
  { id: "s_z_dragon", pilot: 1, price: 70, once: true,  name: "Dragon's Breath", desc: "Pellets ignite enemies (burn over 2s).",  apply: (s, k = 1) => { s.burn += 1; } },
  { id: "s_z_pellet", pilot: 1, price: 35, once: false, name: "Extra Pellet",  desc: "+1 pellet per blast.",                      apply: (s, k = 1) => { s.split += 1; } },
  { id: "s_z_choke",  pilot: 1, price: 25, once: false, name: "Choke Ring",    desc: "Cone 15% tighter.",                         apply: (s, k = 1) => { s.chokeMul *= 1 - 0.15 * k; } },
  { id: "s_z_accel",  pilot: 1, price: 30, once: false, name: "Accelerant",    desc: "Flame Zone burns +20% hotter.",             apply: (s, k = 1) => { s.flameDps *= 1 + 0.2 * k; } },
  { id: "s_z_cdr",    pilot: 1, price: 35, once: false, name: "Pilot Flame",   desc: "Flame Zone cooldown −1s.",                  apply: (s, k = 1) => { s.abilityCdr += 1 * k; } },
  // ================ AMBER ◯ — Beacon Warp & Heal Aura ================
  { id: "s_a_radia",  pilot: 2, price: 90, once: true,  name: "Radiant Aura",  desc: "Your aura also burns enemies inside it.",   apply: (s, k = 1) => { s.radiantAura += 1; } },
  { id: "s_a_sanct",  pilot: 2, price: 70, once: true,  name: "Sanctuary",     desc: "Allies near your beacon heal +1 every 4s.", apply: (s, k = 1) => { s.sanctuary += 1; } },
  { id: "s_a_blast",  pilot: 2, price: 60, once: true,  name: "Beacon Blast",  desc: "Warping detonates your departure point.",   apply: (s, k = 1) => { s.beaconBlast += 1; } },
  { id: "s_a_glow",   pilot: 2, price: 30, once: false, name: "Warm Glow",     desc: "Aura heals 15% faster.",                    apply: (s, k = 1) => { s.auraRate *= 1 + 0.15 * k; } },
  { id: "s_a_halo",   pilot: 2, price: 30, once: false, name: "Wide Halo",     desc: "Aura 10% larger.",                          apply: (s, k = 1) => { s.auraR *= 1 + 0.1 * k; } },
  { id: "s_a_cdr",    pilot: 2, price: 35, once: false, name: "Beacon Coil",   desc: "Beacon Warp cooldown −1s.",                 apply: (s, k = 1) => { s.abilityCdr += 1 * k; } },
  // ================ DAVE ◉ — Cleaver & Gravity Well ================
  { id: "s_d_whirl",  pilot: 3, price: 90, once: true,  name: "Whirlwind",     desc: "Cleave hits ALL around you (360°).",        apply: (s, k = 1) => { s.cleave360 = 1; } },
  { id: "s_d_shock",  pilot: 3, price: 60, once: true,  name: "Aftershock",    desc: "Each cleave launches a shockwave slug.",    apply: (s, k = 1) => { s.aftershock += 1; } },
  { id: "s_d_edge",   pilot: 3, price: 30, once: false, name: "Sharpened Edge",desc: "+10% cleave damage.",                       apply: (s, k = 1) => { s.dmg *= 1 + 0.1 * k; } },
  { id: "s_d_swing",  pilot: 3, price: 30, once: false, name: "Wider Swing",   desc: "Cleave arc 8% wider.",                      apply: (s, k = 1) => { s.cleaveArcMul *= 1 + 0.08 * k; } },
  { id: "s_d_iron",   pilot: 3, price: 40, once: false, heal: 1, name: "Iron Skin", desc: "+1 max HP (heal 1 now).",              apply: (s, k = 1) => { s.maxHp += 1; } },
  { id: "s_d_cdr",    pilot: 3, price: 35, once: false, name: "Dense Coil",    desc: "Gravity Well cooldown −1s.",                apply: (s, k = 1) => { s.abilityCdr += 1 * k; } },
  // ================ SPARKS ⌁ — Arc Gun & Tesla Pylon ================
  { id: "s_s_twin",   pilot: 4, price: 80, once: true,  name: "Twin Pylon",    desc: "Deploy 2 pylons per cast.",                 apply: (s, k = 1) => { s.pylonCount += 1; } },
  { id: "s_s_cond",   pilot: 4, price: 45, once: false, name: "Conductor",     desc: "Chain lightning jumps +1 more time.",       apply: (s, k = 1) => { s.chainHops += 1; } },
  { id: "s_s_volt",   pilot: 4, price: 30, once: false, name: "High Voltage",  desc: "Chain hops +10% damage.",                   apply: (s, k = 1) => { s.chainDmgBonus += 0.1 * k; } },
  { id: "s_s_cap",    pilot: 4, price: 25, once: false, name: "Capacitor Bank",desc: "Pylons last +1.5s.",                        apply: (s, k = 1) => { s.pylonTtlBonus += 1.5 * k; } },
  { id: "s_s_amp",    pilot: 4, price: 30, once: false, name: "Amp Coils",     desc: "Pylon zaps +0.5 damage.",                   apply: (s, k = 1) => { s.pylonDmgBonus += 0.5 * k; } },
  { id: "s_s_cdr",    pilot: 4, price: 35, once: false, name: "Fast Discharge",desc: "Tesla Pylon cooldown −1s.",                 apply: (s, k = 1) => { s.abilityCdr += 1 * k; } },
  // ================ RIGG ⚒ — Blaster & Auto-Turret ================
  { id: "s_r_twin",   pilot: 5, price: 80, once: true,  name: "Twin Turret",   desc: "Deploy 2 turrets per cast.",                apply: (s, k = 1) => { s.turretCount += 1; } },
  { id: "s_r_servo",  pilot: 5, price: 30, once: false, name: "Servo Tune",    desc: "Turrets fire 10% faster.",                  apply: (s, k = 1) => { s.turretFireMul *= 1 + 0.1 * k; } },
  { id: "s_r_ammo",   pilot: 5, price: 30, once: false, name: "Ammo Press",    desc: "Turret damage +12%.",                       apply: (s, k = 1) => { s.turretDmgMul *= 1 + 0.12 * k; } },
  { id: "s_r_watch",  pilot: 5, price: 30, once: false, name: "Long Watch",    desc: "Turrets last 20% longer.",                  apply: (s, k = 1) => { s.turretTtlMul *= 1 + 0.2 * k; } },
  { id: "s_r_cdr",    pilot: 5, price: 35, once: false, name: "Quick Deploy",  desc: "Auto-Turret cooldown −1s.",                 apply: (s, k = 1) => { s.abilityCdr += 1 * k; } },
  // ================ KELVIN ❄ — Chill Lance & Frost Nova ================
  { id: "s_k_shat",   pilot: 6, price: 90, once: true,  name: "Shatter",       desc: "Chilled/frozen enemies take +75% damage from you.", apply: (s, k = 1) => { s.shatter += 1; } },
  { id: "s_k_deep",   pilot: 6, price: 30, once: false, name: "Deep Freeze",   desc: "Chill lasts 25% longer.",                   apply: (s, k = 1) => { s.chillDurMul *= 1 + 0.25 * k; } },
  { id: "s_k_rounds", pilot: 6, price: 30, once: false, name: "Cold Rounds",   desc: "+8% lance damage.",                         apply: (s, k = 1) => { s.dmg *= 1 + 0.08 * k; } },
  { id: "s_k_front",  pilot: 6, price: 30, once: false, name: "Wide Front",    desc: "Frost Nova 12% larger.",                    apply: (s, k = 1) => { s.frostRMul *= 1 + 0.12 * k; } },
  { id: "s_k_winter", pilot: 6, price: 30, once: false, name: "Long Winter",   desc: "Freeze +0.25s.",                            apply: (s, k = 1) => { s.frostDurBonus += 0.25 * k; } },
  { id: "s_k_cdr",    pilot: 6, price: 35, once: false, name: "Cold Snap",     desc: "Frost Nova cooldown −1s.",                  apply: (s, k = 1) => { s.abilityCdr += 1 * k; } },
  // ================ HAWK ⌖ — Railgun & Triple Rail ================
  { id: "s_h_pen",    pilot: 7, price: 90, once: true,  name: "Overpenetrate", desc: "Rails pierce EVERYTHING.",                  apply: (s, k = 1) => { s.pierce += 96; } },
  { id: "s_h_head",   pilot: 7, price: 70, once: true,  name: "Headhunter",    desc: "+60% damage to elites and bosses.",         apply: (s, k = 1) => { s.headhunter += 1; } },
  { id: "s_h_sabot",  pilot: 7, price: 30, once: false, name: "Sabot Rounds",  desc: "+8% rail damage.",                          apply: (s, k = 1) => { s.dmg *= 1 + 0.08 * k; } },
  { id: "s_h_mag",    pilot: 7, price: 25, once: false, name: "Magrail",       desc: "+10% rail velocity.",                       apply: (s, k = 1) => { s.bulletSpeed *= 1 + 0.1 * k; } },
  { id: "s_h_charge", pilot: 7, price: 45, once: false, name: "Charge Cap",    desc: "Triple Rail fires +1 rail.",                apply: (s, k = 1) => { s.railShots += 1; } },
  { id: "s_h_cdr",    pilot: 7, price: 35, once: false, name: "Quick Charge",  desc: "Triple Rail cooldown −1s.",                 apply: (s, k = 1) => { s.abilityCdr += 1 * k; } },
  // ================ ANY CLASS ================
  { id: "s_g_plate",  pilot: null, price: 50, once: false, heal: 1, name: "Plating Kit",  desc: "+1 max HP (heal 1 now).",        apply: (s, k = 1) => { s.maxHp += 1; } },
  { id: "s_g_bombs",  pilot: null, price: 30, once: false, flat: true, bombs: 2, name: "Bomb Rack",   desc: "+2 smart bombs, right now.",     apply: () => { } },
  { id: "s_g_adren",  pilot: null, price: 25, once: false, flat: true, heal: 99, name: "Adrenal Injector", desc: "Full heal, right now.",     apply: () => { } },
  { id: "s_g_jets",   pilot: null, price: 30, once: false, name: "Boot Jets",   desc: "+4% move speed.",                          apply: (s, k = 1) => { s.speed *= 1 + 0.04 * k; } },
  { id: "s_g_dash",   pilot: null, price: 30, once: false, name: "Servo Dash",  desc: "Dash recovers 0.15s faster.",              apply: (s, k = 1) => { s.dashBonus = (s.dashBonus ?? 0) + 0.15 * k; } },
  { id: "s_g_lucky",  pilot: null, price: 35, once: false, name: "Lucky Charm", desc: "Enemies drop consumables 15% more often.", apply: (s, k = 1) => { s.dropLuck += 0.15 * k; } },
  { id: "s_g_revive", pilot: null, price: 40, once: false, name: "Medic Badge", desc: "You revive teammates 20% faster.",         apply: (s, k = 1) => { s.reviveSpeed *= 1 + 0.2 * k; } },
];

export function shopItemById(id) { return SHOP_ITEMS.find(i => i.id === id); }

// The personal storefront: your class's items + the generics.
export function shopFor(pilot) {
  return SHOP_ITEMS.filter(i => i.pilot === null || i.pilot === pilot);
}
