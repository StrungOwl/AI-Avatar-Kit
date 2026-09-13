using UnityEngine;

/// <summary>
/// Moves your character's mouth from the audio she is actually speaking.
///
/// No phoneme recogniser, no extra package. Two numbers are pulled from the
/// playing clip every frame:
///     loudness (RMS)     -> how far the jaw opens
///     spectral centroid  -> which vowel shape blends in (round vs wide)
///
/// This looks better than you'd expect, because what an audience actually
/// reads is THE MOUTH OPENING IN TIME WITH THE SOUND. Vowel accuracy is a
/// distant second -- a perfect phoneme that lags 100ms looks worse than crude
/// loudness tracking that's exactly in sync.
///
/// BLENDSHAPE NAMES: every character exports different ones. Leave the fields
/// blank and this will try to auto-detect the common conventions. If it can't
/// find them it tells you exactly what your mesh *does* have.
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class AvatarLipSync : MonoBehaviour
{
    [Header("Wiring (leave empty to auto-find)")]
    public SkinnedMeshRenderer faceRenderer;
    public AudioSource source;

    [Header("Blendshape names (leave blank to auto-detect)")]
    public string jawShape = "";
    public string roundShape = "";
    public string wideShape = "";

    [Header("Tuning")]
    [Tooltip("MEASURE THIS, don't guess it. Watch 'Peak Open' below while your " +
             "character talks: aim for 60-90. Too low and the mouth technically " +
             "works but you can't see it.")]
    public float gain = 2600f;
    [Tooltip("Loudness under this counts as silence.")]
    public float noiseFloor = 0.0018f;
    [Range(0f, 100f)] public float maxJaw = 85f;
    [Range(0.02f, 1f)] public float smoothing = 0.30f;

    [Header("Read-only diagnostics")]
    [SerializeField] float peakOpen;

    // Candidate names, most common first. Covers Oculus/viseme rigs, ARKit
    // (Ready Player Me, iPhone capture), VRM, and hand-made rigs.
    // Oculus visemes, ARKit (Avaturn T2, Ready Player Me, iPhone), VRoid/VRM (Fcl_MTH_*),
    // VRChat, CC4/iClone (Mouth_Open, V_Open), Daz and hand-made rigs.
    static readonly string[] JawNames =
        { "v_AA", "viseme_aa", "jawOpen", "JawOpen", "A", "aa", "MouthOpen", "mouthOpen", "Mouth_Open",
          "Fcl_MTH_A", "vrc.v_aa", "V_Open", "Jaw_Open", "Open", "mouth_open", "ah", "AH" };
    static readonly string[] RoundNames =
        { "v_OU", "viseme_O", "viseme_U", "mouthFunnel", "mouthPucker", "O", "ou", "U", "oh",
          "Fcl_MTH_O", "Fcl_MTH_U", "vrc.v_ou", "V_Tight_O", "Mouth_Pucker", "oo" };
    static readonly string[] WideNames =
        { "v_EE", "viseme_E", "viseme_I", "mouthSmile", "mouthSmileLeft", "mouthStretchLeft", "E", "ee", "I", "ih",
          "Fcl_MTH_E", "Fcl_MTH_I", "vrc.v_ee", "V_Wide", "Mouth_Smile", "Mouth_Smile_L", "Smile" };

    const int Window = 1024;
    int _jaw = -1, _round = -1, _wide = -1;
    readonly float[] _samples = new float[Window];
    readonly float[] _spectrum = new float[Window];
    float _a, _o, _e;

    public float CurrentOpen { get { return _a; } }
    /// <summary>False when the mesh has no usable mouth shape -- AvatarBody then nods the head instead.</summary>
    public bool HasMouthShape { get { return _jaw >= 0; } }
    public float PeakOpen { get { return peakOpen; } }
    public bool IsSpeaking { get { return source != null && source.isPlaying; } }

    void Awake()
    {
        if (source == null) source = GetComponent<AudioSource>();
        if (faceRenderer == null) faceRenderer = FindBestFace();
        if (faceRenderer == null || faceRenderer.sharedMesh == null)
        {
            Debug.LogWarning("[AvatarKit] LipSync: no SkinnedMeshRenderer found. " +
                             "Speech energy is still measured for gestures, but no mouth will move.");
            return;
        }

        var mesh = faceRenderer.sharedMesh;
        if (mesh.blendShapeCount == 0)
        {
            // glTF / Avaturn T1 / Mixamo-only bodies land here. Not fatal:
            // AvatarBody uses CurrentOpen to nod the head in time with speech.
            Debug.LogWarning("[AvatarKit] LipSync: '" + mesh.name + "' has no blendshapes, so the " +
                             "mouth can't open. Falling back to a head nod driven by the voice. " +
                             "For a real mouth, use a model with a jaw/open shape (Avaturn: T2 body).");
            return;
        }

        // Blendshapes import from FBX at weight 100, not 0 -- FBX stores a
        // DeformPercent per channel and exporters write 100. Anything a script
        // doesn't drive every frame stays fully switched on. Zero them all.
        for (int i = 0; i < mesh.blendShapeCount; i++)
            faceRenderer.SetBlendShapeWeight(i, 0f);

        _jaw = Resolve(mesh, jawShape, JawNames, "jaw/open");
        _round = Resolve(mesh, roundShape, RoundNames, "rounded (oo)");
        _wide = Resolve(mesh, wideShape, WideNames, "wide (ee)");

        if (_jaw < 0)
        {
            Debug.LogWarning("[AvatarKit] Could not find a jaw/open blendshape on '" +
                             mesh.name + "'. Its shapes are:\n" + ListShapes(mesh) +
                             "\nType the right name into the 'Jaw Shape' field. " +
                             "Until then she nods her head in time with speech instead.");
        }
    }

    // The face is whichever mesh carries the most blendshapes (hair/shoes have none).
    SkinnedMeshRenderer FindBestFace()
    {
        SkinnedMeshRenderer best = null; int bestCount = -1;
        foreach (var r in GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            int n = r.sharedMesh != null ? r.sharedMesh.blendShapeCount : 0;
            if (n > bestCount) { best = r; bestCount = n; }
        }
        return best;
    }

    static int Resolve(Mesh mesh, string explicitName, string[] candidates, string label)
    {
        if (!string.IsNullOrEmpty(explicitName))
        {
            int i = mesh.GetBlendShapeIndex(explicitName);
            if (i < 0)
                Debug.LogWarning("[AvatarKit] blendshape '" + explicitName + "' not found; auto-detecting " + label);
            else return i;
        }
        foreach (var n in candidates)
        {
            int i = mesh.GetBlendShapeIndex(n);
            if (i >= 0) return i;
        }
        // last resort: case-insensitive contains
        for (int i = 0; i < mesh.blendShapeCount; i++)
        {
            var n = mesh.GetBlendShapeName(i).ToLower();
            foreach (var c in candidates)
                if (n.Contains(c.ToLower())) return i;
        }
        return -1;
    }

    public static string ListShapes(Mesh mesh)
    {
        if (mesh.blendShapeCount == 0) return "  (this mesh has NO blendshapes at all)";
        var s = "";
        for (int i = 0; i < mesh.blendShapeCount; i++)
            s += "  [" + i + "] " + mesh.GetBlendShapeName(i) + "\n";
        return s;
    }

    void LateUpdate()
    {
        float tA = 0f, tO = 0f, tE = 0f;

        if (source != null && source.isPlaying && source.clip != null)
        {
            source.GetOutputData(_samples, 0);
            float sum = 0f;
            for (int i = 0; i < Window; i++) sum += _samples[i] * _samples[i];
            float rms = Mathf.Sqrt(sum / Window);

            if (rms > noiseFloor)
            {
                tA = Mathf.Clamp(rms * gain, 0f, maxJaw);

                source.GetSpectrumData(_spectrum, 0, FFTWindow.Hamming);
                float num = 0f, den = 0f;
                for (int i = 1; i < Window / 2; i++) { num += i * _spectrum[i]; den += _spectrum[i]; }
                float bright = den > 1e-7f ? Mathf.Clamp01((num / den) / 90f) : 0f;

                float shaped = Mathf.Clamp01(tA / Mathf.Max(maxJaw, 1f));
                tO = (1f - bright) * 55f * shaped;
                tE = bright * 45f * shaped;
            }
        }

        float k = 1f - Mathf.Pow(1f - smoothing, Time.deltaTime * 60f);
        _a = Mathf.Lerp(_a, tA, k);
        _o = Mathf.Lerp(_o, tO, k);
        _e = Mathf.Lerp(_e, tE, k);

        if (_a > peakOpen) peakOpen = _a;

        if (faceRenderer == null || _jaw < 0) return;   // no mouth shape: AvatarBody nods instead
        faceRenderer.SetBlendShapeWeight(_jaw, _a);
        if (_round >= 0) faceRenderer.SetBlendShapeWeight(_round, _o);
        if (_wide >= 0) faceRenderer.SetBlendShapeWeight(_wide, _e);
    }
}
