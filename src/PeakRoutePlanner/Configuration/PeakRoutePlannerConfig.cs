using System;
using System.IO;
using System.Threading;
using BepInEx.Configuration;
using PeakRoutePlanner.Planning;
using UnityEngine;

namespace PeakRoutePlanner.Configuration;

internal static class PeakRoutePlannerConfig
{
    private const int HotReloadDebounceMilliseconds = 250;
    private const float ClimbJumpStaminaCost = 0.2f;
    private const float ClimbJumpSlideImpulse = 8f;
    private const float ClimbSlideDecay = 0.97f;
    private const float ClimbSlideDecelerationPerSecond = 15f;
    private const float FixedDeltaTimeEstimate = 0.02f;
    private const float StandJumpSafetyMultiplier = 0.72f;
    private const float AirTransferSafetyMultiplier = 0.55f;
    private const float StandJumpUpHeightSafetyMultiplier = 0.65f;

    private static readonly object HotReloadLock = new();

    private static FileSystemWatcher? configWatcher;
    private static ConfigFile? configFile;
    private static Timer? reloadTimer;

    internal static ConfigEntry<bool> EnableRoutePlanner { get; private set; } = null!;
    internal static ConfigEntry<KeyboardShortcut> PlanRouteShortcut { get; private set; } = null!;
    internal static ConfigEntry<KeyboardShortcut> ClearRouteShortcut { get; private set; } = null!;
    internal static ConfigEntry<bool> RenderRoutePath { get; private set; } = null!;
    internal static ConfigEntry<bool> RenderIntermediateRoutePath { get; private set; } = null!;
    internal static ConfigEntry<bool> RenderSamplingWindowPreview { get; private set; } = null!;
    internal static ConfigEntry<float> IntermediateRenderThrottleSeconds { get; private set; } = null!;
    internal static ConfigEntry<bool> LogRoutePlannerDetails { get; private set; } = null!;

    internal static ConfigEntry<string> TargetNameKeywords { get; private set; } = null!;
    internal static ConfigEntry<bool> UseManualTargetPosition { get; private set; } = null!;
    internal static ConfigEntry<float> ManualTargetX { get; private set; } = null!;
    internal static ConfigEntry<float> ManualTargetY { get; private set; } = null!;
    internal static ConfigEntry<float> ManualTargetZ { get; private set; } = null!;

    internal static ConfigEntry<float> CorridorInitialRadius { get; private set; } = null!;
    internal static ConfigEntry<float> CorridorRadiusStep { get; private set; } = null!;
    internal static ConfigEntry<float> MaxCorridorRadius { get; private set; } = null!;
    internal static ConfigEntry<float> HorizontalSampleSpacing { get; private set; } = null!;
    internal static ConfigEntry<float> SurfaceSamplingWindowRadius { get; private set; } = null!;
    internal static ConfigEntry<int> MaxSamplingWindowsPerSide { get; private set; } = null!;
    internal static ConfigEntry<int> MaxGuideCurveSamples { get; private set; } = null!;
    internal static ConfigEntry<float> AdaptiveGuideMinimumStep { get; private set; } = null!;
    internal static ConfigEntry<float> MinimumPartialSegmentDistance { get; private set; } = null!;
    internal static ConfigEntry<float> MinimumFrontierAdvanceDistance { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableBidirectionalFrontierSampling { get; private set; } = null!;
    internal static ConfigEntry<bool> EnableCorridorExpansion { get; private set; } = null!;
    internal static ConfigEntry<int> MaxPhysicsQueriesPerFrame { get; private set; } = null!;
    internal static ConfigEntry<int> MaxEdgeValidationsPerFrame { get; private set; } = null!;
    internal static ConfigEntry<int> MaxEdgeCandidateChecksPerFrame { get; private set; } = null!;
    internal static ConfigEntry<int> MaxSurfacePointsPerAttempt { get; private set; } = null!;
    internal static ConfigEntry<int> MaxEdgeCandidatesPerAttempt { get; private set; } = null!;
    internal static ConfigEntry<float> MaxMainThreadMillisecondsPerFrame { get; private set; } = null!;
    internal static ConfigEntry<float> MaxSampleVerticalLayerGap { get; private set; } = null!;
    internal static ConfigEntry<float> MaxClimbDistancePerStamina { get; private set; } = null!;
    internal static ConfigEntry<float> MaxStandJumpDistance { get; private set; } = null!;
    internal static ConfigEntry<float> MaxAirTransferDistance { get; private set; } = null!;
    internal static ConfigEntry<float> MaxWalkStepUpHeight { get; private set; } = null!;
    internal static ConfigEntry<float> MaxWalkDropHeight { get; private set; } = null!;
    internal static ConfigEntry<float> VerticalScanPadding { get; private set; } = null!;
    internal static ConfigEntry<float> SurfaceNeighborDistance { get; private set; } = null!;
    internal static ConfigEntry<float> StandableNormalAngle { get; private set; } = null!;
    internal static ConfigEntry<float> MaxClimbableNormalAngle { get; private set; } = null!;
    internal static ConfigEntry<int> StaminaBuckets { get; private set; } = null!;

    internal static ConfigEntry<int> SearchMaxExpandedStates { get; private set; } = null!;
    internal static ConfigEntry<float> SearchDistanceFieldHeuristicWeight { get; private set; } = null!;
    internal static ConfigEntry<float> SearchGuideDistanceWeight { get; private set; } = null!;
    internal static ConfigEntry<float> SearchBacktrackPenaltyMultiplier { get; private set; } = null!;
    internal static ConfigEntry<float> SearchLongStepPenaltyMultiplier { get; private set; } = null!;
    internal static ConfigEntry<float> SurfaceClimbMovePenalty { get; private set; } = null!;
    internal static ConfigEntry<float> StandJumpMovePenalty { get; private set; } = null!;
    internal static ConfigEntry<float> AirTransferMovePenalty { get; private set; } = null!;

    internal static void Bind(ConfigFile config)
    {
        EnableRoutePlanner = config.Bind("Route Planner", "EnableRoutePlanner", true, "Enable route planning and route rendering.");
        PlanRouteShortcut = config.Bind("Route Planner", "PlanRouteShortcut", new KeyboardShortcut(KeyCode.Comma, KeyCode.LeftAlt), "Shortcut used to plan a route from the local player to the highest campfire.");
        ClearRouteShortcut = config.Bind("Route Planner", "ClearRouteShortcut", new KeyboardShortcut(KeyCode.Period, KeyCode.LeftAlt), "Shortcut used to clear the current route and cancel any in-progress route planning.");
        RenderRoutePath = config.Bind("Route Planner", "RenderRoutePath", true, "Render the planned route as an in-world line.");
        RenderIntermediateRoutePath = config.Bind("Route Planner", "RenderIntermediateRoutePath", true, "Render throttled intermediate route previews while sampling and validating edges.");
        RenderSamplingWindowPreview = config.Bind("Route Planner", "RenderSamplingWindowPreview", true, "Render a separate translucent cube around the surface sampling window currently being scanned.");
        IntermediateRenderThrottleSeconds = config.Bind("Route Planner", "IntermediateRenderThrottleSeconds", 0.15f, "Minimum seconds between intermediate route preview LineRenderer updates. Runtime preview rendering caps old larger config values so diagnostics stay responsive.");
        LogRoutePlannerDetails = config.Bind("Route Planner", "LogRoutePlannerDetails", true, "Log route planner sampling, validation, and path summary details.");

        TargetNameKeywords = config.Bind("Target", "TargetNameKeywords", "Campfire,Peak", "Fallback comma-separated object-name keywords used if no Campfire component can be found.");
        UseManualTargetPosition = config.Bind("Target", "UseManualTargetPosition", false, "Use ManualTargetX/Y/Z instead of auto-detecting the highest campfire.");
        ManualTargetX = config.Bind("Target", "ManualTargetX", 0f, "Manual target world X.");
        ManualTargetY = config.Bind("Target", "ManualTargetY", 0f, "Manual target world Y.");
        ManualTargetZ = config.Bind("Target", "ManualTargetZ", 0f, "Manual target world Z.");

        CorridorInitialRadius = config.Bind("Sampling", "CorridorInitialRadius", 4f, "Initial radius around the start-to-target corridor.");
        CorridorRadiusStep = config.Bind("Sampling", "CorridorRadiusStep", 4f, "Radius added after each failed route attempt.");
        MaxCorridorRadius = config.Bind("Sampling", "MaxCorridorRadius", 42f, "Maximum route sampling corridor radius.");
        HorizontalSampleSpacing = config.Bind("Sampling", "HorizontalSampleSpacing", 0.5f, "Horizontal spacing between vertical surface-sampling rays.");
        SurfaceSamplingWindowRadius = config.Bind("Sampling", "SurfaceSamplingWindowRadius", 10f, "Foothold-style XZ radius around each frontier seed. Samples are projected from the seed surface normal first so caves and climbable walls stay on the seed's surface layer.");
        MaxSamplingWindowsPerSide = config.Bind("Sampling", "MaxSamplingWindowsPerSide", 1, "Maximum Foothold-style sampling windows expanded per side in one local planning attempt. Keep this at 1 for scan-once-then-advance behavior.");
        MaxGuideCurveSamples = config.Bind("Sampling", "MaxGuideCurveSamples", 192, "Maximum surface-guide center samples used for corridor sampling.");
        AdaptiveGuideMinimumStep = config.Bind("Sampling", "AdaptiveGuideMinimumStep", 0.5f, "Minimum target step in meters for adaptive midpoint guide projection and staged preview search.");
        MinimumPartialSegmentDistance = config.Bind("Sampling", "MinimumPartialSegmentDistance", 1.0f, "Minimum validated partial-route distance to commit before moving the sampling window. Keep this small so dead-end recovery moves the window instead of expanding the same corridor.");
        MinimumFrontierAdvanceDistance = config.Bind("Sampling", "MinimumFrontierAdvanceDistance", 1.0f, "Minimum net guide-progress distance required before a normal partial route can advance the frontier. Smaller forward steps are marked blocked so dead ends do not consume infinite tiny commits.");
        EnableBidirectionalFrontierSampling = config.Bind("Sampling", "EnableBidirectionalFrontierSampling", true, "Sample surface frontiers from both the captured player position and the target campfire so target-rooted distance fields can form before both sides connect.");
        EnableCorridorExpansion = config.Bind("Sampling", "EnableCorridorExpansion", false, "Allow failed local route attempts to widen the current corridor. Disabled by default because route recovery should move to another sampled frontier instead of repeatedly enlarging the same window.");
        MaxPhysicsQueriesPerFrame = config.Bind("Sampling", "MaxPhysicsQueriesPerFrame", 80, "Maximum surface raycasts per frame before the frame-time budget also stops sampling.");
        MaxEdgeValidationsPerFrame = config.Bind("Sampling", "MaxEdgeValidationsPerFrame", 120, "Maximum edge collision checks per frame before the frame-time budget also stops validation.");
        MaxEdgeCandidateChecksPerFrame = config.Bind("Sampling", "MaxEdgeCandidateChecksPerFrame", 1500, "Maximum potential edge pairs inspected per frame while building route edge candidates.");
        MaxSurfacePointsPerAttempt = config.Bind("Sampling", "MaxSurfacePointsPerAttempt", 24000, "Maximum cached surface points retained for one route-planning attempt.");
        MaxEdgeCandidatesPerAttempt = config.Bind("Sampling", "MaxEdgeCandidatesPerAttempt", 240000, "Maximum generated edge candidates for one route-planning attempt.");
        MaxMainThreadMillisecondsPerFrame = config.Bind("Sampling", "MaxMainThreadMillisecondsPerFrame", 2.5f, "Soft main-thread time budget in milliseconds for each planner processing frame.");
        MaxSampleVerticalLayerGap = config.Bind("Sampling", "MaxSampleVerticalLayerGap", 2f, "Maximum vertical distance used to separate stacked raycast surface layers; route sampling prefers the current/seed height layer instead of the lowest hit.");
        VerticalScanPadding = config.Bind("Sampling", "VerticalScanPadding", 20f, "Extra vertical distance above and below the start-target range for surface raycasts.");

        SurfaceNeighborDistance = config.Bind("Movement", "SurfaceNeighborDistance", 1.0f, "Maximum distance for adjacent walk/climb surface movement edges. Keep this near the sampling spacing so routes advance in player-sized steps.");
        MaxClimbDistancePerStamina = config.Bind("Movement", "MaxClimbDistancePerStamina", 18f, "Fallback full-stamina climb distance. Runtime planning overwrites this from CharacterClimbing climb speed and stamina usage when available.");
        MaxStandJumpDistance = config.Bind("Movement", "MaxStandJumpDistance", 4.5f, "Upper bound for a stand-jump edge. Runtime planning estimates jump reach from CharacterMovement fields, then clamps it to this value.");
        MaxAirTransferDistance = config.Bind("Movement", "MaxAirTransferDistance", 2.5f, "Upper bound for an air-transfer edge. Runtime planning estimates climb-jump slide reach, then clamps it to this value.");
        MaxWalkStepUpHeight = config.Bind("Movement", "MaxWalkStepUpHeight", 0.75f, "Maximum upward height for a directed stand-walk edge.");
        MaxWalkDropHeight = config.Bind("Movement", "MaxWalkDropHeight", 2f, "Maximum downward height for a directed stand-walk edge.");
        StandableNormalAngle = config.Bind("Movement", "StandableNormalAngle", 50f, "Surface normal angle from world up at or below which a point is treated as standable.");
        MaxClimbableNormalAngle = config.Bind("Movement", "MaxClimbableNormalAngle", 135f, "Surface normal angle from world up at or below which a non-standable point is treated as climbable.");
        StaminaBuckets = config.Bind("Movement", "StaminaBuckets", 40, "Number of discrete stamina buckets used by the background planner.");

        SearchMaxExpandedStates = config.Bind("Path Search", "SearchMaxExpandedStates", 120000, "Maximum stamina-aware A* states expanded by the background route search.");
        SearchDistanceFieldHeuristicWeight = config.Bind("Path Search", "SearchDistanceFieldHeuristicWeight", 1.0f, "Weight of the target-rooted surface distance field used as the A* heuristic.");
        SearchGuideDistanceWeight = config.Bind("Path Search", "SearchGuideDistanceWeight", 0.65f, "Cost weight for lateral distance away from the projected surface guide.");
        SearchBacktrackPenaltyMultiplier = config.Bind("Path Search", "SearchBacktrackPenaltyMultiplier", 6f, "Cost multiplier for edges that move backward along the surface guide.");
        SearchLongStepPenaltyMultiplier = config.Bind("Path Search", "SearchLongStepPenaltyMultiplier", 3.5f, "Cost multiplier for edges that cover much more world distance than guide progress.");
        SurfaceClimbMovePenalty = config.Bind("Path Search", "SurfaceClimbMovePenalty", 0.35f, "Additional search cost for one surface-climb edge.");
        StandJumpMovePenalty = config.Bind("Path Search", "StandJumpMovePenalty", 8f, "Additional search cost for one stand-jump edge so jumps are used only as gap-solving links.");
        AirTransferMovePenalty = config.Bind("Path Search", "AirTransferMovePenalty", 10f, "Additional search cost for one air-transfer edge so wall switches remain rare and local.");
    }

    internal static PlannerConfig ToPlannerConfig(Character? localCharacter)
    {
        PlannerConfig plannerConfig = new()
        {
            CorridorInitialRadius = Mathf.Clamp(CorridorInitialRadius.Value, 1f, 6f),
            CorridorRadiusStep = Mathf.Clamp(CorridorRadiusStep.Value, 1f, 6f),
            MaxCorridorRadius = Mathf.Max(CorridorInitialRadius.Value, MaxCorridorRadius.Value),
            HorizontalSampleSpacing = Mathf.Clamp(HorizontalSampleSpacing.Value, 0.25f, 0.5f),
            SurfaceSamplingWindowRadius = Mathf.Clamp(SurfaceSamplingWindowRadius.Value, 5f, 20f),
            MaxSamplingWindowsPerSide = Mathf.Clamp(MaxSamplingWindowsPerSide.Value, 1, 4),
            MaxGuideCurveSamples = Mathf.Clamp(Mathf.Max(MaxGuideCurveSamples.Value, 192), 8, 512),
            AdaptiveGuideMinimumStep = Mathf.Clamp(AdaptiveGuideMinimumStep.Value, 0.25f, 5f),
            MinimumPartialSegmentDistance = Mathf.Clamp(MinimumPartialSegmentDistance.Value, 0.5f, 6f),
            MinimumFrontierAdvanceDistance = Mathf.Clamp(MinimumFrontierAdvanceDistance.Value, 0.25f, 6f),
            EnableBidirectionalFrontierSampling = EnableBidirectionalFrontierSampling.Value,
            EnableCorridorExpansion = EnableCorridorExpansion.Value,
            MaxPhysicsQueriesPerFrame = Mathf.Max(1, MaxPhysicsQueriesPerFrame.Value),
            MaxEdgeValidationsPerFrame = Mathf.Max(1, MaxEdgeValidationsPerFrame.Value),
            MaxEdgeCandidateChecksPerFrame = Mathf.Max(10, MaxEdgeCandidateChecksPerFrame.Value),
            MaxSurfacePointsPerAttempt = Mathf.Clamp(Mathf.Max(MaxSurfacePointsPerAttempt.Value, 24000), 100, 60000),
            MaxEdgeCandidatesPerAttempt = Mathf.Clamp(Mathf.Max(MaxEdgeCandidatesPerAttempt.Value, 240000), 100, 600000),
            MaxMainThreadMillisecondsPerFrame = Mathf.Clamp(MaxMainThreadMillisecondsPerFrame.Value, 0.25f, 12f),
            MaxSampleVerticalLayerGap = Mathf.Clamp(MaxSampleVerticalLayerGap.Value, 0.25f, 20f),
            MaxClimbDistancePerStamina = Mathf.Max(1f, MaxClimbDistancePerStamina.Value),
            MaxStandJumpDistance = Mathf.Max(0.5f, MaxStandJumpDistance.Value),
            MaxAirTransferDistance = Mathf.Max(0.25f, MaxAirTransferDistance.Value),
            MaxWalkStepUpHeight = Mathf.Clamp(MaxWalkStepUpHeight.Value, 0.05f, 5f),
            MaxWalkDropHeight = Mathf.Clamp(MaxWalkDropHeight.Value, 0.05f, 20f),
            VerticalScanPadding = Mathf.Max(1f, VerticalScanPadding.Value),
            SurfaceNeighborDistance = Mathf.Clamp(
                SurfaceNeighborDistance.Value,
                0.25f,
                Mathf.Max(0.75f, Mathf.Clamp(HorizontalSampleSpacing.Value, 0.25f, 0.5f) * 1.5f)),
            StandableNormalAngle = Mathf.Clamp(StandableNormalAngle.Value, 0f, 89f),
            MaxClimbableNormalAngle = Mathf.Clamp(MaxClimbableNormalAngle.Value, StandableNormalAngle.Value, 179f),
            StaminaBuckets = Mathf.Clamp(StaminaBuckets.Value, 5, 100),
            SearchMaxExpandedStates = Mathf.Clamp(SearchMaxExpandedStates.Value, 1000, 1000000),
            SearchDistanceFieldHeuristicWeight = Mathf.Clamp(SearchDistanceFieldHeuristicWeight.Value, 0f, 5f),
            SearchGuideDistanceWeight = Mathf.Clamp(SearchGuideDistanceWeight.Value, 0f, 10f),
            SearchBacktrackPenaltyMultiplier = Mathf.Clamp(SearchBacktrackPenaltyMultiplier.Value, 0f, 40f),
            SearchLongStepPenaltyMultiplier = Mathf.Clamp(SearchLongStepPenaltyMultiplier.Value, 0f, 40f),
            SurfaceClimbMovePenalty = Mathf.Clamp(SurfaceClimbMovePenalty.Value, 0f, 20f),
            StandJumpMovePenalty = Mathf.Clamp(StandJumpMovePenalty.Value, 0f, 40f),
            AirTransferMovePenalty = Mathf.Clamp(AirTransferMovePenalty.Value, 0f, 40f),
        };

        ApplyGameMovementModel(plannerConfig, localCharacter);
        return plannerConfig;
    }

    private static void ApplyGameMovementModel(PlannerConfig plannerConfig, Character? localCharacter)
    {
        float configuredStandJumpLimit = plannerConfig.MaxStandJumpDistance;
        float configuredAirTransferLimit = plannerConfig.MaxAirTransferDistance;
        CharacterMovement? movement = null;
        CharacterClimbing? climbing = null;
        float staminaMod = 1f;
        if (localCharacter != null)
        {
            movement = localCharacter.refs?.movement;
            climbing = localCharacter.refs?.climbing;
            staminaMod = Mathf.Max(0.01f, localCharacter.data?.staminaMod ?? 1f);
        }

        float ascentMultiplier = Mathf.Max(0.01f, Ascents.climbStaminaMultiplier);
        float movementForce = Mathf.Max(0.1f, movement?.movementForce ?? 10f);
        float sprintMultiplier = Mathf.Max(1f, movement?.sprintMultiplier ?? 1f);
        float airMovementTurnSpeed = Mathf.Max(0.1f, movement?.airMovementTurnSpeed ?? 2f);
        float jumpImpulse = Mathf.Max(0.1f, movement?.jumpImpulse ?? 6f);
        float maxGravity = Mathf.Abs(movement?.maxGravity ?? -20f);
        float jumpStaminaUsage = Mathf.Max(0f, movement?.jumpStaminaUsage ?? 0.15f) * ascentMultiplier;
        float sprintJumpStaminaUsage = Mathf.Max(jumpStaminaUsage, movement?.jumpStaminaUsageSprinting ?? jumpStaminaUsage) * ascentMultiplier;
        float sprintStaminaUsage = Mathf.Max(0f, movement?.sprintStaminaUsage ?? 0.025f) * ascentMultiplier;

        float climbSpeed = Mathf.Max(0.1f, (climbing?.climbSpeed ?? 1f) * (climbing?.climbSpeedMod ?? 1f));
        float climbUsage = Mathf.Max(0.001f, climbing?.maxStaminaUsage ?? 0.2f) * staminaMod * ascentMultiplier;
        float minClimbUsage = Mathf.Max(0f, climbing?.minStaminaUsage ?? 0.02f) * staminaMod * ascentMultiplier;
        float climbMinimumMultiplier = Mathf.Max(0f, climbing?.climbingStamMinimumMultiplier ?? 1f);

        float normalJumpDistance = EstimateStandJumpDistance(movementForce, 1f, airMovementTurnSpeed, jumpImpulse, maxGravity);
        float sprintJumpDistance = EstimateStandJumpDistance(movementForce, sprintMultiplier, airMovementTurnSpeed, jumpImpulse, maxGravity);
        float jumpUpHeight = EstimateJumpUpHeight(jumpImpulse, maxGravity);
        float airTransferDistance = EstimateClimbJumpDistance(climbSpeed);

        plannerConfig.NormalStandJumpDistance = Mathf.Min(configuredStandJumpLimit, Mathf.Max(0.5f, normalJumpDistance));
        plannerConfig.SprintStandJumpDistance = Mathf.Min(
            configuredStandJumpLimit,
            Mathf.Max(plannerConfig.NormalStandJumpDistance, sprintJumpDistance));
        plannerConfig.AirTransferJumpDistance = Mathf.Min(configuredAirTransferLimit, Mathf.Max(0.5f, airTransferDistance));
        plannerConfig.MaxStandJumpDistance = Mathf.Max(0.5f, plannerConfig.SprintStandJumpDistance);
        plannerConfig.MaxAirTransferDistance = Mathf.Max(0.25f, plannerConfig.AirTransferJumpDistance);
        plannerConfig.MaxStandJumpUpHeight = Mathf.Max(plannerConfig.MaxWalkStepUpHeight, jumpUpHeight);
        plannerConfig.MaxStandJumpDropHeight = Mathf.Max(plannerConfig.MaxWalkDropHeight, jumpUpHeight * 2f);
        plannerConfig.MaxAirTransferVerticalDelta = Mathf.Max(0.5f, plannerConfig.AirTransferJumpDistance * 0.75f);
        plannerConfig.JumpStaminaCost = jumpStaminaUsage;
        plannerConfig.SprintJumpStaminaCost = sprintJumpStaminaUsage;
        plannerConfig.ClimbJumpStaminaCost = ClimbJumpStaminaCost * ascentMultiplier;
        plannerConfig.SprintStaminaUsagePerSecond = sprintStaminaUsage;
        plannerConfig.ClimbSpeed = climbSpeed;
        plannerConfig.ClimbStaminaUsagePerSecond = climbUsage;
        plannerConfig.MinClimbStaminaUsagePerSecond = minClimbUsage;
        plannerConfig.ClimbStaminaMinimumMultiplier = climbMinimumMultiplier;
        plannerConfig.CharacterStaminaMultiplier = staminaMod;
        plannerConfig.AscentStaminaMultiplier = ascentMultiplier;
        plannerConfig.MaxClimbDistancePerStamina = Mathf.Max(1f, climbSpeed / Mathf.Max(0.001f, climbUsage));
    }

    private static float EstimateStandJumpDistance(
        float movementForce,
        float sprintMultiplier,
        float airMovementTurnSpeed,
        float jumpImpulse,
        float maxGravity)
    {
        float airTime = Mathf.Clamp((2f * jumpImpulse) / Mathf.Max(1f, maxGravity), 0.25f, 1.6f);
        float airControlAcceleration = movementForce * sprintMultiplier;
        float airControlFactor = Mathf.Clamp(airMovementTurnSpeed / 2f, 0.4f, 1.25f);
        float runCarrySpeed = Mathf.Sqrt(Mathf.Max(0.1f, airControlAcceleration)) * sprintMultiplier;
        float controlledAirDistance = 0.5f * airControlAcceleration * airTime * airTime * 0.08f * airControlFactor;
        return Mathf.Max(0.5f, (runCarrySpeed * airTime + controlledAirDistance) * StandJumpSafetyMultiplier);
    }

    private static float EstimateJumpUpHeight(float jumpImpulse, float maxGravity)
    {
        return Mathf.Max(
            0.35f,
            jumpImpulse * jumpImpulse / (2f * Mathf.Max(1f, maxGravity)) * StandJumpUpHeightSafetyMultiplier);
    }

    private static float EstimateClimbJumpDistance(float climbSpeed)
    {
        float slide = ClimbJumpSlideImpulse;
        float distance = 0f;
        for (int step = 0; step < 120 && slide > 0.001f; step++)
        {
            slide *= ClimbSlideDecay;
            slide = Mathf.MoveTowards(slide, 0f, FixedDeltaTimeEstimate * ClimbSlideDecelerationPerSecond);
            distance += slide * climbSpeed * FixedDeltaTimeEstimate;
        }

        return Mathf.Max(0.5f, distance * AirTransferSafetyMultiplier);
    }

    internal static Vector3 ManualTargetPosition => new(ManualTargetX.Value, ManualTargetY.Value, ManualTargetZ.Value);

    internal static void EnableHotReload(ConfigFile config)
    {
        DisableHotReload();

        configFile = config;
        string? configDirectory = Path.GetDirectoryName(config.ConfigFilePath);
        string configFileName = Path.GetFileName(config.ConfigFilePath);
        if (string.IsNullOrEmpty(configDirectory) || string.IsNullOrEmpty(configFileName) || !Directory.Exists(configDirectory))
        {
            Plugin.Log.LogWarning($"Config hot reload is disabled because the config directory is unavailable: {config.ConfigFilePath}");
            return;
        }

        reloadTimer = new Timer(ReloadConfigFromTimer);
        configWatcher = new FileSystemWatcher(configDirectory, configFileName)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.CreationTime
                | NotifyFilters.FileName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size,
        };

        configWatcher.Changed += OnConfigFileChanged;
        configWatcher.Created += OnConfigFileChanged;
        configWatcher.Renamed += OnConfigFileChanged;
        configWatcher.EnableRaisingEvents = true;
    }

    internal static void DisableHotReload()
    {
        if (configWatcher != null)
        {
            configWatcher.EnableRaisingEvents = false;
            configWatcher.Changed -= OnConfigFileChanged;
            configWatcher.Created -= OnConfigFileChanged;
            configWatcher.Renamed -= OnConfigFileChanged;
            configWatcher.Dispose();
            configWatcher = null;
        }

        lock (HotReloadLock)
        {
            reloadTimer?.Dispose();
            reloadTimer = null;
            configFile = null;
        }
    }

    private static void OnConfigFileChanged(object sender, FileSystemEventArgs args)
    {
        lock (HotReloadLock)
        {
            reloadTimer?.Change(HotReloadDebounceMilliseconds, Timeout.Infinite);
        }
    }

    private static void ReloadConfigFromTimer(object? state)
    {
        ConfigFile? currentConfig;
        lock (HotReloadLock)
        {
            currentConfig = configFile;
        }

        if (currentConfig == null)
        {
            return;
        }

        try
        {
            currentConfig.Reload();
            Plugin.Log.LogInfo("Reloaded PeakRoutePlanner config from disk.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"Failed to reload PeakRoutePlanner config: {ex.Message}");
        }
    }
}
