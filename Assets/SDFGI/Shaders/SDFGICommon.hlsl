#ifndef SDFGI_COMMON_INCLUDED
#define SDFGI_COMMON_INCLUDED

// ---------------------------------------------------------------------------
// Shared between SDFGIProbes.compute and SDFGIComposite.shader.
// All uniforms are set as globals from SDFGIVolume.cs every frame.
// ---------------------------------------------------------------------------

#define SDFGI_IRR_TEXELS 8.0   // 6x6 interior + 1px border
#define SDFGI_VIS_TEXELS 16.0  // 14x14 interior + 1px border

Texture2D<float4> _SDFGIIrradianceAtlas;
Texture2D<float2> _SDFGIVisibilityAtlas;
SamplerState sdfgi_linear_clamp_sampler; // Unity inline sampler (linear + clamp)

float4 _SDFGIVolumeMin;     // xyz = world-space min corner of the volume cube
float4 _SDFGIProbeSpacing;  // x   = world-space spacing between probes
float4 _SDFGIProbeGrid;     // x   = probes per axis (cubic grid)
float4 _SDFGIIrrAtlasSize;  // xy  = irradiance atlas size in pixels
float4 _SDFGIVisAtlasSize;  // xy  = visibility atlas size in pixels
float4 _SDFGIParams;        // x = intensity, y = normal bias (world),
                            // z = max visibility distance (world), w = volume size (world)

// ---------------------------------------------------------------------------
// Octahedral mapping
// ---------------------------------------------------------------------------
float2 SDFGI_OctWrap(float2 v)
{
    return (1.0 - abs(v.yx)) * float2(v.x >= 0.0 ? 1.0 : -1.0,
                                      v.y >= 0.0 ? 1.0 : -1.0);
}

// dir (normalized) -> [0,1]^2
float2 SDFGI_OctEncode(float3 n)
{
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    n.xy = n.z >= 0.0 ? n.xy : SDFGI_OctWrap(n.xy);
    return n.xy * 0.5 + 0.5;
}

// [0,1]^2 -> dir
float3 SDFGI_OctDecode(float2 f)
{
    f = f * 2.0 - 1.0;
    float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = saturate(-n.z);
    n.xy += float2(n.x >= 0.0 ? -t : t, n.y >= 0.0 ? -t : t);
    return normalize(n);
}

// ---------------------------------------------------------------------------
// Probe atlas addressing
// Layout: cellX = px + py * grid, cellY = pz. One (texels x texels) block per
// probe, with a 1px border so plain bilinear sampling stays inside the block.
// ---------------------------------------------------------------------------
float2 SDFGI_ProbeAtlasUV(int3 probe, float2 octUV, float texels, float2 atlasSize)
{
    float2 cell = float2(probe.x + probe.y * _SDFGIProbeGrid.x, probe.z) * texels;
    float2 px = cell + 1.0 + octUV * (texels - 2.0);
    return px / atlasSize;
}

// ---------------------------------------------------------------------------
// Irradiance field sampling (DDGI-style 8-probe blend with Chebyshev
// occlusion test against the visibility probes — this is the anti-leak part).
// ---------------------------------------------------------------------------
float3 SDFGI_SampleIrradiance(float3 worldPos, float3 normal)
{
    float spacing  = _SDFGIProbeSpacing.x;
    int   grid     = (int)_SDFGIProbeGrid.x;
    float3 volMin  = _SDFGIVolumeMin.xyz;

    float3 biasedPos = worldPos + normal * _SDFGIParams.y;

    float3 gridPos = (biasedPos - volMin) / spacing;
    gridPos = clamp(gridPos, 0.0, (float)(grid - 1) - 1e-3);
    int3   baseProbe = (int3)floor(gridPos);
    float3 f = gridPos - (float3)baseProbe;

    float3 sum = 0.0;
    float wSum = 0.0;

    [unroll]
    for (int i = 0; i < 8; i++)
    {
        int3 offs = int3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        int3 p = clamp(baseProbe + offs, 0, grid - 1);
        float3 probeWorld = volMin + (float3)p * spacing;

        // trilinear weight
        float3 tri = lerp(1.0 - f, f, (float3)offs);
        float w = tri.x * tri.y * tri.z;

        // smooth backface weight (wrap shading)
        float3 toProbe = probeWorld - worldPos;
        float distToProbe = max(length(toProbe), 1e-4);
        float3 dirToProbe = toProbe / distToProbe;
        float nw = (dot(dirToProbe, normal) + 1.0) * 0.5;
        w *= nw * nw + 0.2;

        // Chebyshev visibility test (probe -> biased point)
        float3 probeToPoint = biasedPos - probeWorld;
        float d = length(probeToPoint);
        if (d > 1e-4)
        {
            float2 visUV = SDFGI_ProbeAtlasUV(
                p, SDFGI_OctEncode(probeToPoint / d),
                SDFGI_VIS_TEXELS, _SDFGIVisAtlasSize.xy);
            float2 moments = _SDFGIVisibilityAtlas.SampleLevel(
                sdfgi_linear_clamp_sampler, visUV, 0);
            float mean = moments.x;
            if (d > mean)
            {
                float variance = max(abs(mean * mean - moments.y), 1e-4);
                float dd = d - mean;
                float cheb = variance / (variance + dd * dd);
                w *= max(cheb * cheb * cheb, 0.02);
            }
        }

        w = max(w, 1e-6);

        float2 irrUV = SDFGI_ProbeAtlasUV(
            p, SDFGI_OctEncode(normal),
            SDFGI_IRR_TEXELS, _SDFGIIrrAtlasSize.xy);
        float3 irr = _SDFGIIrradianceAtlas.SampleLevel(
            sdfgi_linear_clamp_sampler, irrUV, 0).rgb;

        sum  += irr * w;
        wSum += w;
    }

    return sum / max(wSum, 1e-5);
}

// Fade GI to zero near the edge of the volume so it doesn't hard-cut.
float SDFGI_VolumeFade(float3 worldPos)
{
    float size = _SDFGIParams.w;
    float3 local = (worldPos - _SDFGIVolumeMin.xyz) / size; // 0..1 inside
    float3 edge = min(local, 1.0 - local);
    float m = min(edge.x, min(edge.y, edge.z));
    return saturate(m / 0.05); // fade over outer 5%
}

#endif // SDFGI_COMMON_INCLUDED
