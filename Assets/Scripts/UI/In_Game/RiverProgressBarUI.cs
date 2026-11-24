using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// View UI murni untuk menampilkan Progress Bar perjalanan kapal di bagian atas layar.
/// Membaca data dari RiverProgressTracker tanpa logika gameplay/jaringan.
/// Jika referensi UI di Inspector dikosongkan, komponen otomatis membuat HUD bar rapi pada Canvas.
/// </summary>
[DisallowMultipleComponent]
public class RiverProgressBarUI : MonoBehaviour
{
    [Header("Referensi UI Kustom (Opsional — kosong = buat otomatis)")]
    [SerializeField] private RectTransform barRoot;
    [SerializeField] private Image fillImage;
    [SerializeField] private RectTransform boatMarker;
    [SerializeField] private TMP_Text statusText;

    [Header("Gaya & Font (Saat Auto-Create)")]
    [SerializeField] private TMP_FontAsset fontAsset;
    [SerializeField] private float barWidth = 520f;
    [SerializeField] private float topMargin = 20f;
    [SerializeField] private Color frameColor = new Color(0.11f, 0.14f, 0.18f, 0.84f);
    [SerializeField] private Color trackColor = new Color(0.20f, 0.25f, 0.30f, 0.92f);
    [SerializeField] private Color fillStartColor = new Color(0.20f, 0.72f, 0.85f, 1f);
    [SerializeField] private Color fillGoalColor = new Color(0.95f, 0.76f, 0.22f, 1f);
    [SerializeField] private Color markerColor = new Color(1f, 0.94f, 0.72f, 1f);

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
            float width = fillTrackRect.rect.width;
            boatMarker.anchoredPosition = new Vector2(progress01 * width, 0f);
        }

        if (forceText)
        {
            UpdateTextIfChanged(force: true);
        }
    }

    private void UpdateTextIfChanged(bool force)
    {
        if (statusText == null) return;

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
            statusText.text = $"{pct}%  •  {km:0.00} km ke tujuan";
        }
        else
        {
            statusText.text = $"{pct}%  •  {meters} m ke tujuan";
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

        // Container utama di atas-tengah layar
        GameObject rootGo = new GameObject("RiverProgressBar_HUD", typeof(RectTransform), typeof(Image));
        rootGo.transform.SetParent(parentCanvas, false);
        rootGo.transform.SetAsFirstSibling(); // Pastikan Pause Menu (Panel_Settings) tetap tampil di atas HUD

        barRoot = rootGo.GetComponent<RectTransform>();
        barRoot.anchorMin = new Vector2(0.5f, 1f);
        barRoot.anchorMax = new Vector2(0.5f, 1f);
        barRoot.pivot = new Vector2(0.5f, 1f);
        barRoot.sizeDelta = new Vector2(barWidth, 58f);
        barRoot.anchoredPosition = new Vector2(0f, -topMargin);

        Image frameBg = rootGo.GetComponent<Image>();
        frameBg.color = frameColor;
        frameBg.raycastTarget = false;

        // Track background bar
        GameObject trackGo = new GameObject("Track", typeof(RectTransform), typeof(Image));
        trackGo.transform.SetParent(barRoot, false);
        fillTrackRect = trackGo.GetComponent<RectTransform>();
        fillTrackRect.anchorMin = new Vector2(0f, 1f);
        fillTrackRect.anchorMax = new Vector2(1f, 1f);
        fillTrackRect.pivot = new Vector2(0.5f, 1f);
        fillTrackRect.offsetMin = new Vector2(18f, -24f);
        fillTrackRect.offsetMax = new Vector2(-18f, -10f);

        Image trackImg = trackGo.GetComponent<Image>();
        trackImg.color = trackColor;
        trackImg.raycastTarget = false;

        // Fill bar (menggunakan anchorX dinamis agar tidak butuh Sprite khusus)
        GameObject fillGo = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fillGo.transform.SetParent(fillTrackRect, false);
        RectTransform fillRt = fillGo.GetComponent<RectTransform>();
        fillRt.anchorMin = Vector2.zero;
        fillRt.anchorMax = new Vector2(0f, 1f);
        fillRt.offsetMin = Vector2.zero;
        fillRt.offsetMax = Vector2.zero;

        fillImage = fillGo.GetComponent<Image>();
        fillImage.color = fillStartColor;
        fillImage.raycastTarget = false;

        // Penanda posisi tujuan di ujung kanan track
        GameObject goalPinGo = new GameObject("GoalPin", typeof(RectTransform), typeof(Image));
        goalPinGo.transform.SetParent(fillTrackRect, false);
        RectTransform goalPinRt = goalPinGo.GetComponent<RectTransform>();
        goalPinRt.anchorMin = new Vector2(1f, 0.5f);
        goalPinRt.anchorMax = new Vector2(1f, 0.5f);
        goalPinRt.pivot = new Vector2(0.5f, 0.5f);
        goalPinRt.sizeDelta = new Vector2(10f, 20f);
        goalPinRt.anchoredPosition = Vector2.zero;

        Image goalPinImg = goalPinGo.GetComponent<Image>();
        goalPinImg.color = fillGoalColor;
        goalPinImg.raycastTarget = false;

        // Penanda posisi kapal yang meluncur sepanjang bar
        GameObject markerGo = new GameObject("BoatMarker", typeof(RectTransform), typeof(Image));
        markerGo.transform.SetParent(fillTrackRect, false);
        boatMarker = markerGo.GetComponent<RectTransform>();
        boatMarker.anchorMin = new Vector2(0f, 0.5f);
        boatMarker.anchorMax = new Vector2(0f, 0.5f);
        boatMarker.pivot = new Vector2(0.5f, 0.5f);
        boatMarker.sizeDelta = new Vector2(16f, 22f);
        boatMarker.anchoredPosition = Vector2.zero;

        Image markerImg = markerGo.GetComponent<Image>();
        markerImg.color = markerColor;
        markerImg.raycastTarget = false;

        // Teks status persentase & sisa jarak di bawah track
        GameObject textGo = new GameObject("StatusText", typeof(RectTransform));
        textGo.transform.SetParent(barRoot, false);
        statusText = textGo.AddComponent<TextMeshProUGUI>();
        if (fontAsset != null) statusText.font = fontAsset;
        statusText.fontSize = 18f;
        statusText.fontStyle = FontStyles.Bold;
        statusText.alignment = TextAlignmentOptions.Center;
        statusText.color = new Color(0.96f, 0.96f, 0.94f, 1f);
        statusText.raycastTarget = false;

        RectTransform textRt = statusText.rectTransform;
        textRt.anchorMin = new Vector2(0f, 0f);
        textRt.anchorMax = new Vector2(1f, 0f);
        textRt.pivot = new Vector2(0.5f, 0f);
        textRt.sizeDelta = new Vector2(-24f, 24f);
        textRt.anchoredPosition = new Vector2(0f, 4f);
    }
}

