using UnityEngine;

/// <summary>Right hand carries the upright cylinder; left hand supports a rigid straight tube.
/// The tube axis follows gaze; the supporting hand follows the tube, not the other way around.</summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(10000)]
public sealed class ExtinguisherCarryRig : MonoBehaviour
{
    private const float TubeLength = .40f;
    private const float TubeRadius = .012f;
    private PickupItem pickup;
    private Transform nozzle;
    private Transform outlet;
    private MeshFilter hose;
    private Mesh originalMesh, rigidMesh;
    private Vector3 nozzlePosition, nozzleScale;
    private Quaternion nozzleRotation;
    private Vector3 handleGrip, nozzleGrip, outletAxis, hoseEndInNozzle;
    private Vector3 tubeStart, hoseRestPosition, hoseRestScale;
    private Quaternion hoseRestRotation;
    private bool ready, holding;
    private MeshFilter[] geometry;

    public Vector3 HandlePosition => transform.TransformPoint(handleGrip);
    public Vector3 NozzleGripPosition => nozzle != null ? nozzle.TransformPoint(nozzleGrip) : transform.position;
    public Vector3 SupportPosition => hose != null ? hose.transform.TransformPoint(new Vector3(0f,0f,.27f)) : transform.position;
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
        // Author a rigid straight tube once; aiming changes transforms, never its vertices.
        // Seat the straight tube inside the front of the real handle. The highest
        // vertices of the authored Hose are above/in front of its outlet, so using
        // them directly leaves a visible floating end after replacing that mesh.
        MeshFilter handleMesh = handle.GetComponent<MeshFilter>();
        if (handleMesh == null || handleMesh.sharedMesh == null || !handleMesh.sharedMesh.isReadable)
        {
            Debug.LogError("[ExtinguisherCarryRig] CarryHandle mesh needs Read/Write enabled.", this);
            return false;
        }
        // The FBX child is rotated relative to the bottle: mesh-local +Z is not
        // bottle-forward. Measure vertices in bottle coordinates instead.
        float frontZ = float.NegativeInfinity;
        foreach (var vertex in handleMesh.sharedMesh.vertices)
            frontZ = Mathf.Max(frontZ, transform.InverseTransformPoint(handle.TransformPoint(vertex)).z);
        Vector3 low = Vector3.one * float.PositiveInfinity;
        Vector3 high = Vector3.one * float.NegativeInfinity;
        foreach (var vertex in handleMesh.sharedMesh.vertices)
        {
            Vector3 point = transform.InverseTransformPoint(handle.TransformPoint(vertex));
            if (point.z < frontZ - .006f) continue;
            low = Vector3.Min(low, point);
            high = Vector3.Max(high, point);
        }
        tubeStart = (low + high) * .5f;
        tubeStart.z -= .004f; // Embed the first four millimetres in the metal fitting.
        hoseEndInNozzle = nozzleGrip - outletAxis * Vector3.Distance(nozzle.InverseTransformPoint(outlet.position), nozzleGrip);
        hoseRestPosition = hose.transform.localPosition;
        hoseRestRotation = hose.transform.localRotation;
        hoseRestScale = hose.transform.localScale;
        rigidMesh = BuildRigidTube(TubeLength, TubeRadius);
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
        if (!ApplyPose(right, aim, actor.transform.forward)) return;
        if (camera == null) return;
        Vector3 correction = GetViewCorrection(camera);
        if (correction.sqrMagnitude > 1e-8f)
        {
            // Move hands with the prop, never break either grip to clear the camera.
            // ArmsPitchFollow resets its base pose before us next frame, so no drift accumulates.
            arms.ShiftHeldPose(correction);
            ApplyPose(right + correction, aim, actor.transform.forward);
        }
        arms.AlignExtinguisherSupport(camera, SupportPosition);
    }

    /// <summary>Shared runtime/QA seam. The right contact positions the cylinder; the rigid tube determines the left contact.
    /// Cylinder yaw follows the body and leans back slightly at steep downward gaze; nozzle follows aim.</summary>
    public bool ApplyPose(Vector3 right, Vector3 aim, Vector3 bodyForward)
    {
        if (!Initialize()) return false;
        bodyForward = Vector3.ProjectOnPlane(bodyForward, Vector3.up);
        if (bodyForward.sqrMagnitude < 1e-6f) bodyForward = Vector3.forward;
        // At deep downward gaze, lean the bottle back from the straight tube's path.
        // The mount stays on the real handle and the bottle never lies sideways.
        float downPitch = -Mathf.Asin(Mathf.Clamp(aim.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
        float bottleLean = 25f * Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((downPitch - 45f) / 40f));
        transform.rotation = Quaternion.LookRotation(bodyForward.normalized, Vector3.up)
            * Quaternion.Euler(bottleLean, 0f, 0f);
        transform.position += right - HandlePosition;
        if (aim.sqrMagnitude < 1e-6f) aim = bodyForward;
        Vector3 aimUp = Mathf.Abs(Vector3.Dot(aim.normalized, Vector3.up)) > .99f ? transform.forward : Vector3.up;
        // Both NozzleBody and the entire black tube are rigid.
        nozzle.rotation = Quaternion.LookRotation(aim.normalized, aimUp) * Quaternion.FromToRotation(outletAxis, Vector3.forward);
        Vector3 start = transform.TransformPoint(tubeStart);
        Vector3 end = start + aim.normalized * TubeLength;
        nozzle.position += end - nozzle.TransformPoint(hoseEndInNozzle);
        hose.sharedMesh = rigidMesh;
        hose.transform.SetPositionAndRotation(start, Quaternion.LookRotation(aim.normalized, aimUp));
        hose.transform.localScale = Vector3.one;
        Vector3 scale = hose.transform.lossyScale;
        hose.transform.localScale = new Vector3(1f/scale.x, 1f/scale.y, 1f/scale.z);
        holding = true;
        return true;
    }

    /// <summary>Within the working pitch range, frame the handle near viewport y=.22,
    /// keeping geometry beyond the near plane through the full downward pitch range. Looking up releases framing.
    /// Evaluate oriented mesh bounds, including the rigid tube, rather than only the handle.
    /// The returned translation must be applied to BOTH hands and prop in the same frame.</summary>
    public Vector3 GetViewCorrection(Camera camera)
    {
        if (!ready || camera == null) return Vector3.zero;
        float pitch = -Mathf.Asin(Mathf.Clamp(camera.transform.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
        // Looking far above the working area must not drag the hands to eye height.
        float framingWeight = 1f - Mathf.SmoothStep(0f, 1f,
            (-25f - pitch) / 15f);
        if (framingWeight <= 0f) return Vector3.zero;
        float halfFovTan = Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f);
        float minDepth = float.PositiveInfinity;
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
            }
        }
        // Include a palm/finger envelope, since model bounds alone omit the gripping hands.
        for (int hand = 0; hand < 2; hand++)
        {
            Vector3 p = ViewPoint(camera, hand == 0 ? HandlePosition : SupportPosition);
            minDepth = Mathf.Min(minDepth, p.z - .10f);
            // Height comes from actual geometry; a 10cm phantom palm above the left grip
            // previously pushed the right hand and bottle off-screen.
        }
        float forward = Mathf.Max(0f, Mathf.Max(.45f, camera.nearClipPlane + .10f) - minDepth);
        Vector3 handle = ViewPoint(camera, HandlePosition);
        float targetSlope = (2f * .22f - 1f) * halfFovTan;
        // Keep the forearms connected to the bottom of the view; the authored pose sets height.
        float vertical = targetSlope * (handle.z + forward) - handle.y;
        return camera.cameraToWorldMatrix.MultiplyVector(new Vector3(0f, vertical, -forward)) * framingWeight;
    }

    private static Vector3 ViewPoint(Camera camera, Vector3 world)
    {
        // A camera ignores Transform scale for projection. Its scaled parent may also shear.
        // Use the actual rendering view matrix, never InverseTransformPoint/TransformVector.
        Vector3 p = camera.worldToCameraMatrix.MultiplyPoint3x4(world);
        p.z = -p.z;
        return p;
    }

    private static Mesh BuildRigidTube(float length, float radius)
    {
        const int sides = 24;
        var points = new Vector3[(sides + 1) * 2];
        var normals = new Vector3[points.Length];
        var uv = new Vector2[points.Length];
        var triangles = new int[sides * 6];
        for (int ring=0; ring<2; ring++) for (int i=0; i<=sides; i++)
        {
            int n=ring*(sides+1)+i;
            float angle=i*Mathf.PI*2f/sides;
            normals[n]=new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0);
            points[n]=normals[n]*radius + Vector3.forward*(ring*length);
            uv[n]=new Vector2(i/(float)sides,ring);
        }
        for(int i=0;i<sides;i++)
        {
            int j=i*6, next=i+sides+1;
            triangles[j]=i;triangles[j+1]=i+1;triangles[j+2]=next;
            triangles[j+3]=i+1;triangles[j+4]=next+1;triangles[j+5]=next;
        }
        var mesh=new Mesh { name="Rigid extinguisher tube (runtime)", vertices=points, normals=normals, uv=uv, triangles=triangles };
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        return mesh;
    }

    public void Restore()
    {
        if (!ready || !holding) return;
        nozzle.localPosition = nozzlePosition; nozzle.localRotation = nozzleRotation; nozzle.localScale = nozzleScale;
        hose.sharedMesh = originalMesh;
        hose.transform.localPosition = hoseRestPosition;
        hose.transform.localRotation = hoseRestRotation;
        hose.transform.localScale = hoseRestScale;
        holding = false;
    }
    private void OnDisable() { Restore(); }
    private void OnDestroy()
    {
        Restore();
        if (rigidMesh != null)
        {
            if (Application.isPlaying) Destroy(rigidMesh); else DestroyImmediate(rigidMesh);
        }
    }
}
