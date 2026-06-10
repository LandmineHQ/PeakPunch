using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using PeakRoutePlanner.Configuration;
using PeakRoutePlanner.Visualization;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace PeakRoutePlanner.Planning;

internal sealed class RoutePlannerRuntime
{
    private readonly SurfaceSampler sampler = new();
    private readonly SurfaceSampleDebugRenderer sampleRenderer = new();
    private readonly SamplingWindowRenderer windowRenderer;
    private readonly Stopwatch samplingStopwatch = new();

    private PlannerConfig config = null!;
    private bool samplingActive;
    private bool hasRenderedSamples;
    private double samplingElapsedMilliseconds;
    private string activeSamplingLabel = string.Empty;
    private SamplingMode activeSamplingMode = SamplingMode.None;
    private RouteSearchRun? activeRouteSearch;

    internal RoutePlannerRuntime(SamplingWindowRenderer windowRenderer)
    {
        this.windowRenderer = windowRenderer;
    }

    internal void Update()
    {
        if (!PeakRoutePlannerConfig.EnableRoutePlanner.Value)
        {
            return;
        }

        if (PeakRoutePlannerConfig.ClearRouteShortcut.Value.IsDown())
        {
            CleanupSampling("clear-shortcut");
            Plugin.Log.LogInfo("Cleared PeakRoutePlanner sampling state.");
            return;
        }

        if (PeakRoutePlannerConfig.DebugSampleBlockShortcut.Value.IsDown())
        {
            TogglePlayerForwardSampling();
        }

        if (PeakRoutePlannerConfig.DebugVerticalAirColumnShortcut.Value.IsDown())
        {
            RunVerticalAirColumnDebug();
        }

        if (PeakRoutePlannerConfig.PlanRouteShortcut.Value.IsDown())
        {
            StartRoutePlanner();
        }

        if (samplingActive)
        {
            UpdateSampling();
        }
    }

    internal void Cleanup()
    {
        CleanupSampling("cleanup");
        sampleRenderer.Clear();
        windowRenderer.Cleanup();
    }

    private void TogglePlayerForwardSampling()
    {
        if (samplingActive || hasRenderedSamples)
        {
            CleanupSampling("debug-toggle-clear");
            Plugin.Log.LogInfo("Cleared PeakRoutePlanner surface sample markers.");
            return;
        }

        if (!TryGetLocalPlayerPosition(out Vector3 playerPosition, out Character localCharacter))
        {
            Plugin.Log.LogWarning("Debug surface sampling skipped because Character.localCharacter is unavailable.");
            return;
        }

        Vector3 forward = localCharacter.transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f)
        {
            forward = Vector3.forward;
        }

        StartSurfaceSampling(
            playerPosition,
            playerPosition + forward.normalized * PlannerDefaults.DefaultSamplingGuideDistance,
            "debug surface block sampling",
            prioritizeGuidedSampling: false);
    }

    private void RunVerticalAirColumnDebug()
    {
        CleanupSampling("vertical-air-column-debug");
        if (!TryGetLocalPlayerPosition(out Vector3 playerPosition, out Character localCharacter))
        {
            Plugin.Log.LogWarning("Vertical air-column debug skipped because Character.localCharacter is unavailable.");
            return;
        }

        config = PlannerDefaults.ToPlannerConfig(localCharacter);
        VerticalAirColumnDebugResult result = sampler.BuildVerticalAirColumnDebug(playerPosition, config);
        sampleRenderer.Render(
            sampler.Points,
            sampler.DebugAirCellCenters,
            PeakRoutePlannerConfig.RenderDebugAirBoundaryProbes.Value ? sampler.DebugAirBoundaryProbes : []);
        hasRenderedSamples = sampler.Points.Count > 0
            || sampler.DebugAirCellCenters.Count > 0
            || (PeakRoutePlannerConfig.RenderDebugAirBoundaryProbes.Value && sampler.DebugAirBoundaryProbes.Count > 0);

        Plugin.Log.LogInfo(
            $"Vertical air-column debug complete: seed=({result.SeedPosition.x:0.00},{result.SeedPosition.y:0.00},{result.SeedPosition.z:0.00}), probeOrigin=({result.ProbeOrigin.x:0.00},{result.ProbeOrigin.y:0.00},{result.ProbeOrigin.z:0.00}), blockedCell=({result.BlockedCellCenter.x:0.00},{result.BlockedCellCenter.y:0.00},{result.BlockedCellCenter.z:0.00}), airCells={result.AirCellCount}, checkedCells={result.CheckedCellCount}, rawHits={result.RawHitCount}, hasBoundary={result.HasBoundary}, hasSurfacePoint={result.HasSurfacePoint}, surfaceKind={result.SurfaceKind}, reason={result.Reason}, elapsedMs={result.ElapsedMilliseconds:0.00}.");
    }

    private void StartRoutePlanner()
    {
        CleanupSampling("route-planner-start");

        if (!TryGetLocalPlayerPosition(out Vector3 playerPosition, out Character localCharacter))
        {
            Plugin.Log.LogWarning("Route planner skipped because Character.localCharacter is unavailable.");
            return;
        }

        if (!GetCampfireTargetPosition(out Vector3 campfirePosition))
        {
            Plugin.Log.LogWarning("Route planner skipped because no campfire or configured target object could be found.");
            return;
        }

        config = PlannerDefaults.ToPlannerConfig(localCharacter);
        RouteSearchSettings settings = new(
            PeakRoutePlannerConfig.MaxRoutePlannerSteps.Value,
            PeakRoutePlannerConfig.RouteTargetReachedDistance.Value,
            PeakRoutePlannerConfig.RouteRegionMergeDistance.Value,
            PeakRoutePlannerConfig.RouteEdgeValidationPairLimit.Value);
        activeRouteSearch = new RouteSearchRun(playerPosition, campfirePosition, config, settings);
        activeSamplingMode = SamplingMode.Route;

        Plugin.Log.LogInfo(
            $"Route planner started: player=({playerPosition.x:0.0},{playerPosition.y:0.0},{playerPosition.z:0.0}), target=({campfirePosition.x:0.0},{campfirePosition.y:0.0},{campfirePosition.z:0.0}), maxSteps={settings.MaxSteps}, targetReachedDistance={settings.TargetReachedDistance:0.0}, regionMergeDistance={settings.RegionMergeDistance:0.00}, edgePairLimit={settings.EdgeValidationPairLimit}.");
        LogStaminaSnapshot();
        StartRouteSamplingStep(playerPosition, preserveSampleCache: false);
    }

    private void StartSurfaceSampling(
        Vector3 start,
        Vector3 target,
        string label,
        bool prioritizeGuidedSampling)
    {
        CleanupSampling("restart");

        config = PlannerDefaults.ToPlannerConfig(Character.localCharacter);
        activeSamplingMode = SamplingMode.Debug;
        IReadOnlyList<Vector3> guidePath = [start, target];
        activeSamplingLabel = label;
        samplingElapsedMilliseconds = 0d;
        samplingActive = true;
        hasRenderedSamples = false;

        sampler.Begin(
            start,
            target,
            guidePath,
            PlannerDefaults.DefaultSamplingCorridorRadius,
            config,
            preserveSampleCache: false,
            includeTargetFrontier: false,
            constrainToGuide: false,
            enforcePointLimitPerWindow: false,
            sampleFullWindowBySlices: true,
            prioritizeGuidedSampling: prioritizeGuidedSampling);

        windowRenderer.CreateSamplingWindowPreview();
        RenderSamplingWindowPreview(force: true);
        RestartSamplingTimer();

        Plugin.Log.LogInfo(
            $"Started {label} at ({start.x:0.0},{start.y:0.0},{start.z:0.0}), target=({target.x:0.0},{target.y:0.0},{target.z:0.0}), radius={config.SurfaceSamplingWindowRadius:0.0}, diameter={config.SurfaceSamplingWindowRadius * 2f:0.0}, windowSize=({sampler.ActiveSampleWindowSize.x:0.0},{sampler.ActiveSampleWindowSize.y:0.0},{sampler.ActiveSampleWindowSize.z:0.0}), pendingRays={sampler.PendingRayCount}.");
        LogStaminaSnapshot();
    }

    private void StartRouteSamplingStep(Vector3 seedPosition, bool preserveSampleCache)
    {
        RouteSearchRun? routeSearch = activeRouteSearch;
        if (routeSearch == null)
        {
            return;
        }

        routeSearch.MarkSamplingSeed(seedPosition);
        IReadOnlyList<Vector3> guidePath = [seedPosition, routeSearch.TargetPosition];
        activeSamplingLabel = $"route planner step {routeSearch.StepIndex + 1}";
        samplingElapsedMilliseconds = 0d;
        samplingActive = true;
        activeSamplingMode = SamplingMode.Route;

        sampler.Begin(
            seedPosition,
            routeSearch.TargetPosition,
            guidePath,
            PlannerDefaults.DefaultSamplingCorridorRadius,
            config,
            preserveSampleCache: preserveSampleCache,
            includeTargetFrontier: false,
            constrainToGuide: false,
            enforcePointLimitPerWindow: false,
            sampleFullWindowBySlices: true,
            prioritizeGuidedSampling: false);

        windowRenderer.CreateSamplingWindowPreview();
        RenderSamplingWindowPreview(force: true);
        RestartSamplingTimer();

        Plugin.Log.LogInfo(
            $"Started {activeSamplingLabel}: seed=({seedPosition.x:0.0},{seedPosition.y:0.0},{seedPosition.z:0.0}), target=({routeSearch.TargetPosition.x:0.0},{routeSearch.TargetPosition.y:0.0},{routeSearch.TargetPosition.z:0.0}), preserveCache={preserveSampleCache}, cachedPoints={sampler.CachedPointCountAtAttemptStart}, radius={config.SurfaceSamplingWindowRadius:0.0}, pendingRays={sampler.PendingRayCount}.");
    }

    private void LogStaminaSnapshot()
    {
        Plugin.Log.LogInfo(
            $"Stamina snapshot captured at trigger time: current={config.CurrentRegularStamina:0.000}, ascentsMultiplier={config.AscentStaminaMultiplier:0.000}, sprintUsage={config.SprintStaminaUsagePerSecond:0.000}/s, jumpCost={config.JumpStaminaCost:0.000}, sprintJumpCost={config.SprintJumpStaminaCost:0.000}, climbSpeed={config.ClimbSpeed:0.000}, climbUsage={config.ClimbStaminaUsagePerSecond:0.000}/s. Later validation uses this cached snapshot only.");
    }

    private void UpdateSampling()
    {
        bool complete = sampler.ProcessFrame();
        AccumulateSamplingTime();
        RenderSamplingWindowPreview(force: true);

        if (!complete)
        {
            RestartSamplingTimer();
            return;
        }

        samplingActive = false;
        double cpuMilliseconds = sampler.TotalProcessFrameMilliseconds;
        if (activeSamplingMode == SamplingMode.Route && activeRouteSearch != null)
        {
            CompleteRouteSamplingStep(cpuMilliseconds);
            return;
        }

        sampleRenderer.Render(
            sampler.Points,
            PeakRoutePlannerConfig.RenderDebugAirCells.Value ? sampler.DebugAirCellCenters : [],
            PeakRoutePlannerConfig.RenderDebugAirBoundaryProbes.Value ? sampler.DebugAirBoundaryProbes : []);
        hasRenderedSamples = sampler.Points.Count > 0
            || (PeakRoutePlannerConfig.RenderDebugAirCells.Value && sampler.DebugAirCellCenters.Count > 0)
            || (PeakRoutePlannerConfig.RenderDebugAirBoundaryProbes.Value && sampler.DebugAirBoundaryProbes.Count > 0);

        LogSamplingCompletion(cpuMilliseconds);
        Plugin.Log.LogInfo(
            $"{activeSamplingLabel} complete: points={sampler.Points.Count}, queries={sampler.ProcessedRayCount}, standable={CountSampleKind(SurfaceKind.Standable)}, climbable={CountSampleKind(SurfaceKind.Climbable)}.");
    }

    private void CompleteRouteSamplingStep(double cpuMilliseconds)
    {
        RouteSearchRun routeSearch = activeRouteSearch!;
        RouteSearchDecision decision = routeSearch.AdvanceAfterSampling(sampler.Points, sampler.StartIndex, sampler);
        IReadOnlyList<Vector3> routePreview = PeakRoutePlannerConfig.RenderRoutePreview.Value
            ? routeSearch.PreviewPath
            : [];
        sampleRenderer.Render(
            sampler.Points,
            PeakRoutePlannerConfig.RenderDebugAirCells.Value ? sampler.DebugAirCellCenters : [],
            PeakRoutePlannerConfig.RenderDebugAirBoundaryProbes.Value ? sampler.DebugAirBoundaryProbes : [],
            routePreview);
        hasRenderedSamples = sampler.Points.Count > 0
            || routePreview.Count > 0
            || (PeakRoutePlannerConfig.RenderDebugAirCells.Value && sampler.DebugAirCellCenters.Count > 0)
            || (PeakRoutePlannerConfig.RenderDebugAirBoundaryProbes.Value && sampler.DebugAirBoundaryProbes.Count > 0);

        LogSamplingCompletion(cpuMilliseconds);
        Plugin.Log.LogInfo(
            $"Route planner decision: status={(decision.IsComplete ? "complete" : decision.IsFailed ? "failed" : "continue")}, step={decision.StepIndex}, regions={decision.RegionCount}, blockedPoints={decision.BlockedPointCount}, sampledPoints={sampler.Points.Count}, standable={CountSampleKind(SurfaceKind.Standable)}, climbable={CountSampleKind(SurfaceKind.Climbable)}, reason={decision.Reason}.");

        if (decision.ShouldSampleNext)
        {
            StartRouteSamplingStep(decision.NextSeedPosition, preserveSampleCache: true);
            return;
        }

        activeRouteSearch = null;
        activeSamplingMode = SamplingMode.None;
    }

    private void LogSamplingCompletion(double cpuMilliseconds)
    {
        Plugin.LogTiming(
            $"{activeSamplingLabel} timing: cpuMs={cpuMilliseconds:0.00}, wallMs={samplingElapsedMilliseconds:0.00}, points={sampler.Points.Count}, queries={sampler.ProcessedRayCount}, broadphaseWindows={sampler.BroadphaseWindowCount}, globalFallbacks={sampler.GlobalRaycastFallbackCount}, gapProbes={sampler.GapProbeCount}, gapPoints={sampler.GapLandingPointCount}, dropProbes={sampler.DropDiscoveryProbeCount}, dropPoints={sampler.DropDiscoveryPointCount}, continuityRejected={sampler.ContinuityRejectedCount}, gapRejected={sampler.GapLandingRejectedCount}, exteriorRejected={sampler.ExteriorVisibilityRejectedCount}, airPocketRejected={sampler.SurfaceAirPocketRejectedCount}, airPathRejected={sampler.SurfaceAirPathRejectedCount}, meshPocketRejected={sampler.SurfaceMeshPocketRejectedCount}, meshSegmentRejected={sampler.SurfaceMeshSegmentRejectedCount}, probeStandRejected={sampler.ProbeStandRejectedCount}, probeMoveRejected={sampler.ProbeMoveRejectedCount}, clearanceChecks={sampler.StandableClearanceCheckCount}, clearanceCacheHits={sampler.StandableClearanceCacheHitCount}.");
        Plugin.LogTiming(GetSamplingProfileLog(activeSamplingLabel + " profile", sampler));
    }

    private void CleanupSampling(string reason)
    {
        samplingActive = false;
        hasRenderedSamples = false;
        samplingElapsedMilliseconds = 0d;
        activeSamplingLabel = string.Empty;
        activeSamplingMode = SamplingMode.None;
        activeRouteSearch = null;
        sampleRenderer.Clear();
        windowRenderer.Clear();
        samplingStopwatch.Reset();
        Plugin.LogTiming($"PeakRoutePlanner sampling cleanup: reason={reason}.");
    }

    private void RenderSamplingWindowPreview(bool force)
    {
        if (windowRenderer.RenderSamplingWindowPreview(
                sampler.ActiveSampleWindowCenter,
                sampler.ActiveSampleWindowSize,
                sampler.ActiveSampleWindowRotation)
            || force)
        {
            Plugin.Log.LogInfo(
                $"Rendered sampling window preview: center=({sampler.ActiveSampleWindowCenter.x:0.0},{sampler.ActiveSampleWindowCenter.y:0.0},{sampler.ActiveSampleWindowCenter.z:0.0}), size=({sampler.ActiveSampleWindowSize.x:0.0},{sampler.ActiveSampleWindowSize.y:0.0},{sampler.ActiveSampleWindowSize.z:0.0}).");
        }
    }

    private void RestartSamplingTimer()
    {
        samplingStopwatch.Restart();
    }

    private void AccumulateSamplingTime()
    {
        samplingStopwatch.Stop();
        samplingElapsedMilliseconds += samplingStopwatch.Elapsed.TotalMilliseconds;
    }

    private int CountSampleKind(SurfaceKind kind)
    {
        IReadOnlyList<SurfacePoint> points = sampler.Points;
        int count = 0;
        for (int index = 0; index < points.Count; index++)
        {
            if (points[index].Kind == kind)
            {
                count++;
            }
        }

        return count;
    }

    private static string GetSamplingProfileLog(string label, SurfaceSampler profileSampler)
    {
        double avgFrameMs = profileSampler.ProcessFrameCount > 0
            ? profileSampler.TotalProcessFrameMilliseconds / profileSampler.ProcessFrameCount
            : 0d;
        return
            $"{label}: frames={profileSampler.ProcessFrameCount}, avgFrameMs={avgFrameMs:0.00}, maxFrameMs={profileSampler.MaxProcessFrameMilliseconds:0.00}, candidates={profileSampler.CandidateHitCount}, queuedProbes={profileSampler.QueuedProbeCount}, dropProbes={profileSampler.DropDiscoveryProbeCount}, dropPoints={profileSampler.DropDiscoveryPointCount}, visibilityChecks={profileSampler.VisibilityCheckCount}, reachabilityChecks={profileSampler.ReachabilityCheckCount}, gapReachChecks={profileSampler.GapReachabilityCheckCount}, supportChecks={profileSampler.SupportCheckCount}, moveProbeChecks={profileSampler.MoveProbeCheckCount}, moveProbeSkipped={profileSampler.MoveProbeSkippedCount}, connectionCasts={profileSampler.ConnectionCastCheckCount}, clearanceProbeChecks={profileSampler.ClearanceProbeCheckCount}, localColliderRaycasts={profileSampler.LocalColliderRaycastQueryCount}, broadphaseColliders={profileSampler.BroadphaseColliderCount}, broadphaseMaxColliders={profileSampler.BroadphaseMaxColliderCount}, broadphaseOverflows={profileSampler.BroadphaseOverflowCount}, broadphaseGlobalByCount={profileSampler.BroadphaseGlobalByColliderCount}, airWindows={profileSampler.SurfaceAirFieldWindowCount}, airCacheHits={profileSampler.SurfaceAirFieldCacheHitCount}, airReachableCells={profileSampler.SurfaceAirReachableCellCount}, airBoundaryCells={profileSampler.SurfaceAirBoundaryCellCount}, airBoundaryProbeSources={profileSampler.SurfaceAirBoundaryProbeSourceCount}, airBoundaryQueued={profileSampler.SurfaceAirBoundaryProbeQueuedCount}, airBoundaryQueuedDirs=({profileSampler.SurfaceAirBoundaryProbeQueuedDownCount}/{profileSampler.SurfaceAirBoundaryProbeQueuedUpCount}/{profileSampler.SurfaceAirBoundaryProbeQueuedSideCount}), airBoundaryWindowSkipped={profileSampler.SurfaceAirBoundaryProbeSkippedWindowCount}, airBoundaryWindowSkippedDirs=({profileSampler.SurfaceAirBoundaryProbeWindowSkippedDownCount}/{profileSampler.SurfaceAirBoundaryProbeWindowSkippedUpCount}/{profileSampler.SurfaceAirBoundaryProbeWindowSkippedSideCount}), airBoundaryDuplicateSkipped={profileSampler.SurfaceAirBoundaryProbeSkippedDuplicateCount}, airBoundaryRawHits={profileSampler.SurfaceAirBoundaryRawHitCount}, airBoundaryZeroHits={profileSampler.SurfaceAirBoundaryZeroHitCount}, airBoundaryPoints={profileSampler.SurfaceAirBoundaryPointCount}, airBoundaryPointDirs=({profileSampler.SurfaceAirBoundaryPointDownCount}/{profileSampler.SurfaceAirBoundaryPointUpCount}/{profileSampler.SurfaceAirBoundaryPointSideCount}), airBoundaryStandable={profileSampler.SurfaceAirBoundaryStandablePointCount}, airBoundaryStandableDirs=({profileSampler.SurfaceAirBoundaryStandableDownCount}/{profileSampler.SurfaceAirBoundaryStandableUpCount}/{profileSampler.SurfaceAirBoundaryStandableSideCount}), airSlices={profileSampler.SurfaceAirSliceAdvanceCount}, airMaxReachableCells={profileSampler.SurfaceAirMaxReachableCellCount}, airCheckedCells={profileSampler.SurfaceAirCheckedCellCount}, airBlockedCells={profileSampler.SurfaceAirBlockedCellCount}, airBlockedTransitions={profileSampler.SurfaceAirBlockedTransitionCount}, airClearCacheHits={profileSampler.SurfaceAirClearCellCacheHitCount}, airTransitionCacheHits={profileSampler.SurfaceAirClearTransitionCacheHitCount}, airSharedClearCells={profileSampler.SurfaceAirSharedClearCellCacheCount}, airSharedTransitions={profileSampler.SurfaceAirSharedClearTransitionCacheCount}, airOverflows={profileSampler.SurfaceAirOverflowCount}, airBuildFailures={profileSampler.SurfaceAirBuildFailedCount}, airPocketChecks={profileSampler.SurfaceAirPocketCheckCount}, airPocketRejected={profileSampler.SurfaceAirPocketRejectedCount}, airPathChecks={profileSampler.SurfaceAirPathCheckCount}, airPathRejected={profileSampler.SurfaceAirPathRejectedCount}, meshWindows={profileSampler.SurfaceMeshFieldWindowCount}, meshTriangles={profileSampler.SurfaceMeshTriangleCount}, meshMaxTriangles={profileSampler.SurfaceMeshMaxTriangleCount}, meshCells={profileSampler.SurfaceMeshCellCount}, meshMaxCells={profileSampler.SurfaceMeshMaxCellCount}, meshColliders={profileSampler.SurfaceMeshColliderCount}, meshSkippedColliders={profileSampler.SurfaceMeshSkippedColliderCount}, meshSkippedTriangles={profileSampler.SurfaceMeshSkippedTriangleCount}, meshPocketChecks={profileSampler.SurfaceMeshPocketCheckCount}, meshPocketRejected={profileSampler.SurfaceMeshPocketRejectedCount}, meshSegmentChecks={profileSampler.SurfaceMeshSegmentCheckCount}, meshSegmentRejected={profileSampler.SurfaceMeshSegmentRejectedCount}, queueMs={profileSampler.QueueMilliseconds:0.00}, raycastMs={profileSampler.RaycastMilliseconds:0.00}, filterMs={profileSampler.FilterMilliseconds:0.00}, broadphaseMs={profileSampler.BroadphaseMilliseconds:0.00}, airBuildMs={profileSampler.SurfaceAirBuildMilliseconds:0.00}, airPocketMs={profileSampler.SurfaceAirPocketMilliseconds:0.00}, airPathMs={profileSampler.SurfaceAirPathMilliseconds:0.00}, meshSnapshotMs={profileSampler.SurfaceMeshSnapshotMilliseconds:0.00}, meshBuildMs={profileSampler.SurfaceMeshBuildMilliseconds:0.00}, meshPocketMs={profileSampler.SurfaceMeshPocketMilliseconds:0.00}, meshSegmentMs={profileSampler.SurfaceMeshSegmentMilliseconds:0.00}, exteriorMs={profileSampler.ExteriorMilliseconds:0.00}, clearanceMs={profileSampler.ClearanceMilliseconds:0.00}, headroomMs={profileSampler.HeadroomMilliseconds:0.00}, standProbeMs={profileSampler.StandProbeMilliseconds:0.00}, reachabilityMs={profileSampler.ReachabilityMilliseconds:0.00}, gapReachMs={profileSampler.GapReachabilityMilliseconds:0.00}, supportMs={profileSampler.SupportMilliseconds:0.00}, moveProbeMs={profileSampler.MoveProbeMilliseconds:0.00}, connectionCastMs={profileSampler.ConnectionCastMilliseconds:0.00}, queueProbeMs={profileSampler.QueueProbeMilliseconds:0.00}.";
    }

    private enum SamplingMode
    {
        None,
        Debug,
        Route,
    }

    internal static bool TryGetLocalPlayerPosition(out Vector3 position, out Character localCharacter)
    {
        localCharacter = Character.localCharacter;
        if (localCharacter == null)
        {
            position = default;
            return false;
        }

        position = GetCapturedPlayerPosition(localCharacter);
        return true;
    }

    internal static bool GetCampfireTargetPosition(out Vector3 target)
    {
        Campfire[] campfires = Object.FindObjectsByType<Campfire>(FindObjectsSortMode.None);
        if (campfires.Length > 0)
        {
            Campfire highest = campfires.OrderByDescending(campfire => campfire.Center().y).First();
            target = highest.Center();
            return true;
        }

        string[] keywords = PlannerDefaults.TargetNameKeywords
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

    private static Vector3 GetCapturedPlayerPosition(Character character)
    {
        if (character.data != null)
        {
            return character.Center;
        }

        return character.transform.position;
    }
}
