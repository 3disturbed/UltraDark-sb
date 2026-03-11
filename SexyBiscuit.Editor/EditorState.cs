using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Editor;

public enum GizmoMode
{
    Translate,
    Rotate,
    Scale,
}

/// <summary>
/// Central shared state for all editor panels. No panel logic lives here —
/// this is purely a data bus.
/// </summary>
public static class EditorState
{
    // -------------------------------------------------------------------------
    // Selection
    // -------------------------------------------------------------------------
    public static Actor? SelectedActor { get; private set; }
    public static Layer? SelectedLayer { get; set; }
    public static string? SelectedAssetPath { get; set; }

    public static event Action<Actor?>? OnSelectionChanged;

    public static void SelectActor(Actor? actor)
    {
        SelectedActor = actor;
        OnSelectionChanged?.Invoke(actor);
    }

    // -------------------------------------------------------------------------
    // Play mode
    // -------------------------------------------------------------------------
    public static bool IsPlaying     { get; set; }
    public static bool IsPlayPaused  { get; set; }

    // -------------------------------------------------------------------------
    // Gizmo
    // -------------------------------------------------------------------------
    public static GizmoMode GizmoMode { get; set; } = GizmoMode.Translate;

    // -------------------------------------------------------------------------
    // Project
    // -------------------------------------------------------------------------
    public static string ProjectPath { get; set; } = Directory.GetCurrentDirectory();
}
