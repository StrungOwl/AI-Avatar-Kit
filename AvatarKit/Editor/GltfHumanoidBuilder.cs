using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

/// <summary>
/// glTF (.glb) characters arrive WITHOUT a Humanoid rig. The glTF importer
/// (glTFast) imports the bones but never builds a Unity Avatar, and it adds
/// an Animator only when the file contains animation clips. FBX gets all of
/// this from the Rig tab; glTF gets nothing. So the kit builds it.
///
/// It reads the character's bone NAMES, works out which is which (hips,
/// spine, head, left upper arm ...), builds a Humanoid Avatar from the pose
/// the character is standing in, saves the Avatar next to the model, and
/// puts an Animator on the root.
///
///     Tools -> AI Avatar -> Make Selected Character Humanoid (glTF)
///
/// Set Up Selected Character calls this for you when the character has no
/// Humanoid Animator, so most students never need the menu item.
///
/// NAME CONVENTIONS UNDERSTOOD (so any model a student brings should work):
///   Mixamo / Avaturn / Ready Player Me   Hips, Spine, Spine1, Spine2, Neck, Head, LeftArm, LeftForeArm, LeftUpLeg ...
///   Unity / generic                      Hips, Spine, Chest, UpperChest, LeftUpperArm, LeftLowerArm, LeftUpperLeg ...
///   VRoid / VRM                          J_Bip_C_Hips, J_Bip_L_UpperArm, J_Bip_L_LowerArm ...
///   Blender Rigify / MakeHuman           DEF-spine, upper_arm.L, forearm.L, thigh.L, shin.L, foot.L ...
///   Character Creator / iClone           CC_Base_Hip, CC_Base_L_Upperarm, CC_Base_L_Forearm, CC_Base_L_Thigh, CC_Base_L_Calf ...
///   Daz                                  hip, lShldrBend, lForearmBend, lThighBend, lShin, lFoot ...
/// Prefixes like "mixamorig:", "J_Bip_", "DEF-", "CC_Base_" are stripped; side
/// is read from "Left/Right", "L_/R_", "_L/_R", ".L/.R", or a leading "l"/"r".
/// </summary>
public static class GltfHumanoidBuilder
{
    // Unity human bone -> keywords (after prefix/side stripping, lower-case, no separators).
    // First keyword list that matches a bone wins; within a list, earlier keywords are preferred.
    // "Spine1"/"Spine2" (Mixamo) become Chest/UpperChest; "spine.001" (Blender) likewise.
    static readonly string[][] Centre =
    {
        new[]{"Hips",       "hips","hip","pelvis","root"},
        new[]{"Spine",      "spine","spine0","abdomenlower","waist"},
        new[]{"Chest",      "chest","spine1","spine001","abdomenupper","chestlower"},
        new[]{"UpperChest", "upperchest","spine2","spine002","chestupper"},
        new[]{"Neck",       "neck","neck1","necklower","neck001"},
        new[]{"Head",       "head"},
        new[]{"Jaw",        "jaw","lowerjaw"},
    };
    static readonly string[][] Sided =
    {
        new[]{"Shoulder", "shoulder","clavicle","collar"},
        new[]{"UpperArm", "upperarm","arm","shldrbend","shldr","upperarm1"},
        new[]{"LowerArm", "lowerarm","forearm","forearmbend","elbow"},
        new[]{"Hand",     "hand","wrist"},
        new[]{"UpperLeg", "upperleg","upleg","thigh","thighbend","hip"},
        new[]{"LowerLeg", "lowerleg","leg","shin","calf","knee"},
        new[]{"Foot",     "foot","ankle"},
        new[]{"Toes",     "toes","toebase","toe","ball"},
        new[]{"Eye",      "eye"},
    };
    static readonly string[] Required =
    {
        "Hips","Spine","Head","LeftUpperArm","LeftLowerArm","LeftHand","RightUpperArm","RightLowerArm","RightHand",
        "LeftUpperLeg","LeftLowerLeg","LeftFoot","RightUpperLeg","RightLowerLeg","RightFoot"
    };
    static readonly string[] Prefixes = { "mixamorig:", "mixamorig", "j_bip_", "j_sec_", "def-", "cc_base_", "bip01", "bip", "b_", "bone_" };

    [MenuItem("Tools/AI Avatar/Make Selected Character Humanoid (glTF)")]
    static void MenuMakeHumanoid()
    {
        var go = Selection.activeGameObject;
        if (go == null)
        {
            EditorUtility.DisplayDialog("AI Avatar", "Select your character's top object in the Hierarchy first.", "OK");
            return;
        }
        string msg;
        bool ok = TryMakeHumanoid(go, out msg);
        EditorUtility.DisplayDialog("AI Avatar - Humanoid from glTF", msg, "OK");
        if (ok) Debug.Log("[AvatarKit] " + msg); else Debug.LogWarning("[AvatarKit] " + msg);
    }

    /// <summary>Builds and assigns a Humanoid Avatar. Returns false with a plain-language reason if it can't.</summary>
    public static bool TryMakeHumanoid(GameObject go, out string message)
    {
        var anim = go.GetComponent<Animator>();
        if (anim != null && anim.avatar != null && anim.avatar.isValid && anim.isHuman)
        {
            message = "'" + go.name + "' is already Humanoid. Nothing to do.";
            return true;
        }

        var all = go.GetComponentsInChildren<Transform>(true);
        var map = MatchBones(all);

        var missing = new List<string>();
        foreach (var r in Required) if (!map.ContainsKey(r)) missing.Add(r);
        if (missing.Count > 0)
        {
            message = "Could not build a Humanoid rig for '" + go.name + "'.\n\nBones I could not find: " +
                      string.Join(", ", missing) + ".\n\nThe kit reads Mixamo, Avaturn, Ready Player Me, VRoid, " +
                      "Rigify, Character Creator and Daz bone names. If your bones are named some other way, " +
                      "rename them to the Mixamo convention in Blender (Hips, Spine, Head, LeftArm, LeftForeArm, " +
                      "LeftHand, LeftUpLeg, LeftLeg, LeftFoot ...), or export FBX and use the Rig tab " +
                      "(Animation Type > Humanoid) instead.\n\nBones found: " + ListBones(all);
            return false;
        }

        var human = new List<HumanBone>();
        foreach (var kv in map)
            human.Add(new HumanBone { humanName = kv.Key, boneName = kv.Value.name, limit = new HumanLimit { useDefaultValues = true } });

        var skeleton = new List<SkeletonBone>();
        foreach (var t in all)
            skeleton.Add(new SkeletonBone { name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale });

        var desc = new HumanDescription
        {
            human = human.ToArray(), skeleton = skeleton.ToArray(),
            upperArmTwist = 0.5f, lowerArmTwist = 0.5f, upperLegTwist = 0.5f, lowerLegTwist = 0.5f,
            armStretch = 0.05f, legStretch = 0.05f, feetSpacing = 0f, hasTranslationDoF = false
        };

        // AvatarBuilder takes the pose the character is in RIGHT NOW as its rest pose.
        // T-pose is ideal (Avaturn, Mixamo "T-pose" export); A-pose still works for this kit.
        var avatar = AvatarBuilder.BuildHumanAvatar(go, desc);
        if (avatar == null || !avatar.isValid || !avatar.isHuman)
        {
            message = "Unity rejected the rig for '" + go.name + "'. Usual causes: the character is posed oddly in the " +
                      "scene (drag a fresh copy from the Project window and try again), or two bones were matched to the " +
                      "same slot. Matched: " + Describe(map);
            return false;
        }

        // Save the Avatar as an asset next to the model, so it survives a reload.
        string folder = "Assets/Character";
        var smr = go.GetComponentInChildren<SkinnedMeshRenderer>();
        if (smr != null && smr.sharedMesh != null)
        {
            string meshPath = AssetDatabase.GetAssetPath(smr.sharedMesh);
            if (!string.IsNullOrEmpty(meshPath)) folder = System.IO.Path.GetDirectoryName(meshPath).Replace('\\', '/');
        }
        if (!AssetDatabase.IsValidFolder(folder)) folder = "Assets";
        string path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + go.name + "_HumanoidAvatar.asset");
        avatar.name = go.name + "_HumanoidAvatar";
        AssetDatabase.CreateAsset(avatar, path);
        AssetDatabase.SaveAssets();

        if (anim == null) anim = Undo.AddComponent<Animator>(go);
        Undo.RecordObject(anim, "Assign Humanoid Avatar");
        anim.avatar = avatar;
        anim.applyRootMotion = false;
        EditorUtility.SetDirty(anim);

        message = "Built a Humanoid rig for '" + go.name + "' from " + human.Count + " named bones.\n" +
                  "Saved: " + path + "\nAnimator added and assigned.";
        return true;
    }

    // ---------------------------------------------------------------- matching

    /// <summary>Human bone name -> transform, from names alone. Public so the Health Check can dry-run it.</summary>
    public static Dictionary<string, Transform> MatchBones(Transform[] all)
    {
        var result = new Dictionary<string, Transform>();
        // Skip mesh objects and the armature root: they are not bones.
        var bones = new List<Transform>();
        foreach (var t in all)
            if (t.GetComponent<Renderer>() == null && t.GetComponent<MeshFilter>() == null) bones.Add(t);

        // Centre bones: no side marker allowed.
        foreach (var row in Centre)
        {
            Transform best = null; int bestRank = int.MaxValue;
            foreach (var t in bones)
            {
                string side; string core = Normalize(t.name, out side);
                if (side != "") continue;
                for (int k = 1; k < row.Length; k++)
                    if (core == row[k] && k < bestRank) { best = t; bestRank = k; }
            }
            if (best != null) result[row[0]] = best;
        }

        // Sided bones.
        foreach (var row in Sided)
        {
            foreach (var sideName in new[] { "Left", "Right" })
            {
                Transform best = null; int bestRank = int.MaxValue;
                foreach (var t in bones)
                {
                    string side; string core = Normalize(t.name, out side);
                    if (side != sideName) continue;
                    for (int k = 1; k < row.Length; k++)
                        if (core == row[k] && k < bestRank) { best = t; bestRank = k; }
                }
                if (best != null) result[sideName + row[0]] = best;
            }
        }

        // A single "Chest" without "UpperChest" on a Mixamo-style rig is fine.
        // But Unity insists Chest exists if UpperChest does.
        if (result.ContainsKey("UpperChest") && !result.ContainsKey("Chest"))
        {
            result["Chest"] = result["UpperChest"]; result.Remove("UpperChest");
        }
        // Optional bones must never be the same transform as a required one.
        foreach (var opt in new[] { "Jaw", "LeftEye", "RightEye", "LeftToes", "RightToes" })
            if (result.ContainsKey(opt))
                foreach (var kv in new List<KeyValuePair<string, Transform>>(result))
                    if (kv.Key != opt && kv.Value == result[opt]) { result.Remove(opt); break; }
        return result;
    }

    /// <summary>
    /// "mixamorig:LeftForeArm" -> core "forearm", side "Left".
    /// "J_Bip_L_UpperArm"      -> core "upperarm", side "Left".
    /// "upper_arm.L"           -> core "upperarm", side "Left".
    /// "lShldrBend"            -> core "shldrbend", side "Left".
    /// "CC_Base_R_Calf"        -> core "calf",     side "Right".
    /// </summary>
    static string Normalize(string raw, out string side)
    {
        string n = raw.Trim().ToLowerInvariant();
        foreach (var p in Prefixes) if (n.StartsWith(p)) { n = n.Substring(p.Length); break; }
        if (n.StartsWith("c_")) n = n.Substring(2);                  // VRM centre marker

        side = "";
        // explicit words
        if (n.StartsWith("left")) { side = "Left"; n = n.Substring(4); }
        else if (n.StartsWith("right")) { side = "Right"; n = n.Substring(5); }
        else if (n.EndsWith("left")) { side = "Left"; n = n.Substring(0, n.Length - 4); }
        else if (n.EndsWith("right")) { side = "Right"; n = n.Substring(0, n.Length - 5); }
        // markers: l_ / r_ / _l / _r / .l / .r / -l / -r
        else if (n.StartsWith("l_") || n.StartsWith("l.") || n.StartsWith("l-")) { side = "Left"; n = n.Substring(2); }
        else if (n.StartsWith("r_") || n.StartsWith("r.") || n.StartsWith("r-")) { side = "Right"; n = n.Substring(2); }
        else if (n.EndsWith("_l") || n.EndsWith(".l") || n.EndsWith("-l")) { side = "Left"; n = n.Substring(0, n.Length - 2); }
        else if (n.EndsWith("_r") || n.EndsWith(".r") || n.EndsWith("-r")) { side = "Right"; n = n.Substring(0, n.Length - 2); }
        // Daz style: a bare leading l/r glued to a capitalised word (lShldrBend, rThighBend)
        else if (raw.Length > 2 && (raw[0] == 'l' || raw[0] == 'r') && char.IsUpper(raw[1]))
        { side = raw[0] == 'l' ? "Left" : "Right"; n = n.Substring(1); }

        // trailing numbering like ".001" (Blender) becomes "001"; keep it for spine001 matching, drop separators
        var sb = new System.Text.StringBuilder();
        foreach (char c in n) if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    static string Describe(Dictionary<string, Transform> map)
    {
        var parts = new List<string>();
        foreach (var kv in map) parts.Add(kv.Key + "=" + kv.Value.name);
        return string.Join(", ", parts);
    }

    static string ListBones(Transform[] all)
    {
        var names = new List<string>();
        foreach (var t in all) if (t.GetComponent<Renderer>() == null) names.Add(t.name);
        if (names.Count > 40) { names.RemoveRange(40, names.Count - 40); names.Add("..."); }
        return string.Join(", ", names);
    }
}
