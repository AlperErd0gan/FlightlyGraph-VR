using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Left-click selects a node (physics raycast against its sphere collider) or an
/// edge (closest arc segment in screen space, no colliders needed). Selecting a
/// node highlights it and its incident edges and dims everything else; selecting
/// an edge highlights it and both endpoints. Esc or clicking empty space clears.
/// XR input (XRGraphInput) calls SelectNode / SelectEdge / PickEdgeAlongRay directly.
/// </summary>
public class GraphSelector : MonoBehaviour
{
    public GraphLoader graph;
    public Camera targetCamera;
    [Tooltip("Desktop mouse/keyboard picking. XRGraphInput turns this off so the mouse-driven XR simulator does not also click here.")]
    public bool mouseInput = true;

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

    public event Action<GraphNode> NodeSelected;
    public event Action<GraphEdge> EdgeSelected;
    public event Action SelectionCleared;

    public GraphNode SelectedNode => selectedNode;
    public GraphEdge SelectedEdge => selectedEdge;

    private GraphNode selectedNode;
    private GraphEdge selectedEdge;
    private Vector3[] pickBuffer = new Vector3[0];

    private void Awake()
    {
        if (targetCamera == null) targetCamera = Camera.main;
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
    }

    private void Update()
    {
        Keyboard kb = Keyboard.current;
        Mouse mouse = Mouse.current;
        if (!mouseInput || mouse == null || targetCamera == null || graph == null || !graph.IsLoaded)
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

    /// <summary>
    /// 3D pick for XR rays: returns the visible edge whose arc passes closest to the
    /// ray, measured as the angle seen from the ray origin, or null if none is within
    /// maxAngleDegrees. Angle (not metres) keeps picking equally easy near and far.
    /// </summary>
    public GraphEdge PickEdgeAlongRay(Ray ray, float maxAngleDegrees)
    {
        GraphEdge best = null;
        float bestAngle = maxAngleDegrees;

        foreach (GraphEdge edge in graph.Edges)
        {
            if (!edge.gameObject.activeSelf) continue;

            int n = edge.line.positionCount;
            if (pickBuffer.Length < n) pickBuffer = new Vector3[n];
            edge.line.GetPositions(pickBuffer);

            for (int i = 1; i < n; i++)
            {
                float angle = AngleRayToSegment(ray, pickBuffer[i - 1], pickBuffer[i], maxRayDistance);
                if (angle < bestAngle)
                {
                    bestAngle = angle;
                    best = edge;
                }
            }
        }
        return best;
    }

    /// <summary>
    /// Angle in degrees, seen from the ray origin, between the ray and the closest
    /// point of segment ab. Returns +infinity if that point is behind the origin or
    /// farther than maxDistance.
    /// </summary>
    private static float AngleRayToSegment(Ray ray, Vector3 a, Vector3 b, float maxDistance)
    {
        Vector3 d1 = ray.direction; // normalised by Ray
        Vector3 d2 = b - a;
        Vector3 r = ray.origin - a;
        float b12 = Vector3.Dot(d1, d2);
        float c22 = Vector3.Dot(d2, d2);
        float d = Vector3.Dot(d1, r);
        float e = Vector3.Dot(d2, r);

        // Minimise |r + t*d1 - s*d2| over t >= 0, s in [0, 1].
        float denom = c22 - b12 * b12;
        float s = denom > 1e-8f ? Mathf.Clamp01((e - d * b12) / denom) : 0f;
        float t = s * b12 - d;
        if (t <= 1e-4f || t > maxDistance) return float.PositiveInfinity;

        Vector3 onRay = ray.origin + d1 * t;
        Vector3 onSegment = a + d2 * s;
        return Mathf.Atan2(Vector3.Distance(onRay, onSegment), t) * Mathf.Rad2Deg;
    }

    private static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len2 = ab.sqrMagnitude;
        float t = len2 < 1e-6f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return Vector2.Distance(p, a + ab * t);
    }

    public void SelectNode(GraphNode node)
    {
        ResetVisuals();
        selectedNode = node;

        var incident = new HashSet<GraphEdge>(graph.EdgesOf(node.id));
        // Show the node's edges even if the edge filter hides them at rest.
        graph.RevealEdges(incident);
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
        NodeSelected?.Invoke(node);
    }

    public void SelectEdge(GraphEdge edge)
    {
        ResetVisuals();
        selectedEdge = edge;

        GraphNode a = graph.Nodes[edge.sourceId];
        GraphNode b = graph.Nodes[edge.targetId];
        graph.RevealEdges(new[] { edge });

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
        EdgeSelected?.Invoke(edge);
    }

    public void ClearSelection()
    {
        ResetVisuals();
        graph.ResetEdgeVisibility();
        selectedNode = null;
        selectedEdge = null;
        SelectionCleared?.Invoke();
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
        graph.SetNodeColor(r, selected ? nodeHighlightColor
            : related ? n.baseColor
            : n.baseColor * dimmedNodeBrightness);
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
        foreach (GraphNode n in graph.Nodes.Values)
        {
            Renderer r = n.GetComponent<Renderer>();
            if (r != null) graph.SetNodeColor(r, n.baseColor);
        }
    }
}
