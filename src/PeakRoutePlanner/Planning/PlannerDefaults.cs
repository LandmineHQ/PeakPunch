using UnityEngine;

namespace PeakRoutePlanner.Planning;

internal static class PlannerDefaults
{
    private const float ClimbJumpSlideImpulse = 8f;
    private const float ClimbSlideDecay = 0.97f;
    private const float ClimbSlideDecelerationPerSecond = 15f;
    private const float FixedDeltaTimeEstimate = 0.02f;
    private const float StandJumpSafetyMultiplier = 0.72f;
    private const float AirTransferSafetyMultiplier = 0.55f;
    private const float StandJumpUpHeightSafetyMultiplier = 0.65f;

    internal const float DefaultSamplingCorridorRadius = 4f;
    internal const float DefaultSamplingGuideDistance = 12f;
    internal static readonly string TargetNameKeywords = "Campfire,Peak";

    internal static PlannerConfig ToPlannerConfig(Character? localCharacter)
    {
        PlannerConfig plannerConfig = new()
        {
            CorridorRadiusStep = 4f,
            HorizontalSampleSpacing = 0.5f,
            SurfaceSamplingWindowRadius = 15f,
            MaxSamplingWindowsPerSide = 1,
            AdaptiveGuideMinimumStep = 0.5f,
            MinimumPartialSegmentDistance = 1.0f,
            MinimumFrontierAdvanceDistance = 1.0f,
            MaxPhysicsQueriesPerFrame = 128,
            MaxSurfacePointsPerAttempt = 80000,
            MaxSurfacePointsPerWindow = 360,
            MaxMainThreadMillisecondsPerFrame = 2.5f,
            MaxSampleVerticalLayerGap = 2f,
            MaxStandJumpDistance = 4.5f,
            MaxAirTransferDistance = 2.5f,
            NormalStandJumpDistance = 3.2f,
            SprintStandJumpDistance = 4.5f,
            MaxWalkStepUpHeight = 0.75f,
            MaxWalkDropHeight = 2f,
            VerticalScanPadding = 20f,
            SurfaceNeighborDistance = 0.75f,
            StandableNormalAngle = 50f,
            MaxClimbableNormalAngle = 135f,
            CurrentRegularStamina = 1f,
            AscentStaminaMultiplier = 1f,
            SprintStaminaUsagePerSecond = 0.025f,
            JumpStaminaCost = 0.05f,
            SprintJumpStaminaCost = 0.15f,
            ClimbJumpStaminaCost = 0.2f,
            ClimbSpeed = 4f,
            ClimbStaminaUsagePerSecond = 0.2f,
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
        CharacterData? data = null;
        if (localCharacter != null)
        {
            movement = localCharacter.refs?.movement;
            climbing = localCharacter.refs?.climbing;
            data = localCharacter.data;
        }

        float movementForce = Mathf.Max(0.1f, movement?.movementForce ?? 10f);
        float sprintMultiplier = Mathf.Max(1f, movement?.sprintMultiplier ?? 1f);
        float airMovementTurnSpeed = Mathf.Max(0.1f, movement?.airMovementTurnSpeed ?? 2f);
        float jumpImpulse = Mathf.Max(0.1f, movement?.jumpImpulse ?? 6f);
        float maxGravity = Mathf.Abs(movement?.maxGravity ?? -20f);
        plannerConfig.StandableNormalAngle = Mathf.Clamp(
            movement?.maxAngle ?? plannerConfig.StandableNormalAngle,
            1f,
            89f);

        float climbSpeed = Mathf.Max(0.1f, (climbing?.climbSpeed ?? 4f) * (climbing?.climbSpeedMod ?? 1f));
        float normalJumpDistance = EstimateStandJumpDistance(
            movementForce,
            1f,
            airMovementTurnSpeed,
            jumpImpulse,
            maxGravity);
        float sprintJumpDistance = EstimateStandJumpDistance(
            movementForce,
            sprintMultiplier,
            airMovementTurnSpeed,
            jumpImpulse,
            maxGravity);
        float jumpUpHeight = EstimateJumpUpHeight(jumpImpulse, maxGravity);
        float airTransferDistance = EstimateClimbJumpDistance(climbSpeed);

        plannerConfig.NormalStandJumpDistance = Mathf.Min(configuredStandJumpLimit, Mathf.Max(0.5f, normalJumpDistance));
        plannerConfig.SprintStandJumpDistance = Mathf.Min(configuredStandJumpLimit, Mathf.Max(plannerConfig.NormalStandJumpDistance, sprintJumpDistance));
        plannerConfig.MaxStandJumpDistance = plannerConfig.SprintStandJumpDistance;
        plannerConfig.MaxAirTransferDistance = Mathf.Min(configuredAirTransferLimit, Mathf.Max(0.25f, airTransferDistance));
        plannerConfig.MaxStandJumpUpHeight = Mathf.Max(plannerConfig.MaxWalkStepUpHeight, jumpUpHeight);
        plannerConfig.MaxStandJumpDropHeight = Mathf.Max(plannerConfig.MaxWalkDropHeight, jumpUpHeight * 2f);
        plannerConfig.CurrentRegularStamina = Mathf.Max(0f, data?.currentStamina ?? plannerConfig.CurrentRegularStamina);
        plannerConfig.AscentStaminaMultiplier = Mathf.Max(0f, Ascents.climbStaminaMultiplier);
        plannerConfig.SprintStaminaUsagePerSecond = Mathf.Max(0f, movement?.sprintStaminaUsage ?? plannerConfig.SprintStaminaUsagePerSecond);
        plannerConfig.JumpStaminaCost = Mathf.Max(0f, movement?.jumpStaminaUsage ?? plannerConfig.JumpStaminaCost);
        plannerConfig.SprintJumpStaminaCost = Mathf.Max(0f, movement?.jumpStaminaUsageSprinting ?? plannerConfig.SprintJumpStaminaCost);
        plannerConfig.ClimbJumpStaminaCost = 0.2f;
        plannerConfig.ClimbSpeed = climbSpeed;
        plannerConfig.ClimbStaminaUsagePerSecond = Mathf.Max(0f, climbing?.maxStaminaUsage ?? plannerConfig.ClimbStaminaUsagePerSecond);
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
}
