using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

/// <summary>
/// Membuat tekstur "peta rintangan" dari atas untuk shader air (River_Water_v2/v4).
/// Channel (semuanya bernilai putih = netral / tidak ada rintangan):
///   R = jarak ke rintangan terdekat / maxDistance      (1 = jauh)
///   G = 1 - turbulensi (wake di hilir + pillow di hulu) (1 = tenang)
///   B = 1 - dorongan air ke kanan (arah +UV.x)
///   A = 1 - dorongan air ke kiri  (arah -UV.x)
///
/// Rintangan STATIS (batu, dll) di-bake sekali saat start.
/// Rintangan BERGERAK (kapal & sampah hanyut) diperbarui tiap beberapa frame hanya di area sekitarnya,
/// jadi busa/foam air ikut bergerak bersama kapal.
/// </summary>
[ExecuteAlways, RequireComponent(typeof(RiverPath))]
public class RiverObstacleMap : MonoBehaviour
{
    [Header("Peta")]
    public int resolution = 512;
    public float padding = 8f;                 // tambahan di luar tepi sungai (m)
    public LayerMask obstacleLayers = ~0;

    [Header("Rintangan")]
    public float maxDistance = 4f;             // jarak maksimum yang disimpan (m)
    public float wakeLength = 8f;              // panjang wake di hilir (m)
    public float pillowLength = 1.5f;          // panjang pillow di hulu (m)
    public float pushRadius = 2f;              // jarak dorongan lateral di luar rintangan (m)
    [Tooltip("Rintangan yang bagian atasnya lebih tinggi dari air + nilai ini dianggap tidak menyentuh air.")]
    public float maxHeightAboveWater = 3f;

    [Header("Rintangan Bergerak (kapal & sampah)")]
    public bool updateDynamicObstacles = true;
    [Range(5f, 60f)] public float dynamicUpdateRate = 30f;
    [Tooltip("Cari BoatController dan TrashObstacle otomatis.")]
    public bool autoFindBoatAndTrash = true;
    [Tooltip("Collider tambahan yang ingin ikut bergerak foam-nya.")]
    public List<Collider> extraDynamicColliders = new List<Collider>();

    [Header("Material")]
    public Material waterMaterial;             // isi sama dengan Water Material di River Path

    Texture2D tex;
    RiverPath riverRef;

    // Parameter peta (diisi saat Bake)
    int res;
    Vector2 mapMin, mapSize;
    float pxX, pxZ;
    bool baked;

    // Data statis (hasil bake) dan data kerja (statis + rintangan bergerak)
    float[] sDist, sTurb, sPushR, sPushL;
    float[] wDist, wTurb, wPushR, wPushL;
    Color32[] pix;

    // Dinamis
    float updateTimer, refreshTimer;
    readonly List<Collider> dynCols = new List<Collider>();
    readonly List<Obstacle> dynObstacles = new List<Obstacle>();
    readonly List<Region> prevRegions = new List<Region>();
    readonly List<Region> dirtyRegions = new List<Region>();

    struct Obstacle
    {
        public Collider col;
        public Vector2 c;                      // pusat (x,z)
        public float radius, waterY;
        public Vector2 f, rt;                  // arah aliran dan arah kanan (x,z)
    }

    struct Region { public int i0, i1, j0, j1; }

    void OnEnable() { Bake(); }

    void OnDisable()
    {
        if (tex) Kill(tex);
        tex = null;
        baked = false;
    }

    static void Kill(Object o)
    {
        if (!o) return;
        if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
    }

    // ------------------------------------------------------------------ BAKE (statis)

    [ContextMenu("Bake")]
    public void Bake()
    {
        baked = false;
        MeshRenderer mr = GetComponent<MeshRenderer>();
        Material rendererMat = mr != null ? mr.sharedMaterial : null;
        if (!waterMaterial) waterMaterial = rendererMat;
        if (!waterMaterial && !rendererMat) return;
        riverRef = GetComponent<RiverPath>();
        var sc = GetComponent<SplineContainer>();
        if (!riverRef || !sc) return;
        Physics.SyncTransforms();

        // 1. Batas peta (x,z) dari spline + lebar sungai + padding
        Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
        Vector2 max = new Vector2(float.MinValue, float.MinValue);
        const int samples = 200;
        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;
            Vector3 p = sc.EvaluatePosition(t);
            float h = riverRef.WidthAt(t) * 0.5f + padding;
            min = Vector2.Min(min, new Vector2(p.x - h, p.z - h));
            max = Vector2.Max(max, new Vector2(p.x + h, p.z + h));
        }
        Vector2 size = max - min;

        res = Mathf.Clamp(resolution, 64, 2048);
        mapMin = min;
        mapSize = size;
        pxX = size.x / res;
        pxZ = size.y / res;

        // 2. Kumpulkan rintangan statis di area sungai
        var list = new List<Obstacle>();
        Vector3 center = new Vector3((min.x + max.x) * 0.5f, transform.position.y, (min.y + max.y) * 0.5f);
        Collider[] hits = Physics.OverlapBox(center, new Vector3(size.x * 0.5f, 1000f, size.y * 0.5f),
            Quaternion.identity, obstacleLayers, QueryTriggerInteraction.Ignore);
        foreach (var col in hits)
        {
            if (col is TerrainCollider) continue;
            if (col is MeshCollider mc && !mc.convex) continue;   // ClosestPoint hanya untuk collider cembung
            if (col.transform.IsChildOf(transform)) continue;
            var rb = col.attachedRigidbody;
            if (rb != null && !rb.isKinematic) continue;          // objek dinamis dilewati
            // Kapal & sampah ditangani sebagai rintangan bergerak (bukan di-bake statis)
            if (col.GetComponentInParent<BoatController>() != null) continue;
            if (col.GetComponentInParent<TrashObstacle>() != null) continue;

            if (TryMakeObstacle(col, out Obstacle o)) list.Add(o);
        }

        // 3. Hitung peta statis
        sDist = new float[res * res];
        sTurb = new float[res * res];
        sPushR = new float[res * res];
        sPushL = new float[res * res];
        for (int k = 0; k < sDist.Length; k++) sDist[k] = 1f;

        foreach (var o in list)
            Accumulate(o, GetRegion(o), sDist, sTurb, sPushR, sPushL);

        // Salinan kerja untuk rintangan bergerak
        wDist = (float[])sDist.Clone();
        wTurb = (float[])sTurb.Clone();
        wPushR = (float[])sPushR.Clone();
        wPushL = (float[])sPushL.Clone();

        // 4. Tulis ke tekstur linear (bukan sRGB)
        pix = new Color32[res * res];
        for (int k = 0; k < pix.Length; k++) pix[k] = ToPixel(k);

        if (tex != null && tex.width != res) { Kill(tex); tex = null; }
        if (tex == null)
        {
            tex = new Texture2D(res, res, TextureFormat.RGBA32, false, true)
            {
                name = "RiverObstacleMap",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
        }
        tex.SetPixels32(pix);
        tex.Apply(false);

        ApplyMapToMaterial(waterMaterial, min, size);
        if (rendererMat != null && rendererMat != waterMaterial)
            ApplyMapToMaterial(rendererMat, min, size);

        prevRegions.Clear();
        dynCols.Clear();
        refreshTimer = 0f;
        updateTimer = 0f;
        baked = true;

        Debug.Log($"[RiverObstacleMap] {list.Count} rintangan statis di-bake ({res}x{res}).", this);
    }

    void ApplyMapToMaterial(Material mat, Vector2 min, Vector2 size)
    {
        if (mat == null) return;
        mat.SetTexture("_ObstacleMap", tex);
        mat.SetVector("_MapOrigin", new Vector4(min.x, min.y, 0f, 0f));
        mat.SetVector("_MapSize", new Vector4(size.x, size.y, 0f, 0f));
        mat.SetFloat("_ObstacleMaxDist", maxDistance);
    }

    // ------------------------------------------------------------------ DINAMIS (kapal, sampah)

    void Update()
    {
        if (!Application.isPlaying || !updateDynamicObstacles || !baked) return;

        updateTimer += Time.deltaTime;
        if (updateTimer < 1f / Mathf.Max(1f, dynamicUpdateRate)) return;
        updateTimer = 0f;

        refreshTimer -= 1f / Mathf.Max(1f, dynamicUpdateRate);
        if (refreshTimer <= 0f)
        {
            refreshTimer = 1f;
            RefreshDynamicColliders();
        }

        UpdateDynamic();
    }

    void RefreshDynamicColliders()
    {
        dynCols.Clear();

        if (autoFindBoatAndTrash)
        {
            foreach (var boat in FindObjectsByType<BoatController>(FindObjectsSortMode.None))
                AddColliders(boat.gameObject);

            foreach (var trash in FindObjectsByType<TrashObstacle>(FindObjectsSortMode.None))
                AddColliders(trash.gameObject);
        }

        foreach (var c in extraDynamicColliders)
            if (c != null) dynCols.Add(c);
    }

    void AddColliders(GameObject go)
    {
        foreach (var c in go.GetComponentsInChildren<Collider>(false))
        {
            if (c.isTrigger) continue;
            if (c is MeshCollider mc && !mc.convex) continue;
            dynCols.Add(c);
        }
    }

    void UpdateDynamic()
    {
        dynObstacles.Clear();
        foreach (var col in dynCols)
        {
            if (col == null || !col.enabled || !col.gameObject.activeInHierarchy) continue;
            if (TryMakeObstacle(col, out Obstacle o)) dynObstacles.Add(o);
        }

        // Area yang perlu dihitung ulang = posisi lama + posisi baru (supaya jejak lama terhapus)
        dirtyRegions.Clear();
        dirtyRegions.AddRange(prevRegions);
        prevRegions.Clear();
        foreach (var o in dynObstacles)
        {
            Region r = GetRegion(o);
            prevRegions.Add(r);
            dirtyRegions.Add(r);
        }
        if (dirtyRegions.Count == 0) return;

        // 1) kembalikan ke data statis
        foreach (var r in dirtyRegions)
        {
            for (int j = r.j0; j <= r.j1; j++)
            for (int i = r.i0; i <= r.i1; i++)
            {
                int idx = j * res + i;
                wDist[idx] = sDist[idx];
                wTurb[idx] = sTurb[idx];
                wPushR[idx] = sPushR[idx];
                wPushL[idx] = sPushL[idx];
            }
        }

        // 2) tambahkan rintangan bergerak di posisi sekarang
        foreach (var o in dynObstacles)
            Accumulate(o, GetRegion(o), wDist, wTurb, wPushR, wPushL);

        // 3) tulis piksel yang berubah
        foreach (var r in dirtyRegions)
        {
            for (int j = r.j0; j <= r.j1; j++)
            for (int i = r.i0; i <= r.i1; i++)
            {
                int idx = j * res + i;
                pix[idx] = ToPixel(idx);
            }
        }

        tex.SetPixels32(pix);
        tex.Apply(false);
    }

    // ------------------------------------------------------------------ Util bersama

    bool TryMakeObstacle(Collider col, out Obstacle o)
    {
        o = default;
        Bounds b = col.bounds;
        var s = riverRef.SampleAt(b.center);
        float waterY = s.center.y;
        if (b.max.y < waterY - 0.2f || b.min.y > waterY + maxHeightAboveWater) return false;   // tidak menyentuh air

        Vector2 f = new Vector2(s.flowVelocity.x, s.flowVelocity.z);
        if (f.sqrMagnitude < 1e-6f) return false;
        f.Normalize();

        o = new Obstacle
        {
            col = col,
            c = new Vector2(b.center.x, b.center.z),
            radius = Mathf.Max(0.3f, 0.5f * (b.extents.x + b.extents.z)),
            waterY = waterY,
            f = f,
            rt = new Vector2(f.y, -f.x)       // sama dengan arah kanan di RiverMeshBuilder
        };
        return true;
    }

    Region GetRegion(Obstacle o)
    {
        float reach = o.radius + Mathf.Max(Mathf.Max(maxDistance, wakeLength), Mathf.Max(pillowLength * 2f, pushRadius * 2f)) + 1f;
        return new Region
        {
            i0 = Mathf.Max(0, Mathf.FloorToInt((o.c.x - reach - mapMin.x) / pxX)),
            i1 = Mathf.Min(res - 1, Mathf.CeilToInt((o.c.x + reach - mapMin.x) / pxX)),
            j0 = Mathf.Max(0, Mathf.FloorToInt((o.c.y - reach - mapMin.y) / pxZ)),
            j1 = Mathf.Min(res - 1, Mathf.CeilToInt((o.c.y + reach - mapMin.y) / pxZ))
        };
    }

    void Accumulate(Obstacle o, Region r, float[] dist, float[] turb, float[] pushR, float[] pushL)
    {
        for (int j = r.j0; j <= r.j1; j++)
        for (int i = r.i0; i <= r.i1; i++)
        {
            var p = new Vector3(mapMin.x + (i + 0.5f) * pxX, o.waterY, mapMin.y + (j + 0.5f) * pxZ);
            Vector3 cp = o.col.ClosestPoint(p);
            float d = new Vector2(cp.x - p.x, cp.z - p.z).magnitude;
            int idx = j * res + i;

            dist[idx] = Mathf.Min(dist[idx], Mathf.Clamp01(d / maxDistance));

            Vector2 rel = new Vector2(p.x - o.c.x, p.z - o.c.y);
            float s = Vector2.Dot(rel, o.f);          // + = hilir
            float l = Vector2.Dot(rel, o.rt);         // + = kanan

            if (s > 0f)
            {   // wake: melebar pelan dan memudar ke hilir
                float len = Mathf.Clamp01(1f - s / wakeLength);
                float w = o.radius * 1.1f + 0.25f * s;
                float lat = Mathf.Clamp01(1f - Mathf.Abs(l) / w);
                turb[idx] = Mathf.Max(turb[idx], len * lat);
            }
            else
            {   // pillow: air menumpuk di hulu rintangan
                float len = Mathf.Clamp01(1f - (-s) / (o.radius + pillowLength));
                float lat = Mathf.Clamp01(1f - Mathf.Abs(l) / (o.radius * 1.2f));
                turb[idx] = Mathf.Max(turb[idx], 0.6f * len * lat);
            }

            // dorongan lateral: air membelok menjauhi rintangan
            float pm = Mathf.Clamp01(1f - d / pushRadius);
            float push = pm * Mathf.Clamp(l / (o.radius * 0.5f), -1f, 1f);
            if (push > 0f) pushR[idx] = Mathf.Max(pushR[idx], push);
            else pushL[idx] = Mathf.Max(pushL[idx], -push);
        }
    }

    Color32 ToPixel(int k)
    {
        return new Color32(
            ToByte(wDist[k]),
            ToByte(1f - wTurb[k]),
            ToByte(1f - wPushR[k]),
            ToByte(1f - wPushL[k]));
    }

    static byte ToByte(float v) { return (byte)Mathf.RoundToInt(Mathf.Clamp01(v) * 255f); }
}