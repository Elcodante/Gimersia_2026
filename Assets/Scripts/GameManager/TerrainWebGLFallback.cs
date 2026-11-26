using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Pada build Player (seperti WebGL), Terrain yang belum memiliki TerrainLayer tidak
/// menampilkan tekstur checkerboard bawaan Unity Editor melainkan putih polos yang silau.
/// Script ini menerapkan TerrainLayer fallback yang identik dengan tampilan Editor hanya
/// saat runtime build apabila Terrain belum memiliki TerrainLayer sama sekali.
/// Jika artist menambahkan TerrainLayer sendiri di masa depan, fallback ini otomatis tidak aktif.
/// </summary>
public static class TerrainWebGLFallback
{
#if !UNITY_EDITOR
    private static Texture2D cachedCheckerTexture;
    private static TerrainLayer cachedFallbackLayer;
    private static bool hookRegistered;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Initialize()
    {
        if (!hookRegistered)
        {
            hookRegistered = true;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        ApplyToActiveTerrains();
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        ApplyToActiveTerrains();
    }

    private static void ApplyToActiveTerrains()
    {
        Terrain[] terrains = Object.FindObjectsByType<Terrain>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        if (terrains == null || terrains.Length == 0) return;

        for (int i = 0; i < terrains.Length; i++)
        {
            Terrain terrain = terrains[i];
            if (terrain == null || terrain.terrainData == null) continue;

            TerrainData data = terrain.terrainData;
            if (data.terrainLayers != null && data.terrainLayers.Length > 0) continue;

            TerrainLayer fallbackLayer = GetOrCreateFallbackLayer();
            data.terrainLayers = new[] { fallbackLayer };
            EnsureFirstAlphamapFilled(data);
            terrain.Flush();
        }
    }

    private static void EnsureFirstAlphamapFilled(TerrainData data)
    {
        int w = data.alphamapWidth;
        int h = data.alphamapHeight;
        int layers = data.alphamapLayers;
        if (w <= 0 || h <= 0 || layers <= 0) return;

        // Jika Unity sudah mengisi channel 0 dengan 1.0, tidak perlu alokasi ulang alphamap.
        float[,,] sample = data.GetAlphamaps(w / 2, h / 2, 1, 1);
        if (sample[0, 0, 0] >= 0.5f) return;

        float[,,] maps = new float[h, w, layers];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                maps[y, x, 0] = 1f;
            }
        }
        data.SetAlphamaps(0, 0, maps);
    }

    private static TerrainLayer GetOrCreateFallbackLayer()
    {
        if (cachedFallbackLayer != null && cachedCheckerTexture != null)
        {
            return cachedFallbackLayer;
        }

        const int size = 128;
        const int half = size / 2;
        cachedCheckerTexture = new Texture2D(size, size, TextureFormat.RGBA32, true, false)
        {
            name = "RuntimeTerrainCheckerFallback",
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 4
        };

        Color32 lightCell = new Color32(198, 198, 198, 255);
        Color32 darkCell = new Color32(166, 166, 166, 255);
        Color32[] pixels = new Color32[size * size];

        for (int y = 0; y < size; y++)
        {
            bool yHigh = y >= half;
            int row = y * size;
            for (int x = 0; x < size; x++)
            {
                bool xHigh = x >= half;
                pixels[row + x] = (xHigh ^ yHigh) ? darkCell : lightCell;
            }
        }

        cachedCheckerTexture.SetPixels32(pixels);
        cachedCheckerTexture.Apply(true, true);

        cachedFallbackLayer = new TerrainLayer
        {
            name = "RuntimeTerrainFallbackLayer",
            diffuseTexture = cachedCheckerTexture,
            tileSize = new Vector2(15f, 15f),
            tileOffset = Vector2.zero,
            smoothness = 0f,
            metallic = 0f
        };

        return cachedFallbackLayer;
    }
#endif
}
