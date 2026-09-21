using UnityEngine;
namespace HMProtection.Quiz
{
    [RequireComponent(typeof(BoxCollider))]
    public sealed class OfficeChoiceTarget : MonoBehaviour,IInteractable
    {
        public OfficeChoiceInteraction owner;
        public string optionId,prompt;
        public bool correct;
        public void SetArmed(bool armed){GetComponent<BoxCollider>().enabled=armed;}
        public string GetInteractPrompt(Interactor actor)=>owner!=null?owner.GetInteractionPrompt(optionId,prompt):null;
        public void Interact(Interactor actor){if(owner!=null && actor!=null)owner.Complete(this);}
    }
}
