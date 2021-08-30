// AMD FSR For Unity Universal render pipeline

//Copyright<2021> < Abigail Hocking (aka Ninlilizi) >
//Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
//documentation files (the "Software"), to deal in the Software without restriction, including without limitation
//the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
//to permit persons to whom the Software is furnished to do so, subject to the following conditions:

//The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

//THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
//THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
//TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
//#if UNIVERSAL_PIPELINE_CORE_INCLUDED
using UnityEngine.Rendering.Universal;
//#endif
#if !UNITY_2019_2_OR_NEWER
using UnityEngine.XR;
#endif

using static UnityEngine.Rendering.Universal.UniversalAdditionalCameraData;

namespace NKLI
{
//#if UNIVERSAL_PIPELINE_CORE_INCLUDED
    /// <summary>
    /// Render pipeline feature
    /// </summary>
    public class FSR_UniversalRenderPipeline : ScriptableRendererFeature
    {
        // Shader
        private static ComputeShader compute_FSR;

        // Render textures
        private static RenderTexture RT_FSR_RenderTarget;
        private static RenderTexture RT_Output;

        // Flags
        private bool textures_created;
        private float render_scale_cached;

        // Settings
        public FSRSettings settings = new FSRSettings();
        private static FSRSettings fsrSettings;

        // Passes
        FSRPass fsrPass;
        BeforeEverythingPass beforePass;

        /// <summary>
        /// Sets the camera render target
        /// </summary>
        private class BeforeEverythingPass : ScriptableRenderPass
        {
            public BeforeEverythingPass(RenderPassEvent renderPass)
            {
                this.renderPassEvent = renderPass;
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                renderingData.cameraData.targetTexture = RT_FSR_RenderTarget;
            }
        }

        /// <summary>
        /// Render pass
        /// </summary>
        private class FSRPass : ScriptableRenderPass
        {
            // Identifiers
            private RenderTargetIdentifier source { get; set; }
            private RenderTargetHandle destination;
            RenderTargetHandle m_TemporaryIntermediaryTexture;


            private void OnDisable()
            {
                // Dispose render target
                if (RT_FSR_RenderTarget != null) RT_FSR_RenderTarget.Release();
                if (RT_Output != null) RT_Output.Release();
            }


            /// <summary>
            /// Render pass setup
            /// </summary>
            /// <param name="renderPassEvent"></param>
            /// <param name="blitMaterial"></param>
            /// <param name="blitShaderPassIndex"></param>
            /// <param name="tag"></param>
            public FSRPass(RenderPassEvent renderPassEvent)
            {
                this.renderPassEvent = renderPassEvent;
                m_TemporaryIntermediaryTexture.Init("_TemporaryIntermediaryTexture");
            }


            /// <summary>
            /// Setup stuff
            /// </summary>
            /// <param name="source"></param>
            /// <param name="destination"></param>
            public void Setup(RenderTargetIdentifier source, RenderTargetHandle destination)
            {
                this.source = source;
                this.destination = destination;
            }


            /// <summary>
            /// Processes the effect
            /// </summary>
            /// <param name="context"></param>
            /// <param name="renderingData"></param>
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                CommandBuffer cmd = CommandBufferPool.Get();

                cmd.SetComputeIntParam(compute_FSR, "input_viewport_width", RT_FSR_RenderTarget.width);
                cmd.SetComputeIntParam(compute_FSR, "input_viewport_height", RT_FSR_RenderTarget.height);
                cmd.SetComputeIntParam(compute_FSR, "input_image_width", RT_FSR_RenderTarget.width);
                cmd.SetComputeIntParam(compute_FSR, "input_image_height", RT_FSR_RenderTarget.height);

                cmd.SetComputeIntParam(compute_FSR, "output_image_width", RT_Output.width);
                cmd.SetComputeIntParam(compute_FSR, "output_image_height", RT_Output.height);

                cmd.SetComputeIntParam(compute_FSR, "upsample_mode", (int)fsrSettings.upsample_mode);

                int dispatchX = (RT_Output.width + (16 - 1)) / 16;
                int dispatchY = (RT_Output.height + (16 - 1)) / 16;


                RenderTextureDescriptor renderDesc = renderingData.cameraData.cameraTargetDescriptor;
                renderDesc.colorFormat = renderingData.cameraData.camera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;
                renderDesc.width = RT_Output.width;
                renderDesc.height = RT_Output.height;
                renderDesc.enableRandomWrite = true;
                renderDesc.useMipMap = false;
                renderDesc.depthBufferBits = 24;
                cmd.GetTemporaryRT(destination.id, renderDesc);


                if (fsrSettings.sharpening && fsrSettings.upsample_mode == FSRSettings.upsample_modes.FSR)
                {
                    // Create intermediary render texture
                    cmd.GetTemporaryRT(m_TemporaryIntermediaryTexture.id, renderDesc);

                    // Upscale
                    cmd.SetComputeIntParams(compute_FSR, "upscale_or_sharpen", 1);
                    cmd.SetComputeTextureParam(compute_FSR, 0, "InputTexture", RT_FSR_RenderTarget);
                    cmd.SetComputeTextureParam(compute_FSR, 0, "OutputTexture", m_TemporaryIntermediaryTexture.id);
                    cmd.DispatchCompute(compute_FSR, 0, dispatchX, dispatchY, 1);

                    // Sharpen
                    cmd.SetComputeIntParams(compute_FSR, "upscale_or_sharpen", 0);
                    cmd.SetComputeFloatParam(compute_FSR, "sharpness", 2 - fsrSettings.sharpness);
                    cmd.SetComputeTextureParam(compute_FSR, 0, "InputTexture", m_TemporaryIntermediaryTexture.id);
                    cmd.SetComputeTextureParam(compute_FSR, 0, "OutputTexture", RT_Output);
                    cmd.DispatchCompute(compute_FSR, 0, dispatchX, dispatchY, 1);
                }
                else
                {
                    cmd.SetComputeIntParams(compute_FSR, "upscale_or_sharpen", 1);
                    cmd.SetComputeTextureParam(compute_FSR, 0, "InputTexture", RT_FSR_RenderTarget);
                    cmd.SetComputeTextureParam(compute_FSR, 0, "OutputTexture", RT_Output);
                    cmd.DispatchCompute(compute_FSR, 0, dispatchX, dispatchY, 1);
                }

                Blit(cmd, RT_Output, destination.id);

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);

                renderingData.cameraData.camera.targetTexture = null;
            }

            public override void FrameCleanup(CommandBuffer cmd)
            {
                if (destination == RenderTargetHandle.CameraTarget)
                {
                    cmd.ReleaseTemporaryRT(destination.id);
                    cmd.ReleaseTemporaryRT(m_TemporaryIntermediaryTexture.id);
                }
            }
        }


        /// <summary>
        /// Initial setup
        /// </summary>
        public override void Create()
        {
            // Load voxel insertion shader
            compute_FSR = Resources.Load("NKLI_FSR/FSR") as ComputeShader;
            if (compute_FSR == null) throw new Exception("[FSR] failed to load compute shader 'NKLI_FSR/FSR'");

            fsrSettings = settings;

            fsrPass = new FSRPass(RenderPassEvent.AfterRendering);
            beforePass = new BeforeEverythingPass(RenderPassEvent.BeforeRendering);

            textures_created = false;
        }


        /// <summary>
        /// Settings container
        /// </summary>
        [System.Serializable]
        public class FSRSettings
        {
            [Range(0.25f, 1)] public float render_scale = 0.75f;

            public enum upsample_modes
            {
                FSR,
                Bilinear
            }

            public upsample_modes upsample_mode;

            public bool sharpening;
            [Range(0, 2)] public float sharpness = 1;
        }


        /// <summary>
        /// Creates render textures
        /// </summary>
        /// <param name="cam"></param>
        private void CreateRenderTexture(Camera cam)
        {
            // Render target texture
            if (RT_FSR_RenderTarget != null) RT_FSR_RenderTarget.Release();
            int target_width = (int)(cam.scaledPixelWidth * fsrSettings.render_scale);
            int target_height = (int)(cam.scaledPixelHeight * fsrSettings.render_scale);
            RT_FSR_RenderTarget = new RenderTexture(target_width, target_height, 24, cam.allowHDR ? DefaultFormat.HDR : DefaultFormat.LDR);

            if (RT_Output != null) RT_Output.Release();
            RT_Output = new RenderTexture(cam.pixelWidth, cam.pixelHeight, 24, cam.allowHDR ? DefaultFormat.HDR : DefaultFormat.LDR);
#if UNITY_2019_2_OR_NEWER
            RT_Output.vrUsage = VRTextureUsage.DeviceSpecific;
#else
            if (UnityEngine.XR.XRSettings.isDeviceActive) RT_Output.vrUsage = VRTextureUsage.TwoEyes;
#endif
            RT_Output.enableRandomWrite = true;
            RT_Output.useMipMap = false;
            RT_Output.Create();
        }


        /// <summary>
        /// Sets up the render pass
        /// </summary>
        /// <param name="renderer"></param>
        /// <param name="renderingData"></param>
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (renderingData.cameraData.cameraType == CameraType.Game)
            {
                // If the render scale has changed we must recreate our textures
                if (fsrSettings.render_scale != render_scale_cached || !textures_created)
                {
                    textures_created = true;
                    render_scale_cached = fsrSettings.render_scale;
                    CreateRenderTexture(renderingData.cameraData.camera);
                }

                // Setup pass
                RenderTargetHandle dest = RenderTargetHandle.CameraTarget;
                fsrPass.Setup(RT_FSR_RenderTarget, dest);
                renderer.EnqueuePass(fsrPass);
                renderer.EnqueuePass(beforePass);
            }
        }
    }
//#endif
}