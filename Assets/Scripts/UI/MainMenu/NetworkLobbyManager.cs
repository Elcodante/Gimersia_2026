using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public class NetworkLobbyManager : NetworkBehaviour
{
    public static NetworkLobbyManager Instance { get; private set; }

    public struct LobbyPlayerState : INetworkSerializable, IEquatable<LobbyPlayerState>
    {
        public ulong ClientId;
        public FixedString32Bytes PlayerName;
        public bool IsHost;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref ClientId);
            serializer.SerializeValue(ref PlayerName);
            serializer.SerializeValue(ref IsHost);
        }

        public bool Equals(LobbyPlayerState other)
        {
            return ClientId == other.ClientId && PlayerName == other.PlayerName && IsHost == other.IsHost;
        }
    }

    public NetworkList<LobbyPlayerState> LobbyPlayers = new NetworkList<LobbyPlayerState>();
    public static event Action OnLobbyUpdated;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
        }
        else if (Instance != this)
        {
            Destroy(gameObject);
        }
    }

    public override void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }

        LobbyPlayers?.Dispose();
        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        LobbyPlayers.OnListChanged += HandleLobbyPlayersChanged;

        // Bersihkan sisa-sisa sesi sebelumnya (mencegah bug "Nama Nyangkut")
        if (IsServer)
        {
            LobbyPlayers.Clear();
            AddPlayerToList(NetworkManager.ServerClientId, MainMenuController.LocalPlayerName, true);
            NetworkManager.Singleton.OnClientDisconnectCallback += HandleClientDisconnect;
            Debug.Log($"[Lobby] Host spawned LobbyManager. HostName='{MainMenuController.LocalPlayerName}'");
        }

        if (IsClient && !IsServer)
        {
            Debug.Log($"[Lobby] Client ({NetworkManager.Singleton.LocalClientId}) spawned LobbyManager. Submitting name='{MainMenuController.LocalPlayerName}' (current list count={LobbyPlayers.Count})");
            SubmitPlayerNameRpc(MainMenuController.LocalPlayerName);
        }

        OnLobbyUpdated?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnect;
            // Server boleh membersihkan daftar
            LobbyPlayers.Clear();
        }
        LobbyPlayers.OnListChanged -= HandleLobbyPlayersChanged;

        // Panggil event agar UI di layar masing-masing jadi kosong
        OnLobbyUpdated?.Invoke();
    }

    [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
    private void SubmitPlayerNameRpc(string playerName, RpcParams rpcParams = default)
    {
        ulong senderId = rpcParams.Receive.SenderClientId;
        Debug.Log($"[Lobby] Server received player name '{playerName}' from clientId={senderId}");
        AddPlayerToList(senderId, playerName, false);
    }

    private void AddPlayerToList(ulong clientId, string playerName, bool isHost)
    {
        string safeName = string.IsNullOrWhiteSpace(playerName)
            ? $"Player {clientId + 1}"
            : playerName.Trim();

        LobbyPlayerState state = new LobbyPlayerState
        {
            ClientId = clientId,
            PlayerName = new FixedString32Bytes(safeName),
            IsHost = isHost
        };

        for (int i = 0; i < LobbyPlayers.Count; i++)
        {
            if (LobbyPlayers[i].ClientId == clientId)
            {
                LobbyPlayers[i] = state;
                return;
            }
        }

        LobbyPlayers.Add(state);
    }

    private void HandleClientDisconnect(ulong clientId)
    {
        for (int i = 0; i < LobbyPlayers.Count; i++)
        {
            if (LobbyPlayers[i].ClientId == clientId)
            {
                bool wasHost = LobbyPlayers[i].IsHost;
                LobbyPlayers.RemoveAt(i);

                // Jika Host keluar, kita tidak bisa over status (karena server mati),
                // tetapi jika pemain biasa keluar, daftar akan langsung dirapikan.
                break;
            }
        }
    }

    private void HandleLobbyPlayersChanged(NetworkListEvent<LobbyPlayerState> changeEvent)
    {
        OnLobbyUpdated?.Invoke();
    }

    public void NotifyClientsToFadeOut()
    {
        if (IsServer)
        {
            PlayFadeOutClientRpc();
        }
    }

    [Rpc(SendTo.NotServer)]
    private void PlayFadeOutClientRpc()
    {
        if (SceneFadeTransition.Instance != null)
        {
            // Null karena perpindahan scene tetap dikendalikan oleh Host;
            // layar akan otomatis terang kembali begitu scene In_Game_Raka selesai dimuat.
            SceneFadeTransition.Instance.FadeOutAndInvoke(null);
        }
    }
}