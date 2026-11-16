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

    public NetworkList<LobbyPlayerState> LobbyPlayers;
    public static event Action OnLobbyUpdated;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            LobbyPlayers = new NetworkList<LobbyPlayerState>();
        }
        else
        {
            Destroy(gameObject);
        }
    }

    public override void OnNetworkSpawn()
    {
        // Bersihkan sisa-sisa sesi sebelumnya (mencegah bug "Nama Nyangkut")
        if (IsServer)
        {
            LobbyPlayers.Clear();
            AddPlayerToList(NetworkManager.ServerClientId, MainMenuController.LocalPlayerName, true);
            NetworkManager.Singleton.OnClientDisconnectCallback += HandleClientDisconnect;
        }

        if (IsClient && !IsServer)
        {
            SubmitPlayerNameRpc(MainMenuController.LocalPlayerName);
        }

        LobbyPlayers.OnListChanged += HandleLobbyPlayersChanged;
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
        AddPlayerToList(rpcParams.Receive.SenderClientId, playerName, false);
    }

    private void AddPlayerToList(ulong clientId, string playerName, bool isHost)
    {
        LobbyPlayers.Add(new LobbyPlayerState
        {
            ClientId = clientId,
            PlayerName = new FixedString32Bytes(playerName),
            IsHost = isHost
        });
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
}