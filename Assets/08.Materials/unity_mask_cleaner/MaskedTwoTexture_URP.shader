Shader "Custom/MaskedTwoTexture_URP"
{
    Properties
    {
        _DirtyTex ("Texture 1 (Dirty)", 2D) = "white" {}
        _CleanTex ("Texture 2 (Clean)", 2D) = "white" {}
        _MaskTex ("Mask", 2D) = "black" {}
        _CleanableTex ("Cleanable Mask", 2D) = "white" {}
        _UseCleanableTex ("Use Cleanable Mask", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalRenderPipeline" "RenderType"="Opaque" "Queue"="Geometry" }

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode"="UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            TEXTURE2D(_DirtyTex);
            SAMPLER(sampler_DirtyTex);

            TEXTURE2D(_CleanTex);
            SAMPLER(sampler_CleanTex);

            TEXTURE2D(_MaskTex);
            SAMPLER(sampler_MaskTex);

            TEXTURE2D(_CleanableTex);
            SAMPLER(sampler_CleanableTex);

            float _UseCleanableTex;

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 dirty = SAMPLE_TEXTURE2D(_DirtyTex, sampler_DirtyTex, input.uv);
                half4 clean = SAMPLE_TEXTURE2D(_CleanTex, sampler_CleanTex, input.uv);
                half cleanable = lerp(1.0h, SAMPLE_TEXTURE2D(_CleanableTex, sampler_CleanableTex, input.uv).r, saturate(_UseCleanableTex));
                half mask = saturate(SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, input.uv).r);
                mask = min(mask, cleanable);
                return lerp(dirty, clean, mask);
            }
            ENDHLSL
        }
    }
}
