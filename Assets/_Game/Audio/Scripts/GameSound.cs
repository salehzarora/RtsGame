/// <summary>
/// Stable id for every one-shot SFX the game can play. This enum is the KEY
/// that <see cref="SoundLibrary"/> maps to a <see cref="SoundEvent"/> (clips +
/// variation rules), and that call sites pass to
/// <see cref="AudioManager.Sfx"/> / <see cref="AudioManager.SfxAt"/>.
///
/// Categories are expressed by the name prefix and grouped in the library
/// Inspector. To ADD a new sound:
///   1. Add a value at the END of the relevant group (don't reorder — existing
///      library entries are matched by enum value).
///   2. In the SoundLibrary asset, run "Populate Missing Entries" (context menu
///      or the Setup Audio System tool) and drop a clip into the new row.
///
/// Music + ambience are NOT in this enum — they're long looping tracks handled
/// directly by <see cref="AudioManager"/> (menuMusic / gameplayMusic /
/// battlefieldAmbience fields) so they can crossfade and avoid duplicates.
/// </summary>
public enum GameSound
{
    // ---- UI ---------------------------------------------------------- //
    UIButtonClick,
    UIButtonHover,
    UIOpenPanel,
    UIClosePanel,
    UIError,
    UIConfirm,

    // ---- Units (local-player feedback) ------------------------------- //
    UnitSelect,
    UnitMoveOrder,
    UnitAttackOrder,
    UnitGatherOrder,
    UnitDamaged,
    UnitDeath,

    // ---- Combat (positional / world-space) --------------------------- //
    Gunfire,
    TurretFire,
    RocketLaunch,
    MissileLaunch,
    ArtilleryLaunch,
    Explosion,
    Impact,

    // ---- Buildings / construction ------------------------------------ //
    BuildingPlace,
    ConstructionLoop,
    ConstructionComplete,
    BuildingDestroyed,

    // ---- Resources --------------------------------------------------- //
    ResourceGather,
}
