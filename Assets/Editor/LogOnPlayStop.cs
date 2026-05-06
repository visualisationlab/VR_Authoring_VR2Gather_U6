using UnityEditor;
using UnityEngine;
using System;
using System.IO;

[InitializeOnLoad]
public static class LogOnPlayStop
{
    static LogOnPlayStop()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingPlayMode)
        {
            SaveLogs();
        }
    }

    private static void SaveLogs()
    {
        try
        {
            string baseDir = @"C:\Users\Ashutosh\Desktop\Logs_Authoring";
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string targetDir = Path.Combine(baseDir, timestamp);
            Directory.CreateDirectory(targetDir);

            string editorLog = @"C:\Users\Ashutosh\AppData\Local\Unity\Editor\Editor.log";
            string currentServerLog = Path.Combine(baseDir, "current_server_log.txt");

            if (File.Exists(editorLog))
                File.Copy(editorLog, Path.Combine(targetDir, "Editor.log"), true);

            if (File.Exists(currentServerLog))
                File.Copy(currentServerLog, Path.Combine(targetDir, "server_log.txt"), true);

            Debug.Log("Logs copied to: " + targetDir);
        }
        catch (Exception e)
        {
            Debug.LogError("Failed to save logs: " + e.Message);
        }
    }
}