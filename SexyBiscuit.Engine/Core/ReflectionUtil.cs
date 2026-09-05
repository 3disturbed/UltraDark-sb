using System.Reflection;

namespace SexyBiscuit.Engine.Core;

/// <summary>
/// Reflection helpers that survive a partially loadable assembly.
/// </summary>
public static class ReflectionUtil
{
    /// <summary>
    /// Returns every type in an assembly, keeping the ones that loaded when some did not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Assembly.GetTypes"/> throws <see cref="ReflectionTypeLoadException"/>
    /// when any single type's dependencies are missing — and the engine references
    /// optional integrations like Steamworks.NET that are frequently absent at runtime.
    /// One such type takes down the call for the entire assembly.
    /// </para>
    /// <para>
    /// The obvious guard, <c>catch { return Array.Empty&lt;Type&gt;(); }</c>, is worse than
    /// the exception: it silently discards every type that loaded perfectly well. That is
    /// how the editor's Add Component list came up empty — 392 usable types thrown away to
    /// avoid one that was not. The exception carries the partial list, so use it.
    /// </para>
    /// </remarks>
    public static IEnumerable<Type> SafeGetTypes(this Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t != null)!;
        }
        catch
        {
            // A dynamic or otherwise unenumerable assembly. Nothing to offer.
            return Array.Empty<Type>();
        }
    }

    /// <summary>Every loadable type across every loaded assembly.</summary>
    public static IEnumerable<Type> AllLoadedTypes()
        => AppDomain.CurrentDomain.GetAssemblies().SelectMany(SafeGetTypes);

    /// <summary>
    /// Every concrete <see cref="Component"/> that can be constructed with no arguments —
    /// what an editor's Add Component list and a scene loader both need.
    /// </summary>
    /// <param name="includeBuiltInTransform">
    /// The 2D <see cref="Transform"/> is excluded by default because every actor already
    /// has one and a second is never what anyone means. <see cref="Transform3D"/> is not
    /// excluded — it is not automatic, and adding one is how a 2D actor becomes a 3D one.
    /// </param>
    public static IEnumerable<Type> FindComponentTypes(bool includeBuiltInTransform = false)
    {
        foreach (var type in AllLoadedTypes())
        {
            if (!type.IsClass || type.IsAbstract) continue;
            if (!typeof(Component).IsAssignableFrom(type)) continue;
            if (type.GetConstructor(Type.EmptyTypes) == null) continue;
            if (!includeBuiltInTransform && type == typeof(Transform)) continue;

            yield return type;
        }
    }
}
