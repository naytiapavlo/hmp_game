using UnityEngine;
using UnityEngine.UI;

namespace HMProtection.Quiz
{
    /// <summary>Quiet warm-to-cool backdrop, independent of screen aspect and bitmap resolution.</summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class InstructorBackdropGraphic : MaskableGraphic
    {
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            var r = rectTransform.rect;
            mesh.AddVert(new Vector3(r.xMin, r.yMin), new Color(.035f, .041f, .052f), Vector2.zero);
            mesh.AddVert(new Vector3(r.xMin, r.yMax), new Color(.12f, .085f, .091f), Vector2.up);
            mesh.AddVert(new Vector3(r.xMax, r.yMax), new Color(.062f, .075f, .093f), Vector2.one);
            mesh.AddVert(new Vector3(r.xMax, r.yMin), new Color(.025f, .030f, .040f), Vector2.right);
            mesh.AddTriangle(0, 1, 2); mesh.AddTriangle(2, 3, 0);
        }
    }
}
