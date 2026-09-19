using UnityEngine;
using UnityEngine.UI;

namespace HMProtection.UI
{
    /// <summary>Resolution-independent rounded menu button, without baked-in text.</summary>
    [AddComponentMenu("UI/HM Protection/Menu Button Graphic")]
    [ExecuteAlways]
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class MenuButtonGraphic : MaskableGraphic
    {
        public enum ButtonStyle { Primary, Secondary, Quiet }
        [SerializeField] private ButtonStyle style;
        [SerializeField, Min(1)] private float cornerRadius = 19f;
        public ButtonStyle Style { get => style; set { style = value; SetVerticesDirty(); } }
        public override Texture mainTexture => Texture2D.whiteTexture;

        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            Rect rect = GetPixelAdjustedRect();
            if (rect.width <= 0 || rect.height <= 0) return;
            bool primary = style == ButtonStyle.Primary;
            bool quiet = style == ButtonStyle.Quiet;
            // Each inset is a separate rounded gradient surface: outer edge, rim, face.
            Layer(mesh, rect, cornerRadius, Hex(quiet ? "515356" : "730B10"), Hex(quiet ? "141618" : "400609"));
            Layer(mesh, Inset(rect, 2), cornerRadius - 2,
                Hex(quiet ? "EEEEEE" : "FF282C"), Hex(quiet ? "909395" : "B90910"));
            Layer(mesh, Inset(rect, 5), cornerRadius - 5,
                Hex(primary ? "A90009" : "151719"), Hex(primary ? "710005" : "080A0C"));
            Layer(mesh, Inset(rect, 7), cornerRadius - 7,
                Hex(primary ? "F3151C" : "45484B"), Hex(primary ? "A10008" : "1A1C1E"));
            // Thin specular edge above the face, kept subtle to match the reference.
        }

        private static Rect Inset(Rect r, float amount) =>
            new Rect(r.x + amount, r.y + amount, Mathf.Max(0, r.width - 2 * amount), Mathf.Max(0, r.height - 2 * amount));

        private static Color Hex(string hex)
        {
            ColorUtility.TryParseHtmlString("#" + hex, out Color result);
            return result;
        }

        private void Layer(VertexHelper mesh, Rect r, float radius, Color top, Color bottom)
        {
            if (r.width <= 0 || r.height <= 0) return;
            radius = Mathf.Clamp(radius, 0, Mathf.Min(r.width, r.height) * .5f);
            const int steps = 12;
            int start = mesh.currentVertCount;
            Add(mesh, r.center, r, top, bottom);
            for (int corner = 0; corner < 4; corner++)
            {
                Vector2 centre = new Vector2(corner == 0 || corner == 3 ? r.xMax - radius : r.xMin + radius,
                    corner < 2 ? r.yMax - radius : r.yMin + radius);
                for (int point = 0; point <= steps; point++)
                {
                    float a = (corner * 90f + point * 90f / steps) * Mathf.Deg2Rad;
                    Add(mesh, centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * radius, r, top, bottom);
                }
            }
            int count = 4 * (steps + 1);
            for (int i = 0; i < count; i++) mesh.AddTriangle(start, start + 1 + (i + 1) % count, start + 1 + i);
        }

        private void Add(VertexHelper mesh, Vector2 position, Rect r, Color top, Color bottom)
        {
            Color tint = Color.Lerp(bottom, top, Mathf.InverseLerp(r.yMin, r.yMax, position.y)) * color;
            mesh.AddVert(position, tint, Vector2.zero);
        }
    }
}
