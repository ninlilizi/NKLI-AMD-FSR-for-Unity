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
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;


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
        public RenderTexture RT_Intermediary;
        public RenderTexture RT_Output;

        private CommandBuffer render_camera_buffer_copies;
        private CommandBuffer attached_camera_buffer_copies;
        private CommandBuffer OnRenderImageBuffer;

        private readonly List<Action> stack_PreCull = new List<Action>();
        private readonly List<Action> stack_OnRenderImage = new List<Action>();


        // Cached camera flags
        [NonSerialized] private int cached_culling_mask;
        [NonSerialized] private CameraClearFlags cached_clear_flags;

        // Render scale
        [Range(0.25f, 1)] public float render_scale = 0.75f;

        // Copies render buffers from down-sampled to attached camera
        [Tooltip("Enable this to support post-effects dependant on the deferred and depth buffers")]
        public bool CopyRenderBuffers = true;

        // Main directional light
        public Light Light_Directional;
        [NonSerialized] private bool Light_Directional_Enabled_Cached;

        [NonSerialized] private Vector2Int renderDimensions;

        // Camera event to attach buffer copy buffer to.
        [NonSerialized] private CameraEvent camEvent;


        public enum upsample_modes
        {
            FSR,
            Bilinear
        }

        public upsample_modes upsample_mode;

        public bool sharpening;
        [Range(0, 2)] public float sharpness = 1;

        // Startup race-condition check
        [NonSerialized] private bool initlised = false;


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


            // Create render camera
            render_camera_gameObject = new GameObject("FSR_Render_Camera");
            render_camera_gameObject.transform.parent = transform;
            render_camera_gameObject.transform.localPosition = Vector3.zero;
            render_camera_gameObject.transform.localRotation = Quaternion.identity;
            render_camera_gameObject.hideFlags = HideFlags.HideAndDontSave;
            render_camera = render_camera_gameObject.AddComponent<Camera>();
            render_camera.gameObject.SetActive(true);

            // Add command buffer to render camera
            render_camera_buffer_copies = new CommandBuffer();
            render_camera_buffer_copies.name = "FSR Buffer transfer";

            // Add command buffer to attached camera.
            attached_camera_buffer_copies = new CommandBuffer();
            attached_camera_buffer_copies.name = "FSR Buffer transfer";

            OnRenderImageBuffer = new CommandBuffer();
            OnRenderImageBuffer.name = "OnRenderImage()";

            // race condition sanity flag
            initlised = true;

            // Create resources
            OnValidate();
        }


        // Chooses buffer injection point based on render path
        private void SetCameraEventTypes()
        {
            // Set camera event
            if (attached_camera.renderingPath != RenderingPath.Forward) camEvent = CameraEvent.AfterReflections;
            else camEvent = CameraEvent.AfterDepthNormalsTexture;
        }


        /// <summary>
        /// Removes commend buffers from camera events
        /// </summary>
        private void RemoveCameraEvents()
        {
            RemoveCameraEvent(render_camera, CameraEvent.BeforeImageEffectsOpaque, render_camera_buffer_copies);

            RemoveCameraEvent(attached_camera, CameraEvent.AfterDepthNormalsTexture, attached_camera_buffer_copies);
            RemoveCameraEvent(attached_camera, CameraEvent.AfterReflections, attached_camera_buffer_copies);
        }


        /// <summary>
        /// Removes command buffer from a specific camera event
        /// </summary>
        /// <param name="cam"></param>
        /// <param name="camEvent"></param>
        /// <param name="buffer"></param>
        private void RemoveCameraEvent(Camera cam, CameraEvent camEvent, CommandBuffer buffer)
        {
            if (cam != null)
            {
                if ((new List<CommandBuffer>(cam.GetCommandBuffers(camEvent))).Find(x => x.name == "FSR Buffer transfer") != null)
                {
                    cam.RemoveCommandBuffer(camEvent, buffer);
                }
            }
        }


        private void OnDisable()
        {
            // Destroy render camera
            DestroyImmediate(render_camera_gameObject);

            // Destroy render textures
            DestroyAllRenderTextures();

            // Remove command buffer
            RemoveCameraEvents();

            initlised = false;
        }


        /// <summary>
        /// Creates render textures
        /// </summary>
        private void CreateRenderTextures()
        {
            // Destroy render textures
            DestroyAllRenderTextures();


            RenderTextureFormat format = attached_camera.allowHDR ? RenderTextureFormat.DefaultHDR : RenderTextureFormat.Default;

            // Descriptor
            RenderTextureDescriptor rtDesc = new RenderTextureDescriptor((int)(attached_camera.pixelWidth * render_scale), (int)(attached_camera.pixelHeight * render_scale), format, 32, 1)
            {
                dimension = TextureDimension.Tex2D,
                enableRandomWrite = true,
                useMipMap = false,
                sRGB = false
            };

            // Create RTs
            CreateRenderTexture(rtDesc, ref RT_FSR_RenderTarget, format);
            CreateRenderTexture(rtDesc, ref RT_FSR_RenderTarget_Raw, format);

            rtDesc.depthBufferBits = 0;
            rtDesc.width = attached_camera.pixelWidth;
            rtDesc.height = attached_camera.pixelHeight;
            if (CopyRenderBuffers)
            {

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
            }

            if (sharpening && upsample_mode == upsample_modes.FSR) CreateRenderTexture(rtDesc, ref RT_Intermediary, format);
            CreateRenderTexture(rtDesc, ref RT_Output, format);
        }


        /// <summary>
        /// Destroys all RenderTextures
        /// </summary>
        private void DestroyAllRenderTextures()
        {
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
            DestroyRenderTexture(ref RT_Intermediary);
            DestroyRenderTexture(ref RT_Output);
        }


        /// <summary>
        /// Destroys a RenderTexture
        /// </summary>
        /// <param name="tex"></param>
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
                else
                    render_camera_buffer_copies.SetComputeIntParam(compute_BufferTransfer, "isDeferred", 0);

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


            // Build OnRenderImage buffer
            RebuildImageEffectBuffer();
        }


        /// <summary>
        /// Builds post effect buffer executed in OnRenderImage()
        /// </summary>
        private void RebuildImageEffectBuffer()
        {
            OnRenderImageBuffer.Clear();

            // Set parameters to shader
            OnRenderImageBuffer.SetComputeIntParam(compute_FSR, "input_viewport_width", RT_FSR_RenderTarget.width);
            OnRenderImageBuffer.SetComputeIntParam(compute_FSR, "input_viewport_height", RT_FSR_RenderTarget.height);
            OnRenderImageBuffer.SetComputeIntParam(compute_FSR, "input_image_width", RT_FSR_RenderTarget.width);
            OnRenderImageBuffer.SetComputeIntParam(compute_FSR, "input_image_height", RT_FSR_RenderTarget.height);

            OnRenderImageBuffer.SetComputeIntParam(compute_FSR, "output_image_width", RT_Output.width);
            OnRenderImageBuffer.SetComputeIntParam(compute_FSR, "output_image_height", RT_Output.height);

            OnRenderImageBuffer.SetComputeIntParam(compute_FSR, "upsample_mode", (int)upsample_mode);

            // Calculate thread counts
            int dispatchX = (RT_Output.width + (16 - 1)) / 16;
            int dispatchY = (RT_Output.height + (16 - 1)) / 16;

            if (sharpening && upsample_mode == upsample_modes.FSR)
            {
                // Upscale
                OnRenderImageBuffer.SetComputeIntParams(compute_FSR, "upscale_or_sharpen", 1);
                OnRenderImageBuffer.SetComputeTextureParam(compute_FSR, 0, "InputTexture", RT_FSR_RenderTarget);
                OnRenderImageBuffer.SetComputeTextureParam(compute_FSR, 0, "OutputTexture", RT_Intermediary);
                OnRenderImageBuffer.DispatchCompute(compute_FSR, 0, dispatchX, dispatchY, 1);

                // Sharpen
                OnRenderImageBuffer.SetComputeIntParams(compute_FSR, "upscale_or_sharpen", 0);
                OnRenderImageBuffer.SetComputeFloatParam(compute_FSR, "sharpness", 2 - sharpness);
                OnRenderImageBuffer.SetComputeTextureParam(compute_FSR, 0, "InputTexture", RT_Intermediary);
                OnRenderImageBuffer.SetComputeTextureParam(compute_FSR, 0, "OutputTexture", RT_Output);
                OnRenderImageBuffer.DispatchCompute(compute_FSR, 0, dispatchX, dispatchY, 1);
            }
            else
            {
                OnRenderImageBuffer.SetComputeIntParams(compute_FSR, "upscale_or_sharpen", 1);
                OnRenderImageBuffer.SetComputeTextureParam(compute_FSR, 0, "InputTexture", RT_FSR_RenderTarget);
                OnRenderImageBuffer.SetComputeTextureParam(compute_FSR, 0, "OutputTexture", RT_Output);
                OnRenderImageBuffer.DispatchCompute(compute_FSR, 0, dispatchX, dispatchY, 1);
            }
        }


        /// <summary>
        /// Rebuild all proxy execution queues
        /// </summary>
        private void RebuildExecutionQueues()
        {
            RebuildQueueOnPreCull();
            RebuildQueueOnRenderImage();
        }


        /// <summary>
        /// Build proxy OnPreCull queue
        /// </summary>
        private void RebuildQueueOnPreCull()
        {
            stack_PreCull.Clear();

            EnqueueStack(stack_PreCull, () =>
            {
                // Clone camera properties
                render_camera.CopyFrom(attached_camera);
                render_camera.enabled = false;

                // Set render target
                render_camera.targetTexture = RT_FSR_RenderTarget_Raw;

                // Cache flags
                cached_culling_mask = attached_camera.cullingMask;
                cached_clear_flags = attached_camera.clearFlags;

                // Clear flags
                attached_camera.cullingMask = 0;
                attached_camera.clearFlags = CameraClearFlags.Nothing;

                // Render to buffers
                render_camera.Render();
            });

            // Disable directional light
            if (Light_Directional != null)
            {
                EnqueueStack(stack_PreCull, () =>
                {
                    Light_Directional_Enabled_Cached = Light_Directional.enabled;
                    Light_Directional.enabled = false;
                });
            }
        }


        /// <summary>
        /// Build proxy OnRenderImage queue
        /// </summary>
        private void RebuildQueueOnRenderImage()
        {
            // Clear queue
            stack_OnRenderImage.Clear();

            // Reset directional light state
            if (Light_Directional != null)
            {
                EnqueueStack(stack_OnRenderImage, () => { Light_Directional.enabled = Light_Directional_Enabled_Cached; });
            }

            EnqueueStack(stack_OnRenderImage, () =>
            {
                // Execute command buffer
                Graphics.ExecuteCommandBuffer(OnRenderImageBuffer);

                // Restore camera flags
                attached_camera.clearFlags = cached_clear_flags;
                attached_camera.cullingMask = cached_culling_mask;
            });
        }


        /// <summary>
        /// Executes every frame
        /// </summary>
        private void Update()
        {
            // Detect viewport size changes
            if (renderDimensions.x != attached_camera.pixelWidth || renderDimensions.y != attached_camera.pixelHeight)
            {
                renderDimensions.x = attached_camera.pixelWidth;
                renderDimensions.y = attached_camera.pixelHeight;

                OnValidate();
            }
        }


        /// <summary>
        /// Recreates buffers, etc on Validation
        /// </summary>
        private void OnValidate()
        {
            // Race-condition sanity check
            if (!initlised) return;

            // Setup camera events
            RemoveCameraEvents();
            SetCameraEventTypes();
            attached_camera.AddCommandBuffer(camEvent, attached_camera_buffer_copies);
            render_camera.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, render_camera_buffer_copies);


            CreateRenderTextures();
            Create_Command_Buffers();
            RebuildExecutionQueues();

            // If we don't have a directional light assigned, attempt to find one
            if (Light_Directional == null)
            {
                Light[] lights = FindObjectsOfType<Light>();

                // Using a For versus Foreach, due to generation of less garbage
                for (int i = 0; i < lights.Length; ++i)
                {
                    // If we find a directional light, assign and exit the loop
                    if (lights[i].type == LightType.Directional)
                    {
                        Light_Directional = lights[i];
                        break;
                    }
                }
            }
        }


        /// <summary>
        /// Executes immediately before culling
        /// </summary>
        private void OnPreCull()
        {
            // Execute proxy queue
            ExecuteStack(stack_PreCull);
        }


        /// <summary>
        /// Applies the effect
        /// </summary>
        /// <param name="src"></param>
        /// <param name="dest"></param>
        private void OnRenderImage(RenderTexture src, RenderTexture dest)
        {
            // Execute proxy queue
            ExecuteStack(stack_OnRenderImage);

            // Copy output to destination
            Graphics.Blit(RT_Output, dest);
        }


        #region ExecutionStack
        /// <summary>
        /// Execution stack for batching functions, used to avoid conditionals in the render loop
        /// </summary>
        [NonSerialized] private int executionStackPosition = 0;

        private void ExecuteStack(List<Action> executionStack)
        {
            for (executionStackPosition = 0; executionStackPosition < executionStack.Count; ++executionStackPosition)
            {
                try
                {
                    executionStack[executionStackPosition].Invoke();
                }
                catch (Exception ex)
                {
                    Debug.LogError(ex);
                }
            }
        }


        /// <summary>
        /// Locks the queue and adds the IENumerator to the queue
        /// </summary>
        /// <param name="executionStack"></param>
        /// <param name="action"></param>
        private void EnqueueStack(List<Action> executionStack, Action action)
        {
            executionStack.Add(action);
        }
        #endregion
    }
}