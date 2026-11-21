using System.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Sampah di sungai: hanyut mengikuti arus (spline), mengapung di permukaan air (tidak tenggelam),
/// bisa menabrak/mendorong kapal, dan tetap bisa "ditarik" (F) sampai hilang.
/// Server menjalankan fisika; client hanya menerima posisi dan menghaluskannya.
/// Tidak perlu NetworkTransform/Rigidbody di prefab: Rigidbody ditambahkan otomatis saat game mulai.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class TrashObstacle : NetworkBehaviour
{
    [Header("Pengaturan Ketahanan Sampah")]
    [SerializeField] private int minTugsRequired = 5; // Minimal tekanan F untuk menghilangkan sampah
    [SerializeField] private int maxTugsRequired = 7; // Maksimal tekanan F untuk menghilangkan sampah

    [Header("Hanyut Mengikuti Arus")]
    [SerializeField] private bool driftWithRiver = true;
    [Tooltip("Kuat arus yang membawa sampah (dikali Flow Speed di RiverPath).")]
    [SerializeField, Range(0f, 1.5f)] private float flowInfluence = 0.8f;
    [Tooltip("Hambatan air relatif terhadap arus. Makin besar = makin cepat menyamai kecepatan arus.")]
    [SerializeField] private float waterDrag = 2f;
    [Tooltip("Massa sampah. Makin besar = makin kuat mendorong kapal (kapal bermassa 10).")]
    [SerializeField] private float mass = 4f;

    [Header("Mengapung")]
    [Tooltip("Pakai tinggi sampah saat awal di scene (relatif ke permukaan air di titik itu) sebagai tinggi mengapung.")]
    [SerializeField] private bool useInitialHeight = true;
    [Tooltip("Dipakai jika Use Initial Height dimatikan: tinggi pusat sampah di atas permukaan air.")]
    [SerializeField] private float floatHeight = 0.1f;
    [SerializeField] private float buoyancySpring = 25f;
    [SerializeField] private float buoyancyDamping = 9f;
    [Tooltip("Naik-turun kecil hanya visual (tidak memengaruhi fisika).")]
    [SerializeField] private float bobHeight = 0.04f;
    [SerializeField] private float bobSpeed = 1.5f;

    [Header("Sinkronisasi & Batas")]
    [SerializeField] private float clientSmoothTime = 0.1f;
    [Tooltip("Sampah yang hanyut lebih jauh dari ini di luar sungai (mis. lewat ujung spline) dihapus.")]
    [SerializeField] private float despawnDistanceFromRiver = 40f;

    [Header("Pengaturan Visual Getaran (saat ditarik)")]
    [Tooltip("Opsional: isi dengan child mesh sampah. Kosong = dibuat otomatis dari mesh di objek utama.")]
    [SerializeField] private Transform visualModel;
    [SerializeField] private float shakeDuration = 0.22f;
    [SerializeField] private float shakeMagnitude = 0.18f;

    private int requiredTugs;
    private int currentTugs = 0;
    private bool isCleared = false;

    private Rigidbody rb;
    private RiverPath river;
    private float heightOffset;

    private readonly NetworkVariable<Vector3> netPos = new NetworkVariable<Vector3>();
    private readonly NetworkVariable<float> netYaw = new NetworkVariable<float>();
    private Vector3 clientVelocity;
    private float clientYawVelocity;

    private Transform shakeTarget;
    private Vector3 initialLocalPos;
    private Vector3 shakeOffset;
    private float bobPhase;
    private Coroutine shakeCoroutine;

    public bool IsCleared => isCleared;

    private void Awake()
    {
        EnsureVisualChild();
        if (shakeTarget != null) initialLocalPos = shakeTarget.localPosition;
        bobPhase = (GetInstanceID() & 0xFF) * 0.1f;

        if (!TryGetComponent(out rb)) rb = gameObject.AddComponent<Rigidbody>();
        rb.mass = mass;
        rb.useGravity = false;
        rb.linearDamping = 0f;
        rb.angularDamping = 1.5f;
        rb.constraints = RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.isKinematic = true; // server baru mengaktifkan fisika di OnNetworkSpawn

        // Licin: meluncur di sisi batu/kapal, tidak menempel
        var slide = new PhysicsMaterial("TrashSlide")
        {
            dynamicFriction = 0.05f,
            staticFriction = 0.05f,
            bounciness = 0.1f,
            frictionCombine = PhysicsMaterialCombine.Minimum,
            bounceCombine = PhysicsMaterialCombine.Average
        };
        foreach (var c in GetComponentsInChildren<Collider>())
            if (!c.isTrigger) c.sharedMaterial = slide;
    }

    // Getaran harus di child visual, BUKAN di root (root digerakkan fisika dan Rigidbody)
    private void EnsureVisualChild()
    {
        if (visualModel == null)
        {
            var mf = GetComponent<MeshFilter>();
            var mr = GetComponent<MeshRenderer>();
            if (mf != null && mr != null)
            {
                var go = new GameObject("Visual");
                go.layer = gameObject.layer;
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mf.sharedMesh;
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = mr.sharedMaterials;
                r.shadowCastingMode = mr.shadowCastingMode;
                r.receiveShadows = mr.receiveShadows;
                mr.enabled = false;
                visualModel = go.transform;
            }
        }

        shakeTarget = visualModel; // null = tidak ada yang bisa digetarkan dengan aman
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            int min = Mathf.Max(1, minTugsRequired);
            int max = Mathf.Max(min, maxTugsRequired);
            requiredTugs = Random.Range(min, max + 1);

            river = FindFirstObjectByType<RiverPath>();

            if (driftWithRiver)
            {
                rb.isKinematic = false;

                if (river != null)
                {
                    float waterY = river.SampleAt(rb.position).center.y;
                    heightOffset = useInitialHeight ? rb.position.y - waterY : floatHeight;
                }
                else
                {
                    // Tanpa sungai: kunci ketinggian supaya tidak jatuh
                    rb.constraints |= RigidbodyConstraints.FreezePositionY;
                }
            }

            netPos.Value = rb.position;
            netYaw.Value = rb.rotation.eulerAngles.y;
        }
        else
        {
            rb.isKinematic = true;
            transform.position = netPos.Value;
            transform.rotation = Quaternion.Euler(0f, netYaw.Value, 0f);
        }
    }

    private void FixedUpdate()
    {
        if (!IsServer || !IsSpawned || isCleared || !driftWithRiver || rb.isKinematic) return;

        Vector3 pos = rb.position;
        Vector3 flow = Vector3.zero;

        if (river != null)
        {
            RiverPath.Sample s = river.SampleAt(pos);
            flow = s.flowVelocity * flowInfluence;
            flow.y = 0f;
            if (!IsFinite(flow)) flow = Vector3.zero;   // jangan sampai NaN masuk ke AddForce

            // Pegas apung: tarik ke permukaan air, redam gerak vertikal (tidak tenggelam, tidak bergetar)
            float targetY = s.center.y + heightOffset;
            float vy = rb.linearVelocity.y;
            float spring = (targetY - pos.y) * buoyancySpring - vy * buoyancyDamping;
            if (float.IsFinite(spring)) rb.AddForce(Vector3.up * spring, ForceMode.Acceleration);

            if (s.lateral > s.halfWidth + despawnDistanceFromRiver)
            {
                ClearTrash();
                return;
            }
        }

        // Drag relatif air: kecepatan -> kecepatan arus
        Vector3 v = rb.linearVelocity;
        if (!IsFinite(v))
        {
            rb.linearVelocity = Vector3.zero;
            v = Vector3.zero;
        }
        v.y = 0f;
        rb.AddForce((flow - v) * waterDrag, ForceMode.Acceleration);

        netPos.Value = rb.position;
        netYaw.Value = rb.rotation.eulerAngles.y;
    }

    private static bool IsFinite(Vector3 v)
    {
        return float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    }

    private void Update()
    {
        if (isCleared) return;

        // Client: ikuti posisi dari server dengan halus
        if (!IsServer && IsSpawned)
        {
            transform.position = Vector3.SmoothDamp(transform.position, netPos.Value, ref clientVelocity, clientSmoothTime);
            float yaw = Mathf.SmoothDampAngle(transform.eulerAngles.y, netYaw.Value, ref clientYawVelocity, clientSmoothTime);
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        // Visual saja: naik-turun kecil + getaran saat ditarik
        if (shakeTarget != null)
        {
            Vector3 bob = Vector3.up * (Mathf.Sin(Time.time * bobSpeed + bobPhase) * bobHeight);
            shakeTarget.localPosition = initialLocalPos + shakeOffset + bob;
        }
    }

    /// <summary>
    /// Dipanggil oleh Server setiap kali pemain menekan F saat laso menancap di sampah.
    /// </summary>
    public void TugTrash()
    {
        if (!IsServer || !IsSpawned || isCleared) return;

        currentTugs++;

        if (currentTugs >= requiredTugs)
        {
            ClearTrash();
        }
        else
        {
            TriggerShakeClientRpc();
        }
    }

    [ClientRpc]
    private void TriggerShakeClientRpc()
    {
        if (isCleared || !gameObject.activeInHierarchy || shakeTarget == null) return;

        if (shakeCoroutine != null)
        {
            StopCoroutine(shakeCoroutine);
        }
        shakeCoroutine = StartCoroutine(ShakeRoutine());
    }

    private IEnumerator ShakeRoutine()
    {
        float elapsed = 0f;

        while (elapsed < shakeDuration)
        {
            elapsed += Time.deltaTime;
            float strength = Mathf.Lerp(shakeMagnitude, 0f, elapsed / shakeDuration);

            shakeOffset = new Vector3(Random.Range(-1f, 1f) * strength, 0f, Random.Range(-1f, 1f) * strength);
            yield return null;
        }

        shakeOffset = Vector3.zero;
        shakeCoroutine = null;
    }

    private void ClearTrash()
    {
        if (isCleared) return;
        isCleared = true;

        // Gunakan Despawn(false) agar aman untuk In-Scene NetworkObject tanpa memicu warning
        if (NetworkObject != null && NetworkObject.IsSpawned)
        {
            NetworkObject.Despawn(false);
        }
    }

    public override void OnNetworkDespawn()
    {
        isCleared = true;
        // Sembunyikan objek & matikan collider di Server maupun semua Client
        gameObject.SetActive(false);
    }
}