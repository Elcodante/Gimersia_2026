using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

public class BoatController : NetworkBehaviour
{
    private class PlayerPullData
    {
        public Vector3 targetPos;
        public Vector3 pullerWorldPos;
        public Collider targetCollider;
        public float holdTime;
        public float smoothWeight;
    }

    private class PlayerTetherData
    {
        public Vector3 anchorPos;
        public Vector3 playerWorldPos;
        public float maxLength;
    }

    [Header("Pengaturan Tarikan Halus & Natural")]
    [SerializeField] private float pullAcceleration = 13.5f;     // Gaya dorong kontinu (sesuai Linear Damping = 3)
    [SerializeField] private float maxPullSpeed = 4.2f;          // Batas kecepatan meluncur kapal yang nyaman & natural
    [SerializeField] private float pullRampUpTime = 0.65f;       // Durasi percepatan awal (semakin besar = semakin halus, tanpa hentakan)
    [SerializeField] private float cadenceFrequency = 0.85f;     // Irama tarikan tangan manusia per detik
    [SerializeField] private float cadenceDepth = 0.22f;         // Variasi lembut irama tarikan (0 = datar, 0.22 = ayunan halus)

    [Header("Pengaturan Rotasi Alami (Torque)")]
    [SerializeField] private float turnResponsiveness = 1.6f;    // Seberapa cepat lambung mengarah ke target
    [SerializeField] private float maxTurnSpeedRad = 1.5f;       // Batas kecepatan sudut (rad/s)
    [SerializeField] private float turnTorqueGain = 4.5f;        // Kehalusan torsi putar
    [SerializeField] private bool snapToFourSides = true;

    [Header("Pengaturan Menghindari & Meluncur di Batu")]
    [SerializeField] private float stopSurfaceDistance = 0.22f;  // Jarak berhenti terhadap batu TARGET
    [SerializeField] private float obstacleSlideBlend = 0.9f;    // Seberapa kuat arah tarikan dibelokkan mengikuti sisi batu penghalang
    [SerializeField] private float obstaclePushOutAccel = 4.0f;  // Dorongan halus keluar dari permukaan batu penghalang
    [SerializeField] private float tetherStiffness = 14.0f;      // Kekuatan tahan tali saat dikunci (tanpa teleportasi MovePosition)

    private Rigidbody rb;
    private Collider boatCollider;

    private readonly Dictionary<ulong, PlayerPullData> activePulls = new Dictionary<ulong, PlayerPullData>();
    private readonly Dictionary<ulong, PlayerTetherData> activeTethers = new Dictionary<ulong, PlayerTetherData>();
    private readonly Dictionary<Collider, Vector3> activeObstacleContacts = new Dictionary<Collider, Vector3>();
    private readonly List<Collider> staleContactsBuffer = new List<Collider>();

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        boatCollider = GetComponentInChildren<Collider>();

        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.constraints = RigidbodyConstraints.FreezePositionY |
                         RigidbodyConstraints.FreezeRotationX |
                         RigidbodyConstraints.FreezeRotationZ;

        // Pengaman jika Angular Damping di Inspector masih terlalu rendah
        if (rb.angularDamping < 2.5f)
        {
            rb.angularDamping = 3.5f;
        }
    }

    /// <summary>
    /// Mengecek apakah lambung kapal sedang bersentuhan atau merapat ke permukaan target tertentu.
    /// </summary>
    public bool IsTouchingOrAtSurface(Collider targetCol, Vector3 fallbackPoint)
    {
        if (targetCol != null && activeObstacleContacts.ContainsKey(targetCol))
        {
            return true;
        }

        return GetSurfaceDistanceTo(targetCol, fallbackPoint) <= stopSurfaceDistance;
    }

    public float GetSurfaceDistanceTo(Collider targetCol, Vector3 fallbackPoint)
    {
        Vector3 closestOnTarget = GetSafeClosestPoint(targetCol, transform.position, fallbackPoint);
        Vector3 closestOnBoat = (boatCollider != null)
            ? GetSafeClosestPoint(boatCollider, closestOnTarget, transform.position)
            : transform.position;

        closestOnTarget.y = 0f;
        closestOnBoat.y = 0f;

        return Vector3.Distance(closestOnBoat, closestOnTarget);
    }

    private Vector3 GetSafeClosestPoint(Collider col, Vector3 fromPoint, Vector3 fallbackPoint)
    {
        if (col == null) return fallbackPoint;

        if (col is MeshCollider meshCol && !meshCol.convex)
        {
            return col.bounds.ClosestPoint(fromPoint);
        }

        return col.ClosestPoint(fromPoint);
    }

    /// <summary>
    /// Mengatur status tarikan kontinu dari pemain tertentu (dipanggil Server dari LassoController).
    /// </summary>
    public void SetPlayerPull(ulong clientId, bool isPulling, Vector3 targetPos, Vector3 pullerWorldPos, Collider targetCollider)
    {
        if (!IsServer || !IsSpawned) return;

        if (!isPulling)
        {
            activePulls.Remove(clientId);
            return;
        }

        if (!activePulls.TryGetValue(clientId, out PlayerPullData data))
        {
            data = new PlayerPullData();
            activePulls[clientId] = data;
        }

        data.targetPos = targetPos;
        data.pullerWorldPos = pullerWorldPos;
        data.targetCollider = targetCollider;
    }

    /// <summary>
    /// Mengatur kaitan tali statis (saat tombol F & G tidak ditekan) dengan gaya fisika murni.
    /// </summary>
    public void SetPlayerTether(ulong clientId, bool isActive, Vector3 anchorPos, Vector3 playerWorldPos, float maxLength)
    {
        if (!IsServer || !IsSpawned) return;

        if (!isActive)
        {
            activeTethers.Remove(clientId);
            return;
        }

        if (!activeTethers.TryGetValue(clientId, out PlayerTetherData data))
        {
            data = new PlayerTetherData();
            activeTethers[clientId] = data;
        }

        data.anchorPos = anchorPos;
        data.playerWorldPos = playerWorldPos;
        data.maxLength = maxLength;
    }

    public void ClearPlayerInputs(ulong clientId)
    {
        if (!IsServer) return;
        activePulls.Remove(clientId);
        activeTethers.Remove(clientId);
    }

    private void FixedUpdate()
    {
        if (!IsServer || !IsSpawned || rb.isKinematic) return;

        CleanupStaleContacts();
        ProcessActivePulls();
        ProcessActiveTethers();
        ClampHorizontalSpeed();
    }

    private void CleanupStaleContacts()
    {
        if (activeObstacleContacts.Count == 0) return;

        staleContactsBuffer.Clear();
        foreach (var kvp in activeObstacleContacts)
        {
            if (kvp.Key == null || !kvp.Key.gameObject.activeInHierarchy)
            {
                staleContactsBuffer.Add(kvp.Key);
            }
        }

        for (int i = 0; i < staleContactsBuffer.Count; i++)
        {
            activeObstacleContacts.Remove(staleContactsBuffer[i]);
        }
    }

    private void ProcessActivePulls()
    {
        if (activePulls.Count == 0) return;

        float dt = Time.fixedDeltaTime;

        foreach (var kvp in activePulls)
        {
            PlayerPullData pull = kvp.Value;

            // Jika sudah menyentuh permukaan batu/sampah TARGET, hentikan tarikan untuk pemain ini
            if (IsTouchingOrAtSurface(pull.targetCollider, pull.targetPos))
            {
                pull.smoothWeight = Mathf.MoveTowards(pull.smoothWeight, 0f, dt * 4f);
                continue;
            }

            Vector3 toTarget = pull.targetPos - transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.001f) continue;

            // 1. Ramp-up halus + gelombang irama tangan manusia (tanpa hentakan mendadak)
            pull.holdTime += dt;
            float rampSpeed = (pullRampUpTime > 0.01f) ? (1f / pullRampUpTime) : 10f;
            pull.smoothWeight = Mathf.MoveTowards(pull.smoothWeight, 1f, dt * rampSpeed);

            // SmoothStep membuat akselerasi awal melengkung lembut (ease-in-out)
            float easedWeight = pull.smoothWeight * pull.smoothWeight * (3f - 2f * pull.smoothWeight);
            float cadence = 1f - cadenceDepth * 0.5f * (1f - Mathf.Sin(pull.holdTime * cadenceFrequency * Mathf.PI * 2f));

            Vector3 desiredDir = toTarget.normalized;
            bool isDeflectingObstacle = false;

            // 2. Belokkan arah tarikan secara otomatis jika menyenggol BATU PENGHALANG di tengah jalur
            foreach (var contact in activeObstacleContacts)
            {
                if (contact.Key == pull.targetCollider) continue;

                Vector3 pushOutNormal = contact.Value;
                float intoObstacle = Vector3.Dot(desiredDir, -pushOutNormal);

                if (intoObstacle > 0f)
                {
                    isDeflectingObstacle = true;

                    // Proyeksikan arah tarik agar sejajar dengan sisi batu (meluncur memutari batu)
                    Vector3 slideDir = Vector3.ProjectOnPlane(desiredDir, pushOutNormal);
                    slideDir.y = 0f;

                    if (slideDir.sqrMagnitude < 0.04f)
                    {
                        // Jika menabrak tepat tegak lurus di tengah, pilih arah samping terdekat
                        Vector3 tangent = Vector3.Cross(Vector3.up, pushOutNormal).normalized;
                        Vector3 rockToBoat = transform.position - contact.Key.transform.position;
                        rockToBoat.y = 0f;
                        if (Vector3.Dot(tangent, rockToBoat) < 0f) tangent = -tangent;
                        slideDir = tangent;
                    }
                    else
                    {
                        slideDir.Normalize();
                    }

                    desiredDir = Vector3.Lerp(desiredDir, slideDir, obstacleSlideBlend).normalized;

                    // Dorongan tipis menjauhi permukaan batu agar kapal tidak menempel/terjepit
                    rb.AddForce(pushOutNormal * obstaclePushOutAccel * easedWeight, ForceMode.Acceleration);
                }
            }

            // 3. Terapkan gaya tarik murni ke Rigidbody (PhysX menjaga tidak ada penembusan)
            float effectiveAccel = pullAcceleration * easedWeight * cadence;
            rb.AddForce(desiredDir * effectiveAccel, ForceMode.Acceleration);

            // 4. Terapkan torsi rotasi halus agar sisi kapal yang ditarik menghadap arah gerak/target
            ApplyNaturalTurnTorque(desiredDir, pull.pullerWorldPos, easedWeight, isDeflectingObstacle);
        }
    }

    private void ApplyNaturalTurnTorque(Vector3 moveDir, Vector3 pullerWorldPos, float weight, bool isDeflectingObstacle)
    {
        Vector3 localPos = transform.InverseTransformPoint(pullerWorldPos);
        localPos.y = 0f;

        Vector3 localPullDir = Vector3.forward;
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

        Quaternion forwardToDir = Quaternion.LookRotation(moveDir, Vector3.up);
        float angleOffset = Vector3.SignedAngle(localPullDir, Vector3.forward, Vector3.up);
        Quaternion targetRotation = forwardToDir * Quaternion.Euler(0f, angleOffset, 0f);

        // PD Controller dalam satuan radian/detik: memutar halus tanpa pernah memaksa menembus batu
        float angleErrorRad = Mathf.DeltaAngle(rb.rotation.eulerAngles.y, targetRotation.eulerAngles.y) * Mathf.Deg2Rad;
        float desiredAngularVel = Mathf.Clamp(angleErrorRad * turnResponsiveness, -maxTurnSpeedRad, maxTurnSpeedRad);

        // Saat sedang meluncur menghindari batu penghalang, longgarkan torsi agar lambung bebas menyesuaikan sudut
        float obstacleTorqueScale = isDeflectingObstacle ? 0.35f : 1f;
        float torqueAccel = (desiredAngularVel - rb.angularVelocity.y) * turnTorqueGain * weight * obstacleTorqueScale;

        rb.AddTorque(Vector3.up * torqueAccel, ForceMode.Acceleration);
    }

    private void ProcessActiveTethers()
    {
        if (activeTethers.Count == 0) return;

        foreach (var kvp in activeTethers)
        {
            PlayerTetherData tether = kvp.Value;

            Vector3 outward = tether.playerWorldPos - tether.anchorPos;
            outward.y = 0f;
            float currentDist = outward.magnitude;

            if (currentDist > tether.maxLength && currentDist > 0.001f)
            {
                Vector3 outwardDir = outward / currentDist;
                float excess = currentDist - tether.maxLength;

                // Tahan kecepatan yang menjauh dengan gaya pegas fisika murni (bukan MovePosition)
                float outwardSpeed = Mathf.Max(0f, Vector3.Dot(rb.linearVelocity, outwardDir));
                float pullBackAccel = Mathf.Min(excess * tetherStiffness + outwardSpeed * 4f, 20f);
                rb.AddForce(-outwardDir * pullBackAccel, ForceMode.Acceleration);
            }
        }
    }

    private void ClampHorizontalSpeed()
    {
        Vector3 flatVel = new Vector3(rb.linearVelocity.x, 0f, rb.linearVelocity.z);
        if (flatVel.magnitude > maxPullSpeed)
        {
            Vector3 clamped = flatVel.normalized * maxPullSpeed;
            rb.linearVelocity = new Vector3(clamped.x, rb.linearVelocity.y, clamped.z);
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        RecordObstacleContact(collision);
    }

    private void OnCollisionStay(Collision collision)
    {
        RecordObstacleContact(collision);
    }

    private void OnCollisionExit(Collision collision)
    {
        if (collision.collider.CompareTag("Batu") || collision.collider.CompareTag("Sampah"))
        {
            activeObstacleContacts.Remove(collision.collider);
        }
    }

    private void RecordObstacleContact(Collision collision)
    {
        if (!IsServer || !IsSpawned) return;

        if (collision.collider.CompareTag("Batu") || collision.collider.CompareTag("Sampah"))
        {
            Vector3 avgNormal = Vector3.zero;
            for (int i = 0; i < collision.contactCount; i++)
            {
                Vector3 n = collision.GetContact(i).normal;
                n.y = 0f;
                avgNormal += n;
            }

            if (avgNormal.sqrMagnitude > 0.0001f)
            {
                activeObstacleContacts[collision.collider] = avgNormal.normalized;
            }
        }
    }
}