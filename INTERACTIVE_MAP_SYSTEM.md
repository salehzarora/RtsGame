# Interactive Tactical Map System

Adds neutral, interactable map features to the RTS: **destructible bridges,
explosive fuel tanks, enterable garrison buildings, watch towers, and tunnels**.
Built to slot into the existing multiplayer model with **no PhotonView, no
`PhotonNetwork.Instantiate`**, reusing the deterministic
`GameEntity` / `EntityRegistry` identity and the custom `RaiseEvent` pipeline.

---

## 1. What was added

### New runtime scripts — `Assets/_Game/Scripts/Map/`
| File | Role |
|---|---|
| `MapInteractable.cs` | Abstract base. Caches the sibling `GameEntity`, exposes the `Authoritative` gate (single-player **or** Photon master), and shared helpers (`IsInfantry`, `OwnerOf`). |
| `DestructibleMapObject.cs` | Phase A base. Reuses `Health` for networked damage/death; runs a one-time destroyed-state transition (visual swap, collider toggles, VFX/SFX, `OnDestroyed` event). |
| `ExplosiveMapObject.cs` | Phase B. Fuel tanks — area damage on destruction (authoritative-only), chain reactions, procedural blast flash on every client. |
| `DestructibleBridge.cs` | Phase C. Swaps to a collapsed visual and blocks crossing via a carving `NavMeshObstacle` + a physical blocker collider. |
| `GarrisonBuilding.cs` | Phase D. Infantry occupancy (instant enter, eject exit), fire-from-building scaling with occupant count, destroy → eject/kill. |
| `WatchTower.cs` | Phase E. Small-capacity garrison variant; neutral until captured, capture-tinted indicator, longer range, placeholder vision radius. |
| `TunnelEntrance.cs` | Phase F. Paired infantry teleport with travel delay + cooldown. |
| `MapInteractableNetworkEvents.cs` | Occupancy/travel sync (garrison enter/exit, tunnel travel) — a separate Photon `IOnEventCallback` so the proven `NetworkMatchEvents` file is untouched. |
| `MapInteractionRouter.cs` | The single right-click hook into `UnitSelector`; routes clicks to enter/exit/travel/attack. |

### Editor helpers — `Assets/_Game/Editor/CreateMapInteractables.cs`
Menu **Tools → RTS → Map → …**: Create Fuel Tank / Destructible Bridge /
Garrison Building / Watch Tower / Tunnel Pair, and **Setup Map Network Events**.

### Files changed (minimal, additive)
| File | Change |
|---|---|
| `Core/GameEntity.cs` | Added `EntityType.MapObject` (appended — existing serialized indices preserved). |
| `Units/Health.cs` | Added `destroyObjectOnDeath` (default **true** → unchanged behaviour) so map objects can persist in a destroyed state; added `ReviveFull()` for reset/repair. |
| `Units/UnitSelector.cs` | One new right-click branch calling `MapInteractionRouter.TryRouteRightClick`. |
| `Editor/ValidateMultiplayerOwnership.cs` | Exempt neutral `MapObject` from the "neutral non-resource" warning. |

Nothing else in movement, combat, economy, production, aircraft, team-color, or
selection was modified.

---

## 2. How to place each object

All creators drop a fully-wired placeholder (Unity primitives) under a
**`MapObjects`** root at the Scene-view pivot. Swap the meshes for art later.

1. **Fuel Tank** — `Tools → RTS → Map → Create Fuel Tank`. Place near a
   chokepoint or unit cluster. Done.
2. **Destructible Bridge** — `Create Destructible Bridge`. Position the deck so
   it spans a gap **on the baked NavMesh**, then rebake the NavMesh. The deck
   must be walkable NavMesh while intact; on destruction it's carved out.
3. **Garrison Building** — `Create Garrison Building`. Place anywhere infantry
   can reach.
4. **Watch Tower** — `Create Watch Tower`. Neutral until captured.
5. **Tunnel Pair** — `Create Tunnel Pair`. Creates two linked entrances; move
   them to the two ends of your secret path (the link is already set both ways).

**Multiplayer:** add the `MapObjects` root to the scene's `GameplayWorldRoot`
target list so map objects stay hidden until MatchStart, and run
`Tools → RTS → Map → Setup Map Network Events` once to add the occupancy event
component to the `NetworkManager`.

> Map objects get a **deterministic, scene-serialized** `GameEntity` id baked by
> the creator. Because both clients load the same scene asset, the id matches
> across the network automatically — you do **not** need to run "Add GameEntity
> To Scene Objects" for them.

---

## 3. Required components per object

| Object | Components |
|---|---|
| Fuel Tank | `GameEntity` (MapObject, owner −1) · `Health` · `ExplosiveMapObject` · a collider |
| Bridge | `GameEntity` · `Health` · `DestructibleBridge` · deck collider · blocker collider · child carving `NavMeshObstacle` (disabled) |
| Garrison | `GameEntity` · `Health` · `DestructibleMapObject` · `GarrisonBuilding` · collider · exit points |
| Watch Tower | `GameEntity` · `Health` · `DestructibleMapObject` · `WatchTower` · collider · owner-indicator renderer · exit points |
| Tunnel | `GameEntity` · `TunnelEntrance` · collider · exit point (no Health — not destructible by default) |

---

## 4. Multiplayer authority rules

| Concern | Authority | Mechanism |
|---|---|---|
| Damage value to a map object | Master-authoritative | Existing `Health.TakeDamage` → master broadcasts resulting health; others snap. |
| "Object destroyed" | Idempotent, any-client + snapshot | Existing `EntityDestroyed` event + the 0.5 s `EntityStateSnapshot` (health) — even a dropped event self-heals, and **late-joiners** get the destroyed state from the snapshot. |
| Explosion area damage | **Authoritative only** (`Authoritative`) | Applied once on SP/master; each victim's health/death then replicates through the normal events. Non-authoritative clients render the flash only. |
| Chain reactions | Authoritative | A victim explosive's own death triggers its own (authoritative) blast; terminates via `Health.dying` / `IsDestroyed` guards. |
| Garrison enter/exit, tunnel travel | Commanding (owner) client | `MapInteractableNetworkEvents` (codes 20–22): owner applies locally + broadcasts; receivers apply local-only (no echo). |
| Watch-tower capture color | Local, deterministic | Driven by the synced occupancy + `MultiplayerColors`. The tower's `GameEntity` stays neutral. |

**No duplicate damage:** units only ever take damage from the authoritative
side; remote clients receive the resulting value, never re-compute it.

---

## 5. How to test

### Single-player
1. Create one of each object near your base.
2. **Fuel tank:** select a tank/artillery, right-click the fuel tank → it
   explodes and damages nearby units/buildings. Put two tanks close → chain.
3. **Bridge:** send units across, then destroy the bridge (aircraft / artillery
   / fuel-tank blast) → new move orders can't cross.
4. **Garrison:** select infantry, right-click the building → they vanish inside
   and it fires at enemies. Right-click again → they pop back out. Destroy it →
   occupants are ejected (or killed, per Inspector).
5. **Watch tower:** garrison infantry → the flag tints to your color and it
   fires at long range.
6. **Tunnel:** select infantry, right-click tunnel A → they disappear and
   re-emerge at tunnel B after the delay.

### 2-client multiplayer
Run two clients (see `PHOTON_SETUP.md`). After **Setup Map Network Events**:
- **Fuel tank / bridge:** destroy on one client → both show the destroyed state,
  blast, and (bridge) blocked crossing; victim health drops **once**.
- **Garrison / tower:** garrison on one client → the other sees the units vanish
  and the building occupied/captured. Eject → both see them re-emerge.
- **Tunnel:** travel on one client → both see the unit emerge at the far side.
- **Late join (bridge/tank):** destroy it, then have a client join → the
  0.5 s snapshot drives the destroyed state on the new client.

---

## 6. Known limitations / TODOs

- **Walk-in animation:** garrison/tower entry is **instant on command** (no
  walk-to-door animation). Far units are sent walking and entered on a second
  click. TODO: a boarding-style approach agent like the APC's.
- **Idle-aim on persistent objects:** a unit that manually attacks a *persistent*
  destructible (bridge/tower) and destroys it may keep aiming at the husk until
  re-ordered (its collider is disabled but the combat keeps the reference).
  Fuel-tank attackers are unaffected in practice. Cosmetic only.
- **Late-join occupants:** a player joining *after* units have garrisoned won't
  see the existing occupants hidden until they next change (occupant ids are
  stored but not yet re-applied on join). Destruction/late-join works.
- **Bridge NavMesh:** path-blocking relies on a carving `NavMeshObstacle`; the
  deck must be on the baked NavMesh and the obstacle must cover it. No runtime
  NavMesh rebake is done (deliberate — avoids desync).
- **Repair/rebuild:** `repairableFutureHook` and `ReviveFull()` exist; full
  repair gameplay is not implemented. Match-restart in the same Play session
  resets map objects via `GameStateManager.OnGameReset`.
- **Vision:** watch-tower `visionRadius` is a placeholder (gizmo + log only) —
  there is no fog-of-war system yet. Range bonus for firing IS active.

---

## 7. Recommended next polish

- Walk-to-enter agent for garrisons/tunnels (reuse the APC boarding pattern).
- Real art + particle prefabs for the destroyed visuals / blast / tunnel poof
  (assign `destroyedVfx`, swap the placeholder meshes).
- Fog-of-war so watch towers grant real vision.
- A small HUD readout of garrison occupancy / tower holder for the selected /
  hovered building.
- Late-join occupant restore (re-hide occupants from a join-time snapshot).
- Repairable bridges (a dozer re-builds a collapsed bridge).
