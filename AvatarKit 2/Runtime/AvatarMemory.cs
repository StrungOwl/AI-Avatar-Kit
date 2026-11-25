using System;
using System.IO;
using System.Collections;
using UnityEngine;

/// <summary>
/// LONG-TERM MEMORY: what your character remembers about the person,
/// across Play sessions, as one plain text file you can open and read.
///
/// Two files, one loop:
///
///   converse -> journal every exchange to disk   (instant, no API call)
///                        |
///        every few exchanges, and at next launch:
///                        v
///   distil: old notes + journal -> updated notes (one background API call)
///                        |
///                        v
///   the notes ride the system prompt on every call -- and on launch,
///   the character GREETS YOU from what they remember.
///
/// Files (named after your character, next to the project like the API key):
///   {name}_memory.md   -- their notes about the person. THEY write it.
///   {name}_pending.txt -- the journal of not-yet-distilled exchanges.
///
/// WHY A JOURNAL AND NOT JUST AN API CALL: distilling needs the model, and
/// you cannot trust an API call to finish before Play mode dies -- there is
/// no reliable "on exit" moment in the Editor. So the raw material is written
/// to disk THE MOMENT it happens, and whatever wasn't distilled yet gets
/// folded in at the start of the NEXT session. Rule worth memorising:
/// never trust an exit hook -- journal now, reconcile at startup.
///
/// THE HONEST CAVEAT (say it in your demo): these files live on this machine
/// and nowhere else -- delete them and she has genuinely forgotten. But every
/// conversation turn is still PROCESSED by the model provider's API. Where
/// memory is STORED is our choice; where words are PROCESSED is theirs.
///
/// This component's enable checkbox IS the memory-vs-privacy toggle from the
/// design sheet: untick it (or remove it) and you have an anonymous companion
/// that keeps nothing.
/// </summary>
public class AvatarMemory : MonoBehaviour
{
    [Tooltip("Distil the journal into the notes file every N exchanges. " +
             "Smaller = fresher notes but more API calls. 3 is comfortable.")]
    public int updateEveryExchanges = 3;

    [Tooltip("File name prefix. Leave empty to use the Persona's character " +
             "name, lowercased -- e.g. 'sydney_memory.md'.")]
    public string fileLabel = "";

    string _memory;          // cached notes; rewritten by updates
    int _sinceUpdate;
    bool _updating;

    public bool IsUpdating { get { return _updating; } }

    // ------------------------------------------------------------------ paths

    /// <summary>Same rule as the API key: next to the project on desktop
    /// (visible, editable, out of the repo -- add *_memory.md and
    /// *_pending.txt to .gitignore), persistentDataPath on a phone.</summary>
    static string Dir
    {
        get
        {
#if UNITY_ANDROID || UNITY_IOS
            return Application.persistentDataPath;
#else
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
#endif
        }
    }

    string Label
    {
        get
        {
            if (!string.IsNullOrEmpty(fileLabel)) return fileLabel;
            var brain = GetComponent<AvatarBrain>();
            string name = (brain != null && brain.persona != null)
                          ? brain.persona.characterName : null;
            if (string.IsNullOrEmpty(name)) return "avatar";
            var sb = new System.Text.StringBuilder();
            foreach (char c in name.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.Length > 0 ? sb.ToString() : "avatar";
        }
    }

    public string MemoryPath { get { return Path.Combine(Dir, Label + "_memory.md"); } }
    public string PendingPath { get { return Path.Combine(Dir, Label + "_pending.txt"); } }

    // ---------------------------------------------------------------- journal

    public string PendingText { get { return ReadOrEmpty(PendingPath); } }
    public bool HasPending { get { return !string.IsNullOrWhiteSpace(PendingText); } }

    void AppendPending(string exchange)
    {
        try { File.AppendAllText(PendingPath, exchange + "\n\n"); }
        catch (Exception e) { Debug.LogError("[AvatarKit] journal write failed: " + e.Message); }
    }

    void ClearPending()
    {
        try { if (File.Exists(PendingPath)) File.Delete(PendingPath); }
        catch (Exception e) { Debug.LogError("[AvatarKit] journal clear failed: " + e.Message); }
    }

    // ------------------------------------------------------------------ notes

    public string MemoryText
    {
        get
        {
            if (_memory == null) _memory = ReadOrEmpty(MemoryPath);
            return _memory;
        }
    }

    public bool HasMemory { get { return !string.IsNullOrWhiteSpace(MemoryText); } }

    public int MemoryLineCount
    {
        get { return HasMemory ? MemoryText.Split('\n').Length : 0; }
    }

    /// <summary>
    /// The read side of memory, in its entirety: a string appended to the
    /// system prompt on every call. Memory is not a database query -- it is
    /// string concatenation into the context window. The override sentence
    /// exists because of a real bug: when persona and memory disagreed about
    /// who she was talking to, the persona won and confabulated confidence.
    /// Persona carries the CHARACTER; memory carries the RELATIONSHIP.
    /// </summary>
    public string PromptAddition
    {
        get
        {
            if (!HasMemory) return "";
            return "\n\nWhat you remember about your person from earlier " +
                   "conversations. This IS your record of past sessions -- if " +
                   "they ask whether you remember, you do; these notes are what " +
                   "you remember. These notes OVERRIDE anything assumed earlier " +
                   "in this prompt about who you are talking to. Use them the " +
                   "way an old friend would: specifically and rarely, never as " +
                   "a list. If a memory conflicts with what they tell you now, " +
                   "trust them now:\n" + MemoryText.Trim();
        }
    }

    // ------------------------------------------------------------- the loop

    /// <summary>Called by AvatarBrain after every completed exchange.</summary>
    public void NotifyExchange(string personSaid, string characterSaid, CharacterPersona persona)
    {
        string who = (persona != null && !string.IsNullOrEmpty(persona.characterName))
                     ? persona.characterName : "Character";
        AppendPending("Person: " + personSaid + "\n" + who + ": " + characterSaid);
        _sinceUpdate++;
        if (updateEveryExchanges > 0 && _sinceUpdate >= updateEveryExchanges)
            StartCoroutine(UpdateNow(persona));
    }

    /// <summary>
    /// The distil pass. Runs in the background every few exchanges, and --
    /// crucially -- at launch, BEFORE the greeting, so a journal left over
    /// from last session (you stopped Play mid-conversation) is folded in
    /// first and the greeting already knows.
    /// </summary>
    public IEnumerator UpdateNow(CharacterPersona persona)
    {
        if (_updating || persona == null) yield break;
        string recent = PendingText;
        if (string.IsNullOrWhiteSpace(recent)) yield break;
        _updating = true;

        string request = "CURRENT NOTES:\n" +
                         (HasMemory ? MemoryText : "(none yet)") +
                         "\n\nRECENT CONVERSATION:\n" + recent;
        string updated = null;
        yield return StartCoroutine(AvatarVoiceAPI.Complete(
            persona.chatModel, UpdaterPrompt(persona), request, 900,
            r => updated = r,
            e => Debug.LogWarning("[AvatarKit] memory update failed " +
                                  "(journal kept, will retry later): " + e)));
        if (!string.IsNullOrWhiteSpace(updated))
        {
            SaveMemory(updated);
            ClearPending();
            _sinceUpdate = 0;
        }
        _updating = false;
    }

    /// <summary>
    /// The instructions for the background update call. Most of the memory
    /// engineering lives in this prompt -- every clause exists because its
    /// absence produced a real bug. Read it before you change it.
    /// </summary>
    public static string UpdaterPrompt(CharacterPersona persona)
    {
        string who = (persona != null && !string.IsNullOrEmpty(persona.characterName))
                     ? persona.characterName : "the character";
        return
        "You maintain the private long-term memory notes of " + who + ", a companion " +
        "avatar, about the person they talk with. Merge the recent conversation " +
        "into the existing notes. Keep: stable facts about the person (name, work, " +
        "people and pets in their life), preferences and tastes, ongoing projects " +
        "and plans, things they said mattered to them, moments worth remembering, " +
        "and open threads worth following up on later. Drop small talk. Prefer " +
        "updating an existing line over adding a near-duplicate. Refer to the " +
        "person as 'they' or 'them' -- or by name once they have given one; the " +
        "transcript label 'Person:' is a label, never a name. " + who + " is the " +
        "avatar's name, never the person's: lines labelled '" + who + ":' are the " +
        "avatar's own words, so never record them as facts about the person. " +
        "Record only what the person actually said. The transcript comes from " +
        "imperfect speech-to-text, so if a word looks misheard, leave it out " +
        "rather than guessing -- never invent titles, honorifics or forms of " +
        "address ('master', 'sir', 'boss') that the person did not clearly use. " +
        "If an existing note contradicts the conversation or looks like a " +
        "transcription error, correct or delete it. Write plain " +
        "prose, one fact per line, no markdown, no headings. Keep it under 40 " +
        "lines -- when full, merge or drop the least important lines. Reply with " +
        "ONLY the complete updated notes and nothing else.";
    }

    void SaveMemory(string updatedNotes)
    {
        try
        {
            _memory = (updatedNotes ?? "").Trim();
            File.WriteAllText(MemoryPath, _memory);
            Debug.Log("[AvatarKit] memory updated -- " + MemoryLineCount +
                      " lines at " + MemoryPath);
        }
        catch (Exception e) { Debug.LogError("[AvatarKit] memory save failed: " + e.Message); }
    }

    // --------------------------------------------------------------- controls

    [ContextMenu("Memory: Update Now")]
    void CtxUpdateNow()
    {
        var brain = GetComponent<AvatarBrain>();
        if (brain != null && Application.isPlaying)
            StartCoroutine(UpdateNow(brain.persona));
    }

    /// <summary>Forget everything about the person. The file is archived with
    /// a timestamp rather than destroyed, so a mis-click is recoverable.</summary>
    [ContextMenu("Memory: Clear (forget everything about them)")]
    public void ClearMemory()
    {
        try
        {
            if (File.Exists(MemoryPath))
            {
                string archived = MemoryPath + "." +
                    DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak";
                File.Move(MemoryPath, archived);
                Debug.Log("[AvatarKit] memory cleared (archived to " + archived + ")");
            }
            _memory = "";
            ClearPending();
            _sinceUpdate = 0;
        }
        catch (Exception e) { Debug.LogError("[AvatarKit] memory clear failed: " + e.Message); }
    }

    [ContextMenu("Memory: Reload file from disk")]
    public void ReloadFromDisk()
    {
        _memory = null;
        Debug.Log("[AvatarKit] memory: " + MemoryLineCount + " lines at " + MemoryPath);
    }

    static string ReadOrEmpty(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (Exception e)
        {
            Debug.LogError("[AvatarKit] could not read " + path + ": " + e.Message);
            return "";
        }
    }
}
