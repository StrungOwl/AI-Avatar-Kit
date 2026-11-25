using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// THE CONVERSATION LOOP.
///
/// AvatarKit 2 gives the person TWO WAYS TO CHAT, picked on a start screen:
///
///   TALK   you speak, she speaks back. For a quiet place.
///            Talk Engine = Three Calls:  hold SPACE (or the button) -> record
///                -> ears (speech-to-text) -> brain (chat) -> voice
///                (text-to-speech) -> AudioSource -> AvatarLipSync
///            Talk Engine = Live:  one open line, speech in and speech out,
///                much faster, and you can talk over her (see AvatarLive)
///
///   TEXT   you type, she answers in a speech bubble. For a busy place.
///            No microphone and no sound. Only the brain call is used.
///
/// WHERE THE MEMORY LIVES -- there are two, and the difference is the lesson:
///   SHORT-TERM: the _history list below. In RAM, on this machine. It
///     disappears when you press Stop. The feeling of a continuous
///     conversation is created by re-sending the whole transcript on every
///     single request.
///   LONG-TERM: the AvatarMemory component (if present) -- plain text files
///     that survive Stop, get distilled in the background, and ride the
///     system prompt on every call.
///
/// The UI is IMGUI (OnGUI) on purpose: no Canvas, no EventSystem, no font
/// asset, no TextMeshPro import step. Every colour, size and word of it is a
/// box under "LOOK AND WORDS" in the Inspector -- no code needed to restyle.
/// </summary>
public class AvatarBrain : MonoBehaviour
{
    public enum State { Idle, Recording, Thinking, Speaking, Error }
    public enum ChatMode { Ask, Talk, Text }
    public enum TalkEngine { ThreeCalls, Live }

    [Header("YOUR CHARACTER -- drag your Persona asset here")]
    public CharacterPersona persona;

    [Header("Wiring (leave empty to auto-find)")]
    public AudioSource voice;
    public AvatarLipSync lipSync;
    public AvatarBody body;
    public AvatarMemory memory;   // optional -- remove/disable for an anonymous companion
    [Tooltip("Optional. Pick OpenAI or ElevenLabs here. Empty = the voice on the Persona.")]
    public AvatarVoice avatarVoice;
    [Tooltip("Optional. Needed only when Talk Engine is Live.")]
    public AvatarLive live;
    [Tooltip("Optional. The bubble by her head. Text mode puts her reply in it.")]
    public AvatarSpeechBubble bubble;
    [Tooltip("Optional. Things she can ask the game to do, and things the game knows. See AvatarActions.")]
    public AvatarActions actions;

    [Header("HOW PEOPLE CHAT")]
    [Tooltip("WHAT THE PERSON SEES FIRST.\n\n" +
             "ASK -- a start screen with two cards, TALK and TEXT. Good for: an app other people use, " +
             "because they pick what fits the place they are in.\n\n" +
             "TALK -- no start screen; she listens and speaks. Good for: a quiet place, a kiosk, a headset.\n\n" +
             "TEXT -- no start screen; the person types and reads her reply in a speech bubble. No microphone, " +
             "no sound. Good for: a busy or loud place, and people who can not talk out loud.")]
    public ChatMode startMode = ChatMode.Ask;
    [Tooltip("Show the small button that changes between Talk and Text while the app runs.")]
    public bool allowModeSwitch = true;
    [Tooltip("HOW SHE TALKS WITH YOU, in Talk mode.\n\n" +
             "THREE CALLS -- hold the button, speak, let go. Then 3 trips to the internet: ears (speech to text), " +
             "brain (chat), voice (text to speech). There is a wait after each turn. " +
             "Good for: learning how an AI avatar works (you can see each step), the best voice quality, " +
             "a weak connection, and knowledge notes on every answer.\n\n" +
             "LIVE -- one open line. Your voice goes up and her voice comes down at the same time. " +
             "Fast, no button, and you can talk over her to cut in. Needs an AvatarLive component. " +
             "Good for: a real back-and-forth conversation. It costs more for each minute.")]
    public TalkEngine talkEngine = TalkEngine.ThreeCalls;
    [Tooltip("Text mode: ALSO say the reply out loud. Off = silent, for a busy place.")]
    public bool textModeSpeaksAloud = false;
    [Tooltip("Text mode: look things up in the knowledge notes before she answers.")]
    public bool textModeUsesKnowledge = true;

    [Header("Microphone")]
    [Tooltip("Which mic, if you have several. 0 = system default order.")]
    public int microphoneIndex = 0;
    public int recordSeconds = 15;
    public int sampleRate = 16000;

    [Header("Behaviour")]
    public bool greetOnStart = true;
    public bool showUI = true;
    [Tooltip("Show which knowledge chunks answered the last question. 'It " +
             "runs' and 'it works' are different claims -- this panel tells " +
             "them apart.")]
    public bool showKnowledgePanel = true;
    [Tooltip("The full-reply box at the bottom. AvatarSpeechBubble turns this off and shows the words by her head instead.")]
    public bool showReplyBox = true;

    /// <summary>Every colour, size and word of the on-screen UI.</summary>
    [Serializable]
    public class Look
    {
        [Header("Size and font")]
        [Tooltip("Make the UI bigger on sharp phone screens by itself.")]
        public bool growOnPhones = true;
        [Range(0.5f, 3f)] public float size = 1f;
        [Tooltip("Any font you imported (.ttf / .otf). Empty = Unity's built-in font.")]
        public Font font;
        [Range(10, 40)] public int fontSize = 16;
        public Color textColor = Color.white;
        public Color faintTextColor = new Color(0.75f, 0.78f, 0.8f);

        [Header("Start screen (the chooser)")]
        public string chooserTitle = "How do you want to chat?";
        public string talkTitle = "TALK";
        [TextArea(1, 3)] public string talkNote = "Speak and listen.\nFor a quiet place.";
        public string textTitle = "TEXT";
        [TextArea(1, 3)] public string textNote = "Type and read. No sound.\nFor a busy place.";
        public Color screenDim = new Color(0f, 0f, 0f, 0.6f);
        public Color cardColor = new Color(0.16f, 0.18f, 0.24f, 0.96f);
        public Color cardTextColor = Color.white;
        public Vector2 cardSize = new Vector2(230f, 140f);

        [Header("Talk button")]
        public Vector2 talkButtonSize = new Vector2(300f, 60f);
        public Color talkButtonColor = new Color(0.16f, 0.18f, 0.24f, 0.92f);
        public Color talkButtonActiveColor = new Color(0.2f, 0.55f, 0.42f, 0.95f);
        public Color talkButtonTextColor = Color.white;
        public string idleLabel = "HOLD to talk  (or hold SPACE)";
        public string listeningLabel = "listening -- release to send";
        public string thinkingLabel = "thinking...";
        public string speakingLabel = "speaking (hold to interrupt)";
        public string liveIdleLabel = "just talk  (tap to hush her)";
        public string liveSpeakingLabel = "speaking -- talk over her, or tap";

        [Header("Text box")]
        public string typeHint = "type here...";
        public string sendLabel = "Send";
        public Color textBoxColor = new Color(0.1f, 0.11f, 0.14f, 0.95f);
        public Color typedTextColor = Color.white;
        public Color sendButtonColor = new Color(0.2f, 0.45f, 0.75f, 0.95f);
        public float textBoxWidth = 420f;

        [Header("Reply box and mode button")]
        public Color replyBoxColor = new Color(0.1f, 0.11f, 0.14f, 0.9f);
        public Color replyTextColor = Color.white;
        public Color yourWordsColor = new Color(0.85f, 0.9f, 1f, 0.9f);
        public string talkModeLabel = "TALK mode  -  tap for Text";
        public string textModeLabel = "TEXT mode  -  tap for Talk";
        public Color modeButtonColor = new Color(0.16f, 0.18f, 0.24f, 0.9f);

        [Header("Small read-outs")]
        [Tooltip("Top-right corner: which API key each call uses, as it happens.")]
        public bool showWhichKey = true;
        public Color whichKeyColor = new Color(0.9f, 0.9f, 0.95f, 0.85f);
        public Color liveLineColor = new Color(0.72f, 0.96f, 0.86f, 0.9f);
        public bool showStatus = true;
        public bool showMemoryCount = true;
    }

    [Header("LOOK AND WORDS -- restyle the UI here, no code")]
    public Look look = new Look();

    /// <summary>The line she is saying (or just said). AvatarSpeechBubble reads this.</summary>
    public string LastReply { get { return _lastReply; } }
    public State CurrentState { get { return _state; } }
    public bool IsSpeaking { get { return _state == State.Speaking; } }
    public bool TextMode { get { return _mode == ChatMode.Text; } }
    /// <summary>True when the live line is the talk engine right now.</summary>
    public bool LiveActive
    {
        get
        {
            return _mode == ChatMode.Talk && talkEngine == TalkEngine.Live &&
                   live != null && live.enabled && AvatarLive.Supported;
        }
    }
    /// <summary>In live talk and Text mode the brain tells the bubble what to show.</summary>
    public bool DrivesBubble { get { return LiveActive || (TextMode && !textModeSpeaksAloud); } }
    /// <summary>Silent Text mode: the bubble stays up until the next reply.</summary>
    public bool KeepBubbleUp { get { return TextMode && !textModeSpeaksAloud; } }

    /// <summary>A web (browser) build. The browser has its own microphone rules.</summary>
    public static bool OnWeb
    {
        get
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            return true;
#else
            return false;
#endif
        }
    }

    State _state = State.Idle;
    ChatMode _mode = ChatMode.Ask;
    bool _booted, _micMissing;
    string _status = "", _lastUser = "", _lastReply = "", _mic;
    AudioClip _recording;
    float _recordStart;
    readonly List<AvatarVoiceAPI.Msg> _history = new List<AvatarVoiceAPI.Msg>();
    readonly AvatarKnowledge _knowledge = new AvatarKnowledge();
    KnowledgeChunk[] _retrieved;
    bool _buttonHeld;

    // live talk
    bool _liveThinking, _liveLastedLongEnough, _liveHeldLast;
    string _livePendingUser, _liveMemorySent;

    // typed words
    string _typed = "";
    bool _typing;
    TouchScreenKeyboard _phoneKeys;
#if ENABLE_INPUT_SYSTEM
    UnityEngine.InputSystem.Keyboard _keysHooked;
#endif

    // ================================================================= start

    IEnumerator Start()
    {
        // Without this, the Editor throttles Play mode whenever its window
        // loses focus: coroutines stall, web requests never finish, and your
        // character silently never answers -- with nothing logged. This one
        // line saves a genuinely baffling hour.
        Application.runInBackground = true;

        if (voice == null) voice = GetComponent<AudioSource>();
        if (lipSync == null) lipSync = GetComponent<AvatarLipSync>();
        if (body == null) body = GetComponent<AvatarBody>();
        if (memory == null) memory = GetComponent<AvatarMemory>();
        if (avatarVoice == null) avatarVoice = GetComponent<AvatarVoice>();
        if (live == null) live = GetComponent<AvatarLive>();
        if (bubble == null) bubble = GetComponent<AvatarSpeechBubble>();
        if (actions == null) actions = GetComponent<AvatarActions>();
        if (ActionsOn) actions.FactsChanged += OnFactsChanged;

        if (persona == null)
        {
            Fail("No Persona assigned. Create one: right-click in Project -> " +
                 "Create -> AI Avatar -> Character Persona, then drag it onto this component.");
            yield break;
        }
        string problem = AvatarConnection.Problem();
        if (problem != null) { Fail(problem); yield break; }
        if (VoiceOn && avatarVoice.Problem() != null) { Fail(avatarVoice.Problem()); yield break; }

        // THE MICROPHONE MUST BE ASKED FOR. A phone and a browser both block
        // it until the app asks and the person says yes. AvatarKit 1 never
        // asked, so builds had no microphone.
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone))
        {
            _status = "waiting for microphone permission...";
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Microphone);
            float until = Time.realtimeSinceStartup + 20f;
            while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Microphone) &&
                   Time.realtimeSinceStartup < until)
                yield return null;
        }
#elif (UNITY_IOS || UNITY_WEBGL) && !UNITY_EDITOR
        if (!Application.HasUserAuthorization(UserAuthorization.Microphone))
        {
            _status = "waiting for microphone permission...";
            yield return Application.RequestUserAuthorization(UserAuthorization.Microphone);
        }
        float listUntil = Time.realtimeSinceStartup + 5f;   // the list fills a moment after "yes"
        while (Microphone.devices.Length == 0 && Time.realtimeSinceStartup < listUntil) yield return null;
#endif
        _status = "";

        // No microphone is not the end: Text mode needs none.
        _micMissing = Microphone.devices.Length == 0;
        if (!_micMissing)
        {
            int idx = Mathf.Clamp(microphoneIndex, 0, Microphone.devices.Length - 1);
            _mic = Microphone.devices[idx];
            Debug.Log("[AvatarKit] Using microphone: " + _mic +
                      "  (" + Microphone.devices.Length + " available)");
        }
        else Debug.LogWarning("[AvatarKit] No microphone found. Talk mode is off; Text mode still works.");

        if (_knowledge.TryLoad())
            Debug.Log("[AvatarKit] Knowledge index loaded: " + _knowledge.ChunkCount +
                      " chunks. (" + (persona.useKnowledge ? "useKnowledge is ON" :
                      "useKnowledge is OFF on the Persona") + ")");

        // Nothing starts until a mode is chosen: by the person, or here.
        var first = startMode;
        if (first == ChatMode.Ask && UnityEngine.XR.XRSettings.isDeviceActive) first = ChatMode.Talk;  // no flat screen to tap in a headset
        if (first == ChatMode.Talk && _micMissing) first = ChatMode.Text;
        if (first != ChatMode.Ask) ChooseMode(first);
    }

    bool MemoryOn { get { return memory != null && memory.isActiveAndEnabled; } }
    bool VoiceOn { get { return avatarVoice != null && avatarVoice.enabled; } }
    bool ActionsOn { get { return actions != null && actions.isActiveAndEnabled; } }
    string Facts { get { return ActionsOn ? actions.FactsBlock : ""; } }

    /// <summary>The game changed a fact. An open live line is told at once;
    /// the other ways read the list fresh on every turn anyway.</summary>
    void OnFactsChanged()
    {
        if (LiveActive && live != null && live.Connected && Facts.Length > 0)
            live.SendContext(Facts);
    }

    // ============================================================ Talk / Text

    /// <summary>Change how we chat. Stops whatever the old mode had running
    /// (the live line, the microphone, a half-made answer), then starts the
    /// new one. The conversation so far and her memory are kept.</summary>
    public void ChooseMode(ChatMode m)
    {
        if (m == ChatMode.Ask || _state == State.Error) return;
        if (_booted && m == _mode) return;
        if (m == ChatMode.Talk && _micMissing)
        {
            _status = "No microphone found, so Talk mode is off.";
            return;
        }

        StopAllCoroutines();
        if (live != null && live.Connected) live.Disconnect();
        if (_recording != null) { Microphone.End(_mic); _recording = null; }
        if (voice != null) voice.Stop();
        _typed = ""; _typing = false; _buttonHeld = false;
        _state = State.Idle; _status = "";

        _mode = m;
        Debug.Log("[AvatarKit] mode: " + (m == ChatMode.Text ? "TEXT (type and read)" :
                  LiveActive ? "TALK, live line" : "TALK, three calls"));
        if (m == ChatMode.Talk && talkEngine == TalkEngine.Live && !LiveActive)
            Debug.LogWarning("[AvatarKit] Talk Engine is Live, but " +
                             (live == null ? "there is no AvatarLive component" : "AvatarLive is switched off") +
                             ". Using Three Calls.");

        if (!_booted) { _booted = true; StartCoroutine(Boot()); }
        else if (LiveActive) StartCoroutine(StartLive(false));
    }

    /// <summary>Fold in any journal left over from last session BEFORE the
    /// greeting, so the greeting already knows about it. This is what makes
    /// stopping Play at any moment safe: nothing said is ever lost.</summary>
    IEnumerator Boot()
    {
        if (MemoryOn && memory.HasPending)
        {
            _status = "remembering last time...";
            yield return StartCoroutine(memory.UpdateNow(persona));
            _status = "";
        }
        bool greet = greetOnStart && !string.IsNullOrEmpty(persona.greeting);
        if (LiveActive) { yield return StartCoroutine(StartLive(greet)); yield break; }
        if (greet) yield return StartCoroutine(Greet());
    }

    /// <summary>The greeting line. With notes on disk she greets like someone
    /// who remembers you, not a doorbell. Falls back to the fixed greeting.</summary>
    IEnumerator GreetingLine(Action<string> onLine)
    {
        string line = persona.greeting;
        if (MemoryOn && memory.HasMemory)
        {
            var nudge = new List<AvatarVoiceAPI.Msg> {
                new AvatarVoiceAPI.Msg { role = "user", content =
                    "(They just walked in. Greet them in one short spoken sentence, " +
                    "as yourself. Pick up an open thread from your notes only if one " +
                    "is genuinely worth picking up.)" } };
            yield return StartCoroutine(AvatarVoiceAPI.Chat(persona, nudge,
                r => { if (!string.IsNullOrWhiteSpace(r)) line = r.Trim(); },
                e => Debug.LogWarning("[AvatarKit] memory greeting failed, " +
                                      "using the fixed one: " + e),
                memory.PromptAddition));
        }
        onLine(line);
    }

    IEnumerator Greet()
    {
        _state = State.Thinking;
        yield return new WaitForSeconds(0.5f);
        string line = null;
        yield return StartCoroutine(GreetingLine(l => line = l));

        _lastReply = line;
        _history.Add(new AvatarVoiceAPI.Msg { role = "assistant", content = line });
        yield return StartCoroutine(SpeakLine(line));
    }

    // ================================================================== update

    void Update()
    {
        if (_state == State.Error) return;

        HookKeyboard();
        UpdatePhoneKeyboard();

        Vector2 at; bool down, heldPointer;
        ReadPointer(out at, out down, out heldPointer);
        if (down) OnPress(at);
        if (!heldPointer) _buttonHeld = false;

        if (_mode == ChatMode.Ask) return;                  // waiting on the chooser

        bool held = !TextMode && !_typing && (_buttonHeld || TalkKeyHeld());

        if (TextMode) { /* no microphone in Text mode */ }
        else if (LiveActive) UpdateLive(held);
        else
        {
            if (held && _state == State.Speaking) Interrupt();
            if (held && _state == State.Idle) StartRecording();
            else if (!held && _state == State.Recording) StopAndSend();

            if (_state == State.Recording && Time.time - _recordStart > recordSeconds - 0.25f)
                StopAndSend();
        }

        if (body != null)
            body.SetMood(_state == State.Recording ? AvatarBody.Mood.Listening :
                         _state == State.Speaking ? AvatarBody.Mood.Talking :
                                                    AvatarBody.Mood.Idle);

        if (!LiveActive && _state == State.Speaking && voice != null && !voice.isPlaying)
        {
            _state = State.Idle;
            _status = "";
        }
    }

    /// <summary>Hush her.</summary>
    public void Interrupt()
    {
        if (LiveActive) { if (live != null) live.Interrupt(); }
        else if (voice != null && voice.isPlaying) voice.Stop();
        if (_state == State.Speaking) { _state = State.Idle; _status = ""; }
    }

    /// <summary>Projects set to "Input System Package (New)" THROW on the
    /// legacy Input.GetKey rather than returning false, so read whichever
    /// API is actually enabled.</summary>
    bool TalkKeyHeld()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != null) return kb.spaceKey.isPressed;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKey(KeyCode.Space);
#else
        return false;
#endif
    }

    // ============================================================ three calls

    void StartRecording()
    {
        if (_micMissing) return;
        _recording = Microphone.Start(_mic, false, recordSeconds, sampleRate);
        _recordStart = Time.time;
        _state = State.Recording;
        _status = "listening...";
        if (voice != null && voice.isPlaying) voice.Stop();   // let the user interrupt
    }

    void StopAndSend()
    {
        if (_recording == null) { _state = State.Idle; return; }
        int written = Microphone.GetPosition(_mic);
        Microphone.End(_mic);

        // On the web the position is not reliable after End; use the time.
        if (OnWeb) written = Mathf.Min(_recording.samples, Mathf.RoundToInt((Time.time - _recordStart) * sampleRate));

        if (written < sampleRate / 4)          // under a quarter second = mis-tap
        {
            _state = State.Idle; _status = ""; _recording = null; return;
        }
        _state = State.Thinking;
        _status = "thinking...";
        if (OnWeb) { StartCoroutine(WebFinishRecording(_recording, written)); _recording = null; return; }

        var wav = AvatarWav.FromAudioClip(_recording, written);
        _recording = null;
        StartCoroutine(RunTurn(wav));
    }

    /// <summary>A BROWSER RULE: the sound of a recording can only be read
    /// AFTER it stops, and the browser needs a moment to unpack it. Until
    /// then the clip reads as empty. Wait for real samples (3 s at most).</summary>
    IEnumerator WebFinishRecording(AudioClip clip, int written)
    {
        int n = Mathf.Clamp(written, 1, clip.samples) * clip.channels;
        var probe = new float[n];
        float deadline = Time.realtimeSinceStartup + 3f;
        bool ready = false;
        while (!ready && Time.realtimeSinceStartup < deadline)
        {
            yield return new WaitForSecondsRealtime(0.1f);
            if (!clip.GetData(probe, 0)) continue;
            for (int i = 0; i < n; i += 16) if (probe[i] != 0f) { ready = true; break; }
        }
        if (!ready) { _status = "didn't catch that"; _state = State.Idle; yield break; }
        StartCoroutine(RunTurn(AvatarWav.FromAudioClip(clip, written)));
    }

    IEnumerator RunTurn(byte[] wav)
    {
        string heard = null;
        yield return StartCoroutine(AvatarVoiceAPI.Transcribe(
            wav, persona.sttModel,
            "A casual spoken conversation with a character named " + persona.characterName + ".",
            t => heard = t, Fail));
        if (_state == State.Error) yield break;

        if (string.IsNullOrWhiteSpace(heard))
        {
            _status = "didn't catch that"; _state = State.Idle; yield break;
        }
        yield return StartCoroutine(RunTurnFromText(heard));
    }

    /// <summary>The turn from the point where we have your words as text --
    /// spoken and transcribed, or typed in Text mode.</summary>
    IEnumerator RunTurnFromText(string heard)
    {
        _lastUser = heard.Trim();
        _history.Add(new AvatarVoiceAPI.Msg { role = "user", content = _lastUser });
        while (_history.Count > Mathf.Max(2, persona.memoryTurns)) _history.RemoveAt(0);
        if (KeepBubbleUp && bubble != null) bubble.Show(". . .", 0.2f);

        // The librarian runs BEFORE the brain: embed the question, fetch the
        // three nearest chunks, and let them ride the system prompt. If the
        // embed call hiccups we answer without notes rather than not at all.
        string context = null;
        _retrieved = null;
        bool useNotes = persona.useKnowledge && _knowledge.Loaded && (!TextMode || textModeUsesKnowledge);
        if (useNotes)
        {
            _status = "checking notes...";
            float[] q = null;
            yield return StartCoroutine(AvatarVoiceAPI.Embed(
                _lastUser, AvatarKnowledge.EmbedModel, v => q = v,
                e => Debug.LogWarning("[AvatarKit] embedding failed -- " +
                                      "answering without notes: " + e)));
            if (q != null)
            {
                _retrieved = _knowledge.TopK(q, 3);
                context = AvatarKnowledge.ContextBlock(_retrieved);
            }
            _status = "thinking...";
        }

        string extraSystem = (MemoryOn ? memory.PromptAddition : "") + (context ?? "") + Facts +
                             (KeepBubbleUp && !string.IsNullOrWhiteSpace(persona.textModeRule) ? "\n\n" + persona.textModeRule : "");
        string reply = null;

        // ACTIONS: she may ask the game to do something instead of answering.
        string tools = ActionsOn && actions.HasActions ? actions.ToolsJson(false) : null;
        string askId = null, askName = null, askArgs = null;
        yield return StartCoroutine(AvatarVoiceAPI.Chat(persona, _history, r => reply = r, Fail,
            extraSystem.Length > 0 ? extraSystem : null, tools, null,
            (id, name, args) => { askId = id; askName = name; askArgs = args; }));
        if (_state == State.Error) yield break;

        if (askName != null)
        {
            // The game does it now. Then the brain is asked again, this time
            // knowing what happened, so she can say something about it.
            _status = "doing: " + askName;
            string result = actions.Run(askName, askArgs);
            string said = "{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"" + AvatarVoiceAPI.Escape(askId) +
                          "\",\"type\":\"function\",\"function\":{\"name\":\"" + AvatarVoiceAPI.Escape(askName) +
                          "\",\"arguments\":\"" + AvatarVoiceAPI.Escape(askArgs ?? "{}") + "\"}}]}," +
                          "{\"role\":\"tool\",\"tool_call_id\":\"" + AvatarVoiceAPI.Escape(askId) +
                          "\",\"content\":\"" + AvatarVoiceAPI.Escape(result) + "\"}";
            yield return StartCoroutine(AvatarVoiceAPI.Chat(persona, _history, r => reply = r, Fail,
                extraSystem.Length > 0 ? extraSystem : null, tools, said, null));
            if (_state == State.Error) yield break;
        }

        _lastReply = (reply ?? "").Trim();
        _history.Add(new AvatarVoiceAPI.Msg { role = "assistant", content = _lastReply });

        // Journal the exchange to disk the moment it exists -- the distil
        // pass runs in the background and at next launch (see AvatarMemory).
        if (MemoryOn) memory.NotifyExchange(_lastUser, _lastReply, persona);

        yield return StartCoroutine(SpeakLine(_lastReply));
    }

    /// <summary>Right-click this component while playing -> "Say Test Line".
    /// The quickest way to tell "the API is broken" from "the mic is broken".</summary>
    [ContextMenu("Say Test Line")]
    public void SayTestLine() { StartCoroutine(SpeakLine("Testing. One, two.")); }

    IEnumerator SpeakLine(string line)
    {
        if (KeepBubbleUp)
        {
            // Silent Text mode: her words go in the bubble. No voice call at all.
            if (bubble != null) bubble.Show(line, Mathf.Clamp(WordCount(line) * 0.07f, 0.3f, 3f));
            _state = State.Idle;
            _status = "";
            yield break;
        }

        _status = "speaking...";
        AudioClip clip = null;
        // AvatarVoice (if present) picks OpenAI or ElevenLabs; otherwise the Persona's voice.
        var speak = VoiceOn ? avatarVoice.Speak(line, c => clip = c, Fail)
                            : AvatarVoiceAPI.Speak(persona, line, c => clip = c, Fail);
        yield return StartCoroutine(speak);
        if (_state == State.Error) yield break;

        if (voice != null && clip != null)
        {
            voice.loop = false;
            voice.clip = clip;
            voice.Play();
            _state = State.Speaking;
        }
        else _state = State.Idle;
    }

    static int WordCount(string s)
    {
        return string.IsNullOrEmpty(s) ? 0 : s.Split(new[] { ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries).Length;
    }

    // ================================================================ live talk

    string LivePrompt()
    {
        return persona.ResolvedPrompt + (MemoryOn ? memory.PromptAddition : "") + Facts +
               (string.IsNullOrWhiteSpace(persona.liveTalkRule) ? "" : "\n\n" + persona.liveTalkRule);
    }

    IEnumerator StartLive(bool greet)
    {
        _state = State.Thinking;
        _status = "connecting...";

        string first = "";
        if (greet) yield return StartCoroutine(GreetingLine(l => first = l));

        live.voice = voice;
        live.micDevice = _mic;
        live.OnUserTranscript = OnLiveUser;
        live.OnAgentResponse = OnLiveAgent;
        live.OnClosed = OnLiveClosed;
        live.OnCutIn = () => { if (_state == State.Speaking) _state = State.Idle; };
        live.OnLookInNotes = (id, question) => StartCoroutine(LiveLookInNotes(id, question));
        live.notesReady = persona.useKnowledge && _knowledge.Loaded;
        live.actionsJson = ActionsOn && actions.HasActions ? actions.ToolsJson(true) : null;
        live.OnAction = (id, name, args) =>
        {
            string result = ActionsOn ? actions.Run(name, args) : "That can not be done here.";
            if (live != null) live.SendNotes(id, result);
        };

        _liveMemorySent = MemoryOn ? memory.PromptAddition : "";
        _livePendingUser = null;
        _liveThinking = false;

        string error = null;
        yield return StartCoroutine(live.Connect(LivePrompt(), first, e => error = e));
        if (error != null) { Fail(error); yield break; }

        _state = State.Idle;
        _status = "";
    }

    /// <summary>LIVE TALK + KNOWLEDGE. In the three-call way the app looks in
    /// the notes BEFORE the brain answers, every time. In live talk there is
    /// no "before": your voice goes straight to the model. So the model gets
    /// a tool instead. When it needs a fact it asks, this runs, and it
    /// answers from what comes back. Small talk never gets here.</summary>
    IEnumerator LiveLookInNotes(string callId, string question)
    {
        if (string.IsNullOrWhiteSpace(question)) question = _lastUser;
        _status = "checking notes...";
        float[] q = null;
        yield return StartCoroutine(AvatarVoiceAPI.Embed(
            question, AvatarKnowledge.EmbedModel, v => q = v,
            e => Debug.LogWarning("[AvatarKit] live notes lookup failed: " + e)));
        string notes = "The notes could not be read. Say you are not sure.";
        if (q != null)
        {
            _retrieved = _knowledge.TopK(q, live != null ? live.notesPerLookup : 3);
            if (string.IsNullOrEmpty(_lastUser)) _lastUser = question;
            notes = AvatarKnowledge.ContextBlock(_retrieved);
        }
        _status = "";
        Debug.Log("[AvatarKit] live talk looked in the notes for: " + question);
        if (live != null) live.SendNotes(callId, notes);
    }

    void OnLiveUser(string heard)
    {
        // "..." -- the service heard a breath or a chair, not words
        bool anyWord = false;
        foreach (char ch in heard) if (char.IsLetterOrDigit(ch)) { anyWord = true; break; }
        if (!anyWord) return;

        _lastUser = heard.Trim();
        _livePendingUser = _lastUser;
        _liveThinking = true;
    }

    void OnLiveAgent(string reply)
    {
        _liveThinking = false;
        // Some agent brains wrap the line in tags like <Name>...</Name>. Take them off.
        _lastReply = System.Text.RegularExpressions.Regex.Replace(reply, "</?[A-Za-z][^>]*>", "").Trim();
        if (bubble != null) bubble.Show(_lastReply, Mathf.Max(1.5f, WordCount(_lastReply) * 0.33f));

        if (MemoryOn && !string.IsNullOrEmpty(_livePendingUser))
        {
            memory.NotifyExchange(_livePendingUser, _lastReply, persona);
            // her notes may have just changed: tell the open line
            string now = memory.PromptAddition;
            if (now != _liveMemorySent && live != null && live.Connected)
            {
                _liveMemorySent = now;
                live.SendContext("Your notes about this person were just updated:" + now);
            }
        }
        _livePendingUser = null;
    }

    void OnLiveClosed(string why)
    {
        if (!isActiveAndEnabled || _mode != ChatMode.Talk) return;
        // A line that lasted a while and then dropped (a timeout, wifi) is
        // worth quietly reopening. One that dies at once is a setup problem.
        if (_liveLastedLongEnough)
        {
            Debug.Log("[AvatarKit] live talk dropped, reopening: " + why);
            StartCoroutine(StartLive(false));
        }
        else Fail("Live talk stopped: " + why);
    }

    void UpdateLive(bool held)
    {
        if (live == null || !live.Connected) return;        // still connecting
        if (live.Uptime > 20f) _liveLastedLongEnough = true;

        if (held && !_liveHeldLast) Interrupt();
        _liveHeldLast = held;

        if (live.SheIsSpeaking) { _liveThinking = false; _state = State.Speaking; }
        else _state = _liveThinking ? State.Thinking
                    : live.MicRms > live.quietestVoice * 0.5f ? State.Recording
                    : State.Idle;
    }

    // ============================================================ typed words

    /// <summary>You typed instead of talking.</summary>
    public void SubmitText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _state == State.Error) return;
        text = text.Trim();
        if (LiveActive)
        {
            if (live == null || !live.Connected) return;
            live.SendText(text);
            OnLiveUser(text);
            return;
        }
        if (_state == State.Thinking || _state == State.Recording) return;   // one turn at a time
        if (voice != null && voice.isPlaying) voice.Stop();
        _state = State.Thinking;
        _status = "thinking...";
        StartCoroutine(RunTurnFromText(text));
    }

    void OnTypedChar(char c)
    {
        if (!TextMode || _state == State.Error) return;
        if (c == '\r' || c == '\n') { SendTyped(); return; }
        if (c == '\b') { if (_typed.Length > 0) _typed = _typed.Substring(0, _typed.Length - 1); return; }
        if (c == 27) { _typed = ""; return; }
        if (char.IsControl(c)) return;
        if (_typed.Length < 400) _typed += c;
        _typing = true;
    }

    void SendTyped()
    {
        string t = _typed;
        _typed = "";
        if (t.Trim().Length > 0) SubmitText(t);
    }

    /// <summary>The keyboard is read here, not through a ready-made text
    /// field: on a phone and in Unity's Device Simulator that field never
    /// gets the keyboard.</summary>
    void HookKeyboard()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = UnityEngine.InputSystem.Keyboard.current;
        if (kb != _keysHooked)
        {
            if (_keysHooked != null) _keysHooked.onTextInput -= OnTypedChar;
            _keysHooked = kb;
            if (kb != null) kb.onTextInput += OnTypedChar;
        }
#elif ENABLE_LEGACY_INPUT_MANAGER
        foreach (char c in Input.inputString) OnTypedChar(c);
#endif
    }

    void OpenPhoneKeyboard()
    {
        if (!TouchScreenKeyboard.isSupported || _phoneKeys != null) return;
        _phoneKeys = TouchScreenKeyboard.Open(_typed, TouchScreenKeyboardType.Default, true, false, false, false, look.typeHint);
    }

    void UpdatePhoneKeyboard()
    {
        if (_phoneKeys == null) return;
        _typed = _phoneKeys.text ?? "";
        if (_phoneKeys.status == TouchScreenKeyboard.Status.Done) { _phoneKeys = null; SendTyped(); }
        else if (_phoneKeys.status != TouchScreenKeyboard.Status.Visible) { _phoneKeys = null; _typed = ""; }
    }

    void OnDestroy()
    {
#if ENABLE_INPUT_SYSTEM
        if (_keysHooked != null) { _keysHooked.onTextInput -= OnTypedChar; _keysHooked = null; }
#endif
        if (actions != null) actions.FactsChanged -= OnFactsChanged;
        FreeStyles();
    }

    void Fail(string msg)
    {
        Debug.LogError("[AvatarKit] " + msg);
        _status = msg;
        _state = State.Error;
    }

    // ================================================================ presses

    // PRESSES ARE READ HERE, NOT IN OnGUI. On a phone, and in Unity's Device
    // Simulator, a press arrives as a touch and an OnGUI button never hears
    // it -- the button draws, and is deaf. AvatarKit 1's talk button had this
    // fault. So OnGUI only DRAWS; the rectangles it drew are tested here.
    Rect _talkCard, _textCard, _modeRect, _talkButton, _boxRect, _sendRect;

    float UiScale
    {
        get
        {
            float s = Mathf.Max(0.1f, look.size);
            if (look.growOnPhones && Screen.dpi > 0f) s *= Mathf.Clamp(Screen.dpi / 160f, 1f, 3f);
            return s;
        }
    }

    /// <summary>The finger or the mouse, in the same units OnGUI draws in.</summary>
    void ReadPointer(out Vector2 gui, out bool down, out bool held)
    {
        Vector2 pos = Vector2.zero; down = false; held = false;
#if ENABLE_INPUT_SYSTEM
        var touch = UnityEngine.InputSystem.Touchscreen.current;
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (touch != null && (touch.primaryTouch.press.isPressed || touch.primaryTouch.press.wasPressedThisFrame))
        {
            pos = touch.primaryTouch.position.ReadValue();
            down = touch.primaryTouch.press.wasPressedThisFrame;
            held = true;
        }
        else if (mouse != null && (mouse.leftButton.isPressed || mouse.leftButton.wasPressedThisFrame))
        {
            pos = mouse.position.ReadValue();
            down = mouse.leftButton.wasPressedThisFrame;
            held = true;
        }
#elif ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetMouseButton(0) || Input.GetMouseButtonDown(0))
        {
            pos = Input.mousePosition;
            down = Input.GetMouseButtonDown(0);
            held = true;
        }
#endif
        float s = UiScale;
        // input counts up from the bottom of the screen; OnGUI counts down from the top
        gui = new Vector2(pos.x / s, (Screen.height - pos.y) / s);
    }

    void OnPress(Vector2 at)
    {
        if (!showUI) return;
        if (_mode == ChatMode.Ask)
        {
            if (_talkCard.Contains(at)) ChooseMode(ChatMode.Talk);
            else if (_textCard.Contains(at)) ChooseMode(ChatMode.Text);
            return;
        }
        if (allowModeSwitch && _modeRect.Contains(at))
        {
            ChooseMode(TextMode ? ChatMode.Talk : ChatMode.Text);
            return;
        }
        if (TextMode)
        {
            if (_sendRect.Contains(at)) SendTyped();
            else if (_boxRect.Contains(at)) { _typing = true; OpenPhoneKeyboard(); }
            return;
        }
        if (_talkButton.Contains(at)) _buttonHeld = true;
    }

    // ================================================================= drawing

    GUIStyle _small, _box, _button, _card, _title, _chip, _input, _send, _you, _tick;
    readonly List<Texture2D> _textures = new List<Texture2D>();
    bool _stylesDirty = true;

    void OnValidate() { _stylesDirty = true; }       // Inspector edits restyle the UI at once, even in Play

    Texture2D Solid(Color c)
    {
        var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        t.SetPixel(0, 0, c);
        t.Apply();
        _textures.Add(t);
        return t;
    }

    void FreeStyles()
    {
        foreach (var t in _textures) if (t != null) Destroy(t);
        _textures.Clear();
    }

    GUIStyle Make(Color back, Color text, int size, TextAnchor align, bool wrap)
    {
        var s = new GUIStyle(GUI.skin.label)
        {
            font = look.font, fontSize = size, alignment = align, wordWrap = wrap, richText = true,
            padding = new RectOffset(12, 12, 8, 8)
        };
        s.normal.textColor = text;
        if (back.a > 0f) s.normal.background = Solid(back);
        return s;
    }

    void BuildStyles()
    {
        FreeStyles();
        int f = look.fontSize;
        var clear = new Color(0, 0, 0, 0);
        _small = Make(clear, look.faintTextColor, f - 1, TextAnchor.UpperLeft, true);
        _small.padding = new RectOffset(0, 0, 0, 0);
        _box = Make(look.replyBoxColor, look.replyTextColor, f + 3, TextAnchor.UpperLeft, true);
        _button = Make(look.talkButtonColor, look.talkButtonTextColor, f, TextAnchor.MiddleCenter, false);
        _card = Make(look.cardColor, look.cardTextColor, f + 1, TextAnchor.MiddleCenter, true);
        _title = Make(clear, look.textColor, f + 8, TextAnchor.MiddleCenter, true);
        _chip = Make(look.modeButtonColor, look.textColor, f - 2, TextAnchor.MiddleCenter, false);
        _input = Make(look.textBoxColor, look.typedTextColor, f, TextAnchor.MiddleLeft, false);
        _send = Make(look.sendButtonColor, look.textColor, f, TextAnchor.MiddleCenter, false);
        _you = Make(clear, look.yourWordsColor, f - 1, TextAnchor.LowerRight, true);
        _you.padding = new RectOffset(0, 0, 0, 0);
        _tick = Make(clear, look.whichKeyColor, f - 3, TextAnchor.UpperRight, false);
        _tick.padding = new RectOffset(0, 0, 0, 0);
        _stylesDirty = false;
    }

    void OnGUI()
    {
        if (!showUI) return;
        if (_stylesDirty || _small == null) BuildStyles();

        float s = UiScale;
        GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
        float w = Screen.width / s, h = Screen.height / s;
        Rect safe = Screen.safeArea;                        // keep clear of a phone's notch
        float top = (Screen.height - safe.yMax) / s + 14f;
        float left = safe.x / s + 16f;
        float bottom = h - safe.y / s - 20f;

        if (_state == State.Error)
        {
            GUI.Label(new Rect(left, top, w - left * 2f, Mathf.Min(260f, h - top - 20f)), "Can't start:\n\n" + _status, _box);
            return;
        }

        if (_mode == ChatMode.Ask) { DrawChooser(w, h, top); return; }

        // --- top-left: status, then the mode button, then the notes panel
        float y = top;
        if (look.showStatus && !string.IsNullOrEmpty(_status))
            GUI.Label(new Rect(left, y, w * 0.6f, 24f), _status, _small);
        y += 26f;
        if (allowModeSwitch)
        {
            _modeRect = new Rect(left, y, 210f, 34f);
            GUI.Label(_modeRect, TextMode ? look.textModeLabel : look.talkModeLabel, _chip);
            y += 42f;
        }
        else _modeRect = new Rect(0, 0, 0, 0);

        DrawKnowledgePanel(left, y, w);
        DrawTicker(w, top, left);

        if (look.showMemoryCount && MemoryOn)
            GUI.Label(new Rect(left, bottom - 4f, 260f, 22f),
                      "memory: " + memory.MemoryLineCount + " lines" +
                      (memory.IsUpdating ? " (updating...)" : ""), _small);

        string who = persona != null ? persona.characterName : "Character";
        float boxW = Mathf.Min(560f, w - left * 2f), boxX = (w - boxW) * 0.5f;

        if (TextMode) { DrawTextMode(w, bottom, boxX, boxW, who); return; }

        // --- Talk mode: the talk button, with the last lines above it
        Vector2 bs = look.talkButtonSize;
        _talkButton = new Rect((w - bs.x) * 0.5f, bottom - 24f - bs.y, bs.x, bs.y);
        bool active = _state == State.Recording;
        string label = LiveActive
            ? (_state == State.Speaking ? look.liveSpeakingLabel : _state == State.Thinking ? look.thinkingLabel : look.liveIdleLabel)
            : (_state == State.Recording ? look.listeningLabel
             : _state == State.Thinking ? look.thinkingLabel
             : _state == State.Speaking ? look.speakingLabel : look.idleLabel);
        var prev = GUI.color;
        if (active) GUI.color = look.talkButtonActiveColor * 1.6f;
        GUI.Label(_talkButton, label, _button);
        GUI.color = prev;

        float above = _talkButton.y - 12f;
        if (showReplyBox && !string.IsNullOrEmpty(_lastReply))
        {
            string text = who + ":  " + _lastReply;
            float bh = Mathf.Min(_box.CalcHeight(new GUIContent(text), boxW), h * 0.35f);
            GUI.Label(new Rect(boxX, above - bh, boxW, bh), text, _box);
            above -= bh + 6f;
        }
        if (!string.IsNullOrEmpty(_lastUser))
        {
            string text = "you:  " + _lastUser;
            float yh = Mathf.Min(_small.CalcHeight(new GUIContent(text), boxW), 60f);
            GUI.Label(new Rect(boxX, above - yh, boxW, yh), text, _small);
        }
    }

    void DrawChooser(float w, float h, float top)
    {
        var prev = GUI.color;
        GUI.color = look.screenDim;
        GUI.DrawTexture(new Rect(0, 0, w, h), Texture2D.whiteTexture);
        GUI.color = prev;

        float gap = 14f;
        float cw = Mathf.Min(look.cardSize.x, (w - 32f - gap) * 0.5f), ch = look.cardSize.y;
        float x0 = (w - (cw * 2f + gap)) * 0.5f, y0 = h * 0.5f - ch * 0.5f + 20f;
        GUI.Label(new Rect(16f, y0 - 70f, w - 32f, 56f), look.chooserTitle, _title);

        // side by side, never stacked: a press can not land on the wrong one
        _talkCard = new Rect(x0, y0, cw, ch);
        _textCard = new Rect(x0 + cw + gap, y0, cw, ch);
        int big = look.fontSize + 10;
        string talk = "<size=" + big + "><b>" + look.talkTitle + "</b></size>\n\n" +
                      (_micMissing ? "No microphone found." : look.talkNote);
        prev = GUI.color;
        if (_micMissing) GUI.color = new Color(1f, 1f, 1f, 0.45f);
        GUI.Label(_talkCard, talk, _card);
        GUI.color = prev;
        GUI.Label(_textCard, "<size=" + big + "><b>" + look.textTitle + "</b></size>\n\n" + look.textNote, _card);

        if (look.showStatus && !string.IsNullOrEmpty(_status))
            GUI.Label(new Rect(16f, y0 + ch + 14f, w - 32f, 40f), _status, _title);
    }

    void DrawTextMode(float w, float bottom, float boxX, float boxW, string who)
    {
        // --- the text box and Send
        float bw = Mathf.Min(look.textBoxWidth, w - 32f - 74f);
        _boxRect = new Rect((w - bw - 74f) * 0.5f, bottom - 24f - 40f, bw, 40f);
        _sendRect = new Rect(_boxRect.xMax + 6f, _boxRect.y, 68f, 40f);
        bool caret = _typing && (Time.unscaledTime % 1f) < 0.5f;
        var prev = GUI.color;
        if (_typed.Length == 0 && !_typing) GUI.color = new Color(1f, 1f, 1f, 0.55f);
        GUI.Label(_boxRect, _typed.Length > 0 || _typing ? _typed + (caret ? "|" : "") : look.typeHint, _input);
        GUI.color = prev;
        GUI.Label(_sendRect, look.sendLabel, _send);

        // --- what was said. With a speech bubble by her head, the bubble
        //     shows her words and only yours are drawn here.
        float above = _boxRect.y - 12f;
        bool bubbleShows = bubble != null && bubble.isActiveAndEnabled && !showReplyBox;
        if (!bubbleShows)
        {
            string hers = _state == State.Thinking ? ". . ." : _lastReply;
            if (!string.IsNullOrEmpty(hers))
            {
                string text = who + ":  " + hers;
                float bh = Mathf.Min(_box.CalcHeight(new GUIContent(text), boxW), Screen.height / UiScale * 0.4f);
                GUI.Label(new Rect(boxX, above - bh, boxW, bh), text, _box);
                above -= bh + 6f;
            }
        }
        if (!string.IsNullOrEmpty(_lastUser))
        {
            float yh = Mathf.Min(_you.CalcHeight(new GUIContent(_lastUser), boxW), 70f);
            GUI.Label(new Rect(boxX, above - yh, boxW, yh), _lastUser, _you);
        }
    }

    /// <summary>The retrieval instrumentation: WHICH chunks answered the last
    /// question, and how confidently. This panel is the whole A/B demo.</summary>
    void DrawKnowledgePanel(float left, float y, float w)
    {
        if (!showKnowledgePanel || persona == null || !persona.useKnowledge || _retrieved == null) return;
        GUI.Label(new Rect(left, y, w - left * 2f, 24f), "notes retrieved for: \"" + _lastUser + "\"", _small);
        y += 24f;
        foreach (var c in _retrieved)
        {
            string first = c.text.Trim();
            int nl = first.IndexOf('\n');
            if (nl > 0) first = first.Substring(0, nl);
            if (first.Length > 90) first = first.Substring(0, 90) + "...";
            GUI.Label(new Rect(left + 12f, y, w - left * 2f - 12f, 24f),
                      "[" + c.source + "  " + c.score.ToString("0.00") + "]  " + first, _small);
            y += 22f;
        }
    }

    /// <summary>Top-right corner: which key each call uses, as it happens.</summary>
    void DrawTicker(float w, float top, float left)
    {
        if (!look.showWhichKey) return;
        float now = Time.realtimeSinceStartup;
        var rect = new Rect(left, top, w - left * 2f, 20f);
        var prev = GUI.color;
        if (!string.IsNullOrEmpty(AvatarTicker.Live))
        {
            GUI.color = look.liveLineColor * 1.2f;
            GUI.Label(rect, "● " + AvatarTicker.Live, _tick);
            rect.y += 19f;
        }
        var recent = AvatarTicker.Recent;
        for (int i = recent.Count - 1; i >= 0; i--)
        {
            float a = 1f - Mathf.Clamp01((now - recent[i].at - 4f) / 2f);      // hold 4 s, fade over 2
            if (a <= 0.01f) continue;
            GUI.color = new Color(1f, 1f, 1f, a);
            GUI.Label(rect, recent[i].who + "  -  " + recent[i].what, _tick);
            rect.y += 18f;
        }
        GUI.color = prev;
    }
}
