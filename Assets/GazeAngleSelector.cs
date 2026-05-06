using UnityEngine;

public class GazeAngleSelector : MonoBehaviour
{
    public Transform head;              // assign your HMD camera transform (or any head transform)
    public float maxAngleDeg = 12f;     // cone size
    public float maxDistance = 10f;     // ignore far objects
    public float distanceTieBreak = 0.02f;

    public AIControllable GetTarget()
    {
        if (head == null) return null;

        var all = FindObjectsByType<AIControllable>(FindObjectsSortMode.None);
        AIControllable best = null;
        float bestScore = float.PositiveInfinity;

        Vector3 p = head.position;
        Vector3 f = head.forward;

        foreach (var a in all)
        {
            Vector3 target = a.targetRenderer ? a.targetRenderer.bounds.center : a.transform.position;
            Vector3 to = target - p;
            float d = to.magnitude;
            if (d < 0.001f || d > maxDistance) continue;

            float angle = Vector3.Angle(f, to / d);
            if (angle > maxAngleDeg) continue;

            float score = angle + d * distanceTieBreak; // mainly angle, slightly prefer closer
            if (score < bestScore) { bestScore = score; best = a; }
        }

        return best;
    }
}
