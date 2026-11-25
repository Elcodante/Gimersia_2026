using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// View UI murni untuk menampilkan Progress Bar perjalanan kapal di bagian atas layar.
/// Membaca data dari RiverProgressTracker tanpa logika gameplay/jaringan.
/// Mendukung sprite desain kustom (Border, Track/Fill, Boat Marker, Goal Icon).
/// </summary>
[DisallowMultipleComponent]
public class RiverProgressBarUI : MonoBehaviour
{
    [Header("Referensi UI Kustom (Opsional — kosong = buat otomatis)")]
    [SerializeField] private RectTransform barRoot;
    [SerializeField] private Image fillImage;
    [SerializeField] private RectTransform boatMarker;
    [SerializeField] private TMP_Text statusText;

    [Header("Sprite Desain Progress Bar")]
    [SerializeField] private Sprite borderSprite;
    [SerializeField] private Sprite trackAndFillSprite;
    [SerializeField] private Sprite boatMarkerSprite;
    [SerializeField] private Sprite goalIconSprite;

    [Header("Ukuran & Posisi")]
    [SerializeField] private float barWidth = 560f;
    [SerializeField] private float barHeight = 26f;
    [SerializeField] private float topMargin = 22f;
    [SerializeField] private Vector2 boatMarkerSize = new Vector2(68f, 44f);
    [SerializeField] private Vector2 goalIconSize = new Vector2(46f, 46f);

    [Header("Warna & Teks")]
    [SerializeField] private TMP_FontAsset fontAsset;
    [SerializeField] private bool showStatusText = true;
    [SerializeField] private Color trackTint = Color.white;
    [SerializeField] private Color fillStartColor = new Color(0.72f, 0.84f, 0.47f, 1f);
    [SerializeField] private Color fillGoalColor = new Color(0.62f, 0.84f, 0.38f, 1f);

    [Header("Animasi")]
    [SerializeField] private float fillSmoothSpeed = 7f;

    private RectTransform fillTrackRect;
    private float targetProgress;
    private float displayedProgress;
    private float remainingMeters;
    private bool isGoalReached;

    private int lastPrintedPercent = -1;
    private int lastPrintedMeters = -1;
    private bool lastPrintedFinished;

    private RiverProgressTracker subscribedTracker;

    private void Awake()
    {
        if (barRoot == null)
        {
            BuildDefaultProgressBar();
        }
        else if (fillImage != null)
        {
            fillTrackRect = fillImage.rectTransform.parent as RectTransform;
        }
    }

    private void OnEnable()
    {
        RiverFinishGoal.OnGoalReached += HandleGoalReached;
        TrySubscribeTracker();
    }

    private void Start()
    {
        TrySubscribeTracker();
        ApplyVisualProgress(displayedProgress, forceText: true);
    }

    private void OnDisable()
    {
        RiverFinishGoal.OnGoalReached -= HandleGoalReached;
        UnsubscribeTracker();
    }

    private void Update()
    {
        if (subscribedTracker == null)
        {
            TrySubscribeTracker();
        }

        float dt = Time.unscaledDeltaTime;
        if (Mathf.Abs(displayedProgress - targetProgress) > 0.0005f)
        {
            displayedProgress = Mathf.Lerp(displayedProgress, targetProgress, 1f - Mathf.Exp(-fillSmoothSpeed * dt));
            if (Mathf.Abs(displayedProgress - targetProgress) <= 0.0005f)
            {
                displayedProgress = targetProgress;
            }
            ApplyVisualProgress(displayedProgress, forceText: false);
        }
    }

    private void TrySubscribeTracker()
    {
        if (subscribedTracker != null) return;

        RiverProgressTracker tracker = RiverProgressTracker.Instance != null
            ? RiverProgressTracker.Instance
            : FindFirstObjectByType<RiverProgressTracker>();

        if (tracker == null) return;

        subscribedTracker = tracker;
        subscribedTracker.OnProgressUpdated += HandleProgressUpdated;
        HandleProgressUpdated(subscribedTracker.NormalizedProgress, subscribedTracker.RemainingMeters);
    }

    private void UnsubscribeTracker()
    {
        if (subscribedTracker != null)
        {
            subscribedTracker.OnProgressUpdated -= HandleProgressUpdated;
            subscribedTracker = null;
        }
    }

    private void HandleProgressUpdated(float normalized, float remaining)
    {
        targetProgress = isGoalReached ? 1f : Mathf.Clamp01(normalized);
        remainingMeters = isGoalReached ? 0f : Mathf.Max(0f, remaining);
        UpdateTextIfChanged(force: false);
    }

    private void HandleGoalReached(float completionSeconds)
    {
        isGoalReached = true;
        targetProgress = 1f;
        remainingMeters = 0f;
        UpdateTextIfChanged(force: true);
    }

    private void ApplyVisualProgress(float progress01, bool forceText)
    {
        progress01 = Mathf.Clamp01(progress01);

        if (fillImage != null)
        {
            if (fillImage.type == Image.Type.Filled)
            {
                fillImage.fillAmount = progress01;
            }
            else
            {
                RectTransform fillRt = fillImage.rectTransform;
                fillRt.anchorMin = Vector2.zero;
                fillRt.anchorMax = new Vector2(progress01, 1f);
                fillRt.offsetMin = Vector2.zero;
                fillRt.offsetMax = Vector2.zero;
            }

            fillImage.color = Color.Lerp(fillStartColor, fillGoalColor, progress01);
        }

        if (boatMarker != null && fillTrackRect != null)
        {
            float trackWidth = fillTrackRect.rect.width;
            float leftInset = boatMarkerSize.x * 0.35f;
            float rightInset = goalIconSize.x * 0.45f;
            float usableWidth = Mathf.Max(1f, trackWidth - leftInset - rightInset);
            boatMarker.anchoredPosition = new Vector2(leftInset + progress01 * usableWidth, 0f);
        }

        if (forceText)
        {
            UpdateTextIfChanged(force: true);
        }
    }

    private void UpdateTextIfChanged(bool force)
    {
        if (statusText == null || !showStatusText) return;

        int pct = Mathf.RoundToInt(targetProgress * 100f);
        int meters = Mathf.CeilToInt(remainingMeters);

        if (!force && pct == lastPrintedPercent && meters == lastPrintedMeters && isGoalReached == lastPrintedFinished)
        {
            return;
        }

        lastPrintedPercent = pct;
        lastPrintedMeters = meters;
        lastPrintedFinished = isGoalReached;

        if (isGoalReached)
        {
            statusText.text = "TUJUAN TERCAPAI! 100%";
        }
        else if (meters >= 1000)
        {
            float km = meters * 0.001f;
            statusText.text = $"{pct}%  •  {km:0.00} km";
        }
        else
        {
            statusText.text = $"{pct}%  •  {meters} m";
        }
    }

    private void BuildDefaultProgressBar()
    {
        Transform parentCanvas = transform;
        if (GetComponent<Canvas>() == null)
        {
            Canvas foundCanvas = GetComponentInParent<Canvas>();
            if (foundCanvas != null) parentCanvas = foundCanvas.transform;
        }

        // Container utama transparan di atas-tengah layar
        GameObject rootGo = new GameObject("RiverProgressBar_HUD", typeof(RectTransform));
        rootGo.transform.SetParent(parentCanvas, false);
        rootGo.transform.SetAsFirstSibling(); // Pause menu tetap tampil di lapisan atas

        barRoot = rootGo.GetComponent<RectTransform>();
        barRoot.anchorMin = new Vector2(0.5f, 1f);
        barRoot.anchorMax = new Vector2(0.5f, 1f);
        barRoot.pivot = new Vector2(0.5f, 1f);
        barRoot.sizeDelta = new Vector2(barWidth, Mathf.Max(barHeight, goalIconSize.y) + (showStatusText ? 26f : 0f));
        barRoot.anchoredPosition = new Vector2(0f, -topMargin);

        // Area bar (track + fill + border)
        GameObject barBodyGo = new GameObject("BarBody", typeof(RectTransform));
        barBodyGo.transform.SetParent(barRoot, false);
        fillTrackRect = barBodyGo.GetComponent<RectTransform>();
        fillTrackRect.anchorMin = new Vector2(0.5f, 1f);
        fillTrackRect.anchorMax = new Vector2(0.5f, 1f);
        fillTrackRect.pivot = new Vector2(0.5f, 1f);
        fillTrackRect.sizeDelta = new Vector2(barWidth, barHeight);
        fillTrackRect.anchoredPosition = new Vector2(0f, -(Mathf.Max(0f, goalIconSize.y - barHeight) * 0.5f));

        // 1. Track background (BLANK PROGRES)
        GameObject trackGo = new GameObject("Track", typeof(RectTransform), typeof(Image));
        trackGo.transform.SetParent(fillTrackRect, false);
        RectTransform trackRt = trackGo.GetComponent<RectTransform>();
        trackRt.anchorMin = Vector2.zero;
        trackRt.anchorMax = Vector2.one;
        trackRt.offsetMin = new Vector2(2f, 2f);
        trackRt.offsetMax = new Vector2(-2f, -2f);

        Image trackImg = trackGo.GetComponent<Image>();
        trackImg.sprite = trackAndFillSprite;
        trackImg.color = trackAndFillSprite != null ? trackTint : new Color(0.74f, 0.67f, 0.59f, 0.95f);
        trackImg.raycastTarget = false;

        // 2. Fill bar (BLANK PROGRES dengan Filled Horizontal atau fallback anchor)
        GameObject fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fillGo.transform.SetParent(fillTrackRect, false);
        RectTransform fillRt = fillGo.GetComponent<RectTransform>();
        fillRt.anchorMin = Vector2.zero;
        fillRt.anchorMax = Vector2.one;
        fillRt.offsetMin = new Vector2(2f, 2f);
        fillRt.offsetMax = new Vector2(-2f, -2f);

        fillImage = fillGo.GetComponent<Image>();
        fillImage.sprite = trackAndFillSprite;
        if (trackAndFillSprite != null)
        {
            fillImage.type = Image.Type.Filled;
            fillImage.fillMethod = Image.FillMethod.Horizontal;
            fillImage.fillOrigin = (int)Image.OriginHorizontal.Left;
            fillImage.fillAmount = 0f;
        }
        fillImage.color = fillStartColor;
        fillImage.raycastTarget = false;

        // 3. Border outline (PROGRES BORDER) di atas Track & Fill
        if (borderSprite != null)
        {
            GameObject borderGo = new GameObject("Border", typeof(RectTransform), typeof(Image));
            borderGo.transform.SetParent(fillTrackRect, false);
            RectTransform borderRt = borderGo.GetComponent<RectTransform>();
            borderRt.anchorMin = Vector2.zero;
            borderRt.anchorMax = Vector2.one;
            borderRt.offsetMin = Vector2.zero;
            borderRt.offsetMax = Vector2.zero;

            Image borderImg = borderGo.GetComponent<Image>();
            borderImg.sprite = borderSprite;
            borderImg.color = Color.white;
            borderImg.raycastTarget = false;
        }

        // 4. Ikon tujuan akhir di ujung kanan bar (OBJECTIF PROGRESS)
        GameObject goalPinGo = new GameObject("GoalIcon", typeof(RectTransform), typeof(Image));
        goalPinGo.transform.SetParent(fillTrackRect, false);
        RectTransform goalPinRt = goalPinGo.GetComponent<RectTransform>();
        goalPinRt.anchorMin = new Vector2(1f, 0.5f);
        goalPinRt.anchorMax = new Vector2(1f, 0.5f);
        goalPinRt.pivot = new Vector2(0.5f, 0.5f);
        goalPinRt.sizeDelta = goalIconSize;
        goalPinRt.anchoredPosition = new Vector2(-goalIconSize.x * 0.2f, 0f);

        Image goalPinImg = goalPinGo.GetComponent<Image>();
        goalPinImg.sprite = goalIconSprite;
        goalPinImg.preserveAspect = true;
        goalPinImg.color = goalIconSprite != null ? Color.white : new Color(0.95f, 0.76f, 0.22f, 1f);
        goalPinImg.raycastTarget = false;

        // 5. Ikon kapal yang meluncur sepanjang bar (BOAT PROGRES)
        GameObject markerGo = new GameObject("BoatMarker", typeof(RectTransform), typeof(Image));
        markerGo.transform.SetParent(fillTrackRect, false);
        boatMarker = markerGo.GetComponent<RectTransform>();
        boatMarker.anchorMin = new Vector2(0f, 0.5f);
        boatMarker.anchorMax = new Vector2(0f, 0.5f);
        boatMarker.pivot = new Vector2(0.5f, 0.5f);
        boatMarker.sizeDelta = boatMarkerSize;
        boatMarker.anchoredPosition = new Vector2(boatMarkerSize.x * 0.35f, 0f);

        Image markerImg = markerGo.GetComponent<Image>();
        markerImg.sprite = boatMarkerSprite;
        markerImg.preserveAspect = true;
        markerImg.color = boatMarkerSprite != null ? Color.white : new Color(1f, 0.94f, 0.72f, 1f);
        markerImg.raycastTarget = false;

        // 6. Teks persentase & jarak yang ringkas di bawah bar (opsional)
        if (showStatusText)
        {
            GameObject textGo = new GameObject("StatusText", typeof(RectTransform));
            textGo.transform.SetParent(barRoot, false);
            statusText = textGo.AddComponent<TextMeshProUGUI>();
            if (fontAsset != null) statusText.font = fontAsset;
            statusText.fontSize = 18f;
            statusText.fontStyle = FontStyles.Bold;
            statusText.alignment = TextAlignmentOptions.Center;
            statusText.color = Color.white;
            statusText.outlineWidth = 0.22f;
            statusText.outlineColor = new Color(0.18f, 0.14f, 0.10f, 0.92f);
            statusText.raycastTarget = false;

            RectTransform textRt = statusText.rectTransform;
            textRt.anchorMin = new Vector2(0f, 0f);
            textRt.anchorMax = new Vector2(1f, 0f);
            textRt.pivot = new Vector2(0.5f, 0f);
            textRt.sizeDelta = new Vector2(0f, 24f);
            textRt.anchoredPosition = Vector2.zero;
        }
    }
}
