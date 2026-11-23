using System;
using Unity.Netcode;
using UnityEngine;

public class BoatImpactBroadcaster : NetworkBehaviour
{
    /// <summary>
    /// Event lokal (diterima semua client) dengan parameter intensity (0.0 = pelan, 1.0 = sangat keras).
    /// </summary>
    public static event Action<float> OnBoatImpact;

    [Header("Kalibrasi Kekuatan Tabrakan")]
    [Tooltip("Kecepatan benturan minimum (m/s) untuk memicu efek. Senggolan di bawah ini diabaikan.")]
    [SerializeField] private float minImpactSpeed = 0.8f;
    [Tooltip("Kecepatan benturan (m/s) yang dianggap intensitas maksimal (1.0).")]
    [SerializeField] private float maxImpactSpeed = 4.8f;
    [Tooltip("Jeda minimal antar efek agar tidak spam saat kapal bergesekan dengan batu.")]
    [SerializeField] private float impactCooldown = 0.35f;

    [Header("Integrasi Audio (Opsional)")]
    [Tooltip("ID SFX di AudioManager saat tabrakan terjadi. Kosongkan jika belum ada.")]
    [SerializeField] private string impactSfxId = "";

    private float lastImpactTime = -10f;

    private void OnCollisionEnter(Collision collision)
    {
        // Fisika kapal berjalan di Server, sehingga kalkulasi kecepatan tabrakan hanya akurat di Server
        if (!IsServer || !IsSpawned) return;

        if (!collision.collider.CompareTag("Batu") && !collision.collider.CompareTag("Sampah"))
            return;

        if (Time.time - lastImpactTime < impactCooldown)
            return;

        // Hitung kecepatan hantaman tegak lurus permukaan (normal impact)
        // agar gesekan menyamping tidak memicu efek tabrakan keras
        float impactSpeed = 0f;
        if (collision.contactCount > 0)
        {
            Vector3 normal = collision.GetContact(0).normal;
            normal.y = 0f;
            if (normal.sqrMagnitude > 0.0001f)
            {
                impactSpeed = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, normal.normalized));
            }
        }
        else
        {
            Vector3 flatVel = collision.relativeVelocity;
            flatVel.y = 0f;
            impactSpeed = flatVel.magnitude;
        }

        if (impactSpeed < minImpactSpeed) return;

        lastImpactTime = Time.time;

        // Normalisasi ke rentang 0..1
        float intensity = Mathf.InverseLerp(minImpactSpeed, maxImpactSpeed, impactSpeed);
        intensity = Mathf.Clamp(intensity, 0.15f, 1f);

        BroadcastImpactClientRpc(intensity);
    }

    [ClientRpc]
    private void BroadcastImpactClientRpc(float intensity)
    {
        OnBoatImpact?.Invoke(intensity);

        if (!string.IsNullOrEmpty(impactSfxId) && AudioManager.Instance != null)
        {
            AudioManager.Instance.PlaySFX(impactSfxId, 0.08f);
        }
    }
}