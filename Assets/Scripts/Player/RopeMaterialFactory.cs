using UnityEngine;

/// <summary>
/// Membuat tekstur + material tali sepenuhnya dari kode (tanpa asset luar).
/// Dipakai otomatis oleh VerletRopeVisual jika Rope Material kosong,
/// dan oleh menu Tools > River Game > Create Rope Material untuk menyimpannya sebagai asset.
/// </summary>
public static class RopeMaterialFactory
{
    private static Material sharedMaterial;
    private static Texture2D sharedTexture;

    /// <summary>Material tali bersama (dibuat sekali, dipakai semua pemain).</summary>
    public static Material GetSharedMaterial()
    {
        if (sharedMaterial == null)
        {
            if (sharedTexture == null) sharedTexture = CreateRopeTexture();
            sharedMaterial = CreateMaterial(sharedTexture);
        }
        return sharedMaterial;
    }

    /// <summary>
    /// Tekstur tali berpilin: U = keliling tali, V = sepanjang tali. Seamless di kedua arah.
    /// </summary>
    public static Texture2D CreateRopeTexture(int width = 64, int height = 96, int strands = 3, int twists = 2)
    {
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, true, false)
        {
            name = "RopeProceduralTexture",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
            anisoLevel = 4
        };

        var dark = new Color(0.20f, 0.13f, 0.07f);
        var light = new Color(0.83f, 0.69f, 0.46f);
        var rnd = new System.Random(1234);
        var px = new Color32[width * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float u = x / (float)width;
                float v = y / (float)height;

                // Untaian diagonal (pilinan). Frekuensi bilangan bulat => tidak ada sambungan terlihat.
                float s = Mathf.Repeat(u * strands + v * twists, 1f);
                float ridge = Mathf.Pow(Mathf.Sin(s * Mathf.PI), 0.6f);   // 0 = alur gelap, 1 = puncak untaian

                // Serat halus acak
                float fiber = (float)rnd.NextDouble();
                float b = Mathf.Lerp(0.12f, 1f, ridge) * (0.88f + 0.24f * fiber);

                Color c = Color.Lerp(dark, light, Mathf.Clamp01(b));
                px[y * width + x] = c;
            }
        }

        tex.SetPixels32(px);
        tex.Apply(true);
        return tex;
    }

    public static Material CreateMaterial(Texture2D texture)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");

        var m = new Material(sh) { name = "Rope" };

        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", texture);
        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", texture);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
        if (m.HasProperty("_Color")) m.SetColor("_Color", Color.white);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.08f);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0.08f);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);

        return m;
    }
}