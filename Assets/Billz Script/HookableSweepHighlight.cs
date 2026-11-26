using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Draws a soft emissive band that sweeps from the bottom to the top of a hookable object.
/// Requires the project's existing HookableTarget component and the companion URP shader
/// "Billz/Hookable Sweep Highlight URP".
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(HookableTarget))]
public sealed class HookableSweepHighlight : MonoBehaviour
{
    private const string ShaderName = "Billz/Hookable Sweep Highlight URP";

    private static readonly int GlowColorId = Shader.PropertyToID("_GlowColor");
    private static readonly int SweepPositionId = Shader.PropertyToID("_SweepPosition");
    private static readonly int SweepWidthId = Shader.PropertyToID("_SweepWidth");
    private static readonly int SweepIntensityId = Shader.PropertyToID("_SweepIntensity");
    private static readonly int ObjectBottomYId = Shader.PropertyToID("_ObjectBottomY");
    private static readonly int ObjectHeightId = Shader.PropertyToID("_ObjectHeight");

    private static Material sharedOverlayMaterial;

    [Header("Appearance")]
    [SerializeField] private Color highlightColor = new Color(1f, 0.88f, 0.48f, 1f);
    [SerializeField, Min(0.01f)] private float bandWidth = 0.22f;
    [SerializeField, Min(0f)] private float intensity = 1.8f;

    [Header("Timing")]
    [SerializeField, Min(0f)] private float firstSweepDelay = 1f;
    [SerializeField, Min(0.1f)] private float minInterval = 3f;
    [SerializeField, Min(0.1f)] private float maxInterval = 5f;
    [SerializeField, Min(0.1f)] private float sweepDuration = 1.1f;

    [Header("Target")]
    [Tooltip("Include inactive child MeshRenderers when constructing the highlight overlay.")]
    [SerializeField] private bool includeInactiveMeshes = true;

    private HookableTarget hookableTarget;
    private readonly List<Renderer> overlayRenderers = new List<Renderer>();
    private readonly List<GameObject> overlayObjects = new List<GameObject>();
    private Coroutine sweepCoroutine;
    private MaterialPropertyBlock propertyBlock;
    private bool overlayBuilt;

    private void Awake()
    {
        hookableTarget = GetComponent<HookableTarget>();
        propertyBlock = new MaterialPropertyBlock();
    }

    private void OnEnable()
    {
        if (hookableTarget == null)
            hookableTarget = GetComponent<HookableTarget>();

        if (hookableTarget == null || !hookableTarget.CanBeHooked)
            return;

        if (!BuildOverlay())
            return;

        sweepCoroutine = StartCoroutine(SweepLoop());
    }

    private void OnDisable()
    {
        if (sweepCoroutine != null)
        {
            StopCoroutine(sweepCoroutine);
            sweepCoroutine = null;
        }

        SetOverlayVisible(false);
    }

    private void OnDestroy()
    {
        RemoveOverlay();
    }

    private IEnumerator SweepLoop()
    {
        if (firstSweepDelay > 0f)
            yield return new WaitForSeconds(firstSweepDelay);

        while (hookableTarget != null && hookableTarget.CanBeHooked)
        {
            yield return SweepOnce();
            float interval = Random.Range(minInterval, Mathf.Max(minInterval, maxInterval));
            yield return new WaitForSeconds(interval);
        }

        sweepCoroutine = null;
    }

    private IEnumerator SweepOnce()
    {
        SetOverlayVisible(true);

        float elapsed = 0f;
        while (elapsed < sweepDuration)
        {
            elapsed += Time.deltaTime;
            float normalizedTime = Mathf.Clamp01(elapsed / sweepDuration);

            // SmoothStep makes the band accelerate/decelerate gently as it travels upward.
            float position = normalizedTime * normalizedTime * (3f - 2f * normalizedTime);
            ApplySweepPosition(position);
            yield return null;
        }

        ApplySweepPosition(1.05f);
        SetOverlayVisible(false);
    }

    private bool BuildOverlay()
    {
        if (overlayBuilt)
        {
            SetOverlayVisible(true);
            return overlayRenderers.Count > 0;
        }

        Shader overlayShader = Shader.Find(ShaderName);
        if (overlayShader == null)
        {
            Debug.LogError($"[{nameof(HookableSweepHighlight)}] Shader '{ShaderName}' not found. " +
                           "Make sure HookableSweepHighlight_URP.shader is inside the project.", this);
            return false;
        }

        if (sharedOverlayMaterial == null)
        {
            sharedOverlayMaterial = new Material(overlayShader)
            {
                name = "Runtime_HookableSweepHighlight_Material",
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        MeshFilter[] meshFilters = GetComponentsInChildren<MeshFilter>(includeInactiveMeshes);
        List<MeshFilter> validFilters = new List<MeshFilter>();
        Bounds combinedBounds = new Bounds();
        bool hasBounds = false;

        for (int i = 0; i < meshFilters.Length; i++)
        {
            MeshFilter meshFilter = meshFilters[i];
            if (meshFilter == null || meshFilter.sharedMesh == null)
                continue;

            MeshRenderer meshRenderer = meshFilter.GetComponent<MeshRenderer>();
            if (meshRenderer == null)
                continue;

            if (!hasBounds)
            {
                combinedBounds = meshRenderer.bounds;
                hasBounds = true;
            }
            else
            {
                combinedBounds.Encapsulate(meshRenderer.bounds);
            }

            validFilters.Add(meshFilter);
        }

        if (validFilters.Count == 0 || !hasBounds)
        {
            Debug.LogWarning($"[{nameof(HookableSweepHighlight)}] No child MeshFilter + MeshRenderer found on '{name}'. " +
                             "This component currently supports regular MeshRenderers, not SkinnedMeshRenderers.", this);
            return false;
        }

        float objectBottomY = combinedBounds.min.y;
        float objectHeight = Mathf.Max(0.01f, combinedBounds.size.y);

        for (int i = 0; i < validFilters.Count; i++)
        {
            MeshFilter sourceFilter = validFilters[i];
            MeshRenderer sourceRenderer = sourceFilter.GetComponent<MeshRenderer>();

            GameObject overlayObject = new GameObject(sourceFilter.name + "_Highlight");
            overlayObject.layer = sourceFilter.gameObject.layer;
            overlayObject.transform.SetParent(sourceFilter.transform, false);
            overlayObjects.Add(overlayObject);

            MeshFilter overlayFilter = overlayObject.AddComponent<MeshFilter>();
            overlayFilter.sharedMesh = sourceFilter.sharedMesh;

            MeshRenderer overlayRenderer = overlayObject.AddComponent<MeshRenderer>();
            overlayRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            overlayRenderer.receiveShadows = false;
            overlayRenderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            overlayRenderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            overlayRenderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;

            int materialCount = Mathf.Max(1, sourceRenderer.sharedMaterials.Length);
            Material[] overlayMaterials = new Material[materialCount];
            for (int m = 0; m < materialCount; m++)
                overlayMaterials[m] = sharedOverlayMaterial;
            overlayRenderer.sharedMaterials = overlayMaterials;

            overlayRenderer.enabled = false;
            overlayRenderers.Add(overlayRenderer);

            overlayRenderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetColor(GlowColorId, highlightColor);
            propertyBlock.SetFloat(SweepWidthId, bandWidth);
            propertyBlock.SetFloat(SweepIntensityId, intensity);
            propertyBlock.SetFloat(ObjectBottomYId, objectBottomY);
            propertyBlock.SetFloat(ObjectHeightId, objectHeight);
            propertyBlock.SetFloat(SweepPositionId, -1f);
            overlayRenderer.SetPropertyBlock(propertyBlock);
        }

        overlayBuilt = true;
        return overlayRenderers.Count > 0;
    }

    private void ApplySweepPosition(float position)
    {
        for (int i = 0; i < overlayRenderers.Count; i++)
        {
            Renderer overlayRenderer = overlayRenderers[i];
            if (overlayRenderer == null)
                continue;

            overlayRenderer.GetPropertyBlock(propertyBlock);
            propertyBlock.SetFloat(SweepPositionId, position);
            overlayRenderer.SetPropertyBlock(propertyBlock);
        }
    }

    private void SetOverlayVisible(bool visible)
    {
        for (int i = 0; i < overlayRenderers.Count; i++)
        {
            if (overlayRenderers[i] != null)
                overlayRenderers[i].enabled = visible;
        }
    }

    private void RemoveOverlay()
    {
        overlayRenderers.Clear();
        overlayBuilt = false;

        for (int i = overlayObjects.Count - 1; i >= 0; i--)
        {
            GameObject overlayObject = overlayObjects[i];
            if (overlayObject == null)
                continue;

            if (Application.isPlaying)
                Destroy(overlayObject);
            else
                DestroyImmediate(overlayObject);
        }

        overlayObjects.Clear();
    }

    private void OnValidate()
    {
        bandWidth = Mathf.Max(0.01f, bandWidth);
        intensity = Mathf.Max(0f, intensity);
        minInterval = Mathf.Max(0.1f, minInterval);
        maxInterval = Mathf.Max(minInterval, maxInterval);
        sweepDuration = Mathf.Max(0.1f, sweepDuration);
        firstSweepDelay = Mathf.Max(0f, firstSweepDelay);
    }
}
