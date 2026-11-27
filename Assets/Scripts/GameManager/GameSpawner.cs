using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class GameSpawner : NetworkBehaviour
{
    public static GameSpawner Instance { get; private set; }

    /// <summary>Dipicu di semua client (termasuk Host) saat seluruh pemain siap dan kamera intro sungai dimulai.</summary>
    public static event Action OnIntroStarted;

    /// <summary>Dipicu di semua client (termasuk Host) saat kamera intro sungai selesai dan permainan dimulai.</summary>
    public static event Action OnIntroFinished;

    [Header("Pengaturan Spawn")]
    [SerializeField] private GameObject playerPrefab;
    [SerializeField] private Transform[] spawnPoints;
    [SerializeField] private NetworkObject perahuNetworkObject;

    [Header("Sinkronisasi Mulai Permainan")]
    [Tooltip("Interval diagnostik saat menunggu semua client selesai membuat world. Match tidak akan dipaksa mulai saat timeout.")]
    [SerializeField] private float maxWaitForAllPlayersSeconds = 30f;

    [Header("Kamera Intro Sungai")]
    [Tooltip("Tampilkan kamera tujuan akhir lalu meluncur menelusuri sungai ke kapal setelah semua pemain siap.")]
    [SerializeField] private bool playRiverIntroCamera = true;
    [Tooltip("Durasi cadangan intro jika komponen RiverIntroCamera belum ditemukan (detik).")]
    [SerializeField, Min(1f)] private float fallbackIntroDurationSeconds = 7.4f;

    [Header("Debug - Reproducible World Seed")]
    [Tooltip("Aktifkan hanya saat mengetes di Unity Editor. Semua client dalam sesi development harus memakai nilai seed yang sama.")]
    [SerializeField] private bool useFixedWorldSeedInEditor = false;
    [SerializeField] private int fixedEditorWorldSeed = 20261126;

    public NetworkVariable<bool> IsMatchStarted = new NetworkVariable<bool>(false);
    public NetworkVariable<bool> IsIntroPlaying = new NetworkVariable<bool>(false);
    public NetworkVariable<int> ReadyPlayerCount = new NetworkVariable<int>(0);
    public NetworkVariable<int> ExpectedPlayerCount = new NetworkVariable<int>(1);
    public NetworkVariable<int> WorldSeed = new NetworkVariable<int>(
        0,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    /// <summary>True jika world lokal selesai dibuat, kamera intro selesai, dan seluruh pemain sudah memulai match.</summary>
    public static bool IsMatchReady => Instance == null || (Instance.localWorldReady && Instance.IsMatchStarted.Value);
    public static bool IsIntroActive => Instance != null && Instance.IsIntroPlaying.Value;
    public bool IsLocalWorldReady => localWorldReady;

    private readonly HashSet<ulong> spawnedClientIds = new HashSet<ulong>();
    private readonly HashSet<ulong> readyClientIds = new HashSet<ulong>();
    private readonly SessionCharacterAllocator characterAllocator = new SessionCharacterAllocator();
    private bool sceneLoadEventReceived = false;
    private bool introTriggeredOnServer = false;
    private float waitTimer = 0f;
    private bool localWorldReady;
    private Coroutine worldBuildRoutine;
    private Coroutine serverIntroRoutine;

    private GameObject waitingOverlayObj;
    private TMP_Text waitingStatusText;
    private CanvasGroup waitingCanvasGroup;
    private Coroutine hideOverlayCoroutine;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Instance = null;
        OnIntroStarted = null;
        OnIntroFinished = null;
    }

    private void Awake()
    {
        Instance = this;
        CreateWaitingOverlay();
    }

    public override void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        IsMatchStarted.OnValueChanged += OnMatchStartedChanged;
        IsIntroPlaying.OnValueChanged += OnIntroPlayingChanged;
        ReadyPlayerCount.OnValueChanged += OnProgressChanged;
        ExpectedPlayerCount.OnValueChanged += OnProgressChanged;

        localWorldReady = false;

        if (IsServer)
        {
            IsMatchStarted.Value = false;
            IsIntroPlaying.Value = false;
            introTriggeredOnServer = false;
            spawnedClientIds.Clear();
            readyClientIds.Clear();
            characterAllocator.BeginNewSession(ResolveCharacterVariantCount());
            sceneLoadEventReceived = false;
            waitTimer = 0f;
            ExpectedPlayerCount.Value = Mathf.Max(1, NetworkManager.Singleton.ConnectedClientsIds.Count);
            ReadyPlayerCount.Value = 0;

            // Legacy compatibility only. The level is baked in the gameplay scene,
            // so runtime session/room seeds no longer control obstacle placement.
            WorldSeed.Value = 1;

            if (NetworkManager.Singleton.SceneManager != null)
                NetworkManager.Singleton.SceneManager.OnLoadEventCompleted += HandleSceneLoaded;
            NetworkManager.Singleton.OnClientDisconnectCallback += HandleClientDisconnected;
        }

        if (worldBuildRoutine != null) StopCoroutine(worldBuildRoutine);
        worldBuildRoutine = StartCoroutine(PrepareLocalWorldRoutine());

        UpdateWaitingOverlayVisual();

        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.PlayBGM("bgm_gameplay");
        }
    }

    public override void OnNetworkDespawn()
    {
        if (worldBuildRoutine != null)
        {
            StopCoroutine(worldBuildRoutine);
            worldBuildRoutine = null;
        }
        if (serverIntroRoutine != null)
        {
            StopCoroutine(serverIntroRoutine);
            serverIntroRoutine = null;
        }
        localWorldReady = false;
        introTriggeredOnServer = false;

        IsMatchStarted.OnValueChanged -= OnMatchStartedChanged;
        IsIntroPlaying.OnValueChanged -= OnIntroPlayingChanged;
        ReadyPlayerCount.OnValueChanged -= OnProgressChanged;
        ExpectedPlayerCount.OnValueChanged -= OnProgressChanged;

        if (IsServer && NetworkManager.Singleton != null)
        {
            if (NetworkManager.Singleton.SceneManager != null)
            {
                NetworkManager.Singleton.SceneManager.OnLoadEventCompleted -= HandleSceneLoaded;
            }
            NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnected;
        }
    }

    private IEnumerator PrepareLocalWorldRoutine()
    {
        // Level sudah di-bake di Editor dan tersimpan sebagai scene objects.
        // Tunggu sampai NGO selesai mem-spawn NetworkObject yang ditanam di scene
        // (terutama sampah hookable yang memakai TrashObstacle).
        const float spawnWaitTimeout = 10f;
        float waited = 0f;
        bool allSceneTrashSpawned = false;

        while (waited < spawnWaitTimeout)
        {
            allSceneTrashSpawned = true;
            TrashObstacle[] sceneTrash = FindObjectsByType<TrashObstacle>(FindObjectsSortMode.None);
            for (int i = 0; i < sceneTrash.Length; i++)
            {
                TrashObstacle trash = sceneTrash[i];
                if (trash == null || trash.gameObject.scene != gameObject.scene) continue;
                if (!trash.IsSpawned)
                {
                    allSceneTrashSpawned = false;
                    break;
                }
            }

            if (allSceneTrashSpawned) break;
            waited += Time.unscaledDeltaTime;
            yield return null;
        }

        if (!allSceneTrashSpawned)
        {
            Debug.LogError(
                "[GameSpawner] Ada TrashObstacle scene object yang belum di-spawn oleh NGO. " +
                "Pastikan Enable Scene Management aktif dan gameplay scene dimuat melalui NetworkManager.SceneManager.", this);
        }

        // World static is ready; no runtime generation, random seed, or loading stage is needed.
        localWorldReady = true;
        worldBuildRoutine = null;
        Debug.Log("[GameSpawner] Static scene world ready. Server: " + IsServer, this);
        UpdateWaitingOverlayVisual();

        if (IsServer)
        {
            RefreshExpectedAndCheckStart();
        }
    }

    private void Update()
    {
        if (!IsServer || !IsSpawned || IsMatchStarted.Value || IsIntroPlaying.Value || !localWorldReady) return;

        waitTimer += Time.unscaledDeltaTime;

        // Never force-start while a connected client may still be generating its local world.
        // A timeout is diagnostic only; disconnected clients are removed through the disconnect callback.
        if (sceneLoadEventReceived && waitTimer >= maxWaitForAllPlayersSeconds)
        {
            Debug.LogWarning($"[GameSpawner] Masih menunggu kesiapan world/player ({readyClientIds.Count}/{ExpectedPlayerCount.Value}). Match tetap ditahan agar layout tidak desinkron.");
            waitTimer = 0f;
        }
    }

    private void HandleSceneLoaded(string sceneName, LoadSceneMode loadSceneMode, List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
    {
        if (sceneName != gameObject.scene.name) return;

        sceneLoadEventReceived = true;
        waitTimer = 0f;

        // Pastikan semua client yang berhasil memuat scene di-spawn
        for (int i = 0; i < clientsCompleted.Count; i++)
        {
            ulong clientId = clientsCompleted[i];
            if (!spawnedClientIds.Contains(clientId))
            {
                SpawnPlayerForClient(clientId, i);
            }
        }

        RefreshExpectedAndCheckStart();
    }

    private void SpawnPlayerForClient(ulong clientId, int spawnIndex)
    {
        if (playerPrefab == null || spawnPoints == null || spawnPoints.Length == 0) return;

        Transform spawnPoint = spawnPoints[spawnIndex % spawnPoints.Length];
        GameObject playerInstance = Instantiate(playerPrefab, spawnPoint.position, spawnPoint.rotation);

        NetworkObject netObj = playerInstance.GetComponent<NetworkObject>();

        // Tandai clientId sudah di-spawn SEBELUM SpawnAsPlayerObject dipanggil.
        // Pada Host, SpawnAsPlayerObject langsung memicu PlayerMovement.OnNetworkSpawn()
        // yang secara sinkron memanggil NotifyClientPlayerReadyRpc() sebelum baris di bawah ini selesai.
        spawnedClientIds.Add(clientId);
        netObj.SpawnAsPlayerObject(clientId, true);

        // Berikan slot karakter unik per pemain (disinkronkan otomatis lewat NetworkVariable)
        PlayerCharacterAppearance appearance = playerInstance.GetComponent<PlayerCharacterAppearance>();
        if (appearance != null)
        {
            appearance.AssignCharacterIndex(AllocateUniqueCharacterSlot(clientId));
        }

        // Kaitkan pemain sebagai child dari kapal dengan worldPositionStays = true
        if (perahuNetworkObject != null && netObj.transform.parent != perahuNetworkObject.transform)
        {
            netObj.TrySetParent(perahuNetworkObject, true);
        }
    }

    private int ResolveCharacterVariantCount()
    {
        if (playerPrefab != null)
        {
            PlayerCharacterAppearance prefabAppearance = playerPrefab.GetComponent<PlayerCharacterAppearance>();
            if (prefabAppearance != null && prefabAppearance.CatalogVariantCount > 0)
            {
                return prefabAppearance.CatalogVariantCount;
            }
        }
        return 4;
    }

    private int AllocateUniqueCharacterSlot(ulong clientId)
    {
        return characterAllocator.Allocate(clientId);
    }

    private void HandleClientDisconnected(ulong clientId)
    {
        if (!IsServer) return;

        characterAllocator.Release(clientId);
        ClearDisconnectedPlayerBoatInputs(clientId);

        if (IsMatchStarted.Value) return;

        spawnedClientIds.Remove(clientId);
        readyClientIds.Remove(clientId);
        RefreshExpectedAndCheckStart();
    }

    private void ClearDisconnectedPlayerBoatInputs(ulong clientId)
    {
        BoatController boat = perahuNetworkObject != null
            ? perahuNetworkObject.GetComponent<BoatController>()
            : FindFirstObjectByType<BoatController>();

        if (boat != null)
        {
            boat.ClearPlayerInputs(clientId);
        }
    }

    /// <summary>
    /// Dipanggil oleh setiap client pemilik PlayerObject begitu objek Player selesai spawn & menempel di kapal.
    /// </summary>
    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    public void NotifyClientPlayerReadyRpc(RpcParams rpcParams = default)
    {
        if (!IsServer || IsMatchStarted.Value || IsIntroPlaying.Value || NetworkManager.Singleton == null) return;

        ulong senderId = rpcParams.Receive.SenderClientId;
        if (!IsClientConnected(senderId)) return;

        // Catat kesiapan client walaupun server masih menyelesaikan PrepareLocalWorldRoutine()
        // atau urutan spawn baru saja berlangsung, agar sinyal siap tidak pernah terbuang.
        bool newlyReady = readyClientIds.Add(senderId);
        if (newlyReady)
        {
            Debug.Log($"[GameSpawner] Client {senderId} siap ({readyClientIds.Count}/{NetworkManager.Singleton.ConnectedClientsIds.Count}).");
        }

        RefreshExpectedAndCheckStart();
    }

    private bool IsClientConnected(ulong clientId)
    {
        if (NetworkManager.Singleton == null) return false;
        var connected = NetworkManager.Singleton.ConnectedClientsIds;
        for (int i = 0; i < connected.Count; i++)
        {
            if (connected[i] == clientId) return true;
        }
        return false;
    }

    private void RefreshExpectedAndCheckStart()
    {
        if (!IsServer || IsMatchStarted.Value || NetworkManager.Singleton == null) return;

        var connected = NetworkManager.Singleton.ConnectedClientsIds;
        int expected = Mathf.Max(1, connected.Count);
        ExpectedPlayerCount.Value = expected;

        int readyConnectedCount = 0;
        bool allSpawned = sceneLoadEventReceived;
        bool allReady = sceneLoadEventReceived;

        for (int i = 0; i < connected.Count; i++)
        {
            ulong cid = connected[i];
            if (!spawnedClientIds.Contains(cid))
            {
                allSpawned = false;
            }
            if (readyClientIds.Contains(cid))
            {
                readyConnectedCount++;
            }
            else
            {
                allReady = false;
            }
        }

        ReadyPlayerCount.Value = readyConnectedCount;

        if (localWorldReady && allSpawned && allReady && connected.Count > 0)
        {
            BeginIntroOrStartMatchOnServer();
        }
    }

    private void BeginIntroOrStartMatchOnServer()
    {
        if (!IsServer || IsMatchStarted.Value || introTriggeredOnServer || !localWorldReady) return;

        if (playRiverIntroCamera && RiverIntroCamera.CanPlayIntro)
        {
            introTriggeredOnServer = true;
            IsIntroPlaying.Value = true;
            Debug.Log($"[GameSpawner] Seluruh pemain siap ({ReadyPlayerCount.Value}/{ExpectedPlayerCount.Value}). Memulai kamera intro sungai...");

            if (serverIntroRoutine != null) StopCoroutine(serverIntroRoutine);
            serverIntroRoutine = StartCoroutine(ServerIntroSequenceRoutine());
        }
        else
        {
            StartMatchOnServer();
        }
    }

    private IEnumerator ServerIntroSequenceRoutine()
    {
        float duration = RiverIntroCamera.Instance != null
            ? RiverIntroCamera.Instance.TotalDuration
            : fallbackIntroDurationSeconds;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            yield return null;
        }

        serverIntroRoutine = null;
        IsIntroPlaying.Value = false;
        StartMatchOnServer();
    }

    private void StartMatchOnServer()
    {
        if (IsMatchStarted.Value || !localWorldReady) return;
        IsIntroPlaying.Value = false;
        IsMatchStarted.Value = true;
        Debug.Log($"[GameSpawner] Seluruh pemain siap ({ReadyPlayerCount.Value}/{ExpectedPlayerCount.Value}). Permainan dimulai!");
    }

    private void OnIntroPlayingChanged(bool previous, bool current)
    {
        UpdateWaitingOverlayVisual();
        if (current)
        {
            OnIntroStarted?.Invoke();
        }
        else if (previous)
        {
            OnIntroFinished?.Invoke();
        }
    }

    private void OnMatchStartedChanged(bool previous, bool current)
    {
        UpdateWaitingOverlayVisual();
        if (current)
        {
            OnIntroFinished?.Invoke();
        }
    }

    private void OnProgressChanged(int previous, int current)
    {
        UpdateWaitingOverlayVisual();
    }

    private void UpdateWaitingOverlayVisual()
    {
        if (waitingOverlayObj == null) return;

        bool waiting = !localWorldReady || (!IsMatchStarted.Value && !IsIntroPlaying.Value);
        if (waiting)
        {
            if (hideOverlayCoroutine != null)
            {
                StopCoroutine(hideOverlayCoroutine);
                hideOverlayCoroutine = null;
            }

            waitingOverlayObj.SetActive(true);
            if (waitingCanvasGroup != null) waitingCanvasGroup.alpha = 1f;

            if (waitingStatusText != null)
            {
                if (!localWorldReady)
                {
                    waitingStatusText.text = "Menyiapkan dunia sungai...";
                }
                else
                {
                    int ready = ReadyPlayerCount.Value;
                    int expected = Mathf.Max(1, ExpectedPlayerCount.Value);
                    waitingStatusText.text = expected > 1
                        ? $"Menunggu semua pemain siap... ({ready}/{expected})"
                        : "Menyiapkan permainan...";
                }
            }
        }
        else if (waitingOverlayObj.activeSelf)
        {
            if (waitingStatusText != null)
            {
                waitingStatusText.text = "Semua pemain siap!";
            }

            if (hideOverlayCoroutine != null) StopCoroutine(hideOverlayCoroutine);
            hideOverlayCoroutine = StartCoroutine(FadeOutWaitingOverlayRoutine());
        }
    }

    private IEnumerator FadeOutWaitingOverlayRoutine()
    {
        yield return new WaitForSecondsRealtime(0.35f);

        if (waitingCanvasGroup != null)
        {
            float t = 0f;
            const float dur = 0.25f;
            while (t < dur)
            {
                t += Time.unscaledDeltaTime;
                waitingCanvasGroup.alpha = Mathf.Lerp(1f, 0f, Mathf.Clamp01(t / dur));
                yield return null;
            }
            waitingCanvasGroup.alpha = 0f;
        }

        waitingOverlayObj.SetActive(false);
        hideOverlayCoroutine = null;
    }

    private void CreateWaitingOverlay()
    {
        GameObject canvasObj = new GameObject("MatchReadyCanvas");
        canvasObj.transform.SetParent(transform, false);

        Canvas canvas = canvasObj.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 500;

        CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        waitingCanvasGroup = canvasObj.AddComponent<CanvasGroup>();
        waitingCanvasGroup.interactable = false;
        waitingCanvasGroup.blocksRaycasts = true; // Cegah klik nyasar saat menunggu pemain lain spawn

        // Panel banner lembut di tengah-bawah/tengah layar
        GameObject pillObj = new GameObject("StatusBanner", typeof(RectTransform), typeof(Image));
        pillObj.transform.SetParent(canvasObj.transform, false);
        RectTransform pillRt = pillObj.GetComponent<RectTransform>();
        pillRt.anchorMin = new Vector2(0.5f, 0.5f);
        pillRt.anchorMax = new Vector2(0.5f, 0.5f);
        pillRt.sizeDelta = new Vector2(680f, 84f);
        pillRt.anchoredPosition = Vector2.zero;

        Image pillBg = pillObj.GetComponent<Image>();
        pillBg.color = new Color(0.08f, 0.11f, 0.16f, 0.84f);
        pillBg.raycastTarget = false;

        GameObject txtObj = new GameObject("StatusText", typeof(RectTransform));
        txtObj.transform.SetParent(pillObj.transform, false);
        waitingStatusText = txtObj.AddComponent<TextMeshProUGUI>();
        waitingStatusText.fontSize = 30f;
        waitingStatusText.fontStyle = FontStyles.Bold;
        waitingStatusText.alignment = TextAlignmentOptions.Center;
        waitingStatusText.color = Color.white;
        waitingStatusText.text = "Menyiapkan permainan...";
        waitingStatusText.raycastTarget = false;

        RectTransform txtRt = waitingStatusText.rectTransform;
        txtRt.anchorMin = Vector2.zero;
        txtRt.anchorMax = Vector2.one;
        txtRt.offsetMin = new Vector2(20f, 8f);
        txtRt.offsetMax = new Vector2(-20f, -8f);

        waitingOverlayObj = canvasObj;
        waitingOverlayObj.SetActive(false);
    }
}
