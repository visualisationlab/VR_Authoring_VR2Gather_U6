using System.Text;
using UnityEngine;

/// <summary>
/// Builds a plain-text description of all AIControllable objects currently
/// in the scene. Feed this into the GPT system prompt so the model knows
/// exactly which objects exist and what it can target.
///
/// Usage:
///   string context = SceneContextBuilder.Instance.Build();
/// </summary>
public class SceneContextBuilder : MonoBehaviour
{
    public static SceneContextBuilder Instance { get; private set; }

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    /// <summary>
    /// Returns a compact scene snapshot string, e.g.:
    ///
    ///   SCENE OBJECTS (speak the exact name to target one):
    ///   - "Human_01"  pos=(0.00, 0.00, 2.00)  scale=(1.00, 1.00, 1.00)  color=#FF5733
    ///   - "Cube"      pos=(1.50, 0.50, 1.00)  scale=(0.50, 0.50, 0.50)  color=#FFFFFF
    /// </summary>
    public string Build()
    {
        var sb = new StringBuilder();
        sb.AppendLine("SCENE OBJECTS (use the exact name to target one):");

        var all = FindObjectsByType<AIControllable>(FindObjectsSortMode.None);
        if (all == null || all.Length == 0)
        {
            sb.AppendLine("  (none)");
            return sb.ToString();
        }

        foreach (var a in all)
        {
            if (a == null) continue;

            var t = a.transform;
            string pos   = $"({t.position.x:0.00},{t.position.y:0.00},{t.position.z:0.00})";
            string scale = $"({t.localScale.x:0.00},{t.localScale.y:0.00},{t.localScale.z:0.00})";

            string colorStr = "";
            if (a.TryGetColor(out Color c))
                colorStr = $"  color=#{ColorUtility.ToHtmlStringRGB(c)}";

            sb.AppendLine($"  - \"{a.gameObject.name}\"  pos={pos}  scale={scale}{colorStr}");
        }

        return sb.ToString();
    }
}
