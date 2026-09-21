using System;
using System.IO;
using System.Linq;
using HMProtection.Quiz;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[InitializeOnLoad]
public static class SceneChoiceSetup
{
    const string ScenePath = "Assets/Scenes/办公室场景.unity";
    const string ArtPath = "Assets/UI/SceneChoice/ChoiceBubble.png";
    const string PrefabPath = "Assets/UI/SceneChoice/SceneChoiceBubble.prefab";
    const string Marker = "Library/SceneChoiceImageSetup.v1.done";
    static SceneChoiceSetup() { EditorApplication.delayCall += Auto; }
    static void Auto()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) { EditorApplication.delayCall += Auto; return; }
        var existing = AssetDatabase.LoadAssetAtPath<SceneChoiceBubble>(PrefabPath);
        if (existing != null && existing.artwork.GetComponent<CanvasRenderer>() == null)
        {
            var contents = PrefabUtility.LoadPrefabContents(PrefabPath);
            try { contents.GetComponentInChildren<SceneChoiceArtwork>().gameObject.AddComponent<CanvasRenderer>(); PrefabUtility.SaveAsPrefabAsset(contents, PrefabPath); }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
        }
        if (File.Exists(Marker)) return;
        Install();
    }
    public static T Find<T>(Scene scene) where T : Component => scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<T>(true)).FirstOrDefault();
    [MenuItem("Tools/Scene Choices/Install in Office")]
    public static void Install()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        Scene office = SceneManager.GetSceneByPath(ScenePath);
        bool opened = !office.isLoaded;
        try
        {
            if (opened) office = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);
            if (office.isDirty) throw new InvalidOperationException("Save the office scene before installing scene choices.");
            if (Find<SceneChoicePresenter>(office) != null) { File.WriteAllText(Marker, "installed"); return; }
            var importer = (TextureImporter)AssetImporter.GetAtPath(ArtPath);
            if (importer == null) { AssetDatabase.ImportAsset(ArtPath); importer = (TextureImporter)AssetImporter.GetAtPath(ArtPath); }
            importer.textureType = TextureImporterType.Default; importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false; importer.textureCompression = TextureImporterCompression.Uncompressed; importer.maxTextureSize = 2048; importer.SaveAndReimport();
            var art = AssetDatabase.LoadAssetAtPath<Texture2D>(ArtPath);
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
            if (art == null || font == null) throw new InvalidOperationException("Missing supplied artwork or TMP font.");
            var prefab = BuildPrefab(art, font);
            var root = new GameObject("Scene Choices"); SceneManager.MoveGameObjectToScene(root, office);
            var view = root.AddComponent<QuizViewController>();
            view.ceiling = Find<CeilingVisibility>(office); view.character = Find<VisibilityToggle>(office);
            view.officeGeometry = office.GetRootGameObjects().First(g => g.name == "场景").transform;
            var cameraGo = new GameObject("Quiz Overview Camera", typeof(Camera), typeof(AudioListener)); cameraGo.transform.SetParent(root.transform, false);
            view.overviewCamera = cameraGo.GetComponent<Camera>(); view.overviewCamera.enabled = false; cameraGo.GetComponent<AudioListener>().enabled = false;
            cameraGo.AddComponent<UniversalAdditionalCameraData>().renderPostProcessing = false; view.FitCamera();
            var es = new GameObject("Quiz EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule)); es.transform.SetParent(root.transform, false);
            es.GetComponent<InputSystemUIInputModule>().AssignDefaultActions(); es.GetComponent<InputSystemUIInputModule>().deselectOnBackgroundClick = false;
            es.SetActive(false); view.fallbackEventSystem = es;
            var cg = new GameObject("Choice Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster)); cg.transform.SetParent(root.transform, false);
            var canvas = cg.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 700;
            var scaler = cg.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = .5f;
            var p = root.AddComponent<SceneChoicePresenter>(); p.viewController = view; p.bubblePrefab = prefab; p.canvas = canvas; p.bubbleContainer = (RectTransform)cg.transform;
            var prompt = Text("Question", cg.transform, font, 28); prompt.alignment = TextAlignmentOptions.Center;
            prompt.rectTransform.anchorMin = new Vector2(0,1); prompt.rectTransform.anchorMax = Vector2.one; prompt.rectTransform.pivot = new Vector2(.5f,1);
            prompt.rectTransform.offsetMin = new Vector2(60,-90); prompt.rectTransform.offsetMax = new Vector2(-60,-22); p.promptLabel = prompt;
            cg.SetActive(false);
            string[] ids = { "exit", "reception", "manager", "pantry", "workstation", "lounge" };
            string[] names = { "SM_Door_EntryLeft_01", "SM_Desk_Reception_01", "SM_Desk_Manager_01", "SM_Door_Pantry_01", "SM_Desk_01", "SM_Sofa_Reception_01" };
            var transforms = view.officeGeometry.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < ids.Length; i++)
            {
                var target = transforms.FirstOrDefault(t => t.name == names[i]);
                if (target == null) throw new InvalidOperationException("Example target not found: " + names[i]);
                var anchor = new GameObject("Choice Anchor - " + ids[i]); anchor.transform.SetParent(target, false);
                var renderer = target.GetComponentInChildren<Renderer>();
                anchor.transform.position = renderer != null ? renderer.bounds.center : target.position;
                anchor.AddComponent<SceneChoiceAnchor>().anchorId = "office." + ids[i];
            }
            EditorSceneManager.MarkSceneDirty(office); EditorSceneManager.SaveScene(office);
            File.WriteAllText(Marker, DateTime.UtcNow.ToString("o"));
            File.WriteAllText("Library/SceneChoiceImageSetup.txt", "SUCCESS: supplied PNG, shared prefab, six anchors and overview installed in office.");
        }
        catch (Exception e) { File.WriteAllText("Library/SceneChoiceImageSetup.txt", e.ToString()); Debug.LogException(e); }
        finally { if (opened && office.isLoaded && !office.isDirty) EditorSceneManager.CloseScene(office, true); }
    }
    static TextMeshProUGUI Text(string name, Transform parent, TMP_FontAsset font, float size)
    {
        var g = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI)); g.transform.SetParent(parent, false);
        var t = g.GetComponent<TextMeshProUGUI>(); t.font = font; t.fontSize = size; t.color = Color.white; t.richText = false; t.raycastTarget = false; return t;
    }
    static SceneChoiceBubble BuildPrefab(Texture2D art, TMP_FontAsset font)
    {
        var existing = AssetDatabase.LoadAssetAtPath<SceneChoiceBubble>(PrefabPath); if (existing != null) return existing;
        var go = new GameObject("SceneChoiceBubble", typeof(RectTransform), typeof(Image), typeof(Button), typeof(SceneChoiceBubble));
        try
        {
            var rect = (RectTransform)go.transform; rect.sizeDelta = new Vector2(400,90);
            var hit = go.GetComponent<Image>(); hit.color = Color.clear;
            var ag = new GameObject("Supplied PNG", typeof(RectTransform), typeof(CanvasRenderer), typeof(SceneChoiceArtwork)); ag.transform.SetParent(go.transform, false);
            var ar = (RectTransform)ag.transform; ar.anchorMin = Vector2.zero; ar.anchorMax = Vector2.one; ar.offsetMin = ar.offsetMax = Vector2.zero;
            var graphic = ag.GetComponent<SceneChoiceArtwork>(); graphic.artwork = art; graphic.raycastTarget = false; graphic.tailTip = new Vector2(190,-100);
            var b = go.GetComponent<SceneChoiceBubble>(); b.bodyRect = rect; b.button = go.GetComponent<Button>(); b.artwork = graphic;
            b.button.targetGraphic = graphic;
            var colors = b.button.colors; colors.normalColor = Color.white; colors.highlightedColor = new Color(1.15f,1.15f,1.15f,1); colors.selectedColor = colors.highlightedColor; colors.disabledColor = Color.white; colors.pressedColor = new Color(.9f,.9f,.9f,1); b.button.colors = colors;
            b.badge = Text("Letter", go.transform, font, 26); b.badge.alignment = TextAlignmentOptions.Center;
            b.badge.rectTransform.anchorMin = new Vector2(0,.5f); b.badge.rectTransform.anchorMax = new Vector2(0,.5f); b.badge.rectTransform.anchoredPosition = new Vector2(40,0); b.badge.rectTransform.sizeDelta = new Vector2(60,55);
            b.label = Text("Label", go.transform, font, 24); b.label.alignment = TextAlignmentOptions.MidlineLeft;
            b.label.rectTransform.anchorMin = Vector2.zero; b.label.rectTransform.anchorMax = Vector2.one; b.label.rectTransform.offsetMin = new Vector2(88,16); b.label.rectTransform.offsetMax = new Vector2(-22,-16);
            var shadow = b.label.gameObject.AddComponent<Shadow>(); shadow.effectColor = new Color(0,0,0,.7f); shadow.effectDistance = new Vector2(1,-2);
            return PrefabUtility.SaveAsPrefabAsset(go, PrefabPath).GetComponent<SceneChoiceBubble>();
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }
}
