using System;
using UnityEngine;

/// <summary>
/// Profil ritme kemunculan serangan ikan (dipisahkan antara mode Solo dan Multiplayer).
/// Dapat diatur langsung di Inspector tanpa mengubah logika penjadwalan.
/// </summary>
[Serializable]
public struct FishPacingProfile
{
    [Tooltip("Jeda awal setelah permainan dimulai sebelum ikan pertama berpeluang muncul (detik).")]
    [Min(1f)] public float initialGraceSeconds;
    [Tooltip("Jeda minimum antar gelombang ikan (detik).")]
    [Min(2f)] public float minWaveInterval;
    [Tooltip("Jeda maksimum antar gelombang ikan (detik).")]
    [Min(3f)] public float maxWaveInterval;
    [Tooltip("Jeda minimum antar ikan dalam satu gelombang beruntun (detik).")]
    [Min(0.4f)] public float minBurstSpacing;
    [Tooltip("Jeda maksimum antar ikan dalam satu gelombang beruntun (detik).")]
    [Min(0.6f)] public float maxBurstSpacing;
    [Tooltip("Jumlah maksimal ikan dalam satu gelombang (1 = selalu tunggal).")]
    [Range(1, 3)] public int maxBurstCount;
    [Tooltip("Peluang minimum gelombang beruntun (>1 ikan) saat tempo tenang.")]
    [Range(0f, 0.5f)] public float minMultiBurstChance;
    [Tooltip("Peluang maksimum gelombang beruntun (>1 ikan) saat tempo ramai.")]
    [Range(0f, 0.6f)] public float maxMultiBurstChance;

    public static FishPacingProfile DefaultSolo => new FishPacingProfile
    {
        initialGraceSeconds = 12f,
        minWaveInterval = 15f,
        maxWaveInterval = 26f,
        minBurstSpacing = 1.4f,
        maxBurstSpacing = 2.4f,
        maxBurstCount = 1,
        minMultiBurstChance = 0f,
        maxMultiBurstChance = 0f
    };

    public static FishPacingProfile DefaultMultiplayer => new FishPacingProfile
    {
        initialGraceSeconds = 8f,
        minWaveInterval = 10f,
        maxWaveInterval = 18f,
        minBurstSpacing = 1.1f,
        maxBurstSpacing = 2.0f,
        maxBurstCount = 2,
        minMultiBurstChance = 0.16f,
        maxMultiBurstChance = 0.32f
    };
}

