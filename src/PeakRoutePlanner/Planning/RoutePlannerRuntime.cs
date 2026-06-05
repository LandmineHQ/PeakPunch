using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PeakRoutePlanner.Configuration;
using PeakRoutePlanner.Visualization;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace PeakRoutePlanner.Planning;

internal sealed class RoutePlannerRuntime
{
    private const int MaxIntermediatePathPoints = 160;
    private const int MaxIntermediatePreviewEdges = 4000;
    private const float MinimumIntermediateRenderThrottleSeconds = 0.05f;
    private const float MaximumIntermediateRenderThrottleSeconds = 0.2f;
    private const float MinimumGuideProgressGain = 0.05f;
    private const float GuideProgressTieTolerance = 0.05f;
    private const float GuideLateralTieTolerance = 0.25f;
    private const int MaxAdaptivePreviewBinarySteps = 20;
    private const int MaxTargetFrontierPreviewEdges = 4000;
    private const float StartAnchorRemapDistance = 6f;
    private const float TargetAnchorRemapDistance = 12f;
    private const float TargetFrontierActivationDistance = 18f;
    private const int MaxCommittedPathPoints = 512;
    private const int MaxRecentFrontierPositions = 32;
    private const int MaxBlockedFrontierPositions = 24;
    private const float MinimumFrontierTargetDistanceGain = 0.5f;

    private readonly SurfaceSampler sampler = new();
    private readonly EdgeValidator edgeValidator = new();
    private readonly RoutePathRenderer pathRenderer;

    private PlannerState state = PlannerState.Idle;
    private PlannerConfig config = null!;
    private Vector3 startPosition;
    private Vector3 planningFrontierPosition;
    private Vector3 targetPosition;
    private List<Vector3> guidePath = [];
    private readonly List<Vector3> committedPath = [];
    private readonly List<Vector3> recentFrontierPositions = [];
    private readonly List<Vector3> blockedFrontierPositions = [];
    private float currentCorridorRadius;
    private float lastFrontierDistanceToTarget;
    private int stalledPartialCommitCount;
    private float nextIntermediateRenderTime;
    private Task<RouteResult>? planningTask;
    private CancellationTokenSource? cancellation;
    private bool preserveGraphForCurrentAttempt;
    private int cachedPreviewPointCount = -1;
    private int cachedPreviewEdgeCount = -1;
    private bool cachedPreviewUsedLocalFallback;
    private List<Vector3> cachedPreviewPath = [];
    private int cachedTargetPreviewPointCount = -1;
    private int cachedTargetPreviewEdgeCount = -1;
    private List<Vector3> cachedTargetPreviewPath = [];

    internal RoutePlannerRuntime(RoutePathRenderer pathRenderer)
    {
        this.pathRenderer = pathRenderer;
    }

    internal void Update()
    {
        if (!PeakRoutePlannerConfig.EnableRoutePlanner.Value)
        {
            return;
        }

        if (PeakRoutePlannerConfig.ClearRouteShortcut.Value.IsDown())
        {
            Cleanup();
            Plugin.Log.LogInfo("Cleared PeakRoutePlanner route.");
            return;
        }

        if (PeakRoutePlannerConfig.PlanRouteShortcut.Value.IsDown())
        {
            BeginPlanning();
        }

        switch (state)
        {
            case PlannerState.Sampling:
                UpdateSampling();
                break;
            case PlannerState.ValidatingEdges:
                UpdateEdgeValidation();
                break;
            case PlannerState.WaitingForWorker:
                UpdateWorker();
                break;
        }
    }

    internal void Cleanup()
    {
        cancellation?.Cancel();
        cancellation?.Dispose();
        cancellation = null;
        planningTask = null;
        state = PlannerState.Idle;
        nextIntermediateRenderTime = 0f;
        recentFrontierPositions.Clear();
        blockedFrontierPositions.Clear();
        stalledPartialCommitCount = 0;
        lastFrontierDistanceToTarget = 0f;
        ClearPreviewCache();
        pathRenderer.Clear();
        pathRenderer.DestroySamplingWindowPreview();
    }

    private void BeginPlanning()
    {
        Character localCharacter = Character.localCharacter;
        if (localCharacter == null)
        {
            Plugin.Log.LogWarning("Route planning skipped because Character.localCharacter is unavailable.");
            return;
        }

        if (!TryResolveTarget(out Vector3 target))
        {
            Plugin.Log.LogWarning("Route planning skipped because no campfire or configured target object could be found.");
            return;
        }

        Cleanup();
        config = PeakRoutePlannerConfig.ToPlannerConfig(localCharacter);
        Vector3 characterPosition = GetCharacterPosition(localCharacter);
        guidePath = RouteGuideBuilder.Build(characterPosition, target, config);
        startPosition = guidePath.Count > 0 ? guidePath[0] : characterPosition;
        planningFrontierPosition = startPosition;
        targetPosition = guidePath.Count > 1 ? guidePath[guidePath.Count - 1] : target;
        committedPath.Clear();
        committedPath.Add(startPosition);
        recentFrontierPositions.Clear();
        blockedFrontierPositions.Clear();
        AddRecentFrontierPosition(startPosition);
        stalledPartialCommitCount = 0;
        lastFrontierDistanceToTarget = Vector3.Distance(planningFrontierPosition, targetPosition);
        currentCorridorRadius = config.CorridorInitialRadius;
        nextIntermediateRenderTime = 0f;
        cancellation = new CancellationTokenSource();
        if (PeakRoutePlannerConfig.RenderSamplingWindowPreview.Value)
        {
            pathRenderer.CreateSamplingWindowPreview();
        }

        LogDetail(
            $"Movement model: normalJump={config.NormalStandJumpDistance:0.00}, sprintJump={config.SprintStandJumpDistance:0.00}, maxStandJump={config.MaxStandJumpDistance:0.00}, airTransfer={config.AirTransferJumpDistance:0.00}, maxAirTransfer={config.MaxAirTransferDistance:0.00}, climbSpeed={config.ClimbSpeed:0.00}, climbUsage={config.ClimbStaminaUsagePerSecond:0.000}/s, jumpCost={config.JumpStaminaCost:0.000}, sprintJumpCost={config.SprintJumpStaminaCost:0.000}, climbJumpCost={config.ClimbJumpStaminaCost:0.000}.");
        BeginSamplingAttempt(preserveCachedGraph: false);
    }

    private void BeginSamplingAttempt(bool preserveCachedGraph)
    {
        preserveGraphForCurrentAttempt = preserveCachedGraph;
        bool includeTargetFrontier = config.EnableBidirectionalFrontierSampling
            || Vector3.Distance(planningFrontierPosition, targetPosition) <= TargetFrontierActivationDistance;
        sampler.Begin(
            planningFrontierPosition,
            targetPosition,
            guidePath,
            currentCorridorRadius,
            config,
            preserveCachedGraph,
            includeTargetFrontier);
        if (!preserveCachedGraph)
        {
            edgeValidator.Begin([], config, planningFrontierPosition, targetPosition);
        }

        nextIntermediateRenderTime = 0f;
        state = PlannerState.Sampling;
        LogDetail(
            $"Sampling {(includeTargetFrontier ? "bidirectional" : "forward")} surface grid radius={currentCorridorRadius:0.0}, committed={committedPath.Count}, cachedPoints={sampler.CachedPointCountAtAttemptStart}, cachedEdges={edgeValidator.Edges.Count}, pendingRays={sampler.PendingRayCount}.");
        RenderIntermediateIfDue(force: true);
    }

    private void UpdateSampling()
    {
        bool completed = sampler.ProcessFrame();
        RenderIntermediateIfDue(completed);

        if (!completed)
        {
            return;
        }

        int newPointCount = Mathf.Max(0, sampler.Points.Count - sampler.CachedPointCountAtAttemptStart);
        LogDetail($"Sampled {sampler.Points.Count} surface points ({newPointCount} new) from {sampler.ProcessedRayCount} queries, broadphaseWindows={sampler.BroadphaseWindowCount}, globalFallbacks={sampler.GlobalRaycastFallbackCount}, gapProbes={sampler.GapProbeCount}, gapPoints={sampler.GapLandingPointCount}.");
        if (sampler.HitPointLimit)
        {
            LogDetail($"Surface point limit reached at {sampler.Points.Count}; continuing with a capped sample set.");
        }

        edgeValidator.Begin(sampler.Points, config, planningFrontierPosition, targetPosition, preserveGraphForCurrentAttempt);
        state = PlannerState.ValidatingEdges;
        LogDetail($"Building and validating directed route edge candidates from {sampler.Points.Count} cached surface points, preservingEdges={preserveGraphForCurrentAttempt}.");
    }

    private void UpdateEdgeValidation()
    {
        bool completed = edgeValidator.ProcessFrame();
        RenderIntermediateIfDue(completed);

        if (!completed)
        {
            return;
        }

        LogDetail($"Validated {edgeValidator.Edges.Count} directed route edges from {edgeValidator.ProcessedCandidateCount} generated candidates; inspectedPairs={edgeValidator.ProcessedCandidatePairCount}, generatedCandidates={edgeValidator.GeneratedCandidateCount}.");
        LogEdgeConnectivity();
        if (edgeValidator.HitCandidateLimit)
        {
            LogDetail($"Edge candidate limit reached at {edgeValidator.GeneratedCandidateCount}; continuing with a capped edge set.");
        }

        AnchorSelection anchors = ResolveEffectiveAnchors(logSelection: true);
        RoutePlannerSnapshot snapshot = new(
            sampler.Points.ToArray(),
            edgeValidator.Edges.ToArray(),
            anchors.StartIndex,
            anchors.TargetIndex,
            guidePath.ToArray(),
            recentFrontierPositions.ToArray(),
            blockedFrontierPositions.ToArray(),
            stalledPartialCommitCount > 0,
            config,
            currentCorridorRadius);

        CancellationToken token = cancellation?.Token ?? CancellationToken.None;
        planningTask = Task.Run(() => RoutePlanningWorker.Plan(snapshot, token), token);
        state = PlannerState.WaitingForWorker;
    }

    private void UpdateWorker()
    {
        if (planningTask == null || !planningTask.IsCompleted)
        {
            return;
        }

        RouteResult result;
        try
        {
            result = planningTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            state = PlannerState.Idle;
            pathRenderer.DestroySamplingWindowPreview();
            return;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Route planning failed: {ex.Message}");
            state = PlannerState.Idle;
            pathRenderer.DestroySamplingWindowPreview();
            return;
        }

        if (result.Found && !result.IsPartial && IsFinalPathBuiltFromValidatedEdges(result))
        {
            state = PlannerState.Idle;
            pathRenderer.DestroySamplingWindowPreview();
            List<Vector3> finalPath = CombineWithCommittedPath(result.Path);
            if (PeakRoutePlannerConfig.RenderRoutePath.Value)
            {
                pathRenderer.Render(finalPath, isFinalPath: true);
            }

            Plugin.Log.LogInfo(
                $"Route found: points={finalPath.Count}, distance={GetPathDistance(finalPath):0.0}, staminaCost={result.TotalStaminaCost:0.00}, sampled={result.SampledPointCount}, edges={result.ValidEdgeCount}, radius={result.CorridorRadius:0.0}.");
            return;
        }

        if (result.Found && result.IsPartial && IsFinalPathBuiltFromValidatedEdges(result))
        {
            if (TryCommitPartialSegment(result))
            {
                BeginSamplingAttempt(preserveCachedGraph: false);
                return;
            }

            if (stalledPartialCommitCount > 0)
            {
                BeginSamplingAttempt(preserveCachedGraph: false);
                return;
            }

            if (PeakRoutePlannerConfig.RenderRoutePath.Value && PeakRoutePlannerConfig.RenderIntermediateRoutePath.Value)
            {
                pathRenderer.Render(CombineWithCommittedPath(result.Path), isFinalPath: false);
            }

            LogDetail(
                $"Partial adaptive route preview: points={result.Path.Count}, distance={result.TotalDistance:0.0}, staminaCost={result.TotalStaminaCost:0.00}, sampled={result.SampledPointCount}, edges={result.ValidEdgeCount}, radius={result.CorridorRadius:0.0}.");
        }

        if (result.Found && !result.IsPartial)
        {
            LogDetail("Discarded route because final node path was not composed from the current validated directed edges.");
        }

        if (config.EnableCorridorExpansion
            && currentCorridorRadius + 0.001f < config.MaxCorridorRadius)
        {
            currentCorridorRadius = Mathf.Min(config.MaxCorridorRadius, currentCorridorRadius + config.CorridorRadiusStep);
            LogDetail($"No route found; expanding corridor to radius={currentCorridorRadius:0.0}.");
            BeginSamplingAttempt(preserveCachedGraph: true);
            return;
        }

        state = PlannerState.Idle;
        pathRenderer.DestroySamplingWindowPreview();
        if (config.EnableCorridorExpansion)
        {
            Plugin.Log.LogWarning(
                $"No route found after expanding to max radius={config.MaxCorridorRadius:0.0}. Last sampled={result.SampledPointCount}, edges={result.ValidEdgeCount}.");
        }
        else
        {
            Plugin.Log.LogWarning(
                $"No route found in the current bounded sampling window. Corridor expansion is disabled; keeping the last validated preview. Last sampled={result.SampledPointCount}, edges={result.ValidEdgeCount}, radius={currentCorridorRadius:0.0}.");
        }
    }

    private bool TryCommitPartialSegment(RouteResult result)
    {
        if (result.Path.Count < 2)
        {
            return false;
        }

        float segmentDistance = GetPathDistance(result.Path);
        Vector3 previousFrontier = planningFrontierPosition;
        Vector3 nextFrontier = result.Path[result.Path.Count - 1];
        float previousDistanceToTarget = Vector3.Distance(previousFrontier, targetPosition);
        float nextDistanceToTarget = Vector3.Distance(nextFrontier, targetPosition);
        bool closeToTarget = nextDistanceToTarget <= TargetFrontierActivationDistance;
        bool wasRecoveryDetour = stalledPartialCommitCount > 0;
        if (segmentDistance < config.MinimumPartialSegmentDistance && !closeToTarget)
        {
            AddBlockedFrontierPosition(nextFrontier);
            stalledPartialCommitCount = Mathf.Min(stalledPartialCommitCount + 1, 8);
            LogDetail(
                $"Partial route segment blocked: segmentDistance={segmentDistance:0.0}, minimum={config.MinimumPartialSegmentDistance:0.0}, points={result.Path.Count}, blocked={blockedFrontierPositions.Count}.");
            return false;
        }

        float guideAdvance = GetGuideProgress(nextFrontier) - GetGuideProgress(previousFrontier);
        if (!wasRecoveryDetour
            && !closeToTarget
            && guideAdvance + 0.001f < config.MinimumFrontierAdvanceDistance)
        {
            AddBlockedFrontierPosition(nextFrontier);
            stalledPartialCommitCount = Mathf.Min(stalledPartialCommitCount + 1, 8);
            LogDetail(
                $"Partial route segment blocked: guideAdvance={guideAdvance:0.0}, minimumAdvance={config.MinimumFrontierAdvanceDistance:0.0}, segmentDistance={segmentDistance:0.0}, blocked={blockedFrontierPositions.Count}.");
            return false;
        }

        AppendCommittedSegment(result.Path);
        planningFrontierPosition = committedPath[committedPath.Count - 1];
        AddRecentFrontierPosition(planningFrontierPosition);
        UpdateFrontierProgressState(previousFrontier, planningFrontierPosition, previousDistanceToTarget, nextDistanceToTarget, wasRecoveryDetour);
        currentCorridorRadius = config.CorridorInitialRadius;
        ClearPreviewCache();
        if (PeakRoutePlannerConfig.RenderRoutePath.Value && PeakRoutePlannerConfig.RenderIntermediateRoutePath.Value)
        {
            pathRenderer.Render(committedPath, isFinalPath: false);
        }

        LogDetail(
            $"Committed route segment: segmentDistance={segmentDistance:0.0}, guideAdvance={guideAdvance:0.0}, committedPoints={committedPath.Count}, frontierDistanceToTarget={Vector3.Distance(planningFrontierPosition, targetPosition):0.0}, stalled={stalledPartialCommitCount}, blocked={blockedFrontierPositions.Count}.");
        return true;
    }

    private float GetGuideProgress(Vector3 position)
    {
        return GuideProjectionMap.Build(guidePath).Project(position).Progress;
    }

    private void UpdateFrontierProgressState(
        Vector3 previousFrontier,
        Vector3 nextFrontier,
        float previousDistanceToTarget,
        float nextDistanceToTarget,
        bool wasRecoveryDetour)
    {
        bool improved = nextDistanceToTarget + MinimumFrontierTargetDistanceGain < previousDistanceToTarget
            || nextDistanceToTarget + MinimumFrontierTargetDistanceGain < lastFrontierDistanceToTarget;
        lastFrontierDistanceToTarget = nextDistanceToTarget;
        if (improved)
        {
            stalledPartialCommitCount = 0;
            return;
        }

        if (wasRecoveryDetour)
        {
            AddBlockedFrontierPosition(previousFrontier);
            stalledPartialCommitCount = 0;
            return;
        }

        AddBlockedFrontierPosition(nextFrontier);
        stalledPartialCommitCount = Mathf.Min(stalledPartialCommitCount + 1, 8);
    }

    private void AddRecentFrontierPosition(Vector3 position)
    {
        if (recentFrontierPositions.Count > 0
            && (recentFrontierPositions[recentFrontierPositions.Count - 1] - position).sqrMagnitude <= 0.25f)
        {
            return;
        }

        recentFrontierPositions.Add(position);
        if (recentFrontierPositions.Count <= MaxRecentFrontierPositions)
        {
            return;
        }

        recentFrontierPositions.RemoveAt(0);
    }

    private void AddBlockedFrontierPosition(Vector3 position)
    {
        for (int index = 0; index < blockedFrontierPositions.Count; index++)
        {
            if ((blockedFrontierPositions[index] - position).sqrMagnitude <= 4f)
            {
                return;
            }
        }

        blockedFrontierPositions.Add(position);
        if (blockedFrontierPositions.Count <= MaxBlockedFrontierPositions)
        {
            return;
        }

        blockedFrontierPositions.RemoveAt(0);
    }

    private void AppendCommittedSegment(IReadOnlyList<Vector3> segment)
    {
        for (int index = 0; index < segment.Count; index++)
        {
            Vector3 position = segment[index];
            if (committedPath.Count > 0 && (committedPath[committedPath.Count - 1] - position).sqrMagnitude <= 0.01f)
            {
                continue;
            }

            committedPath.Add(position);
        }

        if (committedPath.Count <= MaxCommittedPathPoints)
        {
            return;
        }

        int removeCount = committedPath.Count - MaxCommittedPathPoints;
        committedPath.RemoveRange(0, removeCount);
    }

    private List<Vector3> CombineWithCommittedPath(IReadOnlyList<Vector3> localPath)
    {
        List<Vector3> combined = new(committedPath.Count + localPath.Count);
        combined.AddRange(committedPath);
        for (int index = 0; index < localPath.Count; index++)
        {
            Vector3 position = localPath[index];
            if (combined.Count > 0 && (combined[combined.Count - 1] - position).sqrMagnitude <= 0.01f)
            {
                continue;
            }

            combined.Add(position);
        }

        return combined;
    }

    private static float GetPathDistance(IReadOnlyList<Vector3> path)
    {
        float distance = 0f;
        for (int index = 1; index < path.Count; index++)
        {
            distance += Vector3.Distance(path[index - 1], path[index]);
        }

        return distance;
    }

    private void RenderIntermediateIfDue(bool force)
    {
        if (!PeakRoutePlannerConfig.RenderRoutePath.Value || !PeakRoutePlannerConfig.RenderIntermediateRoutePath.Value)
        {
            return;
        }

        float now = Time.unscaledTime;
        if (!force && now < nextIntermediateRenderTime)
        {
            return;
        }

        List<Vector3> previewPath = BuildIntermediatePreviewPath(out bool usedLocalFallback);
        if (previewPath.Count >= 2)
        {
            if (pathRenderer.Render(CombineWithCommittedPath(previewPath), isFinalPath: false))
            {
                LogDetail(usedLocalFallback
                    ? $"Rendered local fallback intermediate route preview: points={previewPath.Count}."
                    : $"Rendered intermediate route preview: points={previewPath.Count}.");
            }
        }
        else if (force || edgeValidator.Edges.Count > 0)
        {
            AnchorSelection anchors = ResolveEffectiveAnchors(logSelection: false);
                LogDetail(
                $"No intermediate route preview yet: points={sampler.Points.Count}, edges={edgeValidator.Edges.Count}, effectiveStart={anchors.StartIndex}, effectiveTarget={anchors.TargetIndex}.");
        }

        List<Vector3> targetPreviewPath = BuildTargetFrontierPreviewPath();
        if (targetPreviewPath.Count >= 2)
        {
            if (pathRenderer.RenderTargetPreview(targetPreviewPath))
            {
                LogDetail($"Rendered target-side intermediate route preview: points={targetPreviewPath.Count}.");
            }
        }
        else if (force)
        {
            pathRenderer.RenderTargetPreview([]);
        }

        RenderSamplingWindowPreview(force);

        nextIntermediateRenderTime = now + Mathf.Max(
            MinimumIntermediateRenderThrottleSeconds,
            Mathf.Min(MaximumIntermediateRenderThrottleSeconds, PeakRoutePlannerConfig.IntermediateRenderThrottleSeconds.Value));
    }

    private void RenderSamplingWindowPreview(bool force)
    {
        if (!PeakRoutePlannerConfig.RenderSamplingWindowPreview.Value
            || !sampler.HasActiveSeedPreview)
        {
            if (force && !PeakRoutePlannerConfig.RenderSamplingWindowPreview.Value)
            {
                pathRenderer.RenderSamplingWindowPreview(Vector3.zero, Vector3.zero);
            }

            return;
        }

        if (pathRenderer.RenderSamplingWindowPreview(sampler.ActiveSampleWindowCenter, sampler.ActiveSampleWindowSize))
        {
            LogDetail(
                $"Rendered sampling window preview: center=({sampler.ActiveSampleWindowCenter.x:0.0},{sampler.ActiveSampleWindowCenter.y:0.0},{sampler.ActiveSampleWindowCenter.z:0.0}), size=({sampler.ActiveSampleWindowSize.x:0.0},{sampler.ActiveSampleWindowSize.y:0.0},{sampler.ActiveSampleWindowSize.z:0.0}).");
        }
    }

    private List<Vector3> BuildIntermediatePreviewPath(out bool usedLocalFallback)
    {
        int pointCount = sampler.Points.Count;
        int edgeCount = edgeValidator.Edges.Count;
        if (cachedPreviewPointCount == pointCount && cachedPreviewEdgeCount == edgeCount)
        {
            usedLocalFallback = cachedPreviewUsedLocalFallback;
            return cachedPreviewPath;
        }

        List<Vector3> path = BuildValidatedEdgePreviewPath(out usedLocalFallback);
        cachedPreviewPointCount = pointCount;
        cachedPreviewEdgeCount = edgeCount;
        cachedPreviewUsedLocalFallback = usedLocalFallback;
        cachedPreviewPath = path;
        return path;
    }

    private void ClearPreviewCache()
    {
        cachedPreviewPointCount = -1;
        cachedPreviewEdgeCount = -1;
        cachedPreviewUsedLocalFallback = false;
        cachedPreviewPath = [];
        cachedTargetPreviewPointCount = -1;
        cachedTargetPreviewEdgeCount = -1;
        cachedTargetPreviewPath = [];
    }

    private List<Vector3> BuildValidatedEdgePreviewPath(out bool usedLocalFallback)
    {
        usedLocalFallback = false;
        IReadOnlyList<SurfacePoint> points = sampler.Points;
        IReadOnlyList<RouteEdge> edges = edgeValidator.Edges;
        if (edges.Count == 0
            || sampler.StartIndex < 0
            || sampler.StartIndex >= points.Count
            || sampler.TargetIndex < 0
            || sampler.TargetIndex >= points.Count)
        {
            return [];
        }

        Dictionary<int, List<RouteEdge>> adjacency = [];
        for (int index = 0; index < edges.Count; index++)
        {
            RouteEdge edge = edges[index];
            AddAdjacency(adjacency, edge);
        }

        GuidePointMetrics guideMetrics = GuideProjectionMap.Build(guidePath).BuildPointMetrics(points);
        AnchorSelection anchors = ResolveEffectiveAnchors(logSelection: false);
        int start = anchors.StartIndex;
        int target = anchors.TargetIndex;
        float[] best = new float[points.Count];
        int[] previous = new int[points.Count];
        Array.Fill(best, float.PositiveInfinity);
        Array.Fill(previous, -1);
        PriorityQueue<int> open = new();
        best[start] = 0f;
        open.Enqueue(start, 0f);

        int expanded = 0;
        while (open.Count > 0 && expanded < MaxIntermediatePreviewEdges)
        {
            int current = open.Dequeue();
            if (current == target)
            {
                return ReconstructPreviewPath(start, target, previous, points);
            }

            expanded++;
            if (!adjacency.TryGetValue(current, out List<RouteEdge> neighbors))
            {
                continue;
            }

            foreach (RouteEdge edge in neighbors)
            {
                float tentative = best[current] + GetPreviewEdgeCost(edge, target, guideMetrics);
                if (tentative + 0.001f >= best[edge.To])
                {
                    continue;
                }

                best[edge.To] = tentative;
                previous[edge.To] = current;
                open.Enqueue(edge.To, tentative);
            }
        }

        int closestTarget = FindClosestReachableToTarget(start, target, best, guideMetrics, points);
        if (closestTarget >= 0)
        {
            return ReconstructPreviewPath(start, closestTarget, previous, points);
        }

        int adaptiveTarget = FindAdaptivePreviewTarget(start, best, guideMetrics);
        if (adaptiveTarget >= 0)
        {
            return ReconstructPreviewPath(start, adaptiveTarget, previous, points);
        }

        int bestLocalFallbackNode = FindLocalFallbackPreviewTarget(start, best, guideMetrics);
        if (bestLocalFallbackNode >= 0)
        {
            usedLocalFallback = true;
            return ReconstructPreviewPath(start, bestLocalFallbackNode, previous, points);
        }

        int bestDetourFallbackNode = FindDetourFallbackPreviewTarget(start, target, best, guideMetrics, points);
        if (bestDetourFallbackNode < 0)
        {
            return [];
        }

        usedLocalFallback = true;
        return ReconstructPreviewPath(start, bestDetourFallbackNode, previous, points);
    }

    private List<Vector3> BuildTargetFrontierPreviewPath()
    {
        if (!config.EnableBidirectionalFrontierSampling)
        {
            return [];
        }

        int pointCount = sampler.Points.Count;
        int edgeCount = edgeValidator.Edges.Count;
        if (cachedTargetPreviewPointCount == pointCount && cachedTargetPreviewEdgeCount == edgeCount)
        {
            return cachedTargetPreviewPath;
        }

        List<Vector3> path = BuildValidatedTargetFrontierPreviewPath();
        cachedTargetPreviewPointCount = pointCount;
        cachedTargetPreviewEdgeCount = edgeCount;
        cachedTargetPreviewPath = path;
        return path;
    }

    private List<Vector3> BuildValidatedTargetFrontierPreviewPath()
    {
        IReadOnlyList<SurfacePoint> points = sampler.Points;
        IReadOnlyList<RouteEdge> edges = edgeValidator.Edges;
        if (edges.Count == 0
            || sampler.StartIndex < 0
            || sampler.StartIndex >= points.Count
            || sampler.TargetIndex < 0
            || sampler.TargetIndex >= points.Count)
        {
            return [];
        }

        Dictionary<int, List<RouteEdge>> reverseAdjacency = [];
        for (int index = 0; index < edges.Count; index++)
        {
            AddReverseAdjacency(reverseAdjacency, edges[index]);
        }

        GuidePointMetrics guideMetrics = GuideProjectionMap.Build(guidePath).BuildPointMetrics(points);
        AnchorSelection anchors = ResolveEffectiveAnchors(logSelection: false);
        int start = anchors.StartIndex;
        int target = anchors.TargetIndex;
        float[] best = new float[points.Count];
        int[] nextTowardTarget = new int[points.Count];
        Array.Fill(best, float.PositiveInfinity);
        Array.Fill(nextTowardTarget, -1);
        PriorityQueue<int> open = new();
        best[target] = 0f;
        open.Enqueue(target, 0f);

        int expanded = 0;
        while (open.Count > 0 && expanded < MaxTargetFrontierPreviewEdges)
        {
            int current = open.Dequeue();
            expanded++;
            if (!reverseAdjacency.TryGetValue(current, out List<RouteEdge> incoming))
            {
                continue;
            }

            foreach (RouteEdge edge in incoming)
            {
                float tentative = best[current] + GetTargetPreviewEdgeCost(edge, start, guideMetrics);
                if (tentative + 0.001f >= best[edge.From])
                {
                    continue;
                }

                best[edge.From] = tentative;
                nextTowardTarget[edge.From] = current;
                open.Enqueue(edge.From, tentative);
            }
        }

        int endpoint = FindBestTargetFrontierPreviewEndpoint(start, target, best, guideMetrics, points);
        return endpoint >= 0
            ? ReconstructTargetPreviewPath(target, endpoint, nextTowardTarget, points)
            : [];
    }

    private int FindClosestReachableToTarget(
        int start,
        int target,
        IReadOnlyList<float> pathCosts,
        GuidePointMetrics guideMetrics,
        IReadOnlyList<SurfacePoint> points)
    {
        int bestNode = -1;
        float bestScore = float.PositiveInfinity;
        float startProgress = guideMetrics.Progress[start];
        float startTargetDistance = Vector3.Distance(points[start].Position, points[target].Position);
        for (int index = 0; index < pathCosts.Count; index++)
        {
            if (index == start || float.IsInfinity(pathCosts[index]))
            {
                continue;
            }

            float targetDistance = Vector3.Distance(points[index].Position, points[target].Position);
            float progressGain = guideMetrics.Progress[index] - startProgress;
            bool makesImmediateProgress = targetDistance + 0.25f < startTargetDistance
                || progressGain > MinimumGuideProgressGain;
            float detourPenalty = makesImmediateProgress
                ? 0f
                : Mathf.Max(2f, currentCorridorRadius) * 2f;
            float remainingGuideDistance = Mathf.Max(0f, guideMetrics.TotalLength - guideMetrics.Progress[index]);
            float score = targetDistance
                + remainingGuideDistance * 0.2f
                + guideMetrics.Distances[index] * config.SearchGuideDistanceWeight
                + pathCosts[index] * 0.05f
                + detourPenalty;
            if (score >= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestNode = index;
        }

        return bestNode;
    }

    private int FindAdaptivePreviewTarget(
        int start,
        IReadOnlyList<float> pathCosts,
        GuidePointMetrics guideMetrics)
    {
        float startProgress = guideMetrics.Progress[start];
        float high = Mathf.Max(startProgress, guideMetrics.TotalLength);
        if (high - startProgress <= MinimumGuideProgressGain)
        {
            return -1;
        }

        float low = startProgress;
        int bestNode = -1;
        float minimumStep = Mathf.Max(0.5f, config.AdaptiveGuideMinimumStep);
        for (int step = 0; step < MaxAdaptivePreviewBinarySteps && high - low > minimumStep; step++)
        {
            float midpoint = (low + high) * 0.5f;
            int candidate = FindBestReachableAtProgress(
                start,
                pathCosts,
                guideMetrics,
                midpoint);
            if (candidate >= 0)
            {
                low = midpoint;
                bestNode = candidate;
            }
            else
            {
                high = midpoint;
            }
        }

        if (bestNode >= 0)
        {
            int refined = FindBestReachableAtProgress(start, pathCosts, guideMetrics, low);
            return refined >= 0 ? refined : bestNode;
        }

        return FindBestReachableAtProgress(
            start,
            pathCosts,
            guideMetrics,
            startProgress + minimumStep);
    }

    private int FindBestReachableAtProgress(
        int start,
        IReadOnlyList<float> pathCosts,
        GuidePointMetrics guideMetrics,
        float requiredProgress)
    {
        int bestNode = -1;
        float bestScore = float.PositiveInfinity;
        float startProgress = guideMetrics.Progress[start];
        float lateralLimit = Mathf.Max(
            config.HorizontalSampleSpacing * 2.5f,
            config.SurfaceNeighborDistance * 2.5f);
        for (int index = 0; index < pathCosts.Count; index++)
        {
            if (index == start || float.IsInfinity(pathCosts[index]))
            {
                continue;
            }

            float progress = guideMetrics.Progress[index];
            if (progress + GuideProgressTieTolerance < requiredProgress
                || progress <= startProgress + MinimumGuideProgressGain)
            {
                continue;
            }

            float lateralDistance = guideMetrics.Distances[index];
            if (lateralDistance > lateralLimit && lateralDistance > config.CorridorInitialRadius * 0.75f)
            {
                continue;
            }

            float progressOvershoot = Mathf.Max(0f, progress - requiredProgress);
            float score = guideMetrics.Distances[index] * 4f
                + pathCosts[index] * 0.2f
                + progressOvershoot * 0.05f;
            if (score >= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestNode = index;
        }

        return bestNode;
    }

    private int FindLocalFallbackPreviewTarget(
        int start,
        IReadOnlyList<float> pathCosts,
        GuidePointMetrics guideMetrics)
    {
        int bestNode = -1;
        float bestProgressGain = float.NegativeInfinity;
        float bestLateralDistance = float.MaxValue;
        float bestPathCost = float.PositiveInfinity;
        float startProgress = guideMetrics.Progress[start];
        for (int index = 0; index < pathCosts.Count; index++)
        {
            if (index == start || float.IsInfinity(pathCosts[index]))
            {
                continue;
            }

            float progressGain = guideMetrics.Progress[index] - startProgress;
            float lateralDistance = guideMetrics.Distances[index];
            if (!IsBetterLocalFallbackPreviewNode(
                progressGain,
                lateralDistance,
                pathCosts[index],
                bestProgressGain,
                bestLateralDistance,
                bestPathCost))
            {
                continue;
            }

            bestNode = index;
            bestProgressGain = progressGain;
            bestLateralDistance = lateralDistance;
            bestPathCost = pathCosts[index];
        }

        return bestNode;
    }

    private bool IsBetterLocalFallbackPreviewNode(
        float progressGain,
        float lateralDistance,
        float pathCost,
        float bestProgressGain,
        float bestLateralDistance,
        float bestPathCost)
    {
        float allowedBacktrack = Mathf.Max(6f, currentCorridorRadius);
        if (progressGain < -allowedBacktrack)
        {
            return false;
        }

        if (lateralDistance + GuideLateralTieTolerance < bestLateralDistance)
        {
            return true;
        }

        if (lateralDistance > bestLateralDistance + GuideLateralTieTolerance)
        {
            return false;
        }

        if (progressGain > bestProgressGain + GuideProgressTieTolerance)
        {
            return true;
        }

        if (progressGain + GuideProgressTieTolerance < bestProgressGain)
        {
            return false;
        }

        return pathCost < bestPathCost;
    }

    private int FindDetourFallbackPreviewTarget(
        int start,
        int target,
        IReadOnlyList<float> pathCosts,
        GuidePointMetrics guideMetrics,
        IReadOnlyList<SurfacePoint> points)
    {
        int bestNode = -1;
        float bestScore = float.PositiveInfinity;
        for (int index = 0; index < pathCosts.Count; index++)
        {
            if (index == start || float.IsInfinity(pathCosts[index]))
            {
                continue;
            }

            float score = Vector3.Distance(points[index].Position, points[target].Position)
                + guideMetrics.Distances[index] * config.SearchGuideDistanceWeight
                + pathCosts[index] * 0.15f;
            if (score >= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestNode = index;
        }

        return bestNode;
    }

    private static void AddAdjacency(Dictionary<int, List<RouteEdge>> adjacency, RouteEdge edge)
    {
        if (!adjacency.TryGetValue(edge.From, out List<RouteEdge> neighbors))
        {
            neighbors = [];
            adjacency[edge.From] = neighbors;
        }

        neighbors.Add(edge);
    }

    private static void AddReverseAdjacency(Dictionary<int, List<RouteEdge>> adjacency, RouteEdge edge)
    {
        if (!adjacency.TryGetValue(edge.To, out List<RouteEdge> neighbors))
        {
            neighbors = [];
            adjacency[edge.To] = neighbors;
        }

        neighbors.Add(edge);
    }

    private void LogEdgeConnectivity()
    {
        if (!PeakRoutePlannerConfig.LogRoutePlannerDetails.Value)
        {
            return;
        }

        IReadOnlyList<SurfacePoint> points = sampler.Points;
        IReadOnlyList<RouteEdge> edges = edgeValidator.Edges;
        if (sampler.StartIndex < 0
            || sampler.StartIndex >= points.Count
            || sampler.TargetIndex < 0
            || sampler.TargetIndex >= points.Count)
        {
            Plugin.Log.LogInfo(
                $"Route edge connectivity: invalid anchors start={sampler.StartIndex}, target={sampler.TargetIndex}, points={points.Count}.");
            return;
        }

        EdgeConnectivity connectivity = BuildEdgeConnectivity(edges, points.Count);
        AnchorSelection anchors = ResolveEffectiveAnchors(connectivity, logSelection: false);
        int reachable = CountReachableNodes(connectivity.Adjacency, anchors.StartIndex, points.Count);
        Plugin.Log.LogInfo(
            $"Route edge connectivity: startOutgoing={connectivity.OutgoingCounts[sampler.StartIndex]}, targetIncoming={connectivity.IncomingCounts[sampler.TargetIndex]}, effectiveStart={anchors.StartIndex}, effectiveTarget={anchors.TargetIndex}, reachableFromEffectiveStart={reachable}/{points.Count}.");
    }

    private AnchorSelection ResolveEffectiveAnchors(bool logSelection)
    {
        return ResolveEffectiveAnchors(
            BuildEdgeConnectivity(edgeValidator.Edges, sampler.Points.Count),
            logSelection);
    }

    private AnchorSelection ResolveEffectiveAnchors(EdgeConnectivity connectivity, bool logSelection)
    {
        IReadOnlyList<SurfacePoint> points = sampler.Points;
        int start = ResolveEffectiveStartIndex(points, connectivity.OutgoingCounts);
        int target = ResolveEffectiveTargetIndex(points, connectivity.IncomingCounts);
        if (logSelection && (start != sampler.StartIndex || target != sampler.TargetIndex))
        {
            Plugin.Log.LogInfo(
                $"Route anchors remapped: start {sampler.StartIndex}->{start}, target {sampler.TargetIndex}->{target}.");
        }

        return new AnchorSelection(start, target);
    }

    private int ResolveEffectiveStartIndex(IReadOnlyList<SurfacePoint> points, int[] outgoingCounts)
    {
        if (!IsValidPointIndex(sampler.StartIndex, points.Count))
        {
            return sampler.StartIndex;
        }

        if (outgoingCounts[sampler.StartIndex] > 0)
        {
            return sampler.StartIndex;
        }

        return FindNearestConnectedPoint(
            points,
            outgoingCounts,
            sampler.StartIndex,
            StartAnchorRemapDistance);
    }

    private int ResolveEffectiveTargetIndex(IReadOnlyList<SurfacePoint> points, int[] incomingCounts)
    {
        if (!IsValidPointIndex(sampler.TargetIndex, points.Count))
        {
            return sampler.TargetIndex;
        }

        if (incomingCounts[sampler.TargetIndex] > 0)
        {
            return sampler.TargetIndex;
        }

        return FindNearestConnectedPoint(
            points,
            incomingCounts,
            sampler.TargetIndex,
            TargetAnchorRemapDistance);
    }

    private static int FindNearestConnectedPoint(
        IReadOnlyList<SurfacePoint> points,
        int[] connectionCounts,
        int anchorIndex,
        float maxDistance)
    {
        int best = anchorIndex;
        float bestScore = float.MaxValue;
        Vector3 anchorPosition = points[anchorIndex].Position;
        for (int index = 0; index < points.Count; index++)
        {
            if (index == anchorIndex || connectionCounts[index] <= 0)
            {
                continue;
            }

            float distance = Vector3.Distance(anchorPosition, points[index].Position);
            if (distance > maxDistance)
            {
                continue;
            }

            float kindPenalty = points[index].Kind == SurfaceKind.Standable ? 0f : 1.5f;
            float score = distance + kindPenalty;
            if (score >= bestScore)
            {
                continue;
            }

            best = index;
            bestScore = score;
        }

        return best;
    }

    private static bool IsValidPointIndex(int index, int count)
    {
        return index >= 0 && index < count;
    }

    private static EdgeConnectivity BuildEdgeConnectivity(IReadOnlyList<RouteEdge> edges, int pointCount)
    {
        Dictionary<int, List<RouteEdge>> adjacency = [];
        int[] outgoingCounts = new int[pointCount];
        int[] incomingCounts = new int[pointCount];
        for (int index = 0; index < edges.Count; index++)
        {
            RouteEdge edge = edges[index];
            if (edge.From < 0 || edge.From >= pointCount || edge.To < 0 || edge.To >= pointCount)
            {
                continue;
            }

            AddAdjacency(adjacency, edge);
            outgoingCounts[edge.From]++;
            incomingCounts[edge.To]++;
        }

        return new EdgeConnectivity(adjacency, outgoingCounts, incomingCounts);
    }

    private static int CountReachableNodes(Dictionary<int, List<RouteEdge>> adjacency, int start, int pointCount)
    {
        if (start < 0 || start >= pointCount)
        {
            return 0;
        }

        bool[] visited = new bool[pointCount];
        Queue<int> queue = new();
        visited[start] = true;
        queue.Enqueue(start);
        int count = 0;
        while (queue.Count > 0)
        {
            int current = queue.Dequeue();
            count++;
            if (!adjacency.TryGetValue(current, out List<RouteEdge> neighbors))
            {
                continue;
            }

            foreach (RouteEdge edge in neighbors)
            {
                if (edge.To < 0 || edge.To >= pointCount || visited[edge.To])
                {
                    continue;
                }

                visited[edge.To] = true;
                queue.Enqueue(edge.To);
            }
        }

        return count;
    }

    private float GetPreviewEdgeCost(RouteEdge edge, int target, GuidePointMetrics guideMetrics)
    {
        float progress = Mathf.Abs(guideMetrics.Progress[target] - guideMetrics.Progress[edge.From])
            - Mathf.Abs(guideMetrics.Progress[target] - guideMetrics.Progress[edge.To]);
        float backtrackPenalty = progress < 0f ? -progress * config.SearchBacktrackPenaltyMultiplier : 0f;
        float longStepPenalty = Mathf.Max(0f, edge.Distance - Mathf.Max(progress, 0.05f))
            * config.SearchLongStepPenaltyMultiplier;
        return edge.Distance
            + edge.StaminaCost * 8f
            + guideMetrics.Distances[edge.To] * config.SearchGuideDistanceWeight
            + GetMovePenalty(edge.Kind)
            + backtrackPenalty
            + longStepPenalty;
    }

    private float GetTargetPreviewEdgeCost(RouteEdge edge, int start, GuidePointMetrics guideMetrics)
    {
        float progress = Mathf.Abs(guideMetrics.Progress[start] - guideMetrics.Progress[edge.To])
            - Mathf.Abs(guideMetrics.Progress[start] - guideMetrics.Progress[edge.From]);
        float backtrackPenalty = progress < 0f ? -progress * config.SearchBacktrackPenaltyMultiplier : 0f;
        float longStepPenalty = Mathf.Max(0f, edge.Distance - Mathf.Max(progress, 0.05f))
            * config.SearchLongStepPenaltyMultiplier;
        return edge.Distance
            + edge.StaminaCost * 8f
            + guideMetrics.Distances[edge.From] * config.SearchGuideDistanceWeight
            + GetMovePenalty(edge.Kind)
            + backtrackPenalty
            + longStepPenalty;
    }

    private float GetMovePenalty(MoveKind kind)
    {
        return kind switch
        {
            MoveKind.SurfaceClimb => config.SurfaceClimbMovePenalty,
            MoveKind.StandJump => config.StandJumpMovePenalty,
            MoveKind.AirTransfer => config.AirTransferMovePenalty,
            _ => 0f,
        };
    }

    private static List<Vector3> ReconstructPreviewPath(
        int start,
        int target,
        int[] previous,
        IReadOnlyList<SurfacePoint> points)
    {
        List<int> nodes = [];
        int current = target;
        int guard = previous.Length + 1;
        while (current >= 0 && guard-- > 0)
        {
            nodes.Add(current);
            if (current == start)
            {
                break;
            }

            current = previous[current];
        }

        nodes.Reverse();
        if (nodes.Count == 0 || nodes[0] != start)
        {
            return [];
        }

        List<Vector3> path = [];
        int stride = Mathf.Max(1, Mathf.CeilToInt(nodes.Count / (float)MaxIntermediatePathPoints));
        for (int index = 0; index < nodes.Count; index += stride)
        {
            path.Add(points[nodes[index]].Position);
        }

        Vector3 last = points[target].Position;
        if (path.Count == 0 || (path[path.Count - 1] - last).sqrMagnitude > 0.0001f)
        {
            path.Add(last);
        }

        return path;
    }

    private int FindBestTargetFrontierPreviewEndpoint(
        int start,
        int target,
        IReadOnlyList<float> pathCosts,
        GuidePointMetrics guideMetrics,
        IReadOnlyList<SurfacePoint> points)
    {
        int bestNode = -1;
        float bestScore = float.PositiveInfinity;
        float targetProgress = guideMetrics.Progress[target];
        for (int index = 0; index < pathCosts.Count; index++)
        {
            if (index == target || float.IsInfinity(pathCosts[index]))
            {
                continue;
            }

            float progressTowardStart = targetProgress - guideMetrics.Progress[index];
            if (progressTowardStart <= MinimumGuideProgressGain
                && Vector3.Distance(points[index].Position, points[target].Position) <= config.SurfaceNeighborDistance)
            {
                continue;
            }

            float startDistance = Vector3.Distance(points[index].Position, points[start].Position);
            float score = startDistance
                + guideMetrics.Distances[index] * config.SearchGuideDistanceWeight
                + pathCosts[index] * 0.05f
                - Mathf.Max(0f, progressTowardStart) * 0.15f;
            if (score >= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestNode = index;
        }

        return bestNode;
    }

    private static List<Vector3> ReconstructTargetPreviewPath(
        int target,
        int endpoint,
        int[] nextTowardTarget,
        IReadOnlyList<SurfacePoint> points)
    {
        List<int> nodes = [];
        int current = endpoint;
        int guard = nextTowardTarget.Length + 1;
        while (current >= 0 && guard-- > 0)
        {
            nodes.Add(current);
            if (current == target)
            {
                break;
            }

            current = nextTowardTarget[current];
        }

        nodes.Reverse();
        if (nodes.Count == 0 || nodes[0] != target)
        {
            return [];
        }

        List<Vector3> path = [];
        int stride = Mathf.Max(1, Mathf.CeilToInt(nodes.Count / (float)MaxIntermediatePathPoints));
        for (int index = 0; index < nodes.Count; index += stride)
        {
            path.Add(points[nodes[index]].Position);
        }

        Vector3 last = points[endpoint].Position;
        if (path.Count == 0 || (path[path.Count - 1] - last).sqrMagnitude > 0.0001f)
        {
            path.Add(last);
        }

        return path;
    }

    private bool IsFinalPathBuiltFromValidatedEdges(RouteResult result)
    {
        if (result.Path.Count < 2 || result.NodeIds.Count < 2)
        {
            return false;
        }

        for (int index = 1; index < result.NodeIds.Count; index++)
        {
            if (!edgeValidator.HasValidatedEdge(result.NodeIds[index - 1], result.NodeIds[index]))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryResolveTarget(out Vector3 target)
    {
        if (PeakRoutePlannerConfig.UseManualTargetPosition.Value)
        {
            target = PeakRoutePlannerConfig.ManualTargetPosition;
            return true;
        }

        Campfire[] campfires = Object.FindObjectsByType<Campfire>(FindObjectsSortMode.None);
        if (campfires.Length > 0)
        {
            Campfire highest = campfires.OrderByDescending(campfire => campfire.Center().y).First();
            target = highest.Center();
            return true;
        }

        string[] keywords = PeakRoutePlannerConfig.TargetNameKeywords.Value
            .Split([','], StringSplitOptions.RemoveEmptyEntries)
            .Select(keyword => keyword.Trim())
            .Where(keyword => keyword.Length > 0)
            .ToArray();
        if (keywords.Length == 0)
        {
            target = default;
            return false;
        }

        Transform? best = null;
        foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
        {
            foreach (Transform transform in root.GetComponentsInChildren<Transform>(includeInactive: false))
            {
                if (!keywords.Any(keyword => transform.name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    continue;
                }

                if (best == null || transform.position.y > best.position.y)
                {
                    best = transform;
                }
            }
        }

        if (best == null)
        {
            target = default;
            return false;
        }

        target = best.position;
        return true;
    }

    private static Vector3 GetCharacterPosition(Character character)
    {
        try
        {
            return character.Center;
        }
        catch
        {
            return character.transform.position;
        }
    }

    private static void LogDetail(string message)
    {
        if (PeakRoutePlannerConfig.LogRoutePlannerDetails.Value)
        {
            Plugin.Log.LogInfo(message);
        }
    }

    private enum PlannerState
    {
        Idle,
        Sampling,
        ValidatingEdges,
        WaitingForWorker,
    }

    private readonly struct AnchorSelection
    {
        internal AnchorSelection(int startIndex, int targetIndex)
        {
            StartIndex = startIndex;
            TargetIndex = targetIndex;
        }

        internal int StartIndex { get; }

        internal int TargetIndex { get; }
    }

    private readonly struct EdgeConnectivity
    {
        internal EdgeConnectivity(
            Dictionary<int, List<RouteEdge>> adjacency,
            int[] outgoingCounts,
            int[] incomingCounts)
        {
            Adjacency = adjacency;
            OutgoingCounts = outgoingCounts;
            IncomingCounts = incomingCounts;
        }

        internal Dictionary<int, List<RouteEdge>> Adjacency { get; }

        internal int[] OutgoingCounts { get; }

        internal int[] IncomingCounts { get; }
    }
}
