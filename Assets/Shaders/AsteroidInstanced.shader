// AsteroidInstanced.shader
// Reads per-instance float4s (xyz = world position, w = scale) from the compact buffer
// written by OrbitCompute.compute. World position is reconstructed as:
//   worldPos = vertex.xyz * scale + position
// Since the TRS has no rotation, world normals equal object normals — no matrix multiply needed.

Shader "Custom/AsteroidInstanced"
{
    Properties
    {
        _MainTex ("Albedo Texture", 2D) = "white" {}
        _Color   ("Tint", Color) = (0.7, 0.7, 0.7, 1)
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.5    // required for StructuredBuffer in vertex shader

            #include "UnityCG.cginc"

            // Compact LOD0 buffer: float4 per visible close asteroid (xyz = world pos, w = scale).
            // Written each frame by OrbitCompute.compute.
            StructuredBuffer<float4> _MatricesLOD0;

            sampler2D _MainTex;
            half4     _Color;   // fixed4 is not a native Metal type; half4 is the correct low-precision type on Apple Silicon

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv     : TEXCOORD0;
            };

            // Set globally each frame by SunController.cs
            float3 _SunWorldPos;
            float  _SunAmbient;

            struct v2f
            {
                float4 pos         : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 worldNormal : TEXCOORD1;
                float3 worldPos    : TEXCOORD2;
            };

            v2f vert(appdata v, uint instanceID : SV_InstanceID)
            {
                float4 inst     = _MatricesLOD0[instanceID];
                float3 worldPos = v.vertex.xyz * inst.w + inst.xyz;

                v2f o;
                o.pos         = mul(UNITY_MATRIX_VP, float4(worldPos, 1.0));
                o.uv          = v.uv;
                o.worldNormal = normalize(v.normal); // uniform scale + no rotation: object normal == world normal
                o.worldPos    = worldPos;
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                half4 texColor = tex2D(_MainTex, i.uv) * _Color;

                // Diffuse shading toward the actual sun position
                float3 lightDir = normalize(_SunWorldPos - i.worldPos);
                float  diff     = saturate(dot(normalize(i.worldNormal), lightDir));

                return texColor * (_SunAmbient + diff * (1.0 - _SunAmbient));
            }
            ENDCG
        }
    }
}
