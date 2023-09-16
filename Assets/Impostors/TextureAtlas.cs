using System.Collections;
using System.Collections.Generic;
using UnityEngine;

internal class Quadrant
{
    internal int resolution;
    internal int subdivision;
    internal int tileResolution;

    internal float tileStep;

    internal Vector2 origin;
    internal bool[,] occupancy;

    internal Quadrant(int resolution, int subdivision, Vector2 origin)
    {
        this.resolution = resolution;
        this.subdivision = subdivision;

        this.tileResolution = resolution / subdivision;
        this.tileStep = 1.0f / (2 * subdivision); //the 2 originates from the assumption, that it is a quadrant(splits the vertical and horizontal axis in two)
                                                  //tileStep is in normalized coordinates. To avoid issues, tileStep has to map to integer values when the quadrants resolution is taken into account
        int mappedTileStep_texels = (int)(resolution * this.tileStep * 2f);
        float safeTileStep = (float)mappedTileStep_texels / (float)resolution;
        this.tileStep = 0.5f * safeTileStep;
        this.origin = origin;

        this.occupancy = new bool[subdivision,subdivision];
    }

    // Returns the (x,y) index of the next free tile inside of this quadrant
    internal Vector2 getClaimableIndices()
    {
        //search next free tile
        int x = -1, y = -1;
        bool found = false;
        for (int i = 0; i < subdivision; i++)
        {
            for (int j = 0; j < subdivision; j++)
            {
                if (!occupancy[i,j])
                {
                    x = i;
                    y = j;
                    found = true;
                    break;
                }
            }

            if (found)
            {
                break;
            }
        }

        return new Vector2(x, y);
    }

    // Returns the (x,y) indices of the next "numTiles" free tiles inside of this quadrant
    internal Vector2[] getClaimableIndices(int numTiles)
    {
        Vector2[] result = new Vector2[numTiles];
        for (int i = 0; i < numTiles; i++)
            result[i] = new Vector2(-1, -1);

        int searchIdx    = 0;
        for (int i = 0; i < subdivision; i++)
        {
            for (int j = 0; j < subdivision; j++)
            {
                if (!occupancy[i, j])
                {
                    result[searchIdx].x = i;
                    result[searchIdx].y = j;
                    searchIdx++;

                    if (searchIdx >= numTiles)
                        return result;
                }
            }
        }
        return result;
    }

    //Converts the (x,y) index of a tile inside of this quadrant into global atlas coordinates.
    internal Vector4 indicesToAtlasCoords(int x, int y)
    {
        return new Vector4(
                this.origin.x + x * tileStep,
                this.origin.y + y * tileStep,
                this.origin.x + (x + 1) * tileStep,
                this.origin.y + (y + 1) * tileStep
        );
    }

    //Converts the global atlas coords of a tile into tile indices of this quadrant
    internal Vector2 atlasCoordsToIndices(Vector4 atlasCoords)
    {
        int x = (int)(((atlasCoords.x + atlasCoords.z) / 2.0f - this.origin.x) / tileStep);
        int y = (int)(((atlasCoords.y + atlasCoords.w) / 2.0f - this.origin.y) / tileStep);
        return new Vector2(x, y);
    }

    internal Vector4 claimTile(int x, int y)
    {
        if (occupancy[x,y] == true)
            return Vector4.negativeInfinity;
        else
        {
            occupancy[x,y] = true;
            return indicesToAtlasCoords(x, y);
        }
    }

    internal void freeTile(Vector4 bounds)
    {
        if (bounds.x < 0)
            return;

        Vector2 indices = atlasCoordsToIndices(bounds);
        occupancy[(int)indices.x, (int)indices.y] = false;
    }

    internal bool checkTileFree(Vector4 bounds)
    {
        if (bounds.x < 0)
            return false;

        Vector2 indices = atlasCoordsToIndices(bounds);
        return occupancy[(int)indices.x, (int)indices.y];
    }

    internal void reset()
    {
        for (int x = 0; x < subdivision; x++)
        {
            for (int y = 0; y < subdivision; y++)
            {
                occupancy[x,y] = false;
            }
        }
    }

}

// internal class QuadrantComparer : Comparer<Quadrant>
// {
//     Vector2 resolution;
//     internal QuadrantComparer(Vector2 targetResolution)
//     {
//         this.resolution = targetResolution;
//     }

//     public override int Compare(Quadrant a, Quadrant b)
//     {
//         float sqrDistanceA = Mathf.Abs((a.tileResolution - this.resolution.x) * (a.tileResolution - this.resolution.x) + (a.tileResolution - this.resolution.y) * (a.tileResolution - this.resolution.y));
//         float sqrDistanceB = Mathf.Abs((b.tileResolution - this.resolution.x) * (b.tileResolution - this.resolution.x) + (b.tileResolution - this.resolution.y) * (b.tileResolution - this.resolution.y));
//         return (int)Mathf.Sign(sqrDistanceA - sqrDistanceB);
//     }
// }

// Compares two Quadrants based on their ability to accomodate targetResolution. A Quadrant is compared less if
// targetResolution fits into them without wasting too much space. If targetResolution does not fit, the quadrant
// with higher resolution is compared as less.
internal class QuadrantComparer : Comparer<Quadrant>
{
    Vector2 resolution;
    float maxRes;
    internal QuadrantComparer(Vector2 targetResolution)
    {
        this.resolution = targetResolution;
        this.maxRes = Mathf.Max(targetResolution.x, targetResolution.y);
    }

    public override int Compare(Quadrant a, Quadrant b)
    {
        float distanceA = a.tileResolution - this.maxRes;
        float distanceB = b.tileResolution - this.maxRes;
        distanceA = distanceA < 0 ? -distanceA * 1000 : distanceA;
        distanceB = distanceB < 0 ? -distanceB * 1000 : distanceB;
        return (int)Mathf.Sign(distanceA - distanceB);
    }
}

internal class ImpostorTextureQuadrants
{
    private int resolution;

    private Quadrant[] quadrants;

    internal ImpostorTextureQuadrants(int resolution, int subdivisionFirst, int subdivisionSecond, int subdivisionThird, int subdivisionFourth)
    {
        this.resolution = resolution;
        int halfRes     = resolution/2;
        quadrants       = new Quadrant[4];

        quadrants[0] = new Quadrant(halfRes, subdivisionFirst,  new Vector2(0, 0));
        quadrants[1] = new Quadrant(halfRes, subdivisionSecond, new Vector2(0.5f, 0));
        quadrants[2] = new Quadrant(halfRes, subdivisionThird,  new Vector2(0, 0.5f));
        quadrants[3] = new Quadrant(halfRes, subdivisionFourth, new Vector2(0.5f, 0.5f));
    }

    internal bool claimTile(Vector4 claimCoords)
    {
        Quadrant targetQuadrant    = quadrants[getQuadrant(claimCoords)];
        Vector2 tileIndices        = targetQuadrant.atlasCoordsToIndices(claimCoords);
        Vector4 claimedAtlasCoords = targetQuadrant.claimTile((int)tileIndices.x, (int)tileIndices.y);
        if(claimedAtlasCoords.x < 0)
            return false;
        else
            return true;

    }

    internal Vector4 getNextFreeTileCoords(Vector4 previousBounds, float reqTileResX, float reqTileResY)
    {
        //find possible quadrants
        List<Quadrant> sortedQuadrants = new List<Quadrant>();
        foreach(Quadrant quadrant in this.quadrants)
            sortedQuadrants.Add(quadrant);

        //sort by smallest difference to resolution
        sortedQuadrants.Sort(new QuadrantComparer(new Vector2(reqTileResX, reqTileResY)));

        //If previous bounds still provide the best fit, reuse them
        if(previousBounds.x >= 0) {
            Quadrant previous = quadrants[getQuadrant(previousBounds)];
            if (previousBounds.x >= 0 && previous.subdivision == sortedQuadrants[0].subdivision) {
                return previousBounds;
            }
        }
        
        //iterate through all, return first claimable tile
        foreach(Quadrant quadrant in sortedQuadrants)
        {
            Vector2 coords = quadrant.getClaimableIndices();
            if(coords.x >= 0)
            {
                return quadrant.indicesToAtlasCoords((int)coords.x, (int)coords.y);
            }
        }

        //return null if no tile could be claimed
        return Vector4.negativeInfinity;
    }

    internal void freeTile(Vector4 bounds)
    {
        quadrants[getQuadrant(bounds)].freeTile(bounds);
    }

    internal Vector2 getTileResolution(Vector4 bounds)
    {
        float res = quadrants[getQuadrant(bounds)].tileResolution;

        return new Vector2(res, res);
    }

    internal void reset()
    {
        foreach(Quadrant quadrant in quadrants)
        {
            quadrant.reset();
        }
    }

    internal static int getQuadrant(Vector4 bounds)
    {
        if(bounds.x >= 0.0f && bounds.y >= 0.0f && bounds.z <= 0.5f && bounds.w <= 0.5f) return 0; // bottom left
        if(bounds.x >= 0.5f && bounds.y >= 0.0f && bounds.z <= 1.0f && bounds.w <= 0.5f) return 1; // bottom right
        if(bounds.x >= 0.0f && bounds.y >= 0.5f && bounds.z <= 0.5f && bounds.w <= 1.0f) return 2; // top left
        return 3; // top right
    }    
    
}

public class TextureAtlas
{
    private RenderTexture radianceTexture;
    private int width;
    private int height;
    private ImpostorTextureQuadrants quadrants;

    public TextureAtlas(int resolution, Vector4 quadrantSubdivisions)
    {
        this.width             = resolution;
        this.height            = resolution;
        this.quadrants         = new ImpostorTextureQuadrants(resolution, (int)quadrantSubdivisions.x, (int)quadrantSubdivisions.y, (int)quadrantSubdivisions.z, (int)quadrantSubdivisions.w);

        this.radianceTexture = new RenderTexture(this.width, this.height, 32, RenderTextureFormat.ARGB32);
        this.radianceTexture.antiAliasing = 1;
        this.radianceTexture.bindTextureMS = false;
        this.radianceTexture.autoGenerateMips = false;
        this.radianceTexture.useMipMap = false;
        this.radianceTexture.useDynamicScale = false;
        this.radianceTexture.filterMode = FilterMode.Bilinear;
        this.radianceTexture.wrapMode = TextureWrapMode.Clamp;
        this.radianceTexture.vrUsage = VRTextureUsage.None;

        this.radianceTexture.Create();
    }

    public RenderTexture getRadianceTexture() {
        return this.radianceTexture;
    }

    public void freeTile_Quadrants(Vector4 bounds)
    {
        this.quadrants.freeTile(bounds);
    }

    public Vector2 getQuadrantTileresolution(Vector4 bounds)
    {
        return this.quadrants.getTileResolution(bounds);
    }

    public bool claimTile(Vector4 claimCoords)
    {
        return this.quadrants.claimTile(claimCoords);
    }

    public Vector4 getNextFreeTileCoords(Vector4 previousBounds, float reqTileResX, float reqTileResY)
    {
        return this.quadrants.getNextFreeTileCoords(previousBounds, reqTileResX, reqTileResY);
    }

    //Returns: 0  if the currentBounds are the best fit for the given resolution
    //Returns: 1  if a currently claimable tile exists, which offer a better fit
    public int isBetterTilePresent(Vector4 currentBounds, float currentResX, float currentResY)
    {
        Vector4 requestedCoords     = this.quadrants.getNextFreeTileCoords(currentBounds, currentResX, currentResY);
        if(requestedCoords.x < 0)
            return 0;
        Vector2 newRes              = this.quadrants.getTileResolution(requestedCoords);
        Vector2 oldRes              = this.quadrants.getTileResolution(currentBounds);
        float errorCurrent          = Mathf.Abs(1f - (currentResX / oldRes.x));
        float errorNextAvailable    = Mathf.Abs(1f - (currentResX / newRes.x));

        if (errorCurrent <= errorNextAvailable) {
            return 0;
        }
        else {
            //Debug.Log("Texture Term: Found a better tile!");
            //Debug.Log("CurrentError: " + errorCurrent);
            //Debug.Log("NextAvailableError: " + errorNextAvailable);
            return 1;
        }
    }

    //Converts [0,1] uv coordinates of an impostor belonging to this atlas into pixel coordinates [x, y, width, height]
    public Vector4 convertUVtoViewportParams(Vector4 atlasCoords)
    {
        int startX = (int)(atlasCoords.x * this.width);
        int startY = (int)(atlasCoords.y * this.height);
        int width  = (int)((atlasCoords.z - atlasCoords.x) * this.width);
        int height = (int)((atlasCoords.w - atlasCoords.y) * this.height);

        return new Vector4(startX, startY, width, height);
    }

    //-1: A is smaller, 0: A&B are equal, 1: A is larger
    public static int tilesEqual(TextureAtlas atlasA, Vector4 boundsA, TextureAtlas atlasB, Vector4 boundsB)
    {
        Vector2 tileResA = atlasA.getQuadrantTileresolution(boundsA);
        Vector2 tileResB = atlasB.getQuadrantTileresolution(boundsB);
        float diff    = (tileResA.x * tileResA.y) - (tileResB.x * tileResB.y);

        if(diff < -0.01f)
            return -1;
        else if (diff > -0.01f && diff < 0.01f)
            return 0;
        else
            return 1;
    }


 
    
}
