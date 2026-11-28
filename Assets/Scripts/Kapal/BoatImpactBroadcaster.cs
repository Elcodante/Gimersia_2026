using System;
using Unity.Netcode;
using UnityEngine;

public class BoatImpactBroadcaster : NetworkBehaviour
{
    /// <summary>
    /// Event lokal (diterima semua client) untuk benturan keras yang memicu cipratan layar &amp; getar kamera.
    /// Parameter intensity (0.0 = ambang minimum layar, 1.0 = benturan maksimal).
    /// </summary>
    public static event Action<float> OnBoatImpact;

    [Header("Kalibrasi Kekuatan Tabrakan")]
    [Tooltip("Kecepatan benturan minimum (m/s) untuk memicu cipratan partikel kecil di sisi kapal.")]
    [SerializeField] private float minImpactSpeed = 0.75f;
    [Tooltip("Kecepatan benturan minimum (m/s) agar cipratan air MUNCUL DI LAYAR. Di bawah ini hanya partikel di sisi kapal.")]
    [SerializeField] private float screenSplashMinSpeed = 2.8f;
    [Tooltip("Kecepatan benturan (m/s) yang dianggap intensitas maksimal (1.0).")]
    [SerializeField] private float maxImpactSpeed = 5.5f;

    [Header("Jeda Anti-Spam (Cooldown)")]
    [SerializeField] private float lightImpactCooldown = 0.25f;
    [SerializeField] private float heavyImpactCooldown = 0.45f;

    [Header("Filter Objek Tabrakan")]
    [Tooltip("Jika dicentang, hanya memicu cipratan untuk objek bertag 'Batu' atau 'Sampah'. Matikan agar tepian sungai/rintangan lain juga memicu cipratan.")]
    [SerializeField] private bool onlyTaggedObstacles = false;

    [Header("Skala Partikel Sisi Kapal")]
    [SerializeField, Min(0.1f)] private float minHullSplashScale = 0.65f;
    [SerializeField, Min(0.2f)] private float maxHullSplashScale = 1.65f;

    [Header("Integrasi Audio (Opsional)")]
    [Tooltip("ID SFX di AudioManager saat tabrakan keras terjadi. Kosongkan jika belum ada.")]
    [SerializeField] private string impactSfxId = "";
    [Tooltip("ID SFX opsional untuk senggolan kecil.")]
    [SerializeField] private string lightImpactSfxId = "";

    private float lastLightImpactTime = -10f;
    private float lastHeavyImpactTime = -10f;

    public override void OnNetworkSpawn()
    {
        WaterSplashVFX.Prewarm();
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!IsServer || !IsSpawned) return;
        if (!GameSpawner.IsMatchReady) return;

        Collider other = collision.collider;
        if (other == null || other.isTrigger) return;

        // Jangan anggap sentuhan pemain sebagai tabrakan kapal
        if (other.GetComponentInParent<PlayerMovement>() != null) return;

        if (onlyTaggedObstacles && !other.CompareTag("Batu") && !other.CompareTag("Sampah"))
            return;

        if (!TryExtractHorizontalImpact(collision, out Vector3 hitPoint, out Vector3 hitNormal, out float impactSpeed))
            return;

        if (impactSpeed < minImpactSpeed) return;

        bool triggersScreenSplash = impactSpeed >= screenSplashMinSpeed;
        float now = Time.time;

        if (triggersScreenSplash)
        {
            if (now - lastHeavyImpactTime < heavyImpactCooldown) return;
            lastHeavyImpactTime = now;
            lastLightImpactTime = now;
        }
        else
        {
            if (now - lastLightImpactTime < lightImpactCooldown) return;
            lastLightImpactTime = now;
        }

        float safeMax = Mathf.Max(minImpactSpeed + 0.2f, maxImpactSpeed);
        float hullIntensity = Mathf.Clamp01(Mathf.InverseLerp(minImpactSpeed, safeMax, impactSpeed));

        float screenIntensity = 0f;
        if (triggersScreenSplash)
        {
            float safeScreenMax = Mathf.Max(screenSplashMinSpeed + 0.5f, maxImpactSpeed);
            screenIntensity = Mathf.Clamp(Mathf.InverseLerp(screenSplashMinSpeed, safeScreenMax, impactSpeed), 0.2f, 1f);
        }

        BroadcastImpactClientRpc(hullIntensity, triggersScreenSplash, screenIntensity, hitPoint, hitNormal);
    }

    private bool TryExtractHorizontalImpact(
        Collision collision,
        out Vector3 hitPoint,
        out Vector3 hitNormal,
        out float impactSpeed)
    {
        hitPoint = transform.position;
        hitNormal = Vector3.forward;
        impactSpeed = 0f;

        Vector3 pointSum = Vector3.zero;
        Vector3 normalSum = Vector3.zero;
        int validCount = 0;

        for (int i = 0; i < collision.contactCount; i++)
        {
            ContactPoint cp = collision.GetContact(i);
            Vector3 flatN = cp.normal;
            flatN.y = 0f;
            if (flatN.sqrMagnitude > 0.04f) // Abaikan kontak lantai/vertikal murni
            {
                pointSum += cp.point;
                normalSum += flatN.normalized;
                validCount++;
            }
        }

        if (validCount == 0)
        {
            if (collision.contactCount > 0) return false;
            hitPoint = collision.collider.bounds.ClosestPoint(transform.position);
            Vector3 fallbackDir = transform.position - hitPoint;
            fallbackDir.y = 0f;
            hitNormal = fallbackDir.sqrMagnitude > 0.0001f ? fallbackDir.normalized : Vector3.forward;
        }
        else
        {
            hitPoint = pointSum / validCount;
            hitNormal = normalSum.sqrMagnitude > 0.0001f ? normalSum.normalized : Vector3.forward;
        }

        Vector3 flatRelVel = collision.relativeVelocity;
        flatRelVel.y = 0f;
        impactSpeed = Mathf.Abs(Vector3.Dot(flatRelVel, hitNormal));
        return true;
    }

    [ClientRpc]
    private void BroadcastImpactClientRpc(
        float hullIntensity,
        bool triggersScreenSplash,
        float screenIntensity,
        Vector3 hitPoint,
        Vector3 hitNormal)
    {
        // 1. Selalu munculkan partikel air 3D di permukaan air tepat pada sisi kapal yang menabrak
        Vector3 splashPos = hitPoint;
        RiverPath river = RiverPath.Main;
        if (river != null && river.IsValid)
        {
            splashPos.y = river.SampleAt(hitPoint).center.y + 0.04f;
        }
        else
        {
            splashPos.y = transform.position.y + 0.04f;
        }

        float hullScale = Mathf.Lerp(minHullSplashScale, maxHullSplashScale, hullIntensity);
        WaterSplashVFX.PlayBoatImpactSplash(splashPos, hitNormal, hullScale);

        // 2. Cipratan di layar & getaran kamera hanya jika melebihi screenSplashMinSpeed
        if (triggersScreenSplash)
        {
            OnBoatImpact?.Invoke(screenIntensity);

            if (!string.IsNullOrEmpty(impactSfxId) && AudioManager.Instance != null)
            {
                AudioManager.Instance.PlaySFX(impactSfxId, 0.08f);
            }
        }
        else if (!string.IsNullOrEmpty(lightImpactSfxId) && AudioManager.Instance != null)
        {
            AudioManager.Instance.PlaySFX(lightImpactSfxId, 0.1f);
        }
    }
}