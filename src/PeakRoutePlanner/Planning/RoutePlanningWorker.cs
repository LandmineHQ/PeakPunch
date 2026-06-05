using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace PeakRoutePlanner.Planning;

internal static class RoutePlanningWorker
{
    private const float InfiniteCost = float.PositiveInfinity;
    private const float CostEpsilon = 0.001f;
    private const float MinimumUsefulProgress = 0.05f;
    private const float MinimumAdaptivePreviewStep = 0.5f;
    private const float GuideProgressTieTolerance = 0.05f;
    private const float RecentFrontierPenaltyRadius = 3f;
    private const float RecentFrontierPenaltyCost = 28f;
    private const float BlockedFrontierPenaltyRadius = 7f;
    private const float BlockedFrontierCurrentStartRadius = 2f;
    private const float BlockedFrontierPenaltyCost = 140f;
    private const int MaxAdaptiveBinarySteps = 20;

    internal static RouteResult Plan(RoutePlannerSnapshot snapshot, CancellationToken cancellationToken)
    {
        RouteResult result = new()
        {
            SampledPointCount = snapshot.Points.Count,
            ValidEdgeCount = snapshot.Edges.Count,
            CorridorRadius = snapshot.CorridorRadius,
        };

        if (snapshot.StartIndex < 0
            || snapshot.StartIndex >= snapshot.Points.Count
            || snapshot.TargetIndex < 0
            || snapshot.TargetIndex >= snapshot.Points.Count
            || snapshot.Edges.Count == 0)
        {
            return result;
        }

        RouteGraph graph = BuildGraph(snapshot.Points.Count, snapshot.Edges);
        GuidePointMetrics guideMetrics = GuideProjectionMap.Build(snapshot.GuidePath).BuildPointMetrics(snapshot.Points);
        RegionMap regionMap = BuildStandableRegionMap(snapshot, graph, cancellationToken);
        SearchResult regionWalkResult = TrySearchSameStandableRegionRoute(
            snapshot,
            graph,
            guideMetrics,
            regionMap,
            cancellationToken);
        if (regionWalkResult.Found)
        {
            ApplySearchResult(result, snapshot, regionWalkResult);
            return result;
        }

        float[] distanceField = BuildTargetDistanceField(snapshot, graph, regionMap, cancellationToken);
        SearchResult searchResult = Search(snapshot, graph, guideMetrics, distanceField, cancellationToken);
        if (!searchResult.Found)
        {
            return result;
        }

        ApplySearchResult(result, snapshot, searchResult);
        return result;
    }

    private static void ApplySearchResult(RouteResult result, RoutePlannerSnapshot snapshot, SearchResult searchResult)
    {
        result.Found = true;
        result.IsPartial = searchResult.IsPartial;
        result.TotalDistance = searchResult.Distance;
        result.TotalStaminaCost = searchResult.StaminaCost;
        foreach (int node in searchResult.Nodes)
        {
            result.NodeIds.Add(node);
            Vector3 position = snapshot.Points[node].Position;
            if (result.Path.Count == 0 || (result.Path[result.Path.Count - 1] - position).sqrMagnitude > 0.0001f)
            {
                result.Path.Add(position);
            }
        }
    }

    private static SearchResult Search(
        RoutePlannerSnapshot snapshot,
        RouteGraph graph,
        GuidePointMetrics guideMetrics,
        float[] distanceField,
        CancellationToken cancellationToken)
    {
        int bucketCount = Mathf.Max(2, snapshot.Config.StaminaBuckets);
        int stateCount = snapshot.Points.Count * bucketCount;
        float[] bestCost = CreateFilled(stateCount, InfiniteCost);
        float[] bestDistance = new float[stateCount];
        float[] bestStaminaCost = new float[stateCount];
        int[] previousState = CreateFilled(stateCount, -1);
        int[] previousNode = CreateFilled(stateCount, -1);
        PriorityQueue<int> open = new();

        int startState = GetStateIndex(snapshot.StartIndex, bucketCount - 1, bucketCount);
        bestCost[startState] = 0f;
        open.Enqueue(startState, GetHeuristic(snapshot.StartIndex, snapshot, guideMetrics, distanceField));

        int expanded = 0;
        int bestGoalState = -1;
        float bestGoalCost = InfiniteCost;
        while (open.Count > 0 && expanded < snapshot.Config.SearchMaxExpandedStates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int currentState = open.Dequeue();
            int currentNode = GetNodeIndex(currentState, bucketCount);
            int currentBucket = GetBucketIndex(currentState, bucketCount);
            float currentCost = bestCost[currentState];
            if (currentNode == snapshot.TargetIndex)
            {
                if (currentCost < bestGoalCost)
                {
                    bestGoalState = currentState;
                    bestGoalCost = currentCost;
                }

                break;
            }

            expanded++;
            float currentStamina = BucketToStamina(currentBucket, bucketCount);
            foreach (GraphEdge edge in graph.Adjacency[currentNode])
            {
                if (currentStamina + 0.001f < edge.StaminaCost)
                {
                    continue;
                }

                float nextStamina = currentStamina - edge.StaminaCost;
                if (edge.To == snapshot.TargetIndex || snapshot.Points[edge.To].Kind == SurfaceKind.Standable)
                {
                    nextStamina = 1f;
                }

                int nextBucket = StaminaToBucket(nextStamina, bucketCount);
                int nextState = GetStateIndex(edge.To, nextBucket, bucketCount);
                float transitionCost = GetTransitionCost(currentNode, edge, snapshot, guideMetrics);
                float nextCost = currentCost + transitionCost;
                if (nextCost + CostEpsilon >= bestCost[nextState])
                {
                    continue;
                }

                bestCost[nextState] = nextCost;
                bestDistance[nextState] = bestDistance[currentState] + edge.Distance;
                bestStaminaCost[nextState] = bestStaminaCost[currentState] + edge.StaminaCost;
                previousState[nextState] = currentState;
                previousNode[nextState] = currentNode;
                float priority = nextCost + GetHeuristic(edge.To, snapshot, guideMetrics, distanceField);
                open.Enqueue(nextState, priority);
            }
        }

        if (bestGoalState >= 0)
        {
            return ReconstructSearchResult(
                bestGoalState,
                snapshot.StartIndex,
                bucketCount,
                previousState,
                previousNode,
                bestDistance,
                bestStaminaCost,
                isPartial: false);
        }

        int partialState = -1;
        if (snapshot.PreferRecoveryDetour)
        {
            partialState = FindRecoveryPartialGoalState(
                snapshot,
                guideMetrics,
                bucketCount,
                bestCost,
                bestDistance,
                requireCommittableDistance: true);
        }

        if (partialState < 0 && snapshot.PreferRecoveryDetour)
        {
            partialState = FindRecoveryPartialGoalState(
                snapshot,
                guideMetrics,
                bucketCount,
                bestCost,
                bestDistance,
                requireCommittableDistance: false);
        }

        if (partialState < 0)
        {
            partialState = FindAdaptivePartialGoalState(
                snapshot,
                guideMetrics,
                bucketCount,
                bestCost,
                bestDistance,
                requireCommittableDistance: true);
        }

        if (partialState < 0)
        {
            partialState = FindClosestPartialGoalState(
                snapshot,
                guideMetrics,
                distanceField,
                bucketCount,
                bestCost,
                bestDistance,
                requireCommittableDistance: true);
        }

        if (partialState < 0)
        {
            partialState = FindDetourPartialGoalState(
                snapshot,
                guideMetrics,
                distanceField,
                bucketCount,
                bestCost,
                bestDistance,
                requireCommittableDistance: true);
        }

        if (partialState < 0)
        {
            partialState = FindAdaptivePartialGoalState(
                snapshot,
                guideMetrics,
                bucketCount,
                bestCost,
                bestDistance,
                requireCommittableDistance: false);
        }

        if (partialState < 0)
        {
            partialState = FindClosestPartialGoalState(
                snapshot,
                guideMetrics,
                distanceField,
                bucketCount,
                bestCost,
                bestDistance,
                requireCommittableDistance: false);
        }

        if (partialState < 0)
        {
            partialState = FindDetourPartialGoalState(
                snapshot,
                guideMetrics,
                distanceField,
                bucketCount,
                bestCost,
                bestDistance,
                requireCommittableDistance: false);
        }

        return partialState >= 0
            ? ReconstructSearchResult(
                partialState,
                snapshot.StartIndex,
                bucketCount,
                previousState,
                previousNode,
                bestDistance,
                bestStaminaCost,
                isPartial: true)
            : SearchResult.Empty;
    }

    private static int FindDetourPartialGoalState(
        RoutePlannerSnapshot snapshot,
        GuidePointMetrics guideMetrics,
        IReadOnlyList<float> distanceField,
        int bucketCount,
        IReadOnlyList<float> bestCost,
        IReadOnlyList<float> bestDistance,
        bool requireCommittableDistance)
    {
        int bestState = -1;
        float bestScore = InfiniteCost;
        for (int node = 0; node < snapshot.Points.Count; node++)
        {
            if (node == snapshot.StartIndex)
            {
                continue;
            }

            float targetFieldDistance = node < distanceField.Count && !float.IsInfinity(distanceField[node])
                ? distanceField[node]
                : Vector3.Distance(snapshot.Points[node].Position, snapshot.Points[snapshot.TargetIndex].Position);
            float progressGain = guideMetrics.Progress[node] - guideMetrics.Progress[snapshot.StartIndex];
            float backtrackPenalty = progressGain < 0f
                ? -progressGain * snapshot.Config.SearchBacktrackPenaltyMultiplier
                : 0f;
            float weakProgressPenalty = progressGain < MinimumUsefulProgress
                ? Mathf.Max(2f, snapshot.CorridorRadius)
                : 0f;
            float progressReward = Mathf.Max(0f, progressGain) * 0.25f;
            float recentPenalty = GetRecentFrontierPenalty(snapshot, guideMetrics, node);
            float blockedPenalty = GetBlockedFrontierPenalty(snapshot, node);
            for (int bucket = 0; bucket < bucketCount; bucket++)
            {
                int state = GetStateIndex(node, bucket, bucketCount);
                float cost = bestCost[state];
                if (float.IsInfinity(cost)
                    || !IsUsablePartialDistance(bestDistance[state], snapshot, requireCommittableDistance))
                {
                    continue;
                }

                float score = targetFieldDistance
                    + guideMetrics.Distances[node] * snapshot.Config.SearchGuideDistanceWeight
                    + cost * 0.15f
                    + backtrackPenalty
                    + weakProgressPenalty
                    + recentPenalty
                    + blockedPenalty
                    - progressReward
                    - BucketToStamina(bucket, bucketCount) * 0.05f;
                if (score >= bestScore)
                {
                    continue;
                }

                bestScore = score;
                bestState = state;
            }
        }

        return bestState;
    }

    private static int FindClosestPartialGoalState(
        RoutePlannerSnapshot snapshot,
        GuidePointMetrics guideMetrics,
        IReadOnlyList<float> distanceField,
        int bucketCount,
        IReadOnlyList<float> bestCost,
        IReadOnlyList<float> bestDistance,
        bool requireCommittableDistance)
    {
        int bestState = -1;
        float bestScore = InfiniteCost;
        float startProgress = guideMetrics.Progress[snapshot.StartIndex];
        float startTargetDistance = Vector3.Distance(
            snapshot.Points[snapshot.StartIndex].Position,
            snapshot.Points[snapshot.TargetIndex].Position);
        for (int node = 0; node < snapshot.Points.Count; node++)
        {
            if (node == snapshot.StartIndex)
            {
                continue;
            }

            float targetDistance = Vector3.Distance(
                snapshot.Points[node].Position,
                snapshot.Points[snapshot.TargetIndex].Position);
            float progressGain = guideMetrics.Progress[node] - startProgress;
            bool makesImmediateProgress = targetDistance + 0.25f < startTargetDistance
                || progressGain > MinimumUsefulProgress;
            float detourPenalty = makesImmediateProgress
                ? 0f
                : Mathf.Max(2f, snapshot.CorridorRadius) * 2f;
            float progressReward = Mathf.Max(0f, progressGain) * 0.35f;
            float recentPenalty = GetRecentFrontierPenalty(snapshot, guideMetrics, node);
            float blockedPenalty = GetBlockedFrontierPenalty(snapshot, node);
            float targetFieldDistance = node < distanceField.Count && !float.IsInfinity(distanceField[node])
                ? distanceField[node]
                : targetDistance + Mathf.Max(0f, guideMetrics.TotalLength - guideMetrics.Progress[node]) * 0.2f;
            for (int bucket = 0; bucket < bucketCount; bucket++)
            {
                int state = GetStateIndex(node, bucket, bucketCount);
                float cost = bestCost[state];
                if (float.IsInfinity(cost)
                    || !IsUsablePartialDistance(bestDistance[state], snapshot, requireCommittableDistance))
                {
                    continue;
                }

                float score = targetFieldDistance
                    + guideMetrics.Distances[node] * snapshot.Config.SearchGuideDistanceWeight
                    + cost * 0.05f
                    + detourPenalty
                    + recentPenalty
                    + blockedPenalty
                    - progressReward
                    - BucketToStamina(bucket, bucketCount) * 0.05f;
                if (score >= bestScore)
                {
                    continue;
                }

                bestScore = score;
                bestState = state;
            }
        }

        return bestState;
    }

    private static int FindAdaptivePartialGoalState(
        RoutePlannerSnapshot snapshot,
        GuidePointMetrics guideMetrics,
        int bucketCount,
        IReadOnlyList<float> bestCost,
        IReadOnlyList<float> bestDistance,
        bool requireCommittableDistance)
    {
        float startProgress = guideMetrics.Progress[snapshot.StartIndex];
        float high = Mathf.Max(startProgress, guideMetrics.TotalLength);
        if (high - startProgress <= MinimumUsefulProgress)
        {
            return -1;
        }

        int bestState = -1;
        float low = startProgress;
        float minimumStep = Mathf.Max(MinimumAdaptivePreviewStep, snapshot.Config.AdaptiveGuideMinimumStep);
        for (int step = 0; step < MaxAdaptiveBinarySteps && high - low > minimumStep; step++)
        {
            float midpoint = (low + high) * 0.5f;
            int candidate = FindBestReachableStateAtProgress(
                snapshot,
                guideMetrics,
                bucketCount,
                bestCost,
                bestDistance,
                midpoint,
                requireCommittableDistance);
            if (candidate >= 0)
            {
                low = midpoint;
                bestState = candidate;
            }
            else
            {
                high = midpoint;
            }
        }

        if (bestState >= 0)
        {
            int refined = FindBestReachableStateAtProgress(
                snapshot,
                guideMetrics,
                bucketCount,
                bestCost,
                bestDistance,
                low,
                requireCommittableDistance);
            return refined >= 0 ? refined : bestState;
        }

        return FindBestReachableStateAtProgress(
            snapshot,
            guideMetrics,
            bucketCount,
            bestCost,
            bestDistance,
            startProgress + minimumStep,
            requireCommittableDistance);
    }

    private static int FindBestReachableStateAtProgress(
        RoutePlannerSnapshot snapshot,
        GuidePointMetrics guideMetrics,
        int bucketCount,
        IReadOnlyList<float> bestCost,
        IReadOnlyList<float> bestDistance,
        float requiredProgress,
        bool requireCommittableDistance)
    {
        int bestState = -1;
        float bestScore = InfiniteCost;
        float startProgress = guideMetrics.Progress[snapshot.StartIndex];
        float lateralLimit = Mathf.Max(
            snapshot.Config.HorizontalSampleSpacing * 2.5f,
            snapshot.Config.SurfaceNeighborDistance * 2.5f);
        for (int node = 0; node < snapshot.Points.Count; node++)
        {
            if (node == snapshot.StartIndex)
            {
                continue;
            }

            float progress = guideMetrics.Progress[node];
            if (progress + GuideProgressTieTolerance < requiredProgress
                || progress <= startProgress + MinimumUsefulProgress)
            {
                continue;
            }

            float lateralDistance = guideMetrics.Distances[node];
            if (lateralDistance > lateralLimit && lateralDistance > snapshot.CorridorRadius * 0.75f)
            {
                continue;
            }

            for (int bucket = 0; bucket < bucketCount; bucket++)
            {
                int state = GetStateIndex(node, bucket, bucketCount);
                float cost = bestCost[state];
                if (float.IsInfinity(cost)
                    || !IsUsablePartialDistance(bestDistance[state], snapshot, requireCommittableDistance))
                {
                    continue;
                }

                float progressOvershoot = Mathf.Max(0f, progress - requiredProgress);
                float recentPenalty = GetRecentFrontierPenalty(snapshot, guideMetrics, node);
                float blockedPenalty = GetBlockedFrontierPenalty(snapshot, node);
                float score = lateralDistance * 4f
                    + cost * 0.2f
                    + progressOvershoot * 0.05f
                    + recentPenalty
                    + blockedPenalty
                    - BucketToStamina(bucket, bucketCount) * 0.05f;
                if (score >= bestScore)
                {
                    continue;
                }

                bestScore = score;
                bestState = state;
            }
        }

        return bestState;
    }

    private static int FindRecoveryPartialGoalState(
        RoutePlannerSnapshot snapshot,
        GuidePointMetrics guideMetrics,
        int bucketCount,
        IReadOnlyList<float> bestCost,
        IReadOnlyList<float> bestDistance,
        bool requireCommittableDistance)
    {
        int bestState = -1;
        float bestScore = InfiniteCost;
        float startProgress = guideMetrics.Progress[snapshot.StartIndex];
        Vector3 startPosition = snapshot.Points[snapshot.StartIndex].Position;
        Vector3 targetPosition = snapshot.Points[snapshot.TargetIndex].Position;
        for (int node = 0; node < snapshot.Points.Count; node++)
        {
            if (node == snapshot.StartIndex)
            {
                continue;
            }

            float displacement = Vector3.Distance(startPosition, snapshot.Points[node].Position);
            if (displacement + CostEpsilon < snapshot.Config.MinimumPartialSegmentDistance)
            {
                continue;
            }

            float progressGain = guideMetrics.Progress[node] - startProgress;
            float backtrackReward = Mathf.Max(0f, -progressGain) * 0.8f;
            float forwardPenalty = Mathf.Max(0f, progressGain) * 4f;
            float blockedPenalty = GetBlockedFrontierPenalty(snapshot, node);
            float targetDistance = Vector3.Distance(snapshot.Points[node].Position, targetPosition);
            for (int bucket = 0; bucket < bucketCount; bucket++)
            {
                int state = GetStateIndex(node, bucket, bucketCount);
                float cost = bestCost[state];
                if (float.IsInfinity(cost)
                    || !IsUsablePartialDistance(bestDistance[state], snapshot, requireCommittableDistance))
                {
                    continue;
                }

                float score = blockedPenalty
                    + guideMetrics.Distances[node] * 0.5f
                    + cost * 0.08f
                    + targetDistance * 0.02f
                    + forwardPenalty
                    - backtrackReward
                    - Mathf.Min(displacement, 10f) * 0.35f
                    - BucketToStamina(bucket, bucketCount) * 0.05f;
                if (score >= bestScore)
                {
                    continue;
                }

                bestScore = score;
                bestState = state;
            }
        }

        return bestState;
    }

    private static bool IsUsablePartialDistance(
        float distance,
        RoutePlannerSnapshot snapshot,
        bool requireCommittableDistance)
    {
        return !requireCommittableDistance
            || distance + CostEpsilon >= snapshot.Config.MinimumPartialSegmentDistance;
    }

    private static float GetRecentFrontierPenalty(
        RoutePlannerSnapshot snapshot,
        GuidePointMetrics guideMetrics,
        int node)
    {
        if (snapshot.RecentFrontierPositions.Count <= 1)
        {
            return 0f;
        }

        Vector3 position = snapshot.Points[node].Position;
        float progressGain = guideMetrics.Progress[node] - guideMetrics.Progress[snapshot.StartIndex];
        float progressRelief = progressGain <= MinimumUsefulProgress
            ? 0f
            : Mathf.Clamp01(progressGain / Mathf.Max(1f, snapshot.Config.MinimumPartialSegmentDistance * 3f));
        float penalty = 0f;
        int previousFrontierCount = snapshot.RecentFrontierPositions.Count - 1;
        for (int index = 0; index < previousFrontierCount; index++)
        {
            float distance = GetHorizontalDistance(position, snapshot.RecentFrontierPositions[index]);
            if (distance >= RecentFrontierPenaltyRadius)
            {
                continue;
            }

            float proximity = 1f - distance / RecentFrontierPenaltyRadius;
            penalty += RecentFrontierPenaltyCost * proximity * (1f - progressRelief * 0.75f);
        }

        return penalty;
    }

    private static float GetBlockedFrontierPenalty(RoutePlannerSnapshot snapshot, int node)
    {
        if (snapshot.BlockedFrontierPositions.Count == 0)
        {
            return 0f;
        }

        Vector3 position = snapshot.Points[node].Position;
        Vector3 startPosition = snapshot.Points[snapshot.StartIndex].Position;
        float penalty = 0f;
        for (int index = 0; index < snapshot.BlockedFrontierPositions.Count; index++)
        {
            Vector3 blocked = snapshot.BlockedFrontierPositions[index];
            float radius = GetHorizontalDistance(blocked, startPosition) <= BlockedFrontierCurrentStartRadius
                ? BlockedFrontierCurrentStartRadius
                : BlockedFrontierPenaltyRadius;
            float distance = GetHorizontalDistance(position, blocked);
            if (distance >= radius)
            {
                continue;
            }

            float proximity = 1f - distance / radius;
            penalty += BlockedFrontierPenaltyCost * proximity;
        }

        return penalty;
    }

    private static float[] BuildTargetDistanceField(
        RoutePlannerSnapshot snapshot,
        RouteGraph graph,
        RegionMap regionMap,
        CancellationToken cancellationToken)
    {
        float[] unitDistances = CreateFilled(regionMap.UnitCount, InfiniteCost);
        List<List<UnitEdge>> reverseUnitAdjacency = [];
        for (int unit = 0; unit < regionMap.UnitCount; unit++)
        {
            reverseUnitAdjacency.Add([]);
        }

        for (int from = 0; from < graph.Adjacency.Count; from++)
        {
            int fromUnit = regionMap.UnitIds[from];
            foreach (GraphEdge edge in graph.Adjacency[from])
            {
                int toUnit = regionMap.UnitIds[edge.To];
                if (fromUnit == toUnit)
                {
                    continue;
                }

                reverseUnitAdjacency[toUnit].Add(new UnitEdge(
                    fromUnit,
                    edge.Distance + GetDistanceFieldMovePenalty(edge.Kind, snapshot.Config)));
            }
        }

        PriorityQueue<int> queue = new();
        int targetUnit = regionMap.UnitIds[snapshot.TargetIndex];
        unitDistances[targetUnit] = 0f;
        queue.Enqueue(targetUnit, 0f);

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int currentUnit = queue.Dequeue();
            float currentDistance = unitDistances[currentUnit];
            foreach (UnitEdge edge in reverseUnitAdjacency[currentUnit])
            {
                float nextDistance = currentDistance + edge.Distance;
                if (nextDistance + CostEpsilon >= unitDistances[edge.ToUnit])
                {
                    continue;
                }

                unitDistances[edge.ToUnit] = nextDistance;
                queue.Enqueue(edge.ToUnit, nextDistance);
            }
        }

        float[] distances = CreateFilled(snapshot.Points.Count, InfiniteCost);
        for (int node = 0; node < snapshot.Points.Count; node++)
        {
            distances[node] = unitDistances[regionMap.UnitIds[node]];
        }

        return distances;
    }

    private static RegionMap BuildStandableRegionMap(
        RoutePlannerSnapshot snapshot,
        RouteGraph graph,
        CancellationToken cancellationToken)
    {
        int[] unitIds = CreateFilled(snapshot.Points.Count, -1);
        int unitCount = 0;
        Queue<int> queue = new();
        for (int node = 0; node < snapshot.Points.Count; node++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (unitIds[node] >= 0 || snapshot.Points[node].Kind != SurfaceKind.Standable)
            {
                continue;
            }

            int regionId = unitCount++;
            unitIds[node] = regionId;
            queue.Enqueue(node);
            while (queue.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int current = queue.Dequeue();
                AssignStandWalkRegionNeighbors(snapshot, graph.Adjacency[current], unitIds, regionId, queue);
                AssignStandWalkRegionNeighbors(snapshot, graph.ReverseAdjacency[current], unitIds, regionId, queue);
            }
        }

        for (int node = 0; node < unitIds.Length; node++)
        {
            if (unitIds[node] < 0)
            {
                unitIds[node] = unitCount++;
            }
        }

        return new RegionMap(unitIds, unitCount);
    }

    private static void AssignStandWalkRegionNeighbors(
        RoutePlannerSnapshot snapshot,
        IReadOnlyList<GraphEdge> edges,
        int[] unitIds,
        int regionId,
        Queue<int> queue)
    {
        foreach (GraphEdge edge in edges)
        {
            if (edge.Kind != MoveKind.StandWalk
                || snapshot.Points[edge.To].Kind != SurfaceKind.Standable
                || unitIds[edge.To] >= 0)
            {
                continue;
            }

            unitIds[edge.To] = regionId;
            queue.Enqueue(edge.To);
        }
    }

    private static SearchResult TrySearchSameStandableRegionRoute(
        RoutePlannerSnapshot snapshot,
        RouteGraph graph,
        GuidePointMetrics guideMetrics,
        RegionMap regionMap,
        CancellationToken cancellationToken)
    {
        if (snapshot.Points[snapshot.StartIndex].Kind != SurfaceKind.Standable
            || snapshot.Points[snapshot.TargetIndex].Kind != SurfaceKind.Standable
            || regionMap.UnitIds[snapshot.StartIndex] != regionMap.UnitIds[snapshot.TargetIndex])
        {
            return SearchResult.Empty;
        }

        int pointCount = snapshot.Points.Count;
        float[] bestCost = CreateFilled(pointCount, InfiniteCost);
        float[] bestDistance = new float[pointCount];
        int[] previous = CreateFilled(pointCount, -1);
        PriorityQueue<int> open = new();
        bestCost[snapshot.StartIndex] = 0f;
        open.Enqueue(snapshot.StartIndex, 0f);

        int expanded = 0;
        while (open.Count > 0 && expanded < snapshot.Config.SearchMaxExpandedStates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int current = open.Dequeue();
            if (current == snapshot.TargetIndex)
            {
                return ReconstructNodeResult(
                    snapshot.StartIndex,
                    snapshot.TargetIndex,
                    previous,
                    bestDistance,
                    isPartial: false);
            }

            expanded++;
            foreach (GraphEdge edge in graph.Adjacency[current])
            {
                if (edge.Kind != MoveKind.StandWalk
                    || regionMap.UnitIds[edge.To] != regionMap.UnitIds[snapshot.StartIndex])
                {
                    continue;
                }

                float transitionCost = GetTransitionCost(current, edge, snapshot, guideMetrics);
                float nextCost = bestCost[current] + transitionCost;
                if (nextCost + CostEpsilon >= bestCost[edge.To])
                {
                    continue;
                }

                bestCost[edge.To] = nextCost;
                bestDistance[edge.To] = bestDistance[current] + edge.Distance;
                previous[edge.To] = current;
                float remaining = Mathf.Abs(guideMetrics.Progress[snapshot.TargetIndex] - guideMetrics.Progress[edge.To]);
                open.Enqueue(edge.To, nextCost + remaining + guideMetrics.Distances[edge.To] * snapshot.Config.SearchGuideDistanceWeight);
            }
        }

        return SearchResult.Empty;
    }

    private static SearchResult ReconstructSearchResult(
        int goalState,
        int startNode,
        int bucketCount,
        int[] previousState,
        int[] previousNode,
        float[] bestDistance,
        float[] bestStaminaCost,
        bool isPartial)
    {
        List<int> nodes = [];
        int currentState = goalState;
        int guard = previousState.Length + 1;
        while (currentState >= 0 && guard-- > 0)
        {
            nodes.Add(GetNodeIndex(currentState, bucketCount));
            int previous = previousState[currentState];
            if (previous < 0)
            {
                break;
            }

            currentState = previous;
        }

        nodes.Reverse();
        if (nodes.Count == 0 || nodes[0] != startNode)
        {
            return SearchResult.Empty;
        }

        return new SearchResult(true, isPartial, nodes, bestDistance[goalState], bestStaminaCost[goalState]);
    }

    private static SearchResult ReconstructNodeResult(
        int startNode,
        int targetNode,
        int[] previous,
        float[] bestDistance,
        bool isPartial)
    {
        List<int> nodes = [];
        int current = targetNode;
        int guard = previous.Length + 1;
        while (current >= 0 && guard-- > 0)
        {
            nodes.Add(current);
            if (current == startNode)
            {
                break;
            }

            current = previous[current];
        }

        nodes.Reverse();
        if (nodes.Count == 0 || nodes[0] != startNode)
        {
            return SearchResult.Empty;
        }

        return new SearchResult(true, isPartial, nodes, bestDistance[targetNode], 0f);
    }

    private static RouteGraph BuildGraph(int pointCount, IReadOnlyList<RouteEdge> edges)
    {
        List<List<GraphEdge>> adjacency = [];
        List<List<GraphEdge>> reverseAdjacency = [];
        for (int index = 0; index < pointCount; index++)
        {
            adjacency.Add([]);
            reverseAdjacency.Add([]);
        }

        for (int index = 0; index < edges.Count; index++)
        {
            RouteEdge edge = edges[index];
            if (edge.From < 0 || edge.From >= pointCount || edge.To < 0 || edge.To >= pointCount)
            {
                continue;
            }

            adjacency[edge.From].Add(new GraphEdge(edge.To, edge.Kind, edge.Distance, edge.StaminaCost));
            reverseAdjacency[edge.To].Add(new GraphEdge(edge.From, edge.Kind, edge.Distance, edge.StaminaCost));
        }

        return new RouteGraph(adjacency, reverseAdjacency);
    }

    private static float GetTransitionCost(
        int from,
        GraphEdge edge,
        RoutePlannerSnapshot snapshot,
        GuidePointMetrics guideMetrics)
    {
        float currentGoalProgressDistance = GetGuideProgressDistance(from, snapshot.TargetIndex, guideMetrics);
        float nextGoalProgressDistance = GetGuideProgressDistance(edge.To, snapshot.TargetIndex, guideMetrics);
        float progress = currentGoalProgressDistance - nextGoalProgressDistance;
        float usefulProgress = Mathf.Max(progress, MinimumUsefulProgress);
        float backtrackMultiplier = snapshot.PreferRecoveryDetour ? 0.35f : 1f;
        float backtrackPenalty = progress < 0f
            ? -progress * snapshot.Config.SearchBacktrackPenaltyMultiplier * backtrackMultiplier
            : 0f;
        float longStepPenalty = Mathf.Max(0f, edge.Distance - usefulProgress)
            * snapshot.Config.SearchLongStepPenaltyMultiplier;

        return edge.Distance
            + edge.StaminaCost * 8f
            + guideMetrics.Distances[edge.To] * snapshot.Config.SearchGuideDistanceWeight
            + GetMovePenalty(edge.Kind, snapshot.Config)
            + backtrackPenalty
            + longStepPenalty;
    }

    private static float GetHeuristic(
        int node,
        RoutePlannerSnapshot snapshot,
        GuidePointMetrics guideMetrics,
        float[] distanceField)
    {
        float fieldDistance = node >= 0 && node < distanceField.Length && !float.IsInfinity(distanceField[node])
            ? distanceField[node]
            : Vector3.Distance(snapshot.Points[node].Position, snapshot.Points[snapshot.TargetIndex].Position);
        float guideDistance = guideMetrics.Distances[node] * snapshot.Config.SearchGuideDistanceWeight;
        return fieldDistance * snapshot.Config.SearchDistanceFieldHeuristicWeight + guideDistance;
    }

    private static float GetGuideProgressDistance(int node, int goal, GuidePointMetrics guideMetrics)
    {
        return Mathf.Abs(guideMetrics.Progress[goal] - guideMetrics.Progress[node]);
    }

    private static float GetDistanceFieldMovePenalty(MoveKind kind, PlannerConfig config)
    {
        return kind switch
        {
            MoveKind.StandJump => config.StandJumpMovePenalty * 0.5f,
            MoveKind.AirTransfer => config.AirTransferMovePenalty * 0.5f,
            MoveKind.SurfaceClimb => config.SurfaceClimbMovePenalty * 0.5f,
            _ => 0f,
        };
    }

    private static float GetMovePenalty(MoveKind kind, PlannerConfig config)
    {
        return kind switch
        {
            MoveKind.SurfaceClimb => config.SurfaceClimbMovePenalty,
            MoveKind.StandJump => config.StandJumpMovePenalty,
            MoveKind.AirTransfer => config.AirTransferMovePenalty,
            _ => 0f,
        };
    }

    private static float GetHorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private static int GetStateIndex(int node, int bucket, int bucketCount)
    {
        return node * bucketCount + bucket;
    }

    private static int GetNodeIndex(int state, int bucketCount)
    {
        return state / bucketCount;
    }

    private static int GetBucketIndex(int state, int bucketCount)
    {
        return state % bucketCount;
    }

    private static int StaminaToBucket(float stamina, int bucketCount)
    {
        return Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(stamina) * (bucketCount - 1)), 0, bucketCount - 1);
    }

    private static float BucketToStamina(int bucket, int bucketCount)
    {
        return bucketCount <= 1 ? 1f : bucket / (float)(bucketCount - 1);
    }

    private static float[] CreateFilled(int length, float value)
    {
        float[] array = new float[length];
        Array.Fill(array, value);
        return array;
    }

    private static int[] CreateFilled(int length, int value)
    {
        int[] array = new int[length];
        Array.Fill(array, value);
        return array;
    }

    private sealed class RouteGraph
    {
        internal RouteGraph(List<List<GraphEdge>> adjacency, List<List<GraphEdge>> reverseAdjacency)
        {
            Adjacency = adjacency;
            ReverseAdjacency = reverseAdjacency;
        }

        internal List<List<GraphEdge>> Adjacency { get; }

        internal List<List<GraphEdge>> ReverseAdjacency { get; }
    }

    private readonly struct GraphEdge
    {
        internal GraphEdge(int to, MoveKind kind, float distance, float staminaCost)
        {
            To = to;
            Kind = kind;
            Distance = distance;
            StaminaCost = staminaCost;
        }

        internal int To { get; }

        internal MoveKind Kind { get; }

        internal float Distance { get; }

        internal float StaminaCost { get; }
    }

    private readonly struct UnitEdge
    {
        internal UnitEdge(int toUnit, float distance)
        {
            ToUnit = toUnit;
            Distance = distance;
        }

        internal int ToUnit { get; }

        internal float Distance { get; }
    }

    private readonly struct RegionMap
    {
        internal RegionMap(int[] unitIds, int unitCount)
        {
            UnitIds = unitIds;
            UnitCount = unitCount;
        }

        internal int[] UnitIds { get; }

        internal int UnitCount { get; }
    }

    private readonly struct SearchResult
    {
        internal SearchResult(bool found, bool isPartial, List<int> nodes, float distance, float staminaCost)
        {
            Found = found;
            IsPartial = isPartial;
            Nodes = nodes;
            Distance = distance;
            StaminaCost = staminaCost;
        }

        internal bool Found { get; }

        internal bool IsPartial { get; }

        internal List<int> Nodes { get; }

        internal float Distance { get; }

        internal float StaminaCost { get; }

        internal static SearchResult Empty => new(false, false, [], 0f, 0f);
    }
}
