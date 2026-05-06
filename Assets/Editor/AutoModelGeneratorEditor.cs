#if UNITY_EDITOR
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class AutoModelGeneratorEditor
{
    // Your FastAPI endpoint
    private const string GenerateEndpoint = "http://localhost:8000/api/text-to-3d";

    // Where we persist generated assets inside the Unity project
    private const string ModelsFolder = "Assets/Generated/Models";
    private const string PrefabsFolder = "Assets/Generated/Prefabs";

    // Polling settings (Meshy refine can take time)
    private const int PollDelayMs = 5000;     // 5 seconds
    private const int MaxPollAttempts = 240;  // 240 * 5s = 20 minutes

    [MenuItem("Tools/AI/Generate GLB From Text (Test)")]
    public static async void TestGenerate()
    {
        try
        {
            var prompt = "low poly glass Table";
            var assetName = MakeUniqueAssetNameFromPrompt(prompt);

            await GenerateAndPlaceAsync(
                prompt: prompt,
                assetName: assetName,
                position: new Vector3(0, 0, 2)
            );
        }
        catch (Exception e)
        {
            Debug.LogError($"[TEXT2MESH] TestGenerate failed: {e}");
        }
    }

    public static async Task<GameObject> GenerateAndPlaceAsync(string prompt, string assetName, Vector3 position)
    {
        EnsureFolders();

        // Always ensure a unique name so we never overwrite previously generated assets
        assetName = MakeUniqueAssetNameFromPrompt(prompt, assetName);
        var safeName = Sanitize(assetName);

        // 1) Ask server to generate and wait until it returns downloadUrl (polls on queued)
        var downloadUrl = await RequestGenerationAsync(prompt, safeName);

        // 2) Download GLB bytes
        var bytes = await DownloadBytesAsync(downloadUrl);

        // 3) Save into Assets so Unity can persist/import it
        var modelAssetPath = $"{ModelsFolder}/{safeName}.glb";
        File.WriteAllBytes(modelAssetPath, bytes);

        // 4) Import + refresh
        AssetDatabase.ImportAsset(modelAssetPath, ImportAssetOptions.ForceUpdate);
        AssetDatabase.Refresh();

        // 5) Instantiate the imported asset (Editor-safe; avoids glTFast DontDestroyOnLoad)
        GameObject instance = null;

        // Try main asset first
        var main = AssetDatabase.LoadMainAssetAtPath(modelAssetPath);
        if (main is GameObject mainGo)
        {
            instance = (GameObject)PrefabUtility.InstantiatePrefab(mainGo);
        }
        else
        {
            // Fallback: search sub-assets for a GameObject
            var subAssets = AssetDatabase.LoadAllAssetsAtPath(modelAssetPath);
            foreach (var a in subAssets)
            {
                if (a is GameObject go)
                {
                    instance = (GameObject)PrefabUtility.InstantiatePrefab(go);
                    break;
                }
            }
        }

        if (instance == null)
        {
            Debug.LogError(
                "GLB imported, but Unity did not produce an instantiable GameObject asset.\n" +
                "Check the Project window: does the .glb show a prefab/model you can drag into the scene?");
            return null;
        }

        instance.name = assetName;
        instance.transform.position = position;

        // Optional: add collider so it behaves like your other objects
        EnsureCollider(instance);

        // Optional: add your AIControllable (if you want voice actions on this object)
        if (instance.GetComponent<AIControllable>() == null)
            instance.AddComponent<AIControllable>();

        // 6) Save prefab asset so it persists & is reusable
        var prefabPath = $"{PrefabsFolder}/{safeName}.prefab";
        PrefabUtility.SaveAsPrefabAssetAndConnect(instance, prefabPath, InteractionMode.UserAction);

        // 7) Save scene so the instance stays in the scene after restart
        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        EditorSceneManager.SaveOpenScenes();

        Debug.Log($"✅ Generated model saved:\n- {modelAssetPath}\n- {prefabPath}");
        return instance;
    }

    private static void EnsureFolders()
    {
        if (!AssetDatabase.IsValidFolder("Assets/Generated"))
            AssetDatabase.CreateFolder("Assets", "Generated");

        if (!AssetDatabase.IsValidFolder(ModelsFolder))
            AssetDatabase.CreateFolder("Assets/Generated", "Models");

        if (!AssetDatabase.IsValidFolder(PrefabsFolder))
            AssetDatabase.CreateFolder("Assets/Generated", "Prefabs");
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s.Trim();
    }

    // Makes a unique asset name like Chair_48392 / Table_10593 / Fountain_39201
    private static string MakeUniqueAssetNameFromPrompt(string prompt, string preferredName = null)
    {
        var baseName = !string.IsNullOrWhiteSpace(preferredName)
            ? GuessBaseName(preferredName)
            : GuessBaseName(prompt);

        for (int i = 0; i < 200; i++)
        {
            var suffix = UnityEngine.Random.Range(10000, 99999);
            var candidate = $"{baseName}_{suffix}";
            var safe = Sanitize(candidate);

            var glbPath = $"{ModelsFolder}/{safe}.glb";
            var prefabPath = $"{PrefabsFolder}/{safe}.prefab";

            if (!File.Exists(glbPath) && !File.Exists(prefabPath))
                return candidate;
        }

        return $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}";
    }

    private static string GuessBaseName(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "Object";

        var p = text.ToLowerInvariant();

        if (p.Contains("chair")) return "Chair";
        if (p.Contains("table") || p.Contains("desk")) return "Table";
        if (p.Contains("fountain")) return "Fountain";
        if (p.Contains("sofa") || p.Contains("couch")) return "Sofa";
        if (p.Contains("lamp") || p.Contains("light")) return "Lamp";
        if (p.Contains("tree") || p.Contains("plant")) return "Plant";
        if (p.Contains("car")) return "Car";

        return "Object";
    }

    /// <summary>
    /// Calls POST /api/text-to-3d and polls until server returns:
    /// { "status":"done", "downloadUrl":"http://..." }
    /// Server may return:
    /// - 202 + {"status":"queued", ...}
    /// - 500 + {"status":"error", ...}
    /// </summary>
    private static async Task<string> RequestGenerationAsync(string prompt, string name)
    {
        using var client = new HttpClient();

        // IMPORTANT: send the unique name to the server so it doesn't overwrite Desktop glbs
        var payloadObj = new TextTo3DRequest
        {
            prompt = prompt,
            name = name,
            stage = "preview",        // "preview" or "refine"
            art_style = "realistic"   // optional
        };

        for (int attempt = 1; attempt <= MaxPollAttempts; attempt++)
        {
            var payloadJson = JsonUtility.ToJson(payloadObj);
            using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
            using var resp = await client.PostAsync(GenerateEndpoint, content);

            var body = await resp.Content.ReadAsStringAsync();
            Debug.Log($"[TEXT2MESH] POST attempt {attempt}/{MaxPollAttempts} => {(int)resp.StatusCode} {resp.ReasonPhrase}\n{body}");

            if ((int)resp.StatusCode == 202)
            {
                await Task.Delay(PollDelayMs);
                continue;
            }

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Server {(int)resp.StatusCode} {resp.ReasonPhrase}: {body}");

            var parsed = JsonUtility.FromJson<TextTo3DResponse>(body);
            if (parsed == null)
                throw new Exception($"Could not parse server JSON. Raw: {body}");

            var status = (parsed.status ?? "").Trim().ToLowerInvariant();

            if (status == "done" && !string.IsNullOrEmpty(parsed.downloadUrl))
                return parsed.downloadUrl;

            if (status == "queued")
            {
                await Task.Delay(PollDelayMs);
                continue;
            }

            if (status == "error")
                throw new Exception($"Server returned error JSON: {body}");

            if (!string.IsNullOrEmpty(parsed.downloadUrl))
                return parsed.downloadUrl;

            await Task.Delay(PollDelayMs);
        }

        throw new Exception($"Timed out waiting for generation (>{(MaxPollAttempts * PollDelayMs) / 1000}s).");
    }

    private static async Task<byte[]> DownloadBytesAsync(string url)
    {
        using var client = new HttpClient();

        Debug.Log($"[TEXT2MESH] GET {url}");
        using var resp = await client.GetAsync(url);
        var bytes = await resp.Content.ReadAsByteArrayAsync();

        Debug.Log($"[TEXT2MESH] GET status={(int)resp.StatusCode} {resp.ReasonPhrase}, bytes={bytes.Length}");

        if (!resp.IsSuccessStatusCode)
        {
            var preview = Encoding.UTF8.GetString(bytes);
            Debug.LogError($"[TEXT2MESH] GET failed body preview:\n{preview}");
            throw new Exception($"Download failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }

        return bytes;
    }

    private static void EnsureCollider(GameObject root)
    {
        var meshFilters = root.GetComponentsInChildren<MeshFilter>();
        foreach (var mf in meshFilters)
        {
            if (mf.sharedMesh == null) continue;
            var col = mf.GetComponent<MeshCollider>();
            if (col == null) col = mf.gameObject.AddComponent<MeshCollider>();
            col.sharedMesh = mf.sharedMesh;
        }
    }

    [Serializable]
    private class TextTo3DRequest
    {
        public string prompt;
        public string name;
        public string stage;      // "preview" or "refine"
        public string art_style;
    }

    [Serializable]
    private class TextTo3DResponse
    {
        public string status;
        public string downloadUrl;
        public string message;
        public string jobId;
    }
}
#endif
