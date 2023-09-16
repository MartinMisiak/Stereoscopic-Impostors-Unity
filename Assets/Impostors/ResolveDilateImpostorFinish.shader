// This shader copies over the radiance and depth values from the accumulation atlas into the current atlas
// If the accumulation atlas uses MSAA, the values are also resolved here to a single value.
// In addition, the depth values are dilated along silhouette edges by a few pixels. 
// After this shader, the depth values remain non-linear [0-1], however the Z-direction becomes platform independent with: 0[near] and 1[far]

Shader "Hidden/ResolveDilateImpostorFinish"
{
    Properties
    {

    }
    SubShader
    {
        Cull Off ZWrite On ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile NO_MSAA MSAA_2 MSAA_4 MSAA_8

            #if defined (MSAA_2)
                #define MSAA_SAMPLES 2
            #endif
            #if defined (MSAA_4)
                #define MSAA_SAMPLES 4
            #endif
            #if defined (MSAA_8)
                #define MSAA_SAMPLES 8
            #endif

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            struct fragOutput
            {
                half4 color : SV_Target;
                float depth : SV_Depth;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            #if defined (NO_MSAA)
                Texture2D _AccumulatedRadianceTex;
                Texture2D _AccumulatedDepthTex;
                // SamplerState sampler_AccumulatedDepthTex;
            #else
                Texture2DMS<half4, MSAA_SAMPLES> _AccumulatedRadianceTex;
                Texture2DMS<float, MSAA_SAMPLES> _AccumulatedDepthTex;
            #endif
            

            ///------------------------
            uniform float4   _AtlasCoords;

            //Converts [0-1] texture coordinates into corresponding atlasCoordinates for a region defined
            //by atlasBounds
            float2 normalizedToAtlasTexels(float2 normTexCoords, float4 atlasBounds)
            {
                float2 range = float2(atlasBounds.z - atlasBounds.x, atlasBounds.w - atlasBounds.y);
                float2 newCoord = atlasBounds.xy + normTexCoords * range;
                return newCoord;
            }

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

            // Ideally this function would mimic Unities way of Resolving MSAA...
            // Pre-mult Alpha? Yes or no?...
            #if defined (MSAA_2) || defined (MSAA_4) || defined (MSAA_8)
            half4 MultisampleResolve(int numSamples, int2 sampleCoords)
            {
                float weight  = 1.0 / float(numSamples);
		        float alpha   = 0.0;

		        half4 radiance    = half4( 0.0, 0.0, 0.0, 0.0 );
                [unroll(8)]
           		for( int i = 0; i < numSamples; i++ )
		        {
			        half4 radianceSample = _AccumulatedRadianceTex.Load(sampleCoords, i);
			        radiance 			+= weight * radianceSample;
		        }

                radiance.rgb *= radiance.a;
                return radiance;
            }
            #endif

            //Dilate silhouette edges of the depth map by a few pixels. This allows better AA during raymarching for silhouettes
            float dilateSilhouetteDepth(float inputDepth, int2 sampleCoords)
            {
	            const float background  = 0.999999;
                const int  filterWidth  = 5;
                float result            = inputDepth;

	            if(inputDepth > background)
	            {
                    uint validDepthSamples = 1;

                    for(int x = -filterWidth; x <= filterWidth; x++)
                    {
                        for (int y = -filterWidth; y <= filterWidth; y++)
                        {
                            #if defined (NO_MSAA)
                                int3 currentFetchCoords  = int3(sampleCoords.x + x, sampleCoords.y + y, 0);
                                float currentDepth       = PlatformAgnosticDepthConversion( _AccumulatedDepthTex.Load(currentFetchCoords) ); 
                            #else
                                int2 currentFetchCoords  = sampleCoords + int2(x, y);
                                float currentDepth       = PlatformAgnosticDepthConversion( _AccumulatedDepthTex.Load(currentFetchCoords, 0) ); 
                            #endif
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

            fragOutput frag (v2f i)
            {
                #if defined (NO_MSAA)
                    uint width      = 0;
                    uint height     = 0;                
                    _AccumulatedRadianceTex.GetDimensions(width, height);    
                #else
                    uint width      = 0;
                    uint height     = 0;
                    uint samples    = 0;
                    _AccumulatedRadianceTex.GetDimensions(width, height, samples);    
                #endif
                
                float2 atlasCoords = normalizedToAtlasTexels(i.uv.xy, _AtlasCoords);
                int texelX = floor(atlasCoords.x * width);
                int texelY = floor(atlasCoords.y * height);
                
                #if defined (NO_MSAA)
                    int3 sampleCoords = int3(texelX, texelY, 0);
                    half4 colorSample = _AccumulatedRadianceTex.Load(sampleCoords);
                    float depthSample = PlatformAgnosticDepthConversion( _AccumulatedDepthTex.Load(sampleCoords) );
                #else
                    int2 sampleCoords = int2(texelX, texelY);
                    half4 colorSample = MultisampleResolve(MSAA_SAMPLES, sampleCoords);
                    float depthSample = PlatformAgnosticDepthConversion( _AccumulatedDepthTex.Load(sampleCoords, 0) );
                #endif

                float dilatedDepthSample = dilateSilhouetteDepth(depthSample, sampleCoords);

                fragOutput output;
                output.color = colorSample;
                output.depth = dilatedDepthSample;
                return output;
            }
            ENDCG
        }
    }
}
