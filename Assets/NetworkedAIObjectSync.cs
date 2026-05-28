using UnityEngine;
using VRT.Core;
using VRT.Orchestrator;
using VRT.OrchestratorComm;

namespace VRT.Pilots.Common
{
    public class NetworkedAIObjectSync : NetworkIdBehaviour
    {
        public class AIObjectStateMessage : BaseMessage
        {
            public string NetworkId;

            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;

            public bool HasColor;
            public Color Color;
        }

        [Header("References")]
        public AIControllable ai;
        public Renderer targetRenderer;
        public SceneStateStore sceneStateStore;

        [Header("Sync")]
        public float updateFrequency = 20f;
        // When false, this component will NOT send or apply localScale changes over the network.
        // Use for objects (like posters) whose localScale is parent-dependent and therefore
        // not meaningful across machines that may have different parent hierarchies/scales.
        public bool syncScale = true;
        public bool debug = false;

        float _lastSendTime;
        bool _applyingRemote;

        Vector3 _lastPosition;
        Quaternion _lastRotation;
        Vector3 _lastScale;
        Color _lastColor;
        bool _hasLastColor;

        protected override void Awake()
        {
            base.Awake();

            if (ai == null) ai = GetComponent<AIControllable>();
            if (targetRenderer == null)
            {
                if (ai != null && ai.targetRenderer != null)
                    targetRenderer = ai.targetRenderer;
                else
                    targetRenderer = GetComponentInChildren<Renderer>(true);
            }

            if (sceneStateStore == null)
                sceneStateStore = FindFirstObjectByType<SceneStateStore>();

            VRTOrchestratorSingleton.Comm.RegisterEventType(
                AIMessageTypeID.TID_AIObjectStateMessage,
                typeof(AIObjectStateMessage)
            );

            CaptureCurrentAsLast();
        }

        void OnEnable()
        {
            VRTOrchestratorSingleton.Comm.Subscribe<AIObjectStateMessage>(OnNetworkStateReceived);
        }

        void OnDisable()
        {
            VRTOrchestratorSingleton.Comm?.Unsubscribe<AIObjectStateMessage>(OnNetworkStateReceived);
        }

        void Update()
        {
            if (_applyingRemote) return;
            if (PilotController.Instance == null || PilotController.Instance.IsLeavingSession) return;

            if (Time.realtimeSinceStartup < _lastSendTime + (1f / updateFrequency))
                return;

            if (!HasLocalStateChanged())
                return;

            _lastSendTime = Time.realtimeSinceStartup;
            SendState();
            CaptureCurrentAsLast();

            if (sceneStateStore != null)
                sceneStateStore.RequestSave();
        }

        public void MarkDirtyAndSendNow()
        {
            if (_applyingRemote) return;

            SendState();
            CaptureCurrentAsLast();

            if (sceneStateStore != null)
                sceneStateStore.RequestSave();
        }

        public void SendState()
        {
            bool hasColor = TryGetColor(out Color c);

            var msg = new AIObjectStateMessage
            {
                NetworkId = NetworkId,
                Position = transform.position,
                Rotation = transform.rotation,
                Scale = transform.localScale,
                HasColor = hasColor,
                Color = c
            };

            if (debug)
                Debug.Log($"[NetworkedAIObjectSync] Send {name}, id={NetworkId}");

            if (!VRTOrchestratorSingleton.Comm.UserIsMaster)
                VRTOrchestratorSingleton.Comm.SendTypeEventToMaster(msg);
            else
                VRTOrchestratorSingleton.Comm.SendTypeEventToAll(msg);
        }

        void OnNetworkStateReceived(AIObjectStateMessage msg)
        {
            if (msg.NetworkId != NetworkId)
                return;

            if (msg.SenderId == VRTOrchestratorSingleton.Comm.SelfUser.userId)
                return;

            if (VRTOrchestratorSingleton.Comm.UserIsMaster)
                VRTOrchestratorSingleton.Comm.SendTypeEventToAll(msg, true);

            _applyingRemote = true;

            transform.position = msg.Position;
            transform.rotation = msg.Rotation;
            if (syncScale)
                transform.localScale = msg.Scale;

            if (msg.HasColor)
                ApplyColor(msg.Color);

            Physics.SyncTransforms();

            CaptureCurrentAsLast();

            if (sceneStateStore != null)
                sceneStateStore.RequestSave();

            _applyingRemote = false;

            if (debug)
                Debug.Log($"[NetworkedAIObjectSync] Applied remote state to {name}, id={NetworkId}");
        }

        bool HasLocalStateChanged()
        {
            if (Vector3.Distance(transform.position, _lastPosition) > 0.001f)
                return true;

            if (Quaternion.Angle(transform.rotation, _lastRotation) > 0.1f)
                return true;

            if (syncScale && Vector3.Distance(transform.localScale, _lastScale) > 0.001f)
                return true;

            bool hasColor = TryGetColor(out Color c);
            if (hasColor != _hasLastColor)
                return true;

            if (hasColor && c != _lastColor)
                return true;

            return false;
        }

        void CaptureCurrentAsLast()
        {
            _lastPosition = transform.position;
            _lastRotation = transform.rotation;
            _lastScale = transform.localScale;

            _hasLastColor = TryGetColor(out _lastColor);
        }

        bool TryGetColor(out Color c)
        {
            c = Color.white;

            if (ai != null && ai.TryGetColor(out c))
                return true;

            if (targetRenderer != null && targetRenderer.material != null)
            {
                var mat = targetRenderer.material;

                if (mat.HasProperty("_BaseColor"))
                {
                    c = mat.GetColor("_BaseColor");
                    return true;
                }

                if (mat.HasProperty("_Color"))
                {
                    c = mat.GetColor("_Color");
                    return true;
                }
            }

            return false;
        }

        void ApplyColor(Color c)
        {
            if (ai != null && ai.TrySetColor(c))
                return;

            if (targetRenderer == null) return;

            var mats = targetRenderer.materials;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i].HasProperty("_BaseColor"))
                    mats[i].SetColor("_BaseColor", c);
                else if (mats[i].HasProperty("_Color"))
                    mats[i].SetColor("_Color", c);
            }

            targetRenderer.materials = mats;
        }
    }
}