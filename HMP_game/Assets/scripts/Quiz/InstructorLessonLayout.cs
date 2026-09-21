using UnityEngine;

namespace HMProtection.Quiz
{
    /// <summary>Uniform scale for the reference layout within the aspect-fitted safe area.</summary>
    public sealed class InstructorLessonLayout : MonoBehaviour
    {
        void LateUpdate()
        {
            var parent = (RectTransform)transform.parent;
            transform.localScale = Vector3.one * (parent.rect.width / 1920f);
        }
    }
}
