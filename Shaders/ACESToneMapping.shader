Shader "Unlit/ACESToneMapping"
{
    Properties
    {
        _Exposure("Exposure", Range(0.1,10)) = 1.0
        _ToneMapStart("ToneMap Start Lum", Range(0,5)) = 0.8
        _ToneMapEnd("ToneMap Full Lum", Range(0,10)) = 1.5
        _HueProtectionStrength("Protection Strength", Range(0,1)) = 0.4
        [HideInInspector] _HueProtectionThreshold("Protection Threshold", Range(0.01,0.2)) = 0.05
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            Name "ACESToneMapping"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            half _Exposure, _ToneMapStart, _ToneMapEnd;
            half _HueProtectionStrength, _HueProtectionThreshold;

            half3 ACES(half3 x)
            {
                half a=2.51, b=0.03, c=2.43, d=0.59, e=0.14;
                return saturate((x*(a*x+b))/(x*(c*x+d)+e));
            }

            half Hue(half3 rgb)
            {
                half M=max(rgb.r,max(rgb.g,rgb.b));
                half m=min(rgb.r,min(rgb.g,rgb.b));
                half c=M-m;
                if(c<1e-5)return 0;
                half h;
                if(M==rgb.r)h=(rgb.g-rgb.b)/c;
                else if(M==rgb.g)h=(rgb.b-rgb.r)/c+2;
                else h=(rgb.r-rgb.g)/c+4;
                return frac(h/6);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float3 rgb = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord).rgb;
                float3 exposed = rgb * _Exposure;

                // 仅对 HDR 区域做 ACES，保护中低调
                float lum = dot(exposed, float3(0.2126,0.7152,0.0722));
                float t   = smoothstep(_ToneMapStart, _ToneMapEnd, lum);

                if (t < 0.0001) return half4((half3)rgb, 1); // 完全不经 ACES

                half3 acesIn = (half3)exposed;
                half3 acesOut = ACES(acesIn);

                if (t > 0.9999) return half4(acesOut, 1); // 完整 ACES，无混合开销

                // 过渡区：混合 + 色相保护
                half3 mapped = lerp(acesIn, acesOut, t);

                half  origHue  = Hue(acesIn);
                half  origLum  = dot(acesIn, half3(0.2126,0.7152,0.0722));
                half  mappedHue= Hue(mapped);
                half  diff     = abs(origHue-mappedHue);
                diff = min(diff, 1-diff);
                half blend = smoothstep(_HueProtectionThreshold, _HueProtectionThreshold*3, diff) * _HueProtectionStrength;
                half mappedLum = dot(mapped, half3(0.2126,0.7152,0.0722));
                half3 hueFixed = mappedLum * (acesIn / max(origLum, 1e-5));

                return half4(lerp(mapped, hueFixed, blend), 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
