Shader "Unlit/RaymarchedImpostor"
{
    Properties
    {

    }
    SubShader
    {
        Tags { "Queue" = "Transparent" }
        Blend SrcAlpha OneMinusSrcAlpha
        Blendop Add
        Pass
        {
            CGPROGRAM
            // #pragma debug
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ USE_IMPOSTOR_BLENDING
            #pragma target 3.0
            #pragma enable_d3d11_debug_symbols

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                //UNITY_FOG_COORDS(1)
                float4 vertex : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            struct fragOutput
            {
                half4 color : SV_Target;
                float depth : SV_Depth;
            };

            // Constants
            #define GRADIENT_SURFACE_THRESHOLD    0.01
            #define RAYMARCH_PRECISION            0.001
            #define HEIGHTFIELD_INF               0.999
            #define EPSILON                       0.0001
            #define LARGE_FLOAT                   1000.0

          	const static float2 MASK_8_ROOKS[8] =
            {
                float2(0.0625, 0.4375),
                float2(-0.1875, 0.3125),
                float2(-0.4375, 0.1875),
                float2(0.3125, 0.0625),
                float2(-0.3125, -0.0625),
                float2(0.4375, -0.1875),
                float2(0.1875, -0.3125),
                float2(-0.0625, -0.4375),
            };
            

            //-------------------------
            Texture2D _PreviousRadianceTex;
            Texture2D _PreviousDepthTex;
            Texture2D _CurrentRadianceTex;
            Texture2D _CurrentDepthTex;
            SamplerState sampler_CurrentRadianceTex;
            SamplerState sampler_CurrentDepthTex;
            ///------------------------

	        uniform float4x4 _CaptureViewMat[2];
            uniform float4x4 _InvCaptureViewMat[2];
	        uniform float4x4 _CaptureProjMat[2];
            uniform float4x4 _InvCaptureProjMat[2];
	        uniform float4x4 _PrevCaptureViewMat[2];
            uniform float4x4 _InvPrevCaptureViewMat[2];
	        uniform float4x4 _PrevCaptureProjMat[2];
            uniform float4x4 _InvPrevCaptureProjMat[2];
	        uniform float4   _BboxMin;
	        uniform float4   _BboxMax;
	        uniform float4   _AtlasCoords[2];
            uniform float4   _PrevAtlasCoords[2];
            uniform float    _BlendingWeight;
            uniform int      _ImpostorMode; // 0: Mono, 1: Mono_Parallax, 2: Stereo, 3: Stereo_Parallax
            float4   _DebugVec;

            v2f vert (appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                o.vertex    = UnityObjectToClipPos(v.vertex);
                o.worldPos  = (mul(unity_ObjectToWorld, v.vertex )).xyz;
                o.screenPos = ComputeScreenPos(o.vertex);
                return o;
            }



            float  maxComponent(float3 v) { return max (max (v.x, v.y), v.z); }
            float  minComponent(float3 v) { return min (min (v.x, v.y), v.z); }
            float  safeInverse(float x)   { return (x == 0.0) ? 1000000000000.0 : (1.0 / x); }
            float3 safeInverse(float3 v)  { return float3(safeInverse(v.x), safeInverse(v.y), safeInverse(v.z)); }

            // Converts [0-1] texture coordinates into corresponding atlasCoordinates for a region defined
            // by atlasBounds
            float2 normalizedToAtlasTexels(float2 normTexCoords, float4 atlasBounds)
            {
                float2 range = float2(atlasBounds.z - atlasBounds.x, atlasBounds.w - atlasBounds.y);
                float2 newCoord = atlasBounds.xy + normTexCoords * range;
                return newCoord;
            }

            // Converts texture coordinates from a bound window in atlas space into a normalized [0,1] region
            float2 atlasToNormalizedTexels(float2 atlasTexCoords, float4 atlasBounds)
            {
                float2 range    = float2(atlasBounds.z - atlasBounds.x, atlasBounds.w - atlasBounds.y);
                float2 newCoord = (atlasTexCoords - atlasBounds.xy) / range;
                return newCoord;
            }

            /* Majercik et al. 2018: "A Ray-Box Intersection Algorithm and Efficient Dynamic Voxel Rendering
            Ingo Wald's variation on Timo Aila's ray-box intersection to incorporate (normals and) distances.
            Personal communication July 2016"
            */
            bool ailaWaldHitAABox(float3 boxCenter, float3 boxRadius, float3 rayOrigin, float3 rayDirection, inout float3 firstIntersectionPoint) {

                float3 rayOriginCompute  = rayOrigin - boxCenter;
                float3 invRayDirection   = safeInverse(rayDirection);
                float3 t_min             = (-boxRadius - rayOriginCompute) * invRayDirection;
                float3 t_max             = ( boxRadius - rayOriginCompute) * invRayDirection;
                float t0               = maxComponent(min(t_min, t_max));
                float t1               = minComponent(max(t_min, t_max));

                // Compute the intersection distance
                float distance = (t0 > 0.0) ? t0 : t1;
                firstIntersectionPoint = (rayOrigin + rayDirection * distance);

                return (t0 <= t1) && (distance > 0.0);
            }

            // Computes the depth (as seen from the current rendering camera) of a world-space position
            float worldToDepth(float3 worldPosition)
            {
                float4 hit_ndc = mul(UNITY_MATRIX_VP, float4(worldPosition, 1.0));
                hit_ndc     /= hit_ndc.w;
                // TODO: Check if under OpenGL this also works as intended
                // hit_ndc      = (hit_ndc + 1)*0.5; 
                return hit_ndc.z;
            }

            float3 Cam2Ts(float3 camPos, float4x4 projMat)
            {
                float4 pos_ndc = mul( projMat, float4(camPos, 1.0) );
                float3 pos_ts  = (pos_ndc.xyz/pos_ndc.w) * 0.5 + 0.5;
                return pos_ts;
            }


            float3 Ts2Cam(float3 tsPos, float4x4 invProj)
            {
                float3  pos_ndc   = (tsPos - 0.5) * 2.0;
                float4  pos_cs    = mul( invProj, float4(pos_ndc, 1.0));
                pos_cs           /= pos_cs.w;
                return pos_cs.xyz;
            }

            float3 Cam2Ws(float3 csPos, float4x4 invView)
            {
                float4 wsPos = mul( invView, float4(csPos, 1.0f) );
                return wsPos.xyz;
            }

            float3 intersectBBox(float3 start, float3 direction)
            {
                float3 bbCenter   = (_BboxMin.xyz + _BboxMax.xyz) * 0.5;
                float3 bbRadius   = float3(abs(_BboxMax.x - _BboxMin.x)*0.5, abs(_BboxMax.y - _BboxMin.y)*0.5, abs(_BboxMax.z - _BboxMin.z)*0.5);
                float3 E_w        = float3(0,0,0);
                bool hit          = ailaWaldHitAABox(bbCenter, bbRadius, start + direction*EPSILON, direction, E_w);

                return E_w;
            }

            // Helper function to intersect a ray (given with "start" and "direction") with the implicit impostor plane P.
            // P is located at the center of the impostors bounding box and its orientation is set to face its capture camera
            float3 intersectImpostorPlane(float3 start, float3 direction)
            {
                float3 capture_position = (mul(_InvCaptureViewMat[unity_StereoEyeIndex], float4(0.0,0.0,0.0,1.0))).xyz;
                float3 bbCenter         = (_BboxMin.xyz + _BboxMax.xyz) * 0.5;
                float3 plane_normal     = normalize(capture_position - bbCenter);
                // plane_origin is the point on the plane that is closest to vec3( 0.0 ),
                float3 plane_origin     = plane_normal *  dot( plane_normal, bbCenter);
                float t                 = dot( plane_origin - start, plane_normal ) / dot( direction, plane_normal );

                float3 intersection = start + t * direction;
                return intersection;
            }

            // Simple Impostor without motion parallax correction. Interprets the impostor as a capture camera facing quad.
            half4 shadeImpostor_simple(out float fragDepth, float3 fragPos)
            {
                half4 result               = half4(0.0,0.0,0.0,-1.0);
                float3 T_w                 = normalize(fragPos - _WorldSpaceCameraPos);
                float3 planeIntersection_w = intersectImpostorPlane(fragPos, T_w);
                float4 planeIntersection_c = mul(_CaptureViewMat[unity_StereoEyeIndex], float4(planeIntersection_w, 1.0f) );
                float3 planeIntersection_t = Cam2Ts(planeIntersection_c.xyz, _CaptureProjMat[unity_StereoEyeIndex]);

                fragDepth = 1.0;

                if( !any(step(float2(1.0,1.0), planeIntersection_t.xy)) && !any(step(planeIntersection_t.xy, float2(0.0,0.0))) )
                {
                    float2 planeIntersection_atlas = normalizedToAtlasTexels(planeIntersection_t.xy, _AtlasCoords[unity_StereoEyeIndex]);
                    result                         =  _CurrentRadianceTex.Sample(sampler_CurrentRadianceTex, planeIntersection_atlas);

                    fragDepth = worldToDepth(planeIntersection_w);
                }
                
                return result;
            }

            // Computes the intersection between the viewray and a perspective heightfield using the secant method.
            // Method can be run for multiple iterations for increased intersection precision.
            float secant_method(inout float lower_alpha, inout float upper_alpha, Texture2D heightfield, float3 Start_ts, float3 d_ts, int iterations, inout float closestAlpha, inout float closestDistance)
            {

                float intersectionAlpha = (lower_alpha + upper_alpha) * 0.5;

                for(int i = 0; i < iterations; i++)
                {
                    float3  lower_texCoord         = Start_ts + lower_alpha*d_ts;
                    float3  upper_texCoord         = Start_ts + upper_alpha*d_ts;

                    float lower_heightVal        = heightfield.SampleLevel(sampler_CurrentDepthTex, lower_texCoord.xy, 0).r;
                    float upper_heightVal        = heightfield.SampleLevel(sampler_CurrentDepthTex, upper_texCoord.xy, 0).r;

                    // Resolve to conservative binary search (Oliveira 2007) if one of the bounds is background
                    if (lower_heightVal > HEIGHTFIELD_INF || upper_heightVal > HEIGHTFIELD_INF)
                        intersectionAlpha = (lower_alpha + upper_alpha) * 0.5;
                    else //Perform secant method
                    {
                        // Get difference between current and previous depth step and the corresponding textureDepth
                        float lower_difference       = lower_heightVal - lower_texCoord.z;
                        float upper_difference       = upper_heightVal - upper_texCoord.z;

                        // interpolation of alpha parameter
                        float weight                 = upper_difference / (upper_difference - lower_difference);
                        intersectionAlpha            = lower_alpha * weight + upper_alpha * (1.0 - weight);
                    }

                    // intersection data
                    float3  intersection_texCoord  = Start_ts + intersectionAlpha*d_ts;
                    float intersection_heightVal   = heightfield.SampleLevel(sampler_CurrentDepthTex, intersection_texCoord.xy, 0).r;
                    
                    // Optional: Keep track of closest distance to the actual surface along the ray
                    float distanceFromSurface = abs(intersection_texCoord.z - intersection_heightVal);
                    if(distanceFromSurface < closestDistance)
                    {
                        closestDistance = distanceFromSurface;
                        closestAlpha        = intersectionAlpha;
                    }

                    if (intersection_texCoord.z > intersection_heightVal)
                        upper_alpha = intersectionAlpha;
                    else
                        lower_alpha = intersectionAlpha;
                }

                return intersectionAlpha;
            }

            bool raymarch_uniform(float3 Start_ts, float3 End_ts, int steps, Texture2D heightfield, inout float alpha, inout float prevAlpha, inout float closestAlpha, inout float closestDistance)
            {
                float3  d_ts               = End_ts - Start_ts;
                int   currentStep          = 0;
                float3 currentTexCoord     = Start_ts + alpha*d_ts; //currentTexCoord.z = ray depth in [0,1]
                float currentTextureDepth  = heightfield.SampleLevel(sampler_CurrentDepthTex, currentTexCoord.xy, 0).r;
                bool  hit                  = false;

                // If the initial sample is already under the heightfield, we are most likely sampling the backfaces
                if(currentTexCoord.z >= currentTextureDepth + 0.01)
                {
                    return false;
                }

                while( currentStep < steps )
                {
                    if(currentTexCoord.z >= currentTextureDepth)
                    {
                        hit = true;
                        break;
                    }

                    // Optional: Keep track of closest distance to the actual surface along the ray
                    float distanceFromSurface = abs(currentTexCoord.z - currentTextureDepth);
                    if(distanceFromSurface < closestDistance)
                    {
                        closestDistance = distanceFromSurface;
                        closestAlpha        = alpha;
                    }

                    prevAlpha            = alpha;
                    alpha               += 1.0/steps;
                    currentTexCoord      = Start_ts + alpha*d_ts;

                    currentTextureDepth  = heightfield.SampleLevel(sampler_CurrentDepthTex, currentTexCoord.xy, 0).r;
                    currentStep++;
                }

                if((currentTexCoord.z >= currentTextureDepth))
                    hit = true;

                return hit;
            }

            float4 RaymarchHeightfield(inout float3 hitpoint_ws, inout float closestHitDistance, float3 Start_w, float3 End_w, float4x4 viewMat, float4x4 projMat,
                                    float4x4 invViewMat, float4x4 invProjMat, Texture2D heightfield, int maxSteps, int secantSteps, float4 atlasBounds, inout int hitClassification)
            {
                float3 Start_c            = mul(viewMat, float4(Start_w.x, Start_w.y, Start_w.z, 1)).xyz;
                float3 End_c              = mul(viewMat, float4(End_w.x, End_w.y, End_w.z, 1)).xyz;

                float3 Start_ts           = Cam2Ts(Start_c, projMat);
                Start_ts.xy               = normalizedToAtlasTexels( Start_ts.xy, atlasBounds );
                float3 End_ts             = Cam2Ts(End_c, projMat);
                End_ts.xy                 = normalizedToAtlasTexels( End_ts.xy, atlasBounds );
                float3 d_ts               = End_ts - Start_ts;
                float alpha               = 0;
                float prevAlpha           = 0;
                float closestAlpha        = 0;
                float closestDistance     = LARGE_FLOAT;

                uint heightfieldWidth      = 0;
                uint heightfieldHeight     = 0;
                heightfield.GetDimensions(heightfieldWidth, heightfieldHeight); 
                int2 heightfieldRes      = int2(heightfieldWidth, heightfieldHeight);

                float2  d_ts_texel        = abs(heightfieldRes*d_ts.xy);
                int  steps                = int(length(d_ts_texel));

                steps                        = max(min(steps, maxSteps), 1);

                float4 resultTexCoord     = float4(Start_ts.x, Start_ts.y, Start_ts.x, Start_ts.y);
                // Raaaaaay....MARCH!
                bool hit = false;
                hit                       = raymarch_uniform(Start_ts, End_ts, steps, heightfield, alpha, prevAlpha, closestAlpha, closestDistance);

                if(hit)
                {
                    // Refine Hitpoint using the secant-method
                    float upper_bound         = alpha;
                    float lower_bound         = prevAlpha;
                    float intersectionAlpha   = (upper_bound + lower_bound) * 0.5;
                    if ((upper_bound - lower_bound) > RAYMARCH_PRECISION)
                         intersectionAlpha         = secant_method(lower_bound, upper_bound, heightfield, Start_ts, d_ts, secantSteps, closestAlpha, closestDistance);

                    float3 result_ts          = Start_ts + intersectionAlpha*d_ts;
                    resultTexCoord.xy         = result_ts.xy;
                    resultTexCoord.zw         = Start_ts.xy + lower_bound*d_ts.xy;
                    float2 camTsHitCoords     = atlasToNormalizedTexels(resultTexCoord.xy, atlasBounds);
                    float3 hitCoords_cs       = Ts2Cam(float3(camTsHitCoords.x, camTsHitCoords.y, result_ts.z), invProjMat);
                    hitpoint_ws               = Cam2Ws(hitCoords_cs, invViewMat);
                    hitClassification         = 0;
                }
                else if(closestDistance < GRADIENT_SURFACE_THRESHOLD)
                {
                    float3 closestHitCoord    = Start_ts + closestAlpha*d_ts;
                    float texDepth            = heightfield.Sample(sampler_CurrentDepthTex, (closestHitCoord.xy), 0).r;
                    float2 camTsHitCoords     = atlasToNormalizedTexels(closestHitCoord.xy, atlasBounds);
                    float3 hitCoords_cs       = Ts2Cam(float3(camTsHitCoords.x, camTsHitCoords.y, texDepth), invProjMat);
                    hitpoint_ws               = Cam2Ws(hitCoords_cs, invViewMat);
                    hitClassification         = 1;
                    closestHitDistance        = closestDistance;
                    resultTexCoord.xy         = closestHitCoord.xy;
                }
                else
                {
                    hitClassification = 2;
                }

                return (resultTexCoord);
            }

            // Impostor with motion parallax
            half4 shadeImpostor(float2 screenPosUV, out float fragDepth, bool usePreviousImpostor)
            {
                half4  result            = half4(0,0,0,0);
                float4 ray_start_ndc     = float4( (screenPosUV.x - 0.5)*2, (screenPosUV.y - 0.5)*2, -1, 1 );
                float4 ray_start_world   = mul(unity_CameraInvProjection, ray_start_ndc);
                ray_start_world         /= ray_start_world.w;
                ray_start_world          = mul(UNITY_MATRIX_I_V, ray_start_world); // Using this matrix instead of unity_CameraToWorld, as its negative Z forward (OpenGL Style)
                
                float3 T_w               = normalize(ray_start_world.xyz - _WorldSpaceCameraPos);
                float3 S_w               = intersectBBox(ray_start_world.xyz, T_w);
                float3 E_w               = intersectBBox(S_w + T_w*LARGE_FLOAT, -T_w);

                float4 newTexCoords        = float4(0,0,0,0);
                float3 hitpoint_ws         = float3(0,0,0);
                float  closestHitDistance  = LARGE_FLOAT;
                int hit                    = 0;

                if(usePreviousImpostor)
                {
                    newTexCoords               = RaymarchHeightfield(hitpoint_ws, closestHitDistance, S_w, E_w, _PrevCaptureViewMat[unity_StereoEyeIndex], _PrevCaptureProjMat[unity_StereoEyeIndex],
                                                 _InvPrevCaptureViewMat[unity_StereoEyeIndex], _InvPrevCaptureProjMat[unity_StereoEyeIndex], _PreviousDepthTex, 16, 4, _PrevAtlasCoords[unity_StereoEyeIndex], hit);
                }
                else
                {
                    newTexCoords               = RaymarchHeightfield(hitpoint_ws, closestHitDistance, S_w, E_w, _CaptureViewMat[unity_StereoEyeIndex], _CaptureProjMat[unity_StereoEyeIndex],
                                                 _InvCaptureViewMat[unity_StereoEyeIndex], _InvCaptureProjMat[unity_StereoEyeIndex], _CurrentDepthTex, 16, 4, _AtlasCoords[unity_StereoEyeIndex], hit);
                }

                fragDepth = 1.0;

                if (hit == 0 )
                {
                    if(usePreviousImpostor)
                        result     +=  _PreviousRadianceTex.Sample(sampler_CurrentRadianceTex, newTexCoords.xy);
                    else
                        result     +=  _CurrentRadianceTex.Sample(sampler_CurrentRadianceTex, newTexCoords.xy);

                    fragDepth = worldToDepth(hitpoint_ws);
                }

                // if(hit == 0)
                //     result = half4(0,1,0,1);
                // if(hit == 1)
                //     result = half4(1,1,0,1);
                // if(hit == 2)
                //     result = half4(1,0,0,1);


                return result;
            }

            // Impostor with motion parallax. Multisampled at final hit location. Rays which closely miss the surface are attentuated
            half4 shadeImpostor_rayDifferential(float2 screenPosUV, out float fragDepth, bool usePreviousImpostor)
            {
                half4  result            = half4(0,0,0,0);
                float4 ray_start_ndc     = float4( (screenPosUV.x - 0.5)*2, (screenPosUV.y - 0.5)*2, -1, 1 );
                float4 ray_start_world   = mul(unity_CameraInvProjection, ray_start_ndc);
                ray_start_world         /= ray_start_world.w;
                ray_start_world          = mul(UNITY_MATRIX_I_V, ray_start_world); // Using this matrix instead of unity_CameraToWorld, as its negative Z forward (OpenGL Style)
                
                float3 T_w               = normalize(ray_start_world.xyz - _WorldSpaceCameraPos);
                float3 S_w               = intersectBBox(ray_start_world.xyz, T_w);
                float3 E_w               = intersectBBox(S_w + T_w*LARGE_FLOAT, -T_w);

                float4 newTexCoords        = float4(0,0,0,0);
                float3 hitpoint_ws         = float3(0,0,0);
                float  closestHitDistance  = LARGE_FLOAT;
                int hit                    = 0;

                if(usePreviousImpostor)
                {
                    newTexCoords               = RaymarchHeightfield(hitpoint_ws, closestHitDistance, S_w, E_w, _PrevCaptureViewMat[unity_StereoEyeIndex], _PrevCaptureProjMat[unity_StereoEyeIndex],
                                                 _InvPrevCaptureViewMat[unity_StereoEyeIndex], _InvPrevCaptureProjMat[unity_StereoEyeIndex], _PreviousDepthTex, 16, 4, _PrevAtlasCoords[unity_StereoEyeIndex], hit);
                }
                else
                {
                    newTexCoords               = RaymarchHeightfield(hitpoint_ws, closestHitDistance, S_w, E_w, _CaptureViewMat[unity_StereoEyeIndex], _CaptureProjMat[unity_StereoEyeIndex],
                                                 _InvCaptureViewMat[unity_StereoEyeIndex], _InvCaptureProjMat[unity_StereoEyeIndex], _CurrentDepthTex, 16, 4, _AtlasCoords[unity_StereoEyeIndex], hit);
                }

                fragDepth = 1.0;

                if (hit == 0 )
                {

                    float4 captureCameraPos_world = float4(0,0,0,0);
                    if(usePreviousImpostor)
                    {
                        captureCameraPos_world = mul(_InvPrevCaptureViewMat[unity_StereoEyeIndex], float4(0,0,0,1));
                        result           +=  _PreviousRadianceTex.Sample(sampler_CurrentRadianceTex, newTexCoords.xy);
                    }
                    else
                    {
                        captureCameraPos_world = mul(_InvCaptureViewMat[unity_StereoEyeIndex], float4(0,0,0,1));
                        result           +=  _CurrentRadianceTex.Sample(sampler_CurrentRadianceTex, newTexCoords.xy);
                    }

                    // multi-sample raymarch hit location
                    for(int i = 0; i < 8; i++)
                    {
                        float2 ray_start_window  = screenPosUV + (MASK_8_ROOKS[i] / _ScreenParams.xy);
                        float4 ray_start_ndc     = float4( (ray_start_window.x - 0.5)*2, (ray_start_window.y - 0.5)*2, -1, 1 );
                        float4 ray_start_world   = mul(unity_CameraInvProjection, ray_start_ndc);
                        ray_start_world         /= ray_start_world.w;
                        ray_start_world          = mul(UNITY_MATRIX_I_V, ray_start_world); // Using this matrix instead of unity_CameraToWorld, as its negative Z forward (OpenGL Style)    

                        float3 plane_normal         = normalize(captureCameraPos_world.xyz - hitpoint_ws);
                        float3 plane_origin         = plane_normal * dot( plane_normal, hitpoint_ws);

                        float3 dir                  = normalize(ray_start_world.xyz - _WorldSpaceCameraPos.xyz);
                        float t                     = dot( plane_origin - _WorldSpaceCameraPos.xyz, plane_normal ) / dot( dir, plane_normal ); // intersect_plane
                        float3 intersection         = _WorldSpaceCameraPos.xyz + t*dir;
                        
                        
                        float4 intersection_capCam;
                        float3 intersection_ts;
                        if(usePreviousImpostor)
                        {
                            intersection_capCam  = mul(_PrevCaptureViewMat[unity_StereoEyeIndex], float4(intersection.xyz, 1));
                            intersection_ts      = Cam2Ts(intersection_capCam.xyz, _PrevCaptureProjMat[unity_StereoEyeIndex]);
                            newTexCoords.xy      = normalizedToAtlasTexels( intersection_ts.xy, _PrevAtlasCoords[unity_StereoEyeIndex]);
                            result              +=  _PreviousRadianceTex.Sample(sampler_CurrentRadianceTex, newTexCoords.xy);
                        }
                        else
                        {
                            intersection_capCam  = mul(_CaptureViewMat[unity_StereoEyeIndex], float4(intersection.xyz, 1));
                            intersection_ts      = Cam2Ts(intersection_capCam.xyz, _CaptureProjMat[unity_StereoEyeIndex]);
                            newTexCoords.xy      = normalizedToAtlasTexels( intersection_ts.xy, _AtlasCoords[unity_StereoEyeIndex]);
                            result              +=  _CurrentRadianceTex.Sample(sampler_CurrentRadianceTex, newTexCoords.xy);
                        }

                    }
                    result     /=  9.0;
                    fragDepth = worldToDepth(hitpoint_ws);
                }   

                if(hit == 1)
                {
                    float fadeOut = 1.0 - (closestHitDistance / GRADIENT_SURFACE_THRESHOLD);

                    if(usePreviousImpostor)
                        result       +=  _PreviousRadianceTex.Sample(sampler_CurrentRadianceTex, newTexCoords.xy);
                    else
                        result       +=  _CurrentRadianceTex.Sample(sampler_CurrentRadianceTex, newTexCoords.xy);

                    result.a     *= fadeOut;
                    fragDepth     = worldToDepth(hitpoint_ws);
                }

                return result;
            }

            fragOutput frag (v2f i)
            {
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                float2 screenUV   = i.screenPos.xy / i.screenPos.w;
                float2 pixelPos   = _ScreenParams.xy * screenUV;
                half4 color       = half4(0.0f,0.0f,0.0f,0.0f);
                float fragDepth   = 1.0f;

                if (_ImpostorMode == 0 || _ImpostorMode == 2)
                    color = shadeImpostor_simple(fragDepth, i.worldPos);
                else
                    color = shadeImpostor_rayDifferential(screenUV, fragDepth, false);
                    //color             = shadeImpostor(screenUV, fragDepth, false);
                    
                
                
                #if defined (USE_IMPOSTOR_BLENDING)
                    if(_BlendingWeight < 1.0)
                    {
                        float fragDepth_prev = 1;
                        // half4 color_prev     = float4 (1,0,0,1); // Good visualization for regeneration timings
                        half4 color_prev     = shadeImpostor_rayDifferential(screenUV, fragDepth_prev, true);
                        color                = (1.0 - _BlendingWeight)*color_prev + _BlendingWeight *color;
                        fragDepth            = min(fragDepth, fragDepth_prev);
                    }
                #endif

                //Clipping candidate
                if(color.a <= 0.0f )
                    discard;

                fragOutput output;
                output.color = color;
                output.depth = fragDepth;
                return output;
            }

            ENDCG
        }
    }
}
