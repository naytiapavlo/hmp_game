using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using HMProtection.Quiz;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class OfficeFireChoiceChecks
{
    [Serializable] class Report { public bool passed; public string error; public List<string> checks=new List<string>(); }
    static OfficeFireChoiceFlow flow;
    static Report report;
    static int option, phase;
    static double wait, timeout;
    static Vector3 playerPosition;
    static OfficeFireChoiceChecks()
    {
        EditorApplication.delayCall += () => { if(File.Exists("Library/OfficeFireChoiceChecks.request")) { File.Delete("Library/OfficeFireChoiceChecks.request"); Begin(); } };
        EditorApplication.playModeStateChanged += state => {
            if(state==PlayModeStateChange.EnteredPlayMode && SessionState.GetBool("OfficeFireChoiceChecks",false)) { SessionState.SetBool("OfficeFireChoiceChecks",false); Run(); }
        };
    }
    [MenuItem("Tools/Scene Choices/Check Fire Question And Routes")]
    public static void Begin()
    {
        if(EditorApplication.isPlaying) { Run(); return; }
        OfficeFireChoiceSetup.Install();
        SessionState.SetBool("OfficeFireChoiceChecks",true); EditorApplication.isPlaying=true;
    }
    static void Run()
    {
        report=new Report(); option=phase=0; flow=null;
        wait=EditorApplication.timeSinceStartup+2; timeout=wait+45;
        EditorApplication.update-=Tick; EditorApplication.update+=Tick;
    }
    static void Require(bool ok,string message) { if(!ok) throw new Exception(message); }
    static T Field<T>(GuidanceSystem g,string name) => (T)typeof(GuidanceSystem).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(g);
    static void Tick()
    {
        if(!EditorApplication.isPlaying) { EditorApplication.update-=Tick; return; }
        if(EditorApplication.timeSinceStartup<wait) return;
        try
        {
            Require(EditorApplication.timeSinceStartup<timeout,"Flow check timed out");
            if(flow==null) { flow=UnityEngine.Object.FindAnyObjectByType<OfficeFireChoiceFlow>(); Require(flow!=null,"Installed flow"); }
            if(phase==0)
            {
                playerPosition=UnityEngine.Object.FindAnyObjectByType<body>().transform.position;
                flow.BeginQuestion(); phase=1; return;
            }
            if(phase==1)
            {
                if(!flow.IsQuestionActive) { Require(string.IsNullOrEmpty(flow.LastError),flow.LastError); return; }
                var p=flow.presenter;
                Require(p.Bubbles.Count==4 && p.Bubbles.All(b=>b.TargetVisible),"All four options visible");
                Require(!p.viewController.ceiling.ceilingsVisible && !UnityEngine.Object.FindAnyObjectByType<body>().enabled,"Overview hides ceiling and disables player");
                var d=flow.destinations[option];
                var scanner=flow.guidance.GetComponent<SceneObstacleScanner>();
                var grid=scanner.BuildGrid(.25f,scanner.GetSceneBounds());
                Require(RoutePlanner.TryPlanPath(playerPosition,d.approach.position,grid,out var path),"No navigable route: "+d.optionId);
                Require(path.Length>0,"Nonempty route");
                p.Bubbles[option].button.onClick.Invoke(); p.Bubbles[option].button.onClick.Invoke();
                phase=2; wait=EditorApplication.timeSinceStartup+.4; return;
            }
            if(phase==2)
            {
                var p=flow.presenter; var d=flow.destinations[option];
                Require(!flow.IsQuestionActive && !p.viewController.IsOverview && p.Bubbles.Count==0,"Question closes and camera restores");
                Require(p.viewController.ceiling.ceilingsVisible && UnityEngine.Object.FindAnyObjectByType<body>().enabled,"Ceiling and player enabled");
                var ceilingMeshes=p.viewController.ceiling.GetComponentsInChildren<MeshRenderer>(true).Where(r=>r.name.StartsWith("SM_Ceiling",StringComparison.Ordinal)).ToArray();
                Require(ceilingMeshes.Length>0 && ceilingMeshes.All(r=>r.enabled && r.gameObject.activeInHierarchy),"Actual ceiling meshes restored and visible");
                Require(Field<bool>(flow.guidance,"active") && !Field<bool>(flow.guidance,"loggedFallback"),"Real guidance route active without fallback");
                Require((Field<Vector3>(flow.guidance,"currentDestination")-d.approach.position).sqrMagnitude<.001f,"Chosen route destination matches");
                Require(flow.LastSelectedOption==d.optionId,"Chosen option preserved");
                Vector3 delta=UnityEngine.Object.FindAnyObjectByType<body>().transform.position-playerPosition;
                Require(new Vector2(delta.x,delta.z).magnitude<.02f,"Player not teleported");
                report.checks.Add(d.optionId+": four-choice UI → first person + ceiling → navigable route "+d.approach.position.ToString("F2"));
                flow.guidance.HideRoute(); option++; phase=0;
                if(option==4) { report.passed=true; Finish(); flow.BeginQuestion(); SceneChoicePreviewWindow.Open(); }
            }
        }
        catch(Exception e) { report.error=e.ToString(); Finish(); Debug.LogException(e); }
    }
    static void Finish() { EditorApplication.update-=Tick; File.WriteAllText("Library/OfficeFireChoiceChecks.json",JsonUtility.ToJson(report,true)); }
}
