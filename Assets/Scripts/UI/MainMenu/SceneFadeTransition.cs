using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class SceneFadeTransition : MonoBehaviour
{
    public static SceneFadeTransition Instance { get; private set; }

    [Header("Pengaturan Transisi")]
    [SerializeField] private Color fadeColor = new Color(0.08f, 0.11f, 0.15f, 1f); // Gelap kebiruan halus
    [SerializeField] private float fadeOutDuration = 0.28f;
    [SerializeField] private float fadeInDuration = 0.35f;

    private CanvasGroup fadeCanvasGroup;
    private Image fadeImage;
    private Coroutine fadeCoroutine;
    private bool isTransitioning = false;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        CreateFadeOverlay();
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += HandleSceneLoaded;
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= HandleSceneLoaded;
    }

    private void CreateFadeOverlay()
    {
        GameObject canvasObj = new GameObject("SceneFadeCanvas");
        canvasObj.transform.SetParent(transform, false);

        Canvas canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 999; // Selalu berada di lapisan paling atas

        fadeCanvasGroup = canvasObj.AddComponent<CanvasGroup>();
        fadeCanvasGroup.alpha = 0f;
        fadeCanvasGroup.interactable = false;
        fadeCanvasGroup.blocksRaycasts = false;

        GameObject imgObj = new GameObject("FadeImage", typeof(RectTransform), typeof(Image));
        imgObj.transform.SetParent(canvasObj.transform, false);

        RectTransform rt = imgObj.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;

        fadeImage = imgObj.GetComponent<Image>();
        fadeImage.color = fadeColor;
        fadeImage.raycastTarget = true;
    }

    /// <summary>
    /// Untuk perpindahan scene lokal (Solo atau kembali ke Main Menu).
    /// Bisa dipanggil langsung dari OnClick Button di Inspector.
    /// </summary>
    public void TransitionToScene(string sceneName)
    {
        if (isTransitioning || string.IsNullOrWhiteSpace(sceneName)) return;
        StartCoroutine(FadeAndLoadRoutine(sceneName));
    }

    /// <summary>
    /// Untuk integrasi Multiplayer / Netcode (misal memanggil StartHost / LoadScene lewat LobbyManager).
    /// </summary>
    public void FadeOutAndInvoke(Action onScreenCovered)
    {
        if (isTransitioning) return;
        StartCoroutine(FadeOutThenInvokeRoutine(onScreenCovered));
    }

    /// <summary>
    /// Pengaman: panggil jika koneksi multiplayer gagal agar layar kembali terang.
    /// </summary>
    public void CancelAndFadeIn()
    {
        isTransitioning = false;
        if (fadeCoroutine != null) StopCoroutine(fadeCoroutine);
        fadeCoroutine = StartCoroutine(FadeAlphaRoutine(0f, fadeInDuration, false));
    }

    private IEnumerator FadeAndLoadRoutine(string sceneName)
    {
        isTransitioning = true;
        yield return FadeAlphaRoutine(1f, fadeOutDuration, true);

        AsyncOperation asyncLoad = SceneManager.LoadSceneAsync(sceneName);
        while (asyncLoad != null && !asyncLoad.isDone)
        {
            yield return null;
        }
    }

    private IEnumerator FadeOutThenInvokeRoutine(Action onScreenCovered)
    {
        isTransitioning = true;
        yield return FadeAlphaRoutine(1f, fadeOutDuration, true);
        onScreenCovered?.Invoke();
    }

    private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (fadeCanvasGroup == null) return;

        // Begitu scene baru selesai dimuat, otomatis buka layar perlahan
        if (fadeCanvasGroup.alpha > 0.01f || isTransitioning)
        {
            isTransitioning = false;
            if (fadeCoroutine != null) StopCoroutine(fadeCoroutine);
            fadeCoroutine = StartCoroutine(FadeAlphaRoutine(0f, fadeInDuration, false));
        }
    }

    private IEnumerator FadeAlphaRoutine(float targetAlpha, float duration, bool blockInput)
    {
        fadeCanvasGroup.blocksRaycasts = blockInput;

        float startAlpha = fadeCanvasGroup.alpha;
        float elapsed = 0f;
        float dur = Mathf.Max(0.01f, duration);

        while (elapsed < dur)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / dur);
            float ease = t * t * (3f - 2f * t); // SmoothStep
            fadeCanvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, ease);
            yield return null;
        }

        fadeCanvasGroup.alpha = targetAlpha;
        fadeCanvasGroup.blocksRaycasts = targetAlpha > 0.01f;
    }
}