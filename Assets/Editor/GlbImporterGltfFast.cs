#if UNITY_EDITOR
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using GLTFast;

public static class GlbImporterGltfFast
{
    public static async Task<GameObject> ImportAndInstantiateAsync(string assetPath)
    {
        // Convert "Assets/..." to absolute path
        var fullPath = Path.GetFullPath(assetPath);
        var uri = $"file://{fullPath}";

        var gltf = new GltfImport();
        bool success = await gltf.Load(uri);
        if (!success)
        {
            Debug.LogError("glTFast: Failed to load GLB");
            return null;
        }

        var root = new GameObject(Path.GetFileNameWithoutExtension(assetPath));
        success = await gltf.InstantiateMainSceneAsync(root.transform);
        if (!success)
        {
            Object.DestroyImmediate(root);
            Debug.LogError("glTFast: Failed to instantiate GLB");
            return null;
        }

        return root;
    }
}
#endif
