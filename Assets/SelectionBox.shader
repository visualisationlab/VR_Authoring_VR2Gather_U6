Shader "Custom/SelectionBox"
{
    // Simple unlit colour for the selection box / bracket lines. Draws on top of the scene
    // (ZTest Always) so the highlight is always clearly visible, and never writes depth.
    Properties
    {
        _Color ("Color", Color) = (1, 0.45, 0.05, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Overlay" }

        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; fixed4 color : COLOR; };
            struct v2f     { float4 pos : SV_POSITION; fixed4 color : COLOR; };

            fixed4 _Color;

            v2f vert (appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color * _Color;   // respects LineRenderer vertex colour too
                return o;
            }

            fixed4 frag (v2f i) : SV_Target { return i.color; }
            ENDCG
        }
    }

    Fallback Off
}
