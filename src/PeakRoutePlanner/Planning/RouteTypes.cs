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
