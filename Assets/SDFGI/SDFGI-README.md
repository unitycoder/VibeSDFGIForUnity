### DISCLAIMER

This is one shot generated with Fable 5 AI.

# SDFGI for Unity 6000.3 URP (basic, static-only)

A single-cascade implementation of Godot's SDFGI (Juan Linietsky's design doc),
adapted for Unity 6 URP with RenderGraph. Static meshes only.

## What it does

- Bakes static scene geometry into a voxel grid: albedo + emissive 3D textures,
  then a jump-flood unsigned distance field.
- Maintains a cubic grid of DDGI-style probes:
  - Irradiance probes: 6x6 octahedral texels (+1px border), atlas texture.
  - Visibility (occlusion) probes: 14x14 octahedral depth moments (+border),
    used with the Chebyshev test to stop light leaking through walls.
- Every frame, 64 jittered rays per probe are sphere-traced through the SDF.
  Hits are shaded with: directional sun (sphere-traced shadow), material
  emissive, and the previous frame's irradiance field (= infinite bounce).
  Misses read a sky/ground gradient. Results converge over time via
  temporal hysteresis.
- A fullscreen render pass adds `irradiance * albedo` on top of the lit scene.

## What's intentionally NOT here (vs. the Godot doc)

- Cascades / cascade scrolling — one fixed volume around the SDFGIVolume
  transform instead. Re-bake if the world is bigger than one volume.
- The 16x sub-voxel bitfield raster (thin walls below ~1 voxel will leak or
  over-occlude — give walls some thickness relative to voxel size).
- Ray-hit / light caches and reduced update frequency for off-screen probes.
- SDF reflections (sharp/medium roughness stages).
- Dynamic objects (they receive GI through the probes like everything else on
  screen, but don't block or contribute light).
- Point/spot lights — only one directional light + emissive surfaces + sky.

## Setup

1. Drop the `Scripts/` and `Shaders/` folders into your project. Keep
   `SDFGICommon.hlsl` next to the .compute files and `SDFGIComposite.shader`
   (they include it by relative path).
2. URP Asset > Renderer: add the **SDFGI Render Feature** to your Universal
   Renderer Data. It auto-finds `Hidden/SDFGI/Composite`.
   - **Recommended: set the Rendering Path to Deferred** so the pass can read
     real albedo from GBuffer0. In Forward it falls back to a constant
     "Fallback Albedo" color, which still looks decent but is approximate.
3. Create an empty GameObject, add **SDFGIVolume**, position it at the center
   of your playable area.
4. Assign the three compute shaders (`SDFGIVoxelize`, `SDFGIJumpFlood`,
   `SDFGIProbes`) to the volume component.
5. Mark your level geometry **Static** (or tick `Include Non Static`), and make
   sure meshes have **Read/Write enabled** in import settings (needed for the
   CPU-side triangle gather at bake time).
6. (Optional) Assign your directional light to `Sun`; otherwise
   `RenderSettings.sun` is used.
7. Enter Play mode — it bakes on enable (`Bake On Enable`), or call
   `SDFGIVolume.Bake()` / use the component context menu.

Since the GI provides ambient/indirect light, you'll usually want to turn the
scene's Environment Lighting down (Lighting window > Environment > Intensity
Multiplier ~0) so you're not double-counting ambient.

## Tuning

| Field | Notes |
|---|---|
| `Volume Size` | World-space cube edge. Voxel size = size / resolution; keep walls thicker than ~1.5 voxels. |
| `Sdf Resolution` | 64 is a good start; 128 = 8x bake cost, better thin geometry. |
| `Probes Per Axis` | 12-17. Spacing = size / (n-1). Per-frame cost scales with n^3. |
| `Hysteresis` | Higher = smoother/slower convergence. 0.9-0.97. |
| `Bounce Gain` | Multiplier on the recursive bounce term. Keep <= 1 or energy can run away. |
| `Normal Bias` | World-units push along the normal when sampling probes; ~0.5-1x voxel size fights leaks/shadow acne in the field. |
| `Intensity` | Final composite multiplier. |

## Known caveats

- This is a starting point, written against the Unity 6 RenderGraph API but
  not battle-tested — expect to iterate on it.
- Probes inside geometry go dark (backface detection kills their rays); the
  Chebyshev weighting mostly excludes them, but probe placement relative to
  walls still matters, as in any DDGI-style system.
- Bake reads `Mesh.vertices` on the CPU; very heavy scenes will hitch on bake.
- Albedo/emissive are flat per-material colors (`_BaseColor`, `_EmissionColor`)
  — textures are not sampled into the voxel grid.
- VR: the composite pass uses standard URP stereo macros but hasn't been
  validated on Quest; the probe update itself is view-independent so it should
  be XR-friendly in principle (no TAA dependency, per the original design).
