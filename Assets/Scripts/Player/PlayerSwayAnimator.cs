using UnityEngine;

/// <summary>
/// Animasi sederhana lewat kode: badan miring beberapa derajat seperti benda tegak ditiup angin
/// (bertumpu di kaki), kepala menyusul dengan delay beberapa milidetik.
/// Saat menarik tali laso, badan juga meniru gerakan tangan manusia yang menarik bergantian (1___1___1).
/// Aman untuk multiplayer: dijalankan secara lokal di semua client.
/// </summary>
[DisallowMultipleComponent]
public class PlayerSwayAnimator : MonoBehaviour
{
    [Header("Bagian Tubuh")]
    [SerializeField] private Transform body;
    [SerializeField] private Transform head;

    [Header("Titik Tumpu (Kaki)")]
    [Tooltip("Opsional: Transform kosong di posisi telapak kaki. Kosong = pakai Feet Local Offset.")]
    [SerializeField] private Transform feetPivot;
    [Tooltip("Posisi kaki relatif ke root Player (local). Kapsul tinggi 2 dengan pivot di tengah = (0, -1, 0).")]
    [SerializeField] private Vector3 feetLocalOffset = new Vector3(0f, -1f, 0f);

    [Header("Kemiringan Badan")]
    [SerializeField] private float maxTiltAngle = 12f;
    [Tooltip("Kecepatan gerak (m/detik) yang menghasilkan kemiringan penuh.")]
    [SerializeField] private float speedForMaxTilt = 5f;
    [Tooltip("Makin besar = badan makin lambat menyusul (lebih 'berat').")]
    [SerializeField] private float bodySmoothTime = 0.12f;
    [Tooltip("Centang bila ingin miring berlawanan arah gerak (seperti tertiup angin dari depan).")]
    [SerializeField] private bool invertTilt = false;

    [Header("Kepala")]
    [Tooltip("Delay kepala terhadap badan, dalam milidetik.")]
    [SerializeField] private float headDelayMs = 90f;
    [Tooltip("1 = sama dengan badan, >1 = kepala miring lebih jauh.")]
    [SerializeField] private float headTiltMultiplier = 1.3f;

    [Header("Irama Menarik Tali (1___1___1)")]
    [Tooltip("Sudut badan condong ke belakang saat momen tangan menarik tali.")]
    [SerializeField, Range(0f, 35f)] private float pullLeanBackAngle = 20f;
    [Tooltip("Sudut badan condong ke depan saat tangan menggapai ke depan untuk ganti tangan.")]
    [SerializeField, Range(0f, 20f)] private float pullReachForwardAngle = 8f;
    [Tooltip("Sudut miring ke samping sesuai tangan yang sedang menarik (kanan/kiri).")]
    [SerializeField, Range(0f, 20f)] private float pullSideLeanAngle = 9f;
    [Tooltip("Sudut putar bahu/badan (derajat) saat menarik bergantian kanan-kiri.")]
    [SerializeField, Range(0f, 30f)] private float pullTwistAngle = 14f;
    [Tooltip("Kehalusan respons gerakan tarik tangan (lebih kecil = lebih sigap/bertenaga).")]
    [SerializeField, Range(0.02f, 0.25f)] private float pullTiltSmoothTime = 0.055f;

    [Header("Goyang Angin Saat Diam (opsional)")]
    [SerializeField] private float idleSwayAngle = 0f;
    [SerializeField] private float idleSwaySpeed = 1.2f;

    private struct LocalPose
    {
        public Vector3 pos;
        public Quaternion rot;
    }

    public Transform Body => body;
    public Transform Head => head;

    private LocalPose bodyPose, headPose;

    private Vector2 bodyTilt, bodyTiltVel;
    private Vector2 headTilt, headTiltVel;
    private float bodyTwist, bodyTwistVel;
    private float headTwist, headTwistVel;

    private Vector3 lastPos;
    private Transform lastParent;
    private bool hasLast;
    private Vector3 smoothedVelocity;

    // State irama tarik tali dari LassoController
    private bool pullRhythmActive;
    private Vector3 pullDirWorld;
    private float pullHandSide;
    private float pullTugPulse;
    private float pullReachPulse;

    private void Awake()
    {
        if (body != null) bodyPose = Capture(body);
        if (head != null) headPose = Capture(head);
    }

    /// <summary>
    /// Memperbarui referensi visual badan & kepala saat model karakter diganti secara dinamis
    /// (mis. oleh PlayerCharacterAppearance).
    /// </summary>
    public void RebindVisuals(Transform newBody, Transform newHead)
    {
        body = newBody;
        head = newHead;
        bodyTilt = Vector2.zero;
        bodyTiltVel = Vector2.zero;
        headTilt = Vector2.zero;
        headTiltVel = Vector2.zero;
        bodyTwist = 0f;
        bodyTwistVel = 0f;
        headTwist = 0f;
        headTwistVel = 0f;
        pullRhythmActive = false;

        if (body != null) bodyPose = Capture(body);
        if (head != null) headPose = Capture(head);
    }

    /// <summary>
    /// Menggerakkan badan seolah sedang menarik tali bergantian kanan-kiri (1___1___1).
    /// </summary>
    public void SetPullRhythm(bool active, Vector3 pullDirectionWorld, float handSide, float tugPulse, float reachPulse)
    {
        pullRhythmActive = active;
        if (!active) return;

        pullDirectionWorld.y = 0f;
        pullDirWorld = pullDirectionWorld.sqrMagnitude > 0.0001f
            ? pullDirectionWorld.normalized
            : transform.forward;
        pullHandSide = handSide;
        pullTugPulse = Mathf.Clamp01(tugPulse);
        pullReachPulse = Mathf.Clamp01(reachPulse);
    }

    private LocalPose Capture(Transform t)
    {
        return new LocalPose
        {
            pos = transform.InverseTransformPoint(t.position),
            rot = Quaternion.Inverse(transform.rotation) * t.rotation
        };
    }

    private void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        UpdateVelocity(dt);

        Vector2 flatVel = new Vector2(smoothedVelocity.x, smoothedVelocity.z);
        Vector2 target = Vector2.ClampMagnitude(flatVel / Mathf.Max(0.01f, speedForMaxTilt), 1f) * maxTiltAngle;
        if (invertTilt) target = -target;

        float targetTwist = 0f;
        float activeSmooth = bodySmoothTime;

        if (pullRhythmActive)
        {
            Vector3 rightWorld = Vector3.Cross(Vector3.up, pullDirWorld).normalized;

            // Saat fase '1' (tugPulse): condong ke belakang + miring ke sisi tangan yang menarik
            // Saat fase '___' (reachPulse): condong sedikit ke depan untuk ganti tangan
            Vector3 leanVec =
                (-pullDirWorld * (pullLeanBackAngle * pullTugPulse)) +
                (pullDirWorld * (pullReachForwardAngle * pullReachPulse)) +
                (rightWorld * (pullSideLeanAngle * pullHandSide * pullTugPulse));

            target += new Vector2(leanVec.x, leanVec.z);
            targetTwist = pullHandSide * pullTwistAngle * pullTugPulse;
            activeSmooth = pullTiltSmoothTime;
        }
        else if (idleSwayAngle > 0f)
        {
            float t = Time.time * idleSwaySpeed;
            target += new Vector2(Mathf.Sin(t), Mathf.Sin(t * 0.73f + 1.3f)) * idleSwayAngle;
        }

        bodyTilt = Vector2.SmoothDamp(bodyTilt, target, ref bodyTiltVel, Mathf.Max(0.001f, activeSmooth), Mathf.Infinity, dt);
        bodyTwist = Mathf.SmoothDamp(bodyTwist, targetTwist, ref bodyTwistVel, Mathf.Max(0.001f, activeSmooth), Mathf.Infinity, dt);

        float headSmooth = Mathf.Max(0.001f, headDelayMs * 0.001f);
        headTilt = Vector2.SmoothDamp(headTilt, bodyTilt * headTiltMultiplier, ref headTiltVel, headSmooth, Mathf.Infinity, dt);
        headTwist = Mathf.SmoothDamp(headTwist, bodyTwist * 0.8f, ref headTwistVel, headSmooth, Mathf.Infinity, dt);

        Vector3 pivot = GetPivotWorld();

        if (body != null) Apply(body, bodyPose, bodyTilt, bodyTwist, pivot);
        if (head != null) Apply(head, headPose, headTilt, headTwist, pivot);
    }

    private Vector3 GetPivotWorld()
    {
        return feetPivot != null ? feetPivot.position : transform.TransformPoint(feetLocalOffset);
    }

    private void Apply(Transform t, LocalPose basePose, Vector2 tilt, float twistDeg, Vector3 pivot)
    {
        Vector3 baseWorldPos = transform.TransformPoint(basePose.pos);
        Quaternion baseWorldRot = transform.rotation * basePose.rot;

        float angle = tilt.magnitude;
        if (angle < 0.0001f && Mathf.Abs(twistDeg) < 0.01f)
        {
            t.SetPositionAndRotation(baseWorldPos, baseWorldRot);
            return;
        }

        Quaternion twistQ = Mathf.Abs(twistDeg) >= 0.01f
            ? Quaternion.AngleAxis(twistDeg, Vector3.up)
            : Quaternion.identity;

        Quaternion tiltQ = Quaternion.identity;
        if (angle >= 0.0001f)
        {
            Vector3 leanDir = new Vector3(tilt.x, 0f, tilt.y).normalized;
            Vector3 axis = Vector3.Cross(Vector3.up, leanDir);
            tiltQ = Quaternion.AngleAxis(angle, axis);
        }

        Quaternion q = tiltQ * twistQ;
        t.SetPositionAndRotation(pivot + q * (baseWorldPos - pivot), q * baseWorldRot);
    }

    private void UpdateVelocity(float dt)
    {
        Transform par = transform.parent;
        Vector3 cur = par != null ? transform.localPosition : transform.position;

        Vector3 raw = Vector3.zero;
        if (hasLast && par == lastParent)
        {
            Vector3 delta = cur - lastPos;
            raw = (par != null ? par.TransformVector(delta) : delta) / dt;
            raw.y = 0f;
        }

        lastPos = cur;
        lastParent = par;
        hasLast = true;

        smoothedVelocity = Vector3.Lerp(smoothedVelocity, raw, 1f - Mathf.Exp(-25f * dt));
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Vector3 p = feetPivot != null ? feetPivot.position : transform.TransformPoint(feetLocalOffset);
        Gizmos.DrawWireSphere(p, 0.1f);
    }
}