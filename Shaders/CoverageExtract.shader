Shader "Hidden/Character/CoverageExtract"
{
    Properties {}
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float Frag(Varyings input) : SV_Target
            {
                float3 c = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord).rgb;
                return any(c > 1e-6) ? 1.0 : 0.0;
            }
            ENDHLSL
        }
    }
}
