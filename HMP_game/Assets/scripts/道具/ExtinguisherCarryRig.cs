using UnityEngine;

/// <summary>Right hand carries the upright cylinder; left hand aims the independent nozzle.
/// Deforms a runtime copy of the existing hose mesh. Never edits the imported mesh or hand rig.</summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(10000)]
public sealed class ExtinguisherCarryRig : MonoBehaviour
{
    private const int Sections = 32;
    private PickupItem pickup;
    private Transform nozzle;
    private Transform outlet;
    private MeshFilter hose;
    private Mesh originalMesh, bentMesh;
    private Vector3 nozzlePosition, nozzleScale;
    private Quaternion nozzleRotation;
    private Vector3 handleGrip, nozzleGrip, outletAxis, hoseEndInNozzle;
    private Vector3[] restVertices, vertices, centers;
    private float[] fractions;
    private bool ready, holding;
    private MeshFilter[] geometry;

    public Vector3 HandlePosition => transform.TransformPoint(handleGrip);
    public Vector3 NozzleGripPosition => nozzle != null ? nozzle.TransformPoint(nozzleGrip) : transform.position;
    public Vector3 OutletPosition => outlet != null ? outlet.position : NozzleGripPosition;

    private void Awake() { pickup = GetComponent<PickupItem>(); }

    // Lazily called only on pickup: all transforms are still in their authored rest pose.
    public bool Initialize()
    {
        if (ready) return true;
        nozzle = PropGeometry.FindDeep(transform, "NozzleBody");
        outlet = PropGeometry.FindDeep(transform, "Nozzle");
        Transform hoseNode = PropGeometry.FindDeep(transform, "Hose");
        Transform handle = PropGeometry.FindDeep(transform, "CarryHandle");
        Transform lever = PropGeometry.FindDeep(transform, "SqueezeLever");
        if (nozzle == null || outlet == null || hoseNode == null || handle == null) return false;
        hose = hoseNode.GetComponent<MeshFilter>();
        if (hose == null || hose.sharedMesh == null || !hose.sharedMesh.isReadable)
        {
            Debug.LogError("[ExtinguisherCarryRig] Hose mesh needs Read/Write enabled. Re-run extinguisher setup.", this);
            return false;
        }
        handleGrip = CenterIn(handle, transform);
        if (lever != null) handleGrip = (handleGrip + CenterIn(lever, transform)) * .5f;
        nozzleGrip = CenterIn(nozzle, nozzle);
        outletAxis = (nozzle.InverseTransformPoint(outlet.position) - nozzleGrip).normalized;
        if (outletAxis.sqrMagnitude < .5f) return false;
        nozzlePosition = nozzle.localPosition; nozzleRotation = nozzle.localRotation; nozzleScale = nozzle.localScale;
        originalMesh = hose.sharedMesh;
        Vector3[] source = originalMesh.vertices;
        restVertices = new Vector3[source.Length]; vertices = new Vector3[source.Length]; fractions = new float[source.Length];
        float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
        for (int i = 0; i < source.Length; i++)
        {
            restVertices[i] = transform.InverseTransformPoint(hose.transform.TransformPoint(source[i]));
            lo = Mathf.Min(lo, restVertices[i].y); hi = Mathf.Max(hi, restVertices[i].y);
        }
        if (hi - lo < .01f) return false;
        centers = new Vector3[Sections + 1];
        for (int s = 0; s <= Sections; s++)
        {
            float y = Mathf.Lerp(hi, lo, s / (float)Sections);
            float nearest = float.PositiveInfinity; Vector3 closest = Vector3.zero;
            Vector3 min = Vector3.one * float.PositiveInfinity, max = Vector3.one * float.NegativeInfinity;
            int count = 0;
            foreach (Vector3 v in restVertices)
            {
                float d = Mathf.Abs(v.y - y);
                if (d < nearest) { nearest = d; closest = v; }
                if (d <= (hi - lo) / Sections * .65f) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); count++; }
            }
            centers[s] = count > 0 ? (min + max) * .5f : closest;
            centers[s].y = y;
        }
        for (int i = 0; i < source.Length; i++) fractions[i] = Mathf.Clamp01((hi - restVertices[i].y) / (hi - lo));
        hoseEndInNozzle = nozzle.InverseTransformPoint(transform.TransformPoint(centers[Sections]));
        bentMesh = Instantiate(originalMesh); bentMesh.name = "Hose (runtime carry)"; bentMesh.MarkDynamic();
        geometry = GetComponentsInChildren<MeshFilter>();
        ready = true;
        return true;
    }

    private static Vector3 CenterIn(Transform part, Transform target)
    {
        MeshFilter filter = part.GetComponent<MeshFilter>();
        return filter != null && filter.sharedMesh != null
            ? target.InverseTransformPoint(part.TransformPoint(filter.sharedMesh.bounds.center))
            : target.InverseTransformPoint(part.position);
    }

    private void LateUpdate()
    {
        if (pickup == null) pickup = GetComponent<PickupItem>();
        if (pickup == null || !pickup.IsHeld) { Restore(); return; }
        Interactor actor = pickup.Carrier;
        if (actor == null) return;
        ArmsPitchFollow arms = actor.Hands != null ? actor.Hands.GetComponent<ArmsPitchFollow>() : null;
        Transform view = actor.ViewTransform;
        Camera camera = view != null ? view.GetComponent<Camera>() : null;
        if (arms != null) arms.PrepareExtinguisherPose(camera);
        if (arms == null || !arms.TryGetGripPoint(true, out Vector3 right)
                         || !arms.TryGetGripPoint(false, out Vector3 left)) return;
        Vector3 aim = view != null ? view.forward : actor.transform.forward;
        if (!ApplyPose(right, left, aim, actor.transform.forward)) return;
        if (camera == null) return;
        Vector3 correction = GetViewCorrection(camera);
        if (correction.sqrMagnitude > 1e-8f)
        {
            // Move hands with the prop, never break either grip to clear the camera.
            // ArmsPitchFollow resets its base pose before us next frame, so no drift accumulates.
            arms.ShiftHeldPose(correction);
            ApplyPose(right + correction, left + correction, aim, actor.transform.forward);
        }
    }

    /// <summary>Shared runtime/QA seam. Both contact points are world coordinates.
    /// Cylinder yaw follows the body, with no pitch or roll; nozzle follows aim.</summary>
    public bool ApplyPose(Vector3 right, Vector3 left, Vector3 aim, Vector3 bodyForward)
    {
        if (!Initialize()) return false;
        bodyForward = Vector3.ProjectOnPlane(bodyForward, Vector3.up);
        if (bodyForward.sqrMagnitude < 1e-6f) bodyForward = Vector3.forward;
        transform.rotation = Quaternion.LookRotation(bodyForward.normalized, Vector3.up);
        transform.position += right - HandlePosition;
        if (aim.sqrMagnitude < 1e-6f) aim = bodyForward;
        Vector3 aimUp = Mathf.Abs(Vector3.Dot(aim.normalized, Vector3.up)) > .99f ? transform.forward : Vector3.up;
        nozzle.rotation = Quaternion.LookRotation(aim.normalized, aimUp) * Quaternion.FromToRotation(outletAxis, Vector3.forward);
        nozzle.position += left - NozzleGripPosition;
        hose.sharedMesh = bentMesh;
        BendHose();
        holding = true;
        return true;
    }

    /// <summary>Frame the handle near viewport y=.30, with geometry below y=.48 and beyond the near plane.
    /// Evaluate oriented mesh bounds, including the deformed hose, rather than only the handle.
    /// The returned translation must be applied to BOTH hands and prop in the same frame.</summary>
    public Vector3 GetViewCorrection(Camera camera)
    {
        if (!ready || camera == null) return Vector3.zero;
        float halfFovTan = Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f);
        float slope = (2f * .48f - 1f) * halfFovTan;
        float minDepth = float.PositiveInfinity, maxPlane = float.NegativeInfinity;
        foreach (MeshFilter filter in geometry)
        {
            if (filter == null || filter.sharedMesh == null) continue;
            Bounds bounds = filter.sharedMesh.bounds;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 p = bounds.center + Vector3.Scale(bounds.extents,
                    new Vector3((corner&1)==0?-1:1, (corner&2)==0?-1:1, (corner&4)==0?-1:1));
                p = ViewPoint(camera, filter.transform.TransformPoint(p));
                minDepth = Mathf.Min(minDepth, p.z);
                maxPlane = Mathf.Max(maxPlane, p.y - slope * p.z);
            }
        }
        // Include a palm/finger envelope, since model bounds alone omit the gripping hands.
        for (int hand = 0; hand < 2; hand++)
        {
            Vector3 p = ViewPoint(camera, hand == 0 ? HandlePosition : NozzleGripPosition);
            minDepth = Mathf.Min(minDepth, p.z - .10f);
            // Height comes from actual geometry; a 10cm phantom palm above the left grip
            // previously pushed the right hand and bottle off-screen.
        }
        float forward = Mathf.Max(0f, Mathf.Max(.45f, camera.nearClipPlane + .10f) - minDepth);
        Vector3 handle = ViewPoint(camera, HandlePosition);
        float targetSlope = (2f * .30f - 1f) * halfFovTan;
        // If the upright bottle projects above the framing limit at steep pitch, move the
        // complete pose farther forward instead of sacrificing visible handle height.
        float framingDepth = (maxPlane + targetSlope * handle.z - handle.y) / (slope - targetSlope);
        forward = Mathf.Max(forward, framingDepth);
        float vertical = targetSlope * (handle.z + forward) - handle.y;
        return camera.cameraToWorldMatrix.MultiplyVector(new Vector3(0f, vertical, -forward));
    }

    private static Vector3 ViewPoint(Camera camera, Vector3 world)
    {
        // A camera ignores Transform scale for projection. Its scaled parent may also shear.
        // Use the actual rendering view matrix, never InverseTransformPoint/TransformVector.
        Vector3 p = camera.worldToCameraMatrix.MultiplyPoint3x4(world);
        p.z = -p.z;
        return p;
    }

    private void BendHose()
    {
        Vector3 start = centers[0];
        Vector3 end = transform.InverseTransformPoint(nozzle.TransformPoint(hoseEndInNozzle));
        Vector3 startDir = (centers[1] - centers[0]).normalized;
        Vector3 endDir = transform.InverseTransformDirection(nozzle.TransformDirection(outletAxis)).normalized;
        float handle = Mathf.Clamp(Vector3.Distance(start, end) * .35f, .07f, .20f);
        Vector3 a = start + startDir * handle;
        Vector3 b = end - endDir * handle + Vector3.down * .06f;
        for (int i = 0; i < vertices.Length; i++)
        {
            float t = fractions[i], u = 1f - t;
            float sample = t * Sections; int s = Mathf.Min((int)sample, Sections - 1);
            Vector3 restCenter = Vector3.Lerp(centers[s], centers[s + 1], sample - s);
            Vector3 oldTangent = (centers[s + 1] - centers[s]).normalized;
            Vector3 position = u*u*u*start + 3*u*u*t*a + 3*u*t*t*b + t*t*t*end;
            Vector3 tangent = 3*u*u*(a-start) + 6*u*t*(b-a) + 3*t*t*(end-b);
            Quaternion turn = Quaternion.FromToRotation(oldTangent, tangent.normalized);
            Vector3 p = position + turn * (restVertices[i] - restCenter);
            vertices[i] = hose.transform.InverseTransformPoint(transform.TransformPoint(p));
        }
        bentMesh.vertices = vertices; bentMesh.RecalculateNormals(); bentMesh.RecalculateBounds();
    }

    public void Restore()
    {
        if (!ready || !holding) return;
        nozzle.localPosition = nozzlePosition; nozzle.localRotation = nozzleRotation; nozzle.localScale = nozzleScale;
        hose.sharedMesh = originalMesh;
        holding = false;
    }
    private void OnDisable() { Restore(); }
    private void OnDestroy()
    {
        Restore();
        if (bentMesh != null)
        {
            if (Application.isPlaying) Destroy(bentMesh); else DestroyImmediate(bentMesh);
        }
    }
}
