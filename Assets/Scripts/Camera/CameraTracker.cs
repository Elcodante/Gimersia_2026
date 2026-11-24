using System.Collections.Generic;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;

public class CameraTracker : MonoBehaviour
{
    private CinemachineTargetGroup targetGroup;

    [Header("Pengaturan Target Pemain")]
    [SerializeField] private float playerRadius = 1.5f;
    [SerializeField] private float boatFallbackRadius = 4.5f;
    [Tooltip("Seberapa cepat bobot pemain berubah ke 0 saat jatuh (atau kembali ke 1 saat respawn) agar kamera tidak terpental.")]
    [SerializeField] private float weightBlendSpeed = 5f;
    [Tooltip("Jeda refresh daftar pemain secara berkala (detik) agar pemain baru otomatis masuk.")]
    [SerializeField] private float rescanInterval = 0.5f;

    private readonly List<PlayerMovement> trackedPlayers = new List<PlayerMovement>();
    private readonly Dictionary<Transform, float> smoothedWeights = new Dictionary<Transform, float>();
    private Transform boatTransform;
    private float rescanTimer;

    private void Start()
    {
        targetGroup = GetComponent<CinemachineTargetGroup>();
        FindBoat();

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientChanged;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientChanged;
        }

        RefreshPlayersList();
    }

    private void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientChanged;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientChanged;
        }
    }

    private void OnClientChanged(ulong clientId)
    {
        rescanTimer = 0f;
    }

    private void FindBoat()
    {
        if (boatTransform != null) return;
        BoatController boat = FindFirstObjectByType<BoatController>();
        if (boat != null) boatTransform = boat.transform;
    }

    private void RefreshPlayersList()
    {
        trackedPlayers.Clear();
        PlayerMovement[] found = FindObjectsByType<PlayerMovement>(FindObjectsSortMode.None);
        for (int i = 0; i < found.Length; i++)
        {
            if (found[i] != null && found[i].gameObject.activeInHierarchy)
            {
                trackedPlayers.Add(found[i]);
            }
        }
    }

    private void LateUpdate()
    {
        if (targetGroup == null) return;

        rescanTimer -= Time.deltaTime;
        if (rescanTimer <= 0f)
        {
            rescanTimer = rescanInterval;
            FindBoat();
            RefreshPlayersList();
        }

        // Hitung berapa pemain yang saat ini aman berada di atas kapal
        int activeOnBoatCount = 0;
        for (int i = trackedPlayers.Count - 1; i >= 0; i--)
        {
            PlayerMovement pm = trackedPlayers[i];
            if (pm == null || !pm.gameObject.activeInHierarchy)
            {
                trackedPlayers.RemoveAt(i);
                continue;
            }

            if (!pm.IsOffBoatOrRespawning)
            {
                activeOnBoatCount++;
            }
        }

        float dt = Time.deltaTime;
        float blendStep = weightBlendSpeed * dt;
        List<CinemachineTargetGroup.Target> newTargets = new List<CinemachineTargetGroup.Target>();

        // 1. Update bobot setiap pemain: pemain yang jatuh / respawn bobotnya diturunkan halus ke 0
        for (int i = 0; i < trackedPlayers.Count; i++)
        {
            PlayerMovement pm = trackedPlayers[i];
            Transform t = pm.transform;

            bool isFallingOrRespawning = pm.IsOffBoatOrRespawning;
            float desiredWeight = isFallingOrRespawning ? 0f : 1f;

            // Jika pemain baru jatuh, potong langsung bobot maksimalnya agar tidak sempat menarik kamera ke bawah
            if (!smoothedWeights.TryGetValue(t, out float currentWeight))
            {
                currentWeight = desiredWeight;
            }

            if (isFallingOrRespawning)
            {
                // Turunkan sangat cepat agar jatuhnya pemain lain tidak menyentak/mementalkan kamera
                currentWeight = Mathf.MoveTowards(currentWeight, 0f, blendStep * 3f);
            }
            else
            {
                currentWeight = Mathf.MoveTowards(currentWeight, desiredWeight, blendStep);
            }

            smoothedWeights[t] = currentWeight;

            if (currentWeight > 0.001f)
            {
                newTargets.Add(new CinemachineTargetGroup.Target
                {
                    Object = t,
                    Weight = currentWeight,
                    Radius = playerRadius
                });
            }
        }

        // 2. Fallback ke Kapal jika tidak ada satupun pemain di atas kapal (mis. semua jatuh atau Solo jatuh)
        if (boatTransform != null)
        {
            float desiredBoatWeight = (activeOnBoatCount == 0) ? 1f : 0f;
            if (!smoothedWeights.TryGetValue(boatTransform, out float boatWeight))
            {
                boatWeight = desiredBoatWeight;
            }

            boatWeight = Mathf.MoveTowards(boatWeight, desiredBoatWeight, blendStep * 2f);
            smoothedWeights[boatTransform] = boatWeight;

            if (boatWeight > 0.001f || newTargets.Count == 0)
            {
                newTargets.Add(new CinemachineTargetGroup.Target
                {
                    Object = boatTransform,
                    Weight = Mathf.Max(boatWeight, newTargets.Count == 0 ? 1f : 0f),
                    Radius = boatFallbackRadius
                });
            }
        }

        targetGroup.Targets = newTargets;
    }
}