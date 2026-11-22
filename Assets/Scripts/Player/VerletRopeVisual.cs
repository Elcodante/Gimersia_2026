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
    [SerializeField, Range(1, 40)] private int constraintIterations = 32;
    [Tooltip("Tali tidak boleh turun lebih rendah dari ujung terendah + offset ini (agar tidak tenggelam).")]
    [SerializeField] private bool useFloor = false;
    [SerializeField] private float floorOffset = -0.15f;

    [Header("Air & Tali Mengapung")]
    [Tooltip("Tali mengapung tepat di atas permukaan air sungai (dibaca dari RiverPath). Matikan untuk memakai Floor Offset di atas.")]
    [SerializeField] private bool useWaterSurface = false;
    [Tooltip("Tinggi tali di atas permukaan air (di luar jari-jari tali). Terlalu rendah = tali menembus air dan memunculkan garis busa putih.")]
    [SerializeField] private float surfaceClearance = 0.06f;
    [Tooltip("Seberapa kuat arus membawa bagian tali yang mengapung (0 = tidak ikut arus).")]
    [SerializeField, Range(0f, 1f)] private float currentInfluence = 0.35f;
    [Tooltip("Hambatan air pada tali yang mengapung. Makin kecil = tali makin cepat berhenti bergoyang.")]
    [SerializeField, Range(0.5f, 1f)] private float surfaceDamping = 0.8f;

    [Header("Kelenturan (anti bergerigi)")]
    [Tooltip("Kekuatan menolak tekukan tajam. 0 = tali bisa bergerigi.")]
    [SerializeField, Range(0f, 1f)] private float bendResistance = 0.25f;
    [Tooltip("Jarak minimum dua titik berselang relatif terhadap lurus. Makin dekat 1 = tali makin kaku.")]
    [SerializeField, Range(0.85f, 1f)] private float minBendRatio = 0.92f;
    [Tooltip("Kekuatan tali kendor membentuk lengkung halus di permukaan air.")]
    [SerializeField, Range(0f, 0.3f)] private float arcShapeStrength = 0f;

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
    private float taut;          // 0 = bebas melengkung, 1 = lurus tegang (dihaluskan)
    private float surfaceY;
    private bool hasSurface;
    private Vector3 flowVel;      // laju arus pada tali (sudah dikali currentInfluence)
    private float bowSign = 1f;   // sisi lengkung tali kendor
    private Vector3[] simBackup;  // keadaan simulasi saat tampilan di-blend ke lurus
    private bool displayBlended;

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
    /// <param name="tautness">0 = kendor (melengkung kena gravitasi), 1 = tegang (lurus). Dihaluskan agar tidak melompat.</param>
    public void Drive(Vector3 start, Vector3 end, float ropeLength, float tautness = 0f)
    {
        if (!initialized) Init();
        SetVisible(true);

        if (needsReset)
        {
            ResetRope(start, end);
            taut = Mathf.Clamp01(tautness);
        }
        else
        {
            taut = Mathf.MoveTowards(taut, Mathf.Clamp01(tautness), 4f * Time.deltaTime);
        }

        UpdateWater(start, end);

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

        // Kelurusan karena tegang HANYA untuk tampilan (keadaan simulasi tidak diubah), supaya tali mengencang halus, tidak menyentak.
        BlendDisplayTowardsStraight(start, end);
        RebuildMesh();
        PlaceHook();
        RestoreSimulationState();
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
        bool floorOn = hasSurface || useFloor;
        float floorY = hasSurface ? surfaceY : Mathf.Min(start.y, end.y) + floorOffset;
        // Makin tegang, makin kecil pengaruh gravitasi
        Vector3 g = Vector3.down * (gravity * (1f - taut) * dt * dt);

        // 1) Integrasi Verlet (inersia + gravitasi) untuk partikel bebas
        for (int i = 1; i < last; i++)
        {
            Vector3 cur = pos[i];
            Vector3 vel = (cur - prev[i]) * damping;

            // Bagian tali yang mengapung di air: redam gerak datar dan terbawa arus (laju seimbang = flowVel)
            if (floorOn && cur.y <= floorY + 0.02f)
            {
                Vector3 flowStep = flowVel * dt;
                vel.x = Mathf.Lerp(flowStep.x, vel.x, surfaceDamping);
                vel.z = Mathf.Lerp(flowStep.z, vel.z, surfaceDamping);
            }

            prev[i] = cur;
            pos[i] = cur + vel + g;
        }

        pos[0] = start; prev[0] = start;
        pos[last] = end; prev[last] = end;

        // Arahkan bentuk lengkung sebelum solver jarak & kelenturan agar segmen tidak mematah
        ApplyArcShape(start, end, rest, floorY, floorOn);

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

            if (bendResistance > 0f) ApplyBend(rest);

            if (floorOn)
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

    // Permukaan air & arus di tengah tali (dibaca dari sungai utama)
    private void UpdateWater(Vector3 start, Vector3 end)
    {
        hasSurface = false;
        flowVel = Vector3.zero;
        if (!useWaterSurface) return;

        RiverPath river = RiverPath.Main;
        if (river == null) return;

        RiverPath.Sample s = river.SampleAt((start + end) * 0.5f);
        if (float.IsFinite(s.center.y))
        {
            surfaceY = s.center.y + radius + surfaceClearance;
            hasSurface = true;
        }

        Vector3 f = s.flowVelocity;
        f.y = 0f;
        if (float.IsFinite(f.x) && float.IsFinite(f.z)) flowVel = f * currentInfluence;
    }

    // Tolak tekukan tajam: titik i-1 dan i+1 tidak boleh terlalu berdekatan (mencegah tali bergerigi / zig-zag)
    private void ApplyBend(float rest)
    {
        int last = particleCount - 1;
        float minD = minBendRatio * 2f * rest;

        for (int i = 1; i < last; i++)
        {
            Vector3 d = pos[i + 1] - pos[i - 1];
            float dist = d.magnitude;
            if (dist < 1e-5f || dist >= minD) continue;

            float wa = (i - 1 == 0) ? 0f : 1f;
            float wc = (i + 1 == last) ? 0f : 1f;
            float ws = wa + wc;
            if (ws <= 0f) continue;

            float push = (minD - dist) * bendResistance;
            Vector3 dir = d / dist;
            pos[i - 1] -= dir * (push * wa / ws);
            pos[i + 1] += dir * (push * wc / ws);
        }
    }

    // Tali kendor yang mengapung membentuk lengkung halus (busur) yang condong ke hilir arus,
    // bukan zig-zag. Transisi halus dari udara ke permukaan air.
    private void ApplyArcShape(Vector3 start, Vector3 end, float rest, float floorY, bool floorOn)
    {
        if (arcShapeStrength <= 0f || !floorOn) return;

        int last = particleCount - 1;
        float L = rest * last;
        Vector3 chord = end - start;
        float c = chord.magnitude;
        float slack = L - c;

        Vector3 flat = chord;
        flat.y = 0f;
        if (slack < 0.03f * L || flat.sqrMagnitude < 1e-4f) return;

        // Busur parabola dengan panjang busur ~ L:  L ~ c + 8A^2 / (3c)
        float A = Mathf.Min(Mathf.Sqrt(3f * c * slack / 8f), L * 0.25f);
        Vector3 perp = Vector3.Cross(Vector3.up, flat.normalized);

        if (flowVel.sqrMagnitude > 1e-4f)
        {
            float dot = Vector3.Dot(perp, flowVel.normalized);
            if (Mathf.Abs(dot) > 0.15f)
                bowSign = Mathf.MoveTowards(bowSign, Mathf.Sign(dot), 3f * FixedStep);   // condong ke hilir secara halus
        }

        float k = arcShapeStrength * (1f - taut);
        for (int i = 1; i < last; i++)
        {
            float waterFactor = Mathf.Clamp01(1f - (pos[i].y - floorY) / 0.8f);
            if (waterFactor <= 0.001f) continue;   // bagian tinggi di udara tetap kena gravitasi

            float u = i / (float)last;
            Vector3 target = Vector3.Lerp(start, end, u) + perp * (bowSign * A * 4f * u * (1f - u));
            Vector3 delta = target - pos[i];
            delta.y = 0f;

            Vector3 shift = delta * (k * waterFactor);
            pos[i] += shift;
            prev[i] += shift * 0.8f;
        }
    }

    // Tampilan saja: geser titik tali ke garis lurus sesuai tautness (halus), simpan keadaan simulasi untuk dipulihkan
    private void BlendDisplayTowardsStraight(Vector3 start, Vector3 end)
    {
        displayBlended = false;
        if (taut <= 0.001f) return;

        if (simBackup == null || simBackup.Length != particleCount) simBackup = new Vector3[particleCount];

        float blend = taut * taut * (3f - 2f * taut);
        int last = particleCount - 1;
        for (int i = 0; i <= last; i++)
        {
            simBackup[i] = pos[i];
            Vector3 straight = Vector3.Lerp(start, end, i / (float)last);
            pos[i] = Vector3.Lerp(pos[i], straight, blend);
        }
        displayBlended = true;
    }

    private void RestoreSimulationState()
    {
        if (!displayBlended) return;
        for (int i = 0; i < particleCount; i++) pos[i] = simBackup[i];
        displayBlended = false;
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