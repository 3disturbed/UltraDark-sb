namespace SexyBiscuit.Engine.Core;

/// <summary>
/// Keeps a public read/write property out of scene files. For editor-facing proxies of state
/// that is already saved another way — a material colour exposed on a renderer for the
/// inspector, say, when the material itself is serialised.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SceneIgnoreAttribute : Attribute
{
}
