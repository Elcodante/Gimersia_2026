using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMovement : NetworkBehaviour
{
    [Header("Pengaturan Gerak")]
    [SerializeField] private float moveSpeed = 5f;
    [SerializeField] private float rotationSpeed = 10f;

    [Header("Tenggelam")]
    [Tooltip("Pemain dianggap tenggelam bila pusatnya lebih rendah dari permukaan air sebesar ini.")]
    [SerializeField] private float drownDepthBelowWater = 0.8f;
    [Tooltip("Lama 'tenggelam' sebelum respawn (detik).")]
    [SerializeField] private float sinkDuration = 1.0f;
    [Tooltip("Pengaman: respawn paksa jika berada di luar kapal selama ini (mis. tersangkut di dasar sungai).")]
    [SerializeField] private float maxTimeOutsideBoat = 4f;

    [Header("Respawn (gaya Among Us)")]
    [Tooltip("Nama awalan child di kapal yang dipakai sebagai titik respawn (SpawnPoint_1, SpawnPoint_2, ...).")]
    [SerializeField] private string spawnPointPrefix = "SpawnPoint";
    [Tooltip("Lama pemain tak terlihat di titik respawn sebelum muncul.")]
    [SerializeField] private float hiddenDuration = 0.6f;
    [Tooltip("Lama animasi muncul. Pemain baru bisa bergerak setelah selesai.")]
    [SerializeField] private float appearDuration = 0.45f;
    [Tooltip("Bagian yang di-skala saat muncul. Kosong = Body & Head dari PlayerSwayAnimator. Jika tetap kosong, memakai efek berkedip.")]
    [SerializeField] private Transform[] appearTargets;

    private Rigidbody rb;
    private bool isFallingOff = false;
    private bool isMovementLocked = false; // Dikontrol oleh LassoController
    private bool respawnLocked = false;    // Owner: terkunci selama tenggelam -> respawn -> animasi muncul
    private bool serverRespawning = false;
    private float fallTimer;
    private float sinkTimer;

    private RiverPath river;
    private bool riverSearched;

    // true = pemain tak terlihat (fase respawn). Ditulis server, dibaca semua client.
    private readonly NetworkVariable<bool> netHidden = new NetworkVariable<bool>(false);

    private Renderer[] hideRenderers = new Renderer[0];
    private Transform[] resolvedTargets = new Transform[0];
    private Vector3[] targetBaseScales = new Vector3[0];
    private Coroutine appearCoroutine;

    /// <summary>True selama pemain tenggelam / respawn / belum terlihat. Dipakai LassoController untuk memblokir input.</summary>
    public bool IsRespawning => respawnLocked || netHidden.Value;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.interpolation = RigidbodyInterpolation.None;
    }

    public override void OnNetworkSpawn()
    {
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.None;

        CacheVisuals();
        netHidden.OnValueChanged += OnHiddenChanged;
        SetRenderersVisible(!netHidden.Value);

        BoatController boat = FindFirstObjectByType<BoatController>();

        if (boat != null)
        {
            IgnoreCollisionWithBoat(boat);

            if (IsServer && transform.parent == null)
            {
                NetworkObject.TrySetParent(boat.NetworkObject, true);
            }
        }
    }

    public override void OnNetworkDespawn()
    {
        netHidden.OnValueChanged -= OnHiddenChanged;
    }

    public void SetMovementLocked(bool locked)
    {
        isMovementLocked = locked;
    }

    private void IgnoreCollisionWithBoat(BoatController boat)
    {
        Collider[] playerColliders = GetComponentsInChildren<Collider>();
        Collider[] boatColliders = boat.GetComponentsInChildren<Collider>();

        foreach (Collider pCol in playerColliders)
        {
            foreach (Collider bCol in boatColliders)
            {
                Physics.IgnoreCollision(pCol, bCol, true);
            }
        }
    }

    private void Update()
    {
        if (!IsSpawned || !IsOwner || !Application.isFocused) return;

        if (!isMovementLocked && !InGameMenuUI.ShouldLockMovement)
        {
            HandleMovement();
        }

        CheckIfStillOnBoat();
    }

    private void HandleMovement()
    {
        Vector2 inputVector = Vector2.zero;

        if (Keyboard.current != null)
        {
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) inputVector.y += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) inputVector.y -= 1f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) inputVector.x += 1f;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) inputVector.x -= 1f;
        }

        inputVector.Normalize();
        Vector3 worldMoveDir = new Vector3(inputVector.x, 0f, inputVector.y);

        if (worldMoveDir.magnitude >= 0.1f)
        {
            if (transform.parent != null)
            {
                Vector3 localMoveDir = transform.parent.InverseTransformDirection(worldMoveDir);
                transform.localPosition += localMoveDir * moveSpeed * Time.deltaTime;
            }
            else
            {
                transform.position += worldMoveDir * moveSpeed * Time.deltaTime;
            }

            Quaternion targetRotation = Quaternion.LookRotation(worldMoveDir, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationSpeed * Time.deltaTime);
        }
    }

    private void CheckIfStillOnBoat()
    {
        if (isFallingOff || transform.parent == null) return;

        if (!IsBoatBelow())
        {
            isFallingOff = true;
            fallTimer = 0f;
            sinkTimer = 0f;
            FallOffBoatServerRpc();
        }
    }

    private bool IsBoatBelow()
    {
        RaycastHit[] hits = Physics.RaycastAll(transform.position + Vector3.up * 0.5f, Vector3.down, 3f);
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.transform == transform || hit.collider.transform.IsChildOf(transform))
                continue;

            if (hit.collider.GetComponentInParent<BoatController>() != null)
                return true;
        }
        return false;
    }

    [ServerRpc]
    private void FallOffBoatServerRpc()
    {
        if (serverRespawning) return;
        NetworkObject.TryRemoveParent(true);
        EnableGravityClientRpc();
    }

    [ClientRpc]
    private void EnableGravityClientRpc()
    {
        rb.isKinematic = false;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
    }

    // ------------------------------------------------------------------ TENGGELAM

    private void CheckDrowning()
    {
        fallTimer += Time.deltaTime;

        bool submerged = IsSubmerged();
        if (submerged) sinkTimer += Time.deltaTime;

        if ((submerged && sinkTimer >= sinkDuration) || fallTimer >= maxTimeOutsideBoat)
        {
            StartDrown();
        }
    }

    private bool IsSubmerged()
    {
        if (river == null && !riverSearched)
        {
            riverSearched = true;
            river = FindFirstObjectByType<RiverPath>();
        }

        if (river != null)
        {
            float waterY = river.SampleAt(transform.position).center.y;
            return transform.position.y < waterY - drownDepthBelowWater;
        }

        // Tanpa RiverPath: anggap tenggelam bila jauh di bawah kapal
        BoatController boat = FindFirstObjectByType<BoatController>();
        return boat != null && transform.position.y < boat.transform.position.y - 2.5f;
    }

    private void StartDrown()
    {
        respawnLocked = true;
        fallTimer = 0f;
        sinkTimer = 0f;
        RequestRespawnServerRpc();
    }

    // ------------------------------------------------------------------ RESPAWN

    [ServerRpc]
    private void RequestRespawnServerRpc()
    {
        if (serverRespawning) return;
        StartCoroutine(ServerRespawnRoutine());
    }

    private IEnumerator ServerRespawnRoutine()
    {
        serverRespawning = true;

        // Lepas tali kalau sedang menancap
        var lasso = GetComponent<LassoController>();
        if (lasso != null) lasso.ServerForceRelease();

        BoatController boat = FindFirstObjectByType<BoatController>();
        if (boat == null)
        {
            serverRespawning = false;
            yield break;
        }

        // 1) Hilang (invisible) di semua client
        netHidden.Value = true;
        ResetPhysicsClientRpc();

        // 2) Pindah ke kapal & minta owner teleport ke titik respawn
        Vector3 localSpawn = PickSpawnLocalPosition(boat);
        if (transform.parent != boat.transform)
        {
            NetworkObject.TrySetParent(boat.NetworkObject, true);
        }

        var target = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
        };
        TeleportToBoatClientRpc(localSpawn, target);

        yield return new WaitForSeconds(hiddenDuration);

        // 3) Muncul (invisible -> visible). Owner membuka kunci gerak setelah animasi selesai.
        netHidden.Value = false;

        yield return new WaitForSeconds(appearDuration);
        serverRespawning = false;
    }

    private Vector3 PickSpawnLocalPosition(BoatController boat)
    {
        var points = new List<Transform>();
        foreach (Transform t in boat.GetComponentsInChildren<Transform>(true))
        {
            if (t != boat.transform && t.name.StartsWith(spawnPointPrefix)) points.Add(t);
        }

        if (points.Count == 0) return new Vector3(0f, 1.5f, 0f);

        // Pilih titik yang paling jauh dari pemain lain; kalau sendirian, acak
        PlayerMovement[] others = FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None);
        Transform best = null;
        float bestScore = float.MinValue;

        foreach (Transform p in points)
        {
            float minDist = float.MaxValue;
            foreach (PlayerMovement o in others)
            {
                if (o == this || o.netHidden.Value || o.transform.parent == null) continue;
                minDist = Mathf.Min(minDist, Vector3.Distance(o.transform.position, p.position));
            }

            float score = (minDist == float.MaxValue) ? Random.value : minDist;
            if (score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }

        return boat.transform.InverseTransformPoint(best.position);
    }

    [ClientRpc]
    private void ResetPhysicsClientRpc()
    {
        if (!rb.isKinematic)
        {
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        rb.isKinematic = true;
        rb.interpolation = RigidbodyInterpolation.None;
    }

    [ClientRpc]
    private void TeleportToBoatClientRpc(Vector3 localSpawn, ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        StartCoroutine(OwnerTeleportRoutine(localSpawn));
    }

    private IEnumerator OwnerTeleportRoutine(Vector3 localSpawn)
    {
        // Tunggu perubahan parent (ke kapal) sampai ke owner
        float waited = 0f;
        while (transform.parent == null && waited < 1.5f)
        {
            waited += Time.deltaTime;
            yield return null;
        }

        transform.localPosition = localSpawn;
        transform.localRotation = Quaternion.identity;
    }

    private IEnumerator OwnerUnlockRoutine()
    {
        yield return new WaitForSeconds(appearDuration);

        isFallingOff = false;
        fallTimer = 0f;
        sinkTimer = 0f;
        respawnLocked = false;
    }

    // ------------------------------------------------------------------ VISUAL (hilang / muncul)

    private void CacheVisuals()
    {
        var list = new List<Renderer>();
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
        {
            if (r is LineRenderer) continue;                              // garis bidik laso
            if (r.GetComponentInParent<VerletRopeVisual>() != null) continue; // tali 3D + kait
            list.Add(r);
        }
        hideRenderers = list.ToArray();

        Transform[] targets = appearTargets;
        if (targets == null || targets.Length == 0)
        {
            var sway = GetComponent<PlayerSwayAnimator>();
            var l2 = new List<Transform>();
            if (sway != null)
            {
                if (sway.Body != null) l2.Add(sway.Body);
                if (sway.Head != null) l2.Add(sway.Head);
            }
            targets = l2.ToArray();
        }

        resolvedTargets = targets;
        targetBaseScales = new Vector3[resolvedTargets.Length];
        for (int i = 0; i < resolvedTargets.Length; i++)
            targetBaseScales[i] = resolvedTargets[i].localScale;
    }

    private void SetRenderersVisible(bool visible)
    {
        foreach (Renderer r in hideRenderers)
        {
            if (r != null) r.enabled = visible;
        }
    }

    private void RestoreTargetScales()
    {
        for (int i = 0; i < resolvedTargets.Length; i++)
        {
            if (resolvedTargets[i] != null) resolvedTargets[i].localScale = targetBaseScales[i];
        }
    }

    private void OnHiddenChanged(bool previous, bool hidden)
    {
        if (appearCoroutine != null)
        {
            StopCoroutine(appearCoroutine);
            appearCoroutine = null;
            RestoreTargetScales();
        }

        if (hidden)
        {
            SetRenderersVisible(false);
        }
        else
        {
            appearCoroutine = StartCoroutine(AppearRoutine());
            if (IsOwner) StartCoroutine(OwnerUnlockRoutine());
        }
    }

    private IEnumerator AppearRoutine()
    {
        float duration = Mathf.Max(0.05f, appearDuration);

        if (resolvedTargets.Length == 0)
        {
            // Tidak ada bagian terpisah untuk di-skala: efek berkedip lalu tetap terlihat
            const int blinks = 6;
            for (int i = 0; i < blinks; i++)
            {
                SetRenderersVisible(i % 2 == 0);
                yield return new WaitForSeconds(duration / blinks);
            }
            SetRenderersVisible(true);
        }
        else
        {
            for (int i = 0; i < resolvedTargets.Length; i++)
                if (resolvedTargets[i] != null) resolvedTargets[i].localScale = Vector3.zero;

            SetRenderersVisible(true);

            float t = 0f;
            while (t < duration)
            {
                t += Time.deltaTime;
                float k = EaseOutBack(Mathf.Clamp01(t / duration));
                for (int i = 0; i < resolvedTargets.Length; i++)
                {
                    if (resolvedTargets[i] != null) resolvedTargets[i].localScale = targetBaseScales[i] * k;
                }
                yield return null;
            }

            RestoreTargetScales();
        }

        appearCoroutine = null;
    }

    private static float EaseOutBack(float x)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float p = x - 1f;
        return 1f + c3 * p * p * p + c1 * p * p;
    }
}