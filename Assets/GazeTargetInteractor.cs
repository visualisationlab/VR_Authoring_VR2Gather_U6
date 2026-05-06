using UnityEngine;
using System.Collections;
using System.Collections.Generic;

public class GazeTargetInteractor : MonoBehaviour
{
    [Header("Head (HMD)")]
    public Transform head;

    [Header("Selection cone")]
    public float maxAngleDeg = 12f;
    public float maxDistance = 10f;
    public float distanceBias = 0.02f;

    [Header("Highlight")]
    public bool highlightSelection = true;
    public Color highlightColor = Color.red;

    [Header("Debug Logging")]
    public bool logCurrentChanges = true;
    public bool logStackActions = true;

    [Header("Gaze Raycast (for wall / surface placement)")]
    [Tooltip("Which layers can be 'hit' by the gaze ray (recommended: set walls to a Walls layer).")]
    public LayerMask gazeLayerMask = ~0;

    [Tooltip("If true, draw a debug ray in Scene view.")]
    public bool debugDrawRay = false;

    AIControllable _current;
    AIControllable _locked;

    bool _lockIsManual = false;

    bool _pendingStack = false;
    float _pendingGap = 0.01f;

    readonly Dictionary<AIControllable, Color> _savedColors = new Dictionary<AIControllable, Color>();

    // ✅ NEW: last physics hit info (for posters / decals / placing objects on surfaces)
    RaycastHit _lastHit;
    bool _hasHit;

    void OnEnable()
    {
        StartCoroutine(BindHeadWhenReady());
    }

    IEnumerator BindHeadWhenReady()
    {
        float elapsed = 0f;
        while (head == null && elapsed < 5f)
        {
            var cam = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
            if (cam != null) head = cam.transform;

            if (head != null)
            {
                Debug.Log("[GazeTargetInteractor] Head bound to: " + head.name);
                yield break;
            }

            elapsed += 0.1f;
            yield return new WaitForSeconds(0.1f);
        }

        if (head == null)
            Debug.LogWarning("[GazeTargetInteractor] No head/camera found. Assign 'head' in Inspector.");
    }

    void Update()
    {
        if (head == null) return;

        // ✅ record a real raycast hit for "place poster here" use-cases
        _hasHit = Physics.Raycast(
            head.position,
            head.forward,
            out _lastHit,
            maxDistance,
            gazeLayerMask,
            QueryTriggerInteraction.Ignore
        );

        if (debugDrawRay)
        {
            Debug.DrawRay(head.position, head.forward * maxDistance, _hasHit ? Color.green : Color.yellow);
            if (_hasHit) Debug.DrawRay(_lastHit.point, _lastHit.normal * 0.25f, Color.cyan);
        }

        var next = FindBestByAngle();

        if (next != _current)
        {
            if (highlightSelection && _current != null)
                RestoreColor(_current);

            if (highlightSelection && next != null)
                Highlight(next);

            _current = next;

            if (logCurrentChanges)
                Debug.Log($"[GAZE] current={NameOrNull(_current)} locked={NameOrNull(_locked)} pendingStack={_pendingStack} manualLock={_lockIsManual}");

            TryCompletePendingStack();
        }
        else
        {
            TryCompletePendingStack();
        }

        if (Input.GetKeyDown(KeyCode.Alpha1))
        {
            LockCurrent(fromVoice: false);
        }
    }

    // ✅ expose the gaze RaycastHit for wall/surface placement (posters, decals, etc.)
    public bool TryGetCurrentHit(out RaycastHit hit)
    {
        hit = _lastHit;
        return _hasHit;
    }

    void TryCompletePendingStack()
    {
        if (!_pendingStack) return;
        if (_locked == null) return;
        if (_current == null) return;
        if (_current == _locked) return;

        if (logStackActions)
            Debug.Log($"[STACK] AUTO stack -> locked={_locked.name} base={_current.name} gap={_pendingGap:0.###}");

        StackLockedOnCurrent(_pendingGap);
        _pendingStack = false;
    }

    // ---------------- LOCKING ----------------

    public void LockCurrent(bool fromVoice = true)
    {
        if (_lockIsManual && fromVoice)
        {
            if (logStackActions)
                Debug.Log($"[STACK] VOICE LOCK IGNORED (manual lock active) locked={NameOrNull(_locked)}");
            return;
        }

        _locked = _current;
        _lockIsManual = !fromVoice;

        if (logStackActions)
        {
            Debug.Log($"[STACK] LOCK -> locked={NameOrNull(_locked)} current={NameOrNull(_current)} fromVoice={fromVoice}");
            if (_locked != null)
                Debug.Log($"[STACK] lockedPos={_locked.transform.position}");
        }
    }

    // ✅ NEW: lock a specific object even if user is not looking at it
    public void LockSpecific(AIControllable target, bool fromVoice = true)
    {
        if (target == null) return;

        if (_lockIsManual && fromVoice)
        {
            if (logStackActions)
                Debug.Log($"[STACK] VOICE LOCKSPECIFIC IGNORED (manual lock active) locked={NameOrNull(_locked)}");
            return;
        }

        _locked = target;
        _lockIsManual = !fromVoice;

        if (logStackActions)
            Debug.Log($"[STACK] LOCKSPECIFIC -> locked={NameOrNull(_locked)} fromVoice={fromVoice}");
    }

    // ✅ convenience for VoiceCaptureAndSend (it remembers a GameObject)
    public void LockSpecific(GameObject go, bool fromVoice = true)
    {
        if (go == null) return;

        // try same object then parent
        AIControllable a = go.GetComponent<AIControllable>();
        if (a == null) a = go.GetComponentInParent<AIControllable>();
        if (a == null)
        {
            if (logStackActions)
                Debug.LogWarning("[GazeTargetInteractor] LockSpecific(GameObject): AIControllable not found on object or parents: " + go.name);
            return;
        }

        LockSpecific(a, fromVoice);
    }

    public void ClearLock()
    {
        _locked = null;
        _pendingStack = false;
        _lockIsManual = false;

        if (logStackActions)
            Debug.Log($"[STACK] UNLOCK -> locked=null current={NameOrNull(_current)} pendingStack=false manualLock=false");
    }

    public void ArmStackOnNext(float gap = 0.01f)
    {
        if (_locked == null)
        {
            Debug.LogWarning("[STACK] Cannot arm stack: no locked object. Look at the TOP cube and 'select' first.");
            return;
        }

        _pendingStack = true;
        _pendingGap = gap;

        if (logStackActions)
            Debug.Log($"[STACK] ARMED -> pendingStack=true locked={NameOrNull(_locked)} gap={_pendingGap:0.###}");
    }

    public void StackLockedOnCurrent(float gap = 0.01f)
    {
        if (logStackActions)
            Debug.Log($"[STACK] STACK_ON called -> locked={NameOrNull(_locked)} current={NameOrNull(_current)} gap={gap:0.###}");

        if (_locked == null)
        {
            Debug.LogWarning("[STACK] No locked object. Look at the TOP cube and say 'select this' first.");
            return;
        }

        if (_current == null)
        {
            Debug.LogWarning("[STACK] No current target. Look at the BASE cube and try again.");
            return;
        }

        if (_locked == _current)
        {
            Debug.LogWarning("[STACK] Cannot stack an object on itself. Look at a different cube for the base.");
            return;
        }

        _locked.StackOn(_current, gap);

        if (logStackActions)
        {
            Debug.Log($"[STACK] DONE -> locked={_locked.name} on base={_current.name}");
            Debug.Log($"[STACK] lockedPos={_locked.transform.position} basePos={_current.transform.position}");
        }
    }

    public void LogState()
    {
        Debug.Log($"[STATE] locked={NameOrNull(_locked)} current={NameOrNull(_current)} pendingStack={_pendingStack} manualLock={_lockIsManual}");
    }

    public AIControllable Current => _current;
    public AIControllable Locked => _locked;

    // ✅ NEW: effective target for normal actions
    private AIControllable TargetForActions => (_locked != null) ? _locked : _current;

    // ---------------- Gaze APIs ----------------

    public void SetColorOnGazed(string colorName)
    {
        var t = TargetForActions;
        if (t == null) return;

        // avoid persisting highlight red
        if (highlightSelection) RestoreColor(t);
        t.SetColor(colorName);
        if (highlightSelection) Highlight(t);

        if (logStackActions)
            Debug.Log($"[GAZE] set_color -> target={NameOrNull(t)} color={colorName}");
    }

    public void SetMaterialOnGazed(string materialName)
    {
        var t = TargetForActions;
        if (t == null) return;

        // avoid persisting highlight red
        if (highlightSelection) RestoreColor(t);

        t.SetMaterial(materialName);

        if (highlightSelection) Highlight(t);

        if (logStackActions)
            Debug.Log($"[GAZE] set_material -> target={NameOrNull(t)} material={materialName}");
    }

    public void SetScaleUniformOnGazed(float s)
    {
        var t = TargetForActions;
        if (t != null) t.SetScaleUniform(s);
        if (logStackActions) Debug.Log($"[GAZE] set_scale_uniform -> target={NameOrNull(t)} s={s}");
    }

    public void SetScaleXYZOnGazed(float x, float y, float z)
    {
        var t = TargetForActions;
        if (t != null) t.SetScaleXYZ(x, y, z);
        if (logStackActions) Debug.Log($"[GAZE] set_scale_xyz -> target={NameOrNull(t)} x={x} y={y} z={z}");
    }

    public void ScaleGazedBy(float factor)
    {
        var t = TargetForActions;
        if (t == null) return;

        var poster = t.GetComponent<PersistablePoster>();
        if (poster != null)
        {
            poster.widthMeters  = Mathf.Max(0.01f, poster.widthMeters  * factor);
            poster.heightMeters = Mathf.Max(0.01f, poster.heightMeters * factor);
            var spawner = FindFirstObjectByType<PosterSpawner>();
            if (spawner != null)
                spawner.ApplyWorldSizeToPoster(t.transform, poster.widthMeters, poster.heightMeters);
            else
                t.transform.localScale *= factor;
            if (logStackActions) Debug.Log($"[GAZE] scale_by poster -> target={NameOrNull(t)} ×{factor} → w={poster.widthMeters} h={poster.heightMeters}");
        }
        else
        {
            t.ScaleBy(factor);
            if (logStackActions) Debug.Log($"[GAZE] scale_by -> target={NameOrNull(t)} factor={factor}");
        }
    }

    public void MoveGazedToWorld(float x, float y, float z)
    {
        var t = TargetForActions;
        if (t != null) t.MoveWorld(x, y, z);
        if (logStackActions) Debug.Log($"[GAZE] move -> target={NameOrNull(t)} x={x} y={y} z={z}");
    }

    public void TranslateGazedWorld(float dx, float dy, float dz)
    {
        var t = TargetForActions;
        if (t != null) t.TranslateWorld(dx, dy, dz);
        if (logStackActions) Debug.Log($"[GAZE] translate -> target={NameOrNull(t)} dx={dx} dy={dy} dz={dz}");
    }

    public void PlaceGazedOnFloor(float floorY = 0f)
    {
        var t = TargetForActions;
        if (t == null) return;
        t.PlaceOnFloor(floorY);
        if (logStackActions) Debug.Log($"[GAZE] place_on_floor -> target={NameOrNull(t)} floorY={floorY}");
    }

    public void DropGazed()
    {
        var t = TargetForActions;
        if (t != null) t.Drop();
        if (logStackActions) Debug.Log($"[GAZE] drop -> target={NameOrNull(t)}");
    }

    public void FreezeGazed()
    {
        var t = TargetForActions;
        if (t != null) t.Freeze();
        if (logStackActions) Debug.Log($"[GAZE] freeze -> target={NameOrNull(t)}");
    }

    void Highlight(AIControllable a)
    {
        if (a == null) return;

        if (!_savedColors.ContainsKey(a))
        {
            if (a.TryGetColor(out var baseCol))
                _savedColors[a] = baseCol;
        }

        a.TrySetColor(highlightColor);
    }

    void RestoreColor(AIControllable a)
    {
        if (a == null) return;

        if (_savedColors.TryGetValue(a, out var col))
        {
            a.TrySetColor(col);
            _savedColors.Remove(a);
        }
    }

    public void ScaleAxisOnGazed(string axis, float deltaMeters)
    {
        var target = TargetForActions;
        if (target == null) return;

        var poster = target.GetComponent<PersistablePoster>();
        if (poster != null)
        {
            // Poster local axes depend on wall orientation (set via LookRotation(-normal, up)).
            // We must map world-space axis intent → poster width or height.
            //
            // Strategy: project each world axis onto the poster's local X (width) and Y (height).
            // Whichever world axis aligns most with poster local X → that axis letter = width.
            // Whichever world axis aligns most with poster local Y → that axis letter = height.
            //
            // This works regardless of which wall the poster is on.
            Transform pt = target.transform;
            Vector3 posterRight = pt.right;   // local X → poster width direction in world
            Vector3 posterUp    = pt.up;      // local Y → poster height direction in world

            // Dominant world axis of posterRight (X=0, Y=1, Z=2)
            int widthWorldAxis  = DominantAxis(posterRight);
            // Dominant world axis of posterUp
            int heightWorldAxis = DominantAxis(posterUp);

            int requestedAxis = axis == "x" ? 0 : axis == "y" ? 1 : 2;

            var spawner = FindFirstObjectByType<PosterSpawner>();
            if (requestedAxis == heightWorldAxis)
                poster.heightMeters = Mathf.Max(0.01f, poster.heightMeters + deltaMeters);
            else // treat any non-height axis as width (x or z depending on wall)
                poster.widthMeters  = Mathf.Max(0.01f, poster.widthMeters  + deltaMeters);

            if (spawner != null)
                spawner.ApplyWorldSizeToPoster(pt, poster.widthMeters, poster.heightMeters);
            else
            {
                Vector3 s = pt.localScale;
                s.x = poster.widthMeters;
                s.y = poster.heightMeters;
                pt.localScale = s;
            }

            if (logStackActions)
                Debug.Log($"[GAZE] scale_axis poster -> target={NameOrNull(target)} axis={axis} " +
                          $"widthAxis={widthWorldAxis} heightAxis={heightWorldAxis} " +
                          $"delta={deltaMeters} → w={poster.widthMeters} h={poster.heightMeters}");
        }
        else
        {
            // Regular scene object: localScale axes match world axes directly.
            Vector3 s = target.transform.localScale;
            switch (axis)
            {
                case "x": s.x = Mathf.Max(0.01f, s.x + deltaMeters); break;
                case "y": s.y = Mathf.Max(0.01f, s.y + deltaMeters); break;
                case "z": s.z = Mathf.Max(0.01f, s.z + deltaMeters); break;
            }
            target.transform.localScale = s;
            if (logStackActions)
                Debug.Log($"[GAZE] scale_axis -> target={NameOrNull(target)} axis={axis} delta={deltaMeters} → localScale={s}");
        }
    }

    // Returns 0 for X, 1 for Y, 2 for Z — whichever component of v has the largest magnitude.
    static int DominantAxis(Vector3 v)
    {
        float ax = Mathf.Abs(v.x), ay = Mathf.Abs(v.y), az = Mathf.Abs(v.z);
        if (ax >= ay && ax >= az) return 0;
        if (ay >= ax && ay >= az) return 1;
        return 2;
    }

    AIControllable FindBestByAngle()
    {
        var all = FindObjectsByType<AIControllable>(FindObjectsSortMode.None);
        if (all == null || all.Length == 0) return null;

        Vector3 p = head.position;
        Vector3 fwd = head.forward;

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
            if (score < bestScore)
            {
                bestScore = score;
                best = a;
            }
        }

        return best;
    }

    static string NameOrNull(Object o) => o ? o.name : "null";
}