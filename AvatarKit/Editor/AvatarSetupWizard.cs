using UnityEngine;
using UnityEditor;

/// <summary>
/// One-click wiring, so a student's first hour goes into their CHARACTER
/// rather than into dragging references between components.
///
/// Select your rigged character in the Hierarchy, then:
///     Tools -> AI Avatar -> Set Up Selected Character
///
/// It also runs a health check and tells you, in plain language, what is
/// wrong and how to fix it.
/// </summary>
public static class AvatarSetupWizard
{
    [MenuItem("Tools/AI Avatar/Set Up Selected Character")]
    static void SetUp()
    {
        var go = Selection.activeGameObject;
        if (go == null)
        {
            EditorUtility.DisplayDialog("AI Avatar",
                "Select your character in the Hierarchy first (the top object, " +
                "the one with the Animator).", "OK");
            return;
        }

        // glTF characters arrive with no Animator/Avatar at all -- build one.
        var existing = go.GetComponent<Animator>();
        if (existing == null || !existing.isHuman)
        {
            string msg;
            if (GltfHumanoidBuilder.TryMakeHumanoid(go, out msg)) Debug.Log("[AvatarKit] " + msg);
            else Debug.LogWarning("[AvatarKit] " + msg);
        }

        var smr = BestFaceRenderer(go);
        if (smr == null && go.GetComponentInChildren<Renderer>(true) == null)
        {
            EditorUtility.DisplayDialog("AI Avatar",
                "Nothing visible found under '" + go.name + "'.\n\n" +
                "Did you select the character root? It should have a mesh " +
                "somewhere underneath it.", "OK");
            return;
        }
        if (smr == null)
            Debug.LogWarning("[AvatarKit] '" + go.name + "' has no skinned mesh (a static model). " +
                             "She can still talk, think, remember and show a speech bubble -- " +
                             "just no mouth or body motion. That's fine for a first build.");

        Undo.RegisterFullObjectHierarchyUndo(go, "Set Up AI Avatar");

        var audio = go.GetComponent<AudioSource>();
        if (audio == null) audio = Undo.AddComponent<AudioSource>(go);
        audio.playOnAwake = false;
        audio.spatialBlend = 0f;

        var lip = go.GetComponent<AvatarLipSync>();
        if (lip == null) lip = Undo.AddComponent<AvatarLipSync>(go);
        lip.source = audio;
        lip.faceRenderer = smr;

        var body = go.GetComponent<AvatarBody>();
        if (body == null) body = Undo.AddComponent<AvatarBody>(go);
        body.animator = go.GetComponent<Animator>();
        body.faceRenderer = smr;
        body.lipSync = lip;
        if (body.lookTarget == null && Camera.main != null)
            body.lookTarget = Camera.main.transform;

        var brain = go.GetComponent<AvatarBrain>();
        if (brain == null) brain = Undo.AddComponent<AvatarBrain>(go);
        brain.voice = audio;
        brain.lipSync = lip;
        brain.body = body;

        // Long-term memory: on by default, because a companion that forgets
        // you every session is just a chat bot. The component's enable
        // checkbox is the privacy toggle -- disable it for an anonymous build.
        var mem = go.GetComponent<AvatarMemory>();
        if (mem == null) mem = Undo.AddComponent<AvatarMemory>(go);
        brain.memory = mem;

        // The voice picker. A new one copies the Persona's voice, so nothing
        // changes until you pick something else in its dropdown.
        var av = go.GetComponent<AvatarVoice>();
        if (av == null)
        {
            av = Undo.AddComponent<AvatarVoice>(go);
            av.CopyFromPersona(brain.persona);
        }
        brain.avatarVoice = av;

        // blendshapes arrive from FBX at 100, not 0 -- fix it in the scene too
        var mesh = smr.sharedMesh;
        if (mesh != null)
        {
            Undo.RecordObject(smr, "Zero blendshapes");
            for (int i = 0; i < mesh.blendShapeCount; i++) smr.SetBlendShapeWeight(i, 0f);
            EditorUtility.SetDirty(smr);
        }

        EditorUtility.SetDirty(go);
        Debug.Log("[AvatarKit] Set up '" + go.name + "'. Now assign a Persona asset " +
                  "to the AvatarBrain, and create your key file at:\n" + AvatarVoiceAPI.KeyPath +
                  "\nTo change her voice (OpenAI or ElevenLabs), use the AvatarVoice component.");
        HealthCheck();
    }

    /// <summary>
    /// Characters are often several meshes (body, hair, shoes, eyes...). The
    /// face is the one that carries the blendshapes -- pick the mesh with the
    /// most of them, not the first one found.
    /// </summary>
    public static SkinnedMeshRenderer BestFaceRenderer(GameObject go)
    {
        SkinnedMeshRenderer best = null; int bestCount = -1;
        foreach (var r in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            int n = r.sharedMesh != null ? r.sharedMesh.blendShapeCount : 0;
            if (n > bestCount) { best = r; bestCount = n; }
        }
        return best;
    }

    [MenuItem("Tools/AI Avatar/Health Check")]
    static void HealthCheck()
    {
        var brain = Object.FindAnyObjectByType<AvatarBrain>();
        var sb = new System.Text.StringBuilder();
        bool ok = true;

        if (brain == null)
        {
            EditorUtility.DisplayDialog("AI Avatar Health Check",
                "No AvatarBrain in the scene.\n\nSelect your character and use " +
                "Tools > AI Avatar > Set Up Selected Character.", "OK");
            return;
        }
        var go = brain.gameObject;

        sb.AppendLine("CHARACTER: " + go.name).AppendLine();

        var anim = go.GetComponent<Animator>();
        if (anim == null)
        {
            sb.AppendLine("!  No Animator -- no head look-at, nod or gestures. (Voice and bubble still work.)");
            sb.AppendLine("   Fix (FBX): model file > Rig > Animation Type > Humanoid > Apply.");
            sb.AppendLine("   Fix (glTF/.glb): Tools > AI Avatar > Make Selected Character Humanoid (glTF).");
            sb.AppendLine("   Static mesh with no bones: nothing to fix -- use the speech bubble.");
        }
        else if (!anim.isHuman)
        {
            sb.AppendLine("!  Rig is not Humanoid -- no head look-at or gestures.");
            sb.AppendLine("   Fix: select the model file > Rig > Animation Type > Humanoid > Apply.");
            ok = false;
        }
        else sb.AppendLine("OK Humanoid rig.");

        if (anim != null && anim.runtimeAnimatorController == null)
            sb.AppendLine("!  No Animator Controller -- she'll stand perfectly still.");

        var smr = BestFaceRenderer(go);
        if (smr == null || smr.sharedMesh == null)
        {
            if (go.GetComponentInChildren<Renderer>(true) == null) { sb.AppendLine("X  No mesh at all."); ok = false; }
            else sb.AppendLine("!  Static model (no skinned mesh) -- voice, brain, memory and the speech bubble work; no mouth or body motion.");
        }
        else
        {
            int n = smr.sharedMesh.blendShapeCount;
            if (n == 0)
            {
                sb.AppendLine("!  No blendshapes on any mesh -- the mouth cannot open.");
                sb.AppendLine("   The kit falls back to a head nod in time with her voice, so she still reads as talking.");
                sb.AppendLine("   Real mouth: Avaturn > pick a T2 (animatable face) body and re-export;");
                sb.AppendLine("   or add shape keys in Blender; or use a model that has them.");
            }
            else
            {
                sb.AppendLine("OK " + n + " blendshapes:");
                sb.Append(AvatarLipSync.ListShapes(smr.sharedMesh));
            }
        }

        if (brain.persona == null)
        {
            sb.AppendLine("X  No Persona assigned.");
            sb.AppendLine("   Fix: right-click in Project > Create > AI Avatar > Character Persona,");
            sb.AppendLine("        then drag it onto the AvatarBrain component.");
            ok = false;
        }
        else sb.AppendLine("OK Persona: " + brain.persona.characterName);

        if (!AvatarVoiceAPI.HasKey)
        {
            sb.AppendLine("X  No API key.");
            sb.AppendLine("   Fix: create a text file containing only your key at:");
            sb.AppendLine("        " + AvatarVoiceAPI.KeyPaths[0]);
            sb.AppendLine("   (or one folder above the project, outside the repo:");
            sb.AppendLine("        " + AvatarVoiceAPI.KeyPaths[1] + ")");
            ok = false;
        }
        else sb.AppendLine("OK API key found: " + AvatarVoiceAPI.KeyPath);

        var av = go.GetComponent<AvatarVoice>();
        if (av == null || !av.enabled)
            sb.AppendLine("OK Voice: the Persona's OpenAI voice" +
                          (brain.persona != null ? " '" + brain.persona.ttsVoice + "'" : "") +
                          ". (Add an AvatarVoice component to pick ElevenLabs.)");
        else if (av.Problem() != null)
        {
            sb.AppendLine("X  Voice: " + av.Problem().Replace("\n", "\n   "));
            ok = false;
        }
        else sb.AppendLine("OK Voice: " + av.Summary);

        var know = new AvatarKnowledge();
        if (know.TryLoad())
            sb.AppendLine("OK Knowledge index: " + know.ChunkCount + " chunks. " +
                          "(Persona > Use Knowledge is the A/B toggle.)");
        else
            sb.AppendLine("!  No knowledge index -- factual answers come from the " +
                          "base model's guesses.\n   Fix: put .md files in " +
                          "Assets/Knowledge, then Tools > AI Avatar > Build Knowledge Index.");

        var mem = go.GetComponent<AvatarMemory>();
        if (mem == null || !mem.enabled)
            sb.AppendLine("!  Long-term memory is OFF -- she will forget everything " +
                          "between sessions. (Fine if anonymity is the design.)");
        else
            sb.AppendLine("OK Memory on: " + (mem.HasMemory
                ? mem.MemoryLineCount + " lines of notes."
                : "no notes yet -- she writes them as you talk.") +
                "\n     file: " + mem.MemoryPath);

        if (Microphone.devices.Length == 0) { sb.AppendLine("X  No microphone."); ok = false; }
        else
        {
            sb.AppendLine("OK " + Microphone.devices.Length + " microphone(s):");
            for (int i = 0; i < Microphone.devices.Length; i++)
                sb.AppendLine("     [" + i + "] " + Microphone.devices[i]);
            sb.AppendLine("   (AvatarBrain > Microphone Index picks which one.)");
        }

        if (!PlayerSettings.runInBackground)
            sb.AppendLine("!  'Run In Background' is OFF -- she may never answer if the " +
                          "Editor loses focus. It will be set automatically at runtime.");

        sb.AppendLine().AppendLine(ok ? "READY. Press Play." : "Fix the X items above, then re-run.");
        Debug.Log("[AvatarKit] Health Check\n" + sb);
        EditorUtility.DisplayDialog("AI Avatar Health Check", sb.ToString(), "OK");
    }
}
