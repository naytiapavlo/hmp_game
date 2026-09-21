using System;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using HMProtection.Quiz;

[InitializeOnLoad]
public static class OfficeFireChoiceSetup
{
    const string Marker = "Library/OfficeFireChoiceSetup.v2.done";
    static OfficeFireChoiceSetup() { EditorApplication.delayCall += Inspect; EditorApplication.delayCall += Auto; }
    static void Auto()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) { EditorApplication.delayCall += Auto; return; }
        if (!EditorApplication.isPlayingOrWillChangePlaymode && !File.Exists(Marker)) Install();
    }
    [MenuItem("Tools/Scene Choices/Install Office Fire Question")]
    public static void Install()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (scene.path != "Assets/Scenes/办公室场景.unity") return;
        try
        {
            // Preserve current user edits before adding the authorized question flow.
            if (scene.isDirty) EditorSceneManager.SaveScene(scene);
            SceneChoiceSetup.Install();
            var p = SceneChoiceSetup.Find<SceneChoicePresenter>(scene);
            var guide = SceneChoiceSetup.Find<GuidanceSystem>(scene);
            if (p == null || guide == null) throw new InvalidOperationException("Scene choices / guidance not installed.");
            var flow = p.GetComponent<OfficeFireChoiceFlow>();
            if (flow == null) flow = p.gameObject.AddComponent<OfficeFireChoiceFlow>();
            flow.presenter=p; flow.guidance=guide;
            var geometry = p.viewController.officeGeometry;
            // This rollback deleted the ceiling modules as prefab overrides. Restore only those modules.
            int restoredCeilings=0;
            foreach(var instance in geometry.GetComponentsInChildren<Transform>(true).Where(t=>PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject)).ToArray())
            foreach(var removed in PrefabUtility.GetRemovedGameObjects(instance.gameObject).ToArray())
            {
                if(removed.assetGameObject==null || !removed.assetGameObject.name.StartsWith("SM_Ceiling",StringComparison.Ordinal)) continue;
                PrefabUtility.RevertRemovedGameObject(instance.gameObject,removed.assetGameObject,InteractionMode.AutomatedAction);
                restoredCeilings++;
            }
            if(p.viewController.ceiling!=null) p.viewController.ceiling.CollectCeilings();
            var fire = scene.GetRootGameObjects().FirstOrDefault(g => g.name=="起火点");
            if (fire == null) throw new InvalidOperationException("Current scene fire origin not found.");
            // These positions are the four destinations in the supplied overview, in current office world coordinates.
            var records = Anchor(geometry, "records", new Vector3(-8.35f, 1.0f, 10.35f));
            var window = Anchor(geometry, "window", new Vector3(10.2f, 1.25f, -1.8f));
            var entrance = Anchor(geometry, "entrance", new Vector3(-.72f, 1.1f, 12.8f));
            var strip = Anchor(fire.transform, "power_strip", fire.transform.position + Vector3.up * .2f);
            flow.destinations = new[] {
                Destination(records, "rescue_documents", "Records Room", new Vector3(-8.35f, .03f, 9.1f)),
                Destination(window, "open_window", "Office Window", new Vector3(9.65f, .03f, -1.8f)),
                Destination(entrance, "raise_alarm", "Entrance", new Vector3(-.72f, .03f, 11.6f)),
                Destination(strip, "unplug_strip", "Burning Power Strip", new Vector3(fire.transform.position.x, .03f, fire.transform.position.z+.55f))
            };
            EditorSceneManager.MarkSceneDirty(scene); EditorSceneManager.SaveScene(scene);
            File.WriteAllText(Marker,DateTime.UtcNow.ToString("o"));
            File.WriteAllText("Library/OfficeFireChoiceSetup.txt", "SUCCESS: four destinations, entry flow and existing guidance connected. Restored ceiling modules: "+restoredCeilings);
        }
        catch (Exception e) { File.WriteAllText("Library/OfficeFireChoiceSetup.txt", e.ToString()); Debug.LogException(e); }
    }
    static SceneChoiceAnchor Anchor(Transform parent, string id, Vector3 position)
    {
        var anchor=parent.GetComponentsInChildren<SceneChoiceAnchor>(true).FirstOrDefault(a=>a.anchorId=="office.fire."+id);
        if(anchor==null) { var go=new GameObject("Fire Choice - "+id); go.transform.SetParent(parent,false); anchor=go.AddComponent<SceneChoiceAnchor>(); }
        anchor.anchorId="office.fire."+id; anchor.transform.position=position; return anchor;
    }
    static OfficeFireChoiceFlow.Destination Destination(SceneChoiceAnchor anchor,string id,string label,Vector3 position)
    {
        var t=anchor.transform.Find("Approach");
        if(t==null) { t=new GameObject("Approach").transform; t.SetParent(anchor.transform,false); }
        t.position=position;
        return new OfficeFireChoiceFlow.Destination { optionId=id,label=label,approach=t };
    }
    [MenuItem("Tools/Scene Choices/Inspect Office Targets")]
    public static void Inspect()
    {
        if (EditorApplication.isCompiling || EditorApplication.isUpdating) { EditorApplication.delayCall += Inspect; return; }
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (!scene.name.Contains("办公室")) return;
        var sb = new StringBuilder();
        foreach (var root in scene.GetRootGameObjects())
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            string n=t.name;
            if(n=="GRP_Architecture_Ceiling") foreach(var c in t.GetComponentsInChildren<Transform>(true)) sb.AppendLine("CEILING CHILD: "+c.name+" active="+c.gameObject.activeSelf+" components="+string.Join(",",c.GetComponents<Component>().Select(v=>v==null?"Missing":v.GetType().Name)));
            if (!(n.Contains("Ceiling") || n.Contains("Desk") || n.Contains("Door") || n.Contains("Window") || n.Contains("Glass") || n.Contains("Storage") || n.Contains("Shelf") || n.Contains("Cabinet") || n.Contains("插排") || n.Contains("起火") || n=="body" || n.Contains("Choice"))) continue;
            var r=t.GetComponent<Renderer>();
            sb.AppendLine(n+" | "+t.position.ToString("F3")+" | "+(r==null?"":r.bounds.center.ToString("F3")+" size "+r.bounds.size.ToString("F3")+" enabled="+r.enabled)+" | active="+t.gameObject.activeInHierarchy+" | parent "+t.parent?.name);
        }
        File.WriteAllText("Library/OfficeFireChoiceTargets.txt",sb.ToString());
    }
}
