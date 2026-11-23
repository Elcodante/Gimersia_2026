using UnityEngine;
using UnityEngine.UI;

public class WaterSplashUI : MonoBehaviour
{
    private class SplashSlot
    {
        public GameObject gameObject;
        public RectTransform rectTransform;
        public Image image;
        public bool isActive;
        public float elapsed;
        public float duration;
        public float targetScale;
        public float fallSpeed;
        public float startAlpha;
    }

    [Header("Aset Cipratan (Masukkan 4 Sprite)")]
    [SerializeField] private Sprite[] splashSprites;

    [Header("Pengaturan Pool & Area Muncul")]
    [Tooltip("RectTransform wadah full-screen tempat cipratan muncul. Kosong = pakai RectTransform objek ini.")]
    [SerializeField] private RectTransform splashContainer;
    [SerializeField] private int poolSize = 16;
    [SerializeField] private Vector2 baseSplashSize = new Vector2(260f, 260f);
    [Tooltip("Margin dari tepi layar (piksel) agar cipratan tidak terlalu terpotong di pinggir.")]
    [SerializeField] private float screenPadding = 80f;

    [Header("Skala & Jumlah Berdasarkan Kekuatan Tabrakan")]
    [SerializeField] private int minSplashCount = 1;
    [SerializeField] private int maxSplashCount = 7;
    [SerializeField] private float minScale = 0.55f;
    [SerializeField] private float maxScale = 1.45f;

    [Header("Animasi Jatuh & Memudar")]
    [SerializeField] private float popInDuration = 0.07f;    // Muncul cepat seperti air mengenai lensa
    [SerializeField] private float minDuration = 1.4f;
    [SerializeField] private float maxDuration = 2.6f;
    [SerializeField] private float minFallSpeed = 35f;       // Piksel per detik
    [SerializeField] private float maxFallSpeed = 95f;
    [SerializeField, Range(0f, 1f)] private float minStartAlpha = 0.55f;
    [SerializeField, Range(0f, 1f)] private float maxStartAlpha = 0.92f;
    [SerializeField] private Color splashTint = Color.white;

    private SplashSlot[] pool;
    private int activeCount = 0;

    private void Awake()
    {
        if (splashContainer == null)
        {
            splashContainer = GetComponent<RectTransform>();
        }

        InitializePool();
    }

    private void OnEnable()
    {
        BoatImpactBroadcaster.OnBoatImpact += SpawnSplashes;
    }

    private void OnDisable()
    {
        BoatImpactBroadcaster.OnBoatImpact -= SpawnSplashes;
    }

    private void InitializePool()
    {
        pool = new SplashSlot[poolSize];

        for (int i = 0; i < poolSize; i++)
        {
            GameObject go = new GameObject($"Splash_{i}", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(splashContainer, false);

            RectTransform rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = baseSplashSize;

            Image img = go.GetComponent<Image>();
            img.raycastTarget = false; // Wajib false agar tidak menghalangi klik mouse laso / UI lain
            img.preserveAspect = true;

            go.SetActive(false);

            pool[i] = new SplashSlot
            {
                gameObject = go,
                rectTransform = rt,
                image = img,
                isActive = false
            };
        }
    }

    public void SpawnSplashes(float intensity)
    {
        if (splashSprites == null || splashSprites.Length == 0 || splashContainer == null) return;

        intensity = Mathf.Clamp01(intensity);

        // Jumlah cipratan meningkat seiring kerasnya tabrakan
        int count = Mathf.RoundToInt(Mathf.Lerp(minSplashCount, maxSplashCount, intensity));

        Rect rect = splashContainer.rect;
        float halfW = Mathf.Max(10f, rect.width * 0.5f - screenPadding);
        float halfH = Mathf.Max(10f, rect.height * 0.5f - screenPadding);

        for (int i = 0; i < count; i++)
        {
            SplashSlot slot = GetAvailableSlot();

            // Pilih 1 dari 4 aset cipratan secara acak
            slot.image.sprite = splashSprites[Random.Range(0, splashSprites.Length)];

            // Posisi acak di layar (sedikit condong ke tengah-atas agar punya ruang turun ke bawah)
            float posX = Random.Range(-halfW, halfW);
            float posY = Random.Range(-halfH * 0.55f, halfH);
            slot.rectTransform.anchoredPosition = new Vector2(posX, posY);

            // Rotasi acak agar 4 aset terlihat sangat bervariasi
            slot.rectTransform.localRotation = Quaternion.Euler(0f, 0f, Random.Range(0f, 360f));

            // Ukuran beragam sesuai kerasnya tabrakan + variasi acak tiap tetesan
            float baseScale = Mathf.Lerp(minScale, maxScale, intensity);
            slot.targetScale = baseScale * Random.Range(0.8f, 1.2f);
            slot.rectTransform.localScale = Vector3.one * (slot.targetScale * 0.25f);

            // Tetesan yang lebih besar/keras menempel lebih lama dan turun lebih cepat
            slot.duration = Mathf.Lerp(minDuration, maxDuration, intensity) * Random.Range(0.85f, 1.15f);
            slot.fallSpeed = Mathf.Lerp(minFallSpeed, maxFallSpeed, intensity) * Random.Range(0.75f, 1.25f);
            slot.startAlpha = Mathf.Lerp(minStartAlpha, maxStartAlpha, intensity);
            slot.elapsed = 0f;

            Color c = splashTint;
            c.a = slot.startAlpha;
            slot.image.color = c;

            if (!slot.isActive)
            {
                slot.isActive = true;
                activeCount++;
                slot.gameObject.SetActive(true);
            }
        }
    }

    private SplashSlot GetAvailableSlot()
    {
        SplashSlot oldestSlot = pool[0];
        float maxProgress = -1f;

        for (int i = 0; i < pool.Length; i++)
        {
            if (!pool[i].isActive)
            {
                return pool[i];
            }

            // Jika seluruh pool sedang terpakai, daur ulang cipratan yang paling hampir habis
            float progress = pool[i].elapsed / Mathf.Max(0.01f, pool[i].duration);
            if (progress > maxProgress)
            {
                maxProgress = progress;
                oldestSlot = pool[i];
            }
        }

        return oldestSlot;
    }

    private void Update()
    {
        // Zero overhead saat tidak ada cipratan aktif
        if (activeCount == 0) return;

        float dt = Time.deltaTime;

        for (int i = 0; i < pool.Length; i++)
        {
            SplashSlot slot = pool[i];
            if (!slot.isActive) continue;

            slot.elapsed += dt;
            float normalizedTime = slot.elapsed / slot.duration;

            if (normalizedTime >= 1f)
            {
                slot.isActive = false;
                slot.gameObject.SetActive(false);
                activeCount--;
                continue;
            }

            // 1. Efek Pop-in cepat saat air baru mengenai layar
            if (slot.elapsed < popInDuration)
            {
                float popT = slot.elapsed / popInDuration;
                float easedPop = 1f - (1f - popT) * (1f - popT);
                float s = Mathf.Lerp(slot.targetScale * 0.25f, slot.targetScale, easedPop);
                slot.rectTransform.localScale = Vector3.one * s;
            }
            else
            {
                slot.rectTransform.localScale = Vector3.one * slot.targetScale;
            }

            // 2. Meluncur turun perlahan (makin lama sedikit makin meluncur seperti air menetes)
            Vector2 pos = slot.rectTransform.anchoredPosition;
            pos.y -= slot.fallSpeed * (0.6f + 0.6f * normalizedTime) * dt;
            slot.rectTransform.anchoredPosition = pos;

            // 3. Alpha perlahan turun menuju 0 (menahan sebentar di awal lalu memudar halus)
            float fadeT = Mathf.Clamp01((normalizedTime - 0.15f) / 0.85f);
            float alpha = Mathf.Lerp(slot.startAlpha, 0f, fadeT * fadeT);

            Color c = slot.image.color;
            c.a = alpha;
            slot.image.color = c;
        }
    }
}