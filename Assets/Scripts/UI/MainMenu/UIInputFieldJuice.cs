using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(TMP_InputField))]
public class UIInputFieldJuice : MonoBehaviour
{
    [Header("Focus Pop")]
    [SerializeField] private float focusScale = 1.035f;
    [SerializeField] private float scaleSpeed = 16f;

    [Header("Error Shake (Saat Gagal Join)")]
    [SerializeField] private float shakeDuration = 0.28f;
    [SerializeField] private float shakeMagnitude = 12f;
    [SerializeField] private Color errorFlashColor = new Color(1f, 0.45f, 0.45f, 1f);

    private TMP_InputField inputField;
    private RectTransform rectTransform;
    private Graphic targetGraphic;

    private bool hasCapturedBase = false;
    private Vector2 baseAnchoredPos;
    private Vector3 baseScale;
    private Color baseColor = Color.white;

    private bool isFocused = false;
    private float currentScale = 1f;
    private Coroutine shakeCoroutine;

    private void Awake()
    {
        inputField = GetComponent<TMP_InputField>();
        rectTransform = GetComponent<RectTransform>();
        targetGraphic = inputField.targetGraphic;
        if (targetGraphic != null) baseColor = targetGraphic.color;

        inputField.onSelect.AddListener(HandleSelect);
        inputField.onDeselect.AddListener(HandleDeselect);
    }

    private void OnDestroy()
    {
        if (inputField != null)
        {
            inputField.onSelect.RemoveListener(HandleSelect);
            inputField.onDeselect.RemoveListener(HandleDeselect);
        }
    }

    private void CaptureBaseIfNeeded()
    {
        if (hasCapturedBase) return;
        hasCapturedBase = true;
        baseAnchoredPos = rectTransform.anchoredPosition;
        baseScale = transform.localScale;
    }

    private void OnDisable()
    {
        isFocused = false;
        currentScale = 1f;

        if (shakeCoroutine != null)
        {
            StopCoroutine(shakeCoroutine);
            shakeCoroutine = null;
        }

        if (hasCapturedBase)
        {
            rectTransform.anchoredPosition = baseAnchoredPos;
            transform.localScale = baseScale;
        }

        if (targetGraphic != null) targetGraphic.color = baseColor;
    }

    private void HandleSelect(string _)
    {
        CaptureBaseIfNeeded();
        isFocused = true;
    }

    private void HandleDeselect(string _)
    {
        isFocused = false;
    }

    public void PlayErrorShake()
    {
        CaptureBaseIfNeeded();
        if (!gameObject.activeInHierarchy) return;

        if (shakeCoroutine != null) StopCoroutine(shakeCoroutine);
        shakeCoroutine = StartCoroutine(ErrorShakeRoutine());
    }

    private IEnumerator ErrorShakeRoutine()
    {
        float elapsed = 0f;
        float dur = Mathf.Max(0.05f, shakeDuration);

        while (elapsed < dur)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / dur);

            // Getaran horizontal mereda halus
            float damper = 1f - t;
            float offsetX = Mathf.Sin(t * Mathf.PI * 6f) * shakeMagnitude * damper;
            rectTransform.anchoredPosition = baseAnchoredPos + new Vector2(offsetX, 0f);

            if (targetGraphic != null)
            {
                targetGraphic.color = Color.Lerp(errorFlashColor, baseColor, t);
            }

            yield return null;
        }

        rectTransform.anchoredPosition = baseAnchoredPos;
        if (targetGraphic != null) targetGraphic.color = baseColor;
        shakeCoroutine = null;
    }

    private void Update()
    {
        if (!hasCapturedBase) return;

        float targetScale = isFocused ? focusScale : 1f;
        if (Mathf.Abs(currentScale - targetScale) < 0.001f) return;

        float step = 1f - Mathf.Exp(-scaleSpeed * Time.unscaledDeltaTime);
        currentScale = Mathf.Lerp(currentScale, targetScale, step);
        transform.localScale = baseScale * currentScale;
    }
}