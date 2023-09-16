using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ImpostorTextureManager
{
    private static ImpostorTextureManager itm_instance;

    
    public static int ATLAS_RESOLUTION = 8192; // Resolution of the color+depth atlases
    // How many impostors can fit into the n-th quadrant of the atlas ? A quadrant has a resolution of (0.5 * ATLAS_RESOLUTION)^2
    public static int QUADRANT_1_TILES = 4;    
    public static int QUADRANT_2_TILES = 10;
    public static int QUADRANT_3_TILES = 14;
    public static int QUADRANT_4_TILES = 20;
    //The targeted Object pixel to impostor texel ratio.
    //Values < 1 result in impostors having a lower resolution then the rendered mesh
    //Values > 1 result in impostors having a higher resolution then the rendered mesh (useful for longer validity, but requires more rendering resources)
    public static float OIR                   = 3f;
    public static bool nonDistortedProjection = false;
    public static bool useOIR                 = true;

    public static TextureAtlas currentImpostorAtlas;
    public static TextureAtlas previousImpostorAtlas;

    private ImpostorTextureManager() {

        currentImpostorAtlas          = new TextureAtlas(ATLAS_RESOLUTION, new Vector4(QUADRANT_1_TILES, QUADRANT_2_TILES, QUADRANT_3_TILES, QUADRANT_4_TILES));

        if(ImpostorManager.USE_BLENDING)
            previousImpostorAtlas     = new TextureAtlas(ATLAS_RESOLUTION, new Vector4(QUADRANT_1_TILES, QUADRANT_2_TILES, QUADRANT_3_TILES, QUADRANT_4_TILES));
    }

    public static ImpostorTextureManager getItm_instance() {
        if(itm_instance == null)
            itm_instance = new ImpostorTextureManager();

        return itm_instance;
    }

    public TextureAtlas getCurrentImpostorAtlas() {
        return currentImpostorAtlas;
    }

    public TextureAtlas getPreviousImpostorAtlas() {
        return previousImpostorAtlas;
    }

    //Returns true if: All 2 tiles can be succesfully claimed in the corresponding atlases.
    //In this case, the previous and next coords are returned inside previous, current
    //Returns false otherwise.
    //Input requested resolution of the tile
    public bool checkAtlasTileSlots(Vector2 requestedResolution, Vector4 currentCoords, ref Vector4 previous, ref Vector4 next)
    {
        ///////////////
        Vector4 boundsCurrent         = currentImpostorAtlas.getNextFreeTileCoords(currentCoords, requestedResolution.x, requestedResolution.y);
        if(boundsCurrent.x < 0)
            return false;

        if(ImpostorManager.USE_BLENDING) {
            Vector2 currentTileRes = currentImpostorAtlas.getQuadrantTileresolution(currentCoords);
            // TODO: Maybe instead of Vec4.NegInf we should supply the previous bounds (analogous to "current")
            Vector4 boundsPrev = previousImpostorAtlas.getNextFreeTileCoords(Vector4.negativeInfinity, currentTileRes.x, currentTileRes.y);
            if(boundsPrev.x < 0)
                return false;

            int res  = TextureAtlas.tilesEqual(currentImpostorAtlas, currentCoords, previousImpostorAtlas, boundsPrev);
            if(res != 0)
                return false;

            previous.x = boundsPrev.x; previous.y = boundsPrev.y; previous.z = boundsPrev.z; previous.w = boundsPrev.w;
        }

        next.x = boundsCurrent.x; next.y = boundsCurrent.y; next.z = boundsCurrent.z; next.w = boundsCurrent.w;
        return true;
    }

}
