using HMProtection.Entities;
using UnityEditor;
using UnityEngine;

namespace HMProtection.Entities.Editor
{
    [CustomEditor(typeof(GameEntity))]
    [CanEditMultipleObjects]
    public sealed class GameEntityEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var entity = (GameEntity)target;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Entity authoring checks", EditorStyles.boldLabel);
            if (string.IsNullOrWhiteSpace(entity.Id.Value))
                EditorGUILayout.HelpBox("An entity ID is required before this object can be installed.", MessageType.Error);
            else
                EditorGUILayout.LabelField("ID", entity.Id.Value);

            if (GUILayout.Button("Validate Active Scene"))
                EntitySceneValidator.ValidateActiveSceneMenu();
        }
    }
}
