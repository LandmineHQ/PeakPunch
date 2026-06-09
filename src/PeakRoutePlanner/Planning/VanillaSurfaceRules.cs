using UnityEngine;

namespace PeakRoutePlanner.Planning;

internal static class VanillaSurfaceRules
{
    internal static bool AllowsSurface(Collider? collider)
    {
        if (collider == null)
        {
            return false;
        }

        CollisionModifier modifier = collider.GetComponent<CollisionModifier>();
        return modifier == null || modifier.standable;
    }

    internal static bool AllowsStandableBody(Rigidbody? rigidbody)
    {
        return rigidbody == null || rigidbody.mass > 500f || rigidbody.isKinematic;
    }

    internal static SurfaceKind ClassifySurface(Vector3 normal, PlannerConfig config)
    {
        float angle = Vector3.Angle(Vector3.up, normal);
        if (angle < config.StandableNormalAngle)
        {
            return SurfaceKind.Standable;
        }

        return angle <= config.MaxClimbableNormalAngle
            ? SurfaceKind.Climbable
            : SurfaceKind.Blocked;
    }
}
