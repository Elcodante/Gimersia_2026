using UnityEngine;

public class UIHoverFocusGroup : MonoBehaviour
{
    [SerializeField] private UIButtonJuice[] groupButtons;
    [SerializeField, Range(0.4f, 1f)] private float dimmedAlpha = 0.82f;
    [SerializeField] private float fadeSpeed = 12f;

    private CanvasGroup[] canvasGroups;
    private UIButtonJuice currentHovered;
    private bool isUpdating = false;

    private void Awake()
    {
        if (groupButtons == null) return;

        canvasGroups = new CanvasGroup[groupButtons.Length];
        for (int i = 0; i < groupButtons.Length; i++)
        {
            if (groupButtons[i] == null) continue;

            CanvasGroup cg = groupButtons[i].GetComponent<CanvasGroup>();
            if (cg == null) cg = groupButtons[i].gameObject.AddComponent<CanvasGroup>();
            canvasGroups[i] = cg;
        }
    }

    private void OnEnable()
    {
        if (groupButtons == null) return;
        for (int i = 0; i < groupButtons.Length; i++)
        {
            if (groupButtons[i] != null)
            {
                groupButtons[i].OnHoverChanged += HandleHoverChanged;
            }
        }
    }

    private void OnDisable()
    {
        if (groupButtons == null) return;
        for (int i = 0; i < groupButtons.Length; i++)
        {
            if (groupButtons[i] != null)
            {
                groupButtons[i].OnHoverChanged -= HandleHoverChanged;
            }
        }
        currentHovered = null;
        isUpdating = false;
    }

    private void HandleHoverChanged(UIButtonJuice button, bool hovered)
    {
        if (hovered)
        {
            currentHovered = button;
            isUpdating = true;
        }
        else if (currentHovered == button)
        {
            currentHovered = null;
            isUpdating = true;
        }
    }

    private void Update()
    {
        if (!isUpdating || canvasGroups == null) return;

        float step = 1f - Mathf.Exp(-fadeSpeed * Time.unscaledDeltaTime);
        bool anyStillMoving = false;

        for (int i = 0; i < groupButtons.Length; i++)
        {
            CanvasGroup cg = canvasGroups[i];
            if (cg == null) continue;

            float targetAlpha = (currentHovered == null || groupButtons[i] == currentHovered) ? 1f : dimmedAlpha;

            if (Mathf.Abs(cg.alpha - targetAlpha) > 0.002f)
            {
                cg.alpha = Mathf.Lerp(cg.alpha, targetAlpha, step);
                anyStillMoving = true;
            }
            else
            {
                cg.alpha = targetAlpha;
            }
        }

        if (!anyStillMoving)
        {
            isUpdating = false;
        }
    }
}