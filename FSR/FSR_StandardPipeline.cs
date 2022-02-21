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

// Add 'NKLI_DEBUG' to your platform defines to enable additional error logging.

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
        public Camera render_camera;
        public GameObject render_camera_gameObject;

        // Shaders
        public ComputeShader compute_FSR;
        public ComputeShader compute_BufferTransfer;
        public ComputeShader compute_BufferTonemap;
        public Shader shader_BlitDepth;
        public Shader shader_ReverseTonemap;

        // Materials
        private Material material_BlitDepth;
        private Material material_ReverseTonemap;

        // Render textures
        private RenderTexture RT_FSR_Dummy;
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
        private CommandBuffer attached_camera_buffer_copies_gBufffers;
        private CommandBuffer attached_camera_buffer_copies_gBufffer3;
        private CommandBuffer attached_camera_buffer_copies_Depth;
        private CommandBuffer OnRenderImageBuffer;

        private readonly List<Action> stack_PreCull = new List<Action>();
        private readonly List<Action> stack_OnRenderImage = new List<Action>();

        private int blueNoiseFrameSwitch;
        private Texture2D[] blueNoise;


        // Cached camera flags
        [NonSerialized] private int cached_culling_mask;
        [NonSerialized] private CameraClearFlags cached_clear_flags;

        // Render scale
        [Range(0.5f, 1)] public float render_scale = 0.75f;

        // Copies render buffers from down-sampled to attached camera
        [Tooltip("Enable this to support post-effects dependant on the deferred and depth buffers")]
        public bool CopyRenderBuffers = false;

        [SerializeField] public enum TransferMode
        {
            Assign,
            Copy
        }

        // Flags to instead use an expensive final blit pass instead of global assignment
        [SerializeField] public TransferMode transferMode_gBuffer0;
        [SerializeField] public TransferMode transferMode_gBuffer1;
        [SerializeField] public TransferMode transferMode_gBuffer2;
        [SerializeField] public TransferMode transferMode_gBuffer3;
        [SerializeField] public TransferMode transferMode_Depth;
        [SerializeField] public TransferMode transferMode_DepthNormals;
        [SerializeField] public TransferMode transferMode_MotionVectors;

        // Camera Event CommandBuffer handling
        [Tooltip("Controls event buffer order to correct behaviour of other effects that hook early into the pipeline")]
        [SerializeField] public bool ReorderBufferEvents = false;
        [Tooltip("Controls effect buffer order to correct behaviour of other effects that hook early into the pipeline")]
        [SerializeField] public bool ReorderEffectEvents = false;

        [NonSerialized] private CommandBuffer[] attachedCamera_CommandBuffer_Depth;
        [NonSerialized] private CommandBuffer[] attachedCamera_CommandBuffer_Effect;


        // Main directional light
        public Light Light_Directional;
        [NonSerialized] private bool Light_Directional_Enabled_Cached;

        [NonSerialized] private Vector2Int renderDimensions;
        [NonSerialized] private int cachedDepthTextureMode;

        // Camera event to attach buffer copy buffer to.
        [NonSerialized] private CameraEvent camEvent_Depth;
        [NonSerialized] private CameraEvent camEvent_gBuffers;
        [NonSerialized] private CameraEvent camEvent_gBuffer3;


        public enum upsample_modes
        {
            FSR,
            Bilinear
        }

        public upsample_modes upsample_mode;

        public bool sharpening;
        [Range(0, 2)] public float sharpness = 1.8f;

        // Startup race-condition check
        [NonSerialized] private bool initlised = false;


        // Start is called before the first frame update
        private void OnEnable()
        {
#if UNITY_EDITOR
            // Sanity check - Warn if correct component ordering is not detected
            SanityCheckComponentOrder();
            SanityCheckIncompatibleEffects();
#endif

            // Load voxel insertion shader
            compute_FSR = Resources.Load("NKLI_FSR/FSR") as ComputeShader;
            if (compute_FSR == null) throw new Exception("[NDR] failed to load compute shader 'NKLI_FSR/FSR'");

            compute_BufferTransfer = Resources.Load("NKLI_FSR_BufferTransfer") as ComputeShader;
            if (compute_BufferTransfer == null) throw new Exception("[NDR] failed to load compute shader 'NKLI_FSR_BufferTransfer'");

            compute_BufferTonemap = Resources.Load("NKLI_FSR_BufferTonemap") as ComputeShader;
            if (compute_BufferTransfer == null) throw new Exception("[NDR] failed to load compute shader 'NKLI_FSR_BufferTonemap'");

            shader_BlitDepth = Shader.Find("Hidden/NKLI_FSR_BlitDepth");
            if (shader_BlitDepth == null) throw new Exception("[NDR] failed to load shader 'Hidden/NKLI_FSR_BlitDepth'");

            shader_ReverseTonemap = Shader.Find("Hidden/NKLI_FSR_ReverseTonemap");
            if (shader_ReverseTonemap == null) throw new Exception("[NDR] failed to load shader 'Hidden/NKLI_FSR_ReverseTonemap'");

            material_BlitDepth = new Material(shader_BlitDepth);
            material_ReverseTonemap = new Material(shader_ReverseTonemap);

            // Cache this
            attached_camera = GetComponent<Camera>();


            // If we don't have a child assigned
            if (render_camera == null)
            {
                // First attempt to find a disconnected child
                Camera[] cameras = GetComponentsInChildren<Camera>();
                foreach (Camera cam in cameras)
                {
                    // If we find a child, we assign it
                    if (cam.name == "NDR_Render_Child")
                    {
                        render_camera_gameObject = cam.gameObject;
                        render_camera = cam;
                        break;
                    }
                }

                // If the camera is not found, then create a new one.
                if (render_camera == null)
                {
                    render_camera_gameObject = new GameObject("NDR_Render_Child");
                    render_camera_gameObject.transform.parent = transform;
                    render_camera_gameObject.transform.localPosition = Vector3.zero;
                    render_camera_gameObject.transform.localRotation = Quaternion.identity;
                    render_camera = render_camera_gameObject.AddComponent<Camera>();
                    render_camera.gameObject.SetActive(true);
                }
            }

            // Mux depth texture modes of both cameras
            attached_camera.depthTextureMode |= render_camera.depthTextureMode;
            cachedDepthTextureMode = (int)attached_camera.depthTextureMode;

            // Add command buffer to render camera
            render_camera_buffer_copies = new CommandBuffer();
            render_camera_buffer_copies.name = "[NDR] Buffer transfer";

            // Create command-buffers
            attached_camera_buffer_copies_gBufffers = new CommandBuffer();
            attached_camera_buffer_copies_gBufffers.name = "[NDR] G-Buffers transfer";

            attached_camera_buffer_copies_gBufffer3 = new CommandBuffer();
            attached_camera_buffer_copies_gBufffer3.name = "[NDR] G-Buffer3 transfer";

            attached_camera_buffer_copies_Depth = new CommandBuffer();
            attached_camera_buffer_copies_Depth.name = "[NDR] D-Buffer transfer";

            OnRenderImageBuffer = new CommandBuffer();
            OnRenderImageBuffer.name = "[NDR] Perform upscaling";


            //Get blue noise textures
            blueNoise = new Texture2D[64];
            for (int i = 0; i < 64; i++)
            {
                string fileName = "HDR_RGBA_" + i.ToString();
                Texture2D blueNoiseTexture = Resources.Load("Textures/Blue Noise/64_64/" + fileName) as Texture2D;

                if (blueNoiseTexture == null)
                {
                    Debug.LogWarning("Unable to find noise texture");
                }

                blueNoise[i] = blueNoiseTexture;

            }



            // race condition sanity flag
            initlised = true;

            // Create resources
            validationDelayed = false;
            OnValidate();
        }


#region SanitChecks
#if UNITY_EDITOR
        /// <summary>
        /// Attempts to detect component order and warn if correct order is not detected
        /// </summary>
        private void SanityCheckComponentOrder()
        {
            Component[] goComponents = gameObject.GetComponents(typeof(Component));

            string thisComponent = ToString();
            string[] componentAnticedenceWhitelist = new string[3];
            componentAnticedenceWhitelist[0] = thisComponent;
            componentAnticedenceWhitelist[1] = "UnityEngine.AudioListener";
            componentAnticedenceWhitelist[2] = "FirstPersonFlyingController";


            bool containsCamera = false;
            bool correctComponentOrder = false;
            foreach (Component comp in goComponents)
            {
                // First we find the Camera
                if (!containsCamera)
                {
                    if (comp.ToString().Contains("UnityEngine.Camera"))
                        containsCamera = true;
                }
                else
                {
                    // Next we search for a white-listed component
                    bool whitelistedComponentFound = false;
                    for (int i = 0; i < componentAnticedenceWhitelist.Length; ++i)
                    {
                        if (comp.ToString().Contains(componentAnticedenceWhitelist[i]))
                        {
                            whitelistedComponentFound = true;
                        }
                    }

                    if (!whitelistedComponentFound)
                    {
                        break;
                    }
                    else if (comp.ToString().Contains(thisComponent))
                    {
                        correctComponentOrder = true;
                        break;
                    }
                }
            }

            if (!correctComponentOrder)
            {
                Debug.Log("[NDR] <WARNING> NDR should be first component after Camera. Current component order may result in incorrect behaviour!");
            }
        }


        /// <summary>
        /// Checks for known incompatible effects attached to the camera
        /// </summary>
        private void SanityCheckIncompatibleEffects()
        {
            Component[] goComponents = gameObject.GetComponents(typeof(Component));

            string[] componentBlacklist = new string[3];
            componentBlacklist[0] = "UnityEngine.Rendering.PostProcessing.PostProcessLayer";
            componentBlacklist[1] = "UnityEngine.Rendering.PostProcessing.PostProcessVolume";
            componentBlacklist[2] = "UnityEngine.Rendering.PostProcessing.PostProcessDebug";

            // Search components
            bool incompatibleEffectFound = false;
            foreach (Component comp in goComponents)
            {
                // For each component we compare it against the black-list
                for (int i = 0; i < componentBlacklist.Length; ++i)
                {
                    if (comp.ToString().Contains(componentBlacklist[i]))
                    {
                        Debug.Log("[NDR] <WARNING> Effect '" + componentBlacklist[i] + "' is incompatible with post-scaling pass-through." + Environment.NewLine + "Please move this to the 'FSR_Render_Child' child object, so that it may be run before scaling");
                        incompatibleEffectFound = true;
                    }
                }
            }

            // Log additional, explanatory error. To ensure this receives the developers attention.
            if (incompatibleEffectFound)
            {
                Debug.LogError("[NDR] Effects known to be incompatible with post-scaling pass-through have been detected!" + Environment.NewLine + "Incompatible effects must run before scaling, or unexpected behaviour will occur. Please move these to the 'FSR_Render_Child' child object, so they may run before scaling.");
            }
        }
#endif
#endregion


        // Chooses buffer injection point based on render path
        private void SetCameraEventTypes()
        {
            // Set camera event
            if (attached_camera.renderingPath != RenderingPath.Forward)
            {
                camEvent_Depth = CameraEvent.BeforeReflections;
                camEvent_gBuffers = camEvent_Depth;
                camEvent_gBuffer3 = attached_camera.allowHDR ? CameraEvent.AfterReflections : CameraEvent.BeforeLighting;
            }
            else
            {
                camEvent_Depth = CameraEvent.AfterDepthNormalsTexture;
                camEvent_gBuffers = camEvent_Depth;
                camEvent_gBuffer3 = CameraEvent.AfterDepthNormalsTexture;
            }
        }


        /// <summary>
        /// Removes commend buffers from camera events
        /// </summary>
        private void RemoveCameraEvents()
        {
            // Render
            RemoveCameraEvent(render_camera, CameraEvent.AfterEverything, render_camera_buffer_copies);

            // Depth
            RemoveCameraEvent(attached_camera, CameraEvent.BeforeReflections, attached_camera_buffer_copies_Depth);
            RemoveCameraEvent(attached_camera, CameraEvent.AfterDepthNormalsTexture, attached_camera_buffer_copies_Depth);

            // gBuffers
            RemoveCameraEvent(attached_camera, CameraEvent.BeforeReflections, attached_camera_buffer_copies_gBufffers);
            RemoveCameraEvent(attached_camera, CameraEvent.AfterReflections, attached_camera_buffer_copies_gBufffer3);
            RemoveCameraEvent(attached_camera, CameraEvent.BeforeLighting, attached_camera_buffer_copies_gBufffer3);

            // Effect
            RemoveCameraEvent(attached_camera, CameraEvent.BeforeImageEffectsOpaque, OnRenderImageBuffer);
        }


        /// <summary>
        /// Removes command buffer from a specific camera event
        /// </summary>
        /// <param name="cam"></param>
        /// <param name="camEvent"></param>
        /// <param name="buffer"></param>
        private void RemoveCameraEvent(Camera cam, CameraEvent camEvent, CommandBuffer buffer)
        {
            if (cam != null && buffer != null)
            {
                RemoveCameraEventJMP:
                if ((new List<CommandBuffer>(cam.GetCommandBuffers(camEvent))).Find(x => x.name == buffer.name) != null)
                {
                    cam.RemoveCommandBuffer(camEvent, buffer);
                    goto RemoveCameraEventJMP;
                }
            }
        }


        private void OnDisable()
        {
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

            RenderTextureDescriptor rtDescDummy = rtDesc;
            rtDescDummy.width = 1;
            rtDescDummy.height = 1;

            // Create RTs
            CreateRenderTexture(rtDesc, ref RT_FSR_RenderTarget, RenderTextureFormat.ARGB2101010);
            CreateRenderTexture(rtDesc, ref RT_FSR_RenderTarget_Raw, format);

            rtDesc.depthBufferBits = 0;
            rtDesc.width = attached_camera.pixelWidth;
            rtDesc.height = attached_camera.pixelHeight;
            if (CopyRenderBuffers)
            {
                // Depth
                if (((int)render_camera.depthTextureMode & 1) == 1)
                    CreateRenderTexture(rtDesc, ref RT_FSR_RenderTarget_Depth, RenderTextureFormat.RFloat);

                // DepthNormals
                if ((((int)render_camera.depthTextureMode >> 1) & 1) == 1)
                    CreateRenderTexture(rtDesc, ref RT_FSR_RenderTarget_DepthNormals, RenderTextureFormat.ARGBHalf);
                else
                    CreateRenderTexture(rtDescDummy, ref RT_FSR_RenderTarget_DepthNormals, RenderTextureFormat.ARGBHalf);

                // MotionVectors
                if ((((int)render_camera.depthTextureMode >> 2) & 1) == 1)
                    CreateRenderTexture(rtDesc, ref RT_FSR_RenderTarget_MotionVectors, RenderTextureFormat.ARGBHalf);
                else
                    CreateRenderTexture(rtDescDummy, ref RT_FSR_RenderTarget_MotionVectors, RenderTextureFormat.ARGBHalf);

                if (attached_camera.renderingPath != RenderingPath.Forward)
                {
                    CreateRenderTexture(rtDesc, ref RT_FSR_RenderTarget_gGBuffer0, RenderTextureFormat.ARGBHalf);
                    CreateRenderTexture(rtDesc, ref RT_FSR_RenderTarget_gGBuffer1, RenderTextureFormat.ARGB32);
                    CreateRenderTexture(rtDesc, ref RT_FSR_RenderTarget_gGBuffer2, RenderTextureFormat.ARGB2101010);
                    if (!render_camera.allowHDR) CreateRenderTexture(rtDescDummy, ref RT_FSR_RenderTarget_gGBuffer3, RenderTextureFormat.ARGB2101010);
                }
            }

            if (sharpening && upsample_mode == upsample_modes.FSR) CreateRenderTexture(rtDesc, ref RT_Intermediary, format);
            CreateRenderTexture(rtDesc, ref RT_Output, format);

            CreateRenderTexture(rtDescDummy, ref RT_FSR_Dummy, RenderTextureFormat.ARGB32);
        }


        /// <summary>
        /// Destroys all RenderTextures
        /// </summary>
        private void DestroyAllRenderTextures()
        {
            // Destroy render textures
            DestroyRenderTexture(ref RT_FSR_Dummy);
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
            attached_camera_buffer_copies_gBufffers.Clear();
            attached_camera_buffer_copies_gBufffer3.Clear();
            attached_camera_buffer_copies_Depth.Clear();

            // Dispatch sizes
            int[] dispatchXY0 = { RT_FSR_RenderTarget_Raw.width / 8, RT_FSR_RenderTarget_Raw.height / 8 };
            int[] dispatchXY1 = { attached_camera.pixelWidth / 8, attached_camera.pixelHeight / 8 };

            // Apply tone-mapping and convert to Gamma 2.0
            render_camera_buffer_copies.SetComputeTextureParam(compute_BufferTonemap, 0, "tex_Input", RT_FSR_RenderTarget_Raw);
            render_camera_buffer_copies.SetComputeTextureParam(compute_BufferTonemap, 0, "tex_Output", RT_FSR_RenderTarget);
            render_camera_buffer_copies.SetComputeIntParams(compute_BufferTonemap, "dispatchXY", dispatchXY0);
            render_camera_buffer_copies.DispatchCompute(compute_BufferTonemap, 0, dispatchXY0[0], dispatchXY0[1], 1);

            if (CopyRenderBuffers)
            {
                /// Copy from render slave

                // Depth
                if (((int)render_camera.depthTextureMode & 1) == 1)
                {
                    render_camera_buffer_copies.Blit(null, RT_FSR_RenderTarget_Depth, material_BlitDepth); // Breaks randomly when copied from the compute
                }

                // Props

                //render_camera_buffer_copies.SetComputeIntParam(compute_BufferTransfer, "flipImage", 0);
                render_camera_buffer_copies.SetComputeFloatParam(compute_BufferTransfer, "renderScale", render_scale);
                render_camera_buffer_copies.SetComputeIntParam(compute_BufferTransfer, "output_height", attached_camera.pixelHeight);
                render_camera_buffer_copies.SetComputeIntParams(compute_BufferTransfer, "dispatchXY", dispatchXY1);

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
                    render_camera_buffer_copies.SetComputeTextureParam(compute_BufferTransfer, 0, "tex_Output4", RT_FSR_Dummy);
                }
                else
                    render_camera_buffer_copies.SetComputeIntParam(compute_BufferTransfer, "isDeferred", 0);

                // DepthNormals
                render_camera_buffer_copies.SetComputeTextureParam(compute_BufferTransfer, 0, "tex_Output6", RT_FSR_RenderTarget_DepthNormals);
                if ((((int)render_camera.depthTextureMode >> 1) & 1) == 1)
                {
                    render_camera_buffer_copies.SetComputeIntParams(compute_BufferTransfer, "depth_depthNormals", 1);
                }
                else
                {
                    render_camera_buffer_copies.SetComputeIntParams(compute_BufferTransfer, "depth_depthNormals", 0);
                    render_camera_buffer_copies.SetComputeTextureParam(compute_BufferTransfer, 0, "_CameraDepthNormalsTexture", RT_FSR_Dummy);
                }

                // MotionVectors
                render_camera_buffer_copies.SetComputeTextureParam(compute_BufferTransfer, 0, "tex_Output7", RT_FSR_RenderTarget_MotionVectors);
                if ((((int)render_camera.depthTextureMode >> 2) & 1) == 1)
                {
                    render_camera_buffer_copies.SetComputeIntParams(compute_BufferTransfer, "depth_motionVectors", 1);
                }
                else
                {
                    render_camera_buffer_copies.SetComputeIntParams(compute_BufferTransfer, "depth_motionVectors", 0);
                    render_camera_buffer_copies.SetComputeTextureParam(compute_BufferTransfer, 0, "_CameraMotionVectorsTexture", RT_FSR_Dummy);
                }

                // Dispatch copy from buffers
                render_camera_buffer_copies.DispatchCompute(compute_BufferTransfer, 0, dispatchXY1[0], dispatchXY1[1], 1);


                /// Assign to master

                if (attached_camera.renderingPath != RenderingPath.Forward)
                {
                    // 0
                    if (transferMode_gBuffer0 == TransferMode.Assign) attached_camera_buffer_copies_gBufffers.SetGlobalTexture("_CameraGBufferTexture0", RT_FSR_RenderTarget_gGBuffer0);
                    else attached_camera_buffer_copies_gBufffers.Blit(RT_FSR_RenderTarget_gGBuffer0, BuiltinRenderTextureType.GBuffer0);
                    // 1
                    if (transferMode_gBuffer1 == TransferMode.Assign) attached_camera_buffer_copies_gBufffers.SetGlobalTexture("_CameraGBufferTexture1", RT_FSR_RenderTarget_gGBuffer1);
                    else attached_camera_buffer_copies_gBufffers.Blit(RT_FSR_RenderTarget_gGBuffer1, BuiltinRenderTextureType.GBuffer1);
                    // 2
                    if (transferMode_gBuffer2 == TransferMode.Assign) attached_camera_buffer_copies_gBufffers.SetGlobalTexture("_CameraGBufferTexture2", RT_FSR_RenderTarget_gGBuffer2);
                    else attached_camera_buffer_copies_gBufffers.Blit(RT_FSR_RenderTarget_gGBuffer2, BuiltinRenderTextureType.GBuffer2);
                    // 3
                    if (!render_camera.allowHDR)
                    {
                        if (transferMode_gBuffer3 == TransferMode.Assign) attached_camera_buffer_copies_gBufffer3.SetGlobalTexture("_CameraGBufferTexture3", RT_FSR_RenderTarget_gGBuffer3);
                        else attached_camera_buffer_copies_gBufffers.Blit(RT_FSR_RenderTarget_gGBuffer3, BuiltinRenderTextureType.GBuffer3);
                    }
                }

                // Depth
                if (((int)render_camera.depthTextureMode & 1) == 1)
                {
                    attached_camera_buffer_copies_Depth.SetGlobalTexture("_CameraDepthTexture", RT_FSR_RenderTarget_Depth);
                    if (transferMode_Depth == TransferMode.Copy) {attached_camera_buffer_copies_gBufffers.Blit(RT_FSR_RenderTarget_Depth, BuiltinRenderTextureType.ResolvedDepth);}
                }

                // DepthNormals
                if ((((int)render_camera.depthTextureMode >> 1) & 1) == 1)
                {
                    if (transferMode_DepthNormals == TransferMode.Assign) attached_camera_buffer_copies_Depth.SetGlobalTexture("_CameraDepthNormalsTexture", RT_FSR_RenderTarget_DepthNormals);
                    else attached_camera_buffer_copies_gBufffers.Blit(RT_FSR_RenderTarget_DepthNormals, BuiltinRenderTextureType.DepthNormals);
                }

                // MotionVectors
                if ((((int)render_camera.depthTextureMode >> 2) & 1) == 1)
                {
                    if (transferMode_MotionVectors == TransferMode.Assign) attached_camera_buffer_copies_Depth.SetGlobalTexture("_CameraMotionVectorsTexture", RT_FSR_RenderTarget_MotionVectors);
                    else attached_camera_buffer_copies_gBufffers.Blit(RT_FSR_RenderTarget_MotionVectors, BuiltinRenderTextureType.MotionVectors);
                }
            }

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
            OnRenderImageBuffer.SetComputeIntParam(compute_FSR, "HDR", 0);

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

            // Only do this if we're propagating buffers
            if (CopyRenderBuffers)
            {
                EnqueueStack(stack_PreCull, () =>
                {
                    // Capture changes in depth rendering modes
                    if (cachedDepthTextureMode != (int)attached_camera.depthTextureMode)
                    {
                        cachedDepthTextureMode = (int)attached_camera.depthTextureMode;

                        OnValidate();
                    }
                });
            }

            // Only do this if we're managing camera event buffers
            if (ReorderBufferEvents)
            {
                EnqueueStack(stack_PreCull, () =>
                {
                    InsertReOrderedBufferEvents();
                });
            }

            // Only do this if we're managing effect event buffers
            if (ReorderEffectEvents)
            {
                EnqueueStack(stack_PreCull, () =>
                {
                    InsertReOderedEffectEvents();
                });
            }

            // We always do this
            EnqueueStack(stack_PreCull, () =>
            {
                // Mux depth texture modes of both cameras
                attached_camera.depthTextureMode |= render_camera.depthTextureMode;

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

                // Update blue noise texture
                blueNoiseFrameSwitch = (blueNoiseFrameSwitch + 1) % (64);
                compute_BufferTonemap.SetTexture(0, "NoiseTexture", blueNoise[blueNoiseFrameSwitch]);

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
        /// Inserts the buffer events at the top of the stack
        /// </summary>
        private int xBF; // Garbage reduction
        private void InsertReOrderedBufferEvents()
        {
            // Grab existing buffers
            attachedCamera_CommandBuffer_Depth = attached_camera.GetCommandBuffers(camEvent_Depth);

            // Remove all buffers
            attached_camera.RemoveCommandBuffers(camEvent_Depth);

            // Add our buffers first
            attached_camera.AddCommandBuffer(camEvent_Depth, attached_camera_buffer_copies_Depth);
            attached_camera.AddCommandBuffer(camEvent_gBuffers, attached_camera_buffer_copies_gBufffers);

            // Add the rest
            for (xBF = 0; xBF < attachedCamera_CommandBuffer_Depth.Length; ++xBF)
            {
                attached_camera.AddCommandBuffer(camEvent_Depth, attachedCamera_CommandBuffer_Depth[xBF]);
            }
        }


        /// <summary>
        /// Inserts the effect events at the top of the stack
        /// </summary>
        private int xEF; // Garbage reduction
        private void InsertReOderedEffectEvents()
        {
            // Grab existing buffers
            attachedCamera_CommandBuffer_Effect = attached_camera.GetCommandBuffers(CameraEvent.BeforeImageEffectsOpaque);

            // Remove all buffers
            attached_camera.RemoveCommandBuffers(CameraEvent.BeforeImageEffectsOpaque);

            // Add our buffer first
            attached_camera.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, OnRenderImageBuffer);

            // Add the rest
            for (xEF = 0; xEF < attachedCamera_CommandBuffer_Effect.Length; ++xEF)
            {
                attached_camera.AddCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, attachedCamera_CommandBuffer_Effect[xEF]);
            }
        }


        /// <summary>
        /// Build proxy OnRenderImage queue
        /// </summary>
        private void RebuildQueueOnRenderImage()
        {
            // Clear queue
            stack_OnRenderImage.Clear();

            // Only do this if we're managing camera event buffers
            if (ReorderBufferEvents)
            {
                EnqueueStack(stack_OnRenderImage, () =>
                {
                    attached_camera.RemoveCommandBuffer(camEvent_Depth, attached_camera_buffer_copies_Depth);
                    attached_camera.RemoveCommandBuffer(camEvent_gBuffers, attached_camera_buffer_copies_gBufffers);
                });
            }

            // Only do this if we're managing camera effect event buffers
            if (ReorderEffectEvents)
            {
                EnqueueStack(stack_OnRenderImage, () =>
                {
                    attached_camera.RemoveCommandBuffer(CameraEvent.BeforeImageEffectsOpaque, OnRenderImageBuffer);
                });
            }

            // Reset directional light state
            if (Light_Directional != null)
            {
                EnqueueStack(stack_OnRenderImage, () => { Light_Directional.enabled = Light_Directional_Enabled_Cached; });
            }

            EnqueueStack(stack_OnRenderImage, () =>
            {
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
        private bool validationDelayed = false;
        private void OnValidate()
        {
            // Race-condition sanity check
            if (validationDelayed) return;
            if (!initlised)
            {
                StartCoroutine(delayedValidationRetry());
                return;
            }

            // Build queues and buffers
            CreateRenderTextures();
            Create_Command_Buffers();
            RebuildExecutionQueues();

            // Setup camera events
            RemoveCameraEvents();
            SetCameraEventTypes();
            if (!ReorderEffectEvents) InsertReOderedEffectEvents();
            if (!ReorderBufferEvents) InsertReOrderedBufferEvents();
            if (attached_camera_buffer_copies_gBufffer3.sizeInBytes > 0) attached_camera.AddCommandBuffer(camEvent_gBuffer3, attached_camera_buffer_copies_gBufffer3);
            render_camera.AddCommandBuffer(CameraEvent.AfterEverything, render_camera_buffer_copies);

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
        /// Retry OnValidate after delay
        /// </summary>
        /// <returns></returns>
        IEnumerator delayedValidationRetry()
        {
            validationDelayed = true;
            yield return new WaitForSeconds(1);

            validationDelayed = false;
            OnValidate();
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
        [ImageEffectOpaque]
        private void OnRenderImage(RenderTexture src, RenderTexture dest)
        {
            // Execute proxy queue
            ExecuteStack(stack_OnRenderImage);

            // Copy output to destination
            Graphics.Blit(RT_Output, dest, material_ReverseTonemap);
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
#if NKLI_DEBUG
                try
                {
#endif
                    executionStack[executionStackPosition].Invoke();
#if NKLI_DEBUG
                }
                catch (Exception ex)
                {
                    Debug.LogError(ex);
                }
#endif
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