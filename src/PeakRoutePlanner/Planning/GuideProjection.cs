using System.Collections.Generic;
using UnityEngine;

namespace PeakRoutePlanner.Planning;

internal sealed class GuideProjectionMap
{
    private const float DegenerateSegmentSqrLength = 0.0001f;

    private readonly IReadOnlyList<Vector3> guidePath;
    private readonly float[] cumulativeLengths;
    private readonly bool useThreeDimensionalProjection;

    private GuideProjectionMap(
        IReadOnlyList<Vector3> guidePath,
        float[] cumulativeLengths,
        bool useThreeDimensionalProjection)
    {
        this.guidePath = guidePath;
        this.cumulativeLengths = cumulativeLengths;
        this.useThreeDimensionalProjection = useThreeDimensionalProjection;
    }

    internal float TotalLength => cumulativeLengths.Length == 0
        ? 0f
        : cumulativeLengths[cumulativeLengths.Length - 1];

    internal static GuideProjectionMap Build(IReadOnlyList<Vector3> guidePath)
    {
        if (guidePath.Count < 2)
        {
            return new GuideProjectionMap(guidePath, [], useThreeDimensionalProjection: false);
        }

        float[] horizontalLengths = BuildCumulativeLengths(guidePath, useThreeDimensionalProjection: false);
        bool useThreeDimensionalProjection = horizontalLengths[horizontalLengths.Length - 1] < 0.001f;
        return new GuideProjectionMap(
            guidePath,
            useThreeDimensionalProjection
                ? BuildCumulativeLengths(guidePath, useThreeDimensionalProjection: true)
                : horizontalLengths,
            useThreeDimensionalProjection);
    }

    internal GuideProjectionPoint Project(Vector3 position)
    {
        if (guidePath.Count == 0)
        {
            return new GuideProjectionPoint(0f, 0f);
        }

        if (guidePath.Count == 1 || cumulativeLengths.Length < 2)
        {
            return new GuideProjectionPoint(0f, GetDistance(position, guidePath[0]));
        }

        float bestDistanceSqr = float.MaxValue;
        float bestProgress = 0f;
        for (int index = 0; index < guidePath.Count - 1; index++)
        {
            Vector3 from = guidePath[index];
            Vector3 to = guidePath[index + 1];
            Vector3 segment = GetProjectionVector(to - from);
            float segmentSqrLength = segment.sqrMagnitude;
            if (segmentSqrLength < DegenerateSegmentSqrLength)
            {
                continue;
            }

            Vector3 projectedOrigin = GetProjectionVector(from);
            Vector3 projectedPosition = GetProjectionVector(position);
            float t = Mathf.Clamp01(Vector3.Dot(projectedPosition - projectedOrigin, segment) / segmentSqrLength);
            Vector3 projected = projectedOrigin + segment * t;
            float distanceSqr = (projectedPosition - projected).sqrMagnitude;
            if (distanceSqr >= bestDistanceSqr)
            {
                continue;
            }

            bestDistanceSqr = distanceSqr;
            bestProgress = cumulativeLengths[index]
                + (cumulativeLengths[index + 1] - cumulativeLengths[index]) * t;
        }

        if (bestDistanceSqr == float.MaxValue)
        {
            return new GuideProjectionPoint(0f, GetDistance(position, guidePath[0]));
        }

        return new GuideProjectionPoint(bestProgress, Mathf.Sqrt(bestDistanceSqr));
    }

    internal GuidePointMetrics BuildPointMetrics(IReadOnlyList<SurfacePoint> points)
    {
        float[] progress = new float[points.Count];
        float[] distances = new float[points.Count];
        for (int index = 0; index < points.Count; index++)
        {
            GuideProjectionPoint projection = Project(points[index].Position);
            progress[index] = projection.Progress;
            distances[index] = projection.Distance;
        }

        return new GuidePointMetrics(progress, distances, TotalLength);
    }

    private static float[] BuildCumulativeLengths(IReadOnlyList<Vector3> guidePath, bool useThreeDimensionalProjection)
    {
        float[] lengths = new float[guidePath.Count];
        for (int index = 1; index < guidePath.Count; index++)
        {
            Vector3 delta = guidePath[index] - guidePath[index - 1];
            lengths[index] = lengths[index - 1] + GetLength(delta, useThreeDimensionalProjection);
        }

        return lengths;
    }

    private Vector3 GetProjectionVector(Vector3 value)
    {
        return useThreeDimensionalProjection
            ? value
            : new Vector3(value.x, 0f, value.z);
    }

    private static float GetLength(Vector3 value, bool useThreeDimensionalProjection)
    {
        if (useThreeDimensionalProjection)
        {
            return value.magnitude;
        }

        return Mathf.Sqrt(value.x * value.x + value.z * value.z);
    }

    private float GetDistance(Vector3 a, Vector3 b)
    {
        return GetLength(a - b, useThreeDimensionalProjection);
    }
}

internal readonly struct GuideProjectionPoint
{
    internal GuideProjectionPoint(float progress, float distance)
    {
        Progress = progress;
        Distance = distance;
    }

    internal float Progress { get; }

    internal float Distance { get; }
}

internal readonly struct GuidePointMetrics
{
    internal GuidePointMetrics(float[] progress, float[] distances, float totalLength)
    {
        Progress = progress;
        Distances = distances;
        TotalLength = totalLength;
    }

    internal float[] Progress { get; }

    internal float[] Distances { get; }

    internal float TotalLength { get; }
}
