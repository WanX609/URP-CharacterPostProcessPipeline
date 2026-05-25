Shader "Hidden/Character/SceneComposite"
{
    Properties
    {
        [HideInInspector] _CoverageTex("Coverage", 2D) = "white" {}
        [HideInInspector] _SceneTex("Scene", 2D) = "black" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            Name "SceneComposite"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D(_CoverageTex); SAMPLER(sampler_CoverageTex);
            TEXTURE2D(_SceneTex);    SAMPLER(sampler_SceneTex);

            float4 Frag(Varyings input) : SV_Target
            {
                float coverage = SAMPLE_TEXTURE2D(_CoverageTex, sampler_CoverageTex, input.texcoord).r;
                // TDR FIX: 极端覆盖值快速路径 — 跳过无用采样
                if (coverage <= 0.001) { float3 sc = SAMPLE_TEXTURE2D(_SceneTex, sampler_SceneTex, input.texcoord).rgb; return float4(sc, 1); }
                if (coverage >= 0.999) { float3 ch = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord).rgb; return float4(ch, 1); }
                float3 character = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord).rgb;
                float3 scene     = SAMPLE_TEXTURE2D(_SceneTex, sampler_SceneTex, input.texcoord).rgb;
                return float4(lerp(scene, character, coverage), 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
