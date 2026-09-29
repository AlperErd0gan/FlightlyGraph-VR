using System;

// Plain data classes matching data/processed/nodes.json and edges.json exactly.
// Field names must stay identical to the JSON keys: Newtonsoft maps them verbatim.

[Serializable]
public class NodeData
{
    public string id;
    public string label;
    public float x;
    public float y;
    public float z;
    public int value;
    public float lat;
    public float lon;
    // Only in nodes_3d*.json (layout_3d.py); 0 when absent.
    public int community;
}

[Serializable]
public class EdgeData
{
    public string source;
    public string target;
    public int weight;
}
