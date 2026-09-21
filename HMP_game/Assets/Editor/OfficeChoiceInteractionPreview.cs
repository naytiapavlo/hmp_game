using HMProtection.Quiz;
using HMProtection.Core;
using UnityEditor;
using UnityEngine;

// Places the player in front of an actual target in Play mode; never saves a test pose.
public sealed class OfficeChoiceInteractionPreview : EditorWindow
{
    int pending=-1,phase;
    OfficeChoiceInteraction owner;
    double deadline;
    [MenuItem("Tools/Scene Choices/Preview Physical Interaction")]
    public static void Open()=>GetWindow<OfficeChoiceInteractionPreview>("Interaction Preview");
    void OnGUI(){
        EditorGUILayout.HelpBox("Play mode only. Stops the lesson and places the player at the selected object for an E-key check. Stop Play to restore the scene.",MessageType.Info);
        using(new EditorGUI.DisabledScope(!EditorApplication.isPlaying || pending>=0)){
            string[] labels={"1 Printer — Incorrect","2 Window — Incorrect","3 Door — Correct","4 Power strip — Incorrect"};
            for(int i=0;i<4;i++)if(GUILayout.Button(labels[i]))Preview(i);
        }
    }
    void Preview(int option){
        owner=FindAnyObjectByType<OfficeChoiceInteraction>();if(owner==null)return;
        FindAnyObjectByType<LevelFlowRunner>()?.StopLevel();owner.feedback.Dismiss();owner.flow.CancelQuestion();
        owner.flow.BeginQuestion();pending=option;phase=0;deadline=EditorApplication.timeSinceStartup+15;
        EditorApplication.update-=Tick;EditorApplication.update+=Tick;
    }
    void Tick(){
        if(!EditorApplication.isPlaying || owner==null || EditorApplication.timeSinceStartup>deadline){Stop();return;}
        var flow=owner.flow;
        if(phase==0){if(!flow.IsQuestionActive)return;flow.presenter.Bubbles[pending].button.onClick.Invoke();phase=1;return;}
        if(flow.IsQuestionActive || owner.ArmedOption!=owner.targets[pending].optionId)return;
        var player=FindAnyObjectByType<body>();var cc=player.GetComponent<CharacterController>();
        var position=flow.destinations[pending].approach.position;cc.enabled=false;
        player.transform.position=new Vector3(position.x,player.transform.position.y,position.z);cc.enabled=true;
        var target=owner.targets[pending].GetComponent<Collider>().bounds.center;
        var d=target-player.PlayerCamera.transform.position;
        player.transform.rotation=Quaternion.Euler(0,Mathf.Atan2(d.x,d.z)*Mathf.Rad2Deg,0);
        player.SetPitch(-Mathf.Atan2(d.y,new Vector2(d.x,d.z).magnitude)*Mathf.Rad2Deg);
        player.controlEnabled=true;Physics.SyncTransforms();
        var gameView=System.Type.GetType("UnityEditor.GameView,UnityEditor");if(gameView!=null)GetWindow(gameView).Focus();
        Stop();
    }
    void Stop(){EditorApplication.update-=Tick;pending=-1;Repaint();}
    void OnDisable()=>Stop();
}
