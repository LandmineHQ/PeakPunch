using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace PeakRoutePlanner.Planning;

internal sealed class SurfaceMeshSnapshot
{
    private SurfaceMeshSnapshot(
        List<SurfaceMeshTriangle> triangles,
        int colliderCount,
        int meshColliderCount,
        int skippedColliderCount,
        int skippedTriangleCount)
    {
        Triangles = triangles;
        ColliderCount = colliderCount;
        MeshColliderCount = meshColliderCount;
        SkippedColliderCount = skippedColliderCount;
        SkippedTriangleCount = skippedTriangleCount;
    }

    internal List<SurfaceMeshTriangle> Triangles { get; }

    internal int ColliderCount { get; }

    internal int MeshColliderCount { get; }

    internal int SkippedColliderCount { get; }

    internal int SkippedTriangleCount { get; }

    internal static SurfaceMeshSnapshot Capture(
        Collider[] colliders,
        int colliderCount,
        Vector3 boxCenter,
        Vector3 halfExtents,
        int maxTriangles)
    {
        List<SurfaceMeshTriangle> triangles = [];
        int meshColliderCount = 0;
        int skippedColliderCount = 0;
        int skippedTriangleCount = 0;
        Vector3 min = boxCenter - halfExtents;
        Vector3 max = boxCenter + halfExtents;
        for (int colliderIndex = 0; colliderIndex < colliderCount; colliderIndex++)
        {
            if (triangles.Count >= maxTriangles)
            {
                skippedColliderCount += colliderCount - colliderIndex;
                break;
            }

            if (colliders[colliderIndex] is not MeshCollider meshCollider)
            {
                skippedColliderCount++;
                continue;
            }

            Mesh? mesh = meshCollider.sharedMesh;
            if (mesh == null)
            {
                skippedColliderCount++;
                continue;
            }

            Vector3[] vertices;
            int[] meshTriangles;
            try
            {
                if (!mesh.isReadable)
                {
                    skippedColliderCount++;
                    continue;
                }

                vertices = mesh.vertices;
                meshTriangles = mesh.triangles;
            }
            catch
            {
                skippedColliderCount++;
                continue;
            }

            meshColliderCount++;
            Matrix4x4 localToWorld = meshCollider.transform.localToWorldMatrix;
            int colliderId = meshCollider.GetInstanceID();
            for (int triIndex = 0; triIndex + 2 < meshTriangles.Length; triIndex += 3)
            {
                if (triangles.Count >= maxTriangles)
                {
                    skippedTriangleCount += (meshTriangles.Length - triIndex) / 3;
                    break;
                }

                int ia = meshTriangles[triIndex];
                int ib = meshTriangles[triIndex + 1];
                int ic = meshTriangles[triIndex + 2];
                if ((uint)ia >= (uint)vertices.Length
                    || (uint)ib >= (uint)vertices.Length
                    || (uint)ic >= (uint)vertices.Length)
                {
                    skippedTriangleCount++;
                    continue;
                }

                Vector3 a = localToWorld.MultiplyPoint3x4(vertices[ia]);
                Vector3 b = localToWorld.MultiplyPoint3x4(vertices[ib]);
                Vector3 c = localToWorld.MultiplyPoint3x4(vertices[ic]);
                if (!IntersectsAabb(a, b, c, min, max))
                {
                    continue;
                }

                Vector3 normal = Vector3.Cross(b - a, c - a);
                if (normal.sqrMagnitude <= 0.000001f)
                {
                    skippedTriangleCount++;
                    continue;
                }

                triangles.Add(new SurfaceMeshTriangle(a, b, c, normal.normalized, colliderId));
            }
        }

        return new SurfaceMeshSnapshot(
            triangles,
            colliderCount,
            meshColliderCount,
            skippedColliderCount,
            skippedTriangleCount);
    }

    private static bool IntersectsAabb(Vector3 a, Vector3 b, Vector3 c, Vector3 min, Vector3 max)
    {
        float triMinX = Mathf.Min(a.x, Mathf.Min(b.x, c.x));
        float triMinY = Mathf.Min(a.y, Mathf.Min(b.y, c.y));
        float triMinZ = Mathf.Min(a.z, Mathf.Min(b.z, c.z));
        float triMaxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
        float triMaxY = Mathf.Max(a.y, Mathf.Max(b.y, c.y));
        float triMaxZ = Mathf.Max(a.z, Mathf.Max(b.z, c.z));
        return triMaxX >= min.x
            && triMinX <= max.x
            && triMaxY >= min.y
            && triMinY <= max.y
            && triMaxZ >= min.z
            && triMinZ <= max.z;
    }
}

internal sealed class SurfaceMeshField
{
    private const float EndpointTolerance = 0.16f;
    private const float IntersectionEpsilon = 0.00001f;
    private readonly List<SurfaceMeshTriangle> triangles;
    private readonly Dictionary<MeshCellKey, List<int>> cells;
    private readonly int[] queryMarkers;
    private readonly float cellSize;
    private int queryMarker;

    private SurfaceMeshField(
        List<SurfaceMeshTriangle> triangles,
        Dictionary<MeshCellKey, List<int>> cells,
        float cellSize,
        double buildMilliseconds)
    {
        this.triangles = triangles;
        this.cells = cells;
        this.cellSize = Mathf.Max(0.25f, cellSize);
        queryMarkers = new int[triangles.Count];
        BuildMilliseconds = buildMilliseconds;
    }

    internal int TriangleCount => triangles.Count;

    internal int CellCount => cells.Count;

    internal double BuildMilliseconds { get; }

    internal static SurfaceMeshField Build(SurfaceMeshSnapshot snapshot, float cellSize)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        float safeCellSize = Mathf.Max(0.25f, cellSize);
        Dictionary<MeshCellKey, List<int>> cells = [];
        List<SurfaceMeshTriangle> triangles = snapshot.Triangles;
        for (int index = 0; index < triangles.Count; index++)
        {
            SurfaceMeshTriangle triangle = triangles[index];
            Vector3 min = triangle.Min;
            Vector3 max = triangle.Max;
            MeshCellKey minKey = MeshCellKey.From(min, safeCellSize);
            MeshCellKey maxKey = MeshCellKey.From(max, safeCellSize);
            for (int x = minKey.X; x <= maxKey.X; x++)
            {
                for (int y = minKey.Y; y <= maxKey.Y; y++)
                {
                    for (int z = minKey.Z; z <= maxKey.Z; z++)
                    {
                        MeshCellKey key = new(x, y, z);
                        if (!cells.TryGetValue(key, out List<int> triangleIds))
                        {
                            triangleIds = [];
                            cells[key] = triangleIds;
                        }

                        triangleIds.Add(index);
                    }
                }
            }
        }

        stopwatch.Stop();
        return new SurfaceMeshField(triangles, cells, safeCellSize, stopwatch.Elapsed.TotalMilliseconds);
    }

    internal bool HasClearNormalPocket(Vector3 surfacePosition, Vector3 normal, int surfaceColliderId)
    {
        Vector3 safeNormal = normal.sqrMagnitude > 0.001f ? normal.normalized : Vector3.up;
        Vector3 from = surfacePosition + safeNormal * 0.08f;
        Vector3 to = surfacePosition + safeNormal * 0.7f;
        return HasClearSegment(from, to, surfaceColliderId, surfaceColliderId);
    }

    internal bool HasClearSegment(Vector3 from, Vector3 to, int sourceColliderId, int targetColliderId)
    {
        if (triangles.Count == 0)
        {
            return true;
        }

        Vector3 delta = to - from;
        float distance = delta.magnitude;
        if (distance <= 0.001f)
        {
            return true;
        }

        Vector3 direction = delta / distance;
        int marker = NextQueryMarker();
        Vector3 min = Vector3.Min(from, to) - Vector3.one * 0.05f;
        Vector3 max = Vector3.Max(from, to) + Vector3.one * 0.05f;
        MeshCellKey minKey = MeshCellKey.From(min, cellSize);
        MeshCellKey maxKey = MeshCellKey.From(max, cellSize);
        for (int x = minKey.X; x <= maxKey.X; x++)
        {
            for (int y = minKey.Y; y <= maxKey.Y; y++)
            {
                for (int z = minKey.Z; z <= maxKey.Z; z++)
                {
                    if (!cells.TryGetValue(new MeshCellKey(x, y, z), out List<int> triangleIds))
                    {
                        continue;
                    }

                    for (int index = 0; index < triangleIds.Count; index++)
                    {
                        int triangleId = triangleIds[index];
                        if (queryMarkers[triangleId] == marker)
                        {
                            continue;
                        }

                        queryMarkers[triangleId] = marker;
                        SurfaceMeshTriangle triangle = triangles[triangleId];
                        if (!IntersectsSegmentTriangle(from, direction, distance, triangle, out float hitDistance))
                        {
                            continue;
                        }

                        if (IsEndpointHit(hitDistance, distance)
                            && (triangle.ColliderId == sourceColliderId || triangle.ColliderId == targetColliderId))
                        {
                            continue;
                        }

                        return false;
                    }
                }
            }
        }

        return true;
    }

    private int NextQueryMarker()
    {
        queryMarker++;
        if (queryMarker != int.MaxValue)
        {
            return queryMarker;
        }

        Array.Clear(queryMarkers, 0, queryMarkers.Length);
        queryMarker = 1;
        return queryMarker;
    }

    private static bool IsEndpointHit(float hitDistance, float segmentDistance)
    {
        return hitDistance <= EndpointTolerance || hitDistance >= segmentDistance - EndpointTolerance;
    }

    private static bool IntersectsSegmentTriangle(
        Vector3 origin,
        Vector3 direction,
        float segmentDistance,
        SurfaceMeshTriangle triangle,
        out float hitDistance)
    {
        hitDistance = 0f;
        Vector3 edge1 = triangle.B - triangle.A;
        Vector3 edge2 = triangle.C - triangle.A;
        Vector3 pvec = Vector3.Cross(direction, edge2);
        float determinant = Vector3.Dot(edge1, pvec);
        if (determinant > -IntersectionEpsilon && determinant < IntersectionEpsilon)
        {
            return false;
        }

        float inverseDeterminant = 1f / determinant;
        Vector3 tvec = origin - triangle.A;
        float u = Vector3.Dot(tvec, pvec) * inverseDeterminant;
        if (u < 0f || u > 1f)
        {
            return false;
        }

        Vector3 qvec = Vector3.Cross(tvec, edge1);
        float v = Vector3.Dot(direction, qvec) * inverseDeterminant;
        if (v < 0f || u + v > 1f)
        {
            return false;
        }

        float distance = Vector3.Dot(edge2, qvec) * inverseDeterminant;
        if (distance < 0f || distance > segmentDistance)
        {
            return false;
        }

        hitDistance = distance;
        return true;
    }
}

internal readonly struct SurfaceMeshTriangle
{
    internal SurfaceMeshTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 normal, int colliderId)
    {
        A = a;
        B = b;
        C = c;
        Normal = normal;
        ColliderId = colliderId;
        Min = Vector3.Min(a, Vector3.Min(b, c));
        Max = Vector3.Max(a, Vector3.Max(b, c));
    }

    internal Vector3 A { get; }

    internal Vector3 B { get; }

    internal Vector3 C { get; }

    internal Vector3 Normal { get; }

    internal int ColliderId { get; }

    internal Vector3 Min { get; }

    internal Vector3 Max { get; }
}

internal readonly struct MeshCellKey : IEquatable<MeshCellKey>
{
    internal MeshCellKey(int x, int y, int z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    internal int X { get; }

    internal int Y { get; }

    internal int Z { get; }

    internal static MeshCellKey From(Vector3 position, float cellSize)
    {
        float safeCellSize = Mathf.Max(0.25f, cellSize);
        return new MeshCellKey(
            Mathf.FloorToInt(position.x / safeCellSize),
            Mathf.FloorToInt(position.y / safeCellSize),
            Mathf.FloorToInt(position.z / safeCellSize));
    }

    public bool Equals(MeshCellKey other)
    {
        return X == other.X && Y == other.Y && Z == other.Z;
    }

    public override bool Equals(object? obj)
    {
        return obj is MeshCellKey other && Equals(other);
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
