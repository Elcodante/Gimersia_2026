using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class LobbyUIJuice : MonoBehaviour
{
    private static readonly string[] WaitingFrames =
    {
        "Waiting for player.",
        "Waiting for player..",
        "Waiting for player..."
    };

    [Header("Click-to-Copy Kode Room")]
    [SerializeField] private TMP_Text codeText;
    [Tooltip("Opsional: teks 'COPIED!' buatan sendiri. Jika kosong, otomatis dibuat memakai font yang sama.")]
    [SerializeField] private TMP_Text copiedToastText;
    [SerializeField] private Color copiedToastColor = new Color(0.24f, 0.52f, 0.28f, 1f);

    [Header("Daftar Slot Pemain")]
    [SerializeField] private TMP_Text[] playerTexts;
    [SerializeField] private float waitingDotInterval = 0.45f;

    private bool[] slotOccupied;
    private string[] lastSlotNames;
    private Vector3[] slotBaseScales;
    private Coroutine[] slotPopCoroutines;

    private Coroutine codePunchCoroutine;
    private Coroutine toastCoroutine;
    private Vector3 codeBaseScale = Vector3.one;
    private Vector2 toastRestPos;
    private bool isSlotsInitialized = false;

    private float dotTimer = 0f;
    private int dotIndex = 0;

    private void Awake()
    {
        InitSlots();
        SetupCodeCopyButton();
    }

    private void InitSlots()
    {
        if (isSlotsInitialized || playerTexts == null) return;
        isSlotsInitialized = true;

        int len = playerTexts.Length;
        slotOccupied = new bool[len];
        lastSlotNames = new string[len];
        slotBaseScales = new Vector3[len];
        slotPopCoroutines = new Coroutine[len];

        for (int i = 0; i < len; i++)
        {
            if (playerTexts[i] != null)
            {
                slotBaseScales[i] = playerTexts[i].rectTransform.localScale;
            }
        }
    }

    private void SetupCodeCopyButton()
    {
        if (codeText == null) return;

        codeBaseScale = codeText.rectTransform.localScale;
        codeText.raycastTarget = true;

        Button btn = codeText.GetComponent<Button>();
        if (btn == null)
        {
            btn = codeText.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
        }
        btn.onClick.AddListener(CopyRoomCode);

        // Buat label COPIED! otomatis jika slot di Inspector dikosongkan
        if (copiedToastText == null)
        {
            GameObject toastObj = new GameObject("CopiedToast", typeof(RectTransform));
            toastObj.transform.SetParent(codeText.transform.parent, false);

            copiedToastText = toastObj.AddComponent<TextMeshProUGUI>();
            copiedToastText.font = codeText.font;
            copiedToastText.fontSize = Mathf.Max(18f, codeText.fontSize * 0.42f);
            copiedToastText.alignment = TextAlignmentOptions.Center;
            copiedToastText.color = copiedToastColor;
            copiedToastText.text = "COPIED!";
            copiedToastText.raycastTarget = false;

            RectTransform toastRt = copiedToastText.rectTransform;
            toastRt.anchorMin = codeText.rectTransform.anchorMin;
            toastRt.anchorMax = codeText.rectTransform.anchorMax;
            toastRt.sizeDelta = new Vector2(220f, 40f);
            toastRt.anchoredPosition = codeText.rectTransform.anchoredPosition + new Vector2(0f, 26f);
        }

        toastRestPos = copiedToastText.rectTransform.anchoredPosition;
        copiedToastText.gameObject.SetActive(false);
    }

    private void OnEnable()
    {
        dotTimer = 0f;
        dotIndex = 0;
        if (copiedToastText != null) copiedToastText.gameObject.SetActive(false);
    }

    /// <summary>
    /// Menyalin kode room ke clipboard + memicu pantulan kode & teks COPIED! melayang.
    /// </summary>
    public void CopyRoomCode()
    {
        if (codeText == null || string.IsNullOrWhiteSpace(codeText.text)) return;

        GUIUtility.systemCopyBuffer = codeText.text.Trim();

        if (codePunchCoroutine != null) StopCoroutine(codePunchCoroutine);
        codePunchCoroutine = StartCoroutine(PunchScaleRoutine(codeText.rectTransform, codeBaseScale, 1.14f, 0.22f));

        if (copiedToastText != null)
        {
            if (toastCoroutine != null) StopCoroutine(toastCoroutine);
            toastCoroutine = StartCoroutine(ShowCopiedToastRoutine());
        }
    }

    /// <summary>
    /// Memperbarui daftar slot pemain dan memicu efek Pop-In saat ada pemain baru masuk.
    /// </summary>
    public void UpdatePlayerSlot(int index, bool occupied, string displayName)
    {
        if (playerTexts == null || index < 0 || index >= playerTexts.Length || playerTexts[index] == null)
            return;

        if (slotOccupied == null) InitSlots();

        bool newlyJoined = occupied && (!slotOccupied[index] || lastSlotNames[index] != displayName);
        slotOccupied[index] = occupied;
        lastSlotNames[index] = occupied ? displayName : string.Empty;

        playerTexts[index].text = occupied ? displayName : WaitingFrames[dotIndex];

        if (newlyJoined && gameObject.activeInHierarchy)
        {
            if (slotPopCoroutines[index] != null) StopCoroutine(slotPopCoroutines[index]);
            slotPopCoroutines[index] = StartCoroutine(
                PunchScaleRoutine(playerTexts[index].rectTransform, slotBaseScales[index], 1.15f, 0.24f)
            );
        }
    }

    public void ResetSlots()
    {
        if (!isSlotsInitialized) InitSlots();
        if (playerTexts == null || slotOccupied == null) return;

        for (int i = 0; i < playerTexts.Length; i++)
        {
            slotOccupied[i] = false;
            lastSlotNames[i] = string.Empty;
        }
    }

    private void Update()
    {
        if (playerTexts == null || slotOccupied == null) return;

        dotTimer += Time.unscaledDeltaTime;
        if (dotTimer < waitingDotInterval) return;

        dotTimer = 0f;
        dotIndex = (dotIndex + 1) % WaitingFrames.Length;

        for (int i = 0; i < playerTexts.Length; i++)
        {
            if (!slotOccupied[i] && playerTexts[i] != null)
            {
                playerTexts[i].text = WaitingFrames[dotIndex];
            }
        }
    }

    private IEnumerator PunchScaleRoutine(RectTransform target, Vector3 baseScale, float peakMultiplier, float duration)
    {
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float bump = Mathf.Sin(t * Mathf.PI) * (peakMultiplier - 1f);
            target.localScale = baseScale * (1f + bump);
            yield return null;
        }
        target.localScale = baseScale;
    }

    private IEnumerator ShowCopiedToastRoutine()
    {
        copiedToastText.gameObject.SetActive(true);
        RectTransform rt = copiedToastText.rectTransform;
        Color c = copiedToastColor;

        float duration = 0.75f;
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);

            // Melayang naik 30px sambil memudar halus di paruh kedua
            rt.anchoredPosition = toastRestPos + new Vector2(0f, Mathf.Lerp(0f, 30f, 1f - (1f - t) * (1f - t)));
            c.a = t < 0.45f ? 1f : Mathf.Lerp(1f, 0f, (t - 0.45f) / 0.55f);
            copiedToastText.color = c;

            yield return null;
        }

        copiedToastText.gameObject.SetActive(false);
        rt.anchoredPosition = toastRestPos;
        toastCoroutine = null;
    }
}