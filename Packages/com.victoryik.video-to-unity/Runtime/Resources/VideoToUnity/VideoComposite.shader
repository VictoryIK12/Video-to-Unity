Shader "VideoToUnity/Composite"
{
    Properties
    {
        [PerRendererData] _MainTex ("Video", 2D) = "black" {}
        [HideInInspector] _VideoTex ("Decoder texture", 2D) = "black" {}
        [HideInInspector] _UseVideoTexture ("Dedicated decoder texture", Float) = 0
        _Tint ("Tint", Color) = (1,1,1,1)
        _Opacity ("Opacity", Range(0,1)) = 1
        _Mode ("Alpha mode", Float) = 0
        _Premultiplied ("Source premultiplied", Float) = 0
        _KeyColor ("Key color (sRGB)", Color) = (0,1,0,1)
        _Threshold ("Key threshold", Range(0,1)) = 0.05
        _Softness ("Key softness", Range(0.001,1)) = 0.08
        _FlipY ("Flip Y", Float) = 0
        _CorrectPackedAlphaGamma ("Undo packed alpha RGB gamma", Float) = 1
        _AspectScale ("Aspect UV scale", Vector) = (1,1,0,0)
        [HideInInspector] _DstBlend ("Destination blend", Float) = 10
        [HideInInspector] _StencilComp ("Stencil Comparison", Float) = 8
        [HideInInspector] _Stencil ("Stencil ID", Float) = 0
        [HideInInspector] _StencilOp ("Stencil Operation", Float) = 0
        [HideInInspector] _StencilWriteMask ("Stencil Write Mask", Float) = 255
        [HideInInspector] _StencilReadMask ("Stencil Read Mask", Float) = 255
        [HideInInspector] _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" }
        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend One [_DstBlend], One OneMinusSrcAlpha
        ColorMask [_ColorMask]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2f
            {
                float4 vertex : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float4 local : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };
            sampler2D _MainTex, _VideoTex;
            float4 _MainTex_TexelSize, _VideoTex_TexelSize, _Tint, _KeyColor, _ClipRect, _AspectScale;
            float _UseVideoTexture;
            float _Opacity, _Mode, _Premultiplied, _Threshold, _Softness, _FlipY, _CorrectPackedAlphaGamma;

            v2f vert(appdata input)
            {
                v2f output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.local = input.vertex;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.color = input.color;
                output.uv = input.uv;
                return output;
            }

            float4 SampleVideo(float2 uv)
            {
                if (_UseVideoTexture > 0.5) return tex2D(_VideoTex, uv);
                return tex2D(_MainTex, uv);
            }

            float4 frag(v2f input) : SV_Target
            {
                // Hidden views skip texture work without changing CanvasRenderer's masking/culling state.
                clip(_Opacity - 0.000001);
                float2 uv = (input.uv - 0.5) * _AspectScale.xy + 0.5;
                float inside = step(0, uv.x) * step(uv.x, 1) * step(0, uv.y) * step(uv.y, 1);
                if (_FlipY > 0.5) uv.y = 1 - uv.y;
                float2 halfTexel = abs(_UseVideoTexture > 0.5 ? _VideoTex_TexelSize.xy : _MainTex_TexelSize.xy) * 0.5;
                float4 color;
                float alpha;
                if (_Mode > 3.5 && _Mode < 4.5)
                {
                    // RGB left, grayscale matte right. Clamp inside each panel to avoid seam bleed.
                    float2 colorUV = clamp(float2(uv.x * 0.5, uv.y), halfTexel, float2(0.5, 1) - halfTexel);
                    float2 matteUV = clamp(float2(0.5 + uv.x * 0.5, uv.y), float2(0.5, 0) + halfTexel, 1 - halfTexel);
                    color = SampleVideo(colorUV);
                    alpha = SampleVideo(matteUV).r;
                    #ifndef UNITY_COLORSPACE_GAMMA
                    if (_CorrectPackedAlphaGamma > 0.5) alpha = LinearToGammaSpaceExact(alpha);
                    #endif
                }
                else
                {
                    color = SampleVideo(clamp(uv, halfTexel, 1 - halfTexel));
                    alpha = color.a;
                }

                float3 rgb = color.rgb;
                float sourcePremultiplied = _Premultiplied;
                if (_Mode > 1.5 && _Mode < 3.5)
                {
                    float3 displayRGB = rgb;
                    #ifndef UNITY_COLORSPACE_GAMMA
                    displayRGB = LinearToGammaSpace(displayRGB);
                    #endif
                    float difference = _Mode < 2.5 ? max(displayRGB.r, max(displayRGB.g, displayRGB.b))
                        : length(displayRGB - _KeyColor.rgb) / 1.7320508;
                    alpha = smoothstep(_Threshold, _Threshold + max(_Softness, 0.001), difference);
                    // Estimate foreground from a flattened background. This cannot reconstruct lost alpha.
                    float3 background = _Mode < 2.5 ? float3(0,0,0) : _KeyColor.rgb;
                    #ifndef UNITY_COLORSPACE_GAMMA
                    background = GammaToLinearSpace(background);
                    #endif
                    rgb = saturate((rgb - background * (1 - alpha)) / max(alpha, 0.00001));
                    sourcePremultiplied = 0;
                }
                if (_Mode > 4.5) { alpha = 1; sourcePremultiplied = 0; }

                float coverage = inside * _Opacity * _Tint.a * input.color.a;
                #ifdef UNITY_UI_CLIP_RECT
                coverage *= UnityGet2DClipping(input.local.xy, _ClipRect);
                #endif
                float3 result = rgb * _Tint.rgb * input.color.rgb;
                if (sourcePremultiplied < 0.5) result *= alpha;
                result *= coverage;
                float outAlpha = saturate(alpha * coverage);
                #ifdef UNITY_UI_ALPHACLIP
                clip(outAlpha - 0.001);
                #endif
                // Additive light must not reduce the destination or accumulate opaque alpha.
                if (_Mode > 0.5 && _Mode < 1.5) outAlpha = 0;
                return float4(result, outAlpha);
            }
            ENDCG
        }
    }
}
