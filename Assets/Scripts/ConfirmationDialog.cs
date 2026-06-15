using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using VRT.Orchestrator;

/// <summary>
/// From VoiceManager / VoiceCaptureAndSend call:
///   ConfirmationDialog.Instance.ShowTranscript(transcript)   ← live text while LLM thinks
///   ConfirmationDialog.Instance.Show(sessionId, msg, cb)     ← show YES/NO dialog
///
/// Master-only behavior:
///   - The whole dialog GameObject is visible only for the VR2Gather master/session creator.
///   - Joined users do not see the message, panel, Yes button, No button, or progress text from this dialog.
/// </summary>
public class ConfirmationDialog : MonoBehaviour
{
    public static ConfirmationDialog Instance { get; private set; }

    [Header("Existing UI — drag from your hierarchy")]
    [Tooltip("Drag 'Panel' here")]
    public GameObject dialogPanel;

    [Tooltip("Drag 'LLMText' (TMP) here")]
    public TMP_Text messageText;

    [Tooltip("Drag the Button component inside Button_Yes here")]
    public Button yesButton;

    [Tooltip("Drag the Button component inside Button_No here")]
    public Button noButton;

    [Header("Server")]
    public string serverUrl = "http://localhost:8000";

    [Header("Idle State")]
    public string idleMessage = "Press A and speak a command...";

    [Header("Master Only UI")]
    [Tooltip("When true, the whole confirmation dialog is visible only for the VR2Gather master/session creator.")]
    public bool showDialogOnlyForMaster = true;

    [Tooltip("Keep dialog visible in editor/solo mode when VR2Gather Comm is not available yet.")]
    public bool showDialogIfCommUnavailable = true;

    private string _pendingSessionId;
    private Action<string> _onExecute;
    private bool _awaitingAnswer = false;

    private bool IsMasterUser()
    {
        if (!showDialogOnlyForMaster)
            return true;

        if (VRTOrchestratorSingleton.Comm == null)
            return showDialogIfCommUnavailable;

        return VRTOrchestratorSingleton.Comm.UserIsMaster;
    }

    /// <summary>
    /// Hide/show the entire dialog root, not only the panel.
    /// This hides message, background, Yes/No buttons, and DialogFollowAgent together.
    /// </summary>
    private void SetWholeDialogVisible(bool visible)
    {
        bool finalVisible = IsMasterUser() && visible;

        // Hide the whole object that this script is attached to.
        // This is stronger than only hiding dialogPanel.
        if (gameObject.activeSelf != finalVisible)
            gameObject.SetActive(finalVisible);
    }

    /// <summary>
    /// Hide/show only the UI panel while keeping this component alive.
    /// Use this only for master user states.
    /// </summary>
    private void SetPanelVisible(bool visible)
    {
        if (dialogPanel != null)
            dialogPanel.SetActive(IsMasterUser() && visible);
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        Canvas canvas = GetComponent<Canvas>();
        if (canvas != null && canvas.worldCamera == null)
        {
            Camera cam = Camera.main;
            if (cam != null)
            {
                canvas.worldCamera = cam;
                Debug.Log("[ConfirmationDialog] Event camera assigned: " + cam.name);
            }
            else
            {
                Debug.LogWarning("[ConfirmationDialog] Camera.main not found — tag your XR camera as MainCamera.");
            }
        }

        if (messageText != null)
            messageText.text = idleMessage;

        SetButtonsInteractable(false);

        if (yesButton != null)
            yesButton.onClick.AddListener(OnYes);

        if (noButton != null)
            noButton.onClick.AddListener(OnNo);

        // IMPORTANT:
        // Joined users should not see any part of this dialog.
        if (!IsMasterUser())
        {
            _awaitingAnswer = false;
            SetPanelVisible(false);
            gameObject.SetActive(false);
            return;
        }

        SetPanelVisible(true);
    }

    void Start()
    {
        // In some VR2Gather scenes, Comm may become available after Awake().
        // Check once more at Start and hide the whole dialog if this client is not master.
        if (!IsMasterUser())
        {
            _awaitingAnswer = false;
            SetPanelVisible(false);
            gameObject.SetActive(false);
            return;
        }

        Canvas canvas = GetComponent<Canvas>();
        if (canvas != null && canvas.worldCamera == null)
        {
            Camera cam = Camera.main;
            if (cam != null)
                canvas.worldCamera = cam;
        }
    }

    public void ShowTranscript(string transcript)
    {
        if (!IsMasterUser())
        {
            _awaitingAnswer = false;
            gameObject.SetActive(false);
            return;
        }

        SetWholeDialogVisible(true);

        if (messageText != null)
            messageText.text = $"You said:\n\"{transcript}\"\n\nProcessing...";

        SetPanelVisible(true);
        SetButtonsInteractable(false);
    }

    public void Show(string sessionId, string message, Action<string> onExecute)
    {
        if (!IsMasterUser())
        {
            _awaitingAnswer = false;
            _pendingSessionId = null;
            _onExecute = null;
            gameObject.SetActive(false);
            return;
        }

        SetWholeDialogVisible(true);

        _pendingSessionId = sessionId;
        _onExecute = onExecute;
        _awaitingAnswer = true;

        if (messageText != null)
            messageText.text = FormatDialogMessage(message);

        SetPanelVisible(true);
        SetButtonsInteractable(true);
    }

    public bool IsPanelVisible => IsMasterUser() && _awaitingAnswer;

    public void Hide()
    {
        _awaitingAnswer = false;

        if (!IsMasterUser())
        {
            _pendingSessionId = null;
            _onExecute = null;
            gameObject.SetActive(false);
            return;
        }

        if (messageText != null)
            messageText.text = idleMessage;

        SetPanelVisible(true);
        SetButtonsInteractable(false);
    }


    private string FormatDialogMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "Confirm action?";

        string s = message.Replace("\r", " ").Replace("\n", " ").Trim();

        while (s.Contains("  "))
            s = s.Replace("  ", " ");

        const int maxChars = 160;
        if (s.Length > maxChars)
            s = s.Substring(0, maxChars - 3).TrimEnd() + "...";

        return s;
    }

    public void OnYes()
    {
        if (!IsMasterUser())
            return;

        if (!_awaitingAnswer)
            return;

        _awaitingAnswer = false;
        SetButtonsInteractable(false);

        if (messageText != null)
            messageText.text = "Executing...";

        // Keeps your existing confirmation callback behavior.
        _onExecute?.Invoke("confirmed");

        if (!string.IsNullOrEmpty(_pendingSessionId))
        {
            StartCoroutine(PostExecute(_pendingSessionId));
        }
        else
        {
            if (messageText != null)
                messageText.text = "Error: missing session id.";

            Hide();
        }
    }

    public void OnNo()
    {
        if (!IsMasterUser())
            return;

        if (!_awaitingAnswer)
            return;

        _awaitingAnswer = false;

        if (messageText != null)
            messageText.text = idleMessage;

        SetButtonsInteractable(false);

        if (!string.IsNullOrEmpty(_pendingSessionId))
            StartCoroutine(PostCancel(_pendingSessionId));

        _pendingSessionId = null;
        _onExecute = null;
    }

    private IEnumerator PostExecute(string sessionId)
    {
        string url = $"{serverUrl}/execute";
        string json = "{\"session_id\":\"" + sessionId + "\"}";

        using (var req = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json);
            req.uploadHandler = new UploadHandlerRaw(bodyRaw);
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError("[ConfirmationDialog] Execute failed: " + req.error);

                if (messageText != null)
                    messageText.text = "Execution failed.";

                _onExecute?.Invoke(null);
                yield break;
            }

            string responseText = req.downloadHandler.text;
            Debug.Log("[ConfirmationDialog] Execute response: " + responseText);

            _onExecute?.Invoke(responseText);

            _pendingSessionId = null;
            _onExecute = null;

            Hide();
        }
    }

    private IEnumerator PostCancel(string sessionId)
    {
        string body = $"{{\"session_id\":\"{sessionId}\"}}";

        using (var req = new UnityWebRequest($"{serverUrl}/cancel", "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(body));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");

            yield return req.SendWebRequest();

            Debug.Log($"[Dialog] /cancel → {req.downloadHandler.text}");
        }
    }

    private void SetButtonsInteractable(bool state)
    {
        if (yesButton != null)
            yesButton.interactable = state;

        if (noButton != null)
            noButton.interactable = state;
    }
}
