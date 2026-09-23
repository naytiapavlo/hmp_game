using UnityEngine;
namespace HMProtection.Quiz
{
    [RequireComponent(typeof(BoxCollider))]
    public sealed class OfficeChoiceTarget : MonoBehaviour,IInteractable
    {
        public OfficeChoiceInteraction owner;
        public string optionId,prompt;
        public bool correct;
        [Tooltip("Explicit visible object for this interaction. Keeps the hit volume attached when the prop moves.")]
        public Transform visualTarget;
        Transform cachedVisualTarget;
        Renderer[] visualRenderers;
        public void SetArmed(bool armed){SyncVisualBounds();GetComponent<BoxCollider>().enabled=armed;}
        void LateUpdate(){if(visualTarget!=null)SyncVisualBounds();}
        public void SyncVisualBounds()
        {
            if(visualTarget==null)return;
            if(visualRenderers==null || cachedVisualTarget!=visualTarget)
            {cachedVisualTarget=visualTarget;visualRenderers=visualTarget.GetComponentsInChildren<Renderer>(true);}
            bool found=false;Bounds world=default;
            foreach(var r in visualRenderers)
            {
                if(r==null || !r.enabled)continue;
                if(!found){world=r.bounds;found=true;}else world.Encapsulate(r.bounds);
            }
            if(!found)return;
            transform.position=world.center;
            Bounds local=new Bounds(transform.InverseTransformPoint(world.center),Vector3.zero);
            world.Expand(.04f);
            for(int i=0;i<8;i++)local.Encapsulate(transform.InverseTransformPoint(world.center+Vector3.Scale(world.extents,
                new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1))));
            var box=GetComponent<BoxCollider>();box.center=local.center;box.size=local.size;
        }
        public string GetInteractPrompt(Interactor actor)=>owner!=null?owner.GetInteractionPrompt(optionId,prompt):null;
        public void Interact(Interactor actor){if(owner!=null && actor!=null)owner.Complete(this);}
    }
}
