using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Mcp;

namespace SexyBiscuit.Engine.Code;

/// <summary>
/// The collectible load context one generation of game code lives in.
/// </summary>
/// <remarks>
/// Anything the host can supply is the host's: the first thing <see cref="Load"/> does is ask
/// the default context, which is what makes the game's <c>Actor</c>, <c>Component</c> and
/// <c>Vector3</c> the very same <see cref="Type"/> objects the editor uses. Checking
/// <c>Default.Assemblies</c> would not be enough — assemblies load lazily, so a package the
/// engine references may not be loaded yet even though the editor can resolve it. Only a
/// dependency the engine does not have (a NuGet package the game added) comes from the game's
/// own output folder. Files are read into memory rather than mapped, so a rebuild can overwrite
/// them on Windows while the previous generation is still loaded.
/// </remarks>
internal sealed class GameAssemblyLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver? _resolver;
    private readonly string _directory;

    public GameAssemblyLoadContext(string mainAssemblyPath, int generation)
        : base($"SexyBiscuit.Game#{generation}", isCollectible: true)
    {
        _directory = Path.GetDirectoryName(mainAssemblyPath) ?? ".";

        try
        {
            _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        }
        catch (Exception)
        {
            // No deps.json next to the DLL; fall back to sibling files by name.
            _resolver = null;
        }
    }

    protected override Assembly? Load(AssemblyName name)
    {
        try
        {
            return Default.LoadFromAssemblyName(name);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
        }

        string? path = _resolver?.ResolveAssemblyToPath(name);
        if (path == null && name.Name != null)
        {
            string sibling = Path.Combine(_directory, name.Name + ".dll");
            if (File.Exists(sibling)) path = sibling;
        }

        return path == null ? null : LoadFromBytes(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path == null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }

    public Assembly LoadFromBytes(string dllPath)
    {
        using var dll = new MemoryStream(File.ReadAllBytes(dllPath));

        string pdbPath = Path.ChangeExtension(dllPath, ".pdb");
        if (!File.Exists(pdbPath)) return LoadFromStream(dll);

        using var pdb = new MemoryStream(File.ReadAllBytes(pdbPath));
        return LoadFromStream(dll, pdb);
    }
}

/// <summary>What a loaded generation of game code contains.</summary>
public sealed record GameTypeSnapshot(
    Assembly            Assembly,
    int                 Generation,
    string              AssemblyPath,
    IReadOnlyList<Type> ActorTypes,
    IReadOnlyList<Type> ComponentTypes,
    IReadOnlyList<Type> ToolHolders,
    Guid?               EngineMvidCompiledAgainst);

/// <summary>Whether an unloaded generation was actually collected.</summary>
public sealed record UnloadReport(bool Collected, int GcPasses, int Generation);

/// <summary>
/// Loads a game assembly into the running process and unloads it again for the next build.
/// </summary>
/// <remarks>
/// Unloading is best effort: a static event subscription or a cached <see cref="Type"/> keeps a
/// generation alive, and the runtime keeps its memory until that reference goes. Correctness
/// never depends on collection — a retired assembly is filtered out of every type search — so a
/// leak costs memory and a warning, nothing else.
/// </remarks>
public sealed class GameAssemblyLoader
{
    private GameAssemblyLoadContext? _context;
    private GameTypeSnapshot?        _snapshot;
    private readonly List<int>       _leaked = new();

    public int Generation { get; private set; }

    public Assembly? Current => _snapshot?.Assembly;

    public GameTypeSnapshot? Types => _snapshot;

    public IReadOnlyList<int> LeakedGenerations => _leaked;

    public event Action<GameTypeSnapshot>? AssemblyLoaded;
    public event Action<GameTypeSnapshot>? AssemblyUnloading;

    /// <summary>Loads a built game assembly. Throws when a generation is still loaded or the file is not a .NET assembly.</summary>
    public GameTypeSnapshot Load(string dllPath)
    {
        if (_context != null) throw new InvalidOperationException("Unload the current game assembly before loading another.");
        if (!File.Exists(dllPath)) throw new FileNotFoundException("Game assembly not found.", dllPath);

        int generation = Generation + 1;
        var context    = new GameAssemblyLoadContext(dllPath, generation);
        Assembly assembly;
        try
        {
            assembly = context.LoadFromBytes(dllPath);
        }
        catch (Exception)
        {
            context.Unload();
            throw;
        }

        string xml = Path.ChangeExtension(dllPath, ".xml");
        if (File.Exists(xml)) XmlDocs.Register(assembly, xml);

        var types = assembly.SafeGetTypes().ToList();
        var snapshot = new GameTypeSnapshot(
            assembly,
            generation,
            dllPath,
            types.Where(t => typeof(Actor).IsAssignableFrom(t) && ReflectionUtil.IsPlaceable(t)).ToArray(),
            types.Where(t => typeof(Component).IsAssignableFrom(t) && ReflectionUtil.IsPlaceable(t)).ToArray(),
            types.Where(HasToolMethods).ToArray(),
            ReadEngineMvidNextTo(dllPath));

        Generation = generation;
        _context   = context;
        _snapshot  = snapshot;

        AssemblyLoaded?.Invoke(snapshot);
        return snapshot;
    }

    /// <summary>Unloads the current generation, retiring its types from every search first.</summary>
    public UnloadReport Unload(int maxGcPasses = 10)
    {
        var snapshot = _snapshot;
        if (_context == null || snapshot == null) return new UnloadReport(true, 0, Generation);

        AssemblyUnloading?.Invoke(snapshot);
        ReflectionUtil.RetireAssembly(snapshot.Assembly);
        XmlDocs.Forget(snapshot.Assembly);

        int generation = snapshot.Generation;
        _snapshot = null;
        snapshot  = null;

        var weak = ReleaseContext();
        int passes = 0;
        for (; passes < maxGcPasses && weak.IsAlive; passes++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        bool collected = !weak.IsAlive;
        if (!collected) _leaked.Add(generation);
        return new UnloadReport(collected, passes, generation);
    }

    // Separate and never inlined, so this frame holds no reference to the context or its
    // assembly while the caller's GC loop runs.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference ReleaseContext()
    {
        var context = _context!;
        _context = null;
        var weak = new WeakReference(context, trackResurrection: true);
        context.Unload();
        return weak;
    }

    public bool IsCurrentType(Type type) => _snapshot != null && type.Assembly == _snapshot.Assembly;

    private static bool HasToolMethods(Type type)
        => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
               .Any(m => m.IsDefined(typeof(McpToolAttribute), inherit: false));

    private static Guid? ReadEngineMvidNextTo(string dllPath)
    {
        string engine = Path.Combine(Path.GetDirectoryName(dllPath) ?? ".", "SexyBiscuit.Engine.dll");
        return File.Exists(engine) ? AssemblyIdentity.TryReadMvid(engine) : null;
    }
}
