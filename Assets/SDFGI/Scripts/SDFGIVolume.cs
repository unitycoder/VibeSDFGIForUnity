// SDFGIVolume.cs
// Single-cascade SDFGI volume for Unity 6 URP. Static meshes only.
//
// Bake (once, or whenever the scene changes):
//   1. Collect static MeshRenderers inside the volume, build a world-space
//      triangle soup with per-material albedo + emissive.
//   2. Voxelize into albedo/emissive 3D textures (compute).
//   3. Jump-flood into an unsigned distance field (compute).
//
// Every frame:
//   4. Sphere-trace 64 jittered rays per probe through the SDF (compute),
//      lighting hits with sun + emissive + previous-frame irradiance (bounce).
//   5. Integrate into octahedral irradiance + visibility atlases (ping-pong,
//      temporal hysteresis).
//   6. Publish atlases + uniforms as globals; SDFGIRenderFeature composites.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[ExecuteAlways]
public class SDFGIVolume : MonoBehaviour
{
    public static SDFGIVolume Active { get; private set; }

    [Header("Volume (cube, centered on this transform)")]
    [Min(1f)] public float volumeSize = 40f;
    [Range(32, 128)] public int sdfResolution = 64;
    [Range(4, 32)] public int probesPerAxis = 12;

    [Header("Lighting")]
    public Light sun;                       // falls back to RenderSettings.sun
    [ColorUsage(false, true)] public Color skyColor = new Color(0.35f, 0.45f, 0.65f);
    [ColorUsage(false, true)] public Color groundColor = new Color(0.18f, 0.16f, 0.14f);
    [Min(0f)] public float intensity = 1f;
    [Min(0f)] public float bounceGain = 0.95f;
    [Range(0.5f, 0.99f)] public float hysteresis = 0.92f;
    [Min(0f)] public float normalBias = 0.25f;
    public Color fallbackAlbedo = new Color(0.5f, 0.5f, 0.5f); // used in Forward (no GBuffer)

    [Header("Bake")]
    public bool includeNonStatic = false;
    public LayerMask layers = ~0;
    public bool bakeOnEnable = true;

    [Header("Compute Shaders")]
    public ComputeShader voxelizeCS;
    public ComputeShader jumpFloodCS;
    public ComputeShader probesCS;

    const int IRR_TEXELS = 8;   // 6x6 + border
    const int VIS_TEXELS = 16;  // 14x14 + border
    const int RAYS_PER_PROBE = 64;

    RenderTexture _albedoTex, _emissiveTex, _sdfTex;
    RenderTexture _jfaA, _jfaB;
    RenderTexture[] _irradiance = new RenderTexture[2];
    RenderTexture[] _visibility = new RenderTexture[2];
    ComputeBuffer _rayBuffer;
    int _pingPong;
    int _frame;
    bool _baked;

    public bool IsReady => _baked && enabled;

    Vector3 VolumeMin => transform.position - Vector3.one * (volumeSize * 0.5f);
    float VoxelSize => volumeSize / sdfResolution;
    float ProbeSpacing => volumeSize / (probesPerAxis - 1);
    int ProbeCount => probesPerAxis * probesPerAxis * probesPerAxis;

    struct Tri
    {
        public Vector3 a, b, c;
        public Vector3 albedo;
        public Vector3 emissive;
        public const int Stride = sizeof(float) * 15;
    }

    // ------------------------------------------------------------------ setup

    void OnEnable()
    {
        Active = this;
        if (Application.isPlaying && bakeOnEnable)
            Bake();
    }

    void OnDisable()
    {
        if (Active == this) Active = null;
        ReleaseAll();
        _baked = false;
    }

    void AllocateResources()
    {
        ReleaseAll();
        int res = sdfResolution;

        // Explicit linear (non-sRGB) formats: D3D12 rejects UAV access on sRGB
        // resources, and integer formats must use Point filtering.
        _albedoTex = NewVolume(res, GraphicsFormat.R8G8B8A8_UNorm, FilterMode.Bilinear);
        _emissiveTex = NewVolume(res, GraphicsFormat.R16G16B16A16_SFloat, FilterMode.Bilinear);
        _sdfTex = NewVolume(res, GraphicsFormat.R32_SFloat, FilterMode.Bilinear);
        _jfaA = NewVolume(res, GraphicsFormat.R32_SInt, FilterMode.Point);
        _jfaB = NewVolume(res, GraphicsFormat.R32_SInt, FilterMode.Point);

        int g = probesPerAxis;
        int irrW = g * g * IRR_TEXELS, irrH = g * IRR_TEXELS;
        int visW = g * g * VIS_TEXELS, visH = g * VIS_TEXELS;
        for (int i = 0; i < 2; i++)
        {
            _irradiance[i] = NewAtlas(irrW, irrH, GraphicsFormat.R16G16B16A16_SFloat, $"SDFGI_Irradiance{i}");
            _visibility[i] = NewAtlas(visW, visH, GraphicsFormat.R32G32_SFloat, $"SDFGI_Visibility{i}");
        }

        _rayBuffer = new ComputeBuffer(ProbeCount * RAYS_PER_PROBE, sizeof(float) * 8);
        _frame = 0;
        _pingPong = 0;
    }

    static RenderTexture NewVolume(int res, GraphicsFormat fmt, FilterMode filter)
    {
        var rt = new RenderTexture(res, res, 0, fmt)
        {
            dimension = UnityEngine.Rendering.TextureDimension.Tex3D,
            volumeDepth = res,
            enableRandomWrite = true,
            filterMode = filter,
            wrapMode = TextureWrapMode.Clamp
        };
        rt.Create();
        return rt;
    }

    static RenderTexture NewAtlas(int w, int h, GraphicsFormat fmt, string name)
    {
        var rt = new RenderTexture(w, h, 0, fmt)
        {
            name = name,
            enableRandomWrite = true,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        rt.Create();
        return rt;
    }

    void ReleaseAll()
    {
        Release(ref _albedoTex); Release(ref _emissiveTex); Release(ref _sdfTex);
        Release(ref _jfaA); Release(ref _jfaB);
        for (int i = 0; i < 2; i++) { Release(ref _irradiance[i]); Release(ref _visibility[i]); }
        _rayBuffer?.Release();
        _rayBuffer = null;
    }

    static void Release(ref RenderTexture rt)
    {
        if (rt != null) { rt.Release(); DestroyImmediate(rt); rt = null; }
    }

    // ------------------------------------------------------------------- bake

    [ContextMenu("Bake")]
    public void Bake()
    {
        if (voxelizeCS == null || jumpFloodCS == null || probesCS == null)
        {
            Debug.LogError("[SDFGI] Assign the three compute shaders on the SDFGIVolume.", this);
            return;
        }

        AllocateResources();

        var tris = GatherTriangles();
        if (tris.Count == 0)
        {
            Debug.LogWarning("[SDFGI] No static triangles found inside the volume.", this);
        }

        // --- voxelize
        int res = sdfResolution;
        int clearK = voxelizeCS.FindKernel("Clear");
        int voxK = voxelizeCS.FindKernel("Voxelize");

        voxelizeCS.SetInt("_Res", res);
        voxelizeCS.SetVector("_VolMin", VolumeMin);
        voxelizeCS.SetFloat("_VoxelSize", VoxelSize);

        voxelizeCS.SetTexture(clearK, "_AlbedoRW", _albedoTex);
        voxelizeCS.SetTexture(clearK, "_EmissiveRW", _emissiveTex);
        int g3 = Mathf.CeilToInt(res / 4f);
        voxelizeCS.Dispatch(clearK, g3, g3, g3);

        if (tris.Count > 0)
        {
            using var triBuffer = new ComputeBuffer(tris.Count, Tri.Stride);
            triBuffer.SetData(tris);
            voxelizeCS.SetBuffer(voxK, "_Triangles", triBuffer);
            voxelizeCS.SetInt("_TriangleCount", tris.Count);
            voxelizeCS.SetTexture(voxK, "_AlbedoRW", _albedoTex);
            voxelizeCS.SetTexture(voxK, "_EmissiveRW", _emissiveTex);
            voxelizeCS.Dispatch(voxK, Mathf.CeilToInt(tris.Count / 64f), 1, 1);
        }

        // --- jump flood -> SDF
        int seedK = jumpFloodCS.FindKernel("Seed");
        int floodK = jumpFloodCS.FindKernel("Flood");
        int resolveK = jumpFloodCS.FindKernel("Resolve");
        jumpFloodCS.SetInt("_Res", res);

        jumpFloodCS.SetTexture(seedK, "_AlbedoTex", _albedoTex);
        jumpFloodCS.SetTexture(seedK, "_DstPtr", _jfaA);
        jumpFloodCS.Dispatch(seedK, g3, g3, g3);

        var src = _jfaA;
        var dst = _jfaB;
        for (int step = res / 2; step >= 1; step /= 2)
        {
            jumpFloodCS.SetInt("_Step", step);
            jumpFloodCS.SetTexture(floodK, "_SrcPtr", src);
            jumpFloodCS.SetTexture(floodK, "_DstPtr", dst);
            jumpFloodCS.Dispatch(floodK, g3, g3, g3);
            (src, dst) = (dst, src);
        }

        jumpFloodCS.SetTexture(resolveK, "_SrcPtr", src);
        jumpFloodCS.SetTexture(resolveK, "_SDFOut", _sdfTex);
        jumpFloodCS.Dispatch(resolveK, g3, g3, g3);

        _baked = true;
        _frame = 0;
        Debug.Log($"[SDFGI] Baked {tris.Count} triangles into {res}^3 SDF, {ProbeCount} probes.", this);
    }

    List<Tri> GatherTriangles()
    {
        var result = new List<Tri>(4096);
        var volumeBounds = new Bounds(transform.position, Vector3.one * volumeSize);
        var renderers = FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None);

        foreach (var r in renderers)
        {
            if (!r.enabled || !r.gameObject.activeInHierarchy) continue;
            if (!includeNonStatic && !r.gameObject.isStatic) continue;
            if (((1 << r.gameObject.layer) & layers) == 0) continue;
            if (!volumeBounds.Intersects(r.bounds)) continue;

            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null) continue;
            var mesh = mf.sharedMesh;
            if (!mesh.isReadable)
            {
                Debug.LogWarning($"[SDFGI] Mesh '{mesh.name}' is not readable (enable Read/Write in import settings). Skipped.", r);
                continue;
            }

            var verts = mesh.vertices;
            var l2w = r.transform.localToWorldMatrix;
            var world = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++)
                world[i] = l2w.MultiplyPoint3x4(verts[i]);

            var mats = r.sharedMaterials;
            int subCount = mesh.subMeshCount;
            for (int s = 0; s < subCount; s++)
            {
                var mat = mats.Length > 0 ? mats[Mathf.Min(s, mats.Length - 1)] : null;
                GetMaterialColors(mat, out var albedo, out var emissive);

                var indices = mesh.GetTriangles(s);
                for (int i = 0; i < indices.Length; i += 3)
                {
                    result.Add(new Tri
                    {
                        a = world[indices[i]],
                        b = world[indices[i + 1]],
                        c = world[indices[i + 2]],
                        albedo = albedo,
                        emissive = emissive
                    });
                }
            }
        }
        return result;
    }

    static void GetMaterialColors(Material mat, out Vector3 albedo, out Vector3 emissive)
    {
        albedo = new Vector3(0.5f, 0.5f, 0.5f);
        emissive = Vector3.zero;
        if (mat == null) return;

        Color c = Color.gray;
        if (mat.HasProperty("_BaseColor")) c = mat.GetColor("_BaseColor");
        else if (mat.HasProperty("_Color")) c = mat.GetColor("_Color");
        albedo = new Vector3(c.r, c.g, c.b);

        if (mat.IsKeywordEnabled("_EMISSION") && mat.HasProperty("_EmissionColor"))
        {
            Color e = mat.GetColor("_EmissionColor");
            emissive = new Vector3(e.r, e.g, e.b);
        }
    }

    // -------------------------------------------------------------- per frame

    void LateUpdate()
    {
        if (!_baked || probesCS == null) return;
        if (!Application.isPlaying) return; // edit-mode update intentionally skipped

        int prev = _pingPong;
        int curr = 1 - _pingPong;

        PushGlobals(_irradiance[prev], _visibility[prev]); // sampling targets for the trace

        var sunLight = sun != null ? sun : RenderSettings.sun;
        Vector4 sunDir = Vector4.zero;
        Vector4 sunColor = Vector4.zero;
        if (sunLight != null && sunLight.type == LightType.Directional && sunLight.isActiveAndEnabled)
        {
            Vector3 d = sunLight.transform.forward;
            sunDir = new Vector4(d.x, d.y, d.z, 1f);
            Color c = sunLight.color.linear * sunLight.intensity;
            sunColor = new Vector4(c.r, c.g, c.b, 0f);
        }

        var rot = Matrix4x4.Rotate(Random.rotationUniform);

        probesCS.SetMatrix("_RayRotation", rot);
        probesCS.SetVector("_SunDir", sunDir);
        probesCS.SetVector("_SunColor", sunColor);
        probesCS.SetVector("_SkyColor", skyColor.linear);
        probesCS.SetVector("_GroundColor", groundColor.linear);
        probesCS.SetFloat("_Hysteresis", hysteresis);
        probesCS.SetFloat("_BounceGain", bounceGain);
        probesCS.SetInt("_Res", sdfResolution);
        probesCS.SetFloat("_VoxelSize", VoxelSize);
        probesCS.SetInt("_FirstFrame", _frame == 0 ? 1 : 0);

        int traceK = probesCS.FindKernel("TraceRays");
        probesCS.SetTexture(traceK, "_SDF", _sdfTex);
        probesCS.SetTexture(traceK, "_AlbedoTex", _albedoTex);
        probesCS.SetTexture(traceK, "_EmissiveTex", _emissiveTex);
        probesCS.SetTexture(traceK, "_SDFGIIrradianceAtlas", _irradiance[prev]);
        probesCS.SetTexture(traceK, "_SDFGIVisibilityAtlas", _visibility[prev]);
        probesCS.SetBuffer(traceK, "_Rays", _rayBuffer);
        probesCS.Dispatch(traceK, ProbeCount, 1, 1);

        int irrK = probesCS.FindKernel("UpdateIrradiance");
        probesCS.SetBuffer(irrK, "_Rays", _rayBuffer);
        probesCS.SetTexture(irrK, "_SDFGIIrradianceAtlas", _irradiance[prev]);
        probesCS.SetTexture(irrK, "_IrradianceRW", _irradiance[curr]);
        probesCS.Dispatch(irrK, ProbeCount, 1, 1);

        int visK = probesCS.FindKernel("UpdateVisibility");
        probesCS.SetBuffer(visK, "_Rays", _rayBuffer);
        probesCS.SetTexture(visK, "_SDFGIVisibilityAtlas", _visibility[prev]);
        probesCS.SetTexture(visK, "_VisibilityRW", _visibility[curr]);
        probesCS.Dispatch(visK, ProbeCount, 1, 1);

        _pingPong = curr;
        _frame++;

        // Publish the freshly written atlases for the composite pass.
        PushGlobals(_irradiance[curr], _visibility[curr]);
    }

    void PushGlobals(RenderTexture irr, RenderTexture vis)
    {
        Shader.SetGlobalTexture("_SDFGIIrradianceAtlas", irr);
        Shader.SetGlobalTexture("_SDFGIVisibilityAtlas", vis);
        Shader.SetGlobalVector("_SDFGIVolumeMin", VolumeMin);
        Shader.SetGlobalVector("_SDFGIProbeSpacing", new Vector4(ProbeSpacing, 0, 0, 0));
        Shader.SetGlobalVector("_SDFGIProbeGrid", new Vector4(probesPerAxis, probesPerAxis, probesPerAxis, 0));
        Shader.SetGlobalVector("_SDFGIIrrAtlasSize", new Vector4(irr.width, irr.height, IRR_TEXELS, 0));
        Shader.SetGlobalVector("_SDFGIVisAtlasSize", new Vector4(vis.width, vis.height, VIS_TEXELS, 0));
        float maxVisDist = ProbeSpacing * 1.75f;
        Shader.SetGlobalVector("_SDFGIParams", new Vector4(intensity, normalBias, maxVisDist, volumeSize));
        Shader.SetGlobalVector("_SDFGIFallbackAlbedo", fallbackAlbedo.linear);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.3f, 0.9f, 0.5f, 0.8f);
        Gizmos.DrawWireCube(transform.position, Vector3.one * volumeSize);
    }
}
