using System;
using System.Collections;
using UnityEngine;

public class MainMenuEntrance : MonoBehaviour
{
    [Serializable]
    public class EntranceItem
    {
        public RectTransform target;
        [Tooltip("Jeda waktu sebelum elemen ini mulai muncul (detik).")]
        public float delay = 0f;
        public float duration = 0.35f;
        [Tooltip("Posisi awal relatif dari posisi aslinya. Contoh: (0, -50) untuk meluncur naik.")]
        public Vector2 startOffset = new Vector2(0f, -50f);
        public float startScale = 0.94f;
        [Tooltip("Rotasi awal Z (derajat). Bagus diisi -90 untuk ikon Gear.")]
        public float startRotationZ = 0f;
        public bool useBounce = true;
    }

    [Header("Daftar Elemen yang Masuk Bergantian")]
    [SerializeField] private EntranceItem[] items;

    private void Start()
    {
        if (items == null || items.Length == 0) return;

        // Pastikan Layout Group (jika ada) selesai menghitung posisi akhir sebelum posisi disimpan
        Canvas.ForceUpdateCanvases();

        for (int i = 0; i < items.Length; i++)
        {
            if (items[i] != null && items[i].target != null)
            {
                StartCoroutine(AnimateItemRoutine(items[i]));
            }
        }
    }

    private IEnumerator AnimateItemRoutine(EntranceItem item)
    {
        RectTransform rt = item.target;
        CanvasGroup cg = rt.GetComponent<CanvasGroup>();
        if (cg == null) cg = rt.gameObject.AddComponent<CanvasGroup>();

        Vector2 restPos = rt.anchoredPosition;
        Vector3 restScale = rt.localScale;
        Quaternion restRot = rt.localRotation;

        // Posisikan di kondisi awal (tersembunyi & belum bisa diklik)
        rt.anchoredPosition = restPos + item.startOffset;
        rt.localScale = restScale * item.startScale;
        rt.localRotation = restRot * Quaternion.Euler(0f, 0f, item.startRotationZ);
        cg.alpha = 0f;
        cg.interactable = false;
        cg.blocksRaycasts = false;

        if (item.delay > 0f)
        {
            yield return new WaitForSecondsRealtime(item.delay);
        }

        float elapsed = 0f;
        float duration = Mathf.Max(0.01f, item.duration);

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);

            float motionEase = item.useBounce ? EaseOutBack(t, 1.25f) : EaseOutCubic(t);
            float alphaEase = EaseOutCubic(t);

            rt.anchoredPosition = Vector2.LerpUnclamped(restPos + item.startOffset, restPos, motionEase);
            rt.localScale = Vector3.LerpUnclamped(restScale * item.startScale, restScale, motionEase);
            float currentRotZ = Mathf.LerpUnclamped(item.startRotationZ, 0f, motionEase);
            rt.localRotation = restRot * Quaternion.Euler(0f, 0f, currentRotZ);
            cg.alpha = Mathf.Lerp(0f, 1f, alphaEase);

            yield return null;
        }

        // Kembalikan persis ke posisi akhir lalu aktifkan klik
        rt.anchoredPosition = restPos;
        rt.localScale = restScale;
        rt.localRotation = restRot;
        cg.alpha = 1f;
        cg.interactable = true;
        cg.blocksRaycasts = true;
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