using System;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Titik tujuan akhir (Finish Goal) di ujung hulu sungai.
/// Diletakkan di Scene Root (/) agar NetworkObject aman di multiplayer Netcode & WebGL.
/// Server memverifikasi kedatangan kapal lalu menyinkronkan status selesai ke seluruh pemain.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(SphereCollider))]
public class RiverFinishGoal : NetworkBehaviour
{
    public static RiverFinishGoal Instance { get; private set; }

    /// <summary>True jika kapal sudah mencapai garis akhir pada sesi ini.</summary>
    public static bool HasFinished => Instance != null && Instance.IsGoalReached.Value;

    /// <summary>Dipicu di semua client (termasuk Host) saat garis akhir tercapai. Parameter = total waktu tempuh (detik).</summary>
    public static event Action<float> OnGoalReached;

    [Header("Pengaturan Deteksi Finis")]
    [Tooltip("Radius area finis di sekitar titik tujuan (meter).")]
    [SerializeField] private float goalRadius = 14f;
    [Tooltip("Interval pengecekan jarak cadangan di server (detik) agar deteksi tetap andal di WebGL.")]
    [SerializeField] private float fallbackCheckInterval = 0.25f;
    [Tooltip("ID SFX kemenangan di AudioManager.")]
    [SerializeField] private string victorySfxId = "trash_clear";

    public NetworkVariable<bool> IsGoalReached = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public NetworkVariable<float> CompletionTimeSeconds = new NetworkVariable<float>(
        0f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public Vector3 GoalWorldPosition => transform.position;
    public float GoalRadius => goalRadius;

    private SphereCollider triggerCollider;
    private BoatController cachedBoat;
    private float elapsedMatchSeconds;
    private float fallbackCheckTimer;
    private bool notifiedLocally;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Instance = null;
        OnGoalReached = null;
    }

    private void Awake()
    {
        Instance = this;
        triggerCollider = GetComponent<SphereCollider>();
        if (triggerCollider != null)
        {
            triggerCollider.isTrigger = true;
            triggerCollider.radius = goalRadius;
        }
    }

    public override void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        notifiedLocally = false;
        IsGoalReached.OnValueChanged += HandleGoalReachedChanged;

        if (IsServer)
        {
            elapsedMatchSeconds = 0f;
            fallbackCheckTimer = 0f;
            IsGoalReached.Value = false;
            CompletionTimeSeconds.Value = 0f;
        }
        else if (IsGoalReached.Value)
        {
            TriggerLocalGoalCallbacks(CompletionTimeSeconds.Value);
        }
    }

    public override void OnNetworkDespawn()
    {
        IsGoalReached.OnValueChanged -= HandleGoalReachedChanged;
    }

    private void Update()
    {
        if (!IsServer || !IsSpawned || IsGoalReached.Value) return;
        if (!GameSpawner.IsMatchReady) return;

        float dt = Time.deltaTime;
        elapsedMatchSeconds += dt;

        // Cek jarak cadangan yang sangat ringan (4 Hz) jika trigger collider tidak sempat terpanggil
        fallbackCheckTimer -= dt;
        if (fallbackCheckTimer <= 0f)
        {
            fallbackCheckTimer = fallbackCheckInterval;
            if (cachedBoat == null)
            {
                cachedBoat = FindFirstObjectByType<BoatController>();
            }

            if (cachedBoat != null)
            {
                Vector3 delta = cachedBoat.transform.position - transform.position;
                delta.y = 0f;
                if (delta.sqrMagnitude <= goalRadius * goalRadius)
                {
                    CompleteGoalOnServer();
                }
            }
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer || !IsSpawned || IsGoalReached.Value) return;
        if (!GameSpawner.IsMatchReady) return;

        if (other.GetComponentInParent<BoatController>() != null)
        {
            CompleteGoalOnServer();
        }
    }

    private void CompleteGoalOnServer()
    {
        if (!IsServer || IsGoalReached.Value) return;

        CompletionTimeSeconds.Value = elapsedMatchSeconds;
        IsGoalReached.Value = true;
        Debug.Log($"[RiverFinishGoal] Kapal mencapai tujuan akhir dalam {elapsedMatchSeconds:F1} detik!");
    }

    private void HandleGoalReachedChanged(bool previousValue, bool newValue)
    {
        if (newValue)
        {
            TriggerLocalGoalCallbacks(CompletionTimeSeconds.Value);
        }
    }

    private void TriggerLocalGoalCallbacks(float completionSeconds)
    {
        if (notifiedLocally) return;
        notifiedLocally = true;

        if (!string.IsNullOrEmpty(victorySfxId) && AudioManager.Instance != null)
        {
            AudioManager.Instance.PlaySFX(victorySfxId, 0f);
        }

        OnGoalReached?.Invoke(completionSeconds);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 0.95f, 0.45f, 0.85f);
        Gizmos.DrawWireSphere(transform.position, goalRadius);
    }
}

