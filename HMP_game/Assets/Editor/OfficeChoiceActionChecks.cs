using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using HMProtection.Quiz;
using HMProtection.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

// Opt-in integration checks. Test player movements affect Play mode only.
[InitializeOnLoad] public static class OfficeChoiceActionChecks
{
    static OfficeChoiceInteraction owner;
    static OfficeFireChoiceFlow flow;
    static LevelFlowRunner runner;
    static body player;
    static Interactor actor;
    static Vector3 oldPosition;
    static Quaternion oldRotation;
    static float oldPitch;
    static int phase,option,submits;
    static double wait,deadline;
    static readonly List<string> lines=new List<string>();
    static InputSettings originalInputSettings;
    static InputSettings.BackgroundBehavior originalBackground;
    static InputSettings.EditorInputBehaviorInPlayMode originalEditorInput;
    static bool originalRunInBackground;
    static Keyboard testKeyboard, originalKeyboard;
    static OfficeChoiceActionChecks(){
        EditorApplication.delayCall+=()=>{if(File.Exists("Library/OfficeChoiceActions.request")){File.Delete("Library/OfficeChoiceActions.request");Begin();}};
        EditorApplication.playModeStateChanged+=s=>{if(s==PlayModeStateChange.ExitingPlayMode)CleanupInput();if(s==PlayModeStateChange.EnteredPlayMode && SessionState.GetBool("ChoiceActionChecks",false)){SessionState.SetBool("ChoiceActionChecks",false);Run();}};
    }
    [MenuItem("Tools/Scene Choices/Check Interactive Destinations")]
    public static void Begin(){if(EditorApplication.isPlaying){Run();return;}OfficeChoiceActionSetup.Install();SessionState.SetBool("ChoiceActionChecks",true);EditorApplication.isPlaying=true;}
    static void Run(){
        CleanupInput();phase=option=submits=0;owner=null;lines.Clear();wait=EditorApplication.timeSinceStartup+2;deadline=wait+140;originalRunInBackground=Application.runInBackground;Application.runInBackground=true;
        // Isolate test input from desktop focus without changing the project's saved input settings.
        originalInputSettings=InputSystem.settings;originalBackground=originalInputSettings.backgroundBehavior;originalEditorInput=originalInputSettings.editorInputBehaviorInPlayMode;
        originalInputSettings.backgroundBehavior=InputSettings.BackgroundBehavior.IgnoreFocus;
        originalInputSettings.editorInputBehaviorInPlayMode=InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
        originalKeyboard=Keyboard.current;testKeyboard=InputSystem.AddDevice<Keyboard>();testKeyboard.MakeCurrent();
        EditorApplication.update-=Tick;EditorApplication.update+=Tick;
    }
    static void Need(bool condition,string message){if(!condition)throw new Exception(message);}
    static void Tick(){
        if(!EditorApplication.isPlaying){EditorApplication.update-=Tick;CleanupInput();return;}
        if(EditorApplication.timeSinceStartup<wait)return;
        try{
            Need(EditorApplication.timeSinceStartup<deadline,"Test timeout phase "+phase+" option "+option);
            if(phase==0){
                owner=UnityEngine.Object.FindAnyObjectByType<OfficeChoiceInteraction>();Need(owner!=null,"Installed interaction owner");flow=owner.flow;
                runner=UnityEngine.Object.FindAnyObjectByType<LevelFlowRunner>();runner.StopLevel();flow.CancelQuestion();
                player=UnityEngine.Object.FindAnyObjectByType<body>();actor=player.GetComponent<Interactor>();oldPosition=player.transform.position;oldRotation=player.transform.rotation;oldPitch=player.Pitch;
                owner.onActionCompleted.AddListener(Count);
                Need(!owner.feedback.IsShowing && !owner.feedback.overlay.gameObject.activeSelf,"No initial black overlay");
                Need(owner.targets.Length==4 && owner.targets.Count(t=>t.correct)==1 && owner.targets.Single(t=>t.correct).optionId=="raise_alarm","Only door is correct");
                Need(owner.targets.All(t=>t.transform.parent.GetComponentsInChildren<Renderer>().Any(r=>r.sharedMaterials.All(m=>m!=null))),"Physical target renderers and materials");
                phase=1;
            }
            if(phase==1){flow.BeginQuestion();phase=2;return;}
            if(phase==2){if(!flow.IsQuestionActive)return;Need(flow.presenter.Bubbles.Count==4,"Four options");flow.presenter.Bubbles[option].button.onClick.Invoke();phase=3;wait=EditorApplication.timeSinceStartup+.6;return;}
            if(phase==3){
                var t=owner.targets[option];Need(owner.ArmedOption==t.optionId && owner.targets.Count(x=>x.GetComponent<Collider>().enabled)==1,"Only selected target armed");
                Need(!flow.presenter.viewController.IsOverview && flow.presenter.viewController.ceiling.ceilingsVisible,"First person and ceiling restored");
                var cc=player.GetComponent<CharacterController>();cc.enabled=false;var pos=flow.destinations[option].approach.position;player.transform.position=new Vector3(pos.x,oldPosition.y,pos.z);cc.enabled=true;
                var center=t.GetComponent<Collider>().bounds.center;var dir=center-player.PlayerCamera.transform.position;
                player.transform.rotation=Quaternion.Euler(0,Mathf.Atan2(dir.x,dir.z)*Mathf.Rad2Deg,0);player.SetPitch(-Mathf.Atan2(dir.y,new Vector2(dir.x,dir.z).magnitude)*Mathf.Rad2Deg);
                Physics.SyncTransforms();
                Need(Physics.Raycast(player.PlayerCamera.transform.position,player.PlayerCamera.transform.forward,out var hit,2.8f),"Eye ray hits target: "+t.optionId);
                Need(hit.collider.GetComponentInParent<OfficeChoiceTarget>()==t,"Eye ray blocked for "+t.optionId+" by "+hit.collider.name);
                Need(!string.IsNullOrEmpty(t.GetInteractPrompt(actor)),"Interaction prompt available");
                InputSystem.QueueStateEvent(testKeyboard,new KeyboardState(Key.E));phase=30;wait=EditorApplication.timeSinceStartup+.4;return;
            }
            if(phase==30){
                InputSystem.QueueStateEvent(testKeyboard,new KeyboardState());
                Need(owner.feedback.IsShowing,"Real Input System E key triggers interaction");
                owner.targets[option].Interact(actor);Need(submits==option+1,"Exactly one completion for repeated interaction");
                Need(owner.feedback.IsShowing && !player.enabled && !actor.enabled,"Blackout locks player and interaction");
                phase=4;wait=EditorApplication.timeSinceStartup+2.7;return;
            }
            if(phase==4){
                var f=owner.feedback;bool correct=option==2;
                Need(f.overlay.alpha==1 && f.content.alpha==1 && f.title.text==(correct?"Correct":"Incorrect") && f.LastCorrect==correct,"Feedback outcome and full blackout");
                Need(f.continueButton.gameObject.activeInHierarchy,"Animation completes and Continue appears");
                var es=UnityEngine.EventSystems.EventSystem.current;
                Need(es!=null && es.isActiveAndEnabled,"Active feedback EventSystem");
                var module=es.GetComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
                Need(module!=null && module.enabled && module.leftClick.action.enabled && module.submit.action.enabled,"Feedback mouse/submit actions enabled");
                var hits=new List<UnityEngine.EventSystems.RaycastResult>();
                es.RaycastAll(new UnityEngine.EventSystems.PointerEventData(es){position=RectTransformUtility.WorldToScreenPoint(null,f.continueButton.transform.position)},hits);
                Need(hits.Count>0 && hits[0].gameObject==f.continueButton.gameObject,"Continue is first UI raycast hit");
                InputSystem.QueueStateEvent(testKeyboard,new KeyboardState(Key.Enter));phase=40;wait=EditorApplication.timeSinceStartup+.4;return;
            }
            if(phase==40){
                InputSystem.QueueStateEvent(testKeyboard,new KeyboardState());var f=owner.feedback;bool correct=option==2;
                Need(owner.instructor.IsShowing && !player.enabled && !actor.enabled,"Continue opens instructor with controls locked");
                owner.instructor.Dismiss();
                Need(!f.IsShowing && player.enabled && actor.enabled,"Input System Enter submits Continue and restores controls");
                lines.Add("PASS "+owner.targets[option].optionId+": UI -> first person/ceiling -> unobstructed 2.8m ray -> one result -> "+(correct?"Correct":"Incorrect")+" -> Continue restores");
                option++;phase=1;
                if(option==4){
                    var cc=player.GetComponent<CharacterController>();cc.enabled=false;player.transform.SetPositionAndRotation(oldPosition,oldRotation);player.SetPitch(oldPitch);cc.enabled=true;
                    // Now verify the real runner waits through arrival and feedback before advancing.
                    runner.Begin(runner.Entry,runner.Config);phase=5;wait=EditorApplication.timeSinceStartup+.5;
                }
                return;
            }
            if(phase==5){if(!flow.IsQuestionActive)return;flow.presenter.Bubbles[2].button.onClick.Invoke();phase=6;wait=EditorApplication.timeSinceStartup+1;return;}
            if(phase==6){
                Need(runner.CurrentStage.kind.ToLowerInvariant()=="freeroam" && player.controlEnabled,"Runner lets player walk immediately");
                var cc=player.GetComponent<CharacterController>();cc.enabled=false;var p=flow.destinations[2].approach.position;player.transform.position=new Vector3(p.x,oldPosition.y,p.z);cc.enabled=true;
                phase=7;wait=EditorApplication.timeSinceStartup+2;return;
            }
            if(phase==7){Need(runner.CurrentStage.kind.ToLowerInvariant()=="freeroam" && !owner.feedback.IsShowing,"Arrival alone does not show result or advance");owner.targets[2].Interact(actor);phase=8;wait=EditorApplication.timeSinceStartup+3;return;}
            if(phase==8){Need(runner.IsPaused && runner.CurrentStage.kind.ToLowerInvariant()=="freeroam" && owner.feedback.LastCorrect,"Feedback freezes runner until dismissed");Need(runner.Score.LastAnswerCorrect==true,"Runner score uses door as correct");owner.feedback.Dismiss();phase=9;wait=EditorApplication.timeSinceStartup+.5;return;}
            if(phase==9){Need(owner.instructor.IsShowing && runner.IsPaused && runner.CurrentStage.kind.ToLowerInvariant()=="freeroam","Instructor keeps runner paused");owner.instructor.Dismiss();phase=91;wait=EditorApplication.timeSinceStartup+.5;return;}
            if(phase==91){Need(runner.CurrentStage.kind.ToLowerInvariant()=="result" && !runner.IsPaused,"Review dismissal advances runner");lines.Add("PASS runner: arrival waits for E; feedback and instructor freeze; door scoring correct; review dismissal advances");runner.StopLevel();owner.onActionCompleted.RemoveListener(Count);Finish("PASS");}
        }catch(Exception e){lines.Add(e.ToString());Finish("FAIL");Debug.LogException(e);}
    }
    static void Count(string id,bool correct){submits++;}
    static void Finish(string result){
        EditorApplication.update-=Tick;
        CleanupInput();
        File.WriteAllText("Library/OfficeChoiceActionChecks.txt",result+" (Input System E + Enter, UI raycast and event checks)\n"+string.Join("\n",lines));
    }
    static void CleanupInput(){
        if(testKeyboard!=null){InputSystem.RemoveDevice(testKeyboard);testKeyboard=null;originalKeyboard?.MakeCurrent();}
        if(originalInputSettings!=null){originalInputSettings.backgroundBehavior=originalBackground;originalInputSettings.editorInputBehaviorInPlayMode=originalEditorInput;originalInputSettings=null;Application.runInBackground=originalRunInBackground;}
    }
    [MenuItem("Tools/Scene Choices/Preview Correct Feedback (Play)")]
    static void Correct(){if(EditorApplication.isPlaying)UnityEngine.Object.FindAnyObjectByType<QuizResultFeedback>()?.Show(true);}
    [MenuItem("Tools/Scene Choices/Preview Incorrect Feedback (Play)")]
    static void Incorrect(){if(EditorApplication.isPlaying)UnityEngine.Object.FindAnyObjectByType<QuizResultFeedback>()?.Show(false);}
}
