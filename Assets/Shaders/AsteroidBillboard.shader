// AsteroidBillboard.shader
// Camera-facing quad billboard for LOD1 asteroids.
// Reads per-instance float4s (xyz = world position, w = scale) from the compact LOD1 buffer.
// The quad is oriented toward the camera using the view matrix right/up axes so no CPU work
// is needed per frame — the GPU reconstructs the full world position in the vertex shader.

Shader "Custom/AsteroidBillboard"
{
    Properties
    {
        _MainTex       ("Impostor Texture", 2D) = "white" {}
        _Color         ("Tint", Color) = (0.7, 0.7, 0.7, 1)
        _Cutoff        ("Alpha Cutoff", Range(0, 1)) = 0.5
        _BillboardScale("Billboard Scale", Float) = 1.0
    }

    SubShader
    {
        Tags { "RenderType"="TransparentCutout" "Queue"="AlphaTest" }
        LOD 100
        Cull Off   // render both sides so the quad is visible from any angle

        Pass
        {
            CGPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma target   4.5    // required for StructuredBuffer in vertex shader

            #include "UnityCG.cginc"

            // Compact LOD1 buffer: float4 per visible far asteroid (xyz = world pos, w = scale).
            StructuredBuffer<float4> _Matrices;

            // Set globally each frame by SunController.cs
            float3 _SunWorldPos;
            float  _SunAmbient;

            sampler2D _MainTex;
            half4     _Color;   // fixed4 is not a native Metal type; half4 is the correct low-precision type on Apple Silicon
            float     _Cutoff;
            float     _BillboardScale;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos    : SV_POSITION;
                float2 uv     : TEXCOORD0;
                float3 center : TEXCOORD1;  // asteroid world-space center, for lighting
            };

            v2f vert(appdata v, uint instanceID : SV_InstanceID)
            {
                float4 inst   = _Matrices[instanceID];
                float3 center = inst.xyz;
                float  scale  = inst.w * _BillboardScale;

                // Extract camera right and up from the view matrix so the quad always faces the camera.
                float3 right = UNITY_MATRIX_V[0].xyz;
                float3 up    = UNITY_MATRIX_V[1].xyz;

                // v.vertex.xy are the local quad offsets (±0.5 from CreateBillboardQuad).
                float3 worldPos = center + (v.vertex.x * right + v.vertex.y * up) * scale;

                v2f o;
                o.pos    = mul(UNITY_MATRIX_VP, float4(worldPos, 1.0));
                o.uv     = v.uv;
                o.center = center;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Approximate lighting: how much does the sun direction align with
                // the camera view direction? If the sun is roughly behind the camera,
                // we see the lit face of the asteroid (bright). If in front, dark side.
                float3 toSun   = normalize(_SunWorldPos - i.center);
                float3 viewDir = normalize(_WorldSpaceCameraPos - i.center);
                float  diff    = saturate(dot(viewDir, toSun));
                float  light   = _SunAmbient + diff * (1.0 - _SunAmbient);

                half4 col = tex2D(_MainTex, i.uv) * _Color;
                col.rgb   *= light;
                clip(col.a - _Cutoff);
                return col;
            }
            ENDCG
        }
    }
}
