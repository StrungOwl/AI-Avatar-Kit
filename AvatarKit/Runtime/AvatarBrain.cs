using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// THE CONVERSATION LOOP.
///
///   hold SPACE (or the on-screen button) -> record from the mic
///   release                              -> WAV -> speech-to-text
///   transcript                           -> chat completion (persona + history)
///   reply                                -> text-to-speech -> AudioSource
///   AudioSource                          -> AvatarLipSync moves the mouth
///
/// WHERE THE MEMORY LIVES -- there are two, and the difference is the lesson:
///   SHORT-TERM: the _history list below. In RAM, on this machine. It
///     disappears when you press Stop. The feeling of a continuous
///     conversation is created by re-sending the whole transcript on every
///     single request.
///   LONG-TERM: the AvatarMemory component (if present) -- plain text files
///     next to the project that survive Stop, get distilled in the background,
///     and ride the system prompt on every call. That component is what turns
///     a chat bot into a companion.
///
/// The UI is IMGUI (OnGUI) on purpose: no Canvas, no EventSystem, no font
/// asset, no TextMeshPro import step. Fewer things to break on someone else's
/// machine. Replace it with real UI once everything works.
/// </summary>
public class AvatarBrain : MonoBehaviour
{
    public enum State { Idle, Recording, Thinking, Speaking, Error }

    [Header("YOUR CHARACTER -- drag your Persona asset here")]
    public CharacterPersona persona;

    [Header("Wiring (leave empty to auto-find)")]
    public AudioSource voice;
    public AvatarLipSync lipSync;
    public AvatarBody body;
    public AvatarMemory memory;   // optional -- remove/disable for an anonymous companion
    [Tooltip("Optional. Pick OpenAI or ElevenLabs here. Empty = the voice on the Persona.")]
    public AvatarVoice avatarVoice;

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

    /// <summary>The line she is saying (or just said). AvatarSpeechBubble reads this.</summary>
    public string LastReply { get { return _lastReply; } }

    State _state = State.Idle;
    string _status = "", _lastUser = "", _lastReply = "", _mic;
    AudioClip _recording;
    float _recordStart;
    readonly List<AvatarVoiceAPI.Msg> _history = new List<AvatarVoiceAPI.Msg>();
    readonly AvatarKnowledge _knowledge = new AvatarKnowledge();
    KnowledgeChunk[] _retrieved;
    GUIStyle _small, _bubble;
    bool _buttonHeld;

    void Start()
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

        if (persona == null)
        {
            Fail("No Persona assigned. Create one: right-click in Project -> " +
                 "Create -> AI Avatar -> Character Persona, then drag it onto this component.");
            return;
        }
        if (!AvatarVoiceAPI.HasKey)
        {
            Fail("No API key. Create a text file containing your key at:\n" + AvatarVoiceAPI.KeyPath);
            return;
        }
        if (VoiceOn && avatarVoice.Problem() != null)
        {
            Fail(avatarVoice.Problem());
            return;
        }
        if (Microphone.devices.Length == 0)
        {
            Fail("No microphone detected. Plug one in and press Play again.");
            return;
        }

        int idx = Mathf.Clamp(microphoneIndex, 0, Microphone.devices.Length - 1);
        _mic = Microphone.devices[idx];
        Debug.Log("[AvatarKit] Using microphone: " + _mic +
                  "  (" + Microphone.devices.Length + " available)");

        if (_knowledge.TryLoad())
            Debug.Log("[AvatarKit] Knowledge index loaded: " + _knowledge.ChunkCount +
                      " chunks. (" + (persona.useKnowledge ? "useKnowledge is ON" :
                      "useKnowledge is OFF on the Persona") + ")");

        StartCoroutine(Boot());
    }

    bool MemoryOn { get { return memory != null && memory.isActiveAndEnabled; } }
    bool VoiceOn { get { return avatarVoice != null && avatarVoice.enabled; } }

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
        if (greetOnStart && !string.IsNullOrEmpty(persona.greeting))
            yield return StartCoroutine(Greet());
    }

    IEnumerator Greet()
    {
        yield return new WaitForSeconds(0.5f);

        // With notes on disk she greets like someone who remembers you, not a
        // doorbell. Falls back to the fixed greeting on any hiccup.
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

        _lastReply = line;
        _history.Add(new AvatarVoiceAPI.Msg { role = "assistant", content = line });
        yield return StartCoroutine(SpeakLine(line));
    }

    void Update()
    {
        if (_state == State.Error) return;

        bool held = _buttonHeld || TalkKeyHeld();
        if (held && _state == State.Idle) StartRecording();
        else if (!held && _state == State.Recording) StopAndSend();

        if (_state == State.Recording && Time.time - _recordStart > recordSeconds - 0.25f)
            StopAndSend();

        if (body != null)
            body.SetMood(_state == State.Recording ? AvatarBody.Mood.Listening :
                         _state == State.Speaking ? AvatarBody.Mood.Talking :
                                                    AvatarBody.Mood.Idle);

        if (_state == State.Speaking && voice != null && !voice.isPlaying)
        {
            _state = State.Idle;
            _status = "";
        }
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

    void StartRecording()
    {
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

        if (written < sampleRate / 4)          // under a quarter second = mis-tap
        {
            _state = State.Idle; _status = ""; return;
        }
        var wav = AvatarWav.FromAudioClip(_recording, written);
        _recording = null;
        _state = State.Thinking;
        _status = "thinking...";
        StartCoroutine(RunTurn(wav));
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

        _lastUser = heard.Trim();
        _history.Add(new AvatarVoiceAPI.Msg { role = "user", content = _lastUser });
        while (_history.Count > Mathf.Max(2, persona.memoryTurns)) _history.RemoveAt(0);

        // The librarian runs BEFORE the brain: embed the question, fetch the
        // three nearest chunks, and let them ride the system prompt. If the
        // embed call hiccups we answer without notes rather than not at all.
        string context = null;
        _retrieved = null;
        if (persona.useKnowledge && _knowledge.Loaded)
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

        string extraSystem = (MemoryOn ? memory.PromptAddition : "") + (context ?? "");
        string reply = null;
        yield return StartCoroutine(AvatarVoiceAPI.Chat(persona, _history, r => reply = r, Fail,
            extraSystem.Length > 0 ? extraSystem : null));
        if (_state == State.Error) yield break;

        _lastReply = reply.Trim();
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
        _status = "speaking...";
        AudioClip clip = null;
        // AvatarVoice (if present) picks OpenAI or ElevenLabs; otherwise the Persona's voice.
        var speak = VoiceOn ? avatarVoice.Speak(line, c => clip = c, Fail)
                            : AvatarVoiceAPI.Speak(persona, line, c => clip = c, Fail);
        yield return StartCoroutine(speak);
        if (_state == State.Error) yield break;

        if (voice != null && clip != null)
        {
            voice.clip = clip;
            voice.Play();
            _state = State.Speaking;
        }
        else _state = State.Idle;
    }

    void Fail(string msg)
    {
        Debug.LogError("[AvatarKit] " + msg);
        _status = msg;
        _state = State.Error;
    }

    void OnGUI()
    {
        if (!showUI) return;
        if (_small == null)
        {
            _small = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
            _small.normal.textColor = new Color(0.75f, 0.78f, 0.8f);
            _bubble = new GUIStyle(GUI.skin.box)
            { wordWrap = true, fontSize = 19, padding = new RectOffset(14, 14, 12, 12) };
            _bubble.normal.textColor = Color.white;
            _bubble.alignment = TextAnchor.UpperLeft;
        }

        float w = Screen.width, h = Screen.height;
        if (_state == State.Error)
        {
            GUI.Box(new Rect(24, 20, w - 48, 150), "Can't start:\n\n" + _status, _bubble);
            return;
        }

        string who = persona != null ? persona.characterName : "Character";
        if (showReplyBox && !string.IsNullOrEmpty(_lastReply))
            GUI.Box(new Rect(24, h - 210, w - 48, 96), who + ":  " + _lastReply, _bubble);
        if (!string.IsNullOrEmpty(_lastUser))
            GUI.Label(new Rect(28, h - 246, w - 56, 30), "you:  " + _lastUser, _small);

        var r = new Rect(w * 0.5f - 150, h - 96, 300, 60);
        string label = _state == State.Recording ? "listening -- release to send"
                     : _state == State.Thinking ? "thinking..."
                     : _state == State.Speaking ? "speaking (hold to interrupt)"
                                                : "HOLD to talk  (or hold SPACE)";
        GUI.Button(r, label);
        if (Event.current.type == EventType.MouseDown && r.Contains(Event.current.mousePosition))
            _buttonHeld = true;
        if (Event.current.type == EventType.MouseUp) _buttonHeld = false;

        if (!string.IsNullOrEmpty(_status))
            GUI.Label(new Rect(24, 18, w - 48, 26), _status, _small);

        // "it runs" and "it works" are different claims -- show the memory
        // line count so students can SEE the notes grow.
        if (MemoryOn)
            GUI.Label(new Rect(w - 300, 18, 276, 26),
                      "memory: " + memory.MemoryLineCount + " lines" +
                      (memory.IsUpdating ? " (updating...)" : ""), _small);

        // The retrieval instrumentation: WHICH chunks answered the last
        // question, and how confidently. This panel is the whole A/B demo.
        if (showKnowledgePanel && persona != null && persona.useKnowledge && _retrieved != null)
        {
            float y = 46;
            GUI.Label(new Rect(24, y, w - 48, 24), "notes retrieved for: \"" +
                      _lastUser + "\"", _small);
            y += 24;
            foreach (var c in _retrieved)
            {
                string first = c.text.Trim();
                int nl = first.IndexOf('\n');
                if (nl > 0) first = first.Substring(0, nl);
                if (first.Length > 90) first = first.Substring(0, 90) + "...";
                GUI.Label(new Rect(36, y, w - 72, 24),
                          "[" + c.source + "  " + c.score.ToString("0.00") + "]  " + first,
                          _small);
                y += 22;
            }
        }
    }
}
