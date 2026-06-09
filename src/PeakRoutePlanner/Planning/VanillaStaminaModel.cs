using UnityEngine;

namespace PeakRoutePlanner.Planning;

// Snapshot-only stamina model.
// Populate it once when the player triggers route/sampling start, then reuse the
// cached values for every later validation step in that run.
internal readonly struct VanillaStaminaModel
{
    private readonly PlannerConfig config;

    internal VanillaStaminaModel(PlannerConfig config)
    {
        this.config = config;
    }

    internal float CurrentRegularStamina => Mathf.Max(0f, config.CurrentRegularStamina);

    internal float GetJumpCost(bool sprintJump)
    {
        float rawCost = sprintJump ? config.SprintJumpStaminaCost : config.JumpStaminaCost;
        return ApplyAscentsMultiplier(rawCost);
    }

    internal bool CanAffordJump(bool sprintJump)
    {
        return CanAfford(GetJumpCost(sprintJump));
    }

    internal float GetClimbCost(float distance)
    {
        float climbSpeed = Mathf.Max(0.001f, config.ClimbSpeed);
        float seconds = Mathf.Max(0f, distance) / climbSpeed;
        return ApplyAscentsMultiplier(config.ClimbStaminaUsagePerSecond * seconds);
    }

    internal bool CanAffordClimb(float distance)
    {
        return CanAfford(GetClimbCost(distance));
    }

    internal bool CanAffordClimbJump()
    {
        return CanAfford(ApplyAscentsMultiplier(config.ClimbJumpStaminaCost));
    }

    internal float GetSprintCost(float seconds)
    {
        return ApplyAscentsMultiplier(config.SprintStaminaUsagePerSecond * Mathf.Max(0f, seconds));
    }

    internal bool CanAffordSprint(float seconds)
    {
        return CanAfford(GetSprintCost(seconds));
    }

    private float ApplyAscentsMultiplier(float rawCost)
    {
        return Mathf.Max(0f, rawCost) * Mathf.Max(0f, config.AscentStaminaMultiplier);
    }

    private bool CanAfford(float staminaCost)
    {
        return CurrentRegularStamina + 0.0001f >= Mathf.Max(0f, staminaCost);
    }
}
