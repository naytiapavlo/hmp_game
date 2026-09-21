using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
[InitializeOnLoad] public static class OfficeActionAssetProbe
{
    static OfficeActionAssetProbe(){ EditorApplication.delayCall+=Run; }
    static void Run(){
        if(EditorApplication.isCompiling || EditorApplication.isUpdating){EditorApplication.delayCall+=Run;return;}
        if(EditorApplication.isPlayingOrWillChangePlaymode) return;
        var sb=new StringBuilder();
        foreach(var root in SceneManager.GetActiveScene().GetRootGameObjects())
        foreach(var t in root.GetComponentsInChildren<Transform>(true))
        if(t.name.Contains("Printer") || t.name.Contains("Socket") || t.name.Contains("Entrance") || t.name.Contains("Entry") || t.name=="SM_Facade_West_Glass_04") {
            var r=t.GetComponent<Renderer>(); sb.AppendLine(t.name+" pos="+t.position+" bounds="+(r==null?"none":r.bounds.ToString())+" components="+string.Join(",",t.GetComponents<Component>().Select(c=>c==null?"missing":c.GetType().Name)));
        }
        var asset=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Props/Models/SM_Hazard_OverloadSocket_01_Aged.fbx");
        if(asset!=null) foreach(var r in asset.GetComponentsInChildren<Renderer>(true)) sb.AppendLine("ASSET "+r.name+" "+r.bounds+" mats="+string.Join(",",r.sharedMaterials.Select(m=>m==null?"null":m.name)));
        File.WriteAllText("Library/OfficeActionAssets.txt",sb.ToString());
    }
}
