using UnityEngine;

/// <summary>
/// Builds a flat equirectangular world-map quad under the graph.
/// Size 20 x 10 centred at the origin matches build_graph.py's projection:
/// x = lon / 18 (-10..10), z = lat / 18 (-5..5).
/// </summary>
public class MapPlane : MonoBehaviour
{
    [Tooltip("Equirectangular world map (2:1 aspect).")]
    public Texture2D mapTexture;
    [Tooltip("Taken from GraphLoader on the same object if present; otherwise this value is used.")]
    public float positionScale = 1f;
    [Tooltip("Small drop so node spheres at altitude 0 sit on top of the map.")]
    public float yOffset = -0.05f;

    private void Start()
    {
        GraphLoader loader = GetComponent<GraphLoader>();
        if (loader != null)
        {
            positionScale = loader.positionScale;
        }

        GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        quad.name = "WorldMap";
        quad.transform.SetParent(transform, false);
        // Quad faces +Z by default; rotate to lie flat facing up.
        quad.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
        quad.transform.localPosition = new Vector3(0f, yOffset, 0f);
        quad.transform.localScale = new Vector3(20f, 10f, 1f) * positionScale;
        // Map must not intercept node selection raycasts.
        Destroy(quad.GetComponent<Collider>());

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
        Material mat = new Material(shader);
        if (mapTexture != null)
        {
            mat.mainTexture = mapTexture;
        }
        quad.GetComponent<Renderer>().material = mat;
    }
}
