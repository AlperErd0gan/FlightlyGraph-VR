using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Left-click selects a node (physics raycast against its sphere collider) or an
/// edge (closest arc segment in screen space, no colliders needed). Selecting a
/// node highlights it and its incident edges and dims everything else; selecting
/// an edge highlights it and both endpoints. Esc or clicking empty space clears.
/// </summary>
public class GraphSelector : MonoBehaviour
{
    public GraphLoader graph;
    public Camera targetCamera;

    [Header("Highlight")]
    public Color nodeHighlightColor = Color.yellow;
    public Color edgeHighlightColor = new Color(1f, 0.9f, 0.2f, 1f);
    [Range(0f, 1f)] public float dimmedEdgeAlpha = 0.03f;
    [Range(0f, 1f)] public float dimmedNodeBrightness = 0.35f;
    public float highlightedEdgeWidthMultiplier = 2.5f;

    [Header("Picking")]
    public float maxRayDistance = 500f;
    [Tooltip("Max distance in pixels from the cursor to an edge for it to count as clicked.")]
    public float edgePickPixels = 8f;

    private readonly Dictionary<GraphNode, Color> nodeBaseColors = new Dictionary<GraphNode, Color>();
    private GraphNode selectedNode;
    private GraphEdge selectedEdge;

    private void Awake()
    {
        if (targetCamera == null) targetCamera = Camera.main;
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
    }

    private void Update()
    {
        Keyboard kb = Keyboard.current;
        Mouse mouse = Mouse.current;
        if (mouse == null || targetCamera == null || graph == null || !graph.IsLoaded)
        {
            return;
        }

        if (kb != null && kb.escapeKey.wasPressedThisFrame)
        {
            ClearSelection();
            return;
        }

        if (!mouse.leftButton.wasPressedThisFrame)
        {
            return;
        }

        Vector2 screenPos = mouse.position.ReadValue();

        Ray ray = targetCamera.ScreenPointToRay(screenPos);
        if (Physics.Raycast(ray, out RaycastHit hit, maxRayDistance))
        {
            GraphNode node = hit.collider.GetComponent<GraphNode>();
            if (node != null)
            {
                SelectNode(node);
                return;
            }
        }

        GraphEdge edge = PickEdge(screenPos);
        if (edge != null)
        {
            SelectEdge(edge);
        }
        else
        {
            ClearSelection();
        }
    }

    private GraphEdge PickEdge(Vector2 screenPos)
    {
        GraphEdge best = null;
        float bestDist = edgePickPixels;

        foreach (GraphEdge edge in graph.Edges)
        {
            if (!edge.gameObject.activeSelf) continue;

            int n = edge.line.positionCount;
            Vector3[] arr = new Vector3[n];
            edge.line.GetPositions(arr);

            Vector3 prev = targetCamera.WorldToScreenPoint(arr[0]);
            for (int i = 1; i < n; i++)
            {
                Vector3 cur = targetCamera.WorldToScreenPoint(arr[i]);
                if (prev.z > 0f && cur.z > 0f)
                {
                    float d = DistancePointToSegment(screenPos, prev, cur);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = edge;
                    }
                }
                prev = cur;
            }
        }
        return best;
    }

    private static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        float t = len2 < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return Vector2.Distance(p, a + ab * t);
    }

    private void SelectNode(GraphNode node)
    {
        ResetVisuals();
        selectedNode = node;

        var incident = new HashSet<GraphEdge>(graph.EdgesOf(node.id));
        var neighbours = new HashSet<GraphNode> { node };
        foreach (GraphEdge e in incident)
        {
            neighbours.Add(graph.Nodes[e.sourceId]);
            neighbours.Add(graph.Nodes[e.targetId]);
        }

        foreach (GraphEdge e in graph.Edges)
        {
            SetEdgeVisual(e, incident.Contains(e));
        }
        foreach (GraphNode n in graph.Nodes.Values)
        {
            SetNodeVisual(n, n == node, neighbours.Contains(n));
        }

        Debug.Log($"Selected node '{node.label}' — {incident.Count} connections, degree {node.value}, " +
                  $"lat {node.lat:F3}, lon {node.lon:F3}");
    }

    private void SelectEdge(GraphEdge edge)
    {
        ResetVisuals();
        selectedEdge = edge;

        GraphNode a = graph.Nodes[edge.sourceId];
        GraphNode b = graph.Nodes[edge.targetId];

        foreach (GraphEdge e in graph.Edges)
        {
            SetEdgeVisual(e, e == edge);
        }
        foreach (GraphNode n in graph.Nodes.Values)
        {
            bool endpoint = n == a || n == b;
            SetNodeVisual(n, endpoint, endpoint);
        }

        Debug.Log($"Selected edge '{a.label}' <-> '{b.label}' — weight {edge.weight}");
    }

    public void ClearSelection()
    {
        ResetVisuals();
        selectedNode = null;
        selectedEdge = null;
    }

    private void SetEdgeVisual(GraphEdge e, bool highlighted)
    {
        Color c = highlighted ? edgeHighlightColor : e.baseColor;
        if (!highlighted) c.a = dimmedEdgeAlpha;
        e.line.startColor = c;
        e.line.endColor = c;
        float w = highlighted ? graph.edgeWidth * highlightedEdgeWidthMultiplier : graph.edgeWidth;
        e.line.startWidth = w;
        e.line.endWidth = w;
    }

    private void SetNodeVisual(GraphNode n, bool selected, bool related)
    {
        Renderer r = n.GetComponent<Renderer>();
        if (r == null) return;
        if (!nodeBaseColors.ContainsKey(n)) nodeBaseColors[n] = r.material.color;
        Color baseColor = nodeBaseColors[n];
        r.material.color = selected ? nodeHighlightColor
            : related ? baseColor
            : baseColor * dimmedNodeBrightness;
    }

    private void ResetVisuals()
    {
        foreach (GraphEdge e in graph.Edges)
        {
            e.line.startColor = e.baseColor;
            e.line.endColor = e.baseColor;
            e.line.startWidth = graph.edgeWidth;
            e.line.endWidth = graph.edgeWidth;
        }
        foreach (var kv in nodeBaseColors)
        {
            Renderer r = kv.Key.GetComponent<Renderer>();
            if (r != null) r.material.color = kv.Value;
        }
    }
}
