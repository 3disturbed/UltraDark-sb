using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Assets;

/// <summary>
/// One asset to load, paired with the type to load it as.
/// </summary>
public sealed class AssetRequest
{
    /// <summary>Path passed to <see cref="AssetManager"/>.</summary>
    public string Path { get; init; } = "";

    /// <summary>The type to load the asset as.</summary>
    public Type Type { get; init; } = typeof(object);

    /// <summary>
    /// Relative cost of this asset, used to weight the progress bar. Leave at 1 unless
    /// you know one asset dwarfs the others — a 200 MB video against a dozen icons.
    /// </summary>
    public float Weight { get; init; } = 1f;

    /// <summary>Creates a request for a typed asset.</summary>
    public static AssetRequest For<T>(string path, float weight = 1f) where T : class
        => new() { Path = path, Type = typeof(T), Weight = weight };
}

/// <summary>
/// Loads a batch of assets and reports weighted progress, for driving a loading bar.
/// </summary>
/// <remarks>
/// <para>
/// Progress is reported and completion callbacks are raised from the game thread rather
/// than the loading thread. Asset loading itself touches the GPU (texture upload), so it
/// cannot simply be moved to a background thread; instead the batch is loaded a few items
/// per frame, which keeps the window responsive and the bar animating without any
/// cross-thread marshalling in game code.
/// </para>
/// <para>
/// <see cref="ItemsPerFrame"/> is the trade: higher finishes sooner, lower keeps the frame
/// rate up so the loading screen itself stays smooth.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var loader = new LoadingScreen(SBEngine.Instance.Assets);
/// loader.Add(AssetRequest.For&lt;Texture2D&gt;("Assets/atlas.png", weight: 4f));
/// loader.Add(AssetRequest.For&lt;SoundEffect&gt;("Assets/hit.wav"));
///
/// loader.ProgressChanged.Add(p =&gt; bar.Value = p);
/// loader.Completed.Add(() =&gt; SceneManager.LoadScene("Assets/Scenes/Level1.json"));
/// loader.Start();
///
/// // In your update:
/// loader.Tick();
/// </code>
/// </example>
public sealed class LoadingScreen
{
    private readonly Action<AssetRequest> _loadOne;
    private readonly List<AssetRequest>   _queue = new();

    private int   _index;
    private float _completedWeight;
    private float _totalWeight;

    /// <summary>Assets loaded per <see cref="Tick"/> call. Higher finishes sooner but hitches more.</summary>
    public int ItemsPerFrame { get; set; } = 2;

    /// <summary>Weighted completion in 0..1.</summary>
    public float Progress => _totalWeight <= 0f ? 1f : SBMath.Clamp01(_completedWeight / _totalWeight);

    /// <summary>True between <see cref="Start"/> and completion.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>True once every queued asset has been attempted.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Paths that failed to load, with the reason.</summary>
    public IReadOnlyList<(string path, string error)> Failures => _failures;
    private readonly List<(string path, string error)> _failures = new();

    /// <summary>Raised each time progress advances, with the new 0..1 value.</summary>
    public SBEvent<float> ProgressChanged { get; } = new();

    /// <summary>Raised as each asset finishes, with its path. Useful for a status line.</summary>
    public SBEvent<string> AssetLoaded { get; } = new();

    /// <summary>Raised once when the whole batch is done, successes and failures alike.</summary>
    public SBEvent Completed { get; } = new();

    /// <summary>Loads through the given <see cref="AssetManager"/>.</summary>
    public LoadingScreen(AssetManager assets)
        : this(MakeAssetManagerLoader(assets ?? throw new ArgumentNullException(nameof(assets)))) { }

    /// <summary>
    /// Loads through a custom action, for assets that do not come from
    /// <see cref="AssetManager"/> — a bundle, a download, a procedural generator.
    /// </summary>
    /// <param name="loadOne">
    /// Loads one request. Throw to report a failure; the message is recorded in
    /// <see cref="Failures"/> and the batch carries on.
    /// </param>
    public LoadingScreen(Action<AssetRequest> loadOne)
        => _loadOne = loadOne ?? throw new ArgumentNullException(nameof(loadOne));

    /// <summary>
    /// Builds the default loader: a reflected call to <see cref="AssetManager.Load{T}"/>,
    /// so the caller does not need the asset type at the call site.
    /// </summary>
    private static Action<AssetRequest> MakeAssetManagerLoader(AssetManager assets) => request =>
    {
        var method = typeof(AssetManager)
            .GetMethod(nameof(AssetManager.Load))!
            .MakeGenericMethod(request.Type);

        method.Invoke(assets, new object[] { request.Path });
    };

    /// <summary>Queues one asset. Call before <see cref="Start"/>.</summary>
    public LoadingScreen Add(AssetRequest request)
    {
        _queue.Add(request);
        return this;
    }

    /// <summary>Queues a typed asset.</summary>
    public LoadingScreen Add<T>(string path, float weight = 1f) where T : class
        => Add(AssetRequest.For<T>(path, weight));

    /// <summary>Queues every asset listed in a manifest file, one <c>Type|path</c> per line.</summary>
    /// <remarks>
    /// Blank lines and lines starting with <c>#</c> are ignored, so a manifest can carry
    /// comments. An unresolvable type name is recorded as a failure rather than throwing,
    /// so one stale line does not abort the whole load.
    /// </remarks>
    public LoadingScreen AddManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            _failures.Add((manifestPath, "manifest not found"));
            return this;
        }

        foreach (var raw in File.ReadAllLines(manifestPath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split('|', 2);
            if (parts.Length != 2)
            {
                _failures.Add((line, "expected 'Type|path'"));
                continue;
            }

            var type = ResolveType(parts[0].Trim());
            if (type == null)
            {
                _failures.Add((line, $"unknown type '{parts[0].Trim()}'"));
                continue;
            }

            _queue.Add(new AssetRequest { Path = parts[1].Trim(), Type = type });
        }

        return this;
    }

    /// <summary>
    /// Resolves a manifest type name: assembly-qualified first, then a full name in any
    /// loaded assembly, then a bare type name as a last resort.
    /// </summary>
    /// <remarks>
    /// Enumerating an assembly's types can throw <see cref="System.Reflection.ReflectionTypeLoadException"/>
    /// when it references something that is not present — Steamworks.NET in a headless
    /// test host, for instance. The partial type list on the exception is still usable, so
    /// the search continues rather than failing the whole manifest over an unrelated
    /// assembly. Cheap lookups are tried first so the expensive enumeration is usually
    /// skipped entirely.
    /// </remarks>
    private static Type? ResolveType(string name)
    {
        var direct = Type.GetType(name);
        if (direct != null) return direct;

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();

        foreach (var assembly in assemblies)
        {
            var byFullName = assembly.GetType(name, throwOnError: false);
            if (byFullName != null) return byFullName;
        }

        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.SafeGetTypes())
            {
                if (string.Equals(type.Name, name, StringComparison.Ordinal))
                    return type;
            }
        }

        return null;
    }

    /// <summary>Begins loading. Call <see cref="Tick"/> each frame afterwards.</summary>
    public void Start()
    {
        _index           = 0;
        _completedWeight = 0f;
        _totalWeight     = _queue.Sum(r => MathF.Max(0.0001f, r.Weight));
        IsLoading        = _queue.Count > 0;
        IsComplete       = _queue.Count == 0;

        if (IsComplete) Completed.Broadcast();
    }

    /// <summary>
    /// Loads up to <see cref="ItemsPerFrame"/> assets. Call once per frame while
    /// <see cref="IsLoading"/> is true.
    /// </summary>
    public void Tick()
    {
        if (!IsLoading) return;

        for (int i = 0; i < ItemsPerFrame && _index < _queue.Count; i++, _index++)
        {
            var request = _queue[_index];

            try
            {
                _loadOne(request);
                AssetLoaded.Broadcast(request.Path);
            }
            catch (Exception ex)
            {
                _failures.Add((request.Path, (ex.InnerException ?? ex).Message));
            }

            _completedWeight += MathF.Max(0.0001f, request.Weight);
        }

        ProgressChanged.Broadcast(Progress);

        if (_index < _queue.Count) return;

        IsLoading  = false;
        IsComplete = true;
        Completed.Broadcast();
    }

    /// <summary>Abandons the remaining queue. Already-loaded assets stay loaded.</summary>
    public void Cancel()
    {
        IsLoading = false;
        _queue.Clear();
    }
}
