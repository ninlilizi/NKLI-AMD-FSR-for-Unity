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
using UnityEngine.XR;

namespace NKLI
{
    [ExecuteInEditMode]
    public class FSR_StandardPipeline : MonoBehaviour
    {
        // Cache local camera
        private Camera attached_camera;

        // Shader
        public ComputeShader compute_FSR;

        // Render textures
        private RenderTexture RT_FSR_RenderTarget;
        private RenderTexture RT_Output;

        // Render scale
        [Range(0.125f, 1)] public float render_scale = 0.75f;
        private float render_scale_cached;

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

            // Cache this
            attached_camera = GetComponent<Camera>();

            // Create textures
            CreateRenderTexture();
        }


        private void OnDisable()
        {
            // Dispose render target
            if (RT_FSR_RenderTarget != null) RT_FSR_RenderTarget.Release();
            if (RT_Output != null) RT_Output.Release();
        }


        /// <summary>
        /// Creates render textures
        /// </summary>
        private void CreateRenderTexture()
        {

            if (RT_FSR_RenderTarget != null) RT_FSR_RenderTarget.Release();
            float target_width = attached_camera.pixelWidth * render_scale;
            float target_height = attached_camera.pixelHeight * render_scale;
            RT_FSR_RenderTarget = new RenderTexture((int)target_width, (int)target_height, 24, attached_camera.allowHDR ? DefaultFormat.HDR : DefaultFormat.LDR);

            if (RT_Output != null) RT_Output.Release();
            RT_Output = new RenderTexture(attached_camera.pixelWidth, attached_camera.pixelHeight, 24, attached_camera.allowHDR ? DefaultFormat.HDR : DefaultFormat.LDR);
#if UNITY_2019_2_OR_NEWER
            RT_Output.vrUsage = VRTextureUsage.DeviceSpecific;
#else
            if (UnityEngine.XR.XRSettings.isDeviceActive) RT_Output.vrUsage = VRTextureUsage.TwoEyes;
#endif
            RT_Output.enableRandomWrite = true;
            RT_Output.useMipMap = false;
            RT_Output.Create();
        }


        // Update is called once per frame
        void LateUpdate()
        {
            // If the render scale has changed we must recreate our textures
            if (render_scale != render_scale_cached)
            {
                render_scale_cached = render_scale;
                CreateRenderTexture();
            }

            attached_camera.targetTexture = RT_FSR_RenderTarget;
            Graphics.SetRenderTarget(RT_FSR_RenderTarget.colorBuffer, RT_FSR_RenderTarget.depthBuffer);
        }


        private void OnRenderImage(RenderTexture src, RenderTexture dest)
        {
            compute_FSR.SetInt("input_viewport_width", attached_camera.scaledPixelWidth);
            compute_FSR.SetInt("input_viewport_height", attached_camera.scaledPixelHeight);
            compute_FSR.SetInt("input_image_width", attached_camera.pixelWidth);
            compute_FSR.SetInt("input_image_height", attached_camera.pixelHeight);

            compute_FSR.SetInt("output_image_width", RT_Output.width);
            compute_FSR.SetInt("output_image_height", RT_Output.height);

            compute_FSR.SetInt("upsample_mode", (int)upsample_mode);

            int dispatchX = (RT_Output.width + (16 - 1)) / 16;
            int dispatchY = (RT_Output.height + (16 - 1)) / 16;

            if (sharpening && upsample_mode == upsample_modes.FSR)
            {
                // Create intermediary render texture
                RenderTexture intermediary = RenderTexture.GetTemporary(RT_Output.width, RT_Output.height, 24, RT_Output.format);
#if UNITY_2019_2_OR_NEWER
                intermediary.vrUsage = VRTextureUsage.DeviceSpecific;
#else
                if (UnityEngine.XR.XRSettings.isDeviceActive) intermediary.vrUsage = VRTextureUsage.TwoEyes;
#endif
                intermediary.enableRandomWrite = true;
                intermediary.useMipMap = false;
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

            attached_camera.targetTexture = null;
            Graphics.Blit(RT_Output, dest);
        }
    }
}