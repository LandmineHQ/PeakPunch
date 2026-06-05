using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace PeakRoutePlanner.Planning;

internal sealed class EdgeValidator
{
    private const float CastRadius = 0.08f;
    private const float StandWalkReachSampleMultiplier = 1.5f;
    private const float MaxDerivedStandWalkReach = 0.85f;
    private const float StandWalkClearanceLift = 0.45f;
    private const float StandWalkClearanceRadius = 0.14f;
    private const float JumpLift = 0.35f;
    private const float EndpointTolerance = 0.12f;
    private const float SameSurfaceJumpNormalTolerance = 18f;
    private const float SurfaceProbeLift = 0.6f;
    private const float SurfaceProbeDepth = 1.4f;
    private const float MaxSurfaceProbeDistance = 0.45f;
    private const float ClimbSurfaceOutwardOffset = 0.18f;
    private const float ClimbSurfaceProbeDepth = 0.55f;
    private const float MaxClimbNormalAngleDelta = 55f;
    private const float MinClimbNormalFacingDot = 0.25f;
    private const int DenseStandableNeighborThreshold = 6;
    private const int MaxJumpSupportSamples = 5;
    private const int MaxSurfaceSupportSamples = 6;

    private readonly PriorityQueue<EdgeCandidate> candidates = new();
    private readonly List<RouteEdge> edges = [];
    private readonly Dictionary<CellKey, List<int>> spatialCells = [];
    private readonly HashSet<EdgeKey> candidateKeys = [];
    private readonly HashSet<EdgeKey> validatedEdgeKeys = [];
    private readonly List<CellKey> localNeighborOffsets = [];
    private readonly List<CellKey> extendedNeighborOffsets = [];
    private readonly Dictionary<CellKey, List<int>> localCells = [];
    private readonly RaycastHit[] castHits = new RaycastHit[16];

    private IReadOnlyList<SurfacePoint> points = [];
    private PlannerConfig config = null!;
    private List<int>? candidateCellPoints;
    private float cellSize;
    private float localCellSize;
    private int candidateBuildIndex;
    private int candidateOffsetIndex;
    private int candidateCellPointIndex;
    private int collisionMask;
    private int preservedPointCount;
    private int validatedPointCount;
    private Vector3 priorityStartPosition;
    private Vector3 priorityTargetPosition;
    private bool[] standJumpEligible = [];
    private bool allowPreviousPointCandidates;
    private bool candidateBuildComplete;

    internal IReadOnlyList<RouteEdge> Edges => edges;

    internal int PendingCandidateCount => candidates.Count;

    internal bool IsBuildingCandidates => !candidateBuildComplete;

    internal bool HitCandidateLimit { get; private set; }

    internal int GeneratedCandidateCount { get; private set; }

    internal int ProcessedCandidateCount { get; private set; }

    internal int ProcessedCandidatePairCount { get; private set; }

    internal void Begin(
        IReadOnlyList<SurfacePoint> surfacePoints,
        PlannerConfig plannerConfig,
        Vector3 priorityStart,
        Vector3 priorityTarget,
        bool preserveEdgeCache = false)
    {
        int previousPointCount = preserveEdgeCache ? validatedPointCount : 0;
        points = surfacePoints;
        config = plannerConfig;
        priorityStartPosition = priorityStart;
        priorityTargetPosition = priorityTarget;
        collisionMask = HelperFunctions.GetMask(HelperFunctions.LayerType.AllPhysicalExceptCharacter);
        cellSize = GetCellSize();
        localCellSize = GetLocalCellSize();
        candidates.Clear();
        if (!preserveEdgeCache)
        {
            edges.Clear();
            candidateKeys.Clear();
            validatedEdgeKeys.Clear();
            validatedPointCount = 0;
        }

        spatialCells.Clear();
        localCells.Clear();
        localNeighborOffsets.Clear();
        extendedNeighborOffsets.Clear();
        candidateCellPoints = null;
        candidateBuildIndex = previousPointCount;
        candidateOffsetIndex = 0;
        candidateCellPointIndex = 0;
        preservedPointCount = previousPointCount;
        allowPreviousPointCandidates = preserveEdgeCache;
        candidateBuildComplete = candidateBuildIndex >= points.Count;
        HitCandidateLimit = false;
        GeneratedCandidateCount = 0;
        ProcessedCandidateCount = 0;
        ProcessedCandidatePairCount = 0;

        BuildSpatialCells();
        BuildLocalCells();
        BuildNeighborOffsets();
        BuildStandJumpEligibility();
    }

    internal bool ProcessFrame()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        int buildBudget = config.MaxEdgeCandidateChecksPerFrame;
        int processedThisFrame = 0;
        while (buildBudget > 0 && !candidateBuildComplete && HasFrameBudget(stopwatch, processedThisFrame))
        {
            if (!TryBuildNextCandidate())
            {
                break;
            }

            buildBudget--;
            processedThisFrame++;
        }

        int validationBudget = config.MaxEdgeValidationsPerFrame;
        while (validationBudget > 0 && candidates.Count > 0 && HasFrameBudget(stopwatch, processedThisFrame))
        {
            EdgeCandidate candidate = candidates.Dequeue();
            ProcessedCandidateCount++;
            validationBudget--;
            processedThisFrame++;

            if (Validate(candidate))
            {
                RouteEdge edge = new(
                    candidate.From,
                    candidate.To,
                    candidate.Kind,
                    candidate.Distance,
                    candidate.StaminaCost);
                edges.Add(edge);
                validatedEdgeKeys.Add(new EdgeKey(edge.From, edge.To));
            }
        }

        bool completed = candidateBuildComplete && candidates.Count == 0;
        if (completed)
        {
            validatedPointCount = points.Count;
        }

        return completed;
    }

    internal bool HasValidatedEdge(int from, int to)
    {
        return validatedEdgeKeys.Contains(new EdgeKey(from, to));
    }

    private void BuildSpatialCells()
    {
        for (int index = 0; index < points.Count; index++)
        {
            CellKey key = CellKey.From(points[index].Position, cellSize);
            if (!spatialCells.TryGetValue(key, out List<int> cellPoints))
            {
                cellPoints = [];
                spatialCells[key] = cellPoints;
            }

            cellPoints.Add(index);
        }
    }

    private void BuildLocalCells()
    {
        for (int index = 0; index < points.Count; index++)
        {
            CellKey key = CellKey.From(points[index].Position, localCellSize);
            if (!localCells.TryGetValue(key, out List<int> cellPoints))
            {
                cellPoints = [];
                localCells[key] = cellPoints;
            }

            cellPoints.Add(index);
        }
    }

    private void BuildNeighborOffsets()
    {
        BuildNeighborOffsets(localNeighborOffsets, GetStandWalkReachDistance());
        BuildNeighborOffsets(extendedNeighborOffsets, GetMaxReachDistance());
    }

    private void BuildNeighborOffsets(List<CellKey> offsets, float reachDistance)
    {
        offsets.Clear();
        int neighborRange = Mathf.CeilToInt(reachDistance / cellSize);
        for (int x = -neighborRange; x <= neighborRange; x++)
        {
            for (int y = -neighborRange; y <= neighborRange; y++)
            {
                for (int z = -neighborRange; z <= neighborRange; z++)
                {
                    offsets.Add(new CellKey(x, y, z));
                }
            }
        }
    }

    private void BuildStandJumpEligibility()
    {
        standJumpEligible = new bool[points.Count];
        float walkReachSqr = GetStandWalkReachDistance() * GetStandWalkReachDistance();
        for (int index = 0; index < points.Count; index++)
        {
            SurfacePoint point = points[index];
            if (point.Kind != SurfaceKind.Standable)
            {
                standJumpEligible[index] = true;
                continue;
            }

            CellKey origin = CellKey.From(point.Position, localCellSize);
            int standableNeighbors = 0;
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        CellKey key = new(origin.X + x, origin.Y + y, origin.Z + z);
                        if (!localCells.TryGetValue(key, out List<int> cellPoints))
                        {
                            continue;
                        }

                        for (int pointIndex = 0; pointIndex < cellPoints.Count; pointIndex++)
                        {
                            int otherIndex = cellPoints[pointIndex];
                            if (otherIndex == index || points[otherIndex].Kind != SurfaceKind.Standable)
                            {
                                continue;
                            }

                            Vector3 delta = points[otherIndex].Position - point.Position;
                            float horizontalSqr = delta.x * delta.x + delta.z * delta.z;
                            if (horizontalSqr > walkReachSqr || !CanStep(delta.y))
                            {
                                continue;
                            }

                            standableNeighbors++;
                            if (standableNeighbors >= DenseStandableNeighborThreshold)
                            {
                                break;
                            }
                        }

                        if (standableNeighbors >= DenseStandableNeighborThreshold)
                        {
                            break;
                        }
                    }

                    if (standableNeighbors >= DenseStandableNeighborThreshold)
                    {
                        break;
                    }
                }

                if (standableNeighbors >= DenseStandableNeighborThreshold)
                {
                    break;
                }
            }
            standJumpEligible[index] = standableNeighbors < DenseStandableNeighborThreshold;
        }
    }

    private bool TryBuildNextCandidate()
    {
        while (candidateBuildIndex < points.Count)
        {
            SurfacePoint point = points[candidateBuildIndex];
            bool useLocalGrid = UseLocalCandidateGrid(point, candidateBuildIndex);
            Dictionary<CellKey, List<int>> candidateCells = useLocalGrid ? localCells : spatialCells;
            float candidateCellSize = useLocalGrid ? localCellSize : cellSize;
            IReadOnlyList<CellKey> neighborOffsets = GetNeighborOffsets(point, candidateBuildIndex);
            CellKey origin = CellKey.From(point.Position, candidateCellSize);
            while (candidateOffsetIndex < neighborOffsets.Count)
            {
                if (candidateCellPoints == null)
                {
                    CellKey offset = neighborOffsets[candidateOffsetIndex];
                    CellKey key = new(origin.X + offset.X, origin.Y + offset.Y, origin.Z + offset.Z);
                    if (!candidateCells.TryGetValue(key, out candidateCellPoints))
                    {
                        candidateOffsetIndex++;
                        continue;
                    }

                    candidateCellPointIndex = 0;
                }

                while (candidateCellPointIndex < candidateCellPoints.Count)
                {
                    int otherIndex = candidateCellPoints[candidateCellPointIndex];
                    candidateCellPointIndex++;
                    if (otherIndex == candidateBuildIndex)
                    {
                        continue;
                    }

                    if (otherIndex < candidateBuildIndex
                        && (!allowPreviousPointCandidates || otherIndex >= preservedPointCount))
                    {
                        continue;
                    }

                    ProcessedCandidatePairCount++;
                    TryAddCandidate(candidateBuildIndex, otherIndex);
                    TryAddCandidate(otherIndex, candidateBuildIndex);
                    return true;
                }

                candidateCellPoints = null;
                candidateOffsetIndex++;
            }

            candidateOffsetIndex = 0;
            candidateBuildIndex++;
            return true;
        }

        candidateBuildComplete = true;
        return false;
    }

    private void TryAddCandidate(int from, int to)
    {
        if (GeneratedCandidateCount >= config.MaxEdgeCandidatesPerAttempt)
        {
            HitCandidateLimit = true;
            candidateBuildComplete = true;
            return;
        }

        SurfacePoint a = points[from];
        SurfacePoint b = points[to];
        if (!CanConsiderPair(a, b, from, to))
        {
            return;
        }

        float distance = Vector3.Distance(a.Position, b.Position);
        MoveKind? kind = DetermineMoveKind(a, b, distance);
        if (kind == null)
        {
            return;
        }

        EdgeKey key = new(from, to);
        if (!candidateKeys.Add(key))
        {
            return;
        }

        EdgeCandidate candidate = new(
            from,
            to,
            kind.Value,
            distance,
            GetStaminaCost(a, b, kind.Value, distance));
        candidates.Enqueue(candidate, GetCandidateValidationPriority(a, b, kind.Value, distance));
        GeneratedCandidateCount++;
        if (GeneratedCandidateCount >= config.MaxEdgeCandidatesPerAttempt)
        {
            HitCandidateLimit = true;
            candidateBuildComplete = true;
        }
    }

    private float GetCandidateValidationPriority(SurfacePoint from, SurfacePoint to, MoveKind kind, float distance)
    {
        float startDistance = Mathf.Min(
            Vector3.Distance(from.Position, priorityStartPosition),
            Vector3.Distance(to.Position, priorityStartPosition));
        float targetProgress = Vector3.Distance(from.Position, priorityTargetPosition)
            - Vector3.Distance(to.Position, priorityTargetPosition);
        float kindPenalty = kind switch
        {
            MoveKind.StandWalk => 0f,
            MoveKind.SurfaceClimb => 0.35f,
            MoveKind.StandJump => 4f,
            MoveKind.AirTransfer => 5f,
            _ => 2f,
        };

        return startDistance * 0.75f
            + distance * 2f
            + kindPenalty
            - Mathf.Max(0f, targetProgress) * 0.25f;
    }

    private IReadOnlyList<CellKey> GetNeighborOffsets(SurfacePoint point, int pointIndex)
    {
        return point.Kind == SurfaceKind.Standable
            && pointIndex >= 0
            && pointIndex < standJumpEligible.Length
            && !standJumpEligible[pointIndex]
            ? localNeighborOffsets
            : extendedNeighborOffsets;
    }

    private bool UseLocalCandidateGrid(SurfacePoint point, int pointIndex)
    {
        return point.Kind == SurfaceKind.Standable
            && pointIndex >= 0
            && pointIndex < standJumpEligible.Length
            && !standJumpEligible[pointIndex];
    }

    private bool CanConsiderPair(SurfacePoint a, SurfacePoint b, int from, int to)
    {
        if (a.Kind != SurfaceKind.Standable || b.Kind != SurfaceKind.Standable)
        {
            return true;
        }

        float horizontalDistance = GetHorizontalDistance(a.Position, b.Position);
        float verticalDelta = b.Position.y - a.Position.y;
        if (horizontalDistance <= GetStandWalkReachDistance() && CanStep(verticalDelta))
        {
            return true;
        }

        bool fromEligible = from >= 0 && from < standJumpEligible.Length && standJumpEligible[from];
        bool toEligible = to >= 0 && to < standJumpEligible.Length && standJumpEligible[to];
        return fromEligible || toEligible;
    }

    private MoveKind? DetermineMoveKind(SurfacePoint a, SurfacePoint b, float distance)
    {
        float horizontalDistance = GetHorizontalDistance(a.Position, b.Position);
        float verticalDelta = b.Position.y - a.Position.y;
        if (a.Kind == SurfaceKind.Standable && b.Kind == SurfaceKind.Standable)
        {
            if (horizontalDistance <= GetStandWalkReachDistance() && CanStep(verticalDelta))
            {
                return MoveKind.StandWalk;
            }

            if (IsLikelySameStandableSurface(a, b, verticalDelta))
            {
                return null;
            }

            return CanStandJump(horizontalDistance, verticalDelta)
                ? MoveKind.StandJump
                : null;
        }

        if (distance <= config.SurfaceNeighborDistance
            && CanSurfaceClimbBetween(a, b, distance))
        {
            return MoveKind.SurfaceClimb;
        }

        if (horizontalDistance <= config.MaxAirTransferDistance
            && Mathf.Abs(verticalDelta) <= config.MaxAirTransferVerticalDelta
            && a.Kind == SurfaceKind.Climbable
            && b.Kind == SurfaceKind.Climbable)
        {
            return MoveKind.AirTransfer;
        }

        return null;
    }

    private bool CanStep(float verticalDelta)
    {
        return verticalDelta <= config.MaxWalkStepUpHeight
            && -verticalDelta <= config.MaxWalkDropHeight;
    }

    private bool CanStandJump(float horizontalDistance, float verticalDelta)
    {
        return horizontalDistance <= config.MaxStandJumpDistance
            && verticalDelta <= config.MaxStandJumpUpHeight
            && -verticalDelta <= config.MaxStandJumpDropHeight;
    }

    private bool CanSurfaceClimbBetween(SurfacePoint a, SurfacePoint b, float distance)
    {
        if (a.Kind != SurfaceKind.Climbable && b.Kind != SurfaceKind.Climbable)
        {
            return false;
        }

        if (distance > config.SurfaceNeighborDistance)
        {
            return false;
        }

        if (a.Kind == SurfaceKind.Standable && b.Kind == SurfaceKind.Standable)
        {
            return false;
        }

        if (a.Kind == SurfaceKind.Climbable && b.Kind == SurfaceKind.Climbable)
        {
            return a.ColliderId == 0
                || b.ColliderId == 0
                || a.ColliderId == b.ColliderId
                || Vector3.Angle(a.Normal, b.Normal) <= MaxClimbNormalAngleDelta;
        }

        SurfacePoint climbable = a.Kind == SurfaceKind.Climbable ? a : b;
        SurfacePoint other = a.Kind == SurfaceKind.Climbable ? b : a;
        if (other.Kind != SurfaceKind.Standable)
        {
            return true;
        }

        return GetHorizontalDistance(climbable.Position, other.Position) <= GetStandWalkReachDistance()
            && Mathf.Abs(climbable.Position.y - other.Position.y) <= config.SurfaceNeighborDistance * 1.25f;
    }

    private bool IsLikelySameStandableSurface(SurfacePoint a, SurfacePoint b, float verticalDelta)
    {
        if (a.ColliderId == 0 || a.ColliderId != b.ColliderId)
        {
            return false;
        }

        return Mathf.Abs(verticalDelta) <= config.MaxWalkStepUpHeight
            && Vector3.Angle(a.Normal, b.Normal) <= SameSurfaceJumpNormalTolerance;
    }

    private float GetStaminaCost(SurfacePoint a, SurfacePoint b, MoveKind kind, float distance)
    {
        return kind switch
        {
            MoveKind.StandWalk => 0f,
            MoveKind.SurfaceClimb => GetSurfaceClimbStaminaCost(a, b, distance),
            MoveKind.StandJump => GetStandJumpStaminaCost(GetHorizontalDistance(a.Position, b.Position)),
            MoveKind.AirTransfer => config.ClimbJumpStaminaCost,
            _ => GetSurfaceClimbStaminaCost(a, b, distance),
        };
    }

    private float GetSurfaceClimbStaminaCost(SurfacePoint a, SurfacePoint b, float distance)
    {
        Vector3 normal = (a.Normal + b.Normal).normalized;
        if (normal.sqrMagnitude < 0.001f)
        {
            normal = a.Kind == SurfaceKind.Climbable ? a.Normal : b.Normal;
        }

        float angleUsage = GetClimbAngleUsage(normal);
        float climbUsage = Mathf.Clamp(
            config.ClimbStaminaUsagePerSecond,
            config.MinClimbStaminaUsagePerSecond * config.ClimbStaminaMinimumMultiplier,
            config.ClimbStaminaUsagePerSecond);
        float travelTime = distance / Mathf.Max(0.1f, config.ClimbSpeed);
        return climbUsage * angleUsage * travelTime;
    }

    private float GetStandJumpStaminaCost(float distance)
    {
        if (distance <= config.NormalStandJumpDistance + 0.001f)
        {
            return config.JumpStaminaCost;
        }

        const float SprintRunupSeconds = 0.35f;
        return config.SprintJumpStaminaCost + config.SprintStaminaUsagePerSecond * SprintRunupSeconds;
    }

    private static float GetClimbAngleUsage(Vector3 normal)
    {
        float angle = Vector3.Angle(Vector3.up, normal);
        float t = Mathf.InverseLerp(40f, 60f, angle);
        return Mathf.Lerp(0.2f, 1f, t);
    }

    private bool Validate(EdgeCandidate candidate)
    {
        SurfacePoint a = points[candidate.From];
        SurfacePoint b = points[candidate.To];
        if (candidate.Kind == MoveKind.StandJump || candidate.Kind == MoveKind.AirTransfer)
        {
            if (!HasClearAirChord(a.Position + Vector3.up * JumpLift, b.Position + Vector3.up * JumpLift, CastRadius))
            {
                return false;
            }
        }
        else if (candidate.Kind == MoveKind.SurfaceClimb)
        {
            if (!HasClimbSurfaceContinuity(a, b))
            {
                return false;
            }
        }
        else if (!HasSurfaceSupportAlongEdge(a, b, candidate.Kind))
        {
            return false;
        }

        if (candidate.Kind == MoveKind.StandWalk && !HasStandWalkClearance(a, b))
        {
            return false;
        }

        if (candidate.Kind == MoveKind.StandJump && HasContinuousStandableSupport(a, b))
        {
            return false;
        }

        return true;
    }

    private bool HasClimbSurfaceContinuity(SurfacePoint a, SurfacePoint b)
    {
        if (!HasCompatibleClimbNormals(a, b))
        {
            return false;
        }

        Vector3 expectedNormal = GetExpectedClimbNormal(a, b);
        if (expectedNormal.sqrMagnitude < 0.001f)
        {
            return false;
        }

        if (!HasClearClimbOutsideChord(a, b, expectedNormal))
        {
            return false;
        }

        int sampleCount = Mathf.Clamp(
            Mathf.CeilToInt(Vector3.Distance(a.Position, b.Position) / Mathf.Max(0.25f, config.SurfaceNeighborDistance * 0.5f)),
            1,
            MaxSurfaceSupportSamples);
        for (int sample = 1; sample <= sampleCount; sample++)
        {
            float t = sample / (float)(sampleCount + 1);
            Vector3 position = Vector3.Lerp(a.Position, b.Position, t);
            if (!HasClimbSurfaceSupportNear(position, expectedNormal, a.ColliderId, b.ColliderId))
            {
                return false;
            }
        }

        return true;
    }

    private bool HasCompatibleClimbNormals(SurfacePoint a, SurfacePoint b)
    {
        if (a.Kind != SurfaceKind.Climbable || b.Kind != SurfaceKind.Climbable)
        {
            return true;
        }

        return Vector3.Angle(a.Normal, b.Normal) <= MaxClimbNormalAngleDelta;
    }

    private static Vector3 GetExpectedClimbNormal(SurfacePoint a, SurfacePoint b)
    {
        Vector3 normal = Vector3.zero;
        if (a.Kind == SurfaceKind.Climbable)
        {
            normal += a.Normal;
        }

        if (b.Kind == SurfaceKind.Climbable)
        {
            normal += b.Normal;
        }

        if (normal.sqrMagnitude < 0.001f)
        {
            normal = (a.Normal + b.Normal) * 0.5f;
        }

        return normal.normalized;
    }

    private bool HasClearClimbOutsideChord(SurfacePoint a, SurfacePoint b, Vector3 expectedNormal)
    {
        Vector3 from = a.Position + expectedNormal * ClimbSurfaceOutwardOffset;
        Vector3 to = b.Position + expectedNormal * ClimbSurfaceOutwardOffset;
        Vector3 delta = to - from;
        float distance = delta.magnitude;
        if (distance <= 0.001f)
        {
            return false;
        }

        int hitCount = Physics.SphereCastNonAlloc(
            from,
            CastRadius,
            delta / distance,
            castHits,
            distance,
            collisionMask,
            QueryTriggerInteraction.Ignore);

        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = castHits[index];
            Collider collider = hit.collider;
            if (collider == null)
            {
                continue;
            }

            if (hit.distance <= EndpointTolerance || hit.distance >= distance - EndpointTolerance)
            {
                continue;
            }

            int colliderId = collider.GetInstanceID();
            if (colliderId == a.ColliderId || colliderId == b.ColliderId)
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private bool HasClimbSurfaceSupportNear(Vector3 position, Vector3 expectedNormal, int fromColliderId, int toColliderId)
    {
        Vector3 origin = position + expectedNormal * ClimbSurfaceOutwardOffset;
        int hitCount = Physics.SphereCastNonAlloc(
            origin,
            CastRadius,
            -expectedNormal,
            castHits,
            ClimbSurfaceProbeDepth,
            collisionMask,
            QueryTriggerInteraction.Ignore);

        float bestDistance = float.MaxValue;
        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = castHits[index];
            Collider collider = hit.collider;
            if (collider == null)
            {
                continue;
            }

            CollisionModifier modifier = collider.GetComponent<CollisionModifier>();
            if (modifier != null && !modifier.standable)
            {
                continue;
            }

            SurfaceKind hitKind = ClassifySurface(hit.normal);
            if (hitKind == SurfaceKind.Blocked)
            {
                continue;
            }

            int colliderId = collider.GetInstanceID();
            if (fromColliderId != 0
                && toColliderId != 0
                && colliderId != fromColliderId
                && colliderId != toColliderId)
            {
                continue;
            }

            if (hitKind == SurfaceKind.Climbable
                && Vector3.Dot(hit.normal.normalized, expectedNormal) < MinClimbNormalFacingDot)
            {
                continue;
            }

            float surfaceDistance = Vector3.Distance(position, hit.point);
            if (surfaceDistance >= bestDistance)
            {
                continue;
            }

            bestDistance = surfaceDistance;
        }

        return bestDistance <= MaxSurfaceProbeDistance;
    }

    private bool HasClearAirChord(Vector3 from, Vector3 to, float radius)
    {
        Vector3 delta = to - from;
        float distance = delta.magnitude;
        if (distance <= 0.001f)
        {
            return false;
        }

        int hitCount = Physics.SphereCastNonAlloc(
            from,
            radius,
            delta / distance,
            castHits,
            distance,
            collisionMask,
            QueryTriggerInteraction.Ignore);

        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = castHits[index];
            Collider collider = hit.collider;
            if (collider == null)
            {
                continue;
            }

            if (hit.distance > EndpointTolerance && hit.distance < distance - EndpointTolerance)
            {
                return false;
            }
        }

        return true;
    }

    private bool HasStandWalkClearance(SurfacePoint a, SurfacePoint b)
    {
        Vector3 from = a.Position + Vector3.up * StandWalkClearanceLift;
        Vector3 to = b.Position + Vector3.up * StandWalkClearanceLift;
        Vector3 delta = to - from;
        float distance = delta.magnitude;
        if (distance <= 0.001f)
        {
            return false;
        }

        int hitCount = Physics.SphereCastNonAlloc(
            from,
            StandWalkClearanceRadius,
            delta / distance,
            castHits,
            distance,
            collisionMask,
            QueryTriggerInteraction.Ignore);

        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = castHits[index];
            Collider collider = hit.collider;
            if (collider == null)
            {
                continue;
            }

            if (hit.distance <= EndpointTolerance || hit.distance >= distance - EndpointTolerance)
            {
                continue;
            }

            CollisionModifier modifier = collider.GetComponent<CollisionModifier>();
            if (modifier != null && !modifier.standable)
            {
                return false;
            }

            return false;
        }

        return true;
    }

    private bool HasSurfaceSupportAlongEdge(SurfacePoint a, SurfacePoint b, MoveKind kind)
    {
        int sampleCount = Mathf.Clamp(
            Mathf.CeilToInt(Vector3.Distance(a.Position, b.Position) / Mathf.Max(0.25f, config.SurfaceNeighborDistance * 0.5f)),
            1,
            MaxSurfaceSupportSamples);
        for (int sample = 1; sample <= sampleCount; sample++)
        {
            float t = sample / (float)(sampleCount + 1);
            Vector3 position = Vector3.Lerp(a.Position, b.Position, t);
            Vector3 normal = Vector3.Lerp(a.Normal, b.Normal, t).normalized;
            if (normal.sqrMagnitude < 0.001f)
            {
                normal = Vector3.up;
            }

            if (!HasSurfaceSupportNear(position, normal, kind))
            {
                return false;
            }
        }

        return true;
    }

    private bool HasSurfaceSupportNear(Vector3 position, Vector3 expectedNormal, MoveKind kind)
    {
        Vector3 origin = position + expectedNormal * SurfaceProbeLift;
        int hitCount = Physics.SphereCastNonAlloc(
            origin,
            CastRadius,
            -expectedNormal,
            castHits,
            SurfaceProbeDepth,
            collisionMask,
            QueryTriggerInteraction.Ignore);

        float bestDistance = float.MaxValue;
        SurfaceKind bestKind = SurfaceKind.Blocked;
        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = castHits[index];
            Collider collider = hit.collider;
            if (collider == null)
            {
                continue;
            }

            CollisionModifier modifier = collider.GetComponent<CollisionModifier>();
            if (modifier != null && !modifier.standable)
            {
                continue;
            }

            SurfaceKind hitKind = ClassifySurface(hit.normal);
            if (hitKind == SurfaceKind.Blocked)
            {
                continue;
            }

            float surfaceDistance = Vector3.Distance(position, hit.point);
            if (surfaceDistance >= bestDistance)
            {
                continue;
            }

            bestDistance = surfaceDistance;
            bestKind = hitKind;
        }

        if (bestDistance > MaxSurfaceProbeDistance)
        {
            return false;
        }

        return kind != MoveKind.StandWalk || bestKind == SurfaceKind.Standable;
    }

    private float GetCellSize()
    {
        return Mathf.Max(0.5f, GetMaxReachDistance());
    }

    private float GetLocalCellSize()
    {
        return Mathf.Max(0.25f, GetStandWalkReachDistance());
    }

    private float GetMaxReachDistance()
    {
        return Mathf.Max(GetStandWalkReachDistance(), Mathf.Max(config.MaxStandJumpDistance, config.MaxAirTransferDistance));
    }

    private float GetStandWalkReachDistance()
    {
        float sampleReach = config.HorizontalSampleSpacing * StandWalkReachSampleMultiplier;
        return Mathf.Clamp(
            Mathf.Max(config.SurfaceNeighborDistance, sampleReach),
            config.SurfaceNeighborDistance,
            MaxDerivedStandWalkReach);
    }

    private bool HasContinuousStandableSupport(SurfacePoint a, SurfacePoint b)
    {
        float horizontalDistance = GetHorizontalDistance(a.Position, b.Position);
        int sampleCount = Mathf.Clamp(
            Mathf.CeilToInt(horizontalDistance / Mathf.Max(0.25f, config.SurfaceNeighborDistance)),
            1,
            MaxJumpSupportSamples);
        float supportMinY = Mathf.Min(a.Position.y, b.Position.y) - config.MaxWalkDropHeight;
        float supportMaxY = Mathf.Max(a.Position.y, b.Position.y) + config.MaxWalkStepUpHeight;
        float rayTopY = supportMaxY + 1f;
        float rayDistance = Mathf.Max(1f, rayTopY - supportMinY + 0.25f);

        for (int sample = 1; sample <= sampleCount; sample++)
        {
            float t = sample / (float)(sampleCount + 1);
            Vector3 position = Vector3.Lerp(a.Position, b.Position, t);
            Vector3 origin = new(position.x, rayTopY, position.z);
            if (!HasStandableSupportAt(origin, rayDistance, supportMinY, supportMaxY))
            {
                return false;
            }
        }

        return true;
    }

    private bool HasStandableSupportAt(Vector3 origin, float rayDistance, float minY, float maxY)
    {
        int hitCount = Physics.RaycastNonAlloc(
            origin,
            Vector3.down,
            castHits,
            rayDistance,
            collisionMask,
            QueryTriggerInteraction.Ignore);

        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = castHits[index];
            Collider collider = hit.collider;
            if (collider == null || hit.point.y < minY || hit.point.y > maxY)
            {
                continue;
            }

            CollisionModifier modifier = collider.GetComponent<CollisionModifier>();
            if (modifier != null && !modifier.standable)
            {
                continue;
            }

            if (Vector3.Angle(Vector3.up, hit.normal) <= config.StandableNormalAngle)
            {
                return true;
            }
        }

        return false;
    }

    private SurfaceKind ClassifySurface(Vector3 normal)
    {
        float angle = Vector3.Angle(Vector3.up, normal);
        if (angle <= config.StandableNormalAngle)
        {
            return SurfaceKind.Standable;
        }

        return angle <= config.MaxClimbableNormalAngle
            ? SurfaceKind.Climbable
            : SurfaceKind.Blocked;
    }

    private static float GetHorizontalDistance(Vector3 a, Vector3 b)
    {
        float x = a.x - b.x;
        float z = a.z - b.z;
        return Mathf.Sqrt(x * x + z * z);
    }

    private bool HasFrameBudget(Stopwatch stopwatch, int processedThisFrame)
    {
        return processedThisFrame == 0 || stopwatch.Elapsed.TotalMilliseconds < config.MaxMainThreadMillisecondsPerFrame;
    }

    private readonly struct EdgeCandidate
    {
        internal EdgeCandidate(int from, int to, MoveKind kind, float distance, float staminaCost)
        {
            From = from;
            To = to;
            Kind = kind;
            Distance = distance;
            StaminaCost = staminaCost;
        }

        internal int From { get; }

        internal int To { get; }

        internal MoveKind Kind { get; }

        internal float Distance { get; }

        internal float StaminaCost { get; }
    }

    private readonly struct EdgeKey : System.IEquatable<EdgeKey>
    {
        internal EdgeKey(int left, int right)
        {
            A = left;
            B = right;
        }

        private int A { get; }

        private int B { get; }

        public bool Equals(EdgeKey other)
        {
            return A == other.A && B == other.B;
        }

        public override bool Equals(object? obj)
        {
            return obj is EdgeKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (A * 397) ^ B;
            }
        }
    }

    private readonly struct CellKey
    {
        internal CellKey(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        internal int X { get; }

        internal int Y { get; }

        internal int Z { get; }

        internal static CellKey From(Vector3 position, float cellSize)
        {
            return new CellKey(
                Mathf.FloorToInt(position.x / cellSize),
                Mathf.FloorToInt(position.y / cellSize),
                Mathf.FloorToInt(position.z / cellSize));
        }
    }
}
