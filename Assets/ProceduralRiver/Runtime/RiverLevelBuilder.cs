using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Splines;

/// <summary>
/// Builds a deterministic river world from the main RiverPath spline.
/// The progression route contains hookable ROCKS ONLY. Hookable trash is always optional.
/// Static objects are generated locally from the same seed on every peer. Hookable trash
/// is instantiated and spawned by the server only as a NetworkObject.
/// </summary>
[DisallowMultipleComponent]
public sealed class RiverLevelBuilder : MonoBehaviour
{
    private enum SpawnKind
    {
        HookableRock,
        StaticRock,
        HookableTrash,
        StaticTrash
    }

    private struct PathSample
    {
        public float T;
        public float ArcDistance;
        public Vector3 Position;
        public Vector3 Tangent;
        public float HalfWidth;
    }

    private sealed class AnchorRecord
    {
        public float PathDistance;
        public float SignedLateral;
        public Vector3 Position;
        public Bounds Bounds;
        public float HorizontalRadius;
        public GameObject Instance;
    }

    private struct LaneSample
    {
        public float PathDistance;
        public Vector3 Position;
    }

    private struct SpawnRecord
    {
        public SpawnKind Kind;
        public GameObject Prefab;
        public Vector3 Position;
        public Quaternion Rotation;
        public float PathDistance;
        public float SignedLateral;
        public bool Networked;
    }

    [Header("River Reference")]
    [SerializeField] private RiverPath river;
    [SerializeField] private RiverFinishGoal finishGoal;
    [SerializeField] private BoatController boat;
    [SerializeField, Min(256)] private int splineSamples = 1600;

    [Header("Anchor Route - Hookable Rocks Only")]
    [Tooltip("Target center-to-center distance between route rocks. Adjusted downward if the lasso range requires it.")]
    [SerializeField, Min(6f)] private float anchorSpacing = 14f;
    [SerializeField, Min(1f)] private float anchorLateralOffset = 6.3f;
    [SerializeField, Min(0f)] private float firstAnchorDistanceFromStart = 8f;
    [SerializeField, Min(5f)] private float finalAnchorDistanceFromSplineEnd = 28f;
    [SerializeField, Min(5f)] private float lassoRange = 25f;
    [SerializeField, Min(0f)] private float hookSafetyMargin = 4f;
    [SerializeField, Min(0f)] private float anchorEdgeMargin = 2f;

    [Header("Boat Clearance")]
    [Tooltip("Additional free distance around the boat hull. Actual boat collider bounds are measured at runtime.")]
    [SerializeField, Min(0f)] private float boatClearanceMargin = 0.75f;
    [SerializeField, Min(0f)] private float obstacleSeparation = 1.5f;
    [SerializeField, Min(0f)] private float anchorSurfaceGap = 0.25f;
    [SerializeField, Min(0f)] private float fallbackBoatHalfWidth = 2.9f;
    [SerializeField, Min(0f)] private float fallbackBoatFootprintRadius = 5.5f;

    [Header("Obstacle Density Per 100m")]
    [SerializeField, Min(0f)] private float staticRocksPer100m = 1.8f;
    [SerializeField, Min(0f)] private float staticTrashPer100m = 1.4f;
    [SerializeField, Min(0f)] private float hookableTrashPer100m = 0.9f;
    [SerializeField, Range(1, 20)] private int placementAttemptsPerObject = 12;
    [SerializeField, Min(0f)] private float finishObstacleClearance = 25f;
    [SerializeField, Min(0f)] private float riverEdgeMargin = 2f;
    [SerializeField, Min(0f)] private float objectFloatOffset = 0.45f;

    [Header("Generation Performance")]
    [SerializeField, Range(1, 100)] private int objectsPerFrame = 14;
    [SerializeField] private bool rebakeObstacleWaterMap = true;

    [Header("Debug")]
    [SerializeField] private bool drawGeneratedPath = true;
    [SerializeField] private bool drawBoatClearance = false;
    [SerializeField, TextArea(8, 24)] private string lastValidationReport;

    private readonly List<PathSample> pathTable = new List<PathSample>(2048);
    private readonly List<AnchorRecord> anchors = new List<AnchorRecord>(256);
    private readonly List<LaneSample> boatLane = new List<LaneSample>(1024);
    private readonly List<Bounds> placedObstacleBounds = new List<Bounds>(512);
    private readonly List<Vector3> debugObstaclePositions = new List<Vector3>(512);
    private readonly List<NetworkObject> spawnedNetworkTrash = new List<NetworkObject>(128);

    private Transform generatedRoot;
    private float totalPathLength;
    private bool startAtSplineT0;
    private bool buildComplete;
    private int lastBuiltSeed;
    private bool lastBuildValid;
    private float boatHalfWidth;
    private float boatFootprintRadius;
    private Vector3 boatStartPosition;
    private Vector3 finishPosition;
    private Vector3 travelStartEndpoint;
    private Vector3 travelFinishEndpoint;
    private float finishEndpointGap;

    private GameObject[] hookableRockPrefabs;
    private GameObject[] staticRockPrefabs;
    private GameObject[] hookableTrashPrefabs;
    private GameObject[] staticTrashPrefabs;

    public bool IsBuildComplete => buildComplete;
    public bool IsBuildValid => buildComplete && lastBuildValid;
    public int LastBuiltSeed => lastBuiltSeed;
    public string LastValidationReport => lastValidationReport;
    public int GeneratedAnchorCount => anchors.Count;

    private static readonly string[] HookableRockNames =
    {
        "Batu hook 1", "Batu hook 2", "Batu Hook 3"
    };

    private static readonly string[] StaticRockNames =
    {
        "Batu", "BatuDatar", "BatuPohon"
    };

    private static readonly string[] HookableTrashNames =
    {
        "Sampah 1", "Sampah 2", "Sampah 3"
    };

    private static readonly string[] StaticTrashNames =
    {
        "Sampah AH 1", "Sampah AH 2", "Sampah AH 3"
    };

    /// <summary>
    /// Called by GameSpawner after the host publishes the session seed.
    /// Every peer builds the same static layout; only the server spawns hookable trash NetworkObjects.
    /// </summary>
    public IEnumerator BuildWorld(int seed, bool isServer, Action<bool> completed)
    {
        if (buildComplete && lastBuiltSeed == seed)
        {
            completed?.Invoke(lastBuildValid);
            yield break;
        }

        buildComplete = false;
        lastBuildValid = false;
        lastBuiltSeed = seed;
        ClearGeneratedWorld();

        WorldLoadingUI loading = WorldLoadingUI.Show("Finding the river route...");
        yield return null;

        if (!ResolveReferences())
        {
            const string error = "RiverPath, SplineContainer, BoatController, atau FinishGoal tidak ditemukan.";
            lastValidationReport = "WORLD GENERATION FAILED\n" + error;
            Debug.LogError("[RiverLevelBuilder] " + error, this);
            loading.ShowError(error);
            completed?.Invoke(false);
            yield break;
        }

        if (!LoadPrefabPools())
        {
            const string error = "Satu atau lebih prefab tidak ditemukan di Resources/RiverObstacles.";
            lastValidationReport = "WORLD GENERATION FAILED\n" + error;
            loading.ShowError(error);
            completed?.Invoke(false);
            yield break;
        }

        if (!BuildSplinePathTable())
        {
            const string error = "Spline sungai tidak valid atau panjangnya kurang dari 2 meter.";
            lastValidationReport = "WORLD GENERATION FAILED\n" + error;
            Debug.LogError("[RiverLevelBuilder] " + error, this);
            loading.ShowError(error);
            completed?.Invoke(false);
            yield break;
        }

        MeasureBoatFootprint();
        DetermineTravelDirection();
        CreateGeneratedRoot(seed);

        DeterministicRandom random = new DeterministicRandom(unchecked((uint)seed));
        loading.SetProgress(0.08f, "Building rock-only anchor route...");

        BuildAnchorRecords(ref random);
        if (anchors.Count == 0)
        {
            const string error = "Tidak ada anchor batu yang bisa dibuat. Periksa panjang dan lebar spline.";
            lastValidationReport = "WORLD GENERATION FAILED\n" + error;
            loading.ShowError(error);
            completed?.Invoke(false);
            yield break;
        }

        int workCounter = 0;
        for (int i = 0; i < anchors.Count; i++)
        {
            AnchorRecord record = anchors[i];
            GameObject prefab = GetRandomPrefab(hookableRockPrefabs, ref random);
            GameObject instance = Instantiate(prefab, record.Position, RandomYaw(ref random), generatedRoot);
            ConfigureObstacle(instance, isTrash: false, canBeHooked: true);
            record.Instance = instance;
            Physics.SyncTransforms();
            record.Bounds = GetCombinedColliderBounds(instance);
            record.HorizontalRadius = BoundsHorizontalRadius(record.Bounds);
            anchors[i] = record;
            placedObstacleBounds.Add(record.Bounds);

            if (++workCounter >= objectsPerFrame)
            {
                workCounter = 0;
                loading.SetProgress(0.1f + 0.38f * ((i + 1f) / anchors.Count), $"Placing hookable rocks... ({i + 1}/{anchors.Count})");
                yield return null;
            }
        }

        BuildBoatLaneSamples();
        loading.SetProgress(0.52f, "Checking boat clearance...");
        yield return null;

        List<SpawnRecord> optionalObstacles = CreateOptionalObstacleRecords(ref random);
        int placedOptional = 0;
        int attemptedOptional = 0;
        int rejectedOptional = 0;

        for (int i = 0; i < optionalObstacles.Count; i++)
        {
            SpawnRecord record = optionalObstacles[i];
            attemptedOptional++;

            // One temporary instance is repositioned for each placement attempt. This keeps
            // placement deterministic while allowing a blocked candidate to try another slot.
            GameObject candidate = Instantiate(record.Prefab);
            ConfigureObstacle(candidate, IsTrashKind(record.Kind), IsHookableKind(record.Kind));
            candidate.name = record.Prefab.name + "_Generated";

            bool validPlacement = TryFindOptionalPlacement(candidate, ref random, ref record, out Bounds candidateBounds);
            if (!validPlacement)
            {
                Destroy(candidate);
                rejectedOptional++;
            }
            else
            {
                placedObstacleBounds.Add(candidateBounds);
                debugObstaclePositions.Add(candidateBounds.center);

                if (record.Networked)
                {
                    if (isServer)
                    {
                        NetworkObject networkObject = candidate.GetComponent<NetworkObject>();
                        if (networkObject == null)
                        {
                            Debug.LogError($"[RiverLevelBuilder] '{candidate.name}' ditandai Networked tetapi tidak memiliki NetworkObject.", candidate);
                            Destroy(candidate);
                            placedObstacleBounds.RemoveAt(placedObstacleBounds.Count - 1);
                            debugObstaclePositions.RemoveAt(debugObstaclePositions.Count - 1);
                            rejectedOptional++;
                        }
                        else
                        {
                            // NGO object stays at scene root; it is not parented under a non-NetworkObject.
                            networkObject.Spawn(true);
                            spawnedNetworkTrash.Add(networkObject);
                            placedOptional++;
                        }
                    }
                    else
                    {
                        // Client uses the exact same temporary collider for deterministic placement,
                        // then removes it. The authoritative server supplies the real NetworkObject.
                        Destroy(candidate);
                        placedOptional++;
                    }
                }
                else
                {
                    candidate.transform.SetParent(generatedRoot, true);
                    placedOptional++;
                }
            }

            if (++workCounter >= objectsPerFrame)
            {
                workCounter = 0;
                float fraction = optionalObstacles.Count > 0 ? (i + 1f) / optionalObstacles.Count : 1f;
                loading.SetProgress(0.52f + 0.35f * fraction, $"Placing river obstacles... ({i + 1}/{optionalObstacles.Count})");
                yield return null;
            }
        }

        loading.SetProgress(0.9f, "Validating route and obstacle clearance...");
        Physics.SyncTransforms();
        yield return null;

        lastValidationReport = ValidateGeneratedWorld(attemptedOptional, placedOptional, rejectedOptional, isServer);
        lastBuildValid = lastValidationReport.Contains("OVERALL RESULT: PASSED");
        buildComplete = lastBuildValid;

        if (rebakeObstacleWaterMap)
        {
            RiverObstacleMap map = river.GetComponent<RiverObstacleMap>();
            if (map != null) map.Bake();
        }

        Debug.Log(lastValidationReport, this);
        if (lastBuildValid)
        {
            loading.SetProgress(1f, "River ready. Preparing players...");
            loading.Hide(0.45f);
        }
        else
        {
            loading.ShowError("Route validation failed. Check the River Level Builder report.");
        }

        completed?.Invoke(lastBuildValid);
    }

    private bool ResolveReferences()
    {
        if (river == null) river = GetComponent<RiverPath>();
        if (river == null) river = RiverPath.Main;
        if (finishGoal == null) finishGoal = RiverFinishGoal.Instance;
        if (finishGoal == null) finishGoal = FindFirstObjectByType<RiverFinishGoal>();
        if (boat == null) boat = FindFirstObjectByType<BoatController>();

        if (river == null || !river.IsValid || finishGoal == null || boat == null) return false;
        return river.GetComponent<SplineContainer>() != null;
    }

    private bool LoadPrefabPools()
    {
        hookableRockPrefabs = LoadPool(HookableRockNames);
        staticRockPrefabs = LoadPool(StaticRockNames);
        hookableTrashPrefabs = LoadPool(HookableTrashNames);
        staticTrashPrefabs = LoadPool(StaticTrashNames);

        bool valid = PoolIsValid(hookableRockPrefabs) && PoolIsValid(staticRockPrefabs) &&
                     PoolIsValid(hookableTrashPrefabs) && PoolIsValid(staticTrashPrefabs);

        if (!valid)
        {
            Debug.LogError("[RiverLevelBuilder] Prefab pool tidak lengkap. Pastikan 12 prefab ada di Assets/ProceduralRiver/Resources/RiverObstacles/.", this);
            return false;
        }

        for (int i = 0; i < hookableTrashPrefabs.Length; i++)
        {
            if (hookableTrashPrefabs[i].GetComponent<NetworkObject>() == null ||
                hookableTrashPrefabs[i].GetComponent<TrashObstacle>() == null)
            {
                Debug.LogError($"[RiverLevelBuilder] '{hookableTrashPrefabs[i].name}' wajib memiliki NetworkObject dan TrashObstacle pada root.", hookableTrashPrefabs[i]);
                return false;
            }
        }

        return true;
    }

    private static GameObject[] LoadPool(string[] names)
    {
        var result = new GameObject[names.Length];
        for (int i = 0; i < names.Length; i++)
            result[i] = Resources.Load<GameObject>("RiverObstacles/" + names[i]);
        return result;
    }

    private static bool PoolIsValid(GameObject[] pool)
    {
        if (pool == null || pool.Length == 0) return false;
        for (int i = 0; i < pool.Length; i++) if (pool[i] == null) return false;
        return true;
    }

    private bool BuildSplinePathTable()
    {
        pathTable.Clear();
        SplineContainer container = river.GetComponent<SplineContainer>();
        if (container == null || container.Spline == null || container.Spline.Count < 2) return false;

        int count = Mathf.Clamp(splineSamples, 256, 10000);
        Vector3 previous = Vector3.zero;
        float accumulated = 0f;

        for (int i = 0; i < count; i++)
        {
            float t = i / (float)(count - 1);
            Vector3 position = container.EvaluatePosition(t);
            Vector3 tangent = container.EvaluateTangent(t);
            tangent.y = 0f;
            if (tangent.sqrMagnitude > 0.0001f) tangent.Normalize();

            if (i > 0) accumulated += Vector3.Distance(previous, position);
            pathTable.Add(new PathSample
            {
                T = t,
                ArcDistance = accumulated,
                Position = position,
                Tangent = tangent,
                HalfWidth = Mathf.Max(0.1f, river.WidthAt(t) * 0.5f)
            });
            previous = position;
        }

        totalPathLength = accumulated;
        return totalPathLength > 2f;
    }

    private void MeasureBoatFootprint()
    {
        boatHalfWidth = fallbackBoatHalfWidth;
        boatFootprintRadius = fallbackBoatFootprintRadius;

        Collider[] colliders = boat.GetComponentsInChildren<Collider>(true);
        Bounds combined = new Bounds(boat.transform.position, Vector3.zero);
        bool found = false;
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider col = colliders[i];
            if (col == null || !col.enabled || !col.gameObject.activeInHierarchy || col.isTrigger) continue;
            if (!found)
            {
                combined = col.bounds;
                found = true;
            }
            else combined.Encapsulate(col.bounds);
        }

        if (found)
        {
            float smallerHorizontalExtent = Mathf.Min(combined.extents.x, combined.extents.z);
            float radius = Mathf.Sqrt(combined.extents.x * combined.extents.x + combined.extents.z * combined.extents.z);
            if (smallerHorizontalExtent > 0.1f) boatHalfWidth = smallerHorizontalExtent;
            if (radius > 0.1f) boatFootprintRadius = radius;
        }

        boatStartPosition = boat.transform.position;
        finishPosition = finishGoal.transform.position;
    }

    private void DetermineTravelDirection()
    {
        Vector3 atT0 = pathTable[0].Position;
        Vector3 atT1 = pathTable[pathTable.Count - 1].Position;
        float goalT0 = FlatDistanceXZ(finishPosition, atT0);
        float goalT1 = FlatDistanceXZ(finishPosition, atT1);

        // The endpoint nearest FinishGoal is the end of the route. This is more stable
        // across clients than relying on the boat's replicated transform on the first frame.
        if (Mathf.Abs(goalT0 - goalT1) > 2f)
        {
            startAtSplineT0 = goalT0 > goalT1;
        }
        else
        {
            // Fallback for scenes where FinishGoal is centered between both spline endpoints.
            float boatT0 = FlatDistanceXZ(boatStartPosition, atT0);
            float boatT1 = FlatDistanceXZ(boatStartPosition, atT1);
            startAtSplineT0 = boatT0 <= boatT1;
        }

        travelStartEndpoint = startAtSplineT0 ? atT0 : atT1;
        travelFinishEndpoint = startAtSplineT0 ? atT1 : atT0;
        finishEndpointGap = FlatDistanceXZ(travelFinishEndpoint, finishPosition);
    }

    private void CreateGeneratedRoot(int seed)
    {
        // Keep this at scene root. RiverObstacleMap intentionally ignores colliders that
        // are children of the River object, so parenting generated obstacles under River
        // would make the water obstacle bake miss every generated rock.
        generatedRoot = new GameObject("Generated_RiverWorld_" + seed).transform;
        generatedRoot.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
        generatedRoot.localScale = Vector3.one;
    }

    private void BuildAnchorRecords(ref DeterministicRandom random)
    {
        anchors.Clear();
        float firstDistance = Mathf.Clamp(firstAnchorDistanceFromStart, 3f, totalPathLength * 0.3f);

        // Keep the final rock close enough to the FinishGoal. A fixed distance from the
        // spline endpoint is not sufficient when the FinishGoal is offset from that endpoint.
        float finishReachAllowance = lassoRange + finishGoal.GoalRadius + 2f;
        float lateralAllowance = Mathf.Min(anchorLateralOffset, 8f);
        float desiredFinalBacktrack = Mathf.Min(finalAnchorDistanceFromSplineEnd, totalPathLength * 0.35f);
        float maxFinalBacktrack = finishReachAllowance - finishEndpointGap - lateralAllowance - 2f;
        float finalBacktrack = Mathf.Min(desiredFinalBacktrack, Mathf.Max(2f, maxFinalBacktrack));
        float lastDistance = Mathf.Max(firstDistance, totalPathLength - finalBacktrack);

        if (maxFinalBacktrack < 2f)
        {
            Debug.LogWarning(
                $"[RiverLevelBuilder] FinishGoal terlalu jauh dari ujung RiverPath ({finishEndpointGap:F1} m). " +
                "Final anchor akan dibuat sedekat mungkin ke ujung sungai, tetapi validasi tetap akan menolak level jika kapal tidak dapat mencapai finish.", this);
        }

        float minHalfWidth = float.MaxValue;
        for (float d = firstDistance; d <= lastDistance; d += Mathf.Max(5f, anchorSpacing))
            minHalfWidth = Mathf.Min(minHalfWidth, GetTravelSample(d).HalfWidth);
        if (minHalfWidth == float.MaxValue) minHalfWidth = GetTravelSample(totalPathLength * 0.5f).HalfWidth;

        const float estimatedAnchorRadius = 2.2f;
        float maxOffsetFromWidth = Mathf.Max(0f, minHalfWidth - anchorEdgeMargin - estimatedAnchorRadius);
        float effectiveOffset = Mathf.Min(anchorLateralOffset, maxOffsetFromWidth);
        float safeGap = Mathf.Max(6f, lassoRange - hookSafetyMargin);
        float crossRiverDelta = effectiveOffset * 2f;
        float spacingAllowed = Mathf.Sqrt(Mathf.Max(1f, safeGap * safeGap - crossRiverDelta * crossRiverDelta)) - 0.5f;
        float effectiveSpacing = Mathf.Min(anchorSpacing, Mathf.Max(6f, spacingAllowed));

        if (effectiveOffset < 0.75f)
            effectiveOffset = 0f;

        List<float> distances = new List<float>(Mathf.CeilToInt(totalPathLength / Mathf.Max(1f, effectiveSpacing)) + 2);
        for (float d = firstDistance; d <= lastDistance; d += effectiveSpacing)
            distances.Add(d);

        if (distances.Count == 0)
        {
            distances.Add(Mathf.Clamp(totalPathLength * 0.5f, 1f, totalPathLength - 1f));
        }
        else if (lastDistance - distances[distances.Count - 1] > 0.25f)
        {
            // Always include the intended final anchor position; otherwise the last
            // regular spacing step can leave the goal connector several metres too long.
            distances.Add(lastDistance);
        }

        int previousSide = random.NextInt(0, 2) == 0 ? -1 : 1;
        for (int i = 0; i < distances.Count; i++)
        {
            PathSample sample = GetTravelSample(distances[i]);
            int side;
            if (i == 0) side = previousSide;
            else
            {
                // Mostly alternate sides, with occasional same-side anchors to create route variation.
                side = random.NextFloat() < 0.78f ? -previousSide : previousSide;
                previousSide = side;
            }

            float noise = random.Range(-0.65f, 0.65f);
            float offset = Mathf.Clamp(effectiveOffset + noise, 0f, Mathf.Max(0f, sample.HalfWidth - anchorEdgeMargin - estimatedAnchorRadius));
            float signed = side * offset;
            Vector3 right = GetRight(sample.Tangent);
            Vector3 position = sample.Position + right * signed;
            position.y = sample.Position.y + objectFloatOffset;

            anchors.Add(new AnchorRecord
            {
                PathDistance = distances[i],
                SignedLateral = signed,
                Position = position
            });
        }
    }

    private void BuildBoatLaneSamples()
    {
        boatLane.Clear();
        if (anchors.Count == 0) return;

        float sampleStep = 2.5f;
        int count = Mathf.Max(2, Mathf.CeilToInt(totalPathLength / sampleStep) + 1);
        for (int i = 0; i < count; i++)
        {
            float d = totalPathLength * i / (count - 1f);
            PathSample sample = GetTravelSample(d);
            float signedLaneOffset = GetInterpolatedBoatLaneOffset(d);
            Vector3 position = sample.Position + GetRight(sample.Tangent) * signedLaneOffset;
            boatLane.Add(new LaneSample { PathDistance = d, Position = position });
        }

        boatLane[0] = new LaneSample { PathDistance = 0f, Position = boatStartPosition };
    }

    private float GetInterpolatedBoatLaneOffset(float pathDistance)
    {
        if (anchors.Count == 0) return 0f;

        if (pathDistance <= anchors[0].PathDistance)
        {
            float startLateral = GetSignedLateralAtWorldPosition(boatStartPosition, GetTravelSample(0f));
            float approach = GetAnchorApproachOffset(anchors[0]);
            float t = Mathf.InverseLerp(0f, anchors[0].PathDistance, pathDistance);
            return Mathf.Lerp(startLateral, approach, t);
        }

        for (int i = 1; i < anchors.Count; i++)
        {
            if (pathDistance <= anchors[i].PathDistance)
            {
                float a = GetAnchorApproachOffset(anchors[i - 1]);
                float b = GetAnchorApproachOffset(anchors[i]);
                float t = Mathf.InverseLerp(anchors[i - 1].PathDistance, anchors[i].PathDistance, pathDistance);
                return Mathf.Lerp(a, b, t);
            }
        }

        AnchorRecord last = anchors[anchors.Count - 1];
        float lastOffset = GetAnchorApproachOffset(last);
        float finishBlendStart = last.PathDistance;
        float tFinish = Mathf.InverseLerp(finishBlendStart, totalPathLength, pathDistance);
        return Mathf.Lerp(lastOffset, 0f, tFinish);
    }

    private float GetAnchorApproachOffset(AnchorRecord anchor)
    {
        // The validator checks boatFootprintRadius + boatClearanceMargin. Include that
        // same margin here, then add anchorSurfaceGap as extra separation from the rock.
        float inwardDistance = anchor.HorizontalRadius + boatFootprintRadius + boatClearanceMargin + anchorSurfaceGap;
        float approach = anchor.SignedLateral - Mathf.Sign(anchor.SignedLateral) * inwardDistance;
        PathSample sample = GetTravelSample(anchor.PathDistance);
        float maximum = Mathf.Max(0f, sample.HalfWidth - boatFootprintRadius - boatClearanceMargin);
        return Mathf.Clamp(approach, -maximum, maximum);
    }

    private List<SpawnRecord> CreateOptionalObstacleRecords(ref DeterministicRandom random)
    {
        var records = new List<SpawnRecord>();
        float usableLength = Mathf.Max(0f, totalPathLength - 18f - finishObstacleClearance);
        int rockCount = Mathf.RoundToInt(usableLength * staticRocksPer100m / 100f);
        int trashCount = Mathf.RoundToInt(usableLength * staticTrashPer100m / 100f);
        int hookTrashCount = Mathf.RoundToInt(usableLength * hookableTrashPer100m / 100f);

        AddRecords(records, SpawnKind.StaticRock, staticRockPrefabs, rockCount, false, ref random);
        AddRecords(records, SpawnKind.StaticTrash, staticTrashPrefabs, trashCount, false, ref random);
        AddRecords(records, SpawnKind.HookableTrash, hookableTrashPrefabs, hookTrashCount, true, ref random);
        Shuffle(records, ref random);
        return records;
    }

    private void AddRecords(List<SpawnRecord> records, SpawnKind kind, GameObject[] pool, int requestedCount, bool networked, ref DeterministicRandom random)
    {
        int maxCount = Mathf.Clamp(requestedCount, 0, 10000);
        for (int i = 0; i < maxCount; i++)
        {
            records.Add(new SpawnRecord
            {
                Kind = kind,
                Prefab = GetRandomPrefab(pool, ref random),
                Networked = networked
            });
        }
    }

    private bool TryFindOptionalPlacement(GameObject candidate, ref DeterministicRandom random, ref SpawnRecord record, out Bounds acceptedBounds)
    {
        acceptedBounds = default;
        if (candidate == null || totalPathLength <= 18f + finishObstacleClearance) return false;

        float minPathDistance = 18f;
        float maxPathDistance = totalPathLength - finishObstacleClearance;
        if (maxPathDistance <= minPathDistance) return false;

        for (int attempt = 0; attempt < placementAttemptsPerObject; attempt++)
        {
            float pathDistance = random.Range(minPathDistance, maxPathDistance);
            if (!PassesFinishDensityRoll(pathDistance, ref random)) continue;

            PathSample sample = GetTravelSample(pathDistance);
            Quaternion rotation = RandomYaw(ref random);
            Vector3 initialPosition = sample.Position + Vector3.up * objectFloatOffset;
            candidate.transform.SetPositionAndRotation(initialPosition, rotation);
            Physics.SyncTransforms();

            Bounds initialBounds = GetCombinedColliderBounds(candidate);
            float colliderRadius = BoundsHorizontalRadius(initialBounds);
            float riverLimit = sample.HalfWidth - riverEdgeMargin - colliderRadius;
            if (riverLimit <= 0f) continue;

            float laneOffset = GetInterpolatedBoatLaneOffset(pathDistance);
            float requiredOffsetFromLane = boatFootprintRadius + boatClearanceMargin + colliderRadius;
            int side = random.NextInt(0, 2) == 0 ? -1 : 1;
            float availableOnSide = side > 0 ? riverLimit - laneOffset : riverLimit + laneOffset;
            if (availableOnSide < requiredOffsetFromLane) continue;

            float lateralDelta = random.Range(requiredOffsetFromLane, availableOnSide);
            float signedLateral = laneOffset + side * lateralDelta;
            Vector3 position = sample.Position + GetRight(sample.Tangent) * signedLateral;
            position.y = sample.Position.y + objectFloatOffset;
            candidate.transform.SetPositionAndRotation(position, rotation);
            Physics.SyncTransforms();

            Bounds candidateBounds = GetCombinedColliderBounds(candidate);
            record.Position = position;
            record.Rotation = rotation;
            record.PathDistance = pathDistance;
            record.SignedLateral = signedLateral;

            if (!IsValidOptionalPlacement(record, candidateBounds)) continue;

            acceptedBounds = candidateBounds;
            return true;
        }

        return false;
    }

    private bool PassesFinishDensityRoll(float pathDistance, ref DeterministicRandom random)
    {
        // The final 28% of the generated river progressively becomes quieter.
        // The hard finishObstacleClearance band is still enforced separately.
        float fadeStart = totalPathLength * 0.72f;
        if (pathDistance <= fadeStart) return true;

        float fadeEnd = Mathf.Max(fadeStart + 0.01f, totalPathLength - finishObstacleClearance);
        float t = Mathf.InverseLerp(fadeStart, fadeEnd, pathDistance);
        float densityChance = Mathf.Lerp(1f, 0.12f, t);
        return random.NextFloat() <= densityChance;
    }

    private bool IsValidOptionalPlacement(SpawnRecord record, Bounds candidateBounds)
    {
        PathSample sample = GetTravelSample(record.PathDistance);
        float radius = BoundsHorizontalRadius(candidateBounds);
        float centerLateral = GetSignedLateralAtWorldPosition(candidateBounds.center, sample);
        if (Mathf.Abs(centerLateral) + radius > sample.HalfWidth - riverEdgeMargin) return false;

        if (FlatDistanceXZ(candidateBounds.center, finishPosition) < finishObstacleClearance + radius) return false;

        Bounds expanded = candidateBounds;
        expanded.Expand(obstacleSeparation * 2f);
        for (int i = 0; i < placedObstacleBounds.Count; i++)
        {
            Bounds other = placedObstacleBounds[i];
            other.Expand(obstacleSeparation * 2f);
            if (expanded.Intersects(other)) return false;
        }

        float requiredClearance = boatFootprintRadius + boatClearanceMargin;
        for (int i = 0; i < boatLane.Count; i++)
        {
            if (RiverLevelValidator.DistancePointToBoundsXZ(boatLane[i].Position, candidateBounds) < requiredClearance)
                return false;
        }

        return true;
    }

    private string ValidateGeneratedWorld(int attempted, int placed, int rejected, bool isServer)
    {
        var report = RiverLevelValidator.ValidateAnchorRoute(
            boatStartPosition,
            GetAnchorPositions(),
            finishPosition,
            Mathf.Max(1f, lassoRange - hookSafetyMargin),
            lassoRange + finishGoal.GoalRadius + 2f);

        report.ObstacleCount = placed;
        var text = new System.Text.StringBuilder(2048);
        text.AppendLine("=== RIVER LEVEL VALIDATION ===");
        text.AppendLine($"Seed: {lastBuiltSeed}");
        text.AppendLine($"River length: {totalPathLength:F1} m");
        text.AppendLine($"Travel direction: {(startAtSplineT0 ? "Spline T0 -> T1" : "Spline T1 -> T0")}");
        text.AppendLine($"Safe lasso route limit: {lassoRange - hookSafetyMargin:F1} m (raw range {lassoRange:F1} m)");
        text.AppendLine($"Boat footprint radius: {boatFootprintRadius:F2} m");
        text.AppendLine($"Spline start -> boat start: {FlatDistanceXZ(travelStartEndpoint, boatStartPosition):F2} m");
        text.AppendLine($"Spline end -> FinishGoal: {finishEndpointGap:F2} m (trigger radius {finishGoal.GoalRadius:F1} m)");
        text.AppendLine($"Hookable rock anchors: {anchors.Count}");
        text.AppendLine($"Optional obstacles accepted: {placed}/{attempted} (rejected: {rejected})");
        text.AppendLine();
        text.Append(report.Message);

        bool finishApproachValid = finishEndpointGap <= finishGoal.GoalRadius + boatFootprintRadius;
        text.AppendLine($"Finish trigger reach from spline end: {(finishApproachValid ? "PASSED" : "FAILED")} " +
                        $"(allowed center distance: {finishGoal.GoalRadius + boatFootprintRadius:F2} m)");

        bool laneValid = ValidateBoatLaneClearance(out int laneViolations);
        report.BoatCorridorViolations = laneViolations;
        text.AppendLine($"Boat corridor clearance: {(laneValid ? "PASSED" : "FAILED")} (violations: {laneViolations})");

        bool boatBoundaryValid = ValidateBoatLaneBoundaries(out int boatBoundaryViolations);
        text.AppendLine($"Boat hull inside river: {(boatBoundaryValid ? "PASSED" : "FAILED")} (violations: {boatBoundaryViolations})");

        bool approachValid = ValidateAnchorApproachDistances(out int approachViolations, out float maxApproachGap);
        text.AppendLine($"Boat approach -> next rock: {(approachValid ? "PASSED" : "FAILED")} (max: {maxApproachGap:F2} m, violations: {approachViolations})");

        bool recoveryValid = ValidateRecoveryCoverage(out int recoveryViolations, out float maxRecoveryGap);
        text.AppendLine($"Drift/re-hook recovery coverage: {(recoveryValid ? "PASSED" : "FAILED")} (max: {maxRecoveryGap:F2} m, violations: {recoveryViolations})");

        bool boundaryValid = ValidateAnchorBoundaries(out int boundaryViolations);
        report.RiverBoundaryViolations = boundaryViolations;
        text.AppendLine($"Anchor river boundary: {(boundaryValid ? "PASSED" : "FAILED")} (violations: {boundaryViolations})");

        bool overlapValid = ValidatePlacedBounds(out int overlaps);
        report.OverlapViolations = overlaps;
        text.AppendLine($"Obstacle overlap check: {(overlapValid ? "PASSED" : "FAILED")} (overlaps: {overlaps})");

        bool networkValid = !isServer || spawnedNetworkTrash.Count <= hookableTrashPrefabs.Length * Mathf.CeilToInt(totalPathLength / 100f * hookableTrashPer100m + 1f);
        text.AppendLine($"Network trash spawn check: {(networkValid ? "PASSED" : "FAILED")} (server spawned: {spawnedNetworkTrash.Count})");

        bool overall = report.IsValid && finishApproachValid && laneValid && boatBoundaryValid && approachValid && recoveryValid && boundaryValid && overlapValid && networkValid;
        text.AppendLine();
        text.AppendLine(overall ? "OVERALL RESULT: PASSED" : "OVERALL RESULT: FAILED");
        if (overall)
            text.AppendLine("Note: the required progression path uses hookable rocks only. Hookable trash is optional.");
        else
        {
            text.AppendLine("Do not release this layout to players. Review the failed checks before starting the match.");
            if (!finishApproachValid)
                text.AppendLine("ACTION: move FinishGoal to the downstream end of RiverPath, or extend the spline until the boat hull can enter the FinishGoal trigger.");
            if (!laneValid)
                text.AppendLine("ACTION: inspect Boat corridor clearance. Anchor approach clearance now includes boatClearanceMargin and anchorSurfaceGap.");
        }

        return text.ToString();
    }

    private bool ValidateBoatLaneClearance(out int violations)
    {
        violations = 0;
        float requiredClearance = boatFootprintRadius + boatClearanceMargin;
        for (int b = 0; b < placedObstacleBounds.Count; b++)
        {
            Bounds bounds = placedObstacleBounds[b];
            for (int i = 0; i < boatLane.Count; i++)
            {
                if (RiverLevelValidator.DistancePointToBoundsXZ(boatLane[i].Position, bounds) < requiredClearance)
                {
                    violations++;
                    break;
                }
            }
        }
        return violations == 0;
    }

    private bool ValidateRecoveryCoverage(out int violations, out float maxGap)
    {
        violations = 0;
        maxGap = 0f;
        if (anchors.Count == 0 || boatLane.Count == 0)
        {
            violations = 1;
            return false;
        }

        float safeRange = Mathf.Max(1f, lassoRange - hookSafetyMargin);
        float lastAnchorDistance = anchors[anchors.Count - 1].PathDistance;

        // Sample the intended boat corridor before the final anchor. If a player releases
        // the lasso and drifts along this corridor, another hookable ROCK must remain reachable.
        const int sampleStride = 4;
        for (int i = 0; i < boatLane.Count; i += sampleStride)
        {
            LaneSample laneSample = boatLane[i];
            if (laneSample.PathDistance > lastAnchorDistance) break;

            float nearest = float.MaxValue;
            for (int a = 0; a < anchors.Count; a++)
            {
                nearest = Mathf.Min(nearest, FlatDistanceXZ(laneSample.Position, anchors[a].Position));
            }

            maxGap = Mathf.Max(maxGap, nearest);
            if (nearest > safeRange) violations++;
        }

        return violations == 0;
    }

    private bool ValidateBoatLaneBoundaries(out int violations)
    {
        violations = 0;
        for (int i = 0; i < boatLane.Count; i++)
        {
            PathSample sample = GetTravelSample(boatLane[i].PathDistance);
            float lateral = GetSignedLateralAtWorldPosition(boatLane[i].Position, sample);
            if (Mathf.Abs(lateral) + boatFootprintRadius > sample.HalfWidth - riverEdgeMargin)
                violations++;
        }
        return violations == 0;
    }

    private bool ValidateAnchorApproachDistances(out int violations, out float maxGap)
    {
        violations = 0;
        maxGap = 0f;
        float safeRange = Mathf.Max(1f, lassoRange - hookSafetyMargin);

        for (int i = 0; i < anchors.Count - 1; i++)
        {
            AnchorRecord current = anchors[i];
            PathSample sample = GetTravelSample(current.PathDistance);
            Vector3 boatApproach = sample.Position + GetRight(sample.Tangent) * GetAnchorApproachOffset(current);
            boatApproach.y = current.Position.y;
            float gap = FlatDistanceXZ(boatApproach, anchors[i + 1].Position);
            maxGap = Mathf.Max(maxGap, gap);
            if (gap > safeRange) violations++;
        }

        return violations == 0;
    }

    private bool ValidateAnchorBoundaries(out int violations)
    {
        violations = 0;
        for (int i = 0; i < anchors.Count; i++)
        {
            PathSample sample = GetTravelSample(anchors[i].PathDistance);
            if (Mathf.Abs(anchors[i].SignedLateral) + anchors[i].HorizontalRadius > sample.HalfWidth - anchorEdgeMargin)
                violations++;
        }
        return violations == 0;
    }

    private bool ValidatePlacedBounds(out int overlaps)
    {
        overlaps = 0;
        for (int i = 0; i < placedObstacleBounds.Count; i++)
        {
            Bounds a = placedObstacleBounds[i];
            a.Expand(0.05f);
            for (int j = i + 1; j < placedObstacleBounds.Count; j++)
            {
                Bounds b = placedObstacleBounds[j];
                b.Expand(0.05f);
                if (a.Intersects(b)) overlaps++;
            }
        }
        return overlaps == 0;
    }

    private List<Vector3> GetAnchorPositions()
    {
        var result = new List<Vector3>(anchors.Count);
        for (int i = 0; i < anchors.Count; i++) result.Add(anchors[i].Position);
        return result;
    }

    private bool GetSignedLateralAtWorldPosition(Vector3 worldPosition, PathSample sample, out float signed)
    {
        Vector3 delta = worldPosition - sample.Position;
        delta.y = 0f;
        signed = Vector3.Dot(delta, GetRight(sample.Tangent));
        return true;
    }

    private float GetSignedLateralAtWorldPosition(Vector3 worldPosition, PathSample sample)
    {
        GetSignedLateralAtWorldPosition(worldPosition, sample, out float signed);
        return signed;
    }

    private PathSample GetTravelSample(float travelDistance)
    {
        float targetArc = startAtSplineT0 ? travelDistance : totalPathLength - travelDistance;
        targetArc = Mathf.Clamp(targetArc, 0f, totalPathLength);

        int low = 0;
        int high = pathTable.Count - 1;
        while (low < high)
        {
            int mid = (low + high) >> 1;
            if (pathTable[mid].ArcDistance < targetArc) low = mid + 1;
            else high = mid;
        }

        int upper = Mathf.Clamp(low, 1, pathTable.Count - 1);
        PathSample a = pathTable[upper - 1];
        PathSample b = pathTable[upper];
        float t = Mathf.InverseLerp(a.ArcDistance, b.ArcDistance, targetArc);
        float splineT = Mathf.Lerp(a.T, b.T, t);
        SplineContainer container = river.GetComponent<SplineContainer>();
        Vector3 position = container.EvaluatePosition(splineT);
        Vector3 tangent = container.EvaluateTangent(splineT);
        tangent.y = 0f;
        if (!startAtSplineT0) tangent = -tangent;
        if (tangent.sqrMagnitude > 0.0001f) tangent.Normalize();
        else tangent = Vector3.forward;

        return new PathSample
        {
            T = splineT,
            ArcDistance = travelDistance,
            Position = position,
            Tangent = tangent,
            HalfWidth = Mathf.Max(0.1f, river.WidthAt(splineT) * 0.5f)
        };
    }

    private static Vector3 GetRight(Vector3 tangent)
    {
        tangent.y = 0f;
        if (tangent.sqrMagnitude < 0.0001f) tangent = Vector3.forward;
        tangent.Normalize();
        return Vector3.Cross(Vector3.up, tangent).normalized;
    }

    private static float FlatDistanceXZ(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    private static Quaternion RandomYaw(ref DeterministicRandom random)
    {
        return Quaternion.Euler(0f, random.Range(0f, 360f), 0f);
    }

    private static GameObject GetRandomPrefab(GameObject[] pool, ref DeterministicRandom random)
    {
        return pool[random.NextInt(0, pool.Length)];
    }

    private void ConfigureObstacle(GameObject instance, bool isTrash, bool canBeHooked)
    {
        string tagName = isTrash ? "Sampah" : "Batu";
        try
        {
            instance.tag = tagName;
            Collider[] colliders = instance.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                colliders[i].gameObject.tag = tagName;
        }
        catch (UnityException exception)
        {
            Debug.LogError($"[RiverLevelBuilder] Tag '{tagName}' belum tersedia di Tag Manager. {exception.Message}", instance);
        }

        HookableTarget target = instance.GetComponent<HookableTarget>();
        if (target == null) target = instance.AddComponent<HookableTarget>();
        target.Configure(canBeHooked);
    }

    private static Bounds GetCombinedColliderBounds(GameObject instance)
    {
        Collider[] colliders = instance.GetComponentsInChildren<Collider>(true);
        Bounds combined = new Bounds(instance.transform.position, Vector3.one * 0.5f);
        bool found = false;
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider col = colliders[i];
            if (col == null || !col.enabled || !col.gameObject.activeInHierarchy) continue;
            if (!found)
            {
                combined = col.bounds;
                found = true;
            }
            else combined.Encapsulate(col.bounds);
        }

        if (!found)
        {
            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null || !renderers[i].enabled) continue;
                if (!found)
                {
                    combined = renderers[i].bounds;
                    found = true;
                }
                else combined.Encapsulate(renderers[i].bounds);
            }
        }

        return combined;
    }

    private static float BoundsHorizontalRadius(Bounds bounds)
    {
        return Mathf.Sqrt(bounds.extents.x * bounds.extents.x + bounds.extents.z * bounds.extents.z);
    }

    private static bool IsTrashKind(SpawnKind kind)
    {
        return kind == SpawnKind.HookableTrash || kind == SpawnKind.StaticTrash;
    }

    private static bool IsHookableKind(SpawnKind kind)
    {
        return kind == SpawnKind.HookableRock || kind == SpawnKind.HookableTrash;
    }

    private void ClearGeneratedWorld()
    {
        for (int i = spawnedNetworkTrash.Count - 1; i >= 0; i--)
        {
            NetworkObject networkObject = spawnedNetworkTrash[i];
            if (networkObject != null && networkObject.IsSpawned && NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer)
                networkObject.Despawn(true);
        }
        spawnedNetworkTrash.Clear();

        if (generatedRoot != null)
        {
            if (Application.isPlaying) Destroy(generatedRoot.gameObject);
            else DestroyImmediate(generatedRoot.gameObject);
        }

        anchors.Clear();
        boatLane.Clear();
        placedObstacleBounds.Clear();
        debugObstaclePositions.Clear();
        generatedRoot = null;
    }

    [ContextMenu("Clear Generated World")]
    private void ClearGeneratedWorldContextMenu()
    {
        ClearGeneratedWorld();
        buildComplete = false;
        lastBuildValid = false;
        lastValidationReport = "Generated world cleared.";
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawGeneratedPath) return;

        if (boatLane.Count > 1)
        {
            Gizmos.color = Color.green;
            for (int i = 1; i < boatLane.Count; i++)
                Gizmos.DrawLine(boatLane[i - 1].Position, boatLane[i].Position);

            if (drawBoatClearance)
            {
                Gizmos.color = new Color(0.1f, 0.8f, 0.4f, 0.22f);
                for (int i = 0; i < boatLane.Count; i += 8)
                    Gizmos.DrawWireSphere(boatLane[i].Position, boatFootprintRadius + boatClearanceMargin);
            }
        }

        if (anchors.Count > 0)
        {
            Gizmos.color = Color.cyan;
            for (int i = 0; i < anchors.Count; i++)
            {
                Gizmos.DrawWireSphere(anchors[i].Position, 1.1f);
                if (i > 0) Gizmos.DrawLine(anchors[i - 1].Position, anchors[i].Position);
            }
        }

        Gizmos.color = new Color(1f, 0.55f, 0.08f, 0.85f);
        for (int i = 0; i < debugObstaclePositions.Count; i++)
            Gizmos.DrawWireSphere(debugObstaclePositions[i], 1.25f);
    }

    private static void Shuffle<T>(IList<T> list, ref DeterministicRandom random)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = random.NextInt(0, i + 1);
            T temp = list[i];
            list[i] = list[j];
            list[j] = temp;
        }
    }

    private struct DeterministicRandom
    {
        private uint state;

        public DeterministicRandom(uint seed)
        {
            state = seed == 0u ? 0xA341316Cu : seed;
        }

        private uint NextUInt()
        {
            // xorshift32: same result on every peer for the same seed.
            uint x = state;
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            state = x;
            return x;
        }

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive) return minInclusive;
            uint range = (uint)(maxExclusive - minInclusive);
            return minInclusive + (int)(NextUInt() % range);
        }

        public float NextFloat()
        {
            return (NextUInt() & 0x00FFFFFFu) / 16777216f;
        }

        public float Range(float minInclusive, float maxInclusive)
        {
            return Mathf.Lerp(minInclusive, maxInclusive, NextFloat());
        }
    }
}
