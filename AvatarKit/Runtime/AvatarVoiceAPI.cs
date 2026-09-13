using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// THE THREE CALLS.
///
/// This is the entire "AI" part of an AI avatar: three HTTPS POST requests.
///   ears  -> POST /audio/transcriptions
///   brain -> POST /chat/completions
///   voice -> POST /audio/speech
///            (or ElevenLabs /text-to-speech, if AvatarVoice says so)
///
/// Written as raw web requests rather than using a wrapper package, because
/// the wrapper would hide the one idea worth learning here. When something
/// breaks you can read every line of what was sent.
/// </summary>
public static class AvatarVoiceAPI
{
    const string ChatUrl = "https://api.openai.com/v1/chat/completions";
    const string SttUrl = "https://api.openai.com/v1/audio/transcriptions";
    const string TtsUrl = "https://api.openai.com/v1/audio/speech";
    const string EmbedUrl = "https://api.openai.com/v1/embeddings";

    [Serializable] public class Msg { public string role; public string content; }

    // ================================================================== API key

    public const string KeyFileName = "avatar_key.txt";
    static string _key;

    /// <summary>
    /// The key is read from a loose file, never from a field on a component.
    ///
    /// Why: values typed into components are saved inside the scene file, and
    /// scene files get committed to git. A key in a scene is a key on GitHub.
    ///
    /// TWO accepted homes, checked in this order:
    ///   1. inside the project folder, beside Assets  (put it in .gitignore)
    ///   2. one folder ABOVE the project -- outside the repo entirely, so it
    ///      can never be committed, and one key can serve several projects.
    /// </summary>
    public static string[] KeyPaths { get { return KeyPathsFor(KeyFileName); } }

    /// <summary>The path to show in "create it here" messages: wherever the
    /// file actually is, or the in-project default if it isn't anywhere.</summary>
    public static string KeyPath
    {
        get { return FindKeyFile(KeyFileName) ?? KeyPaths[0]; }
    }

    public static bool HasKey { get { return !string.IsNullOrEmpty(ApiKey); } }

    public static string ApiKey
    {
        get
        {
            if (string.IsNullOrEmpty(_key)) _key = ReadKey(KeyFileName, "OpenAI", 20);
            return _key;
        }
    }

    // ------------------------------------------------ ElevenLabs key (optional)

    /// <summary>Only needed when an AvatarVoice component is set to ElevenLabs.
    /// Same two homes as the OpenAI key.</summary>
    public const string ElevenLabsKeyFileName = "elevenlabs_key.txt";
    static string _elevenLabsKey;

    public static string ElevenLabsKeyPath
    {
        get { return FindKeyFile(ElevenLabsKeyFileName) ?? KeyPathsFor(ElevenLabsKeyFileName)[0]; }
    }

    public static bool HasElevenLabsKey { get { return !string.IsNullOrEmpty(ElevenLabsApiKey); } }

    public static string ElevenLabsApiKey
    {
        get
        {
            if (string.IsNullOrEmpty(_elevenLabsKey))
                _elevenLabsKey = ReadKey(ElevenLabsKeyFileName, "ElevenLabs", 12);
            return _elevenLabsKey;
        }
    }

    // ----------------------------------------------------- shared key plumbing

    /// <summary>The two accepted homes for any key file: beside Assets, or
    /// one folder above the project.</summary>
    public static string[] KeyPathsFor(string fileName)
    {
        return new[] {
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", fileName)),
            Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", fileName)),
        };
    }

    /// <summary>Where the file is, or null. Logs nothing, so the Inspector can
    /// call it every frame.</summary>
    public static string FindKeyFile(string fileName)
    {
        foreach (var p in KeyPathsFor(fileName)) if (File.Exists(p)) return p;
        return null;
    }

    static string ReadKey(string fileName, string who, int minLength)
    {
        try
        {
            string found = FindKeyFile(fileName);
            if (found == null)
            {
                var paths = KeyPathsFor(fileName);
                Debug.LogError("[AvatarKit] No " + who + " key file found.\n" +
                               "Create a plain text file containing nothing but your " +
                               who + " key at:\n  " + paths[0] +
                               "\nor one folder above the project, at:\n  " + paths[1]);
                return null;
            }
            string key = File.ReadAllText(found).Trim();
            if (key.Length < minLength)
            {
                Debug.LogError("[AvatarKit] Key file exists but looks empty: " + found);
                return null;
            }
            return key;
        }
        catch (Exception e) { Debug.LogError("[AvatarKit] Key read failed: " + e.Message); }
        return null;
    }

    // ================================================================= 1. EARS

    public static IEnumerator Transcribe(byte[] wav, string model, string hint,
                                         Action<string> onDone, Action<string> onError)
    {
        var form = new List<IMultipartFormSection>
        {
            new MultipartFormFileSection("file", wav, "speech.wav", "audio/wav"),
            new MultipartFormDataSection("model", model),
        };
        if (!string.IsNullOrEmpty(hint))
            form.Add(new MultipartFormDataSection("prompt", hint));

        using (var req = UnityWebRequest.Post(SttUrl, form))
        {
            req.SetRequestHeader("Authorization", "Bearer " + ApiKey);
            req.timeout = 60;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                onError("Speech-to-text failed (" + req.responseCode + "): " + Peek(req));
                yield break;
            }
            onDone(ReadField(req.downloadHandler.text, "text"));
        }
    }

    // ================================================================ 2. BRAIN

    /// <summary>The conversation call. `extraSystem` is appended to the
    /// persona's system prompt -- this is how long-term memory rides along:
    /// not a database, just more text in the context window.</summary>
    public static IEnumerator Chat(CharacterPersona persona, List<Msg> history,
                                   Action<string> onDone, Action<string> onError,
                                   string extraSystem = null)
    {
        var sb = new StringBuilder();
        sb.Append("{\"model\":\"").Append(persona.chatModel).Append("\",");
        sb.Append("\"temperature\":").Append(persona.temperature.ToString("0.00",
            System.Globalization.CultureInfo.InvariantCulture)).Append(',');
        sb.Append("\"max_tokens\":").Append(persona.maxTokens).Append(',');
        sb.Append("\"messages\":[{\"role\":\"system\",\"content\":\"")
          .Append(Escape(persona.ResolvedPrompt + (extraSystem ?? ""))).Append("\"}");
        foreach (var m in history)
            sb.Append(",{\"role\":\"").Append(m.role).Append("\",\"content\":\"")
              .Append(Escape(m.content)).Append("\"}");
        sb.Append("]}");

        using (var req = new UnityWebRequest(ChatUrl, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(sb.ToString()));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Authorization", "Bearer " + ApiKey);
            req.timeout = 60;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                onError("Chat failed (" + req.responseCode + "): " + Peek(req));
                yield break;
            }
            onDone(ReadField(req.downloadHandler.text, "content"));
        }
    }

    /// <summary>A one-shot completion with its own system prompt -- used by
    /// AvatarMemory's background distil pass and nothing else in the loop.
    /// Low temperature on purpose: note-taking wants accuracy, not flair.</summary>
    public static IEnumerator Complete(string model, string systemPrompt, string userText,
                                       int maxTokens, Action<string> onDone, Action<string> onError)
    {
        var sb = new StringBuilder();
        sb.Append("{\"model\":\"").Append(model).Append("\",");
        sb.Append("\"temperature\":0.30,");
        sb.Append("\"max_tokens\":").Append(maxTokens).Append(',');
        sb.Append("\"messages\":[{\"role\":\"system\",\"content\":\"")
          .Append(Escape(systemPrompt)).Append("\"},");
        sb.Append("{\"role\":\"user\",\"content\":\"").Append(Escape(userText)).Append("\"}]}");

        using (var req = new UnityWebRequest(ChatUrl, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(sb.ToString()));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Authorization", "Bearer " + ApiKey);
            req.timeout = 60;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                onError("Completion failed (" + req.responseCode + "): " + Peek(req));
                yield break;
            }
            onDone(ReadField(req.downloadHandler.text, "content"));
        }
    }

    // ============================================================ 4. LIBRARIAN

    /// <summary>Turns text into ~1500 numbers -- coordinates in meaning-space.
    /// Texts that mean similar things get nearby coordinates; that is the
    /// entire trick that makes retrieval work. Used at index time (per chunk)
    /// and once per question (to find the chunks nearest the question).</summary>
    public static IEnumerator Embed(string text, string model,
                                    Action<float[]> onDone, Action<string> onError)
    {
        var sb = new StringBuilder();
        sb.Append("{\"model\":\"").Append(model).Append("\",");
        sb.Append("\"input\":\"").Append(Escape(text)).Append("\"}");

        using (var req = new UnityWebRequest(EmbedUrl, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(sb.ToString()));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Authorization", "Bearer " + ApiKey);
            req.timeout = 30;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                onError("Embedding failed (" + req.responseCode + "): " + Peek(req));
                yield break;
            }
            onDone(ReadFloatArray(req.downloadHandler.text, "embedding"));
        }
    }

    /// <summary>Public because the editor-side indexer parses the same reply.</summary>
    public static float[] ReadFloatArray(string json, string field)
    {
        if (string.IsNullOrEmpty(json)) return null;
        int i = json.IndexOf("\"" + field + "\"", StringComparison.Ordinal);
        if (i < 0) return null;
        int start = json.IndexOf('[', i) + 1;
        int end = json.IndexOf(']', start);
        if (start <= 0 || end < 0) return null;
        var parts = json.Substring(start, end - start).Split(',');
        var v = new float[parts.Length];
        for (int j = 0; j < parts.Length; j++)
            v[j] = float.Parse(parts[j],
                System.Globalization.CultureInfo.InvariantCulture);
        return v;
    }

    // ================================================================ 3. VOICE

    /// <summary>The Persona's voice. Used when there is no AvatarVoice component.</summary>
    public static IEnumerator Speak(CharacterPersona persona, string text,
                                    Action<AudioClip> onDone, Action<string> onError)
    {
        return SpeakOpenAI(text, persona.ttsVoice, persona.ttsModel, persona.voiceDirection,
                           onDone, onError);
    }

    public static IEnumerator SpeakOpenAI(string text, string voice, string model, string direction,
                                          Action<AudioClip> onDone, Action<string> onError)
    {
        var sb = new StringBuilder();
        sb.Append("{\"model\":\"").Append(model).Append("\",");
        sb.Append("\"voice\":\"").Append(voice).Append("\",");
        sb.Append("\"response_format\":\"wav\",");
        if (!string.IsNullOrEmpty(direction))
            sb.Append("\"instructions\":\"").Append(Escape(direction)).Append("\",");
        sb.Append("\"input\":\"").Append(Escape(text)).Append("\"}");

        using (var req = new UnityWebRequest(TtsUrl, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(sb.ToString()));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Authorization", "Bearer " + ApiKey);
            req.timeout = 90;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                onError("Text-to-speech failed (" + req.responseCode + "): " + Peek(req));
                yield break;
            }
            var clip = AvatarWav.ToAudioClip(req.downloadHandler.data, "Speech");
            if (clip == null) { onError("Could not decode the returned audio."); yield break; }
            onDone(clip);
        }
    }

    /// <summary>
    /// The same job through ElevenLabs, so the avatar can use any voice in your
    /// ElevenLabs "My Voices" -- including a clone of your own voice.
    ///
    /// SHAPE OF THE CALL -- note the header is NOT "Authorization: Bearer":
    ///   POST https://api.elevenlabs.io/v1/text-to-speech/{voice_id}?output_format=wav_22050
    ///   header:  xi-api-key: <key>
    ///   body:    { "text": "...", "model_id": "eleven_multilingual_v2" }
    ///
    /// WAV so Unity can decode it with no extra library. 22.05 kHz because
    /// 44.1 kHz WAV needs a Pro plan, and 22.05 is plenty for speech.
    /// </summary>
    public static IEnumerator SpeakElevenLabs(string text, string voiceId, string model,
                                              bool customSettings, float stability, float similarity,
                                              Action<AudioClip> onDone, Action<string> onError)
    {
        if (string.IsNullOrEmpty(voiceId))
        {
            onError("No ElevenLabs Voice ID. Paste one into the AvatarVoice component.");
            yield break;
        }
        if (!HasElevenLabsKey)
        {
            onError("No ElevenLabs key. Create a text file containing only your key at:\n" +
                    ElevenLabsKeyPath);
            yield break;
        }

        string url = "https://api.elevenlabs.io/v1/text-to-speech/" +
                     UnityWebRequest.EscapeURL(voiceId) + "?output_format=wav_22050";

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("{\"text\":\"").Append(Escape(text)).Append("\",");
        sb.Append("\"model_id\":\"").Append(model).Append("\"");
        if (customSettings)   // otherwise the settings saved on the voice are used
            sb.Append(",\"voice_settings\":{")
              .Append("\"stability\":").Append(Mathf.Clamp01(stability).ToString("0.00", inv)).Append(',')
              .Append("\"similarity_boost\":").Append(Mathf.Clamp01(similarity).ToString("0.00", inv))
              .Append('}');
        sb.Append('}');

        using (var req = new UnityWebRequest(url, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(sb.ToString()));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("xi-api-key", ElevenLabsApiKey);
            req.timeout = 90;
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                string hint = req.responseCode == 401 ? "  (401 = the key is wrong, or lacks Text to Speech permission)"
                            : req.responseCode == 404 ? "  (404 = wrong Voice ID, or the voice is not in My Voices)"
                            : "";
                onError("ElevenLabs text-to-speech failed (" + req.responseCode + ")" + hint + ": " + Peek(req));
                yield break;
            }
            var clip = AvatarWav.ToAudioClip(req.downloadHandler.data, "Speech");
            if (clip == null) { onError("Could not decode the returned audio."); yield break; }
            onDone(clip);
        }
    }

    // =============================================================== plumbing

    static string Peek(UnityWebRequest req)
    {
        try
        {
            var t = req.downloadHandler != null ? req.downloadHandler.text : req.error;
            if (string.IsNullOrEmpty(t)) return req.error;
            return t.Length > 300 ? t.Substring(0, 300) : t;
        }
        catch { return req.error; }
    }

    /// <summary>JSON string escaping. Tiny, but without it a single apostrophe
    /// or newline in a sentence produces a 400 error.</summary>
    public static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length + 16);
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Pulls one string field out of a JSON reply. Unity's JsonUtility
    /// needs a class per shape; this is less code for two fields.</summary>
    static string ReadField(string json, string field)
    {
        if (string.IsNullOrEmpty(json)) return null;
        int i = json.IndexOf("\"" + field + "\"", StringComparison.Ordinal);
        if (i < 0) return null;
        i = json.IndexOf(':', i);
        while (i < json.Length && json[i] != '"') i++;
        var sb = new StringBuilder();
        for (int j = i + 1; j < json.Length; j++)
        {
            char c = json[j];
            if (c == '\\' && j + 1 < json.Length)
            {
                char n = json[++j];
                if (n == 'n') sb.Append('\n');
                else if (n == 'u' && j + 4 < json.Length)
                {
                    int code;
                    if (int.TryParse(json.Substring(j + 1, 4),
                        System.Globalization.NumberStyles.HexNumber, null, out code))
                        sb.Append((char)code);
                    j += 4;
                }
                else if (n != 'r') sb.Append(n);
            }
            else if (c == '"') break;
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
