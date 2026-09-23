using System.Collections.Generic;
using System.Linq;
using HMProtection.Entities;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HMProtection.Entities.Editor
{
    /// <summary>Read-only authoring/runtime snapshot. Refresh is explicit so the window never polls a scene.</summary>
    public sealed class EntityDiagnosticsWindow : EditorWindow
    {
        private Vector2 scroll;
        private List<EntitySceneValidator.Finding> findings = new List<EntitySceneValidator.Finding>();
        private List<HostSnapshot> hosts = new List<HostSnapshot>();

        [MenuItem("Tools/Entity System/Diagnostics")]
        public static void ShowWindow()
        {
            var window = GetWindow<EntityDiagnosticsWindow>("Entity Diagnostics");
            window.Refresh();
        }

        private void OnFocus() => Refresh();

        private void Refresh()
        {
            var scene = SceneManager.GetActiveScene();
            findings = EntitySceneValidator.Validate(scene);
            hosts = scene.IsValid() && scene.isLoaded
                ? scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<EntityScopeHost>(true))
                    .Select(CreateSnapshot).ToList()
                : new List<HostSnapshot>();
            Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Active Scene", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(SceneManager.GetActiveScene().path);
            if (GUILayout.Button("Refresh (read-only)")) Refresh();
            EditorGUILayout.Space();

            scroll = EditorGUILayout.BeginScrollView(scroll);
            DrawScopeSnapshots();
            DrawFindings();
            EditorGUILayout.EndScrollView();
        }

        private void DrawScopeSnapshots()
        {
            EditorGUILayout.LabelField("Scope entities", EditorStyles.boldLabel);
            if (hosts.Count == 0)
            {
                EditorGUILayout.HelpBox("No EntityScopeHost components in the active scene.", MessageType.Info);
                return;
            }

            foreach (var snapshot in hosts)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(snapshot.Host.name, EditorStyles.boldLabel);
                DrawSelect(snapshot.Host);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.LabelField("Scope ID", snapshot.Scope == null ? "<not initialized>" : snapshot.Scope.ScopeId);
                EditorGUILayout.LabelField("Ready", snapshot.Scope != null && snapshot.Scope.IsReady ? "true" : "false");
                EditorGUILayout.LabelField("Content roots", snapshot.RootCount.ToString());

                if (snapshot.Entities.Count == 0)
                    EditorGUILayout.HelpBox("No GameEntity found under this host's explicit content roots.", MessageType.Info);
                foreach (var entity in snapshot.Entities)
                    DrawEntity(snapshot.Scope, entity);
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(4);
            }
        }

        private static void DrawEntity(EntityScope scope, GameEntity entity)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(string.IsNullOrWhiteSpace(entity.Id.Value) ? "<empty entity ID>" : entity.Id.Value, EditorStyles.boldLabel);
            DrawSelect(entity);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.LabelField("Tags", entity.Tags == null || entity.Tags.Count == 0 ? "<none>" : string.Join(", ", entity.Tags));
            EditorGUILayout.LabelField("Capability keys", entity.Capabilities == null || entity.Capabilities.Count == 0
                ? "<none>"
                : string.Join(", ", entity.Capabilities.Select(capability => capability == null ? "<missing>" : capability.Key)));
            EditorGUILayout.LabelField("Active", "self=" + entity.ActivationTarget.activeSelf + "  hierarchy=" + entity.ActivationTarget.activeInHierarchy);
            bool handleValid = scope != null && !scope.IsDisposed && scope.Registry.IsValid(entity.Handle);
            EditorGUILayout.LabelField("Handle", entity.Handle.IsValid ? entity.Handle.ToString() : "<not registered>");
            EditorGUILayout.LabelField("Handle valid", handleValid ? "true" : "false");
            EditorGUILayout.EndVertical();
        }

        private void DrawFindings()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Validation findings", EditorStyles.boldLabel);
            if (findings.Count == 0)
            {
                EditorGUILayout.HelpBox("No entity authoring findings.", MessageType.Info);
                return;
            }
            foreach (var finding in findings)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox(finding.Message, finding.Severity);
                DrawSelect(finding.Context);
                EditorGUILayout.EndHorizontal();
            }
        }

        private static HostSnapshot CreateSnapshot(EntityScopeHost host)
        {
            var unique = new HashSet<GameEntity>();
            if (host.ContentRoots != null)
                foreach (var root in host.ContentRoots)
                    if (root != null)
                        foreach (var entity in root.GetComponentsInChildren<GameEntity>(true))
                            if (entity != null) unique.Add(entity);
            return new HostSnapshot(host, unique.OrderBy(entity => entity.Id.Value, System.StringComparer.Ordinal).ToList(), host.ContentRoots == null ? 0 : host.ContentRoots.Count);
        }

        private static void DrawSelect(UnityEngine.Object target)
        {
            if (target == null) return;
            if (!GUILayout.Button("Select", GUILayout.Width(56))) return;
            Selection.activeObject = target;
            EditorGUIUtility.PingObject(target);
        }

        private sealed class HostSnapshot
        {
            public readonly EntityScopeHost Host;
            public readonly EntityScope Scope;
            public readonly List<GameEntity> Entities;
            public readonly int RootCount;
            public HostSnapshot(EntityScopeHost host, List<GameEntity> entities, int rootCount)
            { Host = host; Scope = host.Scope; Entities = entities; RootCount = rootCount; }
        }
    }
}
