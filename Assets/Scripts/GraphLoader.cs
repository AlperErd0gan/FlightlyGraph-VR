using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Loads nodes.json / edges.json from StreamingAssets and renders the graph:
/// one GameObject per node, one LineRenderer per edge.
/// Both files are always read through UnityWebRequest so the same code path
/// works in the Editor, on desktop and on Android/Quest (where StreamingAssets
/// lives inside the APK and plain File I/O fails).
/// </summary>
public class GraphLoader : MonoBehaviour
{
    [Header("Files (relative to Application.streamingAssetsPath)")]
    public string nodesFileName = "nodes.json";
    public string edgesFileName = "edges.json";

    [Header("Nodes")]
    [Tooltip("Optional. If unset, a sphere primitive is created per node.")]
    public GameObject nodePrefab;
    [Tooltip("Multiplier applied to the (x, y, z) read from nodes.json.")]
    public float positionScale = 1f;
    public float nodeBaseSize = 0.15f;
    [Tooltip("Extra radius per unit of node value (route degree).")]
    public float nodeSizePerValue = 0.0005f;

    [Header("Edges")]
    public Material edgeMaterial;
    public float edgeBaseWidth = 0.01f;
    [Tooltip("Extra line width per unit of edge weight.")]
    public float edgeWidthPerWeight = 0.01f;
    public Color edgeLowColor = new Color(0.3f, 0.5f, 1f, 0.35f);
    public Color edgeHighColor = new Color(1f, 0.4f, 0.2f, 0.9f);

    // Runtime state
    private readonly Dictionary<string, Transform> nodeInstances = new Dictionary<string, Transform>();
    private readonly List<EdgeInstance> edgeInstances = new List<EdgeInstance>();
    private Transform nodesRoot;
    private Transform edgesRoot;

    private class EdgeInstance
    {
        public GameObject gameObject;
        public int weight;
    }

    public IReadOnlyDictionary<string, Transform> NodeInstances => nodeInstances;
    public int LoadedNodeCount => nodeInstances.Count;
    public int LoadedEdgeCount => edgeInstances.Count;

    private void Start()
    {
        StartCoroutine(LoadGraph());
    }

    private IEnumerator LoadGraph()
    {
        string nodesJson = null;
        string edgesJson = null;

        yield return ReadStreamingAsset(nodesFileName, text => nodesJson = text);
        if (nodesJson == null)
        {
            yield break; // error already logged
        }

        yield return ReadStreamingAsset(edgesFileName, text => edgesJson = text);
        if (edgesJson == null)
        {
            yield break;
        }

        // No yield inside try/catch: C# does not allow yield statements in catch blocks.
        List<NodeData> nodes = null;
        List<EdgeData> edges = null;
        string parseError = null;
        try
        {
            nodes = JsonConvert.DeserializeObject<List<NodeData>>(nodesJson);
            edges = JsonConvert.DeserializeObject<List<EdgeData>>(edgesJson);
        }
        catch (JsonException ex)
        {
            parseError = ex.Message;
        }

        if (parseError != null)
        {
            Debug.LogError($"GraphLoader: JSON parse failed: {parseError}");
            yield break;
        }
        if (nodes == null || edges == null)
        {
            Debug.LogError("GraphLoader: deserialized null (empty or malformed file).");
            yield break;
        }

        BuildNodes(nodes);
        BuildEdges(edges);

        Debug.Log($"GraphLoader: loaded {nodeInstances.Count} nodes, {edgeInstances.Count} edges " +
                  $"({nodes.Count} nodes / {edges.Count} edges in files).");
    }

    /// <summary>
    /// Reads a file under StreamingAssets via UnityWebRequest. On success invokes
    /// onText with the file contents; on failure logs an error and leaves it uncalled.
    /// </summary>
    private IEnumerator ReadStreamingAsset(string fileName, System.Action<string> onText)
    {
        string path = Path.Combine(Application.streamingAssetsPath, fileName);
        // On desktop/Editor streamingAssetsPath is a bare filesystem path; UnityWebRequest
        // needs a URI. On Android it is already a jar:file:// URI and must be left alone.
        string uri = path.Contains("://") ? path : "file://" + path;

        using (UnityWebRequest request = UnityWebRequest.Get(uri))
        {
            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"GraphLoader: failed to read '{uri}': {request.error}");
                yield break;
            }

            onText(request.downloadHandler.text);
        }
    }

    private void BuildNodes(List<NodeData> nodes)
    {
        nodesRoot = new GameObject("Nodes").transform;
        nodesRoot.SetParent(transform, false);

        foreach (NodeData node in nodes)
        {
            if (string.IsNullOrEmpty(node.id))
            {
                Debug.LogWarning("GraphLoader: node with empty id skipped.");
                continue;
            }
            if (nodeInstances.ContainsKey(node.id))
            {
                Debug.LogWarning($"GraphLoader: duplicate node id '{node.id}' skipped.");
                continue;
            }

            GameObject go;
            if (nodePrefab != null)
            {
                go = Instantiate(nodePrefab, nodesRoot);
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.transform.SetParent(nodesRoot, false);
            }

            go.name = string.IsNullOrEmpty(node.label) ? node.id : node.label;
            go.transform.localPosition = new Vector3(node.x, node.y, node.z) * positionScale;
            float size = nodeBaseSize + node.value * nodeSizePerValue;
            go.transform.localScale = Vector3.one * size;

            nodeInstances.Add(node.id, go.transform);
        }
    }

    private void BuildEdges(List<EdgeData> edges)
    {
        edgesRoot = new GameObject("Edges").transform;
        edgesRoot.SetParent(transform, false);

        int maxWeight = 1;
        foreach (EdgeData edge in edges)
        {
            if (edge.weight > maxWeight)
            {
                maxWeight = edge.weight;
            }
        }

        foreach (EdgeData edge in edges)
        {
            Transform a;
            Transform b;
            if (!nodeInstances.TryGetValue(edge.source, out a) ||
                !nodeInstances.TryGetValue(edge.target, out b))
            {
                Debug.LogWarning($"GraphLoader: edge {edge.source} -> {edge.target} references unknown node; skipped.");
                continue;
            }

            GameObject go = new GameObject($"Edge {edge.source}-{edge.target}");
            go.transform.SetParent(edgesRoot, false);

            LineRenderer line = go.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.SetPosition(0, a.position);
            line.SetPosition(1, b.position);

            float width = edgeBaseWidth + edge.weight * edgeWidthPerWeight;
            line.startWidth = width;
            line.endWidth = width;

            float t = maxWeight > 1 ? (edge.weight - 1f) / (maxWeight - 1f) : 0f;
            Color color = Color.Lerp(edgeLowColor, edgeHighColor, t);
            line.startColor = color;
            line.endColor = color;

            if (edgeMaterial != null)
            {
                line.material = edgeMaterial;
            }

            edgeInstances.Add(new EdgeInstance { gameObject = go, weight = edge.weight });
        }
    }

    /// <summary>
    /// Shows only edges whose weight is >= threshold. Not wired to any UI yet.
    /// </summary>
    public void SetEdgeWeightThreshold(float threshold)
    {
        foreach (EdgeInstance edge in edgeInstances)
        {
            edge.gameObject.SetActive(edge.weight >= threshold);
        }
    }
}
