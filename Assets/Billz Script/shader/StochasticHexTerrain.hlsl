#ifndef STOCHASTIC_HEX_TERRAIN_INCLUDED
#define STOCHASTIC_HEX_TERRAIN_INCLUDED

// Stochastic hex tiling for Unity Shader Graph (URP Terrain Lit).
// Breaks visible repeating texture tiles by blending three randomly-offset
// samples on an equilateral-triangle lattice (hex-tiling).
//
// Shader Graph Custom Function Node:
//   Type: File
//   Source: StochasticHexTerrain.hlsl
//   Name: StochasticHexSample
//   Inputs:
//     Texture        Texture2D
//     UV             Vector2
//     Tiling         Float
//     BlendSharpness Float
//     Seed           Float
//   Output:
//     Out            Vector4
//
// Notes:
// - Intended for fragment-stage albedo/diffuse sampling.
// - Set the source texture Wrap Mode to Repeat and enable mipmaps.
// - UV is transformed through UnityTexture2D.scaleTranslate so the Terrain
//   Layer's tiling/offset can be respected by Shader Graph.

float2 SHT_Hash22(float2 p)
{
    // Small, deterministic 2D hash. Returns values in [0, 1).
    float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.xx + p3.yz) * p3.zy);
}

void StochasticHexSample_float(
    UnityTexture2D Texture,
    float2 UV,
    float Tiling,
    float BlendSharpness,
    float Seed,
    out float4 Out)
{
    // Apply the Terrain Layer's texture scale and offset.
    float2 transformedUV = Texture.GetTransformedUV(UV);

    // Coordinates used to control the density of the stochastic pattern.
    float2 p = transformedUV * max(Tiling, 0.001);

    // Convert to the coordinate system of an equilateral-triangle lattice.
    const float INV_SQRT3 = 0.57735026919;
    const float TWO_OVER_SQRT3 = 1.15470053838;

    float2 grid = float2(
        p.x - p.y * INV_SQRT3,
        p.y * TWO_OVER_SQRT3
    );

    float2 cell = floor(grid);
    float2 f = frac(grid);

    float2 v0;
    float2 v1;
    float2 v2;
    float3 weights;

    // Split each parallelogram into two equilateral triangles.
    if (f.x + f.y < 1.0)
    {
        v0 = cell;
        v1 = cell + float2(1.0, 0.0);
        v2 = cell + float2(0.0, 1.0);

        weights = float3(
            1.0 - f.x - f.y,
            f.x,
            f.y
        );
    }
    else
    {
        v0 = cell + float2(1.0, 0.0);
        v1 = cell + float2(0.0, 1.0);
        v2 = cell + float2(1.0, 1.0);

        weights = float3(
            1.0 - f.y,
            1.0 - f.x,
            f.x + f.y - 1.0
        );
    }

    // Sharpening the weights reduces the washed-out look caused by blending.
    float sharpness = max(BlendSharpness, 0.25);
    weights = pow(max(weights, 0.00001), sharpness);
    weights /= max(weights.x + weights.y + weights.z, 0.00001);

    // Use the same derivatives for all three samples to keep mip selection
    // stable when the selected lattice triangle changes between pixels.
    float2 uvDx = ddx(p);
    float2 uvDy = ddy(p);

    float2 seedOffset = float2(Seed * 17.13, Seed * 41.71);
    float2 offset0 = SHT_Hash22(v0 + seedOffset);
    float2 offset1 = SHT_Hash22(v1 + seedOffset);
    float2 offset2 = SHT_Hash22(v2 + seedOffset);

    float4 sample0 = SAMPLE_TEXTURE2D_GRAD(
        Texture.tex, Texture.samplerstate, p + offset0, uvDx, uvDy);
    float4 sample1 = SAMPLE_TEXTURE2D_GRAD(
        Texture.tex, Texture.samplerstate, p + offset1, uvDx, uvDy);
    float4 sample2 = SAMPLE_TEXTURE2D_GRAD(
        Texture.tex, Texture.samplerstate, p + offset2, uvDx, uvDy);

    Out = sample0 * weights.x
        + sample1 * weights.y
        + sample2 * weights.z;
}

#endif // STOCHASTIC_HEX_TERRAIN_INCLUDED
