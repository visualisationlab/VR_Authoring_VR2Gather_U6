using UnityEngine;

/// <summary>
/// Lightweight tag stamped onto every spawned particle effect GameObject.
/// Lets ParticleEffectManager find and destroy specific effects by ID.
/// </summary>
public class ParticleEffectTracker : MonoBehaviour
{
    [HideInInspector] public string effectId;
}
