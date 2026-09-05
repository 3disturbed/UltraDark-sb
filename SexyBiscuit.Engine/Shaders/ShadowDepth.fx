//=============================================================================
// ShadowDepth.fx — depth-only pass for RenderSystem3D.EnableShadows.
//
// Renders geometry from the light's point of view and writes normalised depth
// to the red channel of a SurfaceFormat.Single target. StandardPBR.fx compares
// against it.
//=============================================================================

#if OPENGL
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_3
    #define PS_SHADERMODEL ps_4_0_level_9_3
#endif

float4x4 WorldViewProjection;

struct VSInput
{
    float4 Position : POSITION0;
};

struct PSInput
{
    float4 Position : POSITION0;
    // Depth is carried through a varying rather than read back from the
    // rasteriser, which shader model 3 does not expose portably.
    float2 Depth    : TEXCOORD0;
};

PSInput MainVS(VSInput input)
{
    PSInput output;
    output.Position = mul(input.Position, WorldViewProjection);
    output.Depth    = output.Position.zw;
    return output;
}

float4 MainPS(PSInput input) : COLOR0
{
    float depth = input.Depth.x / input.Depth.y;
    return float4(depth, depth, depth, 1);
}

technique ShadowDepth
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
