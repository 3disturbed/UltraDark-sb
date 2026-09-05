using System.Numerics;
using System.Reflection;
using ImGuiNET;
using SexyBiscuit.Engine.Core;

namespace SexyBiscuit.Editor.Panels;

/// <summary>
/// API Reference panel — searchable, browsable reference of all public types
/// in the SexyBiscuit.Engine assembly, organized by namespace.
/// </summary>
public sealed class ApiReferencePanel
{
    public ApiReferencePanel()
    {
        GameCode.GameCodeHost.TypesChanged += () => _initialized = false;
    }

    // -------------------------------------------------------------------------
    // Data structures
    // -------------------------------------------------------------------------

    private class ApiTypeInfo
    {
        public string Name { get; set; } = "";
        public string Namespace { get; set; } = "";
        public string FullName { get; set; } = "";
        public string? BaseTypeName { get; set; }
        public bool IsComponent { get; set; }
        public List<ApiMemberInfo> Properties { get; set; } = new();
        public List<ApiMemberInfo> Methods { get; set; } = new();
        public List<ApiMemberInfo> Events { get; set; } = new();
        public string Description { get; set; } = "";
    }

    private class ApiMemberInfo
    {
        public string Name { get; set; } = "";
        public string ReturnType { get; set; } = "";
        public string Signature { get; set; } = "";
        public bool IsStatic { get; set; }
    }

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------

    private bool _initialized;
    private List<ApiTypeInfo> _allTypes = new();
    private Dictionary<string, List<ApiTypeInfo>> _typesByNamespace = new();
    private ApiTypeInfo? _selectedType;
    private byte[] _searchBuf = new byte[256];
    private string _searchText = "";

    // -------------------------------------------------------------------------
    // Hardcoded descriptions for core types
    // -------------------------------------------------------------------------

    private static readonly Dictionary<string, string> CoreDescriptions = new()
    {
        ["Actor"] = "Base game object. Contains components and participates in the scene lifecycle.",
        ["Component"] = "Base class for all components. Attach to Actors for behavior.",
        ["Transform"] = "2D position, rotation, and scale with parent-child hierarchy.",
        ["Transform3D"] = "3D position, rotation, and scale.",
        ["Scene"] = "Container for Actors organized in Layers.",
        ["SceneManager"] = "Manages scene loading, unloading, and transitions.",
    };

    // Methods inherited from System.Object that should be skipped
    private static readonly HashSet<string> SkippedMethods = new()
    {
        "ToString", "Equals", "GetHashCode", "GetType",
    };

    // -------------------------------------------------------------------------
    // Initialization
    // -------------------------------------------------------------------------

    private void Initialize()
    {
        _initialized = true;

        var componentType = typeof(SexyBiscuit.Engine.Core.Component);
        var assembly = componentType.Assembly;

        var publicTypes = assembly.SafeGetTypes().Where(t => t.IsPublic)
            .Where(t => !t.IsCompilerGenerated())
            .OrderBy(t => t.Namespace)
            .ThenBy(t => t.Name);

        foreach (var type in publicTypes)
        {
            var info = new ApiTypeInfo
            {
                Name = type.Name,
                Namespace = type.Namespace ?? "(global)",
                FullName = type.FullName ?? type.Name,
                BaseTypeName = type.BaseType != null && type.BaseType != typeof(object)
                    ? GetFriendlyTypeName(type.BaseType)
                    : null,
                IsComponent = componentType.IsAssignableFrom(type) && type != componentType,
            };

            // Description
            if (CoreDescriptions.TryGetValue(type.Name, out var desc))
                info.Description = desc;

            // Properties — declared on this type (not inherited), plus important base ones
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (var prop in properties)
            {
                if (prop.Name.Contains('<') || prop.GetIndexParameters().Length > 0)
                    continue;

                info.Properties.Add(new ApiMemberInfo
                {
                    Name = prop.Name,
                    ReturnType = GetFriendlyTypeName(prop.PropertyType),
                    Signature = prop.Name,
                    IsStatic = prop.GetGetMethod()?.IsStatic ?? prop.GetSetMethod()?.IsStatic ?? false,
                });
            }

            // Methods — skip getters, setters, object methods, compiler-generated
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                if (method.IsSpecialName) continue; // skip property accessors, event add/remove
                if (SkippedMethods.Contains(method.Name)) continue;
                if (method.Name.Contains('<')) continue; // compiler-generated

                var parameters = method.GetParameters();
                var paramStr = string.Join(", ", parameters.Select(p =>
                    $"{GetFriendlyTypeName(p.ParameterType)} {p.Name}"));

                info.Methods.Add(new ApiMemberInfo
                {
                    Name = method.Name,
                    ReturnType = GetFriendlyTypeName(method.ReturnType),
                    Signature = $"{method.Name}({paramStr})",
                    IsStatic = method.IsStatic,
                });
            }

            // Events
            var events = type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (var evt in events)
            {
                info.Events.Add(new ApiMemberInfo
                {
                    Name = evt.Name,
                    ReturnType = GetFriendlyTypeName(evt.EventHandlerType!),
                    Signature = evt.Name,
                    IsStatic = evt.AddMethod?.IsStatic ?? false,
                });
            }

            _allTypes.Add(info);
        }

        // Group by namespace
        _typesByNamespace = _allTypes
            .GroupBy(t => t.Namespace)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.Name).ToList());
    }

    // -------------------------------------------------------------------------
    // Draw
    // -------------------------------------------------------------------------

    public void Draw()
    {
        if (!EditorState.ShowApiReference) return;

        if (!_initialized)
            Initialize();

        bool open = EditorState.ShowApiReference;
        if (!ImGui.Begin("API Reference", ref open))
        {
            EditorState.ShowApiReference = open;
            ImGui.End();
            return;
        }
        EditorState.ShowApiReference = open;

        // Search bar
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputText("##ApiSearch", _searchBuf, (uint)_searchBuf.Length))
        {
            _searchText = System.Text.Encoding.UTF8.GetString(_searchBuf).TrimEnd('\0');
        }

        ImGui.Separator();

        // Split layout: left tree + right details
        float availWidth = ImGui.GetContentRegionAvail().X;
        float leftWidth = Math.Max(200f, availWidth * 0.3f);

        // Left pane — type tree
        ImGui.BeginChild("##ApiTree", new Vector2(leftWidth, 0f), ImGuiChildFlags.Border);
        DrawTypeTree();
        ImGui.EndChild();

        ImGui.SameLine();

        // Right pane — type details
        ImGui.BeginChild("##ApiDetails", new Vector2(0f, 0f), ImGuiChildFlags.Border);
        DrawTypeDetails();
        ImGui.EndChild();

        ImGui.End();
    }

    // -------------------------------------------------------------------------
    // Left pane — namespace/type tree
    // -------------------------------------------------------------------------

    private void DrawTypeTree()
    {
        bool hasFilter = !string.IsNullOrWhiteSpace(_searchText);
        string filter = _searchText.Trim().ToLowerInvariant();

        foreach (var (ns, types) in _typesByNamespace)
        {
            // Filter: check if any type in this namespace matches
            var matchingTypes = hasFilter
                ? types.Where(t => TypeMatchesFilter(t, filter)).ToList()
                : types;

            if (matchingTypes.Count == 0)
                continue;

            // Namespace node — default open when filtering
            var nsFlags = ImGuiTreeNodeFlags.SpanAvailWidth;
            if (hasFilter)
                nsFlags |= ImGuiTreeNodeFlags.DefaultOpen;

            if (ImGui.TreeNodeEx(ns, nsFlags))
            {
                foreach (var type in matchingTypes)
                {
                    var flags = ImGuiTreeNodeFlags.Leaf | ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth;
                    if (_selectedType == type)
                        flags |= ImGuiTreeNodeFlags.Selected;

                    // Components get a highlight color
                    if (type.IsComponent)
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.4f, 0.8f, 1.0f, 1.0f));

                    ImGui.TreeNodeEx(type.Name, flags);

                    if (type.IsComponent)
                        ImGui.PopStyleColor();

                    if (ImGui.IsItemClicked())
                        _selectedType = type;
                }

                ImGui.TreePop();
            }
        }
    }

    private static bool TypeMatchesFilter(ApiTypeInfo type, string filter)
    {
        if (type.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var member in type.Properties)
            if (member.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;

        foreach (var member in type.Methods)
            if (member.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;

        foreach (var member in type.Events)
            if (member.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    // -------------------------------------------------------------------------
    // Right pane — selected type details
    // -------------------------------------------------------------------------

    private void DrawTypeDetails()
    {
        if (_selectedType == null)
        {
            ImGui.TextDisabled("Select a type from the tree to view details.");
            return;
        }

        var type = _selectedType;

        // Type header
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 0.9f, 0.95f, 1.0f));
        ImGui.Text(type.FullName);
        ImGui.PopStyleColor();

        if (type.BaseTypeName != null)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(":");
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 0.9f, 0.95f, 1.0f));
            ImGui.Text(type.BaseTypeName);
            ImGui.PopStyleColor();
        }

        if (type.IsComponent)
        {
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.4f, 0.8f, 1.0f, 1.0f), "[Component]");
        }

        if (!string.IsNullOrEmpty(type.Description))
        {
            ImGui.Spacing();
            ImGui.TextWrapped(type.Description);
        }

        ImGui.Separator();
        ImGui.Spacing();

        // Properties
        if (type.Properties.Count > 0 && ImGui.CollapsingHeader($"Properties ({type.Properties.Count})", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (ImGui.BeginTable("##Props", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthStretch, 0.4f);
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 0.6f);
                ImGui.TableHeadersRow();

                foreach (var prop in type.Properties)
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 0.9f, 0.95f, 1.0f));
                    ImGui.TextUnformatted(prop.ReturnType);
                    ImGui.PopStyleColor();

                    ImGui.TableNextColumn();
                    if (prop.IsStatic)
                    {
                        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "static ");
                        ImGui.SameLine(0f, 0f);
                    }
                    ImGui.TextUnformatted(prop.Name);
                }

                ImGui.EndTable();
            }
        }

        // Methods
        if (type.Methods.Count > 0 && ImGui.CollapsingHeader($"Methods ({type.Methods.Count})", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (ImGui.BeginTable("##Methods", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Return Type", ImGuiTableColumnFlags.WidthStretch, 0.3f);
                ImGui.TableSetupColumn("Signature", ImGuiTableColumnFlags.WidthStretch, 0.7f);
                ImGui.TableHeadersRow();

                foreach (var method in type.Methods)
                {
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 0.9f, 0.95f, 1.0f));
                    ImGui.TextUnformatted(method.ReturnType);
                    ImGui.PopStyleColor();

                    ImGui.TableNextColumn();
                    if (method.IsStatic)
                    {
                        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), "static ");
                        ImGui.SameLine(0f, 0f);
                    }
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.9f, 0.4f, 1.0f));
                    ImGui.TextUnformatted(method.Signature);
                    ImGui.PopStyleColor();
                }

                ImGui.EndTable();
            }
        }

        // Events
        if (type.Events.Count > 0 && ImGui.CollapsingHeader($"Events ({type.Events.Count})", ImGuiTreeNodeFlags.DefaultOpen))
        {
            foreach (var evt in type.Events)
            {
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.3f, 0.9f, 0.95f, 1.0f));
                ImGui.TextUnformatted(evt.ReturnType);
                ImGui.PopStyleColor();
                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.0f, 0.9f, 0.4f, 1.0f));
                ImGui.TextUnformatted(evt.Name);
                ImGui.PopStyleColor();
            }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string GetFriendlyTypeName(Type t)
    {
        if (t == typeof(void)) return "void";
        if (t == typeof(int)) return "int";
        if (t == typeof(float)) return "float";
        if (t == typeof(double)) return "double";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(string)) return "string";
        if (t == typeof(object)) return "object";
        if (t == typeof(byte)) return "byte";
        if (t == typeof(long)) return "long";
        if (t == typeof(short)) return "short";
        if (t == typeof(uint)) return "uint";
        if (t == typeof(ulong)) return "ulong";
        if (t == typeof(char)) return "char";
        if (t == typeof(decimal)) return "decimal";

        // Nullable<T>
        var nullable = Nullable.GetUnderlyingType(t);
        if (nullable != null)
            return GetFriendlyTypeName(nullable) + "?";

        // Generic types (e.g., List<Actor>)
        if (t.IsGenericType)
        {
            var name = t.Name;
            int backtick = name.IndexOf('`');
            if (backtick > 0)
                name = name[..backtick];

            var args = t.GetGenericArguments();
            var argNames = string.Join(", ", args.Select(GetFriendlyTypeName));
            return $"{name}<{argNames}>";
        }

        // Arrays
        if (t.IsArray)
            return GetFriendlyTypeName(t.GetElementType()!) + "[]";

        // By-ref (out/ref parameters)
        if (t.IsByRef)
            return GetFriendlyTypeName(t.GetElementType()!);

        return t.Name;
    }
}

// -------------------------------------------------------------------------
// Extension — compiler-generated check
// -------------------------------------------------------------------------

internal static class ApiReferencePanelExtensions
{
    public static bool IsCompilerGenerated(this Type type)
    {
        return type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false)
            || type.Name.Contains('<')
            || type.Name.Contains("__");
    }
}
