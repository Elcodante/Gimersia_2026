using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Validation helpers shared by RiverLevelBuilder and editor/debug tools.
/// Hookable trash is deliberately not accepted as an anchor route node.
/// </summary>
public static class RiverLevelValidator
{
    public sealed class Report
    {
        public bool IsValid;
        public int AnchorCount;
        public int ObstacleCount;
        public float MaxAnchorGap;
        public float FirstAnchorGap;
        public float LastAnchorToGoalGap;
        public int BoatCorridorViolations;
        public int RiverBoundaryViolations;
        public int OverlapViolations;
        public string Message;
    }

    public static Report ValidateAnchorRoute(
        Vector3 boatStart,
        IReadOnlyList<Vector3> rockAnchors,
        Vector3 finishPosition,
        float maxHookDistance,
        float finishReachAllowance)
    {
        var report = new Report
        {
            AnchorCount = rockAnchors != null ? rockAnchors.Count : 0,
            IsValid = true
        };

        var text = new StringBuilder(512);
        text.AppendLine("=== RIVER ANCHOR ROUTE ===");
        text.AppendLine($"Anchor count: {report.AnchorCount}");

        if (rockAnchors == null || rockAnchors.Count == 0)
        {
            report.IsValid = false;
            text.AppendLine("FAILED: no hookable rock anchors were generated.");
            report.Message = text.ToString();
            return report;
        }

        report.FirstAnchorGap = FlatDistance(boatStart, rockAnchors[0]);
        text.AppendLine($"Start -> Rock 01: {report.FirstAnchorGap:F2} m");
        if (report.FirstAnchorGap > maxHookDistance)
        {
            report.IsValid = false;
            text.AppendLine($"FAILED: first anchor is outside safe hook range ({maxHookDistance:F2} m).");
        }

        for (int i = 1; i < rockAnchors.Count; i++)
        {
            float gap = FlatDistance(rockAnchors[i - 1], rockAnchors[i]);
            report.MaxAnchorGap = Mathf.Max(report.MaxAnchorGap, gap);
            if (gap > maxHookDistance)
            {
                report.IsValid = false;
                text.AppendLine($"FAILED: Rock {i:00} -> Rock {i + 1:00}: {gap:F2} m (limit {maxHookDistance:F2} m).");
            }
        }

        report.LastAnchorToGoalGap = FlatDistance(rockAnchors[rockAnchors.Count - 1], finishPosition);
        text.AppendLine($"Last rock -> Finish center: {report.LastAnchorToGoalGap:F2} m");
        if (report.LastAnchorToGoalGap > finishReachAllowance)
        {
            report.IsValid = false;
            text.AppendLine($"FAILED: last anchor is too far from the finish approach ({finishReachAllowance:F2} m allowance).");
        }

        text.AppendLine($"Maximum anchor gap: {report.MaxAnchorGap:F2} m");
        text.AppendLine(report.IsValid ? "Anchor Route: PASSED" : "Anchor Route: FAILED");
        report.Message = text.ToString();
        return report;
    }

    public static float FlatDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f;
        b.y = 0f;
        return Vector3.Distance(a, b);
    }

    public static float DistancePointToBoundsXZ(Vector3 point, Bounds bounds)
    {
        float closestX = Mathf.Clamp(point.x, bounds.min.x, bounds.max.x);
        float closestZ = Mathf.Clamp(point.z, bounds.min.z, bounds.max.z);
        float dx = point.x - closestX;
        float dz = point.z - closestZ;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }
}
