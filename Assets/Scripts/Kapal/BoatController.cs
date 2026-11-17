using Unity.Netcode;
using UnityEngine;

public class BoatController : NetworkBehaviour
{
    [Header("Pengaturan Tarikan & Rotasi")]
    [SerializeField] private float maxPullSpeed = 6f; // Kecepatan maksimum saat kapal sudah terseret penuh
    [SerializeField] private float acceleration = 2f; // Seberapa cepat kapal mencapai maxPullSpeed
    [SerializeField] private float rotationSmoothness = 4f; // Ayunan putaran kapal (semakin kecil = semakin berat/lambat)
    [SerializeField] private float stopDistance = 1.5f;

    [Tooltip("Jika aktif, arah hadap dikunci rapi ke 4 sisi kapal (Depan, Belakang, Kiri, Kanan).")]
    [SerializeField] private bool snapToFourSides = true;

    private Rigidbody rb;
    private bool isPulled = false;
    private Vector3 pullTarget;
    private Vector3 localPullDir = Vector3.forward;

    // Variabel untuk meniru inersia kapal di air
    private float currentSpeed = 0f;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();

        // Kunci posisi Y dan rotasi X, Z secara absolut di mesin fisika
        rb.constraints = RigidbodyConstraints.FreezePositionY |
                         RigidbodyConstraints.FreezeRotationX |
                         RigidbodyConstraints.FreezeRotationZ;
    }

    public void StartPull(Vector3 target, Vector3 pullerWorldPos)
    {
        if (!IsServer) return;

        pullTarget = target;
        isPulled = true;

        // Mulai dari 0 agar kapal "terasa berat" di awal tarikan dan fokus berputar dulu
        currentSpeed = 0f;

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
        else
        {
            localPullDir = Vector3.forward;
        }

        rb.angularVelocity = Vector3.zero;
    }

    public void StopPull()
    {
        if (!IsServer) return;
        isPulled = false;
        currentSpeed = 0f;

        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
    }

    private void FixedUpdate()
    {
        if (!IsServer) return;

        // Cegah tabrakan fisik mengubah arah rotasi secara tak terduga
        rb.angularVelocity = Vector3.zero;

        if (!isPulled) return;

        Vector3 toTarget = pullTarget - transform.position;
        toTarget.y = 0f;
        float distance = toTarget.magnitude;

        if (distance <= stopDistance)
        {
            rb.linearVelocity = Vector3.zero;
            return;
        }

        Vector3 directionToRock = toTarget.normalized;

        // 1. TRANSLASI: Akselerasi Maju (Lerp)
        // Kecepatan akan perlahan naik dari 0 menuju maxPullSpeed.
        // Hasilnya: Di awal klik, kapal seolah tertahan air (bergerak pelan) sambil memutar badannya.
        currentSpeed = Mathf.Lerp(currentSpeed, maxPullSpeed, Time.fixedDeltaTime * acceleration);
        rb.linearVelocity = directionToRock * currentSpeed;

        // 2. ROTASI: Ayunan Alami Air (Slerp)
        if (localPullDir.sqrMagnitude > 0.001f)
        {
            Quaternion forwardToRock = Quaternion.LookRotation(directionToRock, Vector3.up);
            float angleOffset = Vector3.SignedAngle(localPullDir, Vector3.forward, Vector3.up);
            Quaternion targetRotation = forwardToRock * Quaternion.Euler(0f, angleOffset, 0f);

            // Slerp (Spherical Linear Interpolation) bergerak cepat di awal lalu melambat secara eksponensial di akhir.
            // Ini sangat identik dengan gaya gesek benda di air (berayun memutar, lalu stabil/mengerem saat sudah sejajar).
            Quaternion nextRotation = Quaternion.Slerp(rb.rotation, targetRotation, rotationSmoothness * Time.fixedDeltaTime);
            rb.MoveRotation(nextRotation);
        }
    }
}