using UnityEngine;
using Object = UnityEngine.Object;

namespace PeakRoutePlanner.Planning;

internal sealed class SurfaceProbeBody
{
    private const float Radius = 0.24f;
    private const float BottomCenterHeight = Radius + 0.16f;
    private const float TopCenterHeight = 1.52f;
    private const float EndpointTolerance = 0.05f;
    private const float MoveSampleSpacing = 0.25f;
    private const int MaxMoveSamples = 8;
    private const int HitBufferSize = 24;

    private readonly Collider[] overlapHits = new Collider[HitBufferSize];
    private readonly RaycastHit[] castHits = new RaycastHit[HitBufferSize];
    private GameObject? probeObject;
    private CapsuleCollider? capsuleCollider;

    internal bool CanStandAt(Vector3 surfacePosition, int collisionMask)
    {
        EnsureCreated();
        MoveTo(surfacePosition);

        GetCapsulePoints(surfacePosition, out Vector3 bottom, out Vector3 top);
        int hitCount = Physics.OverlapCapsuleNonAlloc(
            bottom,
            top,
            Radius,
            overlapHits,
            collisionMask,
            QueryTriggerInteraction.Ignore);
        for (int index = 0; index < hitCount; index++)
        {
            Collider collider = overlapHits[index];
            if (collider == null || collider == capsuleCollider)
            {
                continue;
            }

            return false;
        }

        return true;
    }

    internal bool CanMoveStandable(Vector3 fromSurface, Vector3 toSurface, int collisionMask, int sourceColliderId, int targetColliderId)
    {
        EnsureCreated();
        MoveTo(fromSurface);

        Vector3 delta = toSurface - fromSurface;
        float distance = delta.magnitude;
        if (distance <= 0.001f)
        {
            return CanStandAt(toSurface, collisionMask);
        }

        int steps = Mathf.Clamp(Mathf.CeilToInt(distance / MoveSampleSpacing), 1, MaxMoveSamples);
        for (int step = 1; step <= steps; step++)
        {
            Vector3 sample = Vector3.Lerp(fromSurface, toSurface, step / (float)steps);
            if (!HasCapsuleClearance(sample, collisionMask))
            {
                return false;
            }
        }

        Vector3 direction = delta / distance;
        GetCapsulePoints(fromSurface, out Vector3 bottom, out Vector3 top);
        int hitCount = Physics.CapsuleCastNonAlloc(
            bottom,
            top,
            Radius,
            direction,
            castHits,
            distance,
            collisionMask,
            QueryTriggerInteraction.Ignore);
        for (int index = 0; index < hitCount; index++)
        {
            RaycastHit hit = castHits[index];
            Collider collider = hit.collider;
            if (collider == null
                || collider == capsuleCollider
                || hit.distance <= EndpointTolerance
                || hit.distance >= distance - EndpointTolerance)
            {
                continue;
            }

            int colliderId = collider.GetInstanceID();
            if (colliderId == sourceColliderId || colliderId == targetColliderId)
            {
                continue;
            }

            return false;
        }

        MoveTo(toSurface);
        return true;
    }

    internal void Dispose()
    {
        if (probeObject != null)
        {
            Object.Destroy(probeObject);
            probeObject = null;
            capsuleCollider = null;
        }
    }

    private void EnsureCreated()
    {
        if (probeObject != null && capsuleCollider != null)
        {
            return;
        }

        probeObject = new GameObject("PeakRoutePlanner Surface Probe")
        {
            hideFlags = HideFlags.HideAndDontSave,
        };
        probeObject.layer = 2;
        capsuleCollider = probeObject.AddComponent<CapsuleCollider>();
        capsuleCollider.radius = Radius;
        capsuleCollider.height = TopCenterHeight - BottomCenterHeight + Radius * 2f;
        capsuleCollider.center = Vector3.up * ((BottomCenterHeight + TopCenterHeight) * 0.5f);
        capsuleCollider.direction = 1;
        capsuleCollider.isTrigger = true;
    }

    private void MoveTo(Vector3 surfacePosition)
    {
        if (probeObject == null)
        {
            return;
        }

        probeObject.transform.position = surfacePosition;
    }

    private bool HasCapsuleClearance(Vector3 surfacePosition, int collisionMask)
    {
        MoveTo(surfacePosition);

        GetCapsulePoints(surfacePosition, out Vector3 bottom, out Vector3 top);
        int hitCount = Physics.OverlapCapsuleNonAlloc(
            bottom,
            top,
            Radius,
            overlapHits,
            collisionMask,
            QueryTriggerInteraction.Ignore);
        for (int index = 0; index < hitCount; index++)
        {
            Collider collider = overlapHits[index];
            if (collider == null || collider == capsuleCollider)
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static void GetCapsulePoints(Vector3 surfacePosition, out Vector3 bottom, out Vector3 top)
    {
        bottom = surfacePosition + Vector3.up * BottomCenterHeight;
        top = surfacePosition + Vector3.up * TopCenterHeight;
    }
}
