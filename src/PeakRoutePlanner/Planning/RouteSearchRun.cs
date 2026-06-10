using System;
using System.Collections.Generic;
using UnityEngine;

namespace PeakRoutePlanner.Planning;

internal readonly struct RouteSearchSettings
{
    internal RouteSearchSettings(
        int maxSteps,
        float targetReachedDistance,
        float regionMergeDistance,
        int edgeValidationPairLimit)
    {
        MaxSteps = Mathf.Max(1, maxSteps);
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

    private readonly Vector3 startPosition;
    private readonly Vector3 targetPosition;
    private readonly PlannerConfig config;
    private readonly RouteSearchSettings settings;
    private readonly HashSet<int> blockedPointIds = [];
    private readonly HashSet<RouteSeedKey> sampledSeedKeys = [];
    private readonly HashSet<RouteCandidateAttemptKey> failedTransitions = [];
    private readonly List<RouteRegionVisit> committedVisits = [];
    private readonly List<RouteCommittedStep> committedSteps = [];
    private readonly List<Vector3> previewPath = [];
    private readonly List<Vector3> regionPreviewCenters = [];
    private readonly List<RouteRegionCandidate> candidateBuffer = [];
    private readonly List<int> sourceProbePointBuffer = [];
    private readonly List<int> targetProbePointBuffer = [];
    private readonly List<RouteProbePair> probePairBuffer = [];

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

            LastStatus = $"next-seed step={samplingStepIndex} edge={transition.EdgeKind} edgeDistance={transition.Distance:0.00} sameRegion={transition.IsSameRegionFrontier} sourceRegion={transition.SourceRegion.Id} targetRegion={transition.TargetRegion.Id} next=({CurrentSeedPosition.x:0.0},{CurrentSeedPosition.y:0.0},{CurrentSeedPosition.z:0.0}) regions={regionMap.Regions.Count} blockedPoints={blockedPointIds.Count}";
            return RouteSearchDecision.NextSeed(
                CurrentSeedPosition,
                LastStatus,
                samplingStepIndex,
                regionMap.Regions.Count,
                blockedPointIds.Count);
        }

        MarkBlocked(currentRegion);
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

            LastStatus = $"backtracked next-seed step={samplingStepIndex} edge={transition.EdgeKind} edgeDistance={transition.Distance:0.00} sameRegion={transition.IsSameRegionFrontier} sourceRegion={transition.SourceRegion.Id} targetRegion={transition.TargetRegion.Id} next=({CurrentSeedPosition.x:0.0},{CurrentSeedPosition.y:0.0},{CurrentSeedPosition.z:0.0})";
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

        if (TryBuildSameRegionFrontier(sourceRegion, committedVisits[sourceVisitIndex], points, sampler, out transition))
        {
            selectedSourceIndex = sourceVisitIndex;
            return true;
        }

        BuildCandidateRegions(sourceRegion, regionMap);
        int testedCandidates = 0;
        for (int index = 0; index < candidateBuffer.Count && testedCandidates < MaxCandidateRegionsPerSource; index++)
        {
            StandableRegion candidateRegion = candidateBuffer[index].Region;
            if (candidateRegion.Id == sourceRegion.Id
                || IsBlocked(candidateRegion)
                || IsRegionInCommittedPath(candidateRegion))
            {
                continue;
            }

            RouteCandidateAttemptKey attemptKey = new(sourceRegion.AnchorPointId, candidateRegion.AnchorPointId, sourceRegion.PointIds.Count, candidateRegion.PointIds.Count);
            if (failedTransitions.Contains(attemptKey))
            {
                continue;
            }

            testedCandidates++;
            if (!TryValidateRegionTransition(sourceRegion, candidateRegion, points, sampler, out transition))
            {
                failedTransitions.Add(attemptKey);
                continue;
            }

            selectedSourceIndex = sourceVisitIndex;
            return true;
        }

        return false;
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

        RouteEdgeValidationResult result = sampler.ValidateStandableEdge(points[fromPointId], nextPoint);
        if (!result.IsValid)
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
            isSameRegionFrontier: true);
        return true;
    }

    private void BuildCandidateRegions(StandableRegion sourceRegion, StandableRegionMap regionMap)
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
            float score = region.DistanceToTarget + sourceDistance * 0.08f;
            candidateBuffer.Add(new RouteRegionCandidate(region, score));
        }

        candidateBuffer.Sort((a, b) => a.Score.CompareTo(b.Score));
    }

    private bool TryValidateRegionTransition(
        StandableRegion sourceRegion,
        StandableRegion targetRegion,
        IReadOnlyList<SurfacePoint> points,
        SurfaceSampler sampler,
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
            RouteEdgeValidationResult result = sampler.ValidateStandableEdge(sourcePoint, targetPoint);
            if (!result.IsValid)
            {
                continue;
            }

            if (!TrySelectTargetRegionSeed(pair.TargetPointId, targetRegion, points, sampler, out int nextSeedPointId))
            {
                continue;
            }

            transition = new RouteTransition(
                sourceRegion,
                targetRegion,
                pair.SourcePointId,
                pair.TargetPointId,
                nextSeedPointId,
                result.Kind,
                result.Distance,
                isSameRegionFrontier: false);
            return true;
        }

        return false;
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

        RouteEdgeValidationResult result = sampler.ValidateStandableEdge(points[fromPointId], points[toPointId]);
        return result.IsValid;
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
            RebuildPreviewPath(points);
            return;
        }

        committedVisits.Add(new RouteRegionVisit(currentRegion.AnchorPointId, resolvedSeedPointId, seedPosition));
        RebuildPreviewPath(points);
    }

    private void CommitTransition(RouteTransition transition, int selectedSourceIndex, IReadOnlyList<SurfacePoint> points)
    {
        TrimPathToVisitIndex(selectedSourceIndex);
        committedSteps.Add(new RouteCommittedStep(
            transition.SourcePointId,
            transition.TargetPointId,
            transition.NextSeedPointId,
            transition.EdgeKind));
        SurfacePoint nextSeedPoint = points[transition.NextSeedPointId];
        committedVisits.Add(new RouteRegionVisit(
            transition.TargetRegion.AnchorPointId,
            transition.NextSeedPointId,
            nextSeedPoint.Position));
        CurrentSeedPosition = nextSeedPoint.Position;
        sampledSeedKeys.Add(RouteSeedKey.From(CurrentSeedPosition, SeedQuantizationCellSize));
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
    }

    private void RebuildPreviewPath(IReadOnlyList<SurfacePoint> points)
    {
        previewPath.Clear();
        AppendPreviewPoint(startPosition);
        for (int index = 0; index < committedSteps.Count; index++)
        {
            RouteCommittedStep step = committedSteps[index];
            AppendPointIfValid(points, step.SourcePointId);
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
        internal RouteCommittedStep(int sourcePointId, int targetPointId, int seedPointId, RouteEdgeKind edgeKind)
        {
            SourcePointId = sourcePointId;
            TargetPointId = targetPointId;
            SeedPointId = seedPointId;
            EdgeKind = edgeKind;
        }

        internal int SourcePointId { get; }

        internal int TargetPointId { get; }

        internal int SeedPointId { get; }

        internal RouteEdgeKind EdgeKind { get; }
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
            bool isSameRegionFrontier)
        {
            SourceRegion = sourceRegion;
            TargetRegion = targetRegion;
            SourcePointId = sourcePointId;
            TargetPointId = targetPointId;
            NextSeedPointId = nextSeedPointId;
            EdgeKind = edgeKind;
            Distance = distance;
            IsSameRegionFrontier = isSameRegionFrontier;
        }

        internal StandableRegion SourceRegion { get; }

        internal StandableRegion TargetRegion { get; }

        internal int SourcePointId { get; }

        internal int TargetPointId { get; }

        internal int NextSeedPointId { get; }

        internal RouteEdgeKind EdgeKind { get; }

        internal float Distance { get; }

        internal bool IsSameRegionFrontier { get; }
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
            int targetPointCount)
        {
            SourceAnchorPointId = sourceAnchorPointId;
            TargetAnchorPointId = targetAnchorPointId;
            SourcePointCount = sourcePointCount;
            TargetPointCount = targetPointCount;
        }

        private int SourceAnchorPointId { get; }

        private int TargetAnchorPointId { get; }

        private int SourcePointCount { get; }

        private int TargetPointCount { get; }

        public bool Equals(RouteCandidateAttemptKey other)
        {
            return SourceAnchorPointId == other.SourceAnchorPointId
                && TargetAnchorPointId == other.TargetAnchorPointId
                && SourcePointCount == other.SourcePointCount
                && TargetPointCount == other.TargetPointCount;
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
