using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Object Pool khusus untuk FishProjectile dengan ukuran tetap (fixed-slot array)
/// dan dukungan multi-varian model ikan (mis. Ikan 1 &amp; Ikan 2).
/// - Pre-warm 1x saat start: nol Instantiate/Destroy selama permainan berlangsung.
/// - Pencarian slot O(1) via indeks untuk sinkronisasi RPC multiplayer yang sangat hemat bandwidth.
/// - Round-robin cursor saat akuisisi agar seluruh varian ikan tampil bergantian secara merata.
/// - Nol alokasi GC per frame (optimal untuk WebGL / itch.io).
/// </summary>
public sealed class FishObjectPool
{
    private readonly FishProjectile[] slots;
    private readonly bool[] inUse;
    private int nextAcquireCursor;

    public int Capacity => slots != null ? slots.Length : 0;
    public int ActiveCount { get; private set; }

    public FishObjectPool(
        GameObject[] variantPrefabs,
        FishProjectile fallbackPrefab,
        float visualScale,
        int capacity,
        Transform parentContainer)
    {
        int safeCapacity = Mathf.Clamp(capacity, 1, 32);
        float safeScale = Mathf.Max(0.1f, visualScale);

        slots = new FishProjectile[safeCapacity];
        inUse = new bool[safeCapacity];
        nextAcquireCursor = 0;

        // Saring hanya prefab non-null agar daftar varian bersih
        List<GameObject> validVariants = new List<GameObject>(4);
        if (variantPrefabs != null)
        {
            for (int i = 0; i < variantPrefabs.Length; i++)
            {
                if (variantPrefabs[i] != null)
                {
                    validVariants.Add(variantPrefabs[i]);
                }
            }
        }

        for (int i = 0; i < safeCapacity; i++)
        {
            FishProjectile instance;

            if (validVariants.Count > 0)
            {
                GameObject chosenVariant = validVariants[i % validVariants.Count];
                instance = InstantiateFromVariant(chosenVariant, safeScale, parentContainer, i);
            }
            else if (fallbackPrefab != null)
            {
                instance = Object.Instantiate(fallbackPrefab, parentContainer);
                instance.name = $"ikan_Pool_{i}";
            }
            else
            {
                instance = CreateFallbackBoxFish(parentContainer, i);
            }

            instance.ConfigureSlot(i);
            instance.ForceDeactivate();

            slots[i] = instance;
            inUse[i] = false;
        }
    }

    /// <summary>
    /// Mengambil slot ikan yang tersedia untuk diluncurkan (Server).
    /// Menggunakan kursor round-robin agar semua varian ikan (Ikan 1, Ikan 2, dst.) tampil merata.
    /// </summary>
    public bool TryAcquire(out int slotIndex, out FishProjectile fish)
    {
        int count = slots.Length;
        for (int offset = 0; offset < count; offset++)
        {
            int idx = (nextAcquireCursor + offset) % count;
            if (!inUse[idx] && slots[idx] != null)
            {
                inUse[idx] = true;
                ActiveCount++;
                nextAcquireCursor = (idx + 1) % count;
                slotIndex = idx;
                fish = slots[idx];
                return true;
            }
        }

        slotIndex = -1;
        fish = null;
        return false;
    }

    /// <summary>Akses langsung slot tertentu berdasarkan indeks RPC (Client &amp; Host).</summary>
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

    private static FishProjectile InstantiateFromVariant(
        GameObject variantPrefab,
        float visualScale,
        Transform parent,
        int slotIndex)
    {
        // Jika prefab sudah dilengkapi komponen FishProjectile di root, pakai langsung
        if (variantPrefab.GetComponent<FishProjectile>() != null)
        {
            GameObject existingObj = Object.Instantiate(variantPrefab, parent);
            existingObj.name = $"FishPool_{slotIndex}_{variantPrefab.name}";
            return existingObj.GetComponent<FishProjectile>();
        }

        // Jika prefab adalah model 3D murni (seperti Ikan 1.prefab / Ikan 2.prefab),
        // bungkus ke root kinematik bersih agar rotasi bawaan FBX (-90° X) tetap utuh.
        GameObject root = new GameObject($"FishPool_{slotIndex}_{variantPrefab.name}");
        root.transform.SetParent(parent, false);

        GameObject visual = Object.Instantiate(variantPrefab, root.transform);
        visual.name = variantPrefab.name;
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localScale = visual.transform.localScale * visualScale;
        StripPhysicsFromVisual(visual);

        FishProjectile projectile = root.AddComponent<FishProjectile>();
        projectile.ConfigureVisualRoot(visual.transform);
        return projectile;
    }

    private static void StripPhysicsFromVisual(GameObject visual)
    {
        Collider[] colliders = visual.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Object.Destroy(colliders[i]);
        }

        Rigidbody[] bodies = visual.GetComponentsInChildren<Rigidbody>(true);
        for (int i = 0; i < bodies.Length; i++)
        {
            Object.Destroy(bodies[i]);
        }
    }

    private static FishProjectile CreateFallbackBoxFish(Transform parent, int slotIndex)
    {
        GameObject root = new GameObject($"ikan_Pool_{slotIndex}");
        root.transform.SetParent(parent, false);
        FishProjectile projectile = root.AddComponent<FishProjectile>();

        GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
        visual.name = "Visual";
        visual.transform.SetParent(root.transform, false);
        visual.transform.localScale = new Vector3(0.45f, 0.55f, 1.35f);

        Collider col = visual.GetComponent<Collider>();
        if (col != null) Object.Destroy(col);

        projectile.ConfigureVisualRoot(visual.transform);
        return projectile;
    }
}
