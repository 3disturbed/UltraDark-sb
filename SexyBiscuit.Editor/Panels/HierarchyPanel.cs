using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Editor.Panels;

/// <summary>
/// Hierarchy panel — displays Scene → Layers → Actors as a tree.
/// Supports selection, drag-drop between layers, context menus, and
/// inline visibility toggles.
/// </summary>
public sealed class HierarchyPanel
{
    // Drag-drop payload type tag
    private const string DragDropType = "ACTOR_DRAG";

    // State for rename-in-place
    private Actor?  _renamingActor;
    private byte[]  _renameBuffer = new byte[256];

    // Pending operations (deferred out of the tree traversal)
    private Actor?  _pendingDelete;
    private Actor?  _pendingDuplicate;
    private Actor?  _pendingAddActorToLayer;
    private Layer?  _pendingAddActorLayer;
    private Layer?  _pendingAddLayer;
    private string? _pendingAddLayerName;
    private Actor?  _draggedActor;
    private Layer?  _dropTargetLayer;

    // -------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------

    public void Draw(Scene scene)
    {
        if (!ImGui.Begin("World Outliner"))
        {
            ImGui.End();
            return;
        }

        // Toolbar: "+" new actor button
        if (ImGui.Button("+  Actor"))
        {
            var targetLayer = EditorState.SelectedLayer ?? scene.GetLayer("default") ?? scene.Layers.FirstOrDefault();
            if (targetLayer != null)
            {
                var actor = new Actor("Actor") { };
                targetLayer.AddActor(actor);
                EditorState.SelectActor(actor);
                ConsoleLog.Add($"Added actor to layer '{targetLayer.Name}'.", LogLevel.Info);
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("+  Layer"))
        {
            ImGui.OpenPopup("##AddLayerPopup");
        }

        // "Add Layer" name popup
        if (ImGui.BeginPopup("##AddLayerPopup"))
        {
            ImGui.Text("Layer name:");
            ImGui.SetNextItemWidth(180f);
            var layerNameBuf = new byte[128];
            if (_pendingAddLayerName != null)
            {
                var encoded = Encoding.UTF8.GetBytes(_pendingAddLayerName);
                Buffer.BlockCopy(encoded, 0, layerNameBuf, 0, Math.Min(encoded.Length, layerNameBuf.Length - 1));
            }
            if (ImGui.InputText("##NewLayerName", layerNameBuf, (uint)layerNameBuf.Length))
            {
                _pendingAddLayerName = Encoding.UTF8.GetString(layerNameBuf).TrimEnd('\0');
            }
            if (ImGui.Button("Create") && !string.IsNullOrWhiteSpace(_pendingAddLayerName))
            {
                scene.AddLayer(_pendingAddLayerName);
                ConsoleLog.Add($"Added layer '{_pendingAddLayerName}'.", LogLevel.Info);
                _pendingAddLayerName = null;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel")) { _pendingAddLayerName = null; ImGui.CloseCurrentPopup(); }
            ImGui.EndPopup();
        }

        ImGui.Separator();

        // Scene root label
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.9f, 0.85f, 0.4f, 1f));
        bool sceneOpen = ImGui.TreeNodeEx($"[Scene] {scene.Name}",
            ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.SpanFullWidth);
        ImGui.PopStyleColor();

        if (sceneOpen)
        {
            foreach (var layer in scene.Layers)
                DrawLayer(layer, scene);
            ImGui.TreePop();
        }

        // ----- Deferred operations -----
        ExecutePendingOps(scene);

        ImGui.End();
    }

    // -------------------------------------------------------------------------
    // Draw layer
    // -------------------------------------------------------------------------

    private void DrawLayer(Layer layer, Scene scene)
    {
        ImGui.PushID(layer.Name);

        // Visibility checkbox for the layer
        bool layerVisible = layer.Visible;
        if (ImGui.Checkbox("##vis", ref layerVisible))
            layer.Visible = layerVisible;
        ImGui.SameLine();

        var layerFlags = ImGuiTreeNodeFlags.DefaultOpen
                       | ImGuiTreeNodeFlags.SpanFullWidth
                       | ImGuiTreeNodeFlags.OpenOnArrow;

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.85f, 1.0f, 1f));
        bool layerOpen = ImGui.TreeNodeEx($"[Layer] {layer.Name} ({layer.Actors.Count})", layerFlags);
        ImGui.PopStyleColor();

        // Layer context menu
        if (ImGui.BeginPopupContextItem("##LayerCtx"))
        {
            if (ImGui.MenuItem("Add Actor"))
            {
                var actor = new Actor("Actor");
                layer.AddActor(actor);
                EditorState.SelectActor(actor);
                EditorState.SelectedLayer = layer;
            }
            ImGui.Separator();
            if (ImGui.MenuItem("Delete Layer"))
                scene.RemoveLayer(layer.Name);
            ImGui.EndPopup();
        }

        // Drop target: actors dropped onto a layer header
        if (ImGui.BeginDragDropTarget())
        {
            unsafe
            {
                var payload = ImGui.AcceptDragDropPayload(DragDropType);
                if (payload.NativePtr != null && _draggedActor != null && _draggedActor.Layer_ != layer)
                {
                    // MoveActor, not RemoveActor + AddActor: the removal queue destroys the
                    // actor, which is how dragging a cube between layers used to lose its mesh.
                    scene.MoveActor(_draggedActor, layer.Name, layer.Order);
                    ConsoleLog.Add($"Moved '{_draggedActor.Name}' to layer '{layer.Name}'.", LogLevel.Info);
                    _draggedActor = null;
                }
            }
            ImGui.EndDragDropTarget();
        }

        if (layerOpen)
        {
            foreach (var actor in layer.Actors.ToList())
                DrawActorRow(actor);
            ImGui.TreePop();
        }

        ImGui.PopID();
    }

    // -------------------------------------------------------------------------
    // Draw actor row
    // -------------------------------------------------------------------------

    private void DrawActorRow(Actor actor)
    {
        ImGui.PushID((int)actor.Id);

        bool isSelected = EditorState.SelectedActor == actor;

        // Inline visibility checkbox
        bool active = actor.IsActive;
        if (ImGui.Checkbox("##active", ref active))
            actor.IsActive = active;
        ImGui.SameLine();

        // Actor leaf node — support inline rename
        if (_renamingActor == actor)
        {
            ImGui.SetNextItemWidth(180f);
            if (ImGui.InputText("##rename", _renameBuffer, (uint)_renameBuffer.Length,
                ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll))
            {
                actor.Name = Encoding.UTF8.GetString(_renameBuffer).TrimEnd('\0');
                _renamingActor = null;
            }
            if (!ImGui.IsItemActive() && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                _renamingActor = null;
        }
        else
        {
            var nodeFlags = ImGuiTreeNodeFlags.Leaf
                          | ImGuiTreeNodeFlags.SpanFullWidth
                          | ImGuiTreeNodeFlags.NoTreePushOnOpen;
            if (isSelected)
                nodeFlags |= ImGuiTreeNodeFlags.Selected;

            if (!actor.IsActive)
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1f));

            ImGui.TreeNodeEx(actor.Name, nodeFlags);

            if (!actor.IsActive)
                ImGui.PopStyleColor();

            // Selection
            if (ImGui.IsItemClicked(ImGuiMouseButton.Left))
            {
                EditorState.SelectActor(actor);
                EditorState.SelectedLayer = actor.Layer_;
            }

            // Double-click to rename
            if (ImGui.IsItemHovered() && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
            {
                _renamingActor = actor;
                _renameBuffer = new byte[256];
                var encoded = Encoding.UTF8.GetBytes(actor.Name);
                Buffer.BlockCopy(encoded, 0, _renameBuffer, 0, Math.Min(encoded.Length, _renameBuffer.Length - 1));
            }

            // Drag source
            if (ImGui.BeginDragDropSource(ImGuiDragDropFlags.SourceAllowNullID))
            {
                _draggedActor = actor;
                // Store actor id as payload
                uint actorId = actor.Id;
                unsafe
                {
                    ImGui.SetDragDropPayload(DragDropType, (IntPtr)(&actorId), sizeof(uint));
                }
                ImGui.Text($"Moving: {actor.Name}");
                ImGui.EndDragDropSource();
            }

            // Context menu
            if (ImGui.BeginPopupContextItem("##ActorCtx"))
            {
                if (ImGui.MenuItem("Rename"))
                {
                    _renamingActor = actor;
                    _renameBuffer = new byte[256];
                    var encoded = Encoding.UTF8.GetBytes(actor.Name);
                    Buffer.BlockCopy(encoded, 0, _renameBuffer, 0, Math.Min(encoded.Length, _renameBuffer.Length - 1));
                }
                if (ImGui.MenuItem("Duplicate"))
                    _pendingDuplicate = actor;
                ImGui.Separator();
                if (ImGui.MenuItem("Delete"))
                    _pendingDelete = actor;
                ImGui.EndPopup();
            }
        }

        ImGui.PopID();
    }

    // -------------------------------------------------------------------------
    // Deferred operations
    // -------------------------------------------------------------------------

    private void ExecutePendingOps(Scene scene)
    {
        if (_pendingDelete != null)
        {
            if (EditorState.SelectedActor == _pendingDelete)
                EditorState.SelectActor(null);
            _pendingDelete.Layer_?.RemoveActor(_pendingDelete);
            ConsoleLog.Add($"Deleted actor '{_pendingDelete.Name}'.", LogLevel.Info);
            _pendingDelete = null;
        }

        if (_pendingDuplicate != null)
        {
            var src = _pendingDuplicate;
            var copy = new Actor(src.Name + " (copy)")
            {
                Tag      = src.Tag,
                IsActive = src.IsActive,
            };
            copy.Transform.LocalPosition = src.Transform.LocalPosition + new Microsoft.Xna.Framework.Vector2(8f, 8f);
            copy.Transform.LocalRotation = src.Transform.LocalRotation;
            copy.Transform.LocalScale    = src.Transform.LocalScale;
            src.Layer_?.AddActor(copy);
            EditorState.SelectActor(copy);
            ConsoleLog.Add($"Duplicated '{src.Name}'.", LogLevel.Info);
            _pendingDuplicate = null;
        }
    }
}
