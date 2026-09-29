// All graph edges in one mesh (GraphLoader). Each arc point is duplicated into two
// vertices that the vertex shader pushes sideways, perpendicular to both the arc
// and the view direction, so every edge is a camera-facing ribbon (like a
// LineRenderer) without any per-frame CPU work.
//
// Vertex layout written by GraphLoader.BuildEdgeMesh:
//   POSITION  arc point (world space; the edge object has an identity transform)
//   NORMAL    arc direction at that point
//   TEXCOORD0 x = side (-1 / +1), y = ribbon width in metres (0 hides the edge)
//   COLOR     edge colour (LDR); _Tint multiplies it, >1 for an HDR glow with Bloom
Shader "FlightlyVR/EdgeRibbon"
{
    Properties
    {
        [HDR] _Tint ("Tint (HDR multiplier)", Color) = (1, 1, 1, 1)
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 5  // SrcAlpha
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10 // OneMinusSrcAlpha (1 = One: additive)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "EdgeRibbon"
            Tags { "LightMode" = "UniversalForward" }

            Blend [_SrcBlend] [_DstBlend]
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Tint;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 tangentOS  : NORMAL;
                float2 uv         : TEXCOORD0;
                half4 color       : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color       : COLOR;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float3 positionWS = TransformObjectToWorld(v.positionOS);
                float3 tangentWS = TransformObjectToWorldDir(v.tangentOS, false);
                // Per eye in stereo, so each eye sees a ribbon facing it.
                float3 toCamera = GetCameraPositionWS() - positionWS;
                float3 side = cross(tangentWS, toCamera);
                float sideLength = length(side);
                // Degenerate (zero-length arc, or looking straight along it): keep the vertex in place.
                side = sideLength > 1e-6 ? side / sideLength : float3(0, 0, 0);
                positionWS += side * (v.uv.x * 0.5 * v.uv.y);

                o.positionCS = TransformWorldToHClip(positionWS);
                o.color = v.color * _Tint;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                return i.color;
            }
            ENDHLSL
        }
    }
}
