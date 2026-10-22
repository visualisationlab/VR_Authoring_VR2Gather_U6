using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;

// Add to a GameObject and assign in VoiceCaptureAndSend.liveTranscription.
// Unity keeps microphone samples local until chunks are relayed to the Python server.
public class LiveTranscriptionBridge : MonoBehaviour
{
    public string serverBaseUrl = "http://localhost:8000";
    public float chunkSeconds = 0.30f;
    public string PartialTranscript { get; private set; } = "";
    public string FinalTranscript { get; private set; } = "";
    public bool IsLiveActive { get; private set; }

    private AudioClip _clip;
    private string _device;
    private int _sampleRate;
    private int _lastSample;
    private string _session;
    private bool _capture;
    private int _stopPosition = -1;
    private Coroutine _pump;
    private bool _pumpBusy;

    [Serializable] private class StartResponse { public string session_id; }
    [Serializable] private class ResultResponse { public string transcript; public string finalText; public string final_transcript; public string partial; }

    public void Begin(AudioClip clip, string device, int sampleRate)
    {
        _clip = clip; _device = device; _sampleRate = sampleRate;
        _lastSample = 0; _stopPosition = -1; _capture = true; _session = null;
        PartialTranscript = ""; FinalTranscript = ""; IsLiveActive = false;
        if (_pump != null) StopCoroutine(_pump);
        _pump = StartCoroutine(Pump());
    }

    public void StopCapture(int position = -1) { _stopPosition = position; _capture = false; }

    private IEnumerator Pump()
    {
        using (var req = UnityWebRequest.Post(serverBaseUrl.TrimEnd('/') + "/api/live/start", new WWWForm()))
        {
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success) { Debug.LogWarning("[LiveSTT] Falling back to batch: " + req.error); yield break; }
            var data = JsonUtility.FromJson<StartResponse>(req.downloadHandler.text);
            if (data == null || string.IsNullOrEmpty(data.session_id)) yield break;
            _session = data.session_id;
            IsLiveActive = true;
        }
        while (_capture)
        {
            yield return new WaitForSeconds(chunkSeconds);
            yield return SendAvailableAudio();
            if (!IsLiveActive) yield break;
            using (var status = UnityWebRequest.Get(serverBaseUrl.TrimEnd('/') + "/api/live/" + _session + "/status"))
            {
                yield return status.SendWebRequest();
                if (status.result == UnityWebRequest.Result.Success)
                {
                    var resp = JsonUtility.FromJson<ResultResponse>(status.downloadHandler.text);
                    if (resp != null) PartialTranscript = (resp.finalText ?? "") + " " + (resp.partial ?? "");
                }
            }
        }
    }

    private IEnumerator SendAvailableAudio()
    {
        if (_clip == null || string.IsNullOrEmpty(_session)) yield break;
        int pos = Microphone.GetPosition(_device);
        if (!_capture && _stopPosition >= 0) pos = Mathf.Max(_stopPosition, _lastSample);
        if (pos < _lastSample) { IsLiveActive = false; yield break; } // looped/overrun recording
        int count = pos - _lastSample;
        if (count == 0) yield break;
        float[] full = new float[_clip.samples * _clip.channels];
        _clip.GetData(full, 0);
        // 16k -> 24k: linear interpolation. Convert to mono signed PCM16.
        int outputCount = Mathf.CeilToInt(count * 24000f / _sampleRate);
        byte[] bytes = new byte[outputCount * 2];
        for (int i = 0; i < outputCount; ++i)
        {
            float src = _lastSample + i * (_sampleRate / 24000f);
            int a = Mathf.Clamp(Mathf.FloorToInt(src), 0, _clip.samples - 1);
            int b = Mathf.Min(a + 1, _clip.samples - 1);
            float t = src - a;
            float x = 0f, y = 0f;
            for (int ch = 0; ch < _clip.channels; ++ch)
            { x += full[a * _clip.channels + ch]; y += full[b * _clip.channels + ch]; }
            float value = Mathf.Lerp(x / _clip.channels, y / _clip.channels, t);
            short pcm = (short)Mathf.RoundToInt(Mathf.Clamp(value, -1f, 1f) * 32767);
            bytes[i * 2] = (byte)(pcm & 255);
            bytes[i * 2 + 1] = (byte)((pcm >> 8) & 255);
        }
        _lastSample = pos;
        using (var req = new UnityWebRequest(serverBaseUrl.TrimEnd('/') + "/api/live/" + _session + "/audio", "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(bytes);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/octet-stream");
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            { Debug.LogWarning("[LiveSTT] Chunk failed: " + req.error); IsLiveActive = false; }
        }
    }

    public IEnumerator Finish()
    {
        StopCapture();
        if (!IsLiveActive || string.IsNullOrEmpty(_session)) yield break;
        if (_pump != null) { StopCoroutine(_pump); _pump = null; }
        yield return SendAvailableAudio();
        if (!IsLiveActive) yield break;
        using (var req = UnityWebRequest.Post(serverBaseUrl.TrimEnd('/') + "/api/live/" + _session + "/finish", new WWWForm()))
        {
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success)
            {
                var res = JsonUtility.FromJson<ResultResponse>(req.downloadHandler.text);
                FinalTranscript = res != null ? res.transcript : "";
            }
            else Debug.LogWarning("[LiveSTT] Finish failed: " + req.error);
        }
        IsLiveActive = false;
    }
}
