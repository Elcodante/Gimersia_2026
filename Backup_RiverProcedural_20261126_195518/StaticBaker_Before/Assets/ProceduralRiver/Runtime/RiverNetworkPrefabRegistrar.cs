using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Registers hookable-trash NetworkObject prefabs on every peer before it connects.
/// A tiny bootstrap object finds NetworkManager even when it is created later by the menu scene.
/// </summary>
[DisallowMultipleComponent]
public sealed class RiverNetworkPrefabRegistrar : MonoBehaviour
{
    private static readonly string[] NetworkTrashPrefabNames =
    {
        "Sampah 1",
        "Sampah 2",
        "Sampah 3"
    };

    private static readonly HashSet<int> RegisteredManagerIds = new HashSet<int>();
    private static bool bootstrapCreated;
    private bool registered;
    private bool warnedManagerAlreadyListening;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        RegisteredManagerIds.Clear();
        bootstrapCreated = false;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void CreateBootstrap()
    {
        if (bootstrapCreated) return;
        bootstrapCreated = true;

        GameObject bootstrap = new GameObject("RiverNetworkPrefabRegistrar");
        DontDestroyOnLoad(bootstrap);
        bootstrap.AddComponent<RiverNetworkPrefabRegistrar>();
    }

    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        TryRegister();
    }

    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        TryRegister();
    }

    private void Update()
    {
        if (!registered) TryRegister();
    }


    private static bool IsAlreadyRegistered(NetworkManager manager, GameObject prefab)
    {
        if (manager == null || prefab == null || manager.NetworkConfig == null || manager.NetworkConfig.Prefabs == null)
            return false;

        var registeredPrefabs = manager.NetworkConfig.Prefabs.Prefabs;
        for (int i = 0; i < registeredPrefabs.Count; i++)
        {
            NetworkPrefab entry = registeredPrefabs[i];
            if (entry != null && entry.Prefab == prefab)
                return true;
        }

        return false;
    }

    private void TryRegister()
    {
        NetworkManager manager = NetworkManager.Singleton;
        if (manager == null) return;

        int managerId = manager.GetInstanceID();
        if (RegisteredManagerIds.Contains(managerId))
        {
            registered = true;
            return;
        }

        if (manager.IsListening)
        {
            if (!warnedManagerAlreadyListening)
            {
                Debug.LogError("[RiverPrefabRegistrar] NetworkManager sudah mulai listening sebelum prefab sampah diregistrasikan. Pastikan bootstrap berjalan sebelum tombol Host/Join dipakai.");
                warnedManagerAlreadyListening = true;
            }
            return;
        }
        warnedManagerAlreadyListening = false;

        int registeredCount = 0;
        int alreadyRegisteredCount = 0;
        bool allValid = true;

        for (int i = 0; i < NetworkTrashPrefabNames.Length; i++)
        {
            string resourcePath = "RiverObstacles/" + NetworkTrashPrefabNames[i];
            GameObject prefab = Resources.Load<GameObject>(resourcePath);
            if (prefab == null)
            {
                Debug.LogError($"[RiverPrefabRegistrar] Prefab tidak ditemukan: Resources/{resourcePath}.prefab");
                allValid = false;
                continue;
            }

            if (prefab.GetComponent<NetworkObject>() == null || prefab.GetComponent<TrashObstacle>() == null)
            {
                Debug.LogError($"[RiverPrefabRegistrar] '{prefab.name}' wajib memiliki NetworkObject dan TrashObstacle pada root.");
                allValid = false;
                continue;
            }

            // NetworkManager may already have these assets through its Inspector list or
            // NetworkPrefabsList. AddNetworkPrefab logs a duplicate GlobalObjectIdHash as an
            // error (it does not reliably throw), so check the processed runtime list first.
            if (IsAlreadyRegistered(manager, prefab))
            {
                alreadyRegisteredCount++;
                continue;
            }

            manager.AddNetworkPrefab(prefab);
            if (IsAlreadyRegistered(manager, prefab))
            {
                registeredCount++;
            }
            else
            {
                allValid = false;
                Debug.LogError($"[RiverPrefabRegistrar] NGO gagal mendaftarkan prefab '{prefab.name}'.", prefab);
            }
        }

        if (allValid)
        {
            RegisteredManagerIds.Add(managerId);
            registered = true;
            Debug.Log($"[RiverPrefabRegistrar] Selesai. Added: {registeredCount}, already listed/duplicate: {alreadyRegisteredCount}. Semua peer harus memakai tiga prefab yang sama.");
        }
    }
}
