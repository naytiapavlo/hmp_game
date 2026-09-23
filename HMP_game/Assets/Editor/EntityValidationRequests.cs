using System;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// File-only, opt-in editor request endpoint for the entity-system pilot.
/// Commands are deliberately narrow so an existing Unity editor can validate without being closed.
/// </summary>
[InitializeOnLoad]
public static class EntityValidationRequests
{
    private const string RequestPath = "Library/EntityValidation.request";
    private const string CheckDirectory = "Library/EntityChecks";
    private const string StatusPath = CheckDirectory + "/EntityValidation.status";
    private static bool executing;
    private static bool queued;

    static EntityValidationRequests() => EditorApplication.update += Poll;

    private static void Poll()
    {
        if (executing || queued || !File.Exists(RequestPath)) return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            WriteStatus("WAITING: Unity is compiling or updating.");
            return;
        }
        string command;
        try { command = File.ReadAllText(RequestPath).Trim(); }
        catch (Exception exception) { WriteStatus("FAILED: could not read request: " + exception.Message); return; }
        if (string.IsNullOrEmpty(command)) { ConsumeRequest(); WriteStatus("BLOCKED: request is empty."); return; }
        queued = true;
        EditorApplication.delayCall += () => Execute(command);
    }

    private static void Execute(string command)
    {
        queued = false;
        if (executing) return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
        try
        {
            if (HasDirtyLoadedScene())
            {
                ConsumeRequest();
                WriteStatus("BLOCKED: a loaded scene has unsaved changes. No scene was saved or changed.");
                return;
            }
            ConsumeRequest();
            if (string.Equals(command, "install", StringComparison.OrdinalIgnoreCase))
            {
                executing = true;
                WriteStatus("RUNNING: installing isolated office entity pilot.");
                EntitySetup.InstallOfficePilotBatch();
                WriteStatus("PASS: office pilot installed and validated at Assets/Scenes/办公室场景-实体试点.unity.");
                executing = false;
                return;
            }
            WriteStatus("BLOCKED: unknown request '" + command + "'. Allowed: install.");
        }
        catch (Exception exception)
        {
            executing = false;
            WriteStatus("FAILED: " + exception);
            Debug.LogException(exception);
        }
    }

    private static bool HasDirtyLoadedScene()
    {
        for (int index = 0; index < UnityEngine.SceneManagement.SceneManager.sceneCount; index++)
            if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(index).isDirty) return true;
        return false;
    }

    private static void WriteStatus(string status)
    {
        Directory.CreateDirectory(CheckDirectory);
        File.WriteAllText(StatusPath, DateTime.UtcNow.ToString("o") + " " + status);
    }

    private static void ConsumeRequest()
    {
        if (File.Exists(RequestPath)) File.Delete(RequestPath);
    }
}
