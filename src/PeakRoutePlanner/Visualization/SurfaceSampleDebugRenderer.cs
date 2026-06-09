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
    private const float MarkerScale = 0.18f;
    private const float AirCellScale = 0.92f;

    private readonly List<GameObject> markers = [];
    private Material? standableMaterial;
    private Material? climbableMaterial;
    private Material? airCellMaterial;

    internal bool HasMarkers => markers.Count > 0;

    internal void Render(IReadOnlyList<SurfacePoint> points, IReadOnlyList<Vector3> airCellCenters)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        ClearMarkersOnly();
        EnsureMaterials();
        if (standableMaterial == null || climbableMaterial == null || airCellMaterial == null)
        {
            return;
        }

        int renderedAirCells = RenderAirCells(airCellCenters);
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
            marker.transform.position = point.Position + point.Normal.normalized * 0.08f;
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
        Plugin.Log.LogInfo($"Rendered surface sample debug markers: standable={standableCount}, climbable={climbableCount}, airCells={renderedAirCells}, sampleMarkers={sampleMarkers}, totalMarkers={markers.Count}, sampledPoints={points.Count}, reachableAirCells={airCellCenters.Count}, boundsSize=({boundsSize.x:0.0},{boundsSize.y:0.0},{boundsSize.z:0.0}), renderMs={stopwatch.Elapsed.TotalMilliseconds:0.00}.");
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
        if (standableMaterial != null && climbableMaterial != null && airCellMaterial != null)
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
