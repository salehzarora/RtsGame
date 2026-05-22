using UnityEngine;

/// <summary>
/// Procedurally builds low-poly environment dressing (trees, rocks) from Unity primitives.
/// All decoration objects have their colliders removed — zero impact on NavMesh or raycasts.
///
/// USAGE:
///   1. Create an empty GameObject named "Environment" in the Hierarchy.
///   2. Attach this script to it.
///   3. Tune the fields in the Inspector.
///   4. Right-click the component header → "Generate Environment".
///   5. To rebuild: right-click → "Clear Environment", then generate again.
///   6. Optionally assign your Ground plane's Renderer to tint it automatically.
///
/// NavMesh note:
///   Colliders are destroyed on every generated object, so decoration NEVER blocks
///   pathfinding. You do NOT need to re-bake after generating.
/// </summary>
public class EnvironmentDresser : MonoBehaviour
{
    // ------------------------------------------------------------------ //
    // Inspector
    // ------------------------------------------------------------------ //

    [Header("Ground Tint (optional)")]
    [Tooltip("Drag the Plane/Ground Renderer here to auto-set its color")]
    public Renderer groundRenderer;
    [ColorUsage(false)] public Color groundColor = new Color(0.30f, 0.45f, 0.22f);

    [Header("Trees")]
    public int treeCount = 18;
    [ColorUsage(false)] public Color trunkColor    = new Color(0.34f, 0.22f, 0.10f);
    [ColorUsage(false)] public Color foliageColor  = new Color(0.18f, 0.46f, 0.13f);
    [Range(0.5f, 2.0f)]  public float treeMinScale = 0.7f;
    [Range(0.5f, 2.0f)]  public float treeMaxScale = 1.5f;

    [Header("Rocks")]
    public int rockCount = 12;
    [ColorUsage(false)] public Color rockColor = new Color(0.50f, 0.50f, 0.48f);

    [Header("Placement")]
    [Tooltip("Half-size of the ground plane (default Unity Plane at scale 10 = 50 units)")]
    public float groundHalfSize = 48f;
    [Tooltip("Radius around the origin kept clear for gameplay")]
    public float clearRadius = 24f;
    [Tooltip("Random seed — change for a different layout")]
    public int seed = 42;

    // ------------------------------------------------------------------ //
    // Context menu actions
    // ------------------------------------------------------------------ //

    [ContextMenu("Generate Environment")]
    public void Generate()
    {
        ClearChildren();
        Random.InitState(seed);

        // Ground tint
        if (groundRenderer != null)
            groundRenderer.material.color = groundColor;

        for (int i = 0; i < treeCount; i++)
            SpawnTree(RandomEdgePosition());

        for (int i = 0; i < rockCount; i++)
            SpawnRock(RandomEdgePosition());
    }

    [ContextMenu("Clear Environment")]
    public void ClearChildren()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            SafeDestroy(transform.GetChild(i).gameObject);
    }

    // ------------------------------------------------------------------ //
    // Spawners
    // ------------------------------------------------------------------ //

    private void SpawnTree(Vector3 origin)
    {
        float scale = Random.Range(treeMinScale, treeMaxScale);
        float yaw   = Random.Range(0f, 360f);

        GameObject root = new GameObject("Tree");
        root.transform.SetParent(transform);
        root.transform.SetPositionAndRotation(origin, Quaternion.Euler(0f, yaw, 0f));

        // --- Trunk ---
        GameObject trunk = MakePrimitive(PrimitiveType.Cylinder, root.transform,
            new Vector3(0f, 0.75f * scale, 0f),
            new Vector3(0.22f * scale, 0.75f * scale, 0.22f * scale),
            trunkColor);
        _ = trunk; // suppress unused warning

        // --- Main foliage sphere ---
        MakePrimitive(PrimitiveType.Sphere, root.transform,
            new Vector3(0f, 2.1f * scale, 0f),
            Vector3.one * (1.35f * scale),
            foliageColor);

        // --- Secondary foliage (60 % chance) for silhouette variety ---
        if (Random.value > 0.4f)
        {
            float ox = Random.Range(-0.35f, 0.35f) * scale;
            float oz = Random.Range(-0.35f, 0.35f) * scale;
            MakePrimitive(PrimitiveType.Sphere, root.transform,
                new Vector3(ox, 2.9f * scale, oz),
                Vector3.one * (0.85f * scale),
                foliageColor * 0.88f);
        }

        // --- Tiny top spike (cone-like: tall narrow sphere) ---
        if (Random.value > 0.55f)
        {
            MakePrimitive(PrimitiveType.Sphere, root.transform,
                new Vector3(0f, 3.6f * scale, 0f),
                new Vector3(0.4f * scale, 0.65f * scale, 0.4f * scale),
                foliageColor * 0.75f);
        }
    }

    private void SpawnRock(Vector3 origin)
    {
        float s = Random.Range(0.5f, 1.7f);
        Vector3 scale = new Vector3(
            s * Random.Range(0.8f, 1.25f),
            s * Random.Range(0.45f, 0.85f),
            s * Random.Range(0.8f, 1.25f));

        Quaternion rot = Quaternion.Euler(
            Random.Range(-12f, 12f),
            Random.Range(0f,   360f),
            Random.Range(-12f, 12f));

        // Sink rock slightly into the ground so it looks seated
        Vector3 pos = origin + Vector3.up * (scale.y * 0.4f);

        GameObject rock = MakePrimitive(PrimitiveType.Cube, transform,
            pos, scale, rockColor * Random.Range(0.82f, 1.08f));
        rock.name = "Rock";
        rock.transform.rotation = rot;
    }

    // ------------------------------------------------------------------ //
    // Helpers
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Random world position on the ground that is outside <see cref="clearRadius"/>
    /// but inside <see cref="groundHalfSize"/>.
    /// </summary>
    private Vector3 RandomEdgePosition()
    {
        Vector3 pos = Vector3.zero;
        int guard = 0;
        do
        {
            pos = new Vector3(
                Random.Range(-groundHalfSize, groundHalfSize),
                0f,
                Random.Range(-groundHalfSize, groundHalfSize));
            guard++;
        }
        while (new Vector2(pos.x, pos.z).magnitude < clearRadius && guard < 200);

        return pos;
    }

    /// <summary>Create a primitive, remove its collider, apply color, parent it.</summary>
    private static GameObject MakePrimitive(
        PrimitiveType type,
        Transform parent,
        Vector3 localPos,
        Vector3 localScale,
        Color color)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        go.transform.SetParent(parent);
        go.transform.localPosition = localPos;
        go.transform.localScale    = localScale;
        go.GetComponent<Renderer>().material.color = color;
        SafeDestroy(go.GetComponent<Collider>());
        return go;
    }

    private static void SafeDestroy(Object obj)
    {
        if (obj == null) return;
#if UNITY_EDITOR
        if (!Application.isPlaying)
            DestroyImmediate(obj);
        else
#endif
            Destroy(obj);
    }
}
