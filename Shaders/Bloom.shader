Shader "Unlit/Bloom"
{
    Properties
    {
        [HideInInspector] _CoverageTex("Coverage", 2D) = "white" {}
        _BloomThreshold("Threshold", Range(0,10)) = 1.0
        _BloomSoftKnee("Soft Knee", Range(0,1)) = 0.5
        [HideInInspector] _KawaseOffset("Kawase Offset", Float) = 1.0
        [HideInInspector] _BloomTex("Bloom Tex", 2D) = "black" {}
        _BloomIntensity("Intensity", Range(0,5)) = 1.0
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        // ── Pass 0: Prefilter ──
        Pass
        {
            Name "BloomPrefilter"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragPrefilter
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D(_CoverageTex); SAMPLER(sampler_CoverageTex);
            float _BloomThreshold, _BloomSoftKnee;

            float Knee(float L, float t, float k)
            {
                float s = clamp(L - t + k, 0, 2*k);
                return max(s*s/(4*k+1e-5), L-t) / max(L, 1e-5);
            }

            float4 FragPrefilter(Varyings input) : SV_Target
            {
                float3 c = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord).rgb;
                float  v = SAMPLE_TEXTURE2D(_CoverageTex, sampler_CoverageTex, input.texcoord).r;
                float L = dot(c, float3(0.2126, 0.7152, 0.0722));
                float k = _BloomThreshold * _BloomSoftKnee + 1e-5;
                float3 bloom = c * Knee(L, _BloomThreshold, k);
                return float4(bloom * v, v);
            }
            ENDHLSL
        }

        // ── Pass 1: Kawase Down ──
        Pass
        {
            Name "KawaseDown"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragKawase
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _KawaseOffset;
            float4 _BlitTexture_TexelSize;

            float4 FragKawase(Varyings input) : SV_Target
            {
                float2 o = _BlitTexture_TexelSize.xy * _KawaseOffset;
                float2 uv = input.texcoord;
                float4 s0 = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2( o.x,  o.y));
                float4 s1 = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2( o.x, -o.y));
                float4 s2 = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(-o.x,  o.y));
                float4 s3 = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(-o.x, -o.y));
                return (s0 + s1 + s2 + s3) * 0.25;
            }
            ENDHLSL
        }

        // ── Pass 2: Kawase Up ──
        Pass
        {
            Name "KawaseUp"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragKawase
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            float _KawaseOffset;
            float4 _BlitTexture_TexelSize;

            float4 FragKawase(Varyings input) : SV_Target
            {
                float2 o = _BlitTexture_TexelSize.xy * _KawaseOffset;
                float2 uv = input.texcoord;
                float4 s0 = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2( o.x,  o.y));
                float4 s1 = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2( o.x, -o.y));
                float4 s2 = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(-o.x,  o.y));
                float4 s3 = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv + float2(-o.x, -o.y));
                return (s0 + s1 + s2 + s3) * 0.25;
            }
            ENDHLSL
        }

        // ── Pass 3: Composite ──
        Pass
        {
            Name "BloomComposite"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            TEXTURE2D(_BloomTex); SAMPLER(sampler_BloomTex);
            float _BloomIntensity;

            float4 FragComposite(Varyings input) : SV_Target
            {
                float3 character = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord).rgb;
                float4 bloomPM   = SAMPLE_TEXTURE2D(_BloomTex, sampler_BloomTex, input.texcoord);
                return float4(character + bloomPM.rgb * _BloomIntensity, 0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
