using UnityEngine;

[DisallowMultipleComponent]
public class PersistablePoster : MonoBehaviour
{
    public string id;
    // Where the image came from (optional, good for debugging / re-download)
    public string imageUrl;
    // Where the image is stored locally (this is what guarantees persistence)
    public string localPngPath;
    public float widthMeters = 1f;
    public float heightMeters = 1f;

    void Awake()
    {
        if (string.IsNullOrEmpty(id))
            id = System.Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Call this after any scale or move operation so widthMeters/heightMeters
    /// stay in sync with the actual world-space size of the quad.
    /// Uses lossyScale so it works correctly regardless of parent scaling.
    /// </summary>
    public void SyncSizeFromTransform()
    {
        widthMeters  = Mathf.Max(0.01f, transform.lossyScale.x);
        heightMeters = Mathf.Max(0.01f, transform.lossyScale.y);
    }

    [System.Serializable]
    public class PosterState
    {
        public string id;
        public float px, py, pz;
        public float rx, ry, rz, rw;
        // sx/sy/sz intentionally removed — localScale is parent-dependent and
        // must be recomputed via PosterSpawner.ApplyWorldSizeToPoster() on restore.
        public float widthMeters, heightMeters;
        public string imageUrl;
        public string localPngPath;
    }

    public PosterState Capture()
    {
        var t = transform;
        return new PosterState
        {
            id = id,
            px = t.position.x,
            py = t.position.y,
            pz = t.position.z,
            rx = t.rotation.x,
            ry = t.rotation.y,
            rz = t.rotation.z,
            rw = t.rotation.w,
            // ✅ sx/sy/sz NOT saved — widthMeters/heightMeters are the source of truth
            widthMeters = widthMeters,
            heightMeters = heightMeters,
            imageUrl = imageUrl,
            localPngPath = localPngPath
        };
    }

    // Apply only restores pose and metadata.
    // Scale MUST be set by PosterSpawner.ApplyWorldSizeToPoster() AFTER parenting,
    // so do NOT set localScale here.
    public void Apply(PosterState s)
    {
        transform.position = new Vector3(s.px, s.py, s.pz);
        transform.rotation = new Quaternion(s.rx, s.ry, s.rz, s.rw);
        // ✅ localScale intentionally NOT set here — caller must call
        //    posterSpawner.ApplyWorldSizeToPoster(transform, s.widthMeters, s.heightMeters)
        //    after parenting is done.
        widthMeters = s.widthMeters;
        heightMeters = s.heightMeters;
        imageUrl = s.imageUrl;
        localPngPath = s.localPngPath;
    }
}
