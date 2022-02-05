# AMD FSR (Fidelity Super Resolution) for Unity.

#### For Standard Pipeline
Implimented as an image effect.
* Add FSR_StandardPipeline.cs to your camera.
* It must be the first effect on the camera.
* If you use post-effects reliant on deferred or depth buffers, enable 'Copy Render Buffers' to pass them through.

#### Features
* Forward and Deferred render paths.
* Limited support for passsing through buffers to downstream image effects.
* CPU Optimized: No garbage generation and a single branch instruction per frame.

#### In progress
* Expanded support for additional types of downstream image effect.


Enjoy using this as an example for integration into your Unity projects!
