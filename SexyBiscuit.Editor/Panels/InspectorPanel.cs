using System.Numerics;
using System.Reflection;
using System.Text;
using ImGuiNET;
using SexyBiscuit.Engine.Core;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaVector2 = Microsoft.Xna.Framework.Vector2;

namespace SexyBiscuit.Editor.Panels;

/// <summary>
/// Inspector panel — shows and edits the properties of the selected actor.
/// </summary>
public sealed class InspectorPanel
{
    // Buffers for text inputs
    private byte[] _nameBuf  = new byte[256];
    private byte[] _tagBuf   = new byte[128];

    // "Add Component" popup state
    private Type[]? _componentTypes;
    private string  _componentFilter = "";
    private byte[]  _componentFilterBuf = new byte[128];

    // -------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------

    public void Draw()
    {
        if (!ImGui.Begin("Inspector"))
        {
            ImGui.End();
            return;
        }

        var actor = EditorState.SelectedActor;

        if (actor == null)
        {
            ImGui.TextDisabled("No selection");
            ImGui.End();
            return;
        }

        DrawActorHeader(actor);
        ImGui.Separator();
        DrawTransformSection(actor.Transform);
        ImGui.Separator();
        DrawComponentList(actor);
        ImGui.Separator();
        DrawAddComponentButton(actor);

        ImGui.End();
    }

    // -------------------------------------------------------------------------
    // Actor header
    // -------------------------------------------------------------------------

    private void DrawActorHeader(Actor actor)
    {
        // Name
        ImGui.Text("Name:");
        ImGui.SameLine();
        EncodeToBuffer(actor.Name, _nameBuf);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##actorName", _nameBuf, (uint)_nameBuf.Length))
            actor.Name = DecodeBuffer(_nameBuf);

        // Tag
        ImGui.Text("Tag: ");
        ImGui.SameLine();
        EncodeToBuffer(actor.Tag, _tagBuf);
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##actorTag", _tagBuf, (uint)_tagBuf.Length))
            actor.Tag = DecodeBuffer(_tagBuf);

        // IsActive
        bool isActive = actor.IsActive;
        if (ImGui.Checkbox("Active", ref isActive))
            actor.IsActive = isActive;
    }

    // -------------------------------------------------------------------------
    // Transform section
    // -------------------------------------------------------------------------

    private void DrawTransformSection(Transform t)
    {
        if (!ImGui.CollapsingHeader("Transform", ImGuiTreeNodeFlags.DefaultOpen)) return;

        // Position
        var pos = new Vector2(t.LocalPosition.X, t.LocalPosition.Y);
        ImGui.Text("Position");
        ImGui.SameLine(80f);
        ImGui.SetNextItemWidth(80f);
        if (ImGui.DragFloat("##PosX", ref pos.X, 0.5f)) { }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f);
        if (ImGui.DragFloat("##PosY", ref pos.Y, 0.5f)) { }
        t.LocalPosition = new XnaVector2(pos.X, pos.Y);

        // Rotation (display degrees, store radians)
        float rotDeg = t.LocalRotation * (180f / MathF.PI);
        ImGui.Text("Rotation");
        ImGui.SameLine(80f);
        ImGui.SetNextItemWidth(166f);
        if (ImGui.DragFloat("##Rot", ref rotDeg, 0.5f))
            t.LocalRotation = rotDeg * (MathF.PI / 180f);

        // Scale
        var scl = new Vector2(t.LocalScale.X, t.LocalScale.Y);
        ImGui.Text("Scale");
        ImGui.SameLine(80f);
        ImGui.SetNextItemWidth(80f);
        if (ImGui.DragFloat("##SclX", ref scl.X, 0.01f)) { }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(80f);
        if (ImGui.DragFloat("##SclY", ref scl.Y, 0.01f)) { }
        t.LocalScale = new XnaVector2(scl.X, scl.Y);
    }

    // -------------------------------------------------------------------------
    // Component list
    // -------------------------------------------------------------------------

    private void DrawComponentList(Actor actor)
    {
        var components = actor.GetAllComponents();
        Component? toRemove = null;

        foreach (var component in components)
        {
            // Skip the built-in Transform (already shown above)
            if (component is Transform) continue;

            var typeName = component.GetType().Name;

            ImGui.PushID(component.GetHashCode());

            bool headerOpen = ImGui.CollapsingHeader(typeName, ImGuiTreeNodeFlags.DefaultOpen | ImGuiTreeNodeFlags.AllowOverlap);

            // "X" remove button aligned to the right
            ImGui.SameLine(ImGui.GetContentRegionAvail().X - 20f + ImGui.GetCursorPosX());
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.6f, 0.1f, 0.1f, 0.8f));
            if (ImGui.SmallButton("X##Remove"))
                toRemove = component;
            ImGui.PopStyleColor();

            // Enabled toggle
            if (headerOpen)
            {
                bool enabled = component.Enabled;
                if (ImGui.Checkbox("Enabled##comp", ref enabled))
                    component.Enabled = enabled;

                DrawComponentProperties(component);
            }

            ImGui.PopID();
        }

        if (toRemove != null)
        {
            // Use reflection to call the generic RemoveComponent via type
            var method = typeof(Actor).GetMethod("RemoveComponent")!
                .MakeGenericMethod(toRemove.GetType());
            try { method.Invoke(actor, null); }
            catch (Exception ex)
            {
                ConsoleLog.Add($"Could not remove component: {ex.Message}", LogLevel.Warning);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Component property inspector via reflection
    // -------------------------------------------------------------------------

    private static readonly HashSet<string> _skipProperties = new()
    {
        "Actor", "Enabled",
    };

    private void DrawComponentProperties(Component component)
    {
        var type  = component.GetType();
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Where(p => p.CanRead
                                 && p.CanWrite
                                 && p.GetMethod?.IsPublic == true
                                 && p.SetMethod?.IsPublic == true
                                 && !_skipProperties.Contains(p.Name))
                        .OrderBy(p => p.Name);

        foreach (var prop in props)
        {
            DrawPropertyControl(component, prop);
        }
    }

    private static readonly byte[] _strBuf = new byte[512];

    private void DrawPropertyControl(Component component, PropertyInfo prop)
    {
        try
        {
            var value = prop.GetValue(component);
            var pType = prop.PropertyType;

            ImGui.PushID(prop.Name);
            ImGui.Text(prop.Name);
            ImGui.SameLine(140f);
            ImGui.SetNextItemWidth(-1f);

            if (pType == typeof(float))
            {
                float v = (float)(value ?? 0f);
                if (ImGui.DragFloat("##v", ref v, 0.1f))
                    prop.SetValue(component, v);
            }
            else if (pType == typeof(int))
            {
                int v = (int)(value ?? 0);
                if (ImGui.DragInt("##v", ref v))
                    prop.SetValue(component, v);
            }
            else if (pType == typeof(bool))
            {
                bool v = (bool)(value ?? false);
                if (ImGui.Checkbox("##v", ref v))
                    prop.SetValue(component, v);
            }
            else if (pType == typeof(string))
            {
                var strBytes = new byte[512];
                var encoded = Encoding.UTF8.GetBytes((string?)value ?? "");
                Buffer.BlockCopy(encoded, 0, strBytes, 0, Math.Min(encoded.Length, strBytes.Length - 1));
                if (ImGui.InputText("##v", strBytes, (uint)strBytes.Length))
                    prop.SetValue(component, Encoding.UTF8.GetString(strBytes).TrimEnd('\0'));
            }
            else if (pType == typeof(XnaVector2))
            {
                var xv = (XnaVector2)(value ?? XnaVector2.Zero);
                var sv = new Vector2(xv.X, xv.Y);
                ImGui.SetNextItemWidth(80f);
                if (ImGui.DragFloat("##vx", ref sv.X, 0.1f)) { }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(80f);
                if (ImGui.DragFloat("##vy", ref sv.Y, 0.1f)) { }
                prop.SetValue(component, new XnaVector2(sv.X, sv.Y));
            }
            else if (pType == typeof(XnaColor))
            {
                var col = (XnaColor)(value ?? XnaColor.White);
                var sv  = new Vector4(col.R / 255f, col.G / 255f, col.B / 255f, col.A / 255f);
                if (ImGui.ColorEdit4("##v", ref sv))
                    prop.SetValue(component, new XnaColor(sv.X, sv.Y, sv.Z, sv.W));
            }
            else if (pType.IsEnum)
            {
                var names   = Enum.GetNames(pType);
                var values  = Enum.GetValues(pType);
                int current = 0;
                for (int i = 0; i < values.Length; i++)
                    if (values.GetValue(i)!.Equals(value)) { current = i; break; }

                if (ImGui.BeginCombo("##v", names[current]))
                {
                    for (int i = 0; i < names.Length; i++)
                    {
                        bool sel = i == current;
                        if (ImGui.Selectable(names[i], sel))
                            prop.SetValue(component, values.GetValue(i));
                        if (sel) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
            }
            else
            {
                ImGui.TextDisabled($"({pType.Name})");
            }

            ImGui.PopID();
        }
        catch
        {
            ImGui.PopID();
        }
    }

    // -------------------------------------------------------------------------
    // Add Component
    // -------------------------------------------------------------------------

    private void DrawAddComponentButton(Actor actor)
    {
        float w = ImGui.GetContentRegionAvail().X;
        if (ImGui.Button("Add Component", new Vector2(w, 24f)))
        {
            _componentTypes    = null;
            _componentFilter   = "";
            _componentFilterBuf = new byte[128];
            ImGui.OpenPopup("##AddComponentPopup");
        }

        if (ImGui.BeginPopup("##AddComponentPopup"))
        {
            // Lazy-build component type list
            if (_componentTypes == null)
            {
                _componentTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a =>
                    {
                        try { return a.GetTypes(); }
                        catch { return Array.Empty<Type>(); }
                    })
                    .Where(t => t.IsClass
                             && !t.IsAbstract
                             && typeof(Component).IsAssignableFrom(t)
                             && t != typeof(Transform)
                             && t.GetConstructor(Type.EmptyTypes) != null)
                    .OrderBy(t => t.Name)
                    .ToArray();
            }

            // Filter
            ImGui.SetNextItemWidth(220f);
            if (ImGui.InputText("##CompFilter", _componentFilterBuf, (uint)_componentFilterBuf.Length))
                _componentFilter = Encoding.UTF8.GetString(_componentFilterBuf).TrimEnd('\0');

            ImGui.Separator();

            ImGui.BeginChild("##CompList", new Vector2(220f, 240f));
            foreach (var type in _componentTypes)
            {
                if (!string.IsNullOrEmpty(_componentFilter) &&
                    !type.Name.Contains(_componentFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (ImGui.Selectable(type.Name))
                {
                    try
                    {
                        actor.AddComponentByType(type);
                        ConsoleLog.Add($"Added component '{type.Name}' to '{actor.Name}'.", LogLevel.Info);
                    }
                    catch (Exception ex)
                    {
                        ConsoleLog.Add($"Failed to add '{type.Name}': {ex.Message}", LogLevel.Error);
                    }
                    ImGui.CloseCurrentPopup();
                }
            }
            ImGui.EndChild();

            ImGui.EndPopup();
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static void EncodeToBuffer(string text, byte[] buf)
    {
        Array.Clear(buf, 0, buf.Length);
        var encoded = Encoding.UTF8.GetBytes(text);
        Buffer.BlockCopy(encoded, 0, buf, 0, Math.Min(encoded.Length, buf.Length - 1));
    }

    private static string DecodeBuffer(byte[] buf)
        => Encoding.UTF8.GetString(buf).TrimEnd('\0');
}
