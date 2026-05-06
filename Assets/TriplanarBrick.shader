Shader "Custom/Triplanar"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _Scale("Scale", Float) = 1.0
    }
        SubShader
        {
            Tags { "RenderType" = "Opaque" }
            CGPROGRAM
            #pragma surface surf Lambert
            sampler2D _MainTex;
            float _Scale;

            struct Input { float3 worldPos; float3 worldNormal; };

            void surf(Input IN, inout SurfaceOutput o)
            {
                float3 n = abs(IN.worldNormal);
                float2 uvX = IN.worldPos.zy * _Scale;
                float2 uvY = IN.worldPos.xz * _Scale;
                float2 uvZ = IN.worldPos.xy * _Scale;
                fixed4 col = tex2D(_MainTex, uvX) * n.x
                           + tex2D(_MainTex, uvY) * n.y
                           + tex2D(_MainTex, uvZ) * n.z;
                o.Albedo = col.rgb;
            }
            ENDCG
        }
}