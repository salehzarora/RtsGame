using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Adds (or refreshes) a <see cref="TeamColorMarker"/> on every Player prefab,
/// classifying the prefab's renderers into BODY (painted with team color),
/// DETAIL (kept neutral), and IGNORE (system children: HealthBar, etc.).
///
/// Menu: Tools → RTS → Setup → Apply Team Colors To Prefabs
///
/// Replaces the earlier "floating accent stripe" approach. Now the army color
/// reads on the main body of each unit/building, while mechanical/detail
/// parts (tracks, wheels, cannons, missiles, windows) keep their original
/// neutral materials.
///
/// What it does per prefab:
///   1. Disables (does not destroy — fields are unset, child is renamed and
///      set inactive) any legacy "TeamColorAccent" child created by the old
///      stripe tool.
///   2. Walks the prefab's renderer tree, classifying each renderer by name
///      using <see cref="ClassifyByName"/>.
///   3. Adds a <see cref="TeamColorMarker"/> if missing and assigns the three
///      renderer lists.
///   4. Saves the prefab.
///
/// What it does NOT do:
///   • Modify the prefab's main material asset. Color is applied at runtime
///     via MaterialPropertyBlock on body renderers only.
///   • Touch HealthBar, SelectionCircle, FirePoint, AmmoIndicator, or any
///     other classified-ignore child.
///   • Touch enemy prefabs.
/// </summary>
public static class ApplyTeamColorMarkersToPrefabs
{
    // ------------------------------------------------------------------ //
    // Targets
    // ------------------------------------------------------------------ //

    private static readonly string[] Targets =
    {
        // Infantry
        "SoldierPrefab",
        "RPGSoldierPrefab",
        "WorkerPrefab",
        // Vehicles
        "DozerPrefab",
        "HumveePrefab",
        "ArtilleryTankPrefab",
        // Aircraft
        "StrikeJetPrefab",
        // Buildings — CommandCenter is patched in the scene loop too (it's
        // typically a scene object, not a prefab in this project).
        "CommandCenter",
        "Barracks",
        "PowerPlantPrefab",
        "VehicleFactoryPrefab",
        "AirfieldPrefab",
    };

    // ------------------------------------------------------------------ //
    // Name classification — precedence is IGNORE > DETAIL > BODY > unmatched.
    // ------------------------------------------------------------------ //

    /// <summary>System children whose color is owned by another component.</summary>
    private static readonly string[] IgnoreKeywords =
    {
        "HealthBar", "Background", "Fill",
        "SelectionCircle", "SelectionRing",
        "FirePoint", "AmmoIndicator",
        "TeamColorAccent", "TeamColorStripe", "ColorAccent", "AccentBar",
    };

    /// <summary>Mechanical / neutral parts that should keep their original color.</summary>
    private static readonly string[] DetailKeywords =
    {
        // Vehicle mechanicals
        "Wheel", "Track", "Blade",
        // Weapons & ordnance
        "Cannon", "Barrel", "MachineGun", "Gun", "Weapon",
        "Missile", "Rocket", "Pod", "Rifle",
        // Glass & dark trim
        "Window", "Glass",
        // Limbs/feet that should keep boot/skin color
        "Leg", "Boot", "Head",
        // Plant / building trim
        "Exhaust", "Stack", "Trim", "Door",
        // Ground markings on Airfield
        "Apron", "Runway", "Taxi", "Lane",
    };

    /// <summary>Main body / silhouette parts that read as the army color.</summary>
    private static readonly string[] BodyKeywords =
    {
        // Generic
        "Body", "Hull", "Main", "Wall", "BuildingBody", "Armor",
        // Vehicle bodies
        "Cabin", "Hood", "Turret",
        // Aircraft
        "Fuselage", "Wing", "Tail", "Nose",
        // Buildings
        "Roof", "Hangar", "Tower", "Plant",
        // Infantry uniform
        "Helmet", "Torso", "Arm", "Uniform", "Backpack",
    };

    // ------------------------------------------------------------------ //
    // Entry
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Setup/Apply Team Colors To Prefabs")]
    public static void Run()
    {
        Debug.Log("[ApplyTeamColors] ── Running ──");

        int patched = 0;
        int missing = 0;

        foreach (string name in Targets)
        {
            string path = FindPrefabPath(name);
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogWarning($"[ApplyTeamColors]   ⚠ {name}.prefab not found — skipping.");
                missing++;
                continue;
            }

            if (PatchPrefab(path, name)) patched++;
        }

        int scenePatched = PatchInSceneCommandCenter();

        AssetDatabase.SaveAssets();
        Debug.Log($"[ApplyTeamColors] ✓ Done. Prefabs patched: {patched}, missing: {missing}, " +
                  $"scene CommandCenter patched: {scenePatched}.\n" +
                  "  Body renderers will now take the player's team color at runtime.\n" +
                  "  Run Tools → RTS → Setup → Strip Old Team Accent Bars From Scene if any " +
                  "old stripe children still float above scene objects.");
    }

    // ------------------------------------------------------------------ //
    // Per-prefab patch
    // ------------------------------------------------------------------ //

    private static bool PatchPrefab(string path, string prefabName)
    {
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        try
        {
            int body, detail, ignore;
            ApplyMarkerTo(root, out body, out detail, out ignore);
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Debug.Log($"[ApplyTeamColors]   ✓ {prefabName}: body={body}, detail={detail}, ignore={ignore}.");
            return true;
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private static int PatchInSceneCommandCenter()
    {
        CommandCenter[] ccs = Object.FindObjectsByType<CommandCenter>(FindObjectsSortMode.None);
        if (ccs.Length == 0) return 0;

        int n = 0;
        foreach (CommandCenter cc in ccs)
        {
            ApplyMarkerTo(cc.gameObject, out int body, out int detail, out int ignore);
            EditorUtility.SetDirty(cc.gameObject);
            n++;
            Debug.Log($"[ApplyTeamColors]   ✓ Scene CommandCenter '{cc.name}': " +
                      $"body={body}, detail={detail}, ignore={ignore}.");
        }
        return n;
    }

    // ------------------------------------------------------------------ //
    // Marker assignment
    // ------------------------------------------------------------------ //

    private static void ApplyMarkerTo(GameObject root, out int bodyCount, out int detailCount, out int ignoreCount)
    {
        // Disable any legacy TeamColorAccent child created by the prior stripe
        // tool. We rename + deactivate rather than destroy so the prefab diff
        // stays small and a human can re-enable if they want.
        DisableLegacyAccentChildren(root);

        List<Renderer> body   = new List<Renderer>();
        List<Renderer> detail = new List<Renderer>();
        List<Renderer> ignore = new List<Renderer>();

        Renderer[] all = root.GetComponentsInChildren<Renderer>(includeInactive: true);
        foreach (Renderer r in all)
        {
            if (r == null) continue;

            // Never paint UI / health-bar renderers or sprite/text meshes.
            if (r is CanvasRenderer) { ignore.Add(r); continue; }

            switch (ClassifyByName(r.transform))
            {
                case Role.Body:   body.Add(r);   break;
                case Role.Detail: detail.Add(r); break;
                case Role.Ignore: ignore.Add(r); break;
                default: /* unmatched — leave untouched */ break;
            }
        }

        TeamColorMarker marker = root.GetComponent<TeamColorMarker>();
        if (marker == null) marker = root.AddComponent<TeamColorMarker>();
        marker.team                = TeamColorMarker.Team.Player;
        marker.bodyColorRenderers  = body;
        marker.detailRenderers     = detail;
        marker.ignoreRenderers     = ignore;
        EditorUtility.SetDirty(marker);
        EditorUtility.SetDirty(root);

        bodyCount   = body.Count;
        detailCount = detail.Count;
        ignoreCount = ignore.Count;
    }

    private static void DisableLegacyAccentChildren(GameObject root)
    {
        // Match any child that smells like an old stripe.
        foreach (Transform t in root.GetComponentsInChildren<Transform>(includeInactive: true))
        {
            if (t == null) continue;
            string n = t.name;
            if (n == "TeamColorAccent" || n == "TeamColorStripe" ||
                n == "ColorAccent"     || n == "AccentBar")
            {
                t.gameObject.SetActive(false);
                t.name = "TeamColorAccent_DISABLED";
            }
        }
    }

    // ------------------------------------------------------------------ //
    // Name-based classifier
    // ------------------------------------------------------------------ //

    private enum Role { Unmatched, Body, Detail, Ignore }

    /// <summary>
    /// Classifies a renderer by its full Transform name AND by inspecting each
    /// ancestor's name. The ancestor check lets us catch cases like
    /// "TeamColorAccent/Stripe" — the child mesh inherits its parent's role.
    /// </summary>
    private static Role ClassifyByName(Transform t)
    {
        for (Transform cur = t; cur != null; cur = cur.parent)
        {
            string n = cur.name;
            if (Match(n, IgnoreKeywords)) return Role.Ignore;
            if (Match(n, DetailKeywords)) return Role.Detail;
        }

        // Body check is on the leaf only — we don't want a child named "Vent"
        // to be painted just because its grandparent is named "Hull". Body
        // intent should be expressed at the renderer's own GameObject.
        if (Match(t.name, BodyKeywords)) return Role.Body;
        return Role.Unmatched;
    }

    private static bool Match(string source, string[] keywords)
    {
        if (string.IsNullOrEmpty(source)) return false;
        for (int i = 0; i < keywords.Length; i++)
            if (source.IndexOf(keywords[i], System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        return false;
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private static string FindPrefabPath(string prefabName)
    {
        string canonical = $"Assets/_Game/Prefabs/{prefabName}.prefab";
        if (File.Exists(canonical)) return canonical;

        string[] guids = AssetDatabase.FindAssets($"{prefabName} t:Prefab");
        foreach (string guid in guids)
        {
            string p = AssetDatabase.GUIDToAssetPath(guid);
            if (p.EndsWith($"/{prefabName}.prefab")) return p;
        }
        return null;
    }
}
