using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Small vector icons for the runtime UI (the default TMP font has no media symbols):
/// drawn as a mesh in the rect, in the graphic's colour, one draw call with the canvas.
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class UIIcon : MaskableGraphic
{
    public enum Shape { None, Play, Pause, Close, Previous, Next, Timeline, Grid }

    public Shape shape = Shape.Play;

    public void SetShape(Shape value)
    {
        if (shape == value) return;
        shape = value;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        Rect r = GetPixelAdjustedRect();
        // Square drawing area centred in the rect.
        float s = Mathf.Min(r.width, r.height);
        Vector2 o = r.center - new Vector2(s, s) * 0.5f;
        Vector2 P(float x, float y) => o + new Vector2(x * s, y * s);   // (0,0) bottom-left .. (1,1) top-right
        float line = s * 0.12f;

        switch (shape)
        {
            case Shape.Play:
                Triangle(vh, P(0.22f, 0.1f), P(0.22f, 0.9f), P(0.9f, 0.5f));
                break;
            case Shape.Pause:
                Quad(vh, P(0.2f, 0.1f), P(0.42f, 0.9f));
                Quad(vh, P(0.58f, 0.1f), P(0.8f, 0.9f));
                break;
            case Shape.Close:
                Line(vh, P(0.18f, 0.18f), P(0.82f, 0.82f), line);
                Line(vh, P(0.18f, 0.82f), P(0.82f, 0.18f), line);
                break;
            case Shape.Previous:
                Triangle(vh, P(0.75f, 0.12f), P(0.75f, 0.88f), P(0.3f, 0.5f));
                Quad(vh, P(0.18f, 0.12f), P(0.3f, 0.88f));
                break;
            case Shape.Next:
                Triangle(vh, P(0.25f, 0.12f), P(0.25f, 0.88f), P(0.7f, 0.5f));
                Quad(vh, P(0.7f, 0.12f), P(0.82f, 0.88f));
                break;
            case Shape.Timeline:
                // A small line chart over an axis.
                Quad(vh, P(0.08f, 0.1f), P(0.92f, 0.1f + 0.08f));
                Line(vh, P(0.12f, 0.35f), P(0.38f, 0.75f), line);
                Line(vh, P(0.38f, 0.75f), P(0.6f, 0.45f), line);
                Line(vh, P(0.6f, 0.45f), P(0.88f, 0.85f), line);
                break;
            case Shape.Grid:
                Quad(vh, P(0.12f, 0.12f), P(0.44f, 0.44f));
                Quad(vh, P(0.56f, 0.12f), P(0.88f, 0.44f));
                Quad(vh, P(0.12f, 0.56f), P(0.44f, 0.88f));
                Quad(vh, P(0.56f, 0.56f), P(0.88f, 0.88f));
                break;
        }
    }

    private void Triangle(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c)
    {
        int start = vh.currentVertCount;
        vh.AddVert(a, color, Vector2.zero);
        vh.AddVert(b, color, Vector2.zero);
        vh.AddVert(c, color, Vector2.zero);
        vh.AddTriangle(start, start + 1, start + 2);
    }

    private void Quad(VertexHelper vh, Vector2 min, Vector2 max)
    {
        int start = vh.currentVertCount;
        vh.AddVert(new Vector2(min.x, min.y), color, Vector2.zero);
        vh.AddVert(new Vector2(min.x, max.y), color, Vector2.zero);
        vh.AddVert(new Vector2(max.x, max.y), color, Vector2.zero);
        vh.AddVert(new Vector2(max.x, min.y), color, Vector2.zero);
        vh.AddTriangle(start, start + 1, start + 2);
        vh.AddTriangle(start, start + 2, start + 3);
    }

    private void Line(VertexHelper vh, Vector2 a, Vector2 b, float width)
    {
        Vector2 dir = b - a;
        if (dir.sqrMagnitude < 1e-6f) return;
        Vector2 n = new Vector2(-dir.y, dir.x).normalized * (width * 0.5f);
        int start = vh.currentVertCount;
        vh.AddVert(a - n, color, Vector2.zero);
        vh.AddVert(a + n, color, Vector2.zero);
        vh.AddVert(b + n, color, Vector2.zero);
        vh.AddVert(b - n, color, Vector2.zero);
        vh.AddTriangle(start, start + 1, start + 2);
        vh.AddTriangle(start, start + 2, start + 3);
    }
}
