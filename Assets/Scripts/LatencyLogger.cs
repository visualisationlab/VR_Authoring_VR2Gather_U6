using System;
using System.Globalization;
using System.IO;
using UnityEngine;

public static class LatencyLogger
{
    private static string _filePath;

    public static string FilePath
    {
        get
        {
            EnsureFile();
            return _filePath;
        }
    }

    private static void EnsureFile()
    {
        if (!string.IsNullOrEmpty(_filePath))
            return;

        string folder = Path.Combine(
            Application.persistentDataPath,
            "LatencyLogs"
        );

        Directory.CreateDirectory(folder);

        string fileName =
            $"latency_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv";

        _filePath = Path.Combine(folder, fileName);

        string header =
            "timestamp,command_id,transcript,action,target," +
            "whisper_ms,vision_ms,llm_ms,server_total_ms," +
            "http_roundtrip_ms,confirmation_wait_ms," +
            "dispatch_ms,local_total_ms,sync_ms,success\n";

        File.WriteAllText(_filePath, header);

        Debug.Log("[LATENCY] Log file: " + _filePath);
    }

    public static void Log(
        int commandId,
        string transcript,
        string action,
        string target,
        long whisperMs,
        long visionMs,
        long llmMs,
        long serverTotalMs,
        long httpRoundtripMs,
        long confirmationWaitMs,
        long dispatchMs,
        long localTotalMs,
        long syncMs,
        bool success)
    {
        EnsureFile();

        string line = string.Join(",",
            Escape(DateTime.Now.ToString(
                "yyyy-MM-dd HH:mm:ss.fff",
                CultureInfo.InvariantCulture)),
            commandId.ToString(),
            Escape(transcript),
            Escape(action),
            Escape(target),
            whisperMs.ToString(),
            visionMs.ToString(),
            llmMs.ToString(),
            serverTotalMs.ToString(),
            httpRoundtripMs.ToString(),
            confirmationWaitMs.ToString(),
            dispatchMs.ToString(),
            localTotalMs.ToString(),
            syncMs.ToString(),
            success ? "1" : "0"
        );

        File.AppendAllText(_filePath, line + Environment.NewLine);
    }

    private static string Escape(string value)
    {
        if (value == null)
            return "\"\"";

        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}