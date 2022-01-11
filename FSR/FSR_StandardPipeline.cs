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
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;


namespace NKLI
{
    [ExecuteInEditMode]
    public class FSR_StandardPipeline : MonoBehaviour
    {
        // Cache local camera
        private Camera attached_camera;
        private Camera render_camera;
        private GameObject render_camera_gameObject;

        // Shaders
        public ComputeShader compute_FSR;
        public ComputeShader compute_BufferTransfer;
        public Shader shader_BlitDepth;

        // Materials
        private Material material_BlitDepth;

        // Render textures
        public RenderTexture RT_FSR_RenderTarget;
        public RenderTexture RT_FSR_RenderTarget_Raw;
        public RenderTexture RT_FSR_RenderTarget_Depth;
        public RenderTexture RT_FSR_RenderTarget_gGBuffer0;
        public RenderTexture RT_FSR_RenderTarget_gGBuffer1;
        public RenderTexture RT_FSR_RenderTarget_gGBuffer2;
        public RenderTexture RT_FSR_RenderTarget_gGBuffer3;
        public RenderTexture RT_FSR_RenderTarget_DepthNormals;
        public RenderTexture RT_FSR_RenderTarget_MotionVectors;
        public RenderTexture RT_Output;

        private CommandBuffer render_camera_buffer_copies;
        private CommandBuffer attached_camera_buffer_copies;

        // Cached camera flags
        private int cached_culling_mask;
        private CameraClearFlags cached_clear_flags;

        // Render scale
        [Range(0.25f, 1)] public float render_scale = 0.75f;
        private float render_scale_cached;

        // Copies render buffers from down-sampled to attached camera
        [Tooltip("Enable this to support post-effects dependant on the deferred and depth buffers")]
        public bool CopyRenderBuffers = true;
        private bool CopyRenderBuffers_Cached;

        private RenderingPath renderPath_Cached;

        private CameraEvent camEvent; // Camera event to attach to

        public enum upsample_modes
        {
            FSR,
            Bilinear
        }

        public upsample_modes upsample_mode;

        public bool sharpening;
        [Range(0, 2)] public float sharpness = 1;


        // Start is called before the first frame update
        private void OnEnable()
        {
            // Load voxel insertion shader
            compute_FSR = Resources.Load("NKLI_FSR/FSR") as ComputeShader;
            if (compute_FSR == null) throw new Exception("[FSR] failed to load compute shader 'NKLI_FSR/FSR'");

            compute_BufferTransfer = Resources.Load("NKLI_FSR_BufferTransfer") as ComputeShader;
            if (compute_BufferTransfer == null) throw new Exception("[FSR] failed to load compute shader 'NKLI_FSR_BufferTransfer'");

            shader_BlitDepth = Shader.Find("Hidden/NKLI_FSR_BlitDepth");
            if (shader_BlitDepth == null) throw new Exception("[FSR] failed to load shader 'Hidden/NKLI_FSR_BlitDepth'");

            material_BlitDepth = new Material(shader_BlitDepth);

            // Cache this
            attached_camera = GetComponent<Camera>();

            SetCameraEventTypes();

            // Remove command buffer if one is left over
            if ((new List<CommandBuffer>(attached_camera.GetCommandBuffers(camEvent))).Find(x => x.name == "FSR Buffer copy") != null)
            {
                attached_camera.RemoveCommandBuffer(camEvent, attached_camera_buffer_copies);
            }

            // Create render camera
            render_camera_gameObject = new GameObject("FSR_Render_Camera");
            render_camera_gameObject.transform.parent = transform;
            render_camera_gameObject.transform.localPosition = Vector3.zero;
            render_camera_gameObject.transform.localRotation = Quaternion.identity;
            render_camera_gameObject.hideFlags = HideFlags.DontSave;
            render_camera = render_camera_gameObject.AddComponent<Camera>();
            render_camera.gameObject.SetActive(true);

            // Add command buffer to render camera
            render_camera_buffer_copies = new CommandBuffer();
            render_camera_buffer_copies.name = "FSR Buffer copy";
            render_camera.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, render_camera_buffer_copies);

            // Add command buffer to attached camera.
            attached_camera_buffer_copies = new CommandBuffer();
            attached_camera_buffer_copies.name = "FSR Buffer copy";
            attached_camera.AddCommandBuffer(camEvent, attached_camera_buffer_copies);

            // Create textures
            CreateRenderTextures();
            Create_Command_Buffers();
        }


        // Chooses buffer injection point based on render path
        private void SetCameraEventTypes()
        {
            // Set camera event
            if (attached_camera.renderingPath != RenderingPath.Forward) camEvent = CameraEvent.AfterReflections;
            else camEvent = CameraEvent.AfterDepthNormalsTexture;
        }

        private void OnDisable()
        {
            // Destroy render camera
            DestroyImmediate(render_camera_gameObject);

            // Destroy render textures
            DestroyRenderTexture(ref RT_FSR_RenderTarget);
            DestroyRenderTexture(ref RT_FSR_RenderTarget_Raw);
            DestroyRenderTexture(ref RT_FSR_RenderTarget_Depth);
            DestroyRenderTexture(ref RT_FSR_RenderTarget_gGBuffer0);
            DestroyRenderTexture(ref RT_FSR_RenderTarget_gGBuffer1);
            DestroyRenderTexture(ref RT_FSR_RenderTarget_gGBuffer2);
            DestroyRenderTexture(ref RT_FSR_RenderTarget_gGBuffer3);
            DestroyRenderTexture(ref RT_FSR_RenderTarget_DepthNormals);
            DestroyRenderTexture(ref RT_FSR_RenderTarget_MotionVectors);
            DestroyRenderTexture(ref RT_Output);

            // Remove command buffers
            if (render_camera != null) render_camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, render_camera_buffer_copies);
            attached_camera.RemoveCommandBuffer(camEvent, attached_camera_buffer_copies);
        }


        /// <summary>
        /// Creates render textures
        /// </summary>
        private void CreateRenderTextures()
        {
            // Descriptor
            RenderTextureDescriptor rtDesc = new RenderTextureDescriptor
            {
                width = (int)(attached_camera.pixelWidth * render_scale),
                height = (int)(attached_camera.pixelHeight * render_scale),
                dimension = TextureDimension.Tex2D,
                enableRandomWrite = true,
                depthBufferBits = 24,
                volumeDepth = 1,
                msaaSamples = 1,
                useMipMap = false,
                sRGB = false
            };
            if (XRSettings.isDeviceActive) rtDesc.vrUsage = XRSettings.eyeTextureDesc.vrUsage;
            RenderTextureFormat format = attached_camera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;

            // Create RTs
            CreateRenderTexture(rtDesc, ref RT_FSR_RenderTarget, format);
            CreateRenderTexture(rtDesc, ref RT_FSR_RenderTarget_Raw, format);

            rtDesc.depthBufferBits = 0;
            rtDesc.width = attached_camera.pixelWidth;
            rtDesc.height = attached_camera.pixelHeight;
            CreateRenderTexture(rtDesc, ref RT_FSR_RenderTarget_Depth, RenderTextureFormat.RFloat);
            CreateRenderTexture(rtDesc, ref RT_FSR_RenderTarget_DepthNormals, RenderTextureFormat.ARGBHalf);
            CreateRenderTexture(rtDesc, ref RT_FSR_RenderTarget_MotionVectors, RenderTextureFormat.ARGBHalf);
            if (attached_camera.renderingPath != RenderingPath.Forward)
            {
                CreateRenderTexture(rtDesc, ref RT_FSR_RenderTarget_gGBuffer0, format);
                CreateRenderTexture(rtDesc, ref RT_FSR_RenderTarget_gGBuffer1, format);
                CreateRenderTexture(rtDesc, ref RT_FSR_RenderTarget_gGBuffer2, format);
                CreateRenderTexture(rtDesc, ref RT_FSR_RenderTarget_gGBuffer3, format);
            }

            //rtDesc.width = attached_camera.pixelWidth;
            //rtDesc.height = attached_camera.pixelHeight;
            //rtDesc.enableRandomWrite = true;

            CreateRenderTexture(rtDesc, ref RT_Output, format);
        }

        private void DestroyRenderTexture(ref RenderTexture tex)
        {
            if (tex != null)
            {
                tex.Release();
                tex = null;
            }
        }


        /// <summary>
        /// Creates a render texture, releasing first if required
        /// Creates a render texture, releasing first if required
        /// </summary>
        /// <param name="rtDesc"></param>
        /// <param name="rt"></param>
        private void CreateRenderTexture(RenderTextureDescriptor rtDesc, ref RenderTexture rt, RenderTextureFormat format)
        {
            rtDesc.colorFormat = format;

            if (rt != null) rt.Release();
            rt = new RenderTexture(rtDesc);
            rt.Create();
        }


        private void Create_Command_Buffers()
        {
            // Fill command buffers
            render_camera_buffer_copies.Clear();
            attached_camera_buffer_copies.Clear();

            // Render target
            render_camera_buffer_copies.Blit(RT_FSR_RenderTarget_Raw, RT_FSR_RenderTarget);

            if (CopyRenderBuffers)
            {
                /// Copy from render slave

                render_camera_buffer_copies.Blit(null, RT_FSR_RenderTarget_Depth, material_BlitDepth); // Breaks randomly when copied from the compute

                // Props
                render_camera_buffer_copies.SetComputeIntParam(compute_BufferTransfer, "flipImage", 1);
                render_camera_buffer_copies.SetComputeFloatParam(compute_BufferTransfer, "renderScale", render_scale);
                render_camera_buffer_copies.SetComputeIntParam(compute_BufferTransfer, "input_height", attached_camera.pixelHeight);

                // Set inputs and outputs
                if (attached_camera.renderingPath != RenderingPath.Forward)
                {
                    render_camera_buffer_copies.SetComputeIntParam(compute_BufferTransfer, "isDeferred", 1);

                    render_camera_buffer_copies.SetComputeTextureParam(compute_BufferTransfer, 0, "tex_Input1", BuiltinRenderTextureType.GBuffer0);
                    render_camera_buffer_copies.SetComputeTextureParam(compute_BufferTransfer, 0, "tex_Input2", BuiltinRenderTextureType.GBuffer1);
                    render_camera_buffer_copies.SetComputeTextureParam(compute_BufferTransfer, 0, "tex_Input3", BuiltinRenderTextureType.GBuffer2);

                    if (!render_camera.allowHDR)
                    {
                        render_camera_buffer_copies.SetComputeIntParam(compute_BufferTransfer, "copyGBuffer3", 1);
                        render_camera_buffer_copies.SetComputeTextureParam(compute_BufferTransfer, 0, "tex_Input4", BuiltinRenderTextureType.GBuffer3);
                    }
                    else render_camera_buffer_copies.SetComputeTextureParam(compute_BufferTransfer, 0, "tex_Input4", BuiltinRenderTextureType.None);

                    render_camera_buffer_copies.SetComputeTextureParam(compute_BufferTransfer, 0, "tex_Output1", RT_FSR_RenderTarget_gGBuffer0);
                    render_camera_buffer_copies.SetComputeTextureParam(compute_BufferTransfer, 0, "tex_Output2", RT_FSR_RenderTarget_gGBuffer1);
                    render_camera_buffer_copies.SetComputeTextureParam(compute_BufferTransfer, 0, "tex_Output3", RT_FSR_RenderTarget_gGBuffer2);
                    render_camera_buffer_copies.SetComputeTextureParam(compute_BufferTransfer, 0, "tex_Output4", RT_FSR_RenderTarget_gGBuffer3);
                }

                render_camera_buffer_copies.SetComputeTextureParam(compute_BufferTransfer, 0, "tex_Output6", RT_FSR_RenderTarget_DepthNormals);
                render_camera_buffer_copies.SetComputeTextureParam(compute_BufferTransfer, 0, "tex_Output7", RT_FSR_RenderTarget_MotionVectors);


                /// Assign to master

                if (attached_camera.renderingPath != RenderingPath.Forward)
                {
                    attached_camera_buffer_copies.SetGlobalTexture("_CameraGBufferTexture0", RT_FSR_RenderTarget_gGBuffer0);
                    attached_camera_buffer_copies.SetGlobalTexture("_CameraGBufferTexture1", RT_FSR_RenderTarget_gGBuffer1);
                    attached_camera_buffer_copies.SetGlobalTexture("_CameraGBufferTexture2", RT_FSR_RenderTarget_gGBuffer2);
                    if (!render_camera.allowHDR) attached_camera_buffer_copies.SetGlobalTexture("_CameraGBufferTexture3", RT_FSR_RenderTarget_gGBuffer3);
                }

                attached_camera_buffer_copies.SetGlobalTexture("_CameraDepthTexture", RT_FSR_RenderTarget_Depth);
                attached_camera_buffer_copies.SetGlobalTexture("_CameraDepthNormalsTexture", RT_FSR_RenderTarget_DepthNormals);
                attached_camera_buffer_copies.SetGlobalTexture("_CameraMotionVectorsTexture", RT_FSR_RenderTarget_MotionVectors);
            }

            // Dispatch copy from buffers
            render_camera_buffer_copies.DispatchCompute(compute_BufferTransfer, 0, attached_camera.pixelWidth / 8, attached_camera.pixelHeight / 8, 1);
        }


        private void Update()
        {
            // If the render scale has changed we must recreate our textures
            if (render_scale != render_scale_cached)
            {
                render_scale_cached = render_scale;
                CreateRenderTextures();
                Create_Command_Buffers();
            }

            // Recreate command buffers if the rendering path has changed
            if (attached_camera.renderingPath != renderPath_Cached || CopyRenderBuffers != CopyRenderBuffers_Cached)
            {
                SetCameraEventTypes();

                CopyRenderBuffers_Cached = CopyRenderBuffers;
                renderPath_Cached = attached_camera.renderingPath;
                Create_Command_Buffers();
            }
        }


        private void OnPreCull()
        {
            // Enable depth modes
            attached_camera.depthTextureMode = DepthTextureMode.Depth | DepthTextureMode.DepthNormals | DepthTextureMode.MotionVectors;

            // Clone camera properties
            render_camera.CopyFrom(attached_camera);
            render_camera.enabled = false;

            // Set render target
            render_camera.targetTexture = RT_FSR_RenderTarget_Raw;
            //render_camera.SetTargetBuffers(RT_FSR_RenderTarget_Raw.colorBuffer, RT_FSR_RenderTarget_Raw.depthBuffer);

            // Cache flags
            cached_culling_mask = attached_camera.cullingMask;
            cached_clear_flags = attached_camera.clearFlags;

            // Clear flags
            attached_camera.cullingMask = 0;
            attached_camera.clearFlags = CameraClearFlags.Nothing;
            //GL.Clear(true, true, Color.clear);

            // Render to buffers
            //render_camera.clearFlags = CameraClearFlags.SolidColor;
            render_camera.Render();

        }


        private void OnRenderImage(RenderTexture src, RenderTexture dest)
        {
            // Set parameters to shader
            compute_FSR.SetInt("input_viewport_width", RT_FSR_RenderTarget.width);
            compute_FSR.SetInt("input_viewport_height", RT_FSR_RenderTarget.height);
            compute_FSR.SetInt("input_image_width", RT_FSR_RenderTarget.width);
            compute_FSR.SetInt("input_image_height", RT_FSR_RenderTarget.height);

            compute_FSR.SetInt("output_image_width", RT_Output.width);
            compute_FSR.SetInt("output_image_height", RT_Output.height);

            compute_FSR.SetInt("upsample_mode", (int)upsample_mode);

            // Calculate thread counts
            int dispatchX = (RT_Output.width + (16 - 1)) / 16;
            int dispatchY = (RT_Output.height + (16 - 1)) / 16;

            if (sharpening && upsample_mode == upsample_modes.FSR)
            {
                // Create intermediary render texture
                RenderTextureDescriptor intermdiaryDesc = new RenderTextureDescriptor
                {
                    width = RT_Output.width,
                    height = RT_Output.height,
                    depthBufferBits = 24,
                    volumeDepth = 1,
                    msaaSamples = 1,
                    dimension = TextureDimension.Tex2D,
#if UNITY_2019_4_OR_NEWER
                    graphicsFormat = RT_Output.graphicsFormat,
#else
                    colorFormat = RT_Output.format,
#endif
                    enableRandomWrite = true,
                    useMipMap = false
                };
                if (XRSettings.isDeviceActive) intermdiaryDesc.vrUsage = XRSettings.eyeTextureDesc.vrUsage;
                RenderTexture intermediary = RenderTexture.GetTemporary(intermdiaryDesc);
                intermediary.Create();

                // Upscale
                compute_FSR.SetInt("upscale_or_sharpen", 1);
                compute_FSR.SetTexture(0, "InputTexture", RT_FSR_RenderTarget);
                compute_FSR.SetTexture(0, "OutputTexture", intermediary);
                compute_FSR.Dispatch(0, dispatchX, dispatchY, 1);

                // Sharpen
                compute_FSR.SetInt("upscale_or_sharpen", 0);
                compute_FSR.SetFloat("sharpness", 2 - sharpness);
                compute_FSR.SetTexture(0, "InputTexture", intermediary);
                compute_FSR.SetTexture(0, "OutputTexture", RT_Output);
                compute_FSR.Dispatch(0, dispatchX, dispatchY, 1);

                // Dispose
                intermediary.Release();
            }
            else
            {
                compute_FSR.SetInt("upscale_or_sharpen", 1);
                compute_FSR.SetTexture(0, "InputTexture", RT_FSR_RenderTarget);
                compute_FSR.SetTexture(0, "OutputTexture", RT_Output);
                compute_FSR.Dispatch(0, dispatchX, dispatchY, 1);
            }

            Graphics.Blit(RT_Output, dest);

            // Restore camera flags
            attached_camera.clearFlags = cached_clear_flags;
            attached_camera.cullingMask = cached_culling_mask;
        }
    }
}