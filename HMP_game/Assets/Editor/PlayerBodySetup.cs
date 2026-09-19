// One-shot player rig setup: attaches the body first-person controller (class "body"
// in Assets/Scripts/人物/body.cs) to the object named "body" in the currently open
// scene, configures its CharacterController to
// match the existing rig (人物 root at floor level, body ~0.91m up, Main Camera child
// at eye height), removes the placeholder CapsuleCollider that would fight the
// controller and block interaction raycasts, wires the eye camera reference and
// saves the scene. Writes Library/PlayerBodySetup.json. A marker file prevents it
// from running twice; use the menu item to force a re-run.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class PlayerBodySetup
{
    private const string BodyObjectName = "body";
    private const string ReportPath = "Library/PlayerBodySetup.json";
    private const string MarkerPath = "Library/PlayerBodySetup.done";

    static PlayerBodySetup()
    {
        EditorApplication.delayCall += Run;
    }

    public static void Run()
    {
        if (File.Exists(MarkerPath)) return;
        if (EditorApplication.isCompiling || EditorApplication.isUpdating)
        {
            EditorApplication.delayCall += Run;
            return;
        }

        var log = new List<string>();
        bool processed = false;
        string error = null;
        try
        {
            processed = ProcessScene(log);
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            Debug.LogException(e);
        }

        try
        {
            WriteReport(processed, error, log);
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }

        if (processed) File.WriteAllText(MarkerPath, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        Debug.Log("PlayerBodySetup finished. processed=" + processed + " error=" + (error ?? "none")
                  + " report=" + ReportPath);
    }

    [MenuItem("Tools/Player/Re-run Body Setup")]
    public static void ForceRerun()
    {
        if (File.Exists(MarkerPath)) File.Delete(MarkerPath);
        Run();
    }

    private static bool ProcessScene(List<string> log)
    {
        Scene scene = SceneManager.GetActiveScene();

        // 在当前场景里按名字找 body（它是「人物」的子物体，不在根层级）
        GameObject body = null;
        int matches = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name != BodyObjectName) continue;
                matches++;
                if (body == null) body = t.gameObject;
            }
        }
        if (body == null)
        {
            log.Add("no object named '" + BodyObjectName + "' in scene '" + scene.name + "'");
            return false;
        }
        if (matches > 1) log.Add("multiple objects named '" + BodyObjectName + "' (" + matches + "), using the first");

        string path = GetHierarchyPath(body.transform);
        log.Add("body path: " + path + " worldPos=" + body.transform.position.ToString("F3"));

        // 0) 清理误挂/重复挂载的 body 组件：整个场景只保留 body 物体上的一个，
        //    优先保留已接好相机引用的那个（其余会在运行时互相打架或空引用报错）。
        var strays = new List<body>();
        foreach (GameObject root in scene.GetRootGameObjects())
            strays.AddRange(root.GetComponentsInChildren<body>(true));
        body bodyScript = null;
        foreach (body c in strays.Where(c => c.gameObject == body))
        {
            if (bodyScript == null) bodyScript = c;
            else if (PlayerCameraIsWired(c) && !PlayerCameraIsWired(bodyScript)) bodyScript = c;
        }
        int removedStrays = 0;
        foreach (body c in strays)
        {
            if (c == bodyScript) continue;
            string where = GetHierarchyPath(c.transform);
            UnityEngine.Object.DestroyImmediate(c);
            removedStrays++;
            log.Add("removed stray body component on: " + where);
        }
        log.Add("stray/duplicate body components removed: " + removedStrays);

        // 0.5) 同理清理不在 body 上的 CharacterController：RequireComponent 曾给误挂的
        //      body 组件自动补过控制器（比如手部骨骼上），残留在玩家胶囊内会引起物理冲突。
        int removedControllers = 0;
        foreach (GameObject root in scene.GetRootGameObjects())
        {
            foreach (CharacterController cc in root.GetComponentsInChildren<CharacterController>(true))
            {
                if (cc.gameObject == body) continue;
                log.Add("removed stray CharacterController on: " + GetHierarchyPath(cc.transform));
                UnityEngine.Object.DestroyImmediate(cc);
                removedControllers++;
            }
        }
        log.Add("stray CharacterControllers removed: " + removedControllers);

        // 1) 移除占位 CapsuleCollider：它和 CharacterController 的胶囊重叠，
        //    既会让角色卡在自己的碰撞体里，也会挡住从相机发出的交互射线。
        var removed = new List<string>();
        foreach (CapsuleCollider cc in body.GetComponents<CapsuleCollider>())
        {
            removed.Add(cc.name);
            UnityEngine.Object.DestroyImmediate(cc);
        }
        log.Add("removed CapsuleCollider count: " + removed.Count);

        // 2) 挂 body 玩家控制脚本（RequireComponent 会自动补 CharacterController）。
        //    注意：类名必须与文件名一致（Assets/Scripts/人物/body.cs -> class body）。
        bool addedScript = bodyScript == null;
        if (addedScript) bodyScript = body.AddComponent<body>();

        // 2.5) 行走速度等「脚本默认值」对已挂在场景里的组件不生效（Inspector 序列化值优先），
        //      所以这里同步刷一遍，保证调参后重跑本设置能把新默认值应用到场景组件上。
        var speedSo = new SerializedObject(bodyScript);
        speedSo.FindProperty("walkSpeed").floatValue = 1.4f; // 正常人步行速度（米/秒）
        speedSo.ApplyModifiedPropertiesWithoutUndo();
        log.Add("Body script added: " + addedScript);

        // 3) 配置 CharacterController：body 当前悬在地面上方约 0.91m，
        //    胶囊取 height=1.8、center=0 后正好下探到地板、上沿约 1.81m，包住 1.56m 的眼睛。
        //    若脚本是在旧版存根（无 RequireComponent）时手动挂上的，这里补加控制器。
        var controller = bodyScript.GetComponent<CharacterController>();
        if (controller == null) controller = body.AddComponent<CharacterController>();
        controller.height = 1.8f;
        controller.radius = 0.3f;
        controller.center = Vector3.zero;
        controller.slopeLimit = 45f;
        controller.stepOffset = 0.3f;
        controller.skinWidth = 0.08f;
        controller.minMoveDistance = 0f;
        log.Add("CharacterController configured: height=1.8 radius=0.3 center=(0,0,0)");

        // 4) 接上眼睛相机引用（body 下的 Main Camera）
        Camera cam = body.GetComponentInChildren<Camera>(true);
        if (cam != null)
        {
            var so = new SerializedObject(bodyScript);
            so.FindProperty("playerCamera").objectReferenceValue = cam;
            so.ApplyModifiedPropertiesWithoutUndo();
            log.Add("playerCamera wired: " + GetHierarchyPath(cam.transform)
                    + " eyeWorldY=" + cam.transform.position.y.ToString("F3"));
        }
        else log.Add("WARNING: no camera under body, look will not work until playerCamera is assigned");

        EditorSceneManager.MarkSceneDirty(scene);
        bool saved = EditorSceneManager.SaveOpenScenes();
        log.Add("scene saved: " + saved + " (" + scene.path + ")");
        return true;
    }

    private static bool PlayerCameraIsWired(body c)
    {
        var so = new SerializedObject(c);
        return so.FindProperty("playerCamera").objectReferenceValue != null;
    }

    private static string GetHierarchyPath(Transform t)
    {
        var sb = new StringBuilder(t.name);
        while (t.parent != null)
        {
            t = t.parent;
            sb.Insert(0, t.name + "/");
            sb.Insert(0, "/");
        }
        return sb.ToString();
    }

    private static string J(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 32) sb.Append(' ');
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    private static void WriteReport(bool processed, string error, List<string> log)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"unityVersion\": ").Append(J(Application.unityVersion)).Append(",\n");
        sb.Append("  \"processed\": ").Append(processed ? "true" : "false").Append(",\n");
        sb.Append("  \"error\": ").Append(J(error)).Append(",\n");
        sb.Append("  \"log\": [\n    ").Append(string.Join(",\n    ", ConvertAll(log, J))).Append("\n  ]\n}\n");

        Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
        File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
    }

    private static List<string> ConvertAll(List<string> src, Func<string, string> f)
    {
        var dst = new List<string>(src.Count);
        foreach (string s in src) dst.Add(f(s));
        return dst;
    }
}
