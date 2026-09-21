using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using HMProtection.UI;
using HMProtection.Quiz;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.Video;

// Integration check: follows the serialized menu button events and lets the real CG finish.
[InitializeOnLoad]
public static class OfficeFireEntryChecks
{
    static int phase;
    static double deadline, wait, maxVideoTime;
    static bool sawVideo;
    static List<string> checks;
    static OfficeFireEntryChecks()
    {
        EditorApplication.delayCall += () => {
            if (!File.Exists("Library/OfficeFireEntryChecks.request")) return;
            File.Delete("Library/OfficeFireEntryChecks.request");
            SessionState.SetBool("OfficeFireEntryChecks", true);
            EditorApplication.isPlaying = true;
        };
        EditorApplication.playModeStateChanged += state => {
            if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool("OfficeFireEntryChecks", false)) return;
            SessionState.SetBool("OfficeFireEntryChecks", false);
            phase=0; checks=new List<string>(); sawVideo=false; maxVideoTime=0;
            wait=EditorApplication.timeSinceStartup+2; deadline=wait+100;
            EditorApplication.update+=Tick;
        };
    }
    static void Require(bool ok,string message) { if(!ok) throw new Exception(message); }
    static void Tick()
    {
        if (!EditorApplication.isPlaying) { EditorApplication.update-=Tick; return; }
        if (EditorApplication.timeSinceStartup<wait) return;
        try {
            Require(EditorApplication.timeSinceStartup<deadline,"Entry integration timed out");
            if(phase==0) {
                var menu=UnityEngine.Object.FindAnyObjectByType<MainMenuView>();
                Require(menu!=null,"Main menu must be open");
                var button=menu.GetComponentsInChildren<Button>().First(b=>b.name=="StartTraining");
                var corners=new Vector3[4]; ((RectTransform)button.transform).GetWorldCorners(corners);
                var pointer=new PointerEventData(EventSystem.current) {position=(corners[0]+corners[2])*.5f};
                var hits=new List<RaycastResult>(); EventSystem.current.RaycastAll(pointer,hits);
                checks.Add("Main menu raycast: "+string.Join(",",hits.Select(h=>h.gameObject.name)));
                button.onClick.Invoke();
                var select=UnityEngine.Object.FindAnyObjectByType<SceneSelectUI>();
                Require(select!=null && select.windowContent.activeInHierarchy,"Main menu button opens Scene Selection");
                select.officeCard.onClick.Invoke();
                Require(select.startButton.interactable,"Office selection enables Start Training");
                select.startButton.onClick.Invoke();
                checks.Add("Serialized main menu → office card → Start Training events passed");
                phase=1; return;
            }
            var video=UnityEngine.Object.FindAnyObjectByType<VideoPlayer>();
            if(video!=null && video.isPlaying) { sawVideo=true; maxVideoTime=Math.Max(maxVideoTime,video.time); }
            var flow=UnityEngine.Object.FindAnyObjectByType<OfficeFireChoiceFlow>();
            if(flow==null || !flow.IsQuestionActive) return;
            Require(sawVideo && maxVideoTime>1,"Real CG video must play before question");
            Require(UnityEngine.Object.FindAnyObjectByType<CgTransitionPlayer>()==null,"CG blackout cleaned up");
            Require(flow.presenter.Bubbles.Count==4 && flow.presenter.Bubbles.All(b=>b.TargetVisible),"Four visible choices automatically opened");
            Require(flow.presenter.viewController.IsOverview && !flow.presenter.viewController.ceiling.ceilingsVisible,"Overview active with ceiling hidden");
            checks.Add("CG played naturally through "+maxVideoTime.ToString("F2")+" seconds; then four-choice overview became interactive");
            File.WriteAllText("Library/OfficeFireEntryChecks.txt","PASS\n"+string.Join("\n",checks));
            EditorApplication.update-=Tick;
        } catch(Exception e) {
            EditorApplication.update-=Tick;
            File.WriteAllText("Library/OfficeFireEntryChecks.txt","FAIL\n"+string.Join("\n",checks)+"\n"+e);
            Debug.LogException(e);
        }
    }
}
