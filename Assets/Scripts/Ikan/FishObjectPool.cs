using UnityEngine;

/// <summary>
/// Object Pool khusus untuk FishProjectile dengan ukuran tetap (fixed-slot array).
/// - Pre-warm 1x saat start: nol Instantiate/Destroy selama permainan berlangsung.
/// - Pencarian slot O(1) via indeks untuk sinkronisasi RPC multiplayer yang sangat hemat bandwidth.
/// - Nol alokasi GC per frame (optimal untuk WebGL / itch.io).
/// </summary>
public sealed class FishObjectPool
{
    private readonly FishProjectile[] slots;
    private readonly bool[] inUse;

    public int Capacity => slots != null ? slots.Length : 0;
    public int ActiveCount { get; private set; }

    public FishObjectPool(FishProjectile prefab, int capacity, Transform parentContainer)
    {
        int safeCapacity = Mathf.Clamp(capacity, 1, 32);
        slots = new FishProjectile[safeCapacity];
        inUse = new bool[safeCapacity];

        for (int i = 0; i < safeCapacity; i++)
        {
            FishProjectile instance = prefab != null
                ? Object.Instantiate(prefab, parentContainer)
                : CreateFallbackBoxFish(parentContainer);

            instance.name = $"ikan_Pool_{i}";
            instance.ConfigureSlot(i);
            instance.ForceDeactivate();

            slots[i] = instance;
            inUse[i] = false;
        }
    }

    /// <summary>Mengambil slot ikan yang tersedia untuk diluncurkan (Server).</summary>
    public bool TryAcquire(out int slotIndex, out FishProjectile fish)
    {
        for (int i = 0; i < slots.Length; i++)
        {
            if (!inUse[i] && slots[i] != null)
            {
                inUse[i] = true;
                ActiveCount++;
                slotIndex = i;
                fish = slots[i];
                return true;
            }
        }

        slotIndex = -1;
        fish = null;
        return false;
    }

    /// <summary>Akses langsung slot tertentu berdasarkan indeks RPC (Client & Host).</summary>
    public FishProjectile GetSlot(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= slots.Length) return null;
        return slots[slotIndex];
    }

    /// <summary>Menandai slot sedang aktif (dipakai client saat menerima LaunchFishClientRpc).</summary>
    public FishProjectile MarkSlotActive(int slotIndex)
    {
        FishProjectile fish = GetSlot(slotIndex);
        if (fish == null) return null;

        if (!inUse[slotIndex])
        {
            inUse[slotIndex] = true;
            ActiveCount++;
        }

        return fish;
    }

    /// <summary>Mengembalikan ikan ke pool setelah kembali masuk ke air.</summary>
    public void Release(FishProjectile fish)
    {
        if (fish == null) return;

        int idx = fish.SlotIndex;
        if (idx >= 0 && idx < slots.Length && inUse[idx])
        {
            inUse[idx] = false;
            ActiveCount = Mathf.Max(0, ActiveCount - 1);
        }
    }

    /// <summary>Menghentikan seluruh ikan aktif (mis. saat match selesai atau keluar scene).</summary>
    public void ReleaseAll()
    {
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] != null)
            {
                slots[i].ForceDeactivate();
            }
            inUse[i] = false;
        }
        ActiveCount = 0;
    }

    private static FishProjectile CreateFallbackBoxFish(Transform parent)
    {
        GameObject root = new GameObject("ikan");
        root.transform.SetParent(parent, false);
        FishProjectile projectile = root.AddComponent<FishProjectile>();

        GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
        visual.name = "Visual";
        visual.transform.SetParent(root.transform, false);
        visual.transform.localScale = new Vector3(0.45f, 0.55f, 1.35f);

        Collider col = visual.GetComponent<Collider>();
        if (col != null) Object.Destroy(col);

        return projectile;
    }
}
