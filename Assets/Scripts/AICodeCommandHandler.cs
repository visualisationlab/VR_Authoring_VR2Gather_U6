using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public class AICodeCommandHandler : MonoBehaviour
{
    public static AICodeCommandHandler Instance { get; private set; }

    [Header("OpenAI")]
    [Tooltip("Optional: leave empty to load OPENAI_API_KEY from project-root .env")]
    public string openAiApiKey = "";
    public string model = "gpt-4o";
    public int maxTokens = 2000;
    public float temperature = 0.2f;

    [Header("UI Feedback (optional)")]
    public TMPro.TextMeshProUGUI statusText;

    [Header("Auto-Target")]
    [Tooltip("Camera used for raycast auto-detection. Defaults to Camera.main.")]
    public Camera targetCamera;
    [Tooltip("Max raycast distance for auto-target detection.")]
    public float autoTargetRayDistance = 100f;
    [Tooltip("Layer mask for auto-target raycast. Default = all layers.")]
    public LayerMask autoTargetLayers = ~0;

    /// <summary>The last object that was successfully resolved and used as a target.</summary>
    public GameObject LastAutoTarget { get; private set; }

    const string kApiUrl = "https://api.openai.com/v1/chat/completions";

    // {TARGET_NAME} and {SCENE_CONTEXT} are replaced per-request
    const string kSystemPrompt =
        "You are a Unity C# code generator for Unity 2022.3 LTS. " +
        "The user describes a behaviour they want applied to a specific GameObject. " +
        "The TARGET GameObject is named '{TARGET_NAME}'. " +
        "Write code as if it will be attached directly to that object " +
        "(use 'this.gameObject' or 'transform' to refer to it). " +
        "Reply with ONLY a single fenced ```csharp code block. Rules:\n" +
        "  - Particle effects must respect realistic room-scale size suitable for XR environments.\n" +
        "    Default particle sizes should be small and subtle unless the user explicitly requests large effects.\n" +
        "    Use startSize typically in the range of 0.02 to 0.2 units.\n" +
        "    Avoid particles that are so large that they cover large parts of walls, tables, or the camera view.\n" +
        "    Particle systems should enhance the scene, not dominate it.\n" +
        "  - One class per response, must inherit MonoBehaviour.\n" +
        "  - Only use: UnityEngine, System, System.Collections, System.Collections.Generic.\n" +
        "  - No Editor-only APIs. No ML-Agents. No external packages.\n" +
        "  - Keep it simple and correct for Unity 2022.\n" +
        "  - Prefer transform-based movement/rotation.\n" +
        "  - Do not assume Renderer, Animator, or Rigidbody exists on the same GameObject.\n" +
        "  - If visual changes are needed, prefer GetComponentsInChildren<Renderer>().\n" +
        "  - For particle effects: build the ParticleSystem entirely in code using AddComponent<ParticleSystem>().\n" +
        "    Configure all modules (main, emission, shape, colorOverLifetime, sizeOverLifetime, noise,\n" +
        "    velocityOverLifetime, collision, trails, subEmitters) as appropriate for the effect type.\n" +
        "    Always call ps.Play() at the end. Parent the particle GameObject to the target object.\n" +
        "  - For particle effects: build the ParticleSystem entirely in code using AddComponent<ParticleSystem>().\n" +
        "    Configure all modules (main, emission, shape, colorOverLifetime, sizeOverLifetime, noise,\n" +
        "    velocityOverLifetime, collision, trails, subEmitters) as appropriate for the effect type.\n" +
        "    Always call ps.Play() at the end. Parent the particle GameObject to the target object.\n" +
        "  - For smoke, fire, water, fountain, sparks, mist, or similar particle effects:\n" +
        "    ALWAYS create the ParticleSystem directly in code.\n" +
        "    ALWAYS assign a valid material to the ParticleSystemRenderer.\n" +
        "    NEVER leave the particle system without a material, because that causes pink or magenta rendering.\n" +
        "    Create a Material in code using a shader compatible with the current render pipeline.\n" +
        "    First try Shader.Find(\"Particles/Standard Unlit\").\n" +
        "    If that returns null, fallback to Shader.Find(\"Legacy Shaders/Particles/Alpha Blended\").\n" +
        "    After creating the ParticleSystem, get its ParticleSystemRenderer and assign a new Material using the valid shader.\n" +
        "    Example pattern: var psRenderer = ps.GetComponent<ParticleSystemRenderer>(); if (psRenderer != null && validShader != null) psRenderer.material = new Material(validShader);\n" +
        "    Configure the particle system so the effect is immediately visible and visually correct.\n" +
        "  - For smoke specifically:\n" +
        "    Use small, soft particles (typically 0.05 to 0.2).\n" +
        "    Use slow upward motion and fading alpha.\n" +
        "    Use grey or dark colors.\n" +
        "    Enable color over lifetime with fading alpha so the particles fade to transparent.\n" +
        "    Use low speed and upward motion.\n" +
        "    - For water or fountain effects:\n" +
        "    Use small droplet-like particles.\n" +
        "    Avoid large splash quads.\n" +
        "    Keep particle size modest (typically 0.02 to 0.1).\n" +
        "    Use higher speed but small size for realism.\n" +
        "    Use soft spread with a cone, sphere, circle, or rectangle shape as appropriate.\n" +
        "  - The generated script must work immediately after being attached, without requiring any manual setup in the Unity Inspector.\n" +
        "  PARTICLE API RULES — these are hard Unity 2022 constraints, never violate them:\n" +
        "  - NEVER use ParticleSystemShapeType.Plane — it does not exist. Use ParticleSystemShapeType.Rectangle instead.\n" +
        "  - NEVER use lights.color on ParticleSystem.LightsModule — that property does not exist.\n" +
        "    To tint particle lights, get the Light component from a child GameObject and set light.color there.\n" +
        "  - NEVER use ParticleSystemShapeType.Cone as a flat emitter — use Rectangle or Circle instead.\n" +
        "  - Do NOT include any explanation outside the code block.\n\n" +
        "Current scene objects:\n{SCENE_CONTEXT}";

    // ------------------------------------------------------------------ lifecycle

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        if (string.IsNullOrWhiteSpace(openAiApiKey))
            openAiApiKey = LoadKeyFromEnv("OPENAI_API_KEY");

        if (string.IsNullOrWhiteSpace(openAiApiKey))
            Debug.LogWarning("[AICodeCommandHandler] No OpenAI API key found.");
        else
            Debug.Log("[AICodeCommandHandler] Ready.");
    }

    // ------------------------------------------------------------------ public API

    /// <summary>
    /// Main entry point from the XR server pipeline.
    /// Called with the behaviour_prompt and resolved target from run_code command.
    /// Generates a C# script via GPT and attaches it to the target.
    /// If the behaviour involves particles, also saves to ParticleEffectManager for persistence.
    /// </summary>
    public void HandleCommand(string naturalLanguageCommand)
    {
        GameObject target = ResolveTarget(naturalLanguageCommand);
        HandleCommand(naturalLanguageCommand, target);
    }

    /// <summary>Explicit overload — supply a target directly if you already know it.</summary>
    public void HandleCommand(string naturalLanguageCommand, GameObject target)
    {
        if (string.IsNullOrWhiteSpace(naturalLanguageCommand))
        {
            Log("Empty command.");
            return;
        }

        if (target == null)
        {
            Log("No target found. Look at an object, or mention its name in the command.");
            return;
        }

        if (string.IsNullOrWhiteSpace(openAiApiKey))
        {
            Log("OpenAI key not found in Inspector or .env");
            return;
        }

        LastAutoTarget = target;
        StartCoroutine(RequestCode(naturalLanguageCommand, target, effectId: null, isReplay: false));
    }

    /// <summary>
    /// Called by ParticleEffectManager when replaying a saved particle effect on scene reload,
    /// and also used for new particle commands that need to be saved for persistence.
    ///
    /// isReplay = true  → skips saving again (already in save file)
    /// isReplay = false → saves the effect after successful compile
    /// effectId = null  → generates a new ID (new command)
    /// effectId = "..." → reuses the existing ID (replay)
    /// </summary>
    public void HandleRunCode(string behaviourPrompt, GameObject target,
                              string effectId = null, bool isReplay = false)
    {
        if (target == null)
        {
            Log("HandleRunCode: target is null, skipping.");
            return;
        }

        if (string.IsNullOrWhiteSpace(openAiApiKey))
        {
            Log("OpenAI key not found.");
            return;
        }

        string id = string.IsNullOrEmpty(effectId) ? Guid.NewGuid().ToString() : effectId;
        LastAutoTarget = target;

        StartCoroutine(RequestCode(behaviourPrompt, target, id, isReplay));
    }

    // ------------------------------------------------------------------ auto-target

    /// <summary>
    /// Resolves the best target GameObject using three strategies in order:
    ///   1. Raycast from camera centre (what the player is looking at)
    ///   2. Name match — does any scene object's name appear in the command?
    ///   3. Fallback to the last successfully resolved target
    /// </summary>
    public GameObject ResolveTarget(string command)
    {
        // 1. Raycast from camera centre
        Camera cam = targetCamera != null ? targetCamera : Camera.main;
        if (cam != null)
        {
            Ray ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (Physics.Raycast(ray, out RaycastHit hit, autoTargetRayDistance, autoTargetLayers))
            {
                GameObject found = hit.collider.gameObject;

                // Walk up to the meaningful root — prefer PersistableAIObject root
                var persist = found.GetComponentInParent<PersistableAIObject>();
                if (persist != null)
                {
                    found = persist.gameObject;
                }
                else
                {
                    Transform t = found.transform;
                    while (t.parent != null && t.parent.parent != null)
                        t = t.parent;
                    found = t.gameObject;
                }

                Log("Auto-target (raycast): " + found.name);
                LastAutoTarget = found;
                return found;
            }
        }

        // 2. Name match in command text
        if (!string.IsNullOrWhiteSpace(command))
        {
            GameObject byName = FindObjectByNameInCommand(command);
            if (byName != null)
            {
                Log("Auto-target (name match): " + byName.name);
                LastAutoTarget = byName;
                return byName;
            }
        }

        // 3. Fallback to last known target
        if (LastAutoTarget != null)
        {
            Log("Auto-target (fallback): " + LastAutoTarget.name);
            return LastAutoTarget;
        }

        Log("Could not resolve a target automatically.");
        return null;
    }

    static GameObject FindObjectByNameInCommand(string command)
    {
        string cmdLower = command.ToLowerInvariant();

        foreach (var root in UnityEngine.SceneManagement.SceneManager
                                .GetActiveScene().GetRootGameObjects())
        {
            if (NameAppearsInCommand(root.name, cmdLower))
                return root;

            foreach (Transform child in root.transform)
                if (NameAppearsInCommand(child.name, cmdLower))
                    return child.gameObject;
        }

        return null;
    }

    static bool NameAppearsInCommand(string objectName, string commandLower)
    {
        if (string.IsNullOrWhiteSpace(objectName) || objectName.Length < 3)
            return false;
        return commandLower.Contains(objectName.ToLowerInvariant());
    }

    // ------------------------------------------------------------------ core coroutine

    IEnumerator RequestCode(string userCommand, GameObject target,
                            string effectId, bool isReplay)
    {
        Log($"Asking GPT for code — target: '{target.name}'" +
            (isReplay ? " [REPLAY]" : "") + "...");

        string sceneCtx = SceneContextBuilder.Instance != null
            ? SceneContextBuilder.Instance.Build()
            : "(SceneContextBuilder not found)";

        string systemPrompt = kSystemPrompt
            .Replace("{TARGET_NAME}", target.name)
            .Replace("{SCENE_CONTEXT}", sceneCtx);

        // Up to 2 attempts: first normal, second with compile errors fed back to GPT
        string lastCode         = null;
        string lastCompileError = null;

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            // On retry, append the exact compile errors so GPT can self-correct
            string userMsg = attempt == 1
                ? userCommand
                : $"{userCommand}\n\n" +
                  $"Your previous code attempt failed to compile with these errors:\n{lastCompileError}\n\n" +
                  $"Here was your previous code:\n```csharp\n{lastCode}\n```\n\n" +
                  $"Fix ONLY the compile errors and return the corrected full script.";

            string payload = BuildPayload(systemPrompt, userMsg);
            byte[] body    = Encoding.UTF8.GetBytes(payload);

            using (var www = new UnityWebRequest(kApiUrl, "POST"))
            {
                www.uploadHandler   = new UploadHandlerRaw(body);
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");
                www.SetRequestHeader("Authorization", "Bearer " + openAiApiKey);

                yield return www.SendWebRequest();

                if (www.result != UnityWebRequest.Result.Success)
                {
                    Log("GPT request failed: " + www.error + "\n" + www.downloadHandler.text);
                    yield break;
                }

                string gptText = ExtractContent(www.downloadHandler.text);
                if (string.IsNullOrEmpty(gptText))
                {
                    Log("Empty GPT response.");
                    yield break;
                }

                Debug.Log(attempt == 1
                    ? "[AICodeCommandHandler] GPT response:\n" + gptText
                    : "[AICodeCommandHandler] GPT retry response:\n" + gptText);

                string code = RuntimeBehaviourRegistry.ParseCodeFromGPTResponse(gptText);
                if (string.IsNullOrEmpty(code))
                {
                    Log("No C# code block found in GPT response.");
                    yield break;
                }

                Log($"Compiling and attaching to '{target.name}'" +
                    (attempt > 1 ? $" (retry {attempt})" : "") + "...");

                // Try the overload that returns compile errors (out param).
                // If RuntimeBehaviourRegistry only has the old bool overload,
                // add: public bool RegisterAndAttach(GameObject go, string code, out string err)
                // that captures errors from the compiler diagnostics and returns them via err.
                string compileError = null;
                bool ok = RuntimeBehaviourRegistry.Instance != null &&
                          RuntimeBehaviourRegistry.Instance.RegisterAndAttach(
                              target, code, out compileError);

                if (ok)
                {
                    Log($"Attached to '{target.name}' successfully" +
                        (attempt > 1 ? " after retry." : "."));

                    // --- Particle persistence ---
                    if (!isReplay && IsParticleCommand(userCommand) && effectId != null)
                    {
                        if (ParticleEffectManager.Instance != null)
                        {
                            GameObject particleGO = FindParticleChildOnTarget(target);
                            ParticleEffectManager.Instance.SaveEffect(
                                effectId:        effectId,
                                targetName:      target.name,
                                behaviourPrompt: userCommand,
                                spawnedGO:       particleGO ?? target
                            );
                        }
                        else
                        {
                            Debug.LogWarning("[AICodeCommandHandler] ParticleEffectManager not found — particle will not persist.");
                        }
                    }
                    yield break; // success
                }

                // Compile failed — store errors for retry
                lastCode         = code;
                lastCompileError = compileError ?? "Unknown compile error.";
                Log($"Compile failed (attempt {attempt}): {lastCompileError}");

                if (attempt == 2)
                    Log("Compile failed after retry — check Console for details.");
                // else: loop continues to attempt 2
            }
        }
    }

    // ------------------------------------------------------------------ particle helpers

    /// <summary>
    /// Detects whether a behaviour_prompt describes a particle effect.
    /// The LLM is responsible for writing the actual code — this just decides
    /// whether to save the command for persistence.
    /// </summary>
    static bool IsParticleCommand(string prompt)
    {
        if (string.IsNullOrEmpty(prompt)) return false;
        string lower = prompt.ToLowerInvariant();

        return lower.Contains("particle") ||
               lower.Contains("fire")     ||
               lower.Contains("flame")    ||
               lower.Contains("smoke")    ||
               lower.Contains("spark")    ||
               lower.Contains("explosion")||
               lower.Contains("fountain") ||
               lower.Contains("water")    ||
               lower.Contains("rain")     ||
               lower.Contains("snow")     ||
               lower.Contains("magic")    ||
               lower.Contains("glow")     ||
               lower.Contains("trail")    ||
               lower.Contains("dust")     ||
               lower.Contains("fog")      ||
               lower.Contains("mist");
    }

    /// <summary>
    /// After GPT attaches a script, the generated code typically creates a
    /// child GameObject for the ParticleSystem. This finds it.
    /// </summary>
    static GameObject FindParticleChildOnTarget(GameObject target)
    {
        // Look one frame later — PS child is created on Start in the generated script
        // so we check all children for a ParticleSystem component
        foreach (Transform child in target.transform)
        {
            if (child.GetComponent<ParticleSystem>() != null)
                return child.gameObject;
        }

        // Maybe the PS is directly on the target
        if (target.GetComponent<ParticleSystem>() != null)
            return target;

        return null;
    }

    // ------------------------------------------------------------------ helpers

    string BuildPayload(string system, string user)
    {
        string s = EscapeJson(system);
        string u = EscapeJson(user);
        string t = temperature.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return "{\"model\":\"" + model + "\",\"max_tokens\":" + maxTokens +
               ",\"temperature\":" + t +
               ",\"messages\":[{\"role\":\"system\",\"content\":\"" + s +
               "\"},{\"role\":\"user\",\"content\":\"" + u + "\"}]}";
    }

    static string ExtractContent(string json)
    {
        const string marker = "\"content\":";
        int idx = json.LastIndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;

        idx += marker.Length;
        while (idx < json.Length && (json[idx] == ' ' || json[idx] == '\n' || json[idx] == '\r'))
            idx++;

        if (idx >= json.Length || json[idx] != '"') return null;
        idx++;

        var sb = new StringBuilder();
        while (idx < json.Length)
        {
            char ch = json[idx++];
            if (ch == '\\' && idx < json.Length)
            {
                char esc = json[idx++];
                if      (esc == '"')  sb.Append('"');
                else if (esc == '\\') sb.Append('\\');
                else if (esc == 'n')  sb.Append('\n');
                else if (esc == 'r')  sb.Append('\r');
                else if (esc == 't')  sb.Append('\t');
                else if (esc == 'u' && idx + 3 < json.Length)
                {
                    string hex = json.Substring(idx, 4);
                    if (ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                                        null, out ushort code))
                    { sb.Append((char)code); idx += 4; }
                    else
                    { sb.Append("\\u"); }
                }
                else sb.Append(esc);
            }
            else if (ch == '"') break;
            else sb.Append(ch);
        }

        return sb.ToString();
    }

    static string EscapeJson(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
    }

    string LoadKeyFromEnv(string keyName)
    {
        try
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string envPath = Path.Combine(projectRoot, ".env");

            if (!File.Exists(envPath))
            {
                Debug.LogWarning("[AICodeCommandHandler] .env not found at: " + envPath);
                return null;
            }

            foreach (var rawLine in File.ReadAllLines(envPath))
            {
                if (string.IsNullOrWhiteSpace(rawLine)) continue;
                string line = rawLine.Trim();
                if (line.StartsWith("#")) continue;
                if (!line.StartsWith(keyName + "=", StringComparison.Ordinal)) continue;

                string value = line.Substring(keyName.Length + 1).Trim();
                if (value.StartsWith("\"") && value.EndsWith("\"") && value.Length >= 2)
                    value = value.Substring(1, value.Length - 2);
                if (value.StartsWith("'") && value.EndsWith("'") && value.Length >= 2)
                    value = value.Substring(1, value.Length - 2);

                return value;
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("[AICodeCommandHandler] Failed reading .env: " + ex);
        }

        return null;
    }

    void Log(string msg)
    {
        Debug.Log("[AICodeCommandHandler] " + msg);
        if (statusText != null) statusText.text = msg;
    }
}
