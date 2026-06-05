using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace PeakRoutePlanner.Planning;

internal sealed class SurfaceSampler
{
    private const float PointQuantization = 10f;
    private const float SameRayLayerHeightTolerance = 0.35f;
    private const float StandWallProbeReach = 1.35f;
    private const float ClimbSurfaceProbeOffset = 0.35f;
    private const float ClimbSurfaceProbeDistance = 0.85f;
    private const float MinimumWallFacingDot = 0.15f;
    private const float SurfaceStandClearanceHeight = 1.55f;
    private const float SurfaceStandClearanceRadius = 0.22f;
    private const float SurfaceStandClearanceBottom = 0.18f;
    private const float OutermostHitDistanceTolerance = 0.05f;
    private const float LocalLayerUpPadding = 0.35f;
    private const float LocalLayerDownPadding = 0.75f;
    private const float MinLocalLayerUpExtent = 1.8f;
    private const float MaxLocalLayerUpExtent = 3f;
    private const float MinLocalLayerDownExtent = 2.25f;
    private const float MaxLocalLayerDownExtent = 6f;
    private const float NeighborSurfaceProbeOffset = 0.35f;
    private const float GapProbeUpPadding = 0.5f;
    private const float GapProbeDownPadding = 0.75f;
    private const float FlatStandableRegionCellSize = 2f;
    private const float FlatStandableNormalAngle = 12f;
    private const int MaxFlatStandablePointsPerRegionCell = 3;
    private const int MaxPerColliderRaycastColliders = 96;
    private const int MaxGapProbesPerWindow = 96;
    private static readonly float[] GapProbeDistanceMultipliers = [0.55f, 0.8f, 1f];
    private static readonly RaycastHit[] TerrainHitBuffer = new RaycastHit[128];
    private static readonly RaycastHit[] ClearanceHitBuffer = new RaycastHit[16];
    private static readonly Collider[] ClearanceColliderBuffer = new Collider[16];
    private static readonly Collider[] WindowColliderBuffer = new Collider[256];
    private static readonly float[] StandWallProbeHeightOffsets = [0.6f, 1.2f];
    private static readonly Vector2[] HorizontalProbeDirections =
    [
        new(1f, 0f),
        new(-1f, 0f),
        new(0f, 1f),
        new(0f, -1f),
        new(0.7071f, 0.7071f),
        new(0.7071f, -0.7071f),
        new(-0.7071f, 0.7071f),
        new(-0.7071f, -0.7071f),
    ];

    private readonly HashSet<QueryKey> queuedRayKeys = [];
    private readonly HashSet<ExpandedSeedKey> expandedSeedKeys = [];
    private readonly HashSet<LayeredSeedKey> queuedSeedKeys = [];
    private readonly Queue<SurfaceQuery> pendingRayOrigins = new();
    private readonly PriorityQueue<FrontierSeed> frontierSeeds = new();
    private readonly List<Vector2>[] queuedWindowCentersBySide = [[], []];
    private readonly List<Vector2>[] expandedWindowCentersBySide = [[], []];
    private readonly Dictionary<PointKey, int> pointIdsByKey = [];
    private readonly Dictionary<FlatRegionKey, int> flatStandableCountsByRegion = [];
    private readonly HashSet<FlatRegionKey> queuedStandableWallProbeCells = [];
    private readonly List<SurfacePoint> points = [];
    private readonly List<HitCandidate> hitCandidates = [];
    private readonly List<Collider> activeWindowColliders = [];
    private readonly HashSet<int> activeWindowColliderIds = [];

    private PlannerConfig config = null!;
    private float corridorSpacing;
    private Vector3 startPosition;
    private Vector3 targetPosition;
    private GuideProjectionMap guideProjection = null!;
    private float maxSeedDistanceFromAnchor;
    private float maxGuideLateralDistance;
    private float samplingWindowRadius;
    private float samplingWindowVerticalHalfExtent;
    private float samplingWindowVerticalExtent;
    private float localLayerSearchUpExtent;
    private float localLayerSearchDownExtent;
    private float localLayerVerticalExtent;
    private Vector2 activeWindowCenter;
    private float activeWindowSurfaceY;
    private int expansionBucket;
    private int gridSamplesPerSeed;
    private int activeWindowGapProbeCount;
    private bool rayGenerationComplete;
    private bool activeWindowUsesGlobalRaycast;
    private int terrainMask;
    private int collisionMask;

    internal IReadOnlyList<SurfacePoint> Points => points;

    internal int StartIndex { get; private set; }

    internal int TargetIndex { get; private set; }

    internal int PendingRayCount => rayGenerationComplete
        ? 0
        : pendingRayOrigins.Count + frontierSeeds.Count * Mathf.Max(1, gridSamplesPerSeed);

    internal int ProcessedRayCount { get; private set; }

    internal int BroadphaseWindowCount { get; private set; }

    internal int GlobalRaycastFallbackCount { get; private set; }

    internal int GapProbeCount { get; private set; }

    internal int GapLandingPointCount { get; private set; }

    internal bool HitPointLimit { get; private set; }

    internal int CachedPointCountAtAttemptStart { get; private set; }

    internal bool HasActiveSeedPreview { get; private set; }

    internal Vector3 ActiveSeedPreviewPosition { get; private set; }

    internal Vector3 ActiveSampleWindowCenter { get; private set; }

    internal Vector3 ActiveSampleWindowSize { get; private set; }

    internal void Begin(
        Vector3 start,
        Vector3 target,
        IReadOnlyList<Vector3> guidePath,
        float corridorRadius,
        PlannerConfig plannerConfig,
        bool preserveSampleCache,
        bool includeTargetFrontier)
    {
        config = plannerConfig;
        terrainMask = HelperFunctions.GetMask(HelperFunctions.LayerType.TerrainMap);
        collisionMask = HelperFunctions.GetMask(HelperFunctions.LayerType.AllPhysicalExceptCharacter);

        if (!preserveSampleCache)
        {
            queuedRayKeys.Clear();
            expandedSeedKeys.Clear();
            pointIdsByKey.Clear();
            flatStandableCountsByRegion.Clear();
            queuedStandableWallProbeCells.Clear();
            points.Clear();
            StartIndex = -1;
            TargetIndex = -1;
        }

        frontierSeeds.Clear();
        queuedSeedKeys.Clear();
        for (int index = 0; index < queuedWindowCentersBySide.Length; index++)
        {
            queuedWindowCentersBySide[index].Clear();
            expandedWindowCentersBySide[index].Clear();
        }

        while (pendingRayOrigins.Count > 0)
        {
            pendingRayOrigins.Dequeue();
        }

        activeWindowColliders.Clear();
        activeWindowColliderIds.Clear();
        activeWindowUsesGlobalRaycast = false;
        ProcessedRayCount = 0;
        BroadphaseWindowCount = 0;
        GlobalRaycastFallbackCount = 0;
        GapProbeCount = 0;
        GapLandingPointCount = 0;
        HitPointLimit = false;
        CachedPointCountAtAttemptStart = points.Count;
        HasActiveSeedPreview = false;
        ActiveSeedPreviewPosition = start;

        PrepareBidirectionalGrid(start, target, guidePath, corridorRadius);
        SetActiveSampleWindowPreview(new Vector2(start.x, start.z), start.y);
        if (!preserveSampleCache || StartIndex < 0 || StartIndex >= points.Count)
        {
            StartIndex = AddAnchoredPoint(start, SurfaceKind.Standable);
        }

        if (!preserveSampleCache || TargetIndex < 0 || TargetIndex >= points.Count)
        {
            TargetIndex = AddAnchoredPoint(target, SurfaceKind.Standable);
        }

        EnqueueSeed(StartIndex, FrontierSide.Start);
        if (includeTargetFrontier)
        {
            EnqueueSeed(TargetIndex, FrontierSide.Target);
        }

        if (preserveSampleCache)
        {
            RequeueCachedFrontierSeeds();
        }
    }

    internal bool ProcessFrame()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        int budget = config.MaxPhysicsQueriesPerFrame;
        int processedThisFrame = 0;
        while (budget > 0 && !rayGenerationComplete)
        {
            if (HitPointLimit)
            {
                rayGenerationComplete = true;
                break;
            }

            if (!TryGetNextSurfaceQuery(out SurfaceQuery query))
            {
                break;
            }

            ProcessedRayCount++;
            processedThisFrame++;
            budget--;

            int hitCount = RaycastQuery(query);

            AddFilteredHits(hitCount, query);

            if (processedThisFrame > 0 && stopwatch.Elapsed.TotalMilliseconds >= config.MaxMainThreadMillisecondsPerFrame)
            {
                break;
            }
        }

        return rayGenerationComplete;
    }

    private void PrepareBidirectionalGrid(Vector3 start, Vector3 target, IReadOnlyList<Vector3> guidePath, float expansionRadius)
    {
        startPosition = start;
        targetPosition = target;
        guideProjection = GuideProjectionMap.Build(guidePath);
        corridorSpacing = Mathf.Max(0.25f, config.HorizontalSampleSpacing);
        samplingWindowRadius = Mathf.Max(
            corridorSpacing,
            Mathf.Round(Mathf.Max(corridorSpacing, config.SurfaceSamplingWindowRadius) / corridorSpacing) * corridorSpacing);
        samplingWindowVerticalHalfExtent = samplingWindowRadius * 1.5f;
        samplingWindowVerticalExtent = samplingWindowRadius * 3f;
        localLayerSearchUpExtent = Mathf.Clamp(
            Mathf.Max(SurfaceStandClearanceHeight + SurfaceStandClearanceBottom, config.MaxWalkStepUpHeight + LocalLayerUpPadding),
            MinLocalLayerUpExtent,
            Mathf.Min(MaxLocalLayerUpExtent, samplingWindowVerticalHalfExtent));
        localLayerSearchDownExtent = Mathf.Clamp(
            Mathf.Max(config.MaxWalkDropHeight + LocalLayerDownPadding, config.MaxSampleVerticalLayerGap + LocalLayerDownPadding),
            MinLocalLayerDownExtent,
            Mathf.Min(MaxLocalLayerDownExtent, samplingWindowVerticalHalfExtent));
        localLayerVerticalExtent = localLayerSearchUpExtent + localLayerSearchDownExtent;
        maxSeedDistanceFromAnchor = Mathf.Max(samplingWindowRadius, expansionRadius * 2f);
        maxGuideLateralDistance = Mathf.Max(samplingWindowRadius, expansionRadius + samplingWindowRadius * 0.5f);
        expansionBucket = Mathf.RoundToInt(expansionRadius / Mathf.Max(0.25f, config.CorridorRadiusStep));

        rayGenerationComplete = false;
        gridSamplesPerSeed = 1;
    }

    private int AddAnchoredPoint(Vector3 position, SurfaceKind fallbackKind)
    {
        if (TryProjectAnchorToSurface(position, out Vector3 surfacePosition, out Vector3 normal, out int colliderId, out SurfaceKind kind))
        {
            return AddPoint(surfacePosition, normal, colliderId, kind, forceKeep: true);
        }

        return AddPoint(position, Vector3.up, 0, fallbackKind, forceKeep: true);
    }

    private bool TryProjectAnchorToSurface(
        Vector3 position,
        out Vector3 surfacePosition,
        out Vector3 normal,
        out int colliderId,
        out SurfaceKind kind)
    {
        Vector3 origin = new(position.x, position.y + localLayerSearchUpExtent, position.z);
        int hitCount = Physics.RaycastNonAlloc(
            origin,
            Vector3.down,
            TerrainHitBuffer,
            localLayerVerticalExtent,
            terrainMask,
            QueryTriggerInteraction.Ignore);

        surfacePosition = default;
        normal = Vector3.up;
        colliderId = 0;
        kind = SurfaceKind.Blocked;
        float bestScore = float.MaxValue;
        bool found = false;
        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = TerrainHitBuffer[index];
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

            HitCandidate candidate = new(hit.point, hit.normal, collider.GetInstanceID(), hitKind, hit.distance);
            if (IsOccludedStandableSurface(candidate))
            {
                continue;
            }

            float score = ScoreLayerCandidate(candidate, position.y);
            if (found && score >= bestScore)
            {
                continue;
            }

            found = true;
            bestScore = score;
            surfacePosition = candidate.Position;
            normal = candidate.Normal;
            colliderId = candidate.ColliderId;
            kind = hitKind;
        }

        return found;
    }

    private int RaycastQuery(SurfaceQuery query)
    {
        if (query.Kind == QueryKind.Vertical || query.Kind == QueryKind.GapLanding)
        {
            return Physics.RaycastNonAlloc(
                query.Origin,
                query.Direction,
                TerrainHitBuffer,
                query.Distance,
                terrainMask,
                QueryTriggerInteraction.Ignore);
        }

        if (activeWindowUsesGlobalRaycast || activeWindowColliders.Count == 0)
        {
            GlobalRaycastFallbackCount++;
            return Physics.RaycastNonAlloc(
                query.Origin,
                query.Direction,
                TerrainHitBuffer,
                query.Distance,
                terrainMask,
                QueryTriggerInteraction.Ignore);
        }

        int hitCount = 0;
        Ray ray = new(query.Origin, query.Direction);
        for (int index = 0; index < activeWindowColliders.Count; index++)
        {
            Collider collider = activeWindowColliders[index];
            if (collider == null || !collider.Raycast(ray, out RaycastHit hit, query.Distance))
            {
                continue;
            }

            if (hitCount >= TerrainHitBuffer.Length)
            {
                activeWindowUsesGlobalRaycast = true;
                GlobalRaycastFallbackCount++;
                return Physics.RaycastNonAlloc(
                    query.Origin,
                    query.Direction,
                    TerrainHitBuffer,
                    query.Distance,
                    terrainMask,
                    QueryTriggerInteraction.Ignore);
            }

            TerrainHitBuffer[hitCount++] = hit;
        }

        return hitCount;
    }

    private bool TryGetNextSurfaceQuery(out SurfaceQuery query)
    {
        while (pendingRayOrigins.Count == 0)
        {
            if (!TryQueueNextFrontierGrid())
            {
                rayGenerationComplete = true;
                query = default;
                return false;
            }
        }

        query = pendingRayOrigins.Dequeue();
        return true;
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

    private void AddFilteredHits(int hitCount, SurfaceQuery query)
    {
        hitCandidates.Clear();
        bool useVerticalLayerSelection = query.Kind == QueryKind.Vertical || query.Kind == QueryKind.GapLanding;
        bool requireOutermostHit = !useVerticalLayerSelection;
        float outermostDistance = requireOutermostHit ? GetOutermostHitDistance(hitCount) : float.MaxValue;
        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = TerrainHitBuffer[index];
            Collider collider = hit.collider;
            if (collider == null)
            {
                continue;
            }

            if (requireOutermostHit && !IsOutermostHit(hit.distance, outermostDistance))
            {
                continue;
            }

            CollisionModifier modifier = collider.GetComponent<CollisionModifier>();
            if (modifier != null && !modifier.standable)
            {
                continue;
            }

            SurfaceKind kind = ClassifySurface(hit.normal);
            if (kind == SurfaceKind.Blocked)
            {
                continue;
            }

            if (!useVerticalLayerSelection
                && Vector3.Dot(hit.normal.normalized, -query.Direction) < MinimumWallFacingDot)
            {
                continue;
            }

            hitCandidates.Add(new HitCandidate(hit.point, hit.normal, collider.GetInstanceID(), kind, hit.distance));
        }

        if (hitCandidates.Count == 0)
        {
            QueueGapProbesFromFailedBoundary(query);
            return;
        }

        if (!useVerticalLayerSelection)
        {
            AddBestDirectedHit(query);
            return;
        }

        if (!TrySelectVisibleSurfaceLayer(query.PreferredSurfaceY, out HitCandidate selectedLayer))
        {
            return;
        }

        float baseY = selectedLayer.Position.y;
        float sameLayerHeightTolerance = Mathf.Min(config.MaxSampleVerticalLayerGap, SameRayLayerHeightTolerance);
        for (int index = 0; index < hitCandidates.Count; index++)
        {
            HitCandidate candidate = hitCandidates[index];
            if (Mathf.Abs(candidate.Position.y - baseY) > sameLayerHeightTolerance)
            {
                continue;
            }

            if (IsOccludedStandableSurface(candidate))
            {
                continue;
            }

            if (!IsInsideGuideCorridor(candidate.Position))
            {
                continue;
            }

            int pointId = AddPoint(candidate.Position, candidate.Normal, candidate.ColliderId, candidate.Kind);
            if (pointId >= 0)
            {
                if (query.Kind == QueryKind.GapLanding)
                {
                    GapLandingPointCount++;
                }

                EnqueueSeed(pointId, GetPreferredSide(candidate.Position));
                QueueNeighborSurfaceProbes(pointId);
            }
        }
    }

    private void AddBestDirectedHit(SurfaceQuery query)
    {
        int bestIndex = -1;
        float bestScore = float.MaxValue;
        for (int index = 0; index < hitCandidates.Count; index++)
        {
            HitCandidate candidate = hitCandidates[index];
            if (!IsInsideGuideCorridor(candidate.Position))
            {
                continue;
            }

            if (IsOccludedStandableSurface(candidate))
            {
                continue;
            }

            float score = candidate.Distance
                + Mathf.Abs(candidate.Position.y - query.PreferredSurfaceY) * 0.2f
                + (candidate.Kind == SurfaceKind.Climbable ? 0f : 0.6f);
            if (score >= bestScore)
            {
                continue;
            }

            bestIndex = index;
            bestScore = score;
        }

        if (bestIndex < 0)
        {
            QueueGapProbesFromFailedBoundary(query);
            return;
        }

        HitCandidate best = hitCandidates[bestIndex];
        int pointId = AddPoint(best.Position, best.Normal, best.ColliderId, best.Kind);
        if (pointId >= 0)
        {
            EnqueueSeed(pointId, GetPreferredSide(best.Position));
            QueueNeighborSurfaceProbes(pointId);
        }
    }

    private void QueueNeighborSurfaceProbes(int pointId)
    {
        if (pointId < 0 || pointId >= points.Count)
        {
            return;
        }

        SurfacePoint point = points[pointId];
        if (point.Kind == SurfaceKind.Standable)
        {
            QueueStandableNeighborProbes(point);
            QueueStandableWallProbesThrottled(point);
            return;
        }

        if (point.Kind == SurfaceKind.Climbable)
        {
            QueueClimbableSurfaceProbes(point);
        }
    }

    private bool TrySelectVisibleSurfaceLayer(float preferredSurfaceY, out HitCandidate layer)
    {
        layer = default;
        bool found = false;
        float bestScore = float.MaxValue;
        for (int index = 0; index < hitCandidates.Count; index++)
        {
            HitCandidate candidate = hitCandidates[index];
            if (IsOccludedStandableSurface(candidate))
            {
                continue;
            }

            float score = ScoreLayerCandidate(candidate, preferredSurfaceY);
            if (found && score >= bestScore)
            {
                continue;
            }

            layer = candidate;
            bestScore = score;
            found = true;
        }

        return found;
    }

    private static float GetOutermostHitDistance(int hitCount)
    {
        float bestDistance = float.MaxValue;
        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = TerrainHitBuffer[index];
            if (hit.collider == null || hit.distance >= bestDistance)
            {
                continue;
            }

            bestDistance = hit.distance;
        }

        return bestDistance;
    }

    private static bool IsOutermostHit(float distance, float outermostDistance)
    {
        return outermostDistance < float.MaxValue
            && distance <= outermostDistance + OutermostHitDistanceTolerance;
    }

    private static float ScoreLayerCandidate(HitCandidate candidate, float preferredSurfaceY)
    {
        float kindPenalty = candidate.Kind == SurfaceKind.Standable ? 0f : 0.75f;
        return Mathf.Abs(candidate.Position.y - preferredSurfaceY) + kindPenalty;
    }

    private bool IsOccludedStandableSurface(HitCandidate candidate)
    {
        if (candidate.Kind != SurfaceKind.Standable)
        {
            return false;
        }

        if (!HasStandableClearance(candidate))
        {
            return true;
        }

        return false;
    }

    private bool HasStandableClearance(HitCandidate candidate)
    {
        if (!HasStandableHeadroom(candidate))
        {
            return false;
        }

        Vector3 bottom = candidate.Position + Vector3.up * SurfaceStandClearanceBottom;
        Vector3 top = candidate.Position + Vector3.up * SurfaceStandClearanceHeight;
        int overlapCount = Physics.OverlapCapsuleNonAlloc(
            bottom,
            top,
            SurfaceStandClearanceRadius,
            ClearanceColliderBuffer,
            collisionMask,
            QueryTriggerInteraction.Ignore);

        for (int index = 0; index < overlapCount; index++)
        {
            Collider collider = ClearanceColliderBuffer[index];
            if (collider == null || collider.GetInstanceID() == candidate.ColliderId)
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private bool HasStandableHeadroom(HitCandidate candidate)
    {
        Vector3 origin = candidate.Position + Vector3.up * (SurfaceStandClearanceBottom + SurfaceStandClearanceRadius + 0.05f);
        float distance = Mathf.Max(0.1f, SurfaceStandClearanceHeight - SurfaceStandClearanceBottom - SurfaceStandClearanceRadius - 0.05f);
        int hitCount = Physics.SphereCastNonAlloc(
            origin,
            SurfaceStandClearanceRadius,
            Vector3.up,
            ClearanceHitBuffer,
            distance,
            collisionMask,
            QueryTriggerInteraction.Ignore);

        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = ClearanceHitBuffer[index];
            if (hit.collider == null || hit.distance <= 0.02f)
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private int AddPoint(Vector3 position, Vector3 normal, int colliderId, SurfaceKind kind, bool forceKeep = false)
    {
        PointKey key = PointKey.From(position);
        if (pointIdsByKey.TryGetValue(key, out int existingId))
        {
            return existingId;
        }

        if (!forceKeep && ShouldSuppressFlatStandablePoint(position, normal, kind))
        {
            return -1;
        }

        if (points.Count >= config.MaxSurfacePointsPerAttempt)
        {
            HitPointLimit = true;
            return -1;
        }

        int id = points.Count;
        pointIdsByKey[key] = id;
        points.Add(new SurfacePoint(id, position, normal.normalized, colliderId, kind));
        return id;
    }

    private bool ShouldSuppressFlatStandablePoint(Vector3 position, Vector3 normal, SurfaceKind kind)
    {
        if (kind != SurfaceKind.Standable
            || Vector3.Angle(Vector3.up, normal) > FlatStandableNormalAngle)
        {
            return false;
        }

        FlatRegionKey regionKey = FlatRegionKey.From(position);
        flatStandableCountsByRegion.TryGetValue(regionKey, out int count);
        if (count >= MaxFlatStandablePointsPerRegionCell)
        {
            return true;
        }

        flatStandableCountsByRegion[regionKey] = count + 1;
        return false;
    }

    private void EnqueueSeed(int pointId, FrontierSide side)
    {
        if (pointId < 0 || pointId >= points.Count || points[pointId].Kind == SurfaceKind.Blocked)
        {
            return;
        }

        LayeredGridKey seedKey = ToSeedKey(points[pointId].Position);
        if (expandedSeedKeys.Contains(new ExpandedSeedKey(seedKey, side, expansionBucket)))
        {
            return;
        }

        int sideIndex = (int)side;
        int maxWindowsPerSide = Mathf.Max(1, config.MaxSamplingWindowsPerSide);
        if (expandedWindowCentersBySide[sideIndex].Count + queuedWindowCentersBySide[sideIndex].Count >= maxWindowsPerSide)
        {
            return;
        }

        Vector3 position = points[pointId].Position;
        Vector2 center = new(position.x, position.z);
        if (DoesWindowOverlapExisting(center, sideIndex))
        {
            return;
        }

        if (!IsInsideGuideCorridor(position))
        {
            return;
        }

        if (!queuedSeedKeys.Add(new LayeredSeedKey(seedKey, side)))
        {
            return;
        }

        queuedWindowCentersBySide[sideIndex].Add(center);
        float oppositeDistance = side == FrontierSide.Start
            ? Vector3.Distance(position, targetPosition)
            : Vector3.Distance(position, startPosition);
        float guideDistance = guideProjection.Project(position).Distance;
        float kindPenalty = points[pointId].Kind == SurfaceKind.Standable ? 0f : 2f;
        frontierSeeds.Enqueue(
            new FrontierSeed(pointId, side, center),
            oppositeDistance + guideDistance * 0.35f + kindPenalty);
    }

    private void RequeueCachedFrontierSeeds()
    {
        for (int index = 0; index < points.Count; index++)
        {
            EnqueueSeed(index, GetPreferredSide(points[index].Position));
        }
    }

    private bool TryQueueNextFrontierGrid()
    {
        while (frontierSeeds.Count > 0)
        {
            FrontierSeed seed = frontierSeeds.Dequeue();
            if (seed.PointId < 0 || seed.PointId >= points.Count)
            {
                continue;
            }

            LayeredGridKey seedKey = ToSeedKey(points[seed.PointId].Position);
            queuedSeedKeys.Remove(new LayeredSeedKey(seedKey, seed.Side));
            RemoveWindowCenter(queuedWindowCentersBySide[(int)seed.Side], seed.Center);
            if (!CanExpandSeed(seed))
            {
                continue;
            }

            if (!expandedSeedKeys.Add(new ExpandedSeedKey(seedKey, seed.Side, expansionBucket)))
            {
                continue;
            }

            expandedWindowCentersBySide[(int)seed.Side].Add(seed.Center);
            Vector2 center = seed.Center;
            HasActiveSeedPreview = true;
            ActiveSeedPreviewPosition = points[seed.PointId].Position;
            SetActiveSampleWindowPreview(center, points[seed.PointId].Position.y);
            PrepareActiveWindowColliders(center, points[seed.PointId].Position.y);
            activeWindowCenter = center;
            activeWindowSurfaceY = points[seed.PointId].Position.y;
            activeWindowGapProbeCount = 0;
            SurfacePoint surfaceSeed = points[seed.PointId];
            QueueWallSurfaceProbes(surfaceSeed);
            QueueNeighborSurfaceProbes(seed.PointId);
            if (pendingRayOrigins.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private bool DoesWindowOverlapExisting(Vector2 center, int sideIndex)
    {
        return DoesWindowOverlapAny(center, queuedWindowCentersBySide[sideIndex])
            || DoesWindowOverlapAny(center, expandedWindowCentersBySide[sideIndex]);
    }

    private bool DoesWindowOverlapAny(Vector2 center, IReadOnlyList<Vector2> existingCenters)
    {
        float nonOverlapStep = samplingWindowRadius * 2f - corridorSpacing * 0.5f;
        for (int index = 0; index < existingCenters.Count; index++)
        {
            Vector2 existing = existingCenters[index];
            if (Mathf.Abs(existing.x - center.x) < nonOverlapStep
                && Mathf.Abs(existing.y - center.y) < nonOverlapStep)
            {
                return true;
            }
        }

        return false;
    }

    private static void RemoveWindowCenter(List<Vector2> centers, Vector2 center)
    {
        for (int index = centers.Count - 1; index >= 0; index--)
        {
            if ((centers[index] - center).sqrMagnitude > 0.01f)
            {
                continue;
            }

            centers.RemoveAt(index);
            return;
        }
    }

    private void SetActiveSampleWindowPreview(Vector2 center, float preferredSurfaceY)
    {
        ActiveSampleWindowCenter = new Vector3(
            center.x,
            preferredSurfaceY,
            center.y);
        ActiveSampleWindowSize = new Vector3(
            samplingWindowRadius * 2f,
            samplingWindowVerticalExtent,
            samplingWindowRadius * 2f);
    }

    private void PrepareActiveWindowColliders(Vector2 center, float preferredSurfaceY)
    {
        activeWindowColliders.Clear();
        activeWindowColliderIds.Clear();
        activeWindowUsesGlobalRaycast = false;

        float horizontalExtent = samplingWindowRadius
            + Mathf.Max(StandWallProbeReach, ClimbSurfaceProbeOffset + ClimbSurfaceProbeDistance)
            + Mathf.Max(0.5f, config.SurfaceNeighborDistance);
        Vector3 boxCenter = new(
            center.x,
            preferredSurfaceY,
            center.y);
        Vector3 halfExtents = new(
            horizontalExtent,
            samplingWindowVerticalHalfExtent + 0.5f,
            horizontalExtent);
        int colliderCount = Physics.OverlapBoxNonAlloc(
            boxCenter,
            halfExtents,
            WindowColliderBuffer,
            Quaternion.identity,
            terrainMask,
            QueryTriggerInteraction.Ignore);
        BroadphaseWindowCount++;

        if (colliderCount >= WindowColliderBuffer.Length)
        {
            activeWindowUsesGlobalRaycast = true;
            return;
        }

        for (int index = 0; index < colliderCount; index++)
        {
            Collider collider = WindowColliderBuffer[index];
            if (collider == null || !activeWindowColliderIds.Add(collider.GetInstanceID()))
            {
                continue;
            }

            activeWindowColliders.Add(collider);
            if (activeWindowColliders.Count > MaxPerColliderRaycastColliders)
            {
                activeWindowColliders.Clear();
                activeWindowColliderIds.Clear();
                activeWindowUsesGlobalRaycast = true;
                return;
            }
        }
    }

    private bool CanExpandSeed(FrontierSeed seed)
    {
        Vector3 position = points[seed.PointId].Position;
        float distance = seed.Side == FrontierSide.Start
            ? Vector3.Distance(position, startPosition)
            : Vector3.Distance(position, targetPosition);
        return distance <= maxSeedDistanceFromAnchor
            && IsInsideGuideCorridor(position);
    }

    private void QueueStandableNeighborProbes(SurfacePoint seed)
    {
        Vector3 seedNormal = seed.Normal.normalized;
        if (seedNormal.sqrMagnitude < 0.001f)
        {
            seedNormal = Vector3.up;
        }

        float step = Mathf.Clamp(corridorSpacing, 0.25f, 0.5f);
        for (int directionIndex = 0; directionIndex < HorizontalProbeDirections.Length; directionIndex++)
        {
            Vector2 direction2 = HorizontalProbeDirections[directionIndex];
            Vector3 horizontalOffset = new(direction2.x * step, 0f, direction2.y * step);
            Vector3 tangentOffset = Vector3.ProjectOnPlane(horizontalOffset, seedNormal);
            if (tangentOffset.sqrMagnitude < 0.001f)
            {
                tangentOffset = horizontalOffset;
            }

            Vector3 projectedSurfaceEstimate = seed.Position + tangentOffset;
            if (!IsInsideActiveSampleWindow(projectedSurfaceEstimate) || !IsInsideGuideCorridor(projectedSurfaceEstimate))
            {
                continue;
            }

            if (!queuedRayKeys.Add(ToSurfaceProjectionKey(projectedSurfaceEstimate)))
            {
                continue;
            }

            pendingRayOrigins.Enqueue(SurfaceQuery.SurfaceProjection(
                projectedSurfaceEstimate + seedNormal * NeighborSurfaceProbeOffset,
                -seedNormal,
                NeighborSurfaceProbeOffset + localLayerSearchDownExtent,
                projectedSurfaceEstimate.y,
                seed.Id,
                directionIndex,
                tangentOffset.normalized));
        }
    }

    private void QueueGapProbesFromFailedBoundary(SurfaceQuery failedQuery)
    {
        if (failedQuery.Kind != QueryKind.SurfaceProjection
            || failedQuery.SourcePointId < 0
            || failedQuery.SourcePointId >= points.Count
            || activeWindowGapProbeCount >= MaxGapProbesPerWindow)
        {
            return;
        }

        SurfacePoint source = points[failedQuery.SourcePointId];
        if (source.Kind != SurfaceKind.Standable)
        {
            return;
        }

        Vector3 direction = failedQuery.NeighborDirection;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.001f)
        {
            return;
        }

        direction.Normalize();
        float minDistance = Mathf.Max(config.SurfaceNeighborDistance * 1.5f, corridorSpacing * 2f);
        float maxDistance = Mathf.Max(minDistance, config.MaxStandJumpDistance);
        for (int index = 0; index < GapProbeDistanceMultipliers.Length; index++)
        {
            if (activeWindowGapProbeCount >= MaxGapProbesPerWindow)
            {
                return;
            }

            float distance = Mathf.Max(minDistance, maxDistance * GapProbeDistanceMultipliers[index]);
            Vector3 landingEstimate = source.Position + direction * distance;
            if (!IsInsideActiveSampleWindow(landingEstimate) || !IsInsideGuideCorridor(landingEstimate))
            {
                continue;
            }

            if (!queuedRayKeys.Add(ToGapProbeKey(landingEstimate)))
            {
                continue;
            }

            float upExtent = Mathf.Max(config.MaxStandJumpUpHeight + GapProbeUpPadding, localLayerSearchUpExtent);
            float downExtent = Mathf.Max(config.MaxStandJumpDropHeight + GapProbeDownPadding, localLayerSearchDownExtent);
            pendingRayOrigins.Enqueue(SurfaceQuery.GapLanding(
                new Vector3(landingEstimate.x, source.Position.y + upExtent, landingEstimate.z),
                upExtent + downExtent,
                landingEstimate.y));
            activeWindowGapProbeCount++;
            GapProbeCount++;
        }
    }

    private void QueueStandableWallProbesThrottled(SurfacePoint seed)
    {
        FlatRegionKey key = FlatRegionKey.From(seed.Position);
        if (!queuedStandableWallProbeCells.Add(key))
        {
            return;
        }

        QueueStandableWallProbes(seed);
    }

    private void QueueWallSurfaceProbes(SurfacePoint seed)
    {
        if (seed.Kind == SurfaceKind.Standable)
        {
            QueueStandableWallProbes(seed);
            return;
        }

        if (seed.Kind == SurfaceKind.Climbable)
        {
            QueueClimbableSurfaceProbes(seed);
        }
    }

    private void QueueStandableWallProbes(SurfacePoint seed)
    {
        float reach = Mathf.Max(0.75f, Mathf.Min(StandWallProbeReach, config.SurfaceNeighborDistance + 0.6f));
        for (int heightIndex = 0; heightIndex < StandWallProbeHeightOffsets.Length; heightIndex++)
        {
            float yOffset = StandWallProbeHeightOffsets[heightIndex];
            Vector3 origin = seed.Position + Vector3.up * yOffset;
            for (int directionIndex = 0; directionIndex < HorizontalProbeDirections.Length; directionIndex++)
            {
                Vector2 direction2 = HorizontalProbeDirections[directionIndex];
                Vector3 direction = new(direction2.x, 0f, direction2.y);
                Vector3 target = origin + direction * reach;
                if (!IsInsideActiveSampleWindow(target))
                {
                    continue;
                }

                if (!queuedRayKeys.Add(ToProbeKey(origin, directionIndex, heightIndex)))
                {
                    continue;
                }

                pendingRayOrigins.Enqueue(SurfaceQuery.Directed(origin, direction, reach, origin.y));
            }
        }
    }

    private void QueueClimbableSurfaceProbes(SurfacePoint seed)
    {
        Vector3 normal = seed.Normal.normalized;
        if (normal.sqrMagnitude < 0.001f)
        {
            return;
        }

        Vector3 tangent = Vector3.Cross(Vector3.up, normal).normalized;
        if (tangent.sqrMagnitude < 0.001f)
        {
            tangent = Vector3.Cross(Vector3.forward, normal).normalized;
        }

        float step = Mathf.Clamp(config.SurfaceNeighborDistance, 0.35f, 1f);
        QueueClimbSurfaceProbe(seed.Position, normal, Vector3.up * step, 100);
        QueueClimbSurfaceProbe(seed.Position, normal, Vector3.down * step, 101);
        QueueClimbSurfaceProbe(seed.Position, normal, tangent * step, 102);
        QueueClimbSurfaceProbe(seed.Position, normal, -tangent * step, 103);
        QueueClimbSurfaceProbe(seed.Position, normal, (Vector3.up + tangent).normalized * step, 104);
        QueueClimbSurfaceProbe(seed.Position, normal, (Vector3.up - tangent).normalized * step, 105);
    }

    private void QueueClimbSurfaceProbe(Vector3 surfacePosition, Vector3 normal, Vector3 tangentOffset, int directionIndex)
    {
        Vector3 projectedSurfaceEstimate = surfacePosition + tangentOffset;
        if (!IsInsideActiveSampleWindow(projectedSurfaceEstimate) || !IsInsideGuideCorridor(projectedSurfaceEstimate))
        {
            return;
        }

        Vector3 origin = surfacePosition + tangentOffset + normal * ClimbSurfaceProbeOffset;
        if (!queuedRayKeys.Add(ToProbeKey(origin, directionIndex, ToLayerKey(origin.y))))
        {
            return;
        }

        pendingRayOrigins.Enqueue(SurfaceQuery.Directed(
            origin,
            -normal,
            ClimbSurfaceProbeDistance,
            surfacePosition.y + tangentOffset.y));
    }

    private FrontierSide GetPreferredSide(Vector3 position)
    {
        float startDistance = Vector3.Distance(position, startPosition);
        float targetDistance = Vector3.Distance(position, targetPosition);
        return startDistance <= targetDistance ? FrontierSide.Start : FrontierSide.Target;
    }

    private bool IsInsideGuideCorridor(Vector3 position)
    {
        return guideProjection.Project(position).Distance <= maxGuideLateralDistance;
    }

    private bool IsInsideGuideCorridor(Vector2 position)
    {
        return guideProjection.Project(new Vector3(position.x, startPosition.y, position.y)).Distance <= maxGuideLateralDistance;
    }

    private bool IsInsideActiveSampleWindow(Vector3 position)
    {
        return Mathf.Abs(position.x - activeWindowCenter.x) <= samplingWindowRadius + 0.001f
            && Mathf.Abs(position.z - activeWindowCenter.y) <= samplingWindowRadius + 0.001f
            && Mathf.Abs(position.y - activeWindowSurfaceY) <= samplingWindowVerticalHalfExtent + 0.5f;
    }

    private QueryKey ToSurfaceProjectionKey(Vector3 position)
    {
        return new QueryKey(
            Mathf.RoundToInt(position.x / corridorSpacing),
            ToLayerKey(position.y),
            Mathf.RoundToInt(position.z / corridorSpacing),
            direction: -2);
    }

    private QueryKey ToGapProbeKey(Vector3 position)
    {
        return new QueryKey(
            Mathf.RoundToInt(position.x / corridorSpacing),
            ToLayerKey(position.y),
            Mathf.RoundToInt(position.z / corridorSpacing),
            direction: -3);
    }

    private QueryKey ToProbeKey(Vector3 origin, int directionIndex, int heightLayer)
    {
        return new QueryKey(
            Mathf.RoundToInt(origin.x / corridorSpacing),
            heightLayer,
            Mathf.RoundToInt(origin.z / corridorSpacing),
            directionIndex);
    }

    private LayeredGridKey ToSeedKey(Vector3 position)
    {
        return new LayeredGridKey(
            Mathf.RoundToInt(position.x * PointQuantization),
            ToLayerKey(position.y),
            Mathf.RoundToInt(position.z * PointQuantization));
    }

    private int ToLayerKey(float y)
    {
        float layerSize = Mathf.Max(0.5f, config.MaxSampleVerticalLayerGap);
        return Mathf.RoundToInt(y / layerSize);
    }

    private readonly struct PointKey
    {
        private PointKey(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        private int X { get; }

        private int Y { get; }

        private int Z { get; }

        internal static PointKey From(Vector3 position)
        {
            return new PointKey(
                Mathf.RoundToInt(position.x * PointQuantization),
                Mathf.RoundToInt(position.y * PointQuantization),
                Mathf.RoundToInt(position.z * PointQuantization));
        }
    }

    private readonly struct FlatRegionKey : System.IEquatable<FlatRegionKey>
    {
        private FlatRegionKey(int x, int z)
        {
            X = x;
            Z = z;
        }

        private int X { get; }

        private int Z { get; }

        internal static FlatRegionKey From(Vector3 position)
        {
            return new FlatRegionKey(
                Mathf.FloorToInt(position.x / FlatStandableRegionCellSize),
                Mathf.FloorToInt(position.z / FlatStandableRegionCellSize));
        }

        public bool Equals(FlatRegionKey other)
        {
            return X == other.X && Z == other.Z;
        }

        public override bool Equals(object? obj)
        {
            return obj is FlatRegionKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (X * 397) ^ Z;
            }
        }
    }

    private readonly struct HitCandidate
    {
        internal HitCandidate(Vector3 position, Vector3 normal, int colliderId, SurfaceKind kind, float distance)
        {
            Position = position;
            Normal = normal;
            ColliderId = colliderId;
            Kind = kind;
            Distance = distance;
        }

        internal Vector3 Position { get; }

        internal Vector3 Normal { get; }

        internal int ColliderId { get; }

        internal SurfaceKind Kind { get; }

        internal float Distance { get; }
    }

    private readonly struct SurfaceQuery
    {
        private SurfaceQuery(
            QueryKind kind,
            Vector3 origin,
            Vector3 direction,
            float distance,
            float preferredSurfaceY,
            int sourcePointId,
            int directionIndex,
            Vector3 neighborDirection)
        {
            Kind = kind;
            Origin = origin;
            Direction = direction.normalized;
            Distance = distance;
            PreferredSurfaceY = preferredSurfaceY;
            SourcePointId = sourcePointId;
            DirectionIndex = directionIndex;
            NeighborDirection = neighborDirection;
        }

        internal QueryKind Kind { get; }

        internal Vector3 Origin { get; }

        internal Vector3 Direction { get; }

        internal float Distance { get; }

        internal float PreferredSurfaceY { get; }

        internal int SourcePointId { get; }

        internal int DirectionIndex { get; }

        internal Vector3 NeighborDirection { get; }

        internal static SurfaceQuery Vertical(Vector3 origin, float distance, float preferredSurfaceY)
        {
            return new SurfaceQuery(QueryKind.Vertical, origin, Vector3.down, distance, preferredSurfaceY, -1, -1, Vector3.zero);
        }

        internal static SurfaceQuery SurfaceProjection(
            Vector3 origin,
            Vector3 direction,
            float distance,
            float preferredSurfaceY,
            int sourcePointId,
            int directionIndex,
            Vector3 neighborDirection)
        {
            return new SurfaceQuery(
                QueryKind.SurfaceProjection,
                origin,
                direction,
                distance,
                preferredSurfaceY,
                sourcePointId,
                directionIndex,
                neighborDirection);
        }

        internal static SurfaceQuery GapLanding(Vector3 origin, float distance, float preferredSurfaceY)
        {
            return new SurfaceQuery(QueryKind.GapLanding, origin, Vector3.down, distance, preferredSurfaceY, -1, -1, Vector3.zero);
        }

        internal static SurfaceQuery Directed(Vector3 origin, Vector3 direction, float distance, float preferredSurfaceY)
        {
            return new SurfaceQuery(QueryKind.Directed, origin, direction, distance, preferredSurfaceY, -1, -1, Vector3.zero);
        }
    }

    private readonly struct LayeredGridKey : System.IEquatable<LayeredGridKey>
    {
        internal LayeredGridKey(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        internal int X { get; }

        private int Y { get; }

        internal int Z { get; }

        public bool Equals(LayeredGridKey other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object? obj)
        {
            return obj is LayeredGridKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X;
                hash = (hash * 397) ^ Y;
                hash = (hash * 397) ^ Z;
                return hash;
            }
        }
    }

    private readonly struct QueryKey : System.IEquatable<QueryKey>
    {
        internal QueryKey(int x, int y, int z, int direction)
        {
            X = x;
            Y = y;
            Z = z;
            Direction = direction;
        }

        private int X { get; }

        private int Y { get; }

        private int Z { get; }

        private int Direction { get; }

        public bool Equals(QueryKey other)
        {
            return X == other.X && Y == other.Y && Z == other.Z && Direction == other.Direction;
        }

        public override bool Equals(object? obj)
        {
            return obj is QueryKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X;
                hash = (hash * 397) ^ Y;
                hash = (hash * 397) ^ Z;
                hash = (hash * 397) ^ Direction;
                return hash;
            }
        }
    }

    private readonly struct LayeredSeedKey : System.IEquatable<LayeredSeedKey>
    {
        internal LayeredSeedKey(LayeredGridKey key, FrontierSide side)
        {
            Key = key;
            Side = side;
        }

        private LayeredGridKey Key { get; }

        private FrontierSide Side { get; }

        public bool Equals(LayeredSeedKey other)
        {
            return Key.Equals(other.Key) && Side == other.Side;
        }

        public override bool Equals(object? obj)
        {
            return obj is LayeredSeedKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (Key.GetHashCode() * 397) ^ (int)Side;
            }
        }
    }

    private readonly struct ExpandedSeedKey : System.IEquatable<ExpandedSeedKey>
    {
        internal ExpandedSeedKey(LayeredGridKey key, FrontierSide side, int expansionBucket)
        {
            Key = key;
            Side = side;
            ExpansionBucket = expansionBucket;
        }

        private LayeredGridKey Key { get; }

        private FrontierSide Side { get; }

        private int ExpansionBucket { get; }

        public bool Equals(ExpandedSeedKey other)
        {
            return Key.Equals(other.Key)
                && Side == other.Side
                && ExpansionBucket == other.ExpansionBucket;
        }

        public override bool Equals(object? obj)
        {
            return obj is ExpandedSeedKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = Key.GetHashCode();
                hash = (hash * 397) ^ (int)Side;
                hash = (hash * 397) ^ ExpansionBucket;
                return hash;
            }
        }
    }

    private readonly struct FrontierSeed
    {
        internal FrontierSeed(int pointId, FrontierSide side, Vector2 center)
        {
            PointId = pointId;
            Side = side;
            Center = center;
        }

        internal int PointId { get; }

        internal FrontierSide Side { get; }

        internal Vector2 Center { get; }
    }

    private enum FrontierSide
    {
        Start,
        Target,
    }

    private enum QueryKind
    {
        Vertical,
        SurfaceProjection,
        GapLanding,
        Directed,
    }
}
