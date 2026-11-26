using UnityEngine;

/// <summary>
/// Penjadwal kemunculan serangan ikan secara acak namun dinamis.
/// Menggunakan ritme acak (fase tenang vs gelombang ramai & peluang salvo beruntun),
/// sepenuhnya independen dari jarak kapal ke tujuan akhir.
/// </summary>
public sealed class FishAttackScheduler
{
    private readonly float initialGraceSeconds;
    private readonly float minWaveInterval;
    private readonly float maxWaveInterval;
    private readonly float minBurstSpacing;
    private readonly float maxBurstSpacing;
    private readonly int maxBurstCount;

    private float timer;
    private int pendingBurstShots;
    private float tempoPhase;
    private float tempoSpeed;

    public FishAttackScheduler(
        float initialGraceSeconds,
        float minWaveInterval,
        float maxWaveInterval,
        float minBurstSpacing,
        float maxBurstSpacing,
        int maxBurstCount)
    {
        this.initialGraceSeconds = Mathf.Max(0.5f, initialGraceSeconds);
        this.minWaveInterval = Mathf.Max(1f, minWaveInterval);
        this.maxWaveInterval = Mathf.Max(this.minWaveInterval, maxWaveInterval);
        this.minBurstSpacing = Mathf.Max(0.3f, minBurstSpacing);
        this.maxBurstSpacing = Mathf.Max(this.minBurstSpacing, maxBurstSpacing);
        this.maxBurstCount = Mathf.Clamp(maxBurstCount, 1, 4);

        Reset();
    }

    public void Reset()
    {
        pendingBurstShots = 0;
        tempoPhase = Random.Range(0f, Mathf.PI * 2f);
        tempoSpeed = Random.Range(0.08f, 0.16f);
        timer = initialGraceSeconds + Random.Range(0f, 2.0f);
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
        float biasedMax = Mathf.Lerp(maxWaveInterval, Mathf.Lerp(minWaveInterval, maxWaveInterval, 0.55f), dynamicFactor);
        return Random.Range(minWaveInterval, Mathf.Max(minWaveInterval + 0.25f, biasedMax));
    }

    private int RollBurstSize()
    {
        if (maxBurstCount <= 1) return 1;

        float roll = Random.value;
        float livelyBonus = 0.5f + 0.5f * Mathf.Sin(tempoPhase);

        // Mayoritas 1 ikan, sesekali 2 ikan beruntun, jarang 3 ikan saat momen ramai
        float multiChance = Mathf.Lerp(0.22f, 0.45f, livelyBonus);
        if (roll > multiChance) return 1;

        if (maxBurstCount >= 3 && roll < multiChance * 0.28f)
        {
            return 3;
        }

        return 2;
    }
}
