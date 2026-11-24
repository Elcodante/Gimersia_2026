using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// View UI murni untuk layar kemenangan saat kapal mencapai RiverFinishGoal.
/// Jika panel kustom tidak diisi di Inspector, panel kemenangan rapi akan dibuat otomatis pada Canvas.
/// </summary>
[DisallowMultipleComponent]
public class RiverVictoryUI : MonoBehaviour
{
    [Header("Referensi UI Kustom (Opsional — kosong = buat otomatis)")]
    [SerializeField] private GameObject victoryPanel;
    [SerializeField] private TMP_Text titleText;
    [SerializeField] private TMP_Text timeText;
    [SerializeField] private Button returnMenuButton;
    [SerializeField] private ReturnToMenuController returnToMenuController;

    [Header("Gaya & Font (Saat Auto-Create)")]
    [SerializeField] private TMP_FontAsset fontAsset;
    [SerializeField] private string victoryTitle = "SELAMAT! SAMPAI DI TUJUAN!";
    [SerializeField] private string returnButtonLabel = "Kembali ke Menu Utama";

    private CanvasGroup panelCanvasGroup;
    private Coroutine fadeRoutine;

    private void Awake()
    {
        if (returnToMenuController == null)
        {
            returnToMenuController = GetComponent<ReturnToMenuController>();
            if (returnToMenuController == null)
            {
                returnToMenuController = FindFirstObjectByType<ReturnToMenuController>();
            }
        }

        if (victoryPanel == null)
        {
            BuildDefaultVictoryPanel();
        }
        else
        {
            panelCanvasGroup = victoryPanel.GetComponent<CanvasGroup>();
            victoryPanel.SetActive(false);
        }

        if (returnMenuButton != null)
        {
            returnMenuButton.onClick.AddListener(HandleReturnToMenuClicked);
        }
    }

    private void OnEnable()
    {
        RiverFinishGoal.OnGoalReached += HandleGoalReached;
    }

    private void OnDisable()
    {
        RiverFinishGoal.OnGoalReached -= HandleGoalReached;
    }

    private void OnDestroy()
    {
        if (returnMenuButton != null)
        {
            returnMenuButton.onClick.RemoveListener(HandleReturnToMenuClicked);
        }
    }

    private void HandleGoalReached(float completionSeconds)
    {
        if (victoryPanel == null) return;

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        if (titleText != null)
        {
            titleText.text = victoryTitle;
        }

        if (timeText != null)
        {
            int totalSec = Mathf.Max(0, Mathf.FloorToInt(completionSeconds));
            int minutes = totalSec / 60;
            int seconds = totalSec % 60;
            timeText.text = $"Waktu tempuh tim: {minutes:00}:{seconds:00}";
        }

        victoryPanel.SetActive(true);
        victoryPanel.transform.SetAsLastSibling();

        if (panelCanvasGroup != null)
        {
            if (fadeRoutine != null) StopCoroutine(fadeRoutine);
            fadeRoutine = StartCoroutine(FadeInRoutine());
        }
    }

    private void HandleReturnToMenuClicked()
    {
        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.PlaySFX("ui_click", 0.05f);
        }

        if (returnToMenuController == null)
        {
            returnToMenuController = FindFirstObjectByType<ReturnToMenuController>();
        }

        if (returnToMenuController != null)
        {
            returnToMenuController.ReturnToMainMenu();
        }
    }

    private IEnumerator FadeInRoutine()
    {
        panelCanvasGroup.alpha = 0f;
        float t = 0f;
        const float duration = 0.35f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            panelCanvasGroup.alpha = Mathf.Clamp01(t / duration);
            yield return null;
        }
        panelCanvasGroup.alpha = 1f;
        fadeRoutine = null;
    }

    private void BuildDefaultVictoryPanel()
    {
        Transform parentCanvas = transform;
        if (GetComponent<Canvas>() == null)
        {
            Canvas foundCanvas = GetComponentInParent<Canvas>();
            if (foundCanvas != null) parentCanvas = foundCanvas.transform;
        }

        // Latar gelap transparan penuh layar
        victoryPanel = new GameObject("RiverVictory_Panel", typeof(RectTransform), typeof(Image), typeof(CanvasGroup));
        victoryPanel.transform.SetParent(parentCanvas, false);

        RectTransform overlayRt = victoryPanel.GetComponent<RectTransform>();
        overlayRt.anchorMin = Vector2.zero;
        overlayRt.anchorMax = Vector2.one;
        overlayRt.offsetMin = Vector2.zero;
        overlayRt.offsetMax = Vector2.zero;

        Image overlayImg = victoryPanel.GetComponent<Image>();
        overlayImg.color = new Color(0.05f, 0.08f, 0.12f, 0.72f);

        panelCanvasGroup = victoryPanel.GetComponent<CanvasGroup>();
        panelCanvasGroup.alpha = 0f;

        // Kartu kemenangan di tengah layar
        GameObject cardGo = new GameObject("VictoryCard", typeof(RectTransform), typeof(Image));
        cardGo.transform.SetParent(victoryPanel.transform, false);
        RectTransform cardRt = cardGo.GetComponent<RectTransform>();
        cardRt.anchorMin = new Vector2(0.5f, 0.5f);
        cardRt.anchorMax = new Vector2(0.5f, 0.5f);
        cardRt.sizeDelta = new Vector2(640f, 320f);
        cardRt.anchoredPosition = Vector2.zero;

        Image cardBg = cardGo.GetComponent<Image>();
        cardBg.color = new Color(0.13f, 0.17f, 0.22f, 0.96f);

        // Garis aksen emas di atas kartu
        GameObject accentGo = new GameObject("TopAccent", typeof(RectTransform), typeof(Image));
        accentGo.transform.SetParent(cardGo.transform, false);
        RectTransform accentRt = accentGo.GetComponent<RectTransform>();
        accentRt.anchorMin = new Vector2(0f, 1f);
        accentRt.anchorMax = new Vector2(1f, 1f);
        accentRt.pivot = new Vector2(0.5f, 1f);
        accentRt.sizeDelta = new Vector2(0f, 10f);
        accentRt.anchoredPosition = Vector2.zero;
        accentGo.GetComponent<Image>().color = new Color(0.95f, 0.76f, 0.22f, 1f);

        // Judul kemenangan
        GameObject titleGo = new GameObject("TitleText", typeof(RectTransform));
        titleGo.transform.SetParent(cardGo.transform, false);
        titleText = titleGo.AddComponent<TextMeshProUGUI>();
        if (fontAsset != null) titleText.font = fontAsset;
        titleText.text = victoryTitle;
        titleText.fontSize = 36f;
        titleText.fontStyle = FontStyles.Bold;
        titleText.alignment = TextAlignmentOptions.Center;
        titleText.color = new Color(1f, 0.92f, 0.62f, 1f);

        RectTransform titleRt = titleText.rectTransform;
        titleRt.anchorMin = new Vector2(0f, 1f);
        titleRt.anchorMax = new Vector2(1f, 1f);
        titleRt.pivot = new Vector2(0.5f, 1f);
        titleRt.sizeDelta = new Vector2(-48f, 70f);
        titleRt.anchoredPosition = new Vector2(0f, -44f);

        // Teks waktu tempuh
        GameObject timeGo = new GameObject("TimeText", typeof(RectTransform));
        timeGo.transform.SetParent(cardGo.transform, false);
        timeText = timeGo.AddComponent<TextMeshProUGUI>();
        if (fontAsset != null) timeText.font = fontAsset;
        timeText.text = "Waktu tempuh tim: 00:00";
        timeText.fontSize = 24f;
        timeText.alignment = TextAlignmentOptions.Center;
        timeText.color = new Color(0.88f, 0.93f, 0.96f, 1f);

        RectTransform timeRt = timeText.rectTransform;
        timeRt.anchorMin = new Vector2(0f, 0.5f);
        timeRt.anchorMax = new Vector2(1f, 0.5f);
        timeRt.pivot = new Vector2(0.5f, 0.5f);
        timeRt.sizeDelta = new Vector2(-48f, 48f);
        timeRt.anchoredPosition = new Vector2(0f, -8f);

        // Tombol Kembali ke Menu Utama
        GameObject btnGo = new GameObject("ReturnMenuButton", typeof(RectTransform), typeof(Image), typeof(Button));
        btnGo.transform.SetParent(cardGo.transform, false);
        RectTransform btnRt = btnGo.GetComponent<RectTransform>();
        btnRt.anchorMin = new Vector2(0.5f, 0f);
        btnRt.anchorMax = new Vector2(0.5f, 0f);
        btnRt.pivot = new Vector2(0.5f, 0f);
        btnRt.sizeDelta = new Vector2(320f, 60f);
        btnRt.anchoredPosition = new Vector2(0f, 40f);

        Image btnImg = btnGo.GetComponent<Image>();
        btnImg.color = new Color(0.20f, 0.68f, 0.52f, 1f);

        returnMenuButton = btnGo.GetComponent<Button>();
        ColorBlock colors = returnMenuButton.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(1.1f, 1.1f, 1.1f, 1f);
        colors.pressedColor = new Color(0.85f, 0.85f, 0.85f, 1f);
        returnMenuButton.colors = colors;

        GameObject btnLabelGo = new GameObject("Label", typeof(RectTransform));
        btnLabelGo.transform.SetParent(btnGo.transform, false);
        TMP_Text btnLabel = btnLabelGo.AddComponent<TextMeshProUGUI>();
        if (fontAsset != null) btnLabel.font = fontAsset;
        btnLabel.text = returnButtonLabel;
        btnLabel.fontSize = 24f;
        btnLabel.fontStyle = FontStyles.Bold;
        btnLabel.alignment = TextAlignmentOptions.Center;
        btnLabel.color = Color.white;

        RectTransform labelRt = btnLabel.rectTransform;
        labelRt.anchorMin = Vector2.zero;
        labelRt.anchorMax = Vector2.one;
        labelRt.offsetMin = Vector2.zero;
        labelRt.offsetMax = Vector2.zero;

        victoryPanel.SetActive(false);
    }
}

