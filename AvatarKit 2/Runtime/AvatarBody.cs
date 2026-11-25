using UnityEngine;

/// <summary>
/// Everything that makes a character look alive when they are NOT talking,
/// plus two body movements: a listening lean and a talking gesture.
///
/// All procedural, all in LateUpdate.
///
/// THE LATEUPDATE RULE -- read this before you change anything:
/// The Animator writes the character's pose during Update. If you rotate bones
/// in Update, the Animator overwrites you on the same frame and NOTHING
/// APPEARS TO HAPPEN. You then conclude your code is broken, when really your
/// ORDERING is broken. Bone tweaks must run in LateUpdate, after the Animator.
///
/// Requires a HUMANOID rig (Model import settings -> Rig -> Animation Type ->
/// Humanoid). Generic rigs have no standard bone names, so look-at and
/// gestures can't find anything.
/// </summary>
public class AvatarBody : MonoBehaviour
{
    public enum Mood { Idle, Listening, Talking }

    [Header("Wiring (leave empty to auto-find)")]
    public Animator animator;
    public SkinnedMeshRenderer faceRenderer;
    public Transform lookTarget;
    public AvatarLipSync lipSync;

    [Header("Head look-at")]
    [Range(0f, 1f)] public float headLookWeight = 0.55f;
    public float maxHeadYaw = 32f;
    public float maxHeadPitch = 18f;
    public float lookSpeed = 3.2f;

    [Header("Breathing")]
    public float breathAmplitude = 1.1f;
    public float breathSpeed = 0.22f;
    public float swayAmplitude = 0.9f;
    public float swaySpeed = 0.13f;

    [Header("Blink (leave name blank to auto-detect)")]
    public string blinkShape = "";
    public float blinkMinGap = 2.6f;
    public float blinkMaxGap = 6.5f;
    public float blinkDuration = 0.13f;

    [Header("Gesture")]
    public float gestureAmplitude = 4.5f;
    public float listenLean = 3.5f;

    [Header("Talking head nod (degrees at full voice)")]
    [Tooltip("Small nod that rides the voice even when the mouth works.")]
    public float talkNod = 1.5f;
    [Tooltip("Used INSTEAD when the mesh has no mouth blendshape (glTF / Avaturn T1 / Mixamo). " +
             "This is the visible 'she is talking' signal for face-less characters.")]
    public float talkNodNoMouth = 5f;

    static readonly string[] BlinkNames =
        { "Blink", "blink", "eyeBlinkLeft", "eyeBlink_L", "eyesClosed", "Fcl_EYE_Close", "Eye_Blink",
          "Eye_Blink_L", "Blink_L", "vrc.blink_left", "EyeBlink", "eyeBlinkL", "eyes_closed", "Eyes_Blink" };

    Mood _mood = Mood.Idle;
    int _blinkIdx = -1;
    // THE NO-CLIP RULE: if the Animator has no controller (a glTF character
    // has no idle clip), nothing rewrites the pose each frame, so "*=" on a
    // bone accumulates and she winds into a pretzel within a minute. Cache
    // the rest pose and SET from it instead of multiplying.
    bool _resetPose;
    Transform[] _bones = new Transform[0];
    Quaternion[] _rest = new Quaternion[0];
    float _blinkTimer, _blinkPhase = -1f, _seed, _gesture, _lean;
    Quaternion _headExtra = Quaternion.identity;

    /// <summary>
    /// Called by AvatarBrain as she listens / thinks / talks. If your Animator
    /// Controller has a bool parameter named "Talking" (and optionally
    /// "Listening"), it is set here -- that's how the idle clip hands over to
    /// the talking clip. No parameter? Nothing happens, no error.
    /// </summary>
    public void SetMood(Mood m)
    {
        _mood = m;
        if (animator == null || animator.runtimeAnimatorController == null) return;
        if (_hasTalkingParam) animator.SetBool("Talking", m == Mood.Talking);
        if (_hasListeningParam) animator.SetBool("Listening", m == Mood.Listening);
    }
    bool _hasTalkingParam, _hasListeningParam;

    void Awake()
    {
        if (animator == null) animator = GetComponent<Animator>();
        if (faceRenderer == null)
        {
            int bestCount = -1;   // the face is the mesh with the most blendshapes
            foreach (var r in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                int n = r.sharedMesh != null ? r.sharedMesh.blendShapeCount : 0;
                if (n > bestCount) { faceRenderer = r; bestCount = n; }
            }
        }
        if (lookTarget == null && Camera.main != null) lookTarget = Camera.main.transform;
        if (lipSync == null) lipSync = GetComponentInChildren<AvatarLipSync>();

        if (animator == null)
            Debug.LogWarning("[AvatarKit] AvatarBody: no Animator on the character root, so no head look-at, " +
                             "nod or gestures. Blink still works if the mesh has a blink shape. " +
                             "Humanoid rig = FBX Rig tab, or Tools > AI Avatar > Make Selected Character Humanoid (glTF).");
        else if (!animator.isHuman)
            Debug.LogWarning("[AvatarKit] Rig is not Humanoid, so head look-at and gestures " +
                             "are disabled. Fix: select the model -> Rig -> Animation Type -> Humanoid.");

        if (faceRenderer != null && faceRenderer.sharedMesh != null)
        {
            var mesh = faceRenderer.sharedMesh;
            for (int i = 0; i < mesh.blendShapeCount; i++)
                faceRenderer.SetBlendShapeWeight(i, 0f);   // see AvatarLipSync note

            if (!string.IsNullOrEmpty(blinkShape))
                _blinkIdx = mesh.GetBlendShapeIndex(blinkShape);
            if (_blinkIdx < 0)
                foreach (var n in BlinkNames)
                {
                    _blinkIdx = mesh.GetBlendShapeIndex(n);
                    if (_blinkIdx >= 0) break;
                }
            if (_blinkIdx < 0)
                Debug.LogWarning("[AvatarKit] No blink blendshape found -- she won't blink. " +
                                 "Available shapes:\n" + AvatarLipSync.ListShapes(mesh));
        }

        _seed = Random.value * 100f;
        _blinkTimer = Random.Range(blinkMinGap, blinkMaxGap);

        if (animator != null && animator.runtimeAnimatorController != null)
            foreach (var p in animator.parameters)
            {
                if (p.type != AnimatorControllerParameterType.Bool) continue;
                if (p.name == "Talking") _hasTalkingParam = true;
                if (p.name == "Listening") _hasListeningParam = true;
            }

        if (animator != null && animator.isHuman)
        {
            _resetPose = animator.runtimeAnimatorController == null;
            var list = new System.Collections.Generic.List<Transform>();
            foreach (var b in new[] { HumanBodyBones.Spine, HumanBodyBones.Chest, HumanBodyBones.Head,
                                      HumanBodyBones.LeftUpperArm, HumanBodyBones.RightUpperArm })
            {
                var t = animator.GetBoneTransform(b);
                if (t != null) list.Add(t);
            }
            _bones = list.ToArray();
            _rest = new Quaternion[_bones.Length];
            for (int i = 0; i < _bones.Length; i++) _rest[i] = _bones[i].localRotation;
        }
    }

    void Update()
    {
        if (_blinkIdx >= 0 && faceRenderer != null)
        {
            if (_blinkPhase >= 0f)
            {
                _blinkPhase += Time.deltaTime;
                float t = _blinkPhase / blinkDuration;
                float w = t < 0.5f ? t / 0.5f : 1f - (t - 0.5f) / 0.5f;
                faceRenderer.SetBlendShapeWeight(_blinkIdx, Mathf.Clamp01(w) * 100f);
                if (_blinkPhase >= blinkDuration)
                {
                    _blinkPhase = -1f;
                    faceRenderer.SetBlendShapeWeight(_blinkIdx, 0f);
                    _blinkTimer = Random.Range(blinkMinGap, blinkMaxGap);
                }
            }
            else
            {
                _blinkTimer -= Time.deltaTime;
                if (_blinkTimer <= 0f) _blinkPhase = 0f;
            }
        }

        _gesture = Mathf.MoveTowards(_gesture, _mood == Mood.Talking ? 1f : 0f, Time.deltaTime * 2.2f);
        _lean = Mathf.MoveTowards(_lean, _mood == Mood.Listening ? 1f : 0f, Time.deltaTime * 2.2f);
    }

    void LateUpdate()
    {
        if (animator == null || !animator.isHuman) return;

        if (_resetPose)
            for (int i = 0; i < _bones.Length; i++) _bones[i].localRotation = _rest[i];

        var spine = animator.GetBoneTransform(HumanBodyBones.Spine);
        var chest = animator.GetBoneTransform(HumanBodyBones.Chest);
        var head = animator.GetBoneTransform(HumanBodyBones.Head);
        var lArm = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        var rArm = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);

        float t = Time.time + _seed;
        float breath = Mathf.Sin(t * Mathf.PI * 2f * breathSpeed) * breathAmplitude;
        float sway = Mathf.Sin(t * Mathf.PI * 2f * swaySpeed) * swayAmplitude;

        if (spine != null) spine.localRotation *= Quaternion.Euler(breath * 0.6f, sway * 0.5f, sway * 0.35f);
        if (chest != null) chest.localRotation *= Quaternion.Euler(breath - _lean * listenLean, sway * 0.3f, 0f);

        if (_gesture > 0.001f)
        {
            // gesture size follows how open the mouth is, so the arms move
            // with actual speech instead of on a timer
            float energy = lipSync != null ? Mathf.Clamp01(lipSync.CurrentOpen / 60f) : 0.5f;
            float g = _gesture * gestureAmplitude * (0.35f + energy);
            if (lArm != null) lArm.localRotation *= Quaternion.Euler(0f, 0f, Mathf.Sin(t * 2.1f) * g);
            if (rArm != null) rArm.localRotation *= Quaternion.Euler(0f, 0f, -Mathf.Sin(t * 1.7f + 1.2f) * g);
            if (chest != null) chest.localRotation *= Quaternion.Euler(0f, Mathf.Sin(t * 2.1f) * g * 0.25f, 0f);
        }

        if (head != null && lookTarget != null)
        {
            Vector3 local = transform.InverseTransformDirection((lookTarget.position - head.position).normalized);
            float yaw = Mathf.Clamp(Mathf.Atan2(local.x, -local.z) * Mathf.Rad2Deg, -maxHeadYaw, maxHeadYaw);
            float pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg,
                                      -maxHeadPitch, maxHeadPitch);
            _headExtra = Quaternion.Slerp(_headExtra, Quaternion.Euler(pitch, yaw, 0f), Time.deltaTime * lookSpeed);
            head.localRotation *= Quaternion.Slerp(Quaternion.identity, _headExtra, headLookWeight);
        }

        // Talking nod: the head dips with the loudness of the voice. Tiny when
        // the mouth already moves; the whole "she's speaking" cue when it can't.
        if (head != null && lipSync != null && lipSync.CurrentOpen > 0.01f)
        {
            float amp = lipSync.HasMouthShape ? talkNod : talkNodNoMouth;
            float nod = Mathf.Clamp01(lipSync.CurrentOpen / 60f) * amp;
            head.localRotation *= Quaternion.Euler(nod, 0f, 0f);
        }
    }
}
