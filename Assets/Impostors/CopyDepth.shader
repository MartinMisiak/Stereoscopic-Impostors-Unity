Shader "CustomShaders/CopyDepth"
{
        SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline" = "UniversalPipeline"}
        LOD 100
        Cull Off ZWrite On ZTest Always
        Pass
        {
            Name "ColorBlitPass"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionHCS   : POSITION;
                float2 uv           : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4  positionCS  : SV_POSITION;
                float2  uv          : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            struct fragOutput
            {
                half4 color : SV_Target;
                float depth : SV_Depth;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // Note: The pass is setup with a mesh already in clip
                // space, that's why, it's enough to just output vertex
                // positions
                output.positionCS = float4(input.positionHCS.xyz, 1.0);

                #if UNITY_UV_STARTS_AT_TOP
                output.positionCS.y *= -1;
                #endif

                output.uv = input.uv;
                return output;
            }
            
            TEXTURE2D_X(_CameraDepthTexture);

            // Depth values created on DX11/12 PS, XBOX, Metal have reversed direction (1 - near; 0 - far)
            // This function reverses the depth values if necessary depending on the used platform to 0[near] - 1[far]
            float PlatformAgnosticDepthConversion(float inputDepth)
            {
                #if defined(UNITY_REVERSED_Z)
                    return 1.0f - inputDepth;
                #else
                    return inputDepth;
                #endif
            }

            // Dilate silhouette edges of the depth map by a few pixels. This allows better AA during raymarching for silhouettes
            float dilateSilhouetteDepth(float inputDepth, int2 sampleCoords)
            {
                const float background = 0.999999;
                const int  filterWidth = 5;
                float result = inputDepth;

                if (inputDepth > background)
                {
                    uint validDepthSamples = 1;

                    for (int x = -filterWidth; x <= filterWidth; x++)
                    {
                        for (int y = -filterWidth; y <= filterWidth; y++)
                        {
                            int2 currentFetchCoords = int2(sampleCoords.x + x, sampleCoords.y + y);
                            float currentDepth = PlatformAgnosticDepthConversion(LOAD_TEXTURE2D_X(_CameraDepthTexture, currentFetchCoords));
                            if (currentDepth < background)
                            {
                                result += currentDepth;
                                validDepthSamples++;
                            }
                        }
                    }

                    result /= validDepthSamples;
                }

                return result;
            }

            fragOutput frag (Varyings input)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                int2 sampleCoords  = int2(input.uv.x * _ScreenSize.x, input.uv.y * _ScreenSize.y);
                float depthCenter  = PlatformAgnosticDepthConversion( LOAD_TEXTURE2D_X(_CameraDepthTexture, sampleCoords).r);
                float dilatedDepth = dilateSilhouetteDepth(depthCenter, sampleCoords);

                fragOutput output;
                output.color = float4(0,0,0,0); // Does not matter, as it gets overwritten later by Unity when copying the CameraColorAttachment into the camera.targetTexture
                output.depth = dilatedDepth;
                return output;
            }
            ENDHLSL
        }
    }
}
