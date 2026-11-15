using System;
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

    [Header("Pengaturan Scene & Mode Testing")]
    [SerializeField] private string gameSceneName = "In_Game_Raka";
    [SerializeField] private int maxPlayers = 4;
    [Tooltip("Matikan saat tes di Editor jika Burst TLS bermasalah. Saat di-build ke WebGL, Relay WSS akan otomatis aktif.")]
    [SerializeField] private bool useRelayInEditor = false;

    [Header("Daftar Panel UI")]
    [SerializeField] private GameObject panelMainMenu;
    [SerializeField] private GameObject panelSelection;
    [SerializeField] private GameObject panelHost;
    [SerializeField] private GameObject panelJoin;

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
    [SerializeField] private TMP_Text[] playerTexts; // Masukkan Player1, Player2, Player3, Player4
    [SerializeField] private Button buttonPlayGame;
    [SerializeField] private Button buttonExitHost;

    [Header("UI Panel Join")]
    [SerializeField] private TMP_InputField inputFieldCode;
    [SerializeField] private Button buttonJoinRoom;
    [SerializeField] private Button buttonExitJoin;

    private bool isServicesInitialized = false;

    private async void Start()
    {
        ShowPanel(panelMainMenu);

        // Setup Listener Panel Main Menu
        buttonPlaySolo.onClick.AddListener(StartSoloGame);
        buttonPlayMultiplayer.onClick.AddListener(OpenSelectionPanel);

        // Setup Listener Panel Selection
        inputFieldNama.onValueChanged.AddListener(ValidateNameInput);
        buttonHost.onClick.AddListener(() => _ = StartHostMultiplayerAsync());
        buttonJoinSelection.onClick.AddListener(OpenJoinPanel);
        buttonExitSelection.onClick.AddListener(() => ShowPanel(panelMainMenu));

        // Setup Listener Panel Host
        buttonPlayGame.onClick.AddListener(StartMultiplayerMatch);
        buttonExitHost.onClick.AddListener(LeaveLobby);

        // Setup Listener Panel Join
        inputFieldCode.onValueChanged.AddListener(ValidateCodeInput);
        buttonJoinRoom.onClick.AddListener(() => _ = StartClientMultiplayerAsync());
        buttonExitJoin.onClick.AddListener(() => ShowPanel(panelSelection));

        ValidateNameInput(inputFieldNama.text);
        ValidateCodeInput(inputFieldCode.text);

        NetworkPlayerData.OnPlayerListChanged += UpdateLobbyPlayerListUI;
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
        }

        await InitializeUnityServicesAsync();
    }

    private void OnDestroy()
    {
        NetworkPlayerData.OnPlayerListChanged -= UpdateLobbyPlayerListUI;
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
                await UnityServices.InitializeAsync();
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

    private void ShowPanel(GameObject activePanel)
    {
        panelMainMenu.SetActive(activePanel == panelMainMenu);
        panelSelection.SetActive(activePanel == panelSelection);
        panelHost.SetActive(activePanel == panelHost);
        panelJoin.SetActive(activePanel == panelJoin);
    }

    private void OpenSelectionPanel()
    {
        ValidateNameInput(inputFieldNama.text);
        ShowPanel(panelSelection);
    }

    private void OpenJoinPanel()
    {
        LocalPlayerName = inputFieldNama.text.Trim();
        inputFieldCode.text = string.Empty;
        ValidateCodeInput(string.Empty);
        ShowPanel(panelJoin);
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
        return true; // Saat Build (WebGL / Standalone), selalu gunakan Relay
#endif
    }

    // --- LOGIKA SOLO ---

    private void StartSoloGame()
    {
        LocalPlayerName = "Solo Player";

        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.UseWebSockets = false;
        transport.SetConnectionData("127.0.0.1", 7777);

        if (NetworkManager.Singleton.StartHost())
        {
            NetworkManager.Singleton.SceneManager.LoadScene(gameSceneName, LoadSceneMode.Single);
        }
    }

    // --- LOGIKA MULTIPLAYER (HOST & JOIN) ---

    private async Task StartHostMultiplayerAsync()
    {
        LocalPlayerName = inputFieldNama.text.Trim();
        buttonHost.interactable = false;

        try
        {
            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            string joinCode = "LOCAL1";

            if (ShouldUseRelay())
            {
                if (!isServicesInitialized) await InitializeUnityServicesAsync();

                Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxPlayers - 1);
                joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

                transport.UseWebSockets = true;
                var relayServerData = AllocationUtils.ToRelayServerData(allocation, "wss");
                transport.SetRelayServerData(relayServerData);
            }
            else
            {
                // Mode Testing Lokal Editor (Bebas Error Burst TLS)
                transport.UseWebSockets = false;
                transport.SetConnectionData("127.0.0.1", 7777);
            }

            if (NetworkManager.Singleton.StartHost())
            {
                textCode.text = $"Code: {joinCode}";
                buttonPlayGame.gameObject.SetActive(true);
                ShowPanel(panelHost);
                UpdateLobbyPlayerListUI();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Gagal membuat Host: {e.Message}");
        }
        finally
        {
            ValidateNameInput(inputFieldNama.text);
        }
    }

    private async Task StartClientMultiplayerAsync()
    {
        string joinCode = inputFieldCode.text.Trim().ToUpper();
        buttonJoinRoom.interactable = false;

        try
        {
            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();

            if (ShouldUseRelay())
            {
                if (!isServicesInitialized) await InitializeUnityServicesAsync();

                JoinAllocation joinAllocation = await RelayService.Instance.JoinAllocationAsync(joinCode);

                transport.UseWebSockets = true;
                var relayServerData = AllocationUtils.ToRelayServerData(joinAllocation, "wss");
                transport.SetRelayServerData(relayServerData);
            }
            else
            {
                // Mode Testing Lokal Editor (Ketik kode apa saja minimal 4 huruf, misal LOCAL1)
                transport.UseWebSockets = false;
                transport.SetConnectionData("127.0.0.1", 7777);
            }

            if (NetworkManager.Singleton.StartClient())
            {
                textCode.text = $"Code: {joinCode}";
                buttonPlayGame.gameObject.SetActive(false);
                ShowPanel(panelHost);
                UpdateLobbyPlayerListUI();
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Gagal Join Room: {e.Message}");
            ValidateCodeInput(inputFieldCode.text);
        }
    }

    // --- LOGIKA LOBBY & TRANSISI SCENE ---

    private void UpdateLobbyPlayerListUI()
    {
        for (int i = 0; i < playerTexts.Length; i++)
        {
            if (i < NetworkPlayerData.ConnectedPlayers.Count)
            {
                string pName = NetworkPlayerData.ConnectedPlayers[i].PlayerName.Value.ToString();
                playerTexts[i].text = string.IsNullOrEmpty(pName) ? "Connecting..." : pName;
            }
            else
            {
                playerTexts[i].text = "Waiting for player...";
            }
        }
    }

    private void StartMultiplayerMatch()
    {
        if (NetworkManager.Singleton.IsHost)
        {
            NetworkManager.Singleton.SceneManager.LoadScene(gameSceneName, LoadSceneMode.Single);
        }
    }

    private void LeaveLobby()
    {
        if (NetworkManager.Singleton != null && (NetworkManager.Singleton.IsConnectedClient || NetworkManager.Singleton.IsHost))
        {
            NetworkManager.Singleton.Shutdown();
        }

        NetworkPlayerData.ConnectedPlayers.Clear();
        UpdateLobbyPlayerListUI();
        ShowPanel(panelSelection);
    }

    private void OnClientConnected(ulong clientId)
    {
        UpdateLobbyPlayerListUI();
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (clientId == NetworkManager.Singleton.LocalClientId || clientId == NetworkManager.ServerClientId)
        {
            NetworkPlayerData.ConnectedPlayers.Clear();
            ShowPanel(panelSelection);
        }
        else
        {
            UpdateLobbyPlayerListUI();
        }
    }
}