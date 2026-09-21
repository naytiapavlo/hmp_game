using HMProtection.Quiz;
using UnityEditor;
using UnityEngine;

public sealed class SceneChoicePreviewWindow : EditorWindow
{
    string questionId = "office_fire_first_action", result = "Waiting for selection.";
    SceneChoicePresenter presenter;
    [MenuItem("Tools/Scene Choices/Preview & Validate")]
    public static void Open() => GetWindow<SceneChoicePreviewWindow>("Scene Choices");
    void OnGUI()
    {
        EditorGUILayout.HelpBox("Open Office and enter Play mode. This preview uses the current live scene.", MessageType.Info);
        questionId = EditorGUILayout.TextField("Question ID", questionId);
        if (!EditorApplication.isPlaying) { EditorGUILayout.LabelField("Enter Play mode to preview."); return; }
        var found = FindFirstObjectByType<SceneChoicePresenter>();
        if (found != presenter) { Unsubscribe(); presenter = found; if (presenter != null) presenter.SelectionSubmitted += Selected; }
        if (presenter == null) { EditorGUILayout.HelpBox("Scene Choices is not installed in this scene.", MessageType.Warning); return; }
        if (GUILayout.Button("Reload JSON & Preview")) Show(questionId);
        if (GUILayout.Button("Play Fire Question → Route"))
        {
            var flow = presenter.GetComponent<OfficeFireChoiceFlow>();
            if (flow != null) { flow.CancelQuestion(); presenter.ExitOverview(); questionId=flow.questionId; result="Waiting for selection."; flow.BeginQuestion(); }
        }
        EditorGUILayout.BeginHorizontal(); foreach (int n in new[] {2,4,6}) if (GUILayout.Button(n + " options")) Show("office_choose_" + n); EditorGUILayout.EndHorizontal();
        if (GUILayout.Button("Hide Question")) { CancelFlow(); presenter.HideQuestion(); }
        if (GUILayout.Button("Exit Overview / Restore Player")) { CancelFlow(); presenter.ExitOverview(); }
        if (!string.IsNullOrEmpty(presenter.LastError)) EditorGUILayout.HelpBox(presenter.LastError, MessageType.Error);
        foreach (string warning in presenter.GetLayoutWarnings()) EditorGUILayout.HelpBox(warning, MessageType.Warning);
        EditorGUILayout.TextArea(result, GUILayout.MinHeight(110));
    }
    void CancelFlow() { if(presenter!=null) presenter.GetComponent<OfficeFireChoiceFlow>()?.CancelQuestion(); }
    void Show(string id) { CancelFlow(); questionId = id; result = "Waiting for selection."; if (presenter.ReloadCatalog() && presenter.EnterOverview()) presenter.ShowQuestion(id); }
    void Selected(SceneChoiceResult r) { result = JsonUtility.ToJson(r, true); Repaint(); }
    void Unsubscribe() { if (presenter != null) presenter.SelectionSubmitted -= Selected; }
    void OnDisable() { Unsubscribe(); CancelFlow(); if (presenter != null) presenter.ExitOverview(); }
    void OnInspectorUpdate() => Repaint();
}
