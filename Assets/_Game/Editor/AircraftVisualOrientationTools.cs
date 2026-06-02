using UnityEditor;
using UnityEngine;

/// <summary>
/// Editor menus for <see cref="AircraftVisualOrientation"/>.
///
/// <list type="number">
///   <item><b>Tools → RTS → Aircraft → Add Aircraft Visual Orientation</b>
///         — adds the component to <c>StrikeJetPrefab</c> root if absent,
///         auto-wires the <c>Visual</c> child reference, and saves. Idempotent —
///         re-running on a prefab that already has the component just re-confirms
///         the wiring without resetting tuned values.</item>
///   <item><b>Tools → RTS → Aircraft → Validate Aircraft Orientation</b>
///         — read-only audit. Verifies the component exists, the Visual
///         child is wired, and reports the captured model offset (which must
///         be the same -96.7° set by Replace StrikeJet Visual With Jet2 so
///         pitch+bank ride on top of it instead of overwriting it).</item>
/// </list>
///
/// Neither menu touches: AirUnitController fields, slot positions, taxi
/// routes, runway markers, weapon, colliders, materials, or the team-color
/// pipeline. Pure orientation wiring.
/// </summary>
public static class AircraftVisualOrientationTools
{
    private const string PrefabPath      = "Assets/_Game/Prefabs/StrikeJetPrefab.prefab";
    private const string VisualChildName = "Visual";

    // ------------------------------------------------------------------ //
    // 1. Add
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Aircraft/Add Aircraft Visual Orientation")]
    public static void Add()
    {
        Debug.Log("[AddAircraftOrient] ─── Adding AircraftVisualOrientation to StrikeJetPrefab ───");

        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError($"[AddAircraftOrient] ✗ Prefab not found at '{PrefabPath}'.");
            return;
        }

        try
        {
            // 1. Require AirUnitController — the orientation component reads State + LocallyControlled from it.
            var controller = root.GetComponent<AirUnitController>();
            if (controller == null)
            {
                Debug.LogError("[AddAircraftOrient] ✗ AirUnitController missing on prefab root. " +
                               "Aborting — orientation component requires it.");
                return;
            }

            // 2. Find or add AircraftVisualOrientation.
            var orient = root.GetComponent<AircraftVisualOrientation>();
            bool added = false;
            if (orient == null)
            {
                orient = root.AddComponent<AircraftVisualOrientation>();
                added  = true;
                Debug.Log("[AddAircraftOrient]   AircraftVisualOrientation component ADDED to root.");
            }
            else
            {
                Debug.Log("[AddAircraftOrient]   AircraftVisualOrientation already present — re-confirming wiring without overwriting tuned values.");
            }

            // 3. Auto-wire the Visual child if not already pointed at it.
            Transform visual = root.transform.Find(VisualChildName);
            if (visual == null)
            {
                Debug.LogError($"[AddAircraftOrient] ✗ No '{VisualChildName}' child on prefab. " +
                               "Run Tools → RTS → Aircraft → Replace StrikeJet Visual With Jet2 first.");
                return;
            }

            if (orient.visual != visual)
            {
                orient.visual = visual;
                Debug.Log($"[AddAircraftOrient]   Visual reference wired → '{visual.name}' " +
                          $"(localRotation Euler = {visual.localEulerAngles}).");
            }
            else
            {
                Debug.Log($"[AddAircraftOrient]   Visual reference already correct → '{visual.name}'.");
            }

            // 4. Report the model offset that will be captured at runtime Awake.
            Vector3 offsetEuler = visual.localEulerAngles;
            float expectedY     = ReplaceStrikeJetVisualWithJet2.VisualRotationOffsetY;
            float observedY     = Mathf.DeltaAngle(0f, offsetEuler.y); // wrap to [-180,180] for legibility
            bool offsetOk       = Mathf.Abs(observedY - expectedY) < 0.5f;
            string offsetVerdict = offsetOk
                ? "OK — matches Replace StrikeJet Visual With Jet2's offset."
                : $"WARNING — expected ~{expectedY:F2}°, found {observedY:F2}°. " +
                  "Run Tools → RTS → Aircraft → Repair StrikeJet Visual Orientation if the model now slides sideways.";
            Debug.Log($"[AddAircraftOrient]   Visual.localEulerAngles.y (model offset that will be captured at Awake) " +
                      $"= {observedY:F2}° → {offsetVerdict}");

            // 5. Save prefab + asset DB.
            EditorUtility.SetDirty(orient);
            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[AddAircraftOrient] ✓ Done ({(added ? "ADDED" : "already present, re-wired")}). " +
                      "Component will compute yaw from velocity, pitch from FlightState, and bank from yaw rate. " +
                      "Existing AirUnitController flight logic UNCHANGED.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[AddAircraftOrient] ─────────────────────────────────────────");
    }

    // ------------------------------------------------------------------ //
    // 2. Validate
    // ------------------------------------------------------------------ //

    [MenuItem("Tools/RTS/Aircraft/Validate Aircraft Orientation")]
    public static void Validate()
    {
        Debug.Log("[ValidateAircraftOrient] ─── Audit ───");

        GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (asset == null)
        {
            Debug.LogError($"[ValidateAircraftOrient] ✗ Prefab not found at '{PrefabPath}'.");
            return;
        }

        Debug.Log($"[ValidateAircraftOrient] Prefab: '{PrefabPath}'");

        // -- Component presence --
        var orient = asset.GetComponent<AircraftVisualOrientation>();
        if (orient == null)
        {
            Debug.LogError("[ValidateAircraftOrient] ✗ AircraftVisualOrientation NOT present on root. " +
                           "Run Tools → RTS → Aircraft → Add Aircraft Visual Orientation.");
            Debug.Log("[ValidateAircraftOrient] ─────────────────────────────────────────");
            return;
        }
        Debug.Log("[ValidateAircraftOrient]   ✓ AircraftVisualOrientation present on root.");

        // -- AirUnitController dependency --
        var controller = asset.GetComponent<AirUnitController>();
        Debug.Log(controller != null
            ? "[ValidateAircraftOrient]   ✓ AirUnitController present (state machine source)."
            : "[ValidateAircraftOrient]   ✗ AirUnitController MISSING — orientation cannot read FlightState.");

        // -- Visual reference --
        Transform visual = orient.visual != null
            ? orient.visual
            : asset.transform.Find(VisualChildName);
        if (visual == null)
        {
            Debug.LogError("[ValidateAircraftOrient]   ✗ Visual child not found. Pitch/bank will no-op.");
        }
        else
        {
            float y = Mathf.DeltaAngle(0f, visual.localEulerAngles.y);
            float expectedY = ReplaceStrikeJetVisualWithJet2.VisualRotationOffsetY;
            bool ok = Mathf.Abs(y - expectedY) < 0.5f;
            Debug.Log($"[ValidateAircraftOrient]   Visual child         = '{visual.name}'");
            Debug.Log($"[ValidateAircraftOrient]   Visual localRotation = {visual.localEulerAngles} " +
                      $"(captured at runtime Awake as ModelOffset)");
            Debug.Log($"[ValidateAircraftOrient]     • Y component      = {y:F2}°, expected ~{expectedY:F2}° " +
                      $"→ {(ok ? "OK" : "MISMATCH — run Repair StrikeJet Visual Orientation")}");
            Debug.Log($"[ValidateAircraftOrient]     • localScale       = {visual.localScale}");
        }

        // -- Pitch mode summary --
        string pitchMode = orient.forceTestPitch
            ? "FORCE-TEST (pipeline verifier)"
            : (orient.useVerticalVelocityForPitch
                ? "hybrid (state baseline + vertical velocity, clamped)"
                : "state-only");
        Debug.Log($"[ValidateAircraftOrient]   Pitch mode             = {pitchMode}");
        Debug.Log("[ValidateAircraftOrient]   Real states used:");
        Debug.Log("      Climbing       → takeoffPitch (nose UP)");
        Debug.Log("      FinalLanding   → landingPitch (nose DOWN — this is the actual descent)");
        Debug.Log("      LandingApproach → cruisePitch (level glide at flightAltitude — descent hasn't started yet)");
        Debug.Log("      Ground (Parked/Taxi/Align/Roll) → 0 (forced)");
        Debug.Log("      Other airborne → cruisePitch + vertical-velocity blend");

        // -- Rotation order (locked) --
        Debug.Log("[ValidateAircraftOrient]   Visual.localRotation order = Euler(pitch,0,bank) * ModelOffset");
        Debug.Log("      → pitch/bank are applied in the ROOT frame AFTER ModelOffset rotates the");
        Debug.Log("        asset's nose to root +Z. Reversed order would map pitch onto the asset's");
        Debug.Log("        nose axis (= roll), not the lateral axis.");

        // -- Tuning snapshot --
        Debug.Log("[ValidateAircraftOrient]   Tuning:");
        Debug.Log($"      yawTurnSpeed                  = {orient.yawTurnSpeed}");
        Debug.Log($"      minVelocityForRotation        = {orient.minVelocityForRotation}");
        Debug.Log($"      takeoffPitch                  = {orient.takeoffPitch}°  (Unity convention: negative = nose UP)");
        Debug.Log($"      landingPitch                  = {orient.landingPitch}°  (positive = nose DOWN)");
        Debug.Log($"      cruisePitch                   = {orient.cruisePitch}°");
        Debug.Log($"      pitchResponsiveness           = {orient.pitchResponsiveness}");
        Debug.Log($"      useVerticalVelocityForPitch   = {orient.useVerticalVelocityForPitch}");
        Debug.Log($"      verticalVelocityToPitchScale  = {orient.verticalVelocityToPitchScale}°/(m/s)");
        Debug.Log($"      verticalVelocityDeadband      = {orient.verticalVelocityDeadband} m/s");
        Debug.Log($"      maxBankAngle                  = {orient.maxBankAngle}°");
        Debug.Log($"      bankYawRateForFullBank        = {orient.bankYawRateForFullBank}°/s");
        Debug.Log($"      bankResponsiveness            = {orient.bankResponsiveness}");
        Debug.Log($"      forceTestPitch                = {orient.forceTestPitch}");
        Debug.Log($"      forcedPitchDegrees            = {orient.forcedPitchDegrees}°");
        Debug.Log($"      debugAircraftOrientation      = {orient.debugAircraftOrientation}");

        // -- Sanity --
        if (orient.takeoffPitch > 0f)
            Debug.LogWarning("[ValidateAircraftOrient]   ⚠ takeoffPitch > 0 means nose DOWN during climb. " +
                             "Did you mean a negative value?");
        if (orient.landingPitch < 0f)
            Debug.LogWarning("[ValidateAircraftOrient]   ⚠ landingPitch < 0 means nose UP during approach. " +
                             "Did you mean a positive value?");
        if (orient.maxBankAngle > 60f)
            Debug.LogWarning("[ValidateAircraftOrient]   ⚠ maxBankAngle > 60° reads as aerobatic; " +
                             "RTS top-down typically tops out around 30°.");
        if (orient.forceTestPitch)
            Debug.LogWarning("[ValidateAircraftOrient]   ⚠ forceTestPitch is ON — the Visual is locked to " +
                             $"{orient.forcedPitchDegrees}° pitch regardless of flight state. Turn it OFF for normal play.");

        Debug.Log("[ValidateAircraftOrient] ✓ Audit complete.");
        Debug.Log("[ValidateAircraftOrient] ─────────────────────────────────────────");
    }
}
