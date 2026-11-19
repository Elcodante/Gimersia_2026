using System.Collections;
using Unity.Netcode;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class TrashObstacle : NetworkBehaviour
{
    [Header("Pengaturan Ketahanan Sampah")]
    [SerializeField] private int minTugsRequired = 5; // Minimal tekanan F untuk menghilangkan sampah
    [SerializeField] private int maxTugsRequired = 7; // Maksimal tekanan F untuk menghilangkan sampah

    [Header("Pengaturan Visual Getaran")]
    [Tooltip("Opsional: isi dengan child mesh sampah. Jika kosong, akan menggetarkan objek utama.")]
    [SerializeField] private Transform visualModel;
    [SerializeField] private float shakeDuration = 0.22f;
    [SerializeField] private float shakeMagnitude = 0.18f;

    private int requiredTugs;
    private int currentTugs = 0;
    private bool isCleared = false;

    private Transform shakeTarget;
    private Vector3 initialLocalPos;
    private Coroutine shakeCoroutine;

    public bool IsCleared => isCleared;

    private void Awake()
    {
        shakeTarget = (visualModel != null) ? visualModel : transform;
        initialLocalPos = shakeTarget.localPosition;
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            int min = Mathf.Max(1, minTugsRequired);
            int max = Mathf.Max(min, maxTugsRequired);
            requiredTugs = Random.Range(min, max + 1);
        }
    }

    /// <summary>
    /// Dipanggil oleh Server setiap kali pemain menekan F saat laso menancap di sampah.
    /// </summary>
    public void TugTrash()
    {
        if (!IsServer || !IsSpawned || isCleared) return;

        currentTugs++;

        if (currentTugs >= requiredTugs)
        {
            ClearTrash();
        }
        else
        {
            TriggerShakeClientRpc();
        }
    }

    [ClientRpc]
    private void TriggerShakeClientRpc()
    {
        if (isCleared || !gameObject.activeInHierarchy) return;

        if (shakeCoroutine != null)
        {
            StopCoroutine(shakeCoroutine);
        }
        shakeCoroutine = StartCoroutine(ShakeRoutine());
    }

    private IEnumerator ShakeRoutine()
    {
        float elapsed = 0f;

        while (elapsed < shakeDuration)
        {
            elapsed += Time.deltaTime;
            float strength = Mathf.Lerp(shakeMagnitude, 0f, elapsed / shakeDuration);

            float offsetX = Random.Range(-1f, 1f) * strength;
            float offsetZ = Random.Range(-1f, 1f) * strength;

            shakeTarget.localPosition = initialLocalPos + new Vector3(offsetX, 0f, offsetZ);
            yield return null;
        }

        shakeTarget.localPosition = initialLocalPos;
        shakeCoroutine = null;
    }

    private void ClearTrash()
    {
        if (isCleared) return;
        isCleared = true;

        // Gunakan Despawn(false) agar aman untuk In-Scene NetworkObject tanpa memicu warning
        if (NetworkObject != null && NetworkObject.IsSpawned)
        {
            NetworkObject.Despawn(false);
        }
    }

    public override void OnNetworkDespawn()
    {
        isCleared = true;
        // Sembunyikan objek & matikan collider di Server maupun semua Client
        gameObject.SetActive(false);
    }
}