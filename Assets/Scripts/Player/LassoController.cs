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

    [Header("Pengaturan Tarikan & Irama Tangan (1___1___1)")]
    [SerializeField] private float trashTugInterval = 0.55f;     // Dipakai jika Sync Trash Tug To Rhythm dimatikan
    [Tooltip("Sinkronkan getaran/hitungan cabut sampah tepat pada tiap tarikan tangan (1___1___1).")]
    [SerializeField] private bool syncTrashTugToRhythm = true;
    [Tooltip("Frekuensi tarikan cadangan jika BoatController tidak ditemukan (otomatis disamakan dengan BoatController).")]
    [SerializeField] private float fallbackPullCadenceFrequency = 1.20f;
    [SerializeField, Range(0.2f, 0.8f)] private float fallbackPullStrokeDuty = 0.45f;
    [Tooltip("Seberapa jauh pangkal tali ikut tertarik mundur oleh tangan (meter).")]
    [SerializeField] private float handPullBackOffset = 0.28f;
    [Tooltip("Jarak geser pangkal tali antara tangan kanan dan kiri (meter).")]
    [SerializeField] private float handSwitchSideOffset = 0.22f;

    [Header("Pengaturan Kendur & Putus Tali")]
    [SerializeField] private Key slackKey = Key.G;
    [SerializeField] private float slackExtendSpeed = 4.0f;
    [Tooltip("Jarak putus: tali lepas otomatis kalau kapal terseret lebih jauh dari ini.")]
    [SerializeField] private float maxRopeLength = 28.0f;
    [Tooltip("Kecepatan menggulung tali kendor saat F ditahan (m/detik). Makin kecil = tali mengencang makin pelan/halus.")]
    [SerializeField] private float ropeReelSpeed = 6.5f;   // kecepatan gulung MAKSIMUM (saat kendor banyak)
    [Tooltip("Gulungan melambat saat kendor tinggal sedikit agar tali mengencang halus, tidak menyentak. Kecepatan = kendor (m) x nilai ini, dibatasi Min dan Max.")]
    [SerializeField] private float ropeReelEase = 1.8f;
    [SerializeField] private float ropeReelMinSpeed = 1.0f;
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
    private PlayerSwayAnimator swayAnimator;

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
    private float serverPullHoldTime = 0f;
    private int lastServerTrashStroke = -1;

    // State irama visual lokal (owner + remote client)
    private bool pullingVisualActive = false;
    private float localPullElapsed = 0f;
    private Vector3 currentPullDir = Vector3.forward;
    private float currentVisualHandSide = 1f;
    private float currentTugPulse = 0f;
    private float currentReachPulse = 0f;

    private NetworkVariable<LassoState> netState = new NetworkVariable<LassoState>(LassoState.Idle);
    private NetworkVariable<Vector3> netTargetPoint = new NetworkVariable<Vector3>();
    private NetworkVariable<float> netProgress = new NetworkVariable<float>(0f);
    private NetworkVariable<float> netRopeLength = new NetworkVariable<float>(0f);
    private NetworkVariable<float> netRopeTension = new NetworkVariable<float>(0f);
    private NetworkVariable<bool> netIsPulling = new NetworkVariable<bool>(false);

    private Coroutine serverSequenceCoroutine;

    private float MaxSlackLength => Mathf.Min(lassoRange, maxRopeLength);
    private float EffectiveCadenceFreq => boatController != null ? boatController.PullCadenceFrequency : fallbackPullCadenceFrequency;
    private float EffectiveStrokeDuty => boatController != null ? boatController.PullStrokeDuty : fallbackPullStrokeDuty;

    private void Awake()
    {
        lineRenderer = GetComponent<LineRenderer>();
        playerMovement = GetComponent<PlayerMovement>();
        swayAnimator = GetComponent<PlayerSwayAnimator>();
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
        if (IsOwner && InputFocusGuard.CanAcceptInput)
        {
            HandleOwnerInput();
            UpdateMovementLock();
        }

        UpdatePullRhythmVisuals();
    }

    // LateUpdate: supaya pangkal tali ikut posisi tangan setelah pemain/kapal selesai bergerak
    private void LateUpdate()
    {
        UpdateRopeVisuals();
    }

    private void HandleOwnerInput()
    {
        if (!GameSpawner.IsMatchReady || (playerMovement != null && (playerMovement.IsRespawning || playerMovement.IsOffBoatOrRespawning)))
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

                FaceWorldPoint(delayedAimPoint, 15f);
            }
            else if (Mouse.current != null && Mouse.current.leftButton.wasReleasedThisFrame)
            {
                isLocalAiming = false;
                ThrowLassoServerRpc(delayedAimPoint);
            }
        }
        else if (currentState == LassoState.Pulling)
        {
            // Tetap menghadap batu/sampah yang ditarik meski kapal berputar
            FaceWorldPoint(netTargetPoint.Value, 12f);

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

    private void FaceWorldPoint(Vector3 worldPoint, float speed)
    {
        Vector3 lookDir = worldPoint - transform.position;
        lookDir.y = 0f;
        if (lookDir.sqrMagnitude <= 0.001f) return;

        Quaternion targetWorldRot = Quaternion.LookRotation(lookDir.normalized, Vector3.up);
        if (transform.parent != null)
        {
            Quaternion targetLocalRot = Quaternion.Inverse(transform.parent.rotation) * targetWorldRot;
            transform.localRotation = Quaternion.Slerp(transform.localRotation, targetLocalRot, speed * Time.deltaTime);
        }
        else
        {
            transform.rotation = Quaternion.Slerp(transform.rotation, targetWorldRot, speed * Time.deltaTime);
        }
    }

    private void UpdatePullRhythmVisuals()
    {
        if (boatController == null) boatController = FindFirstObjectByType<BoatController>();

        bool isPullingNow = IsSpawned
            && netState.Value == LassoState.Pulling
            && (playerMovement == null || !playerMovement.IsOffBoatOrRespawning)
            && (IsOwner
                ? (InputFocusGuard.CanAcceptInput && !isLocalAiming && Keyboard.current != null && Keyboard.current.fKey.isPressed && netIsPulling.Value || (IsOwner && Keyboard.current != null && Keyboard.current.fKey.isPressed && InputFocusGuard.CanAcceptInput && !isLocalAiming && !IsServer))
                : netIsPulling.Value);

        // Gunakan status jaringan sebagai otoritas saat menyentuh batu target,
        // namun tetap responsif di frame pertama owner menekan F.
        if (IsOwner)
        {
            bool localKeyF = InputFocusGuard.CanAcceptInput && !isLocalAiming && Keyboard.current != null && Keyboard.current.fKey.isPressed;
            isPullingNow = IsSpawned
                && netState.Value == LassoState.Pulling
                && (playerMovement == null || !playerMovement.IsOffBoatOrRespawning)
                && localKeyF
                && (netIsPulling.Value || !lastSentPullHeld);
        }

        if (!isPullingNow)
        {
            pullingVisualActive = false;
            localPullElapsed = 0f;
            currentTugPulse = 0f;
            currentReachPulse = 0f;
            swayAnimator?.SetPullRhythm(false, Vector3.forward, 1f, 0f, 0f);
            return;
        }

        pullingVisualActive = true;
        localPullElapsed += Time.deltaTime;

        Vector3 dir = netTargetPoint.Value - transform.position;
        dir.y = 0f;
        currentPullDir = dir.sqrMagnitude > 0.001f ? dir.normalized : transform.forward;

        float freq = Mathf.Max(0.1f, EffectiveCadenceFreq);
        float duty = Mathf.Clamp(EffectiveStrokeDuty, 0.15f, 0.85f);

        float cycle = localPullElapsed * freq;
        int strokeIndex = Mathf.FloorToInt(cycle);
        float phase = cycle - strokeIndex;

        float handSign = (strokeIndex % 2 == 0) ? 1f : -1f; // +1 tangan kanan, -1 tangan kiri

        if (phase < duty)
        {
            float u = phase / duty;
            float s = Mathf.Sin(Mathf.Pow(u, 0.75f) * Mathf.PI);
            currentTugPulse = s;
            currentReachPulse = 0f;
            currentVisualHandSide = handSign;
        }
        else
        {
            float v = (phase - duty) / Mathf.Max(0.01f, 1f - duty);
            float smooth = v * v * (3f - 2f * v);
            currentTugPulse = 0f;
            currentReachPulse = Mathf.Sin(v * Mathf.PI);
            // Geser mulus posisi tangan dari sisi lama ke sisi tangan berikutnya selama jeda '___'
            currentVisualHandSide = Mathf.Lerp(handSign, -handSign, smooth);
        }

        swayAnimator?.SetPullRhythm(true, currentPullDir, currentVisualHandSide, currentTugPulse, currentReachPulse);
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
        Vector3 baseOrigin = (handAnchor != null) ? handAnchor.position : transform.position + Vector3.up * 0.5f;
        if (!pullingVisualActive) return baseOrigin;

        Vector3 fwd = currentPullDir;
        if (fwd.sqrMagnitude < 0.001f)
        {
            fwd = transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.001f) fwd.Normalize();
            else fwd = Vector3.forward;
        }

        Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
        float forwardBack = (currentReachPulse * handPullBackOffset * 0.45f) - (currentTugPulse * handPullBackOffset);
        return baseOrigin + fwd * forwardBack + right * (currentVisualHandSide * handSwitchSideOffset);
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
                Vector3 tip = ArcPoint(origin, netTargetPoint.Value, netProgress.Value, maxArcHeight);
                ropeVisual.Drive(origin, tip, Vector3.Distance(origin, tip) * 1.1f + 0.15f);
                break;
            }

            case LassoState.Pulling:
            {
                Vector3 anchor = netTargetPoint.Value;
                float chord = Vector3.Distance(origin, anchor);
                float flatDist = FlatDistance(transform.position, anchor);
                float slack = Mathf.Max(0f, netRopeLength.Value - flatDist);
                float ropeLen3D = chord + slack;

                float slackRatio = slack / Mathf.Max(0.01f, ropeLen3D);
                float k = Mathf.Clamp01(slackRatio / 0.12f);
                float taut = 1f - k * k * (3f - 2f * k);

                // Modulasi ketegangan tali mengikuti denyut tangan (1___1___1)
                if (pullingVisualActive)
                {
                    taut *= Mathf.Lerp(0.70f, 1f, currentTugPulse);
                }

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
        if (!GameSpawner.IsMatchReady || netState.Value != LassoState.Idle) return;

        if (FlatDistance(transform.position, landingPoint) > lassoRange + 0.25f)
        {
            if (serverSequenceCoroutine != null) StopCoroutine(serverSequenceCoroutine);
            serverSequenceCoroutine = StartCoroutine(RetractRopeRoutine());
            return;
        }

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
        netIsPulling.Value = false;
        netState.Value = LassoState.Throwing;
        PlayLassoSfxClientRpc("lasso_throw", 0.08f);

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

            PlayLassoSfxClientRpc(latchedTrash != null ? "lasso_latch_trash" : "lasso_latch_rock", 0.08f);

            Vector3 targetPos = hitTarget.transform.position;
            netTargetPoint.Value = targetPos;

            float initialDist = FlatDistance(transform.position, targetPos);
            float prevActualDist = initialDist;
            netRopeLength.Value = Mathf.Clamp(initialDist, 0.5f, maxRopeLength - 0.5f);
            netRopeTension.Value = 0.2f;

            serverPullHeld = false;
            serverSlackHeld = false;
            trashTugTimer = 0f;
            serverPullHoldTime = 0f;
            lastServerTrashStroke = -1;
            netIsPulling.Value = false;
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

                if (latchedTrash != null && (netTargetPoint.Value - activeTargetPos).sqrMagnitude > 0.0004f)
                {
                    netTargetPoint.Value = activeTargetPos;
                }

                float actualDist = FlatDistance(transform.position, activeTargetPos);

                if (actualDist >= maxRopeLength)
                {
                    StopPullAndReset(true);
                    yield break;
                }

                if (serverPullHeld)
                {
                    if (latchedRockTransform != null && boatController != null &&
                        boatController.IsTouchingOrAtSurface(latchedRockCollider, activeTargetPos))
                    {
                        netIsPulling.Value = false;
                        serverPullHoldTime = 0f;
                        lastServerTrashStroke = -1;

                        boatController.SetPlayerPull(OwnerClientId, false, activeTargetPos, transform.position, activeTargetCol);
                        boatController.SetPlayerTether(OwnerClientId, false, activeTargetPos, transform.position, netRopeLength.Value);

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

                    netIsPulling.Value = true;
                    serverPullHoldTime += Time.deltaTime;

                    if (boatController != null)
                    {
                        boatController.SetPlayerTether(OwnerClientId, false, activeTargetPos, transform.position, netRopeLength.Value);
                        boatController.SetPlayerPull(OwnerClientId, true, activeTargetPos, transform.position, activeTargetCol);
                    }

                    float boatClosed = Mathf.Max(0f, prevActualDist - actualDist);
                    if (netRopeLength.Value > actualDist)
                    {
                        float reelDelta = boatClosed + ReelRate(netRopeLength.Value - actualDist) * Time.deltaTime;
                        netRopeLength.Value = Mathf.Max(0.5f, Mathf.Max(actualDist, netRopeLength.Value - reelDelta));
                    }

                    // Ketegangan visual mengikuti irama 1___1___1
                    float freq = Mathf.Max(0.1f, EffectiveCadenceFreq);
                    float duty = Mathf.Clamp(EffectiveStrokeDuty, 0.15f, 0.85f);
                    float phase = Mathf.Repeat(serverPullHoldTime * freq, 1f);
                    float pulse = phase < duty ? Mathf.Sin((phase / duty) * Mathf.PI) : 0f;
                    float targetTension = Mathf.Lerp(0.45f, 1f, pulse);
                    netRopeTension.Value = Mathf.MoveTowards(netRopeTension.Value, targetTension, 6f * Time.deltaTime);

                    if (latchedTrash != null)
                    {
                        if (syncTrashTugToRhythm)
                        {
                            int strokeIdx = Mathf.FloorToInt(serverPullHoldTime * freq);
                            if (strokeIdx != lastServerTrashStroke && phase >= duty * 0.25f)
                            {
                                lastServerTrashStroke = strokeIdx;
                                latchedTrash.TugTrash();

                                if (latchedTrash.IsCleared)
                                {
                                    StopPullAndReset(false);
                                    yield break;
                                }
                            }
                        }
                        else
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
                }
                else
                {
                    netIsPulling.Value = false;
                    trashTugTimer = 0f;
                    serverPullHoldTime = 0f;
                    lastServerTrashStroke = -1;

                    if (boatController != null)
                    {
                        boatController.SetPlayerPull(OwnerClientId, false, activeTargetPos, transform.position, activeTargetCol);
                    }

                    if (serverSlackHeld && netRopeLength.Value < MaxSlackLength)
                    {
                        netRopeLength.Value = Mathf.Min(
                            netRopeLength.Value + slackExtendSpeed * Time.deltaTime,
                            MaxSlackLength
                        );
                    }

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
        netIsPulling.Value = false;
        netState.Value = LassoState.Retracting;
        PlayLassoSfxClientRpc("lasso_release", 0.06f);

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
            if (col == null || col.isTrigger) continue;

            HookableTarget hookableTarget = col.GetComponentInParent<HookableTarget>();
            if (hookableTarget == null || !hookableTarget.CanBeHooked) continue;

            TrashObstacle trashTarget = col.GetComponentInParent<TrashObstacle>();
            if (trashTarget != null && trashTarget.IsCleared) continue;

            float dist = Vector3.Distance(point, col.ClosestPoint(point));
            if (dist < closestDist)
            {
                closestDist = dist;
                closestTarget = col;
            }
        }
        return closestTarget;
    }

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
        serverPullHoldTime = 0f;
        lastServerTrashStroke = -1;
        netIsPulling.Value = false;
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

    [ClientRpc]
    private void PlayLassoSfxClientRpc(string sfxId, float pitchRange)
    {
        AudioManager.Instance?.PlaySFX(sfxId, pitchRange);
    }

    private float FlatDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }
}