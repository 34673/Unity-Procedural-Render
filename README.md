# Procedural Rendering
Texture-based procedural generation and rendering system for Unity 6.3+. Featuring:
- Tool-less Unity workflow.
- Editor runtime regeneration/hot reload.
- Compute-based generation (hardware accelerated raycasting when available).
- GPU-driven rendering (indirect draws, compute-based culling).
- Optional angle-based lighting (NdotL).
- Shadow map sampling.

# Overview
The `generation texture` is a top-down view of the chosen `terrain` mesh (similar to a minimap) that represents where the objects are going to exist. Each texel (texture pixel) corresponds to a given area on the mesh and encodes two properties:
- Type/index (`red` channel), corresponding to one of the elements in the `assets` list, where different `meshes` and their respective `material` are enumerated. Since `index 0` in the texture is used to represent an empty area, indexing starts at 1. The system will subtract it to correspond to the correct `asset` element.
- Density/instance count (`green` channel), which is the amount of instances of the specified mesh that will exist within the area occupied by the texel.
Other channels are currently ignored.

# Setup Requirements
- Unity 6.3 (for UnifiedRayTracing API)
- All the referenced assets should be imported with the `Read/Write` option enabled.
- The texture itself should be imported with:
    - Format: `RG 16`
    - Non-power of 2: `None` (when applicable)
    - Generate Mipmap: `Disabled`
    - Wrap Mode: `Clamp`
    - Filter Mode: `Point`
    - Alpha Source: `None` (probably doesn't do anything, but just in case)
- The materials must use the supplied rendering shader(s) as indirect draw calls and a visibility buffer are used.

# Tips
- The texture resolution shouldn't be so high that the texel area is smaller than the mesh asset. Higher resolution make for finer shapes, but the density property is there if more instances are needed in a given area.
- Density shouldn't be so high that it causes major overlaps between a lot of instances, which will dramatically decrease graphics card performance for little visual gains.
- Aspect ratio of the texture isn't limited but should ideally match the terrain mesh's.
- Asset type in texel data is assumed to map to the `Assets` list. Out of bounds accesses are considered undefined behavior.
- Densities ending in perfect rectangles or squares are ideal when filling a region. Small holes may appear between the texel areas otherwise.
- Too many instances may hit compute shader threading limits and cause errors.
- The generation texture should have an empty border (1 texel width should be enough) to prevent some instances floating.
- A mix of dithering and density gradients can be used in the texture to create a smoother transition.
- In play-mode and while inputs are being captured, `Ctrl + alt + G` will trigger the generation again. This can be used alongside the editor's asset hot-reloading to update the generation results.
- Unexpected or absurd values will likely break things, quite obviously.

# To Do

## Planned changes
- More stable lighting (weaker color bleeding).
- Functional shadow casting (optional).
- Terrain-wide color gradients.
- Wind effect (optional).
- Movements when compatible entities walk through (for grass; +optional tactile memory?).
- Interaction texture + dynamically branched effects (burn, cut, etc...).
- Density-over-distance culling (higher density closer to camera).
- Levels of Details.
- Queue system for generation to alleviate cost-per-frame when loading multiple areas.
- Instance removal when raycast is failing.
- Robustness and possibly lifting some limitations/requirements.

## Ideally
- More granular loading queue with multiple zones per area (possibly needed on lower-end hardware).
- Occlusion culling.
- Hierarchical structure (quadtree?) to speed up culling?
- Fully MultiDrawIndirect-compatible setup.

# Changelogs

## Revision 1:
- Added procedural generation.
- Added mesh combining to share GPU resources.
- Added setup for indirect draw calls for later GPU-driven rendering.
- Added untested rendering shader.

## Revision 2:
- Added transform setup to be used by rendering shader.

## Revision 3:
- Moved transform setup to multithreaded burst job.
- Various optimizations.

## Revision 4:
- Moved texture processing and raycast setup steps to burst jobs.
- Various optimizations.

## Revision 5:
- Rewrote algorithm to allow raycast setup step to be multithreaded.
- Various optimizations.

## Revision 6:
- Moved transform setup step to a compute shader.

## Revision 7:
- Buffers now use memory mapping (when applicable) to speed up buffer writes.

## Revision 8:
- Added randomization to instance distribution.

## Revision 9:
- Added randomization to rotation and scale on transforms.
- Draw calls are fully setup.
- Rendering shader is now functional.
- Added custom color and texture sampling to rendering shader.

## Revision 10:
- Upgrading to Unity 6.3 to use `UnifiedRayTracing` API for GPU raycasting.
- Rewrote algorithm to work with `UnifiedRayTracing` API.
- Added automatic support for hardware ray acceleration. Currently disabled for profiling purposes (compute path being the baseline).
- Stripped off ray and hit parameters that can be assumed/that don't change. Indirectly increased generation performance.

## Revision 11:
- Moved raycast setup to a compute shader, as the CPU dependency no longer exists for the raycasting.

## Revision 12:
- Reusing `Ray.distance` to store the hit distance, which allows to remove the hit buffer entirely.

## Revision 13:
- Added compute-based distance and bounding sphere frustum culling.
- Added instance visibility buffer to be used by rendering shader.

## Revision 14:
- Added additional lights and shadow attenuation.
- Added shader keyword and property for optional NdotL use on additional lights.
- Fixes to the GPU-side randomization in the generation steps.
- Enabled automatic support for hardware ray acceleration.
- Removed the `MeshRenderer` reference used for the world-space bounds of the terrain as they are now sourced from the `MeshFilter` reference in the scene hierarchy.

## Revision 15:
- Moved raycast setup global parameters to a constant buffer.
- Removed all unsafe code dependencies.
- Various refactors.

## Revision 16 (v1.0):
- First public release.
- Rebranding system as it can be used for more than grass generation.
- Created sample project.
- Added shader keyword and property for optional NdotL use on the main light.
- Changed transform buffer to use 3x4 matrices rather than 4x4 (-25% memory usage and possibly faster processing).
- Minor refactors/cleanups.

## Revision 17:
- Tucked code into its own namespace to avoid immediate collisions with external systems.