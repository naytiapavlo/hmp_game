using UnityEngine;
using UnityEngine.UI;

namespace HMProtection.UI
{
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class SettlementPanelGraphic : MaskableGraphic
    {
        public bool Accented;
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear(); var r = rectTransform.rect;
            Layer(mesh, r, 24f, Accented ? new Color(.72f, .11f, .14f) : new Color(.25f, .27f, .31f),
                Accented ? new Color(.29f, .06f, .09f) : new Color(.12f, .14f, .18f));
            r = new Rect(r.x + 2, r.y + 2, r.width - 4, r.height - 4);
            Layer(mesh, r, 22f, new Color(.10f, .115f, .14f), new Color(.052f, .06f, .075f));
        }
        static void Layer(VertexHelper mesh, Rect r, float radius, Color top, Color bottom)
        {
            if (r.width <= 0f || r.height <= 0f) return;
            radius = Mathf.Min(radius, Mathf.Min(r.width, r.height) * .5f);
            int start = mesh.currentVertCount; mesh.AddVert(r.center, Color.Lerp(bottom, top, .5f), Vector2.zero);
            const int segments = 12;
            for (int corner = 0; corner < 4; corner++)
            {
                Vector2 center = new Vector2(corner == 0 || corner == 3 ? r.xMax - radius : r.xMin + radius,
                    corner < 2 ? r.yMax - radius : r.yMin + radius);
                for (int i = 0; i <= segments; i++)
                {
                    float angle = (corner * 90f + i * 90f / segments) * Mathf.Deg2Rad;
                    Vector2 p = center + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
                    mesh.AddVert(p, Color.Lerp(bottom, top, Mathf.InverseLerp(r.yMin, r.yMax, p.y)), Vector2.zero);
                }
            }
            const int count = 4 * (segments + 1);
            for (int i = 0; i < count; i++) mesh.AddTriangle(start, start + 1 + (i + 1) % count, start + 1 + i);
        }
    }
}
