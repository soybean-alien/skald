// Demo driver for the real Skald dashboard page: replaces /api/* with a scripted sample timeline.
// Time comes from performance.now(), so headless Chrome's --virtual-time-budget picks the moment to capture.
(function () {
  const _st = window.setTimeout, _si = window.setInterval;
  window.setTimeout = (f, d, ...a) => _st(f, Math.min(d || 0, 200), ...a);   // poll fast for the demo
  window.setInterval = (f, d, ...a) => _si(f, Math.min(d || 0, 200), ...a);
  try { localStorage.setItem('skaldTok', 'demo-token'); } catch (e) {}       // show admin buttons

  const BASE = Date.parse('2026-10-15T19:40:00Z');
  const at = s => new Date(BASE + s * 1000).toISOString();
  const T = () => performance.now() / 1000;            // seconds since page load

  const ev = (s, type, actor, target, detail, pos) => ({ s, t: at(s), type, actor, actorId: null, target, detail, pos: pos || [0, 0, 0] });
  // Older history (negative s) plus the timeline that plays during the GIF.
  const events = [
    ev(-600, 'join', 'Ragnhild'), ev(-560, 'join', 'Bjorn'), ev(-540, 'join', 'Sigrid'), ev(-500, 'join', 'Ulf'),
    ev(-420, 'built', 'Sigrid', 'piece_chest_wood', null, [212, 34, -88]),
    ev(-380, 'chest_add', 'Sigrid', 'piece_chest_wood', 'Wood x50, Stone x30 (chest built by Sigrid)', [212, 34, -88]),
    ev(-300, 'death', 'Bjorn', 'grave', 'dropped: Bronze x12, ArmorBronzeChest x1, AtgeirBronze x1, Mead x3', [640, 28, 410]),
    ev(-240, 'grave_take', 'Bjorn', 'grave of Bjorn', 'Bronze x12, ArmorBronzeChest x1, AtgeirBronze x1, Mead x3', [640, 28, 410]),
    ev(-90, 'removed', 'Ulf', 'woodwall', 'hammer; built by Sigrid', [214, 35, -90]),
    ev(1.5, 'boss_summoned', 'Ragnhild', 'Bonemass', null, [1180, 31, 2240]),
    ev(4.0, 'chest_take', 'Ulf', 'piece_chest_wood', 'Iron x20, Silver x8 (chest built by Sigrid)', [212, 34, -88]),
    ev(6.5, 'cheat_flag', 'Ulf', 'teleported without a portal', '1,204 m in 0.1 s, from 212,-88 to 1180,2240', [1180, 31, 2240]),
    ev(12.5, 'boss_killed', 'Ragnhild', 'Bonemass', '1:34 fight; fighters: Ragnhild, Bjorn, Sigrid; summoned by Ragnhild', [1176, 30, 2236]),
    ev(12.6, 'boss_defeated', 'Ragnhild', 'bonemass', 'first kill on this world (progression)', [1176, 30, 2236]),
    ev(14.0, 'admin_save', 'dashboard admin', 'server', 'world saved from dashboard'),
  ];

  const bossNames = ['Eikthyr', 'The Elder', 'Bonemass', 'Kall Fimbulbringer', 'Moder', 'Yagluth', 'Hive', 'The Queen', 'Fader'];
  function bosses(t) {
    const fightStart = 1.5, fightEnd = 12.5;
    const defeated = new Set(['Eikthyr', 'The Elder']);
    if (t >= fightEnd) defeated.add('Bonemass');
    const history = [
      { boss: 'The Elder', outcome: 'killed', start: at(-5400), end: at(-5290), seconds: 110, killer: 'Bjorn', fighters: ['Bjorn', 'Ragnhild'], summoner: 'Bjorn' },
      { boss: 'Eikthyr', outcome: 'killed', start: at(-9000), end: at(-8958), seconds: 42, killer: 'Ragnhild', fighters: ['Ragnhild'], summoner: 'Ragnhild' },
    ];
    if (t >= fightEnd) history.unshift({ boss: 'Bonemass', outcome: 'killed', start: at(fightStart - 83), end: at(fightEnd), seconds: 94, killer: 'Ragnhild', fighters: ['Ragnhild', 'Bjorn', 'Sigrid'], summoner: 'Ragnhild' });
    const active = [];
    if (t >= fightStart && t < fightEnd) {
      const p = (t - fightStart) / (fightEnd - fightStart);
      const fighters = ['Ragnhild'].concat(t > 3 ? ['Bjorn'] : []).concat(t > 5 ? ['Sigrid'] : []);
      active.push({ boss: 'Bonemass', outcome: 'in progress', fighting: true, seconds: Math.round(11 + p * 83),
        hpPct: Math.max(3, Math.round(100 - p * 97)), summoner: 'Ragnhild', fighters, pos: [1180, 31, 2240] });
    }
    return { active, history, progression: bossNames.map(b => ({ boss: b, defeated: defeated.has(b) })) };
  }

  function flags(t) {
    if (t < 6.5) return [];
    return [{ player: 'Ulf', actorId: null, trusted: false, reasons: [
      { reason: 'teleported without a portal', count: 1, first: at(6.5), last: at(6.5), dismissed: false, example: '1,204 m in 0.1 s, from 212,-88 to 1180,2240' },
      { reason: 'took spawned items', count: 2, first: at(-200), last: at(4), dismissed: false, example: 'SwordCheat x1' },
    ]}];
  }

  window.fetch = async (url) => {
    const t = T(), path = String(url).split('?')[0];
    let body;
    if (path.endsWith('/api/events')) body = events.filter(e => e.s <= t).slice().reverse().map(({ s, ...e }) => e);
    else if (path.endsWith('/api/bosses')) body = bosses(t);
    else if (path.endsWith('/api/flags')) body = flags(t);
    else if (path.endsWith('/api/status')) body = { restartSecondsLeft: t >= 15 ? Math.max(0, 60 - Math.floor((t - 15) * 3)) : null, restartMode: 'relaunch' };
    else body = { ok: true };
    return new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' } });
  };
})();
