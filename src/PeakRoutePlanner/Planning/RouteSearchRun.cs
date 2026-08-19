using System;
using System.Collections.Generic;
using UnityEngine;

namespace PeakRoutePlanner.Planning;

internal readonly struct RouteSearchSettings
{
    private const int MinimumEffectiveMaxSteps = 192;

    internal RouteSearchSettings(
        int maxSteps,
        float targetReachedDistance,
        float regionMergeDistance,
        int edgeValidationPairLimit)
    {
        MaxSteps = Mathf.Clamp(Mathf.Max(MinimumEffectiveMaxSteps, maxSteps), 1, 1024);
        TargetReachedDistance = Mathf.Max(0.25f, targetReachedDistance);
        RegionMergeDistance = Mathf.Max(0.35f, regionMergeDistance);
        EdgeValidationPairLimit = Mathf.Clamp(edgeValidationPairLimit, 8, 2048);
    }

    internal int MaxSteps { get; }

    internal float TargetReachedDistance { get; }

    internal float RegionMergeDistance { get; }

    internal int EdgeValidationPairLimit { get; }
}

internal sealed class RouteSearchRun
{
    private const float SeedQuantizationCellSize = 1.25f;
    private const int MaxCandidateRegionsPerSource = 24;
    private const int MaxForwardCandidateRegionsPerSource = 96;
    private const int MaxDetourCandidateRegionsPerSource = 96;
    private const int MaxClimbAssistedCandidateRegionsPerSource = 16;
    private const int MaxClimbBridgePointCount = 160;
    private const int MaxClimbBridgeExpansions = 160;
    private const int MaxClimbNeighborsPerNode = 10;
    private const int MaxClimbEntryCandidatesPerSource = 20;
    private const int GraphLookaheadMinimumRegionCount = 512;
    private const int MaxGraphLookaheadExpansions = 192;
    private const int MaxGraphLookaheadNeighborRegions = 32;
    private const int MaxGraphLookaheadPathHops = 18;
    private const int MaxGraphEdgePairLimit = 16;
    private const float ClimbBridgeNeighborDistance = 1.65f;
    private const float ClimbBridgeCorridorPadding = 4f;
    private const float GraphLookaheadMinimumProgress = 6f;
    private const float GraphLookaheadMinimumSeedDistance = 8f;
    private const float ForwardCandidateMinimumProgress = 4f;
    private const float ForwardCandidateVerticalProgressWeight = 0.35f;
    private const float GraphLookaheadMaxHopDistancePadding = 2f;
    private const int MaxSameTargetLookaheadExpansions = 96;
    private const int MaxSameTargetLookaheadNeighborRegions = 24;
    private const int MaxSameTargetLookaheadPathHops = 10;
    private const float SameTargetStaminaImprovementEpsilon = 0.002f;

    private readonly Vector3 startPosition;
    private readonly Vector3 targetPosition;
    private readonly PlannerConfig config;
    private readonly RouteSearchSettings settings;
    private readonly HashSet<int> blockedPointIds = [];
    private readonly HashSet<RouteSeedKey> sampledSeedKeys = [];
    private readonly HashSet<int> committedPathPointIds = [];
    private readonly HashSet<RouteCandidateAttemptKey> failedTransitions = [];
    private readonly List<RouteRegionVisit> committedVisits = [];
    private readonly List<RouteCommittedStep> committedSteps = [];
    private readonly List<Vector3> previewPath = [];
    private readonly List<Vector3> regionPreviewCenters = [];
    private readonly List<RouteRegionCandidate> candidateBuffer = [];
    private readonly List<int> sourceProbePointBuffer = [];
    private readonly List<int> targetProbePointBuffer = [];
    private readonly List<RouteProbePair> probePairBuffer = [];
    private readonly List<RouteProbePointScore> seedCandidateBuffer = [];
    private readonly List<int> climbProbePointBuffer = [];
    private readonly List<RouteClimbPointScore> climbPointScoreBuffer = [];
    private readonly List<RouteClimbNeighborScore> climbNeighborBuffer = [];
    private readonly List<int> climbPathPointBuffer = [];
    private readonly PriorityQueue<int> routeGraphOpenQueue = new();
    private readonly List<RouteGraphNode> routeGraphNodes = [];
    private readonly Dictionary<int, float> routeGraphBestCostByRegionId = [];
    private readonly List<RouteRegionCandidate> routeGraphNeighborBuffer = [];
    private readonly List<int> routeGraphPathNodeBuffer = [];
    private readonly List<int> routeGraphIntermediatePointBuffer = [];
    private readonly Queue<int> climbSearchQueue = new();
    private readonly Dictionary<RouteSurfaceEdgeKey, RouteEdgeValidationResult> surfaceEdgeValidationCache = [];

    private int samplingStepIndex;

    internal RouteSearchRun(
        Vector3 startPosition,
        Vector3 targetPosition,
        PlannerConfig config,
        RouteSearchSettings settings)
    {
        this.startPosition = startPosition;
        this.targetPosition = targetPosition;
        this.config = config;
        this.settings = settings;
        CurrentSeedPosition = startPosition;
        previewPath.Add(startPosition);
    }

    internal Vector3 CurrentSeedPosition { get; private set; }

    internal Vector3 TargetPosition => targetPosition;

    internal int StepIndex => samplingStepIndex;

    internal IReadOnlyList<Vector3> PreviewPath => previewPath;

    internal IReadOnlyList<Vector3> RegionPreviewCenters => regionPreviewCenters;

    internal bool IsComplete { get; private set; }

    internal bool IsFailed { get; private set; }

    internal string LastStatus { get; private set; } = string.Empty;

    internal void MarkSamplingSeed(Vector3 seedPosition)
    {
        CurrentSeedPosition = seedPosition;
        sampledSeedKeys.Add(RouteSeedKey.From(seedPosition, SeedQuantizationCellSize));
    }

    internal RouteSearchDecision AdvanceAfterSampling(
        IReadOnlyList<SurfacePoint> points,
        int seedPointId,
        SurfaceSampler sampler)
    {
        samplingStepIndex++;
        if (samplingStepIndex > settings.MaxSteps)
        {
            IsFailed = true;
            LastStatus = $"max-steps-reached steps={samplingStepIndex} max={settings.MaxSteps}";
            return RouteSearchDecision.Failed(LastStatus, samplingStepIndex, 0, blockedPointIds.Count);
        }

        if (points.Count == 0)
        {
            IsFailed = true;
            LastStatus = "no-surface-points";
            return RouteSearchDecision.Failed(LastStatus, samplingStepIndex, 0, blockedPointIds.Count);
        }

        if (seedPointId >= 0 && seedPointId < points.Count)
        {
            sampledSeedKeys.Add(RouteSeedKey.From(points[seedPointId].Position, SeedQuantizationCellSize));
            CurrentSeedPosition = points[seedPointId].Position;
        }

        StandableRegionMap regionMap = StandableRegionMap.Build(points, config, settings, targetPosition);
        regionPreviewCenters.Clear();
        for (int index = 0; index < regionMap.Regions.Count; index++)
        {
            regionPreviewCenters.Add(regionMap.Regions[index].Center);
        }

        if (regionMap.Regions.Count == 0)
        {
            IsFailed = true;
            LastStatus = "no-standable-regions";
            return RouteSearchDecision.Failed(LastStatus, samplingStepIndex, regionMap.Regions.Count, blockedPointIds.Count);
        }

        if (!TryResolveSeedRegion(regionMap, points, seedPointId, CurrentSeedPosition, out StandableRegion currentRegion))
        {
            IsFailed = true;
            LastStatus = "seed-region-not-found";
            return RouteSearchDecision.Failed(LastStatus, samplingStepIndex, regionMap.Regions.Count, blockedPointIds.Count);
        }

        AddOrUpdateCurrentVisit(currentRegion, points, seedPointId);
        if (TryGetRegionCompletionPoint(currentRegion, points, seedPointId, sampler, out int completionPointId))
        {
            CompleteAtCurrentRegion(points, completionPointId);
            LastStatus = $"target-reached currentRegion={currentRegion.Id} distance={Vector3.Distance(points[completionPointId].Position, targetPosition):0.00}";
            return RouteSearchDecision.Complete(LastStatus, samplingStepIndex, regionMap.Regions.Count, blockedPointIds.Count);
        }

        if (committedVisits.Count == 0)
        {
            IsFailed = true;
            LastStatus = "no-committed-seed";
            return RouteSearchDecision.Failed(LastStatus, samplingStepIndex, regionMap.Regions.Count, blockedPointIds.Count);
        }

        int sourceIndex = committedVisits.Count - 1;
        if (TrySelectNextFromPath(sourceIndex, regionMap, points, sampler, out RouteTransition transition, out int selectedSourceIndex))
        {
            CommitTransition(transition, selectedSourceIndex, points);
            bool reached = IsPointAtTarget(transition.NextSeedPointId, points);
            if (reached)
            {
                CompleteAtCurrentRegion(points, transition.NextSeedPointId);
                LastStatus = $"target-reached via={transition.EdgeKind} nextRegion={transition.TargetRegion.Id} distance={Vector3.Distance(points[transition.NextSeedPointId].Position, targetPosition):0.00}";
                return RouteSearchDecision.Complete(LastStatus, samplingStepIndex, regionMap.Regions.Count, blockedPointIds.Count);
            }

            LastStatus = $"next-seed step={samplingStepIndex} edge={transition.EdgeKind} edgeDistance={transition.Distance:0.00} staminaCost={transition.StaminaCost:0.000} sameRegion={transition.IsSameRegionFrontier} sourceRegion={transition.SourceRegion.Id} targetRegion={transition.TargetRegion.Id} next=({CurrentSeedPosition.x:0.0},{CurrentSeedPosition.y:0.0},{CurrentSeedPosition.z:0.0}) regions={regionMap.Regions.Count} blockedPoints={blockedPointIds.Count}";
            return RouteSearchDecision.NextSeed(
                CurrentSeedPosition,
                LastStatus,
                samplingStepIndex,
                regionMap.Regions.Count,
                blockedPointIds.Count);
        }

        MarkBlocked(currentRegion);
        if (TryRetreatToPreviousIntermediateSeed(regionMap, points, out Vector3 retreatSeedPosition))
        {
            LastStatus = $"backtracked intermediate-seed step={samplingStepIndex} currentRegion={currentRegion.Id} blockedPoints={blockedPointIds.Count} next=({retreatSeedPosition.x:0.0},{retreatSeedPosition.y:0.0},{retreatSeedPosition.z:0.0})";
            return RouteSearchDecision.NextSeed(
                retreatSeedPosition,
                LastStatus,
                samplingStepIndex,
                regionMap.Regions.Count,
                blockedPointIds.Count);
        }

        LastStatus = $"blocked currentRegion={currentRegion.Id} blockedPoints={blockedPointIds.Count}; searching-backtrack";
        for (int backtrackIndex = committedVisits.Count - 2; backtrackIndex >= 0; backtrackIndex--)
        {
            if (!TryResolveVisitRegion(committedVisits[backtrackIndex], regionMap, out StandableRegion backtrackRegion)
                || IsBlocked(backtrackRegion))
            {
                continue;
            }

            if (!TrySelectNextFromPath(backtrackIndex, regionMap, points, sampler, out transition, out selectedSourceIndex))
            {
                MarkBlocked(backtrackRegion);
                continue;
            }

            CommitTransition(transition, selectedSourceIndex, points);
            bool reached = IsPointAtTarget(transition.NextSeedPointId, points);
            if (reached)
            {
                CompleteAtCurrentRegion(points, transition.NextSeedPointId);
                LastStatus = $"target-reached after-backtrack via={transition.EdgeKind} sourceRegion={transition.SourceRegion.Id} targetRegion={transition.TargetRegion.Id} distance={Vector3.Distance(points[transition.NextSeedPointId].Position, targetPosition):0.00}";
                return RouteSearchDecision.Complete(LastStatus, samplingStepIndex, regionMap.Regions.Count, blockedPointIds.Count);
            }

            LastStatus = $"backtracked next-seed step={samplingStepIndex} edge={transition.EdgeKind} edgeDistance={transition.Distance:0.00} staminaCost={transition.StaminaCost:0.000} sameRegion={transition.IsSameRegionFrontier} sourceRegion={transition.SourceRegion.Id} targetRegion={transition.TargetRegion.Id} next=({CurrentSeedPosition.x:0.0},{CurrentSeedPosition.y:0.0},{CurrentSeedPosition.z:0.0})";
            return RouteSearchDecision.NextSeed(
                CurrentSeedPosition,
                LastStatus,
                samplingStepIndex,
                regionMap.Regions.Count,
                blockedPointIds.Count);
        }

        IsFailed = true;
        LastStatus = $"no-valid-region-from-reachable-path regions={regionMap.Regions.Count} blockedPoints={blockedPointIds.Count}";
        return RouteSearchDecision.Failed(LastStatus, samplingStepIndex, regionMap.Regions.Count, blockedPointIds.Count);
    }

    private bool TrySelectNextFromPath(
        int sourceVisitIndex,
        StandableRegionMap regionMap,
        IReadOnlyList<SurfacePoint> points,
        SurfaceSampler sampler,
        out RouteTransition transition,
        out int selectedSourceIndex)
    {
        transition = default;
        selectedSourceIndex = sourceVisitIndex;
        if (sourceVisitIndex < 0 || sourceVisitIndex >= committedVisits.Count)
        {
            return false;
        }

        if (!TryResolveVisitRegion(committedVisits[sourceVisitIndex], regionMap, out StandableRegion sourceRegion)
            || IsBlocked(sourceRegion))
        {
            return false;
        }

        if (TrySelectCandidateRegion(
                sourceRegion,
                regionMap,
                points,
                sampler,
                MaxForwardCandidateRegionsPerSource,
                RouteCandidateMode.TargetForward,
                allowClimbAssist: true,
                out transition))
        {
            selectedSourceIndex = sourceVisitIndex;
            return true;
        }

        if (TryBuildSameRegionFrontier(sourceRegion, committedVisits[sourceVisitIndex], points, sampler, out transition))
        {
            selectedSourceIndex = sourceVisitIndex;
            return true;
        }

        if (TrySelectGraphLookaheadTransition(
                sourceRegion,
                committedVisits[sourceVisitIndex],
                regionMap,
                points,
                sampler,
                out transition))
        {
            selectedSourceIndex = sourceVisitIndex;
            return true;
        }

        if (TrySelectCandidateRegion(
                sourceRegion,
                regionMap,
                points,
                sampler,
                MaxCandidateRegionsPerSource,
                RouteCandidateMode.TargetGreedy,
                allowClimbAssist: true,
                out transition))
        {
            selectedSourceIndex = sourceVisitIndex;
            return true;
        }

        int detourLimit = Mathf.Clamp(
            Mathf.Max(MaxDetourCandidateRegionsPerSource, settings.EdgeValidationPairLimit),
            MaxCandidateRegionsPerSource,
            regionMap.Regions.Count);
        if (TrySelectCandidateRegion(
                sourceRegion,
                regionMap,
                points,
                sampler,
                detourLimit,
                RouteCandidateMode.SourceDetour,
                allowClimbAssist: true,
                out transition))
        {
            selectedSourceIndex = sourceVisitIndex;
            return true;
        }

        return false;
    }

    private bool TrySelectCandidateRegion(
        StandableRegion sourceRegion,
        StandableRegionMap regionMap,
        IReadOnlyList<SurfacePoint> points,
        SurfaceSampler sampler,
        int candidateLimit,
        RouteCandidateMode mode,
        bool allowClimbAssist,
        out RouteTransition transition)
    {
        transition = default;
        BuildCandidateRegions(sourceRegion, regionMap, mode);
        int testedCandidates = 0;
        int climbAssistTests = 0;
        for (int index = 0; index < candidateBuffer.Count && testedCandidates < candidateLimit; index++)
        {
            StandableRegion candidateRegion = candidateBuffer[index].Region;
            if (candidateRegion.Id == sourceRegion.Id
                || IsBlocked(candidateRegion)
                || IsRegionInCommittedPath(candidateRegion))
            {
                continue;
            }

            RouteCandidateAttemptKey attemptKey = new(
                sourceRegion.AnchorPointId,
                candidateRegion.AnchorPointId,
                sourceRegion.PointIds.Count,
                candidateRegion.PointIds.Count,
                points.Count);
            if (failedTransitions.Contains(attemptKey))
            {
                continue;
            }

            testedCandidates++;
            bool useClimbAssist = allowClimbAssist && climbAssistTests < MaxClimbAssistedCandidateRegionsPerSource;
            if (useClimbAssist)
            {
                climbAssistTests++;
            }

            if (!TryValidateRegionTransition(sourceRegion, candidateRegion, regionMap, points, sampler, useClimbAssist, out transition))
            {
                if (useClimbAssist)
                {
                    failedTransitions.Add(attemptKey);
                }

                continue;
            }

            return true;
        }

        return false;
    }

    private bool TrySelectGraphLookaheadTransition(
        StandableRegion sourceRegion,
        RouteRegionVisit sourceVisit,
        StandableRegionMap regionMap,
        IReadOnlyList<SurfacePoint> points,
        SurfaceSampler sampler,
        out RouteTransition transition)
    {
        transition = default;
        if (regionMap.Regions.Count < GraphLookaheadMinimumRegionCount)
        {
            return false;
        }

        int sourceSeedPointId = sourceVisit.SeedPointId >= 0 && sourceVisit.SeedPointId < points.Count
            ? sourceVisit.SeedPointId
            : sourceRegion.AnchorPointId;
        if (sourceSeedPointId < 0 || sourceSeedPointId >= points.Count)
        {
            return false;
        }

        routeGraphNodes.Clear();
        routeGraphBestCostByRegionId.Clear();
        routeGraphNeighborBuffer.Clear();
        routeGraphPathNodeBuffer.Clear();
        routeGraphIntermediatePointBuffer.Clear();
        routeGraphOpenQueue.Clear();

        float sourceDistanceToTarget = Vector3.Distance(points[sourceSeedPointId].Position, targetPosition);
        float minimumProgress = Mathf.Max(GraphLookaheadMinimumProgress, config.MinimumFrontierAdvanceDistance * 4f);
        float minimumSeedDistance = Mathf.Max(GraphLookaheadMinimumSeedDistance, settings.RegionMergeDistance * 6f);
        int bestNodeIndex = -1;
        int bestNextSeedPointId = -1;
        float bestScore = float.MaxValue;

        routeGraphNodes.Add(new RouteGraphNode(
            sourceRegion.Id,
            parentNodeIndex: -1,
            sourcePointId: sourceSeedPointId,
            entryPointId: sourceSeedPointId,
            edgeKind: RouteEdgeKind.SameRegion,
            edgeDistance: 0f,
            edgeStaminaCost: 0f,
            cost: 0f,
            hops: 0));
        routeGraphBestCostByRegionId[sourceRegion.Id] = 0f;
        routeGraphOpenQueue.Enqueue(0, sourceRegion.DistanceToTarget);

        int expansions = 0;
        while (routeGraphOpenQueue.Count > 0 && expansions < MaxGraphLookaheadExpansions)
        {
            int nodeIndex = routeGraphOpenQueue.Dequeue();
            if (nodeIndex < 0 || nodeIndex >= routeGraphNodes.Count)
            {
                continue;
            }

            RouteGraphNode node = routeGraphNodes[nodeIndex];
            if (node.RegionId < 0 || node.RegionId >= regionMap.Regions.Count)
            {
                continue;
            }

            if (routeGraphBestCostByRegionId.TryGetValue(node.RegionId, out float knownBestCost)
                && node.Cost > knownBestCost + 0.01f)
            {
                continue;
            }

            StandableRegion nodeRegion = regionMap.Regions[node.RegionId];
            expansions++;
            if (node.Hops >= 2
                && !IsBlocked(nodeRegion)
                && !IsRegionInCommittedPath(nodeRegion)
                && RegionHasUnsampledSeed(nodeRegion, points))
            {
                float regionProgress = sourceDistanceToTarget - nodeRegion.DistanceToTarget;
                float seedDistance = Vector3.Distance(points[sourceSeedPointId].Position, points[node.EntryPointId].Position);
                if (regionProgress >= minimumProgress
                    || (seedDistance >= minimumSeedDistance && regionProgress >= minimumProgress * 0.35f))
                {
                    if (TrySelectTargetRegionSeed(node.EntryPointId, nodeRegion, points, sampler, out int nextSeedPointId))
                    {
                        float seedProgress = sourceDistanceToTarget - Vector3.Distance(points[nextSeedPointId].Position, targetPosition);
                        float score = Vector3.Distance(points[nextSeedPointId].Position, targetPosition)
                            + node.Cost * 0.06f
                            + node.Hops * 0.35f
                            - Mathf.Max(0f, seedProgress) * 0.12f;
                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestNodeIndex = nodeIndex;
                            bestNextSeedPointId = nextSeedPointId;
                        }
                    }
                }
            }

            if (node.Hops >= MaxGraphLookaheadPathHops)
            {
                continue;
            }

            BuildRouteGraphNeighborCandidates(nodeRegion, regionMap);
            int testedNeighbors = 0;
            for (int neighborIndex = 0;
                neighborIndex < routeGraphNeighborBuffer.Count && testedNeighbors < MaxGraphLookaheadNeighborRegions;
                neighborIndex++)
            {
                StandableRegion neighborRegion = routeGraphNeighborBuffer[neighborIndex].Region;
                if (neighborRegion.Id == node.RegionId
                    || neighborRegion.Id == sourceRegion.Id
                    || IsBlocked(neighborRegion)
                    || IsRegionInCommittedPath(neighborRegion))
                {
                    continue;
                }

                testedNeighbors++;
                if (!TryValidateRouteGraphRegionEdge(
                        nodeRegion,
                        neighborRegion,
                        node.EntryPointId,
                        points,
                        sampler,
                        out RouteGraphEdge edge))
                {
                    continue;
                }

                float nextCost = node.Cost + Mathf.Max(edge.Distance, Vector3.Distance(points[edge.SourcePointId].Position, points[edge.TargetPointId].Position));
                if (routeGraphBestCostByRegionId.TryGetValue(neighborRegion.Id, out float existingCost)
                    && existingCost <= nextCost + 0.01f)
                {
                    continue;
                }

                int nextNodeIndex = routeGraphNodes.Count;
                routeGraphNodes.Add(new RouteGraphNode(
                    neighborRegion.Id,
                    nodeIndex,
                    edge.SourcePointId,
                    edge.TargetPointId,
                    edge.Kind,
                    edge.Distance,
                    edge.StaminaCost,
                    nextCost,
                    node.Hops + 1));
                routeGraphBestCostByRegionId[neighborRegion.Id] = nextCost;

                float priority = nextCost * 0.35f
                    + neighborRegion.DistanceToTarget
                    + Mathf.Max(0f, neighborRegion.DistanceToTarget - nodeRegion.DistanceToTarget) * 0.75f
                    + (node.Hops + 1) * 0.25f;
                routeGraphOpenQueue.Enqueue(nextNodeIndex, priority);
            }
        }

        if (bestNodeIndex < 0 || bestNextSeedPointId < 0 || bestNextSeedPointId >= points.Count)
        {
            return false;
        }

        RouteGraphNode bestNode = routeGraphNodes[bestNodeIndex];
        if (bestNode.RegionId < 0 || bestNode.RegionId >= regionMap.Regions.Count)
        {
            return false;
        }

        StandableRegion targetRegion = regionMap.Regions[bestNode.RegionId];
        BuildRouteGraphPath(bestNodeIndex);
        if (routeGraphPathNodeBuffer.Count == 0)
        {
            return false;
        }

        RouteGraphNode firstPathNode = routeGraphNodes[routeGraphPathNodeBuffer[0]];
        int[] intermediatePointIds = BuildRouteGraphIntermediatePointIds();
        float distance = EstimateGraphTransitionDistance(bestNodeIndex, bestNextSeedPointId, points);
        float staminaCost = EstimateGraphTransitionStaminaCost(bestNodeIndex, bestNextSeedPointId, points, sampler);
        transition = new RouteTransition(
            sourceRegion,
            targetRegion,
            firstPathNode.SourcePointId,
            bestNode.EntryPointId,
            bestNextSeedPointId,
            RouteEdgeKind.GraphLookahead,
            distance,
            staminaCost,
            isSameRegionFrontier: false,
            intermediatePointIds: intermediatePointIds);
        return true;
    }


    private bool TryRefineTransitionWithSameTargetLookahead(
        StandableRegion sourceRegion,
        StandableRegion targetRegion,
        RouteTransition directTransition,
        StandableRegionMap regionMap,
        IReadOnlyList<SurfacePoint> points,
        SurfaceSampler sampler,
        out RouteTransition refinedTransition)
    {
        refinedTransition = default;
        if (directTransition.StaminaCost <= SameTargetStaminaImprovementEpsilon
            || sourceRegion.Id == targetRegion.Id)
        {
            return false;
        }

        return TrySelectSameTargetGraphLookaheadTransition(
            sourceRegion,
            targetRegion,
            directTransition,
            regionMap,
            points,
            sampler,
            out refinedTransition);
    }

    private bool TrySelectSameTargetGraphLookaheadTransition(
        StandableRegion sourceRegion,
        StandableRegion targetRegion,
        RouteTransition directTransition,
        StandableRegionMap regionMap,
        IReadOnlyList<SurfacePoint> points,
        SurfaceSampler sampler,
        out RouteTransition transition)
    {
        transition = default;
        int sourceSeedPointId = directTransition.SourcePointId >= 0 && directTransition.SourcePointId < points.Count
            ? directTransition.SourcePointId
            : sourceRegion.AnchorPointId;
        if (sourceSeedPointId < 0 || sourceSeedPointId >= points.Count)
        {
            return false;
        }

        routeGraphNodes.Clear();
        routeGraphBestCostByRegionId.Clear();
        routeGraphNeighborBuffer.Clear();
        routeGraphPathNodeBuffer.Clear();
        routeGraphIntermediatePointBuffer.Clear();
        routeGraphOpenQueue.Clear();

        routeGraphNodes.Add(new RouteGraphNode(
            sourceRegion.Id,
            parentNodeIndex: -1,
            sourcePointId: sourceSeedPointId,
            entryPointId: sourceSeedPointId,
            edgeKind: RouteEdgeKind.SameRegion,
            edgeDistance: 0f,
            edgeStaminaCost: 0f,
            cost: 0f,
            hops: 0));
        routeGraphBestCostByRegionId[sourceRegion.Id] = 0f;
        routeGraphOpenQueue.Enqueue(0, sourceRegion.DistanceToTarget);

        int bestNodeIndex = -1;
        int bestNextSeedPointId = -1;
        float bestStaminaCost = float.MaxValue;
        float bestScore = float.MaxValue;
        int expansions = 0;
        while (routeGraphOpenQueue.Count > 0 && expansions < MaxSameTargetLookaheadExpansions)
        {
            int nodeIndex = routeGraphOpenQueue.Dequeue();
            if (nodeIndex < 0 || nodeIndex >= routeGraphNodes.Count)
            {
                continue;
            }

            RouteGraphNode node = routeGraphNodes[nodeIndex];
            if (node.RegionId < 0 || node.RegionId >= regionMap.Regions.Count)
            {
                continue;
            }

            if (routeGraphBestCostByRegionId.TryGetValue(node.RegionId, out float knownBestCost)
                && node.Cost > knownBestCost + 0.01f)
            {
                continue;
            }

            StandableRegion nodeRegion = regionMap.Regions[node.RegionId];
            expansions++;
            if (node.RegionId == targetRegion.Id && node.Hops >= 2)
            {
                if (TrySelectTargetRegionSeed(node.EntryPointId, targetRegion, points, sampler, out int nextSeedPointId))
                {
                    float staminaCost = EstimateGraphTransitionStaminaCost(nodeIndex, nextSeedPointId, points, sampler);
                    if (staminaCost + SameTargetStaminaImprovementEpsilon < directTransition.StaminaCost)
                    {
                        float distance = EstimateGraphTransitionDistance(nodeIndex, nextSeedPointId, points);
                        float score = staminaCost * 120f + distance * 0.04f + node.Hops * 0.05f;
                        if (score < bestScore)
                        {
                            bestScore = score;
                            bestStaminaCost = staminaCost;
                            bestNodeIndex = nodeIndex;
                            bestNextSeedPointId = nextSeedPointId;
                        }
                    }
                }

                continue;
            }

            if (node.Hops >= MaxSameTargetLookaheadPathHops)
            {
                continue;
            }

            BuildRouteGraphNeighborCandidates(nodeRegion, regionMap);
            int testedNeighbors = 0;
            for (int neighborIndex = 0;
                neighborIndex < routeGraphNeighborBuffer.Count && testedNeighbors < MaxSameTargetLookaheadNeighborRegions;
                neighborIndex++)
            {
                StandableRegion neighborRegion = routeGraphNeighborBuffer[neighborIndex].Region;
                if (neighborRegion.Id == node.RegionId
                    || neighborRegion.Id == sourceRegion.Id
                    || IsBlocked(neighborRegion)
                    || (neighborRegion.Id != targetRegion.Id && IsRegionInCommittedPath(neighborRegion)))
                {
                    continue;
                }

                testedNeighbors++;
                if (!TryValidateRouteGraphRegionEdge(
                        nodeRegion,
                        neighborRegion,
                        node.EntryPointId,
                        points,
                        sampler,
                        out RouteGraphEdge edge))
                {
                    continue;
                }

                float edgePlanningCost = edge.StaminaCost * 120f + Mathf.Max(edge.Distance, 0.1f) * 0.08f;
                float nextCost = node.Cost + edgePlanningCost;
                if (routeGraphBestCostByRegionId.TryGetValue(neighborRegion.Id, out float existingCost)
                    && existingCost <= nextCost + 0.01f)
                {
                    continue;
                }

                int nextNodeIndex = routeGraphNodes.Count;
                routeGraphNodes.Add(new RouteGraphNode(
                    neighborRegion.Id,
                    nodeIndex,
                    edge.SourcePointId,
                    edge.TargetPointId,
                    edge.Kind,
                    edge.Distance,
                    edge.StaminaCost,
                    nextCost,
                    node.Hops + 1));
                routeGraphBestCostByRegionId[neighborRegion.Id] = nextCost;

                float priority = nextCost
                    + (neighborRegion.Id == targetRegion.Id ? 0f : Vector3.Distance(neighborRegion.Center, targetRegion.Center) * 0.08f)
                    + Mathf.Max(0f, neighborRegion.DistanceToTarget - sourceRegion.DistanceToTarget) * 0.05f;
                routeGraphOpenQueue.Enqueue(nextNodeIndex, priority);
            }
        }

        if (bestNodeIndex < 0 || bestNextSeedPointId < 0 || bestNextSeedPointId >= points.Count)
        {
            return false;
        }

        BuildRouteGraphPath(bestNodeIndex);
        if (routeGraphPathNodeBuffer.Count == 0)
        {
            return false;
        }

        RouteGraphNode bestNode = routeGraphNodes[bestNodeIndex];
        RouteGraphNode firstPathNode = routeGraphNodes[routeGraphPathNodeBuffer[0]];
        int[] intermediatePointIds = BuildRouteGraphIntermediatePointIds();
        float totalDistance = EstimateGraphTransitionDistance(bestNodeIndex, bestNextSeedPointId, points);
        transition = new RouteTransition(
            sourceRegion,
            targetRegion,
            firstPathNode.SourcePointId,
            bestNode.EntryPointId,
            bestNextSeedPointId,
            RouteEdgeKind.GraphLookahead,
            totalDistance,
            bestStaminaCost,
            isSameRegionFrontier: false,
            intermediatePointIds: intermediatePointIds);
        return true;
    }

    private void BuildRouteGraphNeighborCandidates(StandableRegion sourceRegion, StandableRegionMap regionMap)
    {
        routeGraphNeighborBuffer.Clear();
        float maxCenterDistance = Mathf.Max(
            config.MaxStandJumpDistance + GraphLookaheadMaxHopDistancePadding,
            settings.RegionMergeDistance * 5f);
        float maxVerticalDelta = Mathf.Max(config.MaxStandJumpUpHeight + config.MaxWalkDropHeight + 0.5f, 3.5f);
        for (int index = 0; index < regionMap.Regions.Count; index++)
        {
            StandableRegion region = regionMap.Regions[index];
            if (region.Id == sourceRegion.Id)
            {
                continue;
            }

            Vector3 delta = region.Center - sourceRegion.Center;
            float horizontalDistance = new Vector2(delta.x, delta.z).magnitude;
            if (horizontalDistance > maxCenterDistance || Mathf.Abs(delta.y) > maxVerticalDelta)
            {
                continue;
            }

            float progress = sourceRegion.DistanceToTarget - region.DistanceToTarget;
            float backwardPenalty = Mathf.Max(0f, -progress) * 1.5f;
            float verticalPenalty = Mathf.Abs(delta.y) * 0.18f;
            float score = horizontalDistance
                + region.DistanceToTarget * 0.025f
                + backwardPenalty
                + verticalPenalty;
            routeGraphNeighborBuffer.Add(new RouteRegionCandidate(region, score));
        }

        routeGraphNeighborBuffer.Sort((a, b) => a.Score.CompareTo(b.Score));
    }

    private bool TryValidateRouteGraphRegionEdge(
        StandableRegion sourceRegion,
        StandableRegion targetRegion,
        int preferredSourcePointId,
        IReadOnlyList<SurfacePoint> points,
        SurfaceSampler sampler,
        out RouteGraphEdge edge)
    {
        edge = default;
        if (preferredSourcePointId < 0 || preferredSourcePointId >= points.Count)
        {
            return false;
        }

        sourceProbePointBuffer.Clear();
        targetProbePointBuffer.Clear();
        probePairBuffer.Clear();
        int perSideLimit = 5;
        SelectProbePoints(sourceRegion, targetRegion.Center, targetPosition, points, perSideLimit, sourceProbePointBuffer);
        SelectProbePoints(targetRegion, sourceRegion.Center, targetPosition, points, perSideLimit, targetProbePointBuffer);
        AddUniquePointId(sourceProbePointBuffer, preferredSourcePointId);
        if (sourceProbePointBuffer.Count == 0 || targetProbePointBuffer.Count == 0)
        {
            return false;
        }

        for (int sourceIndex = 0; sourceIndex < sourceProbePointBuffer.Count; sourceIndex++)
        {
            int sourcePointId = sourceProbePointBuffer[sourceIndex];
            if (sourcePointId < 0 || sourcePointId >= points.Count)
            {
                continue;
            }

            for (int targetIndex = 0; targetIndex < targetProbePointBuffer.Count; targetIndex++)
            {
                int targetPointId = targetProbePointBuffer[targetIndex];
                if (targetPointId < 0 || targetPointId >= points.Count)
                {
                    continue;
                }

                float sourceContinuityCost = sourcePointId == preferredSourcePointId
                    ? 0f
                    : Vector3.Distance(points[preferredSourcePointId].Position, points[sourcePointId].Position);
                float distance = Vector3.Distance(points[sourcePointId].Position, points[targetPointId].Position);
                float score = distance
                    + sourceContinuityCost * 0.45f
                    + Vector3.Distance(points[targetPointId].Position, targetPosition) * 0.045f;
                probePairBuffer.Add(new RouteProbePair(sourcePointId, targetPointId, score));
            }
        }

        probePairBuffer.Sort((a, b) => a.Score.CompareTo(b.Score));
        int tested = 0;
        for (int index = 0; index < probePairBuffer.Count && tested < MaxGraphEdgePairLimit; index++)
        {
            RouteProbePair pair = probePairBuffer[index];
            if (pair.SourcePointId < 0
                || pair.SourcePointId >= points.Count
                || pair.TargetPointId < 0
                || pair.TargetPointId >= points.Count)
            {
                continue;
            }

            tested++;
            if (pair.SourcePointId != preferredSourcePointId
                && !CanReachWithinRegion(preferredSourcePointId, pair.SourcePointId, points, sampler))
            {
                continue;
            }

            if (!TryValidateSurfaceEdgeCached(points[pair.SourcePointId], points[pair.TargetPointId], points.Count, sampler, out RouteEdgeValidationResult result))
            {
                continue;
            }

            float totalStaminaCost = Mathf.Max(0f, result.StaminaCost);
            if (pair.SourcePointId != preferredSourcePointId)
            {
                totalStaminaCost += GetValidatedEdgeStaminaCost(preferredSourcePointId, pair.SourcePointId, points, sampler);
            }

            edge = new RouteGraphEdge(pair.SourcePointId, pair.TargetPointId, result.Kind, result.Distance, totalStaminaCost);
            return true;
        }

        return false;
    }

    private bool RegionHasUnsampledSeed(StandableRegion region, IReadOnlyList<SurfacePoint> points)
    {
        if (region.ClosestToTargetPointId >= 0
            && region.ClosestToTargetPointId < points.Count
            && !sampledSeedKeys.Contains(RouteSeedKey.From(points[region.ClosestToTargetPointId].Position, SeedQuantizationCellSize)))
        {
            return true;
        }

        for (int index = 0; index < region.PointIds.Count; index++)
        {
            int pointId = region.PointIds[index];
            if (pointId < 0 || pointId >= points.Count)
            {
                continue;
            }

            if (!sampledSeedKeys.Contains(RouteSeedKey.From(points[pointId].Position, SeedQuantizationCellSize)))
            {
                return true;
            }
        }

        return false;
    }

    private void BuildRouteGraphPath(int bestNodeIndex)
    {
        routeGraphPathNodeBuffer.Clear();
        int currentNodeIndex = bestNodeIndex;
        while (currentNodeIndex > 0 && currentNodeIndex < routeGraphNodes.Count)
        {
            routeGraphPathNodeBuffer.Add(currentNodeIndex);
            currentNodeIndex = routeGraphNodes[currentNodeIndex].ParentNodeIndex;
        }

        routeGraphPathNodeBuffer.Reverse();
    }

    private int[] BuildRouteGraphIntermediatePointIds()
    {
        routeGraphIntermediatePointBuffer.Clear();
        for (int index = 0; index < routeGraphPathNodeBuffer.Count; index++)
        {
            RouteGraphNode node = routeGraphNodes[routeGraphPathNodeBuffer[index]];
            bool isLast = index == routeGraphPathNodeBuffer.Count - 1;
            if (index > 0)
            {
                AddUniquePointId(routeGraphIntermediatePointBuffer, node.SourcePointId);
            }

            if (!isLast)
            {
                AddUniquePointId(routeGraphIntermediatePointBuffer, node.EntryPointId);
            }
        }

        return routeGraphIntermediatePointBuffer.ToArray();
    }

    private float EstimateGraphTransitionDistance(
        int bestNodeIndex,
        int nextSeedPointId,
        IReadOnlyList<SurfacePoint> points)
    {
        float distance = 0f;
        int currentNodeIndex = bestNodeIndex;
        while (currentNodeIndex > 0 && currentNodeIndex < routeGraphNodes.Count)
        {
            RouteGraphNode node = routeGraphNodes[currentNodeIndex];
            distance += Mathf.Max(0f, node.EdgeDistance);
            currentNodeIndex = node.ParentNodeIndex;
        }

        RouteGraphNode bestNode = routeGraphNodes[bestNodeIndex];
        if (bestNode.EntryPointId >= 0
            && bestNode.EntryPointId < points.Count
            && nextSeedPointId >= 0
            && nextSeedPointId < points.Count
            && bestNode.EntryPointId != nextSeedPointId)
        {
            distance += Vector3.Distance(points[bestNode.EntryPointId].Position, points[nextSeedPointId].Position);
        }

        return distance;
    }

    private bool TryBuildSameRegionFrontier(
        StandableRegion sourceRegion,
        RouteRegionVisit sourceVisit,
        IReadOnlyList<SurfacePoint> points,
        SurfaceSampler sampler,
        out RouteTransition transition)
    {
        transition = default;
        int nextPointId = sourceRegion.ClosestToTargetPointId;
        if (nextPointId < 0 || nextPointId >= points.Count)
        {
            return false;
        }

        SurfacePoint nextPoint = points[nextPointId];
        RouteSeedKey nextSeedKey = RouteSeedKey.From(nextPoint.Position, SeedQuantizationCellSize);
        if (sampledSeedKeys.Contains(nextSeedKey))
        {
            return false;
        }

        Vector3 sourceSeedPosition = sourceVisit.SeedPosition;
        float movementDistance = Vector3.Distance(sourceSeedPosition, nextPoint.Position);
        float progress = Vector3.Distance(sourceSeedPosition, targetPosition) - Vector3.Distance(nextPoint.Position, targetPosition);
        float minimumAdvance = Mathf.Max(0.75f, config.MinimumFrontierAdvanceDistance);
        if (movementDistance < minimumAdvance || progress < minimumAdvance * 0.35f)
        {
            return false;
        }

        int fromPointId = sourceVisit.SeedPointId >= 0 && sourceVisit.SeedPointId < points.Count
            ? sourceVisit.SeedPointId
            : sourceRegion.AnchorPointId;
        if (fromPointId < 0 || fromPointId >= points.Count)
        {
            return false;
        }

        if (!TryValidateSurfaceEdgeCached(points[fromPointId], nextPoint, points.Count, sampler, out RouteEdgeValidationResult result))
        {
            return false;
        }

        transition = new RouteTransition(
            sourceRegion,
            sourceRegion,
            fromPointId,
            nextPointId,
            nextPointId,
            result.Kind == RouteEdgeKind.None ? RouteEdgeKind.SameRegion : result.Kind,
            result.Distance > 0f ? result.Distance : movementDistance,
            result.StaminaCost,
            isSameRegionFrontier: true);
        return true;
    }

    private void BuildCandidateRegions(StandableRegion sourceRegion, StandableRegionMap regionMap, RouteCandidateMode mode)
    {
        candidateBuffer.Clear();
        for (int index = 0; index < regionMap.Regions.Count; index++)
        {
            StandableRegion region = regionMap.Regions[index];
            if (region.Id == sourceRegion.Id)
            {
                continue;
            }

            float sourceDistance = Vector3.Distance(sourceRegion.Center, region.Center);
            float progress = sourceRegion.DistanceToTarget - region.DistanceToTarget;
            if (mode == RouteCandidateMode.TargetForward
                && progress < Mathf.Max(ForwardCandidateMinimumProgress, config.MinimumFrontierAdvanceDistance * 3f))
            {
                continue;
            }

            float verticalProgress = Mathf.Max(0f, region.Center.y - sourceRegion.Center.y);
            float score = mode switch
            {
                RouteCandidateMode.SourceDetour => GetDetourCandidateScore(sourceRegion, region, sourceDistance),
                RouteCandidateMode.TargetForward => region.DistanceToTarget
                    + sourceDistance * 0.04f
                    - Mathf.Max(0f, progress) * 0.25f
                    - verticalProgress * ForwardCandidateVerticalProgressWeight,
                _ => region.DistanceToTarget
                    + sourceDistance * 0.08f
                    + Mathf.Max(0f, -progress) * 2.0f,
            };
            candidateBuffer.Add(new RouteRegionCandidate(region, score));
        }

        candidateBuffer.Sort((a, b) => a.Score.CompareTo(b.Score));
    }

    private float GetDetourCandidateScore(StandableRegion sourceRegion, StandableRegion candidateRegion, float sourceDistance)
    {
        float currentDistanceToTarget = Vector3.Distance(sourceRegion.Center, targetPosition);
        float targetDistance = candidateRegion.DistanceToTarget;
        float backwardPenalty = Mathf.Max(0f, targetDistance - currentDistanceToTarget) * 0.35f;
        float verticalPenalty = Mathf.Abs(candidateRegion.Center.y - sourceRegion.Center.y) * 0.12f;
        return sourceDistance + targetDistance * 0.035f + backwardPenalty + verticalPenalty;
    }

    private bool TryValidateRegionTransition(
        StandableRegion sourceRegion,
        StandableRegion targetRegion,
        StandableRegionMap regionMap,
        IReadOnlyList<SurfacePoint> points,
        SurfaceSampler sampler,
        bool allowClimbAssist,
        out RouteTransition transition)
    {
        transition = default;
        sourceProbePointBuffer.Clear();
        targetProbePointBuffer.Clear();
        probePairBuffer.Clear();

        int pairLimit = Mathf.Max(8, settings.EdgeValidationPairLimit);
        int perSideLimit = Mathf.Clamp(Mathf.CeilToInt(Mathf.Sqrt(pairLimit)) + 2, 4, 24);
        SelectProbePoints(sourceRegion, targetRegion.Center, targetPosition, points, perSideLimit, sourceProbePointBuffer);
        SelectProbePoints(targetRegion, sourceRegion.Center, targetPosition, points, perSideLimit, targetProbePointBuffer);
        if (sourceProbePointBuffer.Count == 0 || targetProbePointBuffer.Count == 0)
        {
            return false;
        }

        for (int sourceIndex = 0; sourceIndex < sourceProbePointBuffer.Count; sourceIndex++)
        {
            int sourcePointId = sourceProbePointBuffer[sourceIndex];
            SurfacePoint sourcePoint = points[sourcePointId];
            for (int targetIndex = 0; targetIndex < targetProbePointBuffer.Count; targetIndex++)
            {
                int targetPointId = targetProbePointBuffer[targetIndex];
                SurfacePoint targetPoint = points[targetPointId];
                float distance = Vector3.Distance(sourcePoint.Position, targetPoint.Position);
                float score = distance + Vector3.Distance(targetPoint.Position, targetPosition) * 0.08f;
                probePairBuffer.Add(new RouteProbePair(sourcePointId, targetPointId, score));
            }
        }

        probePairBuffer.Sort((a, b) => a.Score.CompareTo(b.Score));
        int tested = 0;
        for (int index = 0; index < probePairBuffer.Count && tested < pairLimit; index++)
        {
            RouteProbePair pair = probePairBuffer[index];
            if (pair.SourcePointId < 0
                || pair.SourcePointId >= points.Count
                || pair.TargetPointId < 0
                || pair.TargetPointId >= points.Count)
            {
                continue;
            }

            tested++;
            SurfacePoint sourcePoint = points[pair.SourcePointId];
            SurfacePoint targetPoint = points[pair.TargetPointId];
            if (!TryValidateSurfaceEdgeCached(sourcePoint, targetPoint, points.Count, sampler, out RouteEdgeValidationResult result))
            {
                continue;
            }

            if (!TrySelectTargetRegionSeed(pair.TargetPointId, targetRegion, points, sampler, out int nextSeedPointId))
            {
                continue;
            }

            float staminaCost = Mathf.Max(0f, result.StaminaCost);
            if (pair.TargetPointId != nextSeedPointId)
            {
                staminaCost += GetValidatedEdgeStaminaCost(pair.TargetPointId, nextSeedPointId, points, sampler);
            }

            transition = new RouteTransition(
                sourceRegion,
                targetRegion,
                pair.SourcePointId,
                pair.TargetPointId,
                nextSeedPointId,
                result.Kind,
                result.Distance,
                staminaCost,
                isSameRegionFrontier: false,
                intermediatePointIds: null);

            if (TryRefineTransitionWithSameTargetLookahead(
                    sourceRegion,
                    targetRegion,
                    transition,
                    regionMap,
                    points,
                    sampler,
                    out RouteTransition refinedTransition))
            {
                transition = refinedTransition;
            }

            return true;
        }

        if (allowClimbAssist
            && TryValidateClimbAssistedRegionTransition(sourceRegion, targetRegion, points, sampler, out transition))
        {
            if (TryRefineTransitionWithSameTargetLookahead(
                    sourceRegion,
                    targetRegion,
                    transition,
                    regionMap,
                    points,
                    sampler,
                    out RouteTransition refinedTransition))
            {
                transition = refinedTransition;
            }

            return true;
        }

        return false;
    }

    private bool TryValidateClimbAssistedRegionTransition(
        StandableRegion sourceRegion,
        StandableRegion targetRegion,
        IReadOnlyList<SurfacePoint> points,
        SurfaceSampler sampler,
        out RouteTransition transition)
    {
        transition = default;
        SelectClimbBridgePoints(sourceRegion, targetRegion, points, climbProbePointBuffer);
        if (sourceProbePointBuffer.Count == 0
            || targetProbePointBuffer.Count == 0
            || climbProbePointBuffer.Count == 0)
        {
            return false;
        }

        int climbPointCount = climbProbePointBuffer.Count;
        bool[] visited = new bool[climbPointCount];
        int[] parentByIndex = new int[climbPointCount];
        int[] sourceByIndex = new int[climbPointCount];
        int[] depthByIndex = new int[climbPointCount];
        Array.Fill(parentByIndex, -1);
        Array.Fill(sourceByIndex, -1);
        climbSearchQueue.Clear();

        for (int sourceIndex = 0; sourceIndex < sourceProbePointBuffer.Count; sourceIndex++)
        {
            int sourcePointId = sourceProbePointBuffer[sourceIndex];
            if (sourcePointId < 0 || sourcePointId >= points.Count)
            {
                continue;
            }

            BuildClimbEntryCandidates(sourcePointId, points);
            int testedEntries = 0;
            for (int entryIndex = 0;
                entryIndex < climbNeighborBuffer.Count && testedEntries < MaxClimbEntryCandidatesPerSource;
                entryIndex++)
            {
                int climbIndex = climbNeighborBuffer[entryIndex].CandidateIndex;
                if (climbIndex < 0 || climbIndex >= climbPointCount || visited[climbIndex])
                {
                    continue;
                }

                testedEntries++;
                int climbPointId = climbProbePointBuffer[climbIndex];
                if (!TryValidateSurfaceEdgeCached(points[sourcePointId], points[climbPointId], points.Count, sampler, out _))
                {
                    continue;
                }

                visited[climbIndex] = true;
                sourceByIndex[climbIndex] = sourcePointId;
                depthByIndex[climbIndex] = 1;
                climbSearchQueue.Enqueue(climbIndex);
            }
        }

        int expansions = 0;
        while (climbSearchQueue.Count > 0 && expansions < MaxClimbBridgeExpansions)
        {
            int currentIndex = climbSearchQueue.Dequeue();
            expansions++;
            if (currentIndex < 0 || currentIndex >= climbPointCount)
            {
                continue;
            }

            int currentPointId = climbProbePointBuffer[currentIndex];
            if (currentPointId < 0 || currentPointId >= points.Count)
            {
                continue;
            }

            for (int targetIndex = 0; targetIndex < targetProbePointBuffer.Count; targetIndex++)
            {
                int targetPointId = targetProbePointBuffer[targetIndex];
                if (targetPointId < 0 || targetPointId >= points.Count)
                {
                    continue;
                }

                if (!TryValidateSurfaceEdgeCached(points[currentPointId], points[targetPointId], points.Count, sampler, out _))
                {
                    continue;
                }

                if (!TrySelectTargetRegionSeed(targetPointId, targetRegion, points, sampler, out int nextSeedPointId))
                {
                    continue;
                }

                int sourcePointId = sourceByIndex[currentIndex];
                if (sourcePointId < 0 || sourcePointId >= points.Count)
                {
                    continue;
                }

                int[] intermediatePointIds = BuildClimbIntermediatePath(currentIndex, parentByIndex);
                float distance = EstimateTransitionDistance(sourcePointId, intermediatePointIds, targetPointId, nextSeedPointId, points);
                float staminaCost = EstimateTransitionStaminaCost(sourcePointId, intermediatePointIds, targetPointId, nextSeedPointId, points, sampler);
                transition = new RouteTransition(
                    sourceRegion,
                    targetRegion,
                    sourcePointId,
                    targetPointId,
                    nextSeedPointId,
                    RouteEdgeKind.ClimbAssisted,
                    distance,
                    staminaCost,
                    isSameRegionFrontier: false,
                    intermediatePointIds: intermediatePointIds);
                return true;
            }

            BuildClimbNeighborCandidates(currentIndex, visited, depthByIndex, points);
            int testedNeighbors = 0;
            for (int neighborIndex = 0;
                neighborIndex < climbNeighborBuffer.Count && testedNeighbors < MaxClimbNeighborsPerNode;
                neighborIndex++)
            {
                int candidateIndex = climbNeighborBuffer[neighborIndex].CandidateIndex;
                if (candidateIndex < 0 || candidateIndex >= climbPointCount || visited[candidateIndex])
                {
                    continue;
                }

                testedNeighbors++;
                int candidatePointId = climbProbePointBuffer[candidateIndex];
                if (candidatePointId < 0 || candidatePointId >= points.Count)
                {
                    continue;
                }

                if (!TryValidateSurfaceEdgeCached(points[currentPointId], points[candidatePointId], points.Count, sampler, out _))
                {
                    continue;
                }

                visited[candidateIndex] = true;
                parentByIndex[candidateIndex] = currentIndex;
                sourceByIndex[candidateIndex] = sourceByIndex[currentIndex];
                depthByIndex[candidateIndex] = depthByIndex[currentIndex] + 1;
                climbSearchQueue.Enqueue(candidateIndex);
            }
        }

        return false;
    }

    private void SelectClimbBridgePoints(
        StandableRegion sourceRegion,
        StandableRegion targetRegion,
        IReadOnlyList<SurfacePoint> points,
        List<int> destination)
    {
        destination.Clear();
        climbPointScoreBuffer.Clear();
        Vector3 sourceCenter = sourceRegion.Center;
        Vector3 targetCenter = targetRegion.Center;
        float sourceTargetDistance = Mathf.Max(0.5f, Vector3.Distance(sourceCenter, targetCenter));
        float corridorLimit = Mathf.Max(config.SurfaceNeighborDistance * 3.5f, ClimbBridgeCorridorPadding);
        for (int index = 0; index < points.Count; index++)
        {
            SurfacePoint point = points[index];
            if (point.Kind != SurfaceKind.Climbable)
            {
                continue;
            }

            float corridorDistance = DistancePointToSegment(point.Position, sourceCenter, targetCenter);
            if (corridorDistance > corridorLimit && Vector3.Distance(point.Position, sourceCenter) > sourceTargetDistance + corridorLimit)
            {
                continue;
            }

            float score = corridorDistance
                + Vector3.Distance(point.Position, targetCenter) * 0.035f
                + Vector3.Distance(point.Position, sourceCenter) * 0.015f;
            climbPointScoreBuffer.Add(new RouteClimbPointScore(index, score));
        }

        climbPointScoreBuffer.Sort((a, b) => a.Score.CompareTo(b.Score));
        for (int index = 0; index < climbPointScoreBuffer.Count && destination.Count < MaxClimbBridgePointCount; index++)
        {
            destination.Add(climbPointScoreBuffer[index].PointId);
        }
    }

    private void BuildClimbEntryCandidates(int sourcePointId, IReadOnlyList<SurfacePoint> points)
    {
        climbNeighborBuffer.Clear();
        SurfacePoint sourcePoint = points[sourcePointId];
        for (int index = 0; index < climbProbePointBuffer.Count; index++)
        {
            int pointId = climbProbePointBuffer[index];
            if (pointId < 0 || pointId >= points.Count)
            {
                continue;
            }

            SurfacePoint point = points[pointId];
            float distance = Vector3.Distance(sourcePoint.Position, point.Position);
            float score = distance + Vector3.Distance(point.Position, targetPosition) * 0.04f;
            climbNeighborBuffer.Add(new RouteClimbNeighborScore(index, score));
        }

        climbNeighborBuffer.Sort((a, b) => a.Score.CompareTo(b.Score));
    }

    private void BuildClimbNeighborCandidates(
        int currentIndex,
        bool[] visited,
        int[] depthByIndex,
        IReadOnlyList<SurfacePoint> points)
    {
        climbNeighborBuffer.Clear();
        if (currentIndex < 0 || currentIndex >= climbProbePointBuffer.Count)
        {
            return;
        }

        int currentPointId = climbProbePointBuffer[currentIndex];
        if (currentPointId < 0 || currentPointId >= points.Count)
        {
            return;
        }

        SurfacePoint currentPoint = points[currentPointId];
        float neighborDistance = Mathf.Max(ClimbBridgeNeighborDistance, config.SurfaceNeighborDistance * 2.25f);
        float neighborDistanceSqr = neighborDistance * neighborDistance;
        for (int index = 0; index < climbProbePointBuffer.Count; index++)
        {
            if (index == currentIndex || visited[index])
            {
                continue;
            }

            int pointId = climbProbePointBuffer[index];
            if (pointId < 0 || pointId >= points.Count)
            {
                continue;
            }

            SurfacePoint candidate = points[pointId];
            Vector3 delta = candidate.Position - currentPoint.Position;
            if (delta.sqrMagnitude > neighborDistanceSqr)
            {
                continue;
            }

            float score = delta.magnitude
                + Vector3.Distance(candidate.Position, targetPosition) * 0.035f
                + depthByIndex[currentIndex] * 0.03f;
            climbNeighborBuffer.Add(new RouteClimbNeighborScore(index, score));
        }

        climbNeighborBuffer.Sort((a, b) => a.Score.CompareTo(b.Score));
    }

    private bool TryValidateSurfaceEdgeCached(
        SurfacePoint source,
        SurfacePoint target,
        int pointCount,
        SurfaceSampler sampler,
        out RouteEdgeValidationResult result)
    {
        RouteSurfaceEdgeKey key = new(source.Id, target.Id, pointCount);
        if (surfaceEdgeValidationCache.TryGetValue(key, out result))
        {
            return result.IsValid;
        }

        result = source.Kind == SurfaceKind.Standable && target.Kind == SurfaceKind.Standable
            ? sampler.ValidateStandableEdge(source, target)
            : sampler.ValidateSurfaceEdge(source, target);
        surfaceEdgeValidationCache[key] = result;
        return result.IsValid;
    }

    private int[] BuildClimbIntermediatePath(int lastIndex, int[] parentByIndex)
    {
        climbPathPointBuffer.Clear();
        int currentIndex = lastIndex;
        while (currentIndex >= 0 && currentIndex < climbProbePointBuffer.Count)
        {
            climbPathPointBuffer.Add(climbProbePointBuffer[currentIndex]);
            currentIndex = parentByIndex[currentIndex];
        }

        climbPathPointBuffer.Reverse();
        return climbPathPointBuffer.ToArray();
    }

    private static float EstimateTransitionDistance(
        int sourcePointId,
        int[]? intermediatePointIds,
        int targetPointId,
        int seedPointId,
        IReadOnlyList<SurfacePoint> points)
    {
        float distance = 0f;
        int previousPointId = sourcePointId;
        if (intermediatePointIds != null)
        {
            for (int index = 0; index < intermediatePointIds.Length; index++)
            {
                int pointId = intermediatePointIds[index];
                if (previousPointId >= 0 && previousPointId < points.Count && pointId >= 0 && pointId < points.Count)
                {
                    distance += Vector3.Distance(points[previousPointId].Position, points[pointId].Position);
                }

                previousPointId = pointId;
            }
        }

        if (previousPointId >= 0 && previousPointId < points.Count && targetPointId >= 0 && targetPointId < points.Count)
        {
            distance += Vector3.Distance(points[previousPointId].Position, points[targetPointId].Position);
        }

        if (targetPointId != seedPointId && targetPointId >= 0 && targetPointId < points.Count && seedPointId >= 0 && seedPointId < points.Count)
        {
            distance += Vector3.Distance(points[targetPointId].Position, points[seedPointId].Position);
        }

        return distance;
    }


    private float EstimateGraphTransitionStaminaCost(
        int bestNodeIndex,
        int nextSeedPointId,
        IReadOnlyList<SurfacePoint> points,
        SurfaceSampler sampler)
    {
        float staminaCost = 0f;
        int currentNodeIndex = bestNodeIndex;
        while (currentNodeIndex > 0 && currentNodeIndex < routeGraphNodes.Count)
        {
            RouteGraphNode node = routeGraphNodes[currentNodeIndex];
            staminaCost += Mathf.Max(0f, node.EdgeStaminaCost);
            currentNodeIndex = node.ParentNodeIndex;
        }

        RouteGraphNode bestNode = routeGraphNodes[bestNodeIndex];
        if (bestNode.EntryPointId >= 0
            && bestNode.EntryPointId < points.Count
            && nextSeedPointId >= 0
            && nextSeedPointId < points.Count
            && bestNode.EntryPointId != nextSeedPointId)
        {
            staminaCost += GetValidatedEdgeStaminaCost(bestNode.EntryPointId, nextSeedPointId, points, sampler);
        }

        return staminaCost;
    }

    private float EstimateTransitionStaminaCost(
        int sourcePointId,
        int[]? intermediatePointIds,
        int targetPointId,
        int seedPointId,
        IReadOnlyList<SurfacePoint> points,
        SurfaceSampler sampler)
    {
        float staminaCost = 0f;
        int previousPointId = sourcePointId;
        if (intermediatePointIds != null)
        {
            for (int index = 0; index < intermediatePointIds.Length; index++)
            {
                int pointId = intermediatePointIds[index];
                staminaCost += GetValidatedEdgeStaminaCost(previousPointId, pointId, points, sampler);
                previousPointId = pointId;
            }
        }

        staminaCost += GetValidatedEdgeStaminaCost(previousPointId, targetPointId, points, sampler);
        if (targetPointId != seedPointId)
        {
            staminaCost += GetValidatedEdgeStaminaCost(targetPointId, seedPointId, points, sampler);
        }

        return staminaCost;
    }

    private float GetValidatedEdgeStaminaCost(
        int sourcePointId,
        int targetPointId,
        IReadOnlyList<SurfacePoint> points,
        SurfaceSampler sampler)
    {
        if (sourcePointId == targetPointId)
        {
            return 0f;
        }

        if (sourcePointId < 0
            || sourcePointId >= points.Count
            || targetPointId < 0
            || targetPointId >= points.Count)
        {
            return 0f;
        }

        return TryValidateSurfaceEdgeCached(points[sourcePointId], points[targetPointId], points.Count, sampler, out RouteEdgeValidationResult result)
            ? Mathf.Max(0f, result.StaminaCost)
            : 0f;
    }

    private static float DistancePointToSegment(Vector3 point, Vector3 segmentStart, Vector3 segmentEnd)
    {
        Vector3 segment = segmentEnd - segmentStart;
        float segmentLengthSqr = segment.sqrMagnitude;
        if (segmentLengthSqr <= 0.001f)
        {
            return Vector3.Distance(point, segmentStart);
        }

        float t = Mathf.Clamp01(Vector3.Dot(point - segmentStart, segment) / segmentLengthSqr);
        Vector3 projected = segmentStart + segment * t;
        return Vector3.Distance(point, projected);
    }

    private bool TrySelectTargetRegionSeed(
        int entryPointId,
        StandableRegion targetRegion,
        IReadOnlyList<SurfacePoint> points,
        SurfaceSampler sampler,
        out int nextSeedPointId)
    {
        nextSeedPointId = -1;
        if (entryPointId < 0 || entryPointId >= points.Count)
        {
            return false;
        }

        int closestPointId = targetRegion.ClosestToTargetPointId;
        if (closestPointId >= 0
            && closestPointId < points.Count
            && !sampledSeedKeys.Contains(RouteSeedKey.From(points[closestPointId].Position, SeedQuantizationCellSize))
            && CanReachWithinRegion(entryPointId, closestPointId, points, sampler))
        {
            nextSeedPointId = closestPointId;
            return true;
        }

        RouteSeedKey entrySeedKey = RouteSeedKey.From(points[entryPointId].Position, SeedQuantizationCellSize);
        if (!sampledSeedKeys.Contains(entrySeedKey) || IsPointAtTarget(entryPointId, points))
        {
            nextSeedPointId = entryPointId;
            return true;
        }

        seedCandidateBuffer.Clear();
        for (int index = 0; index < targetRegion.PointIds.Count; index++)
        {
            int pointId = targetRegion.PointIds[index];
            if (pointId < 0 || pointId >= points.Count || pointId == closestPointId || pointId == entryPointId)
            {
                continue;
            }

            RouteSeedKey seedKey = RouteSeedKey.From(points[pointId].Position, SeedQuantizationCellSize);
            if (sampledSeedKeys.Contains(seedKey))
            {
                continue;
            }

            float score = Vector3.Distance(points[pointId].Position, targetPosition)
                + Vector3.Distance(points[pointId].Position, points[entryPointId].Position) * 0.18f;
            seedCandidateBuffer.Add(new RouteProbePointScore(pointId, score));
        }

        seedCandidateBuffer.Sort((a, b) => a.Score.CompareTo(b.Score));
        for (int index = 0; index < seedCandidateBuffer.Count && index < settings.EdgeValidationPairLimit; index++)
        {
            int pointId = seedCandidateBuffer[index].PointId;
            if (CanReachWithinRegion(entryPointId, pointId, points, sampler))
            {
                nextSeedPointId = pointId;
                return true;
            }
        }

        if (closestPointId >= 0
            && closestPointId < points.Count
            && IsPointAtTarget(closestPointId, points)
            && CanReachWithinRegion(entryPointId, closestPointId, points, sampler))
        {
            nextSeedPointId = closestPointId;
            return true;
        }

        return false;
    }

    private bool CanReachWithinRegion(
        int fromPointId,
        int toPointId,
        IReadOnlyList<SurfacePoint> points,
        SurfaceSampler sampler)
    {
        if (fromPointId == toPointId)
        {
            return true;
        }

        if (fromPointId < 0 || fromPointId >= points.Count || toPointId < 0 || toPointId >= points.Count)
        {
            return false;
        }

        return TryValidateSurfaceEdgeCached(points[fromPointId], points[toPointId], points.Count, sampler, out _);
    }

    private bool TryGetRegionCompletionPoint(
        StandableRegion region,
        IReadOnlyList<SurfacePoint> points,
        int seedPointId,
        SurfaceSampler sampler,
        out int completionPointId)
    {
        completionPointId = -1;
        if (!IsRegionAtTarget(region))
        {
            return false;
        }

        int fromPointId = seedPointId >= 0 && seedPointId < points.Count
            ? seedPointId
            : region.AnchorPointId;
        if (IsPointAtTarget(fromPointId, points))
        {
            completionPointId = fromPointId;
            return true;
        }

        int closestPointId = region.ClosestToTargetPointId;
        if (CanReachWithinRegion(fromPointId, closestPointId, points, sampler))
        {
            completionPointId = closestPointId;
            return true;
        }

        return false;
    }

    private static void SelectProbePoints(
        StandableRegion region,
        Vector3 referencePosition,
        Vector3 targetPosition,
        IReadOnlyList<SurfacePoint> points,
        int limit,
        List<int> destination)
    {
        destination.Clear();
        AddUniquePointId(destination, region.AnchorPointId);
        AddUniquePointId(destination, region.ClosestToTargetPointId);
        AddUniquePointId(destination, region.CenterPointId);

        List<RouteProbePointScore> scored = [];
        for (int index = 0; index < region.PointIds.Count; index++)
        {
            int pointId = region.PointIds[index];
            if (pointId < 0 || pointId >= points.Count)
            {
                continue;
            }

            SurfacePoint point = points[pointId];
            float score = Vector3.Distance(point.Position, referencePosition)
                + Vector3.Distance(point.Position, targetPosition) * 0.18f;
            scored.Add(new RouteProbePointScore(pointId, score));
        }

        scored.Sort((a, b) => a.Score.CompareTo(b.Score));
        for (int index = 0; index < scored.Count && destination.Count < limit; index++)
        {
            AddUniquePointId(destination, scored[index].PointId);
        }
    }

    private static void AddUniquePointId(List<int> destination, int pointId)
    {
        if (pointId < 0)
        {
            return;
        }

        for (int index = 0; index < destination.Count; index++)
        {
            if (destination[index] == pointId)
            {
                return;
            }
        }

        destination.Add(pointId);
    }

    private bool TryResolveSeedRegion(
        StandableRegionMap regionMap,
        IReadOnlyList<SurfacePoint> points,
        int seedPointId,
        Vector3 seedPosition,
        out StandableRegion region)
    {
        if (seedPointId >= 0 && regionMap.TryGetRegionByPointId(seedPointId, out region))
        {
            return true;
        }

        return regionMap.TryGetNearestRegion(seedPosition, out region);
    }

    private bool TryResolveVisitRegion(RouteRegionVisit visit, StandableRegionMap regionMap, out StandableRegion region)
    {
        if (regionMap.TryGetRegionByPointId(visit.SeedPointId, out region)
            || regionMap.TryGetRegionByPointId(visit.RegionAnchorPointId, out region))
        {
            return true;
        }

        return regionMap.TryGetNearestRegion(visit.SeedPosition, out region);
    }

    private void AddOrUpdateCurrentVisit(
        StandableRegion currentRegion,
        IReadOnlyList<SurfacePoint> points,
        int seedPointId)
    {
        int resolvedSeedPointId = seedPointId >= 0 && seedPointId < points.Count
            ? seedPointId
            : currentRegion.AnchorPointId;
        Vector3 seedPosition = resolvedSeedPointId >= 0 && resolvedSeedPointId < points.Count
            ? points[resolvedSeedPointId].Position
            : currentRegion.Center;
        if (committedVisits.Count == 0)
        {
            committedVisits.Add(new RouteRegionVisit(currentRegion.AnchorPointId, resolvedSeedPointId, seedPosition));
            RebuildCommittedPathPointIds();
            RebuildPreviewPath(points);
            return;
        }

        RouteRegionVisit last = committedVisits[committedVisits.Count - 1];
        if (currentRegion.ContainsPoint(last.RegionAnchorPointId) || currentRegion.ContainsPoint(last.SeedPointId))
        {
            committedVisits[committedVisits.Count - 1] = new RouteRegionVisit(
                currentRegion.AnchorPointId,
                resolvedSeedPointId,
                seedPosition);
            RebuildCommittedPathPointIds();
            RebuildPreviewPath(points);
            return;
        }

        committedVisits.Add(new RouteRegionVisit(currentRegion.AnchorPointId, resolvedSeedPointId, seedPosition));
        RebuildCommittedPathPointIds();
        RebuildPreviewPath(points);
    }


    private bool TryRetreatToPreviousIntermediateSeed(
        StandableRegionMap regionMap,
        IReadOnlyList<SurfacePoint> points,
        out Vector3 nextSeedPosition)
    {
        nextSeedPosition = default;
        if (committedSteps.Count == 0 || committedVisits.Count < 2)
        {
            return false;
        }

        int stepIndex = committedSteps.Count - 1;
        RouteCommittedStep step = committedSteps[stepIndex];
        if (step.IntermediatePointIds == null || step.IntermediatePointIds.Length == 0)
        {
            return false;
        }

        for (int index = step.IntermediatePointIds.Length - 1; index >= 0; index--)
        {
            int pointId = step.IntermediatePointIds[index];
            if (pointId < 0 || pointId >= points.Count || points[pointId].Kind != SurfaceKind.Standable)
            {
                continue;
            }

            RouteSeedKey seedKey = RouteSeedKey.From(points[pointId].Position, SeedQuantizationCellSize);
            if (sampledSeedKeys.Contains(seedKey))
            {
                continue;
            }

            if (!regionMap.TryGetRegionByPointId(pointId, out StandableRegion retreatRegion)
                || IsBlocked(retreatRegion))
            {
                continue;
            }

            int[]? retainedIntermediatePointIds = CopyIntermediatePrefix(step.IntermediatePointIds, index);
            committedSteps[stepIndex] = new RouteCommittedStep(
                step.SourcePointId,
                pointId,
                pointId,
                step.EdgeKind,
                retainedIntermediatePointIds);

            while (committedSteps.Count > stepIndex + 1)
            {
                committedSteps.RemoveAt(committedSteps.Count - 1);
            }

            while (committedVisits.Count > stepIndex + 2)
            {
                committedVisits.RemoveAt(committedVisits.Count - 1);
            }

            RouteRegionVisit retreatVisit = new(retreatRegion.AnchorPointId, pointId, points[pointId].Position);
            if (committedVisits.Count == stepIndex + 1)
            {
                committedVisits.Add(retreatVisit);
            }
            else
            {
                committedVisits[stepIndex + 1] = retreatVisit;
            }

            CurrentSeedPosition = points[pointId].Position;
            nextSeedPosition = CurrentSeedPosition;
            RebuildCommittedPathPointIds();
            RebuildPreviewPath(points);
            return true;
        }

        return false;
    }

    private static int[]? CopyIntermediatePrefix(int[] source, int count)
    {
        if (count <= 0)
        {
            return null;
        }

        int[] copy = new int[count];
        Array.Copy(source, copy, count);
        return copy;
    }

    private void CommitTransition(RouteTransition transition, int selectedSourceIndex, IReadOnlyList<SurfacePoint> points)
    {
        TrimPathToVisitIndex(selectedSourceIndex);
        committedSteps.Add(new RouteCommittedStep(
            transition.SourcePointId,
            transition.TargetPointId,
            transition.NextSeedPointId,
            transition.EdgeKind,
            transition.IntermediatePointIds));
        SurfacePoint nextSeedPoint = points[transition.NextSeedPointId];
        committedVisits.Add(new RouteRegionVisit(
            transition.TargetRegion.AnchorPointId,
            transition.NextSeedPointId,
            nextSeedPoint.Position));
        CurrentSeedPosition = nextSeedPoint.Position;
        sampledSeedKeys.Add(RouteSeedKey.From(CurrentSeedPosition, SeedQuantizationCellSize));
        MarkTransitionSeedKeys(transition, points);
        RebuildCommittedPathPointIds();
        RebuildPreviewPath(points);
    }

    private void TrimPathToVisitIndex(int visitIndex)
    {
        int targetVisitCount = Mathf.Clamp(visitIndex + 1, 1, committedVisits.Count);
        while (committedVisits.Count > targetVisitCount)
        {
            committedVisits.RemoveAt(committedVisits.Count - 1);
        }

        while (committedSteps.Count > committedVisits.Count - 1)
        {
            committedSteps.RemoveAt(committedSteps.Count - 1);
        }

        RebuildCommittedPathPointIds();
    }

    private void MarkTransitionSeedKeys(RouteTransition transition, IReadOnlyList<SurfacePoint> points)
    {
        MarkSeedKeyIfPointValid(transition.SourcePointId, points);
        MarkSeedKeyIfPointValid(transition.TargetPointId, points);
        MarkSeedKeyIfPointValid(transition.NextSeedPointId, points);
        // Intermediate route-preview points are not sampling seeds. Keeping them unmarked lets
        // a later dead-end retreat rescan from the last valid intermediate standable point.
    }

    private void MarkSeedKeyIfPointValid(int pointId, IReadOnlyList<SurfacePoint> points)
    {
        if (pointId >= 0 && pointId < points.Count)
        {
            sampledSeedKeys.Add(RouteSeedKey.From(points[pointId].Position, SeedQuantizationCellSize));
        }
    }

    private void RebuildCommittedPathPointIds()
    {
        committedPathPointIds.Clear();
        for (int index = 0; index < committedVisits.Count; index++)
        {
            RouteRegionVisit visit = committedVisits[index];
            AddCommittedPathPointId(visit.RegionAnchorPointId);
            AddCommittedPathPointId(visit.SeedPointId);
        }

        for (int index = 0; index < committedSteps.Count; index++)
        {
            RouteCommittedStep step = committedSteps[index];
            AddCommittedPathPointId(step.SourcePointId);
            AddCommittedPathPointId(step.TargetPointId);
            AddCommittedPathPointId(step.SeedPointId);
            if (step.IntermediatePointIds == null)
            {
                continue;
            }

            for (int intermediateIndex = 0; intermediateIndex < step.IntermediatePointIds.Length; intermediateIndex++)
            {
                AddCommittedPathPointId(step.IntermediatePointIds[intermediateIndex]);
            }
        }
    }

    private void AddCommittedPathPointId(int pointId)
    {
        if (pointId >= 0)
        {
            committedPathPointIds.Add(pointId);
        }
    }

    private void RebuildPreviewPath(IReadOnlyList<SurfacePoint> points)
    {
        previewPath.Clear();
        AppendPreviewPoint(startPosition);
        for (int index = 0; index < committedSteps.Count; index++)
        {
            RouteCommittedStep step = committedSteps[index];
            AppendPointIfValid(points, step.SourcePointId);
            if (step.IntermediatePointIds != null)
            {
                for (int intermediateIndex = 0; intermediateIndex < step.IntermediatePointIds.Length; intermediateIndex++)
                {
                    AppendPointIfValid(points, step.IntermediatePointIds[intermediateIndex]);
                }
            }

            AppendPointIfValid(points, step.TargetPointId);
            AppendPointIfValid(points, step.SeedPointId);
        }

        if (committedVisits.Count > 0)
        {
            RouteRegionVisit last = committedVisits[committedVisits.Count - 1];
            AppendPreviewPoint(last.SeedPosition);
        }
    }

    private void AppendPointIfValid(IReadOnlyList<SurfacePoint> points, int pointId)
    {
        if (pointId >= 0 && pointId < points.Count)
        {
            AppendPreviewPoint(points[pointId].Position);
        }
    }

    private void AppendPreviewPoint(Vector3 point)
    {
        if (previewPath.Count > 0 && Vector3.Distance(previewPath[previewPath.Count - 1], point) <= 0.05f)
        {
            return;
        }

        previewPath.Add(point);
    }

    private void CompleteAtCurrentRegion(IReadOnlyList<SurfacePoint> points, int completionPointId)
    {
        IsComplete = true;
        IsFailed = false;
        if (completionPointId >= 0 && completionPointId < points.Count)
        {
            AppendPreviewPoint(points[completionPointId].Position);
        }

        AppendPreviewPoint(targetPosition);
    }

    private void MarkBlocked(StandableRegion region)
    {
        if (committedVisits.Count == 0)
        {
            return;
        }

        if (committedVisits.Count <= 1 && region.ContainsPoint(committedVisits[0].RegionAnchorPointId))
        {
            return;
        }

        for (int index = 0; index < region.PointIds.Count; index++)
        {
            blockedPointIds.Add(region.PointIds[index]);
        }
    }

    private bool IsRegionAtTarget(StandableRegion region)
    {
        return region.DistanceToTarget <= settings.TargetReachedDistance;
    }

    private bool IsPointAtTarget(int pointId, IReadOnlyList<SurfacePoint> points)
    {
        return pointId >= 0
            && pointId < points.Count
            && Vector3.Distance(points[pointId].Position, targetPosition) <= settings.TargetReachedDistance;
    }

    private bool IsBlocked(StandableRegion region)
    {
        for (int index = 0; index < region.PointIds.Count; index++)
        {
            if (blockedPointIds.Contains(region.PointIds[index]))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsRegionInCommittedPath(StandableRegion region)
    {
        for (int index = 0; index < region.PointIds.Count; index++)
        {
            if (committedPathPointIds.Contains(region.PointIds[index]))
            {
                return true;
            }
        }

        for (int index = 0; index < committedVisits.Count; index++)
        {
            RouteRegionVisit visit = committedVisits[index];
            if (region.ContainsPoint(visit.RegionAnchorPointId) || region.ContainsPoint(visit.SeedPointId))
            {
                return true;
            }
        }

        return false;
    }

    private enum RouteCandidateMode
    {
        TargetGreedy,
        TargetForward,
        SourceDetour,
    }

    private readonly struct RouteRegionCandidate
    {
        internal RouteRegionCandidate(StandableRegion region, float score)
        {
            Region = region;
            Score = score;
        }

        internal StandableRegion Region { get; }

        internal float Score { get; }
    }

    private readonly struct RouteProbePointScore
    {
        internal RouteProbePointScore(int pointId, float score)
        {
            PointId = pointId;
            Score = score;
        }

        internal int PointId { get; }

        internal float Score { get; }
    }

    private readonly struct RouteProbePair
    {
        internal RouteProbePair(int sourcePointId, int targetPointId, float score)
        {
            SourcePointId = sourcePointId;
            TargetPointId = targetPointId;
            Score = score;
        }

        internal int SourcePointId { get; }

        internal int TargetPointId { get; }

        internal float Score { get; }
    }

    private readonly struct RouteGraphEdge
    {
        internal RouteGraphEdge(int sourcePointId, int targetPointId, RouteEdgeKind kind, float distance, float staminaCost)
        {
            SourcePointId = sourcePointId;
            TargetPointId = targetPointId;
            Kind = kind;
            Distance = distance;
            StaminaCost = staminaCost;
        }

        internal int SourcePointId { get; }

        internal int TargetPointId { get; }

        internal RouteEdgeKind Kind { get; }

        internal float Distance { get; }

        internal float StaminaCost { get; }
    }

    private readonly struct RouteGraphNode
    {
        internal RouteGraphNode(
            int regionId,
            int parentNodeIndex,
            int sourcePointId,
            int entryPointId,
            RouteEdgeKind edgeKind,
            float edgeDistance,
            float edgeStaminaCost,
            float cost,
            int hops)
        {
            RegionId = regionId;
            ParentNodeIndex = parentNodeIndex;
            SourcePointId = sourcePointId;
            EntryPointId = entryPointId;
            EdgeKind = edgeKind;
            EdgeDistance = edgeDistance;
            EdgeStaminaCost = edgeStaminaCost;
            Cost = cost;
            Hops = hops;
        }

        internal int RegionId { get; }

        internal int ParentNodeIndex { get; }

        internal int SourcePointId { get; }

        internal int EntryPointId { get; }

        internal RouteEdgeKind EdgeKind { get; }

        internal float EdgeDistance { get; }

        internal float EdgeStaminaCost { get; }

        internal float Cost { get; }

        internal int Hops { get; }
    }

    private readonly struct RouteClimbPointScore
    {
        internal RouteClimbPointScore(int pointId, float score)
        {
            PointId = pointId;
            Score = score;
        }

        internal int PointId { get; }

        internal float Score { get; }
    }

    private readonly struct RouteClimbNeighborScore
    {
        internal RouteClimbNeighborScore(int candidateIndex, float score)
        {
            CandidateIndex = candidateIndex;
            Score = score;
        }

        internal int CandidateIndex { get; }

        internal float Score { get; }
    }

    private readonly struct RouteSurfaceEdgeKey : IEquatable<RouteSurfaceEdgeKey>
    {
        internal RouteSurfaceEdgeKey(int sourcePointId, int targetPointId, int pointCount)
        {
            SourcePointId = sourcePointId;
            TargetPointId = targetPointId;
        }

        private int SourcePointId { get; }

        private int TargetPointId { get; }

        public bool Equals(RouteSurfaceEdgeKey other)
        {
            return SourcePointId == other.SourcePointId
                && TargetPointId == other.TargetPointId;
        }

        public override bool Equals(object? obj)
        {
            return obj is RouteSurfaceEdgeKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = SourcePointId;
                hash = (hash * 397) ^ TargetPointId;
                return hash;
            }
        }
    }

    private readonly struct RouteRegionVisit
    {
        internal RouteRegionVisit(int regionAnchorPointId, int seedPointId, Vector3 seedPosition)
        {
            RegionAnchorPointId = regionAnchorPointId;
            SeedPointId = seedPointId;
            SeedPosition = seedPosition;
        }

        internal int RegionAnchorPointId { get; }

        internal int SeedPointId { get; }

        internal Vector3 SeedPosition { get; }
    }

    private readonly struct RouteCommittedStep
    {
        internal RouteCommittedStep(
            int sourcePointId,
            int targetPointId,
            int seedPointId,
            RouteEdgeKind edgeKind,
            int[]? intermediatePointIds)
        {
            SourcePointId = sourcePointId;
            TargetPointId = targetPointId;
            SeedPointId = seedPointId;
            EdgeKind = edgeKind;
            IntermediatePointIds = intermediatePointIds;
        }

        internal int SourcePointId { get; }

        internal int TargetPointId { get; }

        internal int SeedPointId { get; }

        internal RouteEdgeKind EdgeKind { get; }

        internal int[]? IntermediatePointIds { get; }
    }

    private readonly struct RouteTransition
    {
        internal RouteTransition(
            StandableRegion sourceRegion,
            StandableRegion targetRegion,
            int sourcePointId,
            int targetPointId,
            int nextSeedPointId,
            RouteEdgeKind edgeKind,
            float distance,
            float staminaCost,
            bool isSameRegionFrontier,
            int[]? intermediatePointIds = null)
        {
            SourceRegion = sourceRegion;
            TargetRegion = targetRegion;
            SourcePointId = sourcePointId;
            TargetPointId = targetPointId;
            NextSeedPointId = nextSeedPointId;
            EdgeKind = edgeKind;
            Distance = distance;
            StaminaCost = staminaCost;
            IsSameRegionFrontier = isSameRegionFrontier;
            IntermediatePointIds = intermediatePointIds;
        }

        internal StandableRegion SourceRegion { get; }

        internal StandableRegion TargetRegion { get; }

        internal int SourcePointId { get; }

        internal int TargetPointId { get; }

        internal int NextSeedPointId { get; }

        internal RouteEdgeKind EdgeKind { get; }

        internal float Distance { get; }

        internal float StaminaCost { get; }

        internal bool IsSameRegionFrontier { get; }

        internal int[]? IntermediatePointIds { get; }
    }

    private readonly struct RouteSeedKey : IEquatable<RouteSeedKey>
    {
        private RouteSeedKey(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        private int X { get; }

        private int Y { get; }

        private int Z { get; }

        internal static RouteSeedKey From(Vector3 position, float cellSize)
        {
            float scale = 1f / Mathf.Max(0.1f, cellSize);
            return new RouteSeedKey(
                Mathf.RoundToInt(position.x * scale),
                Mathf.RoundToInt(position.y * scale),
                Mathf.RoundToInt(position.z * scale));
        }

        public bool Equals(RouteSeedKey other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object? obj)
        {
            return obj is RouteSeedKey other && Equals(other);
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

    private readonly struct RouteCandidateAttemptKey : IEquatable<RouteCandidateAttemptKey>
    {
        internal RouteCandidateAttemptKey(
            int sourceAnchorPointId,
            int targetAnchorPointId,
            int sourcePointCount,
            int targetPointCount,
            int totalPointCount)
        {
            SourceAnchorPointId = sourceAnchorPointId;
            TargetAnchorPointId = targetAnchorPointId;
            SourcePointCount = sourcePointCount;
            TargetPointCount = targetPointCount;
            TotalPointCount = totalPointCount;
        }

        private int SourceAnchorPointId { get; }

        private int TargetAnchorPointId { get; }

        private int SourcePointCount { get; }

        private int TargetPointCount { get; }

        private int TotalPointCount { get; }

        public bool Equals(RouteCandidateAttemptKey other)
        {
            return SourceAnchorPointId == other.SourceAnchorPointId
                && TargetAnchorPointId == other.TargetAnchorPointId
                && SourcePointCount == other.SourcePointCount
                && TargetPointCount == other.TargetPointCount
                && TotalPointCount == other.TotalPointCount;
        }

        public override bool Equals(object? obj)
        {
            return obj is RouteCandidateAttemptKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = SourceAnchorPointId;
                hash = (hash * 397) ^ TargetAnchorPointId;
                hash = (hash * 397) ^ SourcePointCount;
                hash = (hash * 397) ^ TargetPointCount;
                hash = (hash * 397) ^ TotalPointCount;
                return hash;
            }
        }
    }
}

internal readonly struct RouteSearchDecision
{
    private RouteSearchDecision(
        bool shouldSampleNext,
        bool isComplete,
        bool isFailed,
        Vector3 nextSeedPosition,
        string reason,
        int stepIndex,
        int regionCount,
        int blockedPointCount)
    {
        ShouldSampleNext = shouldSampleNext;
        IsComplete = isComplete;
        IsFailed = isFailed;
        NextSeedPosition = nextSeedPosition;
        Reason = reason;
        StepIndex = stepIndex;
        RegionCount = regionCount;
        BlockedPointCount = blockedPointCount;
    }

    internal bool ShouldSampleNext { get; }

    internal bool IsComplete { get; }

    internal bool IsFailed { get; }

    internal Vector3 NextSeedPosition { get; }

    internal string Reason { get; }

    internal int StepIndex { get; }

    internal int RegionCount { get; }

    internal int BlockedPointCount { get; }

    internal static RouteSearchDecision NextSeed(
        Vector3 nextSeedPosition,
        string reason,
        int stepIndex,
        int regionCount,
        int blockedPointCount)
    {
        return new RouteSearchDecision(true, false, false, nextSeedPosition, reason, stepIndex, regionCount, blockedPointCount);
    }

    internal static RouteSearchDecision Complete(string reason, int stepIndex, int regionCount, int blockedPointCount)
    {
        return new RouteSearchDecision(false, true, false, default, reason, stepIndex, regionCount, blockedPointCount);
    }

    internal static RouteSearchDecision Failed(string reason, int stepIndex, int regionCount, int blockedPointCount)
    {
        return new RouteSearchDecision(false, false, true, default, reason, stepIndex, regionCount, blockedPointCount);
    }
}

internal sealed class StandableRegionMap
{
    private StandableRegionMap(
        IReadOnlyList<StandableRegion> regions,
        int[] regionIdByPointId)
    {
        Regions = regions;
        this.regionIdByPointId = regionIdByPointId;
    }

    private readonly int[] regionIdByPointId;

    internal IReadOnlyList<StandableRegion> Regions { get; }

    internal static StandableRegionMap Build(
        IReadOnlyList<SurfacePoint> points,
        PlannerConfig config,
        RouteSearchSettings settings,
        Vector3 targetPosition)
    {
        int pointCount = points.Count;
        int[] parent = new int[pointCount];
        int[] rank = new int[pointCount];
        int[] regionIdByPointId = new int[pointCount];
        Array.Fill(regionIdByPointId, -1);
        for (int index = 0; index < pointCount; index++)
        {
            parent[index] = index;
        }

        float mergeDistance = Mathf.Max(settings.RegionMergeDistance, config.SurfaceNeighborDistance * 1.35f);
        float mergeDistanceSqr = mergeDistance * mergeDistance;
        float maxVerticalDelta = Mathf.Max(0.35f, config.MaxWalkStepUpHeight + 0.2f);
        Dictionary<StandableRegionCellKey, List<int>> cells = [];
        for (int index = 0; index < pointCount; index++)
        {
            SurfacePoint point = points[index];
            if (point.Kind != SurfaceKind.Standable)
            {
                continue;
            }

            StandableRegionCellKey cellKey = StandableRegionCellKey.From(point.Position, mergeDistance);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        StandableRegionCellKey neighborKey = cellKey.Offset(dx, dy, dz);
                        if (!cells.TryGetValue(neighborKey, out List<int> neighborPointIds))
                        {
                            continue;
                        }

                        for (int neighborIndex = 0; neighborIndex < neighborPointIds.Count; neighborIndex++)
                        {
                            int neighborPointId = neighborPointIds[neighborIndex];
                            SurfacePoint neighbor = points[neighborPointId];
                            float verticalDelta = Mathf.Abs(point.Position.y - neighbor.Position.y);
                            Vector2 pointHorizontal = new(point.Position.x, point.Position.z);
                            Vector2 neighborHorizontal = new(neighbor.Position.x, neighbor.Position.z);
                            if (verticalDelta <= maxVerticalDelta
                                && (pointHorizontal - neighborHorizontal).sqrMagnitude <= mergeDistanceSqr)
                            {
                                Union(parent, rank, index, neighborPointId);
                            }
                        }
                    }
                }
            }

            if (!cells.TryGetValue(cellKey, out List<int> pointIds))
            {
                pointIds = [];
                cells[cellKey] = pointIds;
            }

            pointIds.Add(index);
        }

        Dictionary<int, List<int>> pointIdsByRoot = [];
        for (int index = 0; index < pointCount; index++)
        {
            if (points[index].Kind != SurfaceKind.Standable)
            {
                continue;
            }

            int root = Find(parent, index);
            if (!pointIdsByRoot.TryGetValue(root, out List<int> regionPointIds))
            {
                regionPointIds = [];
                pointIdsByRoot[root] = regionPointIds;
            }

            regionPointIds.Add(index);
        }

        List<StandableRegion> regions = [];
        foreach (List<int> regionPointIds in pointIdsByRoot.Values)
        {
            if (regionPointIds.Count == 0)
            {
                continue;
            }

            int id = regions.Count;
            StandableRegion region = StandableRegion.Create(id, regionPointIds, points, targetPosition);
            regions.Add(region);
            for (int index = 0; index < regionPointIds.Count; index++)
            {
                int pointId = regionPointIds[index];
                if (pointId >= 0 && pointId < regionIdByPointId.Length)
                {
                    regionIdByPointId[pointId] = id;
                }
            }
        }

        return new StandableRegionMap(regions, regionIdByPointId);
    }

    internal bool TryGetRegionByPointId(int pointId, out StandableRegion region)
    {
        if (pointId >= 0 && pointId < regionIdByPointId.Length)
        {
            int regionId = regionIdByPointId[pointId];
            if (regionId >= 0 && regionId < Regions.Count)
            {
                region = Regions[regionId];
                return true;
            }
        }

        region = default;
        return false;
    }

    internal bool TryGetNearestRegion(Vector3 position, out StandableRegion region)
    {
        region = default;
        bool found = false;
        float bestDistanceSqr = float.MaxValue;
        for (int index = 0; index < Regions.Count; index++)
        {
            StandableRegion candidate = Regions[index];
            float distanceSqr = (candidate.Center - position).sqrMagnitude;
            if (found && distanceSqr >= bestDistanceSqr)
            {
                continue;
            }

            region = candidate;
            bestDistanceSqr = distanceSqr;
            found = true;
        }

        return found;
    }

    private static int Find(int[] parent, int value)
    {
        int root = value;
        while (parent[root] != root)
        {
            root = parent[root];
        }

        while (parent[value] != value)
        {
            int next = parent[value];
            parent[value] = root;
            value = next;
        }

        return root;
    }

    private static void Union(int[] parent, int[] rank, int left, int right)
    {
        int leftRoot = Find(parent, left);
        int rightRoot = Find(parent, right);
        if (leftRoot == rightRoot)
        {
            return;
        }

        if (rank[leftRoot] < rank[rightRoot])
        {
            parent[leftRoot] = rightRoot;
        }
        else if (rank[leftRoot] > rank[rightRoot])
        {
            parent[rightRoot] = leftRoot;
        }
        else
        {
            parent[rightRoot] = leftRoot;
            rank[leftRoot]++;
        }
    }
}

internal readonly struct StandableRegion
{
    private readonly HashSet<int>? pointIdSet;

    private StandableRegion(
        int id,
        IReadOnlyList<int> pointIds,
        HashSet<int> pointIdSet,
        Vector3 center,
        int anchorPointId,
        int centerPointId,
        int closestToTargetPointId,
        float distanceToTarget)
    {
        Id = id;
        PointIds = pointIds;
        this.pointIdSet = pointIdSet;
        Center = center;
        AnchorPointId = anchorPointId;
        CenterPointId = centerPointId;
        ClosestToTargetPointId = closestToTargetPointId;
        DistanceToTarget = distanceToTarget;
    }

    internal int Id { get; }

    internal IReadOnlyList<int> PointIds { get; }

    internal Vector3 Center { get; }

    internal int AnchorPointId { get; }

    internal int CenterPointId { get; }

    internal int ClosestToTargetPointId { get; }

    internal float DistanceToTarget { get; }

    internal static StandableRegion Create(
        int id,
        List<int> sourcePointIds,
        IReadOnlyList<SurfacePoint> points,
        Vector3 targetPosition)
    {
        sourcePointIds.Sort();
        List<int> pointIds = [..sourcePointIds];
        HashSet<int> pointIdSet = [..pointIds];
        Vector3 sum = Vector3.zero;
        for (int index = 0; index < pointIds.Count; index++)
        {
            sum += points[pointIds[index]].Position;
        }

        Vector3 center = pointIds.Count > 0 ? sum / pointIds.Count : Vector3.zero;
        int anchorPointId = pointIds.Count > 0 ? pointIds[0] : -1;
        int centerPointId = anchorPointId;
        int closestToTargetPointId = anchorPointId;
        float bestCenterDistanceSqr = float.MaxValue;
        float bestTargetDistance = float.MaxValue;
        for (int index = 0; index < pointIds.Count; index++)
        {
            int pointId = pointIds[index];
            Vector3 position = points[pointId].Position;
            float centerDistanceSqr = (position - center).sqrMagnitude;
            if (centerDistanceSqr < bestCenterDistanceSqr)
            {
                centerPointId = pointId;
                bestCenterDistanceSqr = centerDistanceSqr;
            }

            float targetDistance = Vector3.Distance(position, targetPosition);
            if (targetDistance < bestTargetDistance)
            {
                closestToTargetPointId = pointId;
                bestTargetDistance = targetDistance;
            }
        }

        return new StandableRegion(
            id,
            pointIds,
            pointIdSet,
            center,
            anchorPointId,
            centerPointId,
            closestToTargetPointId,
            bestTargetDistance);
    }

    internal bool ContainsPoint(int pointId)
    {
        return pointId >= 0 && pointIdSet != null && pointIdSet.Contains(pointId);
    }
}

internal readonly struct StandableRegionCellKey : IEquatable<StandableRegionCellKey>
{
    private StandableRegionCellKey(int x, int y, int z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    private int X { get; }

    private int Y { get; }

    private int Z { get; }

    internal static StandableRegionCellKey From(Vector3 position, float cellSize)
    {
        float scale = 1f / Mathf.Max(0.1f, cellSize);
        return new StandableRegionCellKey(
            Mathf.FloorToInt(position.x * scale),
            Mathf.FloorToInt(position.y * scale),
            Mathf.FloorToInt(position.z * scale));
    }

    internal StandableRegionCellKey Offset(int dx, int dy, int dz)
    {
        return new StandableRegionCellKey(X + dx, Y + dy, Z + dz);
    }

    public bool Equals(StandableRegionCellKey other)
    {
        return X == other.X && Y == other.Y && Z == other.Z;
    }

    public override bool Equals(object? obj)
    {
        return obj is StandableRegionCellKey other && Equals(other);
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
