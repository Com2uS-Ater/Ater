using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Rendering;

[DisallowMultipleComponent]
public sealed class RenderTextureMaskCleaner : MonoBehaviour
{
    [Serializable]
    public sealed class FloatEvent : UnityEvent<float> { }

    private static readonly int BrushUVId = Shader.PropertyToID("_BrushUV");
    private static readonly int BrushSizeId = Shader.PropertyToID("_BrushSize");
    private static readonly int BrushHardnessId = Shader.PropertyToID("_BrushHardness");
    private static readonly int BrushStrengthId = Shader.PropertyToID("_BrushStrength");
    private static readonly int CleanableTexId = Shader.PropertyToID("_CleanableTex");
    private static readonly int UseCleanableTexId = Shader.PropertyToID("_UseCleanableTex");

    [Header("References")]
    [SerializeField] private Camera rayCamera;
    [SerializeField] private Renderer targetRenderer;
    [SerializeField] private MeshCollider targetMeshCollider;
    [SerializeField] private Shader brushShader;
    [SerializeField] private Shader progressShader;

    [Header("Mask")]
    [SerializeField, Range(128, 4096)] private int maskResolution = 1024;
    [SerializeField] private string maskTextureProperty = "_MaskTex";
    [SerializeField] private Texture cleanableMask;
    [SerializeField] private string cleanableMaskTextureProperty = "_CleanableTex";
    [SerializeField] private string useCleanableMaskProperty = "_UseCleanableTex";

    [Header("Brush")]
    [SerializeField, Range(0.001f, 0.5f)] private float brushSize = 0.05f;
    [SerializeField, Range(0f, 0.99f)] private float brushHardness = 0.8f;
    [SerializeField, Range(0.01f, 1f)] private float brushStrength = 1f;
    [SerializeField] private bool resizeWithMouseWheel = true;
    [SerializeField] private float resizeStep = 0.01f;
    [SerializeField] private float minBrushSize = 0.005f;
    [SerializeField] private float maxBrushSize = 0.2f;

    [Header("Input")]
    [SerializeField] private int paintMouseButton = 0;
    [SerializeField] private LayerMask raycastLayers = ~0;

    [Header("Progress Tracking")]
    [SerializeField] private bool trackProgress = true;
    [SerializeField, Range(16, 512)] private int progressResolution = 128;
    [SerializeField, Min(0.02f)] private float progressUpdateInterval = 0.15f;
    [SerializeField] private bool useAsyncGPUReadback = true;

    [Header("Events")] 
    [SerializeField] private float _targetProgress = 0.9f;
    [SerializeField] private FloatEvent onProgressChanged;
    [SerializeField] private UnityEvent onFullyCleaned;

    private RenderTexture maskA;
    private RenderTexture maskB;
    private RenderTexture currentMask;
    private RenderTexture progressRt;
    private Texture2D progressReadbackTexture;

    private Material brushMaterial;
    private Material progressMaterial;
    private MaterialPropertyBlock propertyBlock;

    private int maskTexturePropertyId;
    private int cleanableMaskTexturePropertyId;
    private int useCleanableMaskPropertyId;

    private bool progressDirty = true;
    private bool readbackPending;
    private float nextProgressUpdateTime;
    private bool fullyCleanedEventRaised;

    public float BrushSize
    {
        get => brushSize;
        set => brushSize = Mathf.Clamp(value, minBrushSize, maxBrushSize);
    }

    public Texture CleanableMask
    {
        get => cleanableMask;
        set => SetCleanableMask(value);
    }

    [SerializeField] float _currentProgress = 0;
    public float CurrentProgress01 => _currentProgress;

    public float CleanPercent => CurrentProgress01 * 100f;

    public bool IsFullyCleaned => CurrentProgress01 >= _targetProgress;

    private void Reset()
    {
        if (rayCamera == null) rayCamera = Camera.main;
        if (targetRenderer == null) targetRenderer = GetComponent<Renderer>();
        if (targetMeshCollider == null) targetMeshCollider = GetComponent<MeshCollider>();
        if (brushShader == null) brushShader = Shader.Find("Hidden/MaskPainter");
        if (progressShader == null) progressShader = Shader.Find("Hidden/MaskProgressCombine");
    }

    private void Awake()
    {
        if (rayCamera == null) rayCamera = Camera.main;
        if (targetRenderer == null) targetRenderer = GetComponent<Renderer>();
        if (targetMeshCollider == null) targetMeshCollider = GetComponent<MeshCollider>();
        if (brushShader == null) brushShader = Shader.Find("Hidden/MaskPainter");
        if (progressShader == null) progressShader = Shader.Find("Hidden/MaskProgressCombine");

        if (targetRenderer == null)
        {
            Debug.LogError($"{name}: Target Renderer가 없습니다.", this);
            enabled = false;
            return;
        }

        if (brushShader == null)
        {
            Debug.LogError($"{name}: Hidden/MaskPainter 셰이더를 찾지 못했습니다.", this);
            enabled = false;
            return;
        }

        if (trackProgress && progressShader == null)
        {
            Debug.LogError($"{name}: Hidden/MaskProgressCombine 셰이더를 찾지 못했습니다.", this);
            enabled = false;
            return;
        }

        propertyBlock = new MaterialPropertyBlock();
        maskTexturePropertyId = Shader.PropertyToID(maskTextureProperty);
        cleanableMaskTexturePropertyId = Shader.PropertyToID(cleanableMaskTextureProperty);
        useCleanableMaskPropertyId = Shader.PropertyToID(useCleanableMaskProperty);

        brushMaterial = new Material(brushShader)
        {
            hideFlags = HideFlags.HideAndDontSave
        };

        if (trackProgress)
        {
            progressMaterial = new Material(progressShader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        CreateMaskTextures();
        CreateProgressResources();
        RefreshSharedTextures();
        ApplyTexturesToRenderer();
        ForceRefreshProgress();
    }

    private void Update()
    {
        HandleBrushResize();

        if (Input.GetMouseButton(paintMouseButton) && TryGetHitUV(out Vector2 uv))
        {
            PaintAtUV(uv);
        }

        UpdateProgressTracking();
    }

    private void HandleBrushResize()
    {
        if (!resizeWithMouseWheel)
            return;

        float scroll = Input.mouseScrollDelta.y;
        if (Mathf.Abs(scroll) > Mathf.Epsilon)
            BrushSize += scroll * resizeStep;
    }

    private bool TryGetHitUV(out Vector2 uv)
    {
        uv = default;

        Camera cam = rayCamera != null ? rayCamera : Camera.main;
        if (cam == null)
            return false;

        Ray ray = cam.ScreenPointToRay(Input.mousePosition);
        if (!Physics.Raycast(ray, out RaycastHit hit, Mathf.Infinity, raycastLayers, QueryTriggerInteraction.Ignore))
            return false;

        if (hit.collider is not MeshCollider)
            return false;

        if (targetMeshCollider != null)
        {
            if (hit.collider != targetMeshCollider)
                return false;
        }
        else
        {
            Renderer hitRenderer = hit.collider.GetComponentInParent<Renderer>();
            if (hitRenderer != targetRenderer)
                return false;
        }

        uv = hit.textureCoord;
        return true;
    }

    public void PaintAtUV(Vector2 uv)
    {
        if (brushMaterial == null || currentMask == null)
            return;

        brushMaterial.SetVector(BrushUVId, new Vector4(uv.x, uv.y, 0f, 0f));
        brushMaterial.SetFloat(BrushSizeId, brushSize);
        brushMaterial.SetFloat(BrushHardnessId, brushHardness);
        brushMaterial.SetFloat(BrushStrengthId, brushStrength);

        RenderTexture next = currentMask == maskA ? maskB : maskA;
        Graphics.Blit(currentMask, next, brushMaterial);
        currentMask = next;

        ApplyTexturesToRenderer();
        MarkProgressDirty();
    }

    public void SetBrushSize(float value)
    {
        BrushSize = value;
    }

    public void SetCleanableMask(Texture texture)
    {
        cleanableMask = texture;
        RefreshSharedTextures();
        ApplyTexturesToRenderer();
        MarkProgressDirty(forceImmediate: true);
    }

    [ContextMenu("Clear Mask")]
    public void ClearMask()
    {
        if (maskA == null || maskB == null)
            return;

        Graphics.Blit(Texture2D.blackTexture, maskA);
        Graphics.Blit(Texture2D.blackTexture, maskB);
        currentMask = maskA;

        ApplyTexturesToRenderer();
        MarkProgressDirty(forceImmediate: true);
    }

    [ContextMenu("Refresh Progress Now")]
    public void ForceRefreshProgress()
    {
        if (!trackProgress)
            return;

        progressDirty = true;
        nextProgressUpdateTime = 0f;
        TryScheduleProgressReadback();
    }

    private void RefreshSharedTextures()
    {
        Texture effectiveCleanable = GetEffectiveCleanableTexture();
        float useCleanable = cleanableMask != null ? 1f : 0f;

        if (brushMaterial != null)
        {
            brushMaterial.SetTexture(CleanableTexId, effectiveCleanable);
            brushMaterial.SetFloat(UseCleanableTexId, useCleanable);
        }

        if (progressMaterial != null)
        {
            progressMaterial.SetTexture(CleanableTexId, effectiveCleanable);
            progressMaterial.SetFloat(UseCleanableTexId, useCleanable);
        }
    }

    private void ApplyTexturesToRenderer()
    {
        if (targetRenderer == null)
            return;

        targetRenderer.GetPropertyBlock(propertyBlock);
        propertyBlock.SetTexture(maskTexturePropertyId, currentMask);
        propertyBlock.SetTexture(cleanableMaskTexturePropertyId, GetEffectiveCleanableTexture());
        propertyBlock.SetFloat(useCleanableMaskPropertyId, cleanableMask != null ? 1f : 0f);
        targetRenderer.SetPropertyBlock(propertyBlock);
    }

    private void UpdateProgressTracking()
    {
        if (!trackProgress || progressMaterial == null || progressRt == null)
            return;

        TryScheduleProgressReadback();
    }

    private void TryScheduleProgressReadback()
    {
        if (!progressDirty || readbackPending || Time.unscaledTime < nextProgressUpdateTime)
            return;

        progressDirty = false;
        nextProgressUpdateTime = Time.unscaledTime + progressUpdateInterval;

        Graphics.Blit(currentMask, progressRt, progressMaterial);

        if (useAsyncGPUReadback && SystemInfo.supportsAsyncGPUReadback)
        {
            readbackPending = true;
            AsyncGPUReadback.Request(progressRt, 0, TextureFormat.RGBA32, OnAsyncProgressReadbackCompleted);
            return;
        }

        ReadProgressSynchronously();
    }

    private void OnAsyncProgressReadbackCompleted(AsyncGPUReadbackRequest request)
    {
        readbackPending = false;

        if (this == null)
            return;

        if (request.hasError)
        {
            ReadProgressSynchronously();
            return;
        }

        NativeArray<Color32> data = request.GetData<Color32>();
        UpdateProgressFromPixels(data);
    }

    private void ReadProgressSynchronously()
    {
        if (progressRt == null)
            return;

        EnsureProgressReadbackTexture();

        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = progressRt;
        progressReadbackTexture.ReadPixels(new Rect(0f, 0f, progressRt.width, progressRt.height), 0, 0, false);
        progressReadbackTexture.Apply(false, false);
        RenderTexture.active = previous;

        NativeArray<Color32> data = progressReadbackTexture.GetRawTextureData<Color32>();
        UpdateProgressFromPixels(data);
    }

    private void UpdateProgressFromPixels(NativeArray<Color32> pixels)
    {
        double cleaned = 0.0;
        double cleanable = 0.0;

        for (int i = 0; i < pixels.Length; i++)
        {
            Color32 pixel = pixels[i];
            cleaned += pixel.r / 255.0;
            cleanable += pixel.g / 255.0;
        }

        float newProgress = cleanable > double.Epsilon
            ? Mathf.Clamp01((float)(cleaned / cleanable))
            : 1f;

        bool changed = !Mathf.Approximately(CurrentProgress01, newProgress);
        _currentProgress = newProgress;

        if (changed)
            onProgressChanged?.Invoke(CurrentProgress01);

        if (!fullyCleanedEventRaised && IsFullyCleaned)
        {
            fullyCleanedEventRaised = true;
            onFullyCleaned?.Invoke();
        }
        else if (!IsFullyCleaned)
        {
            fullyCleanedEventRaised = false;
        }
    }

    private void MarkProgressDirty(bool forceImmediate = false)
    {
        if (!trackProgress)
            return;

        progressDirty = true;

        if (forceImmediate)
            nextProgressUpdateTime = 0f;
    }

    private void CreateMaskTextures()
    {
        ReleaseRT(ref maskA);
        ReleaseRT(ref maskB);

        maskA = CreateRenderTexture($"{name}_MaskA", maskResolution, FilterMode.Bilinear);
        maskB = CreateRenderTexture($"{name}_MaskB", maskResolution, FilterMode.Bilinear);

        Graphics.Blit(Texture2D.blackTexture, maskA);
        Graphics.Blit(Texture2D.blackTexture, maskB);

        currentMask = maskA;
    }

    private void CreateProgressResources()
    {
        ReleaseRT(ref progressRt);
        DestroyReadbackTexture();

        if (!trackProgress)
            return;

        progressRt = CreateRenderTexture($"{name}_ProgressRT", progressResolution, FilterMode.Bilinear);
    }

    private RenderTexture CreateRenderTexture(string rtName, int resolution, FilterMode filterMode)
    {
        RenderTexture rt = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGB32)
        {
            name = rtName,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = filterMode,
            useMipMap = false,
            autoGenerateMips = false
        };

        rt.Create();
        return rt;
    }

    private void EnsureProgressReadbackTexture()
    {
        if (progressReadbackTexture != null && progressReadbackTexture.width == progressResolution && progressReadbackTexture.height == progressResolution)
            return;

        DestroyReadbackTexture();
        progressReadbackTexture = new Texture2D(progressResolution, progressResolution, TextureFormat.RGBA32, false, true)
        {
            name = $"{name}_ProgressReadback"
        };
    }

    private void DestroyReadbackTexture()
    {
        if (progressReadbackTexture == null)
            return;

        Destroy(progressReadbackTexture);
        progressReadbackTexture = null;
    }

    private Texture GetEffectiveCleanableTexture()
    {
        return cleanableMask != null ? cleanableMask : Texture2D.whiteTexture;
    }

    private void OnDestroy()
    {
        if (brushMaterial != null)
        {
            Destroy(brushMaterial);
            brushMaterial = null;
        }

        if (progressMaterial != null)
        {
            Destroy(progressMaterial);
            progressMaterial = null;
        }

        DestroyReadbackTexture();
        ReleaseRT(ref maskA);
        ReleaseRT(ref maskB);
        ReleaseRT(ref progressRt);
    }

    private static void ReleaseRT(ref RenderTexture rt)
    {
        if (rt == null)
            return;

        if (rt.IsCreated())
            rt.Release();

        Destroy(rt);
        rt = null;
    }

    public void DebugClean()
    {
        Debug.Log($"{CleanPercent}%");
    }
    public void DebugComplete()
    {
        Debug.Log("complete");
    }
}
