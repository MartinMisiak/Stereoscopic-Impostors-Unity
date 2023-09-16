![Impostor_Title](https://github.com/MartinMisiak/Stereoscopic-Impostors-Unity/assets/40168931/af8f538f-02a0-4a99-8c81-6677ec0de50b)

# Stereoscopic-Impostors-Unity
Implementation of an impostor technique for VR rendering, based on the paper:  
"Impostor-based Rendering Acceleration for Virtual, Augmented, and Mixed Reality" - [Mišiak et al. 2021]  
https://cg.web.th-koeln.de/impostor-based-rendering-acceleration-for-virtual-augmented-and-mixed-reality/  
Developed and tested with Unity 2021.3.6f1 and the Universal Rendering Pipeline 12.1.7.

# Features
- Stereoscopic impostors with correct depth perception in VR 
- Impostors render between 2-6x faster compared to meshes
- Impostors have significantly less aliasing than mesh geometry
- Impostors are generated at runtime. No precomputation necessary (or possible :P)

# Resources
A demo scene of the impostors (CG_Museum from the above paper) can be downloaded from the following link:  
https://1drv.ms/u/s!Ap1NX8WBfJHQgtVkq5k69fnnmI03mg?e=62a7SV

# Quick Start
- Download the repository and open it as a Unity project
- Download the Unity package containing the demo scene mentioned above and import it into your project
- Open the CG_Museum scene. If there are some missing references to XR related prefabs, go to Package Manager -> XR Interaction Toolkit -> Samples -> Import Starter Assets
- The scene should now play in VR. Use the keys "J" and "K" to switch between impostor and mesh rendering

# Integration into own URP-based project
- Create two user layers in your Unity project: "Mesh Stash" and "Impostor Regeneration"
- Add "URP_Renderer_Impostors" to your currently used URP asset.
- For VR rendering, make sure Single Pass Instanced is used.
- Assign the ImpostorManager component to a single gameobject on your scene.
- Add an ImpostorGenerator component to each mesh object, that you wish to render using impostors. For hierarchies of objects, it is sufficient to place the component onto the root object and have the "impostorForHierarchy" flag set to true. 
- Adjust settings inside ImpostorManager and ImpostorTextureManager scripts

# Current Limitations 
- Impostors do not cast shadows onto the scene
- Impostor rendering does not play nicely with the SRP batcher. Currently it is highly recommended to leave it off
- Only meshes with 1 material are supported. For multi-material meshes, only the first submesh will be considered

# Disclaimer
This is a research prototype, meant to be developed further. Do not expect a polished API or full integration with all of Unity's features. It may or may not work with other versions of Unity besides version 2021.3.6f1.
