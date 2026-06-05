using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace PeakRoutePlanner.Visualization;

internal sealed class RoutePathRenderer
{
    private static readonly Color PreviewColor = new(1f, 0.78f, 0.2f, 0.7f);
    private static readonly Color TargetPreviewColor = new(0.45f, 1f, 0.45f, 0.55f);
    private static readonly Color SamplingWindowColor = new(1f, 1f, 1f, 0.18f);
    private static readonly Color FinalColor = new(0.15f, 0.95f, 1f, 0.85f);

    private GameObject? routeObject;
    private LineRenderer? lineRenderer;
    private Material? lineMaterial;
    private int lastPathHash;
    private bool lastPathWasFinal;
    private int lastPathCount;
    private GameObject? targetRouteObject;
    private LineRenderer? targetLineRenderer;
    private Material? targetLineMaterial;
    private int lastTargetPathHash;
    private int lastTargetPathCount;
    private GameObject? samplingWindowObject;
    private MeshRenderer? samplingWindowRenderer;
    private Material? samplingWindowMaterial;
    private int lastSamplingWindowHash;
    private bool samplingWindowVisible;

    internal bool Render(IReadOnlyList<Vector3> path, bool isFinalPath)
    {
        if (path.Count < 2)
        {
            ClearMain();
            return false;
        }

        if (isFinalPath)
        {
            ClearTargetPreview();
        }

        int pathHash = GetPathHash(path);
        if (lineRenderer != null
            && lastPathCount == path.Count
            && lastPathHash == pathHash
            && lastPathWasFinal == isFinalPath)
        {
            return false;
        }

        EnsureRenderer();
        if (lineRenderer == null)
        {
            return false;
        }

        SetColor(isFinalPath ? FinalColor : PreviewColor);
        lineRenderer.positionCount = path.Count;
        for (int index = 0; index < path.Count; index++)
        {
            lineRenderer.SetPosition(index, path[index] + Vector3.up * 0.08f);
        }

        Plugin.Log.LogInfo($"Route LineRenderer updated: points={path.Count}, final={isFinalPath}.");
        lastPathCount = path.Count;
        lastPathHash = pathHash;
        lastPathWasFinal = isFinalPath;
        return true;
    }

    internal bool RenderTargetPreview(IReadOnlyList<Vector3> path)
    {
        if (path.Count < 2)
        {
            ClearTargetPreview();
            return false;
        }

        int pathHash = GetPathHash(path);
        if (targetLineRenderer != null
            && lastTargetPathCount == path.Count
            && lastTargetPathHash == pathHash)
        {
            return false;
        }

        EnsureTargetRenderer();
        if (targetLineRenderer == null)
        {
            return false;
        }

        SetTargetColor(TargetPreviewColor);
        targetLineRenderer.positionCount = path.Count;
        for (int index = 0; index < path.Count; index++)
        {
            targetLineRenderer.SetPosition(index, path[index] + Vector3.up * 0.08f);
        }

        Plugin.Log.LogInfo($"Route target-side LineRenderer updated: points={path.Count}.");
        lastTargetPathCount = path.Count;
        lastTargetPathHash = pathHash;
        return true;
    }

    internal bool RenderSamplingWindowPreview(Vector3 center, Vector3 size)
    {
        if (size.x <= 0.001f || size.y <= 0.001f || size.z <= 0.001f)
        {
            ClearSamplingWindowPreview();
            return false;
        }

        int windowHash = GetWindowHash(center, size);
        if (samplingWindowRenderer != null
            && samplingWindowVisible
            && lastSamplingWindowHash == windowHash)
        {
            return false;
        }

        EnsureSamplingWindowRenderer();
        if (samplingWindowObject == null || samplingWindowRenderer == null)
        {
            return false;
        }

        samplingWindowObject.transform.position = center;
        samplingWindowObject.transform.localScale = size;
        samplingWindowRenderer.enabled = true;
        samplingWindowVisible = true;
        lastSamplingWindowHash = windowHash;
        Plugin.Log.LogInfo($"Route sampling-window cube updated: center=({center.x:0.0},{center.y:0.0},{center.z:0.0}), size=({size.x:0.0},{size.y:0.0},{size.z:0.0}).");
        return true;
    }

    internal void CreateSamplingWindowPreview()
    {
        EnsureSamplingWindowRenderer();
    }

    internal void DestroySamplingWindowPreview()
    {
        if (samplingWindowObject != null)
        {
            Object.Destroy(samplingWindowObject);
            samplingWindowObject = null;
            samplingWindowRenderer = null;
        }

        if (samplingWindowMaterial != null)
        {
            Object.Destroy(samplingWindowMaterial);
            samplingWindowMaterial = null;
        }

        samplingWindowVisible = false;
        lastSamplingWindowHash = 0;
    }

    internal void Clear()
    {
        ClearMain();
        ClearTargetPreview();
        ClearSamplingWindowPreview();
    }

    private void ClearMain()
    {
        if (lineRenderer != null)
        {
            lineRenderer.positionCount = 0;
        }

        lastPathCount = 0;
        lastPathHash = 0;
        lastPathWasFinal = false;
    }

    private void ClearTargetPreview()
    {
        if (targetLineRenderer != null)
        {
            targetLineRenderer.positionCount = 0;
        }

        lastTargetPathCount = 0;
        lastTargetPathHash = 0;
    }

    private void ClearSamplingWindowPreview()
    {
        if (samplingWindowRenderer != null)
        {
            samplingWindowRenderer.enabled = false;
        }

        samplingWindowVisible = false;
        lastSamplingWindowHash = 0;
    }

    internal void Cleanup()
    {
        if (routeObject != null)
        {
            Object.Destroy(routeObject);
            routeObject = null;
            lineRenderer = null;
        }

        if (targetRouteObject != null)
        {
            Object.Destroy(targetRouteObject);
            targetRouteObject = null;
            targetLineRenderer = null;
        }

        DestroySamplingWindowPreview();

        if (lineMaterial != null)
        {
            Object.Destroy(lineMaterial);
            lineMaterial = null;
        }

        if (targetLineMaterial != null)
        {
            Object.Destroy(targetLineMaterial);
            targetLineMaterial = null;
        }

    }

    private void EnsureRenderer()
    {
        if (lineRenderer != null)
        {
            return;
        }

        routeObject = new GameObject("PeakRoutePlanner Route Path");
        lineRenderer = routeObject.AddComponent<LineRenderer>();
        lineRenderer.useWorldSpace = true;
        lineRenderer.widthMultiplier = 0.14f;
        lineRenderer.numCornerVertices = 2;
        lineRenderer.numCapVertices = 2;

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit")
            ?? Shader.Find("Sprites/Default")
            ?? Shader.Find("Unlit/Color")
            ?? Shader.Find("Standard");
        if (shader == null)
        {
            Plugin.Log.LogWarning("Unable to create PeakRoutePlanner route line because no compatible line shader was found.");
            Object.Destroy(routeObject);
            routeObject = null;
            lineRenderer = null;
            return;
        }

        lineMaterial = new Material(shader)
        {
            name = "PeakRoutePlanner Route Path",
            color = FinalColor,
        };
        lineRenderer.sharedMaterial = lineMaterial;
        SetColor(FinalColor);
        Plugin.Log.LogInfo($"Created PeakRoutePlanner Route LineRenderer with shader={shader.name}.");
    }

    private void EnsureTargetRenderer()
    {
        if (targetLineRenderer != null)
        {
            return;
        }

        targetRouteObject = new GameObject("PeakRoutePlanner Target Frontier Path");
        targetLineRenderer = targetRouteObject.AddComponent<LineRenderer>();
        targetLineRenderer.useWorldSpace = true;
        targetLineRenderer.widthMultiplier = 0.1f;
        targetLineRenderer.numCornerVertices = 2;
        targetLineRenderer.numCapVertices = 2;

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit")
            ?? Shader.Find("Sprites/Default")
            ?? Shader.Find("Unlit/Color")
            ?? Shader.Find("Standard");
        if (shader == null)
        {
            Plugin.Log.LogWarning("Unable to create PeakRoutePlanner target-side line because no compatible line shader was found.");
            Object.Destroy(targetRouteObject);
            targetRouteObject = null;
            targetLineRenderer = null;
            return;
        }

        targetLineMaterial = new Material(shader)
        {
            name = "PeakRoutePlanner Target Frontier Path",
            color = TargetPreviewColor,
        };
        targetLineRenderer.sharedMaterial = targetLineMaterial;
        SetTargetColor(TargetPreviewColor);
        Plugin.Log.LogInfo($"Created PeakRoutePlanner target-side LineRenderer with shader={shader.name}.");
    }

    private void EnsureSamplingWindowRenderer()
    {
        if (samplingWindowRenderer != null)
        {
            return;
        }

        Shader shader = Shader.Find("Universal Render Pipeline/Unlit")
            ?? Shader.Find("Sprites/Default")
            ?? Shader.Find("Unlit/Color")
            ?? Shader.Find("Standard");
        if (shader == null)
        {
            Plugin.Log.LogWarning("Unable to create PeakRoutePlanner sampling window cube because no compatible shader was found.");
            return;
        }

        samplingWindowObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
        samplingWindowObject.name = "PeakRoutePlanner Sampling Window";
        Collider? collider = samplingWindowObject.GetComponent<Collider>();
        if (collider != null)
        {
            Object.Destroy(collider);
        }

        samplingWindowRenderer = samplingWindowObject.GetComponent<MeshRenderer>();
        if (samplingWindowRenderer == null)
        {
            Object.Destroy(samplingWindowObject);
            samplingWindowObject = null;
            return;
        }

        samplingWindowMaterial = new Material(shader)
        {
            name = "PeakRoutePlanner Sampling Window",
            color = SamplingWindowColor,
        };
        ConfigureTransparentMaterial(samplingWindowMaterial);
        samplingWindowRenderer.sharedMaterial = samplingWindowMaterial;
        samplingWindowRenderer.shadowCastingMode = ShadowCastingMode.Off;
        samplingWindowRenderer.receiveShadows = false;
        samplingWindowRenderer.enabled = false;
        Plugin.Log.LogInfo($"Created PeakRoutePlanner sampling window cube with shader={shader.name}.");
    }

    private void SetColor(Color color)
    {
        if (lineMaterial != null)
        {
            lineMaterial.color = color;
        }

        if (lineRenderer != null)
        {
            lineRenderer.startColor = color;
            lineRenderer.endColor = new Color(color.r, color.g, color.b, Mathf.Min(1f, color.a + 0.15f));
        }
    }

    private void SetTargetColor(Color color)
    {
        if (targetLineMaterial != null)
        {
            targetLineMaterial.color = color;
        }

        if (targetLineRenderer != null)
        {
            targetLineRenderer.startColor = color;
            targetLineRenderer.endColor = new Color(color.r, color.g, color.b, Mathf.Min(1f, color.a + 0.15f));
        }
    }

    private static void ConfigureTransparentMaterial(Material material)
    {
        material.color = SamplingWindowColor;
        material.renderQueue = (int)RenderQueue.Transparent;
        if (material.HasProperty("_Surface"))
        {
            material.SetFloat("_Surface", 1f);
        }

        if (material.HasProperty("_Blend"))
        {
            material.SetFloat("_Blend", 0f);
        }

        if (material.HasProperty("_SrcBlend"))
        {
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        }

        if (material.HasProperty("_DstBlend"))
        {
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        }

        if (material.HasProperty("_ZWrite"))
        {
            material.SetInt("_ZWrite", 0);
        }

        material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        material.EnableKeyword("_ALPHABLEND_ON");
    }

    private static int GetPathHash(IReadOnlyList<Vector3> path)
    {
        unchecked
        {
            int hash = 17;
            for (int index = 0; index < path.Count; index++)
            {
                Vector3 point = path[index];
                hash = hash * 31 + Mathf.RoundToInt(point.x * 20f);
                hash = hash * 31 + Mathf.RoundToInt(point.y * 20f);
                hash = hash * 31 + Mathf.RoundToInt(point.z * 20f);
            }

            return hash;
        }
    }

    private static int GetWindowHash(Vector3 center, Vector3 size)
    {
        unchecked
        {
            int hash = 23;
            hash = hash * 31 + Mathf.RoundToInt(center.x * 10f);
            hash = hash * 31 + Mathf.RoundToInt(center.y * 10f);
            hash = hash * 31 + Mathf.RoundToInt(center.z * 10f);
            hash = hash * 31 + Mathf.RoundToInt(size.x * 10f);
            hash = hash * 31 + Mathf.RoundToInt(size.y * 10f);
            hash = hash * 31 + Mathf.RoundToInt(size.z * 10f);
            return hash;
        }
    }
}
