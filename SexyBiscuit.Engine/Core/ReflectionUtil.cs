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

    private static readonly HashSet<Assembly> _retired = new();

    /// <summary>
    /// Marks an assembly as retired: a previous generation of hot-reloaded game code whose
    /// types must no longer be offered or resolved, even though the runtime may keep it
    /// loaded until its load context is collected.
    /// </summary>
    public static void RetireAssembly(Assembly assembly)
    {
        lock (_retired) _retired.Add(assembly);
    }

    public static bool IsRetired(Assembly assembly)
    {
        lock (_retired) return _retired.Contains(assembly);
    }

    /// <summary>
    /// Every assembly worth reflecting over: not dynamic, not retired. Includes assemblies
    /// loaded into collectible load contexts, which is how game code becomes visible to the
    /// scene loader and the editor.
    /// </summary>
    public static IEnumerable<Assembly> LoadedAssemblies()
        => AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !IsRetired(a));

    /// <summary>Every loadable type across every loaded, non-retired assembly.</summary>
    public static IEnumerable<Type> AllLoadedTypes()
        => LoadedAssemblies().SelectMany(SafeGetTypes);

    /// <summary>A concrete class with a public parameterless constructor — something a tool can instantiate by name.</summary>
    public static bool IsPlaceable(Type type)
        => type.IsClass && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null;

    /// <summary>
    /// Every concrete <see cref="Actor"/> class that can be constructed with no arguments,
    /// the base class included — what a "place actor" palette and the scene loader's
    /// <c>class</c> field both draw from.
    /// </summary>
    public static IEnumerable<Type> FindActorTypes()
    {
        foreach (var type in AllLoadedTypes())
        {
            if (!typeof(Actor).IsAssignableFrom(type)) continue;
            if (!IsPlaceable(type)) continue;
            yield return type;
        }
    }

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

            // Placeholders the loader creates for unresolved types are not something anyone
            // adds on purpose.
            if (type == typeof(MissingComponent) || type == typeof(MissingActorClass)) continue;

            yield return type;
        }
    }
}
