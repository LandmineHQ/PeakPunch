using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace PeakRoutePlanner.Visualization;

internal sealed class SamplingWindowRenderer
{
    private static readonly Color SamplingWindowColor = new(1f, 1f, 1f, 0.18f);

    private GameObject? samplingWindowObject;
    private MeshRenderer? samplingWindowRenderer;
    private Material? samplingWindowMaterial;
    private int lastSamplingWindowHash;
    private bool samplingWindowVisible;

    internal bool RenderSamplingWindowPreview(Vector3 center, Vector3 size, Quaternion rotation)
    {
        if (size.x <= 0.001f || size.y <= 0.001f || size.z <= 0.001f)
        {
            Clear();
            return false;
        }

        int windowHash = GetWindowHash(center, size, rotation);
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
        samplingWindowObject.transform.rotation = rotation;
        samplingWindowObject.transform.localScale = size;
        samplingWindowRenderer.enabled = true;
        samplingWindowVisible = true;
        lastSamplingWindowHash = windowHash;
        Plugin.Log.LogInfo($"Sampling window updated: center=({center.x:0.0},{center.y:0.0},{center.z:0.0}), size=({size.x:0.0},{size.y:0.0},{size.z:0.0}).");
        return true;
    }

    internal void CreateSamplingWindowPreview()
    {
        EnsureSamplingWindowRenderer();
    }

    internal void Clear()
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
            Plugin.Log.LogWarning("Unable to create PeakRoutePlanner sampling window ellipsoid because no compatible shader was found.");
            return;
        }

        samplingWindowObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
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
        Plugin.Log.LogInfo($"Created PeakRoutePlanner sampling window ellipsoid with shader={shader.name}.");
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

    private static int GetWindowHash(Vector3 center, Vector3 size, Quaternion rotation)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 31 + Mathf.RoundToInt(center.x * 20f);
            hash = hash * 31 + Mathf.RoundToInt(center.y * 20f);
            hash = hash * 31 + Mathf.RoundToInt(center.z * 20f);
            hash = hash * 31 + Mathf.RoundToInt(size.x * 20f);
            hash = hash * 31 + Mathf.RoundToInt(size.y * 20f);
            hash = hash * 31 + Mathf.RoundToInt(size.z * 20f);
            hash = hash * 31 + Mathf.RoundToInt(rotation.x * 1000f);
            hash = hash * 31 + Mathf.RoundToInt(rotation.y * 1000f);
            hash = hash * 31 + Mathf.RoundToInt(rotation.z * 1000f);
            hash = hash * 31 + Mathf.RoundToInt(rotation.w * 1000f);
            return hash;
        }
    }
}
