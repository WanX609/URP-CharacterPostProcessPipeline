using UnityEngine;
using UnityEngine.Rendering.Universal;

public class CharacterPostProcessFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        [Header("Materials")]
        public Material bloomMaterial;
        public Material colorAdjustmentsMaterial;
        public Material sceneCompositeMaterial;
        public Material acesToneMappingMaterial;
        public Material maskCopyMaterial;

        [Header("Character")]
        public LayerMask characterLayer;

        [Header("Bloom")]
        public bool enableBloom = true;
        [Range(0f,10f)] public float bloomThreshold = 0.9f;
        [Range(0f,1f)]  public float bloomSoftKnee  = 0.6f;
        [Range(0f,5f)]  public float bloomIntensity = 1.5f;
        [Range(1,4)]    public int   bloomKawaseIterations = 3;

        [Header("Color Adjustments")]
        public bool enableColorAdjustments = true;
        [Range(0f,2f)]      public float saturation = 1.0f;
        [Range(-1f,1f)]     public float vibrance   = 0.0f;
        [Range(-0.5f,0.5f)] public float hueShift   = 0.0f;
        public Color colorFilter = Color.white;
        public bool useHighQualityHueShift = false;

        [Header("Tone Mapping")]
        public bool enableToneMapping = true;
        [Range(0f,5f)]    public float toneMapStart = 0.8f;
        [Range(0f,10f)]   public float toneMapEnd   = 1.5f;
        [Range(0.1f,10f)]  public float exposure = 1.0f;
        [Range(0f,1f)]     public float hueProtectionStrength  = 0.4f;
        [Range(0.01f,0.2f)]public float hueProtectionThreshold = 0.05f;
    }

    public Settings settings = new();
    private CharacterPostProcessPass m_Pass;
    private Camera m_CoverageCam;
    private RenderTexture m_CoverageRT;

    public override void Create()
    {
        if (m_CoverageCam == null)
        {
            var go = new GameObject("_CoverageCamera");
            go.hideFlags = HideFlags.HideAndDontSave;
            m_CoverageCam = go.AddComponent<Camera>();
            m_CoverageCam.enabled = true;
        }
        m_Pass = new CharacterPostProcessPass(settings);
        m_Pass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        var cam = renderingData.cameraData.camera;
        if (cam.cameraType != CameraType.Game) return;
        if (cam != Camera.main) return;
        if (settings.characterLayer.value == 0) return;

        // 每个主相机帧更新第二相机设置
        var mainCam = Camera.main;
        int w = renderingData.cameraData.cameraTargetDescriptor.width;
        int h = renderingData.cameraData.cameraTargetDescriptor.height;

        if (m_CoverageRT == null || m_CoverageRT.width != w || m_CoverageRT.height != h)
        {
            if (m_CoverageRT != null) Object.Destroy(m_CoverageRT);
            m_CoverageRT = new RenderTexture(w, h, 24, RenderTextureFormat.ARGBHalf);
            m_CoverageRT.name = "_CoverageCamRT";
            m_CoverageRT.filterMode = FilterMode.Bilinear;
            m_CoverageRT.wrapMode = TextureWrapMode.Clamp;
        }

        m_CoverageCam.CopyFrom(mainCam);
        m_CoverageCam.cullingMask = settings.characterLayer;
        m_CoverageCam.backgroundColor = Color.black;
        m_CoverageCam.clearFlags = CameraClearFlags.SolidColor;
        m_CoverageCam.depth = mainCam.depth - 1;
        m_CoverageCam.targetTexture = m_CoverageRT;
        m_CoverageCam.allowHDR = true;
        m_CoverageCam.allowMSAA = false;
        m_CoverageCam.allowDynamicResolution = false;
        m_CoverageCam.useOcclusionCulling = false;

        Shader.SetGlobalTexture("_CoverageCamRT", m_CoverageRT);
        m_Pass.SetCoverageRT(m_CoverageRT);

        renderer.EnqueuePass(m_Pass);
    }

    protected override void Dispose(bool disposing)
    {
        m_Pass?.Dispose();
        if (m_CoverageRT != null) Object.Destroy(m_CoverageRT);
        if (m_CoverageCam != null) Object.Destroy(m_CoverageCam.gameObject);
    }
}
