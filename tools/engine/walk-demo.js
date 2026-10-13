// Writes a demo that walks the player round a map, for tools/engine/port-check.sh: forward at a run, a turn, the use key
// every second, the fire key now and then. Formats: vanilla (Doom 1.9, version 109: Chocolate, Crispy, any port), boom
// (version 202, complevel 9) and mbf21 (version 221, complevel 21, long tics), with the headers dsda-doom writes
// (prboom2 g_game.c G_BeginRecording, dsda/options.c). The demo ends with its marker, so -timedemo times it and quits.
//   node tools/engine/walk-demo.js OUT.lmp FORMAT SLOT [SKILL 1-5] [TICS]
const fs = require("fs");
const [out, format, slot, skillArg = "3", ticsArg = "700"] = process.argv.slice(2);
if (!out || !["vanilla", "boom", "mbf21"].includes(format) || !slot) {
  console.error("usage: walk-demo.js OUT.lmp vanilla|boom|mbf21 SLOT [SKILL] [TICS]");
  process.exit(2);
}
const m = /^E(\d)M(\d)$/i.exec(slot) || /^MAP(\d\d)$/i.exec(slot);
if (!m) { console.error(`walk-demo: ${slot} is not ExMy or MAPxx`); process.exit(2); }
const episode = m.length === 3 ? +m[1] : 1, map = m.length === 3 ? +m[2] : +m[1];
const skill = Math.min(5, Math.max(1, +skillArg)) - 1, tics = +ticsArg;
const bytes = [];
const players = [1, 0, 0, 0];
if (format === "vanilla") {
  bytes.push(109, skill, episode, map, 0, 0, 0, 0, 0, ...players);
} else {
  const v = format === "boom" ? 202 : 221;
  bytes.push(v, 0x1d, ...(format === "boom" ? [..."Boom"].map(c => c.charCodeAt(0)) : [..."MBF"].map(c => c.charCodeAt(0))), 0xe6);
  if (format === "mbf21") bytes.push(0);      // the MBF signature's sixth byte
  else bytes.push(0);                          // Boom: the compatibility flag (0 = Boom 2.02)
  bytes.push(skill, episode, map, 0, 0);       // skill, episode, map, deathmatch, console player
  if (format === "boom") {
    // monsters_remember, variable_friction, weapon_recoil, allow_pushers, 0, player_bobbing, respawn, fast, nomonsters,
    // demo_insurance, rngseed (4); the rest of the 64 bytes is MBF's and read only at MBF levels.
    const o = [1, 1, 0, 1, 0, 1, 0, 0, 0, 0, 0, 0, 0, 1];
    bytes.push(...o, ...new Array(64 - o.length).fill(0));
  } else {
    // monsters_remember, weapon_recoil, player_bobbing, respawn, fast, nomonsters, rngseed (4), infighting, dogs,
    // distfriend (2), backing, avoid hazards, friction, help friends, dog jumping, monkeys, then 25 compatibility flags.
    bytes.push(1, 0, 1, 0, 0, 0, 0, 0, 0, 1, 1, 0, 0, 128, 0, 1, 1, 0, 1, 0, 25, ...new Array(25).fill(0));
  }
  bytes.push(...players, ...new Array(32 - players.length).fill(0));
}
const longtics = format === "mbf21";
for (let t = 0; t < tics; t++) {
  const phase = t % 70;
  const forward = phase < 50 ? 50 : 0;
  const turn = phase >= 50 && phase < 60 ? 8 : 0;          // 8 × 256 angle units a tic: about 110° over the turn
  const buttons = (t % 35 === 0 ? 2 : 0) | (t % 140 === 100 ? 1 : 0);
  bytes.push(forward & 0xff, 0);
  if (longtics) bytes.push(0, turn & 0xff); else bytes.push(turn & 0xff);
  bytes.push(buttons);
}
bytes.push(0x80);
fs.writeFileSync(out, Buffer.from(bytes));
console.log(`walk-demo: ${out}: ${format}, ${slot} (episode ${episode} map ${map}), skill ${skill + 1}, ${tics} tics`);
