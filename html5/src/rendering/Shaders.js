// -----------------------------------------------------------------------------
// Shaders — GLSL ES 3.0 sources, ported from the engine's HLSL .fx files.
//
// A note on matrix conventions, because getting it wrong produces a scene that
// is subtly inside out rather than obviously broken:
//
// Matrix4 stores XNA's row-major, row-vector layout. GL reads a uniform matrix
// as column-major, so it interprets that same array as the transpose — and GL
// also multiplies the other way round (`M * v`). The two inversions cancel, so
// `uViewProjection * uWorld * vec4(position, 1.0)` is correct with the array
// uploaded untransposed. Normals use a world-inverse-transpose built on the CPU
// for the same reason.
// -----------------------------------------------------------------------------

/** The maximum lights any one object is shaded by. Matches StandardPBR.fx's cap. */
export const MAX_LIGHTS = 8;

export const STANDARD_VERTEX = `#version 300 es
precision highp float;

in vec3 aPosition;
in vec3 aNormal;
in vec2 aTexCoord;

uniform mat4 uWorld;
uniform mat4 uViewProjection;
uniform mat4 uWorldInverseTranspose;

out vec3 vWorldPosition;
out vec3 vNormal;
out vec2 vTexCoord;

void main() {
    vec4 worldPosition = uWorld * vec4(aPosition, 1.0);
    vWorldPosition = worldPosition.xyz;
    vNormal = normalize((uWorldInverseTranspose * vec4(aNormal, 0.0)).xyz);
    vTexCoord = aTexCoord;
    gl_Position = uViewProjection * worldPosition;
}
`;

export const STANDARD_FRAGMENT = `#version 300 es
precision highp float;

const int MAX_LIGHTS = ${MAX_LIGHTS};
const float PI = 3.14159265359;

in vec3 vWorldPosition;
in vec3 vNormal;
in vec2 vTexCoord;

uniform vec3  uCameraPosition;
uniform vec3  uAmbientColor;

uniform vec4  uAlbedoColor;
uniform float uMetallic;
uniform float uRoughness;
uniform float uEmissiveIntensity;

uniform sampler2D uAlbedoMap;
uniform bool      uHasAlbedoMap;

uniform int   uLightCount;
uniform int   uLightType[MAX_LIGHTS];      // 0 directional, 1 point, 2 spot
uniform vec3  uLightPosition[MAX_LIGHTS];
uniform vec3  uLightDirection[MAX_LIGHTS];
uniform vec3  uLightColor[MAX_LIGHTS];
uniform float uLightIntensity[MAX_LIGHTS];
uniform float uLightRange[MAX_LIGHTS];
uniform float uLightSpotCos[MAX_LIGHTS];   // cosine of the half angle

out vec4 fragColor;

// GGX normal distribution: how much of the surface faces the halfway vector.
float distributionGGX(vec3 n, vec3 h, float roughness) {
    float a = roughness * roughness;
    float a2 = a * a;
    float nDotH = max(dot(n, h), 0.0);
    float denominator = nDotH * nDotH * (a2 - 1.0) + 1.0;
    return a2 / max(PI * denominator * denominator, 1e-6);
}

// Smith geometry term with the Schlick-GGX approximation, direct-lighting k.
float geometrySchlickGGX(float nDotV, float roughness) {
    float r = roughness + 1.0;
    float k = (r * r) / 8.0;
    return nDotV / (nDotV * (1.0 - k) + k);
}

float geometrySmith(vec3 n, vec3 v, vec3 l, float roughness) {
    return geometrySchlickGGX(max(dot(n, v), 0.0), roughness)
         * geometrySchlickGGX(max(dot(n, l), 0.0), roughness);
}

vec3 fresnelSchlick(float cosTheta, vec3 f0) {
    return f0 + (1.0 - f0) * pow(clamp(1.0 - cosTheta, 0.0, 1.0), 5.0);
}

void main() {
    vec4 albedoSample = uAlbedoColor;
    if (uHasAlbedoMap) albedoSample *= texture(uAlbedoMap, vTexCoord);

    vec3 albedo = albedoSample.rgb;
    float alpha = albedoSample.a;

    vec3 n = normalize(vNormal);
    vec3 v = normalize(uCameraPosition - vWorldPosition);

    // Dielectrics reflect about 4% head-on; metals reflect their albedo.
    vec3 f0 = mix(vec3(0.04), albedo, uMetallic);

    vec3 outgoing = vec3(0.0);

    for (int i = 0; i < MAX_LIGHTS; i++) {
        if (i >= uLightCount) break;

        vec3 l;
        float attenuation = 1.0;

        if (uLightType[i] == 0) {
            l = normalize(-uLightDirection[i]);
        } else {
            vec3 toLight = uLightPosition[i] - vWorldPosition;
            float distance = length(toLight);
            l = toLight / max(distance, 1e-4);

            // Inverse-square falloff, faded to nothing at the light's range so a
            // distant light contributes exactly zero rather than a faint haze.
            float range = max(uLightRange[i], 1e-4);
            float falloff = clamp(1.0 - (distance * distance) / (range * range), 0.0, 1.0);
            attenuation = falloff * falloff / max(distance * distance, 1e-4);

            if (uLightType[i] == 2) {
                float cosAngle = dot(normalize(-l), normalize(uLightDirection[i]));
                float spotCos = uLightSpotCos[i];
                // Soften the cone edge over the outer tenth of the angle.
                attenuation *= smoothstep(spotCos, mix(spotCos, 1.0, 0.1), cosAngle);
            }
        }

        // The PI here is the counterpart of the 1/PI in the Lambertian diffuse
        // below. Without it an intensity of 1 lights a facing white surface to
        // about a third of full brightness, and every scene authored against the
        // C# engine's intensities reads as flat and dim. With it, an intensity of
        // 1 means "fully lights a surface facing this light", which is what the
        // numbers in the shipped scenes assume.
        vec3 radiance = uLightColor[i] * uLightIntensity[i] * attenuation * PI;
        if (radiance == vec3(0.0)) continue;

        vec3 h = normalize(v + l);
        float nDotL = max(dot(n, l), 0.0);

        float d = distributionGGX(n, h, uRoughness);
        float g = geometrySmith(n, v, l, uRoughness);
        vec3  f = fresnelSchlick(max(dot(h, v), 0.0), f0);

        vec3 specular = (d * g * f)
            / max(4.0 * max(dot(n, v), 0.0) * nDotL, 1e-4);

        // Energy conservation: what is not reflected specularly is diffused, and
        // metals have no diffuse term at all.
        vec3 diffuseWeight = (vec3(1.0) - f) * (1.0 - uMetallic);

        outgoing += (diffuseWeight * albedo / PI + specular) * radiance * nDotL;
    }

    // Ambient stands in for an environment map, which this renderer does not
    // have. It has to cover the specular half as well as the diffuse: a metal
    // has no diffuse term at all, so with a diffuse-only ambient it renders
    // black everywhere a light does not happen to reflect into the eye.
    vec3 ambientFresnel = fresnelSchlick(max(dot(n, v), 0.0), f0);
    vec3 ambientDiffuse = (vec3(1.0) - ambientFresnel) * (1.0 - uMetallic) * albedo;
    vec3 ambientSpecular = mix(ambientFresnel, albedo, uMetallic);

    vec3 ambient = uAmbientColor * (ambientDiffuse + ambientSpecular);
    vec3 emissive = albedo * uEmissiveIntensity;
    vec3 color = ambient + outgoing + emissive;

    // Reinhard tone mapping, then gamma. Without it the bright side of a lit
    // sphere clips to flat white as soon as intensity goes above one.
    color = color / (color + vec3(1.0));
    color = pow(color, vec3(1.0 / 2.2));

    fragColor = vec4(color, alpha);
}
`;

/**
 * The sky. Drawn as one full-screen triangle with the depth test disabled, so
 * it costs no geometry and always sits behind everything.
 */
export const SKYBOX_VERTEX = `#version 300 es
precision highp float;

out vec2 vScreenPosition;

void main() {
    // A single oversized triangle covering the viewport, from the vertex id
    // alone: no vertex buffer, no attribute state to disturb.
    vec2 position = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
    vScreenPosition = position;
    gl_Position = vec4(position * 2.0 - 1.0, 0.0, 1.0);
}
`;

export const SKYBOX_FRAGMENT = `#version 300 es
precision highp float;

in vec2 vScreenPosition;

uniform vec3  uTopColor;
uniform vec3  uBottomColor;
uniform mat4  uInverseViewProjection;
uniform vec3  uCameraPosition;
uniform float uExposure;

uniform samplerCube uCubemap;
uniform bool        uHasCubemap;

out vec4 fragColor;

void main() {
    // Unproject the far plane to recover the view ray for this pixel, so the
    // gradient follows the camera's pitch instead of the screen.
    vec4 far = uInverseViewProjection * vec4(vScreenPosition * 2.0 - 1.0, 1.0, 1.0);
    vec3 direction = normalize(far.xyz / far.w - uCameraPosition);

    vec3 color;
    if (uHasCubemap) {
        color = texture(uCubemap, direction).rgb;
    } else {
        // Remap the vertical component to 0..1 and bias towards the horizon, so
        // the sky reads as sky rather than as a linear wash.
        float t = clamp(direction.y * 0.5 + 0.5, 0.0, 1.0);
        color = mix(uBottomColor, uTopColor, pow(t, 0.65));
    }

    color *= uExposure;
    fragColor = vec4(pow(color, vec3(1.0 / 2.2)), 1.0);
}
`;

/** Unlit, single-colour shading, used for gizmos and wireframe overlays. */
export const UNLIT_VERTEX = `#version 300 es
precision highp float;

in vec3 aPosition;

uniform mat4 uWorld;
uniform mat4 uViewProjection;

void main() {
    gl_Position = uViewProjection * uWorld * vec4(aPosition, 1.0);
}
`;

export const UNLIT_FRAGMENT = `#version 300 es
precision highp float;

uniform vec4 uColor;
out vec4 fragColor;

void main() { fragColor = uColor; }
`;

/**
 * Unlit textured shading, used for world-space UI canvases.
 *
 * A canvas is painted into a 2D texture and hung on a quad, and it must arrive on screen
 * the colour it was authored -- no lighting, no tone mapping, no ambient. Straight alpha
 * comes out of the painter, so the blend mode is the caller's business and the shader
 * simply hands the sample through.
 */
export const UNLIT_TEXTURED_VERTEX = `#version 300 es
precision highp float;

in vec3 aPosition;
in vec2 aTexCoord;

uniform mat4 uWorld;
uniform mat4 uViewProjection;

out vec2 vTexCoord;

void main() {
    vTexCoord = aTexCoord;
    gl_Position = uViewProjection * uWorld * vec4(aPosition, 1.0);
}
`;

export const UNLIT_TEXTURED_FRAGMENT = `#version 300 es
precision highp float;

in vec2 vTexCoord;

uniform sampler2D uTexture;
uniform vec4 uColor;

out vec4 fragColor;

void main() {
    fragColor = texture(uTexture, vTexCoord) * uColor;
}
`;
