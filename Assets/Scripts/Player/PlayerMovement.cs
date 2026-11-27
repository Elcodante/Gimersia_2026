using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMovement : NetworkBehaviour, IKnockbackable
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
    private bool remoteFallingOff = false;
    private bool isMovementLocked = false; // Dikontrol oleh LassoController
    private bool respawnLocked = false;    // Owner: terkunci selama tenggelam -> respawn -> animasi muncul
    private bool serverRespawning = false;
    private float fallTimer;
    private float sinkTimer;

    private RiverPath river;
    private bool riverSearched;

    // true = pemain tak terlihat (fase respawn). Ditulis server, dibaca semua client.
    private readonly NetworkVariable<bool> netHidden = new NetworkVariable<bool>(false);
    // true = pemain sedang jatuh di luar kapal / menunggu respawn. Dipakai CameraTracker agar kamera tidak terpental.
    private readonly NetworkVariable<bool> netOffBoat = new NetworkVariable<bool>(false);

    private Renderer[] hideRenderers = new Renderer[0];
    private Transform[] resolvedTargets = new Transform[0];
    private Vector3[] targetBaseScales = new Vector3[0];
    private Coroutine appearCoroutine;

    /// <summary>True selama pemain tenggelam / respawn / belum terlihat. Dipakai LassoController untuk memblokir input.</summary>
    public bool IsRespawning => respawnLocked || netHidden.Value;

    /// <summary>True jika pemain sedang jatuh ke air atau sedang proses respawn (disinkronkan ke semua client untuk CameraTracker).</summary>
    public bool IsOffBoatOrRespawning => isFallingOff || remoteFallingOff || respawnLocked || netOffBoat.Value || netHidden.Value;

    /// <summary>True jika pemain berada di atas kapal dan dapat dihempaskan oleh serangan ikan.</summary>
    public bool CanBeKnockedBack => IsSpawned && !serverRespawning && !netHidden.Value && !netOffBoat.Value && transform.parent != null;

    public Vector3 KnockbackPosition => transform.position;

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

        IgnoreCollisionWithRiver();

        BoatController boat = FindFirstObjectByType<BoatController>();

        if (boat != null)
        {
            IgnoreCollisionWithBoat(boat);

            if (IsServer && transform.parent == null)
            {
                NetworkObject.TrySetParent(boat.NetworkObject, true);
            }
        }

        if (IsOwner)
        {
            StartCoroutine(ReportReadyWhenParentedRoutine());
        }
    }

    private IEnumerator ReportReadyWhenParentedRoutine()
    {
        // World generation is a prerequisite for player readiness. This prevents the host
        // from starting the match while a client is still building its deterministic layout.
        const float timeoutSeconds = 180f;
        const float retryIntervalSeconds = 0.5f;
        float waited = 0f;
        float retryTimer = 0f;
        bool hasReported = false;

        while (waited < timeoutSeconds && IsSpawned && IsOwner)
        {
            GameSpawner spawner = GameSpawner.Instance;
            if (spawner != null && spawner.IsSpawned && (spawner.IsMatchStarted.Value || spawner.IsIntroPlaying.Value))
            {
                yield break;
            }

            BoatController boat = FindFirstObjectByType<BoatController>();
            bool boatParentReady = boat == null || transform.parent != null;
            bool spawnerReady = spawner != null && spawner.IsSpawned;
            bool worldReady = spawnerReady && spawner.IsLocalWorldReady;

            if (boatParentReady && worldReady)
            {
                if (!hasReported || retryTimer >= retryIntervalSeconds)
                {
                    spawner.NotifyClientPlayerReadyRpc();
                    hasReported = true;
                    retryTimer = 0f;
                }
                else
                {
                    retryTimer += Time.unscaledDeltaTime;
                }
            }

            waited += Time.unscaledDeltaTime;
            yield return null;
        }

        if (!hasReported && IsSpawned && IsOwner)
        {
            Debug.LogError("[PlayerMovement] Gagal melaporkan kesiapan: kapal, GameSpawner, atau world generation belum siap setelah 180 detik.", this);
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

    private void IgnoreCollisionWithRiver()
    {
        if (river == null && !riverSearched)
        {
            riverSearched = true;
            river = RiverPath.Main;
        }
        if (river == null) return;

        Collider[] playerColliders = GetComponentsInChildren<Collider>();
        Collider[] riverColliders = river.GetComponentsInChildren<Collider>();

        foreach (Collider pCol in playerColliders)
        {
            foreach (Collider rCol in riverColliders)
            {
                Physics.IgnoreCollision(pCol, rCol, true);
            }
        }
    }

    private void Update()
    {
        if (!IsSpawned || !IsOwner) return;

        // Tahan seluruh pergerakan pemain sampai seluruh anggota tim selesai spawn
        if (!GameSpawner.IsMatchReady) return;

        // Cegah input jalan WASD saat membidik/menarik laso, respawn, menu terbuka, atau sedang jatuh dari kapal
        if (InputFocusGuard.CanAcceptInput && !isMovementLocked && !respawnLocked && !isFallingOff && !InGameMenuUI.ShouldLockMovement)
        {
            HandleMovement();
        }

        if (respawnLocked) return;

        CheckIfStillOnBoat();

        if (isFallingOff)
        {
            CheckDrowning();
        }
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

        // Gerak selalu relatif terhadap arah pandang kamera layar (W = selalu ke depan/atas layar),
        // tidak terpengaruh oleh rotasi kapal yang berputar-putar.
        Vector3 camForward = Vector3.forward;
        Vector3 camRight = Vector3.right;
        Camera mainCam = Camera.main;
        if (mainCam != null)
        {
            camForward = mainCam.transform.forward;
            camForward.y = 0f;
            if (camForward.sqrMagnitude > 0.0001f) camForward.Normalize();
            else camForward = Vector3.forward;

            camRight = mainCam.transform.right;
            camRight.y = 0f;
            if (camRight.sqrMagnitude > 0.0001f) camRight.Normalize();
            else camRight = Vector3.right;
        }

        Vector3 worldMoveDir = camForward * inputVector.y + camRight * inputVector.x;

        if (worldMoveDir.sqrMagnitude >= 0.01f)
        {
            worldMoveDir.Normalize();

            if (transform.parent != null)
            {
                Vector3 localMoveDir = transform.parent.InverseTransformDirection(worldMoveDir);
                transform.localPosition += localMoveDir * moveSpeed * Time.deltaTime;

                Quaternion targetWorldRot = Quaternion.LookRotation(worldMoveDir, Vector3.up);
                Quaternion targetLocalRot = Quaternion.Inverse(transform.parent.rotation) * targetWorldRot;
                transform.localRotation = Quaternion.Slerp(transform.localRotation, targetLocalRot, rotationSpeed * Time.deltaTime);
            }
            else
            {
                transform.position += worldMoveDir * moveSpeed * Time.deltaTime;

                Quaternion targetRotation = Quaternion.LookRotation(worldMoveDir, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, rotationSpeed * Time.deltaTime);
            }
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
        if (serverRespawning || netOffBoat.Value) return;
        netOffBoat.Value = true;
        NetworkObject.TryRemoveParent(true);
        EnableGravityClientRpc();
    }

    /// <summary>
    /// Dipanggil di Server oleh sistem ikan (IKnockbackable) untuk menghempaskan pemain dari kapal ke air.
    /// Melepas laso jika sedang menancap agar kapal tidak tersangkut tali pemain yang terlempar.
    /// </summary>
    public void ApplyFishKnockback(Vector3 knockbackVelocity)
    {
        if (!IsServer || !CanBeKnockedBack) return;

        LassoController lasso = GetComponent<LassoController>();
        if (lasso != null) lasso.ServerForceRelease();

        netOffBoat.Value = true;
        NetworkObject.TryRemoveParent(true);
        ApplyFishKnockbackClientRpc(knockbackVelocity);
    }

    [ClientRpc]
    private void ApplyFishKnockbackClientRpc(Vector3 knockbackVelocity)
    {
        remoteFallingOff = true;
        AudioManager.Instance?.PlaySFX("player_splash", 0.08f);

        rb.isKinematic = !IsOwner;
        rb.interpolation = IsOwner ? RigidbodyInterpolation.Interpolate : RigidbodyInterpolation.None;

        if (!IsOwner) return;

        isFallingOff = true;
        fallTimer = 0f;
        sinkTimer = 0f;

        rb.useGravity = true;
        rb.constraints &= ~RigidbodyConstraints.FreezePositionY;
        IgnoreCollisionWithRiver();

        Vector3 inherited = Vector3.zero;
        BoatController boat = FindFirstObjectByType<BoatController>();
        if (boat != null)
        {
            IgnoreCollisionWithBoat(boat);
            Rigidbody boatRb = boat.GetComponent<Rigidbody>();
            if (boatRb != null)
            {
                inherited = boatRb.linearVelocity;
                inherited.y = 0f;
            }
        }

        rb.linearVelocity = inherited + knockbackVelocity;
    }

    [ClientRpc]
    private void EnableGravityClientRpc()
    {
        remoteFallingOff = true;
        AudioManager.Instance?.PlaySFX("player_splash", 0.08f);

        // Hanya pemilik yang mensimulasikan jatuh. Salinan di peer lain mengikuti NetworkTransform (kinematic),
        // kalau tidak, fisika lokal dan posisi dari jaringan saling tarik sehingga jatuhnya terasa tersangkut.
        rb.isKinematic = !IsOwner;
        rb.interpolation = IsOwner ? RigidbodyInterpolation.Interpolate : RigidbodyInterpolation.None;

        if (!IsOwner) return;

        // Pastikan gravitasi aktif dan sumbu Y tidak terkunci
        rb.useGravity = true;
        rb.constraints &= ~RigidbodyConstraints.FreezePositionY;
        IgnoreCollisionWithRiver();

        BoatController boat = FindFirstObjectByType<BoatController>();
        if (boat == null) return;

        // Pastikan pemain tidak bertabrakan dengan badan kapal saat jatuh (flag ignore dipasang ulang)
        IgnoreCollisionWithBoat(boat);

        // Kapal sekarang hanyut. Bawa kecepatan kapal + dorong sedikit menjauh, supaya buritan kapal
        // yang bergerak tidak menyapu pemain yang jatuh tepat di belakangnya.
        Rigidbody boatRb = boat.GetComponent<Rigidbody>();
        Vector3 inherited = boatRb != null ? boatRb.linearVelocity : Vector3.zero;
        inherited.y = 0f;

        Vector3 away = transform.position - boat.transform.position;
        away.y = 0f;
        away = away.sqrMagnitude > 0.01f ? away.normalized : Vector3.zero;

        rb.linearVelocity = inherited + away * 1.5f;
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
            river = RiverPath.Main;
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
        netOffBoat.Value = false;
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

    /// <summary>
    /// Dipanggil ketika model visual karakter diganti (mis. oleh PlayerCharacterAppearance)
    /// agar efek sembunyi/muncul respawn menyertakan mesh karakter baru.
    /// </summary>
    public void RefreshVisualCache()
    {
        CacheVisuals();
        SetRenderersVisible(!netHidden.Value);
    }

    private void CacheVisuals()
    {
        var list = new List<Renderer>();
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
        {
            if (r is LineRenderer) continue;                                  // garis bidik laso
            if (r.GetComponentInParent<VerletRopeVisual>() != null) continue; // tali 3D + kait
            if (r.GetComponent<TMPro.TMP_Text>() != null) continue;           // nametag diatur PlayerNameTag sendiri
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
            remoteFallingOff = false;
            AudioManager.Instance?.PlaySFX("player_respawn", 0.06f);
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