using System;
using UnityEngine;
using VRT.Orchestrator;
using VRT.OrchestratorComm;
using VRT.Pilots.Common;

/// <summary>
/// Multiplayer sync for AI-generated runtime C# behaviours.
///
/// What this script fixes:
/// 1) Sender machine sends the generated C# source to the other clients.
/// 2) Receiver machine compiles + attaches the same source through RuntimeBehaviourRegistry.
///    This is important because RegisterAndAttach also writes runtime_behaviours.json locally,
///    so the behaviour survives scene/app restart on the receiver.
/// 3) For runtime-spawned GLB models, the behaviourPrompt is also stored in
///    generated_models_registry.json through RuntimeModelSpawner.SaveBehaviourPrompt().
///    This protects against script reattach timing issues when the model is respawned later.
/// </summary>
public class NetworkedAIBehaviourSync : MonoBehaviour
{
    [Serializable]
    public class AIRuntimeCodeSyncMessage : BaseMessage
    {
        public string TargetNetworkId;
        public string GeneratedCode;
        public string BehaviourPrompt;
        public string EffectId;
        public bool IsParticle;
    }

    [Header("Debug")]
    public bool debug = true;

    void Awake()
    {
        VRTOrchestratorSingleton.Comm.RegisterEventType(
            AIMessageTypeID.TID_AIRuntimeCodeSyncMessage,
            typeof(AIRuntimeCodeSyncMessage)
        );
    }

    void OnEnable()
    {
        VRTOrchestratorSingleton.Comm.Subscribe<AIRuntimeCodeSyncMessage>(OnRuntimeCodeReceived);
    }

    void OnDisable()
    {
        VRTOrchestratorSingleton.Comm?.Unsubscribe<AIRuntimeCodeSyncMessage>(OnRuntimeCodeReceived);
    }

    public void SendRuntimeCode(
        string targetNetworkId,
        string generatedCode,
        string behaviourPrompt,
        string effectId,
        bool isParticle)
    {
        if (string.IsNullOrWhiteSpace(targetNetworkId))
        {
            Debug.LogWarning("[NetworkedAIBehaviourSync] Cannot send runtime code: empty targetNetworkId.");
            return;
        }

        if (string.IsNullOrWhiteSpace(generatedCode))
        {
            Debug.LogWarning("[NetworkedAIBehaviourSync] Cannot send runtime code: generatedCode is empty.");
            return;
        }

        var msg = new AIRuntimeCodeSyncMessage
        {
            TargetNetworkId = targetNetworkId,
            GeneratedCode = generatedCode,
            BehaviourPrompt = behaviourPrompt ?? "",
            EffectId = effectId ?? "",
            IsParticle = isParticle
        };

        if (!VRTOrchestratorSingleton.Comm.UserIsMaster)
            VRTOrchestratorSingleton.Comm.SendTypeEventToMaster(msg);
        else
            VRTOrchestratorSingleton.Comm.SendTypeEventToAll(msg);

        if (debug)
            Debug.Log($"[NetworkedAIBehaviourSync] Sent runtime code sync. targetNetworkId={targetNetworkId}, particle={isParticle}");
    }

    void OnRuntimeCodeReceived(AIRuntimeCodeSyncMessage msg)
    {
        if (msg == null)
            return;

        string selfId = VRTOrchestratorSingleton.Comm?.SelfUser?.userId;
        if (!string.IsNullOrEmpty(selfId) && msg.SenderId == selfId)
            return;

        // If a non-master sends to the master, the master must forward to the rest.
        // This mirrors the pattern used by NetworkedAIObjectSync.
        if (VRTOrchestratorSingleton.Comm.UserIsMaster)
            VRTOrchestratorSingleton.Comm.SendTypeEventToAll(msg, true);

        if (debug)
            Debug.Log($"[NetworkedAIBehaviourSync] RECEIVED runtime code. targetNetworkId={msg.TargetNetworkId}, particle={msg.IsParticle}");

        GameObject target = FindByNetworkId(msg.TargetNetworkId);
        if (target == null)
        {
            Debug.LogWarning("[NetworkedAIBehaviourSync] Target not found for runtime code: " + msg.TargetNetworkId);
            return;
        }

        if (RuntimeBehaviourRegistry.Instance == null)
        {
            Debug.LogWarning("[NetworkedAIBehaviourSync] RuntimeBehaviourRegistry missing. Remote runtime code cannot be persisted.");
            return;
        }

        string compileError;
        bool ok = RuntimeBehaviourRegistry.Instance.RegisterAndAttach(
            target,
            msg.GeneratedCode,
            out compileError
        );

        if (!ok)
        {
            Debug.LogError("[NetworkedAIBehaviourSync] Remote compile failed: " + compileError);
            return;
        }

        PersistBehaviourMetadata(target, msg);

        if (msg.IsParticle && ParticleEffectManager.Instance != null && !string.IsNullOrWhiteSpace(msg.EffectId))
        {
            ParticleEffectManager.Instance.SaveEffect(
                msg.EffectId,
                target.name,
                msg.BehaviourPrompt,
                target
            );
        }

        RequestSceneSave();

        if (debug)
            Debug.Log("[NetworkedAIBehaviourSync] Applied + persisted remote runtime code on receiver: " + target.name);
    }

    void PersistBehaviourMetadata(GameObject target, AIRuntimeCodeSyncMessage msg)
    {
        if (target == null || msg == null)
            return;

        string prompt = msg.BehaviourPrompt ?? "";

        // For scene objects, store the prompt in PersistableAIObject too.
        // RuntimeBehaviourRegistry already saves the generated C# source, but this metadata
        // is useful for SceneStateStore and debugging.
        var persistable = target.GetComponent<PersistableAIObject>();
        if (persistable != null)
            persistable.behaviourPrompt = prompt;

        // For generated GLB models, also update generated_models_registry.json.
        // This is important because RuntimeBehaviourRegistry may reattach before the GLB
        // is respawned. RuntimeModelSpawner can reattach after the model exists.
        var modelSpawner = FindFirstObjectByType<RuntimeModelSpawner>();
        if (modelSpawner != null && !string.IsNullOrWhiteSpace(target.name))
            modelSpawner.SaveBehaviourPrompt(target.name, prompt);
    }

    void RequestSceneSave()
    {
        var store = FindFirstObjectByType<SceneStateStore>();
        if (store != null)
            store.RequestSave();
    }

    GameObject FindByNetworkId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var all = FindObjectsByType<NetworkIdBehaviour>(FindObjectsSortMode.None);
        foreach (var n in all)
        {
            if (n != null && n.NetworkId == id)
                return n.gameObject;
        }

        return null;
    }
}
