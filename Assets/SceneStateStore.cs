using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;

/// SceneStateStore
/// - Saves/loads PersistableAIObject transforms/material/color
/// - ALSO saves/loads posters (PersistablePoster) including local PNG path
/// - Spawns posters on Load (because posters are created at runtime and won't exist in-scene)
public class SceneStateStore : MonoBehaviour
{
    public bool loadOnStart = true;
    public float saveDebounceSeconds = 0.25f;

    float _saveAt = -1f;

    string SavePath => Path.Combine(Application.persistentDataPath, "scene_state.json");

    [System.Serializable]
    class SceneState
    {
        public List<PersistableAIObject.ObjectState> objects = new List<PersistableAIObject.ObjectState>();
        public List<PersistablePoster.PosterState> posters = new List<PersistablePoster.PosterState>();
    }

    void Start()
    {
        if (loadOnStart) Load();
    }

    void Update()
    {
        if (_saveAt > 0f && Time.unscaledTime >= _saveAt)
        {
            _saveAt = -1f;
            Save();
        }
    }

    public void RequestSave()
    {
        _saveAt = Time.unscaledTime + saveDebounceSeconds;
    }

    void OnApplicationQuit()
    {
        // Final safety net so "saved when I exit" is true.
        Save();
    }

    [ContextMenu("Save Now")]
    public void Save()
    {
        var state = new SceneState();

        // ---- Save normal AI objects ----
        var persistables = FindObjectsByType<PersistableAIObject>(FindObjectsSortMode.None);
        foreach (var p in persistables)
        {
            if (p == null) continue;
            state.objects.Add(p.Capture());
        }

        // ---- Save posters ----
        var posters = FindObjectsByType<PersistablePoster>(FindObjectsSortMode.None);
        foreach (var p in posters)
        {
            if (p == null) continue;

            // IMPORTANT:
            // Do NOT call p.SyncSizeFromTransform() here.
            // Posters are often parented under scaled wall/building objects.
            // Reading lossyScale during Save can convert a requested 3m poster
            // into 0.03m when the parent has scale 100.
            // widthMeters/heightMeters are the real-world source of truth.
            state.posters.Add(p.Capture());
        }

        var json = JsonUtility.ToJson(state, true);
        File.WriteAllText(SavePath, json);

        Debug.Log("[SceneStateStore] Saved to: " + SavePath);
    }

    [ContextMenu("Load Now")]
    public void Load()
    {
        if (!File.Exists(SavePath))
        {
            Debug.Log("[SceneStateStore] No save file yet: " + SavePath);
            return;
        }

        var json = File.ReadAllText(SavePath);
        var state = JsonUtility.FromJson<SceneState>(json);
        if (state == null) return;

        // ---- Load/apply normal AI objects (only applies to objects already in the scene) ----
        if (state.objects != null)
        {
            var persistables = FindObjectsByType<PersistableAIObject>(FindObjectsSortMode.None);
            var map = new Dictionary<string, PersistableAIObject>();
            foreach (var p in persistables)
            {
                if (p != null && !string.IsNullOrEmpty(p.id))
                    map[p.id] = p;
            }

            // Cached once — RestoreTexture starts a coroutine so it's cheap per call,
            // but FindFirstObjectByType in a loop would be wasteful.
            var texApplier = FindFirstObjectByType<WallTextureApplier>();

            foreach (var s in state.objects)
            {
                if (s == null || string.IsNullOrEmpty(s.id)) continue;
                if (!map.TryGetValue(s.id, out var p)) continue;

                p.Apply(s);

                // Restore wall texture if one was saved (uses local PNG, falls back to URL).
                // WallTextureApplier.RestoreTexture re-computes tile count from live bounds
                // so the texture tiles correctly instead of stretching to (1,1).
                if (texApplier != null && !string.IsNullOrEmpty(s.textureUrl))
                    texApplier.RestoreTexture(p);

                // ✅ Reattach AI-generated behaviour code for pre-placed scene objects
                // (runtime-spawned GLB objects are handled by RuntimeModelSpawner).
                if (!string.IsNullOrEmpty(s.behaviourPrompt))
                    StartCoroutine(ReattachCodeNextFrame(p.gameObject, s.behaviourPrompt));
            }
        }

        // ---- Load posters (spawn them; they may not exist in the scene yet) ----
        if (state.posters != null && state.posters.Count > 0)
        {
            // Avoid duplicate-spawning if Load is called twice
            var existing = FindObjectsByType<PersistablePoster>(FindObjectsSortMode.None);
            var existingIds = new HashSet<string>();
            foreach (var e in existing)
                if (e != null && !string.IsNullOrEmpty(e.id)) existingIds.Add(e.id);

            var spawner = FindFirstObjectByType<PosterSpawner>();

            foreach (var ps in state.posters)
            {
                if (ps == null || string.IsNullOrEmpty(ps.id)) continue;
                if (existingIds.Contains(ps.id)) continue;

                SpawnPosterFromState(ps, spawner);
            }
        }

        Debug.Log("[SceneStateStore] Loaded from: " + SavePath);
    }

    // ✅ Reattach AI-generated behaviour code one frame after load so the
    // GameObject is fully initialised before AICodeCommandHandler runs.
    IEnumerator ReattachCodeNextFrame(GameObject target, string behaviourPrompt)
    {
        yield return null;
        if (AICodeCommandHandler.Instance != null)
        {
            Debug.Log($"[SceneStateStore] Reattaching behaviour to '{target.name}': {behaviourPrompt}");
            AICodeCommandHandler.Instance.HandleCommand(behaviourPrompt, target);
        }
        else
        {
            Debug.LogWarning($"[SceneStateStore] AICodeCommandHandler not found — cannot reattach behaviour for '{target.name}'");
        }
    }

    void SpawnPosterFromState(PersistablePoster.PosterState s, PosterSpawner spawner)
    {
        var poster = GameObject.CreatePrimitive(PrimitiveType.Quad);
        poster.name = "Poster_Loaded";

        // Add component and restore pose + metadata (Apply does NOT touch localScale)
        var persist = poster.AddComponent<PersistablePoster>();
        persist.id = s.id;
        persist.Apply(s);

        // ✅ Recompute localScale from widthMeters/heightMeters AFTER parenting
        // (no parent here at load time, but still use the correct path so
        //  lossyScale compensation is always applied consistently)
        if (spawner != null)
        {
            spawner.ApplyWorldSizeToPoster(poster.transform, s.widthMeters, s.heightMeters);
        }
        else
        {
            // Fallback: no parent, so lossyScale == Vector3.one — safe to set directly
            poster.transform.localScale = new Vector3(
                Mathf.Max(0.01f, s.widthMeters),
                Mathf.Max(0.01f, s.heightMeters),
                1f
            );
        }

        // Renderer + material
        var r = poster.GetComponent<Renderer>();

        Material mat;
        if (spawner != null && spawner.posterMaterialTemplate != null)
            mat = new Material(spawner.posterMaterialTemplate);
        else
            mat = new Material(Shader.Find("Unlit/Texture"));

        r.material = mat;

        // Load PNG from disk
        if (!string.IsNullOrEmpty(s.localPngPath) && File.Exists(s.localPngPath))
        {
            byte[] bytes = File.ReadAllBytes(s.localPngPath);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(bytes);
            tex.wrapMode = TextureWrapMode.Clamp;
            r.material.mainTexture = tex;
        }
        else
        {
            Debug.LogWarning("[SceneStateStore] Poster PNG missing on disk: " + s.localPngPath);
        }

        // Make gaze/voice controllable
        var ai = poster.AddComponent<AIControllable>();
        ai.targetRenderer = r;
        ai.rb = null;
        ai.useRigidbodyWhenAvailable = false;

        // Make restored posters network-editable using their persistent poster id.
        var netSync = poster.AddComponent<VRT.Pilots.Common.NetworkedAIObjectSync>();
        netSync.NetworkId = persist.id;
        netSync.ai = ai;
        netSync.targetRenderer = r;
        netSync.sceneStateStore = this;
        // Poster localScale depends on parent lossyScale, which can differ across
        // machines. Width/height meters are the persistent source of truth, so
        // do NOT network-sync localScale for posters.
        netSync.syncScale = false;
    }
}
