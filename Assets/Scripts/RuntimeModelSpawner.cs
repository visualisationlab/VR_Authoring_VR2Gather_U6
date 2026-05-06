using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using GLTFast;

public class RuntimeModelSpawner : MonoBehaviour
{
    [Header("Server")]
    public string generateEndpoint = "http://localhost:8000/api/text-to-3d";

    [Header("Persistence")]
    public bool loadOnStart = true;

    [Header("Generated Model Setup")]
    public bool addMeshCollidersToChildren = true;
    public bool addRootBoxColliderIfMissing = true;

    [Header("Auto Placement")]
    [Tooltip("If true, normalize spawned model size to a more realistic size.")]
    public bool autoNormalizeSize = true;

    [Tooltip("If true, move model vertically so its bottom touches groundY.")]
    public bool placeOnGround = true;

    [Tooltip("World-space Y coordinate of your floor/ground.")]
    public float groundY = 0f;

    [Tooltip("Fallback target largest dimension for generic objects.")]
    public float genericLargestDimensionMeters = 1.5f;

    [Tooltip("Target height for humans/avatars.")]
    public float humanHeightMeters = 1.75f;

    [Tooltip("Target largest dimension for cars/vehicles.")]
    public float vehicleLargestDimensionMeters = 4.0f;

    [Tooltip("Never scale below this factor in one step.")]
    public float minScaleMultiplier = 0.01f;

    [Tooltip("Never scale above this factor in one step.")]
    public float maxScaleMultiplier = 100f;

    string ModelsDir => Path.Combine(Application.persistentDataPath, "GeneratedModels");
    string RegistryPath => Path.Combine(Application.persistentDataPath, "generated_models_registry.json");

    [Serializable]
    public class SpawnRecord
    {
        public string name;
        public string prompt;
        public string localGlbPath;
        public Vector3 position;
        public Vector3 eulerRotation;
        public Vector3 scale = Vector3.one;
        // ✅ Persisted behaviour prompt so AI-generated code is reattached on reload
        public string behaviourPrompt = "";
    }

    [Serializable]
    public class Registry
    {
        public List<SpawnRecord> items = new List<SpawnRecord>();
    }

    [Serializable]
    class TextTo3DRequest
    {
        public string prompt;
        public string name;
        public string stage = "preview";
        public string art_style = "realistic";
    }

    [Serializable]
    class TextTo3DResponse
    {
        public string status;
        public int progress;
        public string downloadUrl;
        public string message;
        public string jobId;
    }

    void Awake()
    {
        Directory.CreateDirectory(ModelsDir);
    }

    void Start()
    {
        if (loadOnStart)
            StartCoroutine(RespawnSavedModels());
    }

    public void GenerateAndSpawn(string prompt, string assetName, Vector3 position, string stage = "preview", string artStyle = "realistic")
    {
        StartCoroutine(GenerateAndSpawnCo(prompt, assetName, position, stage, artStyle));
    }

    /// <summary>
    /// Save a behaviour prompt onto an already-spawned model's registry record
    /// so it is reattached automatically the next time the scene loads.
    /// Called by VoiceCaptureAndSend after HandleCommand succeeds.
    /// </summary>
    public void SaveBehaviourPrompt(string assetName, string behaviourPrompt)
    {
        var reg = LoadRegistry();
        var rec = reg.items.Find(x => x.name == assetName);
        if (rec == null)
        {
            Debug.LogWarning($"[RuntimeModelSpawner] SaveBehaviourPrompt: no record found for '{assetName}'");
            return;
        }
        rec.behaviourPrompt = behaviourPrompt ?? "";
        File.WriteAllText(RegistryPath, JsonUtility.ToJson(reg, true));
        Debug.Log($"[RuntimeModelSpawner] Saved behaviourPrompt for '{assetName}': {behaviourPrompt}");
    }

    IEnumerator GenerateAndSpawnCo(string prompt, string assetName, Vector3 position, string stage, string artStyle)
    {
        var reqObj = new TextTo3DRequest
        {
            prompt = prompt,
            name = assetName,
            stage = stage,
            art_style = artStyle
        };

        string jsonReq = JsonUtility.ToJson(reqObj);
        string downloadUrl = null;

        for (int attempt = 1; attempt <= 240; attempt++)
        {
            using (var www = new UnityWebRequest(generateEndpoint, "POST"))
            {
                byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(jsonReq);
                www.uploadHandler = new UploadHandlerRaw(bodyRaw);
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");

                yield return www.SendWebRequest();

                if (www.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError($"[RuntimeModelSpawner] Server error: {www.error}\n{www.downloadHandler.text}");
                    yield break;
                }

                var raw = www.downloadHandler.text;
                TextTo3DResponse resp = null;

                try
                {
                    resp = JsonUtility.FromJson<TextTo3DResponse>(raw);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[RuntimeModelSpawner] Could not parse JSON (will retry): " + e + "\nRAW:\n" + raw);
                }

                if (resp != null)
                {
                    string st = (resp.status ?? "").Trim().ToUpperInvariant();
                    int pr = resp.progress;

                    Debug.Log($"[RuntimeModelSpawner] Poll {attempt}/240 status={st} progress={pr} url={(string.IsNullOrEmpty(resp.downloadUrl) ? "null" : "ok")}");

                    bool ready =
                        !string.IsNullOrEmpty(resp.downloadUrl) &&
                        (st == "DONE" || st == "SUCCEEDED" || st == "SUCCESS" || pr >= 100);

                    if (ready)
                    {
                        downloadUrl = resp.downloadUrl;
                        break;
                    }

                    if (st == "FAILED" || st == "ERROR")
                    {
                        Debug.LogError("[RuntimeModelSpawner] Generation failed: " + resp.message);
                        yield break;
                    }
                }
            }

            yield return new WaitForSeconds(3f);
        }

        if (string.IsNullOrEmpty(downloadUrl))
        {
            Debug.LogError("[RuntimeModelSpawner] Timed out waiting for model generation. (No downloadUrl received)");
            yield break;
        }

        string safeName = Sanitize(assetName);
        string localPath = Path.Combine(ModelsDir, safeName + ".glb");

        using (var dl = UnityWebRequest.Get(downloadUrl))
        {
            Debug.Log("[RuntimeModelSpawner] Downloading: " + downloadUrl);
            yield return dl.SendWebRequest();

            if (dl.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[RuntimeModelSpawner] Download failed: {dl.error}\n{dl.downloadHandler.text}");
                yield break;
            }

            try
            {
                File.WriteAllBytes(localPath, dl.downloadHandler.data);
            }
            catch (Exception e)
            {
                Debug.LogError("[RuntimeModelSpawner] Failed writing GLB to disk: " + e + "\nPath=" + localPath);
                yield break;
            }
        }

        GameObject spawnedGo = null;

        yield return LoadGlbIntoScene(
            localPath,
            assetName,
            prompt,
            position,
            applyAutoPlacement: true,
            forcedEulerRotation: null,
            forcedScale: null,
            onDone: go => { spawnedGo = go; }
        );

        if (spawnedGo != null)
        {
            SaveRecord(new SpawnRecord
            {
                name = assetName,
                prompt = prompt,
                localGlbPath = localPath,
                position = spawnedGo.transform.position,
                eulerRotation = spawnedGo.transform.eulerAngles,
                scale = spawnedGo.transform.localScale,
                behaviourPrompt = "" // filled in later by SaveBehaviourPrompt()
            });
        }
    }

    IEnumerator LoadGlbIntoScene(
        string localGlbPath,
        string instanceName,
        string prompt,
        Vector3 position,
        bool applyAutoPlacement,
        Vector3? forcedEulerRotation = null,
        Vector3? forcedScale = null,
        Action<GameObject> onDone = null)
    {
        if (!File.Exists(localGlbPath))
        {
            Debug.LogError("[RuntimeModelSpawner] GLB file missing: " + localGlbPath);
            yield break;
        }

        var import = new GltfImport();
        string uri = new Uri(localGlbPath).AbsoluteUri;

        var loadTask = import.Load(uri);
        while (!loadTask.IsCompleted) yield return null;

        bool success = false;
        try
        {
            success = loadTask.Result;
        }
        catch (Exception e)
        {
            Debug.LogError("[RuntimeModelSpawner] glTFast Load threw: " + e);
            yield break;
        }

        if (!success)
        {
            Debug.LogError("[RuntimeModelSpawner] glTFast Load failed: " + localGlbPath);
            yield break;
        }

        var go = new GameObject(instanceName);
        go.transform.position = position;

        if (forcedEulerRotation.HasValue)
            go.transform.eulerAngles = forcedEulerRotation.Value;

        if (forcedScale.HasValue)
            go.transform.localScale = forcedScale.Value;

        var instTask = import.InstantiateMainSceneAsync(go.transform);
        while (!instTask.IsCompleted) yield return null;

        yield return null;

        if (applyAutoPlacement && autoNormalizeSize)
        {
            NormalizeModelSize(go, prompt, instanceName);
            yield return null;
        }

        if (applyAutoPlacement && placeOnGround)
        {
            PlaceModelOnGround(go, groundY);
            yield return null;
        }

        var ai = go.GetComponent<AIControllable>();
        if (ai == null)
            ai = go.AddComponent<AIControllable>();

        Renderer[] childRenderers = go.GetComponentsInChildren<Renderer>(true);
        if (childRenderers != null && childRenderers.Length > 0)
        {
            ai.targetRenderer = childRenderers
                .OrderByDescending(r => r.bounds.size.magnitude)
                .FirstOrDefault();
        }

        if (addMeshCollidersToChildren)
        {
            var meshFilters = go.GetComponentsInChildren<MeshFilter>(true);
            foreach (var mf in meshFilters)
            {
                if (mf == null || mf.sharedMesh == null) continue;
                if (mf.GetComponent<Collider>() != null) continue;

                try
                {
                    var mc = mf.gameObject.AddComponent<MeshCollider>();
                    mc.sharedMesh = mf.sharedMesh;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[RuntimeModelSpawner] Could not add MeshCollider to " + mf.name + ": " + e.Message);
                }
            }
        }

        if (addRootBoxColliderIfMissing && go.GetComponentInChildren<Collider>(true) == null)
        {
            var bc = go.AddComponent<BoxCollider>();

            if (TryGetCombinedRendererBounds(go, out Bounds b))
            {
                bc.center = go.transform.InverseTransformPoint(b.center);
                bc.size = b.size;
            }
            else
            {
                bc.center = Vector3.zero;
                bc.size = Vector3.one;
            }
        }

        string boundsText = TryGetCombinedRendererBounds(go, out Bounds finalBounds)
            ? $"size={finalBounds.size}, minY={finalBounds.min.y:F3}, maxY={finalBounds.max.y:F3}"
            : "no renderer bounds";

        Debug.Log(
            $"✅ Spawned runtime model: {instanceName} @ {localGlbPath} | pos={go.transform.position} | rot={go.transform.eulerAngles} | scale={go.transform.localScale} | {boundsText}"
        );

        onDone?.Invoke(go);
    }

    IEnumerator RespawnSavedModels()
    {
        var reg = LoadRegistry();
        foreach (var item in reg.items)
        {
            if (string.IsNullOrEmpty(item.localGlbPath)) continue;
            if (!File.Exists(item.localGlbPath)) continue;

            string promptToUse = string.IsNullOrEmpty(item.prompt) ? item.name : item.prompt;

            GameObject respawnedGo = null;

            yield return LoadGlbIntoScene(
                item.localGlbPath,
                item.name,
                promptToUse,
                item.position,
                applyAutoPlacement: false,
                forcedEulerRotation: item.eulerRotation,
                forcedScale: item.scale,
                onDone: go => { respawnedGo = go; }
            );

            // ✅ Reattach AI-generated behaviour code if one was saved for this model.
            // Wait one extra frame so the GameObject is fully initialised before
            // AICodeCommandHandler tries to compile and attach the script.
            if (respawnedGo != null && !string.IsNullOrEmpty(item.behaviourPrompt))
            {
                string capturedPrompt = item.behaviourPrompt;
                GameObject capturedGo = respawnedGo;
                StartCoroutine(ReattachCodeNextFrame(capturedGo, capturedPrompt));
            }
        }
    }

    IEnumerator ReattachCodeNextFrame(GameObject target, string behaviourPrompt)
    {
        yield return null; // one frame for full scene init
        if (AICodeCommandHandler.Instance != null)
        {
            Debug.Log($"[RuntimeModelSpawner] Reattaching behaviour to '{target.name}': {behaviourPrompt}");
            AICodeCommandHandler.Instance.HandleCommand(behaviourPrompt, target);
        }
        else
        {
            Debug.LogWarning($"[RuntimeModelSpawner] AICodeCommandHandler not found — cannot reattach behaviour for '{target.name}'");
        }
    }

    void NormalizeModelSize(GameObject go, string prompt, string instanceName)
    {
        if (!TryGetCombinedRendererBounds(go, out Bounds b))
        {
            Debug.LogWarning("[RuntimeModelSpawner] NormalizeModelSize: no renderers found.");
            return;
        }

        Vector3 size = b.size;
        float currentHeight = Mathf.Max(size.y, 0.0001f);
        float currentLargest = Mathf.Max(size.x, Mathf.Max(size.y, size.z), 0.0001f);

        string text = ((prompt ?? "") + " " + (instanceName ?? "")).ToLowerInvariant();
        float targetScaleFactor;

        if (ContainsAny(text, "human", "person", "man", "woman", "boy", "girl", "avatar", "character"))
        {
            targetScaleFactor = humanHeightMeters / currentHeight;
            Debug.Log($"[RuntimeModelSpawner] Detected HUMAN. height={currentHeight:F3}m -> target={humanHeightMeters:F3}m");
        }
        else if (ContainsAny(text, "car", "vehicle", "truck", "van", "bus", "suv", "automobile"))
        {
            targetScaleFactor = vehicleLargestDimensionMeters / currentLargest;
            Debug.Log($"[RuntimeModelSpawner] Detected VEHICLE. largest={currentLargest:F3}m -> target={vehicleLargestDimensionMeters:F3}m");
        }
        else
        {
            targetScaleFactor = genericLargestDimensionMeters / currentLargest;
            Debug.Log($"[RuntimeModelSpawner] Detected GENERIC. largest={currentLargest:F3}m -> target={genericLargestDimensionMeters:F3}m");
        }

        targetScaleFactor = Mathf.Clamp(targetScaleFactor, minScaleMultiplier, maxScaleMultiplier);
        go.transform.localScale *= targetScaleFactor;

        if (TryGetCombinedRendererBounds(go, out Bounds after))
            Debug.Log($"[RuntimeModelSpawner] Size normalized -> bounds size={after.size}");
    }

    void PlaceModelOnGround(GameObject go, float targetGroundY)
    {
        if (!TryGetCombinedRendererBounds(go, out Bounds b))
        {
            Debug.LogWarning("[RuntimeModelSpawner] PlaceModelOnGround: no renderers found.");
            return;
        }

        float deltaY = targetGroundY - b.min.y;
        go.transform.position += new Vector3(0f, deltaY, 0f);

        if (TryGetCombinedRendererBounds(go, out Bounds after))
            Debug.Log($"[RuntimeModelSpawner] Grounded -> bottomY={after.min.y:F3}, targetGroundY={targetGroundY:F3}");
    }

    bool TryGetCombinedRendererBounds(GameObject go, out Bounds combined)
    {
        combined = default;
        if (go == null) return false;

        var renderers = go.GetComponentsInChildren<Renderer>(true)
            .Where(r => r != null && r.enabled)
            .ToArray();

        if (renderers.Length == 0) return false;

        combined = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            combined.Encapsulate(renderers[i].bounds);

        return true;
    }

    bool ContainsAny(string text, params string[] keywords)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var k in keywords)
            if (text.Contains(k)) return true;
        return false;
    }

    void SaveRecord(SpawnRecord rec)
    {
        var reg = LoadRegistry();
        reg.items.RemoveAll(x => x.name == rec.name);
        reg.items.Add(rec);
        File.WriteAllText(RegistryPath, JsonUtility.ToJson(reg, true));
    }

    Registry LoadRegistry()
    {
        if (!File.Exists(RegistryPath)) return new Registry();

        try
        {
            return JsonUtility.FromJson<Registry>(File.ReadAllText(RegistryPath)) ?? new Registry();
        }
        catch
        {
            return new Registry();
        }
    }

    static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s.Trim();
    }
}
