#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

public class VoiceToModelWindow : EditorWindow
{
    // Defaults that usually work locally
    private string ollamaUrl = "http://localhost:11434/api/generate";   // Llama3.2 via Ollama
    private string comfyUrl = "http://localhost:8188/prompt";          // ComfyUI API (basic prompt endpoint)

    private string transcriptOrPrompt =
        "Create a stone fountain, classical style, game-ready, clean silhouette.";

    private string status = "Idle";
    private Vector2 scroll;

    // Output folders in Assets (Unity will import automatically)
    private const string ImagesDir = "Assets/Generated/Images";
    private const string ModelsDir = "Assets/Generated/Models";

    // Simple “result” placeholders
    private string lastJsonFromLlama = "";
    private string lastImagePath = "";

    [MenuItem("Tools/Voice → 3D/Voice To Model (Step 3 Tool)")]
    public static void Open()
    {
        var w = GetWindow<VoiceToModelWindow>("Voice→3D Tool");
        w.minSize = new Vector2(520, 420);
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Step 3: Editor Tool (Voice → Llama → Image)", EditorStyles.boldLabel);

        EditorGUILayout.Space(6);

        ollamaUrl = EditorGUILayout.TextField("Ollama URL", ollamaUrl);
        comfyUrl = EditorGUILayout.TextField("ComfyUI URL", comfyUrl);

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("Transcript / Prompt");
        transcriptOrPrompt = EditorGUILayout.TextArea(transcriptOrPrompt, GUILayout.Height(80));

        EditorGUILayout.Space(10);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("A) Run Llama (make JSON prompt)", GUILayout.Height(32)))
                _ = RunLlamaAsync();

            if (GUILayout.Button("B) Generate Image (ComfyUI)", GUILayout.Height(32)))
                _ = GenerateImageAsync();
        }

        EditorGUILayout.Space(6);

        // This will later call Meshy + import + save scene
        EditorGUI.BeginDisabledGroup(true);
        GUILayout.Button("C) Send to Meshy + Download GLB + Place + Save (Step 4/5)", GUILayout.Height(32));
        EditorGUI.EndDisabledGroup();

        EditorGUILayout.Space(10);
        EditorGUILayout.HelpBox(status, MessageType.Info);

        EditorGUILayout.Space(10);
        scroll = EditorGUILayout.BeginScrollView(scroll);
        if (!string.IsNullOrEmpty(lastJsonFromLlama))
        {
            EditorGUILayout.LabelField("Last JSON from Llama:");
            EditorGUILayout.TextArea(lastJsonFromLlama);
            EditorGUILayout.Space(8);
        }

        if (!string.IsNullOrEmpty(lastImagePath))
        {
            EditorGUILayout.LabelField("Last image saved at:");
            EditorGUILayout.TextField(lastImagePath);
        }
        EditorGUILayout.EndScrollView();
    }

    private void EnsureFolders()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Generated"))
            AssetDatabase.CreateFolder("Assets", "Generated");
        if (!AssetDatabase.IsValidFolder(ImagesDir))
            AssetDatabase.CreateFolder("Assets/Generated", "Images");
        if (!AssetDatabase.IsValidFolder(ModelsDir))
            AssetDatabase.CreateFolder("Assets/Generated", "Models");
    }

    // -----------------------------
    // A) LLAMA 3.2 via Ollama
    // -----------------------------
    private async Task RunLlamaAsync()
    {
        try
        {
            status = "Calling Llama (Ollama)...";
            Repaint();

            // We ask Llama to output STRICT JSON (very important)
            string systemInstruction =
                "You are a command generator. Output ONLY valid JSON.\n" +
                "Schema:\n" +
                "{\n" +
                "  \"object\": \"...\",\n" +
                "  \"style\": \"...\",\n" +
                "  \"image_prompt\": \"...\"\n" +
                "}\n" +
                "Rules: no markdown, no comments, no extra text.\n";

            // Ollama /api/generate expects: {model, prompt, stream}
            // We embed instruction + user text in prompt.
            var payload =
                "{"
                + "\"model\":\"llama3.2\","
                + "\"prompt\":" + JsonEscape(systemInstruction + "\nUser: " + transcriptOrPrompt) + ","
                + "\"stream\":false"
                + "}";

            var response = await PostJsonAsync(ollamaUrl, payload);

            // Ollama returns JSON with a "response" field containing the text output.
            // We'll do a lightweight extraction to keep dependencies minimal.
            string text = ExtractField(response, "response");
            lastJsonFromLlama = text;

            status = "Llama done. (Check JSON below). Next: Generate Image.";
            Repaint();
        }
        catch (Exception ex)
        {
            status = "Llama error: " + ex.Message;
            Repaint();
        }
    }

    // -----------------------------
    // B) Image generation (ComfyUI)
    // -----------------------------
    private async Task GenerateImageAsync()
    {
        try
        {
            EnsureFolders();

            status = "Generating image (ComfyUI)...";
            Repaint();

            // Use JSON from Llama if available; otherwise use transcript as prompt.
            string imagePrompt = transcriptOrPrompt;

            // If Llama output is JSON, try to pull "image_prompt"
            if (!string.IsNullOrEmpty(lastJsonFromLlama))
            {
                var ip = TryExtractJsonValue(lastJsonFromLlama, "image_prompt");
                if (!string.IsNullOrEmpty(ip)) imagePrompt = ip;
            }

            // ComfyUI normally needs a "workflow" JSON, not a single prompt.
            // EASIEST: you create a simple workflow in ComfyUI and paste it here.
            //
            // For Step 3, we just show the TOOL plumbing; you will insert your workflow next.
            //
            // Placeholder payload (won't work until you paste a real ComfyUI workflow).
            var comfyPayload = BuildComfyWorkflowPlaceholder(imagePrompt);

            var comfyResponse = await PostJsonAsync(comfyUrl, comfyPayload);

            // In real ComfyUI usage: you call /prompt → get prompt_id,
            // then poll /history/{prompt_id} until images are ready, then download.
            //
            // For Step 3 skeleton: we just save the response so you see it works.
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var debugPath = $"{ImagesDir}/comfy_response_{stamp}.json";
            File.WriteAllText(debugPath, comfyResponse, Encoding.UTF8);
            AssetDatabase.Refresh();

            lastImagePath = debugPath;
            status = "ComfyUI request sent. Next: implement poll+download PNG (Step 3.2).";
            Repaint();
        }
        catch (Exception ex)
        {
            status = "Image error: " + ex.Message;
            Repaint();
        }
    }

    // ---------------------------------------
    // Helpers: HTTP + tiny JSON conveniences
    // ---------------------------------------
    private static async Task<string> PostJsonAsync(string url, string json)
    {
        using (var req = new UnityWebRequest(url, "POST"))
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(body);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            var op = req.SendWebRequest();
            while (!op.isDone)
                await Task.Delay(50);

#if UNITY_2020_2_OR_NEWER
            if (req.result != UnityWebRequest.Result.Success)
                throw new Exception(req.error + " | " + req.downloadHandler.text);
#else
            if (req.isNetworkError || req.isHttpError)
                throw new Exception(req.error + " | " + req.downloadHandler.text);
#endif
            return req.downloadHandler.text;
        }
    }

    private static string JsonEscape(string s)
    {
        // Returns a JSON string literal including quotes.
        return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
    }

    private static string ExtractField(string json, string fieldName)
    {
        // Lightweight: finds "fieldName":"...".
        // Good enough for Ollama response field in Step 3.
        var key = "\"" + fieldName + "\":";
        int i = json.IndexOf(key, StringComparison.Ordinal);
        if (i < 0) return "";
        i += key.Length;

        // skip whitespace
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;

        if (i >= json.Length || json[i] != '"') return "";
        i++;

        var sb = new StringBuilder();
        bool esc = false;
        for (; i < json.Length; i++)
        {
            char c = json[i];
            if (esc)
            {
                // basic escapes
                if (c == 'n') sb.Append('\n');
                else sb.Append(c);
                esc = false;
            }
            else
            {
                if (c == '\\') esc = true;
                else if (c == '"') break;
                else sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string TryExtractJsonValue(string json, string key)
    {
        // Very light extraction for: "key": "value"
        var k = "\"" + key + "\"";
        int i = json.IndexOf(k, StringComparison.Ordinal);
        if (i < 0) return "";
        i = json.IndexOf(':', i);
        if (i < 0) return "";
        i++;
        while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
        if (i >= json.Length || json[i] != '"') return "";
        i++;

        var sb = new StringBuilder();
        bool esc = false;
        for (; i < json.Length; i++)
        {
            char c = json[i];
            if (esc) { sb.Append(c); esc = false; }
            else
            {
                if (c == '\\') esc = true;
                else if (c == '"') break;
                else sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string BuildComfyWorkflowPlaceholder(string prompt)
    {
        // NOTE: This placeholder is not a real ComfyUI workflow.
        // In Step 3.2 you will paste your exported workflow JSON here and replace its prompt node text.
        //
        // For now, this at least shows you where prompt injection goes.
        return "{"
             + "\"prompt\":" + JsonEscape(prompt)
             + "}";
    }
}
#endif
