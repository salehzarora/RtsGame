# Project History — major passes

A one-line-per-pass index. Details for each pass live in PROGRESS_REPORT.md
(addenda, newest at the bottom) and TESTING_GUIDE.md (how to verify).

| When | Pass | Outcome |
|---|---|---|
| ~0.5.x | Core RTS loop | Camera, selection, movement, combat, gathering, building, production |
| ~0.5.6–0.5.9 | Aircraft + soldier art | StrikeJet with yaw/pitch/roll physics, rigged soldier + custom run |
| 2026-06 | FileBridge era | Official Unity MCP dead (entitlement 0) → file-based editor bridge + BridgeOps op library |
| 2026-06 | VFX pass | Procedural CombatVFX: muzzle flashes, impacts, 3-size explosions, scorch, smoke |
| 2026-06 | Battlefield pass | Central crossroads + outposts dressing, cover objects, fuel-barrel hazards |
| 2026-06 | (cancelled) skirmish | SkirmishDirector/EnemyAssaultBrain built then DEMOTED to dev-only manual tools — game direction is ONLINE PVP |
| 2026-06 | PvP flow pass | MainMenu → room → match start → corner spawn verified on real Photon (single client) |
| 2026-06-12 | TWO-CLIENT PVP pass | Editor + build, 3 live matches; fixed AutomaticallySyncScene follower bug, NetSnap warning flood, LeaveRoom menu return; verified movement/damage/production/combat-kill sync end-to-end |
| 2026-06-12 | GAME FEEL pass | Camera shake, firing recoil, movement dust, vehicle lean, command markers, explosion overhaul (core/debris/shockwave), camera smoothing, scene lighting (trilight ambient + soft shadows), health-bar sizing, sandbox tidy |

Direction (standing): singleplayer-prototype codebase steering toward
**online player-vs-player RTS**. AI-wave/skirmish content stays dev-only,
autoStart off, hard-gated out of multiplayer rooms.
