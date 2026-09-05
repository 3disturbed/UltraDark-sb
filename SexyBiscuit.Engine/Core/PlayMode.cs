namespace SexyBiscuit.Engine.Core;

/// <summary>
/// Whether gameplay is running. True in a standalone game; the editor turns it off outside
/// play mode, so actors that start in an edit-time scene (a loaded level, an undo, a hot reload)
/// do not run gameplay side effects such as a <see cref="Gameplay.GameMode"/> spawning players.
/// </summary>
/// <remarks>
/// Components still Awake and Start in edit mode so they can render and cache what they need;
/// only behaviour that changes the world belongs behind this flag. The editor sets it true just
/// before it loads the play scene and false again before it restores the edit scene.
/// </remarks>
public static class PlayMode
{
    public static bool IsActive { get; set; } = true;
}
