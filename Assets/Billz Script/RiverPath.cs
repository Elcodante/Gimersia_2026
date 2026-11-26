using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

[ExecuteAlways, RequireComponent(typeof(SplineContainer))]
public class RiverPath : MonoBehaviour
{
    [Header("Bentuk")]
    public float baseWidth = 12f;
    public AnimationCurve widthOverLength = AnimationCurve.Linear(0, 1, 1, 1);
    public float uvTileLength = 10f;       // panjang (meter) satu ulangan tekstur

    [Header("Arus")]
    public float flowSpeed = 4f;           // m/detik di tengah sungai
    [Range(0f, 1f)] public float edgeSpeedFactor = 0.3f; // arus di tepi (relatif)

    [Header("Visual")]
    public Material waterMaterial;         // punya property _FlowSpeed

    SplineContainer container;
    SplineContainer Container => container ? container : (container = GetComponent<SplineContainer>());

    public float WidthAt(float t) => baseWidth * widthOverLength.Evaluate(t);

    /// <summary>Punya spline dengan minimal 2 knot. RiverPath kosong (mis. tertempel di objek lain) tidak valid.</summary>
    public bool IsValid
    {
        get
        {
            var c = Container;
            return c != null && c.Spline != null && c.Spline.Count >= 2;
        }
    }

    static RiverPath mainRiver;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() { mainRiver = null; }

    /// <summary>
    /// Sungai utama untuk gameplay: RiverPath VALID dengan spline terbanyak knot.
    /// Gunakan ini, bukan FindFirstObjectByType, yang bisa mengambil RiverPath kosong.
    /// </summary>
    public static RiverPath Main
    {
        get
        {
            if (mainRiver != null && mainRiver.isActiveAndEnabled && mainRiver.IsValid) return mainRiver;

            mainRiver = null;
            int bestKnots = 0;
            foreach (var r in FindObjectsByType<RiverPath>(FindObjectsSortMode.None))
            {
                if (!r.isActiveAndEnabled || !r.IsValid) continue;
                int knots = r.Container.Spline.Count;
                if (knots > bestKnots)
                {
                    bestKnots = knots;
                    mainRiver = r;
                }
            }
            return mainRiver;
        }
    }

    void OnValidate() => ApplyToMaterial();
    void Awake() => ApplyToMaterial();

    void ApplyToMaterial()
    {
        float speed = flowSpeed / uvTileLength;
        if (waterMaterial && waterMaterial.HasProperty("_FlowSpeed"))
            waterMaterial.SetFloat("_FlowSpeed", speed);

        MeshRenderer mr = GetComponent<MeshRenderer>();
        Material rendererMat = mr != null ? mr.sharedMaterial : null;
        if (rendererMat != null && rendererMat != waterMaterial && rendererMat.HasProperty("_FlowSpeed"))
            rendererMat.SetFloat("_FlowSpeed", speed);
    }

    public struct Sample
    {
        public Vector3 center, flowVelocity;
        public float lateral, halfWidth;
    }

    // Dipakai gameplay nanti: posisi dunia -> arus di titik itu
    public Sample SampleAt(Vector3 worldPos)
    {
        var c = Container;

        // RiverPath tanpa spline valid: sampel netral (bukan NaN)
        if (c == null || c.Spline == null || c.Spline.Count < 2)
            return new Sample { center = worldPos, flowVelocity = Vector3.zero, lateral = 0f, halfWidth = baseWidth * 0.5f };

        // Posisi query rusak (NaN/Infinity): kembalikan sampel netral supaya tidak merambat ke fisika
        if (!float.IsFinite(worldPos.x) || !float.IsFinite(worldPos.y) || !float.IsFinite(worldPos.z))
            return new Sample { center = Vector3.zero, flowVelocity = Vector3.zero, lateral = 0f, halfWidth = baseWidth * 0.5f };

        float3 local = c.transform.InverseTransformPoint(worldPos);
        SplineUtility.GetNearestPoint(c.Spline, local, out float3 _, out float t);
        if (float.IsNaN(t) || float.IsInfinity(t)) t = 0f;
        t = Mathf.Clamp01(t);

        Vector3 center = c.EvaluatePosition(t);

        // Arah aliran dari tangen spline; bila tangen rusak/nol, pakai beda-hingga dari posisi spline
        Vector3 dir = c.EvaluateTangent(t);
        dir.y = 0f;
        if (float.IsNaN(dir.x) || float.IsNaN(dir.z) || dir.sqrMagnitude < 1e-8f)
        {
            Vector3 a = c.EvaluatePosition(Mathf.Clamp01(t - 0.01f));
            Vector3 b = c.EvaluatePosition(Mathf.Clamp01(t + 0.01f));
            dir = b - a;
            dir.y = 0f;
        }
        if (float.IsNaN(dir.x) || float.IsNaN(dir.z) || dir.sqrMagnitude < 1e-8f) dir = Vector3.zero;
        else dir.Normalize();

        float w = WidthAt(t) * 0.5f;
        float half = (w > 0.01f) ? w : 0.01f;   // lebar 0/NaN membuat lateral / half = NaN
        Vector3 off = worldPos - center; off.y = 0f;
        float lateral = off.magnitude;
        float l01 = Mathf.Clamp01(lateral / half);

        float speed = flowSpeed * Mathf.Lerp(1f, edgeSpeedFactor, l01 * l01);
        if (float.IsNaN(speed) || float.IsInfinity(speed)) speed = 0f;

        return new Sample { center = center, flowVelocity = dir * speed, lateral = lateral, halfWidth = half };
    }
}