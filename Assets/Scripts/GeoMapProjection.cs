using UnityEngine;

/// <summary>
/// Geometry of the map view (GeoMapView): build_geomap.py's azimuthal equidistant
/// projection (same formulas) and the gently curved surface the map is drawn on.
/// Planar coordinates are degrees of arc from the map centre (x east, y north): true
/// distance and direction from the centre. The surface is that disc bent onto a sphere
/// of radius curvatureRadius, in the GraphLoader's local space.
/// </summary>
public class GeoMapProjection
{
    public double centerLat;
    public double centerLon;
    /// <summary>Surface metres per degree of arc.</summary>
    public float metersPerDegree = 0.036f;
    /// <summary>Sphere the map is bent onto (m): > 0 bulges towards the viewer like a globe, &lt; 0 curves around the viewer, 0 = flat.</summary>
    public float curvatureRadius = 3.5f;

    /// <summary>Map centre on the surface; normal (towards the viewer), east and north there.</summary>
    public Vector3 center;
    public Vector3 normal = Vector3.back;
    public Vector3 east = Vector3.right;
    public Vector3 north = Vector3.up;

    private bool Flat => Mathf.Abs(curvatureRadius) < 1e-3f;

    /// <summary>Puts the map centre at `centre`, facing `normalTowardsViewer`, north as close to `up` as the tilt allows.</summary>
    public void Place(Vector3 centre, Vector3 normalTowardsViewer, Vector3 up)
    {
        center = centre;
        normal = normalTowardsViewer.normalized;
        north = Vector3.ProjectOnPlane(up, normal).normalized;
        east = Vector3.Cross(normal, north);
    }

    /// <summary>Azimuthal equidistant projection: (x east, y north) in degrees of arc from the centre.</summary>
    public Vector2 Project(double lat, double lon)
    {
        double p = lat * Mathf.Deg2Rad;
        double p0 = centerLat * Mathf.Deg2Rad;
        double dl = (lon - centerLon) * Mathf.Deg2Rad;
        double cosC = System.Math.Sin(p0) * System.Math.Sin(p) + System.Math.Cos(p0) * System.Math.Cos(p) * System.Math.Cos(dl);
        double c = System.Math.Acos(System.Math.Max(-1.0, System.Math.Min(1.0, cosC)));
        double k = c < 1e-9 ? 1.0 : c / System.Math.Sin(c);
        double x = k * System.Math.Cos(p) * System.Math.Sin(dl);
        double y = k * (System.Math.Cos(p0) * System.Math.Sin(p) - System.Math.Sin(p0) * System.Math.Cos(p) * System.Math.Cos(dl));
        return new Vector2((float)(x * Mathf.Rad2Deg), (float)(y * Mathf.Rad2Deg));
    }

    /// <summary>Point of the surface at planar `p` (degrees), raised `height` metres along its normal.</summary>
    public Vector3 Surface(Vector2 p, float height = 0f)
    {
        float d = p.magnitude * metersPerDegree;
        Vector3 dir = p.sqrMagnitude > 1e-12f ? (east * p.x + north * p.y) / p.magnitude : Vector3.zero;
        if (Flat) return center + dir * d + normal * height;
        Vector3 radial = RadialAt(dir, d);
        return center - normal * curvatureRadius + radial * (curvatureRadius + height);
    }

    /// <summary>Surface normal (towards the viewer) at planar `p`.</summary>
    public Vector3 NormalAt(Vector2 p)
    {
        if (Flat) return normal;
        float d = p.magnitude * metersPerDegree;
        Vector3 dir = p.sqrMagnitude > 1e-12f ? (east * p.x + north * p.y) / p.magnitude : Vector3.zero;
        return RadialAt(dir, d);
    }

    /// <summary>The surface's north (up the map) at planar `p`.</summary>
    public Vector3 NorthAt(Vector2 p)
    {
        return Vector3.ProjectOnPlane(north, NormalAt(p)).normalized;
    }

    /// <summary>Planar coordinates (degrees) of the surface point under `local` (inverse of Surface; the height is dropped).</summary>
    public Vector2 Unproject(Vector3 local)
    {
        if (Flat)
        {
            Vector3 v = local - center;
            return new Vector2(Vector3.Dot(v, east), Vector3.Dot(v, north)) / metersPerDegree;
        }
        // Dividing by the signed radius gives sin(phi) dir + cos(phi) normal for either sign.
        Vector3 w = ((local - (center - normal * curvatureRadius)) / curvatureRadius).normalized;
        float c = Vector3.Dot(w, normal);
        Vector3 s = w - normal * c;
        float sinAbs = s.magnitude;
        if (sinAbs < 1e-7f) return Vector2.zero;
        float d = Mathf.Abs(curvatureRadius) * Mathf.Atan2(sinAbs, c);
        Vector3 dir = s / sinAbs * Mathf.Sign(curvatureRadius);
        return new Vector2(Vector3.Dot(dir, east), Vector3.Dot(dir, north)) * (d / metersPerDegree);
    }

    /// <summary>
    /// A route from planar a to b over the surface: straight in the projection, `height`
    /// above the surface at both ends and rising by `lift` metres in the middle.
    /// Points are local; points.Length samples.
    /// </summary>
    public void FillArc(Vector2 a, Vector2 b, float height, float lift, Vector3[] points)
    {
        int n = points.Length - 1;
        for (int i = 0; i <= n; i++)
        {
            float t = n > 0 ? (float)i / n : 0f;
            points[i] = Surface(Vector2.Lerp(a, b, t), height + lift * 4f * t * (1f - t));
        }
    }

    // Unit vector from the sphere's centre through the surface point `d` metres from the map centre along `dir`.
    private Vector3 RadialAt(Vector3 dir, float d)
    {
        float phi = d / curvatureRadius;
        return dir * Mathf.Sin(phi) + normal * Mathf.Cos(phi);
    }
}
