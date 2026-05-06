// =============================================================================
// AIAgentFollower.cs
// Attach this to the AI_Agent GameObject.
//
// Behaviour:
//   - Detects when the player TELEPORTS (position jumps > teleportThreshold metres)
//   - On teleport: snaps 1 m to the left of the player's new position
//   - Between teleports: does NOT move at all (head rotation / small steps ignored)
// =============================================================================

using UnityEngine;

public class AIAgentFollower : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("Leave empty — auto-binds to XROrigin or Camera.main at runtime.")]
    public Transform playerRoot;   // XROrigin / CameraOffset transform (not the head camera)

    [Header("Offset")]
    [Tooltip("1 m to the left. Change X to positive for right side.")]
    public Vector3 localOffset = new Vector3(-1f, 0f, 0f);

    [Header("Ground")]
    public float groundY = 0f;

    [Header("Teleport Detection")]
    [Tooltip("Position jump larger than this (metres) is treated as a teleport.")]
    public float teleportThreshold = 0.8f;

    // ─────────────────────────────────────────────────────────────────────────
    private Vector3 _lastPlayerPos;
    private bool    _initialised = false;

    // ─────────────────────────────────────────────────────────────────────────

    void Start()
    {
        BindPlayer();

        if (playerRoot != null)
        {
            _lastPlayerPos = playerRoot.position;
            _initialised   = true;
            SnapToPlayer();   // place agent correctly at scene start
        }
    }

    void LateUpdate()
    {
        if (playerRoot == null) { BindPlayer(); return; }
        if (!_initialised) { _lastPlayerPos = playerRoot.position; _initialised = true; return; }

        float moved = Vector3.Distance(
            new Vector3(playerRoot.position.x, 0, playerRoot.position.z),
            new Vector3(_lastPlayerPos.x,       0, _lastPlayerPos.z)
        );

        if (moved >= teleportThreshold)
        {
            SnapToPlayer();
            Debug.Log($"[AIAgentFollower] Teleport detected ({moved:0.0} m) — snapped to new position.");
        }

        _lastPlayerPos = playerRoot.position;
    }

    // ─────────────────────────────────────────────────────────────────────────

    void SnapToPlayer()
    {
        // Use horizontal forward only — ignore head tilt
        Vector3 flatForward = Vector3.ProjectOnPlane(playerRoot.forward, Vector3.up).normalized;
        if (flatForward == Vector3.zero) flatForward = Vector3.forward;

        Vector3 flatRight = Vector3.Cross(Vector3.up, flatForward).normalized;

        Vector3 targetPos = playerRoot.position
                          + flatRight   * localOffset.x
                          + flatForward * localOffset.z;

        targetPos.y = groundY;

        transform.position = targetPos;

        // Face the same direction as the player (horizontal only)
        transform.rotation = Quaternion.LookRotation(flatForward, Vector3.up);
    }

    // ─────────────────────────────────────────────────────────────────────────

    void BindPlayer()
    {
        if (playerRoot != null) return;

        // Prefer XROrigin root (stable, doesn't move with head rotation)
        var xrOrigin = FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
        if (xrOrigin != null)
        {
            playerRoot = xrOrigin.transform;
            Debug.Log("[AIAgentFollower] Bound to XROrigin: " + playerRoot.name);
            return;
        }

        // Fallback: same logic as HeadHudFollow
        var cam = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
        if (cam != null)
        {
            playerRoot = cam.transform.parent != null ? cam.transform.parent : cam.transform;
            Debug.Log("[AIAgentFollower] Bound to camera parent: " + playerRoot.name);
            return;
        }

        Debug.LogWarning("[AIAgentFollower] No XROrigin or Camera found.");
    }

    public void SwitchSide(float xOffset) => localOffset.x = xOffset;
}
