using UnityEditor;
using UnityEngine;

/// <summary>
/// Commits the AirfieldPrefab <c>Slot_0..5</c> and <c>Taxi_0..5</c> transforms
/// to the EXACT positions the user captured by manually placing six
/// StrikeJetPrefab references on the painted parking pads of the
/// Airfield_Unity_Final model. The user supplied world positions plus the
/// AirfieldPrefab's own world position; the local positions hard-coded
/// below are <c>jetWorld - airfieldWorld</c> with no rounding past what
/// the user delivered.
///
/// Reference jet world positions (with AirfieldPrefab at world (1, ~0, 1)):
///   Jet 1: ( 9.23999977,  0.6,  4.65932417)  →  Slot 0 local ( 8.23999977, 0.6,  3.65932417)
///   Jet 2: ( 7.38754559,  0.6,  1.20483637)  →  Slot 1 local ( 6.38754559, 0.6,  0.20483637)
///   Jet 3: ( 5.69712925,  0.6, -3.08381081)  →  Slot 2 local ( 4.69712925, 0.6, -4.08381081)
///   Jet 4: (-4.27180529,  0.6, -3.35003638)  →  Slot 3 local (-5.27180529, 0.6, -4.35003638)
///   Jet 5: (-5.12523460,  0.6,  1.26885521)  →  Slot 4 local (-6.12523460, 0.6,  0.26885521)
///   Jet 6: (-7.05147648,  0.6,  4.80998421)  →  Slot 5 local (-8.05147648, 0.6,  3.80998421)
///
/// Rotation: the user observed Y = -55.959° on the reference jet (nose
/// toward (-X, +Z) — diagonal inward + north toward the takeoff end of the
/// runway). That value is used verbatim for the three RIGHT-side slots
/// (positive X). LEFT-side slots are mirrored to +55.959° so their nose
/// points (+X, +Z) — same inward / north feel, opposite side.
///
/// Taxi points: each Taxi_N is placed midway between its Slot_N and the
/// runway centerline (X = slot.x × 0.5, same Z, Y = 0). They're ordered
/// waypoints — the Airfield script's BuildClearance just walks them in
/// sequence (per-slot Taxi → lane corridor mids → runway queue → start),
/// so this midpoint guarantees the jet's first taxi step is inward toward
/// the runway, without me needing to second-guess the FBX's apron
/// geometry beyond what the user already nailed.
///
/// What is NOT touched:
///   • Airfield.cs, AirUnitController.cs, BuildingPlacementManager.cs,
///     ConstructionSite.cs — production, queue, landing logic intact.
///   • Runway / lane corridor / landing markers — already centered on the
///     runway strip from prior passes.
///   • Visual_NewAirfield scale (0.4), root BoxCollider (28×2×28), root
///     scale (1,1,1), TeamColorMarker state, OldVisual_Backup.
///
/// Idempotent — safe to re-run.
///
/// Menu: Tools → RTS → Buildings → Apply Manual Airfield Slot Reference
/// </summary>
public static class ApplyManualAirfieldSlotReference
{
    private const string PrefabPath = "Assets/_Game/Prefabs/AirfieldPrefab.prefab";

    // Y rotation observed on the user's reference jet (right-side). Left-side
    // slots use the negation for a mirrored "fish-bone" pattern.
    public const float RightSideRotY = -55.959f;
    public const float LeftSideRotY  =  55.959f;

    // Slot Y for the new (jet2) aircraft pivot. Raised from 0.6 to 1.0 so
    // the new model sits cleanly on the apron surface (the new pivot is
    // lower in the body, so the previous 0.6 sank the wheels). All six
    // <see cref="References"/> entries below use this constant.
    public const float SlotHeight = 1f;

    public struct SlotRef
    {
        public int     Index;
        public Vector3 LocalPos;
        public float   RotationY;
    }

    /// <summary>
    /// PUBLIC so <see cref="NormalizeAirfieldScale"/> and
    /// <see cref="UseNewAirfieldVisual"/> can read the same table and stay in
    /// sync after a re-run of either of them.
    /// </summary>
    public static readonly SlotRef[] References =
    {
        // RIGHT side (positive X) — three pads, nose toward NW (inward + north).
        new SlotRef { Index = 0, LocalPos = new Vector3( 8.23999977f, SlotHeight,  3.65932417f), RotationY = RightSideRotY },
        new SlotRef { Index = 1, LocalPos = new Vector3( 6.38754559f, SlotHeight,  0.20483637f), RotationY = RightSideRotY },
        new SlotRef { Index = 2, LocalPos = new Vector3( 4.69712925f, SlotHeight, -4.08381081f), RotationY = RightSideRotY },
        // LEFT side (negative X) — three pads, nose mirrored toward NE (inward + north).
        new SlotRef { Index = 3, LocalPos = new Vector3(-5.27180529f, SlotHeight, -4.35003638f), RotationY = LeftSideRotY  },
        new SlotRef { Index = 4, LocalPos = new Vector3(-6.12523460f, SlotHeight,  0.26885521f), RotationY = LeftSideRotY  },
        new SlotRef { Index = 5, LocalPos = new Vector3(-8.05147648f, SlotHeight,  3.80998421f), RotationY = LeftSideRotY  },
    };

    [MenuItem("Tools/RTS/Buildings/Apply Manual Airfield Slot Reference")]
    public static void Apply()
    {
        Debug.Log("[ApplyManualAirfieldSlots] ─── Applying user's reference jet positions to AirfieldPrefab ───");

        GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
        if (asset == null)
        {
            Debug.LogError($"[ApplyManualAirfieldSlots] ✗ Prefab not found at '{PrefabPath}'.");
            return;
        }

        GameObject root = PrefabUtility.LoadPrefabContents(PrefabPath);
        if (root == null)
        {
            Debug.LogError("[ApplyManualAirfieldSlots] ✗ LoadPrefabContents returned null.");
            return;
        }

        try
        {
            int slotsDone = 0, taxiDone = 0, missing = 0;

            for (int i = 0; i < References.Length; i++)
            {
                SlotRef R = References[i];

                Transform slot = root.transform.Find($"Slot_{R.Index}");
                Transform taxi = root.transform.Find($"Taxi_{R.Index}");
                if (slot == null)
                {
                    Debug.LogError($"[ApplyManualAirfieldSlots] ✗ Slot_{R.Index} missing on prefab — skipping.");
                    missing++;
                    continue;
                }

                Vector3 slotBefore = slot.localPosition;
                slot.localPosition    = R.LocalPos;
                slot.localEulerAngles = new Vector3(0f, R.RotationY, 0f);
                slotsDone++;
                Debug.Log($"[AirfieldSlots] Slot {R.Index}: localPos {slotBefore} → {R.LocalPos}, " +
                          $"rotY = {R.RotationY:F3}°.  Side = {(R.LocalPos.x > 0f ? "RIGHT" : "LEFT")}.");

                if (taxi != null)
                {
                    Vector3 taxiBefore = taxi.localPosition;
                    // Place the per-slot Taxi south of its Slot on the SAME
                    // apron X — that way the jet pivots in place, then taxis
                    // straight south down its own side of the airport. The
                    // previous "midway to centerline" form had the path graze
                    // the building's east/west face for the northern slots.
                    // Building has Z extent ≈ ±3.5 m; Z = -7 leaves a 3 m
                    // safety margin south of it.
                    Vector3 taxiPos = new Vector3(R.LocalPos.x, 0f, -7f);
                    taxi.localPosition = taxiPos;
                    // Match the slot's rotation so the jet's initial pull-out
                    // heading reads consistently in gizmos.
                    taxi.localEulerAngles = new Vector3(0f, R.RotationY, 0f);
                    taxiDone++;
                    Debug.Log($"[AirfieldSlots]   Taxi {R.Index}: localPos {taxiBefore} → {taxiPos} " +
                              $"(south of slot on the same apron X={R.LocalPos.x:F2}, clears the building).");
                }
                else
                {
                    Debug.LogWarning($"[ApplyManualAirfieldSlots] ⚠ Taxi_{R.Index} missing — slot moved but taxi not updated.");
                }
            }

            PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[ApplyManualAirfieldSlots] ✓ Applied {slotsDone}/6 slot + {taxiDone}/6 taxi positions. " +
                      $"{missing} missing. RIGHT slots (0,1,2) rotY = {RightSideRotY:F3}°; " +
                      $"LEFT slots (3,4,5) rotY = {LeftSideRotY:F3}°. " +
                      "Runway / landing markers left unchanged (already centered). " +
                      "Reminder: remove the scratch StrikeJet reference objects from your scene manually — they're not gameplay units.");
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }

        Debug.Log("[ApplyManualAirfieldSlots] ─────────────────────────────────────────");
    }
}
