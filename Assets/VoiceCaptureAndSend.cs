// =============================================================================
// VoiceCaptureAndSend.cs
// Records voice → sends to server → server runs Whisper (auto-translates to
// English) → LLM decides an action → Unity dispatches the result.
//
// Actions:
//   generate_model   – 3D object generation via Meshy
//   create_poster    – poster / image generation
//   set_wall_texture – texture generation & application
//   scale            – proportional deterministic scaling
//   set_dimensions   – absolute deterministic world dimensions
//   run_code         – custom runtime C# behaviour (never resizing)
//   no_action        – nothing to do
// =============================================================================

using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine.XR;
using VRT.Orchestrator;

public class VoiceCaptureAndSend : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────────────────────
    // Inspector fields
    // ─────────────────────────────────────────────────────────────────────────

    [Header("Microphone")]
    public string microphoneDevice;
    public int    sampleRate             = 16000;
    public int    maxRecordLengthSeconds = 600;

    [Header("Server")]
    public string serverUrl   = "http://localhost:8000/transcribe";
    public string textTo3dUrl = "http://localhost:8000/api/text-to-3d";

    [Header("Optional Systems")]
    public RuntimeModelSpawner  modelSpawner;
    public PosterSpawner        posterSpawner;
    public SceneStateStore      stateStore;

    [Header("Gaze")]
    public GazeTargetInteractor gazeInteractor;

    [Header("Confirmation Dialog")]
    public ConfirmationDialog confirmationDialog;

    [Header("UI Feedback (optional)")]
    public TextMeshProUGUI aiIntentText;
    public TextMeshProUGUI transcriptText;
    public TextMeshProUGUI recordingStatusText;

    [Header("Keyboard Controls")]
    public KeyCode startKey = KeyCode.R;
    public KeyCode stopKey  = KeyCode.T;

    [Header("Poster Defaults")]
    public float defaultPosterWidthM  = 1f;
    public float defaultPosterHeightM = 1f;

    [Header("UI Settings")]
    public float completedJobLingerSeconds = 7f;
    public float tickUiInterval            = 0.25f;

    [Header("Master Only UI")]
    [Tooltip("When true, AI status/progress UI is shown only for the VR2Gather master/session creator.")]
    public bool showFeedbackOnlyForMaster = true;

    [Tooltip("Keep UI visible in editor/solo mode when VR2Gather Comm is not available yet.")]
    public bool showFeedbackIfCommUnavailable = true;

    // ─────────────────────────────────────────────────────────────────────────
    // Private state
    // ─────────────────────────────────────────────────────────────────────────

    private AudioClip recordedClip;
    private bool      isRecording = false;

    private InputDevice rightController;
    private bool        lastAPressed = false;

    private string _capturedTargetNameAtStop = null;

    // Clean dialog execution progress
    private bool   _isExecutingCleanAction = false;
    private float  _executionStartTime = 0f;
    private string _currentActionLabel = "";
    private bool   _showPercentForCurrentAction = false;   // Only true for 3D model generation
    private int    _lastCleanProgressPercent = 0;

    // ─────────────────────────────────────────────────────────────────────────
    // Job tracker
    // ─────────────────────────────────────────────────────────────────────────

    private int _jobSeq = 0;

    private class Job
    {
        public int        id;
        public string     type;
        public float      startTime;
        public string     status;
        public int        progress;
        public GameObject boundTarget;
        public float ElapsedSeconds => Mathf.Max(0f, Time.realtimeSinceStartup - startTime);
    }

    private class CompletedJob
    {
        public int    id;
        public string type;
        public string targetName;
        public bool   ok;
        public float  startTime;
        public float  finishTime;
        public string detail;
    }

    private readonly Dictionary<int, Job>       _jobs              = new Dictionary<int, Job>();
    private readonly Dictionary<int, Coroutine> _modelPollRoutines = new Dictionary<int, Coroutine>();
    private readonly List<CompletedJob>         _completedJobs     = new List<CompletedJob>();
    private string _headerLine = "";
    private float  _nextTickTime = 0f;

    private int StartJob(string type, string initialStatus, GameObject boundTarget, float startTimeOverride = -1f)
    {
        int id = ++_jobSeq;
        _jobs[id] = new Job
        {
            id         = id,
            type       = type,
            startTime  = startTimeOverride >= 0f ? startTimeOverride : Time.realtimeSinceStartup,
            status     = initialStatus,
            progress   = 0,
            boundTarget = boundTarget
        };
        RefreshJobsUI();
        return id;
    }

    private void UpdateJob(int id, string status, int progress = -1)
    {
        if (!_jobs.TryGetValue(id, out var j)) return;

        j.status = status ?? "";
        if (progress >= 0)
            j.progress = Mathf.Clamp(progress, 0, 100);

        // Only show percentage for actual 3D model generation jobs.
        // For poster, texture, run_code, etc. the dialog shows elapsed time only.
        if (_isExecutingCleanAction &&
            _showPercentForCurrentAction &&
            (j.type == "model" || j.type == "model+code"))
        {
            _lastCleanProgressPercent = j.progress;
            UpdateCleanExecutionProgress();
        }

        RefreshJobsUI();
    }

    private void FinishJob(int id, bool ok, string reason = "")
    {
        if (!_jobs.TryGetValue(id, out var j)) return;
        float jobStartTime = j.startTime;
        string finishedType = j.type;

        _jobs.Remove(id);

        if (_modelPollRoutines.TryGetValue(id, out var co))
        {
            if (co != null) StopCoroutine(co);
            _modelPollRoutines.Remove(id);
        }

        _completedJobs.Add(new CompletedJob
        {
            id         = id,
            type       = finishedType,
            targetName = j.boundTarget != null ? j.boundTarget.name : "?",
            ok         = ok,
            startTime  = jobStartTime,
            finishTime = Time.realtimeSinceStartup,
            detail     = string.IsNullOrWhiteSpace(reason) ? j.status : reason
        });

        // Do not let the internal "voice" dispatch job overwrite long-running poster/model/texture status.
        // Actual action jobs stop the clean UI when they finish.
        if (_isExecutingCleanAction && finishedType != "voice")
        {
            StopCleanExecutionProgress(ok ? "Action completed" : "Action failed");
        }

        RefreshJobsUI();
    }

    private IEnumerator FinishJobNextFrame(int jobId, bool ok, string reason = "")
    {
        float captured = Time.realtimeSinceStartup;
        yield return null;
        if (!_jobs.TryGetValue(jobId, out var j)) yield break;

        _jobs.Remove(jobId);
        if (_modelPollRoutines.TryGetValue(jobId, out var co))
        {
            if (co != null) StopCoroutine(co);
            _modelPollRoutines.Remove(jobId);
        }

        _completedJobs.Add(new CompletedJob
        {
            id         = jobId,
            type       = j.type,
            targetName = j.boundTarget != null ? j.boundTarget.name : "?",
            ok         = ok,
            startTime  = j.startTime,
            finishTime = captured,
            detail     = string.IsNullOrWhiteSpace(reason) ? j.status : reason
        });

        RefreshJobsUI();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Master-only UI visibility
    // ─────────────────────────────────────────────────────────────────────────

    private bool IsMasterUser()
    {
        if (!showFeedbackOnlyForMaster)
            return true;

        if (VRTOrchestratorSingleton.Comm == null)
            return showFeedbackIfCommUnavailable;

        return VRTOrchestratorSingleton.Comm.UserIsMaster;
    }

    private void ApplyFeedbackUIVisibility()
    {
        bool visible = IsMasterUser();

        if (aiIntentText != null)
            aiIntentText.gameObject.SetActive(visible);

        if (transcriptText != null)
            transcriptText.gameObject.SetActive(visible);

        if (recordingStatusText != null)
            recordingStatusText.gameObject.SetActive(visible);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // UI helpers
    // ─────────────────────────────────────────────────────────────────────────

    private void SetHeaderLine(string msg)
    {
        if (!IsMasterUser()) return;

        _headerLine = msg ?? "";
        SetStatusText(_headerLine);
    }

    private void RefreshJobsUI()
    {
        // Clean VR dialog mode:
        // Do not print verbose job tracker lines such as "[RUN] #1 voice..."
        // Status/progress is now controlled explicitly by SetStatusText()
        // and UpdateCleanExecutionProgress().
    }

    private void SetStatusText(string msg)
    {
        if (!IsMasterUser()) return;
        if (recordingStatusText != null)
            recordingStatusText.text = msg ?? "";
    }

    private void SetTranscriptClean(string transcript)
    {
        if (!IsMasterUser()) return;
        if (transcriptText != null)
            transcriptText.text = string.IsNullOrWhiteSpace(transcript)
                ? ""
                : "Transcript:\n\n" + transcript.Trim();
    }

    private void SetIntentClean(Command cmd)
    {
        if (!IsMasterUser()) return;
        if (aiIntentText == null) return;

        if (cmd == null || string.IsNullOrWhiteSpace(cmd.action))
        {
            aiIntentText.text = "Intent (action): no_action";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("Intent (action): " + cmd.action);

        if (!string.IsNullOrWhiteSpace(cmd.behaviour_prompt))
        {
            sb.AppendLine();
            sb.AppendLine("Behaviour prompt:");
            sb.AppendLine(cmd.behaviour_prompt);
        }
        else if (!string.IsNullOrWhiteSpace(cmd.prompt))
        {
            sb.AppendLine();
            sb.AppendLine("Prompt:");
            sb.AppendLine(cmd.prompt);
        }
        else if (!string.IsNullOrWhiteSpace(cmd.image_prompt))
        {
            sb.AppendLine();
            sb.AppendLine("Image prompt:");
            sb.AppendLine(cmd.image_prompt);
        }
        else if (!string.IsNullOrWhiteSpace(cmd.texture_prompt))
        {
            sb.AppendLine();
            sb.AppendLine("Texture prompt:");
            sb.AppendLine(cmd.texture_prompt);
        }

        aiIntentText.text = sb.ToString().TrimEnd();
    }

    private Command GetPrimaryCommand(Command[] commands, Command singleCommand)
    {
        if (singleCommand != null)
            return singleCommand;

        if (commands != null && commands.Length > 0)
        {
            foreach (var c in commands)
            {
                if (c != null && !string.IsNullOrWhiteSpace(c.action))
                    return c;
            }
        }

        return null;
    }

    private string GetCleanActionLabel(Command cmd)
    {
        if (cmd == null || string.IsNullOrWhiteSpace(cmd.action))
            return "Executing action";

        string action = cmd.action.Trim().ToLowerInvariant();

        if (action == "run_code")
        {
            string p = cmd.behaviour_prompt != null ? cmd.behaviour_prompt.ToLowerInvariant() : "";

            if (p.Contains("color") || p.Contains("colour") || p.Contains("renderer") || p.Contains("material"))
                return "Changing color";

            if (p.Contains("move") || p.Contains("place") || p.Contains("position") || p.Contains("put"))
                return "Moving object";

            if (p.Contains("rotate") || p.Contains("rotation"))
                return "Rotating object";

            if (p.Contains("scale") || p.Contains("resize") || p.Contains("size"))
                return "Resizing object";

            if (p.Contains("fire") || p.Contains("smoke") || p.Contains("particle") || p.Contains("water") || p.Contains("spark"))
                return "Creating visual effect";

            return "Running script";
        }

        if (action == "generate_model") return "Generating 3D model";
        if (action == "create_poster") return "Creating poster";
        if (action == "set_wall_texture") return "Applying texture";
        if (action == "scale" || action == "set_dimensions") return "Resizing object";
        if (action == "no_action") return "No action";

        return "Executing action";
    }

    private bool IsLongRunningCommand(Command cmd)
    {
        if (cmd == null || string.IsNullOrWhiteSpace(cmd.action))
            return false;

        string action = cmd.action.Trim().ToLowerInvariant();

        // These actions finish later in their own coroutine.
        // run_code usually dispatches immediately.
        return action == "generate_model" ||
               action == "create_poster" ||
               action == "set_wall_texture";
    }


    private void StartCleanExecutionProgress(Command cmd)
    {
        _currentActionLabel = GetCleanActionLabel(cmd);
        _executionStartTime = Time.realtimeSinceStartup;
        _isExecutingCleanAction = true;
        _lastCleanProgressPercent = 0;

        // Percentage is only meaningful for Meshy / 3D generation jobs.
        // For all other commands we only show elapsed time.
        string action = cmd != null && !string.IsNullOrWhiteSpace(cmd.action)
            ? cmd.action.Trim().ToLowerInvariant()
            : "";

        _showPercentForCurrentAction = action == "generate_model";

        UpdateCleanExecutionProgress();
    }

    private void StopCleanExecutionProgress(string finalLabel = null)
    {
        if (!_isExecutingCleanAction && string.IsNullOrWhiteSpace(_currentActionLabel))
            return;

        float elapsed = Mathf.Max(0f, Time.realtimeSinceStartup - _executionStartTime);
        string label = string.IsNullOrWhiteSpace(finalLabel) ? _currentActionLabel : finalLabel;

        bool showPercent = _showPercentForCurrentAction;

        _isExecutingCleanAction = false;
        _showPercentForCurrentAction = false;

        if (showPercent)
            SetStatusText($"{label}\n\nElapsed time: {elapsed:0.0} s\nProgress: 100%");
        else
            SetStatusText($"{label}\n\nFinished in {elapsed:0.0} s");
    }

    private void UpdateCleanExecutionProgress()
    {
        if (!_isExecutingCleanAction || recordingStatusText == null)
            return;

        float elapsed = Mathf.Max(0f, Time.realtimeSinceStartup - _executionStartTime);

        if (_showPercentForCurrentAction)
        {
            int percent = Mathf.Clamp(_lastCleanProgressPercent, 0, 99);
            SetStatusText($"{_currentActionLabel}\n\nElapsed time: {elapsed:0.0} s\nProgress: {percent}%");
        }
        else
        {
            SetStatusText($"{_currentActionLabel}\n\nElapsed time: {elapsed:0.0} s");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // JSON models
    // ─────────────────────────────────────────────────────────────────────────

    [System.Serializable]
    public class Command
    {
        // shared
        public string action;

        // generate_model
        public string prompt;
        public string name;
        public string stage;
        public string art_style;

        // create_poster
        public string image_prompt;
        public float  width_m;
        public float  height_m;

        // set_wall_texture
        public string texture_prompt;
        public float  tile_scale;

        // scale
        public float    factor;

        // set_dimensions reuses width_m / height_m above.
        // 0 means "not specified / preserve current".
        public float    depth_m;

        // run_code
        public string   behaviour_prompt;
        public string   target;             // fallback target name for run_code
        public string[] targets;
        public string[] reference_objects;
        public string   relation;
    }

    [System.Serializable]
    private class TranscribeGateResponse
    {
        public string    transcript;
        public bool      requires_confirmation;
        public string    session_id;
        public string    confirmation_message;
        public string    dialog_summary;
        public Command   command;
        public Command[] commands;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scene object name collector
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a JSON array string of every root-level and named GameObject in
    /// the scene so the server can ground LLM object references in real names.
    /// Skips unnamed, hidden, or internal Unity objects.
    /// </summary>
    private string CollectSceneObjectNamesJson()
    {
        var names = new System.Collections.Generic.List<string>();
        foreach (var go in FindObjectsByType<GameObject>(FindObjectsSortMode.None))
        {
            // Skip objects that are clearly internal / uninteresting
            if (go == null) continue;
            string n = go.name;
            if (string.IsNullOrWhiteSpace(n))          continue;
            if (n.StartsWith("__"))                     continue;  // Unity internals
            if (!go.activeInHierarchy)                  continue;  // hidden objects
            names.Add(n.Replace("\"", "\\\"")); // escape any quotes
        }
        // Deduplicate and sort for readability
        var unique = new System.Collections.Generic.HashSet<string>(names);
        var sorted = new System.Collections.Generic.List<string>(unique);
        sorted.Sort();
        return "[" + string.Join(",", sorted.ConvertAll(s => "\"" + s + "\"")) + "]";
    }

    [System.Serializable]
    private class TextTo3DRequest
    {
        public string prompt;
        public string name;
        public string stage;
        public string art_style;
    }

    [System.Serializable]
    private class TextTo3DProgressResponse
    {
        public string status;
        public int    progress;
        public string downloadUrl;
        public string message;
    }

    [System.Serializable]
    private class PosterImageRequest
    {
        public string prompt;
        public int    width_px  = 1024;
        public int    height_px = 1024;
    }

    [System.Serializable]
    private class PosterImageResponse
    {
        public string image_url;
        public string message;
    }

    [System.Serializable]
    private class TextureImageRequest
    {
        public string prompt;
        public int    size_px = 1024;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Wall anchor (needed by poster & texture)
    // ─────────────────────────────────────────────────────────────────────────

    [System.Serializable]
    public struct WallAnchor
    {
        public Transform wall;
        public Vector3   localPoint;
        public Vector3   localNormal;

        public static WallAnchor FromHit(RaycastHit hit)
        {
            var a = new WallAnchor();
            a.wall = hit.collider != null ? hit.collider.transform : null;
            if (a.wall != null)
            {
                a.localPoint  = a.wall.InverseTransformPoint(hit.point);
                a.localNormal = a.wall.InverseTransformDirection(hit.normal);
            }
            return a;
        }

        public bool   IsValid()      => wall != null;
        public Vector3 WorldPoint()  => wall != null ? wall.TransformPoint(localPoint) : Vector3.zero;
        public Vector3 WorldNormal() => wall != null ? wall.TransformDirection(localNormal).normalized : Vector3.forward;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Unity lifecycle
    // ─────────────────────────────────────────────────────────────────────────

    void Start()
    {
        if (Microphone.devices.Length > 0)
        {
            microphoneDevice = Microphone.devices[0];
            Debug.Log("[VoiceCaptureAndSend] Using microphone: " + microphoneDevice);
        }
        else
        {
            Debug.LogError("[VoiceCaptureAndSend] No microphone found.");
            SetHeaderLine("No microphone found.");
        }

        if (gazeInteractor == null) gazeInteractor = FindFirstObjectByType<GazeTargetInteractor>();
        if (stateStore    == null) stateStore      = FindFirstObjectByType<SceneStateStore>();
        if (modelSpawner  == null) modelSpawner    = FindFirstObjectByType<RuntimeModelSpawner>();
        if (posterSpawner == null) posterSpawner   = FindFirstObjectByType<PosterSpawner>();

        if (AICodeCommandHandler.Instance == null)
            Debug.LogWarning("[VoiceCaptureAndSend] AICodeCommandHandler not found in scene.");

        ApplyFeedbackUIVisibility();

        SetStatusText("Keep pressing A for Recording\nRelease A to Stop Recording");
        if (transcriptText != null) transcriptText.text = "";
        if (aiIntentText != null) aiIntentText.text = "";

        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(XRNode.RightHand, devices);
        if (devices.Count > 0) rightController = devices[0];
    }

    void Update()
    {
        ApplyFeedbackUIVisibility();

        if (Input.GetKeyDown(startKey)) StartListening();
        if (Input.GetKeyDown(stopKey))  StopListening();

        // XR controllers can become valid after Start(), especially in VR2Gather/OpenXR.
        // Keep refreshing the right-hand controller until Unity reports a valid device.
        if (!rightController.isValid)
        {
            var devices = new List<InputDevice>();
            InputDevices.GetDevicesAtXRNode(XRNode.RightHand, devices);

            if (devices.Count > 0)
            {
                rightController = devices[0];
                Debug.Log("[VoiceCaptureAndSend] Right controller connected: " + rightController.name);
            }
        }

        if (rightController.isValid)
        {
            // Read ONLY the physical A button state
            rightController.TryGetFeatureValue(CommonUsages.primaryButton, out bool btnDown);

            // Debug information
          /*  Debug.Log(
                $"[VoiceCaptureAndSend] primaryButton={btnDown}  " +
                $"lastAPressed={lastAPressed}  " +
                $"isRecording={isRecording}"
            );*/

            // Start recording on button press
            if (btnDown && !lastAPressed && !isRecording)
            {
                Debug.Log("[VoiceCaptureAndSend] A pressed -> START recording");
                StartListening();
            }

            // Stop recording on button release
            if (!btnDown && lastAPressed && isRecording)
            {
                Debug.Log("[VoiceCaptureAndSend] A released -> STOP recording");
                StopListening();
            }

            lastAPressed = btnDown;
        }
        else
        {
            lastAPressed = false;
        }
        
        UpdateCleanExecutionProgress();

        if ((_jobs.Count > 0 || _completedJobs.Count > 0) && Time.realtimeSinceStartup >= _nextTickTime)
        {
            _nextTickTime = Time.realtimeSinceStartup + tickUiInterval;
            RefreshJobsUI();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Recording
    // ─────────────────────────────────────────────────────────────────────────

    public void StartListening()  => StartRecording();
    public void StopListening()   => StopRecordingAndSend();

    public void StartRecording()
    {
        if (isRecording) { SetHeaderLine("Already recording..."); return; }
        if (string.IsNullOrEmpty(microphoneDevice)) { SetHeaderLine("No microphone selected."); return; }

        _capturedTargetNameAtStop = null;

        // Continuously capture screenshots while recording — last one wins at stop
        _screenshotLoopCoroutine = StartCoroutine(CaptureScreenshotLoop());

        recordedClip = Microphone.Start(microphoneDevice, false, maxRecordLengthSeconds, sampleRate);
        isRecording = true;

        _isExecutingCleanAction = false;
        _currentActionLabel = "";

        SetStatusText("Recording...");

        if (transcriptText != null) transcriptText.text = "";
        if (aiIntentText != null) aiIntentText.text = "";
    }

    private Coroutine _screenshotLoopCoroutine = null;
    private readonly System.Collections.Generic.List<string> _capturedScreenshots = new System.Collections.Generic.List<string>();

    // Takes a screenshot every 2 seconds while recording.
    // All captures are collected — server receives all of them for a complete scene picture.
    private IEnumerator CaptureScreenshotLoop()
    {
        _capturedScreenshots.Clear();

        while (true)
        {
            yield return new WaitForEndOfFrame();

            try
            {
                int w = Screen.width;
                int h = Screen.height;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();

                var resized = ResizeTexture(tex, 512, 512);
                string b64 = System.Convert.ToBase64String(resized.EncodeToJPG(60));
                _capturedScreenshots.Add(b64);
                Destroy(tex);
                Destroy(resized);

                Debug.Log($"[VoiceCaptureAndSend] Screenshot captured ({_capturedScreenshots.Count} total)");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[VoiceCaptureAndSend] Screenshot capture failed: " + e.Message);
            }

            yield return new WaitForSeconds(2f);
        }
    }

    private Texture2D ResizeTexture(Texture2D source, int targetWidth, int targetHeight)
    {
        var rt = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32);
        rt.filterMode = FilterMode.Bilinear;
        RenderTexture.active = rt;
        Graphics.Blit(source, rt);
        var result = new Texture2D(targetWidth, targetHeight, TextureFormat.RGB24, false);
        result.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
        result.Apply();
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(rt);
        return result;
    }

    public void StopRecordingAndSend()
    {
        if (!isRecording) { SetHeaderLine("Not recording."); return; }

        // Stop the screenshot loop — _capturedScreenshots now holds all frames
        if (_screenshotLoopCoroutine != null)
        {
            StopCoroutine(_screenshotLoopCoroutine);
            _screenshotLoopCoroutine = null;
        }

        int position = Microphone.GetPosition(microphoneDevice);
        Microphone.End(microphoneDevice);
        isRecording = false;

        if (position <= 0 || recordedClip == null) { SetHeaderLine("No audio captured."); return; }

        // Trim to recorded length
        int     channels = recordedClip.channels;
        float[] samples  = new float[position * channels];
        recordedClip.GetData(samples, 0);
        AudioClip trimmed = AudioClip.Create("Recording", position, channels, sampleRate, false);
        trimmed.SetData(samples, 0);
        byte[] wavData = AudioClipToWav(trimmed);

        // Snapshot target for the server.
        // PRIORITY 1: the LOCKED selection (controller ray + select button). This is the
        //             object the user explicitly chose, and it survives lowering the
        //             controller while speaking — the whole point of the lock workflow.
        // PRIORITY 2 (fallback): the live ray hit at the moment recording stops.
        string     gazeTargetName = "none";
        WallAnchor wallAnchor     = default;

        if (gazeInteractor != null)
        {
            if (gazeInteractor.LockedTarget != null)
            {
                gazeTargetName = gazeInteractor.LockedTarget.name;
                if (gazeInteractor.TryGetLockedHit(out RaycastHit lockedHit))
                    wallAnchor = WallAnchor.FromHit(lockedHit);
                Debug.Log($"[VoiceCaptureAndSend] Target = LOCKED selection: '{gazeTargetName}'");
            }
            else if (gazeInteractor.TryGetCurrentHit(out RaycastHit hit) && hit.collider != null)
            {
                // Resolve through AIControllable so the reported name matches what the
                // selection system would lock (child colliders can have different names).
                var aic = hit.collider.GetComponent<AIControllable>();
                if (aic == null) aic = hit.collider.GetComponentInParent<AIControllable>();
                gazeTargetName = aic != null ? aic.name : hit.collider.gameObject.name;
                wallAnchor     = WallAnchor.FromHit(hit);
                Debug.Log($"[VoiceCaptureAndSend] Target = live ray hit (no lock): '{gazeTargetName}'");
            }
            else
            {
                Debug.Log("[VoiceCaptureAndSend] Target = none (no lock, no ray hit)");
            }
        }

        _capturedTargetNameAtStop = gazeTargetName != "none" ? gazeTargetName : null;

        float requestStartTime = Time.realtimeSinceStartup;
        int   voiceJobId       = StartJob("voice", "UPLOADING", null, requestStartTime);

        SetStatusText("Recording stopped.\n\nProcessing...");
        StartCoroutine(SendAudioToServer(wavData, gazeTargetName, wallAnchor, voiceJobId, requestStartTime));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Server pipeline: POST /transcribe → confirmation gate → dispatch
    // ─────────────────────────────────────────────────────────────────────────

    IEnumerator SendAudioToServer(byte[] wavData, string gazeTargetName, WallAnchor wallAnchor,
                                  int voiceJobId, float requestStartTime)
    {
        WWWForm form = new WWWForm();
        form.AddBinaryData("audio",       wavData, "recording.wav", "audio/wav");
        form.AddField     ("gaze_target", gazeTargetName);

        // Send all screenshots collected during recording as a JSON array
        string screenshotsJson = "[" + string.Join(",", System.Array.ConvertAll(
            _capturedScreenshots.ToArray(),
            s => "\"" + s + "\""
        )) + "]";
        form.AddField("screenshots_b64", screenshotsJson);

        // Send all scene GameObject names so the server LLM can resolve
        // colloquial names ("home", "tree") to exact Unity names
        string sceneObjectsJson = CollectSceneObjectNamesJson();
        form.AddField("scene_objects", sceneObjectsJson);
        Debug.Log("[VoiceCaptureAndSend] Scene objects sent: " + sceneObjectsJson);

        UpdateJob(voiceJobId, "SENDING", 10);

        using (UnityWebRequest www = UnityWebRequest.Post(serverUrl, form))
        {
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[VoiceCaptureAndSend] Upload failed: " + www.error);
                SetHeaderLine("upload_failed");
                FinishJob(voiceJobId, false, "upload_failed");
                yield break;
            }

            UpdateJob(voiceJobId, "PARSING", 60);

            string json = www.downloadHandler.text;
            Debug.Log("[VoiceCaptureAndSend] Raw JSON: " + json);

            TranscribeGateResponse gateResult = null;
            try { gateResult = JsonUtility.FromJson<TranscribeGateResponse>(json); }
            catch (System.Exception e)
            {
                Debug.LogError("[VoiceCaptureAndSend] JSON parse error: " + e);
                SetHeaderLine("json_parse_failed");
                FinishJob(voiceJobId, false, "json_parse_failed");
                yield break;
            }

            if (gateResult == null)
            {
                SetHeaderLine("empty_response");
                FinishJob(voiceJobId, false, "empty_response");
                yield break;
            }

            Command primaryCommandForDisplay = GetPrimaryCommand(gateResult.commands, gateResult.command);
            SetTranscriptClean(gateResult.transcript);
            SetIntentClean(primaryCommandForDisplay);

            // ── Confirmation required ─────────────────────────────────────
            if (gateResult.requires_confirmation)
            {
                UpdateJob(voiceJobId, "AWAITING_CONFIRM", 70);
                SetStatusText("Waiting for confirmation...");

                var  capturedCommands = gateResult.commands;
                var  capturedCommand  = gateResult.command;
                bool confirmed        = false;

                if (confirmationDialog != null)
                {
                    string dialogText = !string.IsNullOrWhiteSpace(gateResult.dialog_summary)
                        ? gateResult.dialog_summary
                        : gateResult.confirmation_message;

                    confirmationDialog.Show(
                        gateResult.session_id,
                        dialogText,
                        onExecute: _ =>
                        {
                            confirmed = true;
                            Command commandForProgress = GetPrimaryCommand(capturedCommands, capturedCommand);
                            StartCleanExecutionProgress(commandForProgress);
                        }
                    );

                    float timeout = 60f, waited = 0f;
                    while (!confirmed && confirmationDialog.IsPanelVisible && waited < timeout)
                    {
                        waited += Time.deltaTime;
                        yield return null;
                    }
                    yield return null; // one extra frame for callback
                }
                else
                {
                    confirmed = true;
                    Debug.LogWarning("[VoiceCaptureAndSend] ConfirmationDialog not assigned — auto-executing.");
                    Command commandForProgress = GetPrimaryCommand(capturedCommands, capturedCommand);
                    StartCleanExecutionProgress(commandForProgress);
                }

                if (!confirmed)
                {
                    _isExecutingCleanAction = false;
                    SetStatusText("Command cancelled.");
                    FinishJob(voiceJobId, false, "cancelled");
                    _headerLine = "";
                    RefreshJobsUI();
                    yield break;
                }

                UpdateJob(voiceJobId, "EXECUTING", 85);
                DispatchCommands(capturedCommands, capturedCommand, wallAnchor, voiceJobId, requestStartTime);
                yield break;
            }

            // ── No confirmation needed ───────────────────────────────────
            UpdateJob(voiceJobId, "EXECUTING", 85);
            DispatchCommands(gateResult.commands, gateResult.command, wallAnchor, voiceJobId, requestStartTime);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Dispatch
    // ─────────────────────────────────────────────────────────────────────────

    void DispatchCommands(Command[] commands, Command singleCommand,
                          WallAnchor wallAnchor, int voiceJobId, float requestStartTime)
    {
        bool executedAnything   = false;
        bool startedAnyAsyncJob = false;
        Command primaryCommandForProgress = GetPrimaryCommand(commands, singleCommand);

        // Special case: the server may return:
        //   1) generate_model  -> e.g. Generated_Tree
        //   2) run_code        -> place/move that Generated_Tree
        //
        // Do NOT execute both immediately. The run_code target does not exist yet,
        // so it would fall back to the gaze target (often Floor) and attach there.
        // Instead: generate/spawn first, wait until the object appears, then attach code.
        if (commands != null && commands.Length >= 2)
        {
            Command gen = null;
            Command code = null;

            foreach (var c in commands)
            {
                if (c == null || string.IsNullOrWhiteSpace(c.action)) continue;

                string a = c.action.Trim().ToLowerInvariant();
                if (gen == null && a == "generate_model")
                    gen = c;
                else if (code == null && a == "run_code")
                    code = c;
            }

            if (gen != null && code != null)
            {
                string genName = string.IsNullOrWhiteSpace(gen.name) ? "Generated_01" : gen.name.Trim();
                bool codeTargetsGeneratedObject = false;

                if (code.targets != null)
                {
                    foreach (string t in code.targets)
                    {
                        if (!string.IsNullOrWhiteSpace(t) &&
                            string.Equals(t.Trim(), genName, System.StringComparison.OrdinalIgnoreCase))
                        {
                            codeTargetsGeneratedObject = true;
                            break;
                        }
                    }
                }

                if (!codeTargetsGeneratedObject && !string.IsNullOrWhiteSpace(code.target))
                {
                    codeTargetsGeneratedObject =
                        string.Equals(code.target.Trim(), genName, System.StringComparison.OrdinalIgnoreCase);
                }

                if (codeTargetsGeneratedObject)
                {
                    string genPrompt = string.IsNullOrWhiteSpace(gen.prompt) ? "simple object" : gen.prompt;
                    string stage     = string.IsNullOrWhiteSpace(gen.stage) ? "preview" : gen.stage;
                    string style     = string.IsNullOrWhiteSpace(gen.art_style) ? "realistic" : gen.art_style;
                    string behaviour = string.IsNullOrWhiteSpace(code.behaviour_prompt)
                        ? "place the generated object in the requested location"
                        : code.behaviour_prompt;

                    int jobId = StartJob("model+code", "GENERATING_MODEL", null, requestStartTime);
                    Vector3 pos = GetPlacementPosition();

                    Debug.Log($"[VoiceCaptureAndSend] Sequenced generate_model + run_code: generate '{genName}', then attach code.");
                    StartCoroutine(GenerateAndAttachCode_Job(jobId, genPrompt, genName, pos, behaviour, stage, style));

                    executedAnything   = true;
                    startedAnyAsyncJob = true;

                    // Keep the clean intent text already shown in the dialog.

                    if (stateStore != null) stateStore.RequestSave();
                    FinishJob(voiceJobId, true, "dispatched_model_then_code");
                    _headerLine = "";
                    RefreshJobsUI();
                    return;
                }
            }
        }

        if (commands != null && commands.Length > 0)
        {
            foreach (var c in commands)
            {
                if (c == null || string.IsNullOrWhiteSpace(c.action)) continue;
                bool async = ApplyCommand(c, wallAnchor, requestStartTime);
                startedAnyAsyncJob |= async;
                executedAnything    = true;
            }
        }
        else if (singleCommand != null)
        {
            startedAnyAsyncJob = ApplyCommand(singleCommand, wallAnchor, requestStartTime);
            executedAnything   = true;
        }

        if (!executedAnything)
        {
            SetHeaderLine("No command executed.");
            FinishJob(voiceJobId, false, "no_command");
            return;
        }

        if (stateStore != null) stateStore.RequestSave();
        FinishJob(voiceJobId, true, startedAnyAsyncJob ? "dispatched_async" : "done");

        // For immediate actions like run_code, stop the clean progress here.
        // For poster/texture/model, their own coroutine stops it when the real work is finished.
        if (_isExecutingCleanAction && !IsLongRunningCommand(primaryCommandForProgress))
            StopCleanExecutionProgress("Action completed");

        _headerLine = "";
        RefreshJobsUI();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ApplyCommand — 5 cases only
    // ─────────────────────────────────────────────────────────────────────────

    private GameObject ResolveDeterministicTarget(Command cmd)
    {
        GameObject targetObj = null;

        if (cmd != null && cmd.targets != null && cmd.targets.Length > 0 &&
            !string.IsNullOrWhiteSpace(cmd.targets[0]))
        {
            targetObj = FindGameObjectCaseInsensitive(cmd.targets[0]);
        }

        if (targetObj == null && cmd != null && !string.IsNullOrWhiteSpace(cmd.target))
            targetObj = FindGameObjectCaseInsensitive(cmd.target);

        // Prefer the locked explicit selection when it exists.
        if (gazeInteractor != null && gazeInteractor.LockedTarget != null)
        {
            var locked = gazeInteractor.LockedTarget;

            // If the server target is a Poster_* and the locked selection is the same poster,
            // keep the locked poster. More importantly, never climb to its wall parent.
            if (targetObj == null || targetObj.name == locked.name)
                targetObj = locked.gameObject;
        }

        if (targetObj == null && !string.IsNullOrWhiteSpace(_capturedTargetNameAtStop))
            targetObj = FindGameObjectCaseInsensitive(_capturedTargetNameAtStop);

        return targetObj;
    }

    private AIControllable ResolveDeterministicAI(GameObject targetObj)
    {
        if (targetObj == null)
            return null;

        // Poster always wins over parent wall.
        PersistablePoster poster = targetObj.GetComponent<PersistablePoster>();
        if (poster == null)
            poster = targetObj.GetComponentInParent<PersistablePoster>();

        if (poster != null)
        {
            AIControllable posterAI = poster.GetComponent<AIControllable>();
            if (posterAI != null)
                return posterAI;
        }

        AIControllable ai = targetObj.GetComponent<AIControllable>();
        if (ai == null)
            ai = targetObj.GetComponentInParent<AIControllable>();

        return ai;
    }

    private static Bounds GetCombinedRendererBounds(GameObject go)
    {
        Renderer[] renderers = go != null ? go.GetComponentsInChildren<Renderer>(true) : null;
        if (renderers == null || renderers.Length == 0)
            return new Bounds(go != null ? go.transform.position : Vector3.zero, Vector3.zero);

        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
            b.Encapsulate(renderers[i].bounds);

        return b;
    }

    bool ApplyCommand(Command cmd, WallAnchor wallAnchor, float startTimeOverride)
    {
        if (cmd == null || string.IsNullOrWhiteSpace(cmd.action)) return false;

        string action = cmd.action.Trim().ToLowerInvariant();

        switch (action)
        {
            // ── 1. generate_model ─────────────────────────────────────────
            case "generate_model":
            {
                if (modelSpawner == null)
                {
                    Debug.LogError("[VoiceCaptureAndSend] generate_model: RuntimeModelSpawner missing.");
                    return false;
                }

                string p     = string.IsNullOrWhiteSpace(cmd.prompt)    ? "simple cube"   : cmd.prompt;
                string n     = string.IsNullOrWhiteSpace(cmd.name)      ? "Generated_01"  : cmd.name;
                string stage = string.IsNullOrWhiteSpace(cmd.stage)     ? "preview"       : cmd.stage;
                string style = string.IsNullOrWhiteSpace(cmd.art_style) ? "realistic"     : cmd.art_style;

                int     jobId = StartJob("model", "PENDING", null);
                Vector3 pos   = GetPlacementPosition();
                modelSpawner.GenerateAndSpawn(p, n, pos, stage, style);

                Coroutine co = StartCoroutine(PollTextTo3D_Job(jobId, p, n, stage, style));
                _modelPollRoutines[jobId] = co;

                // Keep the clean intent text already shown in the dialog.
                return true;
            }

            // ── 2. create_poster ─────────────────────────────────────────
            case "create_poster":
            {
                if (posterSpawner == null)
                {
                    Debug.LogError("[VoiceCaptureAndSend] create_poster: PosterSpawner missing.");
                    return false;
                }
                if (!wallAnchor.IsValid())
                {
                    Debug.LogWarning("[VoiceCaptureAndSend] create_poster: no wall surface — look at a wall before stopping.");
                    SetHeaderLine("Look at a wall first.");
                    return false;
                }

                string prompt = string.IsNullOrWhiteSpace(cmd.image_prompt) ? "abstract art" : cmd.image_prompt;
                float  w      = cmd.width_m  > 0f ? cmd.width_m  : defaultPosterWidthM;
                float  h      = cmd.height_m > 0f ? cmd.height_m : defaultPosterHeightM;

                int jobId = StartJob("poster", "PENDING", null);
                StartCoroutine(GeneratePosterImageAndSpawn_Job(jobId, prompt, wallAnchor, w, h));

                // Keep the clean intent text already shown in the dialog.
                return true;
            }

            // ── 3. set_wall_texture ───────────────────────────────────────
            case "set_wall_texture":
            {
                    if (!wallAnchor.IsValid() && !string.IsNullOrWhiteSpace(cmd.target))
                    {
                        GameObject targetObj = FindGameObjectCaseInsensitive(cmd.target);

                        if (targetObj != null)
                        {
                            wallAnchor = CreateAnchorFromTargetObject(targetObj);
                            Debug.Log("[VoiceCaptureAndSend] set_wall_texture using target: " + targetObj.name);
                        }
                    }

                    if (!wallAnchor.IsValid())
                    {
                        Debug.LogWarning("[VoiceCaptureAndSend] set_wall_texture: no valid target found.");
                        SetStatusText("Texture target not found.");
                        return false;
                    }

                    string tPrompt = string.IsNullOrWhiteSpace(cmd.texture_prompt) ? "brick wall" : cmd.texture_prompt;
                    float tile = cmd.tile_scale > 0f ? cmd.tile_scale : 1f;

                    int jobId = StartJob("texture", "PENDING", null);
                    StartCoroutine(GenerateTextureAndApply_Job(jobId, tPrompt, wallAnchor, tile));

                    return true;
            }

            // ── 4. run_code ───────────────────────────────────────────────
            case "run_code":
                {
                    if (AICodeCommandHandler.Instance == null)
                    {
                        Debug.LogError("[VoiceCaptureAndSend] run_code: AICodeCommandHandler not in scene.");
                        return false;
                    }

                    string behaviourPrompt = string.IsNullOrWhiteSpace(cmd.behaviour_prompt)
                        ? "make this object rotate slowly"
                        : cmd.behaviour_prompt;

                    GameObject resolvedTarget = null;

                    // 1. Prefer exact target from LLM
                    if (cmd.targets != null && cmd.targets.Length > 0 && !string.IsNullOrWhiteSpace(cmd.targets[0]))
                    {
                        resolvedTarget = FindGameObjectCaseInsensitive(cmd.targets[0]);
                    }

                    // 2. Fallback to single target field
                    if (resolvedTarget == null && !string.IsNullOrWhiteSpace(cmd.target))
                    {
                        resolvedTarget = FindGameObjectCaseInsensitive(cmd.target);
                    }

                    // 3. Fallback to frozen target captured at stop-recording
                    if (resolvedTarget == null && !string.IsNullOrWhiteSpace(_capturedTargetNameAtStop))
                    {
                        resolvedTarget = FindGameObjectCaseInsensitive(_capturedTargetNameAtStop);
                    }

                    if (resolvedTarget == null)
                    {
                        Debug.LogWarning("[VoiceCaptureAndSend] run_code: target could not be resolved.");
                        SetHeaderLine("Target not found.");
                        return false;
                    }

                    Debug.Log($"[VoiceCaptureAndSend] run_code attaching to '{resolvedTarget.name}'");
                    AICodeCommandHandler.Instance.HandleRunCode(behaviourPrompt,
                                                                resolvedTarget,
                                                                effectId: System.Guid.NewGuid().ToString(),
                                                                isReplay: false
                                                                );

                    if (modelSpawner != null)
                        modelSpawner.SaveBehaviourPrompt(resolvedTarget.name, behaviourPrompt);
                    if (stateStore != null)
                        stateStore.RequestSave();

                    // Keep the clean intent text already shown in the dialog.

                    return true;
                }

            // ── 5. scale (deterministic, poster-aware) ───────────────────
            case "scale":
                {
                    if (gazeInteractor == null)
                        gazeInteractor = FindFirstObjectByType<GazeTargetInteractor>();
                    if (gazeInteractor == null)
                    {
                        Debug.LogError("[VoiceCaptureAndSend] scale: GazeTargetInteractor not in scene.");
                        return false;
                    }

                    GameObject targetObj = ResolveDeterministicTarget(cmd);
                    AIControllable ai = ResolveDeterministicAI(targetObj);

                    if (ai == null)
                    {
                        Debug.LogWarning("[VoiceCaptureAndSend] scale: target could not be resolved.");
                        SetHeaderLine("Target not found.");
                        return false;
                    }

                    // Lock exactly this AI target; for posters this resolves to the poster itself,
                    // never the supporting wall.
                    gazeInteractor.LockSpecific(ai, fromVoice: true);

                    float factor = cmd.factor > 0.0001f ? cmd.factor : 1f;
                    gazeInteractor.ScaleGazedBy(factor);

                    Debug.Log("[VoiceCaptureAndSend] deterministic scale x" + factor +
                              " on '" + ai.name + "'");

                    if (stateStore != null) stateStore.RequestSave();
                    return true;
                }

            // ── 6. set_dimensions (absolute metres, poster-aware) ───────────
            case "set_dimensions":
                {
                    if (gazeInteractor == null)
                        gazeInteractor = FindFirstObjectByType<GazeTargetInteractor>();
                    if (gazeInteractor == null)
                    {
                        Debug.LogError("[VoiceCaptureAndSend] set_dimensions: GazeTargetInteractor not in scene.");
                        return false;
                    }

                    GameObject targetObj = ResolveDeterministicTarget(cmd);
                    AIControllable ai = ResolveDeterministicAI(targetObj);

                    if (ai == null)
                    {
                        Debug.LogWarning("[VoiceCaptureAndSend] set_dimensions: target could not be resolved.");
                        SetHeaderLine("Target not found.");
                        return false;
                    }

                    gazeInteractor.LockSpecific(ai, fromVoice: true);

                    PersistablePoster poster = ai.GetComponent<PersistablePoster>();

                    if (poster != null)
                    {
                        // Posters have a dedicated real-world metre model.
                        // Preserve unspecified dimensions.
                        float width  = cmd.width_m  > 0.0001f ? cmd.width_m  : poster.widthMeters;
                        float height = cmd.height_m > 0.0001f ? cmd.height_m : poster.heightMeters;

                        Debug.Log(
                            "[VoiceCaptureAndSend] set_dimensions POSTER '" + ai.name +
                            "' width=" + width + "m height=" + height + "m"
                        );

                        // GazeTargetInteractor's poster branch updates widthMeters/heightMeters,
                        // applies the poster scale locally, saves, and sends poster resize sync.
                        gazeInteractor.SetScaleXYZOnGazed(width, height, 1f);
                    }
                    else
                    {
                        // Normal wall / generated 3D object:
                        // convert requested WORLD dimensions to multiplicative localScale ratios.
                        Bounds b = GetCombinedRendererBounds(ai.gameObject);
                        Vector3 currentWorldSize = b.size;
                        Vector3 newLocalScale = ai.transform.localScale;

                        if (cmd.width_m > 0.0001f && currentWorldSize.x > 0.0001f)
                            newLocalScale.x *= cmd.width_m / currentWorldSize.x;

                        if (cmd.height_m > 0.0001f && currentWorldSize.y > 0.0001f)
                            newLocalScale.y *= cmd.height_m / currentWorldSize.y;

                        if (cmd.depth_m > 0.0001f && currentWorldSize.z > 0.0001f)
                            newLocalScale.z *= cmd.depth_m / currentWorldSize.z;

                        Debug.Log(
                            "[VoiceCaptureAndSend] set_dimensions OBJECT '" + ai.name +
                            "' worldBefore=" + currentWorldSize +
                            " localScaleAfter=" + newLocalScale
                        );

                        // This uses the normal object sync path in GazeTargetInteractor.
                        gazeInteractor.SetScaleXYZOnGazed(
                            newLocalScale.x,
                            newLocalScale.y,
                            newLocalScale.z
                        );
                    }

                    if (stateStore != null) stateStore.RequestSave();
                    return true;
                }

            // ── 7. no_action ─────────────────────────────────────────────
            case "no_action":
                SetIntentClean(cmd);
                SetStatusText("No action needed.");
                return false;

            default:
                Debug.LogWarning("[VoiceCaptureAndSend] Unknown action ignored: " + cmd.action);
                return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Async: 3D model polling
    // ─────────────────────────────────────────────────────────────────────────

    private IEnumerator PollTextTo3D_Job(int jobId, string prompt, string name, string stage, string artStyle)
    {
        const float pollInterval = 1f;

        while (true)
        {
            var    reqObj  = new TextTo3DRequest { prompt = prompt, name = name, stage = stage, art_style = artStyle };
            byte[] bodyRaw = Encoding.UTF8.GetBytes(JsonUtility.ToJson(reqObj));

            using (UnityWebRequest www = new UnityWebRequest(textTo3dUrl, "POST"))
            {
                www.uploadHandler   = new UploadHandlerRaw(bodyRaw);
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");
                yield return www.SendWebRequest();

                if (www.result != UnityWebRequest.Result.Success)
                {
                    FinishJob(jobId, false, "poll_failed: " + www.error);
                    yield break;
                }

                TextTo3DProgressResponse resp = null;
                try { resp = JsonUtility.FromJson<TextTo3DProgressResponse>(www.downloadHandler.text); }
                catch { }

                if (resp != null)
                {
                    string st = string.IsNullOrEmpty(resp.status) ? "IN_PROGRESS" : resp.status;
                    UpdateJob(jobId, st, resp.progress);

                    if (st == "SUCCEEDED" || resp.progress >= 100)
                    {
                        UpdateJob(jobId, "SUCCEEDED", 100);
                        FinishJob(jobId, true, "Model generated");
                        yield break;
                    }

                    if (st == "FAILED" || st == "ERROR")
                    {
                        FinishJob(jobId, false, "Model generation failed");
                        yield break;
                    }
                }

                yield return new WaitForSeconds(pollInterval);
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Async: Generate model then attach code
    // ─────────────────────────────────────────────────────────────────────────

    private IEnumerator GenerateAndAttachCode_Job(int jobId, string genPrompt, string genName,
                                                  Vector3 pos, string behaviourPrompt,
                                                  string stage = "preview", string artStyle = "realistic")
    {
        if (modelSpawner == null)                  { FinishJob(jobId, false, "no modelSpawner");          yield break; }
        if (AICodeCommandHandler.Instance == null) { FinishJob(jobId, false, "no AICodeCommandHandler");  yield break; }

        UpdateJob(jobId, "GENERATING_MODEL", 5);
        modelSpawner.GenerateAndSpawn(genPrompt, genName, pos, stage, artStyle);

        // Meshy can sit at 99% for a while, so give this enough time.
        const float timeout = 360f;
        float elapsed = 0f;
        GameObject spawnedObj = null;

        while (elapsed < timeout)
        {
            yield return new WaitForSeconds(1f);
            elapsed += 1f;

            spawnedObj = FindGameObjectCaseInsensitive(genName);
            if (spawnedObj != null && spawnedObj.activeInHierarchy)
                break;

            int pct = Mathf.Clamp(5 + Mathf.RoundToInt((elapsed / timeout) * 70f), 5, 75);
            UpdateJob(jobId, $"WAITING_FOR_{genName} ({elapsed:0}s)", pct);
        }

        if (spawnedObj == null)
        {
            FinishJob(jobId, false, $"'{genName}' never appeared after {timeout:0}s");
            yield break;
        }

        // Wait a couple of frames so GLB child renderers/colliders are available before code asks for bounds.
        yield return null;
        yield return null;

        UpdateJob(jobId, "ATTACHING_CODE", 85);
        Debug.Log($"[VoiceCaptureAndSend] Generated object appeared: '{spawnedObj.name}'. Attaching run_code now.");

        AICodeCommandHandler.Instance.HandleRunCode(
            behaviourPrompt,
            spawnedObj,
            effectId: System.Guid.NewGuid().ToString(),
            isReplay: false
        );

        if (modelSpawner != null)
            modelSpawner.SaveBehaviourPrompt(genName, behaviourPrompt);

        if (stateStore != null)
            stateStore.RequestSave();

        UpdateJob(jobId, "CODE_ATTACHED", 100);
        FinishJob(jobId, true, $"generated '{genName}' + code attached");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Async: Poster generation
    // ─────────────────────────────────────────────────────────────────────────

    private IEnumerator GeneratePosterImageAndSpawn_Job(int jobId, string prompt, WallAnchor anchor,
                                                        float widthM, float heightM)
    {
        string endpoint = BuildServerEndpoint("/api/poster-image");

        int   wpx = 1024, hpx = 1024;
        float aspect = widthM / Mathf.Max(0.0001f, heightM);
        if      (aspect > 1.2f) { wpx = 1344; hpx = 768;  }
        else if (aspect < 0.8f) { wpx = 768;  hpx = 1344; }

        byte[] bodyRaw = Encoding.UTF8.GetBytes(
            JsonUtility.ToJson(new PosterImageRequest { prompt = prompt, width_px = wpx, height_px = hpx }));

        UpdateJob(jobId, "POSTER_GENERATION", 5);

        using (UnityWebRequest www = new UnityWebRequest(endpoint, "POST"))
        {
            www.uploadHandler   = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                FinishJob(jobId, false, "POSTER_FAILED: " + www.error);
                yield break;
            }

            PosterImageResponse resp = null;
            try { resp = JsonUtility.FromJson<PosterImageResponse>(www.downloadHandler.text); }
            catch { }

            if (resp == null || string.IsNullOrWhiteSpace(resp.image_url))
            {
                FinishJob(jobId, false, "POSTER_FAILED (no image_url)");
                yield break;
            }

            UpdateJob(jobId, "POSTER_READY", 100);
            posterSpawner.CreatePosterAtAnchor(anchor, resp.image_url, widthM, heightM);
            if (stateStore != null) stateStore.RequestSave();
            FinishJob(jobId, true, $"{widthM:0.0}x{heightM:0.0}m");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Async: Texture generation
    // ─────────────────────────────────────────────────────────────────────────

    private IEnumerator GenerateTextureAndApply_Job(int jobId, string texturePrompt,
                                                    WallAnchor anchor, float tileScale)
    {
        string endpoint = BuildServerEndpoint("/api/texture-image");
        byte[] bodyRaw  = Encoding.UTF8.GetBytes(
            JsonUtility.ToJson(new TextureImageRequest { prompt = texturePrompt, size_px = 1024 }));

        UpdateJob(jobId, "TEXTURE_GENERATION", 5);

        using (UnityWebRequest www = new UnityWebRequest(endpoint, "POST"))
        {
            www.uploadHandler   = new UploadHandlerRaw(bodyRaw);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                FinishJob(jobId, false, "TEXTURE_FAILED: " + www.error);
                yield break;
            }

            var resp = JsonUtility.FromJson<PosterImageResponse>(www.downloadHandler.text);
            if (resp == null || string.IsNullOrWhiteSpace(resp.image_url))
            {
                FinishJob(jobId, false, "TEXTURE_FAILED (no image_url)");
                yield break;
            }

            var applier = FindFirstObjectByType<WallTextureApplier>();
            if (applier == null) { FinishJob(jobId, false, "WallTextureApplier missing"); yield break; }

            UpdateJob(jobId, "TEXTURE_APPLYING", 70);
            applier.ApplyTextureUrlToAnchor(anchor, resp.image_url, tileScale);
            if (stateStore != null) stateStore.RequestSave();
            UpdateJob(jobId, "TEXTURE_APPLIED", 100);
            FinishJob(jobId, true);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private string BuildServerEndpoint(string apiPath)
    {
        // Derive http://host:port from serverUrl, e.g.
        // http://192.168.0.206:8000/transcribe -> http://192.168.0.206:8000/api/poster-image
        try
        {
            var uri = new System.Uri(serverUrl);
            return uri.GetLeftPart(System.UriPartial.Authority) + apiPath;
        }
        catch
        {
            return "http://localhost:8000" + apiPath;
        }
    }

    private WallAnchor CreateAnchorFromTargetObject(GameObject targetObj)
    {
        WallAnchor anchor = default;

        if (targetObj == null)
            return anchor;

        Renderer r = targetObj.GetComponentInChildren<Renderer>();

        if (r == null)
            return anchor;

        anchor.wall = targetObj.transform;
        anchor.localPoint = targetObj.transform.InverseTransformPoint(r.bounds.center);
        anchor.localNormal = targetObj.transform.InverseTransformDirection(Vector3.forward);

        return anchor;
    }

    private Vector3 GetPlacementPosition()
    {
        var cam = Camera.main;
        return cam != null ? cam.transform.position + cam.transform.forward * 2f : Vector3.zero;
    }

    private GameObject FindGameObjectCaseInsensitive(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        string lower = name.ToLowerInvariant();
        foreach (var go in FindObjectsByType<GameObject>(FindObjectsSortMode.None))
            if (go.name.ToLowerInvariant() == lower) return go;
        return null;
    }

    private byte[] AudioClipToWav(AudioClip clip)
    {
        float[] samples   = new float[clip.samples * clip.channels];
        clip.GetData(samples, 0);

        short[] intData   = new short[samples.Length];
        byte[]  bytesData = new byte[samples.Length * 2];

        const float rescale = 32767f;
        for (int i = 0; i < samples.Length; i++)
        {
            intData[i] = (short)(samples[i] * rescale);
            System.BitConverter.GetBytes(intData[i]).CopyTo(bytesData, i * 2);
        }

        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            int fileSize = bytesData.Length + 44 - 8;
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(fileSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)clip.channels);
            writer.Write(clip.frequency);
            writer.Write(clip.frequency * clip.channels * 2);
            writer.Write((short)(clip.channels * 2));
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(bytesData.Length);
            writer.Write(bytesData);
            return stream.ToArray();
        }
    }
}