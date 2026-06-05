using System.Collections.Generic;
using UnityEngine;

namespace PeakRoutePlanner.Planning;

public enum SurfaceKind
{
    Standable,
    Climbable,
    Blocked,
}

public enum MoveKind
{
    SurfaceClimb,
    StandWalk,
    StandJump,
    AirTransfer,
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

public readonly struct RouteEdge
{
    public RouteEdge(int from, int to, MoveKind kind, float distance, float staminaCost)
    {
        From = from;
        To = to;
        Kind = kind;
        Distance = distance;
        StaminaCost = staminaCost;
    }

    public int From { get; }

    public int To { get; }

    public MoveKind Kind { get; }

    public float Distance { get; }

    public float StaminaCost { get; }
}

public sealed class PlannerConfig
{
    public float CorridorInitialRadius { get; set; }

    public float CorridorRadiusStep { get; set; }

    public float MaxCorridorRadius { get; set; }

    public float HorizontalSampleSpacing { get; set; }

    public float SurfaceSamplingWindowRadius { get; set; }

    public int MaxSamplingWindowsPerSide { get; set; }

    public int MaxGuideCurveSamples { get; set; }

    public float AdaptiveGuideMinimumStep { get; set; }

    public float MinimumPartialSegmentDistance { get; set; }

    public float MinimumFrontierAdvanceDistance { get; set; }

    public bool EnableBidirectionalFrontierSampling { get; set; }

    public bool EnableCorridorExpansion { get; set; }

    public int MaxPhysicsQueriesPerFrame { get; set; }

    public int MaxEdgeValidationsPerFrame { get; set; }

    public int MaxEdgeCandidateChecksPerFrame { get; set; }

    public int MaxSurfacePointsPerAttempt { get; set; }

    public int MaxEdgeCandidatesPerAttempt { get; set; }

    public float MaxMainThreadMillisecondsPerFrame { get; set; }

    public float MaxSampleVerticalLayerGap { get; set; }

    public float MaxClimbDistancePerStamina { get; set; }

    public float MaxStandJumpDistance { get; set; }

    public float MaxAirTransferDistance { get; set; }

    public float MaxWalkStepUpHeight { get; set; }

    public float MaxWalkDropHeight { get; set; }

    public float MaxStandJumpUpHeight { get; set; }

    public float MaxStandJumpDropHeight { get; set; }

    public float MaxAirTransferVerticalDelta { get; set; }

    public float NormalStandJumpDistance { get; set; }

    public float SprintStandJumpDistance { get; set; }

    public float AirTransferJumpDistance { get; set; }

    public float JumpStaminaCost { get; set; }

    public float SprintJumpStaminaCost { get; set; }

    public float ClimbJumpStaminaCost { get; set; }

    public float SprintStaminaUsagePerSecond { get; set; }

    public float ClimbSpeed { get; set; }

    public float ClimbStaminaUsagePerSecond { get; set; }

    public float MinClimbStaminaUsagePerSecond { get; set; }

    public float ClimbStaminaMinimumMultiplier { get; set; }

    public float CharacterStaminaMultiplier { get; set; }

    public float AscentStaminaMultiplier { get; set; }

    public float VerticalScanPadding { get; set; }

    public float SurfaceNeighborDistance { get; set; }

    public float StandableNormalAngle { get; set; }

    public float MaxClimbableNormalAngle { get; set; }

    public int StaminaBuckets { get; set; }

    public int SearchMaxExpandedStates { get; set; }

    public float SearchDistanceFieldHeuristicWeight { get; set; }

    public float SearchGuideDistanceWeight { get; set; }

    public float SearchBacktrackPenaltyMultiplier { get; set; }

    public float SearchLongStepPenaltyMultiplier { get; set; }

    public float SurfaceClimbMovePenalty { get; set; }

    public float StandJumpMovePenalty { get; set; }

    public float AirTransferMovePenalty { get; set; }
}

public sealed class RouteResult
{
    public bool Found { get; set; }

    public bool IsPartial { get; set; }

    public List<Vector3> Path { get; } = [];

    public List<int> NodeIds { get; } = [];

    public float TotalDistance { get; set; }

    public float TotalStaminaCost { get; set; }

    public int SampledPointCount { get; set; }

    public int ValidEdgeCount { get; set; }

    public float CorridorRadius { get; set; }
}

internal sealed class RoutePlannerSnapshot
{
    internal RoutePlannerSnapshot(
        IReadOnlyList<SurfacePoint> points,
        IReadOnlyList<RouteEdge> edges,
        int startIndex,
        int targetIndex,
        IReadOnlyList<Vector3> guidePath,
        IReadOnlyList<Vector3> recentFrontierPositions,
        IReadOnlyList<Vector3> blockedFrontierPositions,
        bool preferRecoveryDetour,
        PlannerConfig config,
        float corridorRadius)
    {
        Points = points;
        Edges = edges;
        StartIndex = startIndex;
        TargetIndex = targetIndex;
        GuidePath = guidePath;
        RecentFrontierPositions = recentFrontierPositions;
        BlockedFrontierPositions = blockedFrontierPositions;
        PreferRecoveryDetour = preferRecoveryDetour;
        Config = config;
        CorridorRadius = corridorRadius;
    }

    internal IReadOnlyList<SurfacePoint> Points { get; }

    internal IReadOnlyList<RouteEdge> Edges { get; }

    internal int StartIndex { get; }

    internal int TargetIndex { get; }

    internal IReadOnlyList<Vector3> GuidePath { get; }

    internal IReadOnlyList<Vector3> RecentFrontierPositions { get; }

    internal IReadOnlyList<Vector3> BlockedFrontierPositions { get; }

    internal bool PreferRecoveryDetour { get; }

    internal PlannerConfig Config { get; }

    internal float CorridorRadius { get; }
}
