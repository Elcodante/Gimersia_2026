using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Lightweight runtime loading overlay. Created automatically by RiverLevelBuilder,
/// so no Canvas setup is required in the scene.
/// </summary>
public sealed class WorldLoadingUI : MonoBehaviour
{
    private static WorldLoadingUI instance;

    private CanvasGroup canvasGroup;
    private TMP_Text titleText;
    private TMP_Text statusText;
    private TMP_Text percentText;
    private Image progressFill;
    private Button returnToMenuButton;
    private Coroutine fadeRoutine;

    public static WorldLoadingUI Show(string status)
    {
        if (instance == null)
        {
            var root = new GameObject("WorldLoadingOverlay");
            DontDestroyOnLoad(root);
            instance = root.AddComponent<WorldLoadingUI>();
            instance.BuildUI();
        }

        instance.gameObject.SetActive(true);
        instance.canvasGroup.alpha = 1f;
        instance.canvasGroup.blocksRaycasts = true;
        instance.canvasGroup.interactable = false;
        if (instance.returnToMenuButton != null) instance.returnToMenuButton.gameObject.SetActive(false);
        if (instance.titleText != null) instance.titleText.text = "PREPARING RIVER";
        instance.SetProgress(0f, status);
        return instance;
    }

    public void SetProgress(float progress, string status)
    {
        progress = Mathf.Clamp01(progress);
        if (progressFill != null) progressFill.fillAmount = progress;
        if (percentText != null) percentText.text = $"{Mathf.RoundToInt(progress * 100f)}%";
        if (statusText != null) statusText.text = status;
    }

    public void ShowError(string message)
    {
        if (statusText != null) statusText.text = message;
        if (percentText != null) percentText.text = "!";
        if (progressFill != null) progressFill.fillAmount = 0f;
        if (titleText != null) titleText.text = "WORLD GENERATION FAILED";
        canvasGroup.alpha = 1f;
        // An explicit return button prevents a failed validation from trapping the player.
        canvasGroup.blocksRaycasts = true;
        canvasGroup.interactable = true;
        if (returnToMenuButton != null) returnToMenuButton.gameObject.SetActive(true);
    }

    public void Hide(float delay = 0.35f)
    {
        if (fadeRoutine != null) StopCoroutine(fadeRoutine);
        fadeRoutine = StartCoroutine(HideRoutine(delay));
    }

    private IEnumerator HideRoutine(float delay)
    {
        if (delay > 0f) yield return new WaitForSecondsRealtime(delay);

        float elapsed = 0f;
        const float duration = 0.28f;
        float startAlpha = canvasGroup.alpha;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            canvasGroup.alpha = Mathf.Lerp(startAlpha, 0f, elapsed / duration);
            yield return null;
        }

        canvasGroup.alpha = 0f;
        canvasGroup.blocksRaycasts = false;
        fadeRoutine = null;
        gameObject.SetActive(false);
    }

    private void BuildUI()
    {
        Canvas canvas = gameObject.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;

        CanvasScaler scaler = gameObject.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;
        gameObject.AddComponent<GraphicRaycaster>();

        canvasGroup = gameObject.AddComponent<CanvasGroup>();
        canvasGroup.blocksRaycasts = true;
        canvasGroup.interactable = false;

        GameObject dim = CreateRect("DimBackground", transform);
        Stretch(dim.GetComponent<RectTransform>());
        Image dimImage = dim.AddComponent<Image>();
        dimImage.color = new Color(0.025f, 0.045f, 0.065f, 0.92f);
        dimImage.raycastTarget = false;

        GameObject panel = CreateRect("LoadingPanel", transform);
        RectTransform panelRect = panel.GetComponent<RectTransform>();
        panelRect.anchorMin = new Vector2(0.5f, 0.5f);
        panelRect.anchorMax = new Vector2(0.5f, 0.5f);
        panelRect.sizeDelta = new Vector2(720f, 320f);
        panelRect.anchoredPosition = Vector2.zero;
        Image panelImage = panel.AddComponent<Image>();
        panelImage.color = new Color(0.055f, 0.085f, 0.11f, 0.96f);
        panelImage.raycastTarget = false;

        titleText = CreateText("Title", panel.transform, "PREPARING RIVER", 38f, FontStyles.Bold);
        AnchorText(titleText.rectTransform, new Vector2(0.08f, 0.76f), new Vector2(0.92f, 0.94f));
        titleText.alignment = TextAlignmentOptions.Center;
        titleText.color = new Color(0.92f, 0.96f, 1f, 1f);

        statusText = CreateText("Status", panel.transform, "Generating river...", 25f, FontStyles.Normal);
        AnchorText(statusText.rectTransform, new Vector2(0.08f, 0.56f), new Vector2(0.92f, 0.72f));
        statusText.alignment = TextAlignmentOptions.Center;
        statusText.color = new Color(0.72f, 0.82f, 0.88f, 1f);

        GameObject barBackground = CreateRect("ProgressBackground", panel.transform);
        RectTransform barRect = barBackground.GetComponent<RectTransform>();
        barRect.anchorMin = new Vector2(0.12f, 0.42f);
        barRect.anchorMax = new Vector2(0.88f, 0.50f);
        barRect.offsetMin = Vector2.zero;
        barRect.offsetMax = Vector2.zero;
        Image barImage = barBackground.AddComponent<Image>();
        barImage.color = new Color(0.15f, 0.2f, 0.23f, 1f);
        barImage.raycastTarget = false;

        GameObject fill = CreateRect("ProgressFill", barBackground.transform);
        Stretch(fill.GetComponent<RectTransform>());
        progressFill = fill.AddComponent<Image>();
        progressFill.color = new Color(0.26f, 0.78f, 0.58f, 1f);
        progressFill.type = Image.Type.Filled;
        progressFill.fillMethod = Image.FillMethod.Horizontal;
        progressFill.fillOrigin = (int)Image.OriginHorizontal.Left;
        progressFill.fillAmount = 0f;
        progressFill.raycastTarget = false;

        percentText = CreateText("Percent", panel.transform, "0%", 22f, FontStyles.Bold);
        AnchorText(percentText.rectTransform, new Vector2(0.35f, 0.29f), new Vector2(0.65f, 0.39f));
        percentText.alignment = TextAlignmentOptions.Center;
        percentText.color = Color.white;

        GameObject buttonObject = CreateRect("ReturnToMenuButton", panel.transform);
        RectTransform buttonRect = buttonObject.GetComponent<RectTransform>();
        buttonRect.anchorMin = new Vector2(0.32f, 0.06f);
        buttonRect.anchorMax = new Vector2(0.68f, 0.22f);
        buttonRect.offsetMin = Vector2.zero;
        buttonRect.offsetMax = Vector2.zero;
        Image buttonImage = buttonObject.AddComponent<Image>();
        buttonImage.color = new Color(0.18f, 0.48f, 0.40f, 1f);
        returnToMenuButton = buttonObject.AddComponent<Button>();
        returnToMenuButton.targetGraphic = buttonImage;
        returnToMenuButton.onClick.AddListener(ReturnToMenuFromError);

        TMP_Text buttonText = CreateText("ButtonLabel", buttonObject.transform, "RETURN TO MENU", 19f, FontStyles.Bold);
        AnchorText(buttonText.rectTransform, Vector2.zero, Vector2.one);
        buttonText.alignment = TextAlignmentOptions.Center;
        buttonText.color = Color.white;
        buttonObject.SetActive(false);
    }

    private void ReturnToMenuFromError()
    {
        // Hide the persistent overlay before the menu scene loads, otherwise it would
        // remain above the menu because this Canvas uses DontDestroyOnLoad.
        gameObject.SetActive(false);

        ReturnToMenuController returnController = FindFirstObjectByType<ReturnToMenuController>();
        if (returnController != null)
        {
            returnController.ReturnToMainMenu();
        }
        else
        {
            Debug.LogWarning("[WorldLoadingUI] ReturnToMenuController tidak ditemukan. Menggunakan fallback langsung ke Main_Menu.");
            if (Unity.Netcode.NetworkManager.Singleton != null && Unity.Netcode.NetworkManager.Singleton.IsListening)
            {
                Unity.Netcode.NetworkManager.Singleton.Shutdown();
            }
            NetworkPlayerData.ConnectedPlayers.Clear();
            UnityEngine.SceneManagement.SceneManager.LoadScene("Main_Menu");
        }
    }

    private static GameObject CreateRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return go;
    }

    private static TMP_Text CreateText(string name, Transform parent, string value, float size, FontStyles style)
    {
        GameObject go = CreateRect(name, parent);
        TMP_Text text = go.AddComponent<TextMeshProUGUI>();
        text.text = value;
        text.fontSize = size;
        text.fontStyle = style;
        text.enableWordWrapping = true;
        text.raycastTarget = false;
        return text;
    }

    private static void Stretch(RectTransform rect)
    {
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }

    private static void AnchorText(RectTransform rect, Vector2 min, Vector2 max)
    {
        rect.anchorMin = min;
        rect.anchorMax = max;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
    }
}
