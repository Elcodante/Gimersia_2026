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

            try
            {
                manager.AddNetworkPrefab(prefab);
                registeredCount++;
            }
            catch (System.Exception exception)
            {
                // A duplicate is valid only when it was already entered in NetworkManager's Inspector list.
                // We cannot safely infer the reason, so log it and verify with the prefab hash list in NGO.
                Debug.LogWarning($"[RiverPrefabRegistrar] AddNetworkPrefab untuk '{prefab.name}' ditolak. Jika prefab sudah ada di Network Prefabs List, ini aman. Detail: {exception.Message}");
                alreadyRegisteredCount++;
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
