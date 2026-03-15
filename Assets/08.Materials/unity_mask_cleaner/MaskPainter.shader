Shader "Hidden/MaskPainter"
{
    Properties
    {
        _MainTex ("Previous Mask", 2D) = "black" {}
        _CleanableTex ("Cleanable Mask", 2D) = "white" {}
        _UseCleanableTex ("Use Cleanable Mask", Float) = 0
        _BrushUV ("Brush UV", Vector) = (0,0,0,0)
        _BrushSize ("Brush Size", Float) = 0.05
        _BrushHardness ("Brush Hardness", Range(0,1)) = 0.8
        _BrushStrength ("Brush Strength", Range(0,1)) = 1.0
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            sampler2D _CleanableTex;
            float4 _BrushUV;
            float _BrushSize;
            float _BrushHardness;
            float _BrushStrength;
            float _UseCleanableTex;

            fixed4 frag(v2f_img i) : SV_Target
            {
                float cleanable = lerp(1.0, tex2D(_CleanableTex, i.uv).r, saturate(_UseCleanableTex));
                float oldMask = min(tex2D(_MainTex, i.uv).r, cleanable);

                float radius = max(_BrushSize, 1e-5);
                float inner = radius * min(_BrushHardness, 0.999);
                float dist = distance(i.uv, _BrushUV.xy);

                float brush = 1.0 - smoothstep(inner, radius, dist);
                brush *= _BrushStrength;
                brush *= cleanable;

                float result = max(oldMask, brush);
                return fixed4(result, result, result, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
