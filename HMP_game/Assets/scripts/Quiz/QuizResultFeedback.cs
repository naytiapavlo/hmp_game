using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
namespace HMProtection.Quiz
{
    /// <summary>Reusable unscaled black-screen feedback. Scene logic supplies only correctness and optional copy.</summary>
    public sealed class QuizResultFeedback : MonoBehaviour
    {
        public CanvasGroup overlay, content;
        public QuizFeedbackGraphic symbol;
        public TMP_Text title, subtitle;
        public UnityEngine.UI.Button continueButton;
        public float blackoutDuration=.3f, animationDuration=1.65f;
        public UnityEvent<bool> onPresented=new UnityEvent<bool>();
        public UnityEvent onDismissed=new UnityEvent();
        public bool IsShowing {get;private set;}
        public bool LastCorrect {get;private set;}
        public GameObject fallbackEventSystem;
        readonly Dictionary<Behaviour,bool> controls=new Dictionary<Behaviour,bool>();
        readonly Dictionary<GameObject,bool> hud=new Dictionary<GameObject,bool>();
        CursorLockMode oldLock; bool oldCursor,oldFallback; GameObject oldSelection;
        Coroutine routine;
        void Awake(){overlay.gameObject.SetActive(false);continueButton.onClick.AddListener(Dismiss);}
        public bool Show(bool correct,string message=null)
        {
            if(IsShowing || !isActiveAndEnabled) return false;
            IsShowing=true;LastCorrect=correct;controls.Clear();hud.Clear();
            oldLock=Cursor.lockState;oldCursor=Cursor.visible;
            oldSelection=EventSystem.current!=null?EventSystem.current.currentSelectedGameObject:null;
            foreach(var root in gameObject.scene.GetRootGameObjects()) {
                foreach(var b in root.GetComponentsInChildren<Behaviour>(true)) if(b is body || b is Interactor) {controls[b]=b.enabled;b.enabled=false;}
                foreach(var h in root.GetComponentsInChildren<InteractionHUD>(true)) {hud[h.gameObject]=h.gameObject.activeSelf;h.gameObject.SetActive(false);}
            }
            if (EventSystem.current == null && fallbackEventSystem == null)
            {
                fallbackEventSystem = new GameObject("Feedback EventSystem");
                fallbackEventSystem.SetActive(false);
                fallbackEventSystem.transform.SetParent(transform, false);
                fallbackEventSystem.AddComponent<EventSystem>();
                fallbackEventSystem.AddComponent<InputSystemUIInputModule>();
            }
            if(fallbackEventSystem!=null){oldFallback=fallbackEventSystem.activeSelf;if(EventSystem.current==null)fallbackEventSystem.SetActive(true);}
            Cursor.lockState=CursorLockMode.None;Cursor.visible=true;
            symbol.correct=correct;symbol.progress=0;symbol.SetVerticesDirty();
            title.text=correct?"Correct":"Incorrect";
            subtitle.text=message??(correct?"Well done! You made the right choice.":"That was not the right choice. Keep learning.");
            title.color=correct?new Color(.4f,1f,.73f):new Color(1f,.4f,.43f);
            overlay.alpha=0;content.alpha=0;continueButton.gameObject.SetActive(false);overlay.gameObject.SetActive(true);
            routine=StartCoroutine(Animate());onPresented.Invoke(correct);return true;
        }
        // No-argument entry points can also be wired directly from Inspector UnityEvents.
        public void ShowCorrect() => Show(true);
        public void ShowIncorrect() => Show(false);
        IEnumerator Animate()
        {
            float time=0;
            while(time<blackoutDuration){time+=Time.unscaledDeltaTime;overlay.alpha=Mathf.Clamp01(time/Mathf.Max(.01f,blackoutDuration));yield return null;}
            overlay.alpha=1; time=0;
            var rect=(RectTransform)content.transform;
            while(time<animationDuration){
                time+=Time.unscaledDeltaTime;float t=Mathf.Clamp01(time/Mathf.Max(.01f,animationDuration));
                content.alpha=Mathf.Clamp01(t*5);symbol.progress=t;symbol.SetVerticesDirty();
                float pop=1-Mathf.Pow(1-Mathf.Clamp01(t*3),3);
                rect.localScale=Vector3.one*(.88f+.12f*pop+.045f*Mathf.Sin(t*Mathf.PI*3)*(1-t));
                rect.anchoredPosition=new Vector2(LastCorrect?0:Mathf.Sin(t*45)*13*(1-t)*(1-t),0);
                yield return null;
            }
            content.alpha=1;rect.localScale=Vector3.one;rect.anchoredPosition=Vector2.zero;
            continueButton.gameObject.SetActive(true);EventSystem.current?.SetSelectedGameObject(continueButton.gameObject);routine=null;
        }
        public void Dismiss()
        {
            if(!IsShowing)return;
            if(routine!=null)StopCoroutine(routine);routine=null;overlay.gameObject.SetActive(false);Restore();onDismissed.Invoke();
        }
        void Restore(){
            foreach(var p in controls)if(p.Key!=null)p.Key.enabled=p.Value;
            foreach(var p in hud)if(p.Key!=null)p.Key.SetActive(p.Value);
            if(fallbackEventSystem!=null)fallbackEventSystem.SetActive(oldFallback);
            Cursor.lockState=oldLock;Cursor.visible=oldCursor;
            EventSystem.current?.SetSelectedGameObject(oldSelection);controls.Clear();hud.Clear();IsShowing=false;
        }
        void OnDisable(){if(IsShowing)Dismiss();}
        void OnDestroy(){if(continueButton!=null)continueButton.onClick.RemoveListener(Dismiss);}
    }
}
