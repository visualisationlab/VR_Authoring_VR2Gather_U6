using System;
using UnityEngine;
using VRT.Orchestrator;
using VRT.OrchestratorComm;
using VRT.Pilots.Common;

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
        var msg = new AIRuntimeCodeSyncMessage
        {
            TargetNetworkId = targetNetworkId,
            GeneratedCode = generatedCode,
            BehaviourPrompt = behaviourPrompt,
            EffectId = effectId,
            IsParticle = isParticle
        };

        if (!VRTOrchestratorSingleton.Comm.UserIsMaster)
            VRTOrchestratorSingleton.Comm.SendTypeEventToMaster(msg);
        else
            VRTOrchestratorSingleton.Comm.SendTypeEventToAll(msg);

        Debug.Log("[NetworkedAIBehaviourSync] Sent runtime code sync.");
    }

    void OnRuntimeCodeReceived(AIRuntimeCodeSyncMessage msg)
    {
        if (msg.SenderId == VRTOrchestratorSingleton.Comm.SelfUser.userId)
            return;

        GameObject target = FindByNetworkId(msg.TargetNetworkId);

        if (target == null)
        {
            Debug.LogWarning("[NetworkedAIBehaviourSync] Target not found: " + msg.TargetNetworkId);
            return;
        }

        if (RuntimeBehaviourRegistry.Instance == null)
        {
            Debug.LogWarning("[NetworkedAIBehaviourSync] RuntimeBehaviourRegistry missing.");
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

        if (msg.IsParticle && ParticleEffectManager.Instance != null)
        {
            ParticleEffectManager.Instance.SaveEffect(
                msg.EffectId,
                target.name,
                msg.BehaviourPrompt,
                target
            );
        }

        Debug.Log("[NetworkedAIBehaviourSync] Applied remote runtime code to " + target.name);
    }

    GameObject FindByNetworkId(string id)
    {
        var all = FindObjectsByType<NetworkIdBehaviour>(FindObjectsSortMode.None);

        foreach (var n in all)
        {
            if (n != null && n.NetworkId == id)
                return n.gameObject;
        }

        return null;
    }
}