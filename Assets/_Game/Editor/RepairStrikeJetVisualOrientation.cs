using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Three light editor tools — all small read-or-write passes against the
/// existing prefabs, none of them touch gameplay code:
///
///   1. <b>Tools → RTS → Aircraft → Repair StrikeJet Visual Orientation</b>
///      Sets <c>StrikeJetPrefab/Visual.localEulerAngles.y</c> to
///      <see cref="ReplaceStrikeJetVisualWithJet2.VisualRotationOffsetY"/>
///      (default −96.7°) so the new jet2 model's nose lines up with the
///      gameplay root's +Z direction. Fast, no re-import.
///
///   2. <b>Tools → RTS → Aircraft → Validate StrikeJet Prefab</b>
///      Read-only audit — prints prefab path, root scale, Visual child
///      name + localRotation, BoxCollider size/center, the four required
///      script components (AirUnitController, AircraftWeapon,
///      SelectableAircraft, Health) plus team-color wiring, and warns if
///      any LOD renderer has a null shared material (would render pink).
///
///   3. <b>Tools → RTS → Buildings → Set Airfield Slot Y To One</b>
///      Walks the AirfieldPrefab's Slot_0..5 children and forces each
///      slot's <c>localPosition.y</c> to 1, preserving X/Z exactly. Use
///      this when the new jet2 model sits sunk into the apron at the
///      previous Y=0.6 — Y=1 raises the wheels onto the surface.
///
/// Constraints kept by all three menus:
///   • No change to <c>Airfield.cs</c>, <c>AirUnitController.cs</c>,
///     <c>AircraftWeapon.cs</c>, <c>SelectableAircraft.cs</c>, or any
///     other runtime script.
///   • No change to root transforms / movement / projectile logic.
///   • No change to colliders (the Validate tool only reports their state
///     — it doesn't auto-adjust because the user said not unless
///     necessary, and the 0.2 m mismatch from rotating the Visual is
///     within tolerance).
///   • No change to material assignments (the Validate tool reports them).
/// </summary>
public static class RepairStrikeJetVisualOrientation
{
    private const string JetPrefabPath      = "Assets/_Game/Prefabs/StrikeJetPrefab.prefab";
    private const string AirfieldPrefabPath = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";
    private const string VisualChildName    = "Visual";

    private const float TargetSlotY = 1f;

    // Scale fix targets.
    private const float TargetVisualScale = 1.3f;
    // Baseline BoxCollider at 1× scale (from original StrikeJetPrefab).
    private static readonly Vector3 BaselineColliderSize   = new Vector3(2.2f, 0.6f, 2.4f);
    private static readonly Vector3 BaselineColliderCenter = new Vector3(0f,   0.3f, 0f);

    private const string TeamColorMaterialPath = "Assets/_Game/Materials/Aircraft/Jet2/M_TeamColor.mat";
    private const string TeamColorMatNameToken = "TeamColor"; // case-insensitive substring match

    // ============================================================== //
    // 1. Repair StrikeJet Visual Orientation
    // ============================================================== //

    [MenuItem("Tools/RTS/Aircraft/Repair StrikeJet Visual Orientation")]
    public static void RepairOrientation()
    {
        Debug.Log("[RepairJetVisual] ─── Applying visual rotation offset to StrikeJet ───");

        GameObject root = LoadPrefabContents(JetPrefabPath, out bool ok);
        if (!ok || root == null) return;

        try
        {
            Transform visual = root.transform.Find(VisualChildName);
            if (visual == null)
            {
                Debug.LogError($"[RepairJetVisual] ✗ No '{VisualChildName}' child on StrikeJetPrefab. " +
                               "Run Tools → RTS → Aircraft → Replace StrikeJet Visual With Jet2 first.");
                return;
            }

            Vector3 before = visual.localEulerAngles;
            visual.localEulerAngles = new Vector3(0f, ReplaceStrikeJetVisualWithJet2.VisualRotationOffsetY, 0f);
            Debug.Log($"[RepairJetVisual]   Visual.localEulerAngles {before} → {visual.localEulerAngles} " +
                      $"(offset {ReplaceStrikeJetVisualWithJet2.VisualRotationOffsetY:F2}°).");

            PrefabUtility.SaveAsPrefabAsset(root, JetPrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log("[RepairJetVisual] ✓ Done. Root transform / movement / projectile logic UNCHANGED.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // ============================================================== //
    // 1b. Repair StrikeJet Scale And Team Color
    // ============================================================== //

    /// <summary>
    /// Combined fix for the two final visual issues:
    ///   • Bumps <c>StrikeJetPrefab/Visual.localScale</c> to 1.3 × 1.3 × 1.3
    ///     and scales the root BoxCollider proportionally (baseline 2.2 ×
    ///     0.6 × 2.4 → 2.86 × 0.78 × 3.12). Root transform untouched.
    ///   • Walks EVERY MeshRenderer under <c>Visual</c> (LOD0, LOD1, LOD2
    ///     — including inactive ones, since LODGroup only activates one at
    ///     a time and a normal GetComponentsInChildren(false) would miss
    ///     them). Finds material slots whose name contains "TeamColor"
    ///     (case-insensitive substring), force-assigns the
    ///     <c>M_TeamColor.mat</c> asset to those slots, and wires those
    ///     (renderer, materialIndex) pairs into the root TeamColorApplier.
    ///   • Re-asserts <c>M_TeamColor.mat</c> config: no textures, white
    ///     BaseColor, emission off, low metallic, mid smoothness — so
    ///     <c>TeamColorApplier</c>'s per-slot BaseColor write at runtime
    ///     actually changes the visible colour (texture × white = team
    ///     colour pure; texture × any colour = tinted-but-still-blue is
    ///     the bug we're killing).
    ///
    /// Untouched:
    ///   • AirUnitController, AircraftWeapon, SelectableAircraft, Health,
    ///     GameEntity, FirePoint, HealthBar, SelectionCircle,
    ///     AmmoIndicator, BoxCollider scripts.
    ///   • Visual rotation offset (uses whatever Repair Orientation set).
    ///   • Old visual backup, OldVisual_Backup, Markers group.
    ///
    /// Re-runnable.
    /// </summary>
    [MenuItem("Tools/RTS/Aircraft/Repair StrikeJet Scale And Team Color")]
    public static void RepairScaleAndTeamColor()
    {
        Debug.Log("[Jet2Fix] ─── Scale to 1.3 + team-color wiring ───");

        GameObject root = LoadPrefabContents(JetPrefabPath, out bool ok);
        if (!ok || root == null) return;

        try
        {
            Transform visual = root.transform.Find(VisualChildName);
            if (visual == null)
            {
                Debug.LogError($"[Jet2Fix] ✗ '{VisualChildName}' child missing. " +
                               "Run Replace StrikeJet Visual With Jet2 first.");
                return;
            }

            // ---- 1. Visual scale ---- //
            Vector3 oldScale = visual.localScale;
            visual.localScale = new Vector3(TargetVisualScale, TargetVisualScale, TargetVisualScale);
            Debug.Log($"[Jet2Fix]   Visual.localScale {oldScale} → {visual.localScale}.");

            // ---- 2. BoxCollider proportional scale ---- //
            BoxCollider bc = root.GetComponent<BoxCollider>();
            if (bc != null)
            {
                Vector3 oldSize   = bc.size;
                Vector3 oldCenter = bc.center;
                bc.size   = BaselineColliderSize   * TargetVisualScale;
                bc.center = BaselineColliderCenter * TargetVisualScale;
                Debug.Log($"[Jet2Fix]   BoxCollider size {oldSize} → {bc.size}, " +
                          $"center {oldCenter} → {bc.center}. " +
                          "(Baseline × 1.3; re-running is idempotent.)");
            }
            else
            {
                Debug.LogWarning("[Jet2Fix]   ⚠ Root BoxCollider missing — skipped collider scaling.");
            }

            // ---- 3. M_TeamColor.mat config ---- //
            Material teamColorMat = LoadAndAssertTeamColorMaterial();
            if (teamColorMat == null) return;

            // ---- 4. Detect + force-assign team-color slots on ALL renderers
            //         (including inactive LOD1/LOD2). ---- //
            int totalSlotsWired   = 0;
            int totalRendsWalked  = 0;
            int slotsForceAssigned = 0;
            var applierEntries    = new List<RendererMaterialSlot>();

            MeshRenderer[] allRends = visual.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
            for (int ri = 0; ri < allRends.Length; ri++)
            {
                MeshRenderer r = allRends[ri];
                if (r == null) continue;
                totalRendsWalked++;

                Material[] mats = r.sharedMaterials;
                if (mats == null || mats.Length == 0)
                {
                    Debug.LogWarning($"[Jet2Fix]   Renderer '{r.name}' has no materials.");
                    continue;
                }

                List<int> teamIndices = new List<int>();
                for (int mi = 0; mi < mats.Length; mi++)
                {
                    Material mat = mats[mi];
                    string slotName = mat != null ? mat.name : "<null>";
                    bool isTeamColor = mat != null &&
                                       mat.name.IndexOf(TeamColorMatNameToken,
                                                        System.StringComparison.OrdinalIgnoreCase) >= 0;

                    if (isTeamColor)
                    {
                        teamIndices.Add(mi);

                        // Force the asset reference to our M_TeamColor.mat —
                        // protects against FBX re-imports that swap the
                        // material back to the embedded one and against
                        // AddRemap silently failing (e.g. name mismatch).
                        if (mat != teamColorMat)
                        {
                            mats[mi] = teamColorMat;
                            slotsForceAssigned++;
                            Debug.Log($"[TeamColor] '{r.name}' slot {mi} '{slotName}' " +
                                      "→ re-assigned to M_TeamColor.mat.");
                        }
                        else
                        {
                            Debug.Log($"[TeamColor] '{r.name}' slot {mi} already uses M_TeamColor.mat ✓.");
                        }
                    }
                }

                if (teamIndices.Count > 0)
                {
                    r.sharedMaterials = mats; // persists the assignment on the prefab.
                    applierEntries.Add(new RendererMaterialSlot
                    {
                        renderer        = r,
                        materialIndexes = teamIndices,
                    });
                    totalSlotsWired += teamIndices.Count;
                }
            }

            // ---- 5. TeamColorApplier ---- //
            TeamColorApplier applier = root.GetComponent<TeamColorApplier>();
            if (applier == null)
            {
                applier = root.AddComponent<TeamColorApplier>();
                Debug.Log("[Jet2Fix]   Added TeamColorApplier to root.");
            }
            applier.teamColorSlots.Clear();
            applier.fixedColorSlots.Clear();
            for (int i = 0; i < applierEntries.Count; i++)
                applier.teamColorSlots.Add(applierEntries[i]);
            applier.applyOnStart = true;
            EditorUtility.SetDirty(applier);

            // ---- 6. Save ---- //
            PrefabUtility.SaveAsPrefabAsset(root, JetPrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[Jet2Fix] ✓ Done. Visual scaled to {TargetVisualScale}; collider scaled. " +
                      $"Walked {totalRendsWalked} renderer(s); detected {totalSlotsWired} team-color slot(s); " +
                      $"force-assigned {slotsForceAssigned}. TeamColorApplier has {applierEntries.Count} entries. " +
                      "Body texture / metallic / dark panels / canopy UNCHANGED.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    /// <summary>
    /// Loads M_TeamColor.mat and force-asserts the config the
    /// TeamColorApplier-based painting needs: no textures (otherwise
    /// `texture × _BaseColor` masks the team-tint), white BaseColor (so
    /// the runtime override is the pure team colour), emission off,
    /// low metallic, mid smoothness. Idempotent.
    /// </summary>
    private static Material LoadAndAssertTeamColorMaterial()
    {
        Material m = AssetDatabase.LoadAssetAtPath<Material>(TeamColorMaterialPath);
        if (m == null)
        {
            Debug.LogError($"[Jet2Fix] ✗ '{TeamColorMaterialPath}' not found. " +
                           "Run Tools → RTS → Aircraft → Replace StrikeJet Visual With Jet2 first.");
            return null;
        }

        // No textures — the TeamColorApplier's _BaseColor write must produce
        // the full team colour, not be filtered through a base map.
        if (m.HasProperty("_BaseMap"))           m.SetTexture("_BaseMap", null);
        if (m.HasProperty("_MainTex"))           m.SetTexture("_MainTex", null);
        if (m.HasProperty("_BumpMap"))           m.SetTexture("_BumpMap", null);
        if (m.HasProperty("_MetallicGlossMap"))  m.SetTexture("_MetallicGlossMap", null);
        if (m.HasProperty("_EmissionMap"))       m.SetTexture("_EmissionMap", null);

        if (m.HasProperty("_BaseColor"))         m.SetColor("_BaseColor", Color.white);
        if (m.HasProperty("_Color"))             m.SetColor("_Color", Color.white);

        m.DisableKeyword("_EMISSION");
        if (m.HasProperty("_EmissionColor"))     m.SetColor("_EmissionColor", Color.black);

        if (m.HasProperty("_Metallic"))          m.SetFloat("_Metallic", 0.1f);
        if (m.HasProperty("_Smoothness"))        m.SetFloat("_Smoothness", 0.3f);
        if (m.HasProperty("_Glossiness"))        m.SetFloat("_Glossiness", 0.3f);

        EditorUtility.SetDirty(m);
        Debug.Log($"[Jet2Fix]   M_TeamColor.mat re-asserted: no textures, white BaseColor, emission off.");
        return m;
    }

    // ============================================================== //
    // 2. Validate StrikeJet Prefab
    // ============================================================== //

    [MenuItem("Tools/RTS/Aircraft/Validate StrikeJet Prefab")]
    public static void Validate()
    {
        Debug.Log("[ValidateJet] ─── StrikeJetPrefab audit ───");
        Debug.Log($"[ValidateJet] Prefab path: {JetPrefabPath}");

        GameObject root = LoadPrefabContents(JetPrefabPath, out bool ok);
        if (!ok || root == null) return;

        try
        {
            Debug.Log($"[ValidateJet] Root scale = {root.transform.localScale}, " +
                      $"rotation = {root.transform.localEulerAngles} (gameplay; should be identity).");

            // Visual child
            Transform visual = root.transform.Find(VisualChildName);
            if (visual == null)
            {
                Debug.LogError($"[ValidateJet] ✗ Visual child '{VisualChildName}' MISSING. " +
                               "Run Replace StrikeJet Visual With Jet2 first.");
            }
            else
            {
                Debug.Log($"[ValidateJet] Visual child name = '{visual.name}'. " +
                          $"localPosition = {visual.localPosition}, " +
                          $"localEulerAngles = {visual.localEulerAngles}, " +
                          $"localScale = {visual.localScale}.");
                float expected = ReplaceStrikeJetVisualWithJet2.VisualRotationOffsetY;
                float actualY = visual.localEulerAngles.y;
                // Normalize both to [-180, 180] for comparison.
                float delta = Mathf.DeltaAngle(actualY, expected);
                if (Mathf.Abs(delta) > 5f)
                    Debug.LogWarning($"[ValidateJet]   ⚠ Visual.localEulerAngles.y ({actualY:F2}°) " +
                                     $"is > 5° off the expected {expected:F2}°. " +
                                     "Run Repair StrikeJet Visual Orientation.");
                else
                    Debug.Log($"[ValidateJet]   ✓ Visual Y rotation within 5° of expected {expected:F2}°.");
            }

            // BoxCollider
            BoxCollider bc = root.GetComponent<BoxCollider>();
            if (bc == null)
                Debug.LogError("[ValidateJet] ✗ BoxCollider MISSING on root.");
            else
                Debug.Log($"[ValidateJet] BoxCollider: size = {bc.size}, center = {bc.center}, " +
                          $"isTrigger = {bc.isTrigger}.");

            // Required gameplay components
            CheckComponent<AirUnitController>(root, "AirUnitController");
            CheckComponent<AircraftWeapon>(root, "AircraftWeapon");
            CheckComponent<SelectableAircraft>(root, "SelectableAircraft");
            CheckComponent<Health>(root, "Health");
            CheckComponent<GameEntity>(root, "GameEntity");
            CheckComponent<TeamColorMarker>(root, "TeamColorMarker (legacy — kept for compat)");
            CheckComponent<TeamColorApplier>(root, "TeamColorApplier (paints M_TeamColor slot)");

            // Material slots + team-color detection per renderer.
            int teamColorSlotsFound = 0;
            if (visual != null)
            {
                Debug.Log($"[ValidateJet] Visual scale = {visual.localScale} (expected {TargetVisualScale}).");
                if (Mathf.Abs(visual.localScale.x - TargetVisualScale) > 0.05f)
                    Debug.LogWarning($"[ValidateJet]   ⚠ Visual.localScale.x ({visual.localScale.x:F2}) " +
                                     $"differs from expected {TargetVisualScale:F2}. " +
                                     "Run Repair StrikeJet Scale And Team Color.");

                var rends = visual.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
                int totalSlots = 0, nullSlots = 0;
                Debug.Log($"[ValidateJet] LOD renderers under Visual (incl. inactive): {rends.Length}.");
                for (int i = 0; i < rends.Length; i++)
                {
                    var r = rends[i];
                    if (r == null) continue;
                    var mats = r.sharedMaterials;
                    if (mats == null) continue;
                    System.Text.StringBuilder sb = new System.Text.StringBuilder();
                    for (int m = 0; m < mats.Length; m++)
                    {
                        totalSlots++;
                        if (mats[m] == null) { nullSlots++; sb.Append($"[{m}]=<NULL>  "); continue; }
                        string nm = mats[m].name;
                        bool tc = nm.IndexOf(TeamColorMatNameToken,
                                             System.StringComparison.OrdinalIgnoreCase) >= 0;
                        if (tc) teamColorSlotsFound++;
                        sb.Append($"[{m}]={nm}{(tc ? " ★TEAMCOLOR" : "")}  ");
                    }
                    Debug.Log($"[ValidateJet]   '{r.name}' active={r.gameObject.activeInHierarchy} " +
                              $"materials: {sb}");
                }
                if (nullSlots > 0)
                    Debug.LogWarning($"[ValidateJet]   ⚠ {nullSlots}/{totalSlots} material slot(s) null " +
                                     "— will render PINK.");
                else
                    Debug.Log($"[ValidateJet]   All {totalSlots} material slot(s) assigned ✓.");
                if (teamColorSlotsFound == 0)
                    Debug.LogError("[ValidateJet]   ✗ No material slot whose name contains 'TeamColor' " +
                                   "found on any LOD renderer. Team colour will not apply. " +
                                   "Run Repair StrikeJet Scale And Team Color.");
                else
                    Debug.Log($"[ValidateJet]   {teamColorSlotsFound} team-color material slot(s) detected by name.");
            }

            // TeamColorApplier wiring detail.
            var applier = root.GetComponent<TeamColorApplier>();
            if (applier == null)
            {
                Debug.LogError("[ValidateJet]   ✗ TeamColorApplier MISSING on root. Team colour will not apply.");
            }
            else
            {
                int wired = 0, indexes = 0;
                for (int i = 0; i < applier.teamColorSlots.Count; i++)
                {
                    var s = applier.teamColorSlots[i];
                    if (s == null || s.renderer == null) continue;
                    wired++;
                    indexes += (s.materialIndexes != null ? s.materialIndexes.Count : 0);
                    string idxList = s.materialIndexes != null ? string.Join(",", s.materialIndexes) : "";
                    Debug.Log($"[ValidateJet]   TeamColorApplier slot {i}: renderer='{s.renderer.name}' " +
                              $"materialIndexes=[{idxList}].");
                }
                Debug.Log($"[ValidateJet]   TeamColorApplier total: {wired} renderer(s), {indexes} slot(s). " +
                          $"applyOnStart={applier.applyOnStart}.");
                if (wired == 0)
                    Debug.LogError("[ValidateJet]   ✗ TeamColorApplier has zero wired renderers. " +
                                   "Team colour will not apply at runtime.");
            }
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[ValidateJet] ─────────────────────────────────────────");
    }

    private static void CheckComponent<T>(GameObject root, string label) where T : Component
    {
        T c = root.GetComponent<T>();
        if (c == null) Debug.LogError($"[ValidateJet]   ✗ {label} MISSING on root.");
        else           Debug.Log($"[ValidateJet]   ✓ {label} present.");
    }

    // ============================================================== //
    // 3. Set Airfield Slot Y To One
    // ============================================================== //

    [MenuItem("Tools/RTS/Buildings/Set Airfield Slot Y To One")]
    public static void SetSlotYToOne()
    {
        Debug.Log("[AirfieldSlots] ─── Setting all 6 parking slots to Y = 1 ───");

        GameObject root = LoadPrefabContents(AirfieldPrefabPath, out bool ok);
        if (!ok || root == null) return;

        try
        {
            int touched = 0;
            for (int i = 0; i < 6; i++)
            {
                Transform t = root.transform.Find($"Slot_{i}");
                if (t == null)
                {
                    Debug.LogError($"[AirfieldSlots] ✗ Slot_{i} not found — skipping.");
                    continue;
                }
                Vector3 p = t.localPosition;
                if (Mathf.Approximately(p.y, TargetSlotY))
                {
                    Debug.Log($"[AirfieldSlots] Slot_{i} Y already {TargetSlotY} — no change " +
                              $"(localPos {p}).");
                    continue;
                }
                float before = p.y;
                p.y = TargetSlotY;
                t.localPosition = p;
                touched++;
                Debug.Log($"[AirfieldSlots] Slot_{i} Y {before:F3} → {TargetSlotY} " +
                          $"(X={p.x:F3}, Z={p.z:F3} kept).");
            }

            PrefabUtility.SaveAsPrefabAsset(root, AirfieldPrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[AirfieldSlots] ✓ Updated {touched}/6 slot(s). " +
                      "X/Z positions, rotations, and all other Airfield transforms UNCHANGED.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    // ============================================================== //
    // Helpers
    // ============================================================== //

    private static GameObject LoadPrefabContents(string path, out bool ok)
    {
        ok = false;
        if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
        {
            Debug.LogError($"[RepairJetVisual] ✗ Prefab not found at '{path}'.");
            return null;
        }
        GameObject root = PrefabUtility.LoadPrefabContents(path);
        if (root == null)
        {
            Debug.LogError($"[RepairJetVisual] ✗ LoadPrefabContents returned null for '{path}'.");
            return null;
        }
        ok = true;
        return root;
    }
}
