using SexyBiscuit.Engine.Assets;
using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Mcp;

namespace SexyBiscuit.Editor.Assistant;

/// <summary>The scene tools' view of the editor: the engine host's active scene plus <see cref="EditorState"/>.</summary>
public sealed class EditorSceneHost : IMcpSceneHost
{
    public Scene? ActiveScene => EditorApp.Instance.Engine?.SceneManager.ActiveScene;

    public void AdoptScene(Scene scene)
    {
        var engine = EditorApp.Instance.Engine ?? throw new McpToolException("The engine is not running.");
        engine.SceneManager.AdoptScene(scene);
        scene.FlushPendingActors();
        EditorState.SelectActor(null);
    }

    public string? ProjectRoot => EditorState.CurrentProject != null ? EditorState.ProjectPath : null;

    public string? CurrentScenePath
    {
        get => EditorState.CurrentScenePath;
        set => EditorState.CurrentScenePath = value;
    }

    public bool SceneDirty
    {
        get => EditorState.SceneDirty;
        set => EditorState.SceneDirty = value;
    }

    public bool IsPlaying => EditorState.IsPlaying;

    public Actor? SelectedActor => EditorState.SelectedActor;

    public void SelectActor(Actor? actor)
    {
        EditorState.SelectActor(actor);
        if (actor != null) EditorState.SelectedLayer = actor.Layer_;
    }

    public AssetManager? Assets => EditorApp.Instance.Engine?.Assets;
}
