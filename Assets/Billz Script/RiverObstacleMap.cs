using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

/// <summary>
/// Membuat tekstur "peta rintangan" dari atas untuk shader air (River_Water_v2).
/// Channel (semuanya bernilai putih = netral / tidak ada rintangan):
///   R = jarak ke rintangan terdekat / maxDistance      (1 = jauh)
///   G = 1 - turbulensi (wake di hilir + pillow di hulu) (1 = tenang)
///   B = 1 - dorongan air ke kanan (arah +UV.x)
///   A = 1 - dorongan air ke kiri  (arah -UV.x)
/// Hanya rintangan statis (tanpa Rigidbody dinamis) yang ikut di-bake.
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

    [Header("Material")]
    public Material waterMaterial;             // isi sama dengan Water Material di River Path

    Texture2D tex;

    struct Obstacle
    {
        public Collider col;
        public Vector2 c;                      // pusat (x,z)
        public float radius, waterY;
        public Vector2 f, rt;                  // arah aliran dan arah kanan (x,z)
    }

    void OnEnable() { Bake(); }

    void OnDisable()
    {
        if (tex) Kill(tex);
        tex = null;
    }

    static void Kill(Object o)
    {
        if (!o) return;
        if (Application.isPlaying) Destroy(o); else DestroyImmediate(o);
    }

    [ContextMenu("Bake")]
    public void Bake()
    {
        if (!waterMaterial) return;
        var river = GetComponent<RiverPath>();
        var sc = GetComponent<SplineContainer>();
        if (!river || !sc) return;
        Physics.SyncTransforms();

        // 1. Batas peta (x,z) dari spline + lebar sungai + padding
        Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
        Vector2 max = new Vector2(float.MinValue, float.MinValue);
        const int samples = 200;
        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples;
            Vector3 p = sc.EvaluatePosition(t);
            float h = river.WidthAt(t) * 0.5f + padding;
            min = Vector2.Min(min, new Vector2(p.x - h, p.z - h));
            max = Vector2.Max(max, new Vector2(p.x + h, p.z + h));
        }
        Vector2 size = max - min;

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
            if (rb != null && !rb.isKinematic) continue;          // objek dinamis (kapal) dilewati

            Bounds b = col.bounds;
            var s = river.SampleAt(b.center);
            float waterY = s.center.y;
            if (b.max.y < waterY - 0.2f || b.min.y > waterY + 3f) continue;   // tidak menyentuh air

            Vector2 f = new Vector2(s.flowVelocity.x, s.flowVelocity.z);
            if (f.sqrMagnitude < 1e-6f) continue;
            f.Normalize();

            list.Add(new Obstacle
            {
                col = col,
                c = new Vector2(b.center.x, b.center.z),
                radius = Mathf.Max(0.3f, 0.5f * (b.extents.x + b.extents.z)),
                waterY = waterY,
                f = f,
                rt = new Vector2(f.y, -f.x)       // sama dengan arah kanan di RiverMeshBuilder
            });
        }

        // 3. Hitung peta
        int res = Mathf.Clamp(resolution, 64, 2048);
        float pxX = size.x / res, pxZ = size.y / res;
        var dist = new float[res * res];
        var turb = new float[res * res];
        var pushR = new float[res * res];
        var pushL = new float[res * res];
        for (int k = 0; k < dist.Length; k++) dist[k] = 1f;

        foreach (var o in list)
        {
            float reach = o.radius + Mathf.Max(Mathf.Max(maxDistance, wakeLength), Mathf.Max(pillowLength * 2f, pushRadius * 2f)) + 1f;
            int i0 = Mathf.Max(0, Mathf.FloorToInt((o.c.x - reach - min.x) / pxX));
            int i1 = Mathf.Min(res - 1, Mathf.CeilToInt((o.c.x + reach - min.x) / pxX));
            int j0 = Mathf.Max(0, Mathf.FloorToInt((o.c.y - reach - min.y) / pxZ));
            int j1 = Mathf.Min(res - 1, Mathf.CeilToInt((o.c.y + reach - min.y) / pxZ));

            for (int j = j0; j <= j1; j++)
            for (int i = i0; i <= i1; i++)
            {
                var p = new Vector3(min.x + (i + 0.5f) * pxX, o.waterY, min.y + (j + 0.5f) * pxZ);
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

        // 4. Tulis ke tekstur linear (bukan sRGB)
        var pix = new Color32[res * res];
        for (int k = 0; k < pix.Length; k++)
        {
            pix[k] = new Color32(
                ToByte(dist[k]),
                ToByte(1f - turb[k]),
                ToByte(1f - pushR[k]),
                ToByte(1f - pushL[k]));
        }

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

        waterMaterial.SetTexture("_ObstacleMap", tex);
        waterMaterial.SetVector("_MapOrigin", new Vector4(min.x, min.y, 0f, 0f));
        waterMaterial.SetVector("_MapSize", new Vector4(size.x, size.y, 0f, 0f));
        waterMaterial.SetFloat("_ObstacleMaxDist", maxDistance);

        Debug.Log($"[RiverObstacleMap] {list.Count} rintangan di-bake ({res}x{res}).", this);
    }

    static byte ToByte(float v) { return (byte)Mathf.RoundToInt(Mathf.Clamp01(v) * 255f); }
}