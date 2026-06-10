using System;

namespace PeakRoutePlanner.Visualization;

internal readonly struct SurfaceDebugRenderOptions : IEquatable<SurfaceDebugRenderOptions>
{
    internal SurfaceDebugRenderOptions(
        bool showSurfaceSamples,
        bool showAirCells,
        bool showAirBoundaryProbes,
        bool showRoutePreview)
    {
        ShowSurfaceSamples = showSurfaceSamples;
        ShowAirCells = showAirCells;
        ShowAirBoundaryProbes = showAirBoundaryProbes;
        ShowRoutePreview = showRoutePreview;
    }

    internal bool ShowSurfaceSamples { get; }

    internal bool ShowAirCells { get; }

    internal bool ShowAirBoundaryProbes { get; }

    internal bool ShowRoutePreview { get; }

    internal static SurfaceDebugRenderOptions ForceDebug { get; } = new(
        showSurfaceSamples: true,
        showAirCells: true,
        showAirBoundaryProbes: true,
        showRoutePreview: false);

    public bool Equals(SurfaceDebugRenderOptions other)
    {
        return ShowSurfaceSamples == other.ShowSurfaceSamples
            && ShowAirCells == other.ShowAirCells
            && ShowAirBoundaryProbes == other.ShowAirBoundaryProbes
            && ShowRoutePreview == other.ShowRoutePreview;
    }

    public override bool Equals(object? obj)
    {
        return obj is SurfaceDebugRenderOptions other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + (ShowSurfaceSamples ? 1 : 0);
            hash = hash * 31 + (ShowAirCells ? 1 : 0);
            hash = hash * 31 + (ShowAirBoundaryProbes ? 1 : 0);
            hash = hash * 31 + (ShowRoutePreview ? 1 : 0);
            return hash;
        }
    }

    public static bool operator ==(SurfaceDebugRenderOptions left, SurfaceDebugRenderOptions right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(SurfaceDebugRenderOptions left, SurfaceDebugRenderOptions right)
    {
        return !left.Equals(right);
    }
}
