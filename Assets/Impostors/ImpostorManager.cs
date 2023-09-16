using System.Collections;
using System.Collections.Generic;
using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;


/// <summary>
/// Comparer for comparing two keys, handling equality as beeing greater
/// Use this Comparer e.g. with SortedLists or SortedDictionaries, that don't allow duplicate keys
/// </summary>
/// <typeparam name="TKey"></typeparam>
/// https://stackoverflow.com/questions/5716423/c-sharp-sortable-collection-which-allows-duplicate-keys
public class DuplicateKeyComparer<TKey> :
             IComparer<TKey> where TKey : IComparable
{
    public int Compare(TKey x, TKey y)
    {
        int result = y.CompareTo(x);

        if (result == 0)
            return 1; // Handle equality as being greater. Note: this will break Remove(key) or
        else          // IndexOfKey(key) since the comparer never returns 0 to signal key equality
            return result;
    }
}

public class ImpostorManager : MonoBehaviour
{
    private Material currentToPreviousMat;
    private Dictionary<ImpostorGenerator, float> regenerationMetricList;
    private List<ImpostorGenerator> impostorGenerators; // Holds a list of all ImpostorGenerators in the scene
    private bool REGENERATION = false;
    // Requires a user defined layer named "Impostor Regeneration"
    private int regenerationLayer;
    // Requires a user defined layer named "Mesh Stash"
    private int meshStashLayer;

    public static float GLOBAL_IMPOSTOR_THRESHOLD_DISTANCE  = 1f;    // Defines the distance for switching from impostor back to the mesh representation
    public static bool USE_BLENDING                         = true;  // Blend from previous impostor no new one over time
    public static float BLENDING_DURATION                   = 0.25f;
    // Maximum number of triangles which can be regenerated per frame. Larger objects can still be regenerated, if they are on top of the regeneration list (one such object per frame)
    public static long MAX_REGENERATION_TRIANGLES_PER_FRAME = 100000;  

    public static float PROJECTED_BBOX_METRIC_THRESHOLD     = 1.0f; 
    public static float NAIVE_ANGLE_METRIC_THRESHOLD        = 3.0f; 
    public static int COMPUTE_METRIC_PER_FRAME              = 50;  // How many impostors have their regeneration metric evaluated per frame ?
    public static int PREDICTION_FRAMES                     = 10;  // Predict where the viewer will be in this many frames to regenerate the impostors for this future view. A value of 0 disables prediction

    public Camera regenerationCamera = null;
    public static int frameCounter   = 0;

    private static int lastUpdatedImpostorIndex = -1;
    private static long budgetCounter = MAX_REGENERATION_TRIANGLES_PER_FRAME;
    private Vector2 observerResolution = new Vector2( 1920 , 1080);

    public RenderTexture debug_currentAtlas;
    public RenderTexture debug_previousAtlas;
            
    void Awake()
    {
        this.regenerationMetricList = new Dictionary<ImpostorGenerator, float>();
        this.impostorGenerators     = new List<ImpostorGenerator>();
        this.currentToPreviousMat   = new Material(Shader.Find("Hidden/CopyTex"));

        if (USE_BLENDING)
            Shader.EnableKeyword("USE_IMPOSTOR_BLENDING");
        else 
            Shader.DisableKeyword("USE_IMPOSTOR_BLENDING");
    }

    void doURPSettings()
    {
        UniversalAdditionalCameraData addData = regenerationCamera.gameObject.AddComponent<UniversalAdditionalCameraData>();

        ScriptableRendererData[] rendererDataList = (ScriptableRendererData[])typeof(UniversalRenderPipelineAsset).GetField("m_RendererDataList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(UniversalRenderPipeline.asset);

        bool foundImpostorRenderer = false;
        for (int i = 0; i < rendererDataList.Length; i++)
        {
            if (rendererDataList[i].name == "URP_Renderer_Impostors")
            {
                foundImpostorRenderer = true;
                addData.SetRenderer(i);
            }
        }
        if(!foundImpostorRenderer)
        {
            Debug.LogError("Could not find the URP_Renderer_Impostors renderer in the current URP Asset");
            Component.Destroy(this);
        }

        if(UniversalRenderPipeline.asset.useSRPBatcher)
        {
            Debug.Log("Disabling SRP Batcher, as this breaks impostor regeneration!");
            UniversalRenderPipeline.asset.useSRPBatcher = false;
        }
    }    

    void Start()
    {
        this.regenerationLayer = LayerMask.NameToLayer("Impostor Regeneration");
        if (this.regenerationLayer == -1)
        {
            Debug.LogError("Error! 'Impostor Regeneration' layer not found! Create a custom user layer named 'Impostor Regeneration' in the Unity Editor for this component to work!");
            Component.Destroy(this);
            return;
        }

        this.meshStashLayer = LayerMask.NameToLayer("Mesh Stash");
        if (this.meshStashLayer == -1)
        {
            Debug.LogError("Error! 'Mesh Stash' layer not found! Create a custom user layer named 'Mesh Stash' in the Unity Editor for this component to work!");
            Component.Destroy(this);
            return;
        }

        // Link Atlases to manager for visualization
        this.debug_currentAtlas = ImpostorTextureManager.getItm_instance().getCurrentImpostorAtlas().getRadianceTexture();
        if (USE_BLENDING)
            this.debug_previousAtlas = ImpostorTextureManager.getItm_instance().getPreviousImpostorAtlas().getRadianceTexture();


        // Setup Regeneration camera(s)
        GameObject regCamGO = new GameObject("Virtual Impostor Regeneration Camera");
        regenerationCamera  = regCamGO.AddComponent<Camera>();
        //regenerationCamera.cullingMask = 1 << this.regenerationLayer;
        regenerationCamera.backgroundColor = new Color(0, 0, 0, 0);
        regenerationCamera.clearFlags = CameraClearFlags.Color | CameraClearFlags.Depth;
        regenerationCamera.renderingPath = RenderingPath.Forward;
        regenerationCamera.enabled = false;
        regenerationCamera.forceIntoRenderTexture = true;
        regenerationCamera.depthTextureMode = DepthTextureMode.None; // "depthTextureMode" Not needed as we render the depth into a separate RenderTexture...
        regenerationCamera.allowMSAA        = false;
        regenerationCamera.stereoTargetEye  = StereoTargetEyeMask.None;

        doURPSettings();

        // Exclude "Impostor Regeneration" and "Mesh Stash" layers from the culling mask of the main rendering camera
        Camera.main.cullingMask &= ~(1 << this.meshStashLayer);

        this.updateObserverInformation();
        Invoke("forceImpostorRendering", 5);
        Invoke("delayedRegenerationStart", 5);
    }

    private void updateObserverInformation()
    {
        if (UnityEngine.XR.XRSettings.enabled)
            this.observerResolution = new Vector2(UnityEngine.XR.XRSettings.eyeTextureWidth * ImpostorTextureManager.OIR, UnityEngine.XR.XRSettings.eyeTextureHeight * ImpostorTextureManager.OIR);
        else
            this.observerResolution = new Vector2(Screen.width * ImpostorTextureManager.OIR, Screen.height * ImpostorTextureManager.OIR);
    }


    private void regenerateImpostors(IList<ImpostorGenerator> igs)
    {
        for (int i = 0; i < igs.Count; i++)
        {
            // To single out objects during regeneration, they are assigned temporarilly to the "Impostor Regeneration" layer
            List<int> origLayersTemp = new List<int>();
            foreach (MeshFilter m in igs[i].associatedMeshes)
            {
                origLayersTemp.Add(m.gameObject.layer);
                m.gameObject.layer = this.regenerationLayer;
            }
            ImpostorMetadata[] imMetaData    = igs[i].getImpostorMetadata();
            ImpostorMode currentImpostorMode = igs[i].GetCurrentImpostorMode();
            ImpostorMode nextImpostorMode    = igs[i].GetNextImpostorMode();

            // Copy current impostor to previous-atlas
            if (USE_BLENDING)
            {
                CopyTexture(imMetaData[0].currentTextureBounds, igs[i].getCurrentRT(), imMetaData[0].previousTextureBounds, igs[i].getPreviousRT());

                if(currentImpostorMode == ImpostorMode.STEREO || currentImpostorMode == ImpostorMode.STEREO_PARALLAX)
                {
                    CopyTexture(imMetaData[1].currentTextureBounds, igs[i].getCurrentRT(), imMetaData[1].previousTextureBounds, igs[i].getPreviousRT());
                }
            }

            // Rendering Setup & Rendering Command
            Camera cam = regenerationCamera;
            //ISSUE: cam.targetTexture does not respect targetTexture.antiAliasing values....just takes project quality settings...
            //cam.SetTargetBuffers(igs[i].getAccumulationRT().colorBuffer, igs[i].getAccumulationRT().depthBuffer);
            cam.targetTexture = igs[i].getCurrentRT();
            // TODO: Protect against inf. or negative render bounds
            Vector4 renderBounds = imMetaData[0].nextTextureBounds;
            Vector2 renderStart = new Vector2(renderBounds.x, renderBounds.y);
            Vector2 renderSize = new Vector2(renderBounds.z - renderBounds.x, renderBounds.w - renderBounds.y);
            cam.rect = new Rect(renderStart, renderSize);
            cam.transform.position  = imMetaData[0].nextCapturePosition;
            cam.worldToCameraMatrix = imMetaData[0].nextCaptureViewMatrix;
            cam.projectionMatrix    = imMetaData[0].nextCaptureProjectionMatrix;
            cam.Render();

            if (nextImpostorMode == ImpostorMode.STEREO || nextImpostorMode == ImpostorMode.STEREO_PARALLAX)
            {
                renderBounds = imMetaData[1].nextTextureBounds;
                renderStart = new Vector2(renderBounds.x, renderBounds.y);
                renderSize = new Vector2(renderBounds.z - renderBounds.x, renderBounds.w - renderBounds.y);
                cam.rect = new Rect(renderStart, renderSize);
                cam.transform.position = imMetaData[1].nextCapturePosition;
                cam.worldToCameraMatrix = imMetaData[1].nextCaptureViewMatrix;
                cam.projectionMatrix = imMetaData[1].nextCaptureProjectionMatrix;
                cam.Render();
            }

            igs[i].finalizeImpostor();
            // The objects previous layers are restored after the regeneration step
            int count = 0;
            foreach (MeshFilter m in igs[i].associatedMeshes)
            {
                m.gameObject.layer = origLayersTemp[count];
                count++;
            }
        }

    }

    private void CopyTexture(Vector4 srcCoords, RenderTexture srcColor, Vector4 destCoords, RenderTexture destColor)
    {
        GL.PushMatrix();
        Graphics.SetRenderTarget(destColor.colorBuffer, destColor.depthBuffer);
        this.currentToPreviousMat.SetTexture("_SrcColorTex", srcColor, RenderTextureSubElement.Color);
        this.currentToPreviousMat.SetTexture("_SrcDepthTex", srcColor, RenderTextureSubElement.Depth);
        this.currentToPreviousMat.SetVector("_AtlasCoords", srcCoords);
        currentToPreviousMat.SetPass(0);
        GL.LoadOrtho();
        GL.Viewport(new Rect(destCoords.x * destColor.width, destCoords.y * destColor.height, (destCoords.z - destCoords.x) * destColor.width, (destCoords.w - destCoords.y) * destColor.height));

        GL.Begin(GL.QUADS);
        GL.TexCoord2(0, 0);
        GL.Vertex3(0.0F, 0.0F, 0);
        GL.TexCoord2(0, 1);
        GL.Vertex3(0.0F, 1.0F, 0);
        GL.TexCoord2(1, 1);
        GL.Vertex3(1.0F, 1.0F, 0);
        GL.TexCoord2(1, 0);
        GL.Vertex3(1.0F, 0.0F, 0);
        GL.End();

        GL.PopMatrix();
    }

    internal void registerImpostorGenerator(ImpostorGenerator ig)
    {
        if (impostorGenerators.Find(r => r == ig))
            Debug.LogWarning("Cannot register impostorGenerator, as it is already registered!");
        else
        {
            impostorGenerators.Add(ig);
            regenerationMetricList.Add(ig, 0);
        }
    }

    internal void removeImpostorGenerator(ImpostorGenerator ig)
    {
        if (impostorGenerators.Find(r => r == ig))
        {
            impostorGenerators.Remove(ig);
            regenerationMetricList.Remove(ig);
        }
        else
            Debug.Log("Cannot remove impostorGenerator, as it is was not previously registered!");
    }

    private void forceImpostorRendering()
    {
        List<ImpostorGenerator> regenerationCandidates = new List<ImpostorGenerator>();
        foreach (ImpostorGenerator ig in impostorGenerators)
        {
            ig.switchToImpostor();

            ImpostorMode mode = determineImpostorMode(ig);
            bool regeneration_scheduled = ig.generateNewImpostor(mode);
            if (regeneration_scheduled)
                regenerationCandidates.Add(ig);
        }
        regenerateImpostors(regenerationCandidates);
    }

    private void forceMeshRendering()
    {
        foreach (ImpostorGenerator ig in impostorGenerators)
            ig.switchToOriginalMesh();
    }

    // Compute and update the regeneration metric of the next few impostors
    // Compute the distance between the next few impostors and the observer. Switch to mesh if necessary
    private SortedList<float, ImpostorGenerator> determineRegenerationCandidates()
    {
        // Compute and update the regeneration metric of the next few impostors
        int impostorID = 0;
        for (int i = 1; i <= Mathf.Min(COMPUTE_METRIC_PER_FRAME, impostorGenerators.Count); i++)
        {
            impostorID = (lastUpdatedImpostorIndex + i) % impostorGenerators.Count;
            ImpostorGenerator ig = impostorGenerators[impostorID];
            float priority = 0;
            if (ig.getCurrentState() == ImpostorState.IDLE)
            {
                // Compute distance to observer
                float distanceToObserver = Vector3.Distance(ig.getImpostorBounds().center, ig.viewerCamera.transform.position);
                distanceToObserver -= Vector3.Magnitude(ig.getImpostorBounds().extents);
                if (distanceToObserver <= GLOBAL_IMPOSTOR_THRESHOLD_DISTANCE)
                    ig.switchToOriginalMesh();
                else
                {
                    ig.switchToImpostor();
                    priority = ig.computeRegenerationMetric();
                }
            }
            this.regenerationMetricList[ig] = priority;
        }

        lastUpdatedImpostorIndex = impostorID;
        // Create a list sorted in descending order
        SortedList<float, ImpostorGenerator> result = new SortedList<float, ImpostorGenerator>(new DuplicateKeyComparer<float>());
        foreach (KeyValuePair<ImpostorGenerator, float> kv in this.regenerationMetricList)
        {
            if (kv.Value > 0)
                result.Add(kv.Value, kv.Key); // priority is not unique as a key!!!
        }

        return result;
    }

    internal int getStashLayer()
    {
        return this.meshStashLayer;
    }

    internal Vector2 getObserverResolution()
    {
        return this.observerResolution;
    }

    private void DEBUGKeypress()
    {

        if (Input.GetKeyUp(KeyCode.J))
        {
            this.forceImpostorRendering();
            this.REGENERATION = true;
        }

        if (Input.GetKeyUp(KeyCode.K))
        {
            this.forceMeshRendering();
            this.REGENERATION = false;
        }
        
        if (Input.GetKeyUp(KeyCode.P))
        {
            foreach (ImpostorGenerator ig in impostorGenerators)
                ig.visualize_nextProjectionMatrix(5f);
        }
    }
    

    private void delayedRegenerationStart()
    {
        this.REGENERATION = true;
    }

    void Update()
    {
        frameCounter++;
        this.DEBUGKeypress();
        this.updateObserverInformation();

        if (REGENERATION)
        {
            // Hand out regeneration budget at the start of the frame
            budgetCounter = MAX_REGENERATION_TRIANGLES_PER_FRAME;

            // Determine regeneration candidates
            SortedList<float, ImpostorGenerator> candidateList = determineRegenerationCandidates();
            if (candidateList.Count == 0)
                return;

            // Distribute available budget onto "regeneration-worthy" impostors
            List<ImpostorGenerator> selectedCandidates = new List<ImpostorGenerator>();
            for (int i = 0; i < candidateList.Count; i++)
            {
                ImpostorGenerator ig = candidateList.Values[i];
                long budget = ig.getTriangleCount();

                // The first candidate is always regenerated, even if the budget is insufficient
                if ((budgetCounter >= budget) || (i == 0))
                {
                    ImpostorMode mode = determineImpostorMode(ig);
                    bool regeneration_scheduled = ig.generateNewImpostor(mode);

                    if (regeneration_scheduled)
                    {
                        selectedCandidates.Add(ig);
                        budgetCounter -= budget;
                    }
                }
            }

            // Perform the actual rendering step
            regenerateImpostors(selectedCandidates);
        }
    }

    private ImpostorMode determineImpostorMode(ImpostorGenerator ig)
    {
        ImpostorMode mode = ig.impostorMode;

        // TODO: Include metric for switching between impostor modes
        if (mode == ImpostorMode.AUTOMATIC)
        {
            if (XRGraphics.isDeviceActive)
                mode = ImpostorMode.STEREO_PARALLAX;
            else
                mode = ImpostorMode.MONO_PARALLAX;
        }
        else if (mode == ImpostorMode.STEREO && !XRGraphics.isDeviceActive)
            mode = ImpostorMode.MONO;
        else if (mode == ImpostorMode.STEREO_PARALLAX && !XRGraphics.isDeviceActive)
            mode = ImpostorMode.MONO_PARALLAX;

        return mode;
    }

}
