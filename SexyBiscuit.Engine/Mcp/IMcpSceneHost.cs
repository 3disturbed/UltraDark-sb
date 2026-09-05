using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Engine.Mcp;

/// <summary>
/// What the scene tools need from whoever owns the scene — the editor, or a headless host in
/// tests and the self-test.
/// </summary>
public interface IMcpSceneHost
{
    Core.Scene? ActiveScene { get; }

    /// <summary>Replaces the active scene, destroying the previous one and flushing the new one.</summary>
    void AdoptScene(Core.Scene scene);

    /// <summary>The open project's root, or null when none is open.</summary>
    string? ProjectRoot { get; }

    /// <summary>Where the active scene was loaded from or last saved, relative to the root.</summary>
    string? CurrentScenePath { get; set; }

    bool SceneDirty { get; set; }

    bool IsPlaying { get; }

    Actor? SelectedActor { get; }

    void SelectActor(Actor? actor);

    /// <summary>The running host's asset manager; null headless.</summary>
    Assets.AssetManager? Assets { get; }
}

/// <summary>Owns one scene with no editor around it. Disposing destroys the scene, which clears the static component registries.</summary>
public sealed class HeadlessSceneHost : IMcpSceneHost, IDisposable
{
    public HeadlessSceneHost(Core.Scene? initial = null, string? projectRoot = null)
    {
        ActiveScene = initial ?? new Core.Scene("Untitled");
        ActiveScene.FlushPendingActors();
        ProjectRoot = projectRoot;
    }

    public Core.Scene? ActiveScene { get; private set; }

    public void AdoptScene(Core.Scene scene)
    {
        if (ReferenceEquals(scene, ActiveScene)) return;

        ActiveScene?.Destroy();
        ActiveScene = scene;
        scene.FlushPendingActors();
        SelectedActor = null;
    }

    public string? ProjectRoot       { get; set; }
    public string? CurrentScenePath  { get; set; }
    public bool    SceneDirty        { get; set; }
    public bool    IsPlaying         { get; set; }
    public Actor?  SelectedActor     { get; private set; }

    public void SelectActor(Actor? actor) => SelectedActor = actor;

    public Assets.AssetManager? Assets => null;

    public void Dispose()
    {
        ActiveScene?.Destroy();
        ActiveScene = null;
    }
}
