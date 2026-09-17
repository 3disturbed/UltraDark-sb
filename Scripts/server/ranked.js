// -----------------------------------------------------------------------------
// ranked — UltraDark's scores table and Daily Dark seeds, on Darks Games.
//
// The original server kept its leaderboards in SQLite (server/db.js): a run's
// score was written at run end from the server's own state, never from a client,
// and each day's Daily Dark seed was drawn once and kept. On the engine the boards
// are Darks Games leaderboards, and only DarkShapes' official dedicated server can
// write them (DG.ranked: `node html5/tools/server.js <project> --dg <slug>` with the
// app secret), so a score still comes from nowhere but the server's own simulation.
// This is the part of db.js a room calls:
//
//   getDailySeed(date)   the day's seed. The original drew one at random and kept it in
//                        its database; with no database the seed is the date's hash, so
//                        every server, every restart and every Daily played offline
//                        meets the same waves that day.
//   submit(run)          at run end, each verified machine's score goes to the runs
//                        boards (all time and this week) or to the Daily Dark board,
//                        with the wave, squad and names the original kept in its row.
//                        A machine is one account, so couch seats count once.
//
// The hub answers a submission later, player by player, where SQLite answered at
// once; each answer is sent to that player's socket as EVENT {t: "ranked", mode,
// counted, rank}, which the score screen shows as the original showed its rank. The
// Daily Dark's one attempt is the hub's rule, per account, where the original's was
// per lead callsign; a second attempt answers counted: false.
// -----------------------------------------------------------------------------

import { MSG, encodeJson } from "../shared/protocol.js";

/** The boards the ranked server registers: the original's run table, by all time and by week, and the daily. */
export const BOARDS = Object.freeze([
  Object.freeze({ key: "runs", name: "Runs" }),
  Object.freeze({ key: "runs_weekly", name: "This week", period: "weekly" }),
  Object.freeze({ key: "daily", name: "Daily Dark", period: "daily", attemptsPerPeriod: 1, personalBests: false }),
]);

/** The Daily Dark seed for a UTC date, yyyy-mm-dd: FNV-1a of the date, the same everywhere. */
export function dailySeed(date) {
  const text = `darkshapes-daily:${date}`;
  let h = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  return h >>> 0;
}

/** db.js for a room on the ranked server: the daily seed, and run-end scores sent to the hub. */
export class Ranked {
  /**
   * @param {object} options
   * @param {object} options.dg The engine's DG global, or anything with its ranked members.
   * @param {(message: string) => void} [options.warn] Where a board the hub refused is reported.
   */
  constructor({ dg, warn = () => {} }) {
    this.dg = dg;
    this.warn = warn;
    this.waiting = new Map();   // `${playerId}/${key}` -> { ws, mode } for a score the hub has not answered
    dg.on("score", (playerId, result) => this.scored(playerId, result));
    dg.on("registered", (kind, result) => {
      if (kind === "leaderboards" && result && result.ok === false) warn(`DarkShapes: the hub refused the boards: ${result.error}`);
    });
    dg.registerLeaderboards(BOARDS.map((board) => ({ ...board })));
  }

  getDailySeed(date) {
    return dailySeed(date);
  }

  /**
   * A finished run, as room.js reports it: its mode, score, wave, squad and names, and the room's
   * clients, each { ws, account }. Returns null: a place arrives later, for each account alone.
   */
  submit({ mode, squad, score, wave, names, clients }) {
    const keys = mode === "daily" ? ["daily"] : ["runs", "runs_weekly"];
    const meta = { wave, squad, names };
    const machines = new Set();
    for (const client of clients) {
      const playerId = client.ws.sender;
      if (!client.account || machines.has(playerId)) continue;
      machines.add(playerId);
      for (const key of keys) {
        this.waiting.set(`${playerId}/${key}`, { ws: client.ws, mode });
        this.dg.submitScore(playerId, key, score, meta);
      }
    }
    return null;
  }

  /** The hub's answer to one submission: the player hears their place on the board the score screen names. */
  scored(playerId, result) {
    const id = `${playerId}/${result?.key}`;
    const asked = this.waiting.get(id);
    if (asked === undefined) return;
    this.waiting.delete(id);
    if (result.key === "runs_weekly") return;   // the screen shows the world rank, as the original did
    const counted = result.ok === true;
    if (!counted && result.error !== "attempts_exhausted") return;   // no place to show, and nothing to own up to
    const ev = { t: "ranked", mode: asked.mode, counted, rank: counted ? result.rank : null };
    if (asked.ws.readyState === 1) asked.ws.send(encodeJson(MSG.EVENT, ev));
  }
}
