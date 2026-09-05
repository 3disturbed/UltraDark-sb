using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace SexyBiscuit.Editor;

/// <summary>
/// The subset of Dear ImGui's DockBuilder API needed to construct a docking layout in
/// code.
/// </summary>
/// <remarks>
/// <para>
/// DockBuilder lives in <c>imgui_internal.h</c>, so ImGui.NET's generated bindings do not
/// cover it — but the native <c>cimgui</c> the package ships does export the C entry
/// points. These declarations bind to those directly, which is how a C# editor builds a
/// default layout rather than shipping a hand-written <c>imgui.ini</c> whose node IDs
/// would have to be kept in sync by hand.
/// </para>
/// <para>
/// The API is "internal" in the sense of unstable across ImGui versions, not private. It
/// is pinned here to the ImGui.NET version in the project file; a package bump is the one
/// thing that could break it, and the failure is loud (a missing-entry-point exception at
/// startup) rather than silent.
/// </para>
/// </remarks>
internal static class ImGuiDock
{
    private const string Lib = "cimgui";

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint igDockBuilderAddNode(uint nodeId, ImGuiDockNodeFlags flags);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void igDockBuilderRemoveNode(uint nodeId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void igDockBuilderSetNodeSize(uint nodeId, Vector2 size);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void igDockBuilderSetNodePos(uint nodeId, Vector2 pos);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint igDockBuilderSplitNode(
        uint nodeId, ImGuiDir splitDir, float sizeRatioForNodeAtDir,
        out uint outIdAtDir, out uint outIdAtOppositeDir);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void igDockBuilderDockWindow(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string windowName, uint nodeId);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void igDockBuilderFinish(uint nodeId);

    /// <summary>Creates a fresh dock node, discarding whatever was under that id.</summary>
    public static uint AddNode(uint nodeId, ImGuiDockNodeFlags flags = ImGuiDockNodeFlags.None)
        => igDockBuilderAddNode(nodeId, flags);

    /// <summary>Removes a node and everything docked in it.</summary>
    public static void RemoveNode(uint nodeId) => igDockBuilderRemoveNode(nodeId);

    /// <summary>Sets a node's size. Only meaningful on the root before splitting.</summary>
    public static void SetNodeSize(uint nodeId, Vector2 size) => igDockBuilderSetNodeSize(nodeId, size);

    /// <summary>Sets a node's position. Only meaningful on the root.</summary>
    public static void SetNodePos(uint nodeId, Vector2 pos) => igDockBuilderSetNodePos(nodeId, pos);

    /// <summary>
    /// Splits a node in two and returns the id of the half on <paramref name="direction"/>.
    /// </summary>
    /// <param name="nodeId">Node to split. Becomes the parent of both halves.</param>
    /// <param name="direction">Which side the new node occupies.</param>
    /// <param name="ratio">Fraction of the parent the new node takes, 0 to 1.</param>
    /// <param name="remainder">Receives the id of the other half.</param>
    /// <remarks>
    /// The parent id is reused by ImGui for one of the halves, so always keep the two ids
    /// this returns and split those — reusing the original id after a split docks windows
    /// into the wrong place.
    /// </remarks>
    public static uint Split(uint nodeId, ImGuiDir direction, float ratio, out uint remainder)
    {
        // The return value and out_id_at_dir are the same id; take the out parameters so
        // both halves come from one place.
        igDockBuilderSplitNode(nodeId, direction, ratio, out uint atDirection, out uint opposite);
        remainder = opposite;
        return atDirection;
    }

    /// <summary>Docks a window, by title, into a node.</summary>
    public static void DockWindow(string windowName, uint nodeId)
        => igDockBuilderDockWindow(windowName, nodeId);

    /// <summary>Commits the layout. Call once after all splits and docks.</summary>
    public static void Finish(uint nodeId) => igDockBuilderFinish(nodeId);
}
