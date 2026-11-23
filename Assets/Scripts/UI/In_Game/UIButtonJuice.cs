using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class UIButtonJuice : MonoBehaviour,
    IPointerEnterHandler,
    IPointerExitHandler,
    IPointerDownHandler,
    IPointerUpHandler,
    IPointerClickHandler
{
    public event Action<UIButtonJuice, bool> OnHoverChanged;

    [Header("Pengaturan Skala & Kemiringan")]
    [SerializeField] private float hoverScale = 1.06f;
    [SerializeField] private float pressedScale = 0.94f;
    [SerializeField] private float hoverTiltZ = 1.5f;
    [SerializeField] private float animSpeed = 16f;

    [Header("Efek Tekan Fisik, Geser Panah, & Putaran")]
    [Tooltip("Geser Y saat ditekan (piksel). Isi -4 atau -5 untuk tombol berkaki tebal.")]
    [SerializeField] private float pressOffsetY = 0f;
    [Tooltip("Geser X saat hover (piksel). Isi -4 pada tombol panah kembali (kiri).")]
    [SerializeField] private float hoverOffsetX = 0f;
    [Tooltip("Putaran ekstra saat diklik (derajat). Isi -90 pada ikon Gear.")]
    [SerializeField] private float clickSpinZ = 0f;

    [Header("Unlock Bounce & Breathing Pulse (Opsional)")]
    [Tooltip("Centang pada tombol HOST, JOIN CLUB, dan OK agar redup saat terkunci & membal saat aktif.")]
    [SerializeField] private bool enableUnlockBounce = false;
    [SerializeField, Range(0.2f, 1f)] private float disabledAlpha = 0.52f;
    [Tooltip("Centang pada tombol START di Lobby agar berdenyut halus saat menunggu mulai.")]
    [SerializeField] private bool enableIdlePulse = false;
    [SerializeField] private float pulseAmplitude = 0.03f;
    [SerializeField] private float pulseFrequency = 0.55f;

    private RectTransform rectTransform;
    private Selectable selectable;
    private CanvasGroup canvasGroup;

    private bool hasCapturedBase = false;
    private Vector2 baseAnchoredPos;
    private Vector3 baseScale;
    private Quaternion baseRotation;

    private bool isHovered = false;
    private bool isPressed = false;
    private bool wasInteractable = true;

    private float currentScale = 1f;
    private float currentTiltZ = 0f;
    private float currentOffsetX = 0f;
    private float currentOffsetY = 0f;
    private float currentSpinZ = 0f;
    private float targetSpinZ = 0f;
    private float unlockPunchTime = 0f;

    private bool IsInteractable => selectable == null || selectable.interactable;

    private void Awake()
    {
        rectTransform = GetComponent<RectTransform>();
        selectable = GetComponent<Selectable>();

        if (enableUnlockBounce)
        {
            canvasGroup = GetComponent<CanvasGroup>();
            if (canvasGroup == null) canvasGroup = gameObject.AddComponent<CanvasGroup>();
        }
    }

    private void OnEnable()
    {
        wasInteractable = IsInteractable;
        if (enableUnlockBounce && canvasGroup != null)
        {
            canvasGroup.alpha = wasInteractable ? 1f : disabledAlpha;
        }
    }

    private void CaptureBaseIfNeeded()
    {
        if (hasCapturedBase) return;
        hasCapturedBase = true;

        if (rectTransform != null) baseAnchoredPos = rectTransform.anchoredPosition;
        baseScale = transform.localScale;
        baseRotation = transform.localRotation;
    }

    private void OnDisable()
    {
        if (isHovered)
        {
            isHovered = false;
            OnHoverChanged?.Invoke(this, false);
        }

        isPressed = false;
        currentScale = 1f;
        currentTiltZ = 0f;
        currentOffsetX = 0f;
        currentOffsetY = 0f;
        currentSpinZ = 0f;
        targetSpinZ = 0f;
        unlockPunchTime = 0f;

        if (hasCapturedBase)
        {
            if (rectTransform != null) rectTransform.anchoredPosition = baseAnchoredPos;
            transform.localScale = baseScale;
            transform.localRotation = baseRotation;
        }
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (!IsInteractable) return;
        CaptureBaseIfNeeded();
        isHovered = true;
        OnHoverChanged?.Invoke(this, true);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (isHovered)
        {
            isHovered = false;
            OnHoverChanged?.Invoke(this, false);
        }
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        if (!IsInteractable) return;
        CaptureBaseIfNeeded();
        isPressed = true;
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        isPressed = false;
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (!IsInteractable) return;
        if (Mathf.Abs(clickSpinZ) > 0.01f)
        {
            CaptureBaseIfNeeded();
            targetSpinZ += clickSpinZ;
        }
    }

    private void Update()
    {
        bool interactable = IsInteractable;

        // Deteksi perubahan status terkunci -> terbuka (Unlock Bounce)
        if (interactable != wasInteractable)
        {
            wasInteractable = interactable;
            if (!interactable)
            {
                if (isHovered)
                {
                    isHovered = false;
                    OnHoverChanged?.Invoke(this, false);
                }
                isPressed = false;
            }
            else if (enableUnlockBounce)
            {
                CaptureBaseIfNeeded();
                unlockPunchTime = 0.24f;
            }
        }

        if (enableUnlockBounce && canvasGroup != null)
        {
            float targetAlpha = interactable ? 1f : disabledAlpha;
            if (Mathf.Abs(canvasGroup.alpha - targetAlpha) > 0.005f)
            {
                float alphaStep = 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime);
                canvasGroup.alpha = Mathf.Lerp(canvasGroup.alpha, targetAlpha, alphaStep);
            }
            else
            {
                canvasGroup.alpha = targetAlpha;
            }
        }

        if (enableIdlePulse && interactable)
        {
            CaptureBaseIfNeeded();
        }

        if (!hasCapturedBase) return;

        float dt = Time.unscaledDeltaTime;

        // Hitung pantulan sesaat ketika tombol baru terbuka
        float unlockBump = 0f;
        if (unlockPunchTime > 0f)
        {
            unlockPunchTime = Mathf.Max(0f, unlockPunchTime - dt);
            float norm = unlockPunchTime / 0.24f;
            unlockBump = Mathf.Sin(norm * Mathf.PI) * 0.08f;
        }

        // Hitung napas halus (breathing pulse) saat tombol idle
        float pulseOffset = 0f;
        if (enableIdlePulse && interactable && !isHovered && !isPressed)
        {
            pulseOffset = (Mathf.Sin(Time.unscaledTime * pulseFrequency * Mathf.PI * 2f) * 0.5f + 0.5f) * pulseAmplitude;
        }

        float targetScale = (isPressed ? pressedScale : (isHovered ? hoverScale : 1f)) + unlockBump + pulseOffset;
        float targetTilt = (isHovered && !isPressed) ? hoverTiltZ : 0f;
        float targetOffsetX = (isHovered && !isPressed) ? hoverOffsetX : 0f;
        float targetOffsetY = isPressed ? pressOffsetY : 0f;

        bool isAnimating =
            unlockPunchTime > 0f ||
            (enableIdlePulse && interactable) ||
            Mathf.Abs(currentScale - targetScale) > 0.0008f ||
            Mathf.Abs(currentTiltZ - targetTilt) > 0.01f ||
            Mathf.Abs(currentOffsetX - targetOffsetX) > 0.05f ||
            Mathf.Abs(currentOffsetY - targetOffsetY) > 0.05f ||
            Mathf.Abs(currentSpinZ - targetSpinZ) > 0.05f;

        if (!isAnimating) return;

        float step = 1f - Mathf.Exp(-animSpeed * dt);
        currentScale = Mathf.Lerp(currentScale, targetScale, step);
        currentTiltZ = Mathf.Lerp(currentTiltZ, targetTilt, step);
        currentOffsetX = Mathf.Lerp(currentOffsetX, targetOffsetX, step);
        currentOffsetY = Mathf.Lerp(currentOffsetY, targetOffsetY, step);
        currentSpinZ = Mathf.Lerp(currentSpinZ, targetSpinZ, step);

        transform.localScale = baseScale * currentScale;
        transform.localRotation = baseRotation * Quaternion.Euler(0f, 0f, currentTiltZ + currentSpinZ);

        if (rectTransform != null && (Mathf.Abs(pressOffsetY) > 0.001f || Mathf.Abs(hoverOffsetX) > 0.001f))
        {
            rectTransform.anchoredPosition = baseAnchoredPos + new Vector2(currentOffsetX, currentOffsetY);
        }
    }
}