using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// WHERE THE KEYS LIVE, AND WHICH LINKS THE APP CALLS.  (new in AvatarKit 2)
///
/// AvatarKit 1 read your API key from a text file beside the project. That
/// works in the Editor and NOWHERE ELSE: a phone or a web page has no such
/// file. And if you pack the key INTO the app, anyone can pull it out again.
///
/// The fix is a small server of your own (a free Cloudflare "Worker") that
/// holds the keys. The app calls the Worker; the Worker adds the key and
/// passes the call on. The key never leaves the server.
///
///      app  --(app password)-->  your Worker  --(real key)-->  OpenAI / ElevenLabs
///
/// Put this component on your character (the setup command adds it), then:
///   Key Home = Worker    paste your Worker address and app password. Works
///                        in the Editor, on Android, on iPhone and on the web.
///   Key Home = Key File  the AvatarKit 1 way. Editor only. Good for lesson 1.
///
/// Every link the kit calls is a box below. Change a box and the kit calls a
/// different service -- no code. Any service that speaks the "OpenAI format"
/// (Gemini, Groq, OpenRouter, a model on your own computer) can be the brain.
/// </summary>
[DisallowMultipleComponent]
public class AvatarConnection : MonoBehaviour
{
    public enum KeyHome { KeyFile, Worker }
    public enum Link { Chat, SpeechToText, TextToSpeech, Embeddings, LiveToken }

    [Header("WHERE THE KEYS LIVE")]
    [Tooltip("Worker = keys on your own small server. Needed for phone and web builds.\n" +
             "Key File = a text file beside the project. Editor only.")]
    public KeyHome keyHome = KeyHome.Worker;

    [Tooltip("Your Worker address, for example https://avatar-proxy.yourname.workers.dev")]
    public string workerUrl = "";

    [Tooltip("The app password you saved on the Worker as APP_KEY. It is NOT an API key: " +
             "it only stops strangers who find your Worker address from using it.")]
    public string appPassword = "";

    [Header("LINKS -- the brain, ears and voice (OpenAI format)")]
    [Tooltip("Key File mode: the service to call. Change this to use another OpenAI-format service.")]
    public string openAIBase = "https://api.openai.com";
    [Tooltip("Worker mode: what the Worker calls this service. Must match the Worker code.")]
    public string workerOpenAIPrefix = "/openai";
    [Tooltip("The brain (text in, text out).")]
    public string chatPath = "/v1/chat/completions";
    [Tooltip("The ears (sound in, text out).")]
    public string speechToTextPath = "/v1/audio/transcriptions";
    [Tooltip("The voice (text in, sound out).")]
    public string textToSpeechPath = "/v1/audio/speech";
    [Tooltip("The librarian (turns text into numbers for the knowledge notes).")]
    public string embeddingsPath = "/v1/embeddings";
    [Tooltip("Live talk: where the app gets a one-time pass for the live line.")]
    public string liveTokenPath = "/v1/realtime/client_secrets";
    [Tooltip("Live talk: the live line itself. The app opens this directly, with the one-time pass.")]
    public string liveSocketUrl = "wss://api.openai.com/v1/realtime";

    [Header("LINKS -- ElevenLabs (cloned voices)")]
    public string elevenLabsBase = "https://api.elevenlabs.io";
    public string workerElevenLabsPrefix = "/eleven";
    public string elevenTextToSpeechPath = "/v1/text-to-speech/";
    [Tooltip("Live talk with an ElevenLabs agent: where the app gets its one-time address.")]
    public string elevenSignedUrlPath = "/v1/convai/conversation/get-signed-url";

    [Header("Timeouts (seconds)")]
    public int chatTimeout = 60;
    public int speechTimeout = 90;

    static AvatarConnection _current;

    /// <summary>The connection in the scene, or null (then the kit behaves
    /// exactly like AvatarKit 1: key file, OpenAI links).</summary>
    public static AvatarConnection Current
    {
        get
        {
            if (_current == null) _current = FindAnyObjectByType<AvatarConnection>();
            return _current;
        }
    }

    void Awake() { _current = this; }

    public bool UseWorker
    {
        get { return keyHome == KeyHome.Worker && !string.IsNullOrWhiteSpace(workerUrl); }
    }

    string WorkerRoot { get { return (workerUrl ?? "").Trim().TrimEnd('/'); } }

    // ------------------------------------------------------------- the links

    public static string OpenAIUrl(Link link)
    {
        var c = Current;
        if (c == null)
        {
            switch (link)
            {
                case Link.SpeechToText: return "https://api.openai.com/v1/audio/transcriptions";
                case Link.TextToSpeech: return "https://api.openai.com/v1/audio/speech";
                case Link.Embeddings: return "https://api.openai.com/v1/embeddings";
                case Link.LiveToken: return "https://api.openai.com/v1/realtime/client_secrets";
                default: return "https://api.openai.com/v1/chat/completions";
            }
        }
        string path = link == Link.SpeechToText ? c.speechToTextPath
                    : link == Link.TextToSpeech ? c.textToSpeechPath
                    : link == Link.Embeddings ? c.embeddingsPath
                    : link == Link.LiveToken ? c.liveTokenPath
                    : c.chatPath;
        return c.UseWorker ? c.WorkerRoot + c.workerOpenAIPrefix + path
                           : c.openAIBase.TrimEnd('/') + path;
    }

    public static string ElevenUrl(string path)
    {
        var c = Current;
        if (c == null) return "https://api.elevenlabs.io" + path;
        return c.UseWorker ? c.WorkerRoot + c.workerElevenLabsPrefix + path
                           : c.elevenLabsBase.TrimEnd('/') + path;
    }

    public static string ElevenTtsPath { get { var c = Current; return c != null ? c.elevenTextToSpeechPath : "/v1/text-to-speech/"; } }
    public static string ElevenSignedUrlPath { get { var c = Current; return c != null ? c.elevenSignedUrlPath : "/v1/convai/conversation/get-signed-url"; } }
    public static string LiveSocketUrl { get { var c = Current; return c != null ? c.liveSocketUrl : "wss://api.openai.com/v1/realtime"; } }
    public static int ChatTimeout { get { var c = Current; return c != null ? Mathf.Max(5, c.chatTimeout) : 60; } }
    public static int SpeechTimeout { get { var c = Current; return c != null ? Mathf.Max(5, c.speechTimeout) : 90; } }
    public static bool OnWorker { get { var c = Current; return c != null && c.UseWorker; } }

    // ------------------------------------------------------- who is asking

    /// <summary>Adds the proof to a request: the app password (Worker), or
    /// the real key (Key File).</summary>
    public static void SignOpenAI(UnityWebRequest req)
    {
        var c = Current;
        if (c != null && c.UseWorker) req.SetRequestHeader("x-app-key", (c.appPassword ?? "").Trim());
        else req.SetRequestHeader("Authorization", "Bearer " + AvatarVoiceAPI.ApiKey);
    }

    public static void SignElevenLabs(UnityWebRequest req)
    {
        var c = Current;
        if (c != null && c.UseWorker) req.SetRequestHeader("x-app-key", (c.appPassword ?? "").Trim());
        else req.SetRequestHeader("xi-api-key", AvatarVoiceAPI.ElevenLabsApiKey);
    }

    /// <summary>What is missing, in plain words with the fix -- or null if ready.</summary>
    public static string Problem()
    {
        var c = Current;
        bool keyFile = c == null || c.keyHome == KeyHome.KeyFile;
        if (keyFile)
        {
#if !UNITY_EDITOR
            return "This app is set to read its API key from a file, and that only works in the Unity Editor.\n" +
                   "Fix: on the AvatarConnection component set Key Home to Worker, and fill in the Worker address.";
#else
            if (!AvatarVoiceAPI.HasKey)
                return "No API key. Create a text file containing your key at:\n" + AvatarVoiceAPI.KeyPath +
                       "\n(or add an AvatarConnection component and use a Worker instead)";
            return null;
#endif
        }
        if (string.IsNullOrWhiteSpace(c.workerUrl))
            return "AvatarConnection is set to Worker, but the Worker Url box is empty.\n" +
                   "Fix: paste your Worker address, for example https://avatar-proxy.yourname.workers.dev";
        if (!c.workerUrl.Trim().StartsWith("https://"))
            return "The Worker Url must start with https://";
        if (string.IsNullOrWhiteSpace(c.appPassword))
            return "AvatarConnection has no App Password.\n" +
                   "Fix: paste the password you saved on the Worker as APP_KEY.";
        return null;
    }

    /// <summary>One line for the Health Check.</summary>
    public string Summary
    {
        get
        {
            return UseWorker ? "Worker at " + WorkerRoot + " (keys stay on the server)"
                             : "Key file beside the project (Editor only)";
        }
    }
}

/// <summary>
/// WHICH KEY IS IN USE RIGHT NOW.  (new in AvatarKit 2)
/// Every call the kit makes leaves a one-line note here, and AvatarBrain can
/// print the notes in a corner of the screen. It makes the invisible part of
/// an AI avatar -- which service did what, and when -- visible.
/// </summary>
public static class AvatarTicker
{
    public struct Entry { public string who, what; public float at; }

    static readonly List<Entry> _recent = new List<Entry>();
    public static List<Entry> Recent { get { return _recent; } }

    /// <summary>A line that stays up while a live line is open. null = none.</summary>
    public static string Live;

    public static void Note(string who, string what)
    {
        _recent.Add(new Entry { who = who, what = what, at = Time.realtimeSinceStartup });
        while (_recent.Count > 6) _recent.RemoveAt(0);
    }

    /// <summary>"OpenAI key", or "OpenAI key (on the Worker)".</summary>
    public static string Via(string service)
    {
        return AvatarConnection.OnWorker ? service + " key (on the Worker)" : service + " key (from the key file)";
    }
}
