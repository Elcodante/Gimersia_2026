using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(LineRenderer))]
public class LassoController : NetworkBehaviour
{
    private enum LassoState
    {
        Idle,
        Aiming,
        Throwing,
        Pulling,
        Retracting
    }

    [Header("Pengaturan Jangkauan & Bidikan")]
    [SerializeField] private float lassoRange = 25f;
    [SerializeField] private float aimFollowSpeed = 8f;
    [SerializeField] private float catchRadius = 1.5f;

    [Header("Pengaturan Tarikan Halus (Hold F)")]
    [SerializeField] private float trashTugInterval = 0.55f;     // Jeda tiap 1x getaran & hitungan cabut sampah

    [Header("Pengaturan Kendur & Putus Tali")]
    [SerializeField] private Key slackKey = Key.G;
    [SerializeField] private float slackExtendSpeed = 4.0f;
    [Tooltip("Jarak putus: tali lepas otomatis kalau kapal terseret lebih jauh dari ini.")]
    [SerializeField] private float maxRopeLength = 28.0f;
    [Tooltip("Kecepatan menggulung tali kendor saat F ditahan (m/detik). Makin kecil = tali mengencang makin pelan/halus.")]
    [SerializeField] private float ropeReelSpeed = 5.0f;   // kecepatan gulung MAKSIMUM (saat kendor banyak)
    [Tooltip("Gulungan melambat saat kendor tinggal sedikit agar tali mengencang halus, tidak menyentak. Kecepatan = kendor (m) x nilai ini, dibatasi Min dan Max.")]
    [SerializeField] private float ropeReelEase = 1.5f;
    [SerializeField] private float ropeReelMinSpeed = 0.8f;
    [SerializeField] private float slackSagMultiplier = 0.45f;   // hanya dipakai jika Use Verlet Rope dimatikan

    [Header("Visual Tali 3D (ragdoll-like / Verlet)")]
    [SerializeField] private bool useVerletRope = true;
    [Tooltip("Kosong = dibuat otomatis sebagai child saat game mulai.")]
    [SerializeField] private VerletRopeVisual ropeVisual;
    [Tooltip("Material tali (tekstur tali). Kosong = material coklat sederhana.")]
    [SerializeField] private Material ropeMaterial;
    [Tooltip("Opsional: model 3D kait / ujung tali.")]
    [SerializeField] private GameObject hookPrefab;
    [Tooltip("Opsional: titik tangan tempat tali keluar. Kosong = posisi pemain + 0.5 ke atas.")]
    [SerializeField] private Transform handAnchor;

    [Header("Pengaturan Visual Parabola & Durasi")]
    [SerializeField] private int arcSegments = 20;
    [SerializeField] private float maxArcHeight = 3.5f;
    [SerializeField] private float throwDuration = 0.2f;
    [SerializeField] private float retractDuration = 0.45f;

    private LineRenderer lineRenderer;
    private BoatController boatController;
    private PlayerMovement playerMovement;

    private bool isLocalAiming = false;
    private Vector3 delayedAimPoint;
    private bool lastSentPullHeld = false;
    private bool lastSentSlackHeld = false;

    private Transform latchedRockTransform;
    private Collider latchedRockCollider;
    private TrashObstacle latchedTrash;
    private Collider latchedTrashCollider;

    private bool serverPullHeld = false;
    private bool serverSlackHeld = false;
    private float trashTugTimer = 0f;

    private NetworkVariable<LassoState> netState = new NetworkVariable<LassoState>(LassoState.Idle);
    private NetworkVariable<Vector3> netTargetPoint = new NetworkVariable<Vector3>();
    private NetworkVariable<float> netProgress = new NetworkVariable<float>(0f);
    private NetworkVariable<float> netRopeLength = new NetworkVariable<float>(0f);
    private NetworkVariable<float> netRopeTension = new NetworkVariable<float>(0f);

    private Coroutine serverSequenceCoroutine;

    // Panjang maksimal tali boleh dikendorkan (G) = jarak lempar maksimal.
    private float MaxSlackLength => Mathf.Min(lassoRange, maxRopeLength);

    private void Awake()
    {
        lineRenderer = GetComponent<LineRenderer>();
        playerMovement = GetComponent<PlayerMovement>();
        lineRenderer.enabled = false;

        if (useVerletRope)
        {
            if (ropeVisual == null) ropeVisual = GetComponentInChildren<VerletRopeVisual>(true);
            if (ropeVisual == null)
            {
                var go = new GameObject("RopeVisual");
                go.transform.SetParent(transform, false);
                ropeVisual = go.AddComponent<VerletRopeVisual>();
            }
            ropeVisual.Configure(ropeMaterial, hookPrefab);
        }
    }

    public override void OnNetworkSpawn()
    {
        boatController = GetComponentInParent<BoatController>();
        if (boatController == null) boatController = FindFirstObjectByType<BoatController>();
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && boatController != null)
        {
            boatController.ClearPlayerInputs(OwnerClientId);
        }
    }

    private void Update()
    {
        if (IsOwner && Application.isFocused)
        {
            HandleOwnerInput();
            UpdateMovementLock();
        }
    }

    // LateUpdate: supaya pangkal tali ikut posisi tangan setelah pemain/kapal selesai bergerak
    private void LateUpdate()
    {
        UpdateRopeVisuals();
    }

    private void HandleOwnerInput()
    {
        // Selama tenggelam / respawn, tidak ada input laso
        if (playerMovement != null && playerMovement.IsRespawning)
        {
            isLocalAiming = false;
            return;
        }

        LassoState currentState = netState.Value;

        if (currentState == LassoState.Idle && !isLocalAiming)
        {
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            {
                isLocalAiming = true;
                delayedAimPoint = GetCursorWorldPoint();
            }
        }

        if (isLocalAiming)
        {
            if (Mouse.current != null && Mouse.current.leftButton.isPressed)
            {
                Vector3 rawTarget = GetCursorWorldPoint();
                delayedAimPoint = Vector3.Lerp(delayedAimPoint, rawTarget, aimFollowSpeed * Time.deltaTime);

                Vector3 lookDir = delayedAimPoint - transform.position;
                lookDir.y = 0f;
                if (lookDir.sqrMagnitude > 0.001f)
                {
                    transform.rotation = Quaternion.Slerp(
                        transform.rotation,
                        Quaternion.LookRotation(lookDir),
                        15f * Time.deltaTime
                    );
                }
            }
            else if (Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame)
            {
                isLocalAiming = false;
                ThrowLassoServerRpc(delayedAimPoint);
            }
        }
        else if (currentState == LassoState.Pulling)
        {
            bool pullHeld = Keyboard.current != null && Keyboard.current.fKey.isPressed;
            bool slackHeld = Keyboard.current != null && Keyboard.current[slackKey].isPressed;

            if (pullHeld != lastSentPullHeld || slackHeld != lastSentSlackHeld)
            {
                lastSentPullHeld = pullHeld;
                lastSentSlackHeld = slackHeld;
                UpdatePullInputServerRpc(pullHeld, slackHeld);
            }

            if (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame)
            {
                ReleaseLassoServerRpc();
            }
        }
        else
        {
            lastSentPullHeld = false;
            lastSentSlackHeld = false;
        }
    }

    private void UpdateMovementLock()
    {
        if (playerMovement == null) return;

        bool shouldLock = isLocalAiming || netState.Value != LassoState.Idle;
        playerMovement.SetMovementLocked(shouldLock);
    }

    private Vector3 GetCursorWorldPoint()
    {
        if (Camera.main == null) return transform.position + transform.forward * 5f;

        Vector2 mousePos = Mouse.current.position.ReadValue();
        Ray ray = Camera.main.ScreenPointToRay(mousePos);
        Plane waterPlane = new Plane(Vector3.up, new Vector3(0f, transform.position.y, 0f));

        if (waterPlane.Raycast(ray, out float enter))
        {
            Vector3 hitPoint = ray.GetPoint(enter);
            Vector3 offset = hitPoint - transform.position;
            offset.y = 0f;

            if (offset.magnitude > lassoRange)
            {
                offset = offset.normalized * lassoRange;
            }

            return transform.position + offset;
        }

        return transform.position + transform.forward * Mathf.Min(5f, lassoRange);
    }

    private Vector3 GetRopeOrigin()
    {
        return (handAnchor != null) ? handAnchor.position : transform.position + Vector3.up * 0.5f;
    }

    private static Vector3 ArcPoint(Vector3 start, Vector3 end, float t, float arcHeight)
    {
        t = Mathf.Clamp01(t);
        Vector3 p = Vector3.Lerp(start, end, t);
        p.y += 4f * arcHeight * t * (1f - t);
        return p;
    }

    private void UpdateRopeVisuals()
    {
        Vector3 ropeOrigin = GetRopeOrigin();
        bool verlet = useVerletRope && ropeVisual != null;

        if (IsOwner && isLocalAiming)
        {
            if (verlet) ropeVisual.SetVisible(false);
            lineRenderer.enabled = true;
            DrawParabola(ropeOrigin, delayedAimPoint, 1f, maxArcHeight);
            return;
        }

        if (verlet)
        {
            UpdateVerletRope(ropeOrigin);
            return;
        }

        switch (netState.Value)
        {
            case LassoState.Idle:
                lineRenderer.enabled = false;
                break;

            case LassoState.Throwing:
                lineRenderer.enabled = true;
                DrawParabola(ropeOrigin, netTargetPoint.Value, netProgress.Value, maxArcHeight);
                break;

            case LassoState.Pulling:
                lineRenderer.enabled = true;
                float actualDist = FlatDistance(transform.position, netTargetPoint.Value);

                float extraSlack = Mathf.Max(0f, netRopeLength.Value - actualDist);
                float baseSag = Mathf.Clamp(extraSlack * slackSagMultiplier, 0f, 3.0f);

                float finalSag = Mathf.Lerp(-baseSag - 0.2f, 0f, netRopeTension.Value);
                DrawParabola(ropeOrigin, netTargetPoint.Value, 1f, finalSag);
                break;

            case LassoState.Retracting:
                lineRenderer.enabled = true;
                DrawParabola(ropeOrigin, netTargetPoint.Value, netProgress.Value, maxArcHeight * 0.35f);
                break;
        }
    }

    private void UpdateVerletRope(Vector3 origin)
    {
        lineRenderer.enabled = false;

        switch (netState.Value)
        {
            case LassoState.Idle:
                ropeVisual.SetVisible(false);
                break;

            case LassoState.Throwing:
            {
                // Ujung tali terbang mengikuti parabola, sisa tali menjuntai di belakangnya
                Vector3 tip = ArcPoint(origin, netTargetPoint.Value, netProgress.Value, maxArcHeight);
                ropeVisual.Drive(origin, tip, Vector3.Distance(origin, tip) * 1.1f + 0.15f);
                break;
            }

            case LassoState.Pulling:
            {
                // Samakan referensi kendor dengan pusat player (seperti di server) agar tali tegang saat ditarik
                Vector3 anchor = netTargetPoint.Value;
                float chord = Vector3.Distance(origin, anchor);
                float flatDist = FlatDistance(transform.position, anchor);
                float slack = Mathf.Max(0f, netRopeLength.Value - flatDist);
                float ropeLen3D = chord + slack;

                // Murni dari geometri (kendor vs jarak) dan dihaluskan, supaya tali mengencang bertahap, tidak "dihentak".
                float slackRatio = slack / Mathf.Max(0.01f, ropeLen3D);
                float k = Mathf.Clamp01(slackRatio / 0.12f);
                float taut = 1f - k * k * (3f - 2f * k);

                ropeVisual.Drive(origin, anchor, ropeLen3D, taut);
                break;
            }

            case LassoState.Retracting:
            {
                Vector3 tip = ArcPoint(origin, netTargetPoint.Value, netProgress.Value, maxArcHeight * 0.35f);
                ropeVisual.Drive(origin, tip, Vector3.Distance(origin, tip) * 1.1f + 0.15f);
                break;
            }
        }
    }

    private void DrawParabola(Vector3 start, Vector3 end, float fillRatio, float arcHeight)
    {
        fillRatio = Mathf.Clamp01(fillRatio);
        int activePoints = Mathf.Max(2, Mathf.CeilToInt(arcSegments * fillRatio));
        lineRenderer.positionCount = activePoints;

        for (int i = 0; i < activePoints; i++)
        {
            float t = (float)i / (arcSegments - 1);
            if (t > fillRatio) t = fillRatio;

            Vector3 point = Vector3.Lerp(start, end, t);
            float parabolaOffset = 4f * arcHeight * t * (1f - t);
            point.y += parabolaOffset;

            lineRenderer.SetPosition(i, point);
        }
    }

    [ServerRpc]
    private void ThrowLassoServerRpc(Vector3 landingPoint)
    {
        if (netState.Value != LassoState.Idle) return;

        if (serverSequenceCoroutine != null) StopCoroutine(serverSequenceCoroutine);
        serverSequenceCoroutine = StartCoroutine(ServerLassoRoutine(landingPoint));
    }

    [ServerRpc]
    private void UpdatePullInputServerRpc(bool pullHeld, bool slackHeld)
    {
        serverPullHeld = pullHeld;
        serverSlackHeld = slackHeld;
    }

    private IEnumerator ServerLassoRoutine(Vector3 landingPoint)
    {
        netTargetPoint.Value = landingPoint;
        netProgress.Value = 0f;
        netState.Value = LassoState.Throwing;

        float elapsed = 0f;
        while (elapsed < throwDuration)
        {
            elapsed += Time.deltaTime;
            netProgress.Value = Mathf.Clamp01(elapsed / throwDuration);
            yield return null;
        }
        netProgress.Value = 1f;

        Collider hitTarget = FindTargetAtPoint(landingPoint);

        if (hitTarget != null)
        {
            latchedTrash = hitTarget.GetComponentInParent<TrashObstacle>();
            latchedTrashCollider = (latchedTrash != null) ? hitTarget : null;

            latchedRockTransform = (latchedTrash == null) ? hitTarget.transform : null;
            latchedRockCollider = (latchedTrash == null) ? hitTarget : null;

            Vector3 targetPos = hitTarget.transform.position;
            netTargetPoint.Value = targetPos;

            float initialDist = FlatDistance(transform.position, targetPos);
            float prevActualDist = initialDist;
            netRopeLength.Value = Mathf.Clamp(initialDist, 0.5f, maxRopeLength - 0.5f);
            netRopeTension.Value = 0.2f;

            serverPullHeld = false;
            serverSlackHeld = false;
            trashTugTimer = 0f;
            netState.Value = LassoState.Pulling;

            while (netState.Value == LassoState.Pulling)
            {
                if (boatController == null) boatController = FindFirstObjectByType<BoatController>();

                if (latchedTrash != null && (!latchedTrash.IsSpawned || latchedTrash.IsCleared))
                {
                    StopPullAndReset(false);
                    yield break;
                }
                if (latchedTrash == null && latchedRockTransform == null)
                {
                    StopPullAndReset(false);
                    yield break;
                }

                Vector3 activeTargetPos = (latchedTrash != null)
                    ? latchedTrash.transform.position
                    : latchedRockTransform.position;

                Collider activeTargetCol = (latchedTrash != null)
                    ? latchedTrashCollider
                    : latchedRockCollider;

                // Sampah ikut hanyut: kirim posisi terbaru agar ujung tali di semua client ikut bergerak
                if (latchedTrash != null && (netTargetPoint.Value - activeTargetPos).sqrMagnitude > 0.0004f)
                {
                    netTargetPoint.Value = activeTargetPos;
                }

                float actualDist = FlatDistance(transform.position, activeTargetPos);

                // Kapal terseret terlalu jauh dari batu (mis. terbawa arus): tali putus/lepas di kondisi apa pun
                if (actualDist >= maxRopeLength)
                {
                    StopPullAndReset(true);
                    yield break;
                }

                if (serverPullHeld)
                {
                    // Jika target adalah BATU dan kapal sudah menyentuh batu target:
                    // hentikan gaya tarik F, tetapi tali tetap menancap di batu.
                    if (latchedRockTransform != null && boatController != null &&
                        boatController.IsTouchingOrAtSurface(latchedRockCollider, activeTargetPos))
                    {
                        boatController.SetPlayerPull(OwnerClientId, false, activeTargetPos, transform.position, activeTargetCol);
                        boatController.SetPlayerTether(OwnerClientId, false, activeTargetPos, transform.position, netRopeLength.Value);

                        // Kapal menempel di batu (mis. hanyut lalu nyangkut): gaya tarik berhenti, TAPI tali kendor
                        // tetap digulung pelan sampai tegang.
                        if (netRopeLength.Value > actualDist)
                        {
                            netRopeLength.Value = Mathf.Max(actualDist, netRopeLength.Value - ReelRate(netRopeLength.Value - actualDist) * Time.deltaTime);
                        }
                        else
                        {
                            netRopeLength.Value = Mathf.Max(netRopeLength.Value, actualDist);
                        }

                        float stuckTension = (netRopeLength.Value - actualDist > 0.1f) ? 0.3f : 0.85f;
                        netRopeTension.Value = Mathf.MoveTowards(netRopeTension.Value, stuckTension, 3f * Time.deltaTime);
                        prevActualDist = actualDist;
                        yield return null;
                        continue;
                    }

                    // Tarikan halus & kontinu ke BoatController
                    if (boatController != null)
                    {
                        boatController.SetPlayerTether(OwnerClientId, false, activeTargetPos, transform.position, netRopeLength.Value);
                        boatController.SetPlayerPull(OwnerClientId, true, activeTargetPos, transform.position, activeTargetCol);
                    }

                    // Gulung tali kendor + ikut kurangi jarak yang sudah ditempuh kapal agar tali tidak malah mengendur saat F ditahan
                    float boatClosed = Mathf.Max(0f, prevActualDist - actualDist);
                    if (netRopeLength.Value > actualDist)
                    {
                        float reelDelta = boatClosed + ReelRate(netRopeLength.Value - actualDist) * Time.deltaTime;
                        netRopeLength.Value = Mathf.Max(0.5f, Mathf.Max(actualDist, netRopeLength.Value - reelDelta));
                    }

                    // Ketegangan visual tali mengikuti irama tarikan lembut
                    float targetTension = 0.85f + 0.15f * Mathf.Sin(Time.time * 5f);
                    netRopeTension.Value = Mathf.MoveTowards(netRopeTension.Value, targetTension, 4f * Time.deltaTime);

                    // Jika target adalah SAMPAH, tetap hitung getaran & cabutan tiap interval
                    if (latchedTrash != null)
                    {
                        trashTugTimer -= Time.deltaTime;
                        if (trashTugTimer <= 0f)
                        {
                            trashTugTimer = trashTugInterval;
                            latchedTrash.TugTrash();

                            if (latchedTrash.IsCleared)
                            {
                                StopPullAndReset(false);
                                yield break;
                            }
                        }
                    }
                }
                else
                {
                    trashTugTimer = 0f;
                    if (boatController != null)
                    {
                        boatController.SetPlayerPull(OwnerClientId, false, activeTargetPos, transform.position, activeTargetCol);
                    }

                    // G: kendorkan tali, tapi TIDAK boleh melebihi jarak lempar maksimal (lassoRange).
                    if (serverSlackHeld && netRopeLength.Value < MaxSlackLength)
                    {
                        netRopeLength.Value = Mathf.Min(
                            netRopeLength.Value + slackExtendSpeed * Time.deltaTime,
                            MaxSlackLength
                        );
                    }

                    // Tali selalu menahan kapal di ujung panjangnya (baik saat G ditekan maupun tidak).
                    bool nearlyTaut = actualDist > netRopeLength.Value - 0.05f;
                    netRopeTension.Value = Mathf.MoveTowards(netRopeTension.Value, nearlyTaut ? 1f : 0f, (nearlyTaut ? 5f : 3f) * Time.deltaTime);
                    if (boatController != null)
                    {
                        boatController.SetPlayerTether(OwnerClientId, true, activeTargetPos, transform.position, netRopeLength.Value);
                    }
                }

                prevActualDist = actualDist;
                yield return null;
            }
        }
        else
        {
            yield return RetractRopeRoutine();
        }
    }

    private IEnumerator RetractRopeRoutine()
    {
        netState.Value = LassoState.Retracting;

        float retractElapsed = 0f;
        while (retractElapsed < retractDuration)
        {
            retractElapsed += Time.deltaTime;
            netProgress.Value = 1f - Mathf.Clamp01(retractElapsed / retractDuration);
            yield return null;
        }

        netProgress.Value = 0f;
        netState.Value = LassoState.Idle;
        serverSequenceCoroutine = null;
    }

    // Kecepatan gulung tali: cepat saat kendor banyak, melambat di akhir (supaya tali mengencang halus, tidak menyentak)
    private float ReelRate(float slackLength)
    {
        return Mathf.Clamp(slackLength * ropeReelEase, ropeReelMinSpeed, ropeReelSpeed);
    }

    private Collider FindTargetAtPoint(Vector3 point)
    {
        Collider[] hits = Physics.OverlapSphere(point, catchRadius);
        Collider closestTarget = null;
        float closestDist = float.MaxValue;

        foreach (Collider col in hits)
        {
            if (col.CompareTag("Batu") || col.CompareTag("Sampah"))
            {
                // Sampah yang sudah lepas & hanyut tidak bisa di-hook lagi
                if (col.CompareTag("Sampah"))
                {
                    var trashTarget = col.GetComponentInParent<TrashObstacle>();
                    if (trashTarget != null && trashTarget.IsCleared) continue;
                }

                float dist = Vector3.Distance(point, col.transform.position);
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closestTarget = col;
                }
            }
        }
        return closestTarget;
    }

    /// <summary>Dipanggil server (mis. saat pemain respawn) untuk melepas tali seketika.</summary>
    public void ServerForceRelease()
    {
        if (!IsServer || netState.Value == LassoState.Idle) return;
        StopPullAndReset(false);
    }

    [ServerRpc]
    private void ReleaseLassoServerRpc()
    {
        if (netState.Value == LassoState.Pulling)
        {
            StopPullAndReset(true);
        }
    }

    private void StopPullAndReset(bool playRetractAnimation)
    {
        if (serverSequenceCoroutine != null)
        {
            StopCoroutine(serverSequenceCoroutine);
            serverSequenceCoroutine = null;
        }

        if (boatController == null) boatController = FindFirstObjectByType<BoatController>();
        if (boatController != null)
        {
            boatController.ClearPlayerInputs(OwnerClientId);
        }

        latchedTrash = null;
        latchedTrashCollider = null;
        latchedRockTransform = null;
        latchedRockCollider = null;
        serverPullHeld = false;
        serverSlackHeld = false;
        netRopeTension.Value = 0f;

        if (playRetractAnimation)
        {
            serverSequenceCoroutine = StartCoroutine(RetractRopeRoutine());
        }
        else
        {
            netProgress.Value = 0f;
            netState.Value = LassoState.Idle;
        }
    }

    private float FlatDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }
}