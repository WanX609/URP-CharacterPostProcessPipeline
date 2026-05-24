Shader "Unlit/ColorAdjustments"
{
    Properties
    {
        _Saturation("Saturation", Range(0,2)) = 1.0
        _Vibrance("Vibrance", Range(-1,1)) = 0.0
        [KeywordEnum(YCoCg,OKLab)] _HUE_METHOD("Hue Method", Float) = 0
        _HueShift("Hue Shift", Range(-0.5,0.5)) = 0.0
        _ColorFilter("Color Filter", Color) = (1,1,1,1)
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            Name "ColorAdjustments"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _HUE_METHOD_YCOCG _HUE_METHOD_OKLAB
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

            half _Saturation, _Vibrance, _HueShift;
            half3 _ColorFilter;

            // ── RGB ↔ OKLab（感知均匀空间, L/a/b 解耦）──

            float3 RGBtoOKLab(float3 c)
            {
                float l = 0.4122214708*c.r + 0.5363325363*c.g + 0.0514459929*c.b;
                float m = 0.2119034982*c.r + 0.6806995451*c.g + 0.1073969566*c.b;
                float s = 0.0883024619*c.r + 0.2817188376*c.g + 0.6299787005*c.b;
                l = sign(l)*pow(abs(l),1.0/3.0); m = sign(m)*pow(abs(m),1.0/3.0); s = sign(s)*pow(abs(s),1.0/3.0);
                return float3(
                    0.2104542553*l + 0.7936177850*m - 0.0040720468*s,
                    1.9779984951*l - 2.4285922050*m + 0.4505937099*s,
                    0.0259040371*l + 0.7827717662*m - 0.8086757660*s
                );
            }

            float3 OKLabtoRGB(float3 c)
            {
                float l = c.x + 0.3963377774*c.y + 0.2158037573*c.z;
                float m = c.x - 0.1055613458*c.y - 0.0638541728*c.z;
                float s = c.x - 0.0894841775*c.y - 1.2914855480*c.z;
                l = l*l*l; m = m*m*m; s = s*s*s;
                return float3(
                     4.0767416621*l - 3.3077115913*m + 0.2309699292*s,
                    -1.2684380046*l + 2.6097574011*m - 0.3413193965*s,
                    -0.0041960863*l - 0.7034186147*m + 1.7076147010*s
                );
            }

            // ── YCoCg hue rotation（仅 HueShift≠0 时使用）──

            half3 HueShiftYCoCg(half3 rgb, half shift)
            {
                half  Y =  0.25*rgb.r + 0.5*rgb.g + 0.25*rgb.b;
                half Co =  0.5 *rgb.r           -  0.5 *rgb.b;
                half Cg = -0.25*rgb.r + 0.5*rgb.g - 0.25*rgb.b;
                half h = atan2(Cg, Co) + shift * 6.2831853;
                half m = sqrt(Co*Co + Cg*Cg);
                Co = m * cos(h); Cg = m * sin(h);
                return half3(Y+Co-Cg, Y+Cg, Y-Co-Cg);
            }

            // ── OKLab hue rotation ──
            #if defined(_HUE_METHOD_OKLAB)
            half3 HueShiftOKLab(half3 c, half shift)
            {
                half L = 0.41222147*c.r + 0.53633254*c.g + 0.05144599*c.b;
                half M = 0.21190350*c.r + 0.68069955*c.g + 0.10739696*c.b;
                half S = 0.08830246*c.r + 0.28171884*c.g + 0.62997870*c.b;
                L = sign(L)*pow(abs(L),1.0/3.0);
                M = sign(M)*pow(abs(M),1.0/3.0);
                S = sign(S)*pow(abs(S),1.0/3.0);
                half la = 0.21045426*L + 0.79361779*M - 0.00407205*S;
                half ca = 1.97799850*L - 2.42859221*M + 0.45059371*S;
                half cb = 0.02590404*L + 0.78277177*M - 0.80867577*S;
                half h  = atan2(cb, ca) + shift * 6.2831853;
                half mg = sqrt(ca*ca + cb*cb);
                ca = mg * cos(h); cb = mg * sin(h);
                L = la + 0.39633778*ca + 0.21580376*cb;
                M = la - 0.10556135*ca - 0.06385417*cb;
                S = la - 0.08948418*ca - 1.29148555*cb;
                L = L*L*L; M = M*M*M; S = S*S*S;
                return half3( 4.07674166*L - 3.30771159*M + 0.23096993*S,
                             -1.26843800*L + 2.60975740*M - 0.34131940*S,
                             -0.00419609*L - 0.70341861*M + 1.70761470*S);
            }
            #endif

            half4 Frag(Varyings input) : SV_Target
            {
                float3 rgb = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, input.texcoord).rgb;

                // Bypass: 所有参数为默认值时直接透传，零精度损失
                bool bypass = _Saturation == 1.0 && _Vibrance == 0.0 &&
                              abs(_HueShift) < 0.0001 && all(_ColorFilter == 1.0);
                if (!bypass)
                {
                    // ── Saturation（OKLab 空间 — 无损明度/色相）──
                    float3 lab = RGBtoOKLab(rgb);
                    lab.yz *= _Saturation;

                    // ── Vibrance（低饱和区生效更强）──
                    float chroma = sqrt(lab.y*lab.y + lab.z*lab.z);
                    float weight = saturate(1.0 - chroma / max(chroma + 0.3, 0.01));
                    lab.yz += lab.yz * _Vibrance * weight;

                    rgb = OKLabtoRGB(lab);

                    // ── Hue Shift ──
                    if (abs(_HueShift) > 0.0001)
                    {
                        #if defined(_HUE_METHOD_OKLAB)
                            rgb = (float3)HueShiftOKLab((half3)rgb, _HueShift);
                        #else
                            rgb = (float3)HueShiftYCoCg((half3)rgb, _HueShift);
                        #endif
                    }
                    rgb *= _ColorFilter;
                }

                return half4((half3)rgb, 0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
