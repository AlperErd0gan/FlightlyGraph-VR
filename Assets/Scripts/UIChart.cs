using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Minimal UI chart (a uGUI Graphic): a line or bar chart of float values scaled
/// to the rect, with a baseline. Built as a mesh in OnPopulateMesh, so it needs no
/// package and costs one draw call. One bar or point can be highlighted.
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class UIChart : MaskableGraphic
{
    public enum Kind { Line, Bars }

    public Kind kind = Kind.Line;
    public float[] values = System.Array.Empty<float>();
    [Tooltip("Line width in canvas units.")]
    public float lineWidth = 3f;
    [Tooltip("Gap between bars as a fraction of the bar slot.")]
    [Range(0f, 0.9f)] public float barGap = 0.25f;
    public Color axisColor = new Color(1f, 1f, 1f, 0.3f);
    [Tooltip("-1 = none.")]
    public int highlightIndex = -1;
    public Color highlightColor = new Color(1f, 0.55f, 0.15f, 1f);

    public void SetData(float[] data, Kind chartKind, int highlight = -1)
    {
        values = data ?? System.Array.Empty<float>();
        kind = chartKind;
        highlightIndex = highlight;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect r = GetPixelAdjustedRect();
        AddQuad(vh, new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin + 1.5f), axisColor);
        if (values == null || values.Length == 0) return;

        float max = 0f;
        foreach (float v in values) max = Mathf.Max(max, v);
        if (max <= 0f) return;

        int n = values.Length;
        if (kind == Kind.Bars)
        {
            float slot = r.width / n;
            for (int i = 0; i < n; i++)
            {
                float h = Mathf.Max(0f, values[i]) / max * r.height;
                float x0 = r.xMin + i * slot + slot * barGap * 0.5f;
                float x1 = r.xMin + (i + 1) * slot - slot * barGap * 0.5f;
                AddQuad(vh, new Vector2(x0, r.yMin), new Vector2(x1, r.yMin + h), i == highlightIndex ? highlightColor : color);
            }
            return;
        }

        if (n == 1)
        {
            float y = r.yMin + values[0] / max * r.height;
            AddLine(vh, new Vector2(r.xMin, y), new Vector2(r.xMax, y), color);
            return;
        }
        Vector2 previous = Point(r, 0, n, values[0], max);
        for (int i = 1; i < n; i++)
        {
            Vector2 current = Point(r, i, n, values[i], max);
            AddLine(vh, previous, current, color);
            previous = current;
        }
        if (highlightIndex >= 0 && highlightIndex < n)
        {
            Vector2 p = Point(r, highlightIndex, n, values[highlightIndex], max);
            float s = lineWidth * 2f;
            AddQuad(vh, p - new Vector2(s, s), p + new Vector2(s, s), highlightColor);
        }
    }

    private static Vector2 Point(Rect r, int i, int n, float value, float max)
    {
        return new Vector2(r.xMin + r.width * i / (n - 1), r.yMin + Mathf.Max(0f, value) / max * r.height);
    }

    private void AddLine(VertexHelper vh, Vector2 a, Vector2 b, Color c)
    {
        Vector2 dir = b - a;
        if (dir.sqrMagnitude < 1e-6f) return;
        Vector2 normal = new Vector2(-dir.y, dir.x).normalized * (lineWidth * 0.5f);
        int start = vh.currentVertCount;
        vh.AddVert(a - normal, c, Vector2.zero);
        vh.AddVert(a + normal, c, Vector2.zero);
        vh.AddVert(b + normal, c, Vector2.zero);
        vh.AddVert(b - normal, c, Vector2.zero);
        vh.AddTriangle(start, start + 1, start + 2);
        vh.AddTriangle(start, start + 2, start + 3);
    }

    private static void AddQuad(VertexHelper vh, Vector2 min, Vector2 max, Color c)
    {
        int start = vh.currentVertCount;
        vh.AddVert(new Vector3(min.x, min.y), c, Vector2.zero);
        vh.AddVert(new Vector3(min.x, max.y), c, Vector2.zero);
        vh.AddVert(new Vector3(max.x, max.y), c, Vector2.zero);
        vh.AddVert(new Vector3(max.x, min.y), c, Vector2.zero);
        vh.AddTriangle(start, start + 1, start + 2);
        vh.AddTriangle(start, start + 2, start + 3);
    }
}
