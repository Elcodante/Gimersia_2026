using System;
using UnityEngine;

/// <summary>
/// Representasi 1 ekor ikan dari Object Pool.
/// Tanggung jawab tunggal: menjalankan animasi/gerak kinematik (telegraph bawah air ->
/// lompatan parabola -> terpental & masuk kembali ke air -> kembali ke pool).
/// Tanpa Rigidbody agar super ringan di WebGL.
/// </summary>
[DisallowMultipleComponent]
public sealed class FishProjectile : MonoBehaviour
{
    public enum FishState : byte
    {
        Inactive = 0,
        Telegraphing = 1,
        Leaping = 2,
        Deflected = 3
    }

    [Header("Visual & Gerak Ikan")]
    [Tooltip("Transform visual anak (kotak placeholder / model ikan 3D). Kosong = transform root.")]
    [SerializeField] private Transform visualRoot;
    [Tooltip("Gravitasi saat ikan terpental setelah mengenai pemain.")]
    [SerializeField, Min(5f)] private float deflectGravity = 24f;
    [Tooltip("Kecepatan putaran tubuh ikan saat terpental (derajat/detik).")]
    [SerializeField, Min(0f)] private float deflectSpinSpeed = 720f;
    [Tooltip("Kecepatan kibasan ekor/goyangan tubuh selama melompat (Hz).")]
    [SerializeField, Min(0f)] private float wiggleFrequency = 14f;
    [Tooltip("Sudut goyangan tubuh selama melompat (derajat).")]
    [SerializeField, Range(0f, 25f)] private float wiggleAngle = 9f;

    [Header("Audio (ID di AudioManager)")]
    [SerializeField] private string splashSfxId = "player_splash";

    [Header("Partikel Air")]
    [Tooltip("Skala cipratan saat ikan menembus permukaan air untuk melompat.")]
    [SerializeField, Min(0.1f)] private float leapSplashScale = 1.2f;
    [Tooltip("Skala cipratan saat ikan nyemplung kembali ke air.")]
    [SerializeField, Min(0.1f)] private float entrySplashScale = 1.2f;
    [Tooltip("Skala riak di permukaan air sebelum ikan melompat.")]
    [SerializeField, Min(0.1f)] private float rippleScale = 1f;

    public int SlotIndex { get; private set; } = -1;
    public FishState State { get; private set; } = FishState.Inactive;
    public bool IsLeaping => State == FishState.Leaping;
    public Vector3 CurrentPosition => transform.position;
    public Vector3 HorizontalDirection => trajectory.HorizontalDir;

    private FishTrajectory trajectory;
    private Action<FishProjectile> onCompleted;

    private float stateElapsed;
    private Vector3 deflectVelocity;
    private Vector3 deflectSpinAxis;
    private bool hasPlayedSubmergeSplash;
    private Quaternion visualBaseLocalRotation = Quaternion.identity;
    private bool baseRotationCached;

    // Partikel air
    private float exitWaterY;
    private float entryWaterY;
    private float deflectWaterY;
    private bool deflectWaterSampled;
    private float prevY;
    private bool hasPlayedExitVfx;
    private bool hasPlayedEntryVfx;
    private bool hasPlayedMidRipple;

    private void Awake()
    {
        if (visualRoot == null && transform.childCount > 0)
        {
            visualRoot = transform.GetChild(0);
        }
        CacheVisualBaseRotation();
    }

    public void ConfigureVisualRoot(Transform visual)
    {
        visualRoot = visual;
        baseRotationCached = false;
        CacheVisualBaseRotation();
    }

    private void CacheVisualBaseRotation()
    {
        if (baseRotationCached || visualRoot == null) return;
        visualBaseLocalRotation = visualRoot.localRotation;
        baseRotationCached = true;
    }

    public void ConfigureSlot(int slotIndex)
    {
        SlotIndex = slotIndex;
    }

    /// <summary>
    /// Mengaktifkan ikan dari bawah air lalu melompat sesuai lintasan parabola deterministik.
    /// </summary>
    public void Launch(in FishTrajectory newTrajectory, Action<FishProjectile> completedCallback)
    {
        trajectory = newTrajectory;
        onCompleted = completedCallback;
        stateElapsed = 0f;
        hasPlayedSubmergeSplash = false;

        CacheVisualBaseRotation();
        if (visualRoot != null)
        {
            visualRoot.localRotation = visualBaseLocalRotation;
        }

        transform.position = trajectory.StartPos;
        exitWaterY = ResolveWaterSurfaceY(trajectory.StartPos);
        entryWaterY = ResolveWaterSurfaceY(trajectory.EndPos);
        deflectWaterY = entryWaterY;
        deflectWaterSampled = false;
        prevY = trajectory.StartPos.y;
        hasPlayedExitVfx = false;
        hasPlayedEntryVfx = false;
        hasPlayedMidRipple = false;

        Vector3 initialTangent = trajectory.EvaluateTangent(0f);
        if (initialTangent.sqrMagnitude > 0.0001f)
        {
            transform.rotation = Quaternion.LookRotation(initialTangent, Vector3.up);
        }

        gameObject.SetActive(true);

        if (trajectory.TelegraphDuration > 0.01f)
        {
            State = FishState.Telegraphing;
            PlayRippleVfx(trajectory.StartPos, exitWaterY);
        }
        else
        {
            EnterLeapingState();
        }
    }

    /// <summary>
    /// Dipanggil saat ikan mengenai pemain: ikan terpental memantul ke udara lalu tenggelam kembali ke air.
    /// </summary>
    public void Deflect(Vector3 hitPosition, Vector3 bounceVelocity)
    {
        if (State == FishState.Inactive || State == FishState.Deflected) return;

        transform.position = hitPosition;
        deflectVelocity = bounceVelocity;
        prevY = hitPosition.y;
        hasPlayedEntryVfx = false;
        deflectWaterSampled = false;
        deflectSpinAxis = Vector3.Cross(Vector3.up, bounceVelocity);
        if (deflectSpinAxis.sqrMagnitude < 0.001f) deflectSpinAxis = Vector3.right;
        else deflectSpinAxis.Normalize();

        stateElapsed = 0f;
        hasPlayedSubmergeSplash = false;
        State = FishState.Deflected;
    }

    /// <summary>Menghentikan dan menyembunyikan ikan segera (mis. saat scene berakhir/despawn).</summary>
    public void ForceDeactivate()
    {
        State = FishState.Inactive;
        onCompleted = null;
        if (visualRoot != null) visualRoot.localRotation = visualBaseLocalRotation;
        if (gameObject.activeSelf) gameObject.SetActive(false);
    }

    private void Update()
    {
        if (State == FishState.Inactive) return;

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        switch (State)
        {
            case FishState.Telegraphing:
                UpdateTelegraph(dt);
                break;

            case FishState.Leaping:
                UpdateLeap(dt);
                break;

            case FishState.Deflected:
                UpdateDeflected(dt);
                break;
        }
    }

    private void UpdateTelegraph(float dt)
    {
        stateElapsed += dt;
        float progress = Mathf.Clamp01(stateElapsed / trajectory.TelegraphDuration);

        // Naik perlahan mendekati permukaan air sambil bergetar singkat sebelum melesat keluar
        Vector3 pos = trajectory.StartPos;
        pos.y += Mathf.Lerp(0f, 0.55f, progress);
        float shake = Mathf.Sin(stateElapsed * 38f) * 0.08f;
        Vector3 side = Vector3.Cross(Vector3.up, trajectory.HorizontalDir);
        transform.position = pos + side * shake;

        // Riak kedua di permukaan air tepat sebelum ikan melesat keluar
        if (!hasPlayedMidRipple && progress >= 0.55f)
        {
            hasPlayedMidRipple = true;
            PlayRippleVfx(trajectory.StartPos, exitWaterY);
        }

        if (stateElapsed >= trajectory.TelegraphDuration)
        {
            EnterLeapingState();
        }
    }

    private void EnterLeapingState()
    {
        State = FishState.Leaping;
        prevY = transform.position.y;
        stateElapsed = 0f;
        PlaySplashSfx(0.12f);
    }

    private void UpdateLeap(float dt)
    {
        stateElapsed += dt;
        float t = Mathf.Clamp01(stateElapsed / trajectory.FlightDuration);

        Vector3 pos = trajectory.EvaluatePosition(t);
        Vector3 tangent = trajectory.EvaluateTangent(t);

        transform.position = pos;
        UpdateLeapWaterVfx(pos);
        if (tangent.sqrMagnitude > 0.0001f)
        {
            transform.rotation = Quaternion.LookRotation(tangent, Vector3.up);
        }

        if (visualRoot != null && wiggleAngle > 0.01f)
        {
            float yawWiggle = Mathf.Sin(stateElapsed * wiggleFrequency * Mathf.PI * 2f) * wiggleAngle;
            visualRoot.localRotation = Quaternion.Euler(0f, yawWiggle, 0f) * visualBaseLocalRotation;
        }

        // Bunyi cipratan saat ikan menukik kembali menyentuh permukaan air
        if (!hasPlayedSubmergeSplash && t >= 0.88f)
        {
            hasPlayedSubmergeSplash = true;
            PlaySplashSfx(0.15f);
        }

        if (t >= 1f)
        {
            if (!hasPlayedEntryVfx)
            {
                hasPlayedEntryVfx = true;
                PlayEntryVfx(pos, entryWaterY);
            }
            CompleteAndReturnToPool();
        }
    }

    private void UpdateDeflected(float dt)
    {
        stateElapsed += dt;
        deflectVelocity.y -= deflectGravity * dt;

        Vector3 pos = transform.position + deflectVelocity * dt;
        transform.position = pos;
        transform.Rotate(deflectSpinAxis, deflectSpinSpeed * dt, Space.World);

        // Ikan terpental jatuh menembus permukaan air -> cipratan masuk air
        if (deflectVelocity.y < 0f)
        {
            if (!deflectWaterSampled)
            {
                deflectWaterSampled = true;
                deflectWaterY = ResolveWaterSurfaceY(pos);
            }

            if (!hasPlayedEntryVfx && prevY > deflectWaterY && pos.y <= deflectWaterY)
            {
                hasPlayedEntryVfx = true;
                PlayEntryVfx(pos, deflectWaterY);
            }
        }
        prevY = pos.y;

        if (!hasPlayedSubmergeSplash && deflectVelocity.y < 0f && pos.y <= trajectory.SubmergeY + 1.2f)
        {
            hasPlayedSubmergeSplash = true;
            PlaySplashSfx(0.12f);
        }

        // Begitu ikan kembali tenggelam di bawah air (atau melewati batas waktu aman), kembalikan ke pool
        if (pos.y <= trajectory.SubmergeY || stateElapsed >= 3.0f)
        {
            CompleteAndReturnToPool();
        }
    }

    private void CompleteAndReturnToPool()
    {
        State = FishState.Inactive;
        if (visualRoot != null) visualRoot.localRotation = visualBaseLocalRotation;
        gameObject.SetActive(false);

        Action<FishProjectile> callback = onCompleted;
        onCompleted = null;
        callback?.Invoke(this);
    }

    // ------------------------------------------------------------------ PARTIKEL AIR

    private void UpdateLeapWaterVfx(Vector3 pos)
    {
        if (!hasPlayedExitVfx)
        {
            // Naik menembus permukaan air -> cipratan besar ke atas
            if (pos.y >= exitWaterY)
            {
                hasPlayedExitVfx = true;
                PlayLeapVfx(pos, exitWaterY);
            }
        }
        else if (!hasPlayedEntryVfx && prevY > entryWaterY && pos.y <= entryWaterY)
        {
            // Menukik kembali menembus permukaan air -> cipratan masuk air
            hasPlayedEntryVfx = true;
            PlayEntryVfx(pos, entryWaterY);
        }

        prevY = pos.y;
    }

    private float ResolveWaterSurfaceY(Vector3 samplePos)
    {
        RiverPath river = RiverPath.Main;
        if (river != null && river.IsValid)
        {
            return river.SampleAt(samplePos).center.y;
        }

        // Cadangan: FishTrajectory.BuildThroughTarget memasang SubmergeY = waterY - max(0.8, submergeDepth)
        return trajectory.SubmergeY + 1.6f;
    }

    private static Vector3 SurfacePoint(Vector3 pos, float waterY)
    {
        return new Vector3(pos.x, waterY + 0.03f, pos.z);
    }

    private void PlayRippleVfx(Vector3 pos, float waterY)
    {
        WaterSplashVFX.PlayRipple(SurfacePoint(pos, waterY), rippleScale);
    }

    private void PlayLeapVfx(Vector3 pos, float waterY)
    {
        WaterSplashVFX.PlayLeapSplash(SurfacePoint(pos, waterY), leapSplashScale);
    }

    private void PlayEntryVfx(Vector3 pos, float waterY)
    {
        WaterSplashVFX.PlayEntrySplash(SurfacePoint(pos, waterY), entrySplashScale);
    }

    private void PlaySplashSfx(float pitchRange)
    {
        if (!string.IsNullOrEmpty(splashSfxId) && AudioManager.Instance != null)
        {
            AudioManager.Instance.PlaySFX(splashSfxId, pitchRange);
        }
    }
}