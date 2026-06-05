using System.Collections.Generic;
using UnityEngine;

namespace PeakRoutePlanner.Planning;

internal static class RouteGuideBuilder
{
    private const float PointMergeSqrDistance = 0.01f;
    private const int NearbyStandableRings = 3;
    private const float ClimbableGuidePenalty = 1f;
    private const float NearbyStandableHeightPenalty = 1.25f;
    private const float SurfaceStandClearanceHeight = 1.55f;
    private const float SurfaceStandClearanceRadius = 0.22f;
    private const float SurfaceStandClearanceBottom = 0.18f;
    private const float OutermostHitDistanceTolerance = 0.05f;
    private static readonly RaycastHit[] SurfaceHitBuffer = new RaycastHit[64];
    private static readonly Collider[] ClearanceColliderBuffer = new Collider[16];
    private static readonly List<SurfaceHitCandidate> SurfaceCandidates = [];
    private static readonly Vector2[] NearbySearchDirections =
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

    internal static List<Vector3> Build(Vector3 start, Vector3 target, PlannerConfig config)
    {
        Vector3 delta = target - start;
        Vector3 horizontal = new(delta.x, 0f, delta.z);
        float horizontalDistance = horizontal.magnitude;
        float guideStep = Mathf.Max(0.25f, config.AdaptiveGuideMinimumStep);
        int sampleCount = Mathf.Clamp(
            Mathf.CeilToInt(horizontalDistance / guideStep) + 1,
            8,
            config.MaxGuideCurveSamples);

        int terrainMask = HelperFunctions.GetMask(HelperFunctions.LayerType.TerrainMap);
        int collisionMask = HelperFunctions.GetMask(HelperFunctions.LayerType.AllPhysicalExceptCharacter);
        float maxRayY = Mathf.Max(start.y, target.y) + config.VerticalScanPadding;
        float minRayY = Mathf.Min(start.y, target.y) - config.VerticalScanPadding;
        float rayDistance = Mathf.Max(1f, maxRayY - minRayY);
        List<Vector3> guide = [];
        for (int index = 0; index < sampleCount; index++)
        {
            float t = sampleCount <= 1 ? 1f : index / (float)(sampleCount - 1);
            Vector3 reference = Vector3.Lerp(start, target, t);
            float preferredY = guide.Count > 0
                ? guide[guide.Count - 1].y
                : start.y;
            Vector3 point = TryProjectToSurface(reference, preferredY, guideStep, maxRayY, rayDistance, terrainMask, collisionMask, config, out Vector3 surfacePoint)
                ? surfacePoint
                : reference;
            AddDistinct(guide, point);
        }

        if (guide.Count < 2)
        {
            AddDistinct(guide, start);
            AddDistinct(guide, target);
        }

        return guide;
    }

    private static bool TryProjectToSurface(
        Vector3 reference,
        float preferredY,
        float guideStep,
        float maxRayY,
        float rayDistance,
        int terrainMask,
        int collisionMask,
        PlannerConfig config,
        out Vector3 surfacePoint)
    {
        if (TryProjectAt(reference.x, reference.z, preferredY, maxRayY, rayDistance, terrainMask, collisionMask, config, out SurfaceHitCandidate direct))
        {
            if (direct.Kind == SurfaceKind.Standable
                || !TryFindNearbyStandableSurface(reference, preferredY, guideStep, maxRayY, rayDistance, terrainMask, collisionMask, config, out SurfaceHitCandidate nearby))
            {
                surfacePoint = direct.Position;
                return true;
            }

            float directScore = ScoreGuideCandidate(direct, reference.x, reference.z, preferredY);
            float nearbyScore = ScoreNearbyCandidate(nearby, reference.x, reference.z, preferredY);
            surfacePoint = nearbyScore <= directScore
                ? nearby.Position
                : direct.Position;
            return true;
        }

        if (TryFindNearbyStandableSurface(reference, preferredY, guideStep, maxRayY, rayDistance, terrainMask, collisionMask, config, out SurfaceHitCandidate fallback))
        {
            surfacePoint = fallback.Position;
            return true;
        }

        surfacePoint = default;
        return false;
    }

    private static bool TryFindNearbyStandableSurface(
        Vector3 reference,
        float preferredY,
        float guideStep,
        float maxRayY,
        float rayDistance,
        int terrainMask,
        int collisionMask,
        PlannerConfig config,
        out SurfaceHitCandidate surface)
    {
        surface = default;
        bool found = false;
        float bestScore = float.MaxValue;
        float step = Mathf.Max(0.5f, guideStep);
        for (int ring = 1; ring <= NearbyStandableRings; ring++)
        {
            float radius = step * ring;
            for (int index = 0; index < NearbySearchDirections.Length; index++)
            {
                Vector2 direction = NearbySearchDirections[index];
                float x = reference.x + direction.x * radius;
                float z = reference.z + direction.y * radius;
                if (!TryProjectAt(x, z, preferredY, maxRayY, rayDistance, terrainMask, collisionMask, config, out SurfaceHitCandidate candidate)
                    || candidate.Kind != SurfaceKind.Standable)
                {
                    continue;
                }

                float score = ScoreNearbyCandidate(candidate, reference.x, reference.z, preferredY);
                if (found && score >= bestScore)
                {
                    continue;
                }

                found = true;
                bestScore = score;
                surface = candidate;
            }

            if (found)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryProjectAt(
        float x,
        float z,
        float preferredY,
        float maxRayY,
        float rayDistance,
        int terrainMask,
        int collisionMask,
        PlannerConfig config,
        out SurfaceHitCandidate surface)
    {
        Vector3 origin = new(x, maxRayY, z);
        int hitCount = Physics.RaycastNonAlloc(
            origin,
            Vector3.down,
            SurfaceHitBuffer,
            rayDistance,
            terrainMask,
            QueryTriggerInteraction.Ignore);

        surface = default;
        SurfaceCandidates.Clear();
        float outermostDistance = GetOutermostHitDistance(hitCount);
        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = SurfaceHitBuffer[index];
            Collider collider = hit.collider;
            if (collider == null)
            {
                continue;
            }

            if (!IsOutermostHit(hit.distance, outermostDistance))
            {
                continue;
            }

            CollisionModifier modifier = collider.GetComponent<CollisionModifier>();
            if (modifier != null && !modifier.standable)
            {
                continue;
            }

            SurfaceKind kind = ClassifySurface(hit.normal, config);
            if (kind == SurfaceKind.Blocked)
            {
                continue;
            }

            SurfaceCandidates.Add(new SurfaceHitCandidate(hit.point, collider.GetInstanceID(), kind));
        }

        if (SurfaceCandidates.Count == 0)
        {
            return false;
        }

        float bestScore = float.MaxValue;
        for (int index = 0; index < SurfaceCandidates.Count; index++)
        {
            SurfaceHitCandidate candidate = SurfaceCandidates[index];
            if (IsOccludedStandableSurface(candidate, collisionMask))
            {
                continue;
            }

            float score = ScoreGuideCandidate(candidate, x, z, preferredY);
            if (score >= bestScore)
            {
                continue;
            }

            surface = candidate;
            bestScore = score;
        }

        return bestScore < float.MaxValue;
    }

    private static SurfaceKind ClassifySurface(Vector3 normal, PlannerConfig config)
    {
        float angle = Vector3.Angle(Vector3.up, normal);
        if (angle <= config.StandableNormalAngle)
        {
            return SurfaceKind.Standable;
        }

        return angle <= config.MaxClimbableNormalAngle
            ? SurfaceKind.Climbable
            : SurfaceKind.Blocked;
    }

    private static float GetOutermostHitDistance(int hitCount)
    {
        float bestDistance = float.MaxValue;
        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = SurfaceHitBuffer[index];
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

    private static float ScoreGuideCandidate(SurfaceHitCandidate candidate, float referenceX, float referenceZ, float preferredY)
    {
        float horizontalDistance = Vector2.Distance(
            new Vector2(candidate.Position.x, candidate.Position.z),
            new Vector2(referenceX, referenceZ));
        float heightDistance = Mathf.Abs(candidate.Position.y - preferredY);
        float kindPenalty = candidate.Kind == SurfaceKind.Standable ? 0f : ClimbableGuidePenalty;
        return heightDistance + horizontalDistance * 0.25f + kindPenalty;
    }

    private static float ScoreNearbyCandidate(SurfaceHitCandidate candidate, float referenceX, float referenceZ, float preferredY)
    {
        float horizontalDistance = Vector2.Distance(
            new Vector2(candidate.Position.x, candidate.Position.z),
            new Vector2(referenceX, referenceZ));
        float heightDistance = Mathf.Abs(candidate.Position.y - preferredY);
        return horizontalDistance + heightDistance * NearbyStandableHeightPenalty;
    }

    private static bool IsOccludedStandableSurface(SurfaceHitCandidate candidate, int collisionMask)
    {
        if (candidate.Kind != SurfaceKind.Standable)
        {
            return false;
        }

        if (!HasStandableClearance(candidate, collisionMask))
        {
            return true;
        }

        return false;
    }

    private static bool HasStandableClearance(SurfaceHitCandidate candidate, int collisionMask)
    {
        Vector3 bottom = candidate.Position + Vector3.up * SurfaceStandClearanceBottom;
        Vector3 top = candidate.Position + Vector3.up * SurfaceStandClearanceHeight;
        int overlapCount = Physics.OverlapCapsuleNonAlloc(
            bottom,
            top,
            SurfaceStandClearanceRadius,
            ClearanceColliderBuffer,
            collisionMask,
            QueryTriggerInteraction.Ignore);

        for (int index = 0; index < overlapCount; index++)
        {
            Collider collider = ClearanceColliderBuffer[index];
            if (collider == null || collider.GetInstanceID() == candidate.ColliderId)
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static void AddDistinct(List<Vector3> guide, Vector3 point)
    {
        if (guide.Count > 0 && (guide[guide.Count - 1] - point).sqrMagnitude <= PointMergeSqrDistance)
        {
            return;
        }

        guide.Add(point);
    }

    private readonly struct SurfaceHitCandidate
    {
        internal SurfaceHitCandidate(Vector3 position, int colliderId, SurfaceKind kind)
        {
            Position = position;
            ColliderId = colliderId;
            Kind = kind;
        }

        internal Vector3 Position { get; }

        internal int ColliderId { get; }

        internal SurfaceKind Kind { get; }
    }

}
