using System;
using UnityEngine;
using UnityEngine.EventSystems;

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
    [Tooltip("Kemiringan/rotasi Z saat hover (derajat). Isi 1.5 untuk tombol biasa, atau -75 untuk ikon Gear.")]
    [SerializeField] private float hoverTiltZ = 1.5f;
    [SerializeField] private float animSpeed = 16f;

    [Header("Efek Tekan Fisik & Putaran Klik (Opsional)")]
    [Tooltip("Geser posisi Y saat ditekan (piksel). Isi -5 pada tombol SOLO/MULTIPLAYER agar terasa tertekan ke bawah.")]
    [SerializeField] private float pressOffsetY = 0f;
    [Tooltip("Putaran ekstra saat diklik (derajat). Isi -90 pada ikon Gear.")]
    [SerializeField] private float clickSpinZ = 0f;

    private RectTransform rectTransform;
    private bool hasCapturedBase = false;
    private Vector2 baseAnchoredPos;
    private Vector3 baseScale;
    private Quaternion baseRotation;

    private bool isHovered = false;
    private bool isPressed = false;
    private float currentScale = 1f;
    private float currentTiltZ = 0f;
    private float currentOffsetY = 0f;
    private float currentSpinZ = 0f;
    private float targetSpinZ = 0f;

    private void Awake()
    {
        rectTransform = GetComponent<RectTransform>();
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
        currentOffsetY = 0f;
        currentSpinZ = 0f;
        targetSpinZ = 0f;

        if (hasCapturedBase)
        {
            if (rectTransform != null) rectTransform.anchoredPosition = baseAnchoredPos;
            transform.localScale = baseScale;
            transform.localRotation = baseRotation;
        }
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        CaptureBaseIfNeeded();
        isHovered = true;
        OnHoverChanged?.Invoke(this, true);
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        isHovered = false;
        OnHoverChanged?.Invoke(this, false);
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        CaptureBaseIfNeeded();
        isPressed = true;
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        isPressed = false;
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (Mathf.Abs(clickSpinZ) > 0.01f)
        {
            CaptureBaseIfNeeded();
            targetSpinZ += clickSpinZ;
        }
    }

    private void Update()
    {
        if (!hasCapturedBase) return;

        float targetScale = isPressed ? pressedScale : (isHovered ? hoverScale : 1f);
        float targetTilt = (isHovered && !isPressed) ? hoverTiltZ : 0f;
        float targetOffsetY = isPressed ? pressOffsetY : 0f;

        if (Mathf.Abs(currentScale - targetScale) < 0.001f &&
            Mathf.Abs(currentTiltZ - targetTilt) < 0.01f &&
            Mathf.Abs(currentOffsetY - targetOffsetY) < 0.05f &&
            Mathf.Abs(currentSpinZ - targetSpinZ) < 0.05f)
        {
            return;
        }

        float step = 1f - Mathf.Exp(-animSpeed * Time.unscaledDeltaTime);
        currentScale = Mathf.Lerp(currentScale, targetScale, step);
        currentTiltZ = Mathf.Lerp(currentTiltZ, targetTilt, step);
        currentOffsetY = Mathf.Lerp(currentOffsetY, targetOffsetY, step);
        currentSpinZ = Mathf.Lerp(currentSpinZ, targetSpinZ, step);

        transform.localScale = baseScale * currentScale;
        transform.localRotation = baseRotation * Quaternion.Euler(0f, 0f, currentTiltZ + currentSpinZ);

        if (rectTransform != null && Mathf.Abs(pressOffsetY) > 0.001f)
        {
            rectTransform.anchoredPosition = baseAnchoredPos + new Vector2(0f, currentOffsetY);
        }
    }
}