using UnityEngine;
using UnityEngine.UI;

namespace HMProtection.Quiz
{
    // Samples the supplied PNG unchanged. The centre stretches, the circular badge/caps
    // retain their proportions, and the original lower-right tail bends to the target.
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class SceneChoiceArtwork : MaskableGraphic
    {
        public Texture2D artwork;
        public Vector2 tailTip;
        public override Texture mainTexture => artwork != null ? artwork : s_WhiteTexture;
        public void PointAt(Vector2 point) { if ((tailTip - point).sqrMagnitude < .01f) return; tailTip = point; SetVerticesDirty(); }
        Vector2 BodyPoint(float x, float y)
        {
            Rect r = rectTransform.rect;
            float left = Mathf.Min(r.height * 340 / 370, r.width * .30f);
            float right = Mathf.Min(r.height * 302 / 370, r.width * .27f);
            float px = x < 340 ? r.xMin + x / 340 * left : x > 1370 ? r.xMax - (1672 - x) / 302 * right : Mathf.Lerp(r.xMin + left, r.xMax - right, (x - 340) / 1030);
            return new Vector2(px, r.yMax - (y - 255) / 370 * r.height);
        }
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            // Extra transparent margins retain all glow; tail deformation uses the source artwork.
            const int nx = 84, ny = 40;
            Vector2 originalTip = BodyPoint(1632, 755);
            for (int iy = 0; iy <= ny; iy++)
            {
                float y = Mathf.Lerp(200, 820, iy / (float)ny);
                for (int ix = 0; ix <= nx; ix++)
                {
                    float x = 1672 * ix / (float)nx;
                    Vector2 p = BodyPoint(x, y);
                    float influence = Mathf.Clamp01((y - 570) / 185) * Mathf.Clamp01((x - 1360) / 180);
                    p += (tailTip - originalTip) * influence;
                    vh.AddVert(p, color, new Vector2(x / 1672, 1 - y / 941));
                }
            }
            for (int y = 0; y < ny; y++) for (int x = 0; x < nx; x++)
            {
                int a = y * (nx + 1) + x;
                vh.AddTriangle(a, a + 1, a + nx + 1);
                vh.AddTriangle(a + 1, a + nx + 2, a + nx + 1);
            }
        }
    }
}
