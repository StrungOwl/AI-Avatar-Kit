// AvatarPhoneCheck -- one menu item that gets the project ready for an Android or iPhone build.
// It looks at the settings a phone build needs, fixes the safe ones for you, and tells you
// in plain words what you must still do by hand.
//
// Menu: Tools > AI Avatar > Get Ready For A Phone Build
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class AvatarPhoneCheck
{
    const string Title = "AI Avatar: Phone Build Check";
    /// <summary>For automatic tests only: answer "Fix them" without showing the question.</summary>
    public static bool answerYes;

    [MenuItem("Tools/AI Avatar/Get Ready For A Phone Build")]
    static void Run()
    {
        var fixes = new List<string>();     // what the tool can change for you
        var byHand = new List<string>();    // what only you can do
        var good = new List<string>();

        // 1. The scene with the character must be the first scene in the build list.
        var scene = EditorSceneManager.GetActiveScene();
        bool hasBrain = Object.FindAnyObjectByType<AvatarBrain>() != null;
        var list = EditorBuildSettings.scenes.ToList();
        bool sceneFirst = list.Count > 0 && list[0].enabled && list[0].path == scene.path;
        bool fixScene = false;
        if (!hasBrain)
            byHand.Add("This scene has no character with an AvatarBrain. Open your avatar scene, then run this check again.");
        else if (string.IsNullOrEmpty(scene.path))
            byHand.Add("Save this scene first (Ctrl+S), then run this check again.");
        else if (!sceneFirst) { fixScene = true; fixes.Add("Put the scene \"" + scene.name + "\" first in the build list (the app opens the first scene)."); }
        else good.Add("The scene \"" + scene.name + "\" is first in the build list.");

        // 2. Where the keys live. A phone app cannot read a key file next to the project.
        var conn = Object.FindAnyObjectByType<AvatarConnection>();
        if (hasBrain && conn == null)
            byHand.Add("No Avatar Connection on the character. Run Tools > AI Avatar > Set Up Selected Character.");
        else if (conn != null && conn.keyHome == AvatarConnection.KeyHome.KeyFile)
            byHand.Add("Key Home is \"Key File\". A phone app cannot find that file, and a key inside an app is not safe.\n" +
                       "     Set Avatar Connection > Key Home to \"Worker\" and fill in Worker Url and App Password.");
        else if (conn != null && !(conn.workerUrl ?? "").Trim().StartsWith("https://"))
            byHand.Add("Worker Url must start with https://  (Avatar Connection > Worker Url).");
        else if (conn != null && string.IsNullOrEmpty((conn.appPassword ?? "").Trim()))
            byHand.Add("App Password is empty (Avatar Connection > App Password).");
        else if (conn != null) good.Add("Keys are on the Worker, not in the app.");

        // 3. Android: permission to use the internet.
        bool fixNet = !PlayerSettings.Android.forceInternetPermission;
        if (fixNet) fixes.Add("Android: set Internet Access to \"Require\" (she cannot talk without the internet).");
        else good.Add("Android: internet access is required.");

        // 4. Android: 64-bit build. New phones and the Play Store need it.
        bool il2cpp = PlayerSettings.GetScriptingBackend(NamedBuildTarget.Android) == ScriptingImplementation.IL2CPP;
        bool arm64 = (PlayerSettings.Android.targetArchitectures & AndroidArchitecture.ARM64) != 0;
        bool fix64 = !il2cpp || !arm64;
        if (fix64) fixes.Add("Android: set Scripting Backend to IL2CPP and turn on ARM64 (new phones need a 64-bit app).");
        else good.Add("Android: 64-bit build (IL2CPP + ARM64).");

        // 5. iPhone: the sentence the phone shows when it asks for the microphone.
        bool fixMic = string.IsNullOrEmpty(PlayerSettings.iOS.microphoneUsageDescription);
        if (fixMic) fixes.Add("iPhone: add the sentence that the phone shows when it asks for the microphone.");
        else good.Add("iPhone: microphone sentence is set.");

        // 6. The app's own name. Two apps with the same package name cannot be on one phone.
        string company = PlayerSettings.companyName ?? "";
        string idA = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android) ?? "";
        string idI = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.iOS) ?? "";
        if (company == "DefaultCompany" || LooksLikeTemplate(idA) || LooksLikeTemplate(idI))
            byHand.Add("The app still has the Unity template name.\n" +
                       "     Edit > Project Settings > Player: set Company Name and Product Name.\n" +
                       "     Then in Other Settings > Identification, turn off \"Override Default Package Name\"\n" +
                       "     (or type your own, like com.yourname.yourgame). Use small letters, no spaces.");
        else good.Add("App name: " + idA);

        // 7. Are the build parts installed?
        if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
            byHand.Add("Android Build Support is not installed. Unity Hub > Installs > the gear on your Unity version > Add modules > Android Build Support (with OpenJDK and SDK & NDK).");
        else good.Add("Android Build Support is installed.");
        if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.iOS, BuildTarget.iOS))
            byHand.Add("(Only for iPhone) iOS Build Support is not installed. Unity Hub > Installs > gear > Add modules > iOS Build Support.");
        else good.Add("iOS Build Support is installed. (You still need a Mac with Xcode to put the app on an iPhone.)");

        // Ask before changing anything.
        bool didFix = false;
        if (fixes.Count > 0)
        {
            var ask = new StringBuilder("I can fix these for you:\n\n");
            foreach (var f in fixes) ask.AppendLine("  -  " + f);
            didFix = answerYes || EditorUtility.DisplayDialog(Title, ask.ToString(), "Fix them", "Not now");
            if (didFix)
            {
                if (fixScene)
                {
                    list.RemoveAll(s => s.path == scene.path);
                    list.Insert(0, new EditorBuildSettingsScene(scene.path, true));
                    EditorBuildSettings.scenes = list.ToArray();
                }
                if (fixNet) PlayerSettings.Android.forceInternetPermission = true;
                if (fix64)
                {
                    PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
                    PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
                }
                if (fixMic) PlayerSettings.iOS.microphoneUsageDescription = "So you can talk with the character.";
                AssetDatabase.SaveAssets();
            }
        }

        var sb = new StringBuilder();
        if (fixes.Count > 0)
        {
            sb.AppendLine(didFix ? "FIXED FOR YOU:" : "NOT FIXED (you pressed \"Not now\"):");
            foreach (var f in fixes) sb.AppendLine("  " + (didFix ? "OK " : "!  ") + f);
            sb.AppendLine();
        }
        if (byHand.Count > 0)
        {
            sb.AppendLine("YOU MUST DO THESE BY HAND:");
            foreach (var b in byHand) sb.AppendLine("  X  " + b);
            sb.AppendLine();
        }
        sb.AppendLine("ALREADY GOOD:");
        foreach (var g in good) sb.AppendLine("  OK " + g);
        sb.AppendLine();
        sb.AppendLine(byHand.Count == 0 && (fixes.Count == 0 || didFix)
            ? "READY. Next: File > Build Profiles > Android (or iOS) > Switch Platform > Build And Run."
            : "Not ready yet. Do the steps above, then run this check again.");

        Debug.Log("[AvatarKit] Phone build check\n" + sb);
        EditorUtility.DisplayDialog(Title, sb.ToString(), "OK");
    }

    static bool LooksLikeTemplate(string id)
    {
        id = (id ?? "").ToLowerInvariant();
        return id.Length == 0 || id.Contains("unity.template") || id.Contains("unitytechnologies") ||
               id.Contains("unity-technologies") || id.Contains("defaultcompany");
    }
}
