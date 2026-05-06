using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Singleton that persists particle effects across scene reloads.
///
/// Instead of storing particle settings, it stores the original behaviour_prompt
/// and target name. On reload it re-runs the same run_code command through
/// AICodeCommandHandler — rebuilding the particle system identically.
///
/// USAGE (called by AICodeCommandHandler automatically):
///   ParticleEffectManager.Instance.SaveEffect(effectId, targetName, behaviourPrompt, spawnedGO);
///   ParticleEffectManager.Instance.RemoveEffect(effectId);
///   ParticleEffectManager.Instance.ClearAllEffects();
/// </summary>
public class ParticleEffectManager : MonoBehaviour
{
    // ------------------------------------------------------------------ singleton
    public static ParticleEffectManager Instance { get; private set; }

    // ------------------------------------------------------------------ private
    private readonly Dictionary<string, ParticleEffectEntry> _saved = new();
    private readonly Dictionary<string, GameObject>          _live  = new();

    private static string SavePath =>
        Path.Combine(Application.persistentDataPath, "particle_effects.json");

    // ================================================================== lifecycle

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        LoadAndRespawn();
    }

    // ================================================================== public API

    /// <summary>
    /// Called by AICodeCommandHandler after a particle script is successfully
    /// compiled and attached. Saves the command so it can be replayed on reload.
    /// </summary>
    public void SaveEffect(string effectId, string targetName, string behaviourPrompt, GameObject spawnedGO)
    {
        _saved[effectId] = new ParticleEffectEntry
        {
            effectId        = effectId,
            targetName      = targetName,
            behaviourPrompt = behaviourPrompt,
        };

        _live[effectId] = spawnedGO;

        // Stamp the tracker so we can find this GO by ID later
        var tracker = spawnedGO.GetComponent<ParticleEffectTracker>()
                   ?? spawnedGO.AddComponent<ParticleEffectTracker>();
        tracker.effectId = effectId;

        Save();
        Debug.Log($"[ParticleEffectManager] Saved effect '{effectId}' on '{targetName}'.");
    }

    /// <summary>Removes a live particle effect and deletes it from the save file.</summary>
    public bool RemoveEffect(string effectId)
    {
        if (_live.TryGetValue(effectId, out var go))
        {
            if (go != null) Destroy(go);
            _live.Remove(effectId);
        }

        bool removed = _saved.Remove(effectId);
        if (removed) Save();

        Debug.Log($"[ParticleEffectManager] Removed effect '{effectId}'.");
        return removed;
    }

    /// <summary>Removes all live effects and clears the save file.</summary>
    public void ClearAllEffects()
    {
        foreach (var go in _live.Values)
            if (go != null) Destroy(go);

        _live.Clear();
        _saved.Clear();
        Save();

        Debug.Log("[ParticleEffectManager] Cleared all effects.");
    }

    /// <summary>
    /// Removes any live particle effects attached to a specific target GameObject.
    /// Called automatically when a target object is deleted from the scene.
    /// </summary>
    public void RemoveEffectsOnTarget(string targetName)
    {
        var toRemove = new List<string>();

        foreach (var kv in _saved)
            if (kv.Value.targetName == targetName)
                toRemove.Add(kv.Key);

        foreach (var id in toRemove)
            RemoveEffect(id);
    }

    // ================================================================== save / load

    void Save()
    {
        var entries = new ParticleEffectEntry[_saved.Count];
        _saved.Values.CopyTo(entries, 0);

        string json = JsonUtility.ToJson(new ParticleEffectSaveFile { effects = entries }, prettyPrint: true);
        File.WriteAllText(SavePath, json);
        Debug.Log($"[ParticleEffectManager] Saved {entries.Length} effect(s) → {SavePath}");
    }

    void LoadAndRespawn()
    {
        if (!File.Exists(SavePath))
        {
            Debug.Log("[ParticleEffectManager] No save file — starting fresh.");
            return;
        }

        string json = File.ReadAllText(SavePath);
        var file = JsonUtility.FromJson<ParticleEffectSaveFile>(json);

        if (file?.effects == null || file.effects.Length == 0)
        {
            Debug.Log("[ParticleEffectManager] Save file empty.");
            return;
        }

        Debug.Log($"[ParticleEffectManager] Replaying {file.effects.Length} saved particle effect(s)...");

        foreach (var entry in file.effects)
            StartCoroutine(ReplayEffect(entry));
    }

    /// <summary>
    /// Waits one frame for the scene to finish loading, then re-runs the
    /// original behaviour_prompt through AICodeCommandHandler.
    /// </summary>
    IEnumerator ReplayEffect(ParticleEffectEntry entry)
    {
        // Wait for scene and AICodeCommandHandler to be fully ready
        yield return null;
        yield return null;

        if (AICodeCommandHandler.Instance == null)
        {
            Debug.LogWarning("[ParticleEffectManager] AICodeCommandHandler not found — cannot replay effect.");
            yield break;
        }

        // Find the target GameObject by name
        GameObject target = GameObject.Find(entry.targetName);
        if (target == null)
        {
            Debug.LogWarning($"[ParticleEffectManager] Target '{entry.targetName}' not found in scene — skipping replay.");
            yield break;
        }

        Debug.Log($"[ParticleEffectManager] Replaying effect on '{entry.targetName}'...");

        // Re-run the exact same run_code command — this regenerates and reattaches
        // the particle script just as if the user said the command again
        AICodeCommandHandler.Instance.HandleRunCode(
            behaviourPrompt:  entry.behaviourPrompt,
            target:           target,
            effectId:         entry.effectId,   // preserves the original ID
            isReplay:         true              // skips re-saving to avoid duplicate entries
        );
    }

    // ================================================================== data types

    [Serializable]
    private class ParticleEffectEntry
    {
        public string effectId;
        public string targetName;
        public string behaviourPrompt;
    }

    [Serializable]
    private class ParticleEffectSaveFile
    {
        public ParticleEffectEntry[] effects = Array.Empty<ParticleEffectEntry>();
    }
}
