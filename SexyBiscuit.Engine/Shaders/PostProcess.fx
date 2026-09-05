//=============================================================================
// PostProcess.fx — the built-in fullscreen passes.
//
// Each technique is a separate pass. Load the compiled effect once and clone it
// per pass, selecting the technique you want:
//
//     var fx = content.Load<Effect>("PostProcess");
//
//     var bloom = PostProcessing3D.Bloom(0.8f, 1.2f);
//     bloom.Shader = fx.Clone();
//     bloom.Shader.CurrentTechnique = bloom.Shader.Techniques["Bloom"];
//
// SpriteBatch supplies the source texture in register s0, so every pass samples
// through the SpriteBatch sampler rather than declaring its own.
//=============================================================================

#if OPENGL
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_3
    #define PS_SHADERMODEL ps_4_0_level_9_3
#endif

// Supplied by PostProcessing3D / RenderSystem3D for every pass.
float2 Resolution = float2(1920, 1080);
float2 TexelSize  = float2(1.0 / 1920.0, 1.0 / 1080.0);

// Bloom
float Threshold = 0.8;
float Intensity = 1.0;

// Vignette
float VignetteRadius   = 0.75;
float VignetteSoftness = 0.45;

// Colour grading
float Brightness = 0.0;
float Contrast   = 1.0;
float Saturation = 1.0;

// Scanline
float LineSpacing   = 3.0;
float LineIntensity = 0.35;
float Curvature     = 0.0;

sampler2D SourceSampler : register(s0);

float Luminance(float3 c)
{
    // Rec. 709 coefficients — matches how the eye weights the channels.
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

//-----------------------------------------------------------------------------
// Bloom — threshold, then a separable-ish 9-tap blur of the bright parts
//-----------------------------------------------------------------------------
float4 BloomPS(float2 uv : TEXCOORD0) : COLOR0
{
    float4 base = tex2D(SourceSampler, uv);

    // Gather the over-threshold energy from a small neighbourhood. A true
    // two-pass gaussian looks better; this single pass keeps the chain simple
    // and is enough for a glow on highlights.
    float3 bloom = 0;
    float weightSum = 0;

    [unroll]
    for (int y = -2; y <= 2; y++)
    {
        [unroll]
        for (int x = -2; x <= 2; x++)
        {
            float2 offset = float2(x, y) * TexelSize * 2.0;
            float3 s = tex2D(SourceSampler, uv + offset).rgb;

            float bright = max(Luminance(s) - Threshold, 0.0);
            float weight = exp(-(x * x + y * y) * 0.25);

            bloom += s * bright * weight;
            weightSum += weight;
        }
    }

    bloom /= max(weightSum, 1e-4);
    return float4(base.rgb + bloom * Intensity, base.a);
}

//-----------------------------------------------------------------------------
// Vignette — radial darkening toward the edges
//-----------------------------------------------------------------------------
float4 VignettePS(float2 uv : TEXCOORD0) : COLOR0
{
    float4 base = tex2D(SourceSampler, uv);

    float2 centred = uv - 0.5;
    // Correct for aspect so the vignette stays circular on a wide screen.
    centred.x *= Resolution.x / max(Resolution.y, 1.0);

    float dist = length(centred);
    float mask = smoothstep(VignetteRadius, VignetteRadius - VignetteSoftness, dist);

    return float4(base.rgb * mask, base.a);
}

//-----------------------------------------------------------------------------
// Colour grade — brightness, contrast, saturation
//-----------------------------------------------------------------------------
float4 ColourGradePS(float2 uv : TEXCOORD0) : COLOR0
{
    float4 base = tex2D(SourceSampler, uv);
    float3 c = base.rgb;

    c += Brightness;
    c = (c - 0.5) * Contrast + 0.5;          // contrast pivots around mid-grey
    c = lerp(Luminance(c).xxx, c, Saturation);

    return float4(saturate(c), base.a);
}

//-----------------------------------------------------------------------------
// Scanline — CRT lines with optional barrel distortion
//-----------------------------------------------------------------------------
float4 ScanlinePS(float2 uv : TEXCOORD0) : COLOR0
{
    float2 sampleUV = uv;

    if (Curvature > 0.0)
    {
        float2 centred = uv * 2.0 - 1.0;
        float  r2 = dot(centred, centred);
        centred *= 1.0 + Curvature * r2;
        sampleUV = centred * 0.5 + 0.5;

        // Outside the curved screen is bezel, not image.
        if (sampleUV.x < 0 || sampleUV.x > 1 || sampleUV.y < 0 || sampleUV.y > 1)
            return float4(0, 0, 0, 1);
    }

    float4 base = tex2D(SourceSampler, sampleUV);

    float line = frac(sampleUV.y * Resolution.y / max(LineSpacing, 1.0));
    float darken = 1.0 - LineIntensity * step(0.5, line);

    return float4(base.rgb * darken, base.a);
}

technique Bloom       { pass P0 { PixelShader = compile PS_SHADERMODEL BloomPS();       } }
technique Vignette    { pass P0 { PixelShader = compile PS_SHADERMODEL VignettePS();    } }
technique ColourGrade { pass P0 { PixelShader = compile PS_SHADERMODEL ColourGradePS(); } }
technique Scanline    { pass P0 { PixelShader = compile PS_SHADERMODEL ScanlinePS();    } }
