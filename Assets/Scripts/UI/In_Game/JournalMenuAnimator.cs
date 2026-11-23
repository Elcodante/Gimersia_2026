using System.Collections;
using UnityEngine;
using UnityEngine.UI;

public class JournalMenuAnimator : MonoBehaviour
{
    [Header("Referensi Objek UI")]
    [Tooltip("Wadah utama (MenuOverlayRoot) yang membungkus Backdrop dan BookPanel.")]
    [SerializeField] private GameObject menuRoot;
    [Tooltip("RectTransform objek buku besar (yang berisi teks, slider, dan tombol HOME).")]
    [SerializeField] private RectTransform bookPanel;
    [Tooltip("CanvasGroup pada BookPanel untuk fade & kunci klik saat animasi berjalan.")]
    [SerializeField] private CanvasGroup bookCanvasGroup;
    [Tooltip("UI Image hitam full-screen di belakang buku.")]
    [SerializeField] private Image backdropDimmer;

    [Header("Pengaturan Durasi (Gesit untuk Multiplayer)")]
    [SerializeField] private float openDuration = 0.25f;
    [SerializeField] private float closeDuration = 0.16f;

    [Header("Pengaturan Gerak Angkat Buku")]
    [SerializeField] private float startOffsetY = -180f;     // Mulai 180px di bawah posisi normal
    [SerializeField] private float startTiltZ = -5f;         // Miring tipis saat diangkat (derajat)
    [SerializeField] private float startScale = 0.9f;        // Skala awal sebelum membal ke 1.0
    [SerializeField] private float bounceOvershoot = 1.35f;  // Semakin besar = pantulan akhir lebih kuat

    [Header("Pengaturan Backdrop Dimmer")]
    [SerializeField, Range(0f, 1f)] private float backdropTargetAlpha = 0.45f;

    [Header("Pengaturan Awal (Untuk Main Menu)")]
    [Tooltip("Centang jika panel ini dipakai di Main Menu agar otomatis tertutup saat scene mulai.")]
    [SerializeField] private bool startClosed = false;

    private bool isOpenState = false;

    private bool isInitialized = false;
    private Vector2 restAnchoredPos;
    private Vector3 restScale;
    private Color backdropBaseColor = Color.black;

    // State animasi saat ini agar aman jika pemain menekan Esc/Tab berulang kali (spam-proof)
    private float currentOffsetY;
    private float currentTiltZ;
    private float currentScaleMultiplier;
    private float currentBookAlpha;
    private float currentBackdropAlpha;

    private Coroutine animCoroutine;

    private void Awake()
    {
        EnsureInitialized();
        if (startClosed)
        {
            SetClosedImmediate();
        }
    }
    public void Toggle()
    {
        if (isOpenState) PlayClose();
        else PlayOpen();
    }
    private void EnsureInitialized()
    {
        if (isInitialized) return;
        isInitialized = true;

        if (bookPanel != null)
        {
            restAnchoredPos = bookPanel.anchoredPosition;
            restScale = bookPanel.localScale;
        }

        if (backdropDimmer != null)
        {
            backdropBaseColor = backdropDimmer.color;
        }
    }

    public void SetClosedImmediate()
    {
        EnsureInitialized();

        if (animCoroutine != null)
        {
            StopCoroutine(animCoroutine);
            animCoroutine = null;
        }

        currentOffsetY = startOffsetY;
        currentTiltZ = startTiltZ;
        currentScaleMultiplier = startScale;
        currentBookAlpha = 0f;
        currentBackdropAlpha = 0f;

        ApplyVisualState();
        SetInteractable(false);

        if (menuRoot != null) menuRoot.SetActive(false);
        isOpenState = false;
    }

    public void PlayOpen()
    {
        EnsureInitialized();

        if (menuRoot != null && !menuRoot.activeSelf)
        {
            menuRoot.SetActive(true);
        }

        if (animCoroutine != null) StopCoroutine(animCoroutine);
        animCoroutine = StartCoroutine(AnimateRoutine(true));
        isOpenState = true;
    }

    public void PlayClose()
    {
        EnsureInitialized();

        if (menuRoot == null || !menuRoot.activeSelf) return;

        if (animCoroutine != null) StopCoroutine(animCoroutine);
        animCoroutine = StartCoroutine(AnimateRoutine(false));
        isOpenState = false;
    }

    private IEnumerator AnimateRoutine(bool opening)
    {
        // Kunci interaksi tombol selama buku sedang menutup agar tidak terklik tanpa sengaja
        SetInteractable(opening);

        float fromOffsetY = currentOffsetY;
        float fromTiltZ = currentTiltZ;
        float fromScale = currentScaleMultiplier;
        float fromBookAlpha = currentBookAlpha;
        float fromBackdropAlpha = currentBackdropAlpha;

        float toOffsetY = opening ? 0f : startOffsetY;
        float toTiltZ = opening ? 0f : startTiltZ;
        float toScale = opening ? 1f : startScale;
        float toBookAlpha = opening ? 1f : 0f;
        float toBackdropAlpha = opening ? backdropTargetAlpha : 0f;

        float duration = opening ? openDuration : closeDuration;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);

            // Saat membuka: kurva EaseOutBack memberi efek membal lembut; saat menutup: EaseInQuad turun cepat tanpa membal
            float motionEase = opening ? EaseOutBack(t, bounceOvershoot) : EaseInQuad(t);
            float alphaEase = opening ? EaseOutQuad(t) : EaseInQuad(t);

            currentOffsetY = Mathf.LerpUnclamped(fromOffsetY, toOffsetY, motionEase);
            currentTiltZ = Mathf.LerpUnclamped(fromTiltZ, toTiltZ, motionEase);
            currentScaleMultiplier = Mathf.LerpUnclamped(fromScale, toScale, motionEase);

            currentBookAlpha = Mathf.Lerp(fromBookAlpha, toBookAlpha, alphaEase);
            currentBackdropAlpha = Mathf.Lerp(fromBackdropAlpha, toBackdropAlpha, alphaEase);

            ApplyVisualState();
            yield return null;
        }

        currentOffsetY = toOffsetY;
        currentTiltZ = toTiltZ;
        currentScaleMultiplier = toScale;
        currentBookAlpha = toBookAlpha;
        currentBackdropAlpha = toBackdropAlpha;

        ApplyVisualState();

        if (!opening && menuRoot != null)
        {
            menuRoot.SetActive(false);
        }

        animCoroutine = null;
    }

    private void ApplyVisualState()
    {
        if (bookPanel != null)
        {
            bookPanel.anchoredPosition = restAnchoredPos + new Vector2(0f, currentOffsetY);
            bookPanel.localRotation = Quaternion.Euler(0f, 0f, currentTiltZ);
            bookPanel.localScale = restScale * currentScaleMultiplier;
        }

        if (bookCanvasGroup != null)
        {
            bookCanvasGroup.alpha = currentBookAlpha;
        }

        if (backdropDimmer != null)
        {
            Color c = backdropBaseColor;
            c.a = currentBackdropAlpha;
            backdropDimmer.color = c;
        }
    }

    private void SetInteractable(bool state)
    {
        if (bookCanvasGroup != null)
        {
            bookCanvasGroup.interactable = state;
            bookCanvasGroup.blocksRaycasts = state;
        }
    }

    private static float EaseOutBack(float t, float overshoot)
    {
        float c3 = overshoot + 1f;
        float inv = t - 1f;
        return 1f + c3 * inv * inv * inv + overshoot * inv * inv;
    }

    private static float EaseOutQuad(float t) => 1f - (1f - t) * (1f - t);
    private static float EaseInQuad(float t) => t * t;
}