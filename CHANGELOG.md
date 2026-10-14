# Changelog

## 0.1.0 (test release, GPL-3.0)
Tested on Valheim 1.0.17 (network 40), Windows dedicated server, BepInExPack_Valheim 5.4.2351.

- Audit log: chest take/store with item names and chest builder; built, removed and destroyed pieces with attribution;
  deaths with full gravestone contents; gravestone looting; joins and leaves.
- Boss tracker: live fight banner (HP, timer, fighters, summoner), fight history, world progression, startup scan
  for bosses left alive.
- Cheat flags: Valheim's own cheat tracking plus server-side checks (Server devcommands fingerprint, teleports
  without a portal, bosses spawned away from their altar). Dismiss and Trust from the dashboard.
- Admin alerts: in-game (centre of screen and chat box) and an optional Discord webhook.
- Dashboard admin actions: Save now, Save & restart with in-game countdown (auto-relaunch on Windows), Cancel.
- Server frame-rate cap (default 60 fps): about 95% less dedicated-server CPU in testing.
- `/api/perf` self-measurement. Measured overhead is about 1.4 ms of server time per second.
