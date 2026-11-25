using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEditor;

/// <summary>
/// THE INDEXER: run once whenever the knowledge files change.
///
///   Tools -> AI Avatar -> Build Knowledge Index
///
/// Reads every Assets/Knowledge/*.md, splits it into paragraph-sized chunks,
/// asks the embeddings API for each chunk's coordinates in meaning-space, and
/// writes everything to Assets/Knowledge/knowledge.json.
///
/// Chunking rule (the industry standard, in plain words): split on blank
/// lines, merge tiny fragments into their neighbour. That is why the writing
/// rule for knowledge files is ONE TOPIC PER PARAGRAPH -- the paragraph is
/// the unit of retrieval, and it must stand alone.
///
/// Cost, out loud: indexing a whole classroom's knowledge folders costs
/// under a cent, total.
/// </summary>
public static class KnowledgeIndexer
{
    const string Folder = "Assets/Knowledge";
    const int MergeBelowChars = 200;   // fragments shorter than this join the next paragraph

    [MenuItem("Tools/AI Avatar/Build Knowledge Index")]
    static void Build()
    {
        // AvatarKit 2: the index is built through the same connection as the
        // app (your Worker, or the key file).
        string problem = AvatarConnection.Problem();
        if (problem != null)
        {
            EditorUtility.DisplayDialog("Build Knowledge Index", problem, "OK");
            return;
        }
        if (!Directory.Exists(Folder))
        {
            EditorUtility.DisplayDialog("Build Knowledge Index",
                "Create a folder called 'Knowledge' inside Assets and put your " +
                ".md files in it.\n\nRules that matter: one topic per paragraph, " +
                "facts stated absolutely, and deliberately leave one topic out " +
                "(that gap powers the refusal demo).", "OK");
            return;
        }

        var files = Directory.GetFiles(Folder, "*.md");
        var chunks = new List<KnowledgeChunk>();
        foreach (var path in files)
            foreach (var para in ChunkFile(File.ReadAllText(path)))
                chunks.Add(new KnowledgeChunk { source = Path.GetFileName(path), text = para });

        if (chunks.Count == 0)
        {
            EditorUtility.DisplayDialog("Build Knowledge Index",
                "No .md files (or only empty ones) in " + Folder + ".", "OK");
            return;
        }

        try
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                EditorUtility.DisplayProgressBar("Building knowledge index",
                    "[" + chunks[i].source + "]  chunk " + (i + 1) + " / " + chunks.Count,
                    (float)i / chunks.Count);
                chunks[i].vector = EmbedBlocking(chunks[i].text);
                if (chunks[i].vector == null)
                {
                    EditorUtility.DisplayDialog("Build Knowledge Index",
                        "Embedding failed on chunk " + (i + 1) + " (see Console). " +
                        "Nothing was written -- fix the error and run again.", "OK");
                    return;
                }
            }
        }
        finally { EditorUtility.ClearProgressBar(); }

        var index = new KnowledgeIndex
        { embedModel = AvatarKnowledge.EmbedModel, chunks = chunks.ToArray() };
        // AvatarKit 2: written into Assets/Resources so the index is packed
        // inside phone and web builds. The AvatarKit 1 copy is removed so
        // there is only one.
        Directory.CreateDirectory(Path.GetDirectoryName(AvatarKnowledge.ResourcesIndexPath));
        File.WriteAllText(AvatarKnowledge.ResourcesIndexPath, JsonUtility.ToJson(index));
        if (File.Exists(AvatarKnowledge.IndexPath)) AssetDatabase.DeleteAsset(AvatarKnowledge.IndexPath);
        AssetDatabase.Refresh();

        Debug.Log("[AvatarKit] Indexed " + chunks.Count + " chunks from " +
                  files.Length + " file(s) -> " + AvatarKnowledge.ResourcesIndexPath);
        EditorUtility.DisplayDialog("Build Knowledge Index",
            "Indexed " + chunks.Count + " chunks from " + files.Length + " file(s).\n\n" +
            "Re-run this whenever a knowledge file changes. Press Play and ask " +
            "about your facts -- the on-screen panel shows which chunks answered.", "OK");
    }

    /// <summary>Blank-line split, then merge fragments under ~200 chars into
    /// the next paragraph so a lone heading never becomes its own chunk.</summary>
    static List<string> ChunkFile(string text)
    {
        var outp = new List<string>();
        var current = new StringBuilder();
        foreach (var block in text.Replace("\r", "").Split(new[] { "\n\n" },
                     System.StringSplitOptions.RemoveEmptyEntries))
        {
            var b = block.Trim();
            if (b.Length == 0) continue;
            if (current.Length > 0) current.Append("\n\n");
            current.Append(b);
            if (current.Length >= MergeBelowChars)
            {
                outp.Add(current.ToString());
                current.Length = 0;
            }
        }
        if (current.Length > 0)
        {
            if (outp.Count > 0 && current.Length < MergeBelowChars / 2)
                outp[outp.Count - 1] += "\n\n" + current;
            else outp.Add(current.ToString());
        }
        return outp;
    }

    /// <summary>Editor scripts may busy-wait a web request -- ugly but honest,
    /// and it keeps the indexer to one file with zero packages.</summary>
    static float[] EmbedBlocking(string text)
    {
        var body = "{\"model\":\"" + AvatarKnowledge.EmbedModel + "\",\"input\":\"" +
                   AvatarVoiceAPI.Escape(text) + "\"}";
        using (var req = new UnityWebRequest(AvatarConnection.OpenAIUrl(AvatarConnection.Link.Embeddings), "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            AvatarConnection.SignOpenAI(req);
            req.timeout = 30;
            var op = req.SendWebRequest();
            while (!op.isDone) System.Threading.Thread.Sleep(15);
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[AvatarKit] embedding failed (" + req.responseCode + "): " +
                               (req.downloadHandler != null ? req.downloadHandler.text : req.error));
                return null;
            }
            return AvatarVoiceAPI.ReadFloatArray(req.downloadHandler.text, "embedding");
        }
    }
}
