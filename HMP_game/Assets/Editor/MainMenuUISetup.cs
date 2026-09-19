using System;
using System.IO;
using System.Linq;
using HMProtection.UI;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>Explicit, one-shot installer; never rebuilds a scene on ordinary imports.</summary>
[InitializeOnLoad]
public static class MainMenuUISetup
{
    private const string ScenePath = "Assets/Scenes/初始界面.unity";
    private const string Request = "Library/HMPMainMenu.request";
    private const string Report = "Library/HMPMainMenuReport.json";
    private const string Folder = "Assets/UI/MainMenu";
    private static double nextPoll;

    static MainMenuUISetup() { EditorApplication.update += Poll; }

    private static void Poll()
    {
        if (EditorApplication.timeSinceStartup < nextPoll) return;
        nextPoll = EditorApplication.timeSinceStartup + .5;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || !File.Exists(Request)) return;
        File.Delete(Request);
        try { Install(); }
        catch (Exception error)
        {
            File.WriteAllText(Report, JsonUtility.ToJson(new Result { error = error.ToString() }, true));
            Debug.LogException(error);
        }
    }

    [MenuItem("Tools/HM Protection/Create Main Menu UI")]
    public static void Install()
    {
        Scene scene = SceneManager.GetActiveScene();
        if (EditorApplication.isPlayingOrWillChangePlaymode || scene.path != ScenePath)
            throw new InvalidOperationException("Open 初始界面 in Edit Mode before installing the main menu.");
        if (scene.GetRootGameObjects().Any(g => g.GetComponentInChildren<MainMenuView>(true) != null))
            throw new InvalidOperationException("Main menu already exists. Edit its existing objects instead of adding a duplicate.");

        var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
        if (font == null) throw new InvalidOperationException("The project's licensed LiberationSans SDF font is required.");
        Directory.CreateDirectory("Library/MainMenuUIBackup");
        // Preserve the LIVE scene, including the user's unsaved camera/background adjustments.
        string backup = "Library/MainMenuUIBackup/BeforeMainMenu-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".unity";
        if (!EditorSceneManager.SaveScene(scene, backup, true)) throw new IOException("Could not back up active scene.");

        string backgroundBefore = BackgroundSnapshot(scene);
        Directory.CreateDirectory(Folder);
        AssetDatabase.Refresh();
        Material titleMaterial = TextMaterial(font, "Title", .20f, .07f, .75f);
        Material bodyMaterial = TextMaterial(font, "Body", .01f, .025f, .45f);

        Undo.IncrementCurrentGroup();
        int undoGroup = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Create Fire Safety Main Menu");

        var root = new GameObject("MainMenuUI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(MainMenuView));
        Undo.RegisterCreatedObjectUndo(root, "Create main menu");
        root.layer = 5;
        var canvas = root.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 20;
        var scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        scaler.referencePixelsPerUnit = 100;
        var view = root.GetComponent<MainMenuView>();

        RectTransform layout = Rect("MenuLayout", root.transform, 0, 0, 1920, 1080);
        layout.anchorMin = layout.anchorMax = new Vector2(0, .5f);
        layout.pivot = new Vector2(0, .5f);
        layout.anchoredPosition = Vector2.zero;

        Label("Company", layout, "hmprotection inc", 108, 98, 820, 48, 33, font, bodyMaterial, false, new Color(.91f, .91f, .91f));
        Label("TitleLine1", layout, "FIRE SAFETY", 102, 152, 1070, 160, 116, font, titleMaterial, true, Color.white);
        Label("TitleLine2", layout, "TRAINING", 102, 262, 1000, 160, 116, font, titleMaterial, true, Color.white);
        Label("Subtitle", layout, "Initial Fire Prevention & Emergency Response", 111, 409, 1330, 53, 35, font, bodyMaterial, false, new Color(.92f, .92f, .92f));

        Button[] buttons = {
            MenuButton("StartTraining", "Start Training", layout, 494, 96, MenuButtonGraphic.ButtonStyle.Primary, font, bodyMaterial, 46),
            MenuButton("TrainingRecords", "Training Records", layout, 606, 84, MenuButtonGraphic.ButtonStyle.Secondary, font, bodyMaterial, 40),
            MenuButton("DeviceSettings", "Device Settings", layout, 706, 84, MenuButtonGraphic.ButtonStyle.Secondary, font, bodyMaterial, 40),
            MenuButton("HowToPlay", "How to Play", layout, 806, 84, MenuButtonGraphic.ButtonStyle.Secondary, font, bodyMaterial, 40),
            MenuButton("Exit", "Exit", layout, 906, 84, MenuButtonGraphic.ButtonStyle.Quiet, font, bodyMaterial, 40)
        };
        UnityEventTools.AddPersistentListener(buttons[0].onClick, view.StartTraining);
        UnityEventTools.AddPersistentListener(buttons[1].onClick, view.TrainingRecords);
        UnityEventTools.AddPersistentListener(buttons[2].onClick, view.DeviceSettings);
        UnityEventTools.AddPersistentListener(buttons[3].onClick, view.HowToPlay);
        UnityEventTools.AddPersistentListener(buttons[4].onClick, view.Exit);
        for (int i = 0; i < buttons.Length; i++)
        {
            buttons[i].navigation = new Navigation { mode = Navigation.Mode.Explicit,
                selectOnUp = buttons[(i + buttons.Length - 1) % buttons.Length], selectOnDown = buttons[(i + 1) % buttons.Length] };
        }
        Chevron(buttons[0].transform);

        // Keep any custom UI intact. Only hide the known, unfinished TMP placeholder.
        foreach (var existingRoot in scene.GetRootGameObjects())
        {
            if (existingRoot == root) continue;
            foreach (var text in existingRoot.GetComponentsInChildren<TextMeshProUGUI>(true))
                if (text.name == "Text (TMP)" && text.text == "New Text")
                {
                    Undo.RecordObject(text.gameObject, "Hide empty menu placeholder");
                    text.gameObject.SetActive(false);
                }
        }

        var eventSystem = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<EventSystem>(true)).FirstOrDefault(e => e.isActiveAndEnabled);
        if (eventSystem == null)
        {
            var eventObject = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            Undo.RegisterCreatedObjectUndo(eventObject, "Create UI input");
            eventSystem = eventObject.GetComponent<EventSystem>();
            eventObject.GetComponent<InputSystemUIInputModule>().AssignDefaultActions();
        }
        else if (eventSystem.GetComponent<InputSystemUIInputModule>() == null)
        {
            foreach (var oldInput in eventSystem.GetComponents<BaseInputModule>())
            {
                Undo.RecordObject(oldInput, "Use Input System UI");
                oldInput.enabled = false;
            }
            Undo.AddComponent<InputSystemUIInputModule>(eventSystem.gameObject).AssignDefaultActions();
        }
        Undo.RecordObject(eventSystem, "Set initial menu selection");
        eventSystem.firstSelectedGameObject = buttons[0].gameObject;

        Canvas.ForceUpdateCanvases();
        foreach (var text in root.GetComponentsInChildren<TextMeshProUGUI>()) text.ForceMeshUpdate();
        string backgroundAfter = BackgroundSnapshot(scene);
        if (backgroundBefore != backgroundAfter) throw new InvalidOperationException("Background validation failed.");

        AssetDatabase.SaveAssets();
        PrefabUtility.SaveAsPrefabAssetAndConnect(root, Folder + "/MainMenuUI.prefab", InteractionMode.AutomatedAction);
        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveScene(scene);
        Undo.CollapseUndoOperations(undoGroup);
        Selection.activeGameObject = root;
        EditorApplication.RepaintHierarchyWindow();
        File.WriteAllText(Report, JsonUtility.ToJson(new Result {
            success = saved, scene = scene.path, backup = backup, backgroundUnchanged = backgroundBefore == backgroundAfter,
            buttons = buttons.Length, labels = root.GetComponentsInChildren<TextMeshProUGUI>().Select(t => t.text).ToArray(),
            textOverflow = root.GetComponentsInChildren<TextMeshProUGUI>().Where(t => t.isTextOverflowing).Select(t => t.name).ToArray(),
            inputModule = eventSystem.currentInputModule != null ? eventSystem.currentInputModule.GetType().Name : "InputSystemUIInputModule"
        }, true));
        Debug.Log("HM Protection: main menu saved in active scene. Background unchanged. Report: " + Report);
    }

    private static RectTransform Rect(string name, Transform parent, float x, float y, float width, float height)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = 5;
        var r = (RectTransform)go.transform;
        r.SetParent(parent, false);
        r.anchorMin = r.anchorMax = new Vector2(0, 1);
        r.pivot = new Vector2(0, 1);
        r.anchoredPosition = new Vector2(x, -y);
        r.sizeDelta = new Vector2(width, height);
        return r;
    }

    private static TextMeshProUGUI Label(string name, Transform parent, string value, float x, float y,
        float width, float height, float size, TMP_FontAsset font, Material material, bool bold, Color colour)
    {
        var text = Rect(name, parent, x, y, width, height).gameObject.AddComponent<TextMeshProUGUI>();
        text.font = font;
        text.fontSharedMaterial = material;
        text.text = value;
        text.fontSize = size;
        text.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
        text.color = colour;
        text.alignment = TextAlignmentOptions.MidlineLeft;
        text.textWrappingMode = TextWrappingModes.NoWrap;
        text.overflowMode = TextOverflowModes.Overflow;
        text.raycastTarget = false;
        return text;
    }

    private static Button MenuButton(string name, string label, Transform parent, float y, float height,
        MenuButtonGraphic.ButtonStyle style, TMP_FontAsset font, Material material, float fontSize)
    {
        var r = Rect(name, parent, 119, y, 715, height);
        var graphic = r.gameObject.AddComponent<MenuButtonGraphic>();
        graphic.Style = style;
        var shadow = r.gameObject.AddComponent<Shadow>();
        shadow.effectColor = new Color(0, 0, 0, .30f);
        shadow.effectDistance = new Vector2(2, -5);
        var button = r.gameObject.AddComponent<Button>();
        button.targetGraphic = graphic;
        ColorBlock colours = ColorBlock.defaultColorBlock;
        colours.normalColor = Color.white;
        colours.highlightedColor = new Color(1.23f, 1.23f, 1.23f);
        colours.selectedColor = new Color(1.12f, 1.12f, 1.12f);
        colours.pressedColor = new Color(.72f, .72f, .72f);
        colours.disabledColor = new Color(.5f, .5f, .5f, .55f);
        colours.fadeDuration = .10f;
        button.colors = colours;
        Label("Label", r, label, 49, 0, 600, height, fontSize, font, material, true, Color.white);
        return button;
    }

    private static void Chevron(Transform parent)
    {
        var r = Rect("Chevron", parent, 641, 30, 30, 36);
        for (int i = 0; i < 2; i++)
        {
            var stroke = Rect("Stroke" + i, r, 0, 0, 8, 25);
            stroke.anchorMin = stroke.anchorMax = stroke.pivot = new Vector2(.5f, .5f);
            stroke.anchoredPosition = new Vector2(0, i == 0 ? 7.1f : -7.1f);
            stroke.localRotation = Quaternion.Euler(0, 0, i == 0 ? 45 : -45);
            stroke.gameObject.AddComponent<Image>().raycastTarget = false;
        }
    }

    private static Material TextMaterial(TMP_FontAsset font, string name, float dilate, float outline, float shadow)
    {
        string path = Folder + "/" + name + ".mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material != null) return material;
        material = new Material(font.material) { name = "MainMenu " + name };
        material.SetFloat(ShaderUtilities.ID_FaceDilate, dilate);
        material.SetFloat(ShaderUtilities.ID_OutlineWidth, outline);
        material.SetColor(ShaderUtilities.ID_OutlineColor, new Color(.08f, .09f, .1f, .75f));
        material.EnableKeyword("OUTLINE_ON");
        material.EnableKeyword("UNDERLAY_ON");
        material.SetColor(ShaderUtilities.ID_UnderlayColor, new Color(0, 0, 0, shadow));
        material.SetFloat(ShaderUtilities.ID_UnderlayOffsetX, .65f);
        material.SetFloat(ShaderUtilities.ID_UnderlayOffsetY, -.65f);
        material.SetFloat(ShaderUtilities.ID_UnderlaySoftness, .3f);
        AssetDatabase.CreateAsset(material, path);
        return material;
    }

    private static string BackgroundSnapshot(Scene scene)
    {
        // Cameras, lights, volumes and all non-UI transforms must remain byte-for-byte unchanged.
        return string.Join("\n", scene.GetRootGameObjects()
            .Where(g => g.GetComponentInChildren<Canvas>(true) == null && g.GetComponentInChildren<EventSystem>(true) == null)
            .SelectMany(g => g.GetComponentsInChildren<Component>(true))
            .Where(c => c != null)
            .Select(c => c.GetType().FullName + ":" + EditorJsonUtility.ToJson(c)));
    }

    [Serializable] private sealed class Result
    {
        public bool success;
        public string scene;
        public string backup;
        public bool backgroundUnchanged;
        public int buttons;
        public string[] labels;
        public string[] textOverflow;
        public string inputModule;
        public string[] graphics;
        public string error;
    }
}
