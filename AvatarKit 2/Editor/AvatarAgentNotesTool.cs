using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEditor;

/// <summary>
/// ONE CLICK: let an ElevenLabs agent read THIS project's knowledge notes.
///
///   Tools -> AI Avatar -> Add Notes And Actions To My ElevenLabs Agent
///
/// It adds the look_in_notes tool, and one tool for each action in your
/// AvatarActions list. Run it again after you add a new action.
///
/// In live talk the agent can not see your knowledge files by itself. It
/// needs a "client tool": a thing it may ask the APP to do. This command adds
/// a client tool named look_in_notes to your agent. After that, when a
/// question needs a fact, the agent asks the app, AvatarBrain finds the best
/// notes, and the agent answers from them.
///
/// BY HAND, on elevenlabs.io, the same thing is six steps:
///   1. Open your agent and click Tools.
///   2. Click Add tool and pick Client tool.
///   3. Name: look_in_notes
///   4. Add one text parameter named: question
///   5. Switch on "Wait for response".
///   6. Save, then publish the agent.
///
/// It needs your ElevenLabs key in a file on THIS computer (the key never
/// goes into the app). The key must be allowed to change agents: on
/// elevenlabs.io -> API keys -> your key -> ElevenAgents = Write.
/// </summary>
public static class AvatarAgentNotesTool
{
    const string ToolName = "look_in_notes";

    [Serializable] class ToolConfig { public string name; }
    [Serializable] class ToolItem { public string id; public ToolConfig tool_config; }
    [Serializable] class ToolList { public ToolItem[] tools; }
    [Serializable] class PromptPart { public string[] tool_ids; }
    [Serializable] class AgentPart { public PromptPart prompt; }
    [Serializable] class ConfigPart { public AgentPart agent; }
    [Serializable] class AgentReply { public ConfigPart conversation_config; }

    [MenuItem("Tools/AI Avatar/Add Notes And Actions To My ElevenLabs Agent")]
    static void Run()
    {
        const string title = "Add Notes Tool";
        var live = UnityEngine.Object.FindAnyObjectByType<AvatarLive>();
        if (live == null || string.IsNullOrWhiteSpace(live.elevenLabsAgentId))
        {
            EditorUtility.DisplayDialog(title,
                "No Agent ID found.\n\nSelect your character, find the AvatarLive component, and paste your " +
                "ElevenLabs Agent ID into it first.", "OK");
            return;
        }
        if (!AvatarVoiceAPI.HasElevenLabsKey)
        {
            EditorUtility.DisplayDialog(title,
                "This command needs your ElevenLabs key in a file on this computer.\n\n" +
                "Make a plain text file with nothing in it but the key, here:\n" + AvatarVoiceAPI.ElevenLabsKeyPath +
                "\n\nThe key must be allowed to change agents: elevenlabs.io -> API keys -> your key -> " +
                "ElevenAgents = Write.\n\nThe file stays on this computer. It does not go into the app.", "OK");
            return;
        }

        string agentId = live.elevenLabsAgentId.Trim();
        string root = (AvatarConnection.Current != null ? AvatarConnection.Current.elevenLabsBase : "https://api.elevenlabs.io").TrimEnd('/');
        string error;

        try
        {
            // 1. what should the agent have? the notes tool, and each action
            var wanted = new List<string[]>();          // { name, description, parameters as JSON }
            wanted.Add(new[] { ToolName, live.whenToLook,
                "{\"type\":\"object\",\"properties\":{\"question\":{\"type\":\"string\"," +
                "\"description\":\"The question to look up, in full\"}},\"required\":[\"question\"]}" });
            var acts = UnityEngine.Object.FindAnyObjectByType<AvatarActions>();
            if (acts != null)
                foreach (var a in acts.actions)
                    if (a != null && a.on && !string.IsNullOrWhiteSpace(a.name))
                        wanted.Add(new[] { AvatarActions.SafeName(a.name), a.whenToUse, AvatarActions.ParametersJson(a) });

            // 2. which of them are on this account already? make the rest
            EditorUtility.DisplayProgressBar(title, "Looking for the tools...", 0.2f);
            string listJson = Call("GET", root + "/v1/convai/tools", null, out error);
            if (listJson == null) { Fail(title, error); return; }
            var list = JsonUtility.FromJson<ToolList>(listJson);
            var toolIds = new List<string>();
            int madeCount = 0;
            foreach (var w in wanted)
            {
                string id = null;
                if (list != null && list.tools != null)
                    foreach (var t in list.tools)
                        if (t != null && t.tool_config != null && t.tool_config.name == w[0]) { id = t.id; break; }
                if (id == null)
                {
                    EditorUtility.DisplayProgressBar(title, "Making the tool " + w[0] + "...", 0.5f);
                    string body =
                        "{\"tool_config\":{\"type\":\"client\",\"name\":\"" + w[0] + "\"," +
                        "\"description\":\"" + AvatarVoiceAPI.Escape(w[1]) + "\"," +
                        "\"parameters\":" + w[2] + "," +
                        "\"expects_response\":true,\"response_timeout_secs\":15}}";
                    string madeJson = Call("POST", root + "/v1/convai/tools", body, out error);
                    if (madeJson == null) { Fail(title, error); return; }
                    var item = JsonUtility.FromJson<ToolItem>(madeJson);
                    id = item != null ? item.id : null;
                    if (string.IsNullOrEmpty(id)) { Fail(title, "ElevenLabs answered, but gave no tool id."); return; }
                    madeCount++;
                }
                toolIds.Add(id);
            }

            // 3. which tools does the agent have now?
            EditorUtility.DisplayProgressBar(title, "Reading the agent...", 0.7f);
            string agentJson = Call("GET", root + "/v1/convai/agents/" + UnityWebRequest.EscapeURL(agentId), null, out error);
            if (agentJson == null) { Fail(title, error); return; }
            var ids = new List<string>();
            var agent = JsonUtility.FromJson<AgentReply>(agentJson);
            if (agent != null && agent.conversation_config != null && agent.conversation_config.agent != null &&
                agent.conversation_config.agent.prompt != null && agent.conversation_config.agent.prompt.tool_ids != null)
                ids.AddRange(agent.conversation_config.agent.prompt.tool_ids);

            int added = 0;
            foreach (var id in toolIds) if (!ids.Contains(id)) { ids.Add(id); added++; }
            if (added == 0)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog(title, "Your agent already has all " + toolIds.Count + " tool(s). Nothing to do.", "OK");
                return;
            }

            // 4. add ours, keeping the ones it had
            EditorUtility.DisplayProgressBar(title, "Adding the tool to the agent...", 0.9f);
            var sb = new StringBuilder("{\"conversation_config\":{\"agent\":{\"prompt\":{\"tool_ids\":[");
            for (int i = 0; i < ids.Count; i++)
                sb.Append(i > 0 ? "," : "").Append('"').Append(AvatarVoiceAPI.Escape(ids[i])).Append('"');
            sb.Append("]}}}}");
            if (Call("PATCH", root + "/v1/convai/agents/" + UnityWebRequest.EscapeURL(agentId), sb.ToString(), out error) == null)
            { Fail(title, error); return; }

            EditorUtility.ClearProgressBar();
            string names = "";
            foreach (var w in wanted) names += (names.Length > 0 ? ", " : "") + w[0];
            Debug.Log("[AvatarKit] Agent " + agentId + " now has these tools: " + names + " (" + added + " added, " + madeCount + " made new).");
            EditorUtility.DisplayDialog(title,
                "Added " + added + " tool(s) to your agent.\n\nYour agent can now use: " + names + "\n\n" +
                "Run this again after you add a new action.\nTo see them: elevenlabs.io -> your agent -> Tools.", "OK");
        }
        finally { EditorUtility.ClearProgressBar(); }
    }

    static void Fail(string title, string error)
    {
        EditorUtility.ClearProgressBar();
        Debug.LogError("[AvatarKit] " + title + ": " + error);
        EditorUtility.DisplayDialog(title, "It did not work.\n\n" + error, "OK");
    }

    /// <summary>One web call that waits for its answer (fine in the Editor).
    /// Returns the answer text, or null with the reason in `error`.</summary>
    static string Call(string method, string url, string body, out string error)
    {
        error = null;
        using (var req = new UnityWebRequest(url, method))
        {
            if (body != null)
            {
                req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                req.SetRequestHeader("Content-Type", "application/json");
            }
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("xi-api-key", AvatarVoiceAPI.ElevenLabsApiKey);
            req.timeout = 30;
            var op = req.SendWebRequest();
            while (!op.isDone) System.Threading.Thread.Sleep(15);
            string text = req.downloadHandler != null ? req.downloadHandler.text : "";
            if (req.result != UnityWebRequest.Result.Success)
            {
                if (text.Length > 300) text = text.Substring(0, 300);
                error = "ElevenLabs said " + req.responseCode + ". " +
                        (req.responseCode == 401 ? "The key is wrong, or it is not allowed to change agents " +
                                                   "(elevenlabs.io -> API keys -> your key -> ElevenAgents = Write). "
                         : req.responseCode == 404 ? "The Agent ID was not found on this account. " : "") + text;
                return null;
            }
            return text;
        }
    }
}
