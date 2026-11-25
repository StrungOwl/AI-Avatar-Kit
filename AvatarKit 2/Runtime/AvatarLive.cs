using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// LIVE TALK (speech-to-speech).  (new in AvatarKit 2)
///
/// The three-call way is: record -> ears -> brain -> voice -> play. Three
/// trips to the internet for every turn, so there is always a wait.
///
/// Live talk is ONE open line (a "WebSocket"). Your microphone sound goes up
/// the line all the time, and her voice comes back down as she makes it. One
/// model hears, thinks and speaks. It is much faster, and you can talk over
/// her to cut in.
///
///   Provider = OpenAI            the OpenAI Realtime model. Uses your OpenAI
///                                key. Preset voices (marin, cedar...).
///   Provider = ElevenLabsAgent   an "agent" you make on elevenlabs.io. Use
///                                this for a CLONED voice. The agent's brain
///                                (Gemini, GPT, Claude...) is picked on the
///                                ElevenLabs website, not here.
///
/// AvatarBrain uses this component when its Talk Engine is set to Live.
///
/// IN A WEB BROWSER the same thing happens, but a browser does not let Unity
/// open the line, read the microphone while it records, or stream sound. So
/// there the file Plugins/WebGL/AvatarLiveWeb.jslib does those three jobs
/// with the browser's own tools, and this script only reads and writes the
/// messages. Look for "Web" below to see each hand-over.
/// </summary>
[DisallowMultipleComponent]
public class AvatarLive : MonoBehaviour
{
    public enum Provider { OpenAI, ElevenLabsAgent }
    public enum TurnStyle { Pause, Meaning }
    public enum Eagerness { Low, Medium, High, Auto }

    [Header("WHO RUNS THE LIVE LINE")]
    public Provider provider = Provider.OpenAI;

    [Header("OpenAI")]
    [Tooltip("The live model. gpt-realtime-2.1 is the best one; gpt-realtime-2.1-mini costs less.")]
    public string openAIModel = "gpt-realtime-2.1";
    [Tooltip("Her live voice: marin, cedar, alloy, ash, ballad, coral, echo, sage, shimmer, verse.")]
    public string openAIVoice = "marin";
    [Tooltip("The model that writes down what YOU said (for the on-screen text and her memory).")]
    public string transcribeModel = "gpt-4o-mini-transcribe";
    [Tooltip("How she knows you have finished.\nPause = you stopped making sound.\nMeaning = your sentence sounds finished.")]
    public TurnStyle turnStyle = TurnStyle.Pause;
    [Tooltip("Pause style: how long a silence ends your turn, in milliseconds. Lower = she answers sooner, but may cut you off.")]
    [Range(200, 1500)] public int pauseMilliseconds = 500;
    [Tooltip("Meaning style: how keen she is to answer.")]
    public Eagerness eagerness = Eagerness.Auto;

    [Header("KNOWLEDGE NOTES in live talk")]
    [Tooltip("ON: she can look in your knowledge notes, but ONLY when she needs to. She asks the app, " +
             "the app finds the notes, and she answers from them. Small talk costs nothing extra.\n" +
             "OpenAI: nothing more to do.\n" +
             "ElevenLabs agent: do this ONE time: Tools > AI Avatar > Add Notes And Actions To My ElevenLabs Agent. " +
             "Or by hand on elevenlabs.io -> your agent -> Tools -> Add tool -> Client tool. " +
             "Name: look_in_notes. One text parameter named: question. Switch ON 'Wait for response'. " +
             "Then the agent reads the files in THIS project, not files on the website.")]
    public bool lookInNotes = true;
    [Tooltip("Tells her WHEN to look. Name your topics here and she will look at the right times. " +
             "(ElevenLabs agent: type these words into the tool's Description on the website instead.)")]
    [TextArea(2, 5)]
    public string whenToLook =
        "Look up facts in your notes. Call this when the person asks a factual question that your notes " +
        "may cover. Do not call it for greetings or small talk.";
    [Tooltip("How many notes to hand her for each lookup.")]
    [Range(1, 8)] public int notesPerLookup = 3;

    [Header("ElevenLabs agent (for a cloned voice)")]
    [Tooltip("elevenlabs.io -> Agents -> your agent -> copy the Agent ID. The agent must be published.")]
    public string elevenLabsAgentId = "";
    [Tooltip("ON: this kit's Persona prompt replaces the prompt on the ElevenLabs website " +
             "(switch on 'System prompt' and 'First message' overrides in the agent's Security tab).\n" +
             "OFF: the agent uses the prompt you typed on the website.")]
    public bool sendPersonaToAgent = true;
    [Tooltip("Only a label for the on-screen read-out: which brain you picked on the ElevenLabs website.")]
    public string agentBrainLabel = "the agent's brain";

    [Header("CUTTING IN (talk over her and she stops)")]
    public bool cutInByVoice = true;
    [Tooltip("How much louder than her own echo you must be. Lower = easier to cut in, but she may stop herself.\n" +
             "Computer with speakers: about 1.6. Phone speaker: try 6 to 15, because the phone's speaker " +
             "is right next to its microphone. You can always press the talk button to cut in.")]
    [Range(1.2f, 15f)] public float cutInLoudness = 1.6f;
    [Tooltip("How long you must talk before she stops, in seconds.")]
    [Range(0.05f, 0.6f)] public float cutInAfter = 0.15f;
    [Tooltip("The quietest sound that can count as you. Raise it in a noisy room.")]
    [Range(0.005f, 0.2f)] public float quietestVoice = 0.036f;
    [Tooltip("ON only with headphones: the microphone stays open while she speaks, and the service decides " +
             "when you cut in. With speakers she would hear herself and answer herself.")]
    public bool wearingHeadphones = false;
    [Tooltip("Seconds the microphone stays shut after she stops, so the tail of her voice is not heard as you.")]
    [Range(0f, 1f)] public float micShutAfterShe = 0.15f;

    [Header("Debug")]
    [Tooltip("Print the cut-in numbers in the Console once a second while she speaks.")]
    public bool logCutInMeter = false;

    // ---- set by AvatarBrain
    [HideInInspector] public AudioSource voice;
    [HideInInspector] public string micDevice;

    public Action<string> OnUserTranscript;
    public Action<string> OnAgentResponse;
    public Action<string> OnClosed;
    public Action OnCutIn;
    /// <summary>She wants to look in the notes. Arguments: the call's id, her question.
    /// Answer with SendNotes(id, text).</summary>
    public Action<string, string> OnLookInNotes;
    /// <summary>Set by AvatarBrain: is there a notes index to look in?</summary>
    [HideInInspector] public bool notesReady;
    /// <summary>Set by AvatarBrain: the AvatarActions list, written for the live line.</summary>
    [HideInInspector] public string actionsJson;
    /// <summary>She asked for an action. Arguments: the call's id, the action's
    /// name, its text as JSON. Answer with SendNotes(id, whatToTellHer).</summary>
    public Action<string, string, string> OnAction;

    public bool Connected { get; private set; }
    public float Uptime { get { return Connected ? Time.realtimeSinceStartup - _connectedAt : 0f; } }
    public float MicRms { get; private set; }
    public bool SheIsSpeaking
    {
        get
        {
            if (Web) return AvatarLiveWeb_IsPlaying() == 1;
            return Volatile.Read(ref _queued) > 0 || Time.time - _lastHadAudio < 0.2f;
        }
    }

    /// <summary>Live talk runs everywhere: Editor, computer, phone and web.</summary>
    public static bool Supported { get { return true; } }

    // ---- the web hand-over (see Plugins/WebGL/AvatarLiveWeb.jslib)
#if UNITY_WEBGL && !UNITY_EDITOR
    static readonly bool Web = true;
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern void AvatarLiveWeb_Open(string url, string protocols, string go, int kind);
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern void AvatarLiveWeb_Send(string text);
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern void AvatarLiveWeb_StartAudio(int rateIn, int rateOut, string prefix, string suffix);
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern void AvatarLiveWeb_Clear(int drop);
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern int AvatarLiveWeb_IsPlaying();
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern float AvatarLiveWeb_Level();
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern float AvatarLiveWeb_MicLevel();
    [System.Runtime.InteropServices.DllImport("__Internal")] static extern void AvatarLiveWeb_Close();
#else
    static readonly bool Web = false;
    static void AvatarLiveWeb_Open(string url, string protocols, string go, int kind) { }
    static void AvatarLiveWeb_Send(string text) { }
    static void AvatarLiveWeb_StartAudio(int rateIn, int rateOut, string prefix, string suffix) { }
    static void AvatarLiveWeb_Clear(int drop) { }
    static int AvatarLiveWeb_IsPlaying() { return 0; }
    static float AvatarLiveWeb_Level() { return 0f; }
    static float AvatarLiveWeb_MicLevel() { return 0f; }
    static void AvatarLiveWeb_Close() { }
#endif
    bool _webOpen;
    AvatarLipSync _lip;

    // The browser calls these four (by name) when something happens on the line.
    public void WebOnOpen(string nothing) { _webOpen = true; }
    public void WebOnMessage(string message) { _in.Enqueue(message); }
    public void WebOnClosed(string why) { _closeReason = why; _closed = true; }
    public void WebOnMicProblem(string why) { Debug.LogError("[AvatarKit] live talk: " + why); }

    /// <summary>What is missing, in plain words -- or null if ready.</summary>
    public string Problem()
    {
        if (provider == Provider.ElevenLabsAgent)
        {
            if (string.IsNullOrWhiteSpace(elevenLabsAgentId))
                return "AvatarLive is set to ElevenLabs Agent, but the Agent ID box is empty.\n" +
                       "Fix: elevenlabs.io -> Agents -> your agent -> copy the Agent ID -> paste it into AvatarLive.";
            if (!AvatarConnection.OnWorker)
                return "Live talk with an ElevenLabs agent needs the Worker.\n" +
                       "Fix: on AvatarConnection set Key Home to Worker.";
        }
        return null;
    }

    public string Summary
    {
        get
        {
            return provider == Provider.OpenAI
                ? "OpenAI live (" + openAIModel + ", voice '" + openAIVoice + "')"
                : "ElevenLabs agent " + elevenLabsAgentId.Trim();
        }
    }

    // ---- the line
    ClientWebSocket _ws;
    CancellationTokenSource _cts;
    readonly ConcurrentQueue<string> _in = new ConcurrentQueue<string>();
    readonly ConcurrentQueue<string> _out = new ConcurrentQueue<string>();
    volatile bool _closed;
    volatile string _closeReason;
    float _connectedAt;
    Provider _open;                 // the provider of the line that is open now

    // ---- mic (up)
    AudioClip _micClip;
    int _micLen, _micRead, _rateIn = 24000;
    float[] _micBlock;
    byte[] _micBytes;
    string _silence;
    readonly Queue<string> _preRoll = new Queue<string>();
    float _echoPeak, _cutTime, _speakingSince = -1f, _meterAt;

    // ---- voice (down)
    readonly object _playLock = new object();
    readonly Queue<float[]> _chunks = new Queue<float[]>();
    int _chunkPos, _queued, _rateOut = 24000;
    float _lastHadAudio = -999f, _sheStoppedAt = -999f;
    int _dropUpToEventId = -1;
    bool _manualDrop;

    // ---- messages we read (only the fields we need)
    [Serializable] class Signed { public string signed_url; }
    [Serializable] class Token { public string value; }
    [Serializable] class OaErr { public string message, code; }
    [Serializable] class OaEvt { public string type, delta, transcript, call_id, name, arguments; public OaErr error; }
    [Serializable] class LookArgs { public string question; }
    [Serializable] class MetaEvt { public string agent_output_audio_format, user_input_audio_format; }
    [Serializable] class AudioEvt { public string audio_base_64; public int event_id; }
    [Serializable] class UserEvt { public string user_transcript; }
    [Serializable] class AgentEvt { public string agent_response; }
    [Serializable] class IdEvt { public int event_id; }
    [Serializable] class ToolCall { public string tool_name, tool_call_id; public LookArgs parameters; }
    [Serializable] class ElEvt
    {
        public string type;
        public MetaEvt conversation_initiation_metadata_event;
        public AudioEvt audio_event;
        public UserEvt user_transcription_event;
        public AgentEvt agent_response_event;
        public IdEvt interruption_event;
        public IdEvt ping_event;
        public ToolCall client_tool_call;
    }

    // ------------------------------------------------------------- connecting

    /// <param name="systemPrompt">Her whole persona (+ memory).</param>
    /// <param name="firstMessage">What she says first. "" = she waits for you.</param>
    public IEnumerator Connect(string systemPrompt, string firstMessage, Action<string> onError)
    {
        Disconnect();
        if (!Supported) { onError("Live talk does not run in a web browser."); yield break; }
        string problem = Problem();
        if (problem != null) { onError(problem); yield break; }

        _open = provider;
        string address = null, bearer = null;

        // 1. A ONE-TIME PASS. The real key must not travel inside the app, so
        //    the app asks (through the Worker) for a pass that works once,
        //    for a few minutes, for this one line.
        if (_open == Provider.ElevenLabsAgent)
        {
            string url = AvatarConnection.ElevenUrl(AvatarConnection.ElevenSignedUrlPath) +
                         "?agent_id=" + UnityWebRequest.EscapeURL(elevenLabsAgentId.Trim());
            AvatarTicker.Note(AvatarTicker.Via("ElevenLabs"), "one-time pass for the live line");
            using (var req = UnityWebRequest.Get(url))
            {
                AvatarConnection.SignElevenLabs(req);
                req.timeout = 30;
                yield return req.SendWebRequest();
                string body = req.downloadHandler != null ? req.downloadHandler.text : "";
                if (req.result != UnityWebRequest.Result.Success)
                {
                    onError("Live talk: no pass from ElevenLabs (" + req.responseCode + "): " + Short(body, req.error));
                    yield break;
                }
                try { address = JsonUtility.FromJson<Signed>(body).signed_url; } catch { }
            }
            if (string.IsNullOrEmpty(address)) { onError("Live talk: the Worker answered, but with no signed_url."); yield break; }
        }
        else
        {
            address = AvatarConnection.LiveSocketUrl + "?model=" + UnityWebRequest.EscapeURL(openAIModel.Trim());
            if (AvatarConnection.OnWorker)
            {
                string tokenBody = "{\"session\":{\"type\":\"realtime\",\"model\":\"" + AvatarVoiceAPI.Escape(openAIModel.Trim()) + "\"}}";
                AvatarTicker.Note(AvatarTicker.Via("OpenAI"), "one-time pass for the live line");
                using (var req = new UnityWebRequest(AvatarConnection.OpenAIUrl(AvatarConnection.Link.LiveToken), "POST"))
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(tokenBody));
                    req.downloadHandler = new DownloadHandlerBuffer();
                    req.SetRequestHeader("Content-Type", "application/json");
                    AvatarConnection.SignOpenAI(req);
                    req.timeout = 30;
                    yield return req.SendWebRequest();
                    string body = req.downloadHandler != null ? req.downloadHandler.text : "";
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        onError("Live talk: no pass from OpenAI (" + req.responseCode + "): " + Short(body, req.error));
                        yield break;
                    }
                    try { bearer = JsonUtility.FromJson<Token>(body).value; } catch { }
                }
                if (string.IsNullOrEmpty(bearer)) { onError("Live talk: the Worker answered, but with no pass."); yield break; }
            }
            else bearer = AvatarVoiceAPI.ApiKey;       // Key File mode (Editor): the key itself
        }

        // 2. open the line
        if (Web)
        {
            // A browser can not add a header to this kind of line, so the
            // one-time pass rides along as a "sub-protocol" name instead.
            _webOpen = false; _closed = false; _closeReason = null;
            AvatarLiveWeb_Open(address,
                               _open == Provider.OpenAI ? "realtime,openai-insecure-api-key." + bearer : "",
                               gameObject.name, _open == Provider.OpenAI ? 0 : 1);
            float webDeadline = Time.realtimeSinceStartup + 20f;
            while (!_webOpen && !_closed && Time.realtimeSinceStartup < webDeadline) yield return null;
            if (!_webOpen)
            {
                string why = _closeReason ?? "timed out";
                Disconnect();
                onError("Live talk: could not open the line: " + why);
                yield break;
            }
        }
        else
        {
        _cts = new CancellationTokenSource();
        _ws = new ClientWebSocket();
        if (bearer != null) _ws.Options.SetRequestHeader("Authorization", "Bearer " + bearer);
        Task connect;
        try { connect = _ws.ConnectAsync(new Uri(address), _cts.Token); }
        catch (Exception e) { onError("Live talk: " + e.Message); Disconnect(); yield break; }

        float deadline = Time.realtimeSinceStartup + 20f;
        while (!connect.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
        if (!connect.IsCompleted || connect.IsFaulted || _ws.State != WebSocketState.Open)
        {
            string why = connect.IsFaulted && connect.Exception != null
                ? connect.Exception.GetBaseException().Message : "timed out";
            Disconnect();
            onError("Live talk: could not open the line: " + why);
            yield break;
        }
        }

        // 3. who she is, as the very first message
        if (_open == Provider.ElevenLabsAgent)
        {
            var sb = new StringBuilder("{\"type\":\"conversation_initiation_client_data\"");
            if (sendPersonaToAgent)
            {
                sb.Append(",\"conversation_config_override\":{\"agent\":{");
                sb.Append("\"prompt\":{\"prompt\":\"").Append(AvatarVoiceAPI.Escape(systemPrompt)).Append("\"}");
                if (firstMessage != null)
                    sb.Append(",\"first_message\":\"").Append(AvatarVoiceAPI.Escape(firstMessage)).Append("\"");
                sb.Append("}}");
            }
            sb.Append('}');
            _out.Enqueue(sb.ToString());
            AvatarTicker.Live = AvatarTicker.Via("ElevenLabs") + "  -  LIVE line: " + agentBrainLabel + " + agent voice";
            // the agent tells us its sound formats; mic and voice start then
        }
        else
        {
            string turn = turnStyle == TurnStyle.Meaning
                ? "{\"type\":\"semantic_vad\",\"eagerness\":\"" + eagerness.ToString().ToLowerInvariant() + "\"}"
                : "{\"type\":\"server_vad\",\"silence_duration_ms\":" + pauseMilliseconds + "}";
            var sb = new StringBuilder("{\"type\":\"session.update\",\"session\":{\"type\":\"realtime\",");
            sb.Append("\"instructions\":\"").Append(AvatarVoiceAPI.Escape(systemPrompt)).Append("\",");
            sb.Append("\"output_modalities\":[\"audio\"],");
            // A TOOL: something the model may ask the APP to do for it. Here:
            // "look in my notes". The model decides when; the app does the work.
            {
                var tools = new StringBuilder();
                if (lookInNotes && notesReady && OnLookInNotes != null)
                    tools.Append("{\"type\":\"function\",\"name\":\"look_in_notes\",\"description\":\"")
                         .Append(AvatarVoiceAPI.Escape(whenToLook))
                         .Append("\",\"parameters\":{\"type\":\"object\",\"properties\":{\"question\":{\"type\":\"string\",")
                         .Append("\"description\":\"The question to look up, in full\"}},\"required\":[\"question\"]}}");
                // ...and the AvatarActions list: show a video, change scene, and so on
                if (!string.IsNullOrEmpty(actionsJson) && OnAction != null)
                    tools.Append(tools.Length > 0 ? "," : "").Append(actionsJson);
                if (tools.Length > 0)
                    sb.Append("\"tools\":[").Append(tools).Append("],\"tool_choice\":\"auto\",");
            }
            sb.Append("\"audio\":{\"input\":{\"format\":{\"type\":\"audio/pcm\",\"rate\":24000},");
            sb.Append("\"turn_detection\":").Append(turn);
            if (!string.IsNullOrWhiteSpace(transcribeModel))
                sb.Append(",\"transcription\":{\"model\":\"").Append(AvatarVoiceAPI.Escape(transcribeModel.Trim())).Append("\"}");
            sb.Append("},\"output\":{\"format\":{\"type\":\"audio/pcm\",\"rate\":24000},");
            sb.Append("\"voice\":\"").Append(AvatarVoiceAPI.Escape(openAIVoice.Trim())).Append("\"}}}}");
            _out.Enqueue(sb.ToString());

            if (!string.IsNullOrWhiteSpace(firstMessage))
                _out.Enqueue("{\"type\":\"response.create\",\"response\":{\"instructions\":\"" +
                             AvatarVoiceAPI.Escape("Greet the person now. Say exactly this and nothing else: " + firstMessage) + "\"}}");

            AvatarTicker.Live = AvatarTicker.Via("OpenAI") + "  -  LIVE line: " + openAIModel + ", voice " + openAIVoice;
            _rateIn = _rateOut = 24000;
        }

        _closed = false;
        _closeReason = null;
        _dropUpToEventId = -1;
        _manualDrop = false;
        Connected = true;
        _connectedAt = Time.realtimeSinceStartup;

        // 4. Two background loops: one reads the line, one writes to it. They
        //    never touch Unity objects -- they only move text in and out of
        //    two queues, and Update() does the rest on the main thread.
        if (!Web)
        {
            var ws = _ws; var ct = _cts.Token;
            Task.Run(() => ReceiveLoop(ws, ct));
            Task.Run(() => SendLoop(ws, ct));
        }

        if (_open == Provider.OpenAI) { StartVoice(); StartMic(); }
        Debug.Log("[AvatarKit] live talk: line open (" + Summary + ")");
    }

    static string Short(string body, string fallback)
    {
        if (string.IsNullOrEmpty(body)) return fallback;
        return body.Length > 300 ? body.Substring(0, 300) : body;
    }

    async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = new byte[32 * 1024];
        var ms = new MemoryStream();
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    _closeReason = "the other side hung up (" + ws.CloseStatus + ") " + ws.CloseStatusDescription;
                    break;
                }
                ms.Write(buf, 0, r.Count);
                if (r.EndOfMessage)      // one message can arrive in several pieces
                {
                    _in.Enqueue(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
                    ms.SetLength(0);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!ct.IsCancellationRequested) _closeReason = "connection lost: " + e.Message; }
        if (!ct.IsCancellationRequested) _closed = true;
    }

    async Task SendLoop(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                string msg;
                if (_out.TryDequeue(out msg))
                {
                    var bytes = Encoding.UTF8.GetBytes(msg);
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
                }
                else await Task.Delay(4, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (!ct.IsCancellationRequested) { _closeReason = "send failed: " + e.Message; _closed = true; } }
    }

    // ------------------------------------------------------------ every frame

    void Update()
    {
        string msg;
        while (_in.TryDequeue(out msg))
        {
            if (_open == Provider.OpenAI) HandleOpenAI(msg); else HandleElevenLabs(msg);
        }

        if (Web)
        {
            // Web: hand our messages to the browser's line, and read back
            // how loud the mic and her voice are (the lip sync needs that).
            while (Connected && _out.TryDequeue(out msg)) AvatarLiveWeb_Send(msg);
            MicRms = Connected ? AvatarLiveWeb_MicLevel() : 0f;
            if (_lip == null) _lip = GetComponent<AvatarLipSync>();
            if (_lip != null) _lip.externalLevel = Connected ? AvatarLiveWeb_Level() : -1f;
        }

        if (Volatile.Read(ref _queued) > 0) _lastHadAudio = Time.time;
        if (SheIsSpeaking) _sheStoppedAt = Time.time;

        if (Connected && _closed)
        {
            string why = _closeReason ?? "the line closed";
            Disconnect();
            Debug.LogWarning("[AvatarKit] live talk: " + why);
            if (OnClosed != null) OnClosed(why);
            return;
        }

        if (Connected && _micClip != null) PumpMic();
    }

    void HandleOpenAI(string json)
    {
        OaEvt e;
        try { e = JsonUtility.FromJson<OaEvt>(json); } catch { return; }
        if (e == null || string.IsNullOrEmpty(e.type)) return;

        switch (e.type)
        {
            case "response.created":
                _manualDrop = false;            // a new reply: let its sound through
                break;

            case "response.output_audio.delta":
                if (_manualDrop || string.IsNullOrEmpty(e.delta)) break;
                Enqueue(Convert.FromBase64String(e.delta));
                break;

            case "response.output_audio_transcript.done":
                if (OnAgentResponse != null && !string.IsNullOrWhiteSpace(e.transcript)) OnAgentResponse(e.transcript);
                break;

            case "conversation.item.input_audio_transcription.completed":
                if (OnUserTranscript != null && !string.IsNullOrWhiteSpace(e.transcript)) OnUserTranscript(e.transcript);
                break;

            case "response.function_call_arguments.done":
                // she asked to look in the notes: hand the question to AvatarBrain
                if (string.IsNullOrEmpty(e.call_id)) break;
                if (e.name == "look_in_notes" && OnLookInNotes != null)
                {
                    string q = "";
                    try { q = JsonUtility.FromJson<LookArgs>(e.arguments).question; } catch { }
                    OnLookInNotes(e.call_id, q ?? "");
                }
                else if (OnAction != null) OnAction(e.call_id, e.name, e.arguments);   // an AvatarActions action
                else SendNotes(e.call_id, "That can not be done here.");
                break;

            case "input_audio_buffer.speech_started":
                // the service heard you start to talk: stop her sound here too
                if (SheIsSpeaking) { _manualDrop = true; ClearVoice(); }
                break;

            case "error":
                string m = e.error != null ? e.error.message : json;
                // cancelling when she had already finished is not a real error
                if (e.error != null && e.error.code == "response_cancel_not_active") break;
                Debug.LogError("[AvatarKit] live talk error: " + m);
                break;
        }
    }

    void HandleElevenLabs(string json)
    {
        ElEvt e;
        try { e = JsonUtility.FromJson<ElEvt>(json); } catch { return; }
        if (e == null || string.IsNullOrEmpty(e.type)) return;

        switch (e.type)
        {
            case "conversation_initiation_metadata":
                var m = e.conversation_initiation_metadata_event;
                _rateIn = RateOf(m != null ? m.user_input_audio_format : null, 16000);
                _rateOut = RateOf(m != null ? m.agent_output_audio_format : null, 16000);
                StartVoice();
                StartMic();
                break;

            case "audio":
                var a = e.audio_event;
                if (a == null || string.IsNullOrEmpty(a.audio_base_64)) break;
                if (_manualDrop || a.event_id <= _dropUpToEventId) break;
                Enqueue(Convert.FromBase64String(a.audio_base_64));
                break;

            case "user_transcript":
                _manualDrop = false;
                if (e.user_transcription_event != null && OnUserTranscript != null &&
                    !string.IsNullOrWhiteSpace(e.user_transcription_event.user_transcript))
                    OnUserTranscript(e.user_transcription_event.user_transcript);
                break;

            case "agent_response":
                if (e.agent_response_event != null && OnAgentResponse != null &&
                    !string.IsNullOrWhiteSpace(e.agent_response_event.agent_response))
                    OnAgentResponse(e.agent_response_event.agent_response);
                break;

            case "interruption":
                if (e.interruption_event != null) _dropUpToEventId = e.interruption_event.event_id;
                _manualDrop = false;
                ClearVoice();
                break;

            case "ping":
                // "are you still there?" -- answer, or the agent hangs up
                if (e.ping_event != null)
                    _out.Enqueue("{\"type\":\"pong\",\"event_id\":" + e.ping_event.event_id + "}");
                break;

            case "client_tool_call":
                // the agent asked to look in the notes (see "Knowledge notes" in the Inspector)
                var call = e.client_tool_call;
                if (call == null || string.IsNullOrEmpty(call.tool_call_id)) break;
                if (call.tool_name == "look_in_notes" && lookInNotes && notesReady && OnLookInNotes != null)
                    OnLookInNotes(call.tool_call_id, call.parameters != null ? call.parameters.question ?? "" : "");
                else if (call.tool_name != "look_in_notes" && OnAction != null)
                    OnAction(call.tool_call_id, call.tool_name, json);       // an AvatarActions action
                else   // always answer, or the agent waits in silence for its time-out
                    SendNotes(call.tool_call_id, "There are no notes for this. Say you are not sure.");
                break;

            case "client_error":
            case "error":
                Debug.LogError("[AvatarKit] live talk error: " + json);
                break;
        }
    }

    static int RateOf(string format, int fallback)
    {
        // "pcm_16000" -> 16000
        if (string.IsNullOrEmpty(format) || !format.StartsWith("pcm_")) return fallback;
        int rate;
        return int.TryParse(format.Substring(4), out rate) && rate > 0 ? rate : fallback;
    }

    // --------------------------------------------------------------- mic (up)

    string MicMessage(string base64)
    {
        return _open == Provider.OpenAI
            ? "{\"type\":\"input_audio_buffer.append\",\"audio\":\"" + base64 + "\"}"
            : "{\"user_audio_chunk\":\"" + base64 + "\"}";
    }

    void StartMic()
    {
        if (Web)
        {
            // Web: the browser reads the mic and sends it up the line itself.
            // It gets the two ends of the mic message to wrap each piece in.
            string[] ends = MicMessage("\u0001").Split('\u0001');
            AvatarLiveWeb_StartAudio(_rateIn, _rateOut, ends[0], ends[1]);
            return;
        }
        if (Microphone.devices.Length == 0) { Debug.LogError("[AvatarKit] live talk: no microphone"); return; }
        // a 10 second ring the mic writes round and round; we read what is new
        _micClip = Microphone.Start(micDevice, true, 10, _rateIn);
        _micLen = _micClip != null ? _micClip.samples : 0;
        _micRead = 0;
        int block = _rateIn / 20;                 // 50 ms at a time
        _micBlock = new float[block];
        _micBytes = new byte[block * 2];
        _silence = MicMessage(Convert.ToBase64String(new byte[block * 2]));
        _preRoll.Clear();
    }

    void PumpMic()
    {
        if (_micLen <= 0) return;
        int pos = Microphone.GetPosition(micDevice);
        int block = _micBlock.Length;
        int avail = ((pos - _micRead) % _micLen + _micLen) % _micLen;

        // The speaker and the mic share a room: while she is speaking (and
        // for a breath after) we send silence, or she would answer herself.
        bool muted = !wearingHeadphones &&
                     (SheIsSpeaking || Time.time - _sheStoppedAt < micShutAfterShe);

        bool speaking = SheIsSpeaking;
        if (!speaking) _speakingSince = -1f;
        else if (_speakingSince < 0f) _speakingSince = Time.time;
        float blockSeconds = block / (float)_rateIn;

        while (avail >= block)
        {
            _micClip.GetData(_micBlock, _micRead);          // wraps round the ring by itself
            _micRead = (_micRead + block) % _micLen;
            avail -= block;

            float sum = 0f;
            for (int i = 0; i < block; i++)
            {
                float s = _micBlock[i];
                sum += s * s;
                // float -1..1  ->  16-bit numbers, the "pcm" the service asked for
                short v = (short)(Mathf.Clamp(s, -1f, 1f) * 32767f);
                _micBytes[i * 2] = (byte)(v & 0xff);
                _micBytes[i * 2 + 1] = (byte)((v >> 8) & 0xff);
            }
            float rms = Mathf.Sqrt(sum / block);
            MicRms = rms;
            string real = MicMessage(Convert.ToBase64String(_micBytes));

            if (!muted) { _out.Enqueue(real); continue; }

            // Muted: the service gets silence. But keep the last 0.3 s of the
            // real sound, so if this turns out to be you cutting in, your
            // first word is not lost.
            _out.Enqueue(_silence);
            _preRoll.Enqueue(real);
            while (_preRoll.Count > 6) _preRoll.Dequeue();

            if (!cutInByVoice || !speaking) { _cutTime = 0f; continue; }

            // CUTTING IN. The mic hears two things while she speaks: her own
            // voice coming back out of the speaker (the echo), and maybe you.
            // For the first 0.4 s of each reply we learn how loud the echo
            // is. After that, anything clearly louder, held for a moment, is you.
            if (Time.time - _speakingSince < 0.4f)
            {
                _echoPeak = Mathf.Max(_echoPeak * 0.97f, rms);
                _cutTime = 0f;
                continue;
            }

            float bar = Mathf.Max(quietestVoice, _echoPeak * cutInLoudness);
            if (rms > bar) _cutTime += blockSeconds;
            else
            {
                // Speech has small gaps. A gap only takes a little off the
                // count; it does not put it back to zero.
                _cutTime = Mathf.Max(0f, _cutTime - blockSeconds * 0.5f);
                // The echo estimate moves SLOWLY, or the start of your own
                // voice would raise the bar against you.
                _echoPeak += (rms - _echoPeak) * (rms > _echoPeak ? 0.04f : 0.02f);
            }

            if (_cutTime >= cutInAfter)
            {
                _cutTime = 0f;
                Interrupt();
                muted = false;
                while (_preRoll.Count > 0) _out.Enqueue(_preRoll.Dequeue());
                if (OnCutIn != null) OnCutIn();
            }
            else if (logCutInMeter && Time.time - _meterAt > 1f)
            {
                _meterAt = Time.time;
                Debug.Log("[AvatarKit] cut-in meter: mic " + rms.ToString("0.000") +
                          ", echo " + _echoPeak.ToString("0.000") + ", bar " + bar.ToString("0.000"));
            }
        }
    }

    // ------------------------------------------------------------ voice (down)

    void StartVoice()
    {
        if (Web) return;                // Web: the browser plays her voice (see StartMic)
        if (voice == null) return;
        // A "streaming" clip has no sound of its own: every time the speaker
        // needs more, Unity calls OnAudioRead and we hand over whatever has
        // arrived. Nothing arrived -> silence. It loops forever. Because it
        // plays through the normal AudioSource, the lip sync keeps working.
        var clip = AudioClip.Create("AvatarLive", _rateOut, 1, _rateOut, true, OnAudioRead);
        voice.Stop();
        voice.clip = clip;
        voice.loop = true;
        voice.Play();
    }

    void Enqueue(byte[] pcm)
    {
        int n = pcm.Length / 2;
        if (n == 0) return;
        var f = new float[n];
        for (int i = 0; i < n; i++)
            f[i] = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)) / 32768f;
        lock (_playLock)
        {
            _chunks.Enqueue(f);
            _queued += n;
        }
    }

    // Runs on the AUDIO thread, not the main one: no Unity calls in here.
    void OnAudioRead(float[] data)
    {
        int i = 0;
        lock (_playLock)
        {
            while (i < data.Length && _chunks.Count > 0)
            {
                var c = _chunks.Peek();
                int take = Math.Min(data.Length - i, c.Length - _chunkPos);
                Array.Copy(c, _chunkPos, data, i, take);
                i += take;
                _chunkPos += take;
                if (_chunkPos >= c.Length) { _chunks.Dequeue(); _chunkPos = 0; }
            }
            _queued -= i;
        }
        for (; i < data.Length; i++) data[i] = 0f;
    }

    void ClearVoice()
    {
        if (Web) AvatarLiveWeb_Clear(_manualDrop ? 1 : 0);
        lock (_playLock)
        {
            _chunks.Clear();
            _chunkPos = 0;
            _queued = 0;
        }
        _lastHadAudio = -999f;
    }

    // ------------------------------------------------------------- from outside

    /// <summary>Hush her. She stops at once and the microphone opens.</summary>
    public void Interrupt()
    {
        if (!Connected) return;
        if (SheIsSpeaking) _manualDrop = true;
        if (_open == Provider.OpenAI) _out.Enqueue("{\"type\":\"response.cancel\"}");
        ClearVoice();
        _sheStoppedAt = -999f;
    }

    /// <summary>Typed words. She treats them as your turn and answers out loud.</summary>
    public void SendText(string text)
    {
        if (!Connected || string.IsNullOrEmpty(text)) return;
        Interrupt();
        if (_open == Provider.OpenAI)
        {
            _out.Enqueue("{\"type\":\"conversation.item.create\",\"item\":{\"type\":\"message\",\"role\":\"user\"," +
                         "\"content\":[{\"type\":\"input_text\",\"text\":\"" + AvatarVoiceAPI.Escape(text) + "\"}]}}");
            _out.Enqueue("{\"type\":\"response.create\"}");
        }
        else _out.Enqueue("{\"type\":\"user_message\",\"text\":\"" + AvatarVoiceAPI.Escape(text) + "\"}");
    }

    /// <summary>The answer to "look in my notes" or to an action: give her
    /// the result, then let her speak.</summary>
    public void SendNotes(string callId, string notes)
    {
        if (!Connected) return;
        if (_open == Provider.ElevenLabsAgent)
        {
            _out.Enqueue("{\"type\":\"client_tool_result\",\"tool_call_id\":\"" + AvatarVoiceAPI.Escape(callId) +
                         "\",\"result\":\"" + AvatarVoiceAPI.Escape(notes) + "\",\"is_error\":false}");
            return;
        }
        _out.Enqueue("{\"type\":\"conversation.item.create\",\"item\":{\"type\":\"function_call_output\",\"call_id\":\"" +
                     AvatarVoiceAPI.Escape(callId) + "\",\"output\":\"" + AvatarVoiceAPI.Escape(notes) + "\"}}");
        _out.Enqueue("{\"type\":\"response.create\"}");
    }

    /// <summary>Tell her something new mid-conversation without it counting
    /// as you speaking -- used when her memory notes change.</summary>
    public void SendContext(string text)
    {
        if (!Connected || string.IsNullOrEmpty(text)) return;
        if (_open == Provider.OpenAI)
            _out.Enqueue("{\"type\":\"conversation.item.create\",\"item\":{\"type\":\"message\",\"role\":\"system\"," +
                         "\"content\":[{\"type\":\"input_text\",\"text\":\"" + AvatarVoiceAPI.Escape(text) + "\"}]}}");
        else _out.Enqueue("{\"type\":\"contextual_update\",\"text\":\"" + AvatarVoiceAPI.Escape(text) + "\"}");
    }

    public void Disconnect()
    {
        Connected = false;
        AvatarTicker.Live = null;
        if (Web)
        {
            AvatarLiveWeb_Close();
            _webOpen = false;
            if (_lip != null) _lip.externalLevel = -1f;
        }
        if (_cts != null) { try { _cts.Cancel(); } catch { } }
        if (_ws != null) { try { _ws.Abort(); _ws.Dispose(); } catch { } }
        _ws = null; _cts = null;

        if (_micClip != null)
        {
            Microphone.End(micDevice);
            _micClip = null;
            _micLen = 0;
        }
        if (voice != null && voice.clip != null && voice.clip.name == "AvatarLive")
        {
            voice.Stop();
            voice.loop = false;
            voice.clip = null;
        }
        ClearVoice();
        string dump;
        while (_in.TryDequeue(out dump)) { }
        while (_out.TryDequeue(out dump)) { }
    }

    void OnDestroy() { Disconnect(); }
    void OnDisable() { Disconnect(); }
}
