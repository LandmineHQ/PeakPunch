using UnityEngine;

namespace PeakRoutePlanner.Planning;

public enum SurfaceKind
{
    Standable,
    Climbable,
    Blocked,
}

public readonly struct SurfacePoint
{
    public SurfacePoint(int id, Vector3 position, Vector3 normal, int colliderId, SurfaceKind kind)
    {
        Id = id;
        Position = position;
        Normal = normal;
        ColliderId = colliderId;
        Kind = kind;
    }

    public int Id { get; }

    public Vector3 Position { get; }

    public Vector3 Normal { get; }

    public int ColliderId { get; }

    public SurfaceKind Kind { get; }
}


internal enum RouteEdgeKind
{
    None,
    SameRegion,
    StandWalk,
    StandJump,
    SurfaceClimb,
    ClimbAssisted,
    GraphLookahead,
}

internal readonly struct RouteEdgeValidationResult
{
    internal RouteEdgeValidationResult(bool isValid, RouteEdgeKind kind, string reason, float distance, float staminaCost)
    {
        IsValid = isValid;
        Kind = kind;
        Reason = reason;
        Distance = distance;
        StaminaCost = staminaCost;
    }

    internal bool IsValid { get; }

    internal RouteEdgeKind Kind { get; }

    internal string Reason { get; }

    internal float Distance { get; }

    internal float StaminaCost { get; }

    internal static RouteEdgeValidationResult Valid(RouteEdgeKind kind, float distance, float staminaCost = 0f)
    {
        return new RouteEdgeValidationResult(true, kind, string.Empty, distance, staminaCost);
    }

    internal static RouteEdgeValidationResult Invalid(string reason, float distance = 0f, float staminaCost = 0f)
    {
        return new RouteEdgeValidationResult(false, RouteEdgeKind.None, reason, distance, staminaCost);
    }
}

public readonly struct VerticalAirColumnDebugResult
{
    public VerticalAirColumnDebugResult(
        Vector3 seedPosition,
        Vector3 probeOrigin,
        Vector3 blockedCellCenter,
        int airCellCount,
        int checkedCellCount,
        int rawHitCount,
        bool hasBoundary,
        bool hasSurfacePoint,
        SurfaceKind surfaceKind,
        string reason,
        double elapsedMilliseconds)
    {
        SeedPosition = seedPosition;
        ProbeOrigin = probeOrigin;
        BlockedCellCenter = blockedCellCenter;
        AirCellCount = airCellCount;
        CheckedCellCount = checkedCellCount;
        RawHitCount = rawHitCount;
        HasBoundary = hasBoundary;
        HasSurfacePoint = hasSurfacePoint;
        SurfaceKind = surfaceKind;
        Reason = reason;
        ElapsedMilliseconds = elapsedMilliseconds;
    }

    public Vector3 SeedPosition { get; }

    public Vector3 ProbeOrigin { get; }

    public Vector3 BlockedCellCenter { get; }

    public int AirCellCount { get; }

    public int CheckedCellCount { get; }

    public int RawHitCount { get; }

    public bool HasBoundary { get; }

    public bool HasSurfacePoint { get; }

    public SurfaceKind SurfaceKind { get; }

    internal string Reason { get; }

    public double ElapsedMilliseconds { get; }
}

public readonly struct DebugAirBoundaryProbe
{
    public DebugAirBoundaryProbe(Vector3 origin, Vector3 direction, float distance)
    {
        Origin = origin;
        Direction = direction;
        Distance = distance;
    }

    public Vector3 Origin { get; }

    public Vector3 Direction { get; }

    internal float Distance { get; }
}

public sealed class PlannerConfig
{
    public float CorridorRadiusStep { get; set; }

    public float HorizontalSampleSpacing { get; set; }

    public float SurfaceSamplingWindowRadius { get; set; }

    public int MaxSamplingWindowsPerSide { get; set; }

    public float AdaptiveGuideMinimumStep { get; set; }

    public float MinimumPartialSegmentDistance { get; set; }

    public float MinimumFrontierAdvanceDistance { get; set; }

    public int MaxPhysicsQueriesPerFrame { get; set; }

    public int MaxSurfacePointsPerAttempt { get; set; }

    public int MaxSurfacePointsPerWindow { get; set; }

    public float MaxMainThreadMillisecondsPerFrame { get; set; }

    public float MaxSampleVerticalLayerGap { get; set; }

    public float MaxStandJumpDistance { get; set; }

    public float MaxAirTransferDistance { get; set; }

    public float NormalStandJumpDistance { get; set; }

    public float SprintStandJumpDistance { get; set; }

    public float MaxWalkStepUpHeight { get; set; }

    public float MaxWalkDropHeight { get; set; }

    public float MaxStandJumpUpHeight { get; set; }

    public float MaxStandJumpDropHeight { get; set; }

    public float VerticalScanPadding { get; set; }

    public float SurfaceNeighborDistance { get; set; }

    public float StandableNormalAngle { get; set; }

    public float MaxClimbableNormalAngle { get; set; }

    public float CurrentRegularStamina { get; set; }

    public float AscentStaminaMultiplier { get; set; }

    public float SprintStaminaUsagePerSecond { get; set; }

    public float JumpStaminaCost { get; set; }

    public float SprintJumpStaminaCost { get; set; }

    public float ClimbJumpStaminaCost { get; set; }

    public float ClimbSpeed { get; set; }

    public float ClimbStaminaUsagePerSecond { get; set; }
}
