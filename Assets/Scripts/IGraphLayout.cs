using UnityEngine;

/// <summary>
/// A layout other than the data's own (x, y, z of the nodes file): where airports rest
/// and how routes are drawn, e.g. GeoMapView's map. Set with GraphLoader.SetLayout.
/// </summary>
public interface IGraphLayout
{
    /// <summary>Resting position of an airport, in the GraphLoader's local space.</summary>
    Vector3 Home(GraphNode node);

    /// <summary>Fills a route's centre line from a to b (world space, points.Length points).</summary>
    void FillEdge(Vector3 a, Vector3 b, Vector3[] points);

    /// <summary>Airport size multiplier in this layout.</summary>
    float NodeScale { get; }

    /// <summary>Route width multiplier in this layout.</summary>
    float EdgeWidthScale { get; }
}
