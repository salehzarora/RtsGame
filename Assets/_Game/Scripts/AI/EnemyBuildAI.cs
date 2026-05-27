using UnityEngine;

/// <summary>
/// Enemy bot's logical RTS build order. Drives a single Enemy
/// <see cref="DozerBuilder"/> through:
///
///   ProduceDozer → BuildPowerPlant → BuildBarracks → BuildMGDefense → Complete
///
/// Each non-dozer step uses the same ConstructionSite + Dozer flow the player
/// uses — there is no instant spawn. The bot:
///   • Earns 150 resources, spawns an Enemy Dozer next to the CommandCenter.
///   • Waits until it can afford the next building.
///   • Spawns an enemy-team <see cref="ConstructionSite"/> at a fixed offset
///     from the CommandCenter and assigns the Dozer to it.
///   • Records the placed building so the same step is never started twice.
///
/// Per-step "already built" gate:
///   On Start the bot scans the scene for existing Enemy-team
///   <see cref="Building"/> components and matches them by name. If a previous
///   session (or hand-placed test object) already finished a step, the bot
///   advances without spending or building again.
///
/// Frontline MG Defense:
///   When <see cref="buildFrontUsesPlayerDirection"/> is true and a
///   <see cref="MatchManager"/> is present, the Machine Gun Defense site is
///   placed on the line from this CommandCenter toward the player's start
///   position, at <see cref="mgDefenseFrontDistance"/> world units. Falls
///   back to <see cref="mgDefenseOffset"/> otherwise.
///
/// Setup:
///   • Attached automatically by <c>Tools → RTS → Match → Setup Clean Match Map</c>
///     to the enemy CommandCenter GameObject.
///   • Prefab references (enemy buildings + ConstructionSite + Enemy Dozer)
///     wired by <c>Tools → RTS → Enemy → Repair Enemy Builder AI</c>, also
///     called automatically from Setup Clean Match Map.
///
/// What this script intentionally does NOT do:
///   • Send attack waves. No movement orders to combat units.
///   • Produce new enemy units. Enemy Barracks is set-dressing for this
///     milestone, with a comment slot for the future EnemyBarracksProducer.
///   • Touch the player Dozer, BuildingPlacementManager, RTSHUD, PowerManager
///     (player grid stays clean), or any other player system.
/// </summary>
[DisallowMultipleComponent]
public class EnemyBuildAI : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Public step enum — readable in logs and the Inspector
    // ------------------------------------------------------------------ //

    public enum EnemyBuildStep
    {
        ProduceDozer,
        BuildPowerPlant,
        BuildBarracks,
        BuildMGDefense,
        Complete,
    }

    // ------------------------------------------------------------------ //
    // Inspector — behaviour
    // ------------------------------------------------------------------ //

    [Header("Behaviour")]
    [Tooltip("Master switch — uncheck to fully disable the enemy build bot.")]
    public bool enemyBuildAIEnabled = true;

    [Tooltip("Per-step kill switch (spec name). Both this AND enemyBuildAIEnabled " +
             "must be true for the bot to tick. Lets the user pause the build order " +
             "without disabling resource accounting / dozer-spawn intent.")]
    public bool buildOrderEnabled = true;

    [Tooltip("Seconds between affordability / state checks. Slow on purpose so the " +
             "player can watch the enemy economy ramp up.")]
    public float checkInterval = 2f;

    [Tooltip("Seconds the bot waits before retrying a step after a transient failure " +
             "(e.g. construction site spawn failed, last attempt was destroyed before " +
             "completion). Prevents tight retry loops.")]
    public float retryDelay = 4f;

    // ------------------------------------------------------------------ //
    // Inspector — positions
    // ------------------------------------------------------------------ //

    [Header("Build Offsets (local to EnemyCommandCenter)")]
    [Tooltip("PowerPlant — behind/side of base. Default (8, 0, 6) keeps it out of " +
             "the front line.")]
    public Vector3 powerPlantOffset = new Vector3( 8f, 0f,  6f);

    [Tooltip("Barracks — side/front of base. Default (-8, 0, 4).")]
    public Vector3 barracksOffset   = new Vector3(-8f, 0f,  4f);

    [Tooltip("Machine Gun Defense fallback offset, used when " +
             "buildFrontUsesPlayerDirection is false or MatchManager is absent.")]
    public Vector3 mgDefenseOffset  = new Vector3( 0f, 0f, -12f);

    [Tooltip("If true, the MG Defense is placed on the line from this CommandCenter " +
             "toward MatchManager.playerStartPosition at mgDefenseFrontDistance — so " +
             "the turret always faces the actual player base. Falls back to " +
             "mgDefenseOffset when disabled or MatchManager is missing.")]
    public bool buildFrontUsesPlayerDirection = true;

    [Tooltip("Distance from CommandCenter to MG Defense site when " +
             "buildFrontUsesPlayerDirection is true.")]
    public float mgDefenseFrontDistance = 12f;

    // ------------------------------------------------------------------ //
    // Inspector — costs
    // ------------------------------------------------------------------ //

    [Header("Costs (mirror player costs)")]
    public int powerPlantCost = 150;
    public int barracksCost   = 100;
    public int mgDefenseCost  = 250;

    // ------------------------------------------------------------------ //
    // Inspector — construction tuning
    // ------------------------------------------------------------------ //

    [Header("Construction")]
    [Tooltip("Seconds the Enemy Dozer takes to complete a site at speed 1×. " +
             "Mirrors BuildingPlacementManager.dozerBuildTime so the matchup feels " +
             "symmetric.")]
    public float buildTime = 1f;

    // ------------------------------------------------------------------ //
    // Inspector — Enemy Dozer production
    // ------------------------------------------------------------------ //

    [Header("Enemy Dozer Production")]
    [Tooltip("Resource cost to produce one Enemy Dozer. Matches the player Dozer.")]
    public int dozerCost = 150;

    [Tooltip("Local offset from this CommandCenter where a new Enemy Dozer spawns.")]
    public Vector3 dozerSpawnOffset = new Vector3(4f, 0f, 0f);

    [Tooltip("Hard cap on active Enemy Dozers. 1 keeps the bot simple for this " +
             "milestone — replacement logic still works because a destroyed dozer " +
             "leaves count = 0.")]
    public int maxEnemyDozers = 1;

    // ------------------------------------------------------------------ //
    // Inspector — references
    // ------------------------------------------------------------------ //

    [Header("References (wired by Setup Clean Match Map / Repair Enemy Builder AI)")]
    [Tooltip("Prefab the bot instantiates to produce Enemy Dozers.")]
    public GameObject enemyDozerPrefab;

    [Tooltip("Live Enemy Dozer the AI is currently driving. Null at start; " +
             "assigned automatically after the first production.")]
    public DozerBuilder enemyDozer;

    [Tooltip("ConstructionSite prefab — shared with the player. Same script + same " +
             "visual placeholder; team is set via ownerTeam at Initialise time.")]
    public GameObject constructionSitePrefab;

    [Tooltip("Final building spawned at step 1 after construction completes.")]
    public GameObject enemyPowerPlantPrefab;

    [Tooltip("Final building spawned at step 2 after construction completes. " +
             "Slot reserved for a future EnemyBarracksProducer — production is not " +
             "implemented in this milestone.")]
    public GameObject enemyBarracksPrefab;

    [Tooltip("Final building spawned at step 3 after construction completes.")]
    public GameObject enemyMGDefensePrefab;

    // ------------------------------------------------------------------ //
    // Inspector — debug / read-only
    // ------------------------------------------------------------------ //

    [Header("Debug (read-only)")]
    [Tooltip("Current step the build order is on. Updated automatically at runtime. " +
             "Visible in the Inspector for diagnostics.")]
    [SerializeField] private EnemyBuildStep currentStep = EnemyBuildStep.ProduceDozer;

    /// <summary>External read-only accessor for the current build step.</summary>
    public EnemyBuildStep CurrentStep => currentStep;

    // ------------------------------------------------------------------ //
    // Runtime state
    // ------------------------------------------------------------------ //

    // Live ConstructionSite the bot is waiting on, if any. Single in-flight site
    // at a time — never queues a second.
    private ConstructionSite currentSite;

    // Recorded final buildings per step. Set in HandleSiteComplete and also by
    // DetectAlreadyBuiltStructures on Start so a re-Awoken bot doesn't double-spend.
    private GameObject placedPowerPlant;
    private GameObject placedBarracks;
    private GameObject placedMGDefense;

    // EnemyStart root (if present) used to keep spawned structures tidy.
    private Transform parentRoot;

    // CommandCenter pivot, captured once.
    private Vector3   basePosition;

    // Retry cooldown — applied after a recoverable failure (e.g. site destroyed).
    private float     resumeAt;

    // ------------------------------------------------------------------ //
    // Runtime — log gates so the console doesn't spam every tick
    // ------------------------------------------------------------------ //

    private bool waitedForDozer, waitedForPower, waitedForBarracks, waitedForMG;
    private bool warnedNoBank, warnedNoDozerPrefab, warnedNoSitePrefab, warnedNoBuildingPrefab;
    private bool announcedDozerLost;
    private bool isProducingDozer;

    // Logged "Current step: X" once per step entry; reset on transition.
    private EnemyBuildStep lastLoggedStep = (EnemyBuildStep)(-1);

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Start()
    {
        // Phase 3: in multiplayer, the enemy seat is a human player — the AI
        // bot must not run. Single-player keeps the existing behaviour.
        if (NetworkManagerRTS.Instance != null && NetworkManagerRTS.Instance.multiplayerMode)
        {
            Debug.Log("[EnemyBuildAI] Disabled — multiplayer mode is on.");
            enabled = false;
            return;
        }

        basePosition = transform.position;

        GameObject enemyStart = GameObject.Find("EnemyStart");
        parentRoot = enemyStart != null ? enemyStart.transform : transform.parent;

        // Inspector-assigned dozer takes precedence (hand-crafted test scenes);
        // otherwise resolve any Enemy-team DozerBuilder already in the scene.
        if (enemyDozer == null)
            enemyDozer = FindEnemyDozer();

        // Detect any enemy buildings that already exist (e.g. from a partial
        // earlier session) so we don't redo finished steps.
        DetectAlreadyBuiltStructures();
        AdvancePastAlreadyBuiltSteps();

        Debug.Log("[EnemyBuildAI] Build bot online.");
        InvokeRepeating(nameof(Tick), 1f, checkInterval);
    }

    private void OnDisable()
    {
        CancelInvoke(nameof(Tick));
        if (currentSite != null) currentSite.OnComplete -= HandleSiteComplete;
    }

    // ------------------------------------------------------------------ //
    // Tick — one state-machine evaluation per scheduled call
    // ------------------------------------------------------------------ //

    private void Tick()
    {
        if (!enemyBuildAIEnabled || !buildOrderEnabled) return;
        if (Time.time < resumeAt) return;

        EnemyResourceManager bank = EnemyResourceManager.Instance;
        if (bank == null)
        {
            WarnOnce(ref warnedNoBank,
                "[EnemyBuildAI] No EnemyResourceManager in scene — idle.");
            return;
        }

        // Log step transitions exactly once.
        if (currentStep != lastLoggedStep)
        {
            Debug.Log($"[EnemyBuildAI] Current step: {currentStep}");
            lastLoggedStep = currentStep;
            ResetStepWaitFlags();
        }

        // ── Step 0: ensure an Enemy Dozer exists ─────────────────────── //
        // Match starts WITHOUT one. The same path handles replacement after
        // the dozer dies later in the build order.
        if (enemyDozer == null || !enemyDozer)
        {
            // If we lost the dozer mid-build, log once and rewind the step
            // pointer so we don't skip the building that was in progress.
            if (currentStep != EnemyBuildStep.ProduceDozer)
            {
                if (!announcedDozerLost)
                {
                    Debug.Log("[EnemyBuildAI] Enemy Dozer destroyed — pausing build order.");
                    announcedDozerLost = true;
                }
            }
            TryProduceDozer(bank);
            return;
        }
        if (announcedDozerLost) announcedDozerLost = false;

        // We have a dozer. If the step pointer is still on ProduceDozer (e.g.
        // first run) → advance and log the transition next tick.
        if (currentStep == EnemyBuildStep.ProduceDozer)
        {
            AdvanceStepFrom(EnemyBuildStep.ProduceDozer);
            return;
        }

        if (currentStep == EnemyBuildStep.Complete) return;

        // ── Wait on / re-assign current ConstructionSite ─────────────── //
        if (currentSite != null)
        {
            if (!currentSite) { HandleSiteAbandoned(); return; }
            if (currentSite.IsComplete) return;     // OnComplete will advance

            // If the previous dozer died, the site has a stale AssignedDozer.
            // Re-assign the new dozer so progress resumes from where it stopped.
            if (currentSite.AssignedDozer == null || !currentSite.AssignedDozer)
            {
                enemyDozer.AssignBuildOrder(currentSite);
                Debug.Log("[EnemyBuildAI] New Dozer assigned to existing construction site.");
            }
            return;
        }

        // ── Already-built check ─────────────────────────────────────── //
        if (IsAlreadyBuilt(currentStep))
        {
            Debug.Log($"[EnemyBuildAI] {LabelForStep(currentStep)} already exists — " +
                      "skipping step.");
            AdvanceStepFrom(currentStep);
            return;
        }

        // ── Affordability + prefab validation ───────────────────────── //
        int    cost  = CostForStep(currentStep);
        string label = LabelForStep(currentStep);

        if (!bank.CanAfford(cost))
        {
            LogWaitOnce(currentStep, label, cost);
            return;
        }

        GameObject finalPrefab = FinalPrefabForStep(currentStep);
        if (finalPrefab == null)
        {
            WarnOnce(ref warnedNoBuildingPrefab,
                $"[EnemyBuildAI] No final-building prefab for {label} — skipping. " +
                "Run Tools → RTS → Enemy → Repair Enemy Builder AI.");
            AdvanceStepFrom(currentStep);
            return;
        }

        if (constructionSitePrefab == null)
        {
            WarnOnce(ref warnedNoSitePrefab,
                "[EnemyBuildAI] No ConstructionSite prefab assigned — cannot build. " +
                "Run Tools → RTS → Enemy → Repair Enemy Builder AI.");
            return;
        }

        // ── Pay, spawn site, dispatch dozer ──────────────────────────── //
        if (!bank.SpendResources(cost)) return;

        Vector3 offset = ResolveOffsetForStep(currentStep);
        if (!StartConstruction(finalPrefab, cost, label, offset))
        {
            // Refund and back off so a recurring failure doesn't drain the bank.
            bank.AddResources(cost);
            resumeAt = Time.time + retryDelay;
        }
    }

    // ------------------------------------------------------------------ //
    // Construction site dispatch
    // ------------------------------------------------------------------ //

    private bool StartConstruction(GameObject finalPrefab, int cost, string label, Vector3 offset)
    {
        // CC pivot sits ~1u above ground; sites must spawn AT ground so the
        // dress-building prefabs (pivot-at-ground) and EnemyMachineGunDefensePrefab
        // (also pivot-at-ground) land cleanly.
        Vector3 worldPos = new Vector3(basePosition.x + offset.x, 0f, basePosition.z + offset.z);

        Debug.Log($"[EnemyBuildAI] Ordering Dozer to build {label}");

        GameObject siteGO = Instantiate(constructionSitePrefab, worldPos, Quaternion.identity);
        siteGO.name = $"{label} (Enemy Site)";
        if (parentRoot != null) siteGO.transform.SetParent(parentRoot, worldPositionStays: true);

        int buildingLayer = LayerMask.NameToLayer("Building");
        if (buildingLayer >= 0)
        {
            siteGO.layer = buildingLayer;
            foreach (Transform child in siteGO.GetComponentsInChildren<Transform>(true))
                child.gameObject.layer = buildingLayer;
        }

        ConstructionSite site = siteGO.GetComponent<ConstructionSite>();
        if (site == null)
        {
            Debug.LogError("[EnemyBuildAI] constructionSitePrefab is missing the ConstructionSite " +
                           "component. Run Tools → RTS → Construction → Create Construction Site Prefab.");
            Destroy(siteGO);
            return false;
        }

        // team = Enemy so UnitSelector ignores it on player right-click resume.
        site.Initialise(finalPrefab, cost, buildTime, label, Quaternion.identity, Health.Team.Enemy);
        // Phase 10.1 — stamp the canonical owner so the final building
        // inherits Enemy ownership in ConstructionSite.Complete. Without
        // this the bug-fix would leave the AI's buildings unowned (they
        // worked before only because the post-spawn GameEntity stamping
        // happened to put them in the right team via Health.team).
        site.SetOwner(GameEntity.EnemyOwnerId);
        site.OnComplete += HandleSiteComplete;

        enemyDozer.AssignBuildOrder(site);
        currentSite = site;
        return true;
    }

    /// <summary>
    /// Fired by <see cref="ConstructionSite.OnComplete"/> when the dozer finishes
    /// the site. Records the placed building per-step so the same step is never
    /// rebuilt, then advances the order.
    /// </summary>
    private void HandleSiteComplete(GameObject placed)
    {
        EnemyBuildStep finished = currentStep;
        EnemyBuildStep next     = NextStepAfter(finished);

        Debug.Log($"[EnemyBuildAI] {LabelForStep(finished)} complete. Next step: {next}");

        if (parentRoot != null && placed != null)
            placed.transform.SetParent(parentRoot, worldPositionStays: true);

        // Record the placed building so future ticks skip this step entirely.
        switch (finished)
        {
            case EnemyBuildStep.BuildPowerPlant: placedPowerPlant = placed; break;
            case EnemyBuildStep.BuildBarracks:   placedBarracks   = placed; break;
            case EnemyBuildStep.BuildMGDefense:  placedMGDefense  = placed; break;
        }

        if (currentSite != null) currentSite.OnComplete -= HandleSiteComplete;
        currentSite = null;

        AdvanceStepFrom(finished);
    }

    /// <summary>
    /// Fallback when a site is destroyed before completing (sites have no Health
    /// today, so this is defensive future-proofing). Retries the same step after
    /// <see cref="retryDelay"/> seconds — does NOT advance and does NOT refund
    /// (the spend has already happened).
    /// </summary>
    private void HandleSiteAbandoned()
    {
        Debug.LogWarning($"[EnemyBuildAI] Construction site for {LabelForStep(currentStep)} " +
                         "destroyed before completion — retrying after delay.");
        currentSite = null;
        resumeAt    = Time.time + retryDelay;
        // Don't refund — the resource was spent. Re-spending after a delay is the
        // simplest correct behaviour. To refund on site destruction, ConstructionSite
        // would need a cancellation event we don't have today.
    }

    // ------------------------------------------------------------------ //
    // Dozer production
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Saves up <see cref="dozerCost"/> resources and spawns an Enemy Dozer
    /// next to this CommandCenter. Respects <see cref="maxEnemyDozers"/> so
    /// the bot never queues a second dozer.
    /// </summary>
    private void TryProduceDozer(EnemyResourceManager bank)
    {
        if (isProducingDozer) return;
        if (CountActiveEnemyDozers() >= maxEnemyDozers) return;

        if (enemyDozerPrefab == null)
        {
            WarnOnce(ref warnedNoDozerPrefab,
                "[EnemyBuildAI] enemyDozerPrefab not assigned — cannot produce. " +
                "Run Tools → RTS → Enemy → Repair Enemy Builder AI.");
            return;
        }

        if (!bank.CanAfford(dozerCost))
        {
            if (!waitedForDozer)
            {
                Debug.Log($"[EnemyBuildAI] Waiting for resources to produce Enemy Dozer. " +
                          $"Need {dozerCost}.");
                waitedForDozer = true;
            }
            return;
        }

        if (!bank.SpendResources(dozerCost)) return;

        isProducingDozer = true;
        Vector3 spawnPos = new Vector3(
            transform.position.x + dozerSpawnOffset.x,
            0f,
            transform.position.z + dozerSpawnOffset.z);

        GameObject go = Instantiate(enemyDozerPrefab, spawnPos, Quaternion.identity);
        go.name = "EnemyDozer";
        if (parentRoot != null) go.transform.SetParent(parentRoot, worldPositionStays: true);

        enemyDozer       = go.GetComponent<DozerBuilder>();
        isProducingDozer = false;
        waitedForDozer   = false;     // re-arm for future replacement

        Debug.Log($"[EnemyBuildAI] Enemy Dozer produced for {dozerCost} resources.");
        Debug.Log("[EnemyBuildAI] Enemy Dozer assigned.");
    }

    private int CountActiveEnemyDozers()
    {
        return enemyDozer != null && enemyDozer ? 1 : 0;
    }

    // ------------------------------------------------------------------ //
    // "Already built" detection — runs once at Start and on each completion
    // ------------------------------------------------------------------ //

    private bool IsAlreadyBuilt(EnemyBuildStep step)
    {
        switch (step)
        {
            case EnemyBuildStep.BuildPowerPlant: return placedPowerPlant != null && placedPowerPlant;
            case EnemyBuildStep.BuildBarracks:   return placedBarracks   != null && placedBarracks;
            case EnemyBuildStep.BuildMGDefense:  return placedMGDefense  != null && placedMGDefense;
            default:                              return false;
        }
    }

    /// <summary>
    /// Scene-wide scan at Start: any Enemy-team Building whose name matches one
    /// of our three structures is recorded as "already built". Skips the
    /// CommandCenter itself (which also has Building + Health(Enemy)).
    /// </summary>
    private void DetectAlreadyBuiltStructures()
    {
        Building[] all = Object.FindObjectsByType<Building>(FindObjectsSortMode.None);
        foreach (Building b in all)
        {
            if (b == null) continue;
            Health hp = b.GetComponent<Health>();
            if (hp == null || hp.team != Health.Team.Enemy) continue;
            if (b.GetComponent<EnemyCommandCenter>() != null) continue;

            // ConstructionSite.Complete sets placed.name to the BuildingLabel
            // we passed in (e.g. "PowerPlant", "Barracks", "Machine Gun Defense").
            // For pre-existing test objects the user might also name them with
            // an "Enemy" prefix; the Contains check tolerates both.
            string nm = b.gameObject.name;

            if (placedPowerPlant == null && nm.Contains("PowerPlant"))
            { placedPowerPlant = b.gameObject; continue; }

            if (placedBarracks == null && nm.Contains("Barracks"))
            { placedBarracks = b.gameObject; continue; }

            if (placedMGDefense == null &&
                (nm.Contains("Machine Gun") || nm.Contains("MachineGun") || nm.Contains("MGDefense")))
            { placedMGDefense = b.gameObject; continue; }
        }
    }

    /// <summary>
    /// If the build pointer is on a step whose building already exists, fast-
    /// forward through it (and any subsequent steps that are also pre-built)
    /// before the first tick fires.
    /// </summary>
    private void AdvancePastAlreadyBuiltSteps()
    {
        for (int safety = 0; safety < 10; safety++)
        {
            if (currentStep == EnemyBuildStep.Complete) return;
            if (currentStep == EnemyBuildStep.ProduceDozer) return;     // dozer is per-tick
            if (!IsAlreadyBuilt(currentStep)) return;

            Debug.Log($"[EnemyBuildAI] {LabelForStep(currentStep)} already exists at startup " +
                      "— skipping step.");
            currentStep = NextStepAfter(currentStep);
        }
    }

    // ------------------------------------------------------------------ //
    // Step machinery
    // ------------------------------------------------------------------ //

    private void AdvanceStepFrom(EnemyBuildStep from)
    {
        currentStep = NextStepAfter(from);
        if (currentStep == EnemyBuildStep.Complete)
            Debug.Log("[EnemyBuildAI] Build order complete.");
    }

    private static EnemyBuildStep NextStepAfter(EnemyBuildStep step)
    {
        switch (step)
        {
            case EnemyBuildStep.ProduceDozer:    return EnemyBuildStep.BuildPowerPlant;
            case EnemyBuildStep.BuildPowerPlant: return EnemyBuildStep.BuildBarracks;
            case EnemyBuildStep.BuildBarracks:   return EnemyBuildStep.BuildMGDefense;
            case EnemyBuildStep.BuildMGDefense:  return EnemyBuildStep.Complete;
            default:                              return EnemyBuildStep.Complete;
        }
    }

    private int CostForStep(EnemyBuildStep step)
    {
        switch (step)
        {
            case EnemyBuildStep.BuildPowerPlant: return powerPlantCost;
            case EnemyBuildStep.BuildBarracks:   return barracksCost;
            case EnemyBuildStep.BuildMGDefense:  return mgDefenseCost;
            default:                              return 0;
        }
    }

    private string LabelForStep(EnemyBuildStep step)
    {
        switch (step)
        {
            case EnemyBuildStep.ProduceDozer:    return "Enemy Dozer";
            case EnemyBuildStep.BuildPowerPlant: return "PowerPlant";
            case EnemyBuildStep.BuildBarracks:   return "Barracks";
            case EnemyBuildStep.BuildMGDefense:  return "Machine Gun Defense";
            default:                              return "<done>";
        }
    }

    /// <summary>Offset used by <see cref="StartConstruction"/>. Special-cases MG
    /// Defense if <see cref="buildFrontUsesPlayerDirection"/> is enabled.</summary>
    private Vector3 ResolveOffsetForStep(EnemyBuildStep step)
    {
        if (step != EnemyBuildStep.BuildMGDefense || !buildFrontUsesPlayerDirection)
            return RawOffsetForStep(step);

        MatchManager mm = MatchManager.Instance;
        if (mm == null) return RawOffsetForStep(step);

        Vector3 toPlayer = mm.playerStartPosition - basePosition;
        toPlayer.y = 0f;
        if (toPlayer.sqrMagnitude < 0.0001f) return RawOffsetForStep(step);

        return toPlayer.normalized * mgDefenseFrontDistance;
    }

    private Vector3 RawOffsetForStep(EnemyBuildStep step)
    {
        switch (step)
        {
            case EnemyBuildStep.BuildPowerPlant: return powerPlantOffset;
            case EnemyBuildStep.BuildBarracks:   return barracksOffset;
            case EnemyBuildStep.BuildMGDefense:  return mgDefenseOffset;
            default:                              return Vector3.zero;
        }
    }

    private GameObject FinalPrefabForStep(EnemyBuildStep step)
    {
        switch (step)
        {
            case EnemyBuildStep.BuildPowerPlant: return enemyPowerPlantPrefab;
            case EnemyBuildStep.BuildBarracks:   return enemyBarracksPrefab;
            case EnemyBuildStep.BuildMGDefense:  return enemyMGDefensePrefab;
            default:                              return null;
        }
    }

    private void LogWaitOnce(EnemyBuildStep step, string label, int cost)
    {
        switch (step)
        {
            case EnemyBuildStep.BuildPowerPlant:
                if (!waitedForPower)
                {
                    Debug.Log($"[EnemyBuildAI] Waiting for resources: {label} cost {cost}");
                    waitedForPower = true;
                }
                break;
            case EnemyBuildStep.BuildBarracks:
                if (!waitedForBarracks)
                {
                    Debug.Log($"[EnemyBuildAI] Waiting for resources: {label} cost {cost}");
                    waitedForBarracks = true;
                }
                break;
            case EnemyBuildStep.BuildMGDefense:
                if (!waitedForMG)
                {
                    Debug.Log($"[EnemyBuildAI] Waiting for resources: {label} cost {cost}");
                    waitedForMG = true;
                }
                break;
        }
    }

    private void ResetStepWaitFlags()
    {
        waitedForPower    = false;
        waitedForBarracks = false;
        waitedForMG       = false;
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private static void WarnOnce(ref bool gate, string message)
    {
        if (gate) return;
        gate = true;
        Debug.LogWarning(message);
    }

    private static DozerBuilder FindEnemyDozer()
    {
        DozerBuilder[] all = Object.FindObjectsByType<DozerBuilder>(FindObjectsSortMode.None);
        foreach (DozerBuilder d in all)
        {
            if (d == null) continue;
            Health hp = d.GetComponent<Health>();
            if (hp != null && hp.team == Health.Team.Enemy)
                return d;
        }
        return null;
    }
}
