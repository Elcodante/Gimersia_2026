using UnityEngine;

/// <summary>
/// Data & kalkulasi lintasan parabola deterministik untuk lompatan ikan.
/// Menggunakan struct (value type) agar 0 alokasi GC di WebGL.
/// </summary>
public readonly struct FishTrajectory
{
    public readonly Vector3 StartPos;
    public readonly Vector3 EndPos;
    public readonly float ArcHeight;
    public readonly float SubmergeY;
    public readonly float TelegraphDuration;
    public readonly float FlightDuration;
    public readonly Vector3 HorizontalDir;

    public FishTrajectory(
        Vector3 startPos,
        Vector3 endPos,
        float arcHeight,
        float submergeY,
        float telegraphDuration,
        float flightDuration)
    {
        StartPos = startPos;
        EndPos = endPos;
        ArcHeight = Mathf.Max(0.5f, arcHeight);
        SubmergeY = submergeY;
        TelegraphDuration = Mathf.Max(0f, telegraphDuration);
        FlightDuration = Mathf.Max(0.25f, flightDuration);

        Vector3 flat = endPos - startPos;
        flat.y = 0f;
        HorizontalDir = flat.sqrMagnitude > 0.0001f ? flat.normalized : Vector3.forward;
    }

    /// <summary>
    /// Menyusun lintasan parabola dari bawah air melewati titik target (pemain) lalu
    /// menukik kembali masuk ke bawah air di sisi seberang kapal.
    /// </summary>
    public static FishTrajectory BuildThroughTarget(
        Vector3 startPos,
        Vector3 targetPos,
        float waterY,
        float submergeDepth,
        float overshootRatio,
        float extraApexHeight,
        float telegraphDuration,
        float flightDuration)
    {
        float submergeY = waterY - Mathf.Max(0.8f, submergeDepth);
        Vector3 launchStart = new Vector3(startPos.x, submergeY, startPos.z);

        Vector3 toTargetFlat = new Vector3(targetPos.x - launchStart.x, 0f, targetPos.z - launchStart.z);
        if (toTargetFlat.sqrMagnitude < 0.25f)
        {
            toTargetFlat = Vector3.forward * 6f;
        }

        overshootRatio = Mathf.Clamp(overshootRatio, 0.45f, 1.35f);
        Vector3 launchEnd = new Vector3(
            targetPos.x + toTargetFlat.x * overshootRatio,
            submergeY,
            targetPos.z + toTargetFlat.z * overshootRatio);

        // Waktu t ketika proyeksi XZ lintasan tepat melewati targetPos
        float tHit = 1f / (1f + overshootRatio);
        float parabolaFactor = Mathf.Max(0.2f, 4f * tHit * (1f - tHit));
        float requiredArc = (targetPos.y - submergeY) / parabolaFactor + Mathf.Max(0f, extraApexHeight);

        return new FishTrajectory(
            launchStart,
            launchEnd,
            Mathf.Max(2.2f, requiredArc),
            submergeY,
            telegraphDuration,
            flightDuration);
    }

    public Vector3 EvaluatePosition(float t01)
    {
        float t = Mathf.Clamp01(t01);
        Vector3 pos = Vector3.LerpUnclamped(StartPos, EndPos, t);
        pos.y += 4f * ArcHeight * t * (1f - t);
        return pos;
    }

    public Vector3 EvaluateTangent(float t01)
    {
        float t = Mathf.Clamp01(t01);
        Vector3 tangent = EndPos - StartPos;
        tangent.y += 4f * ArcHeight * (1f - 2f * t);
        return tangent.sqrMagnitude > 0.0001f ? tangent.normalized : HorizontalDir;
    }
}
