using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Left-hand "Selected Object" box on the bottom HUD bar. Polls
/// <see cref="UnitSelector"/> every <c>LateUpdate</c> and switches between
/// three sub-views:
///
///   • <b>Single</b>   — one unit / aircraft / building selected.
///                       Shows portrait + display name + HP bar + category.
///   • <b>Group</b>    — two or more objects selected.
///                       Shows "X SELECTED" header + a short type breakdown.
///   • <b>Empty</b>    — nothing selected. Shows the neutral "No Selection"
///                       placeholder.
///
/// This component owns NONE of the selection state; it's a read-only viewer
/// driven by UnitSelector. No editing of selection happens here.
///
/// Setup (handled automatically by SetupGameplayHUD):
///   1. Place an empty RectTransform on the HUD canvas as the panel root.
///   2. Build the three sub-views as children of the panel root and drag
///      them into the Inspector fields below.
///   3. The Portrait Frame is a coloured Image placeholder — real sprites
///      can be wired later via the optional <c>PortraitRegistry</c>
///      mechanism (out of scope here; the placeholder colours are picked
///      from the resolved <see cref="UnitCategory.Category"/>).
/// </summary>
[DisallowMultipleComponent]
public class SelectedInfoPanelUI : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector — sub-view roots (wired by SetupGameplayHUD)
    // ------------------------------------------------------------------ //

    [Header("Sub-view roots")]
    [Tooltip("Container shown when exactly one unit/aircraft/building is selected.")]
    public GameObject singleRoot;

    [Tooltip("Container shown when two or more objects are selected.")]
    public GameObject groupRoot;

    [Tooltip("Container shown when nothing is selected (neutral placeholder).")]
    public GameObject emptyRoot;

    // ------------------------------------------------------------------ //
    // Inspector — single-selection widgets
    // ------------------------------------------------------------------ //

    [Header("Single-selection widgets")]
    [Tooltip("Coloured square placeholder. Tinted by category until real sprites are wired.")]
    public Image portraitFrame;

    [Tooltip("Optional inner icon overlay inside the portrait frame. Hidden if null.")]
    public Image portraitIcon;

    [Tooltip("Display name of the selected object.")]
    public TextMeshProUGUI nameLabel;

    [Tooltip("Category line — e.g. 'Infantry', 'Vehicle', 'Building'.")]
    public TextMeshProUGUI categoryLabel;

    [Tooltip("HP value label — '75 / 100'. Hidden if the selection has no Health component.")]
    public TextMeshProUGUI hpValueLabel;

    [Tooltip("Filled Image (FillMethod = Horizontal) that visualises HP. fillAmount = current/max.")]
    public Image hpBarFill;

    [Tooltip("Optional secondary info line — passenger count for APC, capacity for buildings, etc.")]
    public TextMeshProUGUI extraInfoLabel;

    // ------------------------------------------------------------------ //
    // Inspector — group-selection widgets
    // ------------------------------------------------------------------ //

    [Header("Group-selection widgets")]
    [Tooltip("'X UNITS SELECTED' header.")]
    public TextMeshProUGUI groupHeaderLabel;

    [Tooltip("Multi-line summary listing the most common types in the selection.")]
    public TextMeshProUGUI groupSummaryLabel;

    // ------------------------------------------------------------------ //
    // Inspector — palette for the portrait placeholder
    // ------------------------------------------------------------------ //

    [Header("Portrait placeholder colours (until real icons exist)")]
    public Color portraitInfantry = new Color(0.30f, 0.65f, 0.30f, 1f);
    public Color portraitVehicle  = new Color(0.36f, 0.44f, 0.24f, 1f);
    public Color portraitAircraft = new Color(0.45f, 0.55f, 0.65f, 1f);
    public Color portraitBuilding = new Color(0.25f, 0.40f, 0.60f, 1f);
    public Color portraitUnknown  = new Color(0.40f, 0.40f, 0.40f, 1f);

    [Header("HP bar colours")]
    public Color hpHealthy  = new Color(0.40f, 0.85f, 0.40f, 1f);
    public Color hpInjured  = new Color(0.90f, 0.78f, 0.20f, 1f);
    public Color hpCritical = new Color(0.85f, 0.25f, 0.20f, 1f);

    // ------------------------------------------------------------------ //
    // Runtime
    // ------------------------------------------------------------------ //

    // Cached primary selection so we don't redo string allocations every frame.
    private GameObject    primaryGO;
    private Health        primaryHealth;
    private APCTransport  primaryAPC;

    // ------------------------------------------------------------------ //

    private void LateUpdate()
    {
        UnitSelector sel = UnitSelector.Instance;
        if (sel == null)
        {
            ShowEmpty();
            return;
        }

        int total = ResolveTotalSelected(sel, out SelectableBuilding bld,
                                              out SelectableUnit    unit,
                                              out SelectableAircraft aircraft);

        if (total == 0)
        {
            ShowEmpty();
            return;
        }

        if (total >= 2)
        {
            ShowGroup(sel, bld);
            return;
        }

        // total == 1 — exactly one of bld / unit / aircraft is non-null.
        if (bld != null)        ShowSingle(bld.gameObject, bld.name);
        else if (unit != null)  ShowSingle(unit.gameObject, unit.name);
        else                    ShowSingle(aircraft.gameObject, aircraft.name);

        // Per-frame HP refresh while a single selection is shown.
        RefreshHealth();
        RefreshExtraInfo();
    }

    private static int ResolveTotalSelected(
        UnitSelector sel,
        out SelectableBuilding bld,
        out SelectableUnit     unit,
        out SelectableAircraft aircraft)
    {
        bld      = sel.SelectedBuilding;
        unit     = null;
        aircraft = null;

        var units = sel.SelectedUnits;
        var jets  = sel.SelectedAircraftList;

        int count = (bld != null ? 1 : 0)
                  + (units != null ? units.Count : 0)
                  + (jets  != null ? jets.Count  : 0);

        // Cache the "first" of each so the single-view path doesn't iterate twice.
        if (units != null && units.Count > 0) unit = units[0];
        if (jets  != null && jets.Count  > 0) aircraft = jets[0];
        return count;
    }

    // ------------------------------------------------------------------ //
    // View — single
    // ------------------------------------------------------------------ //

    private void ShowSingle(GameObject go, string displayName)
    {
        SetViewActive(singleRoot, true);
        SetViewActive(groupRoot, false);
        SetViewActive(emptyRoot, false);

        // Re-resolve the cached refs only when the selection actually changes.
        // Saves a few GetComponent calls per frame for static selections.
        if (go != primaryGO)
        {
            primaryGO     = go;
            primaryHealth = go.GetComponentInChildren<Health>(true);
            primaryAPC    = go.GetComponent<APCTransport>();

            if (nameLabel != null)
                nameLabel.text = PrettifyName(displayName);

            if (categoryLabel != null)
                categoryLabel.text = DescribeCategory(go);

            if (portraitFrame != null)
                portraitFrame.color = PortraitColorFor(go);
        }
    }

    private void RefreshHealth()
    {
        bool hasHealth = primaryHealth != null && primaryHealth.maxHealth > 0f;

        if (hpValueLabel != null)
            hpValueLabel.gameObject.SetActive(hasHealth);
        if (hpBarFill != null && hpBarFill.transform.parent != null)
            hpBarFill.transform.parent.gameObject.SetActive(hasHealth);

        if (!hasHealth) return;

        float cur = primaryHealth.CurrentHealth;
        float max = primaryHealth.maxHealth;
        float pct = Mathf.Clamp01(cur / max);

        if (hpValueLabel != null)
            hpValueLabel.text = $"{Mathf.CeilToInt(cur)} / {Mathf.CeilToInt(max)}";

        if (hpBarFill != null)
        {
            hpBarFill.fillAmount = pct;
            hpBarFill.color      = pct > 0.66f ? hpHealthy
                                  : pct > 0.33f ? hpInjured
                                  : hpCritical;
        }
    }

    private void RefreshExtraInfo()
    {
        if (extraInfoLabel == null) return;

        if (primaryAPC != null)
        {
            extraInfoLabel.gameObject.SetActive(true);
            extraInfoLabel.text = $"Passengers: {primaryAPC.PassengerCount} / {primaryAPC.capacity}";
            return;
        }

        extraInfoLabel.gameObject.SetActive(false);
    }

    // ------------------------------------------------------------------ //
    // View — group
    // ------------------------------------------------------------------ //

    private void ShowGroup(UnitSelector sel, SelectableBuilding bld)
    {
        SetViewActive(singleRoot, false);
        SetViewActive(groupRoot, true);
        SetViewActive(emptyRoot, false);

        primaryGO     = null;     // invalidate single-view cache
        primaryHealth = null;
        primaryAPC    = null;

        // Tally by category. Cheap — small selection counts in this game.
        int infantry = 0, vehicle = 0, aircraft = 0, building = bld != null ? 1 : 0;

        var units = sel.SelectedUnits;
        if (units != null)
        {
            for (int i = 0; i < units.Count; i++)
            {
                if (units[i] == null) continue;
                switch (DamageRules.Resolve(units[i].gameObject))
                {
                    case UnitCategory.Category.Vehicle:  vehicle++;  break;
                    case UnitCategory.Category.Aircraft: aircraft++; break;
                    default:                             infantry++; break;
                }
            }
        }

        var jets = sel.SelectedAircraftList;
        if (jets != null) aircraft += jets.Count;

        int total = infantry + vehicle + aircraft + building;

        if (groupHeaderLabel != null)
            groupHeaderLabel.text = $"{total} SELECTED";

        if (groupSummaryLabel != null)
        {
            var sb = new System.Text.StringBuilder(64);
            if (infantry > 0) sb.Append($"Infantry × {infantry}\n");
            if (vehicle  > 0) sb.Append($"Vehicle × {vehicle}\n");
            if (aircraft > 0) sb.Append($"Aircraft × {aircraft}\n");
            if (building > 0) sb.Append($"Building × {building}\n");

            // Trim trailing newline so the box doesn't bottom-pad.
            if (sb.Length > 0 && sb[sb.Length - 1] == '\n') sb.Length--;
            groupSummaryLabel.text = sb.ToString();
        }
    }

    // ------------------------------------------------------------------ //
    // View — empty
    // ------------------------------------------------------------------ //

    private void ShowEmpty()
    {
        SetViewActive(singleRoot, false);
        SetViewActive(groupRoot, false);
        SetViewActive(emptyRoot, true);

        primaryGO     = null;
        primaryHealth = null;
        primaryAPC    = null;
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    private static void SetViewActive(GameObject root, bool on)
    {
        if (root != null && root.activeSelf != on) root.SetActive(on);
    }

    /// <summary>
    /// Cleans common Unity object-name suffixes ("(Clone)", "Prefab") so the
    /// player sees "Soldier" instead of "SoldierPrefab(Clone)".
    /// </summary>
    private static string PrettifyName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "—";
        string s = raw;
        int paren = s.IndexOf('(');
        if (paren > 0) s = s.Substring(0, paren).Trim();
        if (s.EndsWith("Prefab")) s = s.Substring(0, s.Length - "Prefab".Length);
        return s;
    }

    private static string DescribeCategory(GameObject go)
    {
        var cat = DamageRules.Resolve(go);
        switch (cat)
        {
            case UnitCategory.Category.Infantry: return "Category: Infantry";
            case UnitCategory.Category.Vehicle:  return "Category: Vehicle";
            case UnitCategory.Category.Building: return "Category: Building";
            case UnitCategory.Category.Aircraft: return "Category: Aircraft";
        }
        return "Category: —";
    }

    private Color PortraitColorFor(GameObject go)
    {
        switch (DamageRules.Resolve(go))
        {
            case UnitCategory.Category.Infantry: return portraitInfantry;
            case UnitCategory.Category.Vehicle:  return portraitVehicle;
            case UnitCategory.Category.Aircraft: return portraitAircraft;
            case UnitCategory.Category.Building: return portraitBuilding;
        }
        return portraitUnknown;
    }
}
