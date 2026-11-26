using TMPro;
using Unity.Cinemachine;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;
using UnityEngine.UI;

/// <summary>
/// Kamera intro sinematik ringan (WebGL / itch.io friendly) di awal permainan:
/// 1. Saat menunggu pemain bergabung, kamera sudah diparkir menghadap tujuan akhir (FinishGoal).
/// 2. Begitu semua pemain siap (GameSpawner.IsIntroPlaying == true), kamera menampilkan
///    tujuan akhir sejenak, lalu meluncur halus menelusuri lekukan sungai (RiverPath spline)
///    menuju kapal pemain dan menyatu mulus ke kamera gameplay.
/// 3. Selama kamera belum tiba di kapal, IsMatchStarted masih false sehingga pemain, laso,
///    kapal, dan timer match otomatis terkunci tanpa mengubah aturan game.
/// 4. Setelah selesai, virtual camera intro dan overlay dimatikan penuh (0 ms CPU saat gameplay).
/// </summary>
[DisallowMultipleComponent]
public class RiverIntroCamera : MonoBehaviour
{
    public static RiverIntroCamera Instance { get; private set; }

    /// <summary>True jika scene ini memiliki RiverPath dan FinishGoal untuk menjalankan intro flyover.</summary>
    public static bool CanPlayIntro => Instance != null && Instance.HasValidRoute();

    [Header("Durasi Intro (Detik)")]
    [Tooltip("Lama kamera bertahan menampilkan gerbang tujuan akhir sebelum mulai meluncur.")]
    [SerializeField, Min(0.5f)] private float holdAtGoalDuration = 1.8f;
    [Tooltip("Lama perjalanan kamera meluncur menelusuri lekukan sungai dari tujuan akhir ke kapal.")]
    [SerializeField, Min(1.5f)] private float riverGlideDuration = 5.2f;
    [Tooltip("Jeda singkat setelah kamera mendarat mulus di posisi kapal sebelum kendali dibuka.")]
    [SerializeField, Min(0f)] private float settleAtBoatDuration = 0.4f;

    [Header("Framing & Ketinggian (Ringan & Nyaman Dilihat)")]
    [Tooltip("Jarak kamera di depan tujuan akhir saat fase pembukaan (meter).")]
    [SerializeField, Range(8f, 45f)] private float goalFramingDistance = 24f;
    [Tooltip("Ketinggian kamera saat menampilkan tujuan akhir (meter di atas permukaan air).")]
    [SerializeField, Range(6f, 35f)] private float goalFramingHeight = 14f;
    [Tooltip("Ketinggian puncak kamera saat meluncur di tengah sungai agar pemandangan tikungan jelas & tidak bikin pusing.")]
    [SerializeField, Range(14f, 60f)] private float midRiverCruiseHeight = 32f;
    [Tooltip("Jarak pandang ke depan sepanjang spline saat meluncur (meter).")]
    [SerializeField, Range(10f, 80f)] private float glideLookAheadMeters = 38f;
    [Tooltip("Amplitudo gerak menyamping halus saat menyorot tujuan akhir (meter).")]
    [SerializeField, Range(0f, 8f)] private float goalSubtleOrbitAmplitude = 3.2f;
    [Tooltip("Kecepatan smoothing rotasi kamera selama meluncur.")]
    [SerializeField, Range(2f, 14f)] private float rotationSmoothSpeed = 6.5f;

    [Header("Efisiensi Spline LUT (WebGL)")]
    [Tooltip("Jumlah titik sampel spline (192 titik = ~3 KB RAM, nol alokasi GC per frame).")]
    [SerializeField, Range(64, 384)] private int lutSampleCount = 192;

    [Header("Teks Keterangan Singkat (Opsional)")]
    [SerializeField] private bool showIntroCaption = true;
    [SerializeField] private string goalCaptionTitle = "TUJUAN AKHIR";
    [SerializeField] private string goalCaptionSubtitle = "Capai gerbang finis di hulu sungai!";
    [SerializeField] private string glideCaptionSubtitle = "Menuju kapal...";

    /// <summary>Total durasi intro yang disinkronkan dengan GameSpawner di Server.</summary>
    public float TotalDuration => holdAtGoalDuration + riverGlideDuration + settleAtBoatDuration;

    private CinemachineCamera gameplayCamera;
    private CinemachineCamera introVirtualCamera;
    private GameObject introCamObject;

    private RiverPath river;
    private Transform goalTransform;
    private Transform boatTransform;

    // Flat arrays: nol alokasi GC saat runtime
    private float[] lutX;
    private float[] lutY;
    private float[] lutZ;
    private float[] lutArc;
    private bool lutReady;

    private float goalArcDist;
    private float boatArcDist;
    private Vector3 goalWorldPos;
    private Vector3 goalDirTowardBoat;

    private bool introStarted;
    private bool introCompleted;
    private float introElapsed;
    private Quaternion smoothedRotation;
    private bool rotationInitialized;

    // Caption UI ringan (dimatikan penuh setelah intro)
    private GameObject captionCanvasObj;
    private CanvasGroup captionCanvasGroup;
    private TMP_Text captionTitleText;
    private TMP_Text captionSubtitleText;

    private const int IntroCameraPriority = 100;
    private const int InactiveCameraPriority = -10;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Instance = null;
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }

        Instance = this;
        gameplayCamera = GetComponent<CinemachineCamera>();
        if (gameplayCamera == null)
        {
            gameplayCamera = FindFirstObjectByType<CinemachineCamera>();
        }

        ResolveReferences();
        BuildSplineLut();

        if (HasValidRoute())
        {
            CreateIntroVirtualCamera();
            ParkCameraAtGoal(0f);
            if (showIntroCaption)
            {
                BuildCaptionUI();
            }
        }
    }

    private void OnEnable()
    {
        GameSpawner.OnIntroStarted += HandleIntroStarted;
        GameSpawner.OnIntroFinished += HandleIntroFinished;
    }

    private void OnDisable()
    {
        GameSpawner.OnIntroStarted -= HandleIntroStarted;
        GameSpawner.OnIntroFinished -= HandleIntroFinished;
    }

    private void Start()
    {
        if (!lutReady)
        {
            ResolveReferences();
            BuildSplineLut();
            if (HasValidRoute() && introVirtualCamera == null)
            {
                CreateIntroVirtualCamera();
                if (showIntroCaption && captionCanvasObj == null)
                {
                    BuildCaptionUI();
                }
            }
        }

        if (HasValidRoute() && introVirtualCamera != null && !introCompleted)
        {
            // Pastikan posisi awal di Tujuan Akhir sudah akurat setelah semua scene object aktif
            ParkCameraAtGoal(0f);

            // Jika GameSpawner sudah lebih dulu memicu intro atau match sebelum Start()
            if (GameSpawner.Instance != null)
            {
                if (GameSpawner.Instance.IsMatchStarted.Value)
                {
                    FinishIntroImmediately();
                }
                else if (GameSpawner.Instance.IsIntroPlaying.Value)
                {
                    HandleIntroStarted();
                }
            }
        }
        else
        {
            FinishIntroImmediately();
        }
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }

        if (introCamObject != null)
        {
            Destroy(introCamObject);
            introCamObject = null;
        }

        if (captionCanvasObj != null)
        {
            Destroy(captionCanvasObj);
            captionCanvasObj = null;
        }
    }

    private bool HasValidRoute()
    {
        return lutReady && goalTransform != null && boatTransform != null && gameplayCamera != null;
    }

    private void HandleIntroStarted()
    {
        if (introCompleted) return;

        if (!HasValidRoute())
        {
            ResolveReferences();
            BuildSplineLut();
            if (!HasValidRoute())
            {
                FinishIntroImmediately();
                return;
            }
        }

        if (introVirtualCamera == null)
        {
            CreateIntroVirtualCamera();
        }

        introStarted = true;
        introElapsed = 0f;
        rotationInitialized = false;

        if (introCamObject != null) introCamObject.SetActive(true);
        if (introVirtualCamera != null) introVirtualCamera.Priority = IntroCameraPriority;

        ParkCameraAtGoal(0f);
    }

    private void HandleIntroFinished()
    {
        FinishIntroImmediately();
    }

    private void LateUpdate()
    {
        if (introCompleted || introVirtualCamera == null) return;

        // Jika match sudah dimulai oleh server (mis. karena selisih latensi tipis), langsung serahkan ke kamera gameplay
        if (GameSpawner.Instance != null && GameSpawner.Instance.IsMatchStarted.Value)
        {
            FinishIntroImmediately();
            return;
        }

        // Sebelum semua pemain siap, tetap parkir kamera dengan tenang di Tujuan Akhir
        if (!introStarted)
        {
            if (GameSpawner.Instance != null && GameSpawner.Instance.IsIntroPlaying.Value)
            {
                HandleIntroStarted();
            }
            else
            {
                ParkCameraAtGoal(0f);
                UpdateCaptionVisual(0f, false);
                return;
            }
        }

        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
        introElapsed += dt;

        // Update referensi kapal jika baru selesai di-spawn/diposisikan
        if (boatTransform == null)
        {
            ResolveReferences();
            if (boatTransform != null && lutReady)
            {
                boatArcDist = ProjectToArcDist(boatTransform.position);
            }
        }

        // Sinkronkan FOV lensa dengan kamera gameplay
        if (gameplayCamera != null)
        {
            introVirtualCamera.Lens = gameplayCamera.Lens;
        }

        // Fase 1: Menyorot tujuan akhir (Finish Goal)
        if (introElapsed <= holdAtGoalDuration)
        {
            float holdT = holdAtGoalDuration > 0.001f ? Mathf.Clamp01(introElapsed / holdAtGoalDuration) : 1f;
            ParkCameraAtGoal(holdT);
            UpdateCaptionVisual(introElapsed, true);
            return;
        }

        // Fase 2: Meluncur halus menelusuri lekukan sungai dari Tujuan Akhir -> Kapal
        float glideElapsed = introElapsed - holdAtGoalDuration;
        float glideU = riverGlideDuration > 0.001f ? Mathf.Clamp01(glideElapsed / riverGlideDuration) : 1f;

        UpdateRiverGlidePose(glideU, dt);
        UpdateCaptionVisual(introElapsed, true);

        // Fase 3: Sudah tiba mulus di pose kamera gameplay kapal -> tunggu sinyal unlock dari server
        if (introElapsed >= TotalDuration && GameSpawner.Instance == null)
        {
            // Fallback offline/editor tanpa GameSpawner
            FinishIntroImmediately();
        }
    }

    private void ParkCameraAtGoal(float holdProgress01)
    {
        if (introVirtualCamera == null || !lutReady) return;

        Vector3 right = Vector3.Cross(Vector3.up, goalDirTowardBoat);
        if (right.sqrMagnitude < 0.0001f) right = Vector3.right;
        else right.Normalize();

        // Gerak menyamping halus agar tampilan tujuan akhir terasa hidup
        float orbitOffset = Mathf.Sin((holdProgress01 - 0.5f) * Mathf.PI) * goalSubtleOrbitAmplitude;

        Vector3 camPos = goalWorldPos
            + goalDirTowardBoat * goalFramingDistance
            + right * orbitOffset
            + Vector3.up * goalFramingHeight;

        Vector3 lookTarget = goalWorldPos + Vector3.up * 3.2f;
        Vector3 lookDir = lookTarget - camPos;
        Quaternion targetRot = lookDir.sqrMagnitude > 0.001f
            ? Quaternion.LookRotation(lookDir.normalized, Vector3.up)
            : Quaternion.identity;

        introVirtualCamera.transform.SetPositionAndRotation(camPos, targetRot);
        smoothedRotation = targetRot;
        rotationInitialized = true;
    }

    private void UpdateRiverGlidePose(float u, float dt)
    {
        // SmootherStep: akselerasi awal lembut, meluncur cepat di tengah, melambat halus saat mendekati kapal
        float easedU = SmootherStep(u);

        // Titik awal lintasan tepat di posisi kamera saat selesai hold di depan tujuan akhir
        float totalSignedDelta = boatArcDist - goalArcDist;
        float travelSign = totalSignedDelta >= 0f ? 1f : -1f;
        float startArc = Mathf.Clamp(goalArcDist + travelSign * goalFramingDistance, 0f, lutArc[lutArc.Length - 1]);

        float currentArc = Mathf.Lerp(startArc, boatArcDist, easedU);
        Vector3 splinePos = SampleSplineByArc(currentArc);

        // Ketinggian melengkung (naik di tengah sungai, turun mulus ke ketinggian gameplay saat mendekati kapal)
        GetGameplayTargetPose(out Vector3 gameplayPos, out Quaternion gameplayRot);

        float baseHeight = Mathf.Lerp(goalFramingHeight, Mathf.Max(8f, gameplayPos.y - splinePos.y), easedU);
        float cruiseExtra = Mathf.Max(0f, midRiverCruiseHeight - baseHeight) * Mathf.Sin(Mathf.PI * u);
        Vector3 glidePos = new Vector3(splinePos.x, splinePos.y + baseHeight + cruiseExtra, splinePos.z);

        // Titik pandang menatap ke depan sepanjang lekukan sungai menuju kapal
        float remainingToBoat = Mathf.Abs(boatArcDist - currentArc);
        float lookAhead = Mathf.Min(glideLookAheadMeters, Mathf.Max(6f, remainingToBoat));
        float aheadArc = Mathf.Clamp(currentArc + travelSign * lookAhead, 0f, lutArc[lutArc.Length - 1]);
        Vector3 lookPoint = SampleSplineByArc(aheadArc) + Vector3.up * 1.5f;

        // Pada awal transisi (0..18%), putar perlahan dari arah menatap FinishGoal ke arah menyusuri sungai
        Vector3 goalLookPoint = goalWorldPos + Vector3.up * 3.2f;
        float turnAwayFromGoal = SmootherStep(Mathf.Clamp01(u / 0.18f));
        Vector3 blendedLookTarget = Vector3.Lerp(goalLookPoint, lookPoint, turnAwayFromGoal);

        Vector3 dirToLook = blendedLookTarget - glidePos;
        Quaternion glideRot = dirToLook.sqrMagnitude > 0.001f
            ? Quaternion.LookRotation(dirToLook.normalized, Vector3.up)
            : gameplayRot;

        if (!rotationInitialized)
        {
            smoothedRotation = glideRot;
            rotationInitialized = true;
        }
        else
        {
            float rotLerp = 1f - Mathf.Exp(-rotationSmoothSpeed * Mathf.Max(dt, 0.0001f));
            smoothedRotation = Quaternion.Slerp(smoothedRotation, glideRot, rotLerp);
        }

        // Pada 28% terakhir lintasan, menyatu langsung ke posisi & rotasi eksak kamera gameplay di kapal
        const float settleStartU = 0.72f;
        float settleBlend = u > settleStartU
            ? SmootherStep((u - settleStartU) / (1f - settleStartU))
            : 0f;

        Vector3 finalPos = Vector3.Lerp(glidePos, gameplayPos, settleBlend);
        Quaternion finalRot = Quaternion.Slerp(smoothedRotation, gameplayRot, settleBlend);

        introVirtualCamera.transform.SetPositionAndRotation(finalPos, finalRot);
    }

    private void GetGameplayTargetPose(out Vector3 pos, out Quaternion rot)
    {
        Vector3 boatPos = boatTransform != null ? boatTransform.position : Vector3.zero;

        if (gameplayCamera != null)
        {
            CameraState state = gameplayCamera.State;
            pos = state.GetFinalPosition();
            rot = state.GetFinalOrientation();

            // Pastikan pose Cinemachine valid & berada di sekitar area kapal (bukan default origin 0,0,0)
            if (IsFinite(pos) && (pos - boatPos).sqrMagnitude < 10000f)
            {
                return;
            }
        }

        // Fallback aman jika state Cinemachine belum terisi pada frame pertama
        pos = boatPos + new Vector3(0f, 14f, -15f);
        rot = Quaternion.Euler(45f, 0f, 0f);
    }

    private void FinishIntroImmediately()
    {
        if (introCompleted) return;
        introCompleted = true;
        introStarted = false;

        if (introVirtualCamera != null)
        {
            introVirtualCamera.Priority = InactiveCameraPriority;
            introVirtualCamera.enabled = false;
        }

        if (introCamObject != null)
        {
            introCamObject.SetActive(false);
        }

        if (captionCanvasObj != null)
        {
            captionCanvasObj.SetActive(false);
        }

        // Matikan komponen ini setelah intro usai agar 0 ms beban CPU saat gameplay (WebGL friendly)
        enabled = false;
    }

    // ------------------------------------------------------------------ SPLINE LUT (Zero-GC)

    private void ResolveReferences()
    {
        if (river == null) river = RiverPath.Main;

        if (goalTransform == null)
        {
            if (RiverFinishGoal.Instance != null)
            {
                goalTransform = RiverFinishGoal.Instance.transform;
            }
            else
            {
                RiverFinishGoal foundGoal = FindFirstObjectByType<RiverFinishGoal>();
                if (foundGoal != null) goalTransform = foundGoal.transform;
            }
        }

        if (boatTransform == null)
        {
            BoatController boat = FindFirstObjectByType<BoatController>();
            if (boat != null) boatTransform = boat.transform;
        }
    }

    private void BuildSplineLut()
    {
        lutReady = false;
        if (river == null || !river.IsValid) return;

        SplineContainer container = river.GetComponent<SplineContainer>();
        if (container == null || container.Spline == null || container.Spline.Count < 2) return;

        int count = Mathf.Clamp(lutSampleCount, 64, 384);
        lutX = new float[count];
        lutY = new float[count];
        lutZ = new float[count];
        lutArc = new float[count];

        float accum = 0f;
        float prevX = 0f;
        float prevY = 0f;
        float prevZ = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / (count - 1);
            float3 pos = container.EvaluatePosition(t);
            lutX[i] = pos.x;
            lutY[i] = pos.y;
            lutZ[i] = pos.z;

            if (i > 0)
            {
                float dx = pos.x - prevX;
                float dy = pos.y - prevY;
                float dz = pos.z - prevZ;
                accum += Mathf.Sqrt(dx * dx + dy * dy + dz * dz);
            }

            lutArc[i] = accum;
            prevX = pos.x;
            prevY = pos.y;
            prevZ = pos.z;
        }

        lutReady = accum > 1f;
        if (!lutReady) return;

        goalWorldPos = goalTransform != null
            ? goalTransform.position
            : new Vector3(lutX[0], lutY[0], lutZ[0]);

        goalArcDist = ProjectToArcDist(goalWorldPos);
        boatArcDist = boatTransform != null
            ? ProjectToArcDist(boatTransform.position)
            : (goalArcDist < accum * 0.5f ? accum : 0f);

        // Tentukan arah aliran sungai dari Tujuan Akhir menuju Kapal (untuk menaruh kamera di depan gerbang finis)
        float travelSign = (boatArcDist >= goalArcDist) ? 1f : -1f;
        float probeArc = Mathf.Clamp(goalArcDist + travelSign * 18f, 0f, accum);
        Vector3 probePos = SampleSplineByArc(probeArc);
        Vector3 dir = probePos - goalWorldPos;
        dir.y = 0f;
        goalDirTowardBoat = dir.sqrMagnitude > 0.001f ? dir.normalized : Vector3.back;
    }

    private float ProjectToArcDist(Vector3 worldPos)
    {
        int count = lutX.Length;
        int bestIdx = 0;
        float bestSqr = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            float dx = worldPos.x - lutX[i];
            float dz = worldPos.z - lutZ[i];
            float sqr = dx * dx + dz * dz;
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                bestIdx = i;
            }
        }

        return lutArc[bestIdx];
    }

    private Vector3 SampleSplineByArc(float targetArc)
    {
        int last = lutArc.Length - 1;
        if (targetArc <= 0f) return new Vector3(lutX[0], lutY[0], lutZ[0]);
        if (targetArc >= lutArc[last]) return new Vector3(lutX[last], lutY[last], lutZ[last]);

        // Binary search O(log N), nol alokasi memori
        int lo = 0;
        int hi = last;
        while (lo + 1 < hi)
        {
            int mid = (lo + hi) >> 1;
            if (lutArc[mid] <= targetArc) lo = mid;
            else hi = mid;
        }

        float segLen = lutArc[hi] - lutArc[lo];
        float frac = segLen > 1e-5f ? (targetArc - lutArc[lo]) / segLen : 0f;

        return new Vector3(
            Mathf.Lerp(lutX[lo], lutX[hi], frac),
            Mathf.Lerp(lutY[lo], lutY[hi], frac),
            Mathf.Lerp(lutZ[lo], lutZ[hi], frac)
        );
    }

    // ------------------------------------------------------------------ VIRTUAL CAMERA & CAPTION

    private void CreateIntroVirtualCamera()
    {
        if (introCamObject != null) return;

        introCamObject = new GameObject("RiverIntroVirtualCamera");
        introVirtualCamera = introCamObject.AddComponent<CinemachineCamera>();
        introVirtualCamera.Priority = IntroCameraPriority;

        if (gameplayCamera != null)
        {
            gameplayCamera.StandbyUpdate = CinemachineVirtualCameraBase.StandbyUpdateMode.Always;
            introVirtualCamera.Lens = gameplayCamera.Lens;
            introVirtualCamera.OutputChannel = gameplayCamera.OutputChannel;
        }
    }

    private void BuildCaptionUI()
    {
        captionCanvasObj = new GameObject("RiverIntroCaptionCanvas");
        captionCanvasObj.transform.SetParent(transform, false);

        Canvas canvas = captionCanvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 450;

        CanvasScaler scaler = captionCanvasObj.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        captionCanvasGroup = captionCanvasObj.AddComponent<CanvasGroup>();
        captionCanvasGroup.interactable = false;
        captionCanvasGroup.blocksRaycasts = false;
        captionCanvasGroup.alpha = 0f;

        GameObject bannerObj = new GameObject("CaptionBanner", typeof(RectTransform), typeof(Image));
        bannerObj.transform.SetParent(captionCanvasObj.transform, false);
        RectTransform bannerRt = bannerObj.GetComponent<RectTransform>();
        bannerRt.anchorMin = new Vector2(0.5f, 0f);
        bannerRt.anchorMax = new Vector2(0.5f, 0f);
        bannerRt.pivot = new Vector2(0.5f, 0f);
        bannerRt.sizeDelta = new Vector2(680f, 92f);
        bannerRt.anchoredPosition = new Vector2(0f, 44f);

        Image bannerBg = bannerObj.GetComponent<Image>();
        bannerBg.color = new Color(0.07f, 0.10f, 0.14f, 0.78f);
        bannerBg.raycastTarget = false;

        GameObject titleObj = new GameObject("CaptionTitle", typeof(RectTransform));
        titleObj.transform.SetParent(bannerObj.transform, false);
        captionTitleText = titleObj.AddComponent<TextMeshProUGUI>();
        captionTitleText.text = goalCaptionTitle;
        captionTitleText.fontSize = 28f;
        captionTitleText.fontStyle = FontStyles.Bold;
        captionTitleText.alignment = TextAlignmentOptions.Center;
        captionTitleText.color = new Color(1f, 0.86f, 0.38f, 1f);
        captionTitleText.raycastTarget = false;

        RectTransform titleRt = captionTitleText.rectTransform;
        titleRt.anchorMin = new Vector2(0f, 0.48f);
        titleRt.anchorMax = new Vector2(1f, 0.96f);
        titleRt.offsetMin = new Vector2(16f, 0f);
        titleRt.offsetMax = new Vector2(-16f, 0f);

        GameObject subObj = new GameObject("CaptionSubtitle", typeof(RectTransform));
        subObj.transform.SetParent(bannerObj.transform, false);
        captionSubtitleText = subObj.AddComponent<TextMeshProUGUI>();
        captionSubtitleText.text = goalCaptionSubtitle;
        captionSubtitleText.fontSize = 21f;
        captionSubtitleText.fontStyle = FontStyles.Normal;
        captionSubtitleText.alignment = TextAlignmentOptions.Center;
        captionSubtitleText.color = Color.white;
        captionSubtitleText.raycastTarget = false;

        RectTransform subRt = captionSubtitleText.rectTransform;
        subRt.anchorMin = new Vector2(0f, 0.06f);
        subRt.anchorMax = new Vector2(1f, 0.50f);
        subRt.offsetMin = new Vector2(16f, 0f);
        subRt.offsetMax = new Vector2(-16f, 0f);
    }

    private void UpdateCaptionVisual(float elapsed, bool active)
    {
        if (captionCanvasGroup == null) return;

        if (!active)
        {
            captionCanvasGroup.alpha = 0f;
            return;
        }

        float fadeInEnd = 0.35f;
        float fadeOutStart = holdAtGoalDuration + riverGlideDuration * 0.68f;
        float fadeOutEnd = holdAtGoalDuration + riverGlideDuration * 0.88f;

        float alpha;
        if (elapsed < fadeInEnd)
        {
            alpha = Mathf.Clamp01(elapsed / fadeInEnd);
        }
        else if (elapsed > fadeOutStart)
        {
            float span = Mathf.Max(0.1f, fadeOutEnd - fadeOutStart);
            alpha = 1f - Mathf.Clamp01((elapsed - fadeOutStart) / span);
        }
        else
        {
            alpha = 1f;
        }

        captionCanvasGroup.alpha = alpha;

        if (captionSubtitleText != null)
        {
            string desired = elapsed <= holdAtGoalDuration + 0.35f ? goalCaptionSubtitle : glideCaptionSubtitle;
            if (captionSubtitleText.text != desired)
            {
                captionSubtitleText.text = desired;
            }
        }
    }

    private static float SmootherStep(float t)
    {
        t = Mathf.Clamp01(t);
        return t * t * t * (t * (t * 6f - 15f) + 10f);
    }

    private static bool IsFinite(Vector3 v)
    {
        return float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    }
}
