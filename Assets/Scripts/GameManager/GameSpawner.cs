using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

public class GameSpawner : NetworkBehaviour
{
    [Header("Pengaturan Spawn")]
    [SerializeField] private GameObject playerPrefab;
    [SerializeField] private Transform[] spawnPoints;
    [SerializeField] private NetworkObject perahuNetworkObject;
    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            // Daftarkan event: saat ADA client yang SELESAI memuat scene ini, jalankan fungsi spawn
            NetworkManager.Singleton.SceneManager.OnLoadEventCompleted += HandleSceneLoaded;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager.Singleton != null && NetworkManager.Singleton.SceneManager != null)
        {
            NetworkManager.Singleton.SceneManager.OnLoadEventCompleted -= HandleSceneLoaded;
        }
    }

    private void HandleSceneLoaded(string sceneName, UnityEngine.SceneManagement.LoadSceneMode loadSceneMode, System.Collections.Generic.List<ulong> clientsCompleted, System.Collections.Generic.List<ulong> clientsTimedOut)
    {
        // Pastikan kita ada di scene game
        if (sceneName != gameObject.scene.name) return;

        // Spawn pemain untuk semua client yang BERHASIL memuat scene
        for (int i = 0; i < clientsCompleted.Count; i++)
        {
            ulong clientId = clientsCompleted[i];
            SpawnPlayerForClient(clientId, i);
        }
    }

    private void SpawnPlayerForClient(ulong clientId, int spawnIndex)
    {
        if (playerPrefab == null || spawnPoints.Length == 0) return;

        Transform spawnPoint = spawnPoints[spawnIndex % spawnPoints.Length];
        GameObject playerInstance = Instantiate(playerPrefab, spawnPoint.position, spawnPoint.rotation);

        NetworkObject netObj = playerInstance.GetComponent<NetworkObject>();
        netObj.SpawnAsPlayerObject(clientId, true);

        // Kaitkan pemain sebagai child dari kapal dengan worldPositionStays = true
        if (perahuNetworkObject != null)
        {
            netObj.TrySetParent(perahuNetworkObject, true);
        }
    }
}