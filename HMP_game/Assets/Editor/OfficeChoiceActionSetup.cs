using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using HMProtection.Quiz;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

[InitializeOnLoad] public static class OfficeChoiceActionSetup
{
    const string Folder="Assets/UI/QuizFeedback", Marker="Library/OfficeChoiceActions.v1.done";
    static OfficeChoiceActionSetup(){EditorApplication.delayCall+=Auto;}
    static void Auto(){if(EditorApplication.isCompiling||EditorApplication.isUpdating){EditorApplication.delayCall+=Auto;return;}if(!File.Exists(Marker))Install();}
    [MenuItem("Tools/Scene Choices/Install Interactive Destinations")]
    public static void Install()
    {
        var scene=SceneManager.GetActiveScene();
        if(EditorApplication.isPlayingOrWillChangePlaymode || scene.path!="Assets/Scenes/办公室场景.unity")return;
        try {
            if(scene.isDirty)EditorSceneManager.SaveScene(scene);
            var flow=SceneChoiceSetup.Find<OfficeFireChoiceFlow>(scene);
            if(flow==null)throw new Exception("Office question flow missing");
            Directory.CreateDirectory(Folder);Directory.CreateDirectory("Assets/Props/OfficeChoice");AssetDatabase.Refresh();
            var prefab=BuildFeedback();
            var feedback=SceneChoiceSetup.Find<QuizResultFeedback>(scene);
            if(feedback==null)feedback=((GameObject)PrefabUtility.InstantiatePrefab(prefab,scene)).GetComponent<QuizResultFeedback>();
            feedback.fallbackEventSystem=flow.presenter.viewController.fallbackEventSystem;
            var owner=flow.GetComponent<OfficeChoiceInteraction>();if(owner==null)owner=flow.gameObject.AddComponent<OfficeChoiceInteraction>();
            owner.flow=flow;owner.feedback=feedback;
            var root=scene.GetRootGameObjects().FirstOrDefault(g=>g.name=="Office Choice Objects");if(root==null)root=new GameObject("Office Choice Objects");
            var printer=root.transform.Find("SM_Printer_Records");
            if(printer==null){printer=BuildPrinter().transform;printer.SetParent(root.transform,false);printer.position=new Vector3(-8.35f,0,10.6f);}
            var fire=scene.GetRootGameObjects().First(g=>g.name=="起火点");
            var strip=root.transform.Find("SM_Choice_PowerStrip");
            if(strip==null){var model=AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Props/Models/SM_Hazard_OverloadSocket_01_Aged.fbx");
                if(model==null)throw new Exception("Aged power strip model missing");
                strip=((GameObject)PrefabUtility.InstantiatePrefab(model,scene)).transform;strip.name="SM_Choice_PowerStrip";strip.SetParent(root.transform,true);strip.position=new Vector3(fire.transform.position.x,.035f,fire.transform.position.z);}
            Transform Find(string name)=>scene.GetRootGameObjects().SelectMany(g=>g.GetComponentsInChildren<Transform>(true)).First(t=>t.name==name);
            var window=Find("SM_Facade_West_Glass_04");var door=Find("SM_Door_EntryLeft_01");
            owner.targets=new[]{
                Target(owner,printer,"rescue_documents","E  Rescue documents from printer",false,new Vector3(-8.35f,.66f,10.6f),new Vector3(.72f,1.12f,.75f)),
                Target(owner,window,"open_window","E  Open window",false,new Vector3(10.235f,1.6f,-1.8f),new Vector3(.1f,1.5f,1.8f)),
                Target(owner,door,"raise_alarm","E  Shout Fire! at the door",true,new Vector3(-.72f,1.17f,12.75f),new Vector3(2f,2.32f,.14f)),
                Target(owner,strip,"unplug_strip","E  Unplug power strip",false,strip.position+new Vector3(0,.05f,0),new Vector3(.4f,.13f,.24f))
            };
            // Point at the physical printer while keeping the route's accessible standing position.
            var anchor=Find("Fire Choice - records");var approach=anchor.Find("Approach");var oldApproach=approach.position;anchor.position=new Vector3(-8.35f,.9f,10.6f);approach.position=oldApproach;
            EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);AssetDatabase.SaveAssets();
            File.WriteAllText(Marker,DateTime.UtcNow.ToString("o"));File.WriteAllText("Library/OfficeChoiceActionsSetup.txt","PASS: four physical targets and reusable feedback prefab installed");
        }catch(Exception e){File.WriteAllText("Library/OfficeChoiceActionsSetup.txt",e.ToString());Debug.LogException(e);}
    }
    static OfficeChoiceTarget Target(OfficeChoiceInteraction owner,Transform parent,string id,string prompt,bool correct,Vector3 center,Vector3 size)
    {
        var child=parent.Find("Choice Interaction");if(child==null){child=new GameObject("Choice Interaction").transform;child.SetParent(parent,false);}
        child.position=center;child.rotation=Quaternion.identity;child.localScale=Vector3.one;
        var t=child.GetComponent<OfficeChoiceTarget>();if(t==null)t=child.gameObject.AddComponent<OfficeChoiceTarget>();
        t.owner=owner;t.optionId=id;t.prompt=prompt;t.correct=correct;
        child.gameObject.layer=LayerMask.NameToLayer("Interactable");
        var box=child.GetComponent<BoxCollider>();box.size=new Vector3(size.x/child.lossyScale.x,size.y/child.lossyScale.y,size.z/child.lossyScale.z);box.center=Vector3.zero;box.enabled=false;
        return t;
    }
    static GameObject BuildFeedback()
    {
        string path=Folder+"/QuizResultFeedback.prefab";
        var existing=AssetDatabase.LoadAssetAtPath<GameObject>(path);if(existing!=null)return existing;
        var root=new GameObject("Quiz Result Feedback");var f=root.AddComponent<QuizResultFeedback>();
        var canvas=new GameObject("Overlay",typeof(RectTransform),typeof(Canvas),typeof(CanvasScaler),typeof(GraphicRaycaster),typeof(CanvasGroup));canvas.transform.SetParent(root.transform,false);
        canvas.GetComponent<Canvas>().renderMode=RenderMode.ScreenSpaceOverlay;canvas.GetComponent<Canvas>().sortingOrder=31000;
        var scaler=canvas.GetComponent<CanvasScaler>();scaler.uiScaleMode=CanvasScaler.ScaleMode.ScaleWithScreenSize;scaler.referenceResolution=new Vector2(1920,1080);scaler.matchWidthOrHeight=.5f;
        f.overlay=canvas.GetComponent<CanvasGroup>();
        var bg=Rect("Blackout",canvas.transform,Vector2.zero,Vector2.zero);bg.anchorMin=Vector2.zero;bg.anchorMax=Vector2.one;bg.offsetMin=bg.offsetMax=Vector2.zero;bg.gameObject.AddComponent<Image>().color=Color.black;
        var content=Rect("Content",canvas.transform,Vector2.zero,new Vector2(900,700));f.content=content.gameObject.AddComponent<CanvasGroup>();
        var symbol=Rect("Animated Symbol",content,new Vector2(0,100),new Vector2(640,640));f.symbol=symbol.gameObject.AddComponent<QuizFeedbackGraphic>();f.symbol.raycastTarget=false;
        var font=AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/TextMesh Pro/Resources/Fonts & Materials/LiberationSans SDF.asset");
        f.title=Text("Result",content,font,new Vector2(0,-60),new Vector2(800,110),68,"Correct");f.title.fontStyle=FontStyles.Bold;
        f.subtitle=Text("Encouragement",content,font,new Vector2(0,-143),new Vector2(850,80),27,"Well done! You made the right choice.");f.subtitle.color=new Color(.68f,.72f,.77f);
        var b=Rect("Continue",content,new Vector2(0,-265),new Vector2(270,64));var graphic=b.gameObject.AddComponent<Image>();graphic.color=new Color(.12f,.15f,.18f);f.continueButton=b.gameObject.AddComponent<Button>();f.continueButton.targetGraphic=graphic;
        Text("Label",b,font,Vector2.zero,new Vector2(270,64),25,"Continue");
        canvas.SetActive(false);
        var prefab=PrefabUtility.SaveAsPrefabAsset(root,path);UnityEngine.Object.DestroyImmediate(root);return prefab;
    }
    static RectTransform Rect(string name,Transform parent,Vector2 position,Vector2 size){var r=new GameObject(name,typeof(RectTransform)).GetComponent<RectTransform>();r.SetParent(parent,false);r.anchorMin=r.anchorMax=r.pivot=new Vector2(.5f,.5f);r.anchoredPosition=position;r.sizeDelta=size;return r;}
    static TMP_Text Text(string name,Transform parent,TMP_FontAsset font,Vector2 pos,Vector2 size,float sizeFont,string value){var t=Rect(name,parent,pos,size).gameObject.AddComponent<TextMeshProUGUI>();t.font=font;t.fontSize=sizeFont;t.text=value;t.alignment=TextAlignmentOptions.Center;t.raycastTarget=false;return t;}
    static GameObject BuildPrinter()
    {
        var root=new GameObject("SM_Printer_Records");
        Material Mat(string n)=>AssetDatabase.LoadAssetAtPath<Material>("Assets/OfficeClassic/Materials/"+n+".mat");
        var white=Mat("MAT_PowderWhite");var dark=Mat("MAT_PlasticBlack");var rubber=Mat("MAT_Rubber");var display=Mat("MAT_Display");
        void Part(string n,Vector3 p,Vector3 s,Material m){var g=new GameObject(n,typeof(MeshFilter),typeof(MeshRenderer));g.transform.SetParent(root.transform,false);g.transform.localPosition=p;g.GetComponent<MeshFilter>().sharedMesh=BevelMesh(s,n);g.GetComponent<MeshRenderer>().sharedMaterial=m;}
        Part("Cabinet",new Vector3(0,.42f,0),new Vector3(.64f,.72f,.62f),white);
        Part("Base",new Vector3(0,.075f,0),new Vector3(.65f,.09f,.62f),dark);
        Part("Output Recess",new Vector3(0,.76f,-.09f),new Vector3(.56f,.17f,.49f),dark);
        Part("Scanner Deck",new Vector3(0,.89f,0),new Vector3(.68f,.085f,.65f),white);
        Part("Scanner Lid",new Vector3(0,.96f,.04f),new Vector3(.61f,.07f,.52f),white);
        Part("Document Feeder",new Vector3(.08f,1.025f,.055f),new Vector3(.43f,.06f,.38f),dark);
        Part("Paper Stack",new Vector3(-.035f,.708f,-.21f),new Vector3(.3f,.022f,.38f),white);
        Part("Control Panel",new Vector3(.23f,.955f,-.31f),new Vector3(.21f,.04f,.13f),dark);
        Part("LCD Screen",new Vector3(.2f,.979f,-.315f),new Vector3(.115f,.006f,.075f),display);
        for(int i=0;i<3;i++){Part("Paper Drawer "+i,new Vector3(0,.25f+i*.14f,-.316f),new Vector3(.55f,.12f,.018f),white);Part("Drawer Grip "+i,new Vector3(0,.275f+i*.14f,-.33f),new Vector3(.15f,.018f,.015f),dark);}
        for(int i=0;i<7;i++)Part("Cooling Vent "+i,new Vector3(-.325f,.48f+i*.025f,.09f),new Vector3(.012f,.009f,.29f),dark);
        for(int i=0;i<4;i++)Part("Rubber Foot "+i,new Vector3(i%2==0?-.24f:.24f,.025f,i<2?-.22f:.22f),new Vector3(.07f,.05f,.07f),rubber);
        for(int i=0;i<3;i++)Part("Control Button "+i,new Vector3(.28f,.981f,-.35f+i*.028f),new Vector3(.019f,.008f,.015f),white);
        var box=root.AddComponent<BoxCollider>();box.center=new Vector3(0,.53f,0);box.size=new Vector3(.66f,1.06f,.65f);
        PrefabUtility.SaveAsPrefabAsset(root,"Assets/Props/OfficeChoice/RecordsPrinter.prefab");return root;
    }
    static Mesh BevelMesh(Vector3 size,string name)
    {
        var path="Assets/Props/OfficeChoice/Printer_"+name.Replace(" ","_")+".asset";var existing=AssetDatabase.LoadAssetAtPath<Mesh>(path);if(existing!=null)return existing;
        var vertices=new List<Vector3>();var normals=new List<Vector3>();var uv=new List<Vector2>();var triangles=new List<int>();
        float radius=Mathf.Min(.016f,Mathf.Min(size.x,Mathf.Min(size.y,size.z))*.22f);var half=size*.5f;var inner=half-Vector3.one*radius;
        Vector3[] ns={Vector3.right,Vector3.left,Vector3.up,Vector3.down,Vector3.forward,Vector3.back};
        foreach(var normal in ns){var u=normal.y==0?Vector3.up:Vector3.forward;var v=Vector3.Cross(normal,u);int start=vertices.Count;const int n=8;
            for(int y=0;y<=n;y++)for(int x=0;x<=n;x++){var raw=Vector3.Scale(normal+u*(x/(float)n*2-1)+v*(y/(float)n*2-1),half);var core=new Vector3(Mathf.Clamp(raw.x,-inner.x,inner.x),Mathf.Clamp(raw.y,-inner.y,inner.y),Mathf.Clamp(raw.z,-inner.z,inner.z));var dir=(raw-core).normalized;vertices.Add(core+dir*radius);normals.Add(dir);uv.Add(new Vector2(x/(float)n,y/(float)n));}
            for(int y=0;y<n;y++)for(int x=0;x<n;x++){int a=start+y*(n+1)+x;triangles.AddRange(new[]{a,a+1,a+n+2,a,a+n+2,a+n+1});}}
        var mesh=new Mesh{name=name};mesh.SetVertices(vertices);mesh.SetNormals(normals);mesh.SetUVs(0,uv);mesh.SetTriangles(triangles,0);mesh.RecalculateBounds();AssetDatabase.CreateAsset(mesh,path);return mesh;
    }
}
