using System;
using System.Collections;
using System.Linq;
using System.Threading.Tasks;
using TMPro;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class MainMenuController : MonoBehaviour
{
    public static string LocalPlayerName { get; private set; } = "Player";
    public static bool IsSoloMode { get; private set; } = false;

    [Header("Pengaturan Scene & Mode Testing")]
    [SerializeField] private string gameSceneName = "In_Game_Raka";
    [SerializeField] private int maxPlayers = 4;
    [Tooltip("Aktifkan agar bisa bermain lintas perangkat/jaringan dari Editor maupun MPPM lewat Unity Relay. Matikan hanya untuk tes offline localhost (127.0.0.1).")]
    [SerializeField] private bool useRelayInEditor = true;
    [SerializeField] private float clientConnectTimeout = 12f;

    [Header("Daftar Panel UI")]
    [SerializeField] private GameObject panelMainMenu;
    [SerializeField] private GameObject panelSelection;
    [SerializeField] private GameObject panelHost;
    [SerializeField] private GameObject panelJoin;

    [Header("Komponen Juicing UI (Opsional)")]
    [SerializeField] private MenuPanelTransitioner panelTransitioner;
    [SerializeField] private UIInputFieldJuice codeInputJuice;
    [SerializeField] private LobbyUIJuice lobbyUIJuice;

    [Header("UI Panel Main Menu")]
    [SerializeField] private Button buttonPlaySolo;
    [SerializeField] private Button buttonPlayMultiplayer;

    [Header("UI Panel Selection")]
    [SerializeField] private TMP_InputField inputFieldNama;
    [SerializeField] private Button buttonHost;
    [SerializeField] private Button buttonJoinSelection;
    [SerializeField] private Button buttonExitSelection;

    [Header("UI Panel Host (Lobby)")]
    [SerializeField] private TMP_Text textCode;
    [SerializeField] private TMP_Text[] playerTexts;
    [SerializeField] private Button buttonPlayGame;
    [SerializeField] private Button buttonExitHost;

    [Header("UI Panel Join")]
    [SerializeField] private TMP_InputField inputFieldCode;
    [SerializeField] private Button buttonJoinRoom;
    [SerializeField] private Button buttonExitJoin;

    private bool isServicesInitialized = false;
    private Coroutine loadingDotsCoroutine;
    private Coroutine clientConnectTimeoutCoroutine;
    private bool isWaitingForClientConnect = false;
    private string pendingJoinCode = string.Empty;
    private TMP_Text pendingJoinLabel;
    private string pendingJoinOriginalText = string.Empty;

    private async void Start()
    {
        IsSoloMode = false;
        NetworkPlayerData.ConnectedPlayers.Clear();
        RiverWorldSeedContext.ClearRoomCode();

        if (panelTransitioner != null)
        {
            panelTransitioner.SetInitialPanel(
                panelMainMenu,
                new[] { panelMainMenu, panelSelection, panelHost, panelJoin },
                false
            );
        }
        else
        {
            ShowPanelInstant(panelMainMenu);
        }

        // Setup Listener Panel Main Menu
        buttonPlaySolo.onClick.AddListener(StartSoloGame);
        buttonPlayMultiplayer.onClick.AddListener(OpenSelectionPanel);

        // Setup Listener Panel Selection
        inputFieldNama.onValueChanged.AddListener(ValidateNameInput);
        buttonHost.onClick.AddListener(() => _ = StartHostMultiplayerAsync());
        buttonJoinSelection.onClick.AddListener(OpenJoinPanel);
        buttonExitSelection.onClick.AddListener(() =>
            SwitchPanel(panelMainMenu, MenuPanelTransitioner.TransitionStyle.DropOut, false));

        // Setup Listener Panel Host
        buttonPlayGame.onClick.AddListener(StartMultiplayerMatch);
        buttonExitHost.onClick.AddListener(LeaveLobby);

        // Setup Listener Panel Join
        inputFieldCode.onValueChanged.AddListener(ValidateCodeInput);
        buttonJoinRoom.onClick.AddListener(() => _ = StartClientMultiplayerAsync());
        buttonExitJoin.onClick.AddListener(() =>
            SwitchPanel(panelSelection, MenuPanelTransitioner.TransitionStyle.SlideFromLeft, true));

        ValidateNameInput(inputFieldNama.text);
        ValidateCodeInput(inputFieldCode.text);

        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.PlayBGM("bgm_main_menu");
        }

        NetworkLobbyManager.OnLobbyUpdated += UpdateLobbyPlayerListUI;
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        }

        await InitializeUnityServicesAsync();
    }

    private void OnDestroy()
    {
        NetworkLobbyManager.OnLobbyUpdated -= UpdateLobbyPlayerListUI;
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
        }
    }

    private async Task InitializeUnityServicesAsync()
    {
        try
        {
            if (UnityServices.State == ServicesInitializationState.Uninitialized)
            {
                InitializationOptions options = new InitializationOptions();
#if UNITY_EDITOR
                // Gunakan profil unik per instance Editor/MPPM agar sesi Auth tidak bentrok saat tes 2 Play Unity
                string editorProfile = $"ed_{Mathf.Abs(Application.dataPath.GetHashCode()):X8}";
                options.SetProfile(editorProfile);
#endif
                await UnityServices.InitializeAsync(options);
            }

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
            }

            isServicesInitialized = true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Unity Services belum aktif (abaikan jika tes lokal): {e.Message}");
        }
    }

    // --- NAVIGASI PANEL & VALIDASI ---

    private void SwitchPanel(GameObject targetPanel, MenuPanelTransitioner.TransitionStyle style, bool showBackdrop)
    {
        if (panelTransitioner != null)
        {
            panelTransitioner.TransitionTo(targetPanel, style, showBackdrop);
        }
        else
        {
            ShowPanelInstant(targetPanel);
        }
    }

    private void ShowPanelInstant(GameObject activePanel)
    {
        panelMainMenu.SetActive(activePanel == panelMainMenu);
        panelSelection.SetActive(activePanel == panelSelection);
        panelHost.SetActive(activePanel == panelHost);
        panelJoin.SetActive(activePanel == panelJoin);
    }

    private void OpenSelectionPanel()
    {
        IsSoloMode = false;
        ValidateNameInput(inputFieldNama.text);
        SwitchPanel(panelSelection, MenuPanelTransitioner.TransitionStyle.RiseTilt, true);
    }

    private void OpenJoinPanel()
    {
        LocalPlayerName = inputFieldNama.text.Trim();
        inputFieldCode.text = string.Empty;
        ValidateCodeInput(string.Empty);
        SwitchPanel(panelJoin, MenuPanelTransitioner.TransitionStyle.SlideFromRight, true);
    }

    private void ValidateNameInput(string currentName)
    {
        bool isValid = !string.IsNullOrWhiteSpace(currentName);
        buttonHost.interactable = isValid;
        buttonJoinSelection.interactable = isValid;
    }

    private void ValidateCodeInput(string currentCode)
    {
        buttonJoinRoom.interactable = !string.IsNullOrWhiteSpace(currentCode) && currentCode.Trim().Length >= 4;
    }

    private bool ShouldUseRelay()
    {
#if UNITY_EDITOR
        return useRelayInEditor;
#else
        return true;
#endif
    }

    private static string GetRelayConnectionType(out bool useWebSockets)
    {
        // Target platform adalah WebGL (Web), sehingga Unity Relay memakai WebSocket Secure ("wss")
        // baik saat dijalankan di Build WebGL maupun di Unity Editor.
        useWebSockets = true;
        return "wss";
    }

    // --- ANIMASI LOADING DOTS SAAT MENUNGGU RELAY ---

    private TMP_Text StartButtonLoading(Button targetButton, out string originalLabel)
    {
        TMP_Text label = targetButton.GetComponentInChildren<TMP_Text>();
        originalLabel = label != null ? label.text : string.Empty;

        if (label != null)
        {
            if (loadingDotsCoroutine != null) StopCoroutine(loadingDotsCoroutine);
            loadingDotsCoroutine = StartCoroutine(LoadingDotsRoutine(label));
        }

        return label;
    }

    private void StopButtonLoading(TMP_Text label, string originalLabel)
    {
        if (loadingDotsCoroutine != null)
        {
            StopCoroutine(loadingDotsCoroutine);
            loadingDotsCoroutine = null;
        }

        if (label != null)
        {
            label.text = originalLabel;
        }
    }

    private IEnumerator LoadingDotsRoutine(TMP_Text label)
    {
        string[] frames = { "LOADING.", "LOADING..", "LOADING..." };
        int idx = 0;
        while (true)
        {
            label.text = frames[idx];
            idx = (idx + 1) % frames.Length;
            yield return new WaitForSecondsRealtime(0.28f);
        }
    }

    // --- LOGIKA SOLO ---

    private void StartSoloGame()
    {
        if (NetworkManager.Singleton == null) return;

        RiverWorldSeedContext.ClearRoomCode();
        buttonPlaySolo.interactable = false;
        IsSoloMode = true;
        LocalPlayerName = string.Empty;

        Action launchSolo = () =>
        {
            if (NetworkManager.Singleton == null)
            {
                buttonPlaySolo.interactable = true;
                SceneFadeTransition.Instance?.CancelAndFadeIn();
                return;
            }

            // Pakai OfflineSoloTransport agar StartHost() tidak membuka soket UDP/WebSocket
            // (browser WebGL melarang listening socket lokal dan sebelumnya memicu disconnect ke Panel Selection).
            OfflineSoloTransport soloTransport = NetworkManager.Singleton.GetComponent<OfflineSoloTransport>();
            if (soloTransport == null)
            {
                soloTransport = NetworkManager.Singleton.gameObject.AddComponent<OfflineSoloTransport>();
            }
            NetworkManager.Singleton.NetworkConfig.NetworkTransport = soloTransport;

            if (NetworkManager.Singleton.StartHost())
            {
                NetworkManager.Singleton.SceneManager.LoadScene(gameSceneName, LoadSceneMode.Single);
            }
            else
            {
                IsSoloMode = false;
                buttonPlaySolo.interactable = true;
                SceneFadeTransition.Instance?.CancelAndFadeIn();
            }
        };

        if (SceneFadeTransition.Instance != null)
        {
            SceneFadeTransition.Instance.FadeOutAndInvoke(launchSolo);
        }
        else
        {
            launchSolo.Invoke();
        }
    }

    private UnityTransport EnsureUnityTransport()
    {
        if (NetworkManager.Singleton == null) return null;
        UnityTransport transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        if (transport != null)
        {
            NetworkManager.Singleton.NetworkConfig.NetworkTransport = transport;
        }
        return transport;
    }

    // --- LOGIKA MULTIPLAYER (HOST & JOIN) ---

    private async Task StartHostMultiplayerAsync()
    {
        IsSoloMode = false;
        LocalPlayerName = inputFieldNama.text.Trim();
        buttonHost.interactable = false;
        buttonJoinSelection.interactable = false;
        TMP_Text hostLabel = StartButtonLoading(buttonHost, out string originalText);

        try
        {
            var transport = EnsureUnityTransport();
            string joinCode = GenerateRandomCode(6);

            if (ShouldUseRelay())
            {
                if (!isServicesInitialized) await InitializeUnityServicesAsync();
                if (!isServicesInitialized)
                {
                    throw new InvalidOperationException("Unity Services / Authentication gagal diinisialisasi. Pastikan internet aktif.");
                }

                Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxPlayers - 1);
                joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

                string connType = GetRelayConnectionType(out bool useWs);
                transport.UseWebSockets = useWs;
                var relayServerData = AllocationUtils.ToRelayServerData(allocation, connType);
                transport.SetRelayServerData(relayServerData);
                Debug.Log($"[Lobby] Host Relay siap (protocol={connType}, code={joinCode})");
            }
            else
            {
                transport.UseWebSockets = false;
                transport.SetConnectionData("127.0.0.1", 7777);
                Debug.Log($"[Lobby] Host mode lokal (127.0.0.1:7777, code={joinCode})");
            }

            lobbyUIJuice?.ResetSlots();
            RiverWorldSeedContext.SetRoomCode(joinCode);

            if (NetworkManager.Singleton.StartHost())
            {
                textCode.text = $"{joinCode}";
                buttonPlayGame.gameObject.SetActive(true);
                SwitchPanel(panelHost, MenuPanelTransitioner.TransitionStyle.PlantSign, true);
                UpdateLobbyPlayerListUI();
            }
            else
            {
                RiverWorldSeedContext.ClearRoomCode();
                ValidateNameInput(inputFieldNama.text);
            }
        }
        catch (Exception e)
        {
            RiverWorldSeedContext.ClearRoomCode();
            Debug.LogError($"Gagal membuat Host: {e.Message}");
            ValidateNameInput(inputFieldNama.text);
        }
        finally
        {
            StopButtonLoading(hostLabel, originalText);
        }
    }

    private string GenerateRandomCode(int length)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        System.Random random = new System.Random();
        return new string(Enumerable.Repeat(chars, length)
            .Select(s => s[random.Next(s.Length)]).ToArray());
    }

    private async Task StartClientMultiplayerAsync()
    {
        if (isWaitingForClientConnect) return;

        IsSoloMode = false;
        string joinCode = inputFieldCode.text.Trim().ToUpper();
        buttonJoinRoom.interactable = false;
        buttonExitJoin.interactable = false;
        pendingJoinLabel = StartButtonLoading(buttonJoinRoom, out pendingJoinOriginalText);

        bool startedConnecting = false;
        try
        {
            var transport = EnsureUnityTransport();

            if (ShouldUseRelay())
            {
                if (!isServicesInitialized) await InitializeUnityServicesAsync();
                if (!isServicesInitialized)
                {
                    throw new InvalidOperationException("Unity Services / Authentication gagal diinisialisasi. Pastikan internet aktif.");
                }

                JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);

                string connType = GetRelayConnectionType(out bool useWs);
                transport.UseWebSockets = useWs;
                var relayServerData = AllocationUtils.ToRelayServerData(joinAllocation, connType);
                transport.SetRelayServerData(relayServerData);
                Debug.Log($"[Lobby] Client Relay siap (protocol={connType}, code={joinCode})");
            }
            else
            {
                transport.UseWebSockets = false;
                transport.SetConnectionData("127.0.0.1", 7777);
                Debug.Log($"[Lobby] Client mode lokal mencoba konek ke 127.0.0.1:7777");
            }

            lobbyUIJuice?.ResetSlots();

            if (NetworkManager.Singleton.StartClient())
            {
                // Jangan langsung pindah ke Panel_Host sebelum koneksi ke server benar-benar berhasil!
                startedConnecting = true;
                isWaitingForClientConnect = true;
                pendingJoinCode = joinCode;

                if (clientConnectTimeoutCoroutine != null) StopCoroutine(clientConnectTimeoutCoroutine);
                clientConnectTimeoutCoroutine = StartCoroutine(ClientConnectTimeoutRoutine());
            }
            else
            {
                codeInputJuice?.PlayErrorShake();
                ValidateCodeInput(inputFieldCode.text);
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Gagal Join Room: {e.Message}");
            codeInputJuice?.PlayErrorShake();
            ValidateCodeInput(inputFieldCode.text);
        }
        finally
        {
            if (!startedConnecting)
            {
                StopButtonLoading(pendingJoinLabel, pendingJoinOriginalText);
                buttonExitJoin.interactable = true;
            }
        }
    }

    private IEnumerator ClientConnectTimeoutRoutine()
    {
        float timeout = Mathf.Max(3f, clientConnectTimeout);
        yield return new WaitForSecondsRealtime(timeout);

        if (isWaitingForClientConnect)
        {
            Debug.LogWarning($"[Lobby] Koneksi Client timeout setelah {timeout:F0} detik (kode='{pendingJoinCode}'). Pastikan Host aktif dan mode jaringan cocok.");
            AbortPendingClientJoin();
        }
    }

    private void AbortPendingClientJoin()
    {
        RiverWorldSeedContext.ClearRoomCode();
        isWaitingForClientConnect = false;
        pendingJoinCode = string.Empty;

        if (clientConnectTimeoutCoroutine != null)
        {
            StopCoroutine(clientConnectTimeoutCoroutine);
            clientConnectTimeoutCoroutine = null;
        }

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening && !NetworkManager.Singleton.IsHost)
        {
            NetworkManager.Singleton.Shutdown();
        }

        StopButtonLoading(pendingJoinLabel, pendingJoinOriginalText);
        buttonExitJoin.interactable = true;
        codeInputJuice?.PlayErrorShake();
        ValidateCodeInput(inputFieldCode.text);
    }

    // --- LOGIKA LOBBY & TRANSISI SCENE ---

    private void UpdateLobbyPlayerListUI()
    {
        if (NetworkLobbyManager.Instance == null || NetworkLobbyManager.Instance.LobbyPlayers == null) return;

        bool isListening = NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
        var players = NetworkLobbyManager.Instance.LobbyPlayers;
        int count = isListening ? players.Count : 0;

        for (int i = 0; i < playerTexts.Length; i++)
        {
            if (i < count)
            {
                string pName = players[i].PlayerName.ToString();
                if (players[i].IsHost)
                {
                    pName += " (Host)";
                }

                if (lobbyUIJuice != null)
                {
                    lobbyUIJuice.UpdatePlayerSlot(i, true, pName);
                }
                else
                {
                    playerTexts[i].text = pName;
                }

                if (NetworkManager.Singleton != null && players[i].ClientId == NetworkManager.Singleton.LocalClientId)
                {
                    buttonPlayGame.gameObject.SetActive(players[i].IsHost);
                }
            }
            else
            {
                if (lobbyUIJuice != null)
                {
                    lobbyUIJuice.UpdatePlayerSlot(i, false, string.Empty);
                }
                else
                {
                    playerTexts[i].text = "Waiting for player...";
                }
            }
        }
    }

    private void StartMultiplayerMatch()
    {
        if (!NetworkManager.Singleton.IsHost) return;

        buttonPlayGame.interactable = false;
        buttonExitHost.interactable = false;

        if (SceneFadeTransition.Instance != null)
        {
            NetworkLobbyManager.Instance?.NotifyClientsToFadeOut();

            SceneFadeTransition.Instance.FadeOutAndInvoke(() =>
            {
                if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost)
                {
                    NetworkManager.Singleton.SceneManager.LoadScene(gameSceneName, LoadSceneMode.Single);
                }
            });
        }
        else
        {
            NetworkManager.Singleton.SceneManager.LoadScene(gameSceneName, LoadSceneMode.Single);
        }
    }

    private async void LeaveLobby()
    {
        RiverWorldSeedContext.ClearRoomCode();
        buttonExitHost.interactable = false;

        if (NetworkManager.Singleton != null && (NetworkManager.Singleton.IsConnectedClient || NetworkManager.Singleton.IsHost))
        {
            NetworkManager.Singleton.Shutdown();

            while (NetworkManager.Singleton.IsListening)
            {
                await Task.Yield();
            }
        }

        lobbyUIJuice?.ResetSlots();
        UpdateLobbyPlayerListUI();
        SwitchPanel(panelSelection, MenuPanelTransitioner.TransitionStyle.DropOut, true);

        buttonExitHost.interactable = true;
        ValidateNameInput(inputFieldNama.text);
    }

    private void OnClientConnected(ulong clientId)
    {
        if (NetworkManager.Singleton == null) return;

        Debug.Log($"[Lobby] OnClientConnected: clientId={clientId}, localClientId={NetworkManager.Singleton.LocalClientId}, isHost={NetworkManager.Singleton.IsHost}");

        // Client baru benar-benar masuk ke Panel_Host setelah koneksi diterima Server
        if (isWaitingForClientConnect && !NetworkManager.Singleton.IsHost && clientId == NetworkManager.Singleton.LocalClientId)
        {
            isWaitingForClientConnect = false;
            if (clientConnectTimeoutCoroutine != null)
            {
                StopCoroutine(clientConnectTimeoutCoroutine);
                clientConnectTimeoutCoroutine = null;
            }

            StopButtonLoading(pendingJoinLabel, pendingJoinOriginalText);
            buttonExitJoin.interactable = true;

            textCode.text = $"{pendingJoinCode}";
            buttonPlayGame.gameObject.SetActive(false);
            SwitchPanel(panelHost, MenuPanelTransitioner.TransitionStyle.PlantSign, true);
        }

        UpdateLobbyPlayerListUI();
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (NetworkManager.Singleton == null) return;

        if (IsSoloMode)
        {
            SceneFadeTransition.Instance?.CancelAndFadeIn();
            buttonPlaySolo.interactable = true;
            return;
        }

        string reason = NetworkManager.Singleton.DisconnectReason;
        Debug.LogWarning($"[Lobby] OnClientDisconnected: clientId={clientId}, localClientId={NetworkManager.Singleton.LocalClientId}, reason='{reason}'");

        if (isWaitingForClientConnect && !NetworkManager.Singleton.IsHost &&
            (clientId == NetworkManager.Singleton.LocalClientId || clientId == NetworkManager.ServerClientId))
        {
            AbortPendingClientJoin();
            return;
        }

        if (clientId == NetworkManager.Singleton.LocalClientId || clientId == NetworkManager.ServerClientId)
        {
            // Hanya Server yang berhak memanggil Clear() pada NetworkList
            if (NetworkManager.Singleton.IsServer &&
                NetworkLobbyManager.Instance != null &&
                NetworkLobbyManager.Instance.LobbyPlayers != null)
            {
                NetworkLobbyManager.Instance.LobbyPlayers.Clear();
            }

            SceneFadeTransition.Instance?.CancelAndFadeIn();
            lobbyUIJuice?.ResetSlots();
            textCode.text = string.Empty;
            UpdateLobbyPlayerListUI();
            SwitchPanel(panelSelection, MenuPanelTransitioner.TransitionStyle.DropOut, true);

            buttonPlayGame.interactable = true;
            buttonExitHost.interactable = true;
            ValidateNameInput(inputFieldNama.text);
        }
        else
        {
            UpdateLobbyPlayerListUI();
        }
    }
}