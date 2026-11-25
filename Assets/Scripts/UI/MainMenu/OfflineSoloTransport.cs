using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Transport lokal tanpa soket jaringan untuk mode Solo.
/// Menghindari kegagalan bind UDP/WebSocket di browser WebGL saat memanggil NetworkManager.StartHost(),
/// sehingga mode Solo dapat langsung berjalan 100% offline tanpa internet atau Relay.
/// </summary>
public class OfflineSoloTransport : NetworkTransport
{
    public override ulong ServerClientId => 0;

    public override void Initialize(NetworkManager networkManager = null) { }

    public override bool StartServer() => true;

    public override bool StartClient() => true;

    public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
    {
        clientId = 0;
        payload = default;
        receiveTime = Time.realtimeSinceStartup;
        return NetworkEvent.Nothing;
    }

    public override void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery networkDelivery) { }

    public override void DisconnectRemoteClient(ulong clientId) { }

    public override void DisconnectLocalClient() { }

    public override ulong GetCurrentRtt(ulong clientId) => 0;

    public override void Shutdown() { }
}

