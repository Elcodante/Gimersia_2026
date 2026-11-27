using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Mengelola alokasi slot karakter unik untuk setiap pemain dalam satu sesi,
/// sekaligus mengacak urutan karakter di setiap sesi baru agar pemain mendapatkan
/// variasi karakter berbeda antar sesi (baik Solo maupun Multiplayer).
/// </summary>
public sealed class SessionCharacterAllocator
{
    private static int lastSessionLeadSlot = -1;

    private readonly Dictionary<ulong, int> assignedByClient = new Dictionary<ulong, int>();
    private readonly List<int> sessionOrder = new List<int>(8);
    private int nextOrderCursor;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        lastSessionLeadSlot = -1;
    }

    /// <summary>
    /// Memulai sesi baru dengan daftar slot teracak.
    /// Jika jumlah karakter &gt; 1, slot pertama dijamin berbeda dari sesi sebelumnya.
    /// </summary>
    public void BeginNewSession(int variantCount)
    {
        int safeCount = Mathf.Max(1, variantCount);

        assignedByClient.Clear();
        sessionOrder.Clear();
        nextOrderCursor = 0;

        for (int i = 0; i < safeCount; i++)
        {
            sessionOrder.Add(i);
        }

        // Gunakan seed berbasis waktu & GUID agar urutan selalu segar di setiap sesi (termasuk WebGL)
        int seed = Environment.TickCount ^ Guid.NewGuid().GetHashCode();
        System.Random rng = new System.Random(seed);

        // Fisher-Yates shuffle
        for (int i = sessionOrder.Count - 1; i > 0; i--)
        {
            int swapIndex = rng.Next(0, i + 1);
            int temp = sessionOrder[i];
            sessionOrder[i] = sessionOrder[swapIndex];
            sessionOrder[swapIndex] = temp;
        }

        // Hindari mengulang karakter pertama yang persis sama dengan sesi sebelumnya
        if (safeCount > 1 && sessionOrder[0] == lastSessionLeadSlot)
        {
            int swapIndex = rng.Next(1, safeCount);
            int temp = sessionOrder[0];
            sessionOrder[0] = sessionOrder[swapIndex];
            sessionOrder[swapIndex] = temp;
        }

        lastSessionLeadSlot = sessionOrder[0];
    }

    /// <summary>
    /// Memberikan slot karakter unik bagi clientId pada sesi aktif.
    /// </summary>
    public int Allocate(ulong clientId)
    {
        if (assignedByClient.TryGetValue(clientId, out int existingSlot))
        {
            return existingSlot;
        }

        if (sessionOrder.Count == 0)
        {
            BeginNewSession(4);
        }

        // Cari slot dari urutan teracak sesi yang belum dipakai client lain
        for (int offset = 0; offset < sessionOrder.Count; offset++)
        {
            int candidate = sessionOrder[(nextOrderCursor + offset) % sessionOrder.Count];
            if (!IsSlotAssigned(candidate))
            {
                nextOrderCursor = (nextOrderCursor + offset + 1) % sessionOrder.Count;
                assignedByClient[clientId] = candidate;
                return candidate;
            }
        }

        // Fallback jika jumlah pemain melebihi jumlah varian karakter di katalog
        int fallback = sessionOrder[nextOrderCursor % sessionOrder.Count];
        nextOrderCursor = (nextOrderCursor + 1) % sessionOrder.Count;
        assignedByClient[clientId] = fallback;
        return fallback;
    }

    /// <summary>Melepaskan slot karakter saat client keluar dari sesi.</summary>
    public void Release(ulong clientId)
    {
        assignedByClient.Remove(clientId);
    }

    public void Clear()
    {
        assignedByClient.Clear();
    }

    private bool IsSlotAssigned(int slot)
    {
        foreach (KeyValuePair<ulong, int> pair in assignedByClient)
        {
            if (pair.Value == slot) return true;
        }
        return false;
    }
}

