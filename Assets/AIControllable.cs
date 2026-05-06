using UnityEngine;

[DisallowMultipleComponent]
public class AIControllable : MonoBehaviour
{
    [Header("Rendering")]
    public Renderer targetRenderer;

    [Header("Physics (optional but recommended)")]
    [Tooltip("If empty, will auto-pick Rigidbody on this GameObject (not children).")]
    public Rigidbody rb;

    [Tooltip("If true, Move/Translate will use Rigidbody when physics is enabled.")]
    public bool useRigidbodyWhenAvailable = true;

    [Header("Material (persisted by name)")]
    [Tooltip("Last applied material key/name. Persisted by SceneStateStore.")]
    public string materialName = "";

    // Cache instanced materials (supports multi-material renderers)
    Material[] _matInstances;

    void Awake()
    {
        // Include inactive children just in case
        if (targetRenderer == null) targetRenderer = GetComponentInChildren<Renderer>(true);
        if (rb == null) rb = GetComponent<Rigidbody>();

        if (targetRenderer != null)
            _matInstances = targetRenderer.materials; // instanced, safe at runtime
    }

    Material[] Mats
    {
        get
        {
            if (targetRenderer == null) return null;

            if (_matInstances == null || _matInstances.Length == 0)
                _matInstances = targetRenderer.materials;

            return _matInstances;
        }
    }

    void ApplyMaterialToAllSlots(Material m)
    {
        if (targetRenderer == null || m == null) return;

        var mats = targetRenderer.materials; // instanced array
        for (int i = 0; i < mats.Length; i++)
            mats[i] = m;

        targetRenderer.materials = mats;
        _matInstances = mats;
    }

    // ---------------- Material (by name) ----------------

    public bool TryGetMaterialName(out string name)
    {
        name = materialName;
        return !string.IsNullOrEmpty(name);
    }

    // Stable hash (FNV-1a) so it persists across restarts
    static int StableHash(string s)
    {
        unchecked
        {
            const int fnvOffset = (int)2166136261;
            const int fnvPrime = 16777619;

            int hash = fnvOffset;
            for (int i = 0; i < s.Length; i++)
            {
                hash ^= s[i];
                hash *= fnvPrime;
            }
            return hash;
        }
    }

    /// <summary>
    /// Option A: Persist only the name.
    /// 1) Try Resources.Load<Material>(name) or "Materials/name"
    /// 2) If not found, create a deterministic fallback Standard material (so it still looks different and persists).
    /// Applies to ALL material slots (multi-material meshes).
    /// </summary>
    public void SetMaterial(string name)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrEmpty(name))
        {
            Debug.LogWarning($"{this.name}: SetMaterial called with empty name.");
            return;
        }

        materialName = name;

        if (targetRenderer == null)
        {
            Debug.LogWarning($"{this.name}: No targetRenderer to set material.");
            return;
        }

        // 1) Try load from Resources (if you later add real materials here)
        Material loaded = Resources.Load<Material>(name);
        if (loaded == null) loaded = Resources.Load<Material>($"Materials/{name}");

        if (loaded != null)
        {
            ApplyMaterialToAllSlots(loaded);
            return;
        }

        // 2) Fallback: deterministic Standard material based on the name
        var shader = Shader.Find("Standard");
        if (shader == null)
        {
            Debug.LogWarning($"{this.name}: Standard shader not found; cannot create fallback material.");
            return;
        }

        var m = new Material(shader);
        m.name = $"AutoMat_{name}";

        int h = StableHash(name.ToLowerInvariant());
        float r = ((h >> 0) & 255) / 255f;
        float g = ((h >> 8) & 255) / 255f;
        float b = ((h >> 16) & 255) / 255f;
        var col = new Color(r, g, b, 1f);

        string nlow = name.ToLowerInvariant();
        if (nlow.Contains("wood"))
        {
            m.color = new Color(Mathf.Lerp(0.35f, 0.75f, r), Mathf.Lerp(0.2f, 0.5f, g), Mathf.Lerp(0.1f, 0.25f, b), 1f);
            m.SetFloat("_Metallic", 0.05f);
            m.SetFloat("_Glossiness", 0.2f);
        }
        else if (nlow.Contains("metal") || nlow.Contains("steel") || nlow.Contains("iron"))
        {
            m.color = Color.Lerp(Color.gray, col, 0.25f);
            m.SetFloat("_Metallic", 0.9f);
            m.SetFloat("_Glossiness", 0.75f);
        }
        else if (nlow.Contains("glass"))
        {
            m.color = Color.Lerp(Color.white, col, 0.1f);
            m.SetFloat("_Metallic", 0.0f);
            m.SetFloat("_Glossiness", 0.95f);
        }
        else if (nlow.Contains("marble") || nlow.Contains("stone"))
        {
            m.color = Color.Lerp(Color.white, col, 0.15f);
            m.SetFloat("_Metallic", 0.0f);
            m.SetFloat("_Glossiness", 0.35f);
        }
        else
        {
            m.color = col;
            m.SetFloat("_Metallic", 0.0f);
            m.SetFloat("_Glossiness", 0.5f);
        }

        ApplyMaterialToAllSlots(m);
    }

    // ---------------- Color ----------------

    public bool TryGetColor(out Color c)
    {
        var mats = Mats;
        if (mats == null || mats.Length == 0) { c = default; return false; }

        // Representative color (first slot)
        c = mats[0].color;
        return true;
    }

    public bool TrySetColor(Color c)
    {
        var mats = Mats;
        if (mats == null || mats.Length == 0) return false;

        // Apply to ALL sub-materials
        for (int i = 0; i < mats.Length; i++)
            mats[i].color = c;

        return true;
    }

    public void SetColor(string colorName)
    {
        if (!TryParseColor(colorName, out var c))
        {
            Debug.LogWarning("Unknown color: " + colorName);
            return;
        }
        TrySetColor(c);
    }

    // ---------------- Transform / Movement ----------------
    public void MoveWorld(float x, float y, float z)
    {
        Vector3 p = new Vector3(x, y, z);

        if (useRigidbodyWhenAvailable && rb != null && !rb.isKinematic)
            rb.MovePosition(p);
        else
            transform.position = p;
    }

    public void TranslateWorld(float dx, float dy, float dz)
    {
        Vector3 delta = new Vector3(dx, dy, dz);

        if (useRigidbodyWhenAvailable && rb != null && !rb.isKinematic)
            rb.MovePosition(rb.position + delta);
        else
            transform.position += delta;
    }

    public void SetScaleUniform(float s)
    {
        float v = Mathf.Max(0.0001f, s);
        transform.localScale = Vector3.one * v;
    }

    public void SetScaleXYZ(float x, float y, float z)
    {
        transform.localScale = new Vector3(
            Mathf.Max(0.0001f, x),
            Mathf.Max(0.0001f, y),
            Mathf.Max(0.0001f, z)
        );
    }

    public void ScaleBy(float factor)
    {
        factor = Mathf.Max(0.0001f, factor);
        Vector3 s = transform.localScale * factor;

        s.x = Mathf.Max(0.0001f, s.x);
        s.y = Mathf.Max(0.0001f, s.y);
        s.z = Mathf.Max(0.0001f, s.z);

        transform.localScale = s;
    }

    public void PlaceOnFloor(float floorY = 0f, float gap = 0.001f)
    {
        var col = GetComponent<Collider>();
        float halfHeight = 0.5f;
        if (col != null) halfHeight = col.bounds.extents.y;

        Vector3 p = transform.position;
        p.y = floorY + halfHeight + gap;

        if (useRigidbodyWhenAvailable && rb != null && !rb.isKinematic)
            rb.MovePosition(p);
        else
            transform.position = p;

        Physics.SyncTransforms();
    }

    // ---------------- Physics actions ----------------
    public void EnablePhysics(bool enable)
    {
        if (rb == null)
        {
            Debug.LogWarning($"{name}: No Rigidbody found. Add one to enable physics actions.");
            return;
        }

        rb.isKinematic = !enable;
        rb.useGravity = enable;

        if (!enable)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }

    public void Drop() => EnablePhysics(true);
    public void Freeze() => EnablePhysics(false);

    public void StackOn(AIControllable baseObj, float gap = 0.01f)
    {
        if (baseObj == null) return;

        Freeze();
        baseObj.Freeze();

        var myCol = GetComponent<BoxCollider>();
        var baseCol = baseObj.GetComponent<BoxCollider>();

        if (myCol == null || baseCol == null)
        {
            Debug.LogWarning("StackOn requires BoxCollider on both cubes.");
            return;
        }

        Bounds baseBounds = baseCol.bounds;
        Bounds myBounds = myCol.bounds;

        Vector3 p = transform.position;
        p.x = baseBounds.center.x;
        p.z = baseBounds.center.z;
        p.y = baseBounds.max.y + myBounds.extents.y + gap;

        if (rb != null)
        {
            rb.position = p;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        else
        {
            transform.position = p;
        }

        Physics.SyncTransforms();
        EnablePhysics(true);
        baseObj.EnablePhysics(true);
        Debug.Log($"[StackOn] {name} stacked on {baseObj.name} at {p}");
    }

    bool TryParseColor(string name, out Color c)
    {
        name = (name ?? "").Trim().ToLowerInvariant();

        while (name.Length > 0 && (name.EndsWith(".") || name.EndsWith(",") || name.EndsWith("!")))
            name = name.Substring(0, name.Length - 1).Trim();

        switch (name)
        {
            case "red": c = Color.red; return true;
            case "green": c = Color.green; return true;
            case "blue": c = Color.blue; return true;
            case "yellow": c = Color.yellow; return true;
            case "white": c = Color.white; return true;
            case "black": c = Color.black; return true;
            case "gray":
            case "grey": c = Color.gray; return true;
            case "cyan": c = Color.cyan; return true;
            case "magenta": c = Color.magenta; return true;
            case "orange": c = new Color(1f, 0.5f, 0f); return true;
            case "purple": c = new Color(0.5f, 0f, 1f); return true;
            case "pink": c = new Color(1f, 0.4f, 0.7f); return true;
            default: c = default; return false;
        }
    }
}
