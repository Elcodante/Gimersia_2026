using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Koordinator event serangan ikan (Multiplayer Server-Authoritative &amp; WebGL-friendly).
/// - Memanfaatkan FishObjectPool untuk mendaur ulang multi-varian objek ikan (Ikan 1 &amp; Ikan 2) tanpa Instantiate/Destroy.
/// - Memanfaatkan FishAttackScheduler dengan profil berbeda untuk mode Solo (lebih jarang) vs Multiplayer.
/// - Server memilih target pemain secara acak, menyinkronkan lintasan via 1 ClientRpc ringan,
///   memverifikasi benturan di server, lalu memicu knockback pemain &amp; efek terpental pada ikan.
/// </summary>
[DisallowMultipleComponent]
public sealed class FishEventController : NetworkBehaviour
{
    [Header("Prefab & Object Pool")]
    [Tooltip("Daftar prefab model ikan yang di-prewarm bergantian di pool (isi Ikan 1 & Ikan 2).")]
    [SerializeField] private GameObject[] fishVariantPrefabs;
    [Tooltip("Prefab cadangan jika daftar varian di atas kosong (Assets/Prefabs/ikan.prefab).")]
    [SerializeField] private FishProjectile fishPrefab;
    [Tooltip("Skala visual untuk prefab model 3D murni (Ikan 1 / Ikan 2 berukuran ~1 m di FBX).")]
    [SerializeField, Range(0.5f, 4f)] private float fishVisualScale = 1.8f;
    [Tooltip("Jumlah maksimal ikan yang di-prewarm dalam pool (disarankan 6-8 untuk WebGL).")]
    [SerializeField, Range(2, 16)] private int poolCapacity = 8;

    [Header("Pola Kemunculan: Mode Solo (Lebih Jarang)")]
    [SerializeField] private FishPacingProfile soloPacing = new FishPacingProfile
    {
        initialGraceSeconds = 12f,
        minWaveInterval = 15f,
        maxWaveInterval = 26f,
        minBurstSpacing = 1.4f,
        maxBurstSpacing = 2.4f,
        maxBurstCount = 1,
        minMultiBurstChance = 0f,
        maxMultiBurstChance = 0f
    };

    [Header("Pola Kemunculan: Mode Multiplayer")]
    [SerializeField] private FishPacingProfile multiplayerPacing = new FishPacingProfile
    {
        initialGraceSeconds = 8f,
        minWaveInterval = 10f,
        maxWaveInterval = 18f,
        minBurstSpacing = 1.1f,
        maxBurstSpacing = 2.0f,
        maxBurstCount = 2,
        minMultiBurstChance = 0.16f,
        maxMultiBurstChance = 0.32f
    };

    [Header("Lintasan Lompatan Ikan")]
    [SerializeField, Range(4f, 16f)] private float minSpawnRadius = 7.5f;
    [SerializeField, Range(6f, 20f)] private float maxSpawnRadius = 11.5f;
    [SerializeField, Range(0.8f, 3.5f)] private float submergeDepth = 1.6f;
    [SerializeField, Range(0.2f, 1.0f)] private float minTelegraphDuration = 0.35f;
    [SerializeField, Range(0.25f, 1.2f)] private float maxTelegraphDuration = 0.55f;
    [SerializeField, Range(0.6f, 2.0f)] private float minFlightDuration = 0.95f;
    [SerializeField, Range(0.8f, 2.4f)] private float maxFlightDuration = 1.30f;
    [SerializeField, Range(0.5f, 1.2f)] private float overshootRatio = 0.85f;
    [Tooltip("Kompensasi gerak kapal (0 = tidak memprediksi gerak kapal, 1 = penuh).")]
    [SerializeField, Range(0f, 1f)] private float boatVelocityLead = 0.65f;

    [Header("Benturan & Hempasan Pemain")]
    [Tooltip("Radius deteksi benturan ikan terhadap pemain di atas kapal (meter).")]
    [SerializeField, Range(0.5f, 2.5f)] private float hitRadius = 1.15f;
    [Tooltip("Kekuatan hempasan horizontal yang melempar pemain keluar dari kapal ke air (m/s).")]
    [SerializeField, Range(4f, 16f)] private float knockbackHorizontalSpeed = 8.5f;
    [Tooltip("Dorongan ke atas saat pemain terkena hantaman ikan (m/s).")]
    [SerializeField, Range(1f, 10f)] private float knockbackUpwardSpeed = 4.6f;
    [Tooltip("Kecepatan pantulan balik ikan (terpental) setelah mengenai pemain.")]
    [SerializeField, Range(3f, 14f)] private float fishBounceBackSpeed = 6.5f;
    [SerializeField, Range(2f, 12f)] private float fishBounceUpSpeed = 6.2f;
    [SerializeField] private string hitImpactSfxId = "boat_impact";

    private FishObjectPool pool;
    private FishAttackScheduler scheduler;
    private Transform poolContainer;
    private bool pacingAppliedForMatch;

    private RiverPath cachedRiver;
    private BoatController cachedBoat;
    private Rigidbody cachedBoatRb;
    private float referenceScanTimer;

    // Reusable buffer agar 0 alokasi GC di Update
    private readonly List<PlayerMovement> eligiblePlayers = new List<PlayerMovement>(8);
    private PlayerMovement[] playerCache = new PlayerMovement[0];
    private float playerCacheRefreshTimer;

    private void Awake()
    {
        GameObject containerObj = new GameObject("FishPoolContainer");
        containerObj.transform.SetParent(transform, false);
        poolContainer = containerObj.transform;

        pool = new FishObjectPool(fishVariantPrefabs, fishPrefab, fishVisualScale, poolCapacity, poolContainer);
        scheduler = new FishAttackScheduler(SanitizeProfile(multiplayerPacing, FishPacingProfile.DefaultMultiplayer));
        WaterSplashVFX.Prewarm();
    }

    public override void OnNetworkSpawn()
    {
        pacingAppliedForMatch = false;

        if (IsServer)
        {
            RefreshSceneReferences();
            RefreshPlayerCache();
        }
    }

    public override void OnNetworkDespawn()
    {
        pacingAppliedForMatch = false;
        pool?.ReleaseAll();
    }

    private void Update()
    {
        if (!IsServer || !IsSpawned) return;
        if (!GameSpawner.IsMatchReady || RiverFinishGoal.HasFinished) return;

        if (!pacingAppliedForMatch)
        {
            ApplySessionPacingProfile();
        }

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        referenceScanTimer -= dt;
        if (referenceScanTimer <= 0f)
        {
            referenceScanTimer = 1.0f;
            RefreshSceneReferences();
        }

        playerCacheRefreshTimer -= dt;
        if (playerCacheRefreshTimer <= 0f)
        {
            playerCacheRefreshTimer = 0.5f;
            RefreshPlayerCache();
        }

        if (scheduler.Tick(dt))
        {
            TryLaunchRandomFishAttack();
        }

        if (pool.ActiveCount > 0)
        {
            CheckServerHitCollisions();
        }
    }

    private void ApplySessionPacingProfile()
    {
        pacingAppliedForMatch = true;
        bool solo = IsSoloSession();

        FishPacingProfile activeProfile = solo
            ? SanitizeProfile(soloPacing, FishPacingProfile.DefaultSolo)
            : SanitizeProfile(multiplayerPacing, FishPacingProfile.DefaultMultiplayer);

        scheduler.ApplyProfile(activeProfile);
        Debug.Log($"[FishEventController] Profil ikan aktif: {(solo ? "SOLO" : "MULTIPLAYER")} " +
                  $"(grace={activeProfile.initialGraceSeconds:F0}s, interval={activeProfile.minWaveInterval:F0}-{activeProfile.maxWaveInterval:F0}s, maxBurst={activeProfile.maxBurstCount}).");
    }

    private bool IsSoloSession()
    {
        if (MainMenuController.IsSoloMode) return true;
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.ConnectedClientsIds.Count <= 1) return true;
        return false;
    }

    private static FishPacingProfile SanitizeProfile(FishPacingProfile configured, FishPacingProfile fallback)
    {
        if (configured.minWaveInterval < 1f || configured.maxWaveInterval < 1f || configured.maxBurstCount < 1)
        {
            return fallback;
        }
        return configured;
    }

    private void RefreshSceneReferences()
    {
        if (cachedRiver == null) cachedRiver = RiverPath.Main;

        if (cachedBoat == null)
        {
            cachedBoat = FindFirstObjectByType<BoatController>();
            cachedBoatRb = cachedBoat != null ? cachedBoat.GetComponent<Rigidbody>() : null;
        }
    }

    private void RefreshPlayerCache()
    {
        playerCache = FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None);
    }

    private void CollectEligiblePlayers()
    {
        eligiblePlayers.Clear();
        if (playerCache == null) return;

        for (int i = 0; i < playerCache.Length; i++)
        {
            PlayerMovement pm = playerCache[i];
            if (pm != null && pm.gameObject.activeInHierarchy && pm.CanBeKnockedBack)
            {
                eligiblePlayers.Add(pm);
            }
        }
    }

    private void TryLaunchRandomFishAttack()
    {
        CollectEligiblePlayers();
        if (eligiblePlayers.Count == 0) return;

        if (!pool.TryAcquire(out int slotIndex, out _)) return;

        // Pilih target pemain secara acak di atas kapal (berfungsi untuk Solo maupun Multiplayer)
        PlayerMovement targetPlayer = eligiblePlayers[Random.Range(0, eligiblePlayers.Count)];
        Vector3 targetPos = targetPlayer.KnockbackPosition;

        float telegraphDuration = Random.Range(minTelegraphDuration, maxTelegraphDuration);
        float flightDuration = Random.Range(minFlightDuration, maxFlightDuration);

        // Prediksi ringan posisi target berdasarkan kecepatan gerak kapal agar lompatan akurat
        if (cachedBoatRb != null && boatVelocityLead > 0.01f)
        {
            float tHit = 1f / (1f + overshootRatio);
            float leadTime = telegraphDuration + flightDuration * tHit;
            Vector3 boatVel = cachedBoatRb.linearVelocity;
            boatVel.y = 0f;
            targetPos += boatVel * (leadTime * boatVelocityLead);
        }

        // Ambil ketinggian permukaan air dari RiverPath di posisi target
        float waterY = targetPos.y - 0.8f;
        Vector3 spawnPos = PickUnderwaterSpawnPoint(targetPos, ref waterY);

        // Variasi acak ketinggian & sedikit offset target agar tiap serangan dinamis
        float extraApex = Random.Range(0.1f, 0.75f);
        FishTrajectory trajectory = FishTrajectory.BuildThroughTarget(
            spawnPos,
            targetPos,
            waterY,
            submergeDepth,
            overshootRatio,
            extraApex,
            telegraphDuration,
            flightDuration);

        LaunchFishClientRpc(
            (byte)slotIndex,
            trajectory.StartPos,
            trajectory.EndPos,
            trajectory.ArcHeight,
            trajectory.SubmergeY,
            trajectory.TelegraphDuration,
            trajectory.FlightDuration);
    }

    private Vector3 PickUnderwaterSpawnPoint(Vector3 targetPos, ref float waterY)
    {
        RiverPath.Sample sample = default;
        bool hasRiver = cachedRiver != null && cachedRiver.IsValid;
        if (hasRiver)
        {
            sample = cachedRiver.SampleAt(targetPos);
            waterY = sample.center.y;
        }

        float radius = Random.Range(minSpawnRadius, maxSpawnRadius);
        float angleRad = Random.Range(0f, Mathf.PI * 2f);
        Vector3 offset = new Vector3(Mathf.Cos(angleRad), 0f, Mathf.Sin(angleRad)) * radius;
        Vector3 candidate = new Vector3(targetPos.x + offset.x, waterY - submergeDepth, targetPos.z + offset.z);

        // Jika kandidat keluar terlalu jauh dari lebar sungai, balikkan arahnya ke sisi sungai yang lebih lega
        if (hasRiver)
        {
            RiverPath.Sample candSample = cachedRiver.SampleAt(candidate);
            if (candSample.lateral > candSample.halfWidth * 0.92f)
            {
                candidate = new Vector3(targetPos.x - offset.x, waterY - submergeDepth, targetPos.z - offset.z);
                candSample = cachedRiver.SampleAt(candidate);
            }
            waterY = candSample.center.y;
            candidate.y = waterY - submergeDepth;
        }

        return candidate;
    }

    private void CheckServerHitCollisions()
    {
        CollectEligiblePlayers();
        if (eligiblePlayers.Count == 0) return;

        float hitRadiusSqr = hitRadius * hitRadius;
        int capacity = pool.Capacity;

        for (int i = 0; i < capacity; i++)
        {
            FishProjectile fish = pool.GetSlot(i);
            if (fish == null || !fish.IsLeaping) continue;

            Vector3 fishPos = fish.CurrentPosition;

            for (int p = 0; p < eligiblePlayers.Count; p++)
            {
                PlayerMovement player = eligiblePlayers[p];
                if (!player.CanBeKnockedBack) continue;

                Vector3 delta = player.KnockbackPosition - fishPos;
                // Toleransi vertikal sedikit lebih longgar karena tinggi kapsul pemain ~2m
                delta.y *= 0.65f;

                if (delta.sqrMagnitude <= hitRadiusSqr)
                {
                    HandleServerFishHit(fish, player);
                    break;
                }
            }
        }
    }

    private void HandleServerFishHit(FishProjectile fish, PlayerMovement hitPlayer)
    {
        Vector3 hitDir = fish.HorizontalDirection;
        if (hitDir.sqrMagnitude < 0.001f)
        {
            hitDir = hitPlayer.KnockbackPosition - fish.CurrentPosition;
            hitDir.y = 0f;
            hitDir = hitDir.sqrMagnitude > 0.001f ? hitDir.normalized : Vector3.forward;
        }

        // Hempaskan pemain keluar dari kapal ke arah dorongan ikan + sedikit angkat ke atas
        Vector3 knockbackVelocity = hitDir * knockbackHorizontalSpeed + Vector3.up * knockbackUpwardSpeed;
        hitPlayer.ApplyFishKnockback(knockbackVelocity);

        // Hitung arah pantulan ikan (terpental ke belakang + menyamping acak + memantul ke atas lalu jatuh ke air)
        Vector3 side = Vector3.Cross(Vector3.up, hitDir);
        float sideKick = Random.Range(-0.55f, 0.55f);
        Vector3 bounceVel = (-hitDir + side * sideKick).normalized * fishBounceBackSpeed + Vector3.up * fishBounceUpSpeed;

        DeflectFishClientRpc((byte)fish.SlotIndex, fish.CurrentPosition, bounceVel);
    }

    [ClientRpc]
    private void LaunchFishClientRpc(
        byte slotIndex,
        Vector3 startPos,
        Vector3 endPos,
        float arcHeight,
        float submergeY,
        float telegraphDuration,
        float flightDuration)
    {
        if (pool == null) return;

        FishProjectile fish = pool.MarkSlotActive(slotIndex);
        if (fish == null) return;

        FishTrajectory trajectory = new FishTrajectory(
            startPos,
            endPos,
            arcHeight,
            submergeY,
            telegraphDuration,
            flightDuration);

        fish.Launch(trajectory, HandleFishCompleted);
    }

    [ClientRpc]
    private void DeflectFishClientRpc(byte slotIndex, Vector3 hitPosition, Vector3 bounceVelocity)
    {
        if (pool == null) return;

        FishProjectile fish = pool.GetSlot(slotIndex);
        if (fish != null)
        {
            fish.Deflect(hitPosition, bounceVelocity);
        }

        if (!string.IsNullOrEmpty(hitImpactSfxId) && AudioManager.Instance != null)
        {
            AudioManager.Instance.PlaySFX(hitImpactSfxId, 0.1f);
        }
    }

    private void HandleFishCompleted(FishProjectile fish)
    {
        pool?.Release(fish);
    }
}
