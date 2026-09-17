// Consumables — dropped by chunkier enemies, carried (max 3), used on a
// button. Server applies all effects; the client only renders glyphs.

export const CK = { REPAIR: 1, SHIELD: 2, FRENZY: 3, STASIS: 4, BOMB: 5 };

export const CONSUMABLES = {
  [CK.REPAIR]: { name: "Repair Kit",    glyph: "✚", color: "#b8ff5e", desc: "Restore 1 HP" },
  [CK.SHIELD]: { name: "Overshield",    glyph: "◈", color: "#39f0ff", desc: "3s invulnerable" },
  [CK.FRENZY]: { name: "Frenzy Core",   glyph: "⚡", color: "#ffe45b", desc: "Double fire rate, 6s" },
  [CK.STASIS]: { name: "Stasis Charge", glyph: "❄", color: "#8fb4ff", desc: "Enemies slowed, 5s" },
  [CK.BOMB]:   { name: "Bomb Cell",     glyph: "●", color: "#ff7a3d", desc: "+1 smart bomb" },
};

// weighted drop table — repairs common, bombs precious
const DROP_TABLE = [
  [CK.REPAIR, 3], [CK.SHIELD, 2], [CK.FRENZY, 2], [CK.STASIS, 2], [CK.BOMB, 1],
];
const TOTAL = DROP_TABLE.reduce((a, [, w]) => a + w, 0);

export function rollConsumable(rng) {
  let r = rng() * TOTAL;
  for (const [kind, w] of DROP_TABLE) {
    if ((r -= w) < 0) return kind;
  }
  return CK.REPAIR;
}
