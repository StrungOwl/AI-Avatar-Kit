using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// TASKS: THINGS THE AVATAR CAN DO, AND THINGS THE GAME KNOWS.  (AvatarKit 2)
///
/// The avatar never does a task by itself. It ASKS YOUR GAME to do it. You
/// write a short list that says what it may ask for; the avatar decides when.
///
///   DO SOMETHING   "Remind me how to jump."
///                  -> the avatar asks for the action "show_tutorial" with
///                     the text "jump" -> your game plays the video.
///                  Scene changes, opening a web page, starting a level,
///                  giving an item: all the same thing. One row in the list.
///
///   KNOW SOMETHING "How many friends did I invite to the potluck?"
///                  -> your game keeps a short list of facts up to date
///                     (SetFact("friends invited", "3")) and the avatar reads
///                     the list when it answers.
///
/// HOW TO USE IT, no code:
///   1. Put this on your character (the setup command adds it).
///   2. Press + under Actions. Type a name and WHEN to use it.
///   3. Under "What the game does", press + and drag in any object, then
///      pick what it should do. This component has three ready-made ones:
///      LoadScene, OpenLink and SetFactFromText.
///
/// Right-click the component -> "Add Example Actions" to see two filled in.
///
/// This is the same idea as "look in my notes" (see AvatarLive): a TOOL is
/// something the model may ask the app to do for it.
/// </summary>
[DisallowMultipleComponent]
public class AvatarActions : MonoBehaviour
{
    [Serializable] public class TextEvent : UnityEvent<string> { }

    [Serializable]
    public class Action
    {
        [Tooltip("A short name with no spaces, for example show_tutorial. The avatar never says it out loud.")]
        public string name = "show_tutorial";
        public bool on = true;
        [Tooltip("Tell her WHEN to ask for this, in plain words. This sentence is what makes it work.")]
        [TextArea(2, 4)]
        public string whenToUse = "Show a short tutorial video. Use it when the person asks how to do something in the game.";

        [Header("One piece of text she sends along (optional)")]
        [Tooltip("ON: she sends one piece of text with the request, for example WHICH tutorial.")]
        public bool sendsText = false;
        [Tooltip("A short name for that text, for example topic.")]
        public string textName = "topic";
        [Tooltip("Say what the text must be, for example: the move the person asked about.")]
        [TextArea(1, 3)]
        public string textMeans = "The thing the person asked about, in one or two words.";
        [Tooltip("Optional. If you fill this in, she can ONLY send one of these exact values. " +
                 "Use it for scene names, level names, video names.")]
        public string[] onlyTheseValues = new string[0];

        [Header("What the game does")]
        [Tooltip("Runs when she asks. Use this one when you do not need the text.")]
        public UnityEvent whenAsked = new UnityEvent();
        [Tooltip("Runs when she asks, and hands over the text she sent.")]
        public TextEvent whenAskedWithText = new TextEvent();
        [Tooltip("What she is told when it is done, so she can say something sensible.")]
        [TextArea(1, 3)]
        public string thenTellHer = "Done. It is on the screen now.";
    }

    [Serializable]
    public class Fact
    {
        public string name;
        public string value;
    }

    [Header("THINGS THE AVATAR CAN DO")]
    public List<Action> actions = new List<Action>();

    [Header("THINGS THE GAME KNOWS")]
    [Tooltip("Short facts about the game right now. Type some here, or set them from your game code with " +
             "SetFact(\"friends invited\", \"3\"). She reads the list when she answers.")]
    public List<Fact> facts = new List<Fact>();
    [Tooltip("The line that sits above the facts in her instructions.")]
    public string factsTitle = "What the game knows right now. Use these when the person asks about their progress:";

    [Header("Scene changes")]
    [Tooltip("ON: the avatar (and the conversation) stays when LoadScene loads a new scene. " +
             "OFF: she goes away with the old scene; put an avatar in the new scene too.")]
    public bool keepAvatarWhenSceneChanges = false;

    [Header("Debug")]
    [Tooltip("Print each request in the Console.")]
    public bool logRequests = true;

    /// <summary>Raised when a fact changes, so an open live line can be told.</summary>
    public event System.Action FactsChanged;

    string _reply;

    // ============================================================ the facts

    /// <summary>Call this from your game whenever something worth knowing changes.</summary>
    public void SetFact(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        foreach (var f in facts)
            if (f != null && f.name == name)
            {
                if (f.value == value) return;
                f.value = value;
                if (FactsChanged != null) FactsChanged();
                return;
            }
        facts.Add(new Fact { name = name, value = value });
        if (FactsChanged != null) FactsChanged();
    }

    public void SetFact(string name, int value) { SetFact(name, value.ToString()); }
    public void SetFact(string name, float value) { SetFact(name, value.ToString("0.##")); }

    /// <summary>The facts as text for her instructions. "" when there are none.</summary>
    public string FactsBlock
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var f in facts)
                if (f != null && !string.IsNullOrWhiteSpace(f.name))
                    sb.Append("\n- ").Append(f.name.Trim()).Append(": ").Append((f.value ?? "").Trim());
            return sb.Length == 0 ? "" : "\n\n" + factsTitle + sb;
        }
    }

    // ============================================== ready-made things to do

    /// <summary>Drag this component into an action and pick LoadScene. The
    /// scene must be in File > Build Profiles > Scene List.</summary>
    public void LoadScene(string sceneName)
    {
        sceneName = (sceneName ?? "").Trim();
        if (!Application.CanStreamedLevelBeLoaded(sceneName))
        {
            Reply("There is no place called '" + sceneName + "'. Tell the person you can not go there.");
            Debug.LogWarning("[AvatarKit] LoadScene: '" + sceneName + "' is not in the Scene List (File > Build Profiles).");
            return;
        }
        if (keepAvatarWhenSceneChanges) DontDestroyOnLoad(transform.root.gameObject);
        UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName);
    }

    /// <summary>Opens a web page (a tutorial video, a help page).</summary>
    public void OpenLink(string url)
    {
        if (!string.IsNullOrWhiteSpace(url)) Application.OpenURL(url.Trim());
    }

    /// <summary>For your own code: call this INSIDE an action to change what
    /// she is told, for example Reply("The video for 'jump' is playing.").</summary>
    public void Reply(string whatToTellHer) { _reply = whatToTellHer; }

    // ======================================================= the machinery

    public bool HasActions
    {
        get { foreach (var a in actions) if (Usable(a)) return true; return false; }
    }

    static bool Usable(Action a) { return a != null && a.on && !string.IsNullOrWhiteSpace(a.name); }

    /// <summary>Tool names may only hold letters, numbers and _ .</summary>
    public static string SafeName(string name)
    {
        return Regex.Replace((name ?? "").Trim().ToLowerInvariant().Replace(' ', '_'), "[^a-z0-9_-]", "");
    }

    public Action Find(string toolName)
    {
        foreach (var a in actions) if (Usable(a) && SafeName(a.name) == toolName) return a;
        return null;
    }

    /// <summary>She asked for an action. Runs it, and returns what to tell her.</summary>
    public string Run(string toolName, string argumentsJson)
    {
        var a = Find(toolName);
        if (a == null) return "That action does not exist. Tell the person you can not do that.";

        string text = a.sendsText ? ReadText(argumentsJson, SafeName(a.textName)) : "";
        if (a.sendsText && a.onlyTheseValues != null && a.onlyTheseValues.Length > 0)
        {
            string match = null;
            foreach (var v in a.onlyTheseValues)
                if (string.Equals(v, text, StringComparison.OrdinalIgnoreCase)) { match = v; break; }
            if (match == null) return "'" + text + "' is not one of the choices: " + string.Join(", ", a.onlyTheseValues) + ".";
            text = match;
        }

        if (logRequests) Debug.Log("[AvatarKit] action asked for: " + toolName + (a.sendsText ? " (" + text + ")" : ""));
        _reply = null;
        try
        {
            a.whenAsked.Invoke();
            a.whenAskedWithText.Invoke(text);
        }
        catch (Exception e)
        {
            Debug.LogError("[AvatarKit] action '" + toolName + "' failed: " + e.Message);
            return "Something went wrong and it did not work. Say sorry.";
        }
        return string.IsNullOrWhiteSpace(_reply) ? a.thenTellHer : _reply;
    }

    /// <summary>Pulls one text value out of {"topic":"jump"}.</summary>
    public static string ReadText(string json, string key)
    {
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return "";
        var m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
        return m.Success ? Regex.Unescape(m.Groups[1].Value) : "";
    }

    /// <summary>The list, written the way the model wants it.
    /// liveShape = the live line's shape; otherwise the chat call's shape.</summary>
    public string ToolsJson(bool liveShape)
    {
        var sb = new StringBuilder();
        foreach (var a in actions)
        {
            if (!Usable(a)) continue;
            if (sb.Length > 0) sb.Append(',');
            string inner = "\"name\":\"" + SafeName(a.name) + "\",\"description\":\"" + AvatarVoiceAPI.Escape(a.whenToUse) +
                           "\",\"parameters\":" + ParametersJson(a);
            sb.Append(liveShape ? "{\"type\":\"function\"," + inner + "}"
                                : "{\"type\":\"function\",\"function\":{" + inner + "}}");
        }
        return sb.ToString();
    }

    public static string ParametersJson(Action a)
    {
        if (!a.sendsText) return "{\"type\":\"object\",\"properties\":{}}";
        var sb = new StringBuilder("{\"type\":\"object\",\"properties\":{\"");
        sb.Append(SafeName(a.textName)).Append("\":{\"type\":\"string\",\"description\":\"")
          .Append(AvatarVoiceAPI.Escape(a.textMeans)).Append('"');
        if (a.onlyTheseValues != null && a.onlyTheseValues.Length > 0)
        {
            sb.Append(",\"enum\":[");
            for (int i = 0; i < a.onlyTheseValues.Length; i++)
                sb.Append(i > 0 ? "," : "").Append('"').Append(AvatarVoiceAPI.Escape(a.onlyTheseValues[i])).Append('"');
            sb.Append(']');
        }
        sb.Append("}},\"required\":[\"").Append(SafeName(a.textName)).Append("\"]}");
        return sb.ToString();
    }

    [ContextMenu("Add Example Actions")]
    void AddExamples()
    {
#if UNITY_EDITOR
        UnityEditor.Undo.RecordObject(this, "Add Example Actions");
#endif
        actions.Add(new Action
        {
            name = "show_tutorial",
            whenToUse = "Show a short tutorial video. Use it when the person asks how to do something in the game, or asks to be reminded.",
            sendsText = true, textName = "topic", textMeans = "The move or task the person asked about, in one or two words.",
            thenTellHer = "The tutorial is on the screen now. Tell the person to watch it."
        });
        actions.Add(new Action
        {
            name = "go_to_place",
            whenToUse = "Take the person to another place in the game. Use it when they ask to go somewhere.",
            sendsText = true, textName = "place", textMeans = "The name of the place.",
            onlyTheseValues = new[] { "Kitchen", "Garden" },
            thenTellHer = "We are on the way there now."
        });
        if (facts.Count == 0)
        {
            facts.Add(new Fact { name = "minutes played", value = "0" });
            facts.Add(new Fact { name = "friends invited", value = "0" });
        }
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }
}
