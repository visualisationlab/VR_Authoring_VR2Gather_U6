using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using VRT.Core;
using VRT.Orchestrator;
using VRT.OrchestratorComm;

namespace VRT.Pilots.Common
{
    /// <summary>
    /// Multiplayer sync for AI-generated assets:
    /// - texture applied to an existing object/wall
    /// - poster created at a wall position
    /// - GLB model generated and spawned in the scene
    ///
    /// Attach this once to a scene manager object, for example AI_Agent.
    /// Keep NetworkedAIObjectSync on individual objects for transform/color/scale sync.
    /// </summary>
    public class NetworkedAIAssetSync : NetworkIdBehaviour
    {
        [System.Serializable]
        public class AITextureSyncMessage : BaseMessage
        {
            public string TargetNetworkId;
            public string TextureUrl;
            public float TileScale;
        }

        [System.Serializable]
        public class AIPosterCreateMessage : BaseMessage
        {
            public string PosterId;
            public string NetworkId;
            public string ImageUrl;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
            public float WidthMeters;
            public float HeightMeters;
        }

        [System.Serializable]
        public class AIModelCreateMessage : BaseMessage
        {
            public string ModelId;
            public string NetworkId;
            public string ModelName;
            public string Prompt;
            public string DownloadUrl;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
        }

        [Header("References")]
        public WallTextureApplier wallTextureApplier;
        public PosterSpawner posterSpawner;
        public SceneStateStore sceneStateStore;
        public RuntimeModelSpawner runtimeModelSpawner;

        [Header("Debug")]
        public bool debug = true;

        protected override void Awake()
        {
            base.Awake();
            ResolveReferences();

            VRTOrchestratorSingleton.Comm.RegisterEventType(AIMessageTypeID.TID_AITextureSyncMessage, typeof(AITextureSyncMessage));
            VRTOrchestratorSingleton.Comm.RegisterEventType(AIMessageTypeID.TID_AIPosterCreateMessage, typeof(AIPosterCreateMessage));
            VRTOrchestratorSingleton.Comm.RegisterEventType(AIMessageTypeID.TID_AIModelCreateMessage, typeof(AIModelCreateMessage));
        }

        void OnEnable()
        {
            VRTOrchestratorSingleton.Comm.Subscribe<AITextureSyncMessage>(OnTextureSync);
            VRTOrchestratorSingleton.Comm.Subscribe<AIPosterCreateMessage>(OnPosterCreate);
            VRTOrchestratorSingleton.Comm.Subscribe<AIModelCreateMessage>(OnModelCreate);
        }

        void OnDisable()
        {
            VRTOrchestratorSingleton.Comm?.Unsubscribe<AITextureSyncMessage>(OnTextureSync);
            VRTOrchestratorSingleton.Comm?.Unsubscribe<AIPosterCreateMessage>(OnPosterCreate);
            VRTOrchestratorSingleton.Comm?.Unsubscribe<AIModelCreateMessage>(OnModelCreate);
        }

        void ResolveReferences()
        {
            if (wallTextureApplier == null) wallTextureApplier = FindFirstObjectByType<WallTextureApplier>();
            if (posterSpawner == null) posterSpawner = FindFirstObjectByType<PosterSpawner>();
            if (sceneStateStore == null) sceneStateStore = FindFirstObjectByType<SceneStateStore>();
            if (runtimeModelSpawner == null) runtimeModelSpawner = FindFirstObjectByType<RuntimeModelSpawner>();
        }

        // --------------------------------------------------------------------
        // SEND METHODS
        // --------------------------------------------------------------------

        public void SendTextureApplied(string targetNetworkId, string textureUrl, float tileScale)
        {
            if (string.IsNullOrEmpty(targetNetworkId) || string.IsNullOrEmpty(textureUrl)) return;

            var msg = new AITextureSyncMessage
            {
                TargetNetworkId = targetNetworkId,
                TextureUrl = textureUrl,
                TileScale = tileScale
            };

            Send(msg);
            if (debug) Debug.Log($"[NetworkedAIAssetSync] Sent texture sync: target={targetNetworkId}, url={textureUrl}");
        }

        public void SendPosterCreated(PersistablePoster poster)
        {
            if (poster == null || string.IsNullOrEmpty(poster.imageUrl)) return;

            poster.SyncSizeFromTransform();

            // Posters are runtime objects, so make sure they also have a stable network id.
            var net = poster.GetComponent<NetworkedAIObjectSync>();
            if (net == null)
            {
                net = poster.gameObject.AddComponent<NetworkedAIObjectSync>();
                net.ai = poster.GetComponent<AIControllable>();
                net.targetRenderer = poster.GetComponent<Renderer>();
                net.sceneStateStore = sceneStateStore;
                // Poster localScale is parent-dependent — do not sync it.
                net.syncScale = false;
            }
            if (string.IsNullOrEmpty(net.NetworkId))
                net.NetworkId = string.IsNullOrEmpty(poster.id) ? System.Guid.NewGuid().ToString() : poster.id;

            var msg = new AIPosterCreateMessage
            {
                PosterId = poster.id,
                NetworkId = net.NetworkId,
                ImageUrl = poster.imageUrl,
                Position = poster.transform.position,
                Rotation = poster.transform.rotation,
                Scale = poster.transform.localScale,
                WidthMeters = poster.widthMeters,
                HeightMeters = poster.heightMeters
            };

            Send(msg);
            if (debug) Debug.Log($"[NetworkedAIAssetSync] Sent poster create: id={poster.id}, networkId={net.NetworkId}, url={poster.imageUrl}");
        }

        public void SendModelCreated(string modelId, string modelName, string prompt, string downloadUrl, Transform modelTransform)
        {
            if (modelTransform == null || string.IsNullOrEmpty(downloadUrl)) return;

            var msg = new AIModelCreateMessage
            {
                ModelId = string.IsNullOrEmpty(modelId) ? modelName : modelId,
                NetworkId = string.IsNullOrEmpty(modelId) ? modelName : modelId,
                ModelName = modelName,
                Prompt = prompt,
                DownloadUrl = downloadUrl,
                Position = modelTransform.position,
                Rotation = modelTransform.rotation,
                Scale = modelTransform.localScale
            };

            Send(msg);
            if (debug) Debug.Log($"[NetworkedAIAssetSync] Sent model create: id={msg.ModelId}, url={downloadUrl}");
        }

        void Send(AITextureSyncMessage msg)
        {
            if (!VRTOrchestratorSingleton.Comm.UserIsMaster)
                VRTOrchestratorSingleton.Comm.SendTypeEventToMaster(msg);
            else
                VRTOrchestratorSingleton.Comm.SendTypeEventToAll(msg);
        }

        void Send(AIPosterCreateMessage msg)
        {
            if (!VRTOrchestratorSingleton.Comm.UserIsMaster)
                VRTOrchestratorSingleton.Comm.SendTypeEventToMaster(msg);
            else
                VRTOrchestratorSingleton.Comm.SendTypeEventToAll(msg);
        }

        void Send(AIModelCreateMessage msg)
        {
            if (!VRTOrchestratorSingleton.Comm.UserIsMaster)
                VRTOrchestratorSingleton.Comm.SendTypeEventToMaster(msg);
            else
                VRTOrchestratorSingleton.Comm.SendTypeEventToAll(msg);
        }

        // --------------------------------------------------------------------
        // RECEIVE METHODS
        // --------------------------------------------------------------------

        void OnTextureSync(AITextureSyncMessage msg)
        {
            if (msg.SenderId == VRTOrchestratorSingleton.Comm.SelfUser.userId) return;

            if (VRTOrchestratorSingleton.Comm.UserIsMaster)
                VRTOrchestratorSingleton.Comm.SendTypeEventToAll(msg, true);

            ResolveReferences();

            var target = FindByNetworkId(msg.TargetNetworkId);
            if (target == null)
            {
                Debug.LogWarning($"[NetworkedAIAssetSync] Texture target not found: {msg.TargetNetworkId}");
                return;
            }

            var persist = target.GetComponent<PersistableAIObject>();
            if (persist == null) persist = target.GetComponentInParent<PersistableAIObject>();
            if (persist == null)
            {
                Debug.LogWarning($"[NetworkedAIAssetSync] Texture target has no PersistableAIObject: {target.name}");
                return;
            }

            persist.textureUrl = msg.TextureUrl;
            persist.tileScale = msg.TileScale > 0f ? msg.TileScale : persist.tileScale;

            if (wallTextureApplier != null)
                wallTextureApplier.RestoreTexture(persist);

            if (sceneStateStore != null)
                sceneStateStore.RequestSave();

            if (debug) Debug.Log($"[NetworkedAIAssetSync] Applied remote texture to {target.name}");
        }

        void OnPosterCreate(AIPosterCreateMessage msg)
        {
            if (msg.SenderId == VRTOrchestratorSingleton.Comm.SelfUser.userId) return;

            if (VRTOrchestratorSingleton.Comm.UserIsMaster)
                VRTOrchestratorSingleton.Comm.SendTypeEventToAll(msg, true);

            if (FindPosterById(msg.PosterId) != null) return;

            ResolveReferences();
            StartCoroutine(CreateRemotePoster(msg));
        }

        IEnumerator CreateRemotePoster(AIPosterCreateMessage msg)
        {
            var poster = GameObject.CreatePrimitive(PrimitiveType.Quad);
            poster.name = "Poster_Remote";
            poster.transform.position = msg.Position;
            poster.transform.rotation = msg.Rotation;
            if (msg.Scale != Vector3.zero)
                poster.transform.localScale = msg.Scale;

            var persist = poster.AddComponent<PersistablePoster>();
            persist.id = msg.PosterId;
            persist.imageUrl = msg.ImageUrl;
            persist.widthMeters = Mathf.Max(0.01f, msg.WidthMeters);
            persist.heightMeters = Mathf.Max(0.01f, msg.HeightMeters);

            if (posterSpawner != null)
                posterSpawner.ApplyWorldSizeToPoster(poster.transform, persist.widthMeters, persist.heightMeters);
            else
                poster.transform.localScale = new Vector3(persist.widthMeters, persist.heightMeters, 1f);

            using (var req = UnityWebRequestTexture.GetTexture(msg.ImageUrl))
            {
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogError("[NetworkedAIAssetSync] Remote poster download failed: " + req.error);
                    Destroy(poster);
                    yield break;
                }

                var tex = DownloadHandlerTexture.GetContent(req);
                tex.wrapMode = TextureWrapMode.Clamp;

                var r = poster.GetComponent<Renderer>();
                Material mat = (posterSpawner != null && posterSpawner.posterMaterialTemplate != null)
                    ? new Material(posterSpawner.posterMaterialTemplate)
                    : new Material(Shader.Find("Unlit/Texture"));
                mat.mainTexture = tex;
                r.material = mat;

                string dir = Path.Combine(Application.persistentDataPath, "posters");
                Directory.CreateDirectory(dir);
                string localPath = Path.Combine(dir, $"{persist.id}.png");
                File.WriteAllBytes(localPath, tex.EncodeToPNG());
                persist.localPngPath = localPath;
            }

            var ai = poster.AddComponent<AIControllable>();
            ai.targetRenderer = poster.GetComponent<Renderer>();
            ai.rb = null;
            ai.useRigidbodyWhenAvailable = false;

            var sync = poster.AddComponent<NetworkedAIObjectSync>();
            sync.NetworkId = !string.IsNullOrEmpty(msg.NetworkId) ? msg.NetworkId : persist.id;
            sync.ai = ai;
            sync.targetRenderer = ai.targetRenderer;
            sync.sceneStateStore = sceneStateStore;
            // See PosterSpawner: poster localScale is parent-dependent and not portable
            // across machines. Width/height are conveyed via AIPosterCreateMessage instead.
            sync.syncScale = false;

            if (sceneStateStore != null)
                sceneStateStore.RequestSave();

            if (debug) Debug.Log($"[NetworkedAIAssetSync] Created remote poster: id={persist.id}");
        }

        void OnModelCreate(AIModelCreateMessage msg)
        {
            if (msg.SenderId == VRTOrchestratorSingleton.Comm.SelfUser.userId) return;

            if (VRTOrchestratorSingleton.Comm.UserIsMaster)
                VRTOrchestratorSingleton.Comm.SendTypeEventToAll(msg, true);

            string networkId = !string.IsNullOrEmpty(msg.NetworkId) ? msg.NetworkId : msg.ModelId;
            if (FindByNetworkId(networkId) != null) return;

            ResolveReferences();
            if (runtimeModelSpawner == null)
            {
                Debug.LogWarning("[NetworkedAIAssetSync] RuntimeModelSpawner missing; cannot spawn remote model.");
                return;
            }

            runtimeModelSpawner.SpawnRemoteModelFromUrl(
                networkId,
                msg.ModelName,
                msg.Prompt,
                msg.DownloadUrl,
                msg.Position,
                msg.Rotation,
                msg.Scale
            );

            if (debug) Debug.Log($"[NetworkedAIAssetSync] Requested remote model spawn: id={msg.ModelId}");
        }

        GameObject FindByNetworkId(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            var all = FindObjectsByType<NetworkIdBehaviour>(FindObjectsSortMode.None);
            foreach (var n in all)
            {
                if (n != null && n.NetworkId == id)
                    return n.gameObject;
            }
            return null;
        }

        PersistablePoster FindPosterById(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            var all = FindObjectsByType<PersistablePoster>(FindObjectsSortMode.None);
            foreach (var p in all)
            {
                if (p != null && p.id == id)
                    return p;
            }
            return null;
        }
    }
}
