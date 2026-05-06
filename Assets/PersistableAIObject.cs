using UnityEngine;

[DisallowMultipleComponent]
public class PersistableAIObject : MonoBehaviour
{
    [Tooltip("Must be unique per object. Click the context menu to generate.")]
    public string id;

    // ---- Texture persistence ----
    public string textureUrl;
    public string localTexturePath;
    public float tileScale = 1.8f;

    public AIControllable controllable;

    [Tooltip("AI-generated behaviour prompt (e.g. 'walk in a circle'). Persisted so the script is reattached on reload.")]
    public string behaviourPrompt = "";

    void Awake()
    {
        if (controllable == null) controllable = GetComponent<AIControllable>();
        if (string.IsNullOrEmpty(id))
            id = System.Guid.NewGuid().ToString("N");
    }

#if UNITY_EDITOR
    [ContextMenu("Generate New ID")]
    void GenerateNewId()
    {
        id = System.Guid.NewGuid().ToString("N");
        UnityEditor.EditorUtility.SetDirty(this);
    }
#endif

    public ObjectState Capture()
    {
        var t = transform;

        Color col = Color.white;
        bool hasColor = controllable != null && controllable.TryGetColor(out col);

        string matName = "";
        bool hasMaterial = controllable != null && controllable.TryGetMaterialName(out matName);

        return new ObjectState
        {
            id = id,

            px = t.position.x,
            py = t.position.y,
            pz = t.position.z,
            rx = t.rotation.x,
            ry = t.rotation.y,
            rz = t.rotation.z,
            rw = t.rotation.w,
            sx = t.localScale.x,
            sy = t.localScale.y,
            sz = t.localScale.z,

            hasColor = hasColor,
            cr = col.r,
            cg = col.g,
            cb = col.b,
            ca = col.a,

            hasMaterial = hasMaterial,
            material = matName,
            textureUrl = textureUrl,
            localTexturePath = localTexturePath,
            tileScale = tileScale,
            behaviourPrompt = behaviourPrompt
        };
    }

    public void Apply(ObjectState s)
    {
        transform.position = new Vector3(s.px, s.py, s.pz);
        transform.rotation = new Quaternion(s.rx, s.ry, s.rz, s.rw);
        transform.localScale = new Vector3(s.sx, s.sy, s.sz);

        if (s.hasColor && controllable != null)
            controllable.TrySetColor(new Color(s.cr, s.cg, s.cb, s.ca));

        if (s.hasMaterial && controllable != null && !string.IsNullOrEmpty(s.material))
            controllable.SetMaterial(s.material);

        // ✅ RESTORE TEXTURE META
        textureUrl = s.textureUrl;
        localTexturePath = s.localTexturePath;
        tileScale = (s.tileScale > 0f) ? s.tileScale : 1.8f;
        behaviourPrompt = s.behaviourPrompt ?? "";

        // ✅ APPLY THE SAVED TEXTURE FROM DISK
        ApplySavedTextureIfAny();
    }

    void ApplySavedTextureIfAny()
    {
        var r = GetComponentInChildren<Renderer>(true);
        if (r == null) return;

        if (!string.IsNullOrEmpty(localTexturePath) && System.IO.File.Exists(localTexturePath))
        {
            byte[] bytes = System.IO.File.ReadAllBytes(localTexturePath);

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(bytes);
            tex.wrapMode = TextureWrapMode.Repeat;

            float ts = (tileScale > 0f) ? tileScale : 1.8f;

            // ✅ Try triplanar shader first for consistent tiling on all wall faces.
            // Falls back to Standard if the shader isn't in the project.
            Shader shader = Shader.Find("Custom/Triplanar");
            bool isTriplanar = shader != null;

            if (!isTriplanar)
            {
                Debug.LogWarning("[PersistableAIObject] Custom/Triplanar shader not found, falling back to Standard.");
                shader = Shader.Find("Standard");
            }

            if (shader == null) return;

            Material newMat = new Material(shader);
            newMat.name        = $"TexMat_{id}_RESTORED";
            newMat.mainTexture = tex;
            newMat.color       = Color.white; // ✅ critical — prevents tinting-to-black

            if (isTriplanar)
            {
                // Triplanar projects in world-space: _Scale controls texture frequency.
                // tileScale = world units per tile, so 1/tileScale = repeats per world unit.
                newMat.SetFloat("_Scale", 1f / ts);
            }
            else
            {
                newMat.SetFloat("_Metallic",   0f);
                newMat.SetFloat("_Glossiness", 0.1f);
                newMat.mainTextureScale = new Vector2(ts, ts);
            }

            // Apply to all sub-material slots
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
    }

    [System.Serializable]
    public class ObjectState
    {
        public string id;

        public float px, py, pz;
        public float rx, ry, rz, rw;
        public float sx, sy, sz;

        public string textureUrl;
        public string localTexturePath;
        public float tileScale;

        public bool hasColor;
        public float cr, cg, cb, ca;

        public bool hasMaterial;
        public string material;
        public string behaviourPrompt;
    }
}
