using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class CharacterPostProcessPass : ScriptableRenderPass
{
    private static readonly int s_CoverageTexId             = Shader.PropertyToID("_CoverageTex");
    private static readonly int s_BloomTexId               = Shader.PropertyToID("_BloomTex");
    private static readonly int s_SceneTexId               = Shader.PropertyToID("_SceneTex");
    private static readonly int s_BloomThresholdId         = Shader.PropertyToID("_BloomThreshold");
    private static readonly int s_BloomSoftKneeId          = Shader.PropertyToID("_BloomSoftKnee");
    private static readonly int s_BloomIntensityId         = Shader.PropertyToID("_BloomIntensity");
    private static readonly int s_KawaseOffsetId           = Shader.PropertyToID("_KawaseOffset");
    private static readonly int s_SaturationId             = Shader.PropertyToID("_Saturation");
    private static readonly int s_VibranceId               = Shader.PropertyToID("_Vibrance");
    private static readonly int s_HueShiftId               = Shader.PropertyToID("_HueShift");
    private static readonly int s_ColorFilterId            = Shader.PropertyToID("_ColorFilter");
    private static readonly int s_ToneMapStartId           = Shader.PropertyToID("_ToneMapStart");
    private static readonly int s_ToneMapEndId             = Shader.PropertyToID("_ToneMapEnd");
    private static readonly int s_ExposureId               = Shader.PropertyToID("_Exposure");
    private static readonly int s_HueProtectionStrengthId  = Shader.PropertyToID("_HueProtectionStrength");
    private static readonly int s_HueProtectionThresholdId = Shader.PropertyToID("_HueProtectionThreshold");

    private CharacterPostProcessFeature.Settings m_Settings;

    private RTHandle m_CharRT, m_CoverageRT, m_CoverageSrcRT;
    private RTHandle m_BloomRT1, m_BloomRT2;
    private RTHandle m_TempRT, m_AdjustedRT, m_SceneRT;

    private Material m_MaskCopyMat, m_BloomMat, m_ColorMat, m_CompMat, m_AcesMat;

    // ── TDR FIX 1: 缓存 Coverage 材质，不再每帧 new+Dirty ──
    private Material m_CoverageExtractMat;
    private Material m_CoverageDilateMat;

    // ── TDR FIX 2: 属性脏检测 ──
    private struct BloomState { public bool enabled; public float threshold, softKnee, intensity; public int iterations; }
    private struct ColorState { public bool enabled; public float saturation, vibrance, hueShift; public Color filter; public bool highQuality; }
    private struct ToneState  { public bool enabled; public float start, end, exposure, hueStr, hueThr; }
    private BloomState m_LastBloom; private bool m_BloomDirty = true;
    private ColorState m_LastColor; private bool m_ColorDirty = true;
    private ToneState  m_LastTone;  private bool m_ToneDirty  = true;

    // ── TDR FIX 3: 滑块安全模式 ──
    private float m_BloomIterChangeTime = -999f;
    private int   m_LastBloomIterations = -1;

    // ── TDR FIX 4: RT 固定尺寸 ──
    private Vector2Int m_BloomRTSize;
    private bool m_BloomRTSizeInitialized;

    public CharacterPostProcessPass(CharacterPostProcessFeature.Settings s) { m_Settings = s; }

    public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
    {
        var desc = renderingData.cameraData.cameraTargetDescriptor;
        desc.msaaSamples = 1; desc.depthBufferBits = 0; desc.useDynamicScale = false;

        desc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
        RenderingUtils.ReAllocateIfNeeded(ref m_CharRT,        desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name:"_CharRT");
        RenderingUtils.ReAllocateIfNeeded(ref m_TempRT,        desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name:"_TempRT");
        RenderingUtils.ReAllocateIfNeeded(ref m_AdjustedRT,    desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name:"_AdjustedRT");
        RenderingUtils.ReAllocateIfNeeded(ref m_SceneRT,       desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name:"_SceneRT");
        RenderingUtils.ReAllocateIfNeeded(ref m_CoverageSrcRT, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name:"_CoverageSrcRT");

        desc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8_UNorm;
        RenderingUtils.ReAllocateIfNeeded(ref m_CoverageRT, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name:"_CoverageRT");

        // ── TDR FIX 4: Bloom RT 尺寸仅在首次或真正变化时重新分配 ──
        desc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
        Vector2Int targetBloomSize = new Vector2Int(Mathf.Max(1, desc.width / 2), Mathf.Max(1, desc.height / 2));
        if (!m_BloomRTSizeInitialized || m_BloomRTSize != targetBloomSize)
        {
            m_BloomRTSize = targetBloomSize;
            m_BloomRTSizeInitialized = true;
            desc.width = targetBloomSize.x;
            desc.height = targetBloomSize.y;
            RenderingUtils.ReAllocateIfNeeded(ref m_BloomRT1, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name:"_BloomRT1");
            RenderingUtils.ReAllocateIfNeeded(ref m_BloomRT2, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name:"_BloomRT2");
        }

        // ── TDR FIX 1: 确保 Coverage 材质在首次初始化后持续缓存 ──
        EnsureCoverageMaterials();
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (m_Settings.characterLayer.value == 0) return;
        EnsureMaterials();

        // ── TDR FIX 2: 仅在值变化时更新材质属性 ──
        UpdateBloomDirty();
        UpdateColorDirty();
        UpdateToneDirty();

        CommandBuffer cmd = CommandBufferPool.Get("CharacterPostProcess");
        RTHandle cam = renderingData.cameraData.renderer.cameraColorTargetHandle;

        // ── 1. 第二相机 RT → 覆盖提取 ──
        if (m_ExtCoverageRT != null && m_CoverageExtractMat != null && m_CoverageDilateMat != null)
        {
            cmd.CopyTexture(m_ExtCoverageRT, m_CoverageSrcRT.rt);
            Blitter.BlitCameraTexture(cmd, m_CoverageSrcRT, m_CoverageRT, m_CoverageExtractMat, 0);
            for (int i = 0; i < 2; i++)
            {
                Blitter.BlitCameraTexture(cmd, m_CoverageRT, m_TempRT);
                Blitter.BlitCameraTexture(cmd, m_TempRT, m_CoverageRT, m_CoverageDilateMat, 0);
            }
        }
        else
        {
            cmd.SetRenderTarget(m_CoverageRT);
            cmd.ClearRenderTarget(false, true, Color.white);
            context.ExecuteCommandBuffer(cmd); cmd.Clear();
        }

        // ── 2. Scene copy ──
        Blitter.BlitCameraTexture(cmd, cam, m_SceneRT);

        // ── 3. MaskedCopy ──
        m_MaskCopyMat.SetTexture(s_CoverageTexId, m_CoverageRT);
        Blitter.BlitCameraTexture(cmd, m_SceneRT, m_CharRT, m_MaskCopyMat, 0);

        // ── 4. Bloom（全白覆盖）──
        if (m_Settings.enableBloom)
        {
        if (m_BloomDirty)
        {
            m_BloomMat.SetFloat(s_BloomThresholdId, m_Settings.bloomThreshold);
            m_BloomMat.SetFloat(s_BloomSoftKneeId,  m_Settings.bloomSoftKnee);
            m_BloomMat.SetFloat(s_BloomIntensityId, m_Settings.bloomIntensity);
            m_BloomDirty = false;
        }
        m_BloomMat.SetTexture(s_CoverageTexId, Texture2D.whiteTexture);
        Blitter.BlitCameraTexture(cmd, m_CharRT, m_BloomRT1, m_BloomMat, 0);

        RTHandle ks = m_BloomRT1, kd = m_BloomRT2;
        // ── TDR FIX 3: 迭代数变化后 0.5s 内使用安全迭代 ──
        int iters = GetEffectiveIterations();
        for (int i = 0; i < iters; i++)
        {
            m_BloomMat.SetFloat(s_KawaseOffsetId, i * 2f + 1f);
            Blitter.BlitCameraTexture(cmd, ks, kd, m_BloomMat, 1);
            var t = ks; ks = kd; kd = t;
        }
        for (int i = iters - 1; i >= 0; i--)
        {
            m_BloomMat.SetFloat(s_KawaseOffsetId, i * 2f + 1f);
            Blitter.BlitCameraTexture(cmd, ks, kd, m_BloomMat, 2);
            var t = ks; ks = kd; kd = t;
        }
        m_BloomMat.SetTexture(s_BloomTexId, ks);
        Blitter.BlitCameraTexture(cmd, m_CharRT, m_TempRT, m_BloomMat, 3);
        }
        else Blitter.BlitCameraTexture(cmd, m_CharRT, m_TempRT);

        // ── 5. Color Adjust ──
        if (m_Settings.enableColorAdjustments)
        {
        if (m_ColorDirty)
        {
            m_ColorMat.SetFloat(s_SaturationId, m_Settings.saturation);
            m_ColorMat.SetFloat(s_VibranceId,   m_Settings.vibrance);
            m_ColorMat.SetFloat(s_HueShiftId,   m_Settings.hueShift);
            m_ColorMat.SetColor(s_ColorFilterId, m_Settings.colorFilter);
            if (m_Settings.useHighQualityHueShift) m_ColorMat.EnableKeyword("_HUE_METHOD_OKLAB");
            else m_ColorMat.DisableKeyword("_HUE_METHOD_OKLAB");
            m_ColorDirty = false;
        }
        Blitter.BlitCameraTexture(cmd, m_TempRT, m_AdjustedRT, m_ColorMat, 0);
        }
        else Blitter.BlitCameraTexture(cmd, m_TempRT, m_AdjustedRT);

        // ── 6. Composite ──
        m_CompMat.SetTexture(s_CoverageTexId, m_CoverageRT);
        m_CompMat.SetTexture(s_SceneTexId,    m_SceneRT);
        Blitter.BlitCameraTexture(cmd, m_AdjustedRT, cam, m_CompMat, 0);

        // ── 7. ACES ──
        if (m_Settings.enableToneMapping)
        {
        Blitter.BlitCameraTexture(cmd, cam, m_TempRT);
        if (m_ToneDirty)
        {
            m_AcesMat.SetFloat(s_ToneMapStartId, m_Settings.toneMapStart);
            m_AcesMat.SetFloat(s_ToneMapEndId,   m_Settings.toneMapEnd);
            m_AcesMat.SetFloat(s_ExposureId, m_Settings.exposure);
            m_AcesMat.SetFloat(s_HueProtectionStrengthId,  m_Settings.hueProtectionStrength);
            m_AcesMat.SetFloat(s_HueProtectionThresholdId, m_Settings.hueProtectionThreshold);
            m_ToneDirty = false;
        }
        Blitter.BlitCameraTexture(cmd, m_TempRT, m_SceneRT, m_AcesMat, 0);
        Blitter.BlitCameraTexture(cmd, m_SceneRT, cam);
        }

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    private RenderTexture m_ExtCoverageRT;
    public void SetCoverageRT(RenderTexture rt) => m_ExtCoverageRT = rt;

    // ── TDR FIX 1: 覆盖率材质缓存 ──
    private void EnsureCoverageMaterials()
    {
        if (m_CoverageExtractMat == null || m_CoverageExtractMat.Equals(null))
        {
            var s = Shader.Find("Hidden/Character/CoverageExtract");
            if (s != null) m_CoverageExtractMat = new Material(s);
        }
        if (m_CoverageDilateMat == null || m_CoverageDilateMat.Equals(null))
        {
            var s = Shader.Find("Hidden/Character/CoverageDilate");
            if (s != null) m_CoverageDilateMat = new Material(s);
        }
    }

    // ── TDR FIX 2: Bloom 属性脏检测 ──
    private void UpdateBloomDirty()
    {
        var cur = new BloomState { enabled = m_Settings.enableBloom, threshold = m_Settings.bloomThreshold,
            softKnee = m_Settings.bloomSoftKnee, intensity = m_Settings.bloomIntensity, iterations = m_Settings.bloomKawaseIterations };
        if (cur.enabled != m_LastBloom.enabled || cur.threshold != m_LastBloom.threshold ||
            cur.softKnee != m_LastBloom.softKnee || cur.intensity != m_LastBloom.intensity)
        { m_BloomDirty = true; m_LastBloom = cur; }
        // 迭代数变化触发安全模式
        if (cur.iterations != m_LastBloom.iterations && m_LastBloom.iterations >= 0)
        {
            m_BloomIterChangeTime = Time.realtimeSinceStartup;
            m_LastBloomIterations = cur.iterations;
        }
        if (m_LastBloom.iterations < 0) { m_LastBloom.iterations = cur.iterations; m_LastBloomIterations = cur.iterations; }
        m_LastBloom = cur;
    }
    private void UpdateColorDirty()
    {
        var cur = new ColorState { enabled = m_Settings.enableColorAdjustments, saturation = m_Settings.saturation,
            vibrance = m_Settings.vibrance, hueShift = m_Settings.hueShift, filter = m_Settings.colorFilter, highQuality = m_Settings.useHighQualityHueShift };
        if (cur.enabled != m_LastColor.enabled || cur.saturation != m_LastColor.saturation ||
            cur.vibrance != m_LastColor.vibrance || cur.hueShift != m_LastColor.hueShift ||
            cur.filter != m_LastColor.filter || cur.highQuality != m_LastColor.highQuality)
        { m_ColorDirty = true; m_LastColor = cur; }
    }
    private void UpdateToneDirty()
    {
        var cur = new ToneState { enabled = m_Settings.enableToneMapping, start = m_Settings.toneMapStart,
            end = m_Settings.toneMapEnd, exposure = m_Settings.exposure,
            hueStr = m_Settings.hueProtectionStrength, hueThr = m_Settings.hueProtectionThreshold };
        if (cur.enabled != m_LastTone.enabled || cur.start != m_LastTone.start ||
            cur.end != m_LastTone.end || cur.exposure != m_LastTone.exposure ||
            cur.hueStr != m_LastTone.hueStr || cur.hueThr != m_LastTone.hueThr)
        { m_ToneDirty = true; m_LastTone = cur; }
    }

    // ── TDR FIX 3: 滑块安全模式 — 拖动后 0.5s 内限制迭代 ──
    private int GetEffectiveIterations()
    {
        int raw = Mathf.Clamp(m_Settings.bloomKawaseIterations, 0, 4);
        if (!m_Settings.enableSliderSafetyMode) return raw;
        float timeSince = Time.realtimeSinceStartup - m_BloomIterChangeTime;
        if (timeSince < m_Settings.safetyModeDuration)
            return Mathf.Min(raw, m_Settings.safeModeMaxIterations);
        return raw;
    }

    private void EnsureMaterials()
    {
        if ((m_MaskCopyMat==null||m_MaskCopyMat.Equals(null))&&m_Settings.maskCopyMaterial!=null)           m_MaskCopyMat=new Material(m_Settings.maskCopyMaterial);
        if ((m_BloomMat   ==null||m_BloomMat.Equals(null))   &&m_Settings.bloomMaterial!=null)              m_BloomMat   =new Material(m_Settings.bloomMaterial);
        if ((m_ColorMat   ==null||m_ColorMat.Equals(null))   &&m_Settings.colorAdjustmentsMaterial!=null)   m_ColorMat   =new Material(m_Settings.colorAdjustmentsMaterial);
        if ((m_CompMat    ==null||m_CompMat.Equals(null))    &&m_Settings.sceneCompositeMaterial!=null)      m_CompMat    =new Material(m_Settings.sceneCompositeMaterial);
        if ((m_AcesMat    ==null||m_AcesMat.Equals(null))    &&m_Settings.acesToneMappingMaterial!=null)     m_AcesMat    =new Material(m_Settings.acesToneMappingMaterial);
    }

    public void Dispose()
    {
        m_CharRT?.Release(); m_CoverageRT?.Release(); m_CoverageSrcRT?.Release();
        m_BloomRT1?.Release(); m_BloomRT2?.Release();
        m_TempRT?.Release(); m_AdjustedRT?.Release(); m_SceneRT?.Release();
        CoreUtils.Destroy(m_MaskCopyMat); CoreUtils.Destroy(m_BloomMat);
        CoreUtils.Destroy(m_ColorMat); CoreUtils.Destroy(m_CompMat); CoreUtils.Destroy(m_AcesMat);
        // ── TDR FIX 1: 释放缓存的 Coverage 材质 ──
        CoreUtils.Destroy(m_CoverageExtractMat);
        CoreUtils.Destroy(m_CoverageDilateMat);
    }
}
