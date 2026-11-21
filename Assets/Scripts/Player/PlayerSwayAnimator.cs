using UnityEngine;

/// <summary>
/// Animasi sederhana lewat kode: badan miring beberapa derajat seperti benda tegak ditiup angin
/// (bertumpu di kaki), kepala menyusul dengan delay beberapa milidetik.
/// Pasang di root Player. Isi kolom Body dan Head dengan child visual (bukan root yang punya collider).
/// Aman untuk multiplayer: kecepatan dihitung dari perubahan posisi, jadi pemain remote ikut beranimasi.
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

    [Header("Goyang Angin Saat Diam (opsional)")]
    [SerializeField] private float idleSwayAngle = 0f;
    [SerializeField] private float idleSwaySpeed = 1.2f;

    private struct LocalPose
    {
        public Vector3 pos;
        public Quaternion rot;
    }

    private LocalPose bodyPose, headPose;

    private Vector2 bodyTilt, bodyTiltVel;
    private Vector2 headTilt, headTiltVel;

    private Vector3 lastPos;
    private Transform lastParent;
    private bool hasLast;
    private Vector3 smoothedVelocity;

    private void Awake()
    {
        if (body != null) bodyPose = Capture(body);
        if (head != null) headPose = Capture(head);
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

        // Target kemiringan (sumbu X,Z dunia) dalam derajat
        Vector2 flatVel = new Vector2(smoothedVelocity.x, smoothedVelocity.z);
        Vector2 target = Vector2.ClampMagnitude(flatVel / Mathf.Max(0.01f, speedForMaxTilt), 1f) * maxTiltAngle;
        if (invertTilt) target = -target;

        if (idleSwayAngle > 0f)
        {
            float t = Time.time * idleSwaySpeed;
            target += new Vector2(Mathf.Sin(t), Mathf.Sin(t * 0.73f + 1.3f)) * idleSwayAngle;
        }

        bodyTilt = Vector2.SmoothDamp(bodyTilt, target, ref bodyTiltVel, Mathf.Max(0.001f, bodySmoothTime), Mathf.Infinity, dt);

        // Kepala mengejar badan dengan delay tambahan
        float headSmooth = Mathf.Max(0.001f, headDelayMs * 0.001f);
        headTilt = Vector2.SmoothDamp(headTilt, bodyTilt * headTiltMultiplier, ref headTiltVel, headSmooth, Mathf.Infinity, dt);

        Vector3 pivot = GetPivotWorld();

        // Body dulu, lalu head (aman walau head adalah child dari body, karena pose head ditulis ulang dalam world space)
        if (body != null) Apply(body, bodyPose, bodyTilt, pivot);
        if (head != null) Apply(head, headPose, headTilt, pivot);
    }

    private Vector3 GetPivotWorld()
    {
        return feetPivot != null ? feetPivot.position : transform.TransformPoint(feetLocalOffset);
    }

    private void Apply(Transform t, LocalPose basePose, Vector2 tilt, Vector3 pivot)
    {
        Vector3 baseWorldPos = transform.TransformPoint(basePose.pos);
        Quaternion baseWorldRot = transform.rotation * basePose.rot;

        float angle = tilt.magnitude;
        if (angle < 0.0001f)
        {
            t.SetPositionAndRotation(baseWorldPos, baseWorldRot);
            return;
        }

        // Miring ke arah 'tilt' dengan bertumpu di kaki
        Vector3 leanDir = new Vector3(tilt.x, 0f, tilt.y).normalized;
        Vector3 axis = Vector3.Cross(Vector3.up, leanDir);
        Quaternion q = Quaternion.AngleAxis(angle, axis);

        t.SetPositionAndRotation(pivot + q * (baseWorldPos - pivot), q * baseWorldRot);
    }

    /// <summary>
    /// Kecepatan relatif terhadap parent (kapal), jadi hanyutnya kapal tidak ikut membuat pemain miring.
    /// </summary>
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

        // Filter ringan agar jitter network tidak bikin badan bergetar
        smoothedVelocity = Vector3.Lerp(smoothedVelocity, raw, 1f - Mathf.Exp(-25f * dt));
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Vector3 p = feetPivot != null ? feetPivot.position : transform.TransformPoint(feetLocalOffset);
        Gizmos.DrawWireSphere(p, 0.1f);
    }
}