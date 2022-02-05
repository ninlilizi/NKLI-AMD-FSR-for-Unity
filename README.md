# AMD FSR (Fidelity Super Resolution) for Unity.

#### For Standard Pipeline
Implimented as an image effect.
* Add FSR_StandardPipeline.cs to your camera.
* It must be the first effect on the camera.
* If you use post-effects reliant on deferred or depth buffers, enable 'Copy Render Buffers' to pass them through.

#### Features
* Forward and Deferred render paths.
* Compatibility with image effects.
* Correct HDRI and colour-space handling.
* CPU Optimized: No garbage generation and a single branch instruction per frame.

#### Image effect compatiblity
A persistent child camera called 'FSR_Render_Child' will be created when you add this effect to your camera.
In most cases you need to check 'Copy Render Buffers' for effects to work correctly, regardless if executed before or after scaling.
* Maximal compatilibity is provided for effects run before scaling.
* Limited compatibility is provided for effects run after scaling.
* To run an effect before scaling, add the effect to the render child. 
* To run an effect after scaling, add the effect after FSR on the primary camera.

Generally, you should run expensive effects, such as lighting, shadows, or volumetrics before scaling. Final effects such as colourgrading, etc should be run after
The best place to insert Anti-Alisasing is before scaling, as this will reduce artifacts in the final image.

Enjoy using this as an example for integration into your Unity projects!
