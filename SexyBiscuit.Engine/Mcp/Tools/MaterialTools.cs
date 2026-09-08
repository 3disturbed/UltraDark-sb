using System.Text.Json.Nodes;
using Microsoft.Xna.Framework;
using SexyBiscuit.Engine.Rendering;
using static SexyBiscuit.Engine.Mcp.SceneToolSupport;

namespace SexyBiscuit.Engine.Mcp.Tools;

/// <summary>Materials on mesh renderers. Persist in the scene file; textures resolve when a host is running.</summary>
public sealed class MaterialTools
{
    private readonly IMcpSceneHost _host;

    public MaterialTools(IMcpSceneHost host) => _host = host;

    [McpTool("set_material",
        "Set a MeshRenderer material: albedo colour, metallic 0-1, roughness 0-1, emissive intensity, and texture paths " +
        "relative to the project root. Only the given fields change. The material is saved with the scene.",
        Mutating = true, Label = "Set material on {actor}")]
    public McpToolResult SetMaterial(
        [McpParam("Actor id or name")] string actor,
        [McpParam("'#RRGGBB', '#RRGGBBAA' or a colour name")] Color? albedoColor = null,
        [McpParam("0 = dielectric, 1 = metal")] float? metallic = null,
        [McpParam("0 = mirror, 1 = matte")] float? roughness = null,
        [McpParam("0 = none; above 1 glows")] float? emissiveIntensity = null,
        [McpParam("Project-relative texture path, or empty string to clear")] string? albedoTexture = null,
        [McpParam("Project-relative normal map path, or empty string to clear")] string? normalTexture = null,
        [McpParam("Material slot; models may have several")] int materialIndex = 0)
    {
        var (target, mesh) = RequireMesh(actor);
        if (materialIndex < 0 || materialIndex > 15)
            throw new McpToolException("materialIndex must be between 0 and 15.");

        var material = mesh.EnsureOwnMaterial(materialIndex);

        if (albedoColor.HasValue)       material.AlbedoColor       = albedoColor.Value;
        if (metallic.HasValue)          material.Metallic          = Math.Clamp(metallic.Value, 0f, 1f);
        if (roughness.HasValue)         material.Roughness         = Math.Clamp(roughness.Value, 0f, 1f);
        if (emissiveIntensity.HasValue) material.EmissiveIntensity = Math.Max(0f, emissiveIntensity.Value);
        if (albedoTexture != null)      material.AlbedoMapPath     = albedoTexture.Length == 0 ? null : albedoTexture;
        if (normalTexture != null)      material.NormalMapPath     = normalTexture.Length == 0 ? null : normalTexture;

        if (albedoTexture != null || normalTexture != null)
        {
            if (albedoTexture is { Length: 0 }) material.AlbedoMap = null;
            if (normalTexture is { Length: 0 }) material.NormalMap = null;
            material.ResolveTextures();
        }

        return McpToolResult.Json(MaterialView(target.Name, materialIndex, material), $"Updated material {materialIndex} on '{target.Name}'.");
    }

    [McpTool("get_material", "Read a MeshRenderer material.", ReadOnly = true)]
    public McpToolResult GetMaterial(
        [McpParam("Actor id or name")] string actor,
        [McpParam("Material slot")] int materialIndex = 0)
    {
        var (target, mesh) = RequireMesh(actor);
        var material = materialIndex < mesh.Materials.Count ? mesh.Materials[materialIndex] : Material3D.Default;

        var view = MaterialView(target.Name, materialIndex, material);
        view["isSharedDefault"] = ReferenceEquals(material, Material3D.Default);
        view["materialCount"]   = mesh.Materials.Count;
        return McpToolResult.Json(view);
    }

    private (Core.Actor actor, MeshRenderer mesh) RequireMesh(string actor)
    {
        var scene  = RequireScene(_host);
        var target = ActorRef.Resolve(scene, actor);
        var mesh   = target.GetComponent<MeshRenderer>()
            ?? throw new McpToolException($"'{target.Name}' has no MeshRenderer.", "Materials belong to MeshRenderer components; add one first.");
        return (target, mesh);
    }

    internal static JsonObject MaterialView(string actorName, int index, Material3D m) => new()
    {
        ["actor"]             = actorName,
        ["index"]             = index,
        ["albedoColor"]       = ValueConverter.ToJson(m.AlbedoColor),
        ["metallic"]          = m.Metallic,
        ["roughness"]         = m.Roughness,
        ["emissiveIntensity"] = m.EmissiveIntensity,
        ["albedoMap"]         = m.AlbedoMapPath,
        ["normalMap"]         = m.NormalMapPath,
        ["metallicMap"]       = m.MetallicMapPath,
        ["roughnessMap"]      = m.RoughnessMapPath,
        ["emissiveMap"]       = m.EmissiveMapPath,
        ["texturesLoaded"]    = m.AlbedoMap != null || m.NormalMap != null,
    };
}
