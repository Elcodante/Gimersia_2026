using System;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

/// <summary>
/// Melacak progres jarak kapal sepanjang lintasan sungai menuju RiverFinishGoal secara lokal.
/// Nol beban jaringan (posisi kapal sudah disinkronkan oleh NetworkTransform) dan nol alokasi GC per frame:
/// membangun Look-Up Table (LUT) spline 1x saat Start lalu melakukan query sliding-window O(1) pada 10 Hz.
/// </summary>
[DisallowMultipleComponent]
public class RiverProgressTracker : MonoBehaviour
{
    public static RiverProgressTracker Instance { get; private set; }

    [Header("Referensi (Opsional — dicari otomatis)")]
    [SerializeField] private RiverPath river;
    [SerializeField] private Transform boatTransform;
    [SerializeField] private Transform goalTransform;

    [Header("Efisiensi & Presisi (WebGL)")]
    [Tooltip("Jumlah titik sampel spline untuk LUT arc-length. 256 titik memberi presisi < 1 m dengan memori ~4 KB.")]
    [SerializeField, Range(64, 512)] private int lutSampleCount = 256;
    [Tooltip("Interval pembaruan progres (detik). 0.1 = 10 Hz, sangat ringan untuk WebGL.")]
    [SerializeField, Range(0.05f, 0.5f)] private float updateInterval = 0.1f;

    /// <summary>Progres ternormalisasi 0.0 (start) sampai 1.0 (finis).</summary>
    public float NormalizedProgress { get; private set; }

    /// <summary>Sisa jarak kapal menuju tujuan akhir dalam satuan meter.</summary>
    public float RemainingMeters { get; private set; }

    /// <summary>Total panjang jalur dari titik start kapal ke tujuan akhir (meter).</summary>
    public float TotalTrackMeters { get; private set; }

    /// <summary>Dipicu setiap kali progres berubah: (normalizedProgress 0..1, remainingMeters).</summary>
    public event Action<float, float> OnProgressUpdated;

    private float[] lutX;
    private float[] lutZ;
    private float[] lutArcDist;
    private bool lutBuilt;

    private float startArcDist;
    private float goalArcDist;
    private bool movingTowardSplineStart;
    private bool startCalibratedAfterReady;

    private int lastNearestIndex = -1;
    private float updateTimer;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Instance = null;
    }

    private void Awake()
    {
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }

    private void Start()
    {
        ResolveReferences();
        BuildSplineLut();
        CalibrateEndpoints();
        SampleBoatProgress();
    }

    private void Update()
    {
        updateTimer -= Time.deltaTime;
        if (updateTimer > 0f) return;
        updateTimer = updateInterval;

        if (!lutBuilt)
        {
            ResolveReferences();
            BuildSplineLut();
            CalibrateEndpoints();
            if (!lutBuilt) return;
        }

        if (boatTransform == null)
        {
            ResolveReferences();
            if (boatTransform == null) return;
        }

        // Kalibrasi ulang titik awal 1x saat semua pemain siap agar bar mulai bersih di 0%
        if (!startCalibratedAfterReady && GameSpawner.IsMatchReady)
        {
            startCalibratedAfterReady = true;
            CalibrateEndpoints();
        }

        SampleBoatProgress();
    }

    private void ResolveReferences()
    {
        if (river == null) river = RiverPath.Main;

        if (boatTransform == null)
        {
            BoatController boat = FindFirstObjectByType<BoatController>();
            if (boat != null) boatTransform = boat.transform;
        }

        if (goalTransform == null && RiverFinishGoal.Instance != null)
        {
            goalTransform = RiverFinishGoal.Instance.transform;
        }
    }

    private void BuildSplineLut()
    {
        if (river == null || !river.IsValid) return;

        SplineContainer container = river.GetComponent<SplineContainer>();
        if (container == null || container.Spline == null || container.Spline.Count < 2) return;

        int count = Mathf.Clamp(lutSampleCount, 64, 512);
        lutX = new float[count];
        lutZ = new float[count];
        lutArcDist = new float[count];

        float accum = 0f;
        float prevX = 0f;
        float prevZ = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = (float)i / (count - 1);
            float3 pos = container.EvaluatePosition(t);
            lutX[i] = pos.x;
            lutZ[i] = pos.z;

            if (i > 0)
            {
                float dx = pos.x - prevX;
                float dz = pos.z - prevZ;
                accum += Mathf.Sqrt(dx * dx + dz * dz);
            }

            lutArcDist[i] = accum;
            prevX = pos.x;
            prevZ = pos.z;
        }

        lutBuilt = true;
    }

    private void CalibrateEndpoints()
    {
        if (!lutBuilt) return;

        int lastIdx = lutArcDist.Length - 1;
        float totalSplineLen = lutArcDist[lastIdx];

        if (boatTransform != null)
        {
            startArcDist = ProjectToArcDistanceFullScan(boatTransform.position, out lastNearestIndex);
        }
        else
        {
            startArcDist = totalSplineLen;
            lastNearestIndex = lastIdx;
        }

        if (goalTransform != null)
        {
            goalArcDist = ProjectToArcDistanceFullScan(goalTransform.position, out _);
        }
        else
        {
            // Default: jika kapal mulai dekat t=1, tujuan di t=0 (dan sebaliknya)
            goalArcDist = (startArcDist > totalSplineLen * 0.5f) ? 0f : totalSplineLen;
        }

        movingTowardSplineStart = startArcDist > goalArcDist;
        TotalTrackMeters = Mathf.Max(1f, Mathf.Abs(goalArcDist - startArcDist));
    }

    private void SampleBoatProgress()
    {
        if (!lutBuilt || boatTransform == null) return;

        if (RiverFinishGoal.HasFinished)
        {
            NormalizedProgress = 1f;
            RemainingMeters = 0f;
            OnProgressUpdated?.Invoke(NormalizedProgress, RemainingMeters);
            return;
        }

        float currentArcDist = ProjectToArcDistanceFast(boatTransform.position);

        float traveled;
        float remaining;

        if (movingTowardSplineStart)
        {
            traveled = startArcDist - currentArcDist;
            remaining = Mathf.Max(0f, currentArcDist - goalArcDist);
        }
        else
        {
            traveled = currentArcDist - startArcDist;
            remaining = Mathf.Max(0f, goalArcDist - currentArcDist);
        }

        NormalizedProgress = Mathf.Clamp01(traveled / TotalTrackMeters);
        RemainingMeters = remaining;

        OnProgressUpdated?.Invoke(NormalizedProgress, RemainingMeters);
    }

    private float ProjectToArcDistanceFast(Vector3 worldPos)
    {
        int count = lutX.Length;
        if (lastNearestIndex < 0 || lastNearestIndex >= count)
        {
            return ProjectToArcDistanceFullScan(worldPos, out lastNearestIndex);
        }

        const int windowRadius = 16;
        int minIdx = Mathf.Max(0, lastNearestIndex - windowRadius);
        int maxIdx = Mathf.Min(count - 1, lastNearestIndex + windowRadius);

        int bestIdx = FindNearestIndexInRange(worldPos.x, worldPos.z, minIdx, maxIdx, out float bestSqrDist);

        // Jika kapal berpindah sangat jauh (mis. teleport), lakukan scan penuh 1x
        if (bestSqrDist > 900f) // > 30 m
        {
            return ProjectToArcDistanceFullScan(worldPos, out lastNearestIndex);
        }

        lastNearestIndex = bestIdx;
        return InterpolateArcDistanceAt(worldPos.x, worldPos.z, bestIdx);
    }

    private float ProjectToArcDistanceFullScan(Vector3 worldPos, out int nearestIdx)
    {
        nearestIdx = FindNearestIndexInRange(worldPos.x, worldPos.z, 0, lutX.Length - 1, out _);
        return InterpolateArcDistanceAt(worldPos.x, worldPos.z, nearestIdx);
    }

    private int FindNearestIndexInRange(float px, float pz, int startIdx, int endIdx, out float bestSqrDist)
    {
        int bestIdx = startIdx;
        bestSqrDist = float.MaxValue;

        for (int i = startIdx; i <= endIdx; i++)
        {
            float dx = px - lutX[i];
            float dz = pz - lutZ[i];
            float sqr = dx * dx + dz * dz;
            if (sqr < bestSqrDist)
            {
                bestSqrDist = sqr;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    private float InterpolateArcDistanceAt(float px, float pz, int idx)
    {
        int last = lutX.Length - 1;
        float bestArc = lutArcDist[idx];
        float bestSegSqr = float.MaxValue;

        // Periksa proyeksi pada segmen [idx - 1, idx] dan [idx, idx + 1] untuk hasil meter yang halus
        if (idx > 0)
        {
            float arc = ProjectOnSegment(px, pz, idx - 1, idx, out float sqrDist);
            if (sqrDist < bestSegSqr)
            {
                bestSegSqr = sqrDist;
                bestArc = arc;
            }
        }

        if (idx < last)
        {
            float arc = ProjectOnSegment(px, pz, idx, idx + 1, out float sqrDist);
            if (sqrDist < bestSegSqr)
            {
                bestArc = arc;
            }
        }

        return bestArc;
    }

    private float ProjectOnSegment(float px, float pz, int a, int b, out float sqrDist)
    {
        float ax = lutX[a];
        float az = lutZ[a];
        float abx = lutX[b] - ax;
        float abz = lutZ[b] - az;
        float abLenSqr = abx * abx + abz * abz;

        float t = 0f;
        if (abLenSqr > 1e-6f)
        {
            t = Mathf.Clamp01(((px - ax) * abx + (pz - az) * abz) / abLenSqr);
        }

        float cx = ax + abx * t;
        float cz = az + abz * t;
        float dx = px - cx;
        float dz = pz - cz;
        sqrDist = dx * dx + dz * dz;

        return Mathf.Lerp(lutArcDist[a], lutArcDist[b], t);
    }
}

