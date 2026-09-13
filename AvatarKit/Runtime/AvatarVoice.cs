using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// HER VOICE -- pick who does the talking.
///
/// Put this on your character next to AvatarBrain (the setup command adds it).
/// Then, in the Inspector:
///   1. Pick a Provider from the dropdown.
///   2. OpenAI:     pick a voice from the list. Uses your normal avatar_key.txt.
///      ElevenLabs: paste a Voice ID. Any voice in your ElevenLabs "My Voices"
///                  works -- a stock voice, a shared one, or your own clone.
///                  Needs its own key file, elevenlabs_key.txt.
///   3. The Inspector tells you exactly where the key file goes, and whether
///      it found it.
///
/// The words still come from the brain (OpenAI chat). Only the SOUND changes.
///
/// ADDING ANOTHER PROVIDER (e.g. a self-hosted voice):
///   a. add a name to the Provider enum below,
///   b. add its fields here and one `case` in Speak() and Problem(),
///   c. add its web request + key file to AvatarVoiceAPI (copy the ElevenLabs one).
/// </summary>
public class AvatarVoice : MonoBehaviour
{
    public enum Provider { OpenAI, ElevenLabs }

    /// <summary>Lower-case on purpose: each name is sent to OpenAI exactly as written.</summary>
    public enum OpenAIVoice { alloy, ash, ballad, coral, echo, fable, nova, onyx, sage, shimmer, verse }

    [Tooltip("Who makes the sound of her voice.")]
    public Provider provider = Provider.OpenAI;

    [Header("OpenAI")]
    public OpenAIVoice openAIVoice = OpenAIVoice.sage;
    public string openAIModel = "gpt-4o-mini-tts";
    [Tooltip("An acting note for HOW the line is said, not the words. OpenAI only.")]
    [TextArea(2, 4)]
    public string voiceDirection = "Speak at a natural, relaxed pace.";

    [Header("ElevenLabs")]
    [Tooltip("elevenlabs.io -> Voices -> click the voice -> copy its ID. " +
             "A shared voice must be added to My Voices first.")]
    public string elevenLabsVoiceId = "";
    [Tooltip("eleven_multilingual_v2 = best quality. eleven_flash_v2_5 = much faster, a little less natural.")]
    public string elevenLabsModel = "eleven_multilingual_v2";
    [Tooltip("Off = use the settings saved on the voice in ElevenLabs (recommended).")]
    public bool customVoiceSettings = false;
    [Range(0f, 1f)]
    [Tooltip("Lower = more expressive and varied. Higher = steadier, flatter.")]
    public float stability = 0.5f;
    [Range(0f, 1f)]
    [Tooltip("How closely to stick to the original voice.")]
    public float similarity = 0.75f;

    /// <summary>Speak one line with the chosen provider.</summary>
    public IEnumerator Speak(string text, Action<AudioClip> onDone, Action<string> onError)
    {
        switch (provider)
        {
            case Provider.ElevenLabs:
                return AvatarVoiceAPI.SpeakElevenLabs(text, VoiceId, elevenLabsModel,
                    customVoiceSettings, stability, similarity, onDone, onError);
            default:
                return AvatarVoiceAPI.SpeakOpenAI(text, openAIVoice.ToString(), openAIModel,
                    voiceDirection, onDone, onError);
        }
    }

    /// <summary>What is missing, in plain words with the fix -- or null if
    /// ready. Checks files only, so it never spams the Console.</summary>
    public string Problem()
    {
        switch (provider)
        {
            case Provider.ElevenLabs:
                if (string.IsNullOrEmpty(VoiceId))
                    return "AvatarVoice is set to ElevenLabs, but the Voice ID box is empty.\n" +
                           "Fix: elevenlabs.io -> Voices -> click the voice -> copy its ID -> paste it into AvatarVoice.";
                // Real voice IDs are about 20 letters and numbers. A 64-character
                // code is the Voice Library's sharing ID, which the API rejects.
                if (VoiceId.Length > 30)
                    return "That Voice ID is too long (" + VoiceId.Length + " characters). It is a Voice Library " +
                           "sharing ID, not a voice ID.\n" +
                           "Fix: in the Voice Library, click \"Add to My Voices\". Then go to Voices -> My Voices -> " +
                           "click the voice -> copy its ID (about 20 characters).";
                if (AvatarVoiceAPI.FindKeyFile(AvatarVoiceAPI.ElevenLabsKeyFileName) == null)
                    return "No ElevenLabs key file.\n" +
                           "Fix: create a text file containing only your ElevenLabs key at:\n" +
                           AvatarVoiceAPI.ElevenLabsKeyPath;
                return null;
            default:
                return null;   // OpenAI uses the same key as the brain, checked by AvatarBrain
        }
    }

    /// <summary>One line for the Health Check.</summary>
    public string Summary
    {
        get
        {
            return provider == Provider.ElevenLabs
                ? "ElevenLabs, voice ID " + VoiceId + " (" + elevenLabsModel + ")"
                : "OpenAI, '" + openAIVoice + "' (" + openAIModel + ")";
        }
    }

    /// <summary>Carry an older Persona's voice over, so adding this component
    /// changes nothing until you change it.</summary>
    public void CopyFromPersona(CharacterPersona persona)
    {
        if (persona == null) return;
        OpenAIVoice v;
        if (Enum.TryParse(persona.ttsVoice, true, out v)) openAIVoice = v;
        if (!string.IsNullOrEmpty(persona.ttsModel)) openAIModel = persona.ttsModel;
        voiceDirection = persona.voiceDirection;
    }

    /// <summary>Pasted IDs often carry a space or a line break.</summary>
    string VoiceId { get { return (elevenLabsVoiceId ?? "").Trim(); } }
}
