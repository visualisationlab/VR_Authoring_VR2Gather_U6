using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.IO;
using VRT.Pilots.Common;

public class PosterSpawner : MonoBehaviour
{
    [Header("Materials")]
    public Material posterMaterialTemplate;   // Unlit/Texture recommended
    public float surfaceOffsetMeters = 0.005f;

    [Header("Debug")]
    public bool logSizes = false;

    string PostersDir => Path.Combine(Application.persistentDataPath, "posters");

    void Awake()
    {
        Directory.CreateDirectory(PostersDir);
    }

    // -------------------------------------------------------------------------
    // Creation API
    // -------------------------------------------------------------------------

    public void CreatePosterAtHit(RaycastHit hit, string imageUrl, float widthMeters, float heightMeters)
    {
        StartCoroutine(CreatePosterCoroutine(
            hit.collider != null ? hit.collider.transform : null,
            hit.point,
            hit.normal,
            imageUrl,
            widthMeters,
            heightMeters
        ));
    }

    public void CreatePosterAtAnchor(VoiceCaptureAndSend.WallAnchor anchor, string imageUrl, float widthMeters, float heightMeters)
    {
        if (!anchor.IsValid())
        {
            Debug.LogWarning("[PosterSpawner] CreatePosterAtAnchor: invalid anchor (wall == null).");
            return;
        }

        Vector3 point = anchor.WorldPoint();
        Vector3 normal = anchor.WorldNormal();
        StartCoroutine(CreatePosterCoroutine(anchor.wall, point, normal, imageUrl, widthMeters, heightMeters));
    }

    IEnumerator CreatePosterCoroutine(
        Transform parentWall,
        Vector3 worldPoint,
        Vector3 worldNormal,
        string imageUrl,
        float widthMeters,
        float heightMeters)
    {
        widthMeters = Mathf.Max(0.01f, widthMeters);
        heightMeters = Mathf.Max(0.01f, heightMeters);

        // 1) Create quad
        var poster = GameObject.CreatePrimitive(PrimitiveType.Quad);
        poster.name = $"Poster_{System.DateTime.Now:HHmmss}";

        var persist = poster.AddComponent<PersistablePoster>();
        persist.imageUrl = imageUrl;
        persist.widthMeters = widthMeters;
        persist.heightMeters = heightMeters;

        // 2) Compute outward normal
        Vector3 n = (worldNormal.sqrMagnitude > 0.0001f) ? worldNormal.normalized : Vector3.forward;

        // 3) Set world pose
        poster.transform.position = worldPoint + n * surfaceOffsetMeters;
        poster.transform.rotation = Quaternion.LookRotation(-n, Vector3.up);

        // 4) Parent BEFORE applying scale so lossyScale compensation is correct
        if (parentWall != null)
            poster.transform.SetParent(parentWall, worldPositionStays: true);

        ApplyWorldSizeToPoster(poster.transform, widthMeters, heightMeters);

        if (logSizes)
            Debug.Log($"[PosterSpawner] CREATE parent={(parentWall ? parentWall.name : "null")} " +
                      $"lossy={(parentWall ? parentWall.lossyScale.ToString() : "n/a")} " +
                      $"localScale={poster.transform.localScale} " +
                      $"lossyScale={poster.transform.lossyScale} " +
                      $"requested=({widthMeters}, {heightMeters})");

        // 5) Download texture
        using (var req = UnityWebRequestTexture.GetTexture(imageUrl))
        {
            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[PosterSpawner] Download failed: " + req.error);
                Destroy(poster);
                yield break;
            }

            var tex = DownloadHandlerTexture.GetContent(req);
            tex.wrapMode = TextureWrapMode.Clamp;

            // 6) Apply material
            var r = poster.GetComponent<Renderer>();
            Material mat = (posterMaterialTemplate != null)
                ? new Material(posterMaterialTemplate)
                : new Material(Shader.Find("Unlit/Texture"));
            mat.mainTexture = tex;
            r.material = mat;

            // 7) Cache PNG locally
            byte[] png = tex.EncodeToPNG();
            string localPath = Path.Combine(PostersDir, $"{persist.id}.png");
            File.WriteAllBytes(localPath, png);
            persist.localPngPath = localPath;

            Debug.Log("[PosterSpawner] Saved poster png: " + localPath);
        }

        // 8) Make gaze-editable
        var ai = poster.GetComponent<AIControllable>();
        if (ai == null)
            ai = poster.AddComponent<AIControllable>();

        ai.targetRenderer = poster.GetComponent<Renderer>();
        ai.rb = null;
        ai.useRigidbodyWhenAvailable = false;

        // 9) Add network identity + transform/color/scale sync for future edits.
        //    NetworkedAIObjectSync inherits NetworkIdBehaviour, so this gives the
        //    poster a stable NetworkId that can be used later for commands like:
        //    "delete this poster", "rotate this poster", "make this poster bigger".
        var objectSync = poster.GetComponent<NetworkedAIObjectSync>();
        if (objectSync == null)
            objectSync = poster.AddComponent<NetworkedAIObjectSync>();

        objectSync.ai = ai;
        objectSync.targetRenderer = poster.GetComponent<Renderer>();

        var store = FindFirstObjectByType<SceneStateStore>();
        objectSync.sceneStateStore = store;

        // Posters live under walls that may have very different lossyScale on different
        // machines, so localScale is NOT portable. Width/height in meters are the source
        // of truth and are sent via NetworkedAIAssetSync.SendPosterCreated instead.
        objectSync.syncScale = false;

        if (string.IsNullOrEmpty(objectSync.NetworkId))
            objectSync.NetworkId = persist.id;

        // Keep width/height metadata in sync before saving/sending.
        persist.SyncSizeFromTransform();

        // 10) Send poster creation to other machines.
        //     Without this call, the poster is only created locally.
        var assetSync = FindFirstObjectByType<NetworkedAIAssetSync>();
        if (assetSync != null)
        {
            assetSync.SendPosterCreated(persist);
            Debug.Log("[PosterSpawner] Sent poster create sync: " + imageUrl);
        }
        else
        {
            Debug.LogWarning("[PosterSpawner] NetworkedAIAssetSync not found. Poster will not sync.");
        }

        // 11) Save state NOW — localPngPath is populated and the poster is fully
        //     set up. This replaces the early RequestSave() in VoiceCaptureAndSend
        //     which fired before the PNG download coroutine finished, resulting in
        //     an empty localPngPath being persisted and a blank image on reload.
        if (store != null) store.RequestSave();
    }

    // -------------------------------------------------------------------------
    // Scale utility — used by both CreatePosterCoroutine and SceneStateStore.
    // Must be called AFTER parenting so lossyScale is correct.
    // -------------------------------------------------------------------------
    public void ApplyWorldSizeToPoster(Transform posterTransform, float widthMeters, float heightMeters)
    {
        widthMeters = Mathf.Max(0.01f, widthMeters);
        heightMeters = Mathf.Max(0.01f, heightMeters);

        Vector3 parentLossy = posterTransform.parent != null
            ? posterTransform.parent.lossyScale
            : Vector3.one;

        float safeX = Mathf.Abs(parentLossy.x) > 0.0001f ? Mathf.Abs(parentLossy.x) : 1f;
        float safeY = Mathf.Abs(parentLossy.y) > 0.0001f ? Mathf.Abs(parentLossy.y) : 1f;

        posterTransform.localScale = new Vector3(
            widthMeters / safeX,
            heightMeters / safeY,
            1f
        );
    }
}
