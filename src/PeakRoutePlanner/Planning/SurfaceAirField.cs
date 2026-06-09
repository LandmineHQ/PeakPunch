using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace PeakRoutePlanner.Planning;

internal sealed class SurfaceAirField
{
    private static readonly RaycastHit[] TransitionHitBuffer = new RaycastHit[8];
    private static readonly Collider[] PocketColliderBuffer = new Collider[16];
    private static readonly Vector3Int[] NeighborOffsets =
    [
        new(1, 0, 0),
        new(-1, 0, 0),
        new(0, 1, 0),
        new(0, -1, 0),
        new(0, 0, 1),
        new(0, 0, -1),
    ];

    private static readonly float[] PocketProbeOffsets = [0.38f, 0.68f, 1.0f, 1.35f];

    private readonly HashSet<AirCellKey> reachableCells;
    private readonly List<AirBoundaryProbe> boundaryProbes;
    private readonly List<Vector3> boundaryCellCenters;
    private readonly Vector3 min;
    private readonly float cellSize;
    private readonly int sizeX;
    private readonly int sizeY;
    private readonly int sizeZ;
    private readonly float probeRadius;
    private readonly int collisionMask;

    private SurfaceAirField(
        HashSet<AirCellKey> reachableCells,
        List<AirBoundaryProbe> boundaryProbes,
        List<Vector3> boundaryCellCenters,
        Vector3 min,
        float cellSize,
        int sizeX,
        int sizeY,
        int sizeZ,
        float probeRadius,
        int collisionMask,
        int checkedCellCount,
        int blockedCellCount,
        int blockedTransitionCount,
        int clearCellCacheHitCount,
        int clearTransitionCacheHitCount,
        int sliceAdvanceCount,
        bool overflowed,
        bool foundStart,
        double buildMilliseconds)
    {
        this.reachableCells = reachableCells;
        this.boundaryProbes = boundaryProbes;
        this.boundaryCellCenters = boundaryCellCenters;
        this.min = min;
        this.cellSize = cellSize;
        this.sizeX = sizeX;
        this.sizeY = sizeY;
        this.sizeZ = sizeZ;
        this.probeRadius = probeRadius;
        this.collisionMask = collisionMask;
        CheckedCellCount = checkedCellCount;
        BlockedCellCount = blockedCellCount;
        BlockedTransitionCount = blockedTransitionCount;
        ClearCellCacheHitCount = clearCellCacheHitCount;
        ClearTransitionCacheHitCount = clearTransitionCacheHitCount;
        SliceAdvanceCount = sliceAdvanceCount;
        Overflowed = overflowed;
        FoundStart = foundStart;
        BuildMilliseconds = buildMilliseconds;
    }

    internal int ReachableCellCount => reachableCells.Count;

    internal int BoundaryCellCount => boundaryCellCenters.Count;

    internal int BoundaryProbeCount => boundaryProbes.Count;

    internal int CheckedCellCount { get; }

    internal int BlockedCellCount { get; }

    internal int BlockedTransitionCount { get; }

    internal int ClearCellCacheHitCount { get; }

    internal int ClearTransitionCacheHitCount { get; }

    internal int SliceAdvanceCount { get; }

    internal bool Overflowed { get; }

    internal bool FoundStart { get; }

    internal double BuildMilliseconds { get; }

    internal int CopyReachableCellCenters(List<Vector3> target, int maxCount)
    {
        if (maxCount <= 0)
        {
            return 0;
        }

        int added = 0;
        for (int index = 0; index < boundaryCellCenters.Count; index++)
        {
            target.Add(boundaryCellCenters[index]);
            added++;
            if (added >= maxCount)
            {
                break;
            }
        }

        return added;
    }

    internal int QueueBoundaryProbes(
        Queue<AirBoundaryProbe> probes,
        int maxProbeCount)
    {
        int added = 0;
        for (int index = 0; index < boundaryProbes.Count; index++)
        {
            probes.Enqueue(boundaryProbes[index]);
            added++;
            if (added >= maxProbeCount)
            {
                return added;
            }
        }

        return added;
    }

    internal static SurfaceAirField Build(
        Vector3 boxCenter,
        Vector3 halfExtents,
        Vector3 seedPosition,
        Vector3 seedNormal,
        Vector3 scanDirection,
        float scanForwardHalfExtent,
        float scanLateralHalfExtent,
        float scanVerticalHalfExtent,
        float cellSize,
        float probeRadius,
        int collisionMask,
        int maxReachableCells,
        float maxScanForwardHalfExtent = -1f,
        SharedCache? sharedCache = null)
    {
        Builder builder = BeginBuild(
            boxCenter,
            halfExtents,
            seedPosition,
            seedNormal,
            scanDirection,
            scanForwardHalfExtent,
            scanLateralHalfExtent,
            scanVerticalHalfExtent,
            cellSize,
            probeRadius,
            collisionMask,
            maxReachableCells,
            maxScanForwardHalfExtent,
            sharedCache);
        while (!builder.IsComplete)
        {
            builder.Process(1024, double.PositiveInfinity);
        }

        return builder.ToField();
    }

    internal static Builder BeginBuild(
        Vector3 boxCenter,
        Vector3 halfExtents,
        Vector3 seedPosition,
        Vector3 seedNormal,
        Vector3 scanDirection,
        float scanForwardHalfExtent,
        float scanLateralHalfExtent,
        float scanVerticalHalfExtent,
        float cellSize,
        float probeRadius,
        int collisionMask,
        int maxReachableCells,
        float maxScanForwardHalfExtent = -1f,
        SharedCache? sharedCache = null)
    {
        return new Builder(
            boxCenter,
            halfExtents,
            seedPosition,
            seedNormal,
            scanDirection,
            scanForwardHalfExtent,
            scanLateralHalfExtent,
            scanVerticalHalfExtent,
            cellSize,
            probeRadius,
            collisionMask,
            maxReachableCells,
            maxScanForwardHalfExtent,
            sharedCache);
    }

    internal bool HasReachableNormalPocket(Vector3 surfacePosition, Vector3 normal, int surfaceColliderId)
    {
        return HasReachableNormalPocketCore(
            surfacePosition,
            normal,
            surfaceColliderId,
            reachableCells,
            min,
            cellSize,
            sizeX,
            sizeY,
            sizeZ,
            probeRadius,
            collisionMask,
            FoundStart,
            Overflowed);
    }

    private static bool HasReachableNormalPocketCore(
        Vector3 surfacePosition,
        Vector3 normal,
        int surfaceColliderId,
        HashSet<AirCellKey> reachableCells,
        Vector3 min,
        float cellSize,
        int sizeX,
        int sizeY,
        int sizeZ,
        float probeRadius,
        int collisionMask,
        bool foundStart,
        bool overflowed)
    {
        if (!foundStart || reachableCells.Count == 0)
        {
            return true;
        }

        Vector3 safeNormal = normal.sqrMagnitude > 0.001f ? normal.normalized : Vector3.up;
        for (int index = 0; index < PocketProbeOffsets.Length; index++)
        {
            Vector3 pocket = surfacePosition + safeNormal * PocketProbeOffsets[index];
            if (TryGetKey(pocket, min, cellSize, sizeX, sizeY, sizeZ, out AirCellKey key)
                && reachableCells.Contains(key)
                && HasClearPocketAccess(surfacePosition, safeNormal, pocket, surfaceColliderId, probeRadius, collisionMask))
            {
                return true;
            }
        }

        return overflowed;
    }

    private static bool HasClearPocketAccess(
        Vector3 surfacePosition,
        Vector3 normal,
        Vector3 pocket,
        int surfaceColliderId,
        float probeRadius,
        int collisionMask)
    {
        Vector3 start = surfacePosition + normal * Mathf.Max(0.06f, probeRadius * 0.35f);
        if (!IsPocketSphereClear(start, surfaceColliderId, probeRadius, collisionMask)
            || !IsPocketSphereClear(pocket, surfaceColliderId, probeRadius, collisionMask))
        {
            return false;
        }

        Vector3 delta = pocket - start;
        float distance = delta.magnitude;
        if (distance <= 0.001f)
        {
            return true;
        }

        bool previousBackfaceSetting = Physics.queriesHitBackfaces;
        try
        {
            Physics.queriesHitBackfaces = true;
            int hitCount = Physics.SphereCastNonAlloc(
                start,
                probeRadius,
                delta / distance,
                TransitionHitBuffer,
                distance,
                collisionMask,
                QueryTriggerInteraction.Ignore);
            for (int index = 0; index < hitCount; index++)
            {
                RaycastHit hit = TransitionHitBuffer[index];
                Collider collider = hit.collider;
                if (collider == null || hit.distance <= 0.001f || hit.distance >= distance - 0.001f)
                {
                    continue;
                }

                if (surfaceColliderId != 0 && collider.GetInstanceID() == surfaceColliderId)
                {
                    continue;
                }

                return false;
            }
        }
        finally
        {
            Physics.queriesHitBackfaces = previousBackfaceSetting;
        }


        return true;
    }

    private static bool IsPocketSphereClear(
        Vector3 position,
        int surfaceColliderId,
        float probeRadius,
        int collisionMask)
    {
        int colliderCount = Physics.OverlapSphereNonAlloc(
            position,
            probeRadius,
            PocketColliderBuffer,
            collisionMask,
            QueryTriggerInteraction.Ignore);
        for (int index = 0; index < colliderCount; index++)
        {
            Collider collider = PocketColliderBuffer[index];
            if (collider == null)
            {
                continue;
            }

            if (surfaceColliderId != 0 && collider.GetInstanceID() == surfaceColliderId)
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool TryEnqueueReachableCell(
        AirCellKey key,
        bool hasSource,
        AirCellKey sourceKey,
        Vector3 min,
        float cellSize,
        float probeRadius,
        int collisionMask,
        int maxReachableCells,
        int sizeX,
        int sizeY,
        int sizeZ,
        Dictionary<AirCellKey, bool> clearCells,
        SharedCache? sharedCache,
        HashSet<AirCellKey> reachableCells,
        PriorityQueue<AirCellKey> queue,
        Vector3 scanOrigin,
        Vector3 scanDirection,
        float scanForwardHalfExtent,
        float scanLateralHalfExtent,
        float scanVerticalHalfExtent,
        ref int checkedCellCount,
        ref int blockedCellCount,
        ref int blockedTransitionCount,
        ref int clearCellCacheHitCount,
        ref int clearTransitionCacheHitCount,
        ref bool overflowed)
    {
        if (overflowed
            || key.X < 0
            || key.Y < 0
            || key.Z < 0
            || key.X >= sizeX
            || key.Y >= sizeY
            || key.Z >= sizeZ
            || reachableCells.Contains(key))
        {
            return false;
        }

        Vector3 center = GetCellCenter(key, min, cellSize);
        if (!IsInsideScanEllipsoid(
                center,
                scanOrigin,
                scanDirection,
                scanForwardHalfExtent,
                scanLateralHalfExtent,
                scanVerticalHalfExtent))
        {
            return false;
        }

        if (!IsClearCell(
                key,
                center,
                cellSize,
                probeRadius,
                collisionMask,
                clearCells,
                sharedCache,
                ref checkedCellCount,
                ref blockedCellCount,
                ref clearCellCacheHitCount))
        {
            return false;
        }

        if (hasSource
            && !HasClearTransition(
                GetCellCenter(sourceKey, min, cellSize),
                center,
                probeRadius,
                collisionMask,
                cellSize,
                sharedCache,
                ref clearTransitionCacheHitCount))
        {
            blockedTransitionCount++;
            return false;
        }

        if (reachableCells.Count >= maxReachableCells)
        {
            overflowed = true;
            return false;
        }

        reachableCells.Add(key);
        queue.Enqueue(key, GetVerticalPlanePriority(key, min, cellSize, scanOrigin, scanDirection));
        return true;
    }

    private static float GetVerticalPlanePriority(
        AirCellKey key,
        Vector3 min,
        float cellSize,
        Vector3 scanOrigin,
        Vector3 scanDirection)
    {
        Vector3 center = GetCellCenter(key, min, cellSize);
        Vector3 delta = center - scanOrigin;
        float forward = Vector3.Dot(new Vector3(delta.x, 0f, delta.z), scanDirection);
        float lateral = Mathf.Abs(delta.x * scanDirection.z - delta.z * scanDirection.x);
        return forward + lateral * 0.15f;
    }

    private static bool IsInsideScanEllipsoid(
        Vector3 position,
        Vector3 scanOrigin,
        Vector3 scanDirection,
        float forwardHalfExtent,
        float lateralHalfExtent,
        float verticalHalfExtent)
    {
        Vector3 delta = position - scanOrigin;
        float forward = Vector3.Dot(new Vector3(delta.x, 0f, delta.z), scanDirection);
        float lateral = Mathf.Abs(delta.x * scanDirection.z - delta.z * scanDirection.x);
        float forwardRadius = Mathf.Max(0.001f, forwardHalfExtent);
        float lateralRadius = Mathf.Max(0.001f, lateralHalfExtent);
        float verticalRadius = Mathf.Max(0.001f, verticalHalfExtent);
        float normalizedForward = forward / forwardRadius;
        float normalizedLateral = lateral / lateralRadius;
        float normalizedVertical = delta.y / verticalRadius;
        return normalizedForward * normalizedForward
            + normalizedLateral * normalizedLateral
            + normalizedVertical * normalizedVertical <= 1.0001f;
    }

    private static Vector3 GetSafeHorizontalScanDirection(Vector3 direction)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.001f)
        {
            return Vector3.forward;
        }

        return direction.normalized;
    }

    private static bool IsClearCell(
        AirCellKey key,
        Vector3 center,
        float cellSize,
        float probeRadius,
        int collisionMask,
        Dictionary<AirCellKey, bool> clearCells,
        SharedCache? sharedCache,
        ref int checkedCellCount,
        ref int blockedCellCount,
        ref int clearCellCacheHitCount)
    {
        if (clearCells.TryGetValue(key, out bool cached))
        {
            return cached;
        }

        if (sharedCache != null
            && sharedCache.TryGetClearCell(center, cellSize, collisionMask, out cached))
        {
            clearCellCacheHitCount++;
            clearCells[key] = cached;
            if (!cached)
            {
                blockedCellCount++;
            }

            return cached;
        }

        checkedCellCount++;
        bool clear = !Physics.CheckSphere(center, probeRadius, collisionMask, QueryTriggerInteraction.Ignore);
        clearCells[key] = clear;
        sharedCache?.StoreClearCell(center, cellSize, collisionMask, clear);
        if (!clear)
        {
            blockedCellCount++;
        }

        return clear;
    }

    private static bool HasClearTransition(
        Vector3 from,
        Vector3 to,
        float probeRadius,
        int collisionMask,
        float cellSize,
        SharedCache? sharedCache,
        ref int clearTransitionCacheHitCount)
    {
        if (sharedCache != null
            && sharedCache.TryGetClearTransition(from, to, cellSize, collisionMask, out bool cached))
        {
            clearTransitionCacheHitCount++;
            return cached;
        }

        bool clear = !Physics.CheckCapsule(from, to, probeRadius, collisionMask, QueryTriggerInteraction.Ignore);
        sharedCache?.StoreClearTransition(from, to, cellSize, collisionMask, clear);
        return clear;
    }

    private static bool TryGetKey(
        Vector3 position,
        Vector3 min,
        float cellSize,
        int sizeX,
        int sizeY,
        int sizeZ,
        out AirCellKey key)
    {
        key = new AirCellKey(
            Mathf.FloorToInt((position.x - min.x) / cellSize),
            Mathf.FloorToInt((position.y - min.y) / cellSize),
            Mathf.FloorToInt((position.z - min.z) / cellSize));
        return key.X >= 0
            && key.Y >= 0
            && key.Z >= 0
            && key.X < sizeX
            && key.Y < sizeY
            && key.Z < sizeZ;
    }

    private static Vector3 GetCellCenter(AirCellKey key, Vector3 min, float cellSize)
    {
        return min + new Vector3(
            (key.X + 0.5f) * cellSize,
            (key.Y + 0.5f) * cellSize,
            (key.Z + 0.5f) * cellSize);
    }

    private static Vector3 AlignMinToWorldGrid(Vector3 min, float cellSize)
    {
        float safeCellSize = Mathf.Max(0.5f, cellSize);
        return new Vector3(
            Mathf.Floor(min.x / safeCellSize) * safeCellSize,
            Mathf.Floor(min.y / safeCellSize) * safeCellSize,
            Mathf.Floor(min.z / safeCellSize) * safeCellSize);
    }

    internal sealed class SharedCache
    {
        private readonly Dictionary<WorldAirCellKey, bool> clearCells = [];
        private readonly Dictionary<WorldAirTransitionKey, bool> clearTransitions = [];

        internal int ClearCellEntryCount => clearCells.Count;

        internal int ClearTransitionEntryCount => clearTransitions.Count;

        internal void Clear()
        {
            clearCells.Clear();
            clearTransitions.Clear();
        }

        internal bool TryGetClearCell(Vector3 center, float cellSize, int collisionMask, out bool clear)
        {
            return clearCells.TryGetValue(WorldAirCellKey.From(center, cellSize, collisionMask), out clear);
        }

        internal void StoreClearCell(Vector3 center, float cellSize, int collisionMask, bool clear)
        {
            clearCells[WorldAirCellKey.From(center, cellSize, collisionMask)] = clear;
        }

        internal bool TryGetClearTransition(Vector3 from, Vector3 to, float cellSize, int collisionMask, out bool clear)
        {
            return clearTransitions.TryGetValue(WorldAirTransitionKey.From(from, to, cellSize, collisionMask), out clear);
        }

        internal void StoreClearTransition(Vector3 from, Vector3 to, float cellSize, int collisionMask, bool clear)
        {
            clearTransitions[WorldAirTransitionKey.From(from, to, cellSize, collisionMask)] = clear;
        }
    }

    internal sealed class Builder
    {
        private readonly Dictionary<AirCellKey, bool> clearCells = [];
        private readonly HashSet<AirCellKey> reachableCells = [];
        private readonly PriorityQueue<AirCellKey> queue = new();
        private readonly Queue<AirBoundaryProbe> pendingBoundaryProbes = new();
        private readonly List<AirBoundaryProbe> boundaryProbes = [];
        private readonly List<Vector3> boundaryCellCenters = [];
        private readonly HashSet<BoundaryProbeKey> queuedBoundaries = [];
        private readonly HashSet<AirCellKey> boundaryCells = [];
        private readonly HashSet<AirCellKey> sliceFrontierCells = [];
        private readonly Vector3 min;
        private readonly float cellSize;
        private readonly float probeRadius;
        private readonly int collisionMask;
        private readonly int maxReachableCells;
        private readonly int sizeX;
        private readonly int sizeY;
        private readonly int sizeZ;
        private readonly Vector3 scanDirection;
        private readonly Vector3 scanOrigin;
        private readonly float scanForwardStep;
        private readonly float maxScanForwardHalfExtent;
        private readonly float scanLateralHalfExtent;
        private readonly float scanVerticalHalfExtent;
        private readonly SharedCache? sharedCache;
        private float currentScanForwardHalfExtent;
        private int checkedCellCount;
        private int blockedCellCount;
        private int blockedTransitionCount;
        private int clearCellCacheHitCount;
        private int clearTransitionCacheHitCount;
        private int sliceAdvanceCount;
        private bool overflowed;
        private bool foundStart;
        private double buildMilliseconds;

        internal Builder(
            Vector3 boxCenter,
            Vector3 halfExtents,
            Vector3 seedPosition,
            Vector3 seedNormal,
            Vector3 requestedScanDirection,
            float requestedScanForwardHalfExtent,
            float requestedScanLateralHalfExtent,
            float requestedScanVerticalHalfExtent,
            float requestedCellSize,
            float requestedProbeRadius,
            int collisionMask,
            int maxReachableCells,
            float requestedMaxScanForwardHalfExtent = -1f,
            SharedCache? sharedCache = null)
        {
            cellSize = Mathf.Max(0.5f, requestedCellSize);
            probeRadius = Mathf.Clamp(requestedProbeRadius, 0.08f, cellSize * 0.45f);
            this.collisionMask = collisionMask;
            this.maxReachableCells = maxReachableCells;
            this.sharedCache = sharedCache;
            min = AlignMinToWorldGrid(boxCenter - halfExtents, cellSize);
            Vector3 max = boxCenter + halfExtents;
            sizeX = Mathf.Max(1, Mathf.CeilToInt((max.x - min.x) / cellSize));
            sizeY = Mathf.Max(1, Mathf.CeilToInt((max.y - min.y) / cellSize));
            sizeZ = Mathf.Max(1, Mathf.CeilToInt((max.z - min.z) / cellSize));
            scanOrigin = seedPosition;
            scanDirection = GetSafeHorizontalScanDirection(requestedScanDirection);
            scanForwardStep = Mathf.Max(cellSize * 0.5f, requestedScanForwardHalfExtent);
            maxScanForwardHalfExtent = Mathf.Max(
                scanForwardStep,
                requestedMaxScanForwardHalfExtent > 0f ? requestedMaxScanForwardHalfExtent : scanForwardStep);
            currentScanForwardHalfExtent = scanForwardStep;
            scanLateralHalfExtent = Mathf.Max(cellSize * 0.5f, requestedScanLateralHalfExtent);
            scanVerticalHalfExtent = Mathf.Max(cellSize * 0.5f, requestedScanVerticalHalfExtent);

            Vector3 safeNormal = seedNormal.sqrMagnitude > 0.001f ? seedNormal.normalized : Vector3.up;
            Vector3[] startPositions =
            [
                seedPosition + safeNormal * 0.8f,
                seedPosition + safeNormal * 1.15f,
                seedPosition + Vector3.up * 0.8f,
                seedPosition + (safeNormal + Vector3.up).normalized * 0.95f,
            ];
            for (int index = 0; index < startPositions.Length; index++)
            {
                if (!TryGetKey(startPositions[index], min, cellSize, sizeX, sizeY, sizeZ, out AirCellKey key))
                {
                    continue;
                }

                if (TryEnqueueReachableCell(
                        key,
                        hasSource: false,
                        sourceKey: default,
                        min,
                        cellSize,
                        probeRadius,
                        collisionMask,
                        maxReachableCells,
                        sizeX,
                        sizeY,
                        sizeZ,
                        clearCells,
                        sharedCache,
                        reachableCells,
                        queue,
                        scanOrigin,
                        scanDirection,
                        currentScanForwardHalfExtent,
                        scanLateralHalfExtent,
                        scanVerticalHalfExtent,
                        ref checkedCellCount,
                        ref blockedCellCount,
                        ref blockedTransitionCount,
                        ref clearCellCacheHitCount,
                        ref clearTransitionCacheHitCount,
                        ref overflowed))
                {
                    foundStart = true;
                    break;
                }
            }
        }

        internal bool IsComplete => !foundStart
            || overflowed
            || (queue.Count == 0 && currentScanForwardHalfExtent >= maxScanForwardHalfExtent - 0.001f);

        internal bool FoundStart => foundStart;

        internal int ReachableCellCount => reachableCells.Count;

        internal bool HasReachableNormalPocket(Vector3 surfacePosition, Vector3 normal, int surfaceColliderId)
        {
            return HasReachableNormalPocketCore(
                surfacePosition,
                normal,
                surfaceColliderId,
                reachableCells,
                min,
                cellSize,
                sizeX,
                sizeY,
                sizeZ,
                probeRadius,
                collisionMask,
                foundStart,
                overflowed);
        }

        internal int DrainBoundaryProbes(Queue<AirBoundaryProbe> target, int maxCount)
        {
            int added = 0;
            while (pendingBoundaryProbes.Count > 0 && added < maxCount)
            {
                target.Enqueue(pendingBoundaryProbes.Dequeue());
                added++;
            }

            return added;
        }

        internal bool Process(int maxCells, double maxMilliseconds)
        {
            if (IsComplete)
            {
                return true;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            int processed = 0;
            while (!overflowed)
            {
                if (queue.Count == 0)
                {
                    if (!TryAdvanceScanSlice())
                    {
                        break;
                    }
                }

                AirCellKey current = queue.Dequeue();
                Vector3 currentCenter = GetCellCenter(current, min, cellSize);
                for (int index = 0; index < NeighborOffsets.Length; index++)
                {
                    Vector3Int offset = NeighborOffsets[index];
                    AirCellKey neighbor = new(current.X + offset.x, current.Y + offset.y, current.Z + offset.z);
                    bool enqueued = TryEnqueueReachableCell(
                        neighbor,
                        hasSource: true,
                        sourceKey: current,
                        min,
                        cellSize,
                        probeRadius,
                        collisionMask,
                        maxReachableCells,
                        sizeX,
                        sizeY,
                        sizeZ,
                        clearCells,
                        sharedCache,
                        reachableCells,
                        queue,
                        scanOrigin,
                        scanDirection,
                        currentScanForwardHalfExtent,
                        scanLateralHalfExtent,
                        scanVerticalHalfExtent,
                        ref checkedCellCount,
                        ref blockedCellCount,
                        ref blockedTransitionCount,
                        ref clearCellCacheHitCount,
                        ref clearTransitionCacheHitCount,
                        ref overflowed);
                    if (!enqueued)
                    {
                        TryQueueBoundaryProbe(current, currentCenter, offset, neighbor);
                    }

                    if (overflowed)
                    {
                        break;
                    }
                }

                processed++;
                if (processed >= maxCells || stopwatch.Elapsed.TotalMilliseconds >= maxMilliseconds)
                {
                    break;
                }
            }

            stopwatch.Stop();
            buildMilliseconds += stopwatch.Elapsed.TotalMilliseconds;
            return IsComplete;
        }

        private bool TryAdvanceScanSlice()
        {
            if (currentScanForwardHalfExtent >= maxScanForwardHalfExtent - 0.001f)
            {
                return false;
            }

            currentScanForwardHalfExtent = Mathf.Min(
                maxScanForwardHalfExtent,
                currentScanForwardHalfExtent + scanForwardStep);
            sliceAdvanceCount++;

            List<AirCellKey> currentCells = sliceFrontierCells.Count > 0
                ? new List<AirCellKey>(sliceFrontierCells)
                : new List<AirCellKey>(reachableCells);
            sliceFrontierCells.Clear();
            for (int cellIndex = 0; cellIndex < currentCells.Count; cellIndex++)
            {
                AirCellKey current = currentCells[cellIndex];
                Vector3 currentCenter = GetCellCenter(current, min, cellSize);
                for (int index = 0; index < NeighborOffsets.Length; index++)
                {
                    Vector3Int offset = NeighborOffsets[index];
                    AirCellKey neighbor = new(current.X + offset.x, current.Y + offset.y, current.Z + offset.z);
                    bool enqueued = TryEnqueueReachableCell(
                        neighbor,
                        hasSource: true,
                        sourceKey: current,
                        min,
                        cellSize,
                        probeRadius,
                        collisionMask,
                        maxReachableCells,
                        sizeX,
                        sizeY,
                        sizeZ,
                        clearCells,
                        sharedCache,
                        reachableCells,
                        queue,
                        scanOrigin,
                        scanDirection,
                        currentScanForwardHalfExtent,
                        scanLateralHalfExtent,
                        scanVerticalHalfExtent,
                        ref checkedCellCount,
                        ref blockedCellCount,
                        ref blockedTransitionCount,
                        ref clearCellCacheHitCount,
                        ref clearTransitionCacheHitCount,
                        ref overflowed);
                    if (!enqueued)
                    {
                        TryQueueBoundaryProbe(current, currentCenter, offset, neighbor);
                    }

                    if (overflowed)
                    {
                        break;
                    }
                }

                if (overflowed)
                {
                    break;
                }
            }

            return queue.Count > 0;
        }

        private void TryQueueBoundaryProbe(
            AirCellKey cell,
            Vector3 origin,
            Vector3Int offset,
            AirCellKey neighbor)
        {
            if (neighbor.X < 0
                || neighbor.Y < 0
                || neighbor.Z < 0
                || neighbor.X >= sizeX
                || neighbor.Y >= sizeY
                || neighbor.Z >= sizeZ
                || reachableCells.Contains(neighbor))
            {
                return;
            }

            Vector3 neighborCenter = GetCellCenter(neighbor, min, cellSize);
            if (!IsInsideScanEllipsoid(
                    neighborCenter,
                    scanOrigin,
                    scanDirection,
                    currentScanForwardHalfExtent,
                    scanLateralHalfExtent,
                    scanVerticalHalfExtent))
            {
                if (IsInsideScanEllipsoid(
                        neighborCenter,
                        scanOrigin,
                        scanDirection,
                        maxScanForwardHalfExtent,
                        scanLateralHalfExtent,
                        scanVerticalHalfExtent))
                {
                    sliceFrontierCells.Add(cell);
                }

                return;
            }

            if (IsClearCell(
                    neighbor,
                    neighborCenter,
                    cellSize,
                    probeRadius,
                    collisionMask,
                    clearCells,
                    sharedCache,
                    ref checkedCellCount,
                    ref blockedCellCount,
                    ref clearCellCacheHitCount))
            {
                return;
            }

            Vector3 direction = new(offset.x, offset.y, offset.z);
            if (direction.sqrMagnitude < 0.001f)
            {
                return;
            }

            direction.Normalize();
            if (!queuedBoundaries.Add(BoundaryProbeKey.From(origin + direction * (cellSize * 0.5f), direction, cellSize)))
            {
                return;
            }

            AirBoundaryProbe probe = new(origin, direction, cellSize * 1.5f);
            boundaryProbes.Add(probe);
            pendingBoundaryProbes.Enqueue(probe);
            if (boundaryCells.Add(cell))
            {
                boundaryCellCenters.Add(origin);
            }
        }

        internal SurfaceAirField ToField()
        {
            return new SurfaceAirField(
                reachableCells,
                boundaryProbes,
                boundaryCellCenters,
                min,
                cellSize,
                sizeX,
                sizeY,
                sizeZ,
                probeRadius,
                collisionMask,
                checkedCellCount,
                blockedCellCount,
                blockedTransitionCount,
                clearCellCacheHitCount,
                clearTransitionCacheHitCount,
                sliceAdvanceCount,
                overflowed,
                foundStart,
                buildMilliseconds);
        }
    }

    private static void BuildBoundaryData(
        HashSet<AirCellKey> reachableCells,
        Vector3 min,
        float cellSize,
        float probeRadius,
        int collisionMask,
        int sizeX,
        int sizeY,
        int sizeZ,
        out List<AirBoundaryProbe> boundaryProbes,
        out List<Vector3> boundaryCellCenters)
    {
        boundaryProbes = [];
        boundaryCellCenters = [];
        HashSet<BoundaryProbeKey> queuedBoundaries = [];
        HashSet<AirCellKey> boundaryCells = [];
        foreach (AirCellKey cell in reachableCells)
        {
            Vector3 origin = GetCellCenter(cell, min, cellSize);
            bool isBoundaryCell = false;
            for (int index = 0; index < NeighborOffsets.Length; index++)
            {
                Vector3Int offset = NeighborOffsets[index];
                AirCellKey neighbor = new(cell.X + offset.x, cell.Y + offset.y, cell.Z + offset.z);
                if (neighbor.X < 0
                    || neighbor.Y < 0
                    || neighbor.Z < 0
                    || neighbor.X >= sizeX
                    || neighbor.Y >= sizeY
                    || neighbor.Z >= sizeZ
                    || reachableCells.Contains(neighbor))
                {
                    continue;
                }

                Vector3 neighborCenter = GetCellCenter(neighbor, min, cellSize);
                if (!Physics.CheckSphere(neighborCenter, probeRadius, collisionMask, QueryTriggerInteraction.Ignore))
                {
                    continue;
                }

                Vector3 direction = new(offset.x, offset.y, offset.z);
                if (direction.sqrMagnitude < 0.001f)
                {
                    continue;
                }

                direction.Normalize();
                if (!queuedBoundaries.Add(BoundaryProbeKey.From(origin + direction * (cellSize * 0.5f), direction, cellSize)))
                {
                    continue;
                }

                boundaryProbes.Add(new AirBoundaryProbe(origin, direction, cellSize * 1.5f));
                isBoundaryCell = true;
            }

            if (isBoundaryCell && boundaryCells.Add(cell))
            {
                boundaryCellCenters.Add(origin);
            }
        }
    }

    private readonly struct AirCellKey : System.IEquatable<AirCellKey>
    {
        internal AirCellKey(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        internal int X { get; }

        internal int Y { get; }

        internal int Z { get; }

        public bool Equals(AirCellKey other)
        {
            return X == other.X && Y == other.Y && Z == other.Z;
        }

        public override bool Equals(object? obj)
        {
            return obj is AirCellKey other && Equals(other);
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

    private readonly struct WorldAirCellKey : System.IEquatable<WorldAirCellKey>
    {
        private WorldAirCellKey(int x, int y, int z, int mask)
        {
            X = x;
            Y = y;
            Z = z;
            Mask = mask;
        }

        private int X { get; }

        private int Y { get; }

        private int Z { get; }

        private int Mask { get; }

        internal int SortX => X;

        internal int SortY => Y;

        internal int SortZ => Z;

        internal int SortMask => Mask;

        internal static WorldAirCellKey From(Vector3 center, float cellSize, int collisionMask)
        {
            float safeCellSize = Mathf.Max(0.5f, cellSize);
            return new WorldAirCellKey(
                Mathf.RoundToInt((center.x - safeCellSize * 0.5f) / safeCellSize),
                Mathf.RoundToInt((center.y - safeCellSize * 0.5f) / safeCellSize),
                Mathf.RoundToInt((center.z - safeCellSize * 0.5f) / safeCellSize),
                collisionMask);
        }

        public bool Equals(WorldAirCellKey other)
        {
            return X == other.X && Y == other.Y && Z == other.Z && Mask == other.Mask;
        }

        public override bool Equals(object? obj)
        {
            return obj is WorldAirCellKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X;
                hash = (hash * 397) ^ Y;
                hash = (hash * 397) ^ Z;
                hash = (hash * 397) ^ Mask;
                return hash;
            }
        }
    }

    private readonly struct WorldAirTransitionKey : System.IEquatable<WorldAirTransitionKey>
    {
        private WorldAirTransitionKey(WorldAirCellKey a, WorldAirCellKey b)
        {
            A = a;
            B = b;
        }

        private WorldAirCellKey A { get; }

        private WorldAirCellKey B { get; }

        internal static WorldAirTransitionKey From(Vector3 from, Vector3 to, float cellSize, int collisionMask)
        {
            WorldAirCellKey a = WorldAirCellKey.From(from, cellSize, collisionMask);
            WorldAirCellKey b = WorldAirCellKey.From(to, cellSize, collisionMask);
            return Compare(a, b) <= 0
                ? new WorldAirTransitionKey(a, b)
                : new WorldAirTransitionKey(b, a);
        }

        private static int Compare(WorldAirCellKey a, WorldAirCellKey b)
        {
            int result = a.SortX.CompareTo(b.SortX);
            if (result != 0)
            {
                return result;
            }

            result = a.SortY.CompareTo(b.SortY);
            if (result != 0)
            {
                return result;
            }

            result = a.SortZ.CompareTo(b.SortZ);
            return result != 0 ? result : a.SortMask.CompareTo(b.SortMask);
        }

        public bool Equals(WorldAirTransitionKey other)
        {
            return A.Equals(other.A) && B.Equals(other.B);
        }

        public override bool Equals(object? obj)
        {
            return obj is WorldAirTransitionKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (A.GetHashCode() * 397) ^ B.GetHashCode();
            }
        }
    }

    private readonly struct BoundaryProbeKey : System.IEquatable<BoundaryProbeKey>
    {
        private BoundaryProbeKey(int x, int y, int z, int direction)
        {
            X = x;
            Y = y;
            Z = z;
            Direction = direction;
        }

        private int X { get; }

        private int Y { get; }

        private int Z { get; }

        private int Direction { get; }

        internal static BoundaryProbeKey From(Vector3 position, Vector3 direction, float cellSize)
        {
            float quantize = Mathf.Max(0.25f, cellSize * 0.5f);
            int directionKey = Mathf.RoundToInt(direction.x)
                + Mathf.RoundToInt(direction.y) * 7
                + Mathf.RoundToInt(direction.z) * 31;
            return new BoundaryProbeKey(
                Mathf.RoundToInt(position.x / quantize),
                Mathf.RoundToInt(position.y / quantize),
                Mathf.RoundToInt(position.z / quantize),
                directionKey);
        }

        public bool Equals(BoundaryProbeKey other)
        {
            return X == other.X && Y == other.Y && Z == other.Z && Direction == other.Direction;
        }

        public override bool Equals(object? obj)
        {
            return obj is BoundaryProbeKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X;
                hash = (hash * 397) ^ Y;
                hash = (hash * 397) ^ Z;
                hash = (hash * 397) ^ Direction;
                return hash;
            }
        }
    }

    internal readonly struct AirBoundaryProbe
    {
        internal AirBoundaryProbe(Vector3 origin, Vector3 direction, float distance)
        {
            Origin = origin;
            Direction = direction;
            Distance = distance;
        }

        internal Vector3 Origin { get; }

        internal Vector3 Direction { get; }

        internal float Distance { get; }
    }

}
