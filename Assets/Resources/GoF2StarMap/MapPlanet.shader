// GoF2/MapPlanet: the star map's system-view planets lit like StarMap::initStarSystem lights them: one light at the sun's
// position (Engine::LightSetLightPosition, the fixed-function point light: no distance attenuation), ambient 0.2 and a
// diffuse term toward the sun per pixel, so every planet's day side faces its star. URP's point lights fade with
// distance (the outer planets went dark) and the remake's old directional light lit every planet from the same side.
// _StarMapSunPos (world) is a global set by StarMap. In Resources so a build keeps it (StarMap loads it by name).
Shader "GoF2/MapPlanet"
{
    Properties
    {
        _BaseMap ("Texture", 2D) = "white" {}
        _BaseColor ("Color", Color) = (1, 1, 1, 1)
        _Ambient ("Ambient", Float) = 0.2
        _Diffuse ("Diffuse", Float) = 1.6
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half _Ambient, _Diffuse;
            CBUFFER_END
            float4 _StarMapSunPos;

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float3 normalWS : TEXCOORD1; float3 positionWS : TEXCOORD2; };

            Varyings vert(Attributes i)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(i.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(i.normalOS);
                o.uv = TRANSFORM_TEX(i.uv, _BaseMap);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                half4 t = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv) * _BaseColor;
                float3 toSun = normalize(_StarMapSunPos.xyz - i.positionWS);
                half lit = _Ambient + _Diffuse * saturate(dot(normalize(i.normalWS), toSun));
                return half4(t.rgb * lit, 1);
            }
            ENDHLSL
        }
    }
}
