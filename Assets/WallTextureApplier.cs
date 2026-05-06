using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.IO;

public class WallTextureApplier : MonoBehaviour
{
    public float defaultTileScale = 1.8f;

    string TexturesDir => Path.Combine(Application.persistentDataPath, "textures");

    void Awake()
    {
        Directory.CreateDirectory(TexturesDir);
    }

    // ✅ Existing API (still supported)
    public void ApplyTextureUrlToHit(RaycastHit hit, string imageUrl, float tileScale)
    {
        StartCoroutine(ApplyTextureCoroutine_FromHit(hit, imageUrl, tileScale));
    }

    // ✅ NEW API for async jobs (safe to store and use later)
    // Works with VoiceCaptureAndSend.WallAnchor
    public void ApplyTextureUrlToAnchor(VoiceCaptureAndSend.WallAnchor anchor, string imageUrl, float tileScale)
    {
        if (!anchor.IsValid())
        {
            Debug.LogWarning("[WallTextureApplier] ApplyTextureUrlToAnchor: invalid anchor (wall == null).");
            return;
        }

        StartCoroutine(ApplyTextureCoroutine_FromWall(anchor.wall, imageUrl, tileScale));
    }

    // ✅ RESTORE API — called by SceneStateStore on scene reload.
    // Prefers the locally-cached PNG (no network needed); falls back to the
    // original URL if the file is missing. Always re-computes tile count from
    // the renderer's current world-space bounds so tiling is correct after reload.
    public void RestoreTexture(PersistableAIObject persist)
    {
        if (persist == null) return;

        // Prefer the saved local file so we don't re-hit the network.
        bool hasLocal = !string.IsNullOrEmpty(persist.localTexturePath)
                        && File.Exists(persist.localTexturePath);

        if (hasLocal)
            StartCoroutine(RestoreFromLocalFile(persist));
        else if (!string.IsNullOrEmpty(persist.textureUrl))
            StartCoroutine(ApplyTextureCoroutine_FromWall(persist.transform, persist.textureUrl, persist.tileScale));
        else
            Debug.LogWarning($"[WallTextureApplier] RestoreTexture: nothing to restore for '{persist.name}'.");
    }

    // ------------------ Internals ------------------

    IEnumerator RestoreFromLocalFile(PersistableAIObject persist)
    {
        // UnityWebRequest handles file:// URIs cross-platform.
        string uri = "file://" + persist.localTexturePath;

        using (var req = UnityWebRequestTexture.GetTexture(uri))
        {
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[WallTextureApplier] Local texture load failed ({req.error}), falling back to URL.");
                if (!string.IsNullOrEmpty(persist.textureUrl))
                    yield return ApplyTextureCoroutine_FromWall(persist.transform, persist.textureUrl, persist.tileScale);
                yield break;
            }

            var tex = DownloadHandlerTexture.GetContent(req);
            if (tex == null) yield break;

            ApplyTextureToRenderer(persist, tex, persist.tileScale);
        }
    }

    IEnumerator ApplyTextureCoroutine_FromHit(RaycastHit hit, string imageUrl, float tileScale)
    {
        var go = hit.collider != null ? hit.collider.gameObject : null;
        if (go == null) yield break;

        // Prefer PersistableAIObject on parent chain (more stable than collider object)
        var persist = go.GetComponentInParent<PersistableAIObject>();
        if (persist == null)
        {
            Debug.LogWarning("[WallTextureApplier] No PersistableAIObject found in parents. Add it to walls you want to persist.");
            yield break;
        }

        // Apply on the Persistable root (so we always target the same renderer)
        yield return ApplyTextureCoroutine_FromWall(persist.transform, imageUrl, tileScale);
    }

    IEnumerator ApplyTextureCoroutine_FromWall(Transform wallRoot, string imageUrl, float tileScale)
    {
        if (wallRoot == null) yield break;

        // Find persistence component
        var persist = wallRoot.GetComponent<PersistableAIObject>();
        if (persist == null)
            persist = wallRoot.GetComponentInParent<PersistableAIObject>();

        if (persist == null)
        {
            Debug.LogWarning("[WallTextureApplier] No PersistableAIObject found. Add it to walls you want to persist.");
            yield break;
        }

        // 1) Download image from URL
        using (var req = UnityWebRequestTexture.GetTexture(imageUrl))
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[WallTextureApplier] Texture download failed: " + req.error);
                yield break;
            }

            var tex = DownloadHandlerTexture.GetContent(req);
            if (tex == null)
            {
                Debug.LogError("[WallTextureApplier] Downloaded texture is null.");
                yield break;
            }

            // 2) Apply + save
            ApplyTextureToRenderer(persist, tex, tileScale);

            byte[] png = tex.EncodeToPNG();
            string localPath = Path.Combine(TexturesDir, $"{persist.id}_tex.png");
            File.WriteAllBytes(localPath, png);

            persist.textureUrl       = imageUrl;
            persist.localTexturePath = localPath;
            persist.tileScale        = (tileScale > 0f) ? tileScale : defaultTileScale;

            if (persist.controllable != null)
                persist.controllable.TrySetColor(Color.white);

            FindFirstObjectByType<SceneStateStore>()?.RequestSave();

            Debug.Log("[WallTextureApplier] Applied + saved texture png: " + localPath);
        }
    }

    // ── Single authoritative method that sets a texture on a renderer ──────────
    // Called by every code path (first apply AND restore) so tiling is always
    // computed from current world-space bounds — never left at the Unity default (1,1).
    void ApplyTextureToRenderer(PersistableAIObject persist, Texture2D tex, float tileScale)
    {
        // ✅ Safer: renderer may be on child
        var r = persist.transform.GetComponentInChildren<Renderer>(true);
        if (r == null)
        {
            Debug.LogWarning("[WallTextureApplier] No Renderer found on wall or its children.");
            return;
        }

        tex.wrapMode   = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Bilinear;

        float ts = (tileScale > 0f) ? tileScale : defaultTileScale;

        // ✅ Try triplanar shader first for consistent tiling on all wall faces.
        // Falls back to Standard if the shader isn't found in the project.
        Shader shader = Shader.Find("Custom/Triplanar");
        bool isTriplanar = shader != null;

        if (!isTriplanar)
        {
            Debug.LogWarning("[WallTextureApplier] Custom/Triplanar shader not found, falling back to Standard.");
            shader = Shader.Find("Standard");
        }

        if (shader == null)
        {
            Debug.LogError("[WallTextureApplier] No usable shader found (tried Custom/Triplanar and Standard).");
            return;
        }

        Material newMat = new Material(shader);
        newMat.name        = $"TexMat_{persist.id}";
        newMat.mainTexture = tex;
        newMat.color       = Color.white; // prevents tinting-to-black

        if (isTriplanar)
        {
            // Triplanar projects in world-space: _Scale controls texture frequency.
            // tileScale = world units per tile, so 1/tileScale = repeats per world unit.
            newMat.SetFloat("_Scale", 1f / ts);
        }
        else
        {
            // Standard shader: compute UV tiling from world-space bounds as before.
            Vector2 tileCount = ComputeTileCount(r, ts);
            newMat.SetFloat("_Metallic",   0f);
            newMat.SetFloat("_Glossiness", 0.1f);
            newMat.mainTextureScale  = tileCount;
            newMat.mainTextureOffset = Vector2.zero;
        }

        // Apply to all sub-materials (multi-material renderers)
        var mats = r.materials;
        if (mats == null || mats.Length == 0)
        {
            r.material = newMat;
        }
        else
        {
            for (int i = 0; i < mats.Length; i++)
                mats[i] = newMat;
            r.materials = mats;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Works out how many times the texture should repeat across the surface.
    /// Uses the renderer's world-space bounds so a 10 m wall tiled at 1.8 m gets
    /// ~5-6 repeats, while a 1 m wall gets just 1.
    /// Falls back to (1,1) if bounds can't be determined.
    /// Only used when falling back to the Standard shader.
    /// </summary>
    static Vector2 ComputeTileCount(Renderer r, float tileSizeInWorldUnits)
    {
        if (r == null || tileSizeInWorldUnits <= 0f)
            return Vector2.one;

        Bounds bounds = r.bounds;   // world-space AABB

        // Width = longest horizontal extent; height = Y axis.
        float width  = Mathf.Max(bounds.size.x, bounds.size.z);
        float height = bounds.size.y;

        // Floor/ceiling fallback (very thin on Y)
        if (height < 0.01f)
            height = Mathf.Min(bounds.size.x, bounds.size.z);

        float repeatX = Mathf.Max(1f, Mathf.Round(width  / tileSizeInWorldUnits));
        float repeatY = Mathf.Max(1f, Mathf.Round(height / tileSizeInWorldUnits));

        return new Vector2(repeatX, repeatY);
    }
}
