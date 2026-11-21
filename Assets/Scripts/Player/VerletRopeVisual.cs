using UnityEngine;

/// <summary>
/// Tali 3D prosedural (simulasi Verlet) -> bergerak ala ragdoll: ujung-ujungnya "dipaku"
/// (tangan pemain & kait/batu), bagian tengah ngikut gravitasi + inersia, jadi melengkung,
/// bergoyang saat kapal/pemain bergerak, dan lurus saat tegang.
/// Murni visual (tiap client simulasi sendiri), jadi tidak perlu sinkron per-segmen lewat network.
/// Dipanggil dari LassoController lewat Drive() tiap frame.
/// </summary>
[DisallowMultipleComponent]
public class VerletRopeVisual : MonoBehaviour
{
    [Header("Simulasi")]
    [SerializeField, Range(6, 64)] private int particleCount = 24;
    [SerializeField] private float gravity = 14f;
    [Tooltip("Makin kecil = tali makin cepat berhenti bergoyang.")]
    [SerializeField, Range(0.8f, 1f)] private float damping = 0.98f;
    [SerializeField, Range(1, 30)] private int constraintIterations = 14;
    [Tooltip("Tali tidak boleh turun lebih rendah dari ujung terendah + offset ini (agar tidak tenggelam).")]
    [SerializeField] private bool useFloor = true;
    [SerializeField] private float floorOffset = -0.15f;

    [Header("Bentuk 3D")]
    [SerializeField] private float radius = 0.05f;
    [SerializeField, Range(3, 12)] private int radialSegments = 6;
    [Tooltip("Panjang (meter) satu ulangan tekstur tali.")]
    [SerializeField] private float uvTileLength = 0.45f;
    [Tooltip("Kosong = otomatis memakai material tali buatan kode (RopeMaterialFactory). Atau buat asset lewat menu Tools > River Game > Create Rope Material.")]
    [SerializeField] private Material ropeMaterial;

    [Header("Kait / Ujung Tali (opsional)")]
    [Tooltip("Prefab 3D yang dipasang di ujung tali (kait, batu pengait, simpul laso, dll).")]
    [SerializeField] private GameObject hookPrefab;
    [SerializeField] private float hookScale = 1f;

    private const float FixedStep = 1f / 60f;

    private bool initialized;
    private bool needsReset = true;
    private bool visible;
    private float accumulator;

    private Vector3[] pos, prev;
    private Mesh mesh;
    private MeshRenderer meshRenderer;
    private GameObject hookInstance;

    private Vector3[] verts, normals;
    private Vector2[] uvs;

    /// <summary>Dipakai LassoController saat membuat komponen ini otomatis.</summary>
    public void Configure(Material material, GameObject hook)
    {
        if (material != null) ropeMaterial = material;
        if (hook != null) hookPrefab = hook;
    }

    public void SetVisible(bool value)
    {
        if (!initialized) { if (!value) return; Init(); }
        if (visible == value) return;

        visible = value;
        meshRenderer.enabled = value;
        if (hookInstance != null) hookInstance.SetActive(value);
        if (!value) needsReset = true; // tali berikutnya mulai lurus, bukan "lompat" dari posisi lama
    }

    /// <param name="start">Posisi tangan pemain (dunia).</param>
    /// <param name="end">Posisi ujung tali / kait / batu (dunia).</param>
    /// <param name="ropeLength">Panjang tali sebenarnya. Lebih besar dari jarak = melengkung, sama/lebih kecil = tegang.</param>
    public void Drive(Vector3 start, Vector3 end, float ropeLength)
    {
        if (!initialized) Init();
        SetVisible(true);

        if (needsReset) ResetRope(start, end);

        float rest = Mathf.Max(0.01f, ropeLength) / (particleCount - 1);

        accumulator = Mathf.Min(accumulator + Time.deltaTime, 0.1f);
        while (accumulator >= FixedStep)
        {
            accumulator -= FixedStep;
            Step(FixedStep, start, end, rest);
        }

        // Pastikan ujung selalu menempel tepat di posisi terbaru walau belum ada sub-step frame ini
        pos[0] = start;
        pos[particleCount - 1] = end;

        RebuildMesh();
        PlaceHook();
    }

    private void Init()
    {
        initialized = true;

        pos = new Vector3[particleCount];
        prev = new Vector3[particleCount];

        var mf = GetComponent<MeshFilter>();
        if (mf == null) mf = gameObject.AddComponent<MeshFilter>();
        meshRenderer = GetComponent<MeshRenderer>();
        if (meshRenderer == null) meshRenderer = gameObject.AddComponent<MeshRenderer>();

        mesh = new Mesh { name = "VerletRopeMesh" };
        mesh.MarkDynamic();
        mf.sharedMesh = mesh;

        BuildStaticMeshData();

        meshRenderer.sharedMaterial = (ropeMaterial != null) ? ropeMaterial : RopeMaterialFactory.GetSharedMaterial();

        if (hookPrefab != null)
        {
            hookInstance = Instantiate(hookPrefab, transform);
            hookInstance.transform.localScale = Vector3.one * hookScale;
            hookInstance.SetActive(false);
        }

        meshRenderer.enabled = false;
        visible = false;
    }

    private void ResetRope(Vector3 start, Vector3 end)
    {
        for (int i = 0; i < particleCount; i++)
        {
            float t = i / (float)(particleCount - 1);
            pos[i] = Vector3.Lerp(start, end, t);
            prev[i] = pos[i];
        }
        accumulator = 0f;
        needsReset = false;
    }

    private void Step(float dt, Vector3 start, Vector3 end, float rest)
    {
        int last = particleCount - 1;
        float floorY = Mathf.Min(start.y, end.y) + floorOffset;
        Vector3 g = Vector3.down * (gravity * dt * dt);

        // 1) Integrasi Verlet (inersia + gravitasi) untuk partikel bebas
        for (int i = 1; i < last; i++)
        {
            Vector3 cur = pos[i];
            Vector3 vel = (cur - prev[i]) * damping;
            prev[i] = cur;
            pos[i] = cur + vel + g;
        }

        pos[0] = start; prev[0] = start;
        pos[last] = end; prev[last] = end;

        // 2) Jaga panjang tiap segmen (ujung tidak bergeser)
        for (int it = 0; it < constraintIterations; it++)
        {
            for (int i = 0; i < last; i++)
            {
                float w0 = (i == 0) ? 0f : 1f;
                float w1 = (i + 1 == last) ? 0f : 1f;
                float wSum = w0 + w1;
                if (wSum <= 0f) continue;

                Vector3 delta = pos[i + 1] - pos[i];
                float dist = delta.magnitude;
                if (dist < 1e-5f) continue;

                float diff = (dist - rest) / dist;
                pos[i] += delta * (diff * (w0 / wSum));
                pos[i + 1] -= delta * (diff * (w1 / wSum));
            }

            if (useFloor)
            {
                for (int i = 1; i < last; i++)
                {
                    if (pos[i].y < floorY)
                    {
                        pos[i].y = floorY;
                        prev[i].y = floorY; // matikan geser vertikal saat menyentuh "air"
                    }
                }
            }
        }
    }

    private void BuildStaticMeshData()
    {
        int ring = radialSegments + 1;
        int vertCount = particleCount * ring;

        verts = new Vector3[vertCount];
        normals = new Vector3[vertCount];
        uvs = new Vector2[vertCount];

        var tris = new int[(particleCount - 1) * radialSegments * 6];
        int ti = 0;
        for (int i = 0; i < particleCount - 1; i++)
        {
            for (int j = 0; j < radialSegments; j++)
            {
                int a = i * ring + j;
                int b = a + 1;
                int c = a + ring;
                int d = c + 1;

                tris[ti++] = a; tris[ti++] = b; tris[ti++] = c;
                tris[ti++] = b; tris[ti++] = d; tris[ti++] = c;
            }
        }

        mesh.Clear();
        mesh.vertices = verts;
        mesh.triangles = tris;
    }

    private void RebuildMesh()
    {
        int ring = radialSegments + 1;
        int last = particleCount - 1;

        Vector3 normalRef = Vector3.zero;
        float travelled = 0f;

        for (int i = 0; i < particleCount; i++)
        {
            Vector3 prevP = pos[Mathf.Max(i - 1, 0)];
            Vector3 nextP = pos[Mathf.Min(i + 1, last)];
            Vector3 tangent = nextP - prevP;
            if (tangent.sqrMagnitude < 1e-8f) tangent = Vector3.forward;
            tangent.Normalize();

            if (i == 0)
            {
                normalRef = Vector3.Cross(tangent, Vector3.up);
                if (normalRef.sqrMagnitude < 1e-4f) normalRef = Vector3.Cross(tangent, Vector3.right);
            }
            normalRef = Vector3.ProjectOnPlane(normalRef, tangent);
            if (normalRef.sqrMagnitude < 1e-8f) normalRef = Vector3.Cross(tangent, Vector3.up);
            normalRef.Normalize();

            Vector3 n = normalRef;
            Vector3 b = Vector3.Cross(tangent, n);

            if (i > 0) travelled += Vector3.Distance(pos[i], pos[i - 1]);
            float v = travelled / Mathf.Max(0.01f, uvTileLength);

            for (int j = 0; j < ring; j++)
            {
                float ang = (j / (float)radialSegments) * Mathf.PI * 2f;
                Vector3 radial = n * Mathf.Cos(ang) + b * Mathf.Sin(ang);

                int idx = i * ring + j;
                verts[idx] = transform.InverseTransformPoint(pos[i] + radial * radius);
                normals[idx] = transform.InverseTransformDirection(radial);
                uvs[idx] = new Vector2(j / (float)radialSegments, v);
            }
        }

        mesh.vertices = verts;
        mesh.normals = normals;
        mesh.uv = uvs;
        mesh.RecalculateBounds();
    }

    private void PlaceHook()
    {
        if (hookInstance == null) return;

        int last = particleCount - 1;
        Vector3 dir = pos[last] - pos[last - 1];
        if (dir.sqrMagnitude < 1e-8f) dir = transform.forward;

        hookInstance.transform.SetPositionAndRotation(pos[last], Quaternion.LookRotation(dir.normalized, Vector3.up));
    }

    private void OnDestroy()
    {
        if (mesh != null) Destroy(mesh);
    }
}