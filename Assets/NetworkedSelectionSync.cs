using System;
using UnityEngine;
using VRT.Orchestrator;
using VRT.OrchestratorComm;
using VRT.Pilots.Common;

public class NetworkedSelectionSync : MonoBehaviour
{
    [Serializable]
    public class SelectionSyncMessage : BaseMessage
    {
        public string TargetNetworkId;
        public bool ClearSelection;
    }

    [Header("References")]
    public GazeTargetInteractor gazeInteractor;

    [Header("Debug")]
    public bool debug = true;

    void Awake()
    {
        VRTOrchestratorSingleton.Comm.RegisterEventType(
            AIMessageTypeID.TID_AISelectionSyncMessage,
            typeof(SelectionSyncMessage)
        );
    }

    void OnEnable()
    {
        VRTOrchestratorSingleton.Comm
            .Subscribe<SelectionSyncMessage>(OnSelectionReceived);
    }

    void OnDisable()
    {
        VRTOrchestratorSingleton.Comm?
            .Unsubscribe<SelectionSyncMessage>(OnSelectionReceived);
    }

    public void SendSelection(GameObject target)
    {
        if (!VRTOrchestratorSingleton.Comm.UserIsMaster)
            return;

        if (target == null)
        {
            SendClearSelection();
            return;
        }

        var net = target.GetComponent<NetworkIdBehaviour>();

        if (net == null)
            net = target.GetComponentInParent<NetworkIdBehaviour>();

        if (net == null || string.IsNullOrWhiteSpace(net.NetworkId))
        {
            Debug.LogWarning(
                "[NetworkedSelectionSync] Target has no NetworkId: "
                + target.name
            );
            return;
        }

        var msg = new SelectionSyncMessage
        {
            TargetNetworkId = net.NetworkId,
            ClearSelection = false
        };

        VRTOrchestratorSingleton.Comm.SendTypeEventToAll(msg);

        if (debug)
        {
            Debug.Log(
                "[NetworkedSelectionSync] Sent selection: "
                + net.NetworkId
            );
        }
    }

    public void SendClearSelection()
    {
        if (!VRTOrchestratorSingleton.Comm.UserIsMaster)
            return;

        var msg = new SelectionSyncMessage
        {
            TargetNetworkId = "",
            ClearSelection = true
        };

        VRTOrchestratorSingleton.Comm.SendTypeEventToAll(msg);
    }

    void OnSelectionReceived(SelectionSyncMessage msg)
    {
        if (msg == null)
            return;

        // Master already has its own local selection.
        if (VRTOrchestratorSingleton.Comm.UserIsMaster)
            return;

        if (gazeInteractor == null)
            gazeInteractor = FindFirstObjectByType<GazeTargetInteractor>();

        if (gazeInteractor == null)
            return;

        if (msg.ClearSelection)
        {
            gazeInteractor.ClearLock();
            return;
        }

        GameObject target = FindByNetworkId(msg.TargetNetworkId);

        if (target == null)
        {
            Debug.LogWarning(
                "[NetworkedSelectionSync] Selected target not found: "
                + msg.TargetNetworkId
            );
            return;
        }

        gazeInteractor.LockSpecific(target, fromVoice: true);

        if (debug)
        {
            Debug.Log(
                "[NetworkedSelectionSync] Applied remote selection: "
                + target.name
            );
        }
    }

    GameObject FindByNetworkId(string id)
    {
        var all = FindObjectsByType<NetworkIdBehaviour>(
            FindObjectsSortMode.None
        );

        foreach (var n in all)
        {
            if (n != null && n.NetworkId == id)
                return n.gameObject;
        }

        return null;
    }
}