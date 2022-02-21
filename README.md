# AMD FSR (Fidelity Super Resolution) for Unity.

### For Standard Pipeline
Implimented as an image effect.
* Add FSR_StandardPipeline.cs to your camera.
* It must be the first effect on the camera.
* If you use post-effects reliant on deferred or depth buffers, enable 'Copy Render Buffers' to pass them through.

#### Features
* Forward and Deferred render paths.
* Compatibility with image effects.
* CPU Optimized: No garbage generation and minimal branch instructions per frame.

#### Image effect compatiblity
* Maximal compatilibity is provided for effects run before scaling.
* Limited compatibility is provided for effects run after scaling.

A persistent child called '*FSR_Render_Child*' will be created when you add this effect to your camera.
* To run an effect before scaling, add the effect to the render child. 
* To run an effect after scaling, add the effect after FSR on the primary camera.

In some cases you need to check '*Copy Render Buffers*' for effects placed after scaling to work correctly.

The effect will attempt to insert it's buffers first in the stack, if other effects re-assign their command buffers during rendering, this ordering can break. To provide compatibility with this scenario and force the event odering, enable either the '*Force buffer event order*' or '*Force effect event order*' options as required. Note that enabling these options creates garbage and should only be used as a last resort.

Generally, you should run expensive effects, such as lighting, shadows, or volumetrics before scaling. Anything that requires access to Light or Shadow maps must run before scaling. Anything that hooks into the render pipeline before *CameraEvent.BeforeLighting*, appears broken, or does not function, will most likely need to run before scaling.
Final effects and anything that introduces grain or distortions should run after.
* Anti-Alisasing should *always* happen before scaling, as this will reduce artifacts in the final image.


### For SRP (URP / HDRP)
My previous attempt at an SRP compatibile version has been removed, as AMD have provided their own support.
* FSR for SRP is here: https://gpuopen.com/unity/

#### Enjoy using this as an example for integration into your Unity projects!