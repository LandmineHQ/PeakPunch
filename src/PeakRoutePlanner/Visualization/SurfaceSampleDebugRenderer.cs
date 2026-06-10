using System.Collections.Generic;
using System.Diagnostics;
using PeakRoutePlanner.Planning;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace PeakRoutePlanner.Visualization;

internal sealed class SurfaceSampleDebugRenderer
{
    private static readonly Color StandableColor = new(0.1f, 1f, 0.2f, 0.85f);
    private static readonly Color ClimbableColor = new(1f, 0.12f, 0.08f, 0.85f);
    private static readonly Color AirCellColor = new(0.15f, 0.55f, 1f, 0.18f);
    private static readonly Color DownProbeColor = new(1f, 0.92f, 0.08f, 0.85f);
    private static readonly Color SideProbeColor = new(0.05f, 0.95f, 1f, 0.75f);
    private static readonly Color UpProbeColor = new(0.9f, 0.25f, 1f, 0.75f);
    private static readonly Color RoutePreviewColor = new(1f, 0.85f, 0.05f, 0.95f);
    private const float MarkerScale = 0.18f;
    private const float AirCellScale = 0.92f;
    private const float ProbeLineWidth = 0.035f;
    private const float RouteLineWidth = 0.09f;

    private readonly List<GameObject> markers = [];
    private Material? standableMaterial;
    private Material? climbableMaterial;
    private Material? airCellMaterial;
    private Material? downProbeMaterial;
    private Material? sideProbeMaterial;
    private Material? upProbeMaterial;
    private Material? routePreviewMaterial;

    internal bool HasMarkers => markers.Count > 0;

    internal void Render(
        IReadOnlyList<SurfacePoint> points,
        IReadOnlyList<Vector3> airCellCenters,
        IReadOnlyList<DebugAirBoundaryProbe> airBoundaryProbes,
        IReadOnlyList<Vector3>? routePreviewPoints = null)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        ClearMarkersOnly();
        EnsureMaterials();
        if (standableMaterial == null
            || climbableMaterial == null
            || airCellMaterial == null
            || downProbeMaterial == null
            || sideProbeMaterial == null
            || upProbeMaterial == null
            || routePreviewMaterial == null)
        {
            return;
        }

        int renderedAirCells = RenderAirCells(airCellCenters);
        int renderedAirBoundaryProbes = RenderAirBoundaryProbes(airBoundaryProbes);
        int renderedRoutePreview = RenderRoutePreview(routePreviewPoints);
        int standableCount = 0;
        int climbableCount = 0;
        bool hasBounds = false;
        Vector3 min = default;
        Vector3 max = default;
        for (int index = 0; index < points.Count; index++)
        {
            SurfacePoint point = points[index];
            if (point.Kind != SurfaceKind.Standable && point.Kind != SurfaceKind.Climbable)
            {
                continue;
            }

            if (!hasBounds)
            {
                min = point.Position;
                max = point.Position;
                hasBounds = true;
                continue;
            }

            min = Vector3.Min(min, point.Position);
            max = Vector3.Max(max, point.Position);
        }

        int sampleMarkers = 0;
        for (int index = 0; index < points.Count; index++)
        {
            SurfacePoint point = points[index];
            Material material;
            if (point.Kind == SurfaceKind.Standable)
            {
                material = standableMaterial;
                standableCount++;
            }
            else if (point.Kind == SurfaceKind.Climbable)
            {
                material = climbableMaterial;
                climbableCount++;
            }
            else
            {
                continue;
            }

            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = $"PeakRoutePlanner Sample {point.Kind}";
            marker.transform.position = point.Position;
            marker.transform.localScale = Vector3.one * MarkerScale;

            Collider? collider = marker.GetComponent<Collider>();
            if (collider != null)
            {
                Object.Destroy(collider);
            }

            MeshRenderer? renderer = marker.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            markers.Add(marker);
            sampleMarkers++;
        }

        stopwatch.Stop();
        Vector3 boundsSize = hasBounds ? max - min : Vector3.zero;
        Plugin.Log.LogInfo($"Rendered surface sample debug markers: standable={standableCount}, climbable={climbableCount}, airCells={renderedAirCells}, airProbeLines={renderedAirBoundaryProbes}, routePreviewLines={renderedRoutePreview}, sampleMarkers={sampleMarkers}, totalMarkers={markers.Count}, sampledPoints={points.Count}, reachableAirCells={airCellCenters.Count}, queuedAirProbes={airBoundaryProbes.Count}, routePreviewPoints={routePreviewPoints?.Count ?? 0}, boundsSize=({boundsSize.x:0.0},{boundsSize.y:0.0},{boundsSize.z:0.0}), renderMs={stopwatch.Elapsed.TotalMilliseconds:0.00}.");
    }

    internal void Clear()
    {
        ClearMarkersOnly();
        if (standableMaterial != null)
        {
            Object.Destroy(standableMaterial);
            standableMaterial = null;
        }

        if (climbableMaterial != null)
        {
            Object.Destroy(climbableMaterial);
            climbableMaterial = null;
        }

        if (airCellMaterial != null)
        {
            Object.Destroy(airCellMaterial);
            airCellMaterial = null;
        }

        if (downProbeMaterial != null)
        {
            Object.Destroy(downProbeMaterial);
            downProbeMaterial = null;
        }

        if (sideProbeMaterial != null)
        {
            Object.Destroy(sideProbeMaterial);
            sideProbeMaterial = null;
        }

        if (upProbeMaterial != null)
        {
            Object.Destroy(upProbeMaterial);
            upProbeMaterial = null;
        }

        if (routePreviewMaterial != null)
        {
            Object.Destroy(routePreviewMaterial);
            routePreviewMaterial = null;
        }
    }

    private void ClearMarkersOnly()
    {
        for (int index = 0; index < markers.Count; index++)
        {
            if (markers[index] != null)
            {
                Object.Destroy(markers[index]);
            }
        }

        markers.Clear();
    }

    private void EnsureMaterials()
    {
        if (standableMaterial != null
            && climbableMaterial != null
            && airCellMaterial != null
            && downProbeMaterial != null
            && sideProbeMaterial != null
            && upProbeMaterial != null
            && routePreviewMaterial != null)
        {
            return;
        }

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit")
            ?? Shader.Find("Sprites/Default")
            ?? Shader.Find("Unlit/Color")
            ?? Shader.Find("Standard");
        if (shader == null)
        {
            Plugin.Log.LogWarning("Unable to create surface sample debug markers because no compatible shader was found.");
            return;
        }

        standableMaterial ??= CreateMaterial(shader, "PeakRoutePlanner Standable Sample", StandableColor);
        climbableMaterial ??= CreateMaterial(shader, "PeakRoutePlanner Climbable Sample", ClimbableColor);
        airCellMaterial ??= CreateMaterial(shader, "PeakRoutePlanner Reachable Air Cell", AirCellColor);
        downProbeMaterial ??= CreateMaterial(shader, "PeakRoutePlanner Down Air Boundary Probe", DownProbeColor);
        sideProbeMaterial ??= CreateMaterial(shader, "PeakRoutePlanner Side Air Boundary Probe", SideProbeColor);
        upProbeMaterial ??= CreateMaterial(shader, "PeakRoutePlanner Up Air Boundary Probe", UpProbeColor);
        routePreviewMaterial ??= CreateMaterial(shader, "PeakRoutePlanner Route Preview", RoutePreviewColor);
    }

    private int RenderAirCells(IReadOnlyList<Vector3> airCellCenters)
    {
        if (airCellMaterial == null || airCellCenters.Count == 0)
        {
            return 0;
        }

        int rendered = 0;
        for (int index = 0; index < airCellCenters.Count; index++)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            marker.name = "PeakRoutePlanner Reachable Air Cell";
            marker.transform.position = airCellCenters[index];
            marker.transform.localScale = Vector3.one * AirCellScale;

            Collider? collider = marker.GetComponent<Collider>();
            if (collider != null)
            {
                Object.Destroy(collider);
            }

            MeshRenderer? renderer = marker.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = airCellMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            markers.Add(marker);
            rendered++;
        }

        return rendered;
    }

    private int RenderAirBoundaryProbes(IReadOnlyList<DebugAirBoundaryProbe> airBoundaryProbes)
    {
        if (airBoundaryProbes.Count == 0)
        {
            return 0;
        }

        int rendered = 0;
        for (int index = 0; index < airBoundaryProbes.Count; index++)
        {
            DebugAirBoundaryProbe probe = airBoundaryProbes[index];
            GameObject marker = new("PeakRoutePlanner Air Boundary Probe");
            LineRenderer line = marker.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.SetPosition(0, probe.Origin);
            line.SetPosition(1, probe.Origin + probe.Direction * probe.Distance);
            line.startWidth = ProbeLineWidth;
            line.endWidth = ProbeLineWidth;
            line.numCapVertices = 2;
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.sharedMaterial = GetProbeMaterial(probe.Direction);

            markers.Add(marker);
            rendered++;
        }

        return rendered;
    }

    private int RenderRoutePreview(IReadOnlyList<Vector3>? routePreviewPoints)
    {
        if (routePreviewMaterial == null || routePreviewPoints == null || routePreviewPoints.Count < 2)
        {
            return 0;
        }

        GameObject marker = new("PeakRoutePlanner Route Preview");
        LineRenderer line = marker.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = routePreviewPoints.Count;
        for (int index = 0; index < routePreviewPoints.Count; index++)
        {
            line.SetPosition(index, routePreviewPoints[index]);
        }

        line.startWidth = RouteLineWidth;
        line.endWidth = RouteLineWidth;
        line.numCapVertices = 3;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.sharedMaterial = routePreviewMaterial;
        markers.Add(marker);
        return 1;
    }

    private Material? GetProbeMaterial(Vector3 direction)
    {
        if (direction.y < -0.5f)
        {
            return downProbeMaterial;
        }

        return direction.y > 0.5f ? upProbeMaterial : sideProbeMaterial;
    }

    private static Material CreateMaterial(Shader shader, string name, Color color)
    {
        Material material = new(shader)
        {
            name = name,
            color = color,
        };
        if (material.HasProperty("_BaseColor"))
        {
            material.SetColor("_BaseColor", color);
        }

        if (material.HasProperty("_Surface"))
        {
            material.SetFloat("_Surface", 1f);
        }

        if (material.HasProperty("_Blend"))
        {
            material.SetFloat("_Blend", 0f);
        }

        material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        material.SetInt("_ZWrite", 0);
        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.EnableKeyword("_ALPHABLEND_ON");
        material.renderQueue = (int)RenderQueue.Transparent;
        return material;
    }
}
