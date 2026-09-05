//=============================================================================
// StandardPBR.fx — the engine's default lit surface shader.
//
// Implements the parameter contract documented on RenderSystem3D: assign the
// compiled effect to Material3D.Shader and the renderer fills everything in.
//
// Lighting is Cook-Torrance specular (GGX distribution, Smith visibility,
// Schlick Fresnel) over a Lambert diffuse term, which is what "metallic /
// roughness" workflows from Blender and Substance expect. Shader Model 3
// caps the instruction count, so the light loop is bounded at 4.
//=============================================================================

#if OPENGL
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define VS_SHADERMODEL vs_4_0_level_9_3
    #define PS_SHADERMODEL ps_4_0_level_9_3
#endif

#define MAX_LIGHTS 4

// Light type constants, matching SexyBiscuit.Engine.Rendering.LightType.
#define LIGHT_DIRECTIONAL 0
#define LIGHT_POINT       1
#define LIGHT_SPOT        2

//-----------------------------------------------------------------------------
// Transforms
//-----------------------------------------------------------------------------
float4x4 World;
float4x4 View;
float4x4 Projection;
float4x4 WorldViewProjection;
float4x4 WorldInverseTranspose;

float3 CameraPosition;

//-----------------------------------------------------------------------------
// Lighting
//-----------------------------------------------------------------------------
float3 AmbientColor       = float3(0.15, 0.15, 0.17);
int    LightCount         = 0;
float3 LightDirections[MAX_LIGHTS];
float3 LightPositions[MAX_LIGHTS];
float3 LightColors[MAX_LIGHTS];
// (type, range, cosSpotAngle, unused)
float4 LightParams[MAX_LIGHTS];

//-----------------------------------------------------------------------------
// Material — set by Material3D.Apply
//-----------------------------------------------------------------------------
float4 AlbedoColor        = float4(1, 1, 1, 1);
float  Metallic           = 0.0;
float  Roughness          = 0.5;
float  EmissiveIntensity   = 0.0;

texture AlbedoMap;
sampler2D AlbedoSampler = sampler_state
{
    Texture   = <AlbedoMap>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = Linear;
    AddressU  = Wrap;   AddressV  = Wrap;
};

texture NormalMap;
sampler2D NormalSampler = sampler_state
{
    Texture   = <NormalMap>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = Linear;
    AddressU  = Wrap;   AddressV  = Wrap;
};

texture MetallicMap;
sampler2D MetallicSampler = sampler_state
{
    Texture = <MetallicMap>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = Linear;
};

texture RoughnessMap;
sampler2D RoughnessSampler = sampler_state
{
    Texture = <RoughnessMap>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = Linear;
};

texture EmissiveMap;
sampler2D EmissiveSampler = sampler_state
{
    Texture = <EmissiveMap>;
    MinFilter = Linear; MagFilter = Linear; MipFilter = Linear;
};

//-----------------------------------------------------------------------------
// Shadows
//-----------------------------------------------------------------------------
float4x4 LightViewProjection;

texture ShadowMap;
sampler2D ShadowSampler = sampler_state
{
    Texture   = <ShadowMap>;
    MinFilter = Point; MagFilter = Point; MipFilter = None;
    AddressU  = Clamp; AddressV  = Clamp;
};

// Depth bias, in light-space units. Too small and flat surfaces self-shadow into
// stripes; too large and contact shadows detach from their casters.
float ShadowBias = 0.0025;

//-----------------------------------------------------------------------------
// IO
//-----------------------------------------------------------------------------
struct VSInput
{
    float4 Position : POSITION0;
    float3 Normal   : NORMAL0;
    float2 UV       : TEXCOORD0;
};

struct PSInput
{
    float4 Position    : POSITION0;
    float3 WorldPos    : TEXCOORD0;
    float3 Normal      : TEXCOORD1;
    float2 UV          : TEXCOORD2;
    float4 ShadowCoord : TEXCOORD3;
};

//-----------------------------------------------------------------------------
// Vertex
//-----------------------------------------------------------------------------
PSInput MainVS(VSInput input)
{
    PSInput output;

    output.Position    = mul(input.Position, WorldViewProjection);
    output.WorldPos    = mul(input.Position, World).xyz;
    output.Normal      = normalize(mul(float4(input.Normal, 0), WorldInverseTranspose).xyz);
    output.UV          = input.UV;
    output.ShadowCoord = mul(float4(output.WorldPos, 1), LightViewProjection);

    return output;
}

//-----------------------------------------------------------------------------
// BRDF terms
//-----------------------------------------------------------------------------

// GGX / Trowbridge-Reitz normal distribution.
float DistributionGGX(float NdotH, float roughness)
{
    float a  = roughness * roughness;
    float a2 = a * a;
    float d  = NdotH * NdotH * (a2 - 1.0) + 1.0;
    return a2 / max(3.14159265 * d * d, 1e-5);
}

// Smith geometry term with the Schlick-GGX approximation, direct-lighting k.
float GeometrySmith(float NdotV, float NdotL, float roughness)
{
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    float gv = NdotV / (NdotV * (1.0 - k) + k);
    float gl = NdotL / (NdotL * (1.0 - k) + k);
    return gv * gl;
}

float3 FresnelSchlick(float cosTheta, float3 f0)
{
    return f0 + (1.0 - f0) * pow(saturate(1.0 - cosTheta), 5.0);
}

//-----------------------------------------------------------------------------
// Shadow lookup — 2x2 percentage-closer filter to soften the map's stair-steps
//-----------------------------------------------------------------------------
float SampleShadow(float4 shadowCoord)
{
    float3 proj = shadowCoord.xyz / shadowCoord.w;

    // Outside the shadow volume means lit, not shadowed.
    if (proj.x < -1 || proj.x > 1 || proj.y < -1 || proj.y > 1 || proj.z > 1)
        return 1.0;

    float2 uv = proj.xy * float2(0.5, -0.5) + 0.5;
    float depth = proj.z - ShadowBias;

    float2 texel = float2(1.0 / 1024.0, 1.0 / 1024.0);
    float lit = 0.0;

    lit += (tex2D(ShadowSampler, uv + float2(-0.5, -0.5) * texel).r >= depth) ? 1.0 : 0.0;
    lit += (tex2D(ShadowSampler, uv + float2( 0.5, -0.5) * texel).r >= depth) ? 1.0 : 0.0;
    lit += (tex2D(ShadowSampler, uv + float2(-0.5,  0.5) * texel).r >= depth) ? 1.0 : 0.0;
    lit += (tex2D(ShadowSampler, uv + float2( 0.5,  0.5) * texel).r >= depth) ? 1.0 : 0.0;

    return lit * 0.25;
}

//-----------------------------------------------------------------------------
// Pixel
//-----------------------------------------------------------------------------
float4 MainPS(PSInput input) : COLOR0
{
    float4 albedoSample = tex2D(AlbedoSampler, input.UV);
    float3 albedo       = albedoSample.rgb * AlbedoColor.rgb;
    float  alpha        = albedoSample.a * AlbedoColor.a;

    float metallic  = saturate(Metallic  * tex2D(MetallicSampler,  input.UV).r);
    float roughness = clamp(Roughness * tex2D(RoughnessSampler, input.UV).r, 0.04, 1.0);

    float3 N = normalize(input.Normal);
    float3 V = normalize(CameraPosition - input.WorldPos);
    float NdotV = saturate(dot(N, V));

    // Dielectrics reflect ~4% at normal incidence; metals tint their reflection
    // with the albedo and have no diffuse term.
    float3 f0 = lerp(float3(0.04, 0.04, 0.04), albedo, metallic);

    float shadow = SampleShadow(input.ShadowCoord);
    float3 lit = AmbientColor * albedo;

    [unroll]
    for (int i = 0; i < MAX_LIGHTS; i++)
    {
        if (i >= LightCount) break;

        float  type       = LightParams[i].x;
        float  range      = LightParams[i].y;
        float  cosSpot    = LightParams[i].z;
        float3 lightColor = LightColors[i];

        // L points from the surface toward the light.
        float3 L;
        float  attenuation = 1.0;

        if (type == LIGHT_DIRECTIONAL)
        {
            L = -normalize(LightDirections[i]);
        }
        else
        {
            float3 toLight = LightPositions[i] - input.WorldPos;
            float  dist    = length(toLight);
            L = toLight / max(dist, 1e-4);

            // Smooth inverse-square falloff, cut off cleanly at Range so a light
            // never contributes past the radius the designer set.
            float t = saturate(dist / max(range, 1e-4));
            float falloff = 1.0 - t * t;
            attenuation = falloff * falloff;

            if (type == LIGHT_SPOT)
            {
                float cosAngle = dot(-L, normalize(LightDirections[i]));
                attenuation *= smoothstep(cosSpot, lerp(cosSpot, 1.0, 0.25), cosAngle);
            }
        }

        float NdotL = saturate(dot(N, L));
        if (NdotL <= 0.0 || attenuation <= 0.0) continue;

        float3 H     = normalize(V + L);
        float  NdotH = saturate(dot(N, H));
        float  VdotH = saturate(dot(V, H));

        float  D = DistributionGGX(NdotH, roughness);
        float  G = GeometrySmith(NdotV, NdotL, roughness);
        float3 F = FresnelSchlick(VdotH, f0);

        float3 specular = (D * G * F) / max(4.0 * NdotV * NdotL, 1e-4);
        float3 kD = (1.0 - F) * (1.0 - metallic);
        float3 diffuse = kD * albedo / 3.14159265;

        // Only the first light casts shadows — it is the one the renderer built
        // the depth map for.
        float shadowTerm = (i == 0) ? shadow : 1.0;

        lit += (diffuse + specular) * lightColor * NdotL * attenuation * shadowTerm;
    }

    lit += tex2D(EmissiveSampler, input.UV).rgb * EmissiveIntensity;

    return float4(lit, alpha);
}

technique Standard
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
}
