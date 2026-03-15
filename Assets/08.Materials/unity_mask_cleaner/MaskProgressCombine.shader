Shader "Hidden/MaskProgressCombine"
{
    Properties
    {
        _MainTex ("Current Mask", 2D) = "black" {}
        _CleanableTex ("Cleanable Mask", 2D) = "white" {}
        _UseCleanableTex ("Use Cleanable Mask", Float) = 0
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
            float _UseCleanableTex;

            fixed4 frag(v2f_img i) : SV_Target
            {
                float cleanable = lerp(1.0, tex2D(_CleanableTex, i.uv).r, saturate(_UseCleanableTex));
                float cleaned = min(tex2D(_MainTex, i.uv).r, cleanable);
                return fixed4(cleaned, cleanable, 0.0, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
