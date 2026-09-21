using UnityEngine;
using UnityEngine.UI;
namespace HMProtection.Quiz
{
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class QuizFeedbackGraphic : MaskableGraphic
    {
        public bool correct;
        [Range(0,1)] public float progress;
        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear(); var accent=correct?new Color(.24f,1f,.66f):new Color(1f,.27f,.32f);
            float reveal=Mathf.Clamp01(progress*2.8f), radius=86;
            for(int glow=3;glow>=0;glow--) {
                Color c=accent; c.a=glow==0?1:.045f;
                int n=Mathf.CeilToInt(80*reveal);
                for(int i=0;i<n;i++) { float a=i/80f*Mathf.PI*2,b=(i+1)/80f*Mathf.PI*2;
                    Line(vh,new Vector2(Mathf.Cos(a),Mathf.Sin(a))*radius,new Vector2(Mathf.Cos(b),Mathf.Sin(b))*radius,glow==0?2:8+glow*7,c); }
            }
            float mark=Mathf.Clamp01((progress-.12f)*3.5f);
            if(correct) { Segment(vh,new Vector2(-37,0),new Vector2(-10,-26),mark*2,accent); Segment(vh,new Vector2(-10,-26),new Vector2(40,32),(mark-.5f)*2,accent); }
            else { Segment(vh,new Vector2(-28,-28),new Vector2(28,28),mark*2,accent); Segment(vh,new Vector2(-28,28),new Vector2(28,-28),(mark-.5f)*2,accent); }
            if(correct && progress>.22f) for(int i=0;i<28;i++) {
                float t=Mathf.Clamp01((progress-.22f)/.78f), a=i*2.39996f;
                Vector2 p=new Vector2(Mathf.Cos(a),Mathf.Sin(a))*(105+t*(110+(i%5)*15)); p.y-=80*t*t;
                Color c=i%3==0?new Color(1,.8f,.33f):accent; c.a=Mathf.Sin(Mathf.PI*t);
                Vector2 d=new Vector2(Mathf.Cos(a+t*8),Mathf.Sin(a+t*8))*5;
                Line(vh,p-d,p+d,3,c);
            }
        }
        static void Segment(VertexHelper vh,Vector2 a,Vector2 b,float t,Color c) { if(t>0) Line(vh,a,Vector2.Lerp(a,b,Mathf.Clamp01(t)),7,c); }
        static void Line(VertexHelper vh,Vector2 a,Vector2 b,float width,Color c)
        {
            var d=(b-a).normalized; var n=new Vector2(-d.y,d.x)*width*.5f; int k=vh.currentVertCount;
            vh.AddVert(a-n,c,Vector2.zero); vh.AddVert(a+n,c,Vector2.zero); vh.AddVert(b+n,c,Vector2.zero); vh.AddVert(b-n,c,Vector2.zero);
            vh.AddTriangle(k,k+1,k+2); vh.AddTriangle(k,k+2,k+3);
        }
    }
}
