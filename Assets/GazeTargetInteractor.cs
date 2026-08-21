using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using UnityEngine.XR;
using UnityEngine.InputSystem;
using TMPro;
using VRT.Orchestrator;

/// <summary>
/// Selection + action hub. Targeting can come from gaze (head cone) OR a controller ray
/// (recommended — far more reliable for kids/elderly than head-aim).
///
/// Flow: aim the controller ray, press the select button ONCE to LOCK the object. The indicator
/// sphere then pops onto the locked object and stays there even when you lower the controller,
/// so you can give a voice command without holding aim.
///
/// Highlight: an EDGE OUTLINE (like the Unity editor's orange selection outline). The object's
/// own materials are never modified — instead an inverted-hull mesh copy is created at runtime,
/// so only a thin coloured shell around the silhouette is visible.
///
/// Selection panel: assign a TextMeshProUGUI to 'selectionPanelText' and the selected object's
/// name + its parent GameObject's name are shown there. Other systems can read
/// SelectedObjectName / SelectedParentName or subscribe to OnSelectionChanged.
/// </summary>

public class GazeTargetInteractor : MonoBehaviour
{
    public enum TargetingMode { Gaze, ControllerRay }

    [Header("Targeting Mode")]
    public TargetingMode mode = TargetingMode.ControllerRay;

    [Header("Head (HMD) — used only in Gaze mode")]
    public Transform head;

    [Header("Controller — used only in ControllerRay mode")]
    [Tooltip("Assign your left controller's Transform (the one with the OpenXR pose).")]
    public Transform controller;
    [Tooltip("Optional: assign a LineRenderer for the pointer. If left empty, one is created automatically.")]
    public LineRenderer laser;
    public float laserMaxLength = 500f;

    [Header("Runtime Controller Auto-Find")]
    [Tooltip("If the Controller field is empty, search the scene at runtime for a matching Transform.")]
    public bool autoFindRuntimeController = true;
    [Tooltip("Substring to match the controller Transform's name (case-insensitive).")]
    public string controllerNameContains = "Left Controller";
    [Tooltip("Optional: prefer a match whose parent hierarchy contains this name (e.g. your runtime player rig).")]
    public string preferParentNameContains = "";
    [Tooltip("How often (seconds) to retry the search until a controller is found.")]
    public float retryFindControllerEvery = 0.5f;

    [Header("Select Input")]
    [Tooltip("OPTIONAL override. Leave empty and the script auto-binds the button below — no wiring needed.")]
    public InputActionReference selectActionReference;
    [Tooltip("Which left-hand button auto-binds as 'select' when no reference is assigned.")]
    public AutoSelectButton autoSelectButton = AutoSelectButton.Trigger;

    public enum AutoSelectButton { Trigger, X_PrimaryButton, Y_SecondaryButton }

    [Header("Selection cone (Gaze mode only)")]
    public float maxAngleDeg = 12f;
    public float maxDistance = 10f;
    public float distanceBias = 0.02f;

    [Header("Selection ray (ControllerRay mode)")]
    public float rayMaxDistance = 500f;
    [Tooltip("Layers the selection ray can hit. Put your interactable objects' colliders here.")]
    public LayerMask selectableLayerMask = ~0;

    [Header("Confirm / Select Button (ControllerRay mode)")]
    [Tooltip("If true, hovering with the ray alone updates 'current'. " +
             "Pressing the select button LOCKS it — the recommended accessible flow.")]
    public bool requireButtonToLock = true;

    [Header("Selection Highlight (bounding box)")]
    public bool highlightSelection = true;
    public enum HighlightStyle { FullBox, CornerBrackets, NearestFace }
    [Tooltip("FullBox = complete cage. CornerBrackets = L-marks at corners. " +
             "NearestFace = only the single box face pointing toward you (a flat rectangle).")]
    public HighlightStyle highlightStyle = HighlightStyle.NearestFace;
    [Tooltip("Colour of the selection box / brackets.")]
    public Color outlineColor = new Color(1f, 0.45f, 0.05f);
    [Tooltip("Line thickness of the box, in metres.")]
    public float boxLineWidthMeters = 0.04f;
    [Tooltip("How far the box sits outside the object's bounds, in metres (your '5 cm out of the surface').")]
    public float boxPaddingMeters = 0.05f;
    [Tooltip("Corner bracket length as a fraction of each box edge (CornerBrackets style only). 0.15 = 15%.")]
    [Range(0.05f, 0.5f)] public float bracketFraction = 0.15f;
    [Tooltip("If true, hovering with the ray previews the box. If false, only the LOCKED object gets one.")]
    public bool outlineOnHover = true;

    [Header("Selection Panel (UI)")]
    [Tooltip("TextMeshPro text on your panel / wrist menu. Shows the selected object + its parent.")]
    public TextMeshProUGUI selectionPanelText;
    [Tooltip("Text shown when nothing is locked.")]
    public string nothingSelectedText = "Nothing selected";

    [Header("Indicator sphere ('balloon')")]
    public bool showIndicator = false;
    [Tooltip("If true, the sphere appears ONLY on the LOCKED object (after a select press). " +
             "If false, it also previews on whatever the ray is currently hovering.")]
    public bool indicatorOnlyWhenLocked = true;
    [Tooltip("Sphere volume in cubic metres. 1.5 m³ ≈ 1.42 m across. Diameter is computed for you.")]
    public float indicatorVolumeM3 = 1.5f;
    public Color indicatorColor = new Color(1f, 0.25f, 0.2f);
    [Tooltip("How far above the object's top the sphere floats, in metres.")]
    public float indicatorHoverHeight = 0.05f;

    [Header("Debug Logging")]
    public bool logCurrentChanges = true;
    public bool logStackActions = true;

    [Header("Gaze/Ray Raycast (for wall / surface placement)")]
    public LayerMask gazeLayerMask = ~0;
    public bool debugDrawRay = false;

    AIControllable _current;
    AIControllable _locked;
    NetworkedSelectionSync _selectionSync;
    bool _lockIsManual = false;

    bool _pendingStack = false;
    float _pendingGap = 0.01f;

    private float _nextControllerSearchTime = 0f;

    // ---- Public selection info (read by UI / voice pipeline / networking) ----
    /// <summary>The currently LOCKED object, or null.</summary>
    public AIControllable LockedTarget => _locked;
    /// <summary>Name of the locked object, or null when nothing is locked.</summary>
    public string SelectedObjectName => _locked != null ? _locked.name : null;
    /// <summary>Name of the locked object's parent GameObject, or null.</summary>
    public string SelectedParentName =>
        (_locked != null && _locked.transform.parent != null) ? _locked.transform.parent.name : null;
    /// <summary>Fired whenever the locked selection changes: (objectName, parentName). Both null on deselect.</summary>
    public event System.Action<string, string> OnSelectionChanged;

    // ---- Highlight bookkeeping ----
    struct BoxEdge { public LineRenderer lr; public Vector3 aLocal; public Vector3 bLocal; }
    class OutlineSet
    {
        public AIControllable owner;    // object this box belongs to (for per-frame world refresh)
        public GameObject boxRoot;      // parent holding the edge LineRenderers
        public Material material;       // shared box material
        public List<BoxEdge> edges = new List<BoxEdge>();   // static modes (FullBox / CornerBrackets)

        // NearestFace mode:
        public bool dynamicFace;        // if true, redraw only the camera-facing face each frame
        public Vector3[] corners;       // 8 local-space corners
        public Vector3 localCenter;     // local-space box centre
        public LineRenderer[] faceLines; // exactly 4 lines, repositioned to the nearest face
    }
    readonly Dictionary<AIControllable, OutlineSet> _outlines = new Dictionary<AIControllable, OutlineSet>();
    Shader _boxShader;
    Camera _viewCam;

    // Corner index bits: bit0=x, bit1=y, bit2=z.
    // 0:(-,-,-) 1:(+,-,-) 2:(-,+,-) 3:(+,+,-) 4:(-,-,+) 5:(+,-,+) 6:(-,+,+) 7:(+,+,+)
    // Each face lists its 4 corners in loop order; edges connect consecutive corners (and last->first).
    static readonly int[][] kFaceCorners =
    {
        new[]{1,3,7,5},  // +X
        new[]{0,4,6,2},  // -X
        new[]{2,3,7,6},  // +Y
        new[]{0,1,5,4},  // -Y
        new[]{4,5,7,6},  // +Z
        new[]{0,1,3,2},  // -Z
    };
    static readonly Vector3[] kFaceNormals =
    {
        Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back
    };

    GameObject _indicator;

    RaycastHit _lastHit;
    bool _hasHit;

    // Full hit info captured at the moment of locking. Used for the indicator sphere
    // ("in front of the object") AND exposed to the voice pipeline for wall anchoring.
    RaycastHit _lockedHit;
    bool _hasLockedHit;

    // Legacy fallback (often returns nothing in Unity 6 + XRI)
    private UnityEngine.XR.InputDevice leftControllerDevice;
    private bool lastTriggerPressed = false;

    // Auto-bound select action — created at runtime, no Inspector wiring needed.
    private InputAction _selectAction;

    // reusable buffer so RaycastNonAlloc doesn't allocate every frame
    readonly RaycastHit[] _hitBuffer = new RaycastHit[16];

    // Sphere primitive is 1 m diameter at scale 1, so localScale == diameter in metres.
    // V = (pi/6) * d^3  ->  d = cbrt(6V/pi)
    float IndicatorDiameter() => Mathf.Pow(Mathf.Max(0.0001f, indicatorVolumeM3) * 6f / Mathf.PI, 1f / 3f);

    void OnEnable()
    {
        // 1) explicit reference wins if assigned
        if (selectActionReference != null && selectActionReference.action != null)
        {
            selectActionReference.action.Enable();
        }
        else
        {
            // 2) auto-bind the chosen left-hand button — zero Inspector setup
            BuildAutoSelectAction();
        }

        if (mode == TargetingMode.Gaze)
            StartCoroutine(BindHeadWhenReady());

        if (mode == TargetingMode.ControllerRay && laser == null && controller != null)
            EnsureLaser();

        if (showIndicator && _indicator == null)
        {
            _indicator = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _indicator.name = "SelectionIndicator";
            Destroy(_indicator.GetComponent<Collider>());
            _indicator.transform.localScale = Vector3.one * IndicatorDiameter();
            var rend = _indicator.GetComponent<Renderer>();
            rend.material = new Material(Shader.Find("Standard"));
            rend.material.color = indicatorColor;
            rend.material.EnableKeyword("_EMISSION");
            rend.material.SetColor("_EmissionColor", indicatorColor * 1.5f);
            _indicator.SetActive(false);
        }

        if (_selectionSync == null)
            _selectionSync = FindFirstObjectByType<NetworkedSelectionSync>();

        UpdateSelectionPanel();
    }

    void OnDisable()
    {
        if (selectActionReference != null && selectActionReference.action != null)
            selectActionReference.action.Disable();

        _selectAction?.Disable();
        _selectAction?.Dispose();
        _selectAction = null;
    }

    // Creates an InputAction bound directly to the left controller, so no asset/reference is needed.
    void BuildAutoSelectAction()
    {
        _selectAction = new InputAction("AutoSelect", InputActionType.Button);

        switch (autoSelectButton)
        {
            case AutoSelectButton.Y_SecondaryButton:
                _selectAction.AddBinding("<XRController>{LeftHand}/secondaryButton"); // Y
                break;
            case AutoSelectButton.Trigger:
                _selectAction.AddBinding("<XRController>{LeftHand}/triggerPressed");
                _selectAction.AddBinding("<XRController>{LeftHand}/trigger");
                break;
            case AutoSelectButton.X_PrimaryButton:
            default:
                _selectAction.AddBinding("<XRController>{LeftHand}/primaryButton"); // X
                break;
        }

        _selectAction.Enable();
    }

    // Builds a visible pointer line on the controller so users can see where they're aiming.
    void EnsureLaser()
    {
        var go = new GameObject("AutoLaser");
        go.transform.SetParent(controller, false);

        laser = go.AddComponent<LineRenderer>();
        laser.useWorldSpace = true;
        laser.positionCount = 2;
        laser.startWidth = 0.008f;
        laser.endWidth = 0.008f;
        laser.numCapVertices = 4;

        var mat = new Material(Shader.Find("Sprites/Default"));
        mat.color = outlineColor;
        laser.material = mat;
        laser.startColor = outlineColor;
        laser.endColor = outlineColor;

        laser.material.renderQueue = 4000;
    }

    void TryFindRuntimeController()
    {
        if (!autoFindRuntimeController) return;
        if (mode != TargetingMode.ControllerRay) return;
        if (controller != null) return;
        if (Time.time < _nextControllerSearchTime) return;

        _nextControllerSearchTime = Time.time + Mathf.Max(0.1f, retryFindControllerEvery);

        Transform best = null;
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsSortMode.None))
        {
            if (t == null || !t.gameObject.activeInHierarchy) continue;
            if (string.IsNullOrWhiteSpace(t.name)) continue;
            if (!t.name.ToLowerInvariant().Contains(controllerNameContains.ToLowerInvariant())) continue;

            if (best == null)
                best = t;

            if (!string.IsNullOrWhiteSpace(preferParentNameContains) && HasParentNameContaining(t, preferParentNameContains))
            {
                best = t;
                break;
            }
        }

        if (best != null)
        {
            controller = best;
            Debug.Log("[GazeTargetInteractor] Runtime controller found: " + GetFullPath(controller));

            if (laser == null)
                EnsureLaser();
        }
        else
        {
            Debug.Log("[GazeTargetInteractor] Waiting for runtime controller containing name: " + controllerNameContains);
        }
    }

    static bool HasParentNameContaining(Transform t, string text)
    {
        if (t == null || string.IsNullOrWhiteSpace(text)) return false;
        string needle = text.ToLowerInvariant();
        Transform p = t;
        while (p != null)
        {
            if (p.name.ToLowerInvariant().Contains(needle)) return true;
            p = p.parent;
        }
        return false;
    }

    static string GetFullPath(Transform t)
    {
        if (t == null) return "null";
        string path = t.name;
        Transform p = t.parent;
        while (p != null)
        {
            path = p.name + "/" + path;
            p = p.parent;
        }
        return path;
    }

    IEnumerator BindHeadWhenReady()
    {
        float elapsed = 0f;
        while (head == null && elapsed < 5f)
        {
            var cam = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
            if (cam != null) head = cam.transform;
            if (head != null) yield break;
            elapsed += 0.1f;
            yield return new WaitForSeconds(0.1f);
        }
        if (head == null)
            Debug.LogWarning("[GazeTargetInteractor] No head/camera found. Assign 'head' in Inspector.");
    }

    void Update()
    {
        if (mode == TargetingMode.ControllerRay)
            TryFindRuntimeController();

        Vector3 origin, dir;

        if (mode == TargetingMode.ControllerRay)
        {
            if (controller == null) return;
            origin = controller.position;
            dir = controller.forward;
        }
        else
        {
            if (head == null) return;
            origin = head.position;
            dir = head.forward;
        }

        // physics ray for surface placement (posters/decals)
        float surfaceRayDistance = (mode == TargetingMode.ControllerRay) ? rayMaxDistance : maxDistance;
        _hasHit = Physics.Raycast(origin, dir, out _lastHit, surfaceRayDistance, gazeLayerMask, QueryTriggerInteraction.Ignore);

        if (debugDrawRay)
        {
            Debug.DrawRay(origin, dir * surfaceRayDistance, _hasHit ? Color.green : Color.yellow);
            if (_hasHit) Debug.DrawRay(_lastHit.point, _lastHit.normal * 0.25f, Color.cyan);
        }

        if (laser != null)
        {
            laser.positionCount = 2;
            laser.SetPosition(0, origin);
            float len = _hasHit ? _lastHit.distance : laserMaxLength;
            laser.SetPosition(1, origin + dir * len);
        }

        AIControllable next = (mode == TargetingMode.ControllerRay)
            ? FindByControllerRay(origin, dir)
            : FindBestByAngle(origin, dir);

        if (next != _current)
        {
            if (highlightSelection && _current != null && _current != _locked) RemoveOutline(_current);
            bool isMaster =
                VRTOrchestratorSingleton.Comm != null &&
                VRTOrchestratorSingleton.Comm.UserIsMaster;

            if (highlightSelection &&
                isMaster &&
                outlineOnHover &&
                next != null &&
                next != _locked)
            {
                AddOutline(next);
            }
            _current = next;

            if (logCurrentChanges)
                Debug.Log($"[TARGET] current={NameOrNull(_current)} locked={NameOrNull(_locked)} pendingStack={_pendingStack} manualLock={_lockIsManual}");

            TryCompletePendingStack();
        }
        else
        {
            TryCompletePendingStack();
        }

        // Indicator follows the LOCKED object (stays put when you lower the controller).
        UpdateIndicator();

        // Keep the selection box hugging the object as it moves / rotates / scales.
        UpdateSelectionBoxes();

        // ---- Select / Lock input ----
        bool selectPressed =
            Input.GetKeyDown(KeyCode.Alpha1) ||
            (mode == TargetingMode.ControllerRay && requireButtonToLock && ControllerSelectButtonDown());

        if (selectPressed)
        {
            bool isMaster =
                VRTOrchestratorSingleton.Comm != null &&
                VRTOrchestratorSingleton.Comm.UserIsMaster;

            if (isMaster)
            {
                LockCurrent(fromVoice: false);
            }
            else
            {
                Debug.Log(
                    "[GazeTargetInteractor] Selection ignored: only master can select."
                );
            }
        }
    }

    // Order of preference: explicit reference -> auto-bound action -> legacy device.
    bool ControllerSelectButtonDown()
    {
        if (selectActionReference != null && selectActionReference.action != null)
            return selectActionReference.action.WasPressedThisFrame();

        if (_selectAction != null)
            return _selectAction.WasPressedThisFrame();

        // ---- legacy fallback (often returns nothing in Unity 6 + XRI) ----
        if (!leftControllerDevice.isValid)
        {
            var devices = new List<UnityEngine.XR.InputDevice>();
            UnityEngine.XR.InputDevices.GetDevicesAtXRNode(UnityEngine.XR.XRNode.LeftHand, devices);
            if (devices.Count > 0)
                leftControllerDevice = devices[0];
        }

        if (leftControllerDevice.isValid)
        {
            leftControllerDevice.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out bool triggerPressed);
            bool downThisFrame = triggerPressed && !lastTriggerPressed;
            lastTriggerPressed = triggerPressed;
            return downThisFrame;
        }

        lastTriggerPressed = false;
        return false;
    }

    // Occlusion-safe: collect all hits along the ray, sort by distance, and return the
    // FIRST one that carries an AIControllable. A wall/floor in front no longer blocks selection.
    AIControllable FindByControllerRay(Vector3 origin, Vector3 dir)
    {
        int count = Physics.RaycastNonAlloc(origin, dir, _hitBuffer, rayMaxDistance, selectableLayerMask, QueryTriggerInteraction.Ignore);
        if (count == 0) return null;

        // simple insertion sort by distance (count is tiny)
        for (int i = 1; i < count; i++)
        {
            var h = _hitBuffer[i];
            int j = i - 1;
            while (j >= 0 && _hitBuffer[j].distance > h.distance)
            {
                _hitBuffer[j + 1] = _hitBuffer[j];
                j--;
            }
            _hitBuffer[j + 1] = h;
        }

        for (int i = 0; i < count; i++)
        {
            var col = _hitBuffer[i].collider;
            if (col == null)
                continue;

            // If ray hits a poster, ALWAYS select the poster,
            // never the wall/building behind it.
            var poster = col.GetComponentInParent<PersistablePoster>();

            if (poster != null)
            {
                var posterAI = poster.GetComponent<AIControllable>();

                if (posterAI != null)
                    return posterAI;
            }

            // Normal object
            var a = col.GetComponent<AIControllable>();

            if (a == null)
                a = col.GetComponentInParent<AIControllable>();

            if (a != null)
                return a;
        }
        return null;
    }

    public bool TryGetCurrentHit(out RaycastHit hit)
    {
        hit = _lastHit;
        return _hasHit;
    }

    /// <summary>
    /// Hit info captured at the moment the current selection was LOCKED.
    /// Use this (not TryGetCurrentHit) when you need the surface of the SELECTED object,
    /// e.g. for wall anchoring in the voice pipeline. Returns false when nothing is locked
    /// or the lock was made without a ray hit (e.g. via LockSpecific from voice).
    /// </summary>
    public bool TryGetLockedHit(out RaycastHit hit)
    {
        hit = _lockedHit;
        return _locked != null && _hasLockedHit;
    }

    void TryCompletePendingStack()
    {
        if (!_pendingStack || _locked == null || _current == null || _current == _locked) return;
        if (logStackActions)
            Debug.Log($"[STACK] AUTO stack -> locked={_locked.name} base={_current.name} gap={_pendingGap:0.###}");
        StackLockedOnCurrent(_pendingGap);
        _pendingStack = false;
    }

    public void LockCurrent(bool fromVoice = true)
    {
        if (_lockIsManual && fromVoice)
        {
            if (logStackActions) Debug.Log($"[STACK] VOICE LOCK IGNORED (manual lock active) locked={NameOrNull(_locked)}");
            return;
        }

        // Deselect the previously locked object (remove its outline) if it's a different one.
        if (_locked != null && _locked != _current)
            RemoveOutline(_locked);

        _locked = _current;
        _lockIsManual = !fromVoice;

        // Remember the full ray hit, so the sphere can sit in front of the object
        // and the voice pipeline can build a WallAnchor from the locked surface.
        _hasLockedHit = _hasHit && _locked != null;
        if (_hasLockedHit) _lockedHit = _lastHit;

        if (logStackActions)
        {
            Debug.Log($"[STACK] LOCK -> locked={NameOrNull(_locked)} current={NameOrNull(_current)} fromVoice={fromVoice}");
            if (_locked != null) Debug.Log($"[STACK] lockedPos={_locked.transform.position}");
        }

        if (_locked != null)
        {
            if (highlightSelection) AddOutline(_locked);   // edge outline on the selected object
            Debug.Log($"[SELECT] {FriendlyName(_locked)} selected (parent: {SelectedParentName ?? "none"})");
        }

        if (_locked != null && VRTOrchestratorSingleton.Comm != null && VRTOrchestratorSingleton.Comm.UserIsMaster)
        {
            if (_selectionSync == null)
                _selectionSync = FindFirstObjectByType<NetworkedSelectionSync>();

            if (_selectionSync != null)
                _selectionSync.SendSelection(_locked.gameObject);
        }

        UpdateSelectionPanel();

        // Pop the sphere onto the locked object immediately.
        UpdateIndicator();
    }

    public void LockSpecific(AIControllable target, bool fromVoice = true)
    {
        if (target == null) return;
        if (_lockIsManual && fromVoice)
        {
            if (logStackActions) Debug.Log($"[STACK] VOICE LOCKSPECIFIC IGNORED (manual lock active) locked={NameOrNull(_locked)}");
            return;
        }
        if (_locked != null && _locked != target)
            RemoveOutline(_locked);

        _locked = target;
        _lockIsManual = !fromVoice;

        // No ray hit is associated with a programmatic lock — invalidate any stale hit
        // so TryGetLockedHit doesn't report a surface belonging to a previous selection.
        _hasLockedHit = false;

        if (highlightSelection) AddOutline(_locked);
        if (logStackActions) Debug.Log($"[STACK] LOCKSPECIFIC -> locked={NameOrNull(_locked)} fromVoice={fromVoice}");
        UpdateSelectionPanel();
        UpdateIndicator();
    }

    public void LockSpecific(GameObject go, bool fromVoice = true)
    {
        if (go == null) return;
        AIControllable a = go.GetComponent<AIControllable>();
        if (a == null) a = go.GetComponentInParent<AIControllable>();
        if (a == null)
        {
            if (logStackActions) Debug.LogWarning("[GazeTargetInteractor] LockSpecific(GameObject): AIControllable not found: " + go.name);
            return;
        }
        LockSpecific(a, fromVoice);
    }

    public void ClearLock()
    {
        if (_locked != null)
            RemoveOutline(_locked);

        _locked = null;
        _pendingStack = false;
        _lockIsManual = false;
        _hasLockedHit = false;
        if (logStackActions) Debug.Log($"[STACK] UNLOCK -> locked=null current={NameOrNull(_current)} pendingStack=false manualLock=false");
        UpdateSelectionPanel();
        UpdateIndicator();

        if (VRTOrchestratorSingleton.Comm != null && VRTOrchestratorSingleton.Comm.UserIsMaster)
        {
            if (_selectionSync == null)
                _selectionSync = FindFirstObjectByType<NetworkedSelectionSync>();

            if (_selectionSync != null)
                _selectionSync.SendClearSelection();
        }

    }

    public void ArmStackOnNext(float gap = 0.01f)
    {
        if (_locked == null)
        {
            Debug.LogWarning("[STACK] Cannot arm stack: no locked object. Select the TOP object first.");
            return;
        }
        _pendingStack = true;
        _pendingGap = gap;
        if (logStackActions) Debug.Log($"[STACK] ARMED -> pendingStack=true locked={NameOrNull(_locked)} gap={_pendingGap:0.###}");
    }

    public void StackLockedOnCurrent(float gap = 0.01f)
    {
        if (logStackActions) Debug.Log($"[STACK] STACK_ON called -> locked={NameOrNull(_locked)} current={NameOrNull(_current)} gap={gap:0.###}");
        if (_locked == null) { Debug.LogWarning("[STACK] No locked object."); return; }
        if (_current == null) { Debug.LogWarning("[STACK] No current target."); return; }
        if (_locked == _current) { Debug.LogWarning("[STACK] Cannot stack an object on itself."); return; }

        Bounds baseB = BoundsOf(_current);
        Vector3 pos = _locked.transform.position;
        pos.x = baseB.center.x;
        pos.z = baseB.center.z;
        pos.y = baseB.max.y + gap + BoundsOf(_locked).extents.y;
        _locked.transform.position = pos;
    }

    Bounds BoundsOf(AIControllable a)
    {
        if (a.targetRenderer) return a.targetRenderer.bounds;
        return new Bounds(a.transform.position, Vector3.one * 0.1f);
    }

    private AIControllable TargetForActions => (_locked != null) ? _locked : _current;

    // ---------------- Action APIs (unchanged) ----------------
    // Note: the outline never touches the object's materials, so recolouring/material
    // changes no longer need any restore/re-highlight dance around them.

    public void SetColorOnGazed(string colorName)
    {
        var t = TargetForActions;
        if (t == null) return;
        t.SetColor(colorName);
        if (logStackActions) Debug.Log($"[ACT] set_color -> target={NameOrNull(t)} color={colorName}");
    }

    public void SetMaterialOnGazed(string materialName)
    {
        var t = TargetForActions;
        if (t == null) return;
        t.SetMaterial(materialName);
        if (logStackActions) Debug.Log($"[ACT] set_material -> target={NameOrNull(t)} material={materialName}");
    }

    public void SetScaleUniformOnGazed(float s)
    {
        var t = TargetForActions;
        if (t == null) return;

        var poster = t.GetComponent<PersistablePoster>();

        if (poster != null)
        {
            float size = Mathf.Max(0.01f, s);

            poster.widthMeters = size;
            poster.heightMeters = size;

            ApplyPosterSizeAndSync(t, poster);
            return;
        }

        t.SetScaleUniform(s);
        SyncNormalObjectNow(t);
        RefreshOutline(t);
    }

    public void SetScaleXYZOnGazed(float x, float y, float z)
    {
        var t = TargetForActions;
        if (t == null) return;

        var poster = t.GetComponent<PersistablePoster>();

        if (poster != null)
        {
            poster.widthMeters = Mathf.Max(0.01f, x);
            poster.heightMeters = Mathf.Max(0.01f, y);

            ApplyPosterSizeAndSync(t, poster);
            return;
        }

        t.SetScaleXYZ(x, y, z);
        SyncNormalObjectNow(t);
        RefreshOutline(t);
    }

    public void ScaleGazedBy(float factor)
    {
        var t = TargetForActions;
        if (t == null) return;

        factor = Mathf.Max(0.01f, factor);

        var poster = t.GetComponent<PersistablePoster>();

        if (poster != null)
        {
            poster.widthMeters =
                Mathf.Max(0.01f, poster.widthMeters * factor);

            poster.heightMeters =
                Mathf.Max(0.01f, poster.heightMeters * factor);

            ApplyPosterSizeAndSync(t, poster);

            // VERY IMPORTANT:
            // Do not continue into normal object scaling.
            return;
        }

        t.ScaleBy(factor);
        SyncNormalObjectNow(t);
        RefreshOutline(t);
    }

    void ApplyPosterSizeAndSync(
    AIControllable target,
    PersistablePoster poster)
    {
        if (target == null || poster == null)
            return;

        var spawner = FindFirstObjectByType<PosterSpawner>();

        if (spawner != null)
        {
            spawner.ApplyWorldSizeToPoster(
                target.transform,
                poster.widthMeters,
                poster.heightMeters
            );
        }

        RefreshOutline(target);

        var store = FindFirstObjectByType<SceneStateStore>();

        if (store != null)
            store.RequestSave();

        var assetSync =
            FindFirstObjectByType<VRT.Pilots.Common.NetworkedAIAssetSync>();

        if (assetSync != null)
            assetSync.SendPosterResized(poster);
    }

    void SyncNormalObjectNow(AIControllable target)
    {
        if (target == null)
            return;

        var sync =
            target.GetComponent<VRT.Pilots.Common.NetworkedAIObjectSync>();

        if (sync == null)
            sync = target.GetComponentInParent<
                VRT.Pilots.Common.NetworkedAIObjectSync>();

        if (sync != null)
            sync.MarkDirtyAndSendNow();
    }

    public void MoveGazedToWorld(float x, float y, float z) { var t = TargetForActions; if (t != null) t.MoveWorld(x, y, z); }
    public void TranslateGazedWorld(float dx, float dy, float dz) { var t = TargetForActions; if (t != null) t.TranslateWorld(dx, dy, dz); }
    public void PlaceGazedOnFloor(float floorY = 0f) { var t = TargetForActions; if (t != null) t.PlaceOnFloor(floorY); }
    public void DropGazed() { var t = TargetForActions; if (t != null) t.Drop(); }
    public void FreezeGazed() { var t = TargetForActions; if (t != null) t.Freeze(); }

    public void ScaleAxisOnGazed(string axis, float deltaMeters)
    {
        var target = TargetForActions;
        if (target == null)
            return;

        var poster = target.GetComponent<PersistablePoster>();

        if (poster != null)
        {
            Transform pt = target.transform;

            Vector3 posterRight = pt.right;
            Vector3 posterUp = pt.up;

            int widthWorldAxis = DominantAxis(posterRight);
            int heightWorldAxis = DominantAxis(posterUp);

            int requestedAxis =
                axis == "x" ? 0 :
                axis == "y" ? 1 : 2;

            if (requestedAxis == heightWorldAxis)
            {
                poster.heightMeters =
                    Mathf.Max(
                        0.01f,
                        poster.heightMeters + deltaMeters
                    );
            }
            else
            {
                poster.widthMeters =
                    Mathf.Max(
                        0.01f,
                        poster.widthMeters + deltaMeters
                    );
            }

            // Applies locally + saves + sends multiplayer resize.
            ApplyPosterSizeAndSync(target, poster);

            return;
        }

        // ----- Normal wall / object / generated 3D model -----

        Vector3 s = target.transform.localScale;

        switch (axis)
        {
            case "x":
                s.x = Mathf.Max(0.01f, s.x + deltaMeters);
                break;

            case "y":
                s.y = Mathf.Max(0.01f, s.y + deltaMeters);
                break;

            case "z":
                s.z = Mathf.Max(0.01f, s.z + deltaMeters);
                break;
        }

        target.transform.localScale = s;

        SyncNormalObjectNow(target);
        RefreshOutline(target);
    }

    static int DominantAxis(Vector3 v)
    {
        float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z);
        if (ax >= ay && ax >= az) return 0;
        if (ay >= ax && ay >= az) return 1;
        return 2;
    }

    // ---------------- Selection panel ----------------

    void UpdateSelectionPanel()
    {
        string objName = SelectedObjectName;
        string parentName = SelectedParentName;

        if (selectionPanelText != null)
        {
            selectionPanelText.text = (objName != null)
                ? $"Selected: {objName}\nParent: {(parentName ?? "(none)")}"
                : nothingSelectedText;
        }

        OnSelectionChanged?.Invoke(objName, parentName);
    }

    // ---------------- Feedback: selection bounding box / corner brackets ----------------
    //
    // The object's own materials are NEVER modified. We compute an ORIENTED box that hugs the
    // selected object (its combined mesh bounds, expressed in the object's own space), then draw
    // that box's edges with LineRenderers using an always-on-top unlit shader. Because the box
    // GameObject is parented to the object, it follows every move / rotate / scale automatically.
    //
    // This ignores the mesh surface entirely, so window openings and other holes in boolean
    // meshes can never be traced or filled — you always get one clean highlight around the piece.
    //
    // Mesh.bounds works WITHOUT "Read/Write Enabled", so no import-setting changes are needed.

    Shader BoxShader()
    {
        if (_boxShader != null) return _boxShader;
        _boxShader = Shader.Find("Custom/SelectionBox");
        if (_boxShader == null)
            Debug.LogError("[GazeTargetInteractor] Shader 'Custom/SelectionBox' not found. " +
                           "Add SelectionBox.shader to your project — the highlight won't show without it.");
        return _boxShader;
    }

    // Compute the object's combined bounds in ITS OWN local space (an oriented box).
    bool TryGetLocalBounds(AIControllable a, out Bounds local)
    {
        local = new Bounds();
        Bounds acc = new Bounds();
        bool has = false;
        Matrix4x4 worldToA = a.transform.worldToLocalMatrix;

        void Accumulate(Transform t, Bounds meshBounds)
        {
            Matrix4x4 m = worldToA * t.localToWorldMatrix;
            Vector3 c = meshBounds.center, e = meshBounds.extents;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = c + new Vector3(
                    ((i & 1) == 0 ? -e.x : e.x),
                    ((i & 2) == 0 ? -e.y : e.y),
                    ((i & 4) == 0 ? -e.z : e.z));
                Vector3 p = m.MultiplyPoint3x4(corner);
                if (!has) { acc = new Bounds(p, Vector3.zero); has = true; }
                else acc.Encapsulate(p);
            }
        }

        foreach (var mf in a.GetComponentsInChildren<MeshFilter>())
        {
            if (mf == null || mf.sharedMesh == null || mf.name == "OutlineCopy") continue;
            Accumulate(mf.transform, mf.sharedMesh.bounds);
        }
        foreach (var smr in a.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (smr == null || smr.sharedMesh == null || smr.name == "OutlineCopy") continue;
            Accumulate(smr.transform, smr.sharedMesh.bounds);
        }

        if (has) local = acc;
        return has;
    }

    void AddOutline(AIControllable a)
    {
        if (a == null || _outlines.ContainsKey(a)) return;
        if (!TryGetLocalBounds(a, out Bounds lb)) return;

        var sh = BoxShader();
        if (sh == null) return;

        var mat = new Material(sh);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", outlineColor);

        // Pad the box outward so it sits proud of the surface. Padding is a world distance,
        // converted into the object's local units so it becomes a consistent world offset.
        Vector3 ls = a.transform.lossyScale;
        Vector3 pad = new Vector3(
            boxPaddingMeters / Mathf.Max(1e-4f, Mathf.Abs(ls.x)),
            boxPaddingMeters / Mathf.Max(1e-4f, Mathf.Abs(ls.y)),
            boxPaddingMeters / Mathf.Max(1e-4f, Mathf.Abs(ls.z)));
        lb.Expand(pad * 2f);

        // 8 corners in the object's local space.
        Vector3 c0 = lb.center, ex = lb.extents;
        Vector3[] cn = new Vector3[8];
        for (int i = 0; i < 8; i++)
            cn[i] = c0 + new Vector3(
                ((i & 1) == 0 ? -ex.x : ex.x),
                ((i & 2) == 0 ? -ex.y : ex.y),
                ((i & 4) == 0 ? -ex.z : ex.z));

        var root = new GameObject("SelectionBox");
        root.transform.SetParent(a.transform, false);
        root.transform.localPosition = Vector3.zero;
        root.transform.localRotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;

        var set = new OutlineSet { owner = a, boxRoot = root, material = mat };

        if (highlightStyle == HighlightStyle.NearestFace)
        {
            // Just four lines; each frame we point them at whichever face faces the camera.
            set.dynamicFace = true;
            set.corners = cn;
            set.localCenter = c0;
            set.faceLines = new LineRenderer[4];
            for (int i = 0; i < 4; i++)
                set.faceLines[i] = NewLine(root.transform, mat);
        }
        else
        {
            // Static full box: all 12 edges (bit0=x, bit1=y, bit2=z).
            int[,] edges = new int[12, 2]
            {
                {0,1},{1,3},{3,2},{2,0},   // bottom face (y-)
                {4,5},{5,7},{7,6},{6,4},   // top face (y+)
                {0,4},{1,5},{2,6},{3,7}    // verticals
            };
            for (int e = 0; e < 12; e++)
            {
                Vector3 p0 = cn[edges[e, 0]];
                Vector3 p1 = cn[edges[e, 1]];

                if (highlightStyle == HighlightStyle.FullBox)
                {
                    AddEdge(set, root.transform, mat, p0, p1);
                }
                else // CornerBrackets: a short stub at each end of every edge.
                {
                    float f = Mathf.Clamp(bracketFraction, 0.05f, 0.5f);
                    AddEdge(set, root.transform, mat, p0, Vector3.Lerp(p0, p1, f));
                    AddEdge(set, root.transform, mat, p1, Vector3.Lerp(p1, p0, f));
                }
            }
        }

        _outlines[a] = set;
        RefreshBoxWorldPositions(set);   // place the lines immediately
    }

    // Creates and configures a bare 2-point LineRenderer for a box edge.
    LineRenderer NewLine(Transform parent, Material mat)
    {
        var go = new GameObject("Edge");
        go.transform.SetParent(parent, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = true;                 // world positions -> width is an honest metre value
        lr.positionCount = 2;
        lr.widthMultiplier = boxLineWidthMeters; // world-space thickness, no scale math
        lr.numCapVertices = 2;
        lr.numCornerVertices = 0;
        lr.alignment = LineAlignment.View;       // face the camera so lines read from any angle
        lr.textureMode = LineTextureMode.Stretch;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.sharedMaterial = mat;
        lr.startColor = lr.endColor = Color.white; // colour comes from the material's _Color
        return lr;
    }

    void AddEdge(OutlineSet set, Transform parent, Material mat, Vector3 aLocal, Vector3 bLocal)
    {
        var lr = NewLine(parent, mat);
        set.edges.Add(new BoxEdge { lr = lr, aLocal = aLocal, bLocal = bLocal });
    }

    Camera GetViewCamera()
    {
        if (_viewCam != null) return _viewCam;
        _viewCam = Camera.main;
        if (_viewCam == null) _viewCam = FindFirstObjectByType<Camera>();
        return _viewCam;
    }

    // Convert stored local endpoints into current world positions. Called every frame so the box
    // tracks the object as it moves / rotates / scales. In NearestFace mode it also re-picks the
    // single face pointing toward the camera and draws only that face's 4 edges.
    void RefreshBoxWorldPositions(OutlineSet set)
    {
        if (set == null || set.owner == null) return;
        Matrix4x4 l2w = set.owner.transform.localToWorldMatrix;

        if (set.dynamicFace)
        {
            if (set.faceLines == null || set.corners == null) return;

            // Viewer position (HMD camera if we can find one, else the controller).
            Vector3 camPos;
            var cam = GetViewCamera();
            if (cam != null) camPos = cam.transform.position;
            else if (controller != null) camPos = controller.position;
            else camPos = l2w.MultiplyPoint3x4(set.localCenter) + Vector3.up * 1000f;

            Vector3 centerW = l2w.MultiplyPoint3x4(set.localCenter);

            // Pick the face whose outward normal points most toward the viewer.
            int best = 0; float bestDot = float.NegativeInfinity;
            for (int f = 0; f < 6; f++)
            {
                Vector3 nW = l2w.MultiplyVector(kFaceNormals[f]).normalized;
                Vector3 toCam = (camPos - centerW).normalized;
                float d = Vector3.Dot(nW, toCam);
                if (d > bestDot) { bestDot = d; best = f; }
            }

            int[] face = kFaceCorners[best];
            for (int i = 0; i < 4; i++)
            {
                var lr = set.faceLines[i];
                if (lr == null) continue;
                Vector3 pA = set.corners[face[i]];
                Vector3 pB = set.corners[face[(i + 1) % 4]];
                lr.SetPosition(0, l2w.MultiplyPoint3x4(pA));
                lr.SetPosition(1, l2w.MultiplyPoint3x4(pB));
            }
            return;
        }

        foreach (var e in set.edges)
        {
            if (e.lr == null) continue;
            e.lr.SetPosition(0, l2w.MultiplyPoint3x4(e.aLocal));
            e.lr.SetPosition(1, l2w.MultiplyPoint3x4(e.bLocal));
        }
    }

    void UpdateSelectionBoxes()
    {
        if (_outlines.Count == 0) return;
        foreach (var kv in _outlines)
            RefreshBoxWorldPositions(kv.Value);
    }

    void RemoveOutline(AIControllable a)
    {
        if (a == null) return;
        if (!_outlines.TryGetValue(a, out var set)) return;

        if (set.boxRoot != null) Destroy(set.boxRoot);
        if (set.material != null) Destroy(set.material);
        _outlines.Remove(a);
    }

    /// <summary>Rebuild the box after the object changed size, so padding/bounds stay correct.</summary>
    void RefreshOutline(AIControllable a)
    {
        if (a == null || !_outlines.ContainsKey(a)) return;
        RemoveOutline(a);
        AddOutline(a);
    }

    // Sphere sticks to the LOCKED object so it stays after you lower the controller.
    // If indicatorOnlyWhenLocked is false, it previews on the hovered object when nothing is locked.
    void UpdateIndicator()
    {
        if (!showIndicator || _indicator == null) return;

        AIControllable target = (_locked != null)
            ? _locked
            : (indicatorOnlyWhenLocked ? null : _current);

        if (target == null)
        {
            _indicator.SetActive(false);
            return;
        }

        _indicator.SetActive(true);
        _indicator.transform.localScale = Vector3.one * IndicatorDiameter();

        // "In front": sit on the face the user pointed at, nudged toward the viewer so it
        // doesn't sink into the mesh. Falls back to floating above the bounds if we have no hit.
        if (_locked == target && _hasLockedHit && head != null)
        {
            Vector3 toViewer = (head.position - _lockedHit.point).normalized;
            _indicator.transform.position = _lockedHit.point + toViewer * (IndicatorDiameter() * 0.5f + indicatorHoverHeight);
        }
        else
        {
            Bounds b = BoundsOf(target);
            _indicator.transform.position = new Vector3(b.center.x, b.max.y + indicatorHoverHeight, b.center.z);
        }
    }

    AIControllable FindBestByAngle(Vector3 p, Vector3 fwd)
    {
        var all = FindObjectsByType<AIControllable>(FindObjectsSortMode.None);
        if (all == null || all.Length == 0) return null;

        AIControllable best = null;
        float bestScore = float.PositiveInfinity;

        foreach (var a in all)
        {
            if (a == null) continue;
            Vector3 target = a.targetRenderer ? a.targetRenderer.bounds.center : a.transform.position;
            Vector3 to = target - p;
            float dist = to.magnitude;
            if (dist < 0.001f || dist > maxDistance) continue;
            float angle = Vector3.Angle(fwd, to / dist);
            if (angle > maxAngleDeg) continue;
            float score = angle + dist * distanceBias;
            if (score < bestScore) { bestScore = score; best = a; }
        }
        return best;
    }

    static string NameOrNull(Object o) => o ? o.name : "null";
    static string FriendlyName(AIControllable a) => a != null ? a.name.Replace("_", " ") : "nothing";
}
