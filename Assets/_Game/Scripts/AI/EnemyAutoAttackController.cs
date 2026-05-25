using UnityEngine;

/// <summary>
/// Lightweight local guard AI for an enemy combat unit. Periodically scans
/// for Player-team <see cref="Health"/> targets in a radius, scores them by
/// category priority (Aircraft > Vehicle > Infantry > Building) and distance,
/// and hands the best one to a sibling <see cref="RocketCombat"/> (or any
/// future combat component exposing SetTarget / ClearTarget).
///
/// This is NOT a full enemy AI — there's no pathfinding strategy, no base
/// attack, no group behaviour. It's a single-unit guard that engages the
/// nearest valuable Player target on its own. Designed so an EnemyRPGSoldier
/// can be dropped into a scene for testing without re-enabling
/// <see cref="EnemyWaveSpawner"/> or <see cref="EnemyAIController"/>.
///
/// Behaviour:
///   • Idle until a Player target enters scan radius.
///   • Picks one target via priority score (highest wins; ties broken by
///     proximity). Re-evaluates each scan tick.
///   • Calls <see cref="RocketCombat.SetTarget"/> when a candidate is inside
///     the combat component's attack range.
///   • If the chosen target moves outside attack range, the guard either
///     chases (<see cref="canChase"/> = true) or releases the target and
///     returns to scanning (<see cref="canChase"/> = false, default).
///   • If the chosen target dies, the next scan picks a new one.
///
/// Setup:
///   1. Attach to an ENEMY unit alongside Health (team = Enemy),
///      UnitCategory, RocketCombat (or another combat component).
///   2. Tune scan radius / interval in the Inspector. Defaults are
///      tuned for the EnemyRPGSoldier prefab.
///   3. Auto-added by Tools → RTS → Units → Create Enemy RPG Soldier Prefab.
///
/// What it does NOT do:
///   • Move the unit. RocketCombat handles chase when canChase = true; this
///     class only decides "who is the current target".
///   • Touch player selection. Enemy units have no SelectableUnit and the
///     player can't right-click them as commands.
///   • Filter out targets behind walls / line-of-sight. v1 is XZ-distance only.
/// </summary>
[RequireComponent(typeof(Health))]
public class EnemyAutoAttackController : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Scan")]
    [Tooltip("XZ radius (world units) the unit checks each scan tick for Player targets.")]
    public float scanRadius = 18f;

    [Tooltip("Seconds between target re-scans. Cheap — uses OverlapSphere + " +
             "a Health component filter, not FindObjectsByType.")]
    public float scanInterval = 0.4f;

    [Tooltip("Layers to scan for targets. Default leaves this empty so the " +
             "controller auto-resolves a sensible mask (Unit + Building) on " +
             "Awake. Set manually if your scene uses non-standard layer names.")]
    public LayerMask scanLayers;

    [Header("Behaviour")]
    [Tooltip("If true, the unit calls SetTarget on out-of-range targets too — " +
             "RocketCombat then chases via NavMesh. If false (default), the unit " +
             "is a stationary guard: it only engages targets already within attack " +
             "range and releases targets that walk out.")]
    public bool canChase = false;

    [Header("Priority Scores")]
    [Tooltip("Score bonus added to Aircraft targets — highest by default so " +
             "RPG fire prioritises air threats when one is present.")]
    public float aircraftPriority = 100f;

    [Tooltip("Score bonus added to Vehicle targets (Humvee, Artillery Tank).")]
    public float vehiclePriority  = 80f;

    [Tooltip("Score bonus added to Infantry targets (Soldier, RPG Soldier, Worker).")]
    public float infantryPriority = 50f;

    [Tooltip("Score bonus added to Building targets. Low so a guard prefers to " +
             "engage moving units before pecking at structures.")]
    public float buildingPriority = 30f;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private Health        ownHealth;
    private RocketCombat  combat;
    private Health        currentTarget;
    private float         scanTimer;

    // Buffer reused per scan to avoid GC pressure. Sized for typical scenes;
    // overflow is fine — OverlapSphereNonAlloc just truncates.
    private readonly Collider[] scanBuffer = new Collider[32];

    // ------------------------------------------------------------------ //
    // Lifecycle
    // ------------------------------------------------------------------ //

    private void Awake()
    {
        ownHealth = GetComponent<Health>();
        combat    = GetComponent<RocketCombat>();

        if (ownHealth == null)
            Debug.LogError($"[EnemyAutoAttack] '{name}': no Health — disabling.");
        if (combat == null)
            Debug.LogWarning($"[EnemyAutoAttack] '{name}': no RocketCombat sibling — " +
                             "the unit will scan but never fire. Add RocketCombat to enable attacks.");

        // Auto-resolve scan mask when the Inspector value is empty (0). Unit
        // + Building layers cover every standard target category.
        if (scanLayers.value == 0)
        {
            int unitLayer     = LayerMask.NameToLayer("Unit");
            int buildingLayer = LayerMask.NameToLayer("Building");
            int mask = 0;
            if (unitLayer     >= 0) mask |= 1 << unitLayer;
            if (buildingLayer >= 0) mask |= 1 << buildingLayer;
            // Fall back to "everything" if neither layer exists in this project.
            scanLayers = mask != 0 ? mask : ~0;
        }

        // Stagger the first scan so multiple enemies don't all OverlapSphere
        // on the same frame.
        scanTimer = Random.Range(0f, scanInterval);
    }

    private void Update()
    {
        if (ownHealth == null || combat == null) return;

        scanTimer -= Time.deltaTime;
        if (scanTimer <= 0f)
        {
            scanTimer = scanInterval;
            ReevaluateTarget();
        }

        // For stationary guards: each frame, drop a target that has walked
        // out of attack range so RocketCombat doesn't auto-chase via MoveTo.
        // Cheap distance check; no allocation.
        if (!canChase && currentTarget != null)
        {
            float dist = Vector3.Distance(transform.position, currentTarget.transform.position);
            float range = ResolveAttackRangeFor(currentTarget);
            if (dist > range)
            {
                Debug.Log($"[EnemyRPG:{name}] Lost target — '{currentTarget.name}' walked out of attack range.");
                combat.ClearTarget();
                currentTarget = null;
            }
        }

        // Target was killed by someone else (or self-damage). Unity's null
        // operator on Object handles destroyed components transparently.
        if (currentTarget == null && combat.IsIdle == false)
        {
            // RocketCombat went idle on its own (target died). Surface a log.
            combat.ClearTarget();
        }
    }

    // ------------------------------------------------------------------ //
    // Scan + scoring
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Picks the best Player target inside the scan radius and points the
    /// combat component at it. Stationary guards only engage targets already
    /// inside attack range; chasers will engage out-of-range targets too.
    /// </summary>
    private void ReevaluateTarget()
    {
        Health best       = null;
        float  bestScore  = float.NegativeInfinity;

        int count = Physics.OverlapSphereNonAlloc(
            transform.position, scanRadius, scanBuffer, scanLayers.value);

        for (int i = 0; i < count; i++)
        {
            Collider col = scanBuffer[i];
            if (col == null) continue;

            // Resolve a Health up the parent chain — child colliders on
            // visual sub-objects shouldn't break selection.
            Health h = col.GetComponent<Health>() ?? col.GetComponentInParent<Health>();
            if (h == null || h == ownHealth) continue;
            if (h.team != Health.Team.Player)  continue;   // only Player targets are valid

            float dist = Vector3.Distance(transform.position, h.transform.position);
            if (dist > scanRadius) continue;               // capsule colliders can leak slightly outside

            // Stationary guards reject out-of-range targets entirely; chasers
            // accept them and let RocketCombat close the distance.
            float range = ResolveAttackRangeFor(h);
            if (!canChase && dist > range) continue;

            float score = ScoreFor(h, dist);
            if (score > bestScore)
            {
                bestScore = score;
                best      = h;
            }
        }

        if (best == null)
        {
            if (currentTarget != null)
            {
                Debug.Log($"[EnemyRPG:{name}] Lost target.");
                combat.ClearTarget();
                currentTarget = null;
            }
            return;
        }

        if (best != currentTarget)
        {
            Debug.Log($"[EnemyRPG:{name}] Acquired target: {best.name}.");
            currentTarget = best;
            combat.SetTarget(best);
            Debug.Log($"[EnemyRPG:{name}] Firing rocket at {best.name}.");   // first-shot announcement; RocketCombat handles cooldown
        }
    }

    /// <summary>
    /// Higher score = more attractive target. Category bonus dominates;
    /// distance subtly breaks ties so the closer of two equal-priority
    /// targets wins.
    /// </summary>
    private float ScoreFor(Health h, float distance)
    {
        UnitCategory.Category cat = DamageRules.Resolve(h.gameObject);
        float categoryBonus = cat switch
        {
            UnitCategory.Category.Aircraft => aircraftPriority,
            UnitCategory.Category.Vehicle  => vehiclePriority,
            UnitCategory.Category.Infantry => infantryPriority,
            UnitCategory.Category.Building => buildingPriority,
            _                              => 0f,
        };

        return categoryBonus - distance;
    }

    /// <summary>
    /// Reads the correct attack range from the sibling RocketCombat — anti-air
    /// targets get the longer antiAirRange, everything else the standard range.
    /// </summary>
    private float ResolveAttackRangeFor(Health h)
    {
        if (combat == null) return 0f;

        UnitCategory.Category cat = DamageRules.Resolve(h.gameObject);
        return cat == UnitCategory.Category.Aircraft ? combat.antiAirRange : combat.attackRange;
    }

    // ------------------------------------------------------------------ //
    // Gizmos
    // ------------------------------------------------------------------ //

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        UnityEditor.Handles.color = new Color(1f, 0.2f, 0.2f, 0.20f);
        UnityEditor.Handles.DrawWireDisc(transform.position, Vector3.up, scanRadius);
    }
#endif
}
