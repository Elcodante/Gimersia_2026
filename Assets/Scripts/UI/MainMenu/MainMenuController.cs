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

    private async void Start()
    {
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
        buttonPlaySolo.interactable = false;
        LocalPlayerName = "Solo Player";

        Action launchSolo = () =>
        {
            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            transport.UseWebSockets = false;
            transport.SetConnectionData("127.0.0.1", 7777);

            if (NetworkManager.Singleton.StartHost())
            {
                NetworkManager.Singleton.SceneManager.LoadScene(gameSceneName, LoadSceneMode.Single);
            }
            else
            {
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

    // --- LOGIKA MULTIPLAYER (HOST & JOIN) ---

    private async Task StartHostMultiplayerAsync()
    {
        LocalPlayerName = inputFieldNama.text.Trim();
        buttonHost.interactable = false;
        buttonJoinSelection.interactable = false;
        TMP_Text hostLabel = StartButtonLoading(buttonHost, out string originalText);

        try
        {
            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            string joinCode = GenerateRandomCode(6);

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
                transport.UseWebSockets = false;
                transport.SetConnectionData("127.0.0.1", 7777);
            }

            lobbyUIJuice?.ResetSlots();

            if (NetworkManager.Singleton.StartHost())
            {
                textCode.text = $"{joinCode}";
                buttonPlayGame.gameObject.SetActive(true);
                SwitchPanel(panelHost, MenuPanelTransitioner.TransitionStyle.PlantSign, true);
                UpdateLobbyPlayerListUI();
            }
            else
            {
                ValidateNameInput(inputFieldNama.text);
            }
        }
        catch (Exception e)
        {
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
        string joinCode = inputFieldCode.text.Trim().ToUpper();
        buttonJoinRoom.interactable = false;
        TMP_Text joinLabel = StartButtonLoading(buttonJoinRoom, out string originalText);

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
                transport.UseWebSockets = false;
                transport.SetConnectionData("127.0.0.1", 7777);
            }

            lobbyUIJuice?.ResetSlots();

            if (NetworkManager.Singleton.StartClient())
            {
                textCode.text = $"{joinCode}";
                buttonPlayGame.gameObject.SetActive(false);
                SwitchPanel(panelHost, MenuPanelTransitioner.TransitionStyle.PlantSign, true);
                UpdateLobbyPlayerListUI();
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
            StopButtonLoading(joinLabel, originalText);
        }
    }

    // --- LOGIKA LOBBY & TRANSISI SCENE ---

    private void UpdateLobbyPlayerListUI()
    {
        if (NetworkLobbyManager.Instance == null) return;

        var players = NetworkLobbyManager.Instance.LobbyPlayers;

        for (int i = 0; i < playerTexts.Length; i++)
        {
            if (i < players.Count)
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

                if (players[i].ClientId == NetworkManager.Singleton.LocalClientId)
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
        UpdateLobbyPlayerListUI();
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (clientId == NetworkManager.Singleton.LocalClientId || clientId == NetworkManager.ServerClientId)
        {
            if (NetworkLobbyManager.Instance != null && NetworkLobbyManager.Instance.LobbyPlayers != null)
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