using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Floating short labels (airport code, e.g. "FRA") above the biggest nodes,
/// always facing the viewer, so the main hubs can be recognised without
/// selecting them. Top-routes view: the `count` biggest airports, white.
/// Regional view (SetRegional): the `perCommunity` biggest airports of each
/// community, in its colour. Labels have no colliders, so they never block XR
/// rays. Not parented to the graph: size is in metres.
/// </summary>
public class HubLabels : MonoBehaviour
{
    public GraphLoader graph;
    [Tooltip("Camera the labels face. Defaults to Camera.main (the XR head camera).")]
    public Transform viewer;
    [Tooltip("How many of the biggest nodes (by value) get a label.")]
    public int count = 20;
    [Tooltip("TextMeshPro 3D font size; 10 = 1 m line height.")]
    public float fontSize = 1f;
    [Tooltip("Gap between the top of the node and the label (m).")]
    public float gap = 0.03f;
    public Color color = new Color(1f, 1f, 1f, 0.85f);
    [Tooltip("Regional view: labelled airports per community.")]
    public int perCommunity = 3;
    [Tooltip("Label size while another layout is shown (GeoMapView's map: closer, airports packed tighter).")]
    public float layoutLabelScale = 0.35f;

    private readonly List<Transform> labels = new List<Transform>();
    private readonly List<Transform> anchors = new List<Transform>();
    private bool built;
    private bool regional;
    private Transform root;

    private void Awake()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
    }

    private void LateUpdate()
    {
        if (!built)
        {
            if (graph == null || !graph.IsLoaded) return;
            Build();
        }
        if (viewer == null && Camera.main != null) viewer = Camera.main.transform;
        if (viewer == null) return;

        Vector3 scale = Vector3.one * (graph.Layout != null ? layoutLabelScale : 1f);
        for (int i = 0; i < labels.Count; i++)
        {
            Transform node = anchors[i];
            Transform label = labels[i];
            // Airports hidden inside a collapsed city group (CityClusters) have no label.
            bool shown = node.gameObject.activeInHierarchy;
            if (label.gameObject.activeSelf != shown) label.gameObject.SetActive(shown);
            if (!shown) continue;
            label.localScale = scale;
            label.position = node.position + Vector3.up * (node.lossyScale.y * 0.5f + gap * scale.x);
            // TMP text reads correctly when its +Z points away from the viewer.
            Vector3 away = label.position - viewer.position;
            if (away.sqrMagnitude > 1e-6f) label.rotation = Quaternion.LookRotation(away, Vector3.up);
        }
    }

    /// <summary>Relabels for the regional (per community) or top-routes (biggest overall) view.</summary>
    public void SetRegional(bool value)
    {
        regional = value;
        if (built) Build();
    }

    private void OnDestroy()
    {
        if (root != null) Destroy(root.gameObject);
    }

    private void Build()
    {
        if (root != null) Destroy(root.gameObject);
        labels.Clear();
        anchors.Clear();

        var nodes = new List<GraphNode>();
        if (regional)
        {
            foreach (int community in graph.Communities)
            {
                IReadOnlyList<GraphNode> members = graph.CommunityMembers(community);
                for (int i = 0; i < members.Count && i < perCommunity; i++) nodes.Add(members[i]);
            }
        }
        else
        {
            nodes.AddRange(graph.Nodes.Values);
            nodes.Sort((a, b) => a.value != b.value ? b.value.CompareTo(a.value) : string.CompareOrdinal(a.id, b.id));
            if (nodes.Count > count) nodes.RemoveRange(count, nodes.Count - count);
        }

        root = new GameObject("HubLabels").transform;
        int n = nodes.Count;
        for (int i = 0; i < n; i++)
        {
            GameObject go = new GameObject($"Label {nodes[i].id}");
            go.transform.SetParent(root, false);
            TextMeshPro text = go.AddComponent<TextMeshPro>();
            text.text = nodes[i].ShortCode;
            text.fontSize = fontSize;
            Color labelColor = color;
            if (regional)
            {
                labelColor = graph.CommunityColor(nodes[i].community);
                labelColor.a = color.a;
            }
            text.color = labelColor;
            text.alignment = TextAlignmentOptions.Bottom;
            text.textWrappingMode = TextWrappingModes.NoWrap;
            text.overflowMode = TextOverflowModes.Overflow;
            // Pivot at bottom centre: the label sits on top of the node.
            text.rectTransform.pivot = new Vector2(0.5f, 0f);
            text.rectTransform.sizeDelta = new Vector2(1f, 0f);

            labels.Add(go.transform);
            anchors.Add(nodes[i].transform);
        }
        built = true;
    }
}
