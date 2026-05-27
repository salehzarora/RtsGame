using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Phase 10 — builds <c>Assets/_Game/Prefabs/CommandCenterPrefab.prefab</c>
/// so the Dozer build menu can spawn a CommandCenter as a buildable
/// structure. Previously CommandCenters existed only as scene objects
/// (created in-place by SetupCleanMatchMap / SetupMultiplayerMatchMap);
/// the new MP starting flow has each player start with just a Dozer and
/// has to build their CC.
///
/// Menu: Tools → RTS → Construction → Create CommandCenter Prefab
///
/// Idempotent — re-running overwrites the existing prefab with a freshly-
/// built one. Pre-wires Worker + Dozer prefabs into the
/// <see cref="CommandCenterProducer"/> so spawned instances are ready to
/// produce both as soon as construction completes.
/// </summary>
public static class CreateCommandCenterPrefab
{
    public const string PrefabPath = "Assets/_Game/Prefabs/CommandCenterPrefab.prefab";

    private const float CC_ScaleX  = 6f;
    private const float CC_ScaleY  = 4f;
    private const float CC_ScaleZ  = 6f;
    private const float CC_MaxHP   = 500f;

    [MenuItem("Tools/RTS/Construction/Create CommandCenter Prefab")]
    public static void Create()
    {
        Debug.Log("[CreateCCPrefab] ── Building CommandCenterPrefab ──");

        // Build the root primitive that will be saved as the prefab.
        GameObject root = GameObject.CreatePrimitive(PrimitiveType.Cube);
        root.name = "CommandCenterPrefab";
        root.transform.localScale = new Vector3(CC_ScaleX, CC_ScaleY, CC_ScaleZ);

        // Building layer (numeric). LayerMask.NameToLayer fails silently with
        // -1 if the user removed the layer; default to 0 in that case so the
        // prefab still saves.
        int buildingLayer = LayerMask.NameToLayer("Building");
        if (buildingLayer < 0) buildingLayer = 0;
        root.layer = buildingLayer;

        // Visual — use a dedicated material instance so the per-owner colour
        // pass via TeamColorMarker / ApplyOwnership has something to repaint
        // without modifying any other prefab's shared material.
        Renderer rend = root.GetComponent<Renderer>();
        if (rend != null)
        {
            Material instance = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            instance.color = new Color(0.30f, 0.50f, 0.70f);    // neutral steel blue
            rend.sharedMaterial = instance;
        }

        // Core gameplay components.
        root.AddComponent<CommandCenter>();
        root.AddComponent<SelectableBuilding>();
        CommandCenterProducer producer = root.AddComponent<CommandCenterProducer>();

        // Wire Worker + Dozer into the producer so the spawned CC has both
        // buttons enabled immediately. AddGameEntityToPrefabs already stamps
        // both worker / dozer prefabs themselves.
        GameObject workerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/_Game/Prefabs/WorkerPrefab.prefab");
        GameObject dozerPrefab  = AssetDatabase.LoadAssetAtPath<GameObject>(
            "Assets/_Game/Prefabs/DozerPrefab.prefab");

        if (workerPrefab == null)
            Debug.LogError("[CreateCCPrefab] ✗ WorkerPrefab.prefab missing — the CC won't have a Worker button.");
        else producer.workerPrefab = workerPrefab;

        if (dozerPrefab == null)
            Debug.LogError("[CreateCCPrefab] ✗ DozerPrefab.prefab missing — the CC won't have a Dozer button.");
        else producer.dozerPrefab = dozerPrefab;

        producer.workerCost = 75;
        producer.dozerCost  = 150;

        // Health — scene-baked team is just an initial value; the per-client
        // perspective remap on MatchStart overwrites it based on local owner.
        Health hp = root.AddComponent<Health>();
        hp.team      = Health.Team.Player;
        hp.maxHealth = CC_MaxHP;

        UnitCategory cat = root.AddComponent<UnitCategory>();
        cat.category = UnitCategory.Category.Building;

        // GameEntity — default owner 0 on the prefab; the dispatcher's
        // ApplyOwnership stamps the real owner at spawn time.
        GameEntity ge = root.AddComponent<GameEntity>();
        ge.ownerPlayerId          = 0;
        ge.teamId                 = 0;
        ge.entityType             = EntityType.Building;
        ge.prefabTypeId           = "CommandCenter";
        ge.overrideTeamFromHealth = false;   // owner authoritative, not Health.team
        ge.EditorResetId();                  // ensure spawned instances get fresh ids

        // TeamColorMarker so the cube re-paints per owner via the Phase 9
        // ApplyOwnership → ForceRepaint pipeline.
        TeamColorMarker tcm = root.AddComponent<TeamColorMarker>();
        tcm.team = TeamColorMarker.Team.Player;
        if (rend != null) tcm.bodyColorRenderers.Add(rend);

        // Ensure the Prefabs folder exists then save.
        string dir = Path.GetDirectoryName(PrefabPath);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        GameObject saved = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        Debug.Log($"[CreateCCPrefab] ✓ Saved {PrefabPath} (worker={(workerPrefab != null ? "OK" : "MISSING")}, " +
                  $"dozer={(dozerPrefab != null ? "OK" : "MISSING")}, cost=Worker {producer.workerCost} / Dozer {producer.dozerCost}).");
        _ = saved;
    }
}
