#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;
using Unity.Netcode;
using UnityEngine.Splines;

/// <summary>
/// Generates a deterministic, scene-baked river layout in Edit Mode.
/// No runtime procedural generation is required. The generated prefab instances are
/// saved with the scene and are identical for every player loading that scene.
/// </summary>
public sealed class RiverLevelBakerWindow : EditorWindow
{
    private const string GeneratedRootName = "Baked_RiverLevel";
    private const string NetworkTrashPrefix = "Baked_RiverLevel_NetworkTrash_";
    private const string PrefabFolder = "Assets/ProceduralRiver/Resources/RiverObstacles";

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

    [SerializeField] private RiverPath river;
    [SerializeField] private RiverFinishGoal finishGoal;
    [SerializeField] private GameObject boatColliderSource;

    [SerializeField] private int seed = 173377315;
    [SerializeField, Min(25)] private int splineSamples = 1600;
    [SerializeField, Min(6f)] private float anchorSpacing = 14f;
    [SerializeField, Min(1f)] private float anchorLateralOffset = 7f;
    [SerializeField, Min(1f)] private float lassoRange = 25f;
    [SerializeField, Min(0f)] private float hookSafetyMargin = 4f;
    [SerializeField, Min(0f)] private float boatClearanceMargin = 0.75f;
    [SerializeField, Min(0f)] private float anchorEdgeMargin = 2f;
    [SerializeField, Min(0f)] private float riverEdgeMargin = 2f;
    [SerializeField, Min(0f)] private float obstacleSeparation = 2f;
    [SerializeField, Min(1f)] private float firstAnchorDistance = 8f;
    [SerializeField, Min(3f)] private float finalAnchorDistanceFromEnd = 20f;
    [SerializeField, Min(5f)] private float finishObstacleClearance = 40f;
    [SerializeField, Min(0f)] private float objectFloatOffset = 0.45f;

    [SerializeField, Min(0f)] private float staticRocksPer100m = 2.4f;
    [SerializeField, Min(0f)] private float staticTrashPer100m = 2.5f;
    [SerializeField, Min(0f)] private float hookableTrashPer100m = 1.2f;
    [SerializeField, Min(1f)] private float obstacleClusterRadius = 8f;
    [SerializeField, Min(0.5f)] private float minimumNavigableLaneWidth = 2.5f;
    [SerializeField, Range(0f, 0.25f)] private float branchRouteRate = 0.10f;
    [SerializeField, Range(8, 100)] private int attemptsPerObstacle = 40;
    [SerializeField] private bool removeLegacyLevelGroups = true;
    [SerializeField] private bool bakeRiverObstacleMap = true;

    [SerializeField, TextArea(12, 24)] private string lastReport = "Belum ada level yang di-generate.";
    private Vector2 scroll;

    private sealed class PrefabInfo
    {
        public GameObject Prefab;
        public float Radius;
    }

    private struct PathSample
    {
        public float T;
        public float Distance;
        public Vector3 Position;
        public Vector3 Tangent;
        public float HalfWidth;
    }

    private enum ObstacleKind
    {
        HookableRock,
        BranchRock,
        StaticRock,
        HookableTrash,
        StaticTrash
    }

    private sealed class PlacedRecord
    {
        public ObstacleKind Kind;
        public Vector3 Position;
        public float PathDistance;
        public float SignedLateral;
        public float Radius;
        public GameObject Instance;
    }

    [MenuItem("Tools/River Level Baker/Open Static Level Baker")]
    public static void Open()
    {
        RiverLevelBakerWindow window = GetWindow<RiverLevelBakerWindow>("River Level Baker");
        window.minSize = new Vector2(390f, 650f);
        window.TryAutoAssignReferences();
    }

    private void OnEnable()
    {
        TryAutoAssignReferences();
    }

    private void OnGUI()
    {
        scroll = EditorGUILayout.BeginScrollView(scroll);

        EditorGUILayout.LabelField("STATIC RIVER LEVEL BAKER", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Generate obstacle placement while editing the scene. The result is saved as ordinary scene objects; no runtime level generation or loading screen is used.",
            MessageType.Info);

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Scene References", EditorStyles.boldLabel);
        river = (RiverPath)EditorGUILayout.ObjectField("River Path", river, typeof(RiverPath), true);
        finishGoal = (RiverFinishGoal)EditorGUILayout.ObjectField("Finish Goal", finishGoal, typeof(RiverFinishGoal), true);
        boatColliderSource = (GameObject)EditorGUILayout.ObjectField("Boat Collider Source", boatColliderSource, typeof(GameObject), true);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Auto Find References")) TryAutoAssignReferences();
            if (GUILayout.Button("Select River"))
            {
                if (river != null) Selection.activeGameObject = river.gameObject;
            }
        }

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Randomization", EditorStyles.boldLabel);
        using (new EditorGUILayout.HorizontalScope())
        {
            seed = EditorGUILayout.IntField("Seed", seed);
            if (GUILayout.Button("New Seed", GUILayout.Width(90f))) seed = UnityEngine.Random.Range(1, int.MaxValue);
        }
        splineSamples = EditorGUILayout.IntField("Spline Samples", splineSamples);

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Hookable Rock Route", EditorStyles.boldLabel);
        anchorSpacing = EditorGUILayout.FloatField("Target Anchor Spacing", anchorSpacing);
        anchorLateralOffset = EditorGUILayout.FloatField("Anchor Lateral Offset", anchorLateralOffset);
        lassoRange = EditorGUILayout.FloatField("Lasso Range", lassoRange);
        hookSafetyMargin = EditorGUILayout.FloatField("Hook Safety Margin", hookSafetyMargin);
        firstAnchorDistance = EditorGUILayout.FloatField("First Anchor From Start", firstAnchorDistance);
        finalAnchorDistanceFromEnd = EditorGUILayout.FloatField("Last Anchor Before Finish", finalAnchorDistanceFromEnd);
        branchRouteRate = EditorGUILayout.Slider("Optional Branch Route Rate", branchRouteRate, 0f, 0.25f);

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Boat & Obstacle Clearance", EditorStyles.boldLabel);
        boatClearanceMargin = EditorGUILayout.FloatField("Boat Clearance Margin", boatClearanceMargin);
        anchorEdgeMargin = EditorGUILayout.FloatField("Anchor Edge Margin", anchorEdgeMargin);
        riverEdgeMargin = EditorGUILayout.FloatField("Obstacle Edge Margin", riverEdgeMargin);
        obstacleSeparation = EditorGUILayout.FloatField("Obstacle Separation", obstacleSeparation);
        objectFloatOffset = EditorGUILayout.FloatField("Object Height Offset", objectFloatOffset);

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Obstacle Density (per 100m)", EditorStyles.boldLabel);
        staticRocksPer100m = EditorGUILayout.FloatField("Static Rocks", staticRocksPer100m);
        staticTrashPer100m = EditorGUILayout.FloatField("Static Trash", staticTrashPer100m);
        hookableTrashPer100m = EditorGUILayout.FloatField("Hookable Trash (Optional)", hookableTrashPer100m);
        obstacleClusterRadius = EditorGUILayout.FloatField("Obstacle Field Spread", obstacleClusterRadius);
        minimumNavigableLaneWidth = EditorGUILayout.FloatField("Minimum Boat Passage Width", minimumNavigableLaneWidth);
        finishObstacleClearance = EditorGUILayout.FloatField("Clear Zone Before Finish", finishObstacleClearance);
        attemptsPerObstacle = EditorGUILayout.IntSlider("Placement Attempts", attemptsPerObstacle, 8, 100);

        EditorGUILayout.Space(8f);
        removeLegacyLevelGroups = EditorGUILayout.ToggleLeft("Remove old LD_Zone level groups when generating", removeLegacyLevelGroups);
        bakeRiverObstacleMap = EditorGUILayout.ToggleLeft("Re-bake RiverObstacleMap after generation", bakeRiverObstacleMap);

        EditorGUILayout.Space(12f);
        using (new EditorGUILayout.HorizontalScope())
        {
            GUI.backgroundColor = new Color(0.35f, 0.8f, 0.55f);
            if (GUILayout.Button("GENERATE / REPLACE LEVEL", GUILayout.Height(38f))) GenerateLevel();
            GUI.backgroundColor = Color.white;
        }
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Regenerate With New Seed"))
            {
                seed = UnityEngine.Random.Range(1, int.MaxValue);
                GenerateLevel();
            }
            if (GUILayout.Button("Clear Baked Level")) ClearBakedLevel();
        }

        EditorGUILayout.Space(10f);
        EditorGUILayout.LabelField("Last Generation Report", EditorStyles.boldLabel);
        EditorGUILayout.TextArea(lastReport, GUILayout.MinHeight(210f));

        EditorGUILayout.HelpBox(
            "Workflow: Generate → inspect obstacles and route in Scene view → adjust parameters/seed if needed → save the scene (Ctrl+S) → build. The generated scene layout stays the same for Solo and Multiplayer.",
            MessageType.None);

        EditorGUILayout.EndScrollView();
    }

    [MenuItem("Tools/River Level Baker/Remove Runtime Generator Components")]
    public static void RemoveRuntimeGeneratorComponents()
    {
        Scene activeScene = SceneManager.GetActiveScene();
        MonoBehaviour[] behaviours = Resources.FindObjectsOfTypeAll<MonoBehaviour>();
        int removed = 0;
        foreach (MonoBehaviour behaviour in behaviours)
        {
            if (behaviour == null || behaviour.gameObject.scene != activeScene) continue;
            string typeName = behaviour.GetType().Name;
            if (typeName != "RiverLevelBuilder" && typeName != "RiverNetworkPrefabRegistrar") continue;
            Undo.DestroyObjectImmediate(behaviour);
            removed++;
        }
        EditorSceneManager.MarkSceneDirty(activeScene);
        EditorUtility.DisplayDialog("River Level Baker", "Runtime generator components removed: " + removed + "\nThe baked scene objects remain untouched.", "OK");
    }

    private void TryAutoAssignReferences()
    {
        if (river == null)
        {
            RiverPath[] rivers = FindObjectsByType<RiverPath>(FindObjectsSortMode.None);
            int bestKnotCount = 0;
            foreach (RiverPath candidate in rivers)
            {
                if (candidate == null || !candidate.IsValid) continue;
                SplineContainer spline = candidate.GetComponent<SplineContainer>();
                int knotCount = spline != null && spline.Spline != null ? spline.Spline.Count : 0;
                if (knotCount > bestKnotCount)
                {
                    bestKnotCount = knotCount;
                    river = candidate;
                }
            }
        }

        if (finishGoal == null) finishGoal = FindFirstObjectByType<RiverFinishGoal>();

        if (boatColliderSource == null)
        {
            BoatController boat = FindFirstObjectByType<BoatController>();
            if (boat != null) boatColliderSource = boat.gameObject;
        }

        Repaint();
    }

    private void GenerateLevel()
    {
        if (!ValidateSceneReferences(out string referenceError))
        {
            lastReport = "GENERATION ABORTED\n" + referenceError;
            EditorUtility.DisplayDialog("River Level Baker", referenceError, "OK");
            return;
        }

        if (!TryLoadPrefabPools(out PrefabInfo[] hookableRocks, out PrefabInfo[] staticRocks,
                out PrefabInfo[] hookableTrash, out PrefabInfo[] staticTrash, out string prefabError))
        {
            lastReport = "GENERATION ABORTED\n" + prefabError;
            EditorUtility.DisplayDialog("River Level Baker", prefabError, "OK");
            return;
        }

        SplineContainer splineContainer = river.GetComponent<SplineContainer>();
        if (!BuildPathTable(splineContainer, out List<PathSample> path, out float totalLength, out bool startAtT0))
        {
            const string error = "Spline tidak valid atau panjang sungai terlalu pendek.";
            lastReport = "GENERATION ABORTED\n" + error;
            EditorUtility.DisplayDialog("River Level Baker", error, "OK");
            return;
        }

        float boatHalfWidth = MeasureBoatHalfWidth(boatColliderSource);
        float boatHalfLength = MeasureBoatHalfLength(boatColliderSource);
        if (boatHalfWidth <= 0.1f) boatHalfWidth = 3f;
        if (boatHalfLength <= 0.1f) boatHalfLength = boatHalfWidth * 1.5f;

        float maxAnchorRadius = 0.5f;
        foreach (PrefabInfo info in hookableRocks) maxAnchorRadius = Mathf.Max(maxAnchorRadius, info.Radius);

        float minimumAnchorOffset = boatHalfWidth + maxAnchorRadius + boatClearanceMargin;
        float minimumAvailableOffset = float.MaxValue;
        for (float d = Mathf.Max(2f, firstAnchorDistance); d <= totalLength - Mathf.Max(3f, finalAnchorDistanceFromEnd); d += 2f)
        {
            PathSample sample = SampleAtDistance(path, d);
            minimumAvailableOffset = Mathf.Min(minimumAvailableOffset,
                sample.HalfWidth - anchorEdgeMargin - maxAnchorRadius);
        }
        if (minimumAvailableOffset == float.MaxValue) minimumAvailableOffset = 0f;

        float effectiveAnchorOffset = Mathf.Min(Mathf.Max(anchorLateralOffset, minimumAnchorOffset), minimumAvailableOffset);
        bool anchorClearancePossible = effectiveAnchorOffset >= minimumAnchorOffset - 0.01f;

        float safeHookRange = Mathf.Max(1f, lassoRange - hookSafetyMargin);
        bool alternateSides = effectiveAnchorOffset * 2f < safeHookRange * 0.92f;
        float lateralDelta = alternateSides ? effectiveAnchorOffset * 2f : 0f;
        float distanceFromHookConstraint = Mathf.Sqrt(Mathf.Max(1f, safeHookRange * safeHookRange - lateralDelta * lateralDelta)) - 0.75f;
        float effectiveSpacing = Mathf.Min(anchorSpacing, Mathf.Max(5f, distanceFromHookConstraint));

        ClearGeneratedObjects(false);
        if (removeLegacyLevelGroups) RemoveLegacyLevelGroups();

        GameObject generatedRoot = new GameObject(GeneratedRootName + "_" + seed);
        Undo.RegisterCreatedObjectUndo(generatedRoot, "Generate Static River Level");
        generatedRoot.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

        System.Random random = new System.Random(seed);
        List<PlacedRecord> placed = new List<PlacedRecord>(256);
        List<PlacedRecord> anchors = new List<PlacedRecord>(256);
        int anchorCount = 0;
        int optionalPlaced = 0;
        int optionalRejected = 0;

        EnsureTag("Batu");
        EnsureTag("Sampah");

        float startDistance = Mathf.Clamp(firstAnchorDistance, 2f, totalLength * 0.25f);
        float endBacktrack = Mathf.Clamp(finalAnchorDistanceFromEnd, 3f, totalLength * 0.25f);
        float lastAnchorDistance = Mathf.Max(startDistance + 1f, totalLength - endBacktrack);
        List<float> anchorDistances = BuildRandomAnchorDistances(
            startDistance, lastAnchorDistance, effectiveSpacing, safeHookRange, random);

        // Main route uses a random-walk side pattern and variable lateral offsets.
        // It deliberately avoids strict left-right alternation, which caused the
        // previous route to look like a repetitive zig-zag.
        int currentSide = random.Next(0, 2) == 0 ? -1 : 1;
        int sameSideRun = 0;
        for (int i = 0; i < anchorDistances.Count; i++)
        {
            PathSample sample = SampleAtDistance(path, anchorDistances[i]);
            PrefabInfo prefabInfo = hookableRocks[random.Next(0, hookableRocks.Length)];
            float maxOffset = sample.HalfWidth - anchorEdgeMargin - prefabInfo.Radius;
            float requiredOffset = boatHalfWidth + prefabInfo.Radius + boatClearanceMargin;
            if (maxOffset < requiredOffset)
            {
                lastReport = "GENERATION ABORTED\nRiver terlalu sempit untuk anchor rock dan boat clearance.";
                EditorUtility.DisplayDialog("River Level Baker", lastReport, "OK");
                ClearGeneratedObjects(false);
                return;
            }

            int preferredSide = currentSide;
            if (i > 0)
            {
                float keepSideChance = sameSideRun < 2 ? 0.78f : (sameSideRun < 4 ? 0.48f : 0.24f);
                if (random.NextDouble() > keepSideChance) preferredSide = -currentSide;
            }

            float offsetMin = Mathf.Min(maxOffset, requiredOffset + 0.35f);
            Vector3 position = Vector3.zero;
            float signedLateral = 0f;
            int selectedSide = preferredSide;
            bool foundAnchorPosition = false;
            Vector3 previousApproach = Vector3.zero;
            if (i > 0)
                previousApproach = GetBoatApproachPosition(anchors[i - 1], path, boatHalfWidth);

            for (int attempt = 0; attempt < 24; attempt++)
            {
                int side = attempt < 12 ? preferredSide : -preferredSide;
                if (attempt >= 16 && random.NextDouble() < 0.35) side = random.Next(0, 2) == 0 ? -1 : 1;

                float offset = Range(random, offsetMin, Mathf.Max(offsetMin, maxOffset));
                if (random.NextDouble() < 0.30)
                    offset = Mathf.Lerp(offsetMin, maxOffset, Range(random, 0.72f, 1f));

                if (i == 0)
                {
                    Vector3 boatStart = path[0].Position;
                    if (boatColliderSource != null && !EditorUtility.IsPersistent(boatColliderSource))
                        boatStart = boatColliderSource.transform.position;
                    float startToSplineDistance = FlatDistance(boatStart, sample.Position);
                    float firstAnchorLateralLimit = Mathf.Sqrt(Mathf.Max(0f,
                        safeHookRange * safeHookRange * 0.90f - startToSplineDistance * startToSplineDistance));
                    offset = Mathf.Min(offset, Mathf.Max(requiredOffset, firstAnchorLateralLimit));
                }
                else
                {
                    float longitudinalGap = sample.Distance - anchors[i - 1].PathDistance;
                    float maxReachableLateral = Mathf.Sqrt(Mathf.Max(0f,
                        safeHookRange * safeHookRange * 0.90f - longitudinalGap * longitudinalGap));
                    offset = Mathf.Min(offset, Mathf.Max(requiredOffset, maxReachableLateral));
                }

                float candidateLateral = side * offset;
                Vector3 candidatePosition = sample.Position + GetRight(sample.Tangent) * candidateLateral;
                candidatePosition.y = sample.Position.y + objectFloatOffset;

                bool reachable = i == 0
                    ? FlatDistance(boatColliderSource != null && !EditorUtility.IsPersistent(boatColliderSource)
                        ? boatColliderSource.transform.position : path[0].Position, candidatePosition) <= safeHookRange * 0.96f
                    : FlatDistance(previousApproach, candidatePosition) <= safeHookRange * 0.96f;
                if (!reachable) continue;
                if (!HasEnoughSeparation(candidatePosition, prefabInfo.Radius, placed)) continue;
                if (!HasNavigableCrossSectionGap(sample.Distance, sample.HalfWidth, candidateLateral,
                        prefabInfo.Radius, boatHalfWidth, boatHalfLength, placed)) continue;

                position = candidatePosition;
                signedLateral = candidateLateral;
                selectedSide = side;
                foundAnchorPosition = true;
                break;
            }

            if (!foundAnchorPosition)
            {
                lastReport = "GENERATION ABORTED\nTidak menemukan posisi anchor yang sekaligus reachable dan menyisakan jalur kapal pada jarak " + sample.Distance.ToString("F1") + " m. Coba kurangi ukuran prefab/clearance atau lebarkan sungai.";
                EditorUtility.DisplayDialog("River Level Baker", lastReport, "OK");
                ClearGeneratedObjects(false);
                return;
            }

            if (i > 0)
            {
                if (selectedSide == currentSide) sameSideRun++;
                else sameSideRun = 0;
            }
            currentSide = selectedSide;

            Quaternion rotation = Quaternion.Euler(0f, Range(random, 0f, 360f), 0f);
            GameObject instance = InstantiateObstacle(prefabInfo.Prefab, position, rotation,
                $"Anchor_{i + 1:000}_{prefabInfo.Prefab.name}", generatedRoot.transform,
                "Batu", true, false);

            PlacedRecord record = new PlacedRecord
            {
                Kind = ObstacleKind.HookableRock,
                Position = position,
                PathDistance = sample.Distance,
                SignedLateral = signedLateral,
                Radius = prefabInfo.Radius,
                Instance = instance
            };
            placed.Add(record);
            anchors.Add(record);
            anchorCount++;
        }

        // Add optional hookable-rock branches. Each branch connects two adjacent
        // main anchors as an alternate route; the main route remains fully valid.
        List<Vector3> branchVisualEdges = new List<Vector3>();
        List<Vector3> branchAnchorPositions = new List<Vector3>();
        int branchAnchorCount = PlaceBranchAnchors(hookableRocks, anchors, path, boatHalfWidth,
            boatHalfLength, safeHookRange, placed, generatedRoot.transform, random,
            branchVisualEdges, branchAnchorPositions);

        float usableLengthForObstacles = Mathf.Max(0f, totalLength - 18f - finishObstacleClearance);
        int targetStaticRocks = Mathf.RoundToInt(usableLengthForObstacles / 100f * staticRocksPer100m);
        int targetStaticTrash = Mathf.RoundToInt(usableLengthForObstacles / 100f * staticTrashPer100m);
        int targetHookableTrash = Mathf.RoundToInt(usableLengthForObstacles / 100f * hookableTrashPer100m);
        List<float> obstacleFieldCenters = BuildObstacleFieldCenters(
            totalLength, targetStaticRocks + targetStaticTrash + targetHookableTrash, random);

        optionalPlaced += PlaceOptionalObstacles(ObstacleKind.StaticRock, staticRocks, targetStaticRocks,
            path, totalLength, boatHalfWidth, boatHalfLength, placed, generatedRoot.transform,
            obstacleFieldCenters, random, ref optionalRejected);
        optionalPlaced += PlaceOptionalObstacles(ObstacleKind.StaticTrash, staticTrash, targetStaticTrash,
            path, totalLength, boatHalfWidth, boatHalfLength, placed, generatedRoot.transform,
            obstacleFieldCenters, random, ref optionalRejected);

        // Hookable trash remains optional: it can be pulled to clear a passage,
        // but the boat always has a validated route around it without hooking it.
        // Keep each NetworkObject at scene root for NGO in-scene synchronization.
        optionalPlaced += PlaceOptionalObstacles(ObstacleKind.HookableTrash, hookableTrash, targetHookableTrash,
            path, totalLength, boatHalfWidth, boatHalfLength, placed, null,
            obstacleFieldCenters, random, ref optionalRejected);

        Physics.SyncTransforms();

        // Keep an always-visible Scene view route line so level layout can be reviewed
        // without entering Play Mode or keeping this EditorWindow open.
        BakedRiverRouteGizmo routeGizmo = Undo.AddComponent<BakedRiverRouteGizmo>(generatedRoot);
        List<Vector3> visualRoute = new List<Vector3>(anchors.Count + 2);
        Vector3 visualStart = path[0].Position;
        if (boatColliderSource != null && !EditorUtility.IsPersistent(boatColliderSource))
            visualStart = boatColliderSource.transform.position;
        visualRoute.Add(visualStart);
        List<Vector3> visualAnchors = new List<Vector3>(anchors.Count);
        foreach (PlacedRecord anchor in anchors)
        {
            visualRoute.Add(anchor.Position);
            visualAnchors.Add(anchor.Position);
        }
        visualRoute.Add(finishGoal.transform.position);
        routeGizmo.SetRoute(visualRoute, visualAnchors, branchVisualEdges, branchAnchorPositions);
        EditorUtility.SetDirty(routeGizmo);

        string report = BuildValidationReport(path, totalLength, anchors, placed, boatHalfWidth,
            boatHalfLength, branchAnchorCount, safeHookRange, effectiveAnchorOffset, effectiveSpacing,
            anchorClearancePossible, optionalPlaced, optionalRejected, startAtT0);
        lastReport = report;
        Debug.Log(report, generatedRoot);

        if (bakeRiverObstacleMap)
        {
            RiverObstacleMap obstacleMap = river.GetComponent<RiverObstacleMap>();
            if (obstacleMap != null)
            {
                obstacleMap.Bake();
                EditorUtility.SetDirty(obstacleMap);
            }
        }

        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        Selection.activeGameObject = generatedRoot;
        SceneView.RepaintAll();
        EditorUtility.DisplayDialog("River Level Generated",
            $"Static level generated.\n\nMain hookable anchors: {anchorCount}\nOptional branch anchors: {branchAnchorCount}\nObstacle props: {optionalPlaced}\nSkipped placements: {optionalRejected}\n\nReview the validation report before building, then save the scene.", "OK");
    }

    private bool ValidateSceneReferences(out string error)
    {
        if (river == null || !river.IsValid || river.GetComponent<SplineContainer>() == null)
        {
            error = "RiverPath valid belum dipilih atau SplineContainer tidak ditemukan.";
            return false;
        }
        if (finishGoal == null)
        {
            error = "FinishGoal belum dipilih.";
            return false;
        }
        error = null;
        return true;
    }

    private bool TryLoadPrefabPools(out PrefabInfo[] hookableRocks, out PrefabInfo[] staticRocks,
        out PrefabInfo[] hookableTrash, out PrefabInfo[] staticTrash, out string error)
    {
        hookableRocks = LoadPool(HookableRockNames);
        staticRocks = LoadPool(StaticRockNames);
        hookableTrash = LoadPool(HookableTrashNames);
        staticTrash = LoadPool(StaticTrashNames);

        List<string> missingNames = new List<string>();
        CheckPool(hookableRocks, HookableRockNames, missingNames);
        CheckPool(staticRocks, StaticRockNames, missingNames);
        CheckPool(hookableTrash, HookableTrashNames, missingNames);
        CheckPool(staticTrash, StaticTrashNames, missingNames);

        if (missingNames.Count > 0)
        {
            error = "Prefab berikut belum ditemukan:\n- " + string.Join("\n- ", missingNames) +
                    "\n\nPastikan 12 prefab tersedia di Project. Folder default: " + PrefabFolder;
            return false;
        }

        foreach (PrefabInfo info in hookableTrash)
        {
            if (info.Prefab.GetComponent<NetworkObject>() == null || info.Prefab.GetComponent<TrashObstacle>() == null)
            {
                error = $"Prefab '{info.Prefab.name}' wajib memiliki NetworkObject dan TrashObstacle pada root agar mekanik sampah hook tetap bekerja.";
                return false;
            }
        }

        error = null;
        return true;
    }

    private static void CheckPool(PrefabInfo[] pool, string[] names, List<string> missing)
    {
        for (int i = 0; i < pool.Length; i++)
            if (pool[i] == null) missing.Add(names[i]);
    }

    private static PrefabInfo[] LoadPool(string[] names)
    {
        PrefabInfo[] result = new PrefabInfo[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            GameObject prefab = FindPrefabByExactName(names[i]);
            if (prefab == null) continue;
            result[i] = new PrefabInfo { Prefab = prefab, Radius = MeasurePrefabRadius(prefab) };
        }
        return result;
    }

    private static GameObject FindPrefabByExactName(string prefabName)
    {
        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!string.Equals(Path.GetFileNameWithoutExtension(path), prefabName, StringComparison.Ordinal)) continue;
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab != null) return prefab;
        }
        return null;
    }

    private static float MeasurePrefabRadius(GameObject prefab)
    {
        GameObject temporary = PrefabUtility.InstantiatePrefab(prefab, SceneManager.GetActiveScene()) as GameObject;
        if (temporary == null) return 1f;
        temporary.transform.SetPositionAndRotation(new Vector3(10000f, -10000f, 10000f), Quaternion.identity);
        Physics.SyncTransforms();
        Bounds bounds = GetCombinedBounds(temporary);
        Undo.DestroyObjectImmediate(temporary);
        return Mathf.Max(0.5f, Mathf.Sqrt(bounds.extents.x * bounds.extents.x + bounds.extents.z * bounds.extents.z));
    }

    private static float MeasureBoatHalfWidth(GameObject source)
    {
        if (source == null) return 3f;

        GameObject temporary = null;
        GameObject instance = source;
        if (EditorUtility.IsPersistent(source))
        {
            temporary = PrefabUtility.InstantiatePrefab(source, SceneManager.GetActiveScene()) as GameObject;
            if (temporary == null) return 3f;
            instance = temporary;
            instance.transform.SetPositionAndRotation(new Vector3(10000f, -10000f, 10000f), Quaternion.identity);
            Physics.SyncTransforms();
        }

        Bounds bounds = GetCombinedBounds(instance);
        float halfWidth = Mathf.Min(bounds.extents.x, bounds.extents.z);
        if (temporary != null) Undo.DestroyObjectImmediate(temporary);
        return Mathf.Max(0.5f, halfWidth);
    }

    private static float MeasureBoatHalfLength(GameObject source)
    {
        if (source == null) return 4.5f;

        GameObject temporary = null;
        GameObject instance = source;
        if (EditorUtility.IsPersistent(source))
        {
            temporary = PrefabUtility.InstantiatePrefab(source, SceneManager.GetActiveScene()) as GameObject;
            if (temporary == null) return 4.5f;
            instance = temporary;
            instance.transform.SetPositionAndRotation(new Vector3(10000f, -10000f, 10000f), Quaternion.identity);
            Physics.SyncTransforms();
        }

        Bounds bounds = GetCombinedBounds(instance);
        float halfLength = Mathf.Max(bounds.extents.x, bounds.extents.z);
        if (temporary != null) Undo.DestroyObjectImmediate(temporary);
        return Mathf.Max(0.5f, halfLength);
    }

    private static Bounds GetCombinedBounds(GameObject instance)
    {
        Collider[] colliders = instance.GetComponentsInChildren<Collider>(true);
        Bounds result = new Bounds(instance.transform.position, Vector3.one);
        bool found = false;
        foreach (Collider collider in colliders)
        {
            if (collider == null || !collider.enabled || collider.isTrigger) continue;
            if (!found) { result = collider.bounds; found = true; }
            else result.Encapsulate(collider.bounds);
        }

        if (!found)
        {
            Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in renderers)
            {
                if (renderer == null || !renderer.enabled) continue;
                if (!found) { result = renderer.bounds; found = true; }
                else result.Encapsulate(renderer.bounds);
            }
        }

        return result;
    }

    private bool BuildPathTable(SplineContainer container, out List<PathSample> table,
        out float totalLength, out bool startAtT0)
    {
        table = new List<PathSample>(Mathf.Clamp(splineSamples, 128, 10000));
        totalLength = 0f;

        Vector3 t0 = container.EvaluatePosition(0f);
        Vector3 t1 = container.EvaluatePosition(1f);
        float finishT0 = FlatDistance(finishGoal.transform.position, t0);
        float finishT1 = FlatDistance(finishGoal.transform.position, t1);

        if (Mathf.Abs(finishT0 - finishT1) > 2f)
        {
            startAtT0 = finishT0 > finishT1;
        }
        else if (boatColliderSource != null && !EditorUtility.IsPersistent(boatColliderSource))
        {
            startAtT0 = FlatDistance(boatColliderSource.transform.position, t0) <= FlatDistance(boatColliderSource.transform.position, t1);
        }
        else
        {
            startAtT0 = true;
        }

        int count = Mathf.Clamp(splineSamples, 128, 10000);
        Vector3 previous = Vector3.zero;
        for (int i = 0; i < count; i++)
        {
            float travelT = i / (float)(count - 1);
            float splineT = startAtT0 ? travelT : 1f - travelT;
            Vector3 position = container.EvaluatePosition(splineT);
            Vector3 tangent = container.EvaluateTangent(splineT);
            if (!startAtT0) tangent = -tangent;
            tangent.y = 0f;
            if (tangent.sqrMagnitude < 0.0001f) tangent = Vector3.forward;
            tangent.Normalize();

            if (i > 0) totalLength += FlatDistance(previous, position);
            table.Add(new PathSample
            {
                T = splineT,
                Distance = totalLength,
                Position = position,
                Tangent = tangent,
                HalfWidth = Mathf.Max(0.1f, river.WidthAt(splineT) * 0.5f)
            });
            previous = position;
        }
        return totalLength > 15f;
    }

    private static List<float> BuildRandomAnchorDistances(float first, float last, float spacing,
        float safeHookRange, System.Random random)
    {
        List<float> values = new List<float>();
        if (last <= first)
        {
            values.Add(Mathf.Max(1f, first));
            return values;
        }

        // Use a slightly denser base step, then jitter intermediate samples. This
        // gives uneven spacing without leaving an unexpectedly tiny final gap.
        float maxStep = Mathf.Max(6f, Mathf.Min(spacing * 1.20f, safeHookRange * 0.68f));
        int intervals = Mathf.Max(1, Mathf.CeilToInt((last - first) / (maxStep * 0.88f)));
        float baseStep = (last - first) / intervals;
        float jitter = baseStep * 0.05f;

        values.Add(first);
        for (int i = 1; i < intervals; i++)
        {
            float distance = first + baseStep * i + Range(random, -jitter, jitter);
            values.Add(distance);
        }
        values.Add(last);
        return values;
    }

    private List<float> BuildObstacleFieldCenters(float totalLength, int targetCount, System.Random random)
    {
        List<float> centers = new List<float>();
        float start = 22f;
        float end = totalLength - finishObstacleClearance - 12f;
        if (end <= start || targetCount <= 0) return centers;

        // Several obstacles are grouped into loose "fields" instead of being
        // uniformly scattered as isolated dots. This creates readable hazard areas.
        int fieldCount = Mathf.Max(1, Mathf.CeilToInt(targetCount / 2.2f));
        float cellLength = (end - start) / fieldCount;
        for (int i = 0; i < fieldCount; i++)
        {
            float center = start + cellLength * (i + 0.5f);
            center += Range(random, -cellLength * 0.28f, cellLength * 0.28f);
            centers.Add(Mathf.Clamp(center, start, end));
        }
        return centers;
    }

    private int PlaceBranchAnchors(PrefabInfo[] pool, List<PlacedRecord> mainAnchors,
        List<PathSample> path, float boatHalfWidth, float boatHalfLength, float safeHookRange, List<PlacedRecord> placed,
        Transform parent, System.Random random, List<Vector3> visualEdges, List<Vector3> branchPositions)
    {
        if (pool == null || pool.Length == 0 || mainAnchors == null || mainAnchors.Count < 5 || branchRouteRate <= 0f)
            return 0;

        int targetCount = Mathf.Clamp(Mathf.RoundToInt((mainAnchors.Count - 1) * branchRouteRate), 1, 40);
        List<int> segmentOrder = new List<int>();
        for (int i = 1; i < mainAnchors.Count - 2; i++) segmentOrder.Add(i);
        for (int i = segmentOrder.Count - 1; i > 0; i--)
        {
            int swap = random.Next(0, i + 1);
            int temp = segmentOrder[i];
            segmentOrder[i] = segmentOrder[swap];
            segmentOrder[swap] = temp;
        }

        List<int> usedSegments = new List<int>();
        int accepted = 0;
        for (int candidateIndex = 0; candidateIndex < segmentOrder.Count && accepted < targetCount; candidateIndex++)
        {
            int segment = segmentOrder[candidateIndex];
            bool tooCloseToBranch = false;
            foreach (int used in usedSegments)
            {
                if (Mathf.Abs(segment - used) < 4) { tooCloseToBranch = true; break; }
            }
            if (tooCloseToBranch) continue;

            PlacedRecord from = mainAnchors[segment];
            PlacedRecord to = mainAnchors[segment + 1];
            float segmentLength = to.PathDistance - from.PathDistance;
            if (segmentLength < 8f) continue;

            float branchDistance = Mathf.Lerp(from.PathDistance, to.PathDistance, Range(random, 0.34f, 0.66f));
            PathSample sample = SampleAtDistance(path, branchDistance);
            PrefabInfo info = pool[random.Next(0, pool.Length)];
            float minOffset = boatHalfWidth + info.Radius + boatClearanceMargin + 0.35f;
            float maxOffset = sample.HalfWidth - anchorEdgeMargin - info.Radius;
            if (maxOffset < minOffset) continue;

            int preferredSide;
            if (Mathf.Sign(from.SignedLateral) == Mathf.Sign(to.SignedLateral))
                preferredSide = from.SignedLateral < 0f ? -1 : 1;
            else
                preferredSide = random.Next(0, 2) == 0 ? (from.SignedLateral < 0f ? -1 : 1) : (to.SignedLateral < 0f ? -1 : 1);

            bool placedBranch = false;
            for (int attempt = 0; attempt < 10; attempt++)
            {
                int side = attempt < 6 ? preferredSide : -preferredSide;
                float offset = Range(random, minOffset, maxOffset);
                if (random.NextDouble() < 0.35) offset = Mathf.Lerp(minOffset, maxOffset, Range(random, 0.75f, 1f));
                float signedLateral = side * offset;
                Vector3 position = sample.Position + GetRight(sample.Tangent) * signedLateral;
                position.y = sample.Position.y + objectFloatOffset;

                PlacedRecord proposed = new PlacedRecord
                {
                    Kind = ObstacleKind.BranchRock,
                    Position = position,
                    PathDistance = sample.Distance,
                    SignedLateral = signedLateral,
                    Radius = info.Radius
                };

                Vector3 fromApproach = GetBoatApproachPosition(from, path, boatHalfWidth);
                Vector3 branchApproach = GetBoatApproachPosition(proposed, path, boatHalfWidth);
                float branchReachLimit = safeHookRange * 0.90f;
                if (FlatDistance(fromApproach, position) > branchReachLimit) continue;
                if (FlatDistance(branchApproach, to.Position) > branchReachLimit) continue;
                if (!HasEnoughSeparation(position, info.Radius, placed)) continue;
                if (!HasNavigableCrossSectionGap(sample.Distance, sample.HalfWidth, signedLateral,
                        info.Radius, boatHalfWidth, boatHalfLength, placed)) continue;

                Quaternion rotation = Quaternion.Euler(0f, Range(random, 0f, 360f), 0f);
                GameObject instance = InstantiateObstacle(info.Prefab, position, rotation,
                    $"BranchAnchor_{accepted + 1:000}_{info.Prefab.name}", parent, "Batu", true, false);
                proposed.Instance = instance;
                placed.Add(proposed);
                branchPositions.Add(position);
                visualEdges.Add(from.Position);
                visualEdges.Add(position);
                visualEdges.Add(position);
                visualEdges.Add(to.Position);
                usedSegments.Add(segment);
                accepted++;
                placedBranch = true;
                break;
            }

            if (!placedBranch) continue;
        }

        return accepted;
    }

    private Vector3 GetBoatApproachPosition(PlacedRecord anchor, List<PathSample> path, float boatHalfWidth)
    {
        PathSample sample = SampleAtDistance(path, anchor.PathDistance);
        float inward = anchor.Radius + boatHalfWidth + boatClearanceMargin + 0.25f;
        float approachOffset = anchor.SignedLateral - Mathf.Sign(anchor.SignedLateral) * inward;
        float maxApproachOffset = Mathf.Max(0f, sample.HalfWidth - boatHalfWidth - boatClearanceMargin);
        approachOffset = Mathf.Clamp(approachOffset, -maxApproachOffset, maxApproachOffset);
        return sample.Position + GetRight(sample.Tangent) * approachOffset;
    }

    private static float PickObstacleLateral(System.Random random, float maxAbs)
    {
        // Bias a meaningful share of props toward the inner river lanes, where they
        // alter navigation, while retaining some edge props to frame the river.
        double roll = random.NextDouble();
        if (roll < 0.43)
            return Range(random, 0f, maxAbs * 0.34f);
        if (roll < 0.82)
            return Range(random, maxAbs * 0.25f, maxAbs * 0.70f);
        return Range(random, maxAbs * 0.62f, maxAbs);
    }

    private bool HasNavigableCrossSectionGap(float pathDistance, float riverHalfWidth,
        float candidateLateral, float candidateRadius, float boatHalfWidth, float boatHalfLength,
        List<PlacedRecord> placed)
    {
        float allowedMin = -riverHalfWidth + boatHalfWidth + riverEdgeMargin;
        float allowedMax = riverHalfWidth - boatHalfWidth - riverEdgeMargin;
        if (allowedMax - allowedMin < minimumNavigableLaneWidth) return false;

        List<Vector2> blockedIntervals = new List<Vector2>(placed.Count + 1);
        foreach (PlacedRecord other in placed)
        {
            // Be conservative across an entire boat-length transition: two props
            // that are separated longitudinally can still close the same passage
            // halfway between them.
            float longitudinalInfluence = 2f * (boatHalfLength + other.Radius + candidateRadius + boatClearanceMargin);
            if (Mathf.Abs(other.PathDistance - pathDistance) > longitudinalInfluence) continue;
            float blockedRadius = other.Radius + boatHalfWidth + boatClearanceMargin;
            blockedIntervals.Add(new Vector2(other.SignedLateral - blockedRadius, other.SignedLateral + blockedRadius));
        }

        float candidateBlockedRadius = candidateRadius + boatHalfWidth + boatClearanceMargin;
        blockedIntervals.Add(new Vector2(candidateLateral - candidateBlockedRadius,
            candidateLateral + candidateBlockedRadius));

        return MaximumFreeLaneWidth(allowedMin, allowedMax, blockedIntervals) >= minimumNavigableLaneWidth;
    }

    private static float MaximumFreeLaneWidth(float allowedMin, float allowedMax, List<Vector2> blockedIntervals)
    {
        if (allowedMax <= allowedMin) return 0f;
        blockedIntervals.Sort((a, b) => a.x.CompareTo(b.x));
        float cursor = allowedMin;
        float maximumGap = 0f;

        for (int i = 0; i < blockedIntervals.Count; i++)
        {
            Vector2 interval = blockedIntervals[i];
            if (interval.y <= allowedMin || interval.x >= allowedMax) continue;
            float intervalMin = Mathf.Clamp(interval.x, allowedMin, allowedMax);
            float intervalMax = Mathf.Clamp(interval.y, allowedMin, allowedMax);
            if (intervalMin > cursor) maximumGap = Mathf.Max(maximumGap, intervalMin - cursor);
            cursor = Mathf.Max(cursor, intervalMax);
            if (cursor >= allowedMax) break;
        }

        maximumGap = Mathf.Max(maximumGap, allowedMax - cursor);
        return maximumGap;
    }

    private int CountBoatPassageViolations(List<PathSample> path, List<PlacedRecord> placed,
        float boatHalfWidth, float boatHalfLength)
    {
        int violations = 0;
        int stride = Mathf.Max(1, path.Count / 900);
        for (int i = 0; i < path.Count; i += stride)
        {
            PathSample sample = path[i];
            float allowedMin = -sample.HalfWidth + boatHalfWidth + riverEdgeMargin;
            float allowedMax = sample.HalfWidth - boatHalfWidth - riverEdgeMargin;
            if (allowedMax - allowedMin < minimumNavigableLaneWidth)
            {
                violations++;
                continue;
            }

            List<Vector2> blockedIntervals = new List<Vector2>();
            foreach (PlacedRecord record in placed)
            {
                float longitudinalInfluence = boatHalfLength + record.Radius + boatClearanceMargin + 1f;
                if (Mathf.Abs(record.PathDistance - sample.Distance) > longitudinalInfluence) continue;
                float blockedRadius = record.Radius + boatHalfWidth + boatClearanceMargin;
                blockedIntervals.Add(new Vector2(record.SignedLateral - blockedRadius,
                    record.SignedLateral + blockedRadius));
            }

            if (MaximumFreeLaneWidth(allowedMin, allowedMax, blockedIntervals) < minimumNavigableLaneWidth)
                violations++;
        }
        return violations;
    }

    private static PathSample SampleAtDistance(List<PathSample> table, float distance)
    {
        distance = Mathf.Clamp(distance, 0f, table[table.Count - 1].Distance);
        int low = 0;
        int high = table.Count - 1;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (table[mid].Distance < distance) low = mid + 1;
            else high = mid;
        }

        int upper = Mathf.Clamp(low, 1, table.Count - 1);
        PathSample a = table[upper - 1];
        PathSample b = table[upper];
        float t = Mathf.InverseLerp(a.Distance, b.Distance, distance);
        Vector3 tangent = Vector3.Slerp(a.Tangent, b.Tangent, t).normalized;
        return new PathSample
        {
            T = Mathf.Lerp(a.T, b.T, t),
            Distance = distance,
            Position = Vector3.Lerp(a.Position, b.Position, t),
            Tangent = tangent,
            HalfWidth = Mathf.Lerp(a.HalfWidth, b.HalfWidth, t)
        };
    }

    private int PlaceOptionalObstacles(ObstacleKind kind, PrefabInfo[] pool, int targetCount,
        List<PathSample> path, float totalLength, float boatHalfWidth, float boatHalfLength,
        List<PlacedRecord> placed, Transform parent, List<float> fieldCenters,
        System.Random random, ref int rejected)
    {
        int accepted = 0;
        if (pool == null || pool.Length == 0 || targetCount <= 0) return 0;

        for (int i = 0; i < targetCount; i++)
        {
            bool found = false;
            for (int attempt = 0; attempt < attemptsPerObstacle; attempt++)
            {
                if (totalLength <= finishObstacleClearance + 25f) break;

                float pathDistance;
                if (fieldCenters != null && fieldCenters.Count > 0)
                {
                    float fieldCenter = fieldCenters[random.Next(0, fieldCenters.Count)];
                    pathDistance = fieldCenter + Range(random, -obstacleClusterRadius, obstacleClusterRadius);
                }
                else
                {
                    pathDistance = Range(random, 22f, totalLength - finishObstacleClearance - 12f);
                }
                pathDistance = Mathf.Clamp(pathDistance, 18f, totalLength - finishObstacleClearance - 8f);
                if (!PassesFinishDensity(pathDistance, totalLength, random)) continue;

                PathSample sample = SampleAtDistance(path, pathDistance);
                PrefabInfo info = pool[random.Next(0, pool.Length)];
                float radius = info.Radius;
                float maxLateral = sample.HalfWidth - riverEdgeMargin - radius;
                if (maxLateral < 0.5f) continue;

                // Unlike the old placement rule, obstacles are allowed inside the
                // boat's central travel area. A cross-section test keeps at least
                // one real hull-width passage open around every obstacle field.
                float lateralMagnitude = PickObstacleLateral(random, maxLateral);
                int side = random.Next(0, 2) == 0 ? -1 : 1;
                float signedLateral = side * lateralMagnitude;
                Vector3 position = sample.Position + GetRight(sample.Tangent) * signedLateral;
                position.y = sample.Position.y + objectFloatOffset;

                if (!HasEnoughSeparation(position, radius, placed)) continue;
                if (!HasNavigableCrossSectionGap(sample.Distance, sample.HalfWidth, signedLateral,
                        radius, boatHalfWidth, boatHalfLength, placed)) continue;

                bool isTrash = kind == ObstacleKind.StaticTrash || kind == ObstacleKind.HookableTrash;
                bool hookable = kind == ObstacleKind.HookableTrash;
                bool isNetworkTrash = kind == ObstacleKind.HookableTrash;
                Quaternion rotation = Quaternion.Euler(0f, Range(random, 0f, 360f), 0f);
                string objectName = isNetworkTrash
                    ? NetworkTrashPrefix + accepted.ToString("000") + "_" + info.Prefab.name
                    : kind.ToString() + "_" + accepted.ToString("000") + "_" + info.Prefab.name;

                GameObject instance = InstantiateObstacle(info.Prefab, position, rotation, objectName,
                    parent, isTrash ? "Sampah" : "Batu", hookable, isNetworkTrash);

                placed.Add(new PlacedRecord
                {
                    Kind = kind,
                    Position = position,
                    PathDistance = sample.Distance,
                    SignedLateral = signedLateral,
                    Radius = radius,
                    Instance = instance
                });
                accepted++;
                found = true;
                break;
            }

            if (!found) rejected++;
        }

        return accepted;
    }

    private bool PassesFinishDensity(float pathDistance, float totalLength, System.Random random)
    {
        float fadeStart = totalLength * 0.72f;
        if (pathDistance <= fadeStart) return true;
        float fadeEnd = Mathf.Max(fadeStart + 0.01f, totalLength - finishObstacleClearance);
        float t = Mathf.InverseLerp(fadeStart, fadeEnd, pathDistance);
        float chance = Mathf.Lerp(1f, 0.12f, t);
        return random.NextDouble() <= chance;
    }

    private bool HasEnoughSeparation(Vector3 position, float radius, List<PlacedRecord> placed)
    {
        foreach (PlacedRecord other in placed)
        {
            float minimum = radius + other.Radius + obstacleSeparation;
            if (FlatDistance(position, other.Position) < minimum) return false;
        }
        return true;
    }

    private GameObject InstantiateObstacle(GameObject prefab, Vector3 position, Quaternion rotation,
        string objectName, Transform parent, string tagName, bool hookable, bool networkObject)
    {
        GameObject instance = PrefabUtility.InstantiatePrefab(prefab, SceneManager.GetActiveScene()) as GameObject;
        if (instance == null) throw new InvalidOperationException("Prefab gagal di-instantiate: " + prefab.name);

        Undo.RegisterCreatedObjectUndo(instance, "Bake Static River Obstacle");
        instance.name = objectName;
        instance.transform.SetPositionAndRotation(position, rotation);
        if (parent != null) Undo.SetTransformParent(instance.transform, parent, "Parent Baked River Obstacle");

        HookableTarget target = instance.GetComponent<HookableTarget>();
        if (target == null) target = Undo.AddComponent<HookableTarget>(instance);
        target.Configure(hookable);
        EditorUtility.SetDirty(target);

        SetTagsForObstacle(instance, tagName);

        // Scene-placed NetworkObjects (hookable trash) stay as top-level scene objects.
        // NGO scene management synchronizes these in-scene objects to clients.
        if (networkObject && instance.GetComponent<NetworkObject>() == null)
            throw new InvalidOperationException("Hookable trash prefab tidak memiliki NetworkObject: " + prefab.name);

        PrefabUtility.RecordPrefabInstancePropertyModifications(instance.transform);
        EditorUtility.SetDirty(instance);
        return instance;
    }

    private static void SetTagsForObstacle(GameObject instance, string tagName)
    {
        instance.tag = tagName;
        Collider[] colliders = instance.GetComponentsInChildren<Collider>(true);
        foreach (Collider collider in colliders)
        {
            if (collider == null) continue;
            collider.gameObject.tag = tagName;
            PrefabUtility.RecordPrefabInstancePropertyModifications(collider.gameObject);
        }
        PrefabUtility.RecordPrefabInstancePropertyModifications(instance);
    }

    private static void EnsureTag(string tagName)
    {
        string[] tags = InternalEditorUtility.tags;
        if (Array.IndexOf(tags, tagName) >= 0) return;
        InternalEditorUtility.AddTag(tagName);
    }

    private string BuildValidationReport(List<PathSample> path, float totalLength,
        List<PlacedRecord> anchors, List<PlacedRecord> placed, float boatHalfWidth,
        float boatHalfLength, int branchAnchorCount, float safeHookRange, float anchorOffset,
        float spacing, bool anchorClearancePossible, int optionalPlaced, int optionalRejected,
        bool startAtT0)
    {
        System.Text.StringBuilder report = new System.Text.StringBuilder(3000);
        report.AppendLine("=== STATIC RIVER LEVEL VALIDATION ===");
        report.AppendLine("Seed: " + seed);
        report.AppendLine("River length: " + totalLength.ToString("F1") + " m");
        report.AppendLine("Travel direction: Spline T" + (startAtT0 ? "0 -> T1" : "1 -> T0"));
        report.AppendLine("Safe hook distance: " + safeHookRange.ToString("F1") + " m (raw range " + lassoRange.ToString("F1") + " m)");
        report.AppendLine("Measured boat half-width: " + boatHalfWidth.ToString("F2") + " m");
        report.AppendLine("Measured boat half-length: " + boatHalfLength.ToString("F2") + " m");
        report.AppendLine("Anchor lateral offset range: randomized");
        report.AppendLine("Anchor spacing used: " + spacing.ToString("F2") + " m");
        report.AppendLine("Main hookable rock anchors: " + anchors.Count);
        report.AppendLine("Optional branch anchors: " + branchAnchorCount);
        report.AppendLine("Optional obstacles placed: " + Mathf.Max(0, optionalPlaced));
        report.AppendLine("Optional placements skipped: " + optionalRejected);

        float maxAnchorGap = 0f;
        float maxBoatApproachGap = 0f;
        int routeViolations = 0;
        float firstGap = float.MaxValue;
        float lastGap = float.MaxValue;
        Vector3 boatStart = path[0].Position;
        if (boatColliderSource != null && !EditorUtility.IsPersistent(boatColliderSource))
            boatStart = boatColliderSource.transform.position;

        if (anchors.Count > 0)
        {
            firstGap = FlatDistance(boatStart, anchors[0].Position);
            if (firstGap > safeHookRange) routeViolations++;
            for (int i = 0; i < anchors.Count - 1; i++)
            {
                PlacedRecord current = anchors[i];
                PlacedRecord next = anchors[i + 1];
                float directGap = FlatDistance(current.Position, next.Position);
                maxAnchorGap = Mathf.Max(maxAnchorGap, directGap);

                // Estimate where the hull can safely stop beside the current anchor,
                // then check whether the next rock remains within the hook's safe range.
                PathSample anchorSample = SampleAtDistance(path, current.PathDistance);
                float inward = current.Radius + boatHalfWidth + boatClearanceMargin + 0.25f;
                float approachOffset = current.SignedLateral - Mathf.Sign(current.SignedLateral) * inward;
                float maxApproachOffset = Mathf.Max(0f, anchorSample.HalfWidth - boatHalfWidth - boatClearanceMargin);
                approachOffset = Mathf.Clamp(approachOffset, -maxApproachOffset, maxApproachOffset);
                Vector3 boatApproach = anchorSample.Position + GetRight(anchorSample.Tangent) * approachOffset;
                float approachGap = FlatDistance(boatApproach, next.Position);
                maxBoatApproachGap = Mathf.Max(maxBoatApproachGap, approachGap);
                if (approachGap > safeHookRange) routeViolations++;
            }
            lastGap = FlatDistance(anchors[anchors.Count - 1].Position, finishGoal.transform.position);
        }

        report.AppendLine();
        report.AppendLine("=== ROCK-ONLY ANCHOR ROUTE ===");
        report.AppendLine("Boat start -> first rock: " + (firstGap == float.MaxValue ? "N/A" : firstGap.ToString("F2") + " m"));
        report.AppendLine("Maximum anchor center gap: " + maxAnchorGap.ToString("F2") + " m");
        report.AppendLine("Maximum boat approach -> next rock: " + maxBoatApproachGap.ToString("F2") + " m");
        report.AppendLine("Last rock -> FinishGoal: " + (lastGap == float.MaxValue ? "N/A" : lastGap.ToString("F2") + " m"));

        bool routeValid = anchors.Count > 0 && routeViolations == 0;
        report.AppendLine("Anchor reachability: " + (routeValid ? "PASSED" : "FAILED (" + routeViolations + " range violations)"));
        report.AppendLine("Anchor lateral boat clearance: " + (anchorClearancePossible ? "PASSED" : "FAILED - river is too narrow for measured boat/rock size"));

        float finishEndpointGap = FlatDistance(path[path.Count - 1].Position, finishGoal.transform.position);
        float finishAllowed = finishGoal.GoalRadius + boatHalfWidth;
        bool finishValid = finishEndpointGap <= finishAllowed;
        report.AppendLine("Spline end -> FinishGoal: " + finishEndpointGap.ToString("F2") + " m (allowed center gap " + finishAllowed.ToString("F2") + " m)");
        report.AppendLine("Finish endpoint connection: " + (finishValid ? "PASSED" : "FAILED - move FinishGoal or adjust spline endpoint"));

        int boundaryViolations = 0;
        for (int i = 0; i < placed.Count; i++)
        {
            PlacedRecord record = placed[i];
            PathSample sample = SampleAtDistance(path, record.PathDistance);
            if (Mathf.Abs(record.SignedLateral) + record.Radius > sample.HalfWidth - riverEdgeMargin)
                boundaryViolations++;
        }

        // A prop is now allowed to occupy the former centreline. Validate the
        // actual remaining boat-centre intervals across the whole river instead
        // of requiring every obstacle to sit outside a fixed straight corridor.
        int corridorViolations = CountBoatPassageViolations(path, placed, boatHalfWidth, boatHalfLength);
        int overlaps = CountOverlaps(placed);
        report.AppendLine("Boat passage clearance: " + (corridorViolations == 0 ? "PASSED" : "FAILED (" + corridorViolations + " blocked cross-sections)"));
        report.AppendLine("River boundary: " + (boundaryViolations == 0 ? "PASSED" : "FAILED (" + boundaryViolations + ")"));
        report.AppendLine("Minimum navigable lane width: " + minimumNavigableLaneWidth.ToString("F1") + " m");
        report.AppendLine("Object overlap estimate: " + (overlaps == 0 ? "PASSED" : "WARNING (" + overlaps + ")"));

        bool overall = routeValid && anchorClearancePossible && finishValid && corridorViolations == 0 && boundaryViolations == 0 && overlaps == 0;
        report.AppendLine();
        report.AppendLine("OVERALL RESULT: " + (overall ? "PASSED" : "REVIEW REQUIRED"));
        if (!overall)
        {
            report.AppendLine("This is an editor-time estimate based on prefab collider bounds and the spline. Inspect the generated layout in Scene view and test the boat in Play Mode before building.");
        }
        return report.ToString();
    }

    private static int CountOverlaps(List<PlacedRecord> placed)
    {
        int count = 0;
        for (int i = 0; i < placed.Count; i++)
        {
            for (int j = i + 1; j < placed.Count; j++)
            {
                float required = placed[i].Radius + placed[j].Radius;
                if (FlatDistance(placed[i].Position, placed[j].Position) < required) count++;
            }
        }
        return count;
    }

    private void ClearBakedLevel()
    {
        ClearGeneratedObjects(true);
        EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
        lastReport = "Baked river level dihapus dari scene.";
        Repaint();
    }

    private static void ClearGeneratedObjects(bool showDialog)
    {
        Scene activeScene = SceneManager.GetActiveScene();
        GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
        int removed = 0;
        foreach (GameObject go in all)
        {
            if (go == null || go.scene != activeScene) continue;
            bool isRoot = go.name.StartsWith(GeneratedRootName + "_", StringComparison.Ordinal) || go.name == GeneratedRootName;
            bool isNetworkTrash = go.name.StartsWith(NetworkTrashPrefix, StringComparison.Ordinal);
            if (!isRoot && !isNetworkTrash) continue;
            Undo.DestroyObjectImmediate(go);
            removed++;
        }
        if (showDialog) EditorUtility.DisplayDialog("River Level Baker", "Generated objects removed: " + removed, "OK");
    }

    private static void RemoveLegacyLevelGroups()
    {
        string[] legacyNames = { "LD_Zone1_Easy", "LD_Zone2_Medium", "LD_Zone3_Hard", "LD_Zone4_Finale" };
        Scene activeScene = SceneManager.GetActiveScene();
        GameObject[] all = Resources.FindObjectsOfTypeAll<GameObject>();
        foreach (GameObject go in all)
        {
            if (go == null || go.scene != activeScene) continue;
            for (int i = 0; i < legacyNames.Length; i++)
            {
                if (!string.Equals(go.name, legacyNames[i], StringComparison.Ordinal)) continue;
                Undo.DestroyObjectImmediate(go);
                break;
            }
        }
    }

    private static float Range(System.Random random, float min, float max)
    {
        return min + (float)random.NextDouble() * (max - min);
    }

    private static Vector3 GetRight(Vector3 tangent)
    {
        tangent.y = 0f;
        if (tangent.sqrMagnitude < 0.0001f) tangent = Vector3.forward;
        return Vector3.Cross(Vector3.up, tangent.normalized).normalized;
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }
}
#endif
