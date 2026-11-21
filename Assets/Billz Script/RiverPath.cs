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

    void OnValidate() => ApplyToMaterial();
    void Awake() => ApplyToMaterial();

    void ApplyToMaterial()
    {
        if (waterMaterial && waterMaterial.HasProperty("_FlowSpeed"))
            waterMaterial.SetFloat("_FlowSpeed", flowSpeed / uvTileLength);
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
        float3 local = c.transform.InverseTransformPoint(worldPos);
        SplineUtility.GetNearestPoint(c.Spline, local, out float3 _, out float t);

        Vector3 center = c.EvaluatePosition(t);
        Vector3 dir = c.EvaluateTangent(t);
        dir.y = 0f;
        // Pengaman: tangen spline bisa nol/NaN (knot berimpit, ujung spline) -> Normalize jadi NaN
        if (float.IsNaN(dir.x) || float.IsNaN(dir.z) || dir.sqrMagnitude < 1e-8f) dir = Vector3.zero;
        else dir.Normalize();

        // Pengaman: lebar 0/NaN membuat lateral / half = NaN
        float w = WidthAt(t) * 0.5f;
        float half = (w > 0.01f) ? w : 0.01f;
        Vector3 off = worldPos - center; off.y = 0f;
        float lateral = off.magnitude;
        float l01 = Mathf.Clamp01(lateral / half);

        float speed = flowSpeed * Mathf.Lerp(1f, edgeSpeedFactor, l01 * l01);
        if (float.IsNaN(speed) || float.IsInfinity(speed)) speed = 0f;

        return new Sample { center = center, flowVelocity = dir * speed, lateral = lateral, halfWidth = half };
    }
}