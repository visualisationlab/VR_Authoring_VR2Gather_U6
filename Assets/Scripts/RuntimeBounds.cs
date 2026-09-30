using UnityEngine;

public static class RuntimeBounds
{
    // Mesh-only bounds: ignores particle, trail, and line renderers
    public static Bounds GetMeshBounds(GameObject go)
    {
        bool found = false;
        Bounds b = new Bounds(go.transform.position, Vector3.zero);
        foreach (var r in go.GetComponentsInChildren<Renderer>())
        {
            if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;
            if (!found) { b = r.bounds; found = true; }
            else b.Encapsulate(r.bounds);
        }
        return b;
    }
}