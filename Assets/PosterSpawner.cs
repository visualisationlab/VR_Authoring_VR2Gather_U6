using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.IO;

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
        var ai = poster.AddComponent<AIControllable>();
        ai.targetRenderer = poster.GetComponent<Renderer>();
        ai.rb = null;
        ai.useRigidbodyWhenAvailable = false;

        // 9) ✅ Save state NOW — localPngPath is populated and the poster is fully
        //    set up. This replaces the early RequestSave() in VoiceCaptureAndSend
        //    which fired before the PNG download coroutine finished, resulting in
        //    an empty localPngPath being persisted and a blank image on reload.
        var store = FindFirstObjectByType<SceneStateStore>();
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
