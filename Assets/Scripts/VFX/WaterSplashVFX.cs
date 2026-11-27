using UnityEngine;

/// <summary>
/// Pemutar efek partikel air (cipratan) ber-pool untuk scene In_Game.
/// Dipakai oleh:
/// - FishProjectile  : riak saat ikan bersiap lompat, cipratan saat keluar air, cipratan saat nyemplung lagi.
/// - PlayerMovement  : cipratan saat pemain jatuh/nyemplung ke air.
/// Murni visual lokal (tidak butuh NetworkObject): tiap client memainkan efeknya sendiri
/// dari event yang memang sudah tersinkron (RPC ikan &amp; status jatuh pemain).
/// Tanpa Instantiate/Destroy saat bermain -> aman untuk WebGL.
/// </summary>
[DisallowMultipleComponent]
public sealed class WaterSplashVFX : MonoBehaviour
{
    public static WaterSplashVFX Instance { get; private set; }

    [Header("Prefab Partikel")]
    [Tooltip("Cipratan besar ke atas saat ikan menembus permukaan air untuk melompat.")]
    [SerializeField] private ParticleSystem leapSplashPrefab;
    [Tooltip("Cipratan saat sesuatu (ikan / pemain) masuk ke air.")]
    [SerializeField] private ParticleSystem entrySplashPrefab;
    [Tooltip("Riak + gelembung kecil di permukaan air sebagai tanda ikan akan melompat.")]
    [SerializeField] private ParticleSystem ripplePrefab;

    [Header("Pool")]
    [Tooltip("Jumlah instance per jenis efek yang di-prewarm dan didaur ulang bergantian.")]
    [SerializeField, Range(2, 16)] private int instancesPerType = 6;

    private Pool leapPool;
    private Pool entryPool;
    private Pool ripplePool;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        leapPool = new Pool(leapSplashPrefab, instancesPerType, transform);
        entryPool = new Pool(entrySplashPrefab, instancesPerType, transform);
        ripplePool = new Pool(ripplePrefab, instancesPerType, transform);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    /// <summary>Cipratan ikan menembus permukaan air saat melompat.</summary>
    public static void PlayLeapSplash(Vector3 position, float scale = 1f)
    {
        if (Instance != null && Instance.leapPool != null) Instance.leapPool.Play(position, scale);
    }

    /// <summary>Cipratan saat ikan / pemain masuk ke air.</summary>
    public static void PlayEntrySplash(Vector3 position, float scale = 1f)
    {
        if (Instance != null && Instance.entryPool != null) Instance.entryPool.Play(position, scale);
    }

    /// <summary>Riak kecil di permukaan air (telegraph sebelum ikan melompat).</summary>
    public static void PlayRipple(Vector3 position, float scale = 1f)
    {
        if (Instance != null && Instance.ripplePool != null) Instance.ripplePool.Play(position, scale);
    }

    private sealed class Pool
    {
        private readonly ParticleSystem[] items;
        private int next;

        public Pool(ParticleSystem prefab, int count, Transform parent)
        {
            if (prefab == null)
            {
                items = new ParticleSystem[0];
                return;
            }

            items = new ParticleSystem[count];
            for (int i = 0; i < count; i++)
            {
                ParticleSystem ps = Object.Instantiate(prefab, parent);
                ps.gameObject.name = prefab.name + "_" + i;
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                items[i] = ps;
            }
        }

        public void Play(Vector3 position, float scale)
        {
            if (items.Length == 0) return;

            ParticleSystem ps = items[next];
            next = (next + 1) % items.Length;
            if (ps == null) return;

            Transform t = ps.transform;
            t.SetPositionAndRotation(position, Quaternion.identity);
            t.localScale = Vector3.one * Mathf.Max(0.05f, scale);

            ps.Clear(true);
            ps.Play(true);
        }
    }
}
