using UnityEngine;
using UnityEngine.XR;
using TMPro;
using System.Collections.Generic;

public class WristMenuFollower : MonoBehaviour
{
    [Header("References")]
    public Transform hmdCamera;
    public GameObject menuRoot;

    [Header("Dialog Text Fields")]
    public TMP_Text recordingStatusText;
    public TMP_Text transcriptText;
    public TMP_Text aiIntentText;

    [Header("Placement In Front Of User")]
    public float distanceInFront = 1.0f;
    public float heightBelowEyes = 0.25f;
    public float sideOffset = 0.0f;

    private InputDevice leftController;
    private bool menuVisible = false;
    private bool previousXButtonState = false;
    private float nextDebugTime = 0f;

    void Start()
    {
        BindCameraIfNeeded();
        BindMenuRootIfNeeded();

        if (menuRoot != null)
            menuRoot.SetActive(false);

        SetRecordingStatus("Recording status will appear here");
        SetTranscript("Transcript text will appear here");
        SetAIIntent("AI intent text will appear here");
    }

    void Update()
    {
        BindCameraIfNeeded();

        if (hmdCamera == null)
        {
            DebugEverySecond("[WristMenu] No HMD camera found.");
            return;
        }

        if (!leftController.isValid)
            TryFindLeftController();

        if (!leftController.isValid)
        {
            DebugEverySecond("[WristMenu] No left controller found.");
            return;
        }

        HandleXButtonToggle();
    }

    private void HandleXButtonToggle()
    {
        bool xButtonPressed = false;

        leftController.TryGetFeatureValue(
            CommonUsages.primaryButton,
            out xButtonPressed
        );

        if (xButtonPressed && !previousXButtonState)
        {
            ToggleMenu();
        }

        previousXButtonState = xButtonPressed;
    }

    private void ToggleMenu()
    {
        menuVisible = !menuVisible;

        if (menuRoot != null)
            menuRoot.SetActive(menuVisible);

        if (menuVisible)
        {
            PlaceOnceInFrontOfUser();
            Debug.Log("[WristMenu] SHOW by X button");
        }
        else
        {
            Debug.Log("[WristMenu] HIDE by X button");
        }
    }

    private void PlaceOnceInFrontOfUser()
    {
        Vector3 targetPos =
            hmdCamera.position
            + hmdCamera.forward * distanceInFront
            + hmdCamera.right * sideOffset
            - Vector3.up * heightBelowEyes;

        transform.position = targetPos;

        Vector3 lookDirection = transform.position - hmdCamera.position;
        transform.rotation = Quaternion.LookRotation(lookDirection, Vector3.up);
    }

    public void SetRecordingStatus(string message)
    {
        if (recordingStatusText != null)
            recordingStatusText.text = message;
    }

    public void SetTranscript(string message)
    {
        if (transcriptText != null)
            transcriptText.text = message;
    }

    public void SetAIIntent(string message)
    {
        if (aiIntentText != null)
            aiIntentText.text = message;
    }

    private void TryFindLeftController()
    {
        List<InputDevice> devices = new List<InputDevice>();

        InputDevices.GetDevicesWithCharacteristics(
            InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller,
            devices
        );

        if (devices.Count > 0)
        {
            leftController = devices[0];
            Debug.Log("[WristMenu] Found left controller: " + leftController.name);
        }
    }

    private void BindCameraIfNeeded()
    {
        if (hmdCamera != null) return;

        Camera cam = Camera.main ?? FindFirstObjectByType<Camera>();

        if (cam != null)
            hmdCamera = cam.transform;
    }

    private void BindMenuRootIfNeeded()
    {
        if (menuRoot != null) return;

        Transform panel = transform.Find("Panel");

        if (panel != null)
            menuRoot = panel.gameObject;
        else
            Debug.LogWarning("[WristMenu] No Panel child found. Please assign menuRoot manually.");
    }

    private void DebugEverySecond(string msg)
    {
        if (Time.time >= nextDebugTime)
        {
            nextDebugTime = Time.time + 1f;
            Debug.Log(msg);
        }
    }
}