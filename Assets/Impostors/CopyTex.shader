Shader "Hidden/CopyTex"
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


            Texture2D _SrcColorTex;
            Texture2D _SrcDepthTex;
            ///------------------------
            uniform float4   _AtlasCoords;

            // Converts [0-1] texture coordinates into corresponding atlasCoordinates for a region defined
            // by atlasBounds
            float2 normalizedToAtlasTexels(float2 normTexCoords, float4 atlasBounds)
            {
                float2 range = float2(atlasBounds.z - atlasBounds.x, atlasBounds.w - atlasBounds.y);
                float2 newCoord = atlasBounds.xy + normTexCoords * range;
                return newCoord;
            }

            fragOutput frag (v2f i)
            {
                uint width      = 0;
                uint height     = 0;                
                _SrcColorTex.GetDimensions(width, height);    

                float2 atlasCoords = normalizedToAtlasTexels(i.uv.xy, _AtlasCoords);
                int texelX = floor(atlasCoords.x * width);
                int texelY = floor(atlasCoords.y * height);
                
                int3 sampleCoords = int3(texelX, texelY, 0);
                half4 colorSample = _SrcColorTex.Load(sampleCoords);
                float depthSample = _SrcDepthTex.Load(sampleCoords);

                fragOutput output;
                output.color = colorSample;
                output.depth = depthSample;
                return output;
            }
            ENDCG
        }
    }
}
