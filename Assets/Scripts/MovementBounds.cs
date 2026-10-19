using System.Collections.Generic;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;

/// <summary>
/// A room over the scene's floor: see-through grid walls stand on the floor's edges
/// (GraphLoader.floor, else the largest teleport area), and nothing leaves the room.
/// Viewer: teleporting, thumbstick moves or walking through a wall puts you back inside
/// (horizontally); a wall's grid shows only when you come near it (warnDistance). Graph: its
/// centre stays inside the room, between graphMinHeight above
/// the floor and the walls' top. The walls follow the floor when GraphRecenter moves it.
/// Self-contained: it adds itself when the scene has a GraphLoader and changes no other script.
/// </summary>
public class MovementBounds : MonoBehaviour
{
    public GraphLoader graph;
    [Tooltip("Floor whose edges are the walls. Empty = GraphLoader.floor, else the largest teleport area.")]
    public Collider floor;

    [Header("Walls")]
    public float wallHeight = 3f;
    [Tooltip("Grid spacing on the walls (m).")]
    public float gridSpacing = 1f;
    public Color gridColor = new Color(0.3f, 0.75f, 1f, 1f);
    [Tooltip("Grid opacity far from a wall (0 = hidden until you come near) and right at it.")]
    [Range(0f, 1f)] public float farAlpha = 0f;
    [Range(0f, 1f)] public float nearAlpha = 0.6f;
    [Tooltip("A wall's grid fades in from this far (m) from it.")]
    public float warnDistance = 1.5f;

    [Header("Limits")]
    [Tooltip("The viewer's head stays this far (m) inside the walls.")]
    public float viewerMargin = 0.3f;
    [Tooltip("The graph's centre stays this far (m) inside the walls.")]
    public float graphMargin = 0.5f;
    [Tooltip("Lowest height (m) of the graph's centre above the floor.")]
    public float graphMinHeight = 0.3f;

    private bool ready;
    private Vector2 min;
    private Vector2 max;
    private XROrigin origin;
    private Transform room;
    private readonly List<Material> wallMaterials = new List<Material>();
    private readonly List<Mesh> wallMeshes = new List<Mesh>();
    private readonly List<MeshRenderer> wallRenderers = new List<MeshRenderer>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void AddToScene()
    {
        if (FindFirstObjectByType<MovementBounds>() != null) return;
        GraphLoader loader = FindFirstObjectByType<GraphLoader>();
        if (loader != null) loader.gameObject.AddComponent<MovementBounds>();
    }

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
    }

    private void OnDestroy()
    {
        if (room != null) Destroy(room.gameObject);
        foreach (Material m in wallMaterials) if (m != null) Destroy(m);
        foreach (Mesh m in wallMeshes) if (m != null) Destroy(m);
    }

    private void LateUpdate()
    {
        if (graph == null || !graph.IsLoaded) return;
        if (!ready && !Setup()) return;
        float floorY = floor.bounds.max.y;
        room.position = new Vector3(0f, floorY, 0f); // GraphRecenter moves the floor up / down
        KeepGraphInside(floorY);
        KeepViewerInside();
    }

    private bool Setup()
    {
        ready = true;
        if (floor == null && graph.floor != null) floor = graph.floor.GetComponentInChildren<Collider>();
        if (floor == null) floor = LargestTeleportArea();
        if (floor == null)
        {
            Debug.LogWarning("MovementBounds: no floor (GraphLoader.floor or a teleport area) found; no room.");
            enabled = false;
            return false;
        }
        Bounds b = floor.bounds;
        min = new Vector2(b.min.x, b.min.z);
        max = new Vector2(b.max.x, b.max.z);
        origin = FindFirstObjectByType<XROrigin>();
        BuildWalls();
        Debug.Log($"MovementBounds: room over '{floor.name}', {max.x - min.x:F1} x {max.y - min.y:F1} m, walls {wallHeight:F1} m high.");
        return true;
    }

    private static Collider LargestTeleportArea()
    {
        Collider best = null;
        float bestArea = 0f;
        foreach (BaseTeleportationInteractable area in FindObjectsByType<BaseTeleportationInteractable>(FindObjectsSortMode.None))
        {
            Collider c = area.GetComponentInChildren<Collider>();
            if (c == null || !c.enabled) continue;
            float footprint = c.bounds.size.x * c.bounds.size.z;
            if (footprint > bestArea)
            {
                bestArea = footprint;
                best = c;
            }
        }
        return best;
    }

    private void KeepGraphInside(float floorY)
    {
        Transform t = graph.transform;
        Vector3 p = t.position;
        Vector3 inside = new Vector3(
            ClampInside(p.x, min.x, max.x, graphMargin),
            Mathf.Clamp(p.y, floorY + graphMinHeight, floorY + wallHeight),
            ClampInside(p.z, min.y, max.y, graphMargin));
        if ((inside - p).sqrMagnitude > 1e-8f) t.position = inside;
    }

    private void KeepViewerInside()
    {
        if (origin == null || origin.Camera == null) return;
        Vector3 head = origin.Camera.transform.position;
        float x = ClampInside(head.x, min.x, max.x, viewerMargin);
        float z = ClampInside(head.z, min.y, max.y, viewerMargin);
        // Move the whole rig back so the head is inside (works for teleports, thumbstick and walking).
        if (x != head.x || z != head.z) origin.transform.position += new Vector3(x - head.x, 0f, z - head.z);

        // Order as built: west, east, south, north.
        SetWallAlpha(0, x - min.x);
        SetWallAlpha(1, max.x - x);
        SetWallAlpha(2, z - min.y);
        SetWallAlpha(3, max.y - z);
    }

    private static float ClampInside(float v, float lo, float hi, float margin)
    {
        if (hi - lo <= 2f * margin) return (lo + hi) * 0.5f;
        return Mathf.Clamp(v, lo + margin, hi - margin);
    }

    private void SetWallAlpha(int wall, float distance)
    {
        float near = 1f - Mathf.Clamp01(distance / warnDistance);
        Color c = gridColor;
        c.a = Mathf.Lerp(farAlpha, nearAlpha, near);
        wallMaterials[wall].color = c;
        wallRenderers[wall].enabled = c.a > 0.005f;
    }

    // ---- Walls --------------------------------------------------------------

    private void BuildWalls()
    {
        room = new GameObject("MovementBounds Room").transform;
        AddWall("West", new Vector3(min.x, 0f, min.y), new Vector3(min.x, 0f, max.y));
        AddWall("East", new Vector3(max.x, 0f, min.y), new Vector3(max.x, 0f, max.y));
        AddWall("South", new Vector3(min.x, 0f, min.y), new Vector3(max.x, 0f, min.y));
        AddWall("North", new Vector3(min.x, 0f, max.y), new Vector3(max.x, 0f, max.y));
    }

    /// <summary>A grid of lines from a to b (on the floor) up to wallHeight, as one line mesh.</summary>
    private void AddWall(string name, Vector3 a, Vector3 b)
    {
        var vertices = new List<Vector3>();
        float length = Vector3.Distance(a, b);
        int columns = Mathf.Max(1, Mathf.RoundToInt(length / gridSpacing));
        for (int i = 0; i <= columns; i++)
        {
            Vector3 p = Vector3.Lerp(a, b, (float)i / columns);
            vertices.Add(p);
            vertices.Add(p + Vector3.up * wallHeight);
        }
        int rows = Mathf.Max(1, Mathf.RoundToInt(wallHeight / gridSpacing));
        for (int j = 0; j <= rows; j++)
        {
            Vector3 up = Vector3.up * (wallHeight * j / rows);
            vertices.Add(a + up);
            vertices.Add(b + up);
        }
        var indices = new int[vertices.Count];
        for (int i = 0; i < indices.Length; i++) indices[i] = i;

        var mesh = new Mesh { name = "MovementBounds " + name };
        mesh.SetVertices(vertices);
        mesh.SetIndices(indices, MeshTopology.Lines, 0);
        wallMeshes.Add(mesh);

        var go = new GameObject("Wall " + name);
        go.transform.SetParent(room, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer renderer = go.AddComponent<MeshRenderer>();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        Shader shader = Shader.Find("Sprites/Default");
        var material = new Material(shader) { name = "MovementBounds Wall (runtime)" };
        renderer.sharedMaterial = material;
        wallMaterials.Add(material);
        wallRenderers.Add(renderer);
    }
}
