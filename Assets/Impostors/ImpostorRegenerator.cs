using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using System.Collections.Generic;

// This render feature is basically only responsible for copying over the depthbuffer from the regeneration camera into the atlas.
// This is only neccessary as a workaround, since Unity (2021.3.6f1) will not copy the depth buffer of the temporary camera texture,
// into the assigned renderTexture of the camera.
// Detailed explanation:
// When using a custom targetTexture for a given camera, and executing camera.Render(), the camera does NOT render directly into this target texture.
// The camera renders into a temporary renderbuffer first and copies the results to the target texture afterwards. Due to the said bug,
// the depth buffer is not copied over to the target texture, only the color buffer. That is why we have to additionally copy the depth buffer over via this 
// render feature. If this bug is fixed in a future Unity version, this workaround should become obsolete.
public class ImpostorRegenerator : ScriptableRendererFeature
{
    class ImpostorRegenerationPass : ScriptableRenderPass
    {
        ProfilingSampler m_ProfilingSampler = new ProfilingSampler("ImpostorDepthCopyWorkaround");
        Material m_copyDepthMat;
        // This is called once per frame, when this pass is added to the renderer. Used to transfer parameters from "Behaviour" logic to "Render" logic
        public bool SetupPass(Material copyDepthMat)
        {
            if (copyDepthMat == null)
                return false;

            m_copyDepthMat = copyDepthMat;
            return true;
        }

        // This method is called before executing the render pass.
        // It can be used to configure render targets and their clear state. Also to create temporary render target textures.
        // When empty this render pass will render to the active camera render target.
        // You should never call CommandBuffer.SetRenderTarget. Instead call <c>ConfigureTarget</c> and <c>ConfigureClear</c>.
        // The render pipeline will ensure target setup and clearing happens in a performant manner.
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
        }

        // Here you can implement the rendering logic.
        // Use <c>ScriptableRenderContext</c> to issue drawing commands or execute command buffers
        // https://docs.unity3d.com/ScriptReference/Rendering.ScriptableRenderContext.html
        // You don't have to call ScriptableRenderContext.submit, the render pipeline will call it at specific points in the pipeline.
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            // Copy CameraDepthAttachment manually into camera.targetTexture.depthBuffer as Unity does omit depth copying
            Camera camera = renderingData.cameraData.camera;
            CommandBuffer cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                var renderer = renderingData.cameraData.renderer;
                cmd.SetRenderTarget(camera.targetTexture.colorBuffer, camera.targetTexture.depthBuffer);

                Rect pixelRect = new Rect(camera.rect.position.x * camera.targetTexture.width, camera.rect.position.y * camera.targetTexture.height,
                    camera.rect.width * camera.targetTexture.width, camera.rect.height * camera.targetTexture.height);
                cmd.SetViewport(pixelRect);
                cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, m_copyDepthMat);
            }
            context.ExecuteCommandBuffer(cmd);
            cmd.Clear();
            CommandBufferPool.Release(cmd);
        }

        // Cleanup any allocated resources that were created during the execution of this render pass.
        public override void OnCameraCleanup(CommandBuffer cmd)
        {
        }
    }

    class ImpostorFinishPass : ScriptableRenderPass
    {
        // This method is called before executing the render pass.
        // It can be used to configure render targets and their clear state. Also to create temporary render target textures.
        // When empty this render pass will render to the active camera render target.
        // You should never call CommandBuffer.SetRenderTarget. Instead call <c>ConfigureTarget</c> and <c>ConfigureClear</c>.
        // The render pipeline will ensure target setup and clearing happens in a performant manner.
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
        }

        // Here you can implement the rendering logic.
        // Use <c>ScriptableRenderContext</c> to issue drawing commands or execute command buffers
        // https://docs.unity3d.com/ScriptReference/Rendering.ScriptableRenderContext.html
        // You don't have to call ScriptableRenderContext.submit, the render pipeline will call it at specific points in the pipeline.
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
        }

        // Cleanup any allocated resources that were created during the execution of this render pass.
        public override void OnCameraCleanup(CommandBuffer cmd)
        {
        }
    }


    // RenderFeature Variables
    ImpostorRegenerationPass m_RegenerationPass;
    ImpostorManager m_Manager = null;
    Material m_copyDepthMat;
    [SerializeField, HideInInspector] private Shader m_copyDepthShader = null;
    

    /// <inheritdoc/>
    /// This function is called upon serialization. So expect the unexpected, handle everything...
    public override void Create()
    {
        m_Manager = Component.FindObjectOfType<ImpostorManager>();
        
        if (m_Manager != null && m_RegenerationPass == null)
        {
            m_RegenerationPass = new ImpostorRegenerationPass();
            m_RegenerationPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
        }
    }

    // Here you can inject one or multiple render passes in the renderer.
    // This method is called when setting up the renderer once per-camera, per Frame
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (!GetMaterial() || m_Manager == null)
            return;

        if (renderingData.cameraData.camera == m_Manager.regenerationCamera)
        {
            bool shouldAddReg = m_RegenerationPass.SetupPass(m_copyDepthMat);
            m_RegenerationPass.ConfigureInput(ScriptableRenderPassInput.Depth);
            // TODO: Enqueue despite regenerationList being empty ? Could lead to less frame spikes, but overall slower performance ? Should be investigated...
            if (shouldAddReg)
                renderer.EnqueuePass(m_RegenerationPass);
        }
    }

    private bool GetMaterial()
    {
        if (m_copyDepthMat != null)
            return true;

        if (m_copyDepthShader == null)
        {
            m_copyDepthShader = Shader.Find("CustomShaders/CopyDepth");
            if (m_copyDepthShader == null)
                return false;
        }
        m_copyDepthMat = CoreUtils.CreateEngineMaterial(m_copyDepthShader);

        return (m_copyDepthMat != null);
    }
}


