using System.Text.Json;

namespace SexyBiscuit.Engine.Core;

/// <summary>
/// Stands in for a component whose type could not be resolved when a scene loaded — a game
/// class that failed to compile, was renamed, or comes from an assembly that is not loaded.
/// </summary>
/// <remarks>
/// The loader used to drop such components silently, so one bad build turned every instance
/// of a project component into lost data the moment the scene was saved again. The placeholder
/// keeps the original type name and property JSON and writes them back unchanged, the way
/// Unity's "missing script" does. It does nothing at runtime.
/// </remarks>
public sealed class MissingComponent : Component
{
    /// <summary>The type name exactly as the scene file wrote it.</summary>
    public string TypeName { get; set; } = "";

    /// <summary>The property bag exactly as the scene file wrote it.</summary>
    public Dictionary<string, JsonElement>? Properties { get; set; }
}

/// <summary>
/// Remembers the actor class a scene file asked for when that class could not be resolved.
/// The actor loads as a plain <see cref="Actor"/> with all of its components, and the class
/// name is written back on save so nothing is lost.
/// </summary>
public sealed class MissingActorClass : Component
{
    public string ClassName { get; set; } = "";
}
