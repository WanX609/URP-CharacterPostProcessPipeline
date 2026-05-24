Shader "Hidden/Character/CoverageDilate"
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

            float4 _BlitTexture_TexelSize;

            float4 Frag(Varyings input) : SV_Target
            {
                float2 ts = _BlitTexture_TexelSize.xy;
                float2 uv = input.texcoord;
                // 手写展开 3×3 max — SM3.0 不会静默失败
                #define S(x,y) SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + ts * float2(x,y)).r
                float c = S(0,0);
                c = max(c, S(-1,-1)); c = max(c, S(0,-1)); c = max(c, S(1,-1));
                c = max(c, S(-1, 0));                     c = max(c, S(1, 0));
                c = max(c, S(-1, 1));  c = max(c, S(0, 1));  c = max(c, S(1, 1));
                #undef S
                return c;
            }
            ENDHLSL
        }
    }
}
