#if UNITY_EDITOR
using UnityEngine;

public static class VoiceModelGenBridge
{
    public static async void GenerateFromVoice(string prompt, string name, Vector3 position)
    {
        try
        {
            await AutoModelGeneratorEditor.GenerateAndPlaceAsync(prompt, name, position);
        }
        catch (System.Exception e)
        {
            Debug.LogError("[VoiceModelGenBridge] Generation failed: " + e);
        }
    }
}
#endif
