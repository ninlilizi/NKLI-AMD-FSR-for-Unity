// AMD FSR For Unity Standard render pipeline

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
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

namespace NKLI
{
    public class FSR_UniversalRenderPipeline : ScriptableRendererFeature
    {
        // Shader
        private static ComputeShader compute_FSR;

        // Render textures
        private static RenderTexture RT_FSR_RenderTarget;
        private static RenderTexture RT_Output;

        public class FSRPass : ScriptableRenderPass
        {
            public enum RenderTarget
            {
                Color,
                RenderTexture,
            }

            private RenderTargetIdentifier source { get; set; }
            private RenderTargetHandle destination;
            RenderTargetHandle m_TemporaryIntermediaryTexture;


            private void OnDisable()
            {
                // Dispose render target
                if (RT_FSR_RenderTarget != null) RT_FSR_RenderTarget.Release();
                if (RT_Output != null) RT_Output.Release();
            }


            // Update is called once per frame
            void LateUpdate()
            {
                //Graphics.SetRenderTarget(RT_FSR_RenderTarget.colorBuffer, RT_FSR_RenderTarget.depthBuffer);
            }


            public FSRPass(RenderPassEvent renderPassEvent, Material blitMaterial, int blitShaderPassIndex, string tag)
            {
                this.renderPassEvent = renderPassEvent;
                m_TemporaryIntermediaryTexture.Init("_TemporaryIntermediaryTexture");
            }


            public void Setup(RenderTargetIdentifier source, RenderTargetHandle destination)
            {
                this.source = source;
                this.destination = destination;
            }

            void Render(ScriptableRenderContext context, Camera camera)
            {
                /*context.SetupCameraProperties(camera);

                CommandBuffer cameraBuffer = new CommandBuffer()
                {
                    name = "FSR"
                };

                cameraBuffer.SetRenderTarget(
                    RT_FSR_RenderTarget,
                    RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store
                );

                cameraBuffer.SetRenderTarget(RT_FSR_RenderTarget.colorBuffer, RT_FSR_RenderTarget.depthBuffer);

                CameraClearFlags clearFlags = camera.clearFlags;

                context.ExecuteCommandBuffer(cameraBuffer);
                cameraBuffer.Clear();

                context.Submit();*/
            }

            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                CommandBuffer cmd = CommandBufferPool.Get();

                cmd.SetComputeIntParam(compute_FSR, "input_viewport_width", renderingData.cameraData.camera.scaledPixelWidth);
                cmd.SetComputeIntParam(compute_FSR, "input_viewport_height", renderingData.cameraData.camera.scaledPixelHeight);
                cmd.SetComputeIntParam(compute_FSR, "input_image_width", renderingData.cameraData.camera.pixelWidth);
                cmd.SetComputeIntParam(compute_FSR, "input_image_height", renderingData.cameraData.camera.pixelHeight);

                cmd.SetComputeIntParam(compute_FSR, "output_image_width", RT_Output.width);
                cmd.SetComputeIntParam(compute_FSR, "output_image_height", RT_Output.height);

                cmd.SetComputeIntParam(compute_FSR, "upsample_mode", (int)fsrSettings.upsample_mode);

                int dispatchX = (RT_Output.width + (16 - 1)) / 16;
                int dispatchY = (RT_Output.height + (16 - 1)) / 16;


                RenderTextureDescriptor destinationDesc = renderingData.cameraData.cameraTargetDescriptor;
                destinationDesc.colorFormat = renderingData.cameraData.camera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;
                destinationDesc.width = RT_Output.width;
                destinationDesc.height = RT_Output.height;
                destinationDesc.enableRandomWrite = true;
                destinationDesc.useMipMap = false;
                destinationDesc.depthBufferBits = 24;
                cmd.GetTemporaryRT(destination.id, destinationDesc);


                if (fsrSettings.sharpening && fsrSettings.upsample_mode == FSRSettings.upsample_modes.FSR)
                {
                    // Create intermediary render texture
                    RenderTextureDescriptor intermediaryDesc = renderingData.cameraData.cameraTargetDescriptor;
                    intermediaryDesc.colorFormat = renderingData.cameraData.camera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;
                    intermediaryDesc.width = RT_Output.width;
                    intermediaryDesc.height = RT_Output.height;
                    intermediaryDesc.enableRandomWrite = true;
                    intermediaryDesc.useMipMap = false;
                    intermediaryDesc.depthBufferBits = 24;
                    cmd.GetTemporaryRT(m_TemporaryIntermediaryTexture.id, intermediaryDesc);


                    // Upscale
                    cmd.SetComputeIntParams(compute_FSR, "upscale_or_sharpen", 1);
                    cmd.SetComputeTextureParam(compute_FSR, 0, "InputTexture", RT_FSR_RenderTarget);
                    cmd.SetComputeTextureParam(compute_FSR, 0, "OutputTexture", m_TemporaryIntermediaryTexture.id);
                    cmd.DispatchCompute(compute_FSR, 0, dispatchX, dispatchY, 1);

                    // Sharpen
                    cmd.SetComputeIntParams(compute_FSR, "upscale_or_sharpen", 1);
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
                Blit(cmd, RT_FSR_RenderTarget, fsrSettings.render_target);
                Blit(cmd, RT_Output, fsrSettings.render_output);

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


        // Start is called before the first frame update
        public override void Create()
        {
            // Load voxel insertion shader
            compute_FSR = Resources.Load("NKLI_FSR/FSR") as ComputeShader;
            if (compute_FSR == null) throw new Exception("[FSR] failed to load compute shader 'NKLI_FSR/FSR'");

            fsrSettings = settings;
            settings.render_target = RT_FSR_RenderTarget;
            settings.render_output = RT_Output;

            fsrPass = new FSRPass(RenderPassEvent.AfterRendering, null, -1, name);

            textures_created = false;
        }


        private float render_scale_cached;

        [System.Serializable]
        public class FSRSettings
        {
            public RenderTexture render_target;
            public RenderTexture render_output;

            [Range(0.125f, 1)] public float render_scale = 0.75f;

            public enum upsample_modes
            {
                FSR,
                Bilinear
            }

            public upsample_modes upsample_mode;

            public bool sharpening;
            [Range(0, 2)] public float sharpness = 1;

        }

        public FSRSettings settings = new FSRSettings();
        public static FSRSettings fsrSettings;

        FSRPass fsrPass;


        /// <summary>
        /// Creates render textures
        /// </summary>
        private void CreateRenderTexture(Camera cam)
        {

            if (RT_FSR_RenderTarget != null) RT_FSR_RenderTarget.Release();
            float target_width = cam.pixelWidth * fsrSettings.render_scale;
            float target_height = cam.pixelHeight * fsrSettings.render_scale;
            RT_FSR_RenderTarget = new RenderTexture((int)target_width, (int)target_height, 24, cam.allowHDR ? DefaultFormat.HDR : DefaultFormat.LDR);

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

        private bool textures_created;

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            // If the render scale has changed we must recreate our textures
            if (fsrSettings.render_scale != render_scale_cached || !textures_created)
            {
                textures_created = true;
                render_scale_cached = fsrSettings.render_scale;
                CreateRenderTexture(renderingData.cameraData.camera);
            }

            renderingData.cameraData.targetTexture = RT_FSR_RenderTarget;
            Graphics.SetRenderTarget(RT_FSR_RenderTarget.colorBuffer, RT_FSR_RenderTarget.depthBuffer);

            RenderTargetHandle dest = RenderTargetHandle.CameraTarget;

            fsrPass.Setup(RT_FSR_RenderTarget, dest);
            renderer.EnqueuePass(fsrPass);
        }
    }
}