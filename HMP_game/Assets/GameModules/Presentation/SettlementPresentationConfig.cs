using UnityEngine;

namespace HMProtection.Presentation
{
    [CreateAssetMenu(menuName = "HM Protection/Settlement Presentation", fileName = "SettlementPresentation")]
    public sealed class SettlementPresentationConfig : ScriptableObject
    {
        public string brand = "HM PROTECTION  /  OFFICE FIRE SAFETY";
        public string title = "TRAINING COMPLETE";
        public string instructorName = "HAIMO";
        [TextArea] public string instructorRole = "YOUR SAFETY\nINSTRUCTOR";
        public string instructorArtworkResource = "InstructorLesson/Instructor";
        public string badgeArtworkResource = "Settlement/FireSafetyBadge";
        [TextArea] public string perfectCoach = "Great work. You made the right call in every scenario.\nKeep these decisions in mind when it matters.";
        [TextArea] public string practiceCoach = "Every decision is a chance to learn.\nReview the lessons and try again to build safer habits.";
    }
}
