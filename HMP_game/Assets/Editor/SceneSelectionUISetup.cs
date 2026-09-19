using System;
using System.IO;
using System.Linq;
using HMProtection.UI;
using TMPro;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[InitializeOnLoad]
public static class SceneSelectionUISetup
{
    private const string Request = "Library/HMPSceneSelection.request";
    private const string Report = "Library/HMPSceneSelectionReport.json";
    private const string Folder = "Assets/UI/SceneSelection";
    private static double nextCheck;
    private static TMP_FontAsset font;
    private static Material body;

    static SceneSelectionUISetup() { EditorApplication.update += Poll; }
    private static void Poll()
    {
        if (EditorApplication.timeSinceStartup < nextCheck) return;
        nextCheck = EditorApplication.timeSinceStartup + .5;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode || !File.Exists(Request)) return;
        File.Delete(Request);
        try { Install(); }
        catch (Exception e) { File.WriteAllText(Report, JsonUtility.ToJson(new Result { error = e.ToString() }, true)); Debug.LogException(e); }
    }

    [MenuItem("Tools/HM Protection/Create Scene Selection UI")]
    public static void Install()
    {
        var scene = SceneManager.GetActiveScene();
        if (scene.path != "Assets/Scenes/初始界面.unity" || EditorApplication.isPlayingOrWillChangePlaymode)
            throw new InvalidOperationException("Open 初始界面 in Edit Mode first.");
        var menu = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<MainMenuView>(true)).Single();
        if (scene.GetRootGameObjects().Any(g => g.GetComponent<SceneSelectUI>() != null))
            throw new InvalidOperationException("Scene selection already exists; edit the existing prefab.");
        font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
        body = AssetDatabase.LoadAssetAtPath<Material>("Assets/UI/MainMenu/Body.mat");
        if (font == null || body == null) throw new InvalidOperationException("Main-menu typography assets are required.");
        AssetDatabase.ImportAsset(Folder + "/OfficePreview.png", ImportAssetOptions.ForceSynchronousImport);
        var importer = (TextureImporter)AssetImporter.GetAtPath(Folder + "/OfficePreview.png");
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Single;
        importer.mipmapEnabled = false;
        importer.sRGBTexture = true;
        importer.maxTextureSize = 1024;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.SaveAndReimport();
        var officeImage = AssetDatabase.LoadAssetAtPath<Sprite>(Folder + "/OfficePreview.png");
        if (officeImage == null) throw new InvalidOperationException("Office image failed to import.");

        Directory.CreateDirectory("Library/SceneSelectionBackup");
        string backup = "Library/SceneSelectionBackup/BeforeSceneSelection-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".unity";
        if (!EditorSceneManager.SaveScene(scene, backup, true)) throw new IOException("Cannot back up the live scene.");
        string backgroundBefore = BackgroundSnapshot(scene);
        Undo.IncrementCurrentGroup();
        int undo = Undo.GetCurrentGroup();
        Undo.SetCurrentGroupName("Create scene selection window");

        var root = new GameObject("SceneSelectionUI", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster), typeof(SceneSelectUI));
        root.layer = 5;
        Undo.RegisterCreatedObjectUndo(root, "Create scene selection UI");
        root.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        root.GetComponent<Canvas>().sortingOrder = 30;
        var scaler = root.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;
        var controller = root.GetComponent<SceneSelectUI>();

        var content = Rect("WindowContent", root.transform, 0, 0, 0, 0);
        Stretch(content);
        var dim = content.gameObject.AddComponent<Image>();
        dim.color = new Color(0, 0, 0, .56f);
        var panel = Rect("Window", content, 0, 0, 1580, 880);
        panel.anchorMin = panel.anchorMax = panel.pivot = new Vector2(.5f, .5f);
        panel.anchoredPosition = Vector2.zero;
        var panelFace = panel.gameObject.AddComponent<MenuButtonGraphic>();
        panelFace.Style = MenuButtonGraphic.ButtonStyle.Quiet;
        panelFace.color = new Color(.63f, .64f, .65f);
        panelFace.raycastTarget = false;
        Solid("HeaderAccent", panel, 58, 26, 78, 4, Hex("EA111C"));
        Label("Company", panel, "hmprotection inc", 58, 42, 600, 32, 22, false, Hex("C6C7C9"));
        Label("Title", panel, "SELECT SCENE", 54, 79, 1000, 76, 54, true, Color.white);
        Label("Instruction", panel, "Choose a training environment.", 59, 162, 1080, 36, 26, false, Hex("C6C7C9"));
        controller.closeButton = Button("Close", "X", panel, 1458, 46, 64, 56, MenuButtonGraphic.ButtonStyle.Quiet, 26, true);

        var office = Card("Office", panel, 58, false);
        controller.officeCard = office.GetComponent<Button>();
        Solid("PreviewBacking", office, 14, 14, 436, 450, Hex("818487"));
        var preview = Rect("OfficePreview", office, 14, 14, 436, 450).gameObject.AddComponent<Image>();
        preview.sprite = officeImage;
        preview.preserveAspect = true;
        preview.rectTransform.pivot = new Vector2(.5f, .5f);
        preview.rectTransform.anchoredPosition = new Vector2(232, -239);
        preview.raycastTarget = false;
        Label("Available", office, "AVAILABLE", 26, 316, 280, 30, 19, true, Hex("F56368"));
        Label("SceneName", office, "OFFICE", 25, 351, 400, 54, 38, true, Color.white);
        Label("Description", office, "Office fire safety training", 26, 411, 412, 34, 23, false, Hex("CED0D2"));
        var badge = Rect("SelectedBadge", office, 276, 24, 160, 40);
        var badgeImage = badge.gameObject.AddComponent<MenuButtonGraphic>();
        badgeImage.Style = MenuButtonGraphic.ButtonStyle.Primary;
        badgeImage.raycastTarget = false;
        Label("Text", badge, "SELECTED", 0, 0, 160, 40, 18, true, Color.white, true);
        controller.selectedBadge = badge.gameObject;

        for (int i = 0; i < 2; i++)
        {
            var locked = Card("ComingSoon" + (i + 1), panel, 558 + i * 500, true);
            Solid("PreviewBacking", locked, 14, 14, 436, 450, Hex("222629"));
            LockIcon(locked, 196, 111);
            Label("Status", locked, "NOT AVAILABLE", 26, 316, 400, 30, 19, true, Hex("81868B"));
            Label("SceneName", locked, "COMING SOON", 25, 351, 412, 54, 35, true, Hex("9B9FA3"));
            Label("Description", locked, "More training scenes to come", 26, 411, 414, 34, 23, false, Hex("81868B"));
        }
        Solid("FooterDivider", panel, 58, 729, 1464, 1, Hex("4C5054"));
        controller.backButton = Button("Back", "Back", panel, 58, 772, 210, 66, MenuButtonGraphic.ButtonStyle.Quiet, 28, true);
        controller.selectionStatus = Label("SelectionStatus", panel, "Select a scene to continue.", 304, 780, 712, 48, 25, false, Hex("B9BEC3"));
        controller.startButton = Button("StartTraining", "Start Training", panel, 1162, 765, 360, 80, MenuButtonGraphic.ButtonStyle.Primary, 31, true);
        controller.startButton.interactable = false;

        var loading = Rect("LoadingOverlay", content, 0, 0, 0, 0);
        Stretch(loading);
        loading.gameObject.AddComponent<Image>().color = new Color(.055f, .06f, .07f, .97f);
        var loadingBox = Rect("Loading", loading, 0, 0, 700, 190);
        loadingBox.anchorMin = loadingBox.anchorMax = loadingBox.pivot = new Vector2(.5f, .5f);
        loadingBox.anchoredPosition = Vector2.zero;
        Label("Title", loadingBox, "OFFICE", 0, 0, 700, 70, 48, true, Color.white, true);
        controller.loadingLabel = Label("ProgressText", loadingBox, "Loading Office... 0%", 0, 80, 700, 48, 26, false, Hex("C9CDD1"), true);
        Solid("Track", loadingBox, 40, 150, 620, 10, Hex("34393D"));
        controller.loadingProgress = Solid("Progress", loadingBox, 40, 150, 620, 10, Hex("EA111C"));
        // Filled Image requires a sprite; create a project-owned 1x1 white sprite once.
        controller.loadingProgress.sprite = WhiteSprite();
        controller.loadingProgress.type = Image.Type.Filled;
        controller.loadingProgress.fillMethod = Image.FillMethod.Horizontal;
        controller.loadingProgress.fillOrigin = 0;
        controller.loadingProgress.fillAmount = 0;
        controller.loadingOverlay = loading.gameObject;
        controller.windowContent = content.gameObject;
        UnityEventTools.AddPersistentListener(controller.officeCard.onClick, controller.SelectOffice);
        UnityEventTools.AddPersistentListener(controller.startButton.onClick, controller.StartSelectedScene);
        UnityEventTools.AddPersistentListener(controller.backButton.onClick, controller.Close);
        UnityEventTools.AddPersistentListener(controller.closeButton.onClick, controller.Close);
        controller.officeCard.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnDown = controller.startButton, selectOnRight = controller.startButton, selectOnUp = controller.closeButton };
        controller.startButton.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnUp = controller.officeCard, selectOnLeft = controller.backButton };
        controller.backButton.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnUp = controller.officeCard, selectOnRight = controller.startButton, selectOnLeft = controller.closeButton };
        controller.closeButton.navigation = new Navigation { mode = Navigation.Mode.Explicit, selectOnDown = controller.officeCard, selectOnLeft = controller.backButton };

        Canvas.ForceUpdateCanvases();
        foreach (var t in root.GetComponentsInChildren<TextMeshProUGUI>(true)) t.ForceMeshUpdate(true);
        string[] overflow = root.GetComponentsInChildren<TextMeshProUGUI>(true).Where(t => t.isTextOverflowing).Select(t => t.name).ToArray();
        loading.gameObject.SetActive(false);
        badge.gameObject.SetActive(false);
        content.gameObject.SetActive(false);
        // Save reusable visual asset, then wire scene-specific references as overrides.
        PrefabUtility.SaveAsPrefabAssetAndConnect(root, Folder + "/SceneSelectionUI.prefab", InteractionMode.AutomatedAction);
        controller.mainMenuContent = menu.transform.Find("MenuLayout").gameObject;
        controller.returnFocus = controller.mainMenuContent.transform.Find("StartTraining").GetComponent<Button>();
        PrefabUtility.RecordPrefabInstancePropertyModifications(controller);
        Undo.RecordObject(menu, "Connect main menu to scene selection");
        UnityEventTools.AddPersistentListener(menu.startTrainingRequested, controller.Open);
        PrefabUtility.RecordPrefabInstancePropertyModifications(menu);
        string backgroundAfter = BackgroundSnapshot(scene);
        if (backgroundBefore != backgroundAfter) throw new InvalidOperationException("Background changed unexpectedly.");
        EditorSceneManager.MarkSceneDirty(scene);
        AssetDatabase.SaveAssets();
        bool saved = EditorSceneManager.SaveScene(scene);
        Undo.CollapseUndoOperations(undo);
        File.WriteAllText(Report, JsonUtility.ToJson(new Result { success = saved, backgroundUnchanged = true, backup = backup,
            cardCount = 3, availableCards = 1, disabledCards = 2, textOverflow = overflow }, true));
        Debug.Log("Scene selection installed and connected. " + Report);
    }

    private static RectTransform Rect(string name, Transform parent, float x, float y, float w, float h)
    {
        var go = new GameObject(name, typeof(RectTransform)); go.layer = 5;
        var r = (RectTransform)go.transform; r.SetParent(parent, false);
        r.anchorMin = r.anchorMax = r.pivot = new Vector2(0, 1);
        r.anchoredPosition = new Vector2(x, -y); r.sizeDelta = new Vector2(w, h); return r;
    }
    private static void Stretch(RectTransform r) { r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = r.offsetMax = Vector2.zero; }
    private static Color Hex(string value) { ColorUtility.TryParseHtmlString("#" + value, out var c); return c; }
    private static Image Solid(string name, Transform parent, float x, float y, float w, float h, Color colour)
    { var image = Rect(name, parent, x, y, w, h).gameObject.AddComponent<Image>(); image.color = colour; image.raycastTarget = false; return image; }
    private static TextMeshProUGUI Label(string name, Transform parent, string value, float x, float y, float w, float h, float size, bool bold, Color colour, bool centre = false)
    {
        var t = Rect(name, parent, x, y, w, h).gameObject.AddComponent<TextMeshProUGUI>();
        t.font = font; t.fontSharedMaterial = body; t.text = value; t.fontSize = size; t.color = colour;
        if (name == "Available" || name == "Status" || name == "SceneName" || name == "Description")
            t.fontSharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(Folder + "/FloatingText.mat") ?? body;
        t.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
        t.alignment = centre ? TextAlignmentOptions.Midline : TextAlignmentOptions.MidlineLeft;
        t.textWrappingMode = TextWrappingModes.NoWrap; t.raycastTarget = false; return t;
    }
    private static Button Button(string name, string label, Transform parent, float x, float y, float w, float h, MenuButtonGraphic.ButtonStyle style, float size, bool centre)
    {
        var r = Rect(name, parent, x, y, w, h);
        var g = r.gameObject.AddComponent<MenuButtonGraphic>(); g.Style = style;
        var b = r.gameObject.AddComponent<Button>(); b.targetGraphic = g;
        var colors = ColorBlock.defaultColorBlock;
        colors.highlightedColor = new Color(1.25f, 1.25f, 1.25f); colors.selectedColor = new Color(1.12f, 1.12f, 1.12f);
        colors.pressedColor = new Color(.72f, .72f, .72f); colors.disabledColor = new Color(.42f, .42f, .42f); colors.fadeDuration = .1f;
        b.colors = colors;
        if (!string.IsNullOrEmpty(label)) Label("Label", r, label, 16, 0, w - 32, h, size, true, Color.white, centre);
        return b;
    }
    private static RectTransform Card(string name, Transform panel, float x, bool locked)
    {
        var card = Button(name, null, panel, x, 218, 464, 478, MenuButtonGraphic.ButtonStyle.Quiet, 24, false);
        card.interactable = !locked;
        if (locked) card.navigation = new Navigation { mode = Navigation.Mode.None };
        return (RectTransform)card.transform;
    }
    private static void LockIcon(Transform parent, float x, float y)
    {
        var shackle = Rect("LockShackle", parent, x + 10, y, 56, 60).gameObject.AddComponent<MenuButtonGraphic>();
        shackle.Style = MenuButtonGraphic.ButtonStyle.Quiet; shackle.color = Hex("73797E"); shackle.raycastTarget = false;
        Solid("ShackleOpening", parent, x + 24, y + 13, 28, 45, Hex("222629"));
        Solid("LockBody", parent, x, y + 42, 76, 56, Hex("73797E"));
        Solid("Keyhole", parent, x + 34, y + 57, 8, 21, Hex("222629"));
    }
    private static Sprite WhiteSprite()
    {
        string path = Folder + "/ProgressWhite.asset";
        var existing = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Sprite>().FirstOrDefault();
        if (existing != null) return existing;
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false) { name = "ProgressWhite" };
        texture.SetPixel(0, 0, Color.white); texture.Apply(); AssetDatabase.CreateAsset(texture, path);
        var sprite = Sprite.Create(texture, new Rect(0, 0, 1, 1), new Vector2(.5f, .5f), 1); sprite.name = "ProgressWhite";
        AssetDatabase.AddObjectToAsset(sprite, texture); return sprite;
    }
    private static string BackgroundSnapshot(Scene scene) => string.Join("\n", scene.GetRootGameObjects()
        .Where(g => g.GetComponentInChildren<Canvas>(true) == null && g.GetComponentInChildren<UnityEngine.EventSystems.EventSystem>(true) == null)
        .SelectMany(g => g.GetComponentsInChildren<Component>(true)).Where(c => c != null).Select(c => c.GetType().FullName + ":" + EditorJsonUtility.ToJson(c)));
    [Serializable] private sealed class Result { public bool success; public bool backgroundUnchanged; public string backup; public int cardCount; public int availableCards; public int disabledCards; public string[] textOverflow; public string error; }
}
