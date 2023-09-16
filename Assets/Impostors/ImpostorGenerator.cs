using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// TODO: To support multi-material meshes, we have to operate on Unity's submesh granularity
/// <summary>
/// Assigning this component to an objects allows its geometry to be represented by an impostor
/// All MeshFilters which are part of the gameobject or its hierarchy are considered in this process.
/// </summary>
public class ImpostorGenerator : MonoBehaviour
{
    public ImpostorMode impostorMode = ImpostorMode.AUTOMATIC;
    public Camera viewerCamera; // If not set, Main Camera will be used
    private GameObject viewer;
    private float viewerIPD;
    public bool impostorForHierarchy = true; // Considers MeshFilters in the entire hierarchy of an object
    public List<MeshFilter> associatedMeshes;
    private long numTriangles;
    private GameObject generatedImpostor;
    private ImpostorMetadata[] impostorInfo;
    private ProjectionResults[] lastImpostorProjection;
    private Dictionary<MeshFilter, int> layerStorage; // Temp variable for restoring meshes to their "previous" layer upon returning from the "Mesh Stash" layer
    private ValidityField currentValidityField;
    private ValidityField nextValidityField;
    private Bounds impostorBounds;
    // State Variables
    private ImpostorState state = ImpostorState.IDLE;
    private ImpostorMode currentMode = ImpostorMode.MONO;
    private ImpostorMode nextMode = ImpostorMode.MONO;
    private float blendingWeight;
    // External Dependencies
    private ImpostorManager im;
    private RenderTexture currrentRDTex;
    private RenderTexture previousRDTex;
    private MovementPredictor mPred = null;
    private Material imPrimMat;
    // DEBUGING
    public GameObject debugTextObject = null;
    public TextMesh debugText = null;

    /// <summary>
    /// Initialisation of the generator itself
    /// </summary>
    private void initImpostorGenerator()
    {
        // If no viewer is set, get main camera
        if (this.viewerCamera == null)
            this.viewerCamera = Camera.main;

        this.viewer    = this.viewerCamera.gameObject;
        this.viewerIPD = 0.063f;
        this.mPred = this.viewer.GetComponent<MovementPredictor>();

        // Assign impostor atlases
        this.currrentRDTex = ImpostorTextureManager.getItm_instance().getCurrentImpostorAtlas().getRadianceTexture();
        if(ImpostorManager.USE_BLENDING)
            this.previousRDTex    = ImpostorTextureManager.getItm_instance().getPreviousImpostorAtlas().getRadianceTexture();

        // Initial update of associated meshes
        this.associatedMeshes = new List<MeshFilter>();
        this.layerStorage = new Dictionary<MeshFilter, int>();
        this.updateAssociatedMeshes();

        // Init impostor data
        this.impostorInfo = new ImpostorMetadata[2];
        this.lastImpostorProjection = new ProjectionResults[2];
        for (int i = 0; i < 2; i++)
        {
            this.impostorInfo[i] = new ImpostorMetadata(this);
            this.lastImpostorProjection[i] = new ProjectionResults(0);
        }

        this.currentValidityField = new BboxProjectionShade(this);
        this.nextValidityField    = new BboxProjectionShade(this);
        // A more simpler, but slightly faster to evaluate parallax metric
        //this.currentValidityField = new NaiveAngleValidityField(this);
        //this.nextValidityField    = new NaiveAngleValidityField(this);

        this.blendingWeight = 0f;
        
        // Init impostor primitive object
        initImpostorObject();

        //if (debugTextObject == null)
        //{
        //    Bounds bounds = computeImpostorBounds();
        //    debugTextObject = new GameObject("debugTextObject");
        //    debugTextObject.transform.position = bounds.max;

        //    debugText = debugTextObject.gameObject.AddComponent<TextMesh>();
        //    debugText.text = "IMPOSTOR!";
        //    debugText.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
        //}
    }

    /// <summary>
    /// Initialisation of the corresponding impostor primitive object (e.g. a simple box)
    /// </summary>
    private void initImpostorObject()
    {
        // Transform
        this.generatedImpostor = GameObject.CreatePrimitive(PrimitiveType.Cube);
        this.generatedImpostor.name = this.name + " - Impostor";
        Bounds b = this.computeImpostorBounds();
        this.generatedImpostor.transform.position = b.center;
        this.generatedImpostor.transform.localScale = 2 * b.extents;
        Destroy(this.generatedImpostor.GetComponent<BoxCollider>());

        // Visualization
         this.generatedImpostor.GetComponent<MeshRenderer>().enabled = false;

        // Assign textures(atlases)
        this.imPrimMat = new Material(Shader.Find("Unlit/RaymarchedImpostor"));
        imPrimMat.SetTexture("_CurrentRadianceTex", this.currrentRDTex, RenderTextureSubElement.Color);
        imPrimMat.SetTexture("_CurrentDepthTex", this.currrentRDTex, RenderTextureSubElement.Depth);
        
        if(ImpostorManager.USE_BLENDING)
        {
            imPrimMat.SetTexture("_PreviousRadianceTex", this.previousRDTex, RenderTextureSubElement.Color);
            imPrimMat.SetTexture("_PreviousDepthTex", this.previousRDTex, RenderTextureSubElement.Depth);
        }
        this.generatedImpostor.GetComponent<MeshRenderer>().sharedMaterial = imPrimMat;
    }

    // Start is called before the first frame update
    void Start()
    {
        // Try to register with global ImpostorManager
        im = GameObject.FindObjectOfType<ImpostorManager>();
        if (im == null)
        {
            Debug.Log("Could not find an ImpostorManager in the scene. This component cannot function without an ImpostorManager...destroying component!");
            Component.Destroy(this);
            return;
        }
        else
        {
            initImpostorGenerator();
            im.registerImpostorGenerator(this);
        }
    }

    /// <summary>
    /// Impostors are generated from meshes stored in the associatedMeshes list.
    /// This method updates this list to reflect any changes done to the underlying object (such as removing, adding MeshFilter Components etc.)
    /// </summary>
    public void updateAssociatedMeshes()
    {
        associatedMeshes.Clear();

        if (this.impostorForHierarchy)
            associatedMeshes.AddRange(this.GetComponentsInChildren<MeshFilter>());
        else
            associatedMeshes.AddRange(this.GetComponents<MeshFilter>());

        // Recompute the total number of triangles
        long totalIndices = 0;
        foreach (MeshFilter mesh in associatedMeshes)
            totalIndices += mesh.sharedMesh.GetIndexCount(0);

        numTriangles = totalIndices / 3;
    }

    /// <summary>
    /// Returns the total number of triangles of the associatedMeshes list
    /// </summary>
    /// <returns></returns>
    internal long getTriangleCount()
    {
        return numTriangles;
    }


    /// <summary>
    /// Swaps the current ValidityField with the next. 
    /// Performed at the end of the regeneration process
    /// </summary>
    private void swapValidityFields()
    {
        ValidityField temp = this.currentValidityField;
        this.currentValidityField = this.nextValidityField;
        this.nextValidityField = temp;
    }

    /// <summary>
    /// Computes the accumulated AABB of all objects which are included in the impostor 
    /// </summary>
    /// <returns></returns>
    private Bounds computeImpostorBounds()
    {
        Bounds aggregatedBounds = UtilityFunctions.LocalToWorldBounds(associatedMeshes[0].gameObject.transform, associatedMeshes[0].sharedMesh.bounds);
        foreach (MeshFilter mf in associatedMeshes)
        {
            Bounds b = UtilityFunctions.LocalToWorldBounds(mf.gameObject.transform, mf.sharedMesh.bounds);
            aggregatedBounds.Encapsulate(b);
        }

        return aggregatedBounds;
    }

    /// <summary>
    /// Faces the viewer object towards the center of a bounding box and returns the resulting view matrices (world to local transform).
    /// Returns a Matrix4x4[2] array. If impostorMode is STEREO or STEREO_PARALLAX, both matrix entries will be unique. For MONO only the first entry is unique, the second is a copy of the first.
    /// </summary>
    /// <param name="viewerObj">The viewer/camera object that is rotated towards the bounding box center</param>
    /// <param name="bbox">The target bounding box</param>
    /// <param name="viewerOffset">Used to offset the position of the viewer object. Used in conjunction with movement prediction</param>
    /// <param name="outViewerCapturePos">return value</param>
    /// <returns>Matrix4x4[2], where Matrix4x4[0] contains the left eye and Matrix4x4[1] the right eye (if the impostorMode supports this)</returns>
    private void computeViewMatrices(GameObject viewerObj, Bounds bbox, Vector3 viewerOffset, ImpostorMode impostorMode, out Vector3[] outViewerCapturePos, out Matrix4x4[] outViewMatrices)
    {
        // Init output variables
        outViewerCapturePos = new Vector3[2];
        outViewMatrices     = new Matrix4x4[2];

        // Store previous camera transform
        Vector3 prevPosition    = viewerObj.transform.position;
        Quaternion prevRotation = viewerObj.transform.rotation;

        // Virtually transform the viewer to the next impostor capture position & orientation
        viewerObj.transform.position = prevPosition + viewerOffset;
        viewerObj.transform.LookAt(bbox.center);

        if (impostorMode == ImpostorMode.MONO || impostorMode == ImpostorMode.MONO_PARALLAX)
        {
            outViewMatrices[0]              = viewerObj.transform.worldToLocalMatrix;
            outViewerCapturePos[0]          = viewerObj.transform.position;
            outViewMatrices[1]              = viewerObj.transform.worldToLocalMatrix;
            outViewerCapturePos[1]          = viewerObj.transform.position;
        }
        else
        {
            Vector3 viewerCenter = viewerObj.transform.position;
            // Values for left eye
            viewerObj.transform.position = viewerCenter - viewerObj.transform.right * this.viewerIPD * 0.5f;
            outViewerCapturePos[0]       = viewerObj.transform.position;
            outViewMatrices[0]           = viewerObj.transform.worldToLocalMatrix;

            // Values for right eye
            viewerObj.transform.position = viewerCenter + viewerObj.transform.right * this.viewerIPD * 0.5f;
            outViewerCapturePos[1]       = viewerObj.transform.position;
            outViewMatrices[1]           = viewerObj.transform.worldToLocalMatrix;
        }
        //Revert viewer transform to previous
        viewerObj.transform.rotation = prevRotation;
        viewerObj.transform.position = prevPosition;

        // The matrix must be mirrored the along the Z-axis, as this matrix is later used in Camera.worldToCameraMatrix,
        // which uses the OpenGL convention (negative Z forward)
        outViewMatrices[0] = Matrix4x4.Scale(new Vector3(1, 1, -1)) * outViewMatrices[0];
        outViewMatrices[1] = Matrix4x4.Scale(new Vector3(1, 1, -1)) * outViewMatrices[1];
    }

    /// <summary>
    ///  Projects the impostors bounding box into the cameras NDC (using the cameras own (stereo)projection matrix and the supplied viewMatrix) and find min-max values (AABB in NDC).
    /// </summary>
    /// <param name="bbox"></param>
    /// <param name="viewMatrices"></param>
    /// <param name="usedCam"></param>
    /// <returns></returns>
    private ProjectionResults computeImpostorProjection(Bounds bbox, Matrix4x4 viewMatrix, Camera usedCam, Camera.MonoOrStereoscopicEye stereoMonoProjection)
    {
        // Compute correct view-projection matrix
        Matrix4x4 projMatrix = usedCam.nonJitteredProjectionMatrix;
        if (stereoMonoProjection == Camera.MonoOrStereoscopicEye.Left)
            projMatrix = usedCam.GetStereoNonJitteredProjectionMatrix(Camera.StereoscopicEye.Left);
        else if (stereoMonoProjection == Camera.MonoOrStereoscopicEye.Right)
            projMatrix = usedCam.GetStereoNonJitteredProjectionMatrix(Camera.StereoscopicEye.Right);
        Matrix4x4 viewProjMat = projMatrix * viewMatrix;

        // Convert the Bounds object into an array of 8 vertices describing the bounding box
        Vector3 bbMin = bbox.min;
        Vector3 bbMax = bbox.max;
        Vector4[] BB_points = new Vector4[8];
        BB_points[0] = new Vector4(bbMin.x, bbMin.y, bbMin.z, 1);
        BB_points[7] = new Vector4(bbMax.x, bbMax.y, bbMax.z, 1);
        float diffX = BB_points[7].x - BB_points[0].x;
        float diffY = BB_points[7].y - BB_points[0].y;
        float diffZ = BB_points[7].z - BB_points[0].z;
        BB_points[1] = BB_points[0] + new Vector4(diffX, 0, 0, 0);
        BB_points[2] = BB_points[0] + new Vector4(0, 0, diffZ, 0);
        BB_points[3] = BB_points[0] + new Vector4(diffX, 0, diffZ, 0);
        BB_points[4] = BB_points[0] + new Vector4(0, diffY, 0, 0);
        BB_points[5] = BB_points[0] + new Vector4(0, diffY, diffZ, 0);
        BB_points[6] = BB_points[0] + new Vector4(diffX, diffY, 0, 0);

        float minX = float.MaxValue;
        float maxX = float.MinValue;
        float minY = float.MaxValue;
        float maxY = float.MinValue;
        float minZ = float.MaxValue;
        float maxZ = float.MinValue;

        for (int i = 0; i < 8; i++)
        {
            BB_points[i] = viewProjMat * BB_points[i];
            BB_points[i] = BB_points[i] / BB_points[i].w;

            if (BB_points[i].x < minX)
                minX = BB_points[i].x;
            if (BB_points[i].x > maxX)
                maxX = BB_points[i].x;
            if (BB_points[i].y < minY)
                minY = BB_points[i].y;
            if (BB_points[i].y > maxY)
                maxY = BB_points[i].y;
            if (BB_points[i].z < minZ)
                minZ = BB_points[i].z;
            if (BB_points[i].z > maxZ)
                maxZ = BB_points[i].z;
        }

        ProjectionResults result = new ProjectionResults(0);
        result.minX = minX;
        result.maxX = maxX;
        result.minY = minY;
        result.maxY = maxY;
        result.minZ = minZ;
        result.maxZ = maxZ;
        Vector2 resolution = im.getObserverResolution();
        result.resX = 0.5f * (maxX - minX) * resolution.x;
        result.resY = 0.5f * (maxY - minY) * resolution.y;
        result.projMat = projMatrix;

        return result;
    }

    /// <summary>
    /// Computes a projection matrix, which fully encompasses the impostors bounding box.
    /// </summary>
    /// <param name="projResult"> The projection matrix is created based on these projection results</param>
    /// <param name="useOIR"></param>
    /// <returns></returns>
    private Matrix4x4 computeProjectionMatrix(ProjectionResults projResult, bool useOIR)
    {
        Matrix4x4 projMat    = projResult.projMat; // projResult.projMat stores information about the used eye (left, right) if any
        Matrix4x4 invProjMat = Matrix4x4.Inverse(projMat);
        float minX = projResult.minX;
        float maxX = projResult.maxX;
        float minY = projResult.minY;
        float maxY = projResult.maxY;
        float minZ = projResult.minZ;
        float maxZ = projResult.maxZ;
        float pixelsScreen = projResult.resX * projResult.resY;

        ImpostorTextureManager itm = ImpostorTextureManager.getItm_instance();

        // Scale projection frustum to meet OIR (Object/Impostor Resolution Ration)
        // TODO: Reevaluate if still needed, since we now have atlas tiles with different resolutions
        if (useOIR)
        {
            Vector2 tileResolution = itm.getCurrentImpostorAtlas().getQuadrantTileresolution(this.impostorInfo[0].nextTextureBounds);
            float texelsAtlas = tileResolution.x * tileResolution.y;
            float targetPixels = pixelsScreen * ImpostorTextureManager.OIR;

            float atlasResFactor = Mathf.Min(targetPixels / texelsAtlas, 1.0f);
            float frustumScaleFactor = (1f / (float)Mathf.Sqrt(atlasResFactor)) - 1f;

            float ndcWidthHalf = (maxX - minX) * 0.5f;
            float ndcHeightHalf = (maxY - minY) * 0.5f;

            minX -= ndcWidthHalf * frustumScaleFactor;
            maxX += ndcWidthHalf * frustumScaleFactor;
            minY -= ndcHeightHalf * frustumScaleFactor;
            maxY += ndcHeightHalf * frustumScaleFactor;
        }
        //////////////////////////////////////////////////////
        Vector4 ndcMin = new Vector4(minX, minY, minZ, 1);
        Vector4 ndcMax = new Vector4(maxX, maxY, minZ, 1);
        Vector4 ndcDepth = new Vector4(maxX, maxY, maxZ, 1);

        ndcMin = invProjMat * ndcMin;
        ndcMin = ndcMin * 1f / ndcMin.w;
        ndcMax = invProjMat * ndcMax;
        ndcMax = ndcMax * 1f / ndcMax.w;
        ndcDepth = invProjMat * ndcDepth;
        ndcDepth = ndcDepth * 1f / ndcDepth.w;

        float left = ndcMin.x;
        float right = ndcMax.x;
        float bottom = ndcMin.y;
        float top = ndcMax.y;
        float near = -ndcMin.z;
        float far = -ndcDepth.z;

        float height = top - bottom;
        float width = right - left;

        // TODO: Reevaluate if still needed, since we perform now "mip-mapping" at runtime
        if (ImpostorTextureManager.nonDistortedProjection)
        {
            if (height > width)
            {
                float difference = height - width;
                left -= difference * 0.5f;
                right += difference * 0.5f;
            }
            else
            {
                float difference = width - height;
                bottom -= difference * 0.5f;
                top += difference * 0.5f;
            }
        }

        Matrix4x4 newProjMatrix = Matrix4x4.Frustum(left, right, bottom, top, near, far);
        return newProjMatrix;
    }

    /// <summary>
    /// Claims atlas tiles for the next impostor
    /// 1.Case: currentMode->Mono, nextMode->Mono: Claim 1 tile for history atlas and 1 tile for current atlas
    /// 2.Case: currentMode->Mono, nextMode->Stereo: Claim 1 tile for history atlas and 2 tiles for current atlas
    /// 3.Case: currentMode->Stereo, nextMode->Mono: Claim 2 tile for history atlas and 1 tile for current atlas
    /// 4.Case: currentMode->Stereo, nextMode->Stereo: Claim 2 tiles for history atlas and 2 tiles for current atlas
    /// </summary>
    /// <param name="requestedResX"></param>
    /// <param name="requestedResY"></param>
    /// <returns></returns>
    private bool claimImpostorTiles(int requestedResX, int requestedResY, ImpostorMode currentMode, ImpostorMode nextMode)
    {
        ImpostorTextureManager itm = ImpostorTextureManager.getItm_instance();
        Vector2 requestedResolution = new Vector2(requestedResX, requestedResY);

        // For the stereoscopic case:
        // Buffer current and previous texture coords of the first impostor
        // In case the second(right) stereoscopic impostor cannot claim a new tile, we can revert the claim of the first(left) stereoscopic impostor
        Vector4 tempLeftCurrent  = impostorInfo[0].currentTextureBounds;
        Vector4 tempLeftPrevious = impostorInfo[0].previousTextureBounds;

        // Claim first tile
        Vector4 previous = new Vector4();
        Vector4 next     = new Vector4();
        bool claimable   = itm.checkAtlasTileSlots(requestedResolution, impostorInfo[0].currentTextureBounds, ref previous, ref next);
        if (!claimable)
            return false;
        if (ImpostorManager.USE_BLENDING)
        {
            itm.getPreviousImpostorAtlas().claimTile(previous);
            impostorInfo[0].previousTextureBounds = previous;
            impostorInfo[1].previousTextureBounds = previous;
        }
        itm.getCurrentImpostorAtlas().claimTile(next);
        impostorInfo[0].nextTextureBounds = next;
        impostorInfo[1].nextTextureBounds = next;

        // Claim second tile (when trasitioning from or to stereo)
        if ((currentMode == ImpostorMode.STEREO || currentMode == ImpostorMode.STEREO_PARALLAX) ||
            (nextMode == ImpostorMode.STEREO    || nextMode == ImpostorMode.STEREO_PARALLAX))
        {
            previous = new Vector4();
            next     = new Vector4();
            if(currentMode == ImpostorMode.MONO || currentMode == ImpostorMode.MONO_PARALLAX) // When going from mono to stereo, we cannot reuse the current "right eye" tiles as left and right eye are the same for mono.
                claimable = itm.checkAtlasTileSlots(requestedResolution, new Vector4(-1,-1,-1,-1), ref previous, ref next);
            else
                claimable = itm.checkAtlasTileSlots(requestedResolution, impostorInfo[1].currentTextureBounds, ref previous, ref next);

            // If a tile could be claimed for the left impostor, but could not be claimed for the right -> Revert claimed tiles of left impostor and exit
            if (!claimable)
            {                
                itm.getCurrentImpostorAtlas().freeTile_Quadrants(impostorInfo[0].currentTextureBounds);
                impostorInfo[0].currentTextureBounds = tempLeftCurrent;
                if (ImpostorManager.USE_BLENDING)
                {
                    itm.getPreviousImpostorAtlas().freeTile_Quadrants(impostorInfo[0].previousTextureBounds);
                    impostorInfo[0].previousTextureBounds = tempLeftPrevious;
                    impostorInfo[1].previousTextureBounds = tempLeftPrevious;
                }
                return false;
            }
                
            if (ImpostorManager.USE_BLENDING && (currentMode == ImpostorMode.STEREO || currentMode == ImpostorMode.STEREO_PARALLAX))
            {
                itm.getPreviousImpostorAtlas().claimTile(previous);
                impostorInfo[1].previousTextureBounds = previous;
            }

            if (nextMode == ImpostorMode.STEREO || nextMode == ImpostorMode.STEREO_PARALLAX)
            {
                itm.getCurrentImpostorAtlas().claimTile(next);
                impostorInfo[1].nextTextureBounds = next;
            }
        }

        return true;
    }

    // Update is called once per frame
    void Update()
    {
        if (this.state == ImpostorState.BLENDING)
        {
            if (this.blendingWeight > 0.99999f)
            {
                this.state = ImpostorState.IDLE;
                ImpostorTextureManager.getItm_instance().getPreviousImpostorAtlas().freeTile_Quadrants(this.impostorInfo[0].previousTextureBounds);
                ImpostorTextureManager.getItm_instance().getPreviousImpostorAtlas().freeTile_Quadrants(this.impostorInfo[1].previousTextureBounds);

                //reclaim already claimed tile, Does not work atm...
                //                Vec2 res = ImpostorTextureManager.getItm_instance().getQuadrantTileresolution(this.generatedImpostor.getNextAtlasCoords());
                //                ImpostorTextureManager.getItm_instance().claimTile_Quadrants(this.generatedImpostor.getNextAtlasCoords(), res.x,res.y);
                return;
            }

            float blend_duration = ImpostorManager.BLENDING_DURATION; //Mathf.Clamp(this.sinceLastRegenerationReq, ImpostorManager.MIN_BLENDING_DURATION, ImpostorManager.MAX_BLENDING_DURATION);
            this.blendingWeight += Time.deltaTime / blend_duration;
            this.blendingWeight  = Mathf.Clamp01(this.blendingWeight);

            imPrimMat.SetFloat("_BlendingWeight", this.blendingWeight);
        }

        
    }

       
    internal void finalizeImpostor()
    {
        // Shift current to previous (matrices)
        if (ImpostorManager.USE_BLENDING)
        {
            this.impostorInfo[0].previousCaptureViewMatrix       = this.impostorInfo[0].currentCaptureViewMatrix;
            this.impostorInfo[0].previousCaptureProjectionMatrix = this.impostorInfo[0].currentCaptureProjectionMatrix;

            this.impostorInfo[1].previousCaptureViewMatrix       = this.impostorInfo[1].currentCaptureViewMatrix;
            this.impostorInfo[1].previousCaptureProjectionMatrix = this.impostorInfo[1].currentCaptureProjectionMatrix;
        }

        // Shift next to current (matrices)
        this.impostorInfo[0].currentCaptureViewMatrix       = this.impostorInfo[0].nextCaptureViewMatrix;
        this.impostorInfo[0].currentCaptureProjectionMatrix = this.impostorInfo[0].nextCaptureProjectionMatrix;
        this.impostorInfo[1].currentCaptureViewMatrix       = this.impostorInfo[1].nextCaptureViewMatrix;
        this.impostorInfo[1].currentCaptureProjectionMatrix = this.impostorInfo[1].nextCaptureProjectionMatrix;

        // Update the impostors AABB
        generatedImpostor.transform.position   = this.impostorBounds.center;
        generatedImpostor.transform.localScale = 2 * this.impostorBounds.extents;

        // Free Atlas tiles
        if (!(this.impostorInfo[0].currentTextureBounds == this.impostorInfo[0].nextTextureBounds))
            ImpostorTextureManager.getItm_instance().getCurrentImpostorAtlas().freeTile_Quadrants(this.impostorInfo[0].currentTextureBounds);
        if (!(this.impostorInfo[1].currentTextureBounds == this.impostorInfo[1].nextTextureBounds))
            ImpostorTextureManager.getItm_instance().getCurrentImpostorAtlas().freeTile_Quadrants(this.impostorInfo[1].currentTextureBounds);

        this.swapValidityFields();

        //Shift next to current (coordinates)
        this.impostorInfo[0].currentTextureBounds = this.impostorInfo[0].nextTextureBounds;
        this.impostorInfo[1].currentTextureBounds = this.impostorInfo[1].nextTextureBounds;
        //Set next ImpostorMode to current
        this.currentMode = this.nextMode;

        if (ImpostorManager.USE_BLENDING)
        {
            this.state = ImpostorState.BLENDING;
            this.blendingWeight = 0f;

            imPrimMat.SetMatrixArray("_PrevCaptureViewMat", new List<Matrix4x4> { this.impostorInfo[0].previousCaptureViewMatrix, this.impostorInfo[1].previousCaptureViewMatrix });
            imPrimMat.SetMatrixArray("_InvPrevCaptureViewMat", new List<Matrix4x4> { Matrix4x4.Inverse(this.impostorInfo[0].previousCaptureViewMatrix), Matrix4x4.Inverse(this.impostorInfo[1].previousCaptureViewMatrix) });
            imPrimMat.SetMatrixArray("_PrevCaptureProjMat", new List<Matrix4x4> { this.impostorInfo[0].previousCaptureProjectionMatrix, this.impostorInfo[1].previousCaptureProjectionMatrix });
            imPrimMat.SetMatrixArray("_InvPrevCaptureProjMat", new List<Matrix4x4> { Matrix4x4.Inverse(this.impostorInfo[0].previousCaptureProjectionMatrix), Matrix4x4.Inverse(this.impostorInfo[1].previousCaptureProjectionMatrix) });
            imPrimMat.SetVectorArray("_PrevAtlasCoords", new List<Vector4> { this.impostorInfo[0].previousTextureBounds, this.impostorInfo[1].previousTextureBounds });
            imPrimMat.SetFloat("_BlendingWeight", this.blendingWeight);
        }
        else
            this.state = ImpostorState.IDLE;

        // Update impostor material with new values
        imPrimMat.SetMatrixArray("_CaptureViewMat", new List<Matrix4x4> { this.impostorInfo[0].currentCaptureViewMatrix, this.impostorInfo[1].currentCaptureViewMatrix });
        imPrimMat.SetMatrixArray("_InvCaptureViewMat", new List<Matrix4x4> { Matrix4x4.Inverse(this.impostorInfo[0].currentCaptureViewMatrix), Matrix4x4.Inverse(this.impostorInfo[1].currentCaptureViewMatrix) });
        imPrimMat.SetMatrixArray("_CaptureProjMat", new List<Matrix4x4> { this.impostorInfo[0].currentCaptureProjectionMatrix, this.impostorInfo[1].currentCaptureProjectionMatrix });
        imPrimMat.SetMatrixArray("_InvCaptureProjMat", new List<Matrix4x4> { Matrix4x4.Inverse(this.impostorInfo[0].currentCaptureProjectionMatrix), Matrix4x4.Inverse(this.impostorInfo[1].currentCaptureProjectionMatrix) });

        imPrimMat.SetVector("_BboxMin", new Vector4(this.impostorBounds.min.x, this.impostorBounds.min.y, this.impostorBounds.min.z));
        imPrimMat.SetVector("_BboxMax", new Vector4(this.impostorBounds.max.x, this.impostorBounds.max.y, this.impostorBounds.max.z));
        imPrimMat.SetVectorArray("_AtlasCoords", new List<Vector4> { this.impostorInfo[0].currentTextureBounds, this.impostorInfo[1].currentTextureBounds });
        // TODO:
        imPrimMat.SetInteger("_ImpostorMode", (int) this.currentMode);
        // Debug.Log(this.name + " - Impostor was finalized! " + System.DateTime.Now);
    }

    /// <summary>
    /// Computes if the currently stored impostor is over or under sized, with respect to the current screen size and the available atlas slots
    /// Returns 1 if a better sized atlas slot is available (smaller or larger). 0 otherwise
    /// </summary>
    /// <returns></returns>
    private float computeTextureTerm()
    {
        Bounds b                    = this.impostorBounds;
        Vector3[] captureCamPosition;
        Matrix4x4[] virtualCamViewMat;

        // Prediction        
        Vector3 predictedOffset = Vector3.zero;
        if (this.mPred != null)
            predictedOffset = this.mPred.getNextOffsetPrediction(ImpostorManager.PREDICTION_FRAMES);

        this.computeViewMatrices(this.viewer, b, predictedOffset, this.currentMode, out captureCamPosition, out virtualCamViewMat);

        Camera.MonoOrStereoscopicEye cameraType = Camera.MonoOrStereoscopicEye.Mono;
        if (this.currentMode == ImpostorMode.STEREO || this.currentMode == ImpostorMode.STEREO_PARALLAX)
            cameraType = Camera.MonoOrStereoscopicEye.Left;

        ProjectionResults projRes = computeImpostorProjection(b, virtualCamViewMat[0], this.viewerCamera, cameraType);
        float resolutionTerm      = ImpostorTextureManager.currentImpostorAtlas.isBetterTilePresent(this.impostorInfo[0].currentTextureBounds, projRes.resX, projRes.resY);

        return resolutionTerm;
    }

    /// <summary>
    /// </summary>
    /// <returns></returns>
    internal float computeRegenerationMetric()
    {
        Vector3 viewerPosition = this.viewer.transform.position;
        // Prediction                
        if (this.mPred != null)
        {
            Vector3 predictedOffset = this.mPred.getNextOffsetPrediction(ImpostorManager.PREDICTION_FRAMES);
            viewerPosition += predictedOffset;
        }

        float primaryTerm   = 0f;
        float secondaryTerm = 0f;
        // Parallax Term
        float parallaxTerm     = this.currentValidityField.queryValidityField(viewerPosition);
        // Image Resolution Term
        float resolutionTerm = computeTextureTerm();

        primaryTerm = parallaxTerm + resolutionTerm;
        if (primaryTerm < 1)
            primaryTerm = 0;


        // First additional weighting
        float distToViewer = Vector3.Distance(viewerPosition, this.generatedImpostor.transform.position);
        secondaryTerm += (1.0f / distToViewer);

        //Second additional weighting
        Vector4 point_x = new Vector4(this.generatedImpostor.transform.position.x, this.generatedImpostor.transform.position.y, this.generatedImpostor.transform.position.z, 1);
        Matrix4x4 camViewMat = this.viewer.transform.worldToLocalMatrix;
        Vector4 point_x_cam = camViewMat * point_x;
        point_x_cam.z *= -1f; // Unity vs. OpenGL coords
        Vector4 point_x_proj = this.viewerCamera.nonJitteredProjectionMatrix * point_x_cam;
        point_x_proj /= point_x_proj.w;

        float fovDistance = Mathf.Sqrt(point_x_proj.x * point_x_proj.x + point_x_proj.y * point_x_proj.y);
        if (point_x_cam.z < 0)
            secondaryTerm += (1.0f / fovDistance);
        

        float finalMetric = primaryTerm * secondaryTerm;

        //if(debugTextObject != null)
        //    debugText.text = parallaxTermDerivative.ToString("F2");

        return primaryTerm * secondaryTerm;
    }

    public void switchToOriginalMesh()
    {
        if (this.generatedImpostor.GetComponent<MeshRenderer>().enabled)
        {
            //Disable the impostor primitive
            this.generatedImpostor.GetComponent<MeshRenderer>().enabled = false;

            //"Activate object" mesh by moving it away from the "mesh stash" layer
            foreach (MeshFilter mf in this.associatedMeshes)
            {
                if (layerStorage.ContainsKey(mf))
                {
                    mf.gameObject.layer = this.layerStorage[mf];
                }
            }
            this.layerStorage.Clear();
        }
    }

    public void switchToImpostor()
    {
        if (!this.generatedImpostor.GetComponent<MeshRenderer>().enabled)
        {
            //Enable the impostor primitive
            this.generatedImpostor.GetComponent<MeshRenderer>().enabled = true;

            //"Deactivate" object mesh by moving it away to the "mesh stash" layer
            foreach (MeshFilter mf in this.associatedMeshes)
            {
                if (!layerStorage.ContainsKey(mf))
                {
                    this.layerStorage.Add(mf, mf.gameObject.layer);
                    mf.gameObject.layer = this.im.getStashLayer();
                }
            }
        }
    }

    public void visualize_nextProjectionMatrix(float visualizationDuration)
    {
        // Next capture camera position
        GameObject ncp_go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        ncp_go.name = generatedImpostor.name + " - Next Capture Position";
        MeshRenderer ncp_go_mr = ncp_go.GetComponent<MeshRenderer>();
        ncp_go_mr.sharedMaterial.color = Color.red;
        ncp_go.transform.position = impostorInfo[0].nextCapturePosition;
        GameObject.Destroy(ncp_go, visualizationDuration);
        // Next capture camera frustum
        Vector4[] frustumCorners = {new Vector4(-1,-1,-1,1), new Vector4(1,-1,-1,1), new Vector4(1,1,-1,1), new Vector4(-1,1,-1,1),
                                          new Vector4(-1,-1,1,1), new Vector4(1,-1,1,1), new Vector4(1,1,1,1), new Vector4(-1,1,1,1)};
        GameObject[] ncf_gos = new GameObject[8];
        Vector3[] frustumCorners_ws = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            ncf_gos[i] = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ncf_gos[i].name = generatedImpostor.name + " - Next Capture Frustum P" + i;
            MeshRenderer tmp = ncf_gos[i].GetComponent<MeshRenderer>();
            tmp.sharedMaterial.color = Color.red;
            //
            Matrix4x4 invProjMat = Matrix4x4.Inverse(impostorInfo[0].nextCaptureProjectionMatrix);
            Matrix4x4 invViewMat = Matrix4x4.Inverse(impostorInfo[0].nextCaptureViewMatrix);
            Vector4 result = invProjMat * frustumCorners[i];
            result /= result.w;
            result = invViewMat * result;
            frustumCorners_ws[i] = new Vector3(result.x, result.y, result.z);
            ncf_gos[i].transform.position = frustumCorners_ws[i];
            GameObject.Destroy(ncf_gos[i], visualizationDuration);
        }
        UtilityFunctions.DrawBBoxLines(frustumCorners_ws, Color.blue, visualizationDuration);
    }

    internal ImpostorState getCurrentState()
    {
        return this.state;
    }

    internal RenderTexture getCurrentRT()
    {
        return currrentRDTex;
    }

    internal RenderTexture getPreviousRT()
    {
        return previousRDTex;
    }

    internal float getViewerIPD()
    {
        return this.viewerIPD;
    }

    internal ImpostorMode GetCurrentImpostorMode()
    {
        return this.currentMode;
    }

    internal ImpostorMode GetNextImpostorMode()
    {
        return this.nextMode;
    }

    internal void setViewerIPD(float ipd)
    {
        this.viewerIPD = ipd;
    }

    /// <summary>
    /// This method returns a reference to the ImpostorMetadata member. Metadata include information as
    /// the current, next and previous matrices, atlas coordinates etc...
    /// TODO: How to make its members non-editable ? 
    /// </summary>
    internal ImpostorMetadata[] getImpostorMetadata()
    {
        return this.impostorInfo;
    }

    internal Bounds getImpostorBounds()
    {
        return this.impostorBounds;
    }

    /// <summary>
    /// This method claims new atlas tiles, as well as prepares the view/projection matrices for the next scheduled impostor.
    /// returns true or false if it was successful
    /// </summary>
    public bool generateNewImpostor(ImpostorMode nextImpostorMode)
    {
        if (this.state != ImpostorState.IDLE)
            return false;
        this.state      = ImpostorState.GENERATING;
        this.nextMode   = nextImpostorMode;

        // Prediction        
        Vector3 predictedOffset = Vector3.zero;
        if (this.mPred != null)
            predictedOffset = this.mPred.getNextOffsetPrediction(ImpostorManager.PREDICTION_FRAMES);

        // Determine impostor bounding box (for next impostor)
        // TODO: This should only be done when the bounds of an impostor change! For most cases, they remain static
        Bounds b = this.computeImpostorBounds();

        // Declare mono/stereo variables
        Vector3[] captureCamPosition; 
        Matrix4x4[] virtualCamViewMat;

        // Compute view matrices
        this.computeViewMatrices(viewer, b, predictedOffset, nextImpostorMode, out captureCamPosition, out virtualCamViewMat);

        if (nextImpostorMode == ImpostorMode.MONO || nextImpostorMode == ImpostorMode.MONO_PARALLAX)
        {
            // Compute impostor projection
            this.lastImpostorProjection[0] = computeImpostorProjection(b, virtualCamViewMat[0], this.viewerCamera, Camera.MonoOrStereoscopicEye.Mono);
            // Claim texture tile
            bool isClaimed = claimImpostorTiles((int)this.lastImpostorProjection[0].resX, (int)this.lastImpostorProjection[0].resY, this.currentMode, this.nextMode);
            if (!isClaimed)
            {
                Debug.LogWarning("Could not allocate a fitting tile. Postponing regeneration!");
                this.state = ImpostorState.IDLE;
                return false;
            }
            // Set data for next impostor
            this.impostorBounds                              = b;
            this.impostorInfo[0].nextCaptureViewMatrix       = virtualCamViewMat[0];
            this.impostorInfo[0].nextCaptureProjectionMatrix = computeProjectionMatrix(this.lastImpostorProjection[0], ImpostorTextureManager.useOIR);
            this.impostorInfo[0].nextCapturePosition         = captureCamPosition[0];

            this.impostorInfo[1].nextCaptureViewMatrix       = virtualCamViewMat[0];
            this.impostorInfo[1].nextCaptureProjectionMatrix = this.impostorInfo[0].nextCaptureProjectionMatrix;
            this.impostorInfo[1].nextCapturePosition         = captureCamPosition[0];

            //Compute next view cell;
            this.nextValidityField.computeValidityField(captureCamPosition[0]);

            return true;
        }
        else
        {
            // Compute impostor projections
            this.lastImpostorProjection[0] = computeImpostorProjection(b, virtualCamViewMat[0], this.viewerCamera, Camera.MonoOrStereoscopicEye.Left);
            this.lastImpostorProjection[1] = computeImpostorProjection(b, virtualCamViewMat[1], this.viewerCamera, Camera.MonoOrStereoscopicEye.Right);
            // Claim texture tiles
            bool isClaimed = claimImpostorTiles((int)this.lastImpostorProjection[0].resX, (int)this.lastImpostorProjection[0].resY, this.currentMode, this.nextMode);
            if (!isClaimed)
            {
                Debug.LogWarning("Could not allocate a fitting tile. Postponing regeneration!");
                this.state = ImpostorState.IDLE;
                return false;
            }

            // Set data for next impostor
            this.impostorBounds = b;
            this.impostorInfo[0].nextCaptureViewMatrix       = virtualCamViewMat[0];
            this.impostorInfo[0].nextCaptureProjectionMatrix = computeProjectionMatrix(this.lastImpostorProjection[0], ImpostorTextureManager.useOIR);
            this.impostorInfo[0].nextCapturePosition         = captureCamPosition[0];
            this.impostorInfo[1].nextCaptureViewMatrix       = virtualCamViewMat[1];
            this.impostorInfo[1].nextCaptureProjectionMatrix = computeProjectionMatrix(this.lastImpostorProjection[1], ImpostorTextureManager.useOIR);
            this.impostorInfo[1].nextCapturePosition         = captureCamPosition[1];
            //Compute next view cell;
            this.nextValidityField.computeValidityField((captureCamPosition[0] + captureCamPosition[1]) * 0.5f);

            return true;
        }
    }

    void onDisable()
    {
        if (im != null)
            im.removeImpostorGenerator(this);
    }

    void onDestroy()
    {
        if (im != null)
            im.removeImpostorGenerator(this);
    }

    void onEnable()
    {
        if (im != null)
            im.registerImpostorGenerator(this);
    }
}
