using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;

/// <summary>
/// From VoiceManager call:
///   ConfirmationDialog.Instance.ShowTranscript(transcript)   ← live text while LLM thinks
///   ConfirmationDialog.Instance.Show(sessionId, msg, cb)     ← show YES/NO dialog
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

    private string _pendingSessionId;
    private Action<string> _onExecute;
    private bool _awaitingAnswer = false;

    [Header("Idle State")]
    public string idleMessage = "Press A and speak a command...";

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

        if (dialogPanel != null) dialogPanel.SetActive(true);
        if (messageText != null) messageText.text = idleMessage;
        SetButtonsInteractable(false);

        if (yesButton != null) yesButton.onClick.AddListener(OnYes);
        if (noButton != null) noButton.onClick.AddListener(OnNo);
    }

    void Start()
    {
        Canvas canvas = GetComponent<Canvas>();
        if (canvas != null && canvas.worldCamera == null)
        {
            Camera cam = Camera.main;
            if (cam != null) canvas.worldCamera = cam;
        }
    }

    public void ShowTranscript(string transcript)
    {
        if (messageText != null)
            messageText.text = $"You said:\n\"{transcript}\"\n\nProcessing...";

        if (dialogPanel != null) dialogPanel.SetActive(true);
        SetButtonsInteractable(false);
    }

    public void Show(string sessionId, string message, Action<string> onExecute)
    {
        _pendingSessionId = sessionId;
        _onExecute = onExecute;
        _awaitingAnswer = true;

        if (messageText != null)
            messageText.text = message;

        if (dialogPanel != null)
            dialogPanel.SetActive(true);

        SetButtonsInteractable(true);
    }

    public bool IsPanelVisible => _awaitingAnswer;

    public void Hide()
    {
        _awaitingAnswer = false;
        if (messageText != null) messageText.text = idleMessage;
        SetButtonsInteractable(false);
    }

    /*public void OnYes()
    {
        if (!_awaitingAnswer) return;

        _awaitingAnswer = false;
        SetButtonsInteractable(false);

        if (messageText != null)
            messageText.text = "Executing...";

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
    }*/

    public void OnYes()
    {
        if (!_awaitingAnswer) return;

        _awaitingAnswer = false;
        SetButtonsInteractable(false);

        if (messageText != null)
            messageText.text = "Executing...";

        // ✅ CRITICAL FIX
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
        if (!_awaitingAnswer) return;

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
        if (yesButton != null) yesButton.interactable = state;
        if (noButton != null) noButton.interactable = state;
    }
}