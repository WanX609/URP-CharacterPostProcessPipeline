Shader "Hidden/Character/MaskedCopy"
{
    Properties
    {
        [HideInInspector] _CoverageTex("Coverage", 2D) = "white" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            Name "MaskedCopy"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D(_CoverageTex);
            SAMPLER(sampler_CoverageTex);

            float4 Frag(Varyings input) : SV_Target
            {
                float3 camColor = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord).rgb;
                float  coverage = SAMPLE_TEXTURE2D(_CoverageTex, sampler_CoverageTex, input.texcoord).r;
                return float4(camColor * coverage, 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
