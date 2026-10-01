using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

/// <summary>
/// Shared look and building blocks for the world-space UI built at runtime (data
/// dashboard, timeline): one palette and type scale, rounded boxes (a 9-sliced
/// sprite generated once, no asset), text, buttons with hover / press colours and
/// sounds, and helpers that place rects in canvas units from the top-left corner
/// (y downwards; the canvases use 1000 units = 1 m).
/// </summary>
public static class UIKit
{
    // ---- Palette -----------------------------------------------------------
    public static readonly Color PanelColor = new Color(0.055f, 0.065f, 0.09f, 0.94f);
    public static readonly Color SurfaceColor = new Color(0.11f, 0.13f, 0.18f, 1f);
    public static readonly Color SurfaceHighColor = new Color(0.16f, 0.19f, 0.26f, 1f);
    public static readonly Color AccentColor = new Color(0.30f, 0.75f, 1f, 1f);
    public static readonly Color AccentSoftColor = new Color(0.30f, 0.75f, 1f, 0.16f);
    public static readonly Color HighlightColor = new Color(1f, 0.6f, 0.2f, 1f);
    public static readonly Color TextColor = new Color(0.95f, 0.96f, 0.98f, 1f);
    public static readonly Color MutedTextColor = new Color(0.62f, 0.67f, 0.76f, 1f);
    public static readonly Color DividerColor = new Color(1f, 1f, 1f, 0.07f);
    public static readonly Color GoodColor = new Color(0.45f, 0.85f, 0.55f, 1f);
    public static readonly Color BadColor = new Color(1f, 0.45f, 0.45f, 1f);

    // ---- Type scale (canvas units; 1000 units = 1 m) ------------------------
    public const float TitleSize = 40f;
    public const float HeadingSize = 27f;
    public const float BodySize = 22f;
    public const float SmallSize = 18f;

    // ---- Radii -------------------------------------------------------------
    public const float PanelRadius = 28f;
    public const float CardRadius = 16f;
    public const float ButtonRadius = 12f;

    public enum ButtonStyle { Primary, Secondary, Ghost }

    private const int SpriteRadius = 32;   // pixels in the generated sprite = canvas units at multiplier 1
    private static Sprite rounded;

    /// <summary>
    /// White rounded square, 9-sliced: Image.pixelsPerUnitMultiplier sets the corner
    /// radius (SpriteRadius / multiplier canvas units). Anti-aliased edge, generated once.
    /// </summary>
    public static Sprite RoundedSprite
    {
        get
        {
            if (rounded != null) return rounded;
            int size = SpriteRadius * 2 + 4;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "UIKit Rounded",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.DontSave,
            };
            var pixels = new Color32[size * size];
            float centre = size * 0.5f;
            float half = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Distance outside a rounded square with corner radius SpriteRadius.
                    float dx = Mathf.Max(Mathf.Abs(x + 0.5f - centre) - (half - SpriteRadius), 0f);
                    float dy = Mathf.Max(Mathf.Abs(y + 0.5f - centre) - (half - SpriteRadius), 0f);
                    float outside = Mathf.Sqrt(dx * dx + dy * dy) - SpriteRadius;
                    byte alpha = (byte)(Mathf.Clamp01(0.5f - outside) * 255f);
                    pixels[y * size + x] = new Color32(255, 255, 255, alpha);
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            float border = SpriteRadius + 1;
            rounded = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0,
                                    SpriteMeshType.FullRect, new Vector4(border, border, border, border));
            rounded.name = "UIKit Rounded";
            rounded.hideFlags = HideFlags.DontSave;
            return rounded;
        }
    }

    // ---- Layout ------------------------------------------------------------

    public static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    /// <summary>Top-left anchored rect at (x, y) from the parent's top-left corner, y growing downwards.</summary>
    public static void Place(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(w, h);
    }

    /// <summary>Fills the parent, minus `inset` on every side.</summary>
    public static void Stretch(RectTransform rt, float inset = 0f)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(inset, inset);
        rt.offsetMax = new Vector2(-inset, -inset);
    }

    // ---- Elements ----------------------------------------------------------

    /// <summary>Rounded box (radius in canvas units, 0 = square corners).</summary>
    public static Image Box(Transform parent, string name, Color color, float radius)
    {
        RectTransform rt = NewRect(name, parent);
        Image image = rt.gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        if (radius > 0f)
        {
            image.sprite = RoundedSprite;
            image.type = Image.Type.Sliced;
            image.pixelsPerUnitMultiplier = SpriteRadius / radius;
        }
        return image;
    }

    /// <summary>Card: a rounded surface box placed at (x, y).</summary>
    public static Image Card(Transform parent, float x, float y, float w, float h, Color? color = null)
    {
        Image card = Box(parent, "Card", color ?? SurfaceColor, CardRadius);
        Place(card.rectTransform, x, y, w, h);
        return card;
    }

    public static TextMeshProUGUI Text(Transform parent, string text, float size, Color color,
                                       TextAlignmentOptions align = TextAlignmentOptions.TopLeft)
    {
        RectTransform rt = NewRect("Text", parent);
        TextMeshProUGUI t = rt.gameObject.AddComponent<TextMeshProUGUI>();
        t.text = text;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.textWrappingMode = TextWrappingModes.Normal;
        t.overflowMode = TextOverflowModes.Ellipsis;
        t.raycastTarget = false;
        return t;
    }

    /// <summary>Text placed at (x, y) with size w x h.</summary>
    public static TextMeshProUGUI Text(Transform parent, string text, float size, Color color, float x, float y, float w, float h,
                                       TextAlignmentOptions align = TextAlignmentOptions.TopLeft)
    {
        TextMeshProUGUI t = Text(parent, text, size, color, align);
        Place(t.rectTransform, x, y, w, h);
        return t;
    }

    /// <summary>Base colour of each button style.</summary>
    public static Color StyleColor(ButtonStyle style)
    {
        switch (style)
        {
            case ButtonStyle.Primary: return AccentColor;
            case ButtonStyle.Secondary: return SurfaceHighColor;
            default: return new Color(1f, 1f, 1f, 0f);
        }
    }

    /// <summary>
    /// Rounded button with a text label (and / or an icon), hover and press colours,
    /// click / hover sounds (UISounds). Primary buttons get dark text on the accent.
    /// </summary>
    public static Button Button(Transform parent, string label, UnityAction onClick, ButtonStyle style = ButtonStyle.Secondary,
                                float fontSize = BodySize, UIIcon.Shape icon = UIIcon.Shape.None)
    {
        Image image = Box(parent, "Button " + label, Color.white, ButtonRadius);
        image.raycastTarget = true;
        // Configured while inactive: on enable the Button applies its normal colour at once
        // (added to an active object it would fade in from white, a visible flash).
        image.gameObject.SetActive(false);
        Button button = image.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        SetButtonColor(button, StyleColor(style), false);
        if (onClick != null) button.onClick.AddListener(onClick);
        button.onClick.AddListener(() => UISounds.Play(UISounds.Click, image.transform.position));

        // Hover blip when an XR ray / finger / mouse enters the button.
        EventTrigger trigger = image.gameObject.AddComponent<EventTrigger>();
        var enter = new EventTrigger.Entry { eventID = EventTriggerType.PointerEnter };
        enter.callback.AddListener(_ =>
        {
            if (button.interactable) UISounds.Play(UISounds.Hover, image.transform.position);
        });
        trigger.triggers.Add(enter);

        Color textColor = style == ButtonStyle.Primary ? new Color(0.03f, 0.08f, 0.13f, 1f) : TextColor;
        if (icon != UIIcon.Shape.None)
        {
            RectTransform iconRect = NewRect("Icon", image.transform);
            UIIcon graphic = iconRect.gameObject.AddComponent<UIIcon>();
            graphic.shape = icon;
            graphic.color = textColor;
            graphic.raycastTarget = false;
            // Square icon on the left (or centred when there is no label), sized from the button height.
            iconRect.anchorMin = new Vector2(0f, 0.5f);
            iconRect.anchorMax = new Vector2(0f, 0.5f);
            iconRect.pivot = new Vector2(0f, 0.5f);
            iconRect.anchoredPosition = new Vector2(string.IsNullOrEmpty(label) ? 0f : 14f, 0f);
            iconRect.sizeDelta = new Vector2(fontSize, fontSize);
            if (string.IsNullOrEmpty(label))
            {
                iconRect.anchorMin = iconRect.anchorMax = iconRect.pivot = new Vector2(0.5f, 0.5f);
                iconRect.anchoredPosition = Vector2.zero;
            }
        }
        if (!string.IsNullOrEmpty(label))
        {
            TextMeshProUGUI text = Text(image.transform, label, fontSize, textColor, TextAlignmentOptions.Center);
            Stretch(text.rectTransform);
            text.textWrappingMode = TextWrappingModes.NoWrap;
            if (icon != UIIcon.Shape.None) text.margin = new Vector4(fontSize + 20f, 0f, 10f, 0f);
        }
        image.gameObject.SetActive(true);
        return button;
    }

    /// <summary>Button placed at (x, y) with size w x h.</summary>
    public static Button Button(Transform parent, string label, UnityAction onClick, float x, float y, float w, float h,
                                ButtonStyle style = ButtonStyle.Secondary, float fontSize = BodySize, UIIcon.Shape icon = UIIcon.Shape.None)
    {
        Button button = Button(parent, label, onClick, style, fontSize, icon);
        Place((RectTransform)button.transform, x, y, w, h);
        return button;
    }

    /// <summary>
    /// Sets a button's resting colour; hover / press / disabled shades are derived from it.
    /// instant: show it right away instead of the Button's short colour fade.
    /// </summary>
    public static void SetButtonColor(Button button, Color color, bool instant = true)
    {
        bool transparent = color.a < 0.01f;
        ColorBlock colors = button.colors;
        colors.normalColor = color;
        // Selected = normal: in XR a clicked button keeps the selection, it must not stay lit.
        colors.selectedColor = color;
        colors.highlightedColor = transparent ? new Color(1f, 1f, 1f, 0.08f) : Lighten(color, 0.12f);
        colors.pressedColor = transparent ? new Color(1f, 1f, 1f, 0.16f) : Lighten(color, -0.10f);
        colors.disabledColor = new Color(color.r, color.g, color.b, color.a * 0.35f);
        colors.colorMultiplier = 1f;
        colors.fadeDuration = 0.08f;
        button.colors = colors;
        if (instant && button.isActiveAndEnabled && button.targetGraphic != null)
        {
            button.targetGraphic.CrossFadeColor(button.interactable ? color : colors.disabledColor, 0f, true, true);
        }
    }

    /// <summary>Changes a button's label text (first TextMeshProUGUI child).</summary>
    public static void SetButtonLabel(Button button, string label)
    {
        TextMeshProUGUI text = button.GetComponentInChildren<TextMeshProUGUI>();
        if (text != null) text.text = label;
    }

    public static Color Lighten(Color c, float amount)
    {
        return new Color(Mathf.Clamp01(c.r + amount), Mathf.Clamp01(c.g + amount), Mathf.Clamp01(c.b + amount), c.a);
    }

    /// <summary>Thin horizontal line at y.</summary>
    public static Image Divider(Transform parent, float x, float y, float w)
    {
        Image line = Box(parent, "Divider", DividerColor, 0f);
        Place(line.rectTransform, x, y, w, 2f);
        return line;
    }

    // ---- Canvas / event system ---------------------------------------------

    /// <summary>
    /// World-space canvas (1000 units = 1 m) that XR rays, poke and the Editor mouse can
    /// press. Not parented, so it can follow the user independently of the graph.
    /// </summary>
    public static Canvas WorldCanvas(string name, Vector2 size)
    {
        var root = new GameObject(name, typeof(RectTransform));
        Canvas canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        root.AddComponent<TrackedDeviceGraphicRaycaster>(); // XR rays and poke
        root.AddComponent<GraphicRaycaster>();              // mouse in the Editor
        var rect = (RectTransform)root.transform;
        rect.sizeDelta = size;
        rect.localScale = Vector3.one * 0.001f;
        return canvas;
    }

    /// <summary>World-space UI in XR needs an EventSystem with the XR UI Input Module.</summary>
    public static void EnsureXREventSystem()
    {
        EventSystem eventSystem = Object.FindFirstObjectByType<EventSystem>();
        if (eventSystem == null)
        {
            eventSystem = new GameObject("EventSystem (XR)").AddComponent<EventSystem>();
        }
        if (eventSystem.GetComponent<XRUIInputModule>() == null)
        {
            foreach (BaseInputModule module in eventSystem.GetComponents<BaseInputModule>()) module.enabled = false;
            eventSystem.gameObject.AddComponent<XRUIInputModule>();
        }
    }

    /// <summary>Head's forward on the horizontal plane (also when looking straight up / down).</summary>
    public static Vector3 FlatForward(Transform head)
    {
        Vector3 forward = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (forward.sqrMagnitude < 1e-4f) forward = Vector3.ProjectOnPlane(head.up, Vector3.up);
        return forward.normalized;
    }
}
