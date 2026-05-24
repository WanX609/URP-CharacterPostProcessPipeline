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

        desc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
        desc.width  = Mathf.Max(1, desc.width  / 2);
        desc.height = Mathf.Max(1, desc.height / 2);
        RenderingUtils.ReAllocateIfNeeded(ref m_BloomRT1, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name:"_BloomRT1");
        RenderingUtils.ReAllocateIfNeeded(ref m_BloomRT2, desc, FilterMode.Bilinear, TextureWrapMode.Clamp, name:"_BloomRT2");
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        if (m_Settings.characterLayer.value == 0) return;
        EnsureMaterials();

        CommandBuffer cmd = CommandBufferPool.Get("CharacterPostProcess");
        RTHandle cam = renderingData.cameraData.renderer.cameraColorTargetHandle;

        // ── 1. 第二相机 RT → 覆盖提取 ──
        if (m_ExtCoverageRT != null)
        {
            cmd.CopyTexture(m_ExtCoverageRT, m_CoverageSrcRT.rt);
            var extShader = Shader.Find("Hidden/Character/CoverageExtract");
            if (extShader != null)
            {
                var em = new Material(extShader);
                Blitter.BlitCameraTexture(cmd, m_CoverageSrcRT, m_CoverageRT, em, 0);
                CoreUtils.Destroy(em);
            }
            var dilShader = Shader.Find("Hidden/Character/CoverageDilate");
            if (dilShader != null)
            {
                var dm = new Material(dilShader);
                for (int i = 0; i < 2; i++)
                {
                    Blitter.BlitCameraTexture(cmd, m_CoverageRT, m_TempRT);
                    Blitter.BlitCameraTexture(cmd, m_TempRT, m_CoverageRT, dm, 0);
                }
                CoreUtils.Destroy(dm);
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
        m_BloomMat.SetFloat(s_BloomThresholdId, m_Settings.bloomThreshold);
        m_BloomMat.SetFloat(s_BloomSoftKneeId,  m_Settings.bloomSoftKnee);
        m_BloomMat.SetTexture(s_CoverageTexId, Texture2D.whiteTexture);
        Blitter.BlitCameraTexture(cmd, m_CharRT, m_BloomRT1, m_BloomMat, 0);

        RTHandle ks = m_BloomRT1, kd = m_BloomRT2;
        int iters = Mathf.Clamp(m_Settings.bloomKawaseIterations, 0, 4);
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
        m_BloomMat.SetFloat(s_BloomIntensityId, m_Settings.bloomIntensity);
        m_BloomMat.SetTexture(s_BloomTexId, ks);
        Blitter.BlitCameraTexture(cmd, m_CharRT, m_TempRT, m_BloomMat, 3);
        }
        else Blitter.BlitCameraTexture(cmd, m_CharRT, m_TempRT);

        // ── 5. Color Adjust ──
        if (m_Settings.enableColorAdjustments)
        {
        m_ColorMat.SetFloat(s_SaturationId, m_Settings.saturation);
        m_ColorMat.SetFloat(s_VibranceId,   m_Settings.vibrance);
        m_ColorMat.SetFloat(s_HueShiftId,   m_Settings.hueShift);
        m_ColorMat.SetColor(s_ColorFilterId, m_Settings.colorFilter);
        if (m_Settings.useHighQualityHueShift) m_ColorMat.EnableKeyword("_HUE_METHOD_OKLAB");
        else m_ColorMat.DisableKeyword("_HUE_METHOD_OKLAB");
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
        m_AcesMat.SetFloat(s_ToneMapStartId, m_Settings.toneMapStart);
        m_AcesMat.SetFloat(s_ToneMapEndId,   m_Settings.toneMapEnd);
        m_AcesMat.SetFloat(s_ExposureId, m_Settings.exposure);
        m_AcesMat.SetFloat(s_HueProtectionStrengthId,  m_Settings.hueProtectionStrength);
        m_AcesMat.SetFloat(s_HueProtectionThresholdId, m_Settings.hueProtectionThreshold);
        Blitter.BlitCameraTexture(cmd, m_TempRT, m_SceneRT, m_AcesMat, 0);
        Blitter.BlitCameraTexture(cmd, m_SceneRT, cam);
        }

        context.ExecuteCommandBuffer(cmd);
        CommandBufferPool.Release(cmd);
    }

    private RenderTexture m_ExtCoverageRT;
    public void SetCoverageRT(RenderTexture rt) => m_ExtCoverageRT = rt;

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
    }
}
