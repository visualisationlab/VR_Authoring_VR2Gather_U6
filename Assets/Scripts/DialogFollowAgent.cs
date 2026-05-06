using UnityEngine;

public class DialogFollowAgent : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Drag AI_Agent here (only used for auto-binding camera).")]
    public Transform agentTransform;

    [Tooltip("Drag your XR Head / Main Camera here.")]
    public Transform playerCamera;

    [Header("Position")]
    public float distance = 2f;
    public float angleDegrees = 50f;
    public float heightAboveGround = 0.1f;
    public LayerMask groundLayer = Physics.DefaultRaycastLayers;

    [Header("Follow")]
    [Tooltip("How fast the dialog lerps to its new position after a teleport.")]
    public float followSpeed = 8f;

    [Header("Teleport Detection")]
    [Tooltip("If the player moves more than this in one frame, it's treated as a teleport.")]
    public float teleportThreshold = 0.5f;

    // ─────────────────────────────────────────────────────────────────────────
    private Vector3 _targetPosition;
    private Quaternion _targetRotation;
    private Vector3 _lastCameraPosition;
    private bool _isLerping = false;

    // ─────────────────────────────────────────────────────────────────────────

    void Start()
    {
        BindReferences();

        if (playerCamera != null)
        {
            UpdateTarget();
            transform.position = _targetPosition;
            transform.rotation = _targetRotation;
            _lastCameraPosition = playerCamera.position;
        }
    }

    void LateUpdate()
    {
        if (playerCamera == null)
        {
            BindReferences();
            return;
        }

        float movedThisFrame = Vector3.Distance(playerCamera.position, _lastCameraPosition);

        // ── Teleport detected: recalculate target based on new forward ─────
        if (movedThisFrame > teleportThreshold)
        {
            UpdateTarget();
            _isLerping = true;
        }

        _lastCameraPosition = playerCamera.position;

        // ── Smoothly lerp to target only when needed ───────────────────────
        if (_isLerping)
        {
            transform.position = Vector3.Lerp(
                transform.position,
                _targetPosition,
                Time.deltaTime * followSpeed
            );

            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                _targetRotation,
                Time.deltaTime * followSpeed
            );

            // Stop lerping once close enough
            if (Vector3.Distance(transform.position, _targetPosition) < 0.01f)
            {
                transform.position = _targetPosition;
                transform.rotation = _targetRotation;
                _isLerping = false;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────

    void UpdateTarget()
    {
        _targetPosition = GetTargetPosition();
        _targetRotation = GetTargetRotation();
    }

    Vector3 GetTargetPosition()
    {
        Vector3 flatForward = playerCamera.forward;
        flatForward.y = 0f;
        flatForward.Normalize();

        Vector3 offsetDir = Quaternion.AngleAxis(-angleDegrees, Vector3.up) * flatForward;
        Vector3 horizontalPos = playerCamera.position + offsetDir * distance;

        float groundY = SampleGroundHeight(horizontalPos);
        return new Vector3(horizontalPos.x, groundY + heightAboveGround, horizontalPos.z);
    }

    Quaternion GetTargetRotation()
    {
        Vector3 lookDir = _targetPosition - playerCamera.position;
        lookDir.y = 0f;

        if (lookDir.sqrMagnitude < 0.001f)
            return transform.rotation;

        return Quaternion.LookRotation(lookDir, Vector3.up);
    }

    float SampleGroundHeight(Vector3 horizontalPos)
    {
        Vector3 rayOrigin = new Vector3(horizontalPos.x, playerCamera.position.y + 2f, horizontalPos.z);

        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 20f, groundLayer))
            return hit.point.y;

        return playerCamera.position.y - 1.8f;
    }

    // ─────────────────────────────────────────────────────────────────────────

    void BindReferences()
    {
        if (agentTransform == null)
        {
            var agentGO = GameObject.Find("AI_Agent");
            if (agentGO != null)
            {
                agentTransform = agentGO.transform;
                Debug.Log("[DialogFollowAgent] Auto-bound to AI_Agent.");
            }
        }

        if (playerCamera == null)
        {
            var cam = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
            if (cam != null)
            {
                playerCamera = cam.transform;
                Debug.Log("[DialogFollowAgent] Camera bound to: " + playerCamera.name);
            }
            else
            {
                Debug.LogWarning("[DialogFollowAgent] No camera found.");
            }
        }
    }
}