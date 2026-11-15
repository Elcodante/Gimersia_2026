using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public class NetworkPlayerData : NetworkBehaviour
{
    // Menyimpan daftar pemain yang sedang berada di dalam room
    public static readonly List<NetworkPlayerData> ConnectedPlayers = new List<NetworkPlayerData>();
    public static event Action OnPlayerListChanged;

    // Variabel jaringan untuk menyinkronkan nama pemain ke semua perangkat
    public NetworkVariable<FixedString64Bytes> PlayerName = new NetworkVariable<FixedString64Bytes>(
        default,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Owner
    );

    public override void OnNetworkSpawn()
    {
        if (!ConnectedPlayers.Contains(this))
        {
            ConnectedPlayers.Add(this);
        }

        PlayerName.OnValueChanged += OnNameChanged;

        // Jika ini adalah objek milik kita sendiri, kirim nama kita ke jaringan
        if (IsOwner)
        {
            string myName = string.IsNullOrWhiteSpace(MainMenuController.LocalPlayerName)
                ? $"Player {OwnerClientId + 1}"
                : MainMenuController.LocalPlayerName;

            PlayerName.Value = new FixedString64Bytes(myName);
        }

        OnPlayerListChanged?.Invoke();
    }

    public override void OnNetworkDespawn()
    {
        PlayerName.OnValueChanged -= OnNameChanged;
        ConnectedPlayers.Remove(this);
        OnPlayerListChanged?.Invoke();
    }

    private void OnNameChanged(FixedString64Bytes previousValue, FixedString64Bytes newValue)
    {
        OnPlayerListChanged?.Invoke();
    }
}