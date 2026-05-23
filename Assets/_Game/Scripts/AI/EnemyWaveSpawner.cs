// =============================================================================
// PAUSED — Enemy wave spawning is disabled for the current development phase.
// The game currently focuses on the player-side army and base building only.
// Re-enable this system in a later phase when enemy faction gameplay is added.
// DO NOT add this component to any scene object until then.
// =============================================================================

using System.Collections;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Periodically spawns waves of enemy soldiers at a designated spawn point.
///
/// Setup:
///   1. Create an empty GameObject at the edge of the map, name it "EnemyWaveSpawner".
///   2. Attach this script.
///   3. Assign enemySoldierPrefab (the EnemySoldier prefab).
///   4. Assign spawnPoint (an empty child Transform placed on the NavMesh).
///      If left empty, uses this GameObject's own position.
///   5. Tune waveInterval, unitsPerWave in the Inspector.
/// </summary>
public class EnemyWaveSpawner : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Enemy Prefab")]
    [Tooltip("The EnemySoldier prefab to spawn each wave")]
    public GameObject enemySoldierPrefab;

    [Header("Wave Settings")]
    [Tooltip("Seconds between waves")]
    public float waveInterval = 20f;

    [Tooltip("How many enemies spawn per wave")]
    public int unitsPerWave = 3;

    [Tooltip("Seconds before the very first wave. 0 = spawn immediately.")]
    public float firstWaveDelay = 15f;

    [Header("Spawn Point")]
    [Tooltip("Where enemies appear. Leave empty to use this object's position.")]
    public Transform spawnPoint;

    [Tooltip("Lateral spread between units in the same wave (world units)")]
    public float spawnSpread = 1.8f;

    [Tooltip("Radius used to snap spawn positions onto the NavMesh")]
    public float navMeshSnapRadius = 5f;

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    private int currentWave;

    // ------------------------------------------------------------------ //

    private void Start()
    {
        if (enemySoldierPrefab == null)
        {
            Debug.LogError("[WaveSpawner] enemySoldierPrefab is not assigned. Waves will not spawn.");
            return;
        }

        StartCoroutine(WaveLoop());
    }

    // ------------------------------------------------------------------ //
    // Wave loop
    // ------------------------------------------------------------------ //

    private IEnumerator WaveLoop()
    {
        // Wait before first wave
        if (firstWaveDelay > 0f)
            yield return new WaitForSeconds(firstWaveDelay);

        while (true)
        {
            SpawnWave();
            yield return new WaitForSeconds(waveInterval);
        }
    }

    private void SpawnWave()
    {
        currentWave++;
        int toSpawn = unitsPerWave + (currentWave - 1); // optional: ramp up per wave
        // Remove the ramp-up line above and just use unitsPerWave if you want flat waves:
        toSpawn = unitsPerWave;

        Debug.Log($"[WaveSpawner] Wave {currentWave} — spawning {toSpawn} enemies.");

        Vector3 origin = spawnPoint != null ? spawnPoint.position : transform.position;

        for (int i = 0; i < toSpawn; i++)
        {
            // Spread units horizontally so they don't stack on the same point
            float offset = (i - (toSpawn - 1) * 0.5f) * spawnSpread;
            Vector3 desired = origin + transform.right * offset;

            // Snap to nearest NavMesh surface
            if (NavMesh.SamplePosition(desired, out NavMeshHit hit, navMeshSnapRadius, NavMesh.AllAreas))
                desired = hit.position;

            GameObject enemy = Instantiate(enemySoldierPrefab, desired, Quaternion.identity);
            enemy.name = $"EnemySoldier_W{currentWave}_{i + 1}";
        }
    }

    // ------------------------------------------------------------------ //
    // Editor gizmo — shows the spawn zone in Scene view
    // ------------------------------------------------------------------ //

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Vector3 origin = spawnPoint != null ? spawnPoint.position : transform.position;
        Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.6f);
        Gizmos.DrawWireSphere(origin, 1f);

        // Draw the spread line for the default unit count
        if (unitsPerWave > 1)
        {
            float half = (unitsPerWave - 1) * 0.5f * spawnSpread;
            Gizmos.DrawLine(origin - transform.right * half, origin + transform.right * half);
        }
    }
#endif
}
