using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Dev/test skirmish loop — spawns escalating enemy waves that march down the
/// road lanes, through the central crossroads, and into the player base.
/// Each spawned unit gets an <see cref="EnemyAssaultBrain"/> (attack-move +
/// role-based targeting + cover-seek), so the battlefield systems (cover,
/// hazards, props) become part of real fights.
///
/// MULTIPLAYER SAFETY: hard-gated. In a Photon room the director disables
/// itself unless <see cref="allowInMultiplayer"/> is explicitly ticked —
/// skirmish waves are a single-player/dev feature; nothing here broadcasts.
///
/// Setup (done by BridgeOps.AddSkirmishDirector for DevSandboxScene):
///   1. One GameObject "SkirmishDirector" with this component.
///   2. Assign enemy prefab(s), spawn points, waypoint(s), objective.
///   3. autoStart on → waves begin after startDelay in Play Mode.
///
/// Waves escalate by index: count = baseWaveSize + waveNumber, capped by
/// maxActiveEnemies (spawning pauses while at cap). Two spawn lanes
/// alternate so attacks come from rotating directions.
/// </summary>
public class SkirmishDirector : MonoBehaviour
{
    [Header("Enemy prefabs")]
    [Tooltip("Primary assault unit (EnemyRPGSoldierPrefab). More prefab " +
             "variety can be added when more enemy combat prefabs exist.")]
    public GameObject enemyRpgPrefab;

    [Header("Flow")]
    [Tooltip("Begin waves automatically in Play Mode. OFF by default — the game " +
             "direction is online PvP; waves are a manual dev-test tool only " +
             "(Tools → RTS → Test → Spawn Enemy Wave Now).")]
    public bool autoStart = false;

    [Tooltip("Explicit opt-in for multiplayer rooms. Leave OFF.")]
    public bool allowInMultiplayer = false;

    [Tooltip("Seconds before the first wave.")]
    public float startDelay = 12f;

    [Tooltip("Seconds between waves.")]
    public float waveInterval = 35f;

    [Tooltip("Wave size = baseWaveSize + waveNumber (wave 1 = base+1, ...).")]
    public int baseWaveSize = 1;

    [Tooltip("Hard cap on simultaneously alive skirmish enemies.")]
    public int maxActiveEnemies = 10;

    [Header("Route")]
    [Tooltip("Spawn lanes — waves alternate between these points.")]
    public Vector3[] spawnPoints = { new Vector3(45f, 0f, 2f), new Vector3(4f, 0f, 45f) };

    [Tooltip("Mid waypoint — the contested crossroads.")]
    public Vector3 centralWaypoint = new Vector3(10f, 0f, 2f);

    [Tooltip("Final objective — the player base/staging area.")]
    public Vector3 objective = new Vector3(0f, 0f, 0f);

    [Header("Debug")]
    public bool debugLogs = true;

    private readonly List<GameObject> alive = new List<GameObject>();
    private float nextWaveAt;
    private int   waveNumber;
    private bool  running;

    public int WaveNumber  => waveNumber;
    public int AliveCount  { get { alive.RemoveAll(a => a == null); return alive.Count; } }

    private void Start()
    {
        if (NetworkManagerRTS.IsMultiplayerEnabled && !allowInMultiplayer)
        {
            if (debugLogs) Debug.Log("[Skirmish] Multiplayer room detected — director disabled (by design).");
            enabled = false;
            return;
        }
        if (autoStart) StartSkirmish();
    }

    public void StartSkirmish()
    {
        running = true;
        nextWaveAt = Time.time + startDelay;
        if (debugLogs) Debug.Log($"[Skirmish] Started — first wave in {startDelay:F0}s.");
    }

    public void StopSkirmish()
    {
        running = false;
        if (debugLogs) Debug.Log("[Skirmish] Stopped (existing enemies remain).");
    }

    public void ClearEnemies()
    {
        alive.RemoveAll(a => a == null);
        foreach (var go in alive) if (go != null) Destroy(go);
        alive.Clear();
        if (debugLogs) Debug.Log("[Skirmish] Cleared all skirmish enemies.");
    }

    private void Update()
    {
        if (!running || Time.time < nextWaveAt) return;
        if (AliveCount >= maxActiveEnemies)
        {
            nextWaveAt = Time.time + 5f;   // re-check soon; don't skip the wave
            return;
        }
        SpawnWave();
        nextWaveAt = Time.time + waveInterval;
    }

    public void SpawnWave()
    {
        if (enemyRpgPrefab == null)
        {
            Debug.LogError("[Skirmish] enemyRpgPrefab not assigned.");
            return;
        }

        waveNumber++;
        int count = Mathf.Min(baseWaveSize + waveNumber, maxActiveEnemies - AliveCount);
        Vector3 lane = spawnPoints.Length > 0
            ? spawnPoints[(waveNumber - 1) % spawnPoints.Length]
            : transform.position;

        for (int i = 0; i < count; i++)
        {
            Vector3 jitter = new Vector3(Random.Range(-2f, 2f), 0f, Random.Range(-2f, 2f));
            GameObject u = Instantiate(enemyRpgPrefab, lane + jitter, Quaternion.identity);
            u.name = $"Skirmish_W{waveNumber}_{i}";
            var brain = u.AddComponent<EnemyAssaultBrain>();
            brain.SetPath(centralWaypoint, objective);
            alive.Add(u);
        }
        Debug.Log($"[Skirmish] ★ WAVE {waveNumber} INCOMING — {count} unit(s) from lane " +
                  $"{((waveNumber - 1) % Mathf.Max(1, spawnPoints.Length)) + 1}. Active: {AliveCount}.");
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        foreach (var p in spawnPoints) Gizmos.DrawWireSphere(p, 1.5f);
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(centralWaypoint, 1.2f);
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(objective, 1.2f);
    }
#endif
}
