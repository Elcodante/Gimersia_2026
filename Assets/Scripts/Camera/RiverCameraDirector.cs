using Unity.Cinemachine;
using Unity.Cinemachine.TargetTracking;
using UnityEngine;

/// <summary>
/// Mengarahkan CinemachineCamera secara halus mengikuti alur hulu sungai (melawan arus)
/// sambil tetap mempertahankan pelacakan posisi Target Group / pemain.
/// Ketika sungai berbelok, kamera memutar perlahan ke arah tantangan berikutnya di depan kapal.
/// </summary>
[DefaultExecutionOrder(-50)]
[DisallowMultipleComponent]
[RequireComponent(typeof(CinemachineCamera))]
[RequireComponent(typeof(CinemachineFollow))]
public class RiverCameraDirector : MonoBehaviour
{
    [Header("Referensi (Opsional — dicari otomatis)")]
    [SerializeField] private RiverPath river;
    [Tooltip("Transform acuan posisi di sungai untuk membaca belokan (kosong = otomatis pakai Kapal / FollowTarget).")]
    [SerializeField] private Transform riverSampleSource;

    [Header("Framing Kamera")]
    [Tooltip("Jika aktif, jarak, tinggi, pitch, dan yaw awal diambil dari pengaturan CinemachineFollow & Transform di scene.")]
    [SerializeField] private bool preserveInitialOffsetFromScene = true;
    [SerializeField] private float followDistance = 15f;
    [SerializeField] private float followHeight = 14f;
    [SerializeField, Range(15f, 80f)] private float pitchAngle = 45f;
    [SerializeField] private float yawOffsetDegrees = 0f;

    [Header("Antisipasi Belokan Sungai")]
    [Tooltip("Arah maju kamera melawan arus sungai (Hilir -> Hulu). Matikan jika level mengalir searah arus.")]
    [SerializeField] private bool faceUpstream = true;
    [Tooltip("Jarak pandang ke depan (meter) sepanjang sungai untuk mulai berbelok halus sebelum tikungan.")]
    [SerializeField, Range(0f, 40f)] private float lookAheadDistance = 14f;
    [Tooltip("Bobot arah look-ahead dibanding arah sungai tepat di posisi kapal (0 = lokal, 1 = penuh ke depan).")]
    [SerializeField, Range(0f, 1f)] private float lookAheadBlend = 0.65f;

    [Header("Kehalusan & Efisiensi (WebGL)")]
    [Tooltip("Waktu smoothing rotasi horizontal kamera (detik). Lebih besar = lebih lembut dan tidak bikin pusing.")]
    [SerializeField, Range(0.15f, 2.5f)] private float yawSmoothTime = 0.85f;
    [Tooltip("Batas kecepatan putar kamera (derajat/detik).")]
    [SerializeField, Range(15f, 180f)] private float maxYawSpeed = 55f;
    [Tooltip("Interval sampling spline sungai (detik) agar ringan di WebGL.")]
    [SerializeField, Range(0.02f, 0.25f)] private float sampleInterval = 0.08f;

    private CinemachineCamera cmCamera;
    private CinemachineFollow cmFollow;

    private float currentYaw;
    private float targetYaw;
    private float yawVelocity;
    private float sampleTimer;
    private bool initialized;
    private bool riverSearched;
    private float boatSearchTimer;

    private void Awake()
    {
        cmCamera = GetComponent<CinemachineCamera>();
        cmFollow = GetComponent<CinemachineFollow>();

        if (cmFollow != null)
        {
            // Wajib WorldSpace agar FollowOffset tidak ikut berputar liar bersama kapal/TargetGroup
            cmFollow.TrackerSettings.BindingMode = BindingMode.WorldSpace;

            if (preserveInitialOffsetFromScene)
            {
                Vector3 initialOffset = cmFollow.FollowOffset;
                float horizontalDist = new Vector2(initialOffset.x, initialOffset.z).magnitude;
                if (horizontalDist > 0.1f)
                {
                    followDistance = horizontalDist;
                }
                followHeight = initialOffset.y;
                pitchAngle = NormalizePitch(transform.eulerAngles.x);
            }
        }

        currentYaw = transform.eulerAngles.y;
        targetYaw = currentYaw;
        initialized = true;
    }

    private void Start()
    {
        EnsureReferences();
        // Sinkronkan target yaw awal tanpa hentakan bila sudah ada sampel sungai valid
        if (TrySampleDesiredYaw(out float sampledYaw))
        {
            targetYaw = sampledYaw;
        }
        ApplyCameraPose(currentYaw);
    }

    private void LateUpdate()
    {
        if (!initialized || cmFollow == null) return;

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        sampleTimer -= dt;
        if (sampleTimer <= 0f)
        {
            sampleTimer = sampleInterval;
            EnsureReferences();

            if (TrySampleDesiredYaw(out float desiredYaw))
            {
                targetYaw = desiredYaw;
            }
        }

        currentYaw = Mathf.SmoothDampAngle(currentYaw, targetYaw, ref yawVelocity, yawSmoothTime, maxYawSpeed, dt);
        ApplyCameraPose(currentYaw);
    }

    private void EnsureReferences()
    {
        if (river == null && !riverSearched)
        {
            riverSearched = true;
            river = RiverPath.Main;
        }

        if (riverSampleSource == null)
        {
            boatSearchTimer -= sampleInterval;
            if (boatSearchTimer <= 0f)
            {
                boatSearchTimer = 0.5f;
                BoatController boat = FindFirstObjectByType<BoatController>();
                if (boat != null)
                {
                    riverSampleSource = boat.transform;
                }
            }
        }
    }

    private bool TrySampleDesiredYaw(out float desiredYaw)
    {
        desiredYaw = targetYaw;
        if (river == null || !river.IsValid) return false;

        Vector3 samplePos;
        if (riverSampleSource != null)
        {
            samplePos = riverSampleSource.position;
        }
        else if (cmCamera != null && cmCamera.Target.TrackingTarget != null)
        {
            samplePos = cmCamera.Target.TrackingTarget.position;
        }
        else
        {
            return false;
        }

        RiverPath.Sample currentSample = river.SampleAt(samplePos);
        Vector3 localDir = ExtractForwardDir(currentSample.flowVelocity);
        if (localDir.sqrMagnitude < 0.0001f) return false;

        Vector3 combinedDir = localDir;

        if (lookAheadDistance > 0.5f && lookAheadBlend > 0.01f)
        {
            Vector3 aheadPos = currentSample.center + localDir * lookAheadDistance;
            RiverPath.Sample aheadSample = river.SampleAt(aheadPos);
            Vector3 aheadDir = ExtractForwardDir(aheadSample.flowVelocity);

            if (aheadDir.sqrMagnitude > 0.0001f)
            {
                // Gabungkan vektor secara halus di bidang XZ agar tikungan terantisipasi tanpa patah
                combinedDir = Vector3.Slerp(localDir, aheadDir, lookAheadBlend);
                combinedDir.y = 0f;
                if (combinedDir.sqrMagnitude > 0.0001f)
                {
                    combinedDir.Normalize();
                }
                else
                {
                    combinedDir = localDir;
                }
            }
        }

        desiredYaw = Mathf.Atan2(combinedDir.x, combinedDir.z) * Mathf.Rad2Deg + yawOffsetDegrees;
        return float.IsFinite(desiredYaw);
    }

    private Vector3 ExtractForwardDir(Vector3 flowVelocity)
    {
        Vector3 dir = faceUpstream ? -flowVelocity : flowVelocity;
        dir.y = 0f;
        float sqrMag = dir.sqrMagnitude;
        if (!float.IsFinite(sqrMag) || sqrMag < 0.0001f) return Vector3.zero;
        return dir / Mathf.Sqrt(sqrMag);
    }

    private void ApplyCameraPose(float yaw)
    {
        // Posisi kamera selalu berada di belakang target (searah -forward yaw) pada jarak & ketinggian tetap
        Quaternion yawRot = Quaternion.Euler(0f, yaw, 0f);
        cmFollow.FollowOffset = yawRot * new Vector3(0f, followHeight, -followDistance);

        // Orientasi virtual camera menghadap ke depan (pitch ke bawah + yaw mengikuti sungai)
        transform.rotation = Quaternion.Euler(pitchAngle, yaw, 0f);
    }

    private static float NormalizePitch(float rawEulerX)
    {
        if (rawEulerX > 180f) rawEulerX -= 360f;
        return Mathf.Clamp(rawEulerX, 15f, 80f);
    }
}

