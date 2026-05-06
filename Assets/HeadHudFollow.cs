using UnityEngine;

public class HeadHudFollow : MonoBehaviour
{
    [Header("Optional: assign if you know it")]
    public Transform head;

    [Header("Placement")]
    public float distance = 2.0f;
    public float heightOffset = -0.1f;
    public float followSmooth = 15f;

    void Start()
    {
        BindHeadIfNeeded();
    }

    void BindHeadIfNeeded()
    {
        if (head != null) return;

        // Same logic as your GazeTargetInteractor
        var cam = Camera.main != null ? Camera.main : FindFirstObjectByType<Camera>();
        if (cam != null)
        {
            head = cam.transform;
            Debug.Log("[HeadHudFollow] Head bound to: " + head.name);
        }
        else
        {
            Debug.LogWarning("[HeadHudFollow] No camera found. Ensure there is a Camera in the scene.");
        }
    }

    void LateUpdate()
    {
        if (head == null)
        {
            BindHeadIfNeeded();
            if (head == null) return;
        }

        Vector3 targetPos = head.position + head.forward * distance + head.up * heightOffset;
        transform.position = Vector3.Lerp(transform.position, targetPos, Time.deltaTime * followSmooth);

        // Face the user (billboard)
        Vector3 lookDir = transform.position - head.position;
        if (lookDir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(lookDir, Vector3.up);
    }
}
