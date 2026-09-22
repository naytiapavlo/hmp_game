using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
namespace HMProtection.Quiz
{
    public sealed class OfficeChoiceInteraction : MonoBehaviour
    {
        public OfficeFireChoiceFlow flow;
        public QuizResultFeedback feedback;
        public OfficeChoiceTarget[] targets;
        [Tooltip("选择题限时进度条；超时由它回调 OnTimerExpired 跳 Incorrect。")]
        public QuizTimerHUD timer;
        public InstructorLessonView instructor;
        public bool IsPresentationActive => pendingReview || feedback.IsShowing || (instructor != null && instructor.IsShowing);
        public UnityEvent<string,bool> onActionCompleted=new UnityEvent<string,bool>();
        public bool Completed {get;private set;}
        public string ArmedOption {get;private set;}
        public string ArmedTargetId {get;private set;}
        HMProtection.Core.LevelFlowRunner runner; bool pausedByUs, pendingReview;
        void Awake(){if(instructor==null)instructor=GetComponent<InstructorLessonView>();if(instructor==null)instructor=gameObject.AddComponent<InstructorLessonView>();}
        void OnEnable(){flow.RouteStarted+=Arm;flow.QuestionPresented+=ResetQuestion;feedback.onDismissed.AddListener(ShowReview);instructor.Closing+=PrepareTransition;instructor.Closed+=Resume;if(timer!=null)timer.TimedOut+=OnTimerExpired;}
        void ResetQuestion(){Completed=false;ArmedOption=null;ArmedTargetId=null;foreach(var t in targets)t.SetArmed(false);}
        void Start(){foreach(var t in targets)t.SetArmed(false);}
        void Arm(OfficeFireChoiceFlow.Destination d){Completed=false;ArmedOption=d.optionId;ArmedTargetId=string.IsNullOrEmpty(d.targetId)?d.optionId:d.targetId;foreach(var t in targets)t.SetArmed(t.optionId==ArmedTargetId);}
        public bool CanInteract(string id)=>isActiveAndEnabled && !Completed && !feedback.IsShowing && !flow.IsQuestionActive && ArmedTargetId==id;
        public string GetInteractionPrompt(string targetId,string fallback)
        {
            if(!CanInteract(targetId))return null;
            var option=flow.CurrentQuestion?.options.FirstOrDefault(o=>o.id==ArmedOption);
            return string.IsNullOrEmpty(option?.interactionPrompt)?fallback:option.interactionPrompt;
        }
        public void Complete(OfficeChoiceTarget target)
        {
            if(target==null || target.owner!=this || !CanInteract(target.optionId))return;
            if(timer!=null && timer.ExpireIfDue())return;
            string correctId=flow.CurrentQuestion?.correctOptionId;
            bool correct=string.IsNullOrEmpty(correctId)?target.correct:ArmedOption==correctId;
            if(!feedback.Show(correct))return;
            if(timer!=null)timer.StopCountdown();
            Completed=true;pendingReview=true;foreach(var t in targets)t.SetArmed(false);flow.guidance.HideRoute();
            runner=FindAnyObjectByType<HMProtection.Core.LevelFlowRunner>();pausedByUs=runner!=null && runner.IsRunning && !runner.IsPaused;
            if(pausedByUs)runner.SetPaused(true);
            onActionCompleted.Invoke(ArmedOption,correct);
        }
        // 限时结束仍未与所选物体交互：收起题目（若还在俯视），按错误结果弹出 Incorrect。
        void OnTimerExpired()
        {
            if(Completed || feedback.IsShowing)return;
            if(flow.IsQuestionActive)flow.CancelQuestion();
            if(!feedback.Show(false))return;
            Completed=true;pendingReview=true;ArmedOption=null;ArmedTargetId=null;foreach(var t in targets)t.SetArmed(false);flow.guidance.HideRoute();
            runner=FindAnyObjectByType<HMProtection.Core.LevelFlowRunner>();pausedByUs=runner!=null && runner.IsRunning && !runner.IsPaused;
            if(pausedByUs)runner.SetPaused(true);
            onActionCompleted.Invoke(string.Empty,false);
        }
        void ShowReview()
        {
            if(!pendingReview)return;
            if(SceneChoiceCatalog.TryLoad(out var catalog,out var error))
            {
                var lesson=catalog.Find(flow.questionId)?.instructor;
                if(lesson!=null && lesson.enabled && instructor.Show(lesson,feedback.LastCorrect))return;
            }
            else Debug.LogWarning("[InstructorLesson] "+error,this);
            Resume();
        }
        void PrepareTransition(){if(isActiveAndEnabled && pendingReview && runner!=null)runner.PrepareNextQuizTransition();}
        void Resume(){PrepareTransition();pendingReview=false;if(pausedByUs && runner!=null)runner.SetPaused(false);pausedByUs=false;}
        void OnDisable(){if(timer!=null){timer.TimedOut-=OnTimerExpired;timer.StopCountdown();}if(flow!=null){flow.RouteStarted-=Arm;flow.QuestionPresented-=ResetQuestion;}if(feedback!=null){feedback.onDismissed.RemoveListener(ShowReview);feedback.Dismiss();}if(instructor!=null){instructor.Closing-=PrepareTransition;instructor.Closed-=Resume;instructor.Dismiss();}Resume();if(targets!=null)foreach(var t in targets)if(t!=null)t.SetArmed(false);}
    }
}
