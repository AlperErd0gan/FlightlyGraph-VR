using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The start screen's surroundings (intro / loading screen): the scene is a little dim and aircraft fly
/// around the user from far outside to close beside them and out again, each leaving a very thin bright
/// trail that fades behind it. Optional: the network's airports as glowing stars (<see cref="showStars"/>).
/// Calm by design: slow, eased motion (no sudden starts, no overshoot), aircraft that fade in and out
/// while they are far away, nothing turning around the user's head, nothing below the floor.
/// StartPanel feeds <see cref="Progress"/> (0 = loading, 1 = everything ready): the aircraft fly a little
/// faster with it. When the user starts, everything fades and the lights come back.
/// Self-contained: StartPanel adds it at runtime.
/// </summary>
public class StartAmbience : MonoBehaviour
{
    [Header("Stars (off by default)")]
    [Tooltip("Show the airports as glowing stars around the user. Off: only the aircraft and their trails.")]
    public bool showStars = false;
    [Tooltip("Distance (m) of the nearest and the farthest airports from the user's head.")]
    public Vector2 radiusRange = new Vector2(2.4f, 4.2f);
    [Tooltip("Busiest routes whose arcs the aircraft fly along (the arcs themselves are not drawn).")]
    public int routeCount = 150;
    [Tooltip("Radius (m) of a star's core at 3 m distance; the glow around it is several times bigger.")]
    public float starSize = 0.025f;
    [Tooltip("Glow radius as a multiple of the core.")]
    public float glowScale = 4f;

    [Header("Floor")]
    [Tooltip("Nothing of the shell (stars, aircraft) comes lower than this above GraphLoader.FloorY.")]
    public float floorMargin = 0.3f;

    [Header("Dim")]
    [Tooltip("Dim the scene's lights, ambient light and sky while the intro is on.")]
    public bool dimScene = true;
    [Range(0.02f, 1f)]
    [Tooltip("Brightness of the scene during the intro (1 = unchanged).")]
    public float dimLevel = 0.5f;

    [Header("Aircraft")]
    [Tooltip("Aircraft flying in from outside, past the user and out again (half of them with Reduce motion).")]
    public int planeCount = 24;
    [Tooltip("Length (m) of an aircraft.")]
    public float planeSize = 0.45f;
    [Tooltip("Seconds an aircraft takes for one flight (in from outside, past you, out).")]
    public float planeSeconds = 18f;
    [Tooltip("Distance (m) from where aircraft appear and disappear.")]
    public Vector2 outerRadius = new Vector2(6.5f, 8.5f);
    [Tooltip("Closest an aircraft passes to the user's head (m).")]
    public Vector2 passRadius = new Vector2(2.2f, 3.6f);
    [Header("Following the user")]
    [Tooltip("Seconds the aircraft's space takes to catch up when the user walks (critically damped: smooth, no overshoot). The aircraft then drift past and around the user.")]
    public float followSeconds = 1.6f;
    [Range(0f, 1f)]
    [Tooltip("How much an aircraft turns its nose towards the user while it passes (0 = flies straight).")]
    public float watch = 0.4f;
    [Range(0f, 1f)]
    [Tooltip("Share of the flights that pass in front of the user, where they are looking.")]
    public float passInView = 0.5f;
    [Header("Lights")]
    [Tooltip("Size (m) of the small navigation lights (wing tips red / green, tail white) at aircraft size.")]
    public float lightSize = 0.07f;
    [Tooltip("Gentle blinks per second.")]
    public float blinkHz = 0.7f;
    [Header("Trail")]
    [Tooltip("Seconds a trail takes to fade out behind an aircraft.")]
    public float trailSeconds = 2.4f;
    [Tooltip("Width (m) of a trail at its head.")]
    public float trailWidth = 0.008f;
    [Tooltip("Length (s) of the intro when no stars rise (with stars it follows their build-up).")]
    public float introSeconds = 7f;
    [Tooltip("Sides of the fuselage (more = rounder, more triangles).")]
    public int planeDetail = 20;

    [Header("Motion")]
    [Tooltip("Turning speed (deg/s) of the shell. 0 = it stands still (turning it around the head feels like vertigo).")]
    public float spin = 0f;
    [Tooltip("Seconds to fade in, and to fade out.")]
    public float fadeInSeconds = 2.2f;
    public float fadeOutSeconds = 1.4f;
    [Tooltip("Seconds the stars take to rise into place, one after another from the lowest up.")]
    public float riseSeconds = 3f;
    [Tooltip("Metres the stars rise from below.")]
    public float riseHeight = 0.7f;

    /// <summary>Seconds since the shell started to build up.</summary>
    public float IntroClock => introClock;
    /// <summary>Everything is in place (stars risen).</summary>
    public bool IntroDone => built && introClock >= introLength;
    /// <summary>Length (s) of the build-up.</summary>
    public float IntroLength => introLength;

    /// <summary>0 = loading, 1 = ready: the stars glow brighter and the aircraft fly faster.</summary>
    public float Progress { get; set; }

    /// <summary>The shell is wanted (the start screen is open before the first start).</summary>
    public bool Wanted { get; set; }

    private const float RiseDuration = 1.6f;
    private const int TrailPoints = 40;
    private sealed class Flight
    {
        public Transform t;
        public Renderer renderer;
        public MaterialPropertyBlock block;
        public Vector3 a, b, c;         // the flight's Bezier (local): in from outside, past the user, out
        public bool active;
        public float u, speed, startAfter, alpha, blinkPhase;
        public Vector3[] trailPos = new Vector3[TrailPoints];
        public float[] trailTime = new float[TrailPoints];
        public float[] trailAlpha = new float[TrailPoints];
        public int trailCount;
        public float sampleClock;
    }

    private GraphLoader graph;
    private Transform shell;
    private Material starMaterial, planeMaterial, trailMaterial;
    private Texture2D starTexture;
    private Mesh starMesh, planeMesh, trailMesh, lightMesh;
    private Material lightMaterial;
    private GameObject trailObject, lightObject;
    private Vector3[] lightVerts;
    private Color[] lightColors;
    private Vector3 followVelocity;
    private float shellY;
    private Vector3[] trailVerts;
    private Color[] trailColors;

    // Stars: 2 camera-facing quads each (glow, core).
    private Vector3[] starPos, starVerts;
    private Color[] starColors, starBase;
    private float[] starRadius, starDelay, starPhase, starBusy;
    private float starDelayMax;
    private Flight[] flights;

    private float minY;      // lowest local height anything may have (floor + margin)
    private bool built, building;
    private float visible, clock, introClock, introLength, eased;
    private float trailClock; // real seconds: trails fade at the same speed whatever the motion setting

    // Dimming.
    private Light[] lights;
    private float[] lightIntensity;
    private float ambientIntensity, reflectionIntensity;
    private Material skyOriginal, skyDimmed;
    private bool dimmed;
    private float dimAmount;

    private void Update()
    {
        if (graph == null) graph = FindFirstObjectByType<GraphLoader>();
        if (!built && !building && Wanted && graph != null && graph.IsLoaded && Camera.main != null) Build();

        bool reduce = Comfort.ReduceMotion;
        float dt = Time.unscaledDeltaTime;
        if (!built)
        {
            // Not built yet (graph still loading): the scene already dims, so the screen does not flash later.
            ApplyDim(Mathf.MoveTowards(dimAmount, Wanted ? 1f : 0f, dt / Mathf.Max(0.05f, fadeInSeconds)));
            return;
        }

        if (Wanted)
        {
            visible = Mathf.MoveTowards(visible, 1f, dt / Mathf.Max(0.05f, fadeInSeconds));
            introClock += dt * (reduce ? 4f : 1f); // reduced motion: the build-up is nearly instant
        }
        else
        {
            visible = Mathf.MoveTowards(visible, 0f, dt / Mathf.Max(0.05f, fadeOutSeconds));
        }
        ApplyDim(visible);
        if (visible <= 0f && !Wanted)
        {
            SetShown(false);
            return;
        }
        if (visible > 0f) SetShown(true);
        FollowUser(dt);

        eased = Mathf.MoveTowards(eased, Mathf.Clamp01(Progress), dt * 0.5f);
        clock += dt * (reduce ? 0.25f : 1f);
        trailClock += dt;
        float fade = Mathf.SmoothStep(0f, 1f, visible);
        if (starMaterial != null) starMaterial.color = new Color(1f, 1f, 1f, fade);
        trailMaterial.color = new Color(1f, 1f, 1f, fade);
        if (spin != 0f && !reduce) shell.rotation = Quaternion.Euler(0f, clock * spin, 0f);

        if (showStars) AnimateStars(reduce);
        UpdateFlights(dt, reduce, fade);
        UpdateTrails();
        UpdateLights(fade);
    }

    private void SetShown(bool on)
    {
        if (shell.gameObject.activeSelf == on) return;
        shell.gameObject.SetActive(on);
        if (trailObject != null) trailObject.SetActive(on);
        if (lightObject != null) lightObject.SetActive(on);
    }

    /// <summary>
    /// The aircraft's space follows the user's walking, only horizontally and a little late (critically damped,
    /// no overshoot): when the user moves, the aircraft drift past and around them instead of staying behind.
    /// Turning the head does nothing (nothing rotates around the user).
    /// </summary>
    private void FollowUser(float dt)
    {
        if (Camera.main == null) return;
        Vector3 head = Camera.main.transform.position;
        Vector3 target = new Vector3(head.x, shellY, head.z);
        shell.position = Vector3.SmoothDamp(shell.position, target, ref followVelocity, Mathf.Max(0.05f, followSeconds), Mathf.Infinity, dt);
    }

    // ---- Stars ---------------------------------------------------------------------

    private void AnimateStars(bool reduce)
    {
        Transform cam = Camera.main != null ? Camera.main.transform : null;
        // The shell is never turned or scaled (spin is 0), so the camera's axes are also the local ones.
        Vector3 right = cam != null ? cam.right : Vector3.right;
        Vector3 up = cam != null ? cam.up : Vector3.up;
        float glow = 0.6f + 0.4f * eased;
        bool rising = introClock < starDelayMax + RiseDuration;
        for (int i = 0; i < starPos.Length; i++)
        {
            float t = Mathf.Clamp01((introClock - starDelay[i]) / RiseDuration);
            Vector3 p = starPos[i];
            if (rising)
            {
                float e = 1f - (1f - t) * (1f - t) * (1f - t);
                p += Vector3.down * ((1f - e) * riseHeight);
                p.y = Mathf.Max(p.y, minY); // rising never dips into the floor
            }
            float tw = reduce ? 0.9f : 0.78f + 0.22f * Mathf.Sin(clock * 1.1f + starPhase[i]);
            // A flash as the star arrives, then the slow twinkle.
            float arrive = t > 0f && t < 1f ? 1f + 0.8f * Mathf.Sin(t * Mathf.PI) : 1f;
            float core = starSize * starRadius[i] / 3f * starBusy[i] * arrive;
            float halo = core * glowScale * (0.9f + 0.1f * tw);

            Color c = starBase[i];
            float a = Mathf.Clamp01(tw * glow) * Mathf.SmoothStep(0f, 1f, t * 3f);
            Quad(i * 8, p, right * halo, up * halo, new Color(c.r, c.g, c.b, a));
            Quad(i * 8 + 4, p, right * core * 1.6f, up * core * 1.6f, new Color(1f, 1f, 1f, Mathf.Clamp01(a * 1.1f)));
        }
        starMesh.vertices = starVerts;
        starMesh.colors = starColors;
    }

    private void Quad(int v, Vector3 centre, Vector3 r, Vector3 u, Color color)
    {
        starVerts[v] = centre - r - u;
        starVerts[v + 1] = centre + r - u;
        starVerts[v + 2] = centre + r + u;
        starVerts[v + 3] = centre - r + u;
        starColors[v] = starColors[v + 1] = starColors[v + 2] = starColors[v + 3] = color;
    }

    /// <summary>A soft glow with a four-point star: bright centre, long thin spikes along both axes.</summary>
    private static Texture2D BuildStarTexture()
    {
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "Start Ambience Star", wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float fx = (x + 0.5f) / size * 2f - 1f, fy = (y + 0.5f) / size * 2f - 1f;
                float r = Mathf.Sqrt(fx * fx + fy * fy);
                float disc = Mathf.Pow(Mathf.Clamp01(1f - r), 2.6f);
                float spikeX = Mathf.Exp(-Mathf.Abs(fy) * 22f) * Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(fx)), 2f);
                float spikeY = Mathf.Exp(-Mathf.Abs(fx) * 22f) * Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(fy)), 2f);
                float a = Mathf.Clamp01(disc + 0.6f * Mathf.Max(spikeX, spikeY));
                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        }
        tex.SetPixels32(px);
        tex.Apply(true);
        return tex;
    }

    // ---- Build ---------------------------------------------------------------------

    /// <summary>Builds the shell once from the loaded graph, around where the user's head is now.</summary>
    private void Build()
    {
        building = true;
        var nodes = new List<GraphNode>(graph.Nodes.Values);
        if (nodes.Count == 0) { building = false; return; }

        var bounds = new Bounds(nodes[0].transform.position, Vector3.zero);
        foreach (GraphNode nd in nodes) bounds.Encapsulate(nd.transform.position);
        Vector3 layoutCentre = bounds.center;
        float maxD = 0.01f;
        foreach (GraphNode nd in nodes) maxD = Mathf.Max(maxD, (nd.transform.position - layoutCentre).magnitude);

        Vector3 centre = Camera.main.transform.position;
        minY = graph.FloorY + floorMargin - centre.y;
        shell = new GameObject("Start Ambience").transform;
        shell.position = centre;
        shellY = centre.y;
        if (showStars)
        {
            starTexture = BuildStarTexture();
            starMaterial = new Material(Shader.Find("Sprites/Default")) { name = "Start Ambience Stars (runtime)", mainTexture = starTexture };
        }
        planeMaterial = new Material(Shader.Find("Sprites/Default")) { name = "Start Ambience Aircraft (runtime)" };
        trailMaterial = new Material(Shader.Find("Sprites/Default")) { name = "Start Ambience Trails (runtime)" };
        if (starTexture == null) starTexture = BuildStarTexture();
        lightMaterial = new Material(Shader.Find("Sprites/Default")) { name = "Start Ambience Lights (runtime)", mainTexture = starTexture };

        int count = nodes.Count;
        var index = new Dictionary<GraphNode, int>();
        starPos = new Vector3[count];
        starBase = new Color[count];
        starRadius = new float[count];
        starDelay = new float[count];
        starPhase = new float[count];
        starBusy = new float[count];
        float refScale = Mathf.Max(1e-4f, nodes[0].transform.lossyScale.x);
        for (int i = 0; i < count; i++)
        {
            Vector3 offset = nodes[i].transform.position - layoutCentre;
            Vector3 dir = offset.sqrMagnitude > 1e-6f ? offset.normalized : Vector3.up;
            starRadius[i] = Mathf.Lerp(radiusRange.x, radiusRange.y, offset.magnitude / maxD);
            starPos[i] = dir * starRadius[i];
            starPos[i].y = Mathf.Max(starPos[i].y, minY); // above the floor
            index[nodes[i]] = i;
            starBase[i] = Color.Lerp(nodes[i].baseColor, Color.white, 0.35f); // brighter than the graph's colour: they glow
            starPhase[i] = Random.value * Mathf.PI * 2f;
            starBusy[i] = Mathf.Clamp(Mathf.Sqrt(nodes[i].transform.lossyScale.x / refScale), 0.7f, 1.8f);
        }
        // Rise from the lowest to the highest (a sweep upwards), a little jittered.
        var order = new int[count];
        for (int i = 0; i < count; i++) order[i] = i;
        System.Array.Sort(order, (x, y) => starPos[x].y.CompareTo(starPos[y].y));
        for (int k = 0; k < count; k++) starDelay[order[k]] = (float)k / Mathf.Max(1, count - 1) * riseSeconds + Random.value * 0.25f;
        starDelayMax = riseSeconds + 0.25f;
        introLength = showStars ? starDelayMax + RiseDuration + 0.8f : introSeconds;

        if (showStars)
        {
            // Two quads per star.
            starVerts = new Vector3[count * 8];
            starColors = new Color[count * 8];
            var uv = new Vector2[count * 8];
            var tris = new int[count * 12];
            for (int i = 0; i < count * 2; i++)
            {
                uv[i * 4] = new Vector2(0, 0);
                uv[i * 4 + 1] = new Vector2(1, 0);
                uv[i * 4 + 2] = new Vector2(1, 1);
                uv[i * 4 + 3] = new Vector2(0, 1);
                int v = i * 4, t = i * 6;
                tris[t] = v; tris[t + 1] = v + 2; tris[t + 2] = v + 1;
                tris[t + 3] = v; tris[t + 4] = v + 3; tris[t + 5] = v + 2;
            }
            starMesh = new Mesh { name = "Start Ambience Stars" };
            starMesh.MarkDynamic();
            starMesh.vertices = starVerts;
            starMesh.uv = uv;
            starMesh.triangles = tris;
            starMesh.colors = starColors;
            starMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 20f); // vertices move every frame
            var go = new GameObject("Stars");
            go.transform.SetParent(shell, false);
            go.AddComponent<MeshFilter>().sharedMesh = starMesh;
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = starMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

        }

        BuildFlights();
        shell.gameObject.SetActive(false);
        if (trailObject != null) trailObject.SetActive(false);
        if (lightObject != null) lightObject.SetActive(false);
        built = true;
        building = false;
    }

    // ---- Aircraft ------------------------------------------------------------------

    private static float Ease(float u) => u * u * (3f - 2f * u); // smoothstep: no sudden start or stop

    private void BuildFlights()
    {
        if (planeCount <= 0) return;
        // The live flights' own aircraft, with a rounder fuselage.
        planeMesh = ShadedAircraft(FlightSimulator.BuildAircraftMesh(Mathf.Max(8, planeDetail)));
        flights = new Flight[planeCount];
        for (int i = 0; i < planeCount; i++)
        {
            var go = new GameObject("Aircraft " + i);
            go.transform.SetParent(shell, false);
            go.AddComponent<MeshFilter>().sharedMesh = planeMesh;
            MeshRenderer r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = planeMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            go.SetActive(false);
            flights[i] = new Flight
            {
                t = go.transform,
                renderer = r,
                block = new MaterialPropertyBlock(),
                speed = 1f / (planeSeconds * Random.Range(0.85f, 1.2f)),
                startAfter = 0.8f + i * (planeSeconds / Mathf.Max(1, planeCount)) * 0.45f,
                blinkPhase = Random.value // one after another, not all at once
            };
        }

        // Trails: one ribbon per flight in a single dynamic mesh (camera-facing, thin, fading).
        trailVerts = new Vector3[planeCount * TrailPoints * 2];
        trailColors = new Color[trailVerts.Length];
        var idx = new List<int>(planeCount * (TrailPoints - 1) * 6);
        for (int f = 0; f < planeCount; f++)
        {
            for (int k = 0; k < TrailPoints - 1; k++)
            {
                int v = (f * TrailPoints + k) * 2;
                idx.Add(v); idx.Add(v + 1); idx.Add(v + 2);
                idx.Add(v + 1); idx.Add(v + 3); idx.Add(v + 2);
            }
        }
        trailMesh = new Mesh { name = "Start Ambience Trails" };
        trailMesh.MarkDynamic();
        trailMesh.vertices = trailVerts;
        trailMesh.colors = trailColors;
        trailMesh.SetIndices(idx, MeshTopology.Triangles, 0);
        trailMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 40f);
        // In world space (not under the shell): a trail stays where the aircraft flew when the user walks.
        var tgo = new GameObject("Start Ambience Trails");
        trailObject = tgo;
        tgo.AddComponent<MeshFilter>().sharedMesh = trailMesh;
        MeshRenderer tr = tgo.AddComponent<MeshRenderer>();
        tr.sharedMaterial = trailMaterial;
        tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        tr.receiveShadows = false;

        // Navigation lights: 3 camera-facing glows per aircraft (wing tips, tail), world space, one mesh.
        lightVerts = new Vector3[planeCount * 3 * 4];
        lightColors = new Color[lightVerts.Length];
        var luv = new Vector2[lightVerts.Length];
        var ltris = new int[planeCount * 3 * 6];
        for (int q = 0; q < planeCount * 3; q++)
        {
            luv[q * 4] = new Vector2(0, 0);
            luv[q * 4 + 1] = new Vector2(1, 0);
            luv[q * 4 + 2] = new Vector2(1, 1);
            luv[q * 4 + 3] = new Vector2(0, 1);
            int v = q * 4, t = q * 6;
            ltris[t] = v; ltris[t + 1] = v + 2; ltris[t + 2] = v + 1;
            ltris[t + 3] = v; ltris[t + 4] = v + 3; ltris[t + 5] = v + 2;
        }
        lightMesh = new Mesh { name = "Start Ambience Lights" };
        lightMesh.MarkDynamic();
        lightMesh.vertices = lightVerts;
        lightMesh.uv = luv;
        lightMesh.triangles = ltris;
        lightMesh.colors = lightColors;
        lightMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 200f);
        lightObject = new GameObject("Start Ambience Lights");
        lightObject.AddComponent<MeshFilter>().sharedMesh = lightMesh;
        MeshRenderer lr = lightObject.AddComponent<MeshRenderer>();
        lr.sharedMaterial = lightMaterial;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
    }

    // Light positions in the aircraft mesh's own space (1 m long): wing tips and the tip of the fin.
    private static readonly Vector3[] LightSpots =
    {
        new Vector3(-0.5f, -0.025f, -0.13f), new Vector3(0.5f, -0.025f, -0.13f), new Vector3(0f, 0.21f, -0.46f)
    };
    private static readonly Color[] LightColors =
    {
        new Color(1f, 0.25f, 0.2f, 1f), new Color(0.3f, 1f, 0.5f, 1f), new Color(1f, 1f, 1f, 1f)
    };

    /// <summary>Gently blinking lights: a slow smooth breathe, the three out of step, never a hard on/off.</summary>
    private void UpdateLights(float fade)
    {
        if (flights == null) return;
        Transform cam = Camera.main != null ? Camera.main.transform : null;
        Vector3 right = cam != null ? cam.right : Vector3.right;
        Vector3 up = cam != null ? cam.up : Vector3.up;
        for (int fi = 0; fi < flights.Length; fi++)
        {
            Flight f = flights[fi];
            for (int k = 0; k < 3; k++)
            {
                int v = (fi * 3 + k) * 4;
                if (!f.active)
                {
                    lightVerts[v] = lightVerts[v + 1] = lightVerts[v + 2] = lightVerts[v + 3] = Vector3.zero;
                    lightColors[v] = lightColors[v + 1] = lightColors[v + 2] = lightColors[v + 3] = Color.clear;
                    continue;
                }
                Vector3 pos = f.t.TransformPoint(LightSpots[k]);
                float breathe = 0.5f + 0.5f * Mathf.Sin(2f * Mathf.PI * (blinkHz * trailClock + f.blinkPhase + k * 0.33f));
                float size = lightSize * planeSize / 0.45f * (0.8f + 0.2f * breathe);
                Color c = LightColors[k];
                c.a = (0.25f + 0.75f * breathe) * f.alpha * fade;
                Vector3 r = right * size, u = up * size;
                lightVerts[v] = pos - r - u;
                lightVerts[v + 1] = pos + r - u;
                lightVerts[v + 2] = pos + r + u;
                lightVerts[v + 3] = pos - r + u;
                lightColors[v] = lightColors[v + 1] = lightColors[v + 2] = lightColors[v + 3] = c;
            }
        }
        lightMesh.vertices = lightVerts;
        lightMesh.colors = lightColors;
    }

    /// <summary>The scene is dim and the material unlit: the aircraft's own shape shading is baked into vertex colours.</summary>
    private static Mesh ShadedAircraft(Mesh mesh)
    {
        Vector3[] normals = mesh.normals;
        var colors = new Color[normals.Length];
        Vector3 sun = new Vector3(0.35f, 0.85f, 0.4f).normalized;
        for (int i = 0; i < normals.Length; i++)
        {
            float light = 0.5f + 0.5f * Mathf.Clamp01(Vector3.Dot(normals[i], sun));
            colors[i] = new Color(0.86f * light, 0.93f * light, 1f * light, 1f);
        }
        mesh.colors = colors;
        return mesh;
    }

    private Vector3 Bezier(Flight f, float u)
    {
        Vector3 p = (1 - u) * (1 - u) * f.a + 2 * (1 - u) * u * f.c + u * u * f.b;
        p.y = Mathf.Max(p.y, minY); // never below the floor
        return p;
    }

    /// <summary>
    /// A new flight: appears far outside, passes the user at passRadius (never nearer to the head than that),
    /// and leaves far outside again, in another direction.
    /// </summary>
    private void NewFlight(Flight f)
    {
        // Everything is relative to where the user's head is in the shell's space (the shell trails the user a little).
        Vector3 h = Camera.main != null ? shell.InverseTransformPoint(Camera.main.transform.position) : Vector3.zero;
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Vector3 d0 = RandomDirection(), d2 = RandomDirection(), dm = RandomDirection();
            if (Camera.main != null && Random.value < passInView)
            {
                // Passes in front of the user, within about 70 degrees of where they look: they watch and come to look.
                Vector3 fwd = Vector3.ProjectOnPlane(Camera.main.transform.forward, Vector3.up);
                if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
                dm = Quaternion.AngleAxis(Random.Range(-70f, 70f), Vector3.up) * fwd.normalized;
                dm.y = Random.Range(0.05f, 0.4f);
            }
            // The pass is beside the user, not far above or below.
            dm.y = Mathf.Clamp(dm.y, -0.1f, 0.55f);
            dm.Normalize();
            Vector3 p0 = h + d0 * Random.Range(outerRadius.x, outerRadius.y);
            Vector3 p2 = h + d2 * Random.Range(outerRadius.x, outerRadius.y);
            Vector3 mid = h + dm * Random.Range(passRadius.x, passRadius.y);
            f.a = p0;
            f.b = p2;
            f.c = 2f * mid - 0.5f * (p0 + p2); // quadratic Bezier that passes through `mid` at its middle
            float nearest = float.MaxValue;
            for (int k = 0; k <= 16; k++) nearest = Mathf.Min(nearest, (Bezier(f, k / 16f) - h).magnitude);
            if (nearest >= passRadius.x * 0.85f) return;
        }
    }

    private static Vector3 RandomDirection()
    {
        Vector3 d = Random.onUnitSphere;
        d.y = Mathf.Clamp(d.y, -0.15f, 0.75f); // around the user, mostly at and above head height
        return d.normalized;
    }

    private void UpdateFlights(float dt, bool reduce, float fade)
    {
        if (flights == null) return;
        float pace = (reduce ? 0.5f : 1f) * (0.85f + 0.3f * eased);
        // Reduce motion: half as many aircraft at a time.
        int allowed = reduce ? Mathf.Max(1, flights.Length / 2) : flights.Length;
        for (int i = 0; i < flights.Length; i++)
        {
            Flight f = flights[i];
            if (i >= allowed || introClock < f.startAfter)
            {
                if (f.active) { f.active = false; f.t.gameObject.SetActive(false); }
                continue;
            }
            if (!f.active || f.u >= 1f)
            {
                NewFlight(f);
                f.u = 0f;
                f.trailCount = 0; // a new flight starts with a fresh trail (the old one ended out of sight)
                f.active = true;
                f.t.gameObject.SetActive(true);
            }
            f.u += dt * f.speed * pace;
            float u = Mathf.Clamp01(f.u);
            // Slightly slower near the far ends, where it is small anyway: a gentle ease in and out.
            float s = Mathf.Lerp(u, Ease(u), 0.4f);
            Vector3 at = Bezier(f, s);
            Vector3 ahead = Bezier(f, Mathf.Min(1f, s + 0.02f));
            Vector3 dir = ahead - at;
            f.t.localPosition = at;
            if (dir.sqrMagnitude > 1e-8f)
            {
                Vector3 look = dir.normalized;
                if (watch > 0f && Camera.main != null)
                {
                    // Turns its nose towards the user while it is close (the more, the nearer), then back to its course.
                    Vector3 toUser = shell.InverseTransformPoint(Camera.main.transform.position) - at;
                    float close = 1f - Mathf.Clamp01(toUser.magnitude / (passRadius.y * 2.2f));
                    if (toUser.sqrMagnitude > 1e-6f) look = Vector3.Slerp(look, toUser.normalized, watch * Mathf.SmoothStep(0f, 1f, close)).normalized;
                }
                Quaternion target = Quaternion.LookRotation(look, Vector3.up);
                // Eased turning, never a snap.
                f.t.localRotation = f.u <= dt * f.speed * pace * 1.5f ? target : Quaternion.Slerp(f.t.localRotation, target, 1f - Mathf.Exp(-6f * dt));
            }
            f.t.localScale = Vector3.one * planeSize;

            // Semi-transparent while it is far away (large moving things are never fully solid), solid near the user.
            float near = Mathf.SmoothStep(0f, 1f, Mathf.Min(u, 1f - u) * 5f);
            float alpha = near * fade;
            f.alpha = near;
            f.block.SetColor("_Color", new Color(1f, 1f, 1f, alpha));
            f.renderer.SetPropertyBlock(f.block);

            // Trail: a point every few frames, with the alpha it had then.
            f.sampleClock += dt;
            float interval = trailSeconds / (TrailPoints - 1);
            if (f.sampleClock >= interval || f.trailCount == 0)
            {
                f.sampleClock = 0f;
                if (f.trailCount == TrailPoints)
                {
                    System.Array.Copy(f.trailPos, 1, f.trailPos, 0, TrailPoints - 1);
                    System.Array.Copy(f.trailTime, 1, f.trailTime, 0, TrailPoints - 1);
                    System.Array.Copy(f.trailAlpha, 1, f.trailAlpha, 0, TrailPoints - 1);
                    f.trailCount--;
                }
                f.trailPos[f.trailCount] = shell.TransformPoint(at); // world space
                f.trailTime[f.trailCount] = trailClock;
                f.trailAlpha[f.trailCount] = near;
                f.trailCount++;
            }
        }
    }

    /// <summary>Camera-facing ribbons, thin and bright at the aircraft and fading to nothing behind it.</summary>
    private void UpdateTrails()
    {
        if (flights == null) return;
        Vector3 eye = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
        for (int fi = 0; fi < flights.Length; fi++)
        {
            Flight f = flights[fi];
            int baseV = fi * TrailPoints * 2;
            for (int k = 0; k < TrailPoints; k++)
            {
                int v = baseV + k * 2;
                if (k >= f.trailCount)
                {
                    // Unused: collapsed to the last point, invisible.
                    Vector3 last = f.trailCount > 0 ? f.trailPos[f.trailCount - 1] : Vector3.zero;
                    trailVerts[v] = trailVerts[v + 1] = last;
                    trailColors[v] = trailColors[v + 1] = Color.clear;
                    continue;
                }
                Vector3 p = f.trailPos[k];
                Vector3 tangent = k + 1 < f.trailCount ? f.trailPos[k + 1] - p : p - (k > 0 ? f.trailPos[k - 1] : p);
                Vector3 side = Vector3.Cross(tangent, eye - p);
                side = side.sqrMagnitude > 1e-10f ? side.normalized : Vector3.up;
                float age = Mathf.Clamp01((trailClock - f.trailTime[k]) / Mathf.Max(0.1f, trailSeconds));
                float life = 1f - age;
                float width = trailWidth * 0.5f * life;        // tapers to a point
                float a = f.trailAlpha[k] * life * life;       // fades quickly, then lingers faintly
                trailVerts[v] = p - side * width;
                trailVerts[v + 1] = p + side * width;
                Color c = Color.Lerp(new Color(0.75f, 0.9f, 1f, 1f), Color.white, life); // brightest, whitest at the aircraft
                c.a = a;
                trailColors[v] = trailColors[v + 1] = c;
            }
        }
        trailMesh.vertices = trailVerts;
        trailMesh.colors = trailColors;
    }

    // ---- Dim -----------------------------------------------------------------------

    /// <summary>amount 0 = the scene as it is, 1 = dimmed to dimLevel. Restores everything at 0.</summary>
    private void ApplyDim(float amount)
    {
        dimAmount = amount;
        if (!dimScene) return;
        if (amount <= 0f)
        {
            if (dimmed) RestoreDim();
            return;
        }
        if (!dimmed) CaptureDim();
        float k = Mathf.Lerp(1f, dimLevel, Mathf.SmoothStep(0f, 1f, amount));
        for (int i = 0; i < lights.Length; i++) if (lights[i] != null) lights[i].intensity = lightIntensity[i] * k;
        RenderSettings.ambientIntensity = ambientIntensity * k;
        RenderSettings.reflectionIntensity = reflectionIntensity * k;
        if (skyDimmed != null)
        {
            if (skyDimmed.HasProperty("_Exposure")) skyDimmed.SetFloat("_Exposure", skyOriginal.GetFloat("_Exposure") * k);
            else if (skyDimmed.HasProperty("_Tint")) skyDimmed.SetColor("_Tint", skyOriginal.GetColor("_Tint") * k);
        }
    }

    private void CaptureDim()
    {
        dimmed = true;
        lights = FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        lightIntensity = new float[lights.Length];
        for (int i = 0; i < lights.Length; i++) lightIntensity[i] = lights[i].intensity;
        ambientIntensity = RenderSettings.ambientIntensity;
        reflectionIntensity = RenderSettings.reflectionIntensity;
        skyOriginal = RenderSettings.skybox;
        // A copy: the skybox asset itself is never changed.
        if (skyOriginal != null && (skyOriginal.HasProperty("_Exposure") || skyOriginal.HasProperty("_Tint")))
        {
            skyDimmed = new Material(skyOriginal) { name = "Start Ambience Sky (runtime)" };
            RenderSettings.skybox = skyDimmed;
        }
    }

    private void RestoreDim()
    {
        dimmed = false;
        if (lights != null)
        {
            for (int i = 0; i < lights.Length; i++) if (lights[i] != null) lights[i].intensity = lightIntensity[i];
        }
        RenderSettings.ambientIntensity = ambientIntensity;
        RenderSettings.reflectionIntensity = reflectionIntensity;
        if (skyDimmed != null)
        {
            RenderSettings.skybox = skyOriginal;
            Destroy(skyDimmed);
            skyDimmed = null;
        }
        lights = null;
    }

    private void OnDestroy()
    {
        if (dimmed) RestoreDim();
        if (shell != null) Destroy(shell.gameObject);
        if (starMaterial != null) Destroy(starMaterial);
        if (planeMaterial != null) Destroy(planeMaterial);
        if (trailMaterial != null) Destroy(trailMaterial);
        if (trailMesh != null) Destroy(trailMesh);
        if (trailObject != null) Destroy(trailObject);
        if (lightObject != null) Destroy(lightObject);
        if (lightMesh != null) Destroy(lightMesh);
        if (lightMaterial != null) Destroy(lightMaterial);
        if (starTexture != null) Destroy(starTexture);
        if (starMesh != null) Destroy(starMesh);
        if (planeMesh != null) Destroy(planeMesh);
    }
}
