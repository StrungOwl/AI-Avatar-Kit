using UnityEngine;

/// <summary>
/// YOUR CHARACTER, AS AN ASSET.
///
/// Everything that makes the avatar *your* character lives here, not in code.
/// Right-click in the Project window -> Create -> AI Avatar -> Character Persona,
/// fill it in, and drag it onto the AvatarBrain component.
///
/// You should never have to open a .cs file to change who your character is.
/// If you find yourself wanting to, tell your teacher -- that's a bug in this kit.
/// </summary>
[CreateAssetMenu(fileName = "NewPersona", menuName = "AI Avatar/Character Persona")]
public class CharacterPersona : ScriptableObject
{
    [Header("Who they are")]
    [Tooltip("Say it out loud. The text-to-speech will eventually say it too.")]
    public string characterName = "Unnamed";

    [Tooltip("The first thing they say when you press Play. Keep it short.")]
    [TextArea(2, 4)]
    public string greeting = "Hey. Good to see you.";

    [Header("The system prompt")]
    [Tooltip("The standing instructions sent before EVERY message. This is the " +
             "character. Write it from your design sheet -- if a line here can't " +
             "be traced back to a row on that sheet, ask why it's here.")]
    [TextArea(12, 40)]
    public string systemPrompt =
        "You are [NAME]. You are a 3D character standing in a room talking with the person who built you. " +
        "If they ask whether you're an avatar or an AI, say so plainly -- it isn't a big deal to you.\n\n" +
        "HOW YOU TALK\n" +
        "[Describe pace, sentence length, warmth. Be specific: \"short sentences, comfortable with " +
        "silence\" beats \"friendly\".]\n\n" +
        "WHAT YOU CARE ABOUT\n" +
        "[One or two things. Give them one strong, harmless opinion they'll defend.]\n\n" +
        "RULES (do not remove these -- they exist because the reply is SPOKEN ALOUD)\n" +
        "Keep replies short: 1 to 3 sentences, typically. Say the thing, then stop.\n" +
        "Never use bullet points, headers, markdown, or emoji -- text-to-speech reads them as garbage.\n" +
        "Never say \"As an AI...\" or anything like it.\n" +
        "Never offer to help like a customer service agent.\n" +
        "Don't end every message with a question. Most of the time, just stop talking.";

    [Header("Voice (used only if there is no AvatarVoice component)")]
    [Tooltip("OpenAI voices: alloy, ash, ballad, coral, echo, fable, nova, onyx, sage, shimmer, verse. " +
             "To use ElevenLabs or a cloned voice, add an AvatarVoice component instead.")]
    public string ttsVoice = "sage";

    [Tooltip("Free-text direction for HOW the line is delivered. This is not the " +
             "words -- it's the acting note. Used only if there is no AvatarVoice component.")]
    [TextArea(2, 4)]
    public string voiceDirection = "Speak at a natural, relaxed pace.";

    [Header("Knowledge (RAG)")]
    [Tooltip("Answer factual questions from Assets/Knowledge (after Tools > " +
             "AI Avatar > Build Knowledge Index). THE demo toggle: flip it " +
             "off, ask about your facts, watch her guess; flip it on, same " +
             "question, watch her cite. No index = quietly off.")]
    public bool useKnowledge = true;

    [Header("Model settings")]
    public string chatModel = "gpt-4o-mini";
    public string sttModel = "whisper-1";
    public string ttsModel = "gpt-4o-mini-tts";

    [Range(0f, 1.4f)]
    [Tooltip("Higher = more surprising and varied. 0.8-0.9 suits most characters.")]
    public float temperature = 0.85f;

    [Range(40, 400)]
    [Tooltip("Hard cap on reply length. Low numbers keep her from monologuing.")]
    public int maxTokens = 160;

    [Range(2, 40)]
    [Tooltip("How many past turns to remember. Longer costs more per message and " +
             "eventually drags the character off-persona.")]
    public int memoryTurns = 16;

    /// <summary>Substitutes [NAME] so the prompt template stays readable.</summary>
    public string ResolvedPrompt
    {
        get
        {
            var p = string.IsNullOrEmpty(systemPrompt) ? "" : systemPrompt;
            return p.Replace("[NAME]", string.IsNullOrEmpty(characterName) ? "the character" : characterName);
        }
    }
}
