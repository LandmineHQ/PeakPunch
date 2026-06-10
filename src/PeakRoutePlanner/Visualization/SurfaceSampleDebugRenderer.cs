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

    private readonly Dictionary<int, GameObject> sampleMarkersByPointId = [];
    private readonly List<GameObject> airCellMarkers = [];
    private readonly List<GameObject> airProbeLineObjects = [];
    private readonly List<LineRenderer> airProbeLineRenderers = [];

    private GameObject? routePreviewObject;
    private LineRenderer? routePreviewLine;
    private Material? standableMaterial;
    private Material? climbableMaterial;
    private Material? airCellMaterial;
    private Material? downProbeMaterial;
    private Material? sideProbeMaterial;
    private Material? upProbeMaterial;
    private Material? routePreviewMaterial;
    private int renderedPointScanCount;
    private int renderedAirCellCount;
    private int renderedAirProbeCount;
    private bool sampleMarkersVisible;
    private bool airCellsVisible;
    private bool airProbeLinesVisible;
    private bool routePreviewVisible;

    internal bool HasMarkers => ActiveMarkerCount > 0;

    private int ActiveMarkerCount
    {
        get
        {
            int count = 0;
            foreach (GameObject marker in sampleMarkersByPointId.Values)
            {
                if (marker != null && marker.activeSelf)
                {
                    count++;
                }
            }

            for (int index = 0; index < airCellMarkers.Count; index++)
            {
                GameObject marker = airCellMarkers[index];
                if (marker != null && marker.activeSelf)
                {
                    count++;
                }
            }

            for (int index = 0; index < airProbeLineObjects.Count; index++)
            {
                GameObject marker = airProbeLineObjects[index];
                if (marker != null && marker.activeSelf)
                {
                    count++;
                }
            }

            if (routePreviewObject != null && routePreviewObject.activeSelf)
            {
                count++;
            }

            return count;
        }
    }

    private int PooledMarkerCount => sampleMarkersByPointId.Count
        + airCellMarkers.Count
        + airProbeLineObjects.Count
        + (routePreviewObject != null ? 1 : 0);

    internal void Render(
        IReadOnlyList<SurfacePoint> points,
        IReadOnlyList<Vector3> airCellCenters,
        IReadOnlyList<DebugAirBoundaryProbe> airBoundaryProbes,
        IReadOnlyList<Vector3>? routePreviewPoints,
        SurfaceDebugRenderOptions options)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
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

        CountSurfacePoints(points, out int standableCount, out int climbableCount, out Vector3 boundsSize);
        int sampleMarkers = RenderSampleMarkers(points, standableCount + climbableCount, options.ShowSurfaceSamples);
        int renderedAirCells = RenderAirCells(airCellCenters, options.ShowAirCells);
        int renderedAirBoundaryProbes = RenderAirBoundaryProbes(airBoundaryProbes, options.ShowAirBoundaryProbes);
        int renderedRoutePreview = RenderRoutePreview(routePreviewPoints, options.ShowRoutePreview);

        stopwatch.Stop();
        Plugin.Log.LogInfo(
            $"Rendered surface sample debug markers: standable={standableCount}, climbable={climbableCount}, airCells={renderedAirCells}, airProbeLines={renderedAirBoundaryProbes}, routePreviewLines={renderedRoutePreview}, sampleMarkers={sampleMarkers}, activeMarkers={ActiveMarkerCount}, pooledMarkers={PooledMarkerCount}, sampledPoints={points.Count}, reachableAirCells={airCellCenters.Count}, queuedAirProbes={airBoundaryProbes.Count}, routePreviewPoints={routePreviewPoints?.Count ?? 0}, showSamples={options.ShowSurfaceSamples}, showAirCells={options.ShowAirCells}, showProbes={options.ShowAirBoundaryProbes}, showRoutePreview={options.ShowRoutePreview}, boundsSize=({boundsSize.x:0.0},{boundsSize.y:0.0},{boundsSize.z:0.0}), renderMs={stopwatch.Elapsed.TotalMilliseconds:0.00}.");
    }

    internal void Clear()
    {
        ClearMarkersOnly();
        DestroyMaterial(ref standableMaterial);
        DestroyMaterial(ref climbableMaterial);
        DestroyMaterial(ref airCellMaterial);
        DestroyMaterial(ref downProbeMaterial);
        DestroyMaterial(ref sideProbeMaterial);
        DestroyMaterial(ref upProbeMaterial);
        DestroyMaterial(ref routePreviewMaterial);
    }

    private void ClearMarkersOnly()
    {
        foreach (GameObject marker in sampleMarkersByPointId.Values)
        {
            if (marker != null)
            {
                Object.Destroy(marker);
            }
        }

        for (int index = 0; index < airCellMarkers.Count; index++)
        {
            if (airCellMarkers[index] != null)
            {
                Object.Destroy(airCellMarkers[index]);
            }
        }

        for (int index = 0; index < airProbeLineObjects.Count; index++)
        {
            if (airProbeLineObjects[index] != null)
            {
                Object.Destroy(airProbeLineObjects[index]);
            }
        }

        if (routePreviewObject != null)
        {
            Object.Destroy(routePreviewObject);
            routePreviewObject = null;
            routePreviewLine = null;
        }

        sampleMarkersByPointId.Clear();
        airCellMarkers.Clear();
        airProbeLineObjects.Clear();
        airProbeLineRenderers.Clear();
        renderedPointScanCount = 0;
        renderedAirCellCount = 0;
        renderedAirProbeCount = 0;
        sampleMarkersVisible = false;
        airCellsVisible = false;
        airProbeLinesVisible = false;
        routePreviewVisible = false;
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

    private int RenderSampleMarkers(IReadOnlyList<SurfacePoint> points, int eligibleCount, bool visible)
    {
        if (!visible)
        {
            if (sampleMarkersVisible)
            {
                SetSampleMarkersActive(false);
            }

            return 0;
        }

        if (!sampleMarkersVisible)
        {
            SetSampleMarkersActive(true);
        }

        if (points.Count < renderedPointScanCount)
        {
            ClearSampleMarkersOnly();
        }

        for (int index = renderedPointScanCount; index < points.Count; index++)
        {
            SurfacePoint point = points[index];
            if (point.Kind != SurfaceKind.Standable && point.Kind != SurfaceKind.Climbable)
            {
                continue;
            }

            Material? material = point.Kind == SurfaceKind.Standable ? standableMaterial : climbableMaterial;
            if (material == null)
            {
                continue;
            }

            GameObject marker = CreateSampleMarker(point, material);
            sampleMarkersByPointId[point.Id] = marker;
        }

        renderedPointScanCount = points.Count;
        sampleMarkersVisible = true;
        return eligibleCount;
    }

    private int RenderAirCells(IReadOnlyList<Vector3> airCellCenters, bool visible)
    {
        if (!visible)
        {
            if (airCellsVisible)
            {
                SetListActive(airCellMarkers, false);
                airCellsVisible = false;
            }

            return 0;
        }

        if (!airCellsVisible)
        {
            SetListActive(airCellMarkers, true, renderedAirCellCount);
        }

        if (airCellCenters.Count < renderedAirCellCount)
        {
            for (int index = airCellCenters.Count; index < renderedAirCellCount && index < airCellMarkers.Count; index++)
            {
                SetActive(airCellMarkers[index], false);
            }

            renderedAirCellCount = airCellCenters.Count;
        }

        for (int index = renderedAirCellCount; index < airCellCenters.Count; index++)
        {
            GameObject marker;
            if (index < airCellMarkers.Count)
            {
                marker = airCellMarkers[index];
            }
            else
            {
                marker = CreateAirCellMarker();
                airCellMarkers.Add(marker);
            }

            marker.transform.position = airCellCenters[index];
            marker.transform.localScale = Vector3.one * AirCellScale;
            SetActive(marker, true);
        }

        for (int index = airCellCenters.Count; index < airCellMarkers.Count; index++)
        {
            SetActive(airCellMarkers[index], false);
        }

        renderedAirCellCount = airCellCenters.Count;
        airCellsVisible = true;
        return airCellCenters.Count;
    }

    private int RenderAirBoundaryProbes(IReadOnlyList<DebugAirBoundaryProbe> airBoundaryProbes, bool visible)
    {
        if (!visible)
        {
            if (airProbeLinesVisible)
            {
                SetListActive(airProbeLineObjects, false);
                airProbeLinesVisible = false;
            }

            return 0;
        }

        if (!airProbeLinesVisible)
        {
            SetListActive(airProbeLineObjects, true, renderedAirProbeCount);
        }

        if (airBoundaryProbes.Count < renderedAirProbeCount)
        {
            for (int index = airBoundaryProbes.Count; index < renderedAirProbeCount && index < airProbeLineObjects.Count; index++)
            {
                SetActive(airProbeLineObjects[index], false);
            }

            renderedAirProbeCount = airBoundaryProbes.Count;
        }

        for (int index = renderedAirProbeCount; index < airBoundaryProbes.Count; index++)
        {
            GameObject marker;
            LineRenderer line;
            if (index < airProbeLineObjects.Count)
            {
                marker = airProbeLineObjects[index];
                line = airProbeLineRenderers[index];
            }
            else
            {
                marker = CreateAirProbeLine(out line);
                airProbeLineObjects.Add(marker);
                airProbeLineRenderers.Add(line);
            }

            DebugAirBoundaryProbe probe = airBoundaryProbes[index];
            line.positionCount = 2;
            line.SetPosition(0, probe.Origin);
            line.SetPosition(1, probe.Origin + probe.Direction * probe.Distance);
            Material? probeMaterial = GetProbeMaterial(probe.Direction);
            if (probeMaterial != null)
            {
                line.sharedMaterial = probeMaterial;
            }

            SetActive(marker, true);
        }

        for (int index = airBoundaryProbes.Count; index < airProbeLineObjects.Count; index++)
        {
            SetActive(airProbeLineObjects[index], false);
        }

        renderedAirProbeCount = airBoundaryProbes.Count;
        airProbeLinesVisible = true;
        return airBoundaryProbes.Count;
    }

    private int RenderRoutePreview(IReadOnlyList<Vector3>? routePreviewPoints, bool visible)
    {
        if (!visible || routePreviewPoints == null || routePreviewPoints.Count < 2)
        {
            if (routePreviewVisible)
            {
                SetActive(routePreviewObject, false);
                routePreviewVisible = false;
            }

            return 0;
        }

        EnsureRoutePreviewLine();
        if (routePreviewObject == null || routePreviewLine == null)
        {
            return 0;
        }

        routePreviewLine.positionCount = routePreviewPoints.Count;
        for (int index = 0; index < routePreviewPoints.Count; index++)
        {
            routePreviewLine.SetPosition(index, routePreviewPoints[index]);
        }

        SetActive(routePreviewObject, true);
        routePreviewVisible = true;
        return 1;
    }

    private void CountSurfacePoints(
        IReadOnlyList<SurfacePoint> points,
        out int standableCount,
        out int climbableCount,
        out Vector3 boundsSize)
    {
        standableCount = 0;
        climbableCount = 0;
        bool hasBounds = false;
        Vector3 min = default;
        Vector3 max = default;
        for (int index = 0; index < points.Count; index++)
        {
            SurfacePoint point = points[index];
            if (point.Kind == SurfaceKind.Standable)
            {
                standableCount++;
            }
            else if (point.Kind == SurfaceKind.Climbable)
            {
                climbableCount++;
            }
            else
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

        boundsSize = hasBounds ? max - min : Vector3.zero;
    }

    private GameObject CreateSampleMarker(SurfacePoint point, Material material)
    {
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

        return marker;
    }

    private GameObject CreateAirCellMarker()
    {
        GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
        marker.name = "PeakRoutePlanner Reachable Air Cell";

        Collider? collider = marker.GetComponent<Collider>();
        if (collider != null)
        {
            Object.Destroy(collider);
        }

        MeshRenderer? renderer = marker.GetComponent<MeshRenderer>();
        if (renderer != null)
        {
            if (airCellMaterial != null)
            {
                renderer.sharedMaterial = airCellMaterial;
            }

            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        return marker;
    }

    private GameObject CreateAirProbeLine(out LineRenderer line)
    {
        GameObject marker = new("PeakRoutePlanner Air Boundary Probe");
        line = marker.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = 2;
        line.startWidth = ProbeLineWidth;
        line.endWidth = ProbeLineWidth;
        line.numCapVertices = 2;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        return marker;
    }

    private void EnsureRoutePreviewLine()
    {
        if (routePreviewLine != null)
        {
            return;
        }

        routePreviewObject = new GameObject("PeakRoutePlanner Route Preview");
        routePreviewLine = routePreviewObject.AddComponent<LineRenderer>();
        routePreviewLine.useWorldSpace = true;
        routePreviewLine.startWidth = RouteLineWidth;
        routePreviewLine.endWidth = RouteLineWidth;
        routePreviewLine.numCapVertices = 3;
        routePreviewLine.shadowCastingMode = ShadowCastingMode.Off;
        routePreviewLine.receiveShadows = false;
        if (routePreviewMaterial != null)
        {
            routePreviewLine.sharedMaterial = routePreviewMaterial;
        }
    }

    private void ClearSampleMarkersOnly()
    {
        foreach (GameObject marker in sampleMarkersByPointId.Values)
        {
            if (marker != null)
            {
                Object.Destroy(marker);
            }
        }

        sampleMarkersByPointId.Clear();
        renderedPointScanCount = 0;
        sampleMarkersVisible = false;
    }

    private void SetSampleMarkersActive(bool active)
    {
        foreach (GameObject marker in sampleMarkersByPointId.Values)
        {
            SetActive(marker, active);
        }

        sampleMarkersVisible = active;
    }

    private static void SetListActive(IReadOnlyList<GameObject> markers, bool active)
    {
        for (int index = 0; index < markers.Count; index++)
        {
            SetActive(markers[index], active);
        }
    }

    private static void SetListActive(IReadOnlyList<GameObject> markers, bool active, int count)
    {
        int limit = Mathf.Min(count, markers.Count);
        for (int index = 0; index < limit; index++)
        {
            SetActive(markers[index], active);
        }
    }

    private static void SetActive(GameObject? marker, bool active)
    {
        if (marker != null && marker.activeSelf != active)
        {
            marker.SetActive(active);
        }
    }

    private Material? GetProbeMaterial(Vector3 direction)
    {
        if (direction.y < -0.5f)
        {
            return downProbeMaterial;
        }

        return direction.y > 0.5f ? upProbeMaterial : sideProbeMaterial;
    }

    private static void DestroyMaterial(ref Material? material)
    {
        if (material == null)
        {
            return;
        }

        Object.Destroy(material);
        material = null;
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
