Shader "Hidden/SDFGI/Composite"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend One One   // additive on top of the lit scene

        Pass
        {
            Name "SDFGI Composite"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_local_fragment _ _SDFGI_GBUFFER

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"
            #include "SDFGICommon.hlsl"

            #if defined(_SDFGI_GBUFFER)
            TEXTURE2D_X(_SDFGIGBuffer0);
            #endif

            float4 _SDFGIFallbackAlbedo;

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = input.texcoord;

                float depth = SampleSceneDepth(uv);

                // Skip sky
                #if UNITY_REVERSED_Z
                if (depth <= 1e-7) return 0;
                #else
                if (depth >= 1.0 - 1e-7) return 0;
                #endif

                float3 worldPos = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP);
                float3 normal = normalize(SampleSceneNormals(uv));

                #if defined(_SDFGI_GBUFFER)
                float3 albedo = SAMPLE_TEXTURE2D_X(_SDFGIGBuffer0,
                    sdfgi_linear_clamp_sampler, uv).rgb;
                #else
                float3 albedo = _SDFGIFallbackAlbedo.rgb;
                #endif

                float fade = SDFGI_VolumeFade(worldPos);
                if (fade <= 0.0) return 0;

                float3 irradiance = SDFGI_SampleIrradiance(worldPos, normal);
                float3 gi = irradiance * albedo * _SDFGIParams.x * fade;

                return float4(gi, 0.0);
            }
            ENDHLSL
        }
    }
}
