using UnityEngine;

/// <summary>
/// Temporary "foundation" placeholder spawned by <see cref="BuildingPlacementManager"/>
/// when a Dozer is ordered to build a new structure. The site sits inert on the
/// ground (no PowerPlant supply, no PowerConsumer demand, no production) until a
/// <see cref="DozerBuilder"/> drives up and feeds it progress.
///
/// Lifecycle:
///   1. <see cref="BuildingPlacementManager"/> instantiates the site via the
///      <see cref="constructionSitePrefab"/> path, then calls <see cref="Initialise"/>
///      with the final building prefab + cost + label + Dozer reference.
///   2. The assigned Dozer (or any later resume order) calls
///      <see cref="AddBuildProgress"/> each frame while in range.
///   3. When <see cref="currentProgress"/> reaches 1, <see cref="Complete"/>
///      spawns the real building prefab at the same position + rotation and
///      destroys the construction site.
///
/// Visual:
///   • Root GameObject carries this script + a BoxCollider (Building layer).
///   • Child "Foundation" — flat grey cube (sized to the BPM footprint).
///   • Child "ProgressBar" — small world-space cube above the foundation; its
///     X scale is driven from 0 → 1 as construction advances.
///
/// What this script does NOT do:
///   • Block placement of OTHER construction sites — overlap checks in BPM
///     already see the Building-layer collider here.
///   • Register power / demand / production — only the FINAL building does that,
///     after <see cref="Complete"/> swaps it in.
///   • Take damage. Health is intentionally absent: a half-built site cannot
///     be destroyed by enemies in this milestone. Future enemy AI can add a
///     Health component to this prefab and the same flow will still work.
/// </summary>
[DisallowMultipleComponent]
public class ConstructionSite : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector — prefab references
    // ------------------------------------------------------------------ //

    [Header("Visual References (assigned by Create Construction Site Prefab tool)")]
    [Tooltip("Foundation child — flat grey/translucent cube that represents the site footprint. " +
             "Destroyed alongside the site root when construction completes.")]
    public GameObject foundationVisual;

    [Tooltip("Child Transform used to drive the progress bar fill. Its X scale is animated " +
             "from 0 to <see cref=\"progressBarMaxScaleX\"/> as construction advances. " +
             "Tooltips assume the bar is left-anchored via its parent pivot offset.")]
    public Transform progressBarFill;

    [Tooltip("Max world-space X scale of the progress bar fill at 100%. The bar grows from 0 " +
             "to this value. Match to the bar background's X scale set by the prefab tool.")]
    public float progressBarMaxScaleX = 1.6f;

    [Header("Ownership")]
    [Tooltip("Which team commissioned this site. Player sites default to Player and are " +
             "the only ones the player can right-click to resume building. Enemy sites are " +
             "set by EnemyBuildAI and ignored by player right-click. The final building " +
             "carries its own team via its prefab's Health.team.")]
    public Health.Team ownerTeam = Health.Team.Player;

    // ------------------------------------------------------------------ //
    // Public read-only runtime state
    // ------------------------------------------------------------------ //

    /// <summary>Prefab spawned via Instantiate when construction completes.</summary>
    public GameObject FinalBuildingPrefab { get; private set; }

    /// <summary>Cost already deducted by BPM when the site was placed. Stored for logs only.</summary>
    public int BuildCost { get; private set; }

    /// <summary>How many seconds of dozer contact it takes to complete this site.</summary>
    public float BuildTime { get; private set; } = 1f;

    /// <summary>Normalised progress 0..1.</summary>
    public float CurrentProgress { get; private set; }

    /// <summary>True once <see cref="Complete"/> swaps in the final building.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Display label for HUD / Console logs (e.g. "Barracks", "Airfield").</summary>
    public string BuildingLabel { get; private set; } = "Building";

    /// <summary>Read by external code (e.g. <see cref="DozerBuilder"/>) to detect a dead site.</summary>
    public bool IsAlive => this != null && !IsComplete && gameObject != null;

    /// <summary>The dozer currently working on this site, or null if abandoned.</summary>
    public DozerBuilder AssignedDozer { get; private set; }

    /// <summary>
    /// Canonical owner that commissioned the site (= the Dozer's owner at
    /// build-command time). Stamped by <see cref="BuildingPlacementManager.SpawnConstructionSite"/>
    /// via <see cref="SetOwner"/> immediately after spawn so the final
    /// building inherits the correct owner in <see cref="Complete"/> even if
    /// no <see cref="GameEntity"/> is present on the site (legacy prefab) or
    /// the dispatcher's post-spawn <c>EntityRegistry.Find</c> misses.
    ///
    /// Defaults to <see cref="GameEntity.NeutralOwnerId"/> (-1). Any value
    /// other than that is treated as authoritative by <see cref="Complete"/>;
    /// only when it stays neutral do we fall back to the site's GameEntity.
    /// </summary>
    public int OwnerPlayerId { get; private set; } = GameEntity.NeutralOwnerId;

    /// <summary>
    /// Fires once when construction completes, immediately before this site
    /// destroys itself. The argument is the freshly spawned final building
    /// instance. Used by enemy AI (and any future code) to parent / decorate
    /// the placed building without coupling back into ConstructionSite.
    /// </summary>
    public event System.Action<GameObject> OnComplete;

    // ------------------------------------------------------------------ //
    // Private
    // ------------------------------------------------------------------ //

    // Stored at Initialise so Complete() spawns the building facing the same way the ghost did.
    private Quaternion finalRotation = Quaternion.identity;

    // Phase 2 (multiplayer prep): network-allocated id the spawned final
    // building must adopt so every client agrees on its EntityId. Set by
    // BuildingPlacementManager.SetFinalBuildingEntityId immediately after
    // Initialise. Empty in the single-player path → falls back to GUID.
    private string networkFinalBuildingEntityId = "";

    // --- Construction audio ------------------------------------------------ //
    // A self-owned looping AudioSource that plays only while a dozer is actively
    // feeding progress, and stops the moment construction completes or the site
    // is destroyed/cancelled (the source dies with this GameObject). The clip +
    // base volume come from the AudioManager's SoundLibrary so it's all wired in
    // one place. Null-safe: stays silent if no AudioManager / clip exists.
    private AudioSource buildLoopSource;
    private float       lastProgressTime = -999f;
    private bool        loopClipResolved;
    private float       loopEventVolume = 0.5f;

    private void Awake()
    {
        buildLoopSource = GetComponent<AudioSource>();
        if (buildLoopSource == null)
            buildLoopSource = gameObject.AddComponent<AudioSource>();
        buildLoopSource.playOnAwake  = false;
        buildLoopSource.loop         = true;
        buildLoopSource.spatialBlend = 1f;       // positional
        buildLoopSource.minDistance  = 12f;
        buildLoopSource.maxDistance  = 120f;
        buildLoopSource.dopplerLevel = 0f;
    }

    private void Update()
    {
        if (buildLoopSource == null) return;

        // Completed sites never loop.
        if (IsComplete)
        {
            if (buildLoopSource.isPlaying) buildLoopSource.Stop();
            return;
        }

        // "Actively building" = a dozer fed progress within the last frames.
        bool building = (Time.time - lastProgressTime) < 0.25f;

        if (building && AudioManager.Instance != null)
        {
            if (!loopClipResolved)
            {
                SoundEvent ev = AudioManager.Instance.GetEvent(GameSound.ConstructionLoop);
                if (ev != null && ev.HasClip)
                {
                    buildLoopSource.clip = ev.PickClip();
                    loopEventVolume      = ev.volume;
                }
                // Apply the RTS distance falloff so a distant build site's loop
                // fades to silence at the construction max distance (instead of
                // the default Logarithmic rolloff that never quite hits zero).
                AudioManager.Instance.ConfigureSpatialLoop(buildLoopSource, GameSound.ConstructionLoop);
                loopClipResolved = true;     // resolve once (clip may legitimately be null)
            }

            if (buildLoopSource.clip != null)
            {
                buildLoopSource.volume = loopEventVolume * AudioManager.Instance.SfxScalar;
                if (!buildLoopSource.isPlaying) buildLoopSource.Play();
            }
        }
        else if (buildLoopSource.isPlaying)
        {
            buildLoopSource.Pause();         // dozer left / cancelled — pause, don't restart
        }
    }

    // ------------------------------------------------------------------ //
    // Setup — called by BuildingPlacementManager right after Instantiate
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Wires the site to its final building. Called by
    /// <see cref="BuildingPlacementManager"/> immediately after Instantiate.
    /// </summary>
    public void Initialise(GameObject finalPrefab, int cost, float buildTime, string label, Quaternion rotation)
    {
        Initialise(finalPrefab, cost, buildTime, label, rotation, Health.Team.Player);
    }

    /// <summary>
    /// Team-aware overload used by enemy build code. The base building's team
    /// is determined by its prefab's Health.team; <paramref name="team"/> only
    /// records which side commissioned the site so UnitSelector can ignore
    /// right-click resume on enemy sites.
    /// </summary>
    public void Initialise(GameObject finalPrefab, int cost, float buildTime,
                           string label, Quaternion rotation, Health.Team team)
    {
        FinalBuildingPrefab = finalPrefab;
        BuildCost           = cost;
        BuildTime           = Mathf.Max(0.05f, buildTime);
        BuildingLabel       = string.IsNullOrEmpty(label) ? "Building" : label;
        finalRotation       = rotation;
        ownerTeam           = team;

        // Reset the progress bar visual to zero.
        RefreshProgressVisual();

        // "Construction started" cue. Runs on every client (the site spawns on
        // each via the Build command), so both players hear it placed.
        AudioManager.SfxAt(GameSound.BuildingPlace, transform.position);
    }

    /// <summary>
    /// Hands the site a network-allocated entity id that the spawned final
    /// building must adopt on completion. Optional — when unset (or empty
    /// string) the final building falls back to a fresh GUID from
    /// <see cref="GameEntity.Awake"/>.
    /// </summary>
    public void SetFinalBuildingEntityId(string entityId)
    {
        networkFinalBuildingEntityId = entityId ?? "";
    }

    /// <summary>
    /// Stamps the canonical owner onto the site. Called once by
    /// <see cref="BuildingPlacementManager.SpawnConstructionSite"/> (and by
    /// <see cref="AI.EnemyBuildAI"/>) immediately after spawn. The value is
    /// read in <see cref="Complete"/> when the final building inherits
    /// ownership — so even if no GameEntity exists on this site, the final
    /// building still lands with the right owner.
    /// </summary>
    public void SetOwner(int ownerPlayerId)
    {
        OwnerPlayerId = ownerPlayerId;
    }

    // ------------------------------------------------------------------ //
    // Dozer ↔ Site
    // ------------------------------------------------------------------ //

    /// <summary>Records the dozer as the active builder. Idempotent.</summary>
    public void AssignDozer(DozerBuilder dozer)
    {
        AssignedDozer = dozer;
    }

    /// <summary>Clears the assigned dozer (if it matches). Does NOT destroy the site.</summary>
    public void ReleaseDozer(DozerBuilder dozer)
    {
        if (AssignedDozer == dozer)
            AssignedDozer = null;
    }

    /// <summary>
    /// Adds <paramref name="deltaSeconds"/> of dozer contact to this site. When
    /// <see cref="CurrentProgress"/> hits 1, <see cref="Complete"/> fires automatically.
    /// Called by <see cref="DozerBuilder"/> each frame while in range.
    /// </summary>
    public void AddBuildProgress(float deltaSeconds, DozerBuilder dozer)
    {
        if (IsComplete) return;
        if (deltaSeconds <= 0f) return;

        // Mark "actively building" so the construction loop (in Update) plays.
        lastProgressTime = Time.time;

        // The first dozer to reach the site is registered as the active builder
        // even if BPM didn't pre-assign one (e.g. resume-by-right-click).
        if (AssignedDozer == null) AssignedDozer = dozer;

        float deltaProgress = deltaSeconds / BuildTime;
        CurrentProgress = Mathf.Clamp01(CurrentProgress + deltaProgress);

        RefreshProgressVisual();

        if (CurrentProgress >= 1f)
            Complete();
    }

    // ------------------------------------------------------------------ //
    // Completion — swap the placeholder for the final building
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Network-driven completion. Called by <see cref="NetworkMatchEvents"/>
    /// when a ConstructionComplete event arrives. Runs the normal Complete
    /// pipeline; the IsComplete guard makes a re-entry from the broadcast
    /// loop a no-op. Idempotent.
    /// </summary>
    public void ForceCompleteFromNetwork()
    {
        Complete();
    }

    private void Complete()
    {
        if (IsComplete) return;
        IsComplete = true;

        // Stop the build loop the instant we finish (Update also guards this,
        // but the GameObject is destroyed below so do it now).
        if (buildLoopSource != null && buildLoopSource.isPlaying)
            buildLoopSource.Stop();

        if (FinalBuildingPrefab == null)
        {
            Debug.LogError($"[Construction] {BuildingLabel} site at {transform.position:F1} has no final prefab " +
                           "— cannot complete. Site destroyed without spawning.");
            Destroy(gameObject);
            return;
        }

        Vector3 pos = transform.position;

        // "Construction complete" cue at the site — runs on every client.
        AudioManager.SfxAt(GameSound.ConstructionComplete, pos);

        // Phase 2: push the network-allocated final id BEFORE Instantiate so
        // GameEntity.Awake on the spawned building adopts it. Empty preset →
        // GameEntity falls back to a fresh GUID (single-player default).
        GameEntity.SetNextSpawnId(networkFinalBuildingEntityId);
        GameObject placed;
        try
        {
            placed = Instantiate(FinalBuildingPrefab, pos, finalRotation);
        }
        finally
        {
            GameEntity.SetNextSpawnId(null);
        }
        placed.name = BuildingLabel;

        if (!string.IsNullOrEmpty(networkFinalBuildingEntityId))
            Debug.Log($"[NetworkSpawn] Final building '{BuildingLabel}' adopted " +
                      $"entityId={networkFinalBuildingEntityId}.");

        // Inherit ownership from this site so the final building ends up on
        // the same team / owner on every client.
        //
        // Owner resolution priority (Phase 10.1 fix):
        //   1. OwnerPlayerId — stamped at spawn time by BPM /
        //      EnemyBuildAI. This is the canonical source and avoids
        //      depending on a sibling GameEntity (the construction site
        //      prefab historically had none, which silently dropped
        //      ownership on every Dozer-built building).
        //   2. Sibling GameEntity.ownerPlayerId — legacy fallback for any
        //      caller that stamped only the GameEntity without calling
        //      SetOwner.
        //   3. Skip (warn) — would leave the final building with owner 0
        //      from the prefab default, which is the bug we're fixing.
        // ApplyOwnership force-repaints the final building's TeamColorMarker
        // so the colour matches the owner immediately.
        GameEntity selfEntity   = GetComponent<GameEntity>();
        GameEntity placedEntity = placed.GetComponent<GameEntity>();

        int ownerForFinal = OwnerPlayerId;
        if (ownerForFinal == GameEntity.NeutralOwnerId && selfEntity != null)
            ownerForFinal = selfEntity.ownerPlayerId;

        if (placedEntity != null && ownerForFinal != GameEntity.NeutralOwnerId)
        {
            placedEntity.ApplyOwnership(ownerForFinal);
            Debug.Log($"[Construction] Final {BuildingLabel} owner={ownerForFinal} " +
                      $"(site OwnerPlayerId={OwnerPlayerId}, " +
                      $"selfEntity.owner={(selfEntity != null ? selfEntity.ownerPlayerId.ToString() : "none")}).");
        }
        else if (placedEntity != null)
        {
            Debug.LogWarning($"[Construction] {BuildingLabel} completed with NO " +
                             "canonical owner — final building keeps prefab default " +
                             "ownership. This indicates the spawn path failed to call " +
                             "ConstructionSite.SetOwner. Check BPM / EnemyBuildAI.");
        }

        // Ensure placed building is on the Building layer (mirrors BPM behaviour).
        int buildingLayer = LayerMask.NameToLayer("Building");
        if (buildingLayer >= 0)
        {
            placed.layer = buildingLayer;
            foreach (Transform child in placed.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = buildingLayer;
        }

        Debug.Log($"[Construction] {BuildingLabel} complete.");

        // Release the dozer reference so it's free to take a new build order.
        if (AssignedDozer != null)
        {
            AssignedDozer.ReleaseBuildAssignment();
            AssignedDozer = null;
        }

        // Notify subscribers (e.g. EnemyBuildAI) before we destroy ourselves —
        // they may want to parent / decorate the placed building.
        OnComplete?.Invoke(placed);

        // Phase 10.6 — mirror completion to the other clients. The broadcast
        // gate inside NetworkMatchEvents skips when we're already applying a
        // received event, so the receiver's call back into Complete via
        // ForceCompleteFromNetwork doesn't echo. Any-client gate so whichever
        // side finishes first wins; the receive side's "site already gone /
        // final building exists" check makes a stale broadcast harmless.
        string finalIdForBroadcast = placedEntity != null ? placedEntity.EntityId : networkFinalBuildingEntityId;
        string siteIdForBroadcast  = selfEntity   != null ? selfEntity.EntityId   : string.Empty;
        if (!string.IsNullOrEmpty(siteIdForBroadcast))
            NetworkMatchEvents.BroadcastConstructionComplete(
                siteIdForBroadcast, ownerForFinal, finalIdForBroadcast, BuildingLabel);

        Destroy(gameObject);
    }

    // ------------------------------------------------------------------ //
    // Visuals
    // ------------------------------------------------------------------ //

    private void RefreshProgressVisual()
    {
        if (progressBarFill == null) return;

        Vector3 s = progressBarFill.localScale;
        s.x = progressBarMaxScaleX * CurrentProgress;
        progressBarFill.localScale = s;

        // Left-anchor trick — keep the left edge fixed by shifting position
        // by (progress - 1) * width * 0.5.
        Vector3 p = progressBarFill.localPosition;
        // The prefab sets the bar's "neutral" position at x = -progressBarMaxScaleX * 0.5
        // so it grows from the LEFT. Re-apply each refresh.
        p.x = -progressBarMaxScaleX * 0.5f + (s.x * 0.5f);
        progressBarFill.localPosition = p;
    }
}
