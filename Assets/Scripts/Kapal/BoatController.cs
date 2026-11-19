using Unity.Netcode;
using UnityEngine;

public class BoatController : NetworkBehaviour
{
    [Header("Pengaturan Tarikan (Tap Space) & Rotasi")]
    [SerializeField] private float tugSpeedBoost = 2.0f;     // Tambahan kecepatan setiap 1x tekan Space
    [SerializeField] private float maxPullSpeed = 6.5f;      // Batas kecepatan maksimal kapal
    [SerializeField] private float waterDrag = 3.5f;         // Hambatan air (semakin besar = kapal makin cepat melambat jika berhenti tekan F)
    [SerializeField] private float rotationSmoothness = 5f;  // Kecepatan ayunan lambung kapal menghadap batu
    [SerializeField] private float stopDistance = 1.5f;

    [Tooltip("Jika aktif, arah hadap dikunci rapi ke 4 sisi kapal (Depan, Belakang, Kiri, Kanan).")]
    [SerializeField] private bool snapToFourSides = true;

    private Rigidbody rb;
    private Vector3 pullTarget;
    private Vector3 localPullDir = Vector3.forward;
    private float currentSpeed = 0f;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();

        rb.constraints = RigidbodyConstraints.FreezePositionY |
                         RigidbodyConstraints.FreezeRotationX |
                         RigidbodyConstraints.FreezeRotationZ;
    }

    /// <summary>
    /// Dipanggil oleh Server setiap kali pemain menekan tombol F saat laso terkait di batu.
    /// </summary>
    public void TugBoat(Vector3 target, Vector3 pullerWorldPos)
    {
        if (!IsServer || !IsSpawned) return;

        Vector3 toTarget = target - transform.position;
        toTarget.y = 0f;

        if (toTarget.magnitude <= stopDistance) return;

        pullTarget = target;

        // Tentukan sisi kapal tempat pemain menarik agar sisi tersebut berputar menghadap batu
        Vector3 localPos = transform.InverseTransformPoint(pullerWorldPos);
        localPos.y = 0f;

        if (localPos.sqrMagnitude > 0.01f)
        {
            if (snapToFourSides)
            {
                if (Mathf.Abs(localPos.z) >= Mathf.Abs(localPos.x))
                    localPullDir = (localPos.z >= 0f) ? Vector3.forward : Vector3.back;
                else
                    localPullDir = (localPos.x >= 0f) ? Vector3.right : Vector3.left;
            }
            else
            {
                localPullDir = localPos.normalized;
            }
        }

        // Suntikkan lonjakan kecepatan tarikan (dibatasi maksimal maxPullSpeed)
        currentSpeed = Mathf.Min(currentSpeed + tugSpeedBoost, maxPullSpeed);
    }

    /// <summary>
    /// Dipanggil saat laso dilepas. Kecepatan sisa akan melambat alami oleh waterDrag.
    /// </summary>
    public void StopPull()
    {
        if (!IsServer) return;
        if (!rb.isKinematic)
        {
            rb.angularVelocity = Vector3.zero;
        }
    }

    private void FixedUpdate()
    {
        if (!IsServer || !IsSpawned) return;

        if (!rb.isKinematic)
        {
            rb.angularVelocity = Vector3.zero;
        }

        if (currentSpeed > 0.02f)
        {
            Vector3 toTarget = pullTarget - transform.position;
            toTarget.y = 0f;

            // Hentikan laju jika kapal sudah merapat ke batu
            if (toTarget.magnitude <= stopDistance)
            {
                currentSpeed = 0f;
                if (!rb.isKinematic) rb.linearVelocity = Vector3.zero;
                return;
            }

            Vector3 directionToRock = toTarget.normalized;

            // 1. TRANSLASI: Gerakkan kapal sesuai momentum tarikan saat ini
            if (!rb.isKinematic)
            {
                rb.linearVelocity = directionToRock * currentSpeed;
            }

            // 2. ROTASI: Ayunkan sisi kapal menghadap batu sebanding dengan kecepatan tarikan
            if (localPullDir.sqrMagnitude > 0.001f)
            {
                Quaternion forwardToRock = Quaternion.LookRotation(directionToRock, Vector3.up);
                float angleOffset = Vector3.SignedAngle(localPullDir, Vector3.forward, Vector3.up);
                Quaternion targetRotation = forwardToRock * Quaternion.Euler(0f, angleOffset, 0f);

                float speedRatio = Mathf.Clamp01(currentSpeed / maxPullSpeed);
                Quaternion nextRotation = Quaternion.Slerp(
                    rb.rotation,
                    targetRotation,
                    rotationSmoothness * speedRatio * Time.fixedDeltaTime
                );
                rb.MoveRotation(nextRotation);
            }

            // 3. HAMBATAN AIR: Perlambat kecepatan secara bertahap sampai pemain menekan F lagi
            currentSpeed = Mathf.Lerp(currentSpeed, 0f, waterDrag * Time.fixedDeltaTime);
        }
        else
        {
            currentSpeed = 0f;
            if (!rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
            }
        }
    }
}