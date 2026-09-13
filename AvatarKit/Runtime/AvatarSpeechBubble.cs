using UnityEngine;

/// <summary>
/// A comic-style speech bubble that floats beside the character's head and
/// reveals her words ONE BY ONE while the voice clip plays.
///
/// Why it exists: not every model can open its mouth (Avaturn's download has
/// no face shapes, Mixamo rigs have none, most AI-generated meshes have none).
/// A bubble that fills in as she speaks is the clearest "she is talking"
/// signal there is, and it works on every character -- with a mouth or not.
///
/// How the timing works: the voice API gives us audio, not word timestamps.
/// So the bubble spreads the words evenly across the clip's length -- word k
/// appears at (k / totalWords) of the way through. It's not lip-sync-exact,
/// but it stays within a word or two of her voice, which is what people read.
///
/// Styling: every look setting (Font, Shape, Color, Outline) is in the
/// Inspector and updates live while playing. Right-click the component for
/// quick presets (Comic, Thought, Shout, Minimal). The bubble art is drawn
/// in code, so there are still no sprites to make.
///
/// Add it next to AvatarBrain (Add Component -> Avatar Speech Bubble). It
/// finds everything else on its own. Same IMGUI as the rest of the kit: no
/// Canvas, no TextMeshPro, nothing to import.
/// </summary>
public class AvatarSpeechBubble : MonoBehaviour
{
    public enum BubbleShape { Rectangle, Rounded, Pill, Ellipse }
    public enum TailStyle { None, Pointer, ThoughtDots }
    public enum OutlineStyle { None, Solid, Double }

    [Header("Wiring (leave empty to auto-find)")]
    public AvatarBrain brain;
    public AudioSource voice;
    public Animator animator;
    [Tooltip("Where the bubble hangs from. Auto: the Humanoid Head bone; else this object.")]
    public Transform anchor;

    [Header("Placement")]
    [Tooltip("Offset from the anchor, in metres. +X is her right/left, +Y is up.")]
    public Vector3 worldOffset = new Vector3(0.28f, 0.22f, 0f);
    [Tooltip("Bubble width in pixels.")]
    public float width = 340f;

    [Header("Font")]
    [Tooltip("Drag in any font you imported (.ttf / .otf). Empty = Unity's built-in font.")]
    public Font font;
    public int fontSize = 20;
    public FontStyle fontStyle = FontStyle.Normal;
    public Color textColor = new Color(0.12f, 0.12f, 0.14f);
    public TextAnchor textAlignment = TextAnchor.UpperLeft;

    [Header("Name Tag")]
    [Tooltip("Show her name at the top of the bubble.")]
    public bool showName = true;
    public FontStyle nameFontStyle = FontStyle.Bold;
    public Color nameColor = new Color(0.35f, 0.35f, 0.4f);
    [Range(0.3f, 1.5f)] public float nameSizeScale = 0.7f;

    [Header("Shape")]
    public BubbleShape shape = BubbleShape.Rounded;
    [Tooltip("Corner roundness in pixels. Only used by the Rounded shape.")]
    public float cornerRadius = 14f;
    [Tooltip("Space between the text and the bubble's edge, in pixels.")]
    public Vector2 padding = new Vector2(16f, 12f);
    public TailStyle tail = TailStyle.Pointer;
    [Tooltip("Tail width and height in pixels.")]
    public Vector2 tailSize = new Vector2(26f, 22f);
    [Tooltip("How far in from the bubble's side the tail sits, in pixels.")]
    public float tailInset = 22f;

    [Header("Color")]
    [Tooltip("Bubble fill. Keep alpha at 1 for the cleanest tail join.")]
    public Color bubbleColor = Color.white;

    [Header("Outline")]
    public OutlineStyle outline = OutlineStyle.Solid;
    public Color outlineColor = new Color(0.1f, 0.1f, 0.12f, 1f);
    [Range(0f, 12f)] public float outlineWidth = 3f;
    [Tooltip("Space between the two lines. Double outline only.")]
    [Range(0f, 12f)] public float doubleGap = 2f;
    public bool dropShadow = true;
    public Color shadowColor = new Color(0f, 0f, 0f, 0.3f);
    public Vector2 shadowOffset = new Vector2(4f, 4f);

    [Header("Behaviour")]
    [Tooltip("Hide the kit's full-reply box at the bottom of the screen, since the bubble replaces it.")]
    public bool hideBottomReplyBox = true;
    [Tooltip("Seconds the finished bubble stays up after the voice stops.")]
    public float lingerSeconds = 2.5f;

    float _noHeadLift = 0f;
    string[] _words = new string[0];
    float _start = -1f, _duration = 0f;
    AudioClip _lastClip;
    GUIStyle _style, _nameStyle;

    // Generated art. Rebuilt when a style setting changes or the bubble's size changes.
    bool _dirty = true;
    Texture2D _body, _bodyMask, _tailTex, _tailMask, _tailInner;
    readonly Texture2D[] _dots = new Texture2D[3], _dotMasks = new Texture2D[3];
    int _bodyW, _bodyH, _tailW, _tailH;
    static readonly float[] DotScale = { 0.5f, 0.34f, 0.2f };
    static readonly float[] DotPos = { 0f, 0.55f, 1f };

    /// <summary>Words currently visible -- what a viewer sees right now.</summary>
    public string VisibleText { get; private set; }
    public bool IsShowing { get { return _start >= 0f; } }

    void Awake()
    {
        if (brain == null) brain = GetComponent<AvatarBrain>();
        if (voice == null) voice = GetComponent<AudioSource>();
        if (animator == null) animator = GetComponent<Animator>();
        if (anchor == null && animator != null && animator.isHuman)
            anchor = animator.GetBoneTransform(HumanBodyBones.Head);
        if (anchor == null)
        {
            // No Humanoid head: hang the bubble from the top of whatever is visible.
            anchor = transform;
            var rs = GetComponentsInChildren<Renderer>(true);
            if (rs.Length > 0)
            {
                var b = rs[0].bounds;
                foreach (var r in rs) b.Encapsulate(r.bounds);
                _noHeadLift = b.max.y - transform.position.y;
            }
        }
        if (brain != null && hideBottomReplyBox) brain.showReplyBox = false;
        VisibleText = "";
    }

    // Inspector edits (including during Play) redraw the bubble art.
    void OnValidate() { _dirty = true; }
    void OnDestroy() { FreeTextures(); }

    /// <summary>Call after changing style fields from code at runtime.</summary>
    public void RefreshStyle() { _dirty = true; }

    /// <summary>Show text now, spread over 'seconds'. Used by the voice hook, and by the test menu.</summary>
    public void Show(string text, float seconds)
    {
        text = (text ?? "").Trim();
        _words = text.Length == 0 ? new string[0] : text.Split(new[] { ' ', '\n', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
        _duration = Mathf.Max(seconds, 0.2f);
        _start = Time.time;
    }

    /// <summary>Right-click this component while playing -> see the bubble without spending an API call.</summary>
    [ContextMenu("Test Bubble")]
    public void TestBubble()
    {
        Show("This is the speech bubble. Each word shows up as I say it, so you can follow along even when my mouth can't move.", 6f);
    }

    // ---------- Presets (right-click the component) ----------

    [ContextMenu("Style Preset/Comic")]
    void PresetComic()
    {
        BeginPreset();
        shape = BubbleShape.Rounded; cornerRadius = 14f; padding = new Vector2(16f, 12f);
        tail = TailStyle.Pointer; tailSize = new Vector2(26f, 22f); tailInset = 22f;
        bubbleColor = Color.white; textColor = new Color(0.12f, 0.12f, 0.14f);
        fontStyle = FontStyle.Normal; textAlignment = TextAnchor.UpperLeft;
        outline = OutlineStyle.Solid; outlineWidth = 3f; outlineColor = new Color(0.1f, 0.1f, 0.12f);
        dropShadow = true;
        EndPreset();
    }

    [ContextMenu("Style Preset/Thought")]
    void PresetThought()
    {
        BeginPreset();
        shape = BubbleShape.Ellipse; padding = new Vector2(10f, 8f);
        tail = TailStyle.ThoughtDots; tailSize = new Vector2(30f, 40f); tailInset = 40f;
        bubbleColor = Color.white; textColor = new Color(0.2f, 0.2f, 0.25f);
        fontStyle = FontStyle.Italic; textAlignment = TextAnchor.MiddleCenter;
        outline = OutlineStyle.Solid; outlineWidth = 2f; outlineColor = new Color(0.25f, 0.25f, 0.3f);
        dropShadow = false;
        EndPreset();
    }

    [ContextMenu("Style Preset/Shout")]
    void PresetShout()
    {
        BeginPreset();
        shape = BubbleShape.Rectangle; padding = new Vector2(18f, 14f);
        tail = TailStyle.Pointer; tailSize = new Vector2(30f, 26f); tailInset = 24f;
        bubbleColor = new Color(1f, 0.92f, 0.3f); textColor = Color.black;
        fontStyle = FontStyle.Bold; textAlignment = TextAnchor.MiddleCenter;
        outline = OutlineStyle.Double; outlineWidth = 4f; doubleGap = 3f; outlineColor = Color.black;
        dropShadow = true;
        EndPreset();
    }

    [ContextMenu("Style Preset/Minimal")]
    void PresetMinimal()
    {
        BeginPreset();
        shape = BubbleShape.Rounded; cornerRadius = 8f; padding = new Vector2(16f, 12f);
        tail = TailStyle.Pointer; tailSize = new Vector2(18f, 12f); tailInset = 16f;
        bubbleColor = new Color(1f, 1f, 1f, 0.96f); textColor = new Color(0.12f, 0.12f, 0.14f);
        fontStyle = FontStyle.Normal; textAlignment = TextAnchor.UpperLeft;
        outline = OutlineStyle.None; dropShadow = false;
        EndPreset();
    }

    void BeginPreset()
    {
#if UNITY_EDITOR
        UnityEditor.Undo.RecordObject(this, "Speech Bubble Preset");
#endif
    }

    void EndPreset()
    {
        _dirty = true;
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    // ---------- Timing ----------

    void Update()
    {
        // Voice hook: a new clip started playing -> new bubble timed to its length.
        if (voice != null && voice.isPlaying && voice.clip != null && voice.clip != _lastClip)
        {
            _lastClip = voice.clip;
            string line = brain != null ? brain.LastReply : "";
            if (!string.IsNullOrEmpty(line)) Show(line, voice.clip.length);
        }
        if (voice != null && !voice.isPlaying) _lastClip = null;

        if (_start < 0f) { VisibleText = ""; return; }

        float t = Time.time - _start;
        // If the audio is the clock, follow it exactly (handles pauses/interrupts).
        if (voice != null && voice.isPlaying && voice.clip == _lastClip && _lastClip != null)
            t = voice.time;

        int n = _words.Length;
        int visible = t >= _duration ? n : Mathf.Clamp(Mathf.CeilToInt(n * (t / _duration)), 1, n);
        VisibleText = string.Join(" ", _words, 0, Mathf.Max(visible, 0));

        bool audioDone = voice == null || !voice.isPlaying;
        if (t >= _duration + lingerSeconds && audioDone) { _start = -1f; _words = new string[0]; VisibleText = ""; }
    }

    // ---------- Drawing ----------

    void OnGUI()
    {
        if (_start < 0f || string.IsNullOrEmpty(VisibleText)) return;
        var cam = Camera.main;
        if (cam == null) return;

        if (_dirty || _style == null) { BuildStyles(); FreeTextures(); _dirty = false; }

        // Anchor: head + offset, in the CAMERA's right/up so the offset reads the same from any angle.
        Vector3 world = anchor.position + Vector3.up * _noHeadLift
                      + cam.transform.right * worldOffset.x + cam.transform.up * worldOffset.y + cam.transform.forward * worldOffset.z;
        Vector3 sp = cam.WorldToScreenPoint(world);
        if (sp.z < 0f) return;   // behind the camera

        string text = VisibleText;

        // Size: text area first, then grow it to fit the shape.
        float w = Mathf.Ceil(Mathf.Min(width, Screen.width - 32f));
        float innerW = w - 2f * padding.x;
        if (shape == BubbleShape.Ellipse) innerW = w * 0.7f - 2f * padding.x;   // corners of an ellipse are cut off
        else if (shape == BubbleShape.Pill) innerW -= _style.lineHeight;         // round ends eat some width
        innerW = Mathf.Max(innerW, 20f);
        float textH = _style.CalcHeight(new GUIContent(text), innerW);
        float h = Mathf.Ceil((shape == BubbleShape.Ellipse ? textH * 1.42f : textH) + 2f * padding.y);
        float tailH = tail == TailStyle.None ? 0f : Mathf.Ceil(Mathf.Max(4f, tailSize.y));
        float tw = Mathf.Ceil(Mathf.Max(4f, tailSize.x));
        float inset = Mathf.Clamp(tailInset, 0f, Mathf.Max(0f, w - tw));
        float nameH = showName ? _nameStyle.lineHeight + 2f : 0f;

        // Position: tail tip on the anchor point. No room on the right? Hang it to the left.
        bool flip = sp.x - inset + w > Screen.width - 16f;
        float x = flip ? sp.x - w + inset : sp.x - inset;
        float y = Screen.height - sp.y - h - tailH;          // GUI y runs top-down
        x = Mathf.Clamp(x, 16f, Screen.width - w - 16f);
        y = Mathf.Clamp(y, 16f + nameH, Screen.height - h - tailH - 16f);
        var body = new Rect(x, y, w, h);

        EnsureBody((int)w, (int)h);

        // The pointer tail tucks up into the bubble so the two read as one shape.
        float band = OutlineBand();
        float overlap = Mathf.Ceil(Mathf.Min(band + 2f + BottomGap(inset + tw * 0.4f, w, h), h * 0.5f));
        var tailRect = new Rect(flip ? x + w - inset - tw : x + inset, y + h - overlap, tw, tailH + overlap);
        if (tail == TailStyle.Pointer) EnsureTail((int)tw, (int)tailRect.height);
        if (tail == TailStyle.ThoughtDots) EnsureDots();

        Vector2 dotStart = Vector2.zero, dotEnd = Vector2.zero;
        if (tail == TailStyle.ThoughtDots)
        {
            float tipX = flip ? x + w - inset : x + inset;
            dotStart = new Vector2(tipX + (flip ? -tw : tw) * 0.6f, y + h + _dots[0].height * 0.5f + 2f);
            dotEnd = new Vector2(tipX, y + h + tailH - _dots[2].height * 0.5f);
        }

        Color prevColor = GUI.color;

        if (dropShadow)
        {
            GUI.color = shadowColor;
            if (tail == TailStyle.Pointer)
                GUI.DrawTextureWithTexCoords(new Rect(tailRect.x + shadowOffset.x, y + h + shadowOffset.y, tw, tailH),
                                             _tailMask, Uv(flip, 0f, tailH / tailRect.height));
            if (tail == TailStyle.ThoughtDots)
                for (int i = 0; i < 3; i++) GUI.DrawTexture(DotRect(i, dotStart, dotEnd, shadowOffset), _dotMasks[i]);
            GUI.DrawTexture(Shift(body, shadowOffset), _bodyMask);
        }

        GUI.color = Color.white;
        if (tail == TailStyle.Pointer) GUI.DrawTextureWithTexCoords(tailRect, _tailTex, Uv(flip, 0f, 1f));
        GUI.DrawTexture(body, _body);
        if (tail == TailStyle.Pointer && band > 0f)
        {
            // Paint over the bubble's outline where the tail joins it.
            GUI.color = bubbleColor;
            GUI.DrawTextureWithTexCoords(new Rect(tailRect.x, tailRect.y, tw, overlap), _tailInner,
                                         Uv(flip, 1f - overlap / tailRect.height, overlap / tailRect.height));
            GUI.color = Color.white;
        }
        if (tail == TailStyle.ThoughtDots)
            for (int i = 0; i < 3; i++) GUI.DrawTexture(DotRect(i, dotStart, dotEnd, Vector2.zero), _dots[i]);

        GUI.color = prevColor;

        if (showName && brain != null && brain.persona != null)
            GUI.Label(new Rect(x + 4f, y - nameH, w, nameH), brain.persona.characterName, _nameStyle);
        GUI.Label(new Rect(x + (w - innerW) * 0.5f, y + (h - textH) * 0.5f, innerW, textH), text, _style);
    }

    void BuildStyles()
    {
        _style = new GUIStyle(GUI.skin.label)
        {
            font = font, fontSize = fontSize, fontStyle = fontStyle, alignment = textAlignment,
            wordWrap = true, richText = false, padding = new RectOffset(0, 0, 0, 0), margin = new RectOffset(0, 0, 0, 0)
        };
        _style.normal.textColor = textColor;
        _nameStyle = new GUIStyle(GUI.skin.label)
        {
            font = font, fontSize = Mathf.Max(6, Mathf.RoundToInt(fontSize * nameSizeScale)), fontStyle = nameFontStyle, wordWrap = false
        };
        _nameStyle.normal.textColor = nameColor;
    }

    Rect DotRect(int i, Vector2 from, Vector2 to, Vector2 offset)
    {
        float d = _dots[i].width;
        Vector2 c = Vector2.Lerp(from, to, DotPos[i]) + offset;
        return new Rect(c.x - d * 0.5f, c.y - d * 0.5f, d, d);
    }

    static Rect Shift(Rect r, Vector2 o) { return new Rect(r.x + o.x, r.y + o.y, r.width, r.height); }
    static Rect Uv(bool flip, float v0, float vh) { return flip ? new Rect(1f, v0, -1f, vh) : new Rect(0f, v0, 1f, vh); }

    float OutlineBand()
    {
        if (outline == OutlineStyle.None || outlineWidth <= 0f) return 0f;
        if (outline == OutlineStyle.Solid) return outlineWidth;
        return outlineWidth + doubleGap + Mathf.Max(1f, outlineWidth * 0.5f);
    }

    float Radius(float w, float h)
    {
        if (shape == BubbleShape.Pill) return Mathf.Min(w, h) * 0.5f;
        if (shape == BubbleShape.Rounded) return Mathf.Clamp(cornerRadius, 0f, Mathf.Min(w, h) * 0.5f);
        return 0f;
    }

    // How far the bubble's bottom edge curves up at 'xFromSide' pixels in from its side.
    float BottomGap(float xFromSide, float w, float h)
    {
        if (shape == BubbleShape.Ellipse)
        {
            float u = Mathf.Clamp(xFromSide / (w * 0.5f) - 1f, -1f, 1f);
            return h * 0.5f * (1f - Mathf.Sqrt(1f - u * u));
        }
        float r = Radius(w, h);
        if (xFromSide >= r) return 0f;
        float dx = r - xFromSide;
        return r - Mathf.Sqrt(Mathf.Max(0f, r * r - dx * dx));
    }

    // ---------- Texture generation ----------

    void EnsureBody(int w, int h)
    {
        if (_body != null && _bodyW == w && _bodyH == h) return;
        Kill(ref _body); Kill(ref _bodyMask);
        _bodyW = w; _bodyH = h;
        float cx = w * 0.5f, cy = h * 0.5f;

        if (shape == BubbleShape.Ellipse)
        {
            float a = cx, b = cy;
            Paint(w, h, (px, py) =>
            {
                float dx = px - cx, dy = py - cy;
                float k0 = Mathf.Sqrt(dx * dx / (a * a) + dy * dy / (b * b));
                float k1 = Mathf.Sqrt(dx * dx / (a * a * a * a) + dy * dy / (b * b * b * b));
                float d = k1 > 1e-6f ? k0 * (k0 - 1f) / k1 : -Mathf.Min(a, b);
                return new Vector2(d, -d);
            }, false, out _body, out _bodyMask, out _);
        }
        else
        {
            float r = Radius(w, h);
            Paint(w, h, (px, py) =>
            {
                float qx = Mathf.Abs(px - cx) - cx + r, qy = Mathf.Abs(py - cy) - cy + r;
                float d = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - r;
                return new Vector2(d, -d);
            }, false, out _body, out _bodyMask, out _);
        }
    }

    void EnsureTail(int w, int h)
    {
        if (_tailTex != null && _tailW == w && _tailH == h) return;
        Kill(ref _tailTex); Kill(ref _tailMask); Kill(ref _tailInner);
        _tailW = w; _tailH = h;

        // Tip bottom-left, base along the top (runs past the edge so only the two sides get an outline).
        var A = new Vector2(1f, 1f);
        var B = new Vector2(w * 0.4f, h + 2f);
        var C = new Vector2(w - 1f, h + 2f);
        Paint(w, h, (px, py) =>
        {
            var p = new Vector2(px, py);
            float ab = SegDist(p, A, B), ac = SegDist(p, A, C), bc = SegDist(p, B, C);
            float nearest = Mathf.Min(ab, Mathf.Min(ac, bc));
            return InTri(p, A, B, C) ? new Vector2(-nearest, Mathf.Min(ab, ac)) : new Vector2(nearest, 0f);
        }, true, out _tailTex, out _tailMask, out _tailInner);
    }

    void EnsureDots()
    {
        if (_dots[0] != null) return;
        float big = Mathf.Max(tailSize.x, tailSize.y);
        for (int i = 0; i < 3; i++)
        {
            int d = Mathf.Max(4, Mathf.RoundToInt(big * DotScale[i]));
            float r = d * 0.5f;
            Paint(d, d, (px, py) =>
            {
                float dist = new Vector2(px - r, py - r).magnitude - (r - 0.5f);
                return new Vector2(dist, -dist);
            }, false, out _dots[i], out _dotMasks[i], out _);
        }
    }

    /// <summary>
    /// Rasterises a shape. sdf returns (signed distance to the edge, negative inside;
    /// depth used for the outline). Outputs the coloured art, a white mask for the
    /// shadow, and optionally a white mask of just the fill inside the outline.
    /// </summary>
    void Paint(int w, int h, System.Func<float, float, Vector2> sdf, bool wantInner,
               out Texture2D color, out Texture2D mask, out Texture2D inner)
    {
        var c = new Color[w * h];
        var m = new Color[w * h];
        var n = wantInner ? new Color[w * h] : null;
        float ow = outline == OutlineStyle.None ? 0f : outlineWidth;
        float band = OutlineBand();

        for (int j = 0; j < h; j++)
        for (int i = 0; i < w; i++)
        {
            Vector2 s = sdf(i + 0.5f, j + 0.5f);
            float a = Mathf.Clamp01(0.5f - s.x);   // soft 1px edge
            float line = 0f;
            if (ow > 0f)
            {
                line = 1f - Mathf.Clamp01(s.y - ow + 0.5f);
                if (outline == OutlineStyle.Double)
                    line += Mathf.Clamp01(s.y - (ow + doubleGap) + 0.5f) - Mathf.Clamp01(s.y - band + 0.5f);
            }
            Color col = Color.Lerp(bubbleColor, outlineColor, Mathf.Clamp01(line));
            col.a *= a;
            int k = j * w + i;
            c[k] = col;
            m[k] = new Color(1f, 1f, 1f, a);
            if (n != null) n[k] = new Color(1f, 1f, 1f, s.y > 0f ? a * Mathf.Clamp01(s.y - band + 0.5f) : 0f);
        }

        color = MakeTex(w, h, c);
        mask = MakeTex(w, h, m);
        inner = n != null ? MakeTex(w, h, n) : null;
    }

    static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Vector2.Dot(ab, ab));
        return (p - (a + ab * t)).magnitude;
    }

    static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float s1 = (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);
        float s2 = (c.x - b.x) * (p.y - b.y) - (c.y - b.y) * (p.x - b.x);
        float s3 = (a.x - c.x) * (p.y - c.y) - (a.y - c.y) * (p.x - c.x);
        bool neg = s1 < 0f || s2 < 0f || s3 < 0f;
        bool pos = s1 > 0f || s2 > 0f || s3 > 0f;
        return !(neg && pos);
    }

    static Texture2D MakeTex(int w, int h, Color[] pixels)
    {
        var t = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave
        };
        t.SetPixels(pixels);
        t.Apply(false, true);
        return t;
    }

    void FreeTextures()
    {
        Kill(ref _body); Kill(ref _bodyMask);
        Kill(ref _tailTex); Kill(ref _tailMask); Kill(ref _tailInner);
        for (int i = 0; i < 3; i++) { Kill(ref _dots[i]); Kill(ref _dotMasks[i]); }
        _bodyW = _bodyH = _tailW = _tailH = 0;
    }

    static void Kill(ref Texture2D t)
    {
        if (t == null) return;
        if (Application.isPlaying) Destroy(t); else DestroyImmediate(t);
        t = null;
    }
}
