using System.Text.RegularExpressions;
using UnityEngine;

/// <summary>Runtime metadata attached to each node GameObject.</summary>
public class GraphNode : MonoBehaviour
{
    public string id;
    public string label;
    public int value;
    public float lat;
    public float lon;
    public int community;
    /// <summary>Colour before highlight / dim / intensity (set by GraphLoader).</summary>
    public Color baseColor;
    /// <summary>Everything read from the JSON, including dataset-specific fields.</summary>
    [System.NonSerialized] public NodeData data;

    // Trailing "(FRA)" / "(EDDF)" in labels built by build_graph*.py.
    private static readonly Regex CodePattern = new Regex(@"\(([A-Z0-9]{3,4})\)\s*$");

    /// <summary>Short airport code from the label (e.g. "FRA"), else the id.</summary>
    public string ShortCode
    {
        get
        {
            Match m = CodePattern.Match(label ?? string.Empty);
            return m.Success ? m.Groups[1].Value : id;
        }
    }
}

/// <summary>
/// Runtime data of one edge. A plain class, not a component: all edges are drawn
/// by a single mesh owned by GraphLoader. Change how an edge looks through
/// GraphLoader.SetEdgeDisplay / RevealEdges / ResetEdgeVisibility, not these fields.
/// </summary>
public class GraphEdge
{
    public string sourceId;
    public string targetId;
    public int weight;
    /// <summary>Colour at rest (before highlight / dim).</summary>
    public Color baseColor;
    /// <summary>Everything read from the JSON, including dataset-specific fields.</summary>
    public EdgeData data;
    /// <summary>World-space centre line of the arc; used for picking and anchors.</summary>
    public Vector3[] points;
    /// <summary>Position of this edge's vertices in GraphLoader's edge mesh.</summary>
    public int index;

    // Display state, written by GraphLoader.
    public bool visible = true;
    public Color displayColor;
    public float displayWidth;

    public Vector3 Midpoint => points[points.Length / 2];
}
