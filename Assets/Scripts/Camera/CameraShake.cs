using Unity.Cinemachine;
using UnityEngine;

[RequireComponent(typeof(CinemachineImpulseSource))]
public class CameraShake : MonoBehaviour
{
    [Header("Pengaturan Kekuatan Getaran")]
    [SerializeField] private float minShakeForce = 0.3f;
    [SerializeField] private float maxShakeForce = 1.5f;

    [Header("Skala Arah Getaran")]
    [Tooltip("Mengatur dominasi getaran tiap sumbu (X dan Z lebih dominan untuk sudut kamera atas).")]
    [SerializeField] private Vector3 axisScale = new Vector3(1f, 0.5f, 1f);

    private CinemachineImpulseSource impulseSource;

    private void Awake()
    {
        impulseSource = GetComponent<CinemachineImpulseSource>();
    }

    private void OnEnable()
    {
        BoatImpactBroadcaster.OnBoatImpact += TriggerShake;
    }

    private void OnDisable()
    {
        BoatImpactBroadcaster.OnBoatImpact -= TriggerShake;
    }

    public void TriggerShake(float intensity)
    {
        if (impulseSource == null) return;

        intensity = Mathf.Clamp01(intensity);
        float force = Mathf.Lerp(minShakeForce, maxShakeForce, intensity);

        Vector3 velocity = Vector3.Scale(Random.onUnitSphere, axisScale);
        if (velocity.sqrMagnitude < 0.001f)
        {
            velocity = Vector3.right;
        }

        impulseSource.GenerateImpulse(velocity.normalized * force);
    }
}