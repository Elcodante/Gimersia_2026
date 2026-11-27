using UnityEngine;

/// <summary>
/// Penjadwal kemunculan serangan ikan secara acak namun dinamis.
/// Menggunakan ritme acak (fase tenang vs gelombang ramai) berdasarkan FishPacingProfile,
/// sepenuhnya independen dari jarak kapal ke tujuan akhir.
/// </summary>
public sealed class FishAttackScheduler
{
    private float initialGraceSeconds;
    private float minWaveInterval;
    private float maxWaveInterval;
    private float minBurstSpacing;
    private float maxBurstSpacing;
    private int maxBurstCount;
    private float minMultiBurstChance;
    private float maxMultiBurstChance;

    private float timer;
    private int pendingBurstShots;
    private float tempoPhase;
    private float tempoSpeed;

    public FishAttackScheduler(in FishPacingProfile profile)
    {
        ApplyProfile(profile);
    }

    public void ApplyProfile(in FishPacingProfile profile)
    {
        initialGraceSeconds = Mathf.Max(1f, profile.initialGraceSeconds);
        minWaveInterval = Mathf.Max(2f, profile.minWaveInterval);
        maxWaveInterval = Mathf.Max(minWaveInterval + 0.5f, profile.maxWaveInterval);
        minBurstSpacing = Mathf.Max(0.4f, profile.minBurstSpacing);
        maxBurstSpacing = Mathf.Max(minBurstSpacing, profile.maxBurstSpacing);
        maxBurstCount = Mathf.Clamp(profile.maxBurstCount, 1, 3);
        minMultiBurstChance = Mathf.Clamp01(profile.minMultiBurstChance);
        maxMultiBurstChance = Mathf.Clamp(profile.maxMultiBurstChance, minMultiBurstChance, 0.8f);

        Reset();
    }

    public void Reset()
    {
        pendingBurstShots = 0;
        tempoPhase = Random.Range(0f, Mathf.PI * 2f);
        tempoSpeed = Random.Range(0.06f, 0.12f);
        timer = initialGraceSeconds + Random.Range(0f, 3.0f);
    }

    /// <summary>
    /// Memajukan waktu penjadwal. Mengembalikan true tepat pada frame ketika 1 ekor ikan harus melompat.
    /// </summary>
    public bool Tick(float dt)
    {
        if (dt <= 0f) return false;

        // Modulasi tempo organik (tenang <-> ramai) berbasis waktu acak, bukan jarak ke tujuan
        tempoPhase += dt * tempoSpeed;
        timer -= dt;
        if (timer > 0f) return false;

        if (pendingBurstShots > 0)
        {
            pendingBurstShots--;
            timer = pendingBurstShots > 0
                ? Random.Range(minBurstSpacing, maxBurstSpacing)
                : RollNextWaveCooldown();
            return true;
        }

        // Mulai gelombang baru: tentukan jumlah ikan dalam 1 gelombang secara acak berbobot
        int burstSize = RollBurstSize();
        pendingBurstShots = burstSize - 1;
        timer = pendingBurstShots > 0
            ? Random.Range(minBurstSpacing, maxBurstSpacing)
            : RollNextWaveCooldown();

        return true;
    }

    private float RollNextWaveCooldown()
    {
        // Faktor dinamis 0..1 yang bergeser acak seiring waktu agar jeda tidak monoton
        float dynamicFactor = 0.5f + 0.5f * Mathf.Sin(tempoPhase);
        float biasedMax = Mathf.Lerp(maxWaveInterval, Mathf.Lerp(minWaveInterval, maxWaveInterval, 0.65f), dynamicFactor);
        return Random.Range(minWaveInterval, Mathf.Max(minWaveInterval + 0.5f, biasedMax));
    }

    private int RollBurstSize()
    {
        if (maxBurstCount <= 1) return 1;

        float roll = Random.value;
        float livelyBonus = 0.5f + 0.5f * Mathf.Sin(tempoPhase);

        float multiChance = Mathf.Lerp(minMultiBurstChance, maxMultiBurstChance, livelyBonus);
        if (roll > multiChance) return 1;

        if (maxBurstCount >= 3 && roll < multiChance * 0.25f)
        {
            return 3;
        }

        return 2;
    }
}
