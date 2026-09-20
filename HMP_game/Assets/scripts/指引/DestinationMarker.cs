// 路线指引系统——目的地标记。
// 设计文档：《路线指引系统架构设计.md》4.2 节；视觉基准 0c7a3b35-…png。
//
// 表现：目的地上方 2.3m 悬浮发光倒三角（尖朝下，缓慢浮动）+ 上方目的地名称文字（始终面向玩家）。
// 中文渲染策略：若 TMP 默认字体包含中文字集则用 TextMeshPro（更精致），
// 否则回退 legacy TextMesh + 系统中文字体（微软雅黑/黑体，动态字体按需光栅化），
// 保证中文一定显示（文档风险条目 5 的落地方案）。
using TMPro;
using UnityEngine;

public class DestinationMarker : MonoBehaviour
{
    [Header("标记参数")]
    [Tooltip("倒三角悬浮高度（目的地上方，米）")]
    [SerializeField] private float height = 2.3f;
    [Tooltip("倒三角尺寸（半宽，米）")]
    [SerializeField] private float triangleSize = 0.45f;
    [Tooltip("浮动动画幅度（米）")]
    [SerializeField] private float bobAmplitude = 0.12f;
    [Tooltip("浮动动画速度")]
    [SerializeField] private float bobSpeed = 1.2f;
    [Tooltip("标记材质（HMProtection/GuidanceArrow，装配工具自动赋值）")]
    [SerializeField] private Material markerMaterial;

    private Transform triangle;       // 倒三角（程序化网格）
    private Transform label;          // 名称文字
    private Vector3 targetPosition;

    private void Awake()
    {
        BuildTriangle();
        BuildLabel();
        Hide();
    }

    /// <summary>设置目的地（世界坐标）与显示名称</summary>
    public void SetTarget(Vector3 worldPosition, string labelText)
    {
        targetPosition = worldPosition;
        transform.position = worldPosition + Vector3.up * height;
        if (label != null) SetLabelText(labelText);
        Show();
    }

    public void Show() => gameObject.SetActive(true);

    public void Hide() => gameObject.SetActive(false);

    private void Update()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        // 倒三角 Billboard：只绕 Y 轴旋转朝向玩家（保持倒立姿态不倾倒），
        // 从任何侧面看都是一个完整的三角形，不再侧对成一条线
        if (triangle != null)
        {
            Vector3 flat = cam.transform.position - transform.position;
            flat.y = 0f;
            if (flat.sqrMagnitude > 0.001f)
                triangle.rotation = Quaternion.LookRotation(flat.normalized, Vector3.up);

            // 缓慢上下浮动
            Vector3 local = triangle.localPosition;
            local.y = Mathf.Sin(Time.time * bobSpeed) * bobAmplitude;
            triangle.localPosition = local;
        }

        // 文字 Billboard：完全面向玩家相机（带俯仰，仰视也清晰可读）
        if (label != null)
        {
            Vector3 toCam = label.position - cam.transform.position;
            if (toCam.sqrMagnitude > 0.001f)
                label.rotation = Quaternion.LookRotation(toCam.normalized, Vector3.up);
        }
    }

    // 程序化倒三角网格：尖朝下，uv.x 尾(上边)0 → 尖(下)1，与箭头共用着色器（Cull Off 双面）
    private void BuildTriangle()
    {
        if (transform.Find("三角") != null) { triangle = transform.Find("三角"); return; }
        var go = new GameObject("三角");
        go.transform.SetParent(transform, false);
        float s = triangleSize;
        var mesh = new Mesh { name = "GuidanceMarkerTriangle" };
        mesh.vertices = new[]
        {
            new Vector3(-s, s * 0.7f, 0f),
            new Vector3(s, s * 0.7f, 0f),
            new Vector3(0f, -s * 0.9f, 0f),
        };
        mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(1f, 0.5f) };
        mesh.triangles = new[] { 0, 1, 2 };
        mesh.RecalculateBounds();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        if (markerMaterial == null)
        {
            Shader shader = Shader.Find("HMProtection/GuidanceArrow");
            if (shader != null) markerMaterial = new Material(shader);
        }
        if (markerMaterial != null) mr.sharedMaterial = markerMaterial;
        triangle = go.transform;
    }

    // 名称文字：TMP 默认字体含中文则 TextMeshPro，否则 legacy TextMesh + 系统中文字体
    private void BuildLabel()
    {
        if (transform.Find("文字") != null) { label = transform.Find("文字"); return; }
        var go = new GameObject("文字");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(0f, triangleSize * 1.6f, 0f);

        TMP_FontAsset tmpFont = TMP_Settings.defaultFontAsset;
        if (tmpFont != null && tmpFont.HasCharacter('灭'))
        {
            var tmp = go.AddComponent<TextMeshPro>();
            tmp.fontSize = 4f;   // TMP 世界文字 fontSize≈字高(米)×10
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
            tmp.text = "";
        }
        else
        {
            // 系统中文字体（动态光栅化，中文必有）；项目若后续导入中文字体资产可整体替换
            Font osFont = Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft YaHei", "微软雅黑", "SimHei", "Arial" }, 32);
            var textMesh = go.AddComponent<TextMesh>();
            textMesh.font = osFont;
            // 关键：脚本创建的 TextMesh 不会自动把 MeshRenderer 材质设为字体材质
            // （编辑器菜单创建才会自动接），缺了这步文字渲染成乱码
            textMesh.GetComponent<MeshRenderer>().sharedMaterial = osFont.material;
            textMesh.fontSize = 32;
            textMesh.characterSize = 0.1f;   // 字高约 0.32m
            textMesh.anchor = TextAnchor.MiddleCenter;
            textMesh.alignment = TextAlignment.Center;
            textMesh.color = Color.white;
            textMesh.text = "";
        }
        label = go.transform;
    }

    private void SetLabelText(string text)
    {
        if (label == null) return;
        if (label.TryGetComponent<TextMeshPro>(out var tmp)) tmp.text = text;
        else if (label.TryGetComponent<TextMesh>(out var tm)) tm.text = text;
    }
}
