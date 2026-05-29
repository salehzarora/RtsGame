SFX folder
==========

Drop your short one-shot sound effects here (.wav / .ogg), then assign them in
the GameSoundLibrary asset (Assets/_Game/Audio/GameSoundLibrary.asset).

Each row in the library is a GameSound id with a list of clips — if you add
several clips to one row, a random one plays each time (great for gunfire,
impacts, footsteps, etc.).

Sounds the game expects (assign at least the ones you care about):
  UI:        UIButtonClick, UIButtonHover, UIOpenPanel, UIClosePanel,
             UIError, UIConfirm
  Units:     UnitSelect, UnitMoveOrder, UnitAttackOrder, UnitGatherOrder,
             UnitDamaged (optional), UnitDeath
  Combat:    Gunfire, TurretFire, RocketLaunch, MissileLaunch,
             ArtilleryLaunch, Explosion, Impact
  Buildings: BuildingPlace, ConstructionLoop (looping), ConstructionComplete,
             BuildingDestroyed
  Resources: ResourceGather (optional placeholder)

Anything left empty simply stays silent — the game still runs.

LICENSING: use only royalty-free / CC0 / properly-licensed audio. Do NOT add
copyrighted sound files. Good free sources: Kenney.nl (CC0), freesound.org
(check each file's license), sonniss.com GDC bundles.
