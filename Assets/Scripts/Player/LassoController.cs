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

    [Header("Pengaturan Tarikan (Tombol F)")]
    [SerializeField] private float tugCooldown = 0.12f;
    [SerializeField] private float slackSagHeight = 0.65f;

    [Header("Pengaturan Visual Parabola & Durasi")]
    [SerializeField] private int arcSegments = 20;
    [SerializeField] private float maxArcHeight = 3.5f;
    [SerializeField] private float throwDuration = 0.2f;
    [SerializeField] private float retractDuration = 0.45f;
    [SerializeField] private float autoReleaseDistance = 1.8f;

    private LineRenderer lineRenderer;
    private BoatController boatController;
    private PlayerMovement playerMovement;

    private bool isLocalAiming = false;
    private Vector3 delayedAimPoint;
    private float lastLocalTugTime = -10f;

    private Transform latchedRockTransform;
    private TrashObstacle latchedTrash;

    private NetworkVariable<LassoState> netState = new NetworkVariable<LassoState>(LassoState.Idle);
    private NetworkVariable<Vector3> netTargetPoint = new NetworkVariable<Vector3>();
    private NetworkVariable<float> netProgress = new NetworkVariable<float>(0f);
    private NetworkVariable<float> netRopeTension = new NetworkVariable<float>(0f);

    private Coroutine serverSequenceCoroutine;

    private void Awake()
    {
        lineRenderer = GetComponent<LineRenderer>();
        playerMovement = GetComponent<PlayerMovement>();
        lineRenderer.enabled = false;
    }

    public override void OnNetworkSpawn()
    {
        boatController = GetComponentInParent<BoatController>();
        if (boatController == null) boatController = FindFirstObjectByType<BoatController>();
    }

    private void Update()
    {
        if (IsOwner && Application.isFocused)
        {
            HandleOwnerInput();
            UpdateMovementLock();
        }

        if (IsServer && netState.Value == LassoState.Pulling && netRopeTension.Value > 0f)
        {
            netRopeTension.Value = Mathf.MoveTowards(netRopeTension.Value, 0f, 3f * Time.deltaTime);
        }

        UpdateRopeVisuals();
    }

    private void HandleOwnerInput()
    {
        LassoState currentState = netState.Value;

        // 1. MULAI MODE AIM
        if (currentState == LassoState.Idle && !isLocalAiming)
        {
            if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            {
                isLocalAiming = true;
                delayedAimPoint = GetCursorWorldPoint();
            }
        }

        // 2. PROSES MODE AIM & LEMPAR
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
        // 3. MODE TERKAIT: Tekan F berkali-kali untuk menarik kapal ke Batu atau mencabut Sampah
        else if (currentState == LassoState.Pulling)
        {
            if (Keyboard.current != null && Keyboard.current.fKey.wasPressedThisFrame)
            {
                if (Time.time - lastLocalTugTime >= tugCooldown)
                {
                    lastLocalTugTime = Time.time;
                    TugLassoServerRpc(transform.position);
                }
            }
            else if (Mouse.current != null && Mouse.current.rightButton.wasPressedThisFrame)
            {
                ReleaseLassoServerRpc();
            }
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

    private void UpdateRopeVisuals()
    {
        Vector3 ropeOrigin = transform.position + Vector3.up * 0.5f;

        if (IsOwner && isLocalAiming)
        {
            lineRenderer.enabled = true;
            DrawParabola(ropeOrigin, delayedAimPoint, 1f, maxArcHeight);
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
                float currentSag = Mathf.Lerp(-slackSagHeight, 0f, netRopeTension.Value);
                DrawParabola(ropeOrigin, netTargetPoint.Value, 1f, currentSag);
                break;

            case LassoState.Retracting:
                lineRenderer.enabled = true;
                DrawParabola(ropeOrigin, netTargetPoint.Value, netProgress.Value, maxArcHeight * 0.35f);
                break;
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

    private IEnumerator ServerLassoRoutine(Vector3 landingPoint)
    {
        // 1. FASE MELEMPAR
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

        // 2. CEK PENDARATAN PADA BATU ATAU SAMPAH
        Collider hitTarget = FindTargetAtPoint(landingPoint);

        if (hitTarget != null)
        {
            latchedTrash = hitTarget.GetComponentInParent<TrashObstacle>();
            latchedRockTransform = (latchedTrash == null) ? hitTarget.transform : null;

            netTargetPoint.Value = hitTarget.transform.position;
            netRopeTension.Value = 0f;
            netState.Value = LassoState.Pulling;

            while (netState.Value == LassoState.Pulling)
            {
                // CABANG A: Target adalah SAMPAH (diam di dasar sungai, lepas hanya jika sampah sudah hilang)
                if (latchedTrash != null)
                {
                    if (!latchedTrash.IsSpawned || latchedTrash.IsCleared)
                    {
                        StopPullAndReset();
                        yield break;
                    }
                }
                // CABANG B: Target adalah BATU (otomatis lepas jika kapal sudah merapat ke batu)
                else if (latchedRockTransform != null)
                {
                    if (boatController == null) boatController = FindFirstObjectByType<BoatController>();

                    if (boatController != null)
                    {
                        Vector3 flatBoatPos = boatController.transform.position;
                        Vector3 flatRockPos = latchedRockTransform.position;
                        flatBoatPos.y = 0f;
                        flatRockPos.y = 0f;

                        if (Vector3.Distance(flatBoatPos, flatRockPos) <= autoReleaseDistance)
                        {
                            StopPullAndReset();
                            yield break;
                        }
                    }
                }
                else
                {
                    StopPullAndReset();
                    yield break;
                }

                yield return null;
            }
        }
        else
        {
            // MELESET -> Tarik tali kembali
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
        }
    }

    [ServerRpc]
    private void TugLassoServerRpc(Vector3 pullerWorldPos)
    {
        if (netState.Value != LassoState.Pulling) return;

        netRopeTension.Value = 1f;

        if (boatController == null) boatController = FindFirstObjectByType<BoatController>();

        // Jika terkait pada SAMPAH -> getarkan sampah & tarik kapal mendekati sampah
        if (latchedTrash != null)
        {
            Vector3 trashPos = latchedTrash.transform.position;
            latchedTrash.TugTrash();

            if (latchedTrash.IsCleared)
            {
                StopPullAndReset();
            }
            else if (boatController != null)
            {
                boatController.TugBoat(trashPos, pullerWorldPos);
            }
        }
        // Jika terkait pada BATU -> tarik kapal mendekati batu
        else if (latchedRockTransform != null && boatController != null)
        {
            boatController.TugBoat(latchedRockTransform.position, pullerWorldPos);
        }
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

    [ServerRpc]
    private void ReleaseLassoServerRpc()
    {
        if (netState.Value == LassoState.Pulling)
        {
            StopPullAndReset();
        }
    }

    private void StopPullAndReset()
    {
        if (serverSequenceCoroutine != null)
        {
            StopCoroutine(serverSequenceCoroutine);
            serverSequenceCoroutine = null;
        }

        if (boatController == null) boatController = FindFirstObjectByType<BoatController>();
        if (boatController != null)
        {
            boatController.StopPull();
        }

        latchedTrash = null;
        latchedRockTransform = null;
        netRopeTension.Value = 0f;
        netProgress.Value = 0f;
        netState.Value = LassoState.Idle;
    }
}