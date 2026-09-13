using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// The AvatarVoice Inspector: shows only the boxes for the provider you
/// picked, and tells you -- with the exact path -- where the key file goes
/// and whether it is there.
/// </summary>
[CustomEditor(typeof(AvatarVoice))]
public class AvatarVoiceEditor : Editor
{
    SerializedProperty _provider, _openAIVoice, _openAIModel, _direction,
                       _voiceId, _elevenModel, _custom, _stability, _similarity;

    void OnEnable()
    {
        _provider    = serializedObject.FindProperty("provider");
        _openAIVoice = serializedObject.FindProperty("openAIVoice");
        _openAIModel = serializedObject.FindProperty("openAIModel");
        _direction   = serializedObject.FindProperty("voiceDirection");
        _voiceId     = serializedObject.FindProperty("elevenLabsVoiceId");
        _elevenModel = serializedObject.FindProperty("elevenLabsModel");
        _custom      = serializedObject.FindProperty("customVoiceSettings");
        _stability   = serializedObject.FindProperty("stability");
        _similarity  = serializedObject.FindProperty("similarity");
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(_provider);
        EditorGUILayout.Space(4);

        if ((AvatarVoice.Provider)_provider.enumValueIndex == AvatarVoice.Provider.ElevenLabs)
        {
            KeyBox("ElevenLabs", AvatarVoiceAPI.ElevenLabsKeyFileName,
                   "elevenlabs.io -> your profile -> API Keys -> Create. " +
                   "Turn on only \"Text to Speech\".");

            EditorGUILayout.PropertyField(_voiceId, new GUIContent("Voice ID"));
            if (string.IsNullOrWhiteSpace(_voiceId.stringValue))
                EditorGUILayout.HelpBox(
                    "Paste a Voice ID here.\n" +
                    "elevenlabs.io -> Voices -> click the voice -> copy its ID.\n" +
                    "A shared voice must be added to \"My Voices\" first, or you get a 404 error.",
                    MessageType.Warning);
            else if (_voiceId.stringValue.Trim().Length > 30)
                EditorGUILayout.HelpBox(
                    "This ID is too long. It is a Voice Library sharing ID, not a voice ID.\n" +
                    "In the Voice Library, click \"Add to My Voices\". Then go to Voices -> My Voices -> " +
                    "click the voice -> copy its ID (about 20 characters).",
                    MessageType.Error);

            EditorGUILayout.PropertyField(_elevenModel, new GUIContent("Model"));
            EditorGUILayout.PropertyField(_custom, new GUIContent("Custom Voice Settings"));
            if (_custom.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(_stability);
                EditorGUILayout.PropertyField(_similarity);
                EditorGUI.indentLevel--;
            }
        }
        else
        {
            KeyBox("OpenAI", AvatarVoiceAPI.KeyFileName,
                   "This is the same key the brain uses. platform.openai.com -> API keys.");

            EditorGUILayout.PropertyField(_openAIVoice, new GUIContent("Voice"));
            EditorGUILayout.PropertyField(_openAIModel, new GUIContent("Model"));
            EditorGUILayout.PropertyField(_direction, new GUIContent("Voice Direction"));
        }

        serializedObject.ApplyModifiedProperties();
    }

    /// <summary>Where the key goes, whether it is there, and two buttons.</summary>
    static void KeyBox(string who, string fileName, string whereToGetIt)
    {
        string found = AvatarVoiceAPI.FindKeyFile(fileName);
        string[] homes = AvatarVoiceAPI.KeyPathsFor(fileName);
        bool empty = found != null && SafeRead(found).Trim().Length < 12;

        string status = found == null ? "NOT FOUND"
                      : empty         ? "FOUND, BUT EMPTY -- paste your key into it and save"
                                      : "found";

        EditorGUILayout.HelpBox(
            who + " API key -- " + status + "\n\n" +
            "Make a plain text file named  " + fileName + "\n" +
            "with nothing in it but your " + who + " key. Put it here:\n" +
            "   " + homes[0] + "\n" +
            "or one folder above the project:\n" +
            "   " + homes[1] + "\n\n" +
            "Never type the key into this component: components are saved into the " +
            "scene, and scenes go to GitHub. Add " + fileName + " to your .gitignore.\n\n" +
            "Get a key: " + whereToGetIt,
            found != null && !empty ? MessageType.Info : MessageType.Error);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (found == null && GUILayout.Button("Make the key file for me"))
            {
                File.WriteAllText(homes[0], "");
                EditorUtility.OpenWithDefaultApp(homes[0]);   // paste the key, save, close
            }
            else if (found != null && empty && GUILayout.Button("Open the key file"))
                EditorUtility.OpenWithDefaultApp(found);

            if (GUILayout.Button("Show the folder"))
                EditorUtility.RevealInFinder(found ?? Application.dataPath);
        }
        EditorGUILayout.Space(6);
    }

    static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }
}
