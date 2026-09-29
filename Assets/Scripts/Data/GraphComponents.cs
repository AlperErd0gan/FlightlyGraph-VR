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
}

/// <summary>Runtime metadata attached to each edge GameObject.</summary>
public class GraphEdge : MonoBehaviour
{
    public string sourceId;
    public string targetId;
    public int weight;
    public LineRenderer line;
    public Color baseColor;
}
