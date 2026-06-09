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
    private const float StandableClearanceCacheCellSize = 0.35f;
    private const float SurfaceConnectionCastRadius = 0.06f;
    private const float SurfaceConnectionLift = 0.16f;
    private const float SurfaceSupportSampleSpacing = 0.35f;
    private const float SurfaceSupportHeightTolerance = 0.45f;
    private const float GapConnectionCastRadius = 0.12f;
    private const float GapConnectionLift = 0.45f;
    private const float SurfaceAirPathLift = 0.45f;
    private const float SurfaceAirPathProbeRadius = 0.12f;
    private const float SurfaceAirPathSampleSpacing = 0.35f;
    private const float ClimbAirTransferProbeOffset = 0.25f;
    private const float MinimumAirTransferProbeNormalDot = 0.45f;
    private const int MaxDropDiscoveryProbesPerWindow = 72;
    private const float ExteriorVisibilityHeightTolerance = 0.22f;
    private const float ExteriorVisibilityTopPadding = 0.35f;
    private const float ExteriorOverheadClearance = 0.4f;
    private const int MaxPerColliderRaycastColliders = 96;
    private const int MaxEfficientColliderRaycastColliders = 24;
    private const float FlatStandWalkProbeSkipNormalAngle = 18f;
    private const float FlatStandWalkProbeSkipMaxVerticalDelta = 0.2f;
    private const float ClimbProbeThrottleCellSize = 0.75f;
    private const float SurfaceAirFieldCellSize = 1f;
    private const float SurfaceAirProbeRadius = 0.42f;
    private const float SamplingWindowVerticalHalfExtentMultiplier = 4f / 3f;
    private const float SamplingSliceForwardHalfExtentMultiplier = 1.5f;
    private const float SurfaceMeshFieldCellSize = 0.75f;
    private const int MaxGapProbesPerWindow = 96;
    private const int MaxFocusedStandableNeighborDirections = 6;
    private const int MaxFocusedStandableWallProbeDirections = 4;
    private const int MaxSurfaceAirReachableCells = 24000;
    private const int MaxSurfaceAirBoundaryProbesPerWindow = 4096;
    private const int MaxGuidedSurfaceAirBoundaryProbesPerWindow = 768;
    private const int MaxGuidedGapProbesPerWindow = 24;
    private const int MaxGuidedDropDiscoveryProbesPerWindow = 24;
    private const int MaxDebugAirCellCenters = 24000;
    private const int MaxSurfaceAirBuildCellsPerFrame = 256;
    private const int MaxSurfaceMeshSnapshotTriangles = 30000;
    private static readonly float[] GapProbeDistanceMultipliers = [0.55f, 0.8f, 1f];
    private static readonly RaycastHit[] TerrainHitBuffer = new RaycastHit[128];
    private static readonly RaycastHit[] ClearanceHitBuffer = new RaycastHit[16];
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

    private readonly HashSet<QueryKey> processedRayKeys = [];
    private readonly HashSet<QueryKey> pendingRayKeys = [];
    private readonly HashSet<ExpandedSeedKey> expandedSeedKeys = [];
    private readonly HashSet<LayeredSeedKey> queuedSeedKeys = [];
    private readonly Queue<SurfaceQuery> pendingRayOrigins = new();
    private readonly PriorityQueue<FrontierSeed> frontierSeeds = new();
    private readonly List<Vector2>[] queuedWindowCentersBySide = [[], []];
    private readonly List<Vector2>[] expandedWindowCentersBySide = [[], []];
    private readonly List<Vector2>[] routeExpandedWindowCentersBySide = [[], []];
    private readonly Dictionary<PointKey, int> pointIdsByKey = [];
    private readonly Dictionary<StandableClearanceKey, bool> standableClearanceByCell = [];
    private readonly HashSet<FlatRegionKey> queuedStandableWallProbeCells = [];
    private readonly HashSet<ClimbProbeCellKey> queuedClimbableProbeCells = [];
    private readonly HashSet<GapDirectionKey> queuedGapProbeDirections = [];
    private readonly Dictionary<AirWindowKey, SurfaceAirField> surfaceAirFieldsByWindow = [];
    private readonly SurfaceAirField.SharedCache routeSurfaceAirCache = new();
    private readonly List<SurfacePoint> points = [];
    private readonly List<HitCandidate> hitCandidates = [];
    private readonly List<Collider> activeWindowColliders = [];
    private readonly HashSet<int> activeWindowColliderIds = [];
    private readonly List<Vector3> debugAirCellCenters = [];
    private readonly List<ProbeDirection> orderedProbeDirections = [];
    private readonly SurfaceProbeBody probeBody = new();

    private PlannerConfig config = null!;
    private VanillaStaminaModel staminaModel;
    private float corridorSpacing;
    private Vector3 startPosition;
    private Vector3 targetPosition;
    private GuideProjectionMap guideProjection = null!;
    private float maxSeedDistanceFromAnchor;
    private float maxGuideLateralDistance;
    private float samplingWindowRadius;
    private float samplingWindowVerticalHalfExtent;
    private float samplingWindowVerticalExtent;
    private float samplingSliceForwardHalfExtent;
    private float activeWindowForwardHalfExtent;
    private float localLayerSearchUpExtent;
    private float localLayerSearchDownExtent;
    private float localLayerVerticalExtent;
    private float guideProgressMin;
    private float guideProgressMax;
    private Vector2 activeWindowCenter;
    private Vector3 activeWindowScanDirection = Vector3.forward;
    private float activeWindowSurfaceY;
    private int expansionBucket;
    private int gridSamplesPerSeed;
    private int activeWindowGapProbeCount;
    private int activeWindowDropDiscoveryProbeCount;
    private int activeWindowAirBoundaryProbeCount;
    private bool rayGenerationComplete;
    private bool activeWindowUsesGlobalRaycast;
    private bool constrainToGuideCorridor;
    private bool enforceWindowPointLimit;
    private bool includeTargetFrontierInAttempt;
    private bool sampleFullWindowBySlices;
    private bool prioritizeGuidedSampling;
    private SurfaceAirField? activeSurfaceAirField;
    private SurfaceAirField.Builder? activeSurfaceAirBuilder;
    private SurfaceMeshField? activeSurfaceMeshField;
    private SurfacePoint activeSurfaceAirBuildSeed;
    private AirWindowKey activeSurfaceAirBuildKey;
    private int terrainMask;
    private int collisionMask;
    private int surfaceBlockerMask;
    private long profileQueueTicks;
    private long profileRaycastTicks;
    private long profileFilterTicks;
    private long profileBroadphaseTicks;
    private long profileExteriorTicks;
    private long profileClearanceTicks;
    private long profileHeadroomTicks;
    private long profileStandProbeTicks;
    private long profileReachabilityTicks;
    private long profileGapReachabilityTicks;
    private long profileSupportTicks;
    private long profileMoveProbeTicks;
    private long profileConnectionCastTicks;
    private long profileQueueProbeTicks;
    private long profileAirBuildTicks;
    private long profileAirPocketTicks;
    private long profileAirPathTicks;
    private long profileMeshSnapshotTicks;
    private long profileMeshBuildTicks;
    private long profileMeshPocketTicks;
    private long profileMeshSegmentTicks;

    internal IReadOnlyList<SurfacePoint> Points => points;

    internal IReadOnlyList<Vector3> DebugAirCellCenters => debugAirCellCenters;

    internal int StartIndex { get; private set; }

    internal int TargetIndex { get; private set; }

    internal int PendingRayCount => rayGenerationComplete
        ? 0
        : pendingRayOrigins.Count
            + frontierSeeds.Count * Mathf.Max(1, gridSamplesPerSeed)
            + (activeSurfaceAirBuilder != null ? 1 : 0);

    internal int ProcessedRayCount { get; private set; }

    internal int BroadphaseWindowCount { get; private set; }

    internal int GlobalRaycastFallbackCount { get; private set; }

    internal int GapProbeCount { get; private set; }

    internal int GapLandingPointCount { get; private set; }

    internal int DropDiscoveryProbeCount { get; private set; }

    internal int DropDiscoveryPointCount { get; private set; }

    internal int StandableClearanceCheckCount { get; private set; }

    internal int StandableClearanceCacheHitCount { get; private set; }

    internal int ContinuityRejectedCount { get; private set; }

    internal int GapLandingRejectedCount { get; private set; }

    internal int ExteriorVisibilityRejectedCount { get; private set; }

    internal int ProbeStandRejectedCount { get; private set; }

    internal int ProbeMoveRejectedCount { get; private set; }

    internal int ProcessFrameCount { get; private set; }

    internal double TotalProcessFrameMilliseconds { get; private set; }

    internal double MaxProcessFrameMilliseconds { get; private set; }

    internal int CandidateHitCount { get; private set; }

    internal int VisibilityCheckCount { get; private set; }

    internal int ClearanceProbeCheckCount { get; private set; }

    internal int ReachabilityCheckCount { get; private set; }

    internal int GapReachabilityCheckCount { get; private set; }

    internal int SupportCheckCount { get; private set; }

    internal int MoveProbeCheckCount { get; private set; }

    internal int MoveProbeSkippedCount { get; private set; }

    internal int ConnectionCastCheckCount { get; private set; }

    internal int QueuedProbeCount { get; private set; }

    internal int RaycastQueryCount { get; private set; }

    internal int LocalColliderRaycastQueryCount { get; private set; }

    internal int BroadphaseOverflowCount { get; private set; }

    internal int BroadphaseGlobalByColliderCount { get; private set; }

    internal int BroadphaseColliderCount { get; private set; }

    internal int BroadphaseMaxColliderCount { get; private set; }

    internal int SurfaceAirFieldWindowCount { get; private set; }

    internal int SurfaceAirFieldCacheHitCount { get; private set; }

    internal int SurfaceAirReachableCellCount { get; private set; }

    internal int SurfaceAirBoundaryCellCount { get; private set; }

    internal int SurfaceAirBoundaryProbeSourceCount { get; private set; }

    internal int SurfaceAirBoundaryProbeQueuedCount { get; private set; }

    internal int SurfaceAirBoundaryProbeSkippedWindowCount { get; private set; }

    internal int SurfaceAirBoundaryPointCount { get; private set; }

    internal int SurfaceAirBoundaryStandablePointCount { get; private set; }

    internal int SurfaceAirMaxReachableCellCount { get; private set; }

    internal int SurfaceAirCheckedCellCount { get; private set; }

    internal int SurfaceAirBlockedCellCount { get; private set; }

    internal int SurfaceAirBlockedTransitionCount { get; private set; }

    internal int SurfaceAirClearCellCacheHitCount { get; private set; }

    internal int SurfaceAirClearTransitionCacheHitCount { get; private set; }

    internal int SurfaceAirSharedClearCellCacheCount => routeSurfaceAirCache.ClearCellEntryCount;

    internal int SurfaceAirSharedClearTransitionCacheCount => routeSurfaceAirCache.ClearTransitionEntryCount;

    internal int SurfaceAirSliceAdvanceCount { get; private set; }

    internal int SurfaceAirOverflowCount { get; private set; }

    internal int SurfaceAirBuildFailedCount { get; private set; }

    internal int SurfaceAirPocketCheckCount { get; private set; }

    internal int SurfaceAirPocketRejectedCount { get; private set; }

    internal int SurfaceAirPathCheckCount { get; private set; }

    internal int SurfaceAirPathRejectedCount { get; private set; }

    internal int SurfaceMeshFieldWindowCount { get; private set; }

    internal int SurfaceMeshTriangleCount { get; private set; }

    internal int SurfaceMeshCellCount { get; private set; }

    internal int SurfaceMeshMaxTriangleCount { get; private set; }

    internal int SurfaceMeshMaxCellCount { get; private set; }

    internal int SurfaceMeshColliderCount { get; private set; }

    internal int SurfaceMeshSkippedColliderCount { get; private set; }

    internal int SurfaceMeshSkippedTriangleCount { get; private set; }

    internal int SurfaceMeshPocketCheckCount { get; private set; }

    internal int SurfaceMeshPocketRejectedCount { get; private set; }

    internal int SurfaceMeshSegmentCheckCount { get; private set; }

    internal int SurfaceMeshSegmentRejectedCount { get; private set; }

    internal double QueueMilliseconds => TicksToMilliseconds(profileQueueTicks);

    internal double RaycastMilliseconds => TicksToMilliseconds(profileRaycastTicks);

    internal double FilterMilliseconds => TicksToMilliseconds(profileFilterTicks);

    internal double BroadphaseMilliseconds => TicksToMilliseconds(profileBroadphaseTicks);

    internal double ExteriorMilliseconds => TicksToMilliseconds(profileExteriorTicks);

    internal double ClearanceMilliseconds => TicksToMilliseconds(profileClearanceTicks);

    internal double HeadroomMilliseconds => TicksToMilliseconds(profileHeadroomTicks);

    internal double StandProbeMilliseconds => TicksToMilliseconds(profileStandProbeTicks);

    internal double ReachabilityMilliseconds => TicksToMilliseconds(profileReachabilityTicks);

    internal double GapReachabilityMilliseconds => TicksToMilliseconds(profileGapReachabilityTicks);

    internal double SupportMilliseconds => TicksToMilliseconds(profileSupportTicks);

    internal double MoveProbeMilliseconds => TicksToMilliseconds(profileMoveProbeTicks);

    internal double ConnectionCastMilliseconds => TicksToMilliseconds(profileConnectionCastTicks);

    internal double QueueProbeMilliseconds => TicksToMilliseconds(profileQueueProbeTicks);

    internal double SurfaceAirBuildMilliseconds => TicksToMilliseconds(profileAirBuildTicks);

    internal double SurfaceAirPocketMilliseconds => TicksToMilliseconds(profileAirPocketTicks);

    internal double SurfaceAirPathMilliseconds => TicksToMilliseconds(profileAirPathTicks);

    internal double SurfaceMeshSnapshotMilliseconds => TicksToMilliseconds(profileMeshSnapshotTicks);

    internal double SurfaceMeshBuildMilliseconds => TicksToMilliseconds(profileMeshBuildTicks);

    internal double SurfaceMeshPocketMilliseconds => TicksToMilliseconds(profileMeshPocketTicks);

    internal double SurfaceMeshSegmentMilliseconds => TicksToMilliseconds(profileMeshSegmentTicks);

    internal bool HitPointLimit { get; private set; }

    internal bool HitWindowPointLimit { get; private set; }

    internal int CachedPointCountAtAttemptStart { get; private set; }

    internal bool HasActiveSeedPreview { get; private set; }

    internal Vector3 ActiveSeedPreviewPosition { get; private set; }

    internal Vector3 ActiveSampleWindowCenter { get; private set; }

    internal Vector3 ActiveSampleWindowSize { get; private set; }

    internal Quaternion ActiveSampleWindowRotation { get; private set; } = Quaternion.identity;

    internal void Begin(
        Vector3 start,
        Vector3 target,
        IReadOnlyList<Vector3> guidePath,
        float corridorRadius,
        PlannerConfig plannerConfig,
        bool preserveSampleCache,
        bool includeTargetFrontier,
        bool constrainToGuide = true,
        bool enforcePointLimitPerWindow = true,
        bool sampleFullWindowBySlices = false,
        bool prioritizeGuidedSampling = false)
    {
        config = plannerConfig;
        staminaModel = new VanillaStaminaModel(config);
        constrainToGuideCorridor = constrainToGuide;
        enforceWindowPointLimit = enforcePointLimitPerWindow;
        includeTargetFrontierInAttempt = includeTargetFrontier;
        this.sampleFullWindowBySlices = sampleFullWindowBySlices;
        this.prioritizeGuidedSampling = prioritizeGuidedSampling;
        terrainMask = HelperFunctions.GetMask(HelperFunctions.LayerType.TerrainMap);
        collisionMask = HelperFunctions.GetMask(HelperFunctions.LayerType.AllPhysicalExceptCharacter);
        surfaceBlockerMask = terrainMask | collisionMask;

        if (!preserveSampleCache)
        {
            processedRayKeys.Clear();
            pendingRayKeys.Clear();
            expandedSeedKeys.Clear();
            pointIdsByKey.Clear();
            standableClearanceByCell.Clear();
            queuedStandableWallProbeCells.Clear();
            queuedClimbableProbeCells.Clear();
            queuedGapProbeDirections.Clear();
            surfaceAirFieldsByWindow.Clear();
            routeSurfaceAirCache.Clear();
            points.Clear();
            StartIndex = -1;
            TargetIndex = -1;
        }

        for (int index = 0; index < routeExpandedWindowCentersBySide.Length; index++)
        {
            routeExpandedWindowCentersBySide[index].Clear();
        }

        frontierSeeds.Clear();
        queuedSeedKeys.Clear();
        pendingRayKeys.Clear();
        for (int index = 0; index < queuedWindowCentersBySide.Length; index++)
        {
            queuedWindowCentersBySide[index].Clear();
            expandedWindowCentersBySide[index].Clear();
        }
        queuedGapProbeDirections.Clear();

        while (pendingRayOrigins.Count > 0)
        {
            pendingRayOrigins.Dequeue();
        }

        activeWindowColliders.Clear();
        activeWindowColliderIds.Clear();
        if (!preserveSampleCache)
        {
            debugAirCellCenters.Clear();
        }

        activeSurfaceAirField = null;
        activeSurfaceAirBuilder = null;
        activeSurfaceMeshField = null;
        activeWindowUsesGlobalRaycast = false;
        ProcessedRayCount = 0;
        BroadphaseWindowCount = 0;
        GlobalRaycastFallbackCount = 0;
        GapProbeCount = 0;
        GapLandingPointCount = 0;
        DropDiscoveryProbeCount = 0;
        DropDiscoveryPointCount = 0;
        StandableClearanceCheckCount = 0;
        StandableClearanceCacheHitCount = 0;
        ContinuityRejectedCount = 0;
        GapLandingRejectedCount = 0;
        ExteriorVisibilityRejectedCount = 0;
        ProbeStandRejectedCount = 0;
        ProbeMoveRejectedCount = 0;
        ProcessFrameCount = 0;
        TotalProcessFrameMilliseconds = 0d;
        MaxProcessFrameMilliseconds = 0d;
        CandidateHitCount = 0;
        VisibilityCheckCount = 0;
        ClearanceProbeCheckCount = 0;
        ReachabilityCheckCount = 0;
        GapReachabilityCheckCount = 0;
        SupportCheckCount = 0;
        MoveProbeCheckCount = 0;
        MoveProbeSkippedCount = 0;
        ConnectionCastCheckCount = 0;
        QueuedProbeCount = 0;
        RaycastQueryCount = 0;
        LocalColliderRaycastQueryCount = 0;
        BroadphaseOverflowCount = 0;
        BroadphaseGlobalByColliderCount = 0;
        BroadphaseColliderCount = 0;
        BroadphaseMaxColliderCount = 0;
        SurfaceAirFieldWindowCount = 0;
        SurfaceAirFieldCacheHitCount = 0;
        SurfaceAirReachableCellCount = 0;
        SurfaceAirBoundaryCellCount = 0;
        SurfaceAirBoundaryProbeSourceCount = 0;
        SurfaceAirBoundaryProbeQueuedCount = 0;
        SurfaceAirBoundaryProbeSkippedWindowCount = 0;
        SurfaceAirBoundaryPointCount = 0;
        SurfaceAirBoundaryStandablePointCount = 0;
        SurfaceAirMaxReachableCellCount = 0;
        SurfaceAirCheckedCellCount = 0;
        SurfaceAirBlockedCellCount = 0;
        SurfaceAirBlockedTransitionCount = 0;
        SurfaceAirClearCellCacheHitCount = 0;
        SurfaceAirClearTransitionCacheHitCount = 0;
        SurfaceAirSliceAdvanceCount = 0;
        SurfaceAirOverflowCount = 0;
        SurfaceAirBuildFailedCount = 0;
        SurfaceAirPocketCheckCount = 0;
        SurfaceAirPocketRejectedCount = 0;
        SurfaceAirPathCheckCount = 0;
        SurfaceAirPathRejectedCount = 0;
        SurfaceMeshFieldWindowCount = 0;
        SurfaceMeshTriangleCount = 0;
        SurfaceMeshCellCount = 0;
        SurfaceMeshMaxTriangleCount = 0;
        SurfaceMeshMaxCellCount = 0;
        SurfaceMeshColliderCount = 0;
        SurfaceMeshSkippedColliderCount = 0;
        SurfaceMeshSkippedTriangleCount = 0;
        SurfaceMeshPocketCheckCount = 0;
        SurfaceMeshPocketRejectedCount = 0;
        SurfaceMeshSegmentCheckCount = 0;
        SurfaceMeshSegmentRejectedCount = 0;
        profileQueueTicks = 0;
        profileRaycastTicks = 0;
        profileFilterTicks = 0;
        profileBroadphaseTicks = 0;
        profileExteriorTicks = 0;
        profileClearanceTicks = 0;
        profileHeadroomTicks = 0;
        profileStandProbeTicks = 0;
        profileReachabilityTicks = 0;
        profileGapReachabilityTicks = 0;
        profileSupportTicks = 0;
        profileMoveProbeTicks = 0;
        profileConnectionCastTicks = 0;
        profileQueueProbeTicks = 0;
        profileAirBuildTicks = 0;
        profileAirPocketTicks = 0;
        profileAirPathTicks = 0;
        profileMeshSnapshotTicks = 0;
        profileMeshBuildTicks = 0;
        profileMeshPocketTicks = 0;
        profileMeshSegmentTicks = 0;
        HitPointLimit = false;
        HitWindowPointLimit = false;
        CachedPointCountAtAttemptStart = points.Count;
        HasActiveSeedPreview = true;
        ActiveSeedPreviewPosition = start;

        PrepareBidirectionalGrid(start, target, guidePath, corridorRadius, includeTargetFrontier);
        StartIndex = AddAnchoredPoint(start, SurfaceKind.Standable);
        if (StartIndex >= 0 && StartIndex < points.Count)
        {
            SetActiveSampleWindowPreview(new Vector2(start.x, start.z), points[StartIndex].Position.y, GetWindowScanDirection(start));
            activeWindowCenter = new Vector2(start.x, start.z);
            activeWindowSurfaceY = points[StartIndex].Position.y;
        }
        else
        {
            SetActiveSampleWindowPreview(new Vector2(start.x, start.z), start.y, GetWindowScanDirection(start));
            activeWindowCenter = new Vector2(start.x, start.z);
            activeWindowSurfaceY = start.y;
        }

        TargetIndex = includeTargetFrontier
            ? AddAnchoredPoint(target, SurfaceKind.Standable)
            : -1;

        EnqueueSeed(StartIndex, FrontierSide.Start);
        if (includeTargetFrontier)
        {
            EnqueueSeed(TargetIndex, FrontierSide.Target);
        }

        if (preserveSampleCache)
        {
            RequeueCachedFrontierSeeds(includeTargetFrontier);
        }
    }

    internal bool ProcessFrame()
    {
        long frameStart = Stopwatch.GetTimestamp();
        int budget = config.MaxPhysicsQueriesPerFrame;
        int processedThisFrame = 0;
        while (budget > 0 && !rayGenerationComplete)
        {
            if (HitPointLimit || (enforceWindowPointLimit && HitWindowPointLimit))
            {
                rayGenerationComplete = true;
                break;
            }

            if (activeSurfaceAirBuilder != null)
            {
                long airBuildStart = Stopwatch.GetTimestamp();
                bool airBuildComplete = activeSurfaceAirBuilder.Process(
                    MaxSurfaceAirBuildCellsPerFrame,
                    config.MaxMainThreadMillisecondsPerFrame);
                AddProfileTicks(ref profileAirBuildTicks, airBuildStart);
                QueueAirBoundarySurfaceProbes(activeSurfaceAirBuilder);
                processedThisFrame++;
                if (!airBuildComplete)
                {
                    if (pendingRayOrigins.Count == 0)
                    {
                        break;
                    }
                }
                else
                {
                    FinalizeActiveSurfaceAirField(activeSurfaceAirBuilder, activeSurfaceAirBuildSeed);
                    activeSurfaceAirBuilder = null;
                    double elapsedAfterAirBuild = TicksToMilliseconds(Stopwatch.GetTimestamp() - frameStart);
                    if (elapsedAfterAirBuild >= config.MaxMainThreadMillisecondsPerFrame && pendingRayOrigins.Count == 0)
                    {
                        break;
                    }
                }
            }

            long queueStart = Stopwatch.GetTimestamp();
            bool hasQuery = TryGetNextSurfaceQuery(out SurfaceQuery query);
            AddProfileTicks(ref profileQueueTicks, queueStart);
            if (!hasQuery)
            {
                break;
            }

            ProcessedRayCount++;
            processedThisFrame++;
            budget--;
            pendingRayKeys.Remove(query.Key);
            processedRayKeys.Add(query.Key);

            long raycastStart = Stopwatch.GetTimestamp();
            int hitCount = RaycastQuery(query);
            AddProfileTicks(ref profileRaycastTicks, raycastStart);

            long filterStart = Stopwatch.GetTimestamp();
            AddFilteredHits(hitCount, query);
            AddProfileTicks(ref profileFilterTicks, filterStart);

            double elapsedMilliseconds = TicksToMilliseconds(Stopwatch.GetTimestamp() - frameStart);
            if (processedThisFrame > 0 && elapsedMilliseconds >= config.MaxMainThreadMillisecondsPerFrame)
            {
                break;
            }
        }

        double frameMilliseconds = TicksToMilliseconds(Stopwatch.GetTimestamp() - frameStart);
        ProcessFrameCount++;
        TotalProcessFrameMilliseconds += frameMilliseconds;
        if (frameMilliseconds > MaxProcessFrameMilliseconds)
        {
            MaxProcessFrameMilliseconds = frameMilliseconds;
        }

        return rayGenerationComplete;
    }

    private void PrepareBidirectionalGrid(
        Vector3 start,
        Vector3 target,
        IReadOnlyList<Vector3> guidePath,
        float expansionRadius,
        bool includeTargetFrontier)
    {
        startPosition = start;
        targetPosition = target;
        guideProjection = GuideProjectionMap.Build(guidePath);
        corridorSpacing = Mathf.Max(0.25f, config.HorizontalSampleSpacing);
        samplingWindowRadius = Mathf.Max(
            corridorSpacing,
            Mathf.Round(Mathf.Max(corridorSpacing, config.SurfaceSamplingWindowRadius) / corridorSpacing) * corridorSpacing);
        samplingWindowVerticalHalfExtent = samplingWindowRadius * SamplingWindowVerticalHalfExtentMultiplier;
        samplingWindowVerticalExtent = samplingWindowVerticalHalfExtent * 2f;
        samplingSliceForwardHalfExtent = Mathf.Max(
            corridorSpacing,
            config.SurfaceNeighborDistance * SamplingSliceForwardHalfExtentMultiplier);
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
        GuideProjectionPoint startProjection = guideProjection.Project(start);
        GuideProjectionPoint targetProjection = guideProjection.Project(target);
        float backwardAllowance = Mathf.Max(config.SurfaceNeighborDistance * 2f, config.MinimumPartialSegmentDistance);
        float targetProgressDelta = Mathf.Max(0f, targetProjection.Progress - startProjection.Progress);
        float forwardAllowance = includeTargetFrontier
            ? Mathf.Max(samplingWindowRadius * 1.5f, targetProgressDelta + config.SurfaceNeighborDistance * 2f)
            : Mathf.Max(samplingWindowRadius * 1.15f, config.MaxStandJumpDistance + config.MinimumFrontierAdvanceDistance);
        guideProgressMin = startProjection.Progress - backwardAllowance;
        guideProgressMax = startProjection.Progress + forwardAllowance;

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
            surfaceBlockerMask,
            QueryTriggerInteraction.Ignore);

        surfacePosition = default;
        normal = Vector3.up;
        colliderId = 0;
        kind = SurfaceKind.Blocked;
        float bestScore = float.MaxValue;
        bool found = false;
        float outermostDistance = GetOutermostHitDistance(hitCount);
        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = TerrainHitBuffer[index];
            Collider collider = hit.collider;
            if (collider == null)
            {
                continue;
            }

            SurfaceKind hitKind = ClassifySurfaceHit(hit);
            if (hitKind == SurfaceKind.Blocked)
            {
                continue;
            }

            HitCandidate candidate = new(hit.point, hit.normal, collider.GetInstanceID(), hitKind, hit.distance);
            if (!IsOutermostHit(hit.distance, outermostDistance)
                && candidate.Position.y <= position.y + ExteriorVisibilityHeightTolerance)
            {
                continue;
            }

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
        RaycastQueryCount++;
        if (query.Kind == QueryKind.Vertical || query.Kind == QueryKind.GapLanding || query.Kind == QueryKind.DropDiscovery)
        {
            return Physics.RaycastNonAlloc(
                query.Origin,
                query.Direction,
                TerrainHitBuffer,
                query.Distance,
                surfaceBlockerMask,
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
                surfaceBlockerMask,
                QueryTriggerInteraction.Ignore);
        }

        int hitCount = 0;
        Ray ray = new(query.Origin, query.Direction);
        for (int index = 0; index < activeWindowColliders.Count; index++)
        {
            Collider collider = activeWindowColliders[index];
            LocalColliderRaycastQueryCount++;
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
                    surfaceBlockerMask,
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
            if (activeSurfaceAirBuilder != null)
            {
                if (pendingRayOrigins.Count == 0)
                {
                    query = default;
                    return false;
                }

                break;
            }

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
        return VanillaSurfaceRules.ClassifySurface(normal, config);
    }

    private SurfaceKind ClassifySurfaceHit(RaycastHit hit)
    {
        if (!VanillaSurfaceRules.AllowsSurface(hit.collider))
        {
            return SurfaceKind.Blocked;
        }

        SurfaceKind kind = ClassifySurface(hit.normal);
        return kind == SurfaceKind.Standable && !VanillaSurfaceRules.AllowsStandableBody(hit.rigidbody)
            ? SurfaceKind.Blocked
            : kind;
    }

    private void AddFilteredHits(int hitCount, SurfaceQuery query)
    {
        hitCandidates.Clear();
        bool useVerticalLayerSelection = query.Kind == QueryKind.Vertical
            || query.Kind == QueryKind.GapLanding
            || query.Kind == QueryKind.DropDiscovery;
        bool requireOutermostHit = !useVerticalLayerSelection && query.Kind != QueryKind.AirTransfer;
        float outermostDistance = requireOutermostHit ? GetOutermostHitDistance(hitCount) : float.MaxValue;
        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = TerrainHitBuffer[index];
            Collider collider = hit.collider;
            if (collider == null)
            {
                continue;
            }

            int colliderId = collider.GetInstanceID();
            if (query.Kind == QueryKind.AirTransfer
                && query.SourcePointId >= 0
                && query.SourcePointId < points.Count
                && colliderId == points[query.SourcePointId].ColliderId)
            {
                continue;
            }

            if (requireOutermostHit && !IsOutermostHit(hit.distance, outermostDistance))
            {
                continue;
            }

            SurfaceKind kind = ClassifySurfaceHit(hit);
            if (kind == SurfaceKind.Blocked)
            {
                continue;
            }

            if (query.Kind == QueryKind.AirTransfer && kind != SurfaceKind.Climbable)
            {
                continue;
            }

            if (!useVerticalLayerSelection
                && Vector3.Dot(hit.normal.normalized, -query.Direction) < MinimumWallFacingDot)
            {
                continue;
            }

            CandidateHitCount++;
            HitCandidate candidate = new(hit.point, hit.normal, colliderId, kind, hit.distance);
            if (!PassesSurfaceAirPocket(candidate))
            {
                continue;
            }

            if (!PassesSurfaceMeshPocket(candidate))
            {
                continue;
            }

            if (query.Kind != QueryKind.AirBoundary
                && RequiresSourceSurfaceContinuity(query)
                && !PassesSourceSurfaceContinuity(query, candidate))
            {
                continue;
            }

            hitCandidates.Add(candidate);
        }

        if (hitCandidates.Count == 0)
        {
            QueueGapProbesFromFailedBoundary(query);
            return;
        }

        if (query.Kind == QueryKind.GapLanding)
        {
            AddBestDirectedHit(query);
            return;
        }

        if (query.Kind == QueryKind.AirBoundary)
        {
            AddBestDirectedHit(query);
            return;
        }

        if (query.Kind == QueryKind.DropDiscovery)
        {
            AddBestDiscoveryHit(query);
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

            if (!IsVisibleExteriorSurfaceCandidate(query, candidate))
            {
                continue;
            }

            if (IsOccludedStandableSurface(candidate))
            {
                continue;
            }

            if (query.Kind != QueryKind.AirBoundary && !IsAllowedByGuideCorridor(candidate.Position))
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
            if (query.Kind != QueryKind.AirBoundary && !IsAllowedByGuideCorridor(candidate.Position))
            {
                continue;
            }

            if (!IsVisibleExteriorSurfaceCandidate(query, candidate))
            {
                continue;
            }

            if (IsOccludedStandableSurface(candidate))
            {
                continue;
            }

            float score = candidate.Distance
                + Mathf.Abs(candidate.Position.y - query.PreferredSurfaceY) * 0.2f
                + GetDirectedHitKindPenalty(query, candidate);
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
            if (query.Kind == QueryKind.AirBoundary)
            {
                SurfaceAirBoundaryPointCount++;
                if (best.Kind == SurfaceKind.Standable)
                {
                    SurfaceAirBoundaryStandablePointCount++;
                }
            }

            EnqueueSeed(pointId, GetPreferredSide(best.Position));
            if (query.Kind != QueryKind.AirBoundary)
            {
                QueueNeighborSurfaceProbes(pointId);
            }
        }
    }

    private static float GetDirectedHitKindPenalty(SurfaceQuery query, HitCandidate candidate)
    {
        if (query.Kind == QueryKind.AirBoundary)
        {
            return candidate.Kind == SurfaceKind.Standable ? 0f : 0.65f;
        }

        return candidate.Kind == SurfaceKind.Climbable ? 0f : 0.6f;
    }

    private void AddBestDiscoveryHit(SurfaceQuery query)
    {
        int bestIndex = -1;
        float bestScore = float.MaxValue;
        for (int index = 0; index < hitCandidates.Count; index++)
        {
            HitCandidate candidate = hitCandidates[index];
            if (candidate.Kind != SurfaceKind.Standable)
            {
                continue;
            }

            if (!IsAllowedByGuideCorridor(candidate.Position)
                || !IsVisibleExteriorSurfaceCandidate(query, candidate)
                || IsOccludedStandableSurface(candidate))
            {
                continue;
            }

            float score = Mathf.Abs(candidate.Position.y - query.PreferredSurfaceY)
                + candidate.Distance * 0.05f
                + (candidate.Kind == SurfaceKind.Standable ? 0f : 0.8f);
            if (score >= bestScore)
            {
                continue;
            }

            bestIndex = index;
            bestScore = score;
        }

        if (bestIndex < 0)
        {
            return;
        }

        HitCandidate best = hitCandidates[bestIndex];
        int pointId = AddPoint(best.Position, best.Normal, best.ColliderId, best.Kind);
        if (pointId >= 0)
        {
            DropDiscoveryPointCount++;
            EnqueueSeed(pointId, GetPreferredSide(best.Position));
            QueueNeighborSurfaceProbes(pointId);
        }
    }

    private void QueueNeighborSurfaceProbes(int pointId)
    {
        long profileStart = Stopwatch.GetTimestamp();
        try
        {
            QueueNeighborSurfaceProbesUnprofiled(pointId);
        }
        finally
        {
            AddProfileTicks(ref profileQueueProbeTicks, profileStart);
        }
    }

    private void QueueNeighborSurfaceProbesUnprofiled(int pointId)
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
            QueueDropDiscoveryProbes(point);
            return;
        }

        if (point.Kind == SurfaceKind.Climbable)
        {
            QueueClimbableSurfaceProbesThrottled(point);
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

    private bool IsVisibleExteriorSurfaceCandidate(SurfaceQuery query, HitCandidate candidate)
    {
        VisibilityCheckCount++;
        long profileStart = Stopwatch.GetTimestamp();
        try
        {
            return IsVisibleExteriorSurfaceCandidateUnprofiled(query, candidate);
        }
        finally
        {
            AddProfileTicks(ref profileExteriorTicks, profileStart);
        }
    }

    private bool IsVisibleExteriorSurfaceCandidateUnprofiled(SurfaceQuery query, HitCandidate candidate)
    {
        if (query.Kind == QueryKind.Vertical)
        {
            return true;
        }

        if (query.SourcePointId < 0 || query.SourcePointId >= points.Count)
        {
            return true;
        }

        SurfacePoint source = points[query.SourcePointId];
        if (!IsExteriorSeed(source))
        {
            return true;
        }

        if (candidate.Kind == SurfaceKind.Climbable)
        {
            return true;
        }

        if (!IsTopVisibleAt(candidate.Position, candidate.ColliderId, out RaycastHit topHit))
        {
            ExteriorVisibilityRejectedCount++;
            return false;
        }

        if (Mathf.Abs(topHit.point.y - candidate.Position.y) > ExteriorVisibilityHeightTolerance)
        {
            ExteriorVisibilityRejectedCount++;
            return false;
        }

        int topColliderId = topHit.collider != null ? topHit.collider.GetInstanceID() : 0;
        if (candidate.ColliderId != 0 && topColliderId != candidate.ColliderId)
        {
            ExteriorVisibilityRejectedCount++;
            return false;
        }

        return true;
    }

    private bool PassesSurfaceAirPocket(HitCandidate candidate)
    {
        SurfaceAirField? airField = activeSurfaceAirField;
        SurfaceAirField.Builder? airBuilder = activeSurfaceAirBuilder;
        if ((airField == null || !airField.FoundStart)
            && (airBuilder == null || !airBuilder.FoundStart))
        {
            return true;
        }

        SurfaceAirPocketCheckCount++;
        long profileStart = Stopwatch.GetTimestamp();
        try
        {
            bool reachable = airField != null && airField.FoundStart
                ? airField.HasReachableNormalPocket(
                    candidate.Position,
                    candidate.Normal,
                    candidate.ColliderId)
                : airBuilder!.HasReachableNormalPocket(
                    candidate.Position,
                    candidate.Normal,
                    candidate.ColliderId);
            if (!reachable)
            {
                SurfaceAirPocketRejectedCount++;
            }

            return reachable;
        }
        finally
        {
            AddProfileTicks(ref profileAirPocketTicks, profileStart);
        }
    }

    private bool PassesSurfaceMeshPocket(HitCandidate candidate)
    {
        SurfaceMeshField? meshField = activeSurfaceMeshField;
        if (meshField == null || meshField.TriangleCount == 0)
        {
            return true;
        }

        SurfaceMeshPocketCheckCount++;
        long profileStart = Stopwatch.GetTimestamp();
        try
        {
            bool clear = meshField.HasClearNormalPocket(
                candidate.Position,
                candidate.Normal,
                candidate.ColliderId);
            if (!clear)
            {
                SurfaceMeshPocketRejectedCount++;
            }

            return clear;
        }
        finally
        {
            AddProfileTicks(ref profileMeshPocketTicks, profileStart);
        }
    }

    private bool HasSurfaceMeshClearSegment(
        Vector3 from,
        Vector3 to,
        int sourceColliderId,
        int targetColliderId)
    {
        SurfaceMeshField? meshField = activeSurfaceMeshField;
        if (meshField == null || meshField.TriangleCount == 0)
        {
            return true;
        }

        SurfaceMeshSegmentCheckCount++;
        long profileStart = Stopwatch.GetTimestamp();
        try
        {
            bool clear = meshField.HasClearSegment(from, to, sourceColliderId, targetColliderId);
            if (!clear)
            {
                SurfaceMeshSegmentRejectedCount++;
            }

            return clear;
        }
        finally
        {
            AddProfileTicks(ref profileMeshSegmentTicks, profileStart);
        }
    }

    private bool HasClearAirPathBetween(SurfacePoint source, HitCandidate candidate)
    {
        Vector3 sourceNormal = source.Normal.sqrMagnitude > 0.001f ? source.Normal.normalized : Vector3.up;
        Vector3 candidateNormal = candidate.Normal.sqrMagnitude > 0.001f ? candidate.Normal.normalized : Vector3.up;
        Vector3 from = source.Position + sourceNormal * SurfaceAirPathLift;
        Vector3 to = candidate.Position + candidateNormal * SurfaceAirPathLift;
        Vector3 delta = to - from;
        float distance = delta.magnitude;
        if (distance <= SurfaceAirPathProbeRadius * 2f)
        {
            return true;
        }

        SurfaceAirPathCheckCount++;
        long profileStart = Stopwatch.GetTimestamp();
        try
        {
            Vector3 direction = delta / distance;
            int hitCount = Physics.SphereCastNonAlloc(
                from,
                SurfaceAirPathProbeRadius,
                direction,
                ClearanceHitBuffer,
                distance,
                surfaceBlockerMask,
                QueryTriggerInteraction.Ignore);
            for (int index = 0; index < hitCount; index++)
            {
                RaycastHit hit = ClearanceHitBuffer[index];
                if (hit.collider == null
                    || hit.distance <= SurfaceAirPathProbeRadius
                    || hit.distance >= distance - SurfaceAirPathProbeRadius)
                {
                    continue;
                }

                SurfaceAirPathRejectedCount++;
                return false;
            }

            int steps = Mathf.Clamp(Mathf.CeilToInt(distance / SurfaceAirPathSampleSpacing), 2, 8);
            for (int step = 1; step < steps; step++)
            {
                Vector3 sample = Vector3.Lerp(from, to, step / (float)steps);
                if (!Physics.CheckSphere(sample, SurfaceAirPathProbeRadius, surfaceBlockerMask, QueryTriggerInteraction.Ignore))
                {
                    continue;
                }

                SurfaceAirPathRejectedCount++;
                return false;
            }

            return true;
        }
        finally
        {
            AddProfileTicks(ref profileAirPathTicks, profileStart);
        }
    }

    private bool IsExteriorSeed(SurfacePoint source)
    {
        if (source.Kind != SurfaceKind.Standable)
        {
            return false;
        }

        Vector3 origin = source.Position + Vector3.up * ExteriorOverheadClearance;
        float distance = Mathf.Max(
            ExteriorOverheadClearance + 0.25f,
            activeWindowSurfaceY + samplingWindowVerticalHalfExtent - origin.y);
        if (distance <= 0.05f)
        {
            return true;
        }

        int hitCount = Physics.SphereCastNonAlloc(
            origin,
            SurfaceConnectionCastRadius,
            Vector3.up,
            ClearanceHitBuffer,
            distance,
            surfaceBlockerMask,
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

    private bool IsTopVisibleAt(Vector3 position, int candidateColliderId, out RaycastHit topHit)
    {
        topHit = default;
        float topY = activeWindowSurfaceY + samplingWindowVerticalHalfExtent + ExteriorVisibilityTopPadding;
        float bottomY = activeWindowSurfaceY - samplingWindowVerticalHalfExtent - ExteriorVisibilityTopPadding;
        topY = Mathf.Max(topY, position.y + ExteriorVisibilityTopPadding);
        bottomY = Mathf.Min(bottomY, position.y - ExteriorVisibilityTopPadding);
        Vector3 origin = new(position.x, topY, position.z);
        float distance = Mathf.Max(0.1f, topY - bottomY);
        int hitCount = Physics.RaycastNonAlloc(
            origin,
            Vector3.down,
            TerrainHitBuffer,
            distance,
            surfaceBlockerMask,
            QueryTriggerInteraction.Ignore);
        if (hitCount <= 0)
        {
            return false;
        }

        float bestDistance = float.MaxValue;
        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = TerrainHitBuffer[index];
            Collider collider = hit.collider;
            if (collider == null || hit.distance >= bestDistance)
            {
                continue;
            }

            bestDistance = hit.distance;
            topHit = hit;
        }

        return bestDistance < float.MaxValue;
    }

    private bool IsReachableSurfaceCandidate(SurfaceQuery query, HitCandidate candidate)
    {
        ReachabilityCheckCount++;
        long profileStart = Stopwatch.GetTimestamp();
        try
        {
            return IsReachableSurfaceCandidateUnprofiled(query, candidate, requireStandableMoveProbe: true);
        }
        finally
        {
            AddProfileTicks(ref profileReachabilityTicks, profileStart);
        }
    }

    private bool PassesSourceSurfaceContinuity(SurfaceQuery query, HitCandidate candidate)
    {
        ReachabilityCheckCount++;
        long profileStart = Stopwatch.GetTimestamp();
        try
        {
            return IsReachableSurfaceCandidateUnprofiled(query, candidate, requireStandableMoveProbe: false);
        }
        finally
        {
            AddProfileTicks(ref profileReachabilityTicks, profileStart);
        }
    }

    private static bool RequiresSourceSurfaceContinuity(SurfaceQuery query)
    {
        return query.Kind == QueryKind.SurfaceProjection
            || query.Kind == QueryKind.Directed
            || query.Kind == QueryKind.AirTransfer;
    }

    private bool IsReachableSurfaceCandidateUnprofiled(
        SurfaceQuery query,
        HitCandidate candidate,
        bool requireStandableMoveProbe)
    {
        if (query.Kind == QueryKind.Vertical)
        {
            return true;
        }

        if (query.SourcePointId < 0)
        {
            return true;
        }

        if (query.SourcePointId >= points.Count)
        {
            return false;
        }

        SurfacePoint source = points[query.SourcePointId];
        if (query.Kind == QueryKind.GapLanding)
        {
            GapReachabilityCheckCount++;
            long gapStart = Stopwatch.GetTimestamp();
            bool reachable = IsReachableGapLandingCandidate(source, candidate);
            AddProfileTicks(ref profileGapReachabilityTicks, gapStart);
            if (!reachable)
            {
                GapLandingRejectedCount++;
            }

            return reachable;
        }

        Vector3 delta = candidate.Position - source.Position;
        float distance = delta.magnitude;
        float maxDistance = GetReachableCandidateDistanceLimit(query, source, candidate);
        if (distance > maxDistance)
        {
            ContinuityRejectedCount++;
            return false;
        }

        if (source.Kind == SurfaceKind.Standable && candidate.Kind == SurfaceKind.Standable)
        {
            float verticalDelta = candidate.Position.y - source.Position.y;
            if (verticalDelta > config.MaxWalkStepUpHeight + 0.05f
                || -verticalDelta > config.MaxWalkDropHeight + 0.1f)
            {
                ContinuityRejectedCount++;
                return false;
            }
        }

        if (source.ColliderId != 0
            && candidate.ColliderId != 0
            && source.ColliderId != candidate.ColliderId
            && source.Kind == SurfaceKind.Standable
            && candidate.Kind == SurfaceKind.Standable
            && Mathf.Abs(candidate.Position.y - source.Position.y) > config.MaxWalkStepUpHeight)
        {
            ContinuityRejectedCount++;
            return false;
        }

        bool isAirTransferSurfaceProbe = IsAirTransferSurfaceProbe(query, source, candidate);
        if (!HasClearAirPathBetween(source, candidate))
        {
            ContinuityRejectedCount++;
            return false;
        }

        if (isAirTransferSurfaceProbe)
        {
            return staminaModel.CanAffordClimbJump();
        }

        SupportCheckCount++;
        long supportStart = Stopwatch.GetTimestamp();
        bool hasSupport = HasSurfaceSupportBetween(source, candidate);
        AddProfileTicks(ref profileSupportTicks, supportStart);
        if (!hasSupport)
        {
            ContinuityRejectedCount++;
            return false;
        }

        if ((source.Kind == SurfaceKind.Climbable || candidate.Kind == SurfaceKind.Climbable)
            && !CanAffordClimbMove(source, candidate))
        {
            ContinuityRejectedCount++;
            return false;
        }

        if (source.Kind == SurfaceKind.Standable
            && candidate.Kind == SurfaceKind.Standable
            && requireStandableMoveProbe
            && !CanAcceptStandableMoveAfterSupport(source, candidate))
        {
            ContinuityRejectedCount++;
            ProbeMoveRejectedCount++;
            return false;
        }

        Vector3 sourceNormal = source.Normal.sqrMagnitude > 0.001f ? source.Normal.normalized : Vector3.up;
        Vector3 candidateNormal = candidate.Normal.sqrMagnitude > 0.001f ? candidate.Normal.normalized : Vector3.up;
        Vector3 from = source.Position + sourceNormal * SurfaceConnectionLift;
        Vector3 to = candidate.Position + candidateNormal * SurfaceConnectionLift;
        Vector3 direction = to - from;
        float castDistance = direction.magnitude;
        if (castDistance <= 0.001f)
        {
            return true;
        }

        if (!HasSurfaceMeshClearSegment(from, to, source.ColliderId, candidate.ColliderId))
        {
            ContinuityRejectedCount++;
            return false;
        }

        direction /= castDistance;
        ConnectionCastCheckCount++;
        long connectionStart = Stopwatch.GetTimestamp();
        int hitCount = Physics.SphereCastNonAlloc(
            from,
            SurfaceConnectionCastRadius,
            direction,
            ClearanceHitBuffer,
            castDistance,
            surfaceBlockerMask,
            QueryTriggerInteraction.Ignore);
        AddProfileTicks(ref profileConnectionCastTicks, connectionStart);
        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = ClearanceHitBuffer[index];
            Collider collider = hit.collider;
            if (collider == null || hit.distance <= 0.02f || hit.distance >= castDistance - 0.03f)
            {
                continue;
            }

            int colliderId = collider.GetInstanceID();
            if (colliderId == source.ColliderId || colliderId == candidate.ColliderId)
            {
                continue;
            }

            ContinuityRejectedCount++;
            return false;
        }

        return true;
    }

    private float GetReachableCandidateDistanceLimit(SurfaceQuery query, SurfacePoint source, HitCandidate candidate)
    {
        float defaultLimit = Mathf.Max(config.SurfaceNeighborDistance * 1.5f, corridorSpacing * 2.25f);
        if (query.Kind == QueryKind.Directed
            && source.Kind == SurfaceKind.Standable
            && candidate.Kind == SurfaceKind.Climbable)
        {
            return Mathf.Max(
                defaultLimit,
                StandWallProbeReach + StandWallProbeHeightOffsets[StandWallProbeHeightOffsets.Length - 1] + 0.25f);
        }

        if ((query.Kind == QueryKind.Directed || query.Kind == QueryKind.AirTransfer)
            && source.Kind == SurfaceKind.Climbable
            && candidate.Kind == SurfaceKind.Climbable
            && Vector3.Dot(
                query.Direction.normalized,
                source.Normal.sqrMagnitude > 0.001f ? source.Normal.normalized : Vector3.zero) >= MinimumAirTransferProbeNormalDot)
        {
            return Mathf.Max(defaultLimit, config.MaxAirTransferDistance);
        }

        return defaultLimit;
    }

    private static bool IsAirTransferSurfaceProbe(SurfaceQuery query, SurfacePoint source, HitCandidate candidate)
    {
        if ((query.Kind != QueryKind.Directed && query.Kind != QueryKind.AirTransfer)
            || source.Kind != SurfaceKind.Climbable
            || candidate.Kind != SurfaceKind.Climbable)
        {
            return false;
        }

        Vector3 sourceNormal = source.Normal.sqrMagnitude > 0.001f ? source.Normal.normalized : Vector3.zero;
        if (sourceNormal.sqrMagnitude <= 0.001f)
        {
            return false;
        }

        return Vector3.Dot(query.Direction.normalized, sourceNormal) >= MinimumAirTransferProbeNormalDot;
    }

    private bool CanAcceptStandableMoveAfterSupport(SurfacePoint source, HitCandidate candidate)
    {
        if (ShouldSkipStandableMoveProbe(source, candidate))
        {
            MoveProbeSkippedCount++;
            return true;
        }

        return CanProbeMoveStandable(source, candidate);
    }

    private bool ShouldSkipStandableMoveProbe(SurfacePoint source, HitCandidate candidate)
    {
        if (source.ColliderId == 0
            || candidate.ColliderId == 0
            || source.ColliderId != candidate.ColliderId)
        {
            return false;
        }

        Vector3 delta = candidate.Position - source.Position;
        float maxDistance = Mathf.Max(corridorSpacing * 1.25f, config.SurfaceNeighborDistance * 0.65f);
        if (delta.sqrMagnitude > maxDistance * maxDistance
            || Mathf.Abs(delta.y) > FlatStandWalkProbeSkipMaxVerticalDelta)
        {
            return false;
        }

        Vector3 sourceNormal = source.Normal.sqrMagnitude > 0.001f ? source.Normal.normalized : Vector3.up;
        Vector3 candidateNormal = candidate.Normal.sqrMagnitude > 0.001f ? candidate.Normal.normalized : Vector3.up;
        return Vector3.Angle(Vector3.up, sourceNormal) <= FlatStandWalkProbeSkipNormalAngle
            && Vector3.Angle(Vector3.up, candidateNormal) <= FlatStandWalkProbeSkipNormalAngle;
    }

    private bool CanProbeMoveStandable(SurfacePoint source, HitCandidate candidate)
    {
        MoveProbeCheckCount++;
        long profileStart = Stopwatch.GetTimestamp();
        try
        {
            return probeBody.CanMoveStandable(
                source.Position,
                candidate.Position,
                surfaceBlockerMask,
                source.ColliderId,
                candidate.ColliderId);
        }
        finally
        {
            AddProfileTicks(ref profileMoveProbeTicks, profileStart);
        }
    }

    private bool IsReachableGapLandingCandidate(SurfacePoint source, HitCandidate candidate)
    {
        if (source.Kind != SurfaceKind.Standable || candidate.Kind != SurfaceKind.Standable)
        {
            return false;
        }

        float horizontalDistance = Vector2.Distance(
            new Vector2(source.Position.x, source.Position.z),
            new Vector2(candidate.Position.x, candidate.Position.z));
        float verticalDelta = candidate.Position.y - source.Position.y;
        if (horizontalDistance > config.MaxStandJumpDistance
            || verticalDelta > config.MaxStandJumpUpHeight
            || -verticalDelta > config.MaxStandJumpDropHeight)
        {
            return false;
        }

        bool sprintJump = horizontalDistance > config.NormalStandJumpDistance + 0.05f;
        if (!staminaModel.CanAffordJump(sprintJump))
        {
            return false;
        }

        if (!HasClearAirPathBetween(source, candidate))
        {
            return false;
        }

        Vector3 from = source.Position + Vector3.up * GapConnectionLift;
        Vector3 to = candidate.Position + Vector3.up * GapConnectionLift;
        Vector3 direction = to - from;
        float castDistance = direction.magnitude;
        if (castDistance <= 0.001f)
        {
            return true;
        }

        if (!HasSurfaceMeshClearSegment(from, to, source.ColliderId, candidate.ColliderId))
        {
            return false;
        }

        direction /= castDistance;
        int hitCount = Physics.SphereCastNonAlloc(
            from,
            GapConnectionCastRadius,
            direction,
            ClearanceHitBuffer,
            castDistance,
            surfaceBlockerMask,
            QueryTriggerInteraction.Ignore);
        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = ClearanceHitBuffer[index];
            Collider collider = hit.collider;
            if (collider == null || hit.distance <= 0.05f || hit.distance >= castDistance - 0.05f)
            {
                continue;
            }

            int colliderId = collider.GetInstanceID();
            if (colliderId == source.ColliderId || colliderId == candidate.ColliderId)
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private bool CanAffordClimbMove(SurfacePoint source, HitCandidate candidate)
    {
        float climbDistance = Vector3.Distance(source.Position, candidate.Position);
        return staminaModel.CanAffordClimb(climbDistance);
    }

    private bool HasSurfaceSupportBetween(SurfacePoint source, HitCandidate candidate)
    {
        if (source.Kind == SurfaceKind.Standable && candidate.Kind == SurfaceKind.Climbable)
        {
            return true;
        }

        if (source.Kind == SurfaceKind.Climbable || candidate.Kind == SurfaceKind.Climbable)
        {
            return HasClimbSurfaceContinuity(source, candidate);
        }

        if (source.Kind != SurfaceKind.Standable || candidate.Kind != SurfaceKind.Standable)
        {
            return true;
        }

        Vector3 delta = candidate.Position - source.Position;
        float distance = delta.magnitude;
        int steps = Mathf.Clamp(
            Mathf.CeilToInt(distance / SurfaceSupportSampleSpacing),
            1,
            4);
        for (int step = 1; step < steps; step++)
        {
            float t = step / (float)steps;
            Vector3 sample = Vector3.Lerp(source.Position, candidate.Position, t);
            Vector3 origin = sample + Vector3.up * Mathf.Max(0.35f, SurfaceConnectionLift * 2f);
            float rayDistance = Mathf.Max(0.75f, config.MaxWalkStepUpHeight + config.MaxWalkDropHeight + 0.35f);
            int hitCount = Physics.RaycastNonAlloc(
                origin,
                Vector3.down,
                    TerrainHitBuffer,
                    rayDistance,
                    surfaceBlockerMask,
                    QueryTriggerInteraction.Ignore);
            if (!TrySelectSupportHit(
                    hitCount,
                    sample.y,
                    source.ColliderId,
                    candidate.ColliderId,
                    out HitCandidate support))
            {
                return false;
            }

            if (Mathf.Abs(support.Position.y - sample.y) > SurfaceSupportHeightTolerance)
            {
                return false;
            }
        }

        return true;
    }

    private bool HasClimbSurfaceContinuity(SurfacePoint source, HitCandidate candidate)
    {
        Vector3 sourceNormal = source.Normal.sqrMagnitude > 0.001f ? source.Normal.normalized : Vector3.up;
        Vector3 candidateNormal = candidate.Normal.sqrMagnitude > 0.001f ? candidate.Normal.normalized : sourceNormal;
        Vector3 averageNormal = (sourceNormal + candidateNormal).normalized;
        if (averageNormal.sqrMagnitude < 0.001f)
        {
            averageNormal = sourceNormal;
        }

        Vector3 delta = candidate.Position - source.Position;
        float distance = delta.magnitude;
        int steps = Mathf.Clamp(
            Mathf.CeilToInt(distance / SurfaceSupportSampleSpacing),
            1,
            4);
        for (int step = 1; step < steps; step++)
        {
            float t = step / (float)steps;
            Vector3 sample = Vector3.Lerp(source.Position, candidate.Position, t);
            Vector3 origin = sample + averageNormal * ClimbSurfaceProbeOffset;
            int hitCount = Physics.RaycastNonAlloc(
                origin,
                -averageNormal,
                    TerrainHitBuffer,
                    ClimbSurfaceProbeOffset + ClimbSurfaceProbeDistance,
                    surfaceBlockerMask,
                    QueryTriggerInteraction.Ignore);
            if (!TrySelectSupportHit(
                    hitCount,
                    sample.y,
                    source.ColliderId,
                    candidate.ColliderId,
                    out HitCandidate support))
            {
                return false;
            }

            if (Vector3.Angle(support.Normal, averageNormal) > 65f
                || Mathf.Abs(support.Position.y - sample.y) > Mathf.Max(config.SurfaceNeighborDistance, SurfaceSupportHeightTolerance))
            {
                return false;
            }
        }

        return true;
    }

    private bool TrySelectSupportHit(
        int hitCount,
        float expectedY,
        int sourceColliderId,
        int candidateColliderId,
        out HitCandidate support)
    {
        support = default;
        bool found = false;
        float bestScore = float.MaxValue;
        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = TerrainHitBuffer[index];
            Collider collider = hit.collider;
            if (collider == null)
            {
                continue;
            }

            int colliderId = collider.GetInstanceID();
            if (sourceColliderId != 0
                && candidateColliderId != 0
                && sourceColliderId == candidateColliderId
                && colliderId != sourceColliderId)
            {
                continue;
            }

            SurfaceKind kind = ClassifySurfaceHit(hit);
            if (kind == SurfaceKind.Blocked)
            {
                continue;
            }

            HitCandidate candidate = new(hit.point, hit.normal, colliderId, kind, hit.distance);
            float score = Mathf.Abs(hit.point.y - expectedY) + hit.distance * 0.1f;
            if (found && score >= bestScore)
            {
                continue;
            }

            support = candidate;
            bestScore = score;
            found = true;
        }

        return found;
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
        StandableClearanceKey key = StandableClearanceKey.From(candidate, StandableClearanceCacheCellSize);
        if (standableClearanceByCell.TryGetValue(key, out bool cached))
        {
            StandableClearanceCacheHitCount++;
            return cached;
        }

        StandableClearanceCheckCount++;
        bool hasClearance = HasStandableClearanceUncached(candidate);
        standableClearanceByCell[key] = hasClearance;
        return hasClearance;
    }

    private bool HasStandableClearanceUncached(HitCandidate candidate)
    {
        ClearanceProbeCheckCount++;
        long clearanceStart = Stopwatch.GetTimestamp();
        try
        {
            long headroomStart = Stopwatch.GetTimestamp();
            bool hasHeadroom = HasStandableHeadroom(candidate);
            AddProfileTicks(ref profileHeadroomTicks, headroomStart);
            if (!hasHeadroom)
            {
                return false;
            }

            return true;
        }
        finally
        {
            AddProfileTicks(ref profileClearanceTicks, clearanceStart);
        }
    }

    internal void Dispose()
    {
        surfaceAirFieldsByWindow.Clear();
        debugAirCellCenters.Clear();
        probeBody.Dispose();
    }

    private static void AddProfileTicks(ref long target, long startTimestamp)
    {
        target += Stopwatch.GetTimestamp() - startTimestamp;
    }

    private static double TicksToMilliseconds(long ticks)
    {
        return ticks * 1000d / Stopwatch.Frequency;
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
            surfaceBlockerMask,
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

        if (points.Count >= config.MaxSurfacePointsPerAttempt)
        {
            HitPointLimit = true;
            return -1;
        }

        int id = points.Count;
        pointIdsByKey[key] = id;
        points.Add(new SurfacePoint(id, position, normal.normalized, colliderId, kind));
        if (enforceWindowPointLimit
            && points.Count - CachedPointCountAtAttemptStart >= config.MaxSurfacePointsPerWindow)
        {
            HitWindowPointLimit = true;
        }

        return id;
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
        if (!IsInsideActiveSampleWindow(position))
        {
            return;
        }

        Vector2 center = new(position.x, position.z);
        if (DoesWindowOverlapExisting(center, sideIndex))
        {
            return;
        }

        if (!IsAllowedByGuideCorridor(position))
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

    private void RequeueCachedFrontierSeeds(bool includeTargetFrontier)
    {
        for (int index = 0; index < points.Count; index++)
        {
            FrontierSide side = includeTargetFrontier
                ? GetPreferredSide(points[index].Position)
                : FrontierSide.Start;
            EnqueueSeed(index, side);
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
            routeExpandedWindowCentersBySide[(int)seed.Side].Add(seed.Center);
            Vector2 center = seed.Center;
            HasActiveSeedPreview = true;
            ActiveSeedPreviewPosition = points[seed.PointId].Position;
            SurfacePoint surfaceSeed = points[seed.PointId];
            Vector3 scanDirection = GetWindowScanDirection(surfaceSeed.Position);
            SetActiveSampleWindowPreview(center, surfaceSeed.Position.y, scanDirection);
            PrepareActiveWindowColliders(center, surfaceSeed);
            activeWindowCenter = center;
            activeWindowScanDirection = scanDirection;
            activeWindowSurfaceY = surfaceSeed.Position.y;
            activeWindowGapProbeCount = 0;
            activeWindowDropDiscoveryProbeCount = 0;
            activeWindowAirBoundaryProbeCount = 0;
            if (activeSurfaceAirBuilder != null)
            {
                return true;
            }

            QueueAirBoundarySurfaceProbes();
            if (activeSurfaceAirField != null)
            {
                QueueDropDiscoveryProbes(surfaceSeed);
            }
            else
            {
                QueueWallSurfaceProbes(surfaceSeed);
                QueueNeighborSurfaceProbes(seed.PointId);
            }

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
            || DoesWindowOverlapAny(center, expandedWindowCentersBySide[sideIndex])
            || IsNearRouteExpandedWindow(center, routeExpandedWindowCentersBySide[sideIndex]);
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

    private bool IsNearRouteExpandedWindow(Vector2 center, IReadOnlyList<Vector2> existingCenters)
    {
        float repeatStep = Mathf.Max(
            config.MinimumPartialSegmentDistance * 2f,
            samplingWindowRadius * 0.75f);
        for (int index = 0; index < existingCenters.Count; index++)
        {
            Vector2 existing = existingCenters[index];
            if (Mathf.Abs(existing.x - center.x) < repeatStep
                && Mathf.Abs(existing.y - center.y) < repeatStep)
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

    private void SetActiveSampleWindowPreview(Vector2 center, float preferredSurfaceY, Vector3 scanDirection)
    {
        activeWindowScanDirection = GetSafeHorizontalDirection(scanDirection);
        activeWindowForwardHalfExtent = sampleFullWindowBySlices
            ? samplingWindowRadius
            : samplingSliceForwardHalfExtent;
        ActiveSampleWindowCenter = new Vector3(
            center.x,
            preferredSurfaceY,
            center.y);
        ActiveSampleWindowSize = new Vector3(
            samplingWindowRadius * 2f,
            samplingWindowVerticalExtent,
            activeWindowForwardHalfExtent * 2f);
        ActiveSampleWindowRotation = Quaternion.LookRotation(activeWindowScanDirection, Vector3.up);
    }

    private void PrepareActiveWindowColliders(Vector2 center, SurfacePoint seed)
    {
        long profileStart = Stopwatch.GetTimestamp();
        try
        {
            PrepareActiveWindowCollidersUnprofiled(center, seed);
        }
        finally
        {
            AddProfileTicks(ref profileBroadphaseTicks, profileStart);
        }
    }

    private void PrepareActiveWindowCollidersUnprofiled(Vector2 center, SurfacePoint seed)
    {
        activeWindowColliders.Clear();
        activeWindowColliderIds.Clear();
        activeSurfaceAirField = null;
        activeSurfaceAirBuilder = null;
        activeWindowUsesGlobalRaycast = false;

        float broadphaseProbeMargin = Mathf.Max(StandWallProbeReach, ClimbSurfaceProbeOffset + ClimbSurfaceProbeDistance)
            + Mathf.Max(0.5f, config.SurfaceNeighborDistance);
        Vector2 broadphaseHorizontalExtents = GetActiveWindowHorizontalAabbHalfExtents(broadphaseProbeMargin);
        Vector3 boxCenter = new(
            center.x,
            seed.Position.y,
            center.y);
        Vector3 halfExtents = new(
            broadphaseHorizontalExtents.x,
            samplingWindowVerticalHalfExtent + 0.5f,
            broadphaseHorizontalExtents.y);
        int colliderCount = Physics.OverlapBoxNonAlloc(
            boxCenter,
            halfExtents,
            WindowColliderBuffer,
            Quaternion.identity,
            surfaceBlockerMask,
            QueryTriggerInteraction.Ignore);
        BroadphaseWindowCount++;
        BroadphaseColliderCount += colliderCount;
        BroadphaseMaxColliderCount = Mathf.Max(BroadphaseMaxColliderCount, colliderCount);

        if (colliderCount >= WindowColliderBuffer.Length)
        {
            activeWindowUsesGlobalRaycast = true;
            BroadphaseOverflowCount++;
            return;
        }

        Vector3 airBoxCenter = new(center.x, seed.Position.y, center.y);
        Vector2 airHorizontalExtents = GetActiveWindowHorizontalAabbHalfExtents(
            SurfaceAirFieldCellSize + SurfaceAirProbeRadius);
        Vector3 airHalfExtents = new(
            airHorizontalExtents.x,
            samplingWindowVerticalHalfExtent + 0.5f,
            airHorizontalExtents.y);
        AirWindowKey airWindowKey = AirWindowKey.From(
            airBoxCenter,
            activeWindowScanDirection,
            activeWindowForwardHalfExtent,
            samplingWindowRadius,
            SurfaceAirFieldCellSize);
        if (surfaceAirFieldsByWindow.TryGetValue(airWindowKey, out SurfaceAirField cachedAirField))
        {
            activeSurfaceAirField = cachedAirField;
            activeSurfaceAirBuilder = null;
            SurfaceAirFieldCacheHitCount++;
        }
        else
        {
            BeginActiveSurfaceAirFieldBuild(airBoxCenter, airHalfExtents, seed, airWindowKey);
        }

        BuildActiveSurfaceMeshField(WindowColliderBuffer, colliderCount, boxCenter, halfExtents);

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
                BroadphaseOverflowCount++;
                return;
            }
        }

        if (activeWindowColliders.Count > MaxEfficientColliderRaycastColliders)
        {
            activeWindowColliders.Clear();
            activeWindowColliderIds.Clear();
            activeWindowUsesGlobalRaycast = true;
            BroadphaseGlobalByColliderCount++;
        }
    }

    private Vector2 GetActiveWindowHorizontalAabbHalfExtents(float margin)
    {
        Vector3 forward = activeWindowScanDirection;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f)
        {
            forward = Vector3.forward;
        }

        forward.Normalize();
        Vector2 forward2 = new(forward.x, forward.z);
        Vector2 lateral2 = new(-forward2.y, forward2.x);
        float forwardRadius = Mathf.Max(0.001f, activeWindowForwardHalfExtent);
        float lateralRadius = Mathf.Max(0.001f, samplingWindowRadius);
        float x = Mathf.Sqrt(
            forward2.x * forward2.x * forwardRadius * forwardRadius
            + lateral2.x * lateral2.x * lateralRadius * lateralRadius);
        float z = Mathf.Sqrt(
            forward2.y * forward2.y * forwardRadius * forwardRadius
            + lateral2.y * lateral2.y * lateralRadius * lateralRadius);
        float safeMargin = Mathf.Max(0f, margin);
        return new Vector2(x + safeMargin, z + safeMargin);
    }

    private void BeginActiveSurfaceAirFieldBuild(
        Vector3 boxCenter,
        Vector3 halfExtents,
        SurfacePoint seed,
        AirWindowKey airWindowKey)
    {
        activeSurfaceAirField = null;
        activeSurfaceAirBuildSeed = seed;
        activeSurfaceAirBuildKey = airWindowKey;
        activeSurfaceAirBuilder = SurfaceAirField.BeginBuild(
            boxCenter,
            halfExtents,
            seed.Position,
            seed.Normal,
            activeWindowScanDirection,
            sampleFullWindowBySlices ? samplingSliceForwardHalfExtent : activeWindowForwardHalfExtent,
            samplingWindowRadius,
            samplingWindowVerticalHalfExtent,
            SurfaceAirFieldCellSize,
            SurfaceAirProbeRadius,
            surfaceBlockerMask,
            MaxSurfaceAirReachableCells,
            activeWindowForwardHalfExtent,
            routeSurfaceAirCache);
    }

    private void FinalizeActiveSurfaceAirField(SurfaceAirField.Builder builder, SurfacePoint seed)
    {
        SurfaceAirField airField = builder.ToField();
        SurfaceAirCheckedCellCount += airField.CheckedCellCount;
        SurfaceAirBlockedCellCount += airField.BlockedCellCount;
        SurfaceAirBlockedTransitionCount += airField.BlockedTransitionCount;
        SurfaceAirClearCellCacheHitCount += airField.ClearCellCacheHitCount;
        SurfaceAirClearTransitionCacheHitCount += airField.ClearTransitionCacheHitCount;
        if (airField.Overflowed)
        {
            SurfaceAirOverflowCount++;
        }

        if (!airField.FoundStart)
        {
            SurfaceAirBuildFailedCount++;
            QueueWallSurfaceProbes(seed);
            QueueNeighborSurfaceProbes(seed.Id);
            return;
        }

        activeSurfaceAirField = airField;
        surfaceAirFieldsByWindow[activeSurfaceAirBuildKey] = airField;
        SurfaceAirFieldWindowCount++;
        SurfaceAirReachableCellCount += airField.ReachableCellCount;
        SurfaceAirBoundaryCellCount += airField.BoundaryCellCount;
        SurfaceAirBoundaryProbeSourceCount += airField.BoundaryProbeCount;
        SurfaceAirSliceAdvanceCount += airField.SliceAdvanceCount;
        SurfaceAirMaxReachableCellCount = Mathf.Max(SurfaceAirMaxReachableCellCount, airField.ReachableCellCount);
        airField.CopyReachableCellCenters(
            debugAirCellCenters,
            Mathf.Max(0, MaxDebugAirCellCenters - debugAirCellCenters.Count));
        QueueAirBoundarySurfaceProbes();
    }

    private void QueueAirBoundarySurfaceProbes()
    {
        SurfaceAirField? airField = activeSurfaceAirField;
        if (airField == null || !airField.FoundStart)
        {
            return;
        }

        Queue<SurfaceAirField.AirBoundaryProbe> probes = [];
        int probeCount = airField.QueueBoundaryProbes(probes, GetRemainingAirBoundaryProbeBudget(MaxSurfaceAirBoundaryProbesPerWindow));
        QueueAirBoundarySurfaceProbes(probes, probeCount);
    }

    private void QueueAirBoundarySurfaceProbes(SurfaceAirField.Builder builder)
    {
        Queue<SurfaceAirField.AirBoundaryProbe> probes = [];
        int probeCount = builder.DrainBoundaryProbes(probes, GetRemainingAirBoundaryProbeBudget(MaxSurfaceAirBoundaryProbesPerWindow));
        QueueAirBoundarySurfaceProbes(probes, probeCount);
    }

    private void QueueAirBoundarySurfaceProbes(
        Queue<SurfaceAirField.AirBoundaryProbe> probes,
        int probeCount)
    {
        if (probeCount <= 0 || GetRemainingAirBoundaryProbeBudget(int.MaxValue) <= 0)
        {
            return;
        }

        if (prioritizeGuidedSampling && probes.Count > 1)
        {
            List<SurfaceAirField.AirBoundaryProbe> orderedProbes = [];
            while (probes.Count > 0)
            {
                orderedProbes.Add(probes.Dequeue());
            }

            orderedProbes.Sort((a, b) => GetAirBoundaryProbeScore(a).CompareTo(GetAirBoundaryProbeScore(b)));
            for (int index = 0; index < orderedProbes.Count; index++)
            {
                TryQueueAirBoundaryProbe(orderedProbes[index]);
            }

            return;
        }

        while (probes.Count > 0)
        {
            SurfaceAirField.AirBoundaryProbe probe = probes.Dequeue();
            TryQueueAirBoundaryProbe(probe);
        }

    }

    private void TryQueueAirBoundaryProbe(SurfaceAirField.AirBoundaryProbe probe)
    {
        if (GetRemainingAirBoundaryProbeBudget(int.MaxValue) <= 0)
        {
            return;
        }

        Vector3 target = probe.Origin + probe.Direction * probe.Distance;
        if (!IsInsideActiveSampleWindow(target))
        {
            SurfaceAirBoundaryProbeSkippedWindowCount++;
            return;
        }

        QueryKey queryKey = ToAirBoundaryProbeKey(probe.Origin, probe.Direction);
        if (processedRayKeys.Contains(queryKey) || !pendingRayKeys.Add(queryKey))
        {
            return;
        }

        pendingRayOrigins.Enqueue(SurfaceQuery.AirBoundary(
            queryKey,
            probe.Origin,
            probe.Direction,
            probe.Distance,
            probe.Origin.y));
        activeWindowAirBoundaryProbeCount++;
        SurfaceAirBoundaryProbeQueuedCount++;
        QueuedProbeCount++;
    }

    private int GetRemainingAirBoundaryProbeBudget(int requested)
    {
        if (!prioritizeGuidedSampling)
        {
            return Mathf.Max(0, requested);
        }

        int maxBudget = prioritizeGuidedSampling
            ? MaxGuidedSurfaceAirBoundaryProbesPerWindow
            : MaxSurfaceAirBoundaryProbesPerWindow;
        return Mathf.Max(0, Mathf.Min(requested, maxBudget - activeWindowAirBoundaryProbeCount));
    }

    private float GetAirBoundaryProbeScore(SurfaceAirField.AirBoundaryProbe probe)
    {
        Vector3 target = probe.Origin + probe.Direction * probe.Distance;
        Vector3 center = new(activeWindowCenter.x, target.y, activeWindowCenter.y);
        Vector3 delta = target - center;
        float forward = Vector3.Dot(new Vector3(delta.x, 0f, delta.z), activeWindowScanDirection);
        float lateral = Mathf.Abs(delta.x * activeWindowScanDirection.z - delta.z * activeWindowScanDirection.x);
        float vertical = Mathf.Abs(target.y - activeWindowSurfaceY);
        float downProbeReward = probe.Direction.y < -0.5f ? 3f : 0f;
        float forwardReward = Mathf.Max(0f, forward) * 0.75f;
        return lateral * 0.45f
            + vertical * 0.2f
            - downProbeReward
            - forwardReward
            + Mathf.Max(0f, -forward) * 0.5f;
    }

    private int GetMaxGapProbesPerActiveWindow()
    {
        return prioritizeGuidedSampling ? MaxGuidedGapProbesPerWindow : MaxGapProbesPerWindow;
    }

    private int GetMaxDropDiscoveryProbesPerActiveWindow()
    {
        return prioritizeGuidedSampling ? MaxGuidedDropDiscoveryProbesPerWindow : MaxDropDiscoveryProbesPerWindow;
    }

    private void BuildActiveSurfaceMeshField(
        Collider[] colliders,
        int colliderCount,
        Vector3 boxCenter,
        Vector3 halfExtents)
    {
        activeSurfaceMeshField = null;
        if (colliderCount <= 0)
        {
            return;
        }

        long snapshotStart = Stopwatch.GetTimestamp();
        SurfaceMeshSnapshot snapshot = SurfaceMeshSnapshot.Capture(
            colliders,
            colliderCount,
            boxCenter,
            halfExtents,
            MaxSurfaceMeshSnapshotTriangles);
        AddProfileTicks(ref profileMeshSnapshotTicks, snapshotStart);

        SurfaceMeshColliderCount += snapshot.MeshColliderCount;
        SurfaceMeshSkippedColliderCount += snapshot.SkippedColliderCount;
        SurfaceMeshSkippedTriangleCount += snapshot.SkippedTriangleCount;
        if (snapshot.Triangles.Count == 0)
        {
            return;
        }

        long buildStart = Stopwatch.GetTimestamp();
        SurfaceMeshField meshField = SurfaceMeshField.Build(snapshot, SurfaceMeshFieldCellSize);
        AddProfileTicks(ref profileMeshBuildTicks, buildStart);

        activeSurfaceMeshField = meshField;
        SurfaceMeshFieldWindowCount++;
        SurfaceMeshTriangleCount += meshField.TriangleCount;
        SurfaceMeshCellCount += meshField.CellCount;
        SurfaceMeshMaxTriangleCount = Mathf.Max(SurfaceMeshMaxTriangleCount, meshField.TriangleCount);
        SurfaceMeshMaxCellCount = Mathf.Max(SurfaceMeshMaxCellCount, meshField.CellCount);
    }

    private bool CanExpandSeed(FrontierSeed seed)
    {
        Vector3 position = points[seed.PointId].Position;
        return IsInsideActiveSampleWindow(position)
            && IsAllowedByGuideCorridor(position);
    }

    private void QueueStandableNeighborProbes(SurfacePoint seed)
    {
        Vector3 seedNormal = seed.Normal.normalized;
        if (seedNormal.sqrMagnitude < 0.001f)
        {
            seedNormal = Vector3.up;
        }

        float step = Mathf.Clamp(corridorSpacing, 0.25f, 0.5f);
        BuildOrderedHorizontalProbeDirections(
            seed.Position,
            maxCount: constrainToGuideCorridor ? MaxFocusedStandableNeighborDirections : 0);
        for (int directionOrder = 0; directionOrder < orderedProbeDirections.Count; directionOrder++)
        {
            ProbeDirection probeDirection = orderedProbeDirections[directionOrder];
            Vector2 direction2 = probeDirection.Direction;
            Vector3 horizontalOffset = new(direction2.x * step, 0f, direction2.y * step);
            Vector3 tangentOffset = Vector3.ProjectOnPlane(horizontalOffset, seedNormal);
            if (tangentOffset.sqrMagnitude < 0.001f)
            {
                tangentOffset = horizontalOffset;
            }

            Vector3 projectedSurfaceEstimate = seed.Position + tangentOffset;
            if (!IsInsideActiveSampleWindow(projectedSurfaceEstimate) || !IsAllowedByGuideCorridor(projectedSurfaceEstimate))
            {
                continue;
            }

            QueryKey queryKey = ToSurfaceProjectionKey(projectedSurfaceEstimate);
            if (processedRayKeys.Contains(queryKey) || !pendingRayKeys.Add(queryKey))
            {
                continue;
            }

            pendingRayOrigins.Enqueue(SurfaceQuery.SurfaceProjection(
                queryKey,
                projectedSurfaceEstimate + seedNormal * NeighborSurfaceProbeOffset,
                -seedNormal,
                NeighborSurfaceProbeOffset + localLayerSearchDownExtent,
                projectedSurfaceEstimate.y,
                seed.Id,
                probeDirection.Index,
                tangentOffset.normalized));
            QueuedProbeCount++;
        }
    }

    private void QueueGapProbesFromFailedBoundary(SurfaceQuery failedQuery)
    {
        if (failedQuery.Kind != QueryKind.SurfaceProjection
            || failedQuery.SourcePointId < 0
            || failedQuery.SourcePointId >= points.Count
            || activeWindowGapProbeCount >= GetMaxGapProbesPerActiveWindow())
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
        GapDirectionKey gapDirectionKey = GapDirectionKey.From(activeWindowCenter, direction);
        if (!queuedGapProbeDirections.Add(gapDirectionKey))
        {
            return;
        }

        GuideProjectionPoint sourceProjection = guideProjection.Project(source.Position);
        GuideProjectionPoint probeProjection = guideProjection.Project(source.Position + direction * config.MaxStandJumpDistance);
        if (constrainToGuideCorridor
            && probeProjection.Progress - sourceProjection.Progress < config.AdaptiveGuideMinimumStep * 0.5f)
        {
            return;
        }

        float minDistance = Mathf.Max(config.SurfaceNeighborDistance * 1.5f, corridorSpacing * 2f);
        float maxDistance = Mathf.Max(minDistance, config.MaxStandJumpDistance);
        for (int index = 0; index < GapProbeDistanceMultipliers.Length; index++)
        {
            if (activeWindowGapProbeCount >= GetMaxGapProbesPerActiveWindow())
            {
                return;
            }

            float distance = Mathf.Max(minDistance, maxDistance * GapProbeDistanceMultipliers[index]);
            Vector3 landingEstimate = source.Position + direction * distance;
            if (!IsInsideActiveSampleWindow(landingEstimate) || !IsAllowedByGuideCorridor(landingEstimate))
            {
                continue;
            }

            QueryKey queryKey = ToGapProbeKey(landingEstimate);
            if (processedRayKeys.Contains(queryKey) || !pendingRayKeys.Add(queryKey))
            {
                continue;
            }

            float upExtent = Mathf.Max(config.MaxStandJumpUpHeight + GapProbeUpPadding, localLayerSearchUpExtent);
            float downExtent = Mathf.Max(config.MaxStandJumpDropHeight + GapProbeDownPadding, localLayerSearchDownExtent);
            pendingRayOrigins.Enqueue(SurfaceQuery.GapLanding(
                queryKey,
                new Vector3(landingEstimate.x, source.Position.y + upExtent, landingEstimate.z),
                upExtent + downExtent,
                landingEstimate.y,
                source.Id));
            activeWindowGapProbeCount++;
            GapProbeCount++;
            QueuedProbeCount++;
        }
    }

    private void QueueDropDiscoveryProbes(SurfacePoint seed)
    {
        if (activeWindowDropDiscoveryProbeCount >= GetMaxDropDiscoveryProbesPerActiveWindow())
        {
            return;
        }

        BuildOrderedHorizontalProbeDirections(
            seed.Position,
            maxCount: constrainToGuideCorridor ? MaxFocusedStandableNeighborDirections : 0);
        float[] distances =
        [
            Mathf.Max(config.SurfaceNeighborDistance * 1.5f, corridorSpacing * 2f),
            Mathf.Max(config.MaxStandJumpDistance * 0.75f, samplingWindowRadius * 0.35f),
            Mathf.Max(config.MaxStandJumpDistance, samplingWindowRadius * 0.65f),
            samplingWindowRadius * 0.9f,
        ];
        float upExtent = Mathf.Max(localLayerSearchUpExtent, SurfaceStandClearanceHeight + GapProbeUpPadding);
        float downExtent = samplingWindowVerticalHalfExtent + GapProbeDownPadding;
        for (int directionOrder = 0; directionOrder < orderedProbeDirections.Count; directionOrder++)
        {
            ProbeDirection probeDirection = orderedProbeDirections[directionOrder];
            Vector2 direction2 = probeDirection.Direction;
            Vector3 direction = new(direction2.x, 0f, direction2.y);
            if (direction.sqrMagnitude < 0.001f)
            {
                continue;
            }

            direction.Normalize();
            for (int distanceIndex = 0; distanceIndex < distances.Length; distanceIndex++)
            {
                if (activeWindowDropDiscoveryProbeCount >= GetMaxDropDiscoveryProbesPerActiveWindow())
                {
                    return;
                }

                Vector3 estimate = seed.Position + direction * distances[distanceIndex];
                if (!IsInsideActiveSampleWindow(estimate) || !IsAllowedByGuideCorridor(estimate))
                {
                    continue;
                }

                QueryKey queryKey = ToDropDiscoveryProbeKey(estimate, probeDirection.Index, distanceIndex);
                if (processedRayKeys.Contains(queryKey) || !pendingRayKeys.Add(queryKey))
                {
                    continue;
                }

                pendingRayOrigins.Enqueue(SurfaceQuery.DropDiscovery(
                    queryKey,
                    new Vector3(estimate.x, seed.Position.y + upExtent, estimate.z),
                    upExtent + downExtent,
                    estimate.y,
                    seed.Id));
                activeWindowDropDiscoveryProbeCount++;
                DropDiscoveryProbeCount++;
                QueuedProbeCount++;
            }
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
            QueueClimbableSurfaceProbesThrottled(seed);
        }
    }

    private void QueueClimbableSurfaceProbesThrottled(SurfacePoint seed)
    {
        ClimbProbeCellKey key = ClimbProbeCellKey.From(seed.Position, ClimbProbeThrottleCellSize);
        if (!queuedClimbableProbeCells.Add(key))
        {
            return;
        }

        QueueClimbableSurfaceProbes(seed);
    }

    private void QueueStandableWallProbes(SurfacePoint seed)
    {
        float reach = Mathf.Max(0.75f, Mathf.Min(StandWallProbeReach, config.SurfaceNeighborDistance + 0.6f));
        BuildOrderedHorizontalProbeDirections(
            seed.Position,
            maxCount: constrainToGuideCorridor ? MaxFocusedStandableWallProbeDirections : 0);
        for (int heightIndex = 0; heightIndex < StandWallProbeHeightOffsets.Length; heightIndex++)
        {
            float yOffset = StandWallProbeHeightOffsets[heightIndex];
            Vector3 origin = seed.Position + Vector3.up * yOffset;
            for (int directionOrder = 0; directionOrder < orderedProbeDirections.Count; directionOrder++)
            {
                ProbeDirection probeDirection = orderedProbeDirections[directionOrder];
                Vector2 direction2 = probeDirection.Direction;
                Vector3 direction = new(direction2.x, 0f, direction2.y);
                Vector3 target = origin + direction * reach;
                if (!IsInsideActiveSampleWindow(target))
                {
                    continue;
                }

                QueryKey queryKey = ToProbeKey(origin, probeDirection.Index, heightIndex);
                if (processedRayKeys.Contains(queryKey) || !pendingRayKeys.Add(queryKey))
                {
                    continue;
                }

                pendingRayOrigins.Enqueue(SurfaceQuery.Directed(queryKey, origin, direction, reach, origin.y, seed.Id));
                QueuedProbeCount++;
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
        QueueClimbSurfaceProbe(seed.Id, seed.Position, normal, Vector3.up * step, 100);
        QueueClimbSurfaceProbe(seed.Id, seed.Position, normal, Vector3.down * step, 101);
        QueueClimbSurfaceProbe(seed.Id, seed.Position, normal, tangent * step, 102);
        QueueClimbSurfaceProbe(seed.Id, seed.Position, normal, -tangent * step, 103);
        QueueClimbSurfaceProbe(seed.Id, seed.Position, normal, (Vector3.up + tangent).normalized * step, 104);
        QueueClimbSurfaceProbe(seed.Id, seed.Position, normal, (Vector3.up - tangent).normalized * step, 105);
        QueueClimbAirTransferProbe(seed, normal);
    }

    private void QueueClimbSurfaceProbe(int sourcePointId, Vector3 surfacePosition, Vector3 normal, Vector3 tangentOffset, int directionIndex)
    {
        Vector3 projectedSurfaceEstimate = surfacePosition + tangentOffset;
        if (!IsInsideActiveSampleWindow(projectedSurfaceEstimate) || !IsAllowedByGuideCorridor(projectedSurfaceEstimate))
        {
            return;
        }

        Vector3 origin = surfacePosition + tangentOffset + normal * ClimbSurfaceProbeOffset;
        QueryKey queryKey = ToProbeKey(origin, directionIndex, ToLayerKey(origin.y));
        if (processedRayKeys.Contains(queryKey) || !pendingRayKeys.Add(queryKey))
        {
            return;
        }

        pendingRayOrigins.Enqueue(SurfaceQuery.Directed(
            queryKey,
            origin,
            -normal,
            ClimbSurfaceProbeDistance,
            surfacePosition.y + tangentOffset.y,
            sourcePointId));
        QueuedProbeCount++;
    }

    private void QueueClimbAirTransferProbe(SurfacePoint seed, Vector3 normal)
    {
        if (activeWindowGapProbeCount >= GetMaxGapProbesPerActiveWindow())
        {
            return;
        }

        Vector3 safeNormal = normal.sqrMagnitude > 0.001f ? normal.normalized : Vector3.zero;
        if (safeNormal.sqrMagnitude <= 0.001f)
        {
            return;
        }

        float distance = Mathf.Max(
            config.SurfaceNeighborDistance * 2f,
            Mathf.Min(config.MaxStandJumpDistance, config.MaxAirTransferDistance * 1.5f));
        Vector3 targetEstimate = seed.Position + safeNormal * distance;
        if (!IsInsideActiveSampleWindow(targetEstimate) || !IsAllowedByGuideCorridor(targetEstimate))
        {
            return;
        }

        Vector3 origin = seed.Position + safeNormal * ClimbAirTransferProbeOffset;
        QueryKey queryKey = ToProbeKey(origin, 200, ToLayerKey(origin.y));
        if (processedRayKeys.Contains(queryKey) || !pendingRayKeys.Add(queryKey))
        {
            return;
        }

        pendingRayOrigins.Enqueue(SurfaceQuery.AirTransfer(
            queryKey,
            origin,
            safeNormal,
            distance,
            seed.Position.y,
            seed.Id));
        activeWindowGapProbeCount++;
        GapProbeCount++;
        QueuedProbeCount++;
    }

    private FrontierSide GetPreferredSide(Vector3 position)
    {
        if (!includeTargetFrontierInAttempt)
        {
            return FrontierSide.Start;
        }

        float startDistance = Vector3.Distance(position, startPosition);
        float targetDistance = Vector3.Distance(position, targetPosition);
        return startDistance <= targetDistance ? FrontierSide.Start : FrontierSide.Target;
    }

    private bool IsInsideGuideCorridor(Vector3 position)
    {
        if (!constrainToGuideCorridor)
        {
            return true;
        }

        GuideProjectionPoint projection = guideProjection.Project(position);
        return projection.Distance <= maxGuideLateralDistance
            && projection.Progress >= guideProgressMin - 0.001f
            && projection.Progress <= guideProgressMax + 0.001f;
    }

    private bool IsAllowedByGuideCorridor(Vector3 position)
    {
        return !constrainToGuideCorridor || IsInsideGuideCorridor(position);
    }

    private bool IsInsideGuideCorridor(Vector2 position)
    {
        return IsInsideGuideCorridor(new Vector3(position.x, startPosition.y, position.y));
    }

    private void BuildOrderedHorizontalProbeDirections(Vector3 origin, int maxCount)
    {
        orderedProbeDirections.Clear();
        Vector3 guideForward = GetGuideForward(origin);
        for (int index = 0; index < HorizontalProbeDirections.Length; index++)
        {
            Vector2 direction = HorizontalProbeDirections[index];
            Vector3 direction3 = new(direction.x, 0f, direction.y);
            float guideDot = guideForward.sqrMagnitude > 0.001f
                ? Vector3.Dot(direction3.normalized, guideForward)
                : 0f;
            Vector3 estimate = origin + direction3 * Mathf.Max(0.25f, corridorSpacing);
            GuideProjectionPoint estimateProjection = guideProjection.Project(estimate);
            GuideProjectionPoint originProjection = guideProjection.Project(origin);
            float guideAdvance = estimateProjection.Progress - originProjection.Progress;
            float lateralPenalty = estimateProjection.Distance;
            float score = -guideDot * 3f - guideAdvance + lateralPenalty * 0.2f;
            orderedProbeDirections.Add(new ProbeDirection(direction, index, score));
        }

        orderedProbeDirections.Sort(static (a, b) => a.Score.CompareTo(b.Score));
        if (maxCount > 0 && orderedProbeDirections.Count > maxCount)
        {
            orderedProbeDirections.RemoveRange(maxCount, orderedProbeDirections.Count - maxCount);
        }
    }

    private Vector3 GetGuideForward(Vector3 origin)
    {
        const float ProbeDistance = 0.75f;
        GuideProjectionPoint originProjection = guideProjection.Project(origin);
        Vector3 best = targetPosition - origin;
        best.y = 0f;
        float bestScore = float.NegativeInfinity;
        for (int index = 0; index < HorizontalProbeDirections.Length; index++)
        {
            Vector2 direction = HorizontalProbeDirections[index];
            Vector3 direction3 = new Vector3(direction.x, 0f, direction.y).normalized;
            Vector3 probe = origin + direction3 * ProbeDistance;
            GuideProjectionPoint projection = guideProjection.Project(probe);
            float score = projection.Progress - originProjection.Progress - projection.Distance * 0.15f;
            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            best = direction3;
        }

        if (best.sqrMagnitude < 0.001f)
        {
            return Vector3.zero;
        }

        best.y = 0f;
        return best.normalized;
    }

    private bool IsInsideActiveSampleWindow(Vector3 position)
    {
        Vector3 center = new(activeWindowCenter.x, activeWindowSurfaceY, activeWindowCenter.y);
        Vector3 delta = position - center;
        float forward = Vector3.Dot(new Vector3(delta.x, 0f, delta.z), activeWindowScanDirection);
        float lateral = Mathf.Abs(delta.x * activeWindowScanDirection.z - delta.z * activeWindowScanDirection.x);
        float forwardRadius = Mathf.Max(0.001f, activeWindowForwardHalfExtent);
        float lateralRadius = Mathf.Max(0.001f, samplingWindowRadius);
        float verticalRadius = Mathf.Max(0.001f, samplingWindowVerticalHalfExtent);
        float normalizedForward = forward / forwardRadius;
        float normalizedLateral = lateral / lateralRadius;
        float normalizedVertical = delta.y / verticalRadius;
        return normalizedForward * normalizedForward
            + normalizedLateral * normalizedLateral
            + normalizedVertical * normalizedVertical <= 1.0001f;
    }

    private Vector3 GetWindowScanDirection(Vector3 position)
    {
        return GetSafeHorizontalDirection(targetPosition - position);
    }

    private static Vector3 GetSafeHorizontalDirection(Vector3 direction)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.001f)
        {
            return Vector3.forward;
        }

        return direction.normalized;
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

    private QueryKey ToDropDiscoveryProbeKey(Vector3 position, int directionIndex, int distanceIndex)
    {
        return new QueryKey(
            Mathf.RoundToInt(position.x / corridorSpacing),
            ToLayerKey(position.y),
            Mathf.RoundToInt(position.z / corridorSpacing),
            direction: -100 - directionIndex * 10 - distanceIndex);
    }

    private QueryKey ToAirBoundaryProbeKey(Vector3 origin, Vector3 direction)
    {
        int directionKey = Mathf.RoundToInt(direction.x * 9f)
            + Mathf.RoundToInt(direction.y * 9f) * 31
            + Mathf.RoundToInt(direction.z * 9f) * 397;
        return new QueryKey(
            Mathf.RoundToInt(origin.x / SurfaceAirFieldCellSize),
            Mathf.RoundToInt(origin.y / SurfaceAirFieldCellSize),
            Mathf.RoundToInt(origin.z / SurfaceAirFieldCellSize),
            direction: -5000 - directionKey);
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

    private readonly struct GapDirectionKey : System.IEquatable<GapDirectionKey>
    {
        private GapDirectionKey(int windowX, int windowZ, int directionX, int directionZ)
        {
            WindowX = windowX;
            WindowZ = windowZ;
            DirectionX = directionX;
            DirectionZ = directionZ;
        }

        private int WindowX { get; }

        private int WindowZ { get; }

        private int DirectionX { get; }

        private int DirectionZ { get; }

        internal static GapDirectionKey From(Vector2 windowCenter, Vector3 direction)
        {
            Vector3 normalized = direction.sqrMagnitude > 0.001f ? direction.normalized : Vector3.zero;
            return new GapDirectionKey(
                Mathf.RoundToInt(windowCenter.x),
                Mathf.RoundToInt(windowCenter.y),
                Mathf.RoundToInt(normalized.x * 10f),
                Mathf.RoundToInt(normalized.z * 10f));
        }

        public bool Equals(GapDirectionKey other)
        {
            return WindowX == other.WindowX
                && WindowZ == other.WindowZ
                && DirectionX == other.DirectionX
                && DirectionZ == other.DirectionZ;
        }

        public override bool Equals(object? obj)
        {
            return obj is GapDirectionKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = WindowX;
                hash = (hash * 397) ^ WindowZ;
                hash = (hash * 397) ^ DirectionX;
                hash = (hash * 397) ^ DirectionZ;
                return hash;
            }
        }
    }

    private readonly struct ClimbProbeCellKey : System.IEquatable<ClimbProbeCellKey>
    {
        private ClimbProbeCellKey(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        private int X { get; }

        private int Y { get; }

        private int Z { get; }

        internal static ClimbProbeCellKey From(Vector3 position, float cellSize)
        {
            float safeCellSize = Mathf.Max(0.25f, cellSize);
            return new ClimbProbeCellKey(
                Mathf.RoundToInt(position.x / safeCellSize),
                Mathf.RoundToInt(position.y / safeCellSize),
                Mathf.RoundToInt(position.z / safeCellSize));
        }

        public bool Equals(ClimbProbeCellKey other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object? obj)
        {
            return obj is ClimbProbeCellKey other && Equals(other);
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

    private readonly struct StandableClearanceKey : System.IEquatable<StandableClearanceKey>
    {
        private StandableClearanceKey(int x, int y, int z, int colliderId)
        {
            X = x;
            Y = y;
            Z = z;
            ColliderId = colliderId;
        }

        private int X { get; }

        private int Y { get; }

        private int Z { get; }

        private int ColliderId { get; }

        internal static StandableClearanceKey From(HitCandidate candidate, float cellSize)
        {
            float safeCellSize = Mathf.Max(0.1f, cellSize);
            return new StandableClearanceKey(
                Mathf.RoundToInt(candidate.Position.x / safeCellSize),
                Mathf.RoundToInt(candidate.Position.y / safeCellSize),
                Mathf.RoundToInt(candidate.Position.z / safeCellSize),
                candidate.ColliderId);
        }

        public bool Equals(StandableClearanceKey other)
        {
            return X == other.X
                && Y == other.Y
                && Z == other.Z
                && ColliderId == other.ColliderId;
        }

        public override bool Equals(object? obj)
        {
            return obj is StandableClearanceKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X;
                hash = (hash * 397) ^ Y;
                hash = (hash * 397) ^ Z;
                hash = (hash * 397) ^ ColliderId;
                return hash;
            }
        }
    }

    private readonly struct AirWindowKey : System.IEquatable<AirWindowKey>
    {
        private AirWindowKey(
            int x,
            int y,
            int z,
            int directionX,
            int directionZ,
            int forwardExtent,
            int lateralExtent)
        {
            X = x;
            Y = y;
            Z = z;
            DirectionX = directionX;
            DirectionZ = directionZ;
            ForwardExtent = forwardExtent;
            LateralExtent = lateralExtent;
        }

        private int X { get; }

        private int Y { get; }

        private int Z { get; }

        private int DirectionX { get; }

        private int DirectionZ { get; }

        private int ForwardExtent { get; }

        private int LateralExtent { get; }

        internal static AirWindowKey From(
            Vector3 boxCenter,
            Vector3 scanDirection,
            float forwardHalfExtent,
            float lateralHalfExtent,
            float cellSize)
        {
            float safeCellSize = Mathf.Max(0.5f, cellSize);
            Vector3 safeDirection = scanDirection;
            safeDirection.y = 0f;
            if (safeDirection.sqrMagnitude < 0.001f)
            {
                safeDirection = Vector3.forward;
            }

            safeDirection.Normalize();
            return new AirWindowKey(
                Mathf.RoundToInt(boxCenter.x / safeCellSize),
                Mathf.RoundToInt(boxCenter.y / safeCellSize),
                Mathf.RoundToInt(boxCenter.z / safeCellSize),
                Mathf.RoundToInt(safeDirection.x * 16f),
                Mathf.RoundToInt(safeDirection.z * 16f),
                Mathf.RoundToInt(forwardHalfExtent / safeCellSize),
                Mathf.RoundToInt(lateralHalfExtent / safeCellSize));
        }

        public bool Equals(AirWindowKey other)
        {
            return X == other.X
                && Y == other.Y
                && Z == other.Z
                && DirectionX == other.DirectionX
                && DirectionZ == other.DirectionZ
                && ForwardExtent == other.ForwardExtent
                && LateralExtent == other.LateralExtent;
        }

        public override bool Equals(object? obj)
        {
            return obj is AirWindowKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X;
                hash = (hash * 397) ^ Y;
                hash = (hash * 397) ^ Z;
                hash = (hash * 397) ^ DirectionX;
                hash = (hash * 397) ^ DirectionZ;
                hash = (hash * 397) ^ ForwardExtent;
                hash = (hash * 397) ^ LateralExtent;
                return hash;
            }
        }
    }

    private readonly struct ProbeDirection
    {
        internal ProbeDirection(Vector2 direction, int index, float score)
        {
            Direction = direction;
            Index = index;
            Score = score;
        }

        internal Vector2 Direction { get; }

        internal int Index { get; }

        internal float Score { get; }
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
            QueryKey key,
            QueryKind kind,
            Vector3 origin,
            Vector3 direction,
            float distance,
            float preferredSurfaceY,
            int sourcePointId,
            int directionIndex,
            Vector3 neighborDirection)
        {
            Key = key;
            Kind = kind;
            Origin = origin;
            Direction = direction.normalized;
            Distance = distance;
            PreferredSurfaceY = preferredSurfaceY;
            SourcePointId = sourcePointId;
            DirectionIndex = directionIndex;
            NeighborDirection = neighborDirection;
        }

        internal QueryKey Key { get; }

        internal QueryKind Kind { get; }

        internal Vector3 Origin { get; }

        internal Vector3 Direction { get; }

        internal float Distance { get; }

        internal float PreferredSurfaceY { get; }

        internal int SourcePointId { get; }

        internal int DirectionIndex { get; }

        internal Vector3 NeighborDirection { get; }

        internal static SurfaceQuery Vertical(QueryKey key, Vector3 origin, float distance, float preferredSurfaceY)
        {
            return new SurfaceQuery(key, QueryKind.Vertical, origin, Vector3.down, distance, preferredSurfaceY, -1, -1, Vector3.zero);
        }

        internal static SurfaceQuery SurfaceProjection(
            QueryKey key,
            Vector3 origin,
            Vector3 direction,
            float distance,
            float preferredSurfaceY,
            int sourcePointId,
            int directionIndex,
            Vector3 neighborDirection)
        {
            return new SurfaceQuery(
                key,
                QueryKind.SurfaceProjection,
                origin,
                direction,
                distance,
                preferredSurfaceY,
                sourcePointId,
                directionIndex,
                neighborDirection);
        }

        internal static SurfaceQuery GapLanding(QueryKey key, Vector3 origin, float distance, float preferredSurfaceY, int sourcePointId)
        {
            return new SurfaceQuery(key, QueryKind.GapLanding, origin, Vector3.down, distance, preferredSurfaceY, sourcePointId, -1, Vector3.zero);
        }

        internal static SurfaceQuery DropDiscovery(QueryKey key, Vector3 origin, float distance, float preferredSurfaceY, int sourcePointId)
        {
            return new SurfaceQuery(key, QueryKind.DropDiscovery, origin, Vector3.down, distance, preferredSurfaceY, sourcePointId, -1, Vector3.zero);
        }

        internal static SurfaceQuery Directed(QueryKey key, Vector3 origin, Vector3 direction, float distance, float preferredSurfaceY, int sourcePointId)
        {
            return new SurfaceQuery(key, QueryKind.Directed, origin, direction, distance, preferredSurfaceY, sourcePointId, -1, Vector3.zero);
        }

        internal static SurfaceQuery AirTransfer(QueryKey key, Vector3 origin, Vector3 direction, float distance, float preferredSurfaceY, int sourcePointId)
        {
            return new SurfaceQuery(key, QueryKind.AirTransfer, origin, direction, distance, preferredSurfaceY, sourcePointId, -1, Vector3.zero);
        }

        internal static SurfaceQuery AirBoundary(QueryKey key, Vector3 origin, Vector3 direction, float distance, float preferredSurfaceY)
        {
            return new SurfaceQuery(key, QueryKind.AirBoundary, origin, direction, distance, preferredSurfaceY, -1, -1, Vector3.zero);
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
        DropDiscovery,
        Directed,
        AirTransfer,
        AirBoundary,
    }
}
