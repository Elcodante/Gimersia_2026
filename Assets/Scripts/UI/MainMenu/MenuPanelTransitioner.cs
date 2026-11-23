using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MenuPanelTransitioner : MonoBehaviour
{
    public enum TransitionStyle
    {
        Fade,
        RiseTilt,       // Main Menu -> Selection (naik dari bawah + miring tipis)
        SlideFromRight, // Selection -> Join (geser kartu ke kiri)
        SlideFromLeft,  // Join -> Selection (geser kartu kembali ke kanan)
        PlantSign,      // Masuk ke Panel_Host (papan kayu ditancapkan dari bawah)
        DropOut         // Keluar dari Panel_Host / Selection kembali ke menu sebelumnya
    }

    [Header("Backdrop Cokelat Bersama")]
    [Tooltip("Objek MultiplayerBackdrop (UI Image cokelat full-screen di belakang panel multiplayer).")]
    [SerializeField] private GameObject sharedBackdrop;
    [SerializeField] private float backdropFadeDuration = 0.18f;

    [Header("Durasi & Jarak Gerak Panel")]
    [SerializeField] private float exitDuration = 0.14f;
    [SerializeField] private float enterDuration = 0.24f;
    [SerializeField] private float slideDistanceX = 90f;
    [SerializeField] private float riseDistanceY = 120f;
    [SerializeField] private float plantSignDistanceY = 220f;
    [SerializeField] private float riseTiltZ = -3f;

    private readonly Dictionary<RectTransform, Vector2> cachedRestPos = new Dictionary<RectTransform, Vector2>();
    private readonly Dictionary<RectTransform, Vector3> cachedRestScale = new Dictionary<RectTransform, Vector3>();

    private GameObject currentPanel;
    private Coroutine transitionCoroutine;
    private Coroutine backdropCoroutine;
    private CanvasGroup backdropCanvasGroup;

    private void Awake()
    {
        if (sharedBackdrop != null)
        {
            backdropCanvasGroup = GetOrAddCanvasGroup(sharedBackdrop);
        }
    }

    /// <summary>
    /// Menampilkan panel pertama secara instan saat Start() tanpa animasi.
    /// </summary>
    public void SetInitialPanel(GameObject initialPanel, GameObject[] allPanels, bool showBackdrop)
    {
        if (allPanels != null)
        {
            for (int i = 0; i < allPanels.Length; i++)
            {
                if (allPanels[i] == null) continue;
                CacheTransform(allPanels[i]);
                bool isTarget = allPanels[i] == initialPanel;
                allPanels[i].SetActive(isTarget);

                CanvasGroup cg = GetOrAddCanvasGroup(allPanels[i]);
                cg.alpha = isTarget ? 1f : 0f;
                cg.interactable = isTarget;
                cg.blocksRaycasts = isTarget;
            }
        }

        currentPanel = initialPanel;
        SetBackdropImmediate(showBackdrop);
    }

    /// <summary>
    /// Berpindah dari panel aktif ke panel tujuan dengan animasi spasial.
    /// </summary>
    public void TransitionTo(GameObject targetPanel, TransitionStyle style, bool showBackdrop)
    {
        if (targetPanel == null || targetPanel == currentPanel) return;

        CacheTransform(targetPanel);
        if (currentPanel != null) CacheTransform(currentPanel);

        UpdateBackdrop(showBackdrop);

        if (transitionCoroutine != null)
        {
            StopCoroutine(transitionCoroutine);
            // Pastikan panel yang sedang keluar sebelumnya langsung bersih jika transisi dipotong
            ResetPanelVisual(currentPanel, false);
        }

        transitionCoroutine = StartCoroutine(TransitionRoutine(currentPanel, targetPanel, style));
    }

    private IEnumerator TransitionRoutine(GameObject fromPanel, GameObject toPanel, TransitionStyle style)
    {
        // 1. Keluarkan panel lama
        if (fromPanel != null && fromPanel.activeSelf)
        {
            CanvasGroup fromCg = GetOrAddCanvasGroup(fromPanel);
            RectTransform fromRt = fromPanel.GetComponent<RectTransform>();
            fromCg.interactable = false;
            fromCg.blocksRaycasts = false;

            Vector2 exitOffset = GetExitOffset(style);
            Vector2 startPos = fromRt.anchoredPosition;
            Vector2 endPos = cachedRestPos[fromRt] + exitOffset;
            float startAlpha = fromCg.alpha;

            float elapsed = 0f;
            float dur = Mathf.Max(0.01f, exitDuration);

            while (elapsed < dur)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / dur);
                float ease = t * t; // EaseInQuad

                fromRt.anchoredPosition = Vector2.LerpUnclamped(startPos, endPos, ease);
                fromCg.alpha = Mathf.Lerp(startAlpha, 0f, ease);
                yield return null;
            }

            ResetPanelVisual(fromPanel, false);
        }

        // 2. Masuk panel baru
        currentPanel = toPanel;
        toPanel.SetActive(true);

        CanvasGroup toCg = GetOrAddCanvasGroup(toPanel);
        RectTransform toRt = toPanel.GetComponent<RectTransform>();
        toCg.interactable = false;
        toCg.blocksRaycasts = false;

        Vector2 restPos = cachedRestPos[toRt];
        Vector3 restScale = cachedRestScale[toRt];

        Vector2 enterOffset = GetEnterOffset(style);
        float enterTilt = (style == TransitionStyle.RiseTilt) ? riseTiltZ : 0f;
        float startScaleMult = (style == TransitionStyle.Fade) ? 0.98f : 0.94f;
        float overshoot = (style == TransitionStyle.PlantSign) ? 1.55f : 1.15f;
        bool useBounce = style == TransitionStyle.RiseTilt || style == TransitionStyle.PlantSign;

        toRt.anchoredPosition = restPos + enterOffset;
        toRt.localRotation = Quaternion.Euler(0f, 0f, enterTilt);
        toRt.localScale = restScale * startScaleMult;
        toCg.alpha = 0f;

        float inElapsed = 0f;
        float inDur = Mathf.Max(0.01f, enterDuration);

        while (inElapsed < inDur)
        {
            inElapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(inElapsed / inDur);

            float motionEase = useBounce ? EaseOutBack(t, overshoot) : EaseOutCubic(t);
            float alphaEase = EaseOutCubic(t);

            toRt.anchoredPosition = Vector2.LerpUnclamped(restPos + enterOffset, restPos, motionEase);
            toRt.localRotation = Quaternion.Euler(0f, 0f, Mathf.LerpUnclamped(enterTilt, 0f, motionEase));
            toRt.localScale = Vector3.LerpUnclamped(restScale * startScaleMult, restScale, motionEase);
            toCg.alpha = Mathf.Lerp(0f, 1f, alphaEase);

            yield return null;
        }

        ResetPanelVisual(toPanel, true);
        transitionCoroutine = null;
    }

    private Vector2 GetExitOffset(TransitionStyle style)
    {
        return style switch
        {
            TransitionStyle.SlideFromRight => new Vector2(-slideDistanceX * 0.7f, 0f),
            TransitionStyle.SlideFromLeft => new Vector2(slideDistanceX * 0.7f, 0f),
            TransitionStyle.DropOut => new Vector2(0f, -riseDistanceY * 0.5f),
            TransitionStyle.PlantSign => new Vector2(0f, -40f),
            _ => Vector2.zero
        };
    }

    private Vector2 GetEnterOffset(TransitionStyle style)
    {
        return style switch
        {
            TransitionStyle.RiseTilt => new Vector2(0f, -riseDistanceY),
            TransitionStyle.SlideFromRight => new Vector2(slideDistanceX, 0f),
            TransitionStyle.SlideFromLeft => new Vector2(-slideDistanceX, 0f),
            TransitionStyle.PlantSign => new Vector2(0f, -plantSignDistanceY),
            TransitionStyle.DropOut => new Vector2(0f, 35f),
            _ => Vector2.zero
        };
    }

    private void UpdateBackdrop(bool show)
    {
        if (sharedBackdrop == null) return;
        if (backdropCoroutine != null) StopCoroutine(backdropCoroutine);
        backdropCoroutine = StartCoroutine(FadeBackdropRoutine(show));
    }

    private void SetBackdropImmediate(bool show)
    {
        if (sharedBackdrop == null) return;
        if (backdropCanvasGroup == null) backdropCanvasGroup = GetOrAddCanvasGroup(sharedBackdrop);

        backdropCanvasGroup.alpha = show ? 1f : 0f;
        backdropCanvasGroup.blocksRaycasts = show;
        sharedBackdrop.SetActive(show);
    }

    private IEnumerator FadeBackdropRoutine(bool show)
    {
        if (backdropCanvasGroup == null) backdropCanvasGroup = GetOrAddCanvasGroup(sharedBackdrop);

        if (show && !sharedBackdrop.activeSelf)
        {
            backdropCanvasGroup.alpha = 0f;
            sharedBackdrop.SetActive(true);
        }

        backdropCanvasGroup.blocksRaycasts = show;
        float startAlpha = backdropCanvasGroup.alpha;
        float targetAlpha = show ? 1f : 0f;

        if (Mathf.Approximately(startAlpha, targetAlpha))
        {
            if (!show) sharedBackdrop.SetActive(false);
            yield break;
        }

        float elapsed = 0f;
        float dur = Mathf.Max(0.01f, backdropFadeDuration);

        while (elapsed < dur)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / dur);
            backdropCanvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, t * t * (3f - 2f * t));
            yield return null;
        }

        backdropCanvasGroup.alpha = targetAlpha;
        if (!show) sharedBackdrop.SetActive(false);
        backdropCoroutine = null;
    }

    private void CacheTransform(GameObject panel)
    {
        if (panel == null) return;
        RectTransform rt = panel.GetComponent<RectTransform>();
        if (rt != null && !cachedRestPos.ContainsKey(rt))
        {
            cachedRestPos[rt] = rt.anchoredPosition;
            cachedRestScale[rt] = rt.localScale;
        }
    }

    private void ResetPanelVisual(GameObject panel, bool active)
    {
        if (panel == null) return;
        RectTransform rt = panel.GetComponent<RectTransform>();
        CanvasGroup cg = GetOrAddCanvasGroup(panel);

        if (rt != null && cachedRestPos.ContainsKey(rt))
        {
            rt.anchoredPosition = cachedRestPos[rt];
            rt.localScale = cachedRestScale[rt];
            rt.localRotation = Quaternion.identity;
        }

        cg.alpha = active ? 1f : 0f;
        cg.interactable = active;
        cg.blocksRaycasts = active;
        panel.SetActive(active);
    }

    private static CanvasGroup GetOrAddCanvasGroup(GameObject go)
    {
        CanvasGroup cg = go.GetComponent<CanvasGroup>();
        return cg != null ? cg : go.AddComponent<CanvasGroup>();
    }

    private static float EaseOutBack(float t, float overshoot)
    {
        float c3 = overshoot + 1f;
        float inv = t - 1f;
        return 1f + c3 * inv * inv * inv + overshoot * inv * inv;
    }

    private static float EaseOutCubic(float t)
    {
        float inv = 1f - t;
        return 1f - inv * inv * inv;
    }
}