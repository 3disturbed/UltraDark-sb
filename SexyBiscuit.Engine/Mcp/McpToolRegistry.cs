using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SexyBiscuit.Engine.Mcp;

/// <summary>Options for a batch of registrations from one object or type.</summary>
public sealed class McpRegistrationOptions
{
    /// <summary>Prepended to every tool name that does not already start with it (e.g. <c>game_</c>).</summary>
    public string? NamePrefix { get; init; }

    /// <summary>Where the tools come from: <c>engine</c>, <c>editor</c> or <c>project</c>.</summary>
    public string Source { get; init; } = "engine";
}

/// <summary>The handle for one registration batch, so a whole batch can be removed at once.</summary>
public sealed class McpRegistration
{
    internal McpRegistration(IReadOnlyList<string> toolNames, string source)
    {
        ToolNames = toolNames;
        Source    = source;
    }

    public IReadOnlyList<string> ToolNames { get; }
    public string                Source    { get; }
}

/// <summary>Everything the server knows about one tool.</summary>
public sealed record McpToolDescriptor(
    string     Name,
    string     Description,
    JsonObject InputSchema,
    bool       MainThread,
    bool       Mutating,
    bool       Destructive,
    string?    LabelTemplate,
    string     Source,
    MethodInfo Method,
    object?    Target);

/// <summary>
/// Discovers <c>[McpTool]</c> methods, describes them for <c>tools/list</c> and runs them for
/// <c>tools/call</c>: bind arguments, hop to the game thread when required, snapshot before a
/// mutation, flush after, and turn every failure into an <c>isError</c> result.
/// </summary>
public sealed class McpToolRegistry
{
    private static readonly Regex NamePattern = new("^[a-zA-Z0-9_-]{1,64}$", RegexOptions.Compiled);

    private readonly object _lock = new();
    private readonly Dictionary<string, McpToolDescriptor> _tools = new(StringComparer.Ordinal);
    private int  _suspendDepth;
    private bool _changeWhileSuspended;

    public McpToolRegistry(IMcpDispatcher dispatcher) => Dispatcher = dispatcher;

    public IMcpDispatcher Dispatcher { get; }

    /// <summary>Sorted by name, so <c>tools/list</c> is stable between calls.</summary>
    public IReadOnlyList<McpToolDescriptor> Tools
    {
        get { lock (_lock) return _tools.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ToArray(); }
    }

    /// <summary>Raised after registrations change (coalesced while suspended). Any thread.</summary>
    public event Action? ToolsChanged;

    /// <summary>Runs on the main thread before a mutating tool, with its arguments: the undo snapshot.</summary>
    public Action<McpToolDescriptor, JsonElement?, McpCallContext>? BeforeMutation { get; set; }

    /// <summary>Runs on the main thread after every tool: flush pending actors, mark dirty.</summary>
    public Action<McpToolDescriptor, McpCallContext, McpToolResult>? AfterInvoke { get; set; }

    /// <summary>Observes every call. Not on the main thread.</summary>
    public ActivityLog? Activity { get; set; }

    /// <summary>Receives internal errors (a handler threw something unexpected).</summary>
    public Action<string, bool>? Log { get; set; }

    // -------------------------------------------------------------------------
    // Registration
    // -------------------------------------------------------------------------

    /// <summary>Registers every public <c>[McpTool]</c> method (instance and static) on the object.</summary>
    public McpRegistration RegisterInstance(object instance, McpRegistrationOptions? options = null)
        => Register(instance.GetType(), instance, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static, options);

    /// <summary>Registers every public static <c>[McpTool]</c> method on the type.</summary>
    public McpRegistration RegisterStatic(Type type, McpRegistrationOptions? options = null)
        => Register(type, null, BindingFlags.Public | BindingFlags.Static, options);

    private McpRegistration Register(Type type, object? target, BindingFlags flags, McpRegistrationOptions? options)
    {
        options ??= new McpRegistrationOptions();
        var names = new List<string>();

        foreach (var method in type.GetMethods(flags))
        {
            var attr = method.GetCustomAttribute<McpToolAttribute>();
            if (attr == null) continue;

            string name = attr.Name;
            if (!string.IsNullOrEmpty(options.NamePrefix) && !name.StartsWith(options.NamePrefix, StringComparison.Ordinal))
                name = options.NamePrefix + name;

            if (!NamePattern.IsMatch(name))
                throw new InvalidOperationException(
                    $"Tool name '{name}' on {type.Name}.{method.Name} must match [a-zA-Z0-9_-]{{1,64}}.");

            if (method.IsGenericMethodDefinition)
                throw new InvalidOperationException($"Tool '{name}' cannot be a generic method.");

            var descriptor = new McpToolDescriptor(
                name, attr.Description, McpSchema.ForMethod(method),
                attr.MainThread, attr.Mutating, attr.Destructive, attr.Label, options.Source,
                method, method.IsStatic ? null : target);

            lock (_lock)
            {
                if (_tools.ContainsKey(name))
                    throw new InvalidOperationException($"A tool named '{name}' is already registered.");
                _tools[name] = descriptor;
            }

            names.Add(name);
        }

        if (names.Count > 0) NotifyChanged();
        return new McpRegistration(names, options.Source);
    }

    public bool Unregister(string name)
    {
        bool removed;
        lock (_lock) removed = _tools.Remove(name);
        if (removed) NotifyChanged();
        return removed;
    }

    /// <summary>Removes every tool the registration added. Returns how many were still present.</summary>
    public int Unregister(McpRegistration registration)
    {
        int removed = 0;
        lock (_lock)
        {
            foreach (var name in registration.ToolNames)
                if (_tools.Remove(name)) removed++;
        }

        if (removed > 0) NotifyChanged();
        return removed;
    }

    public McpToolDescriptor? Find(string name)
    {
        lock (_lock) return _tools.GetValueOrDefault(name);
    }

    /// <summary>
    /// Coalesces <see cref="ToolsChanged"/> until disposed, so a hot reload that unregisters
    /// one generation and registers the next sends a single <c>tools/list_changed</c>.
    /// </summary>
    public IDisposable SuspendNotifications()
    {
        lock (_lock) _suspendDepth++;
        return new Suspension(this);
    }

    private void NotifyChanged()
    {
        lock (_lock)
        {
            if (_suspendDepth > 0)
            {
                _changeWhileSuspended = true;
                return;
            }
        }

        ToolsChanged?.Invoke();
    }

    private sealed class Suspension : IDisposable
    {
        private McpToolRegistry? _owner;

        public Suspension(McpToolRegistry owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner == null) return;

            bool fire;
            lock (owner._lock)
            {
                owner._suspendDepth--;
                fire = owner._suspendDepth == 0 && owner._changeWhileSuspended;
                if (fire) owner._changeWhileSuspended = false;
            }

            if (fire) owner.ToolsChanged?.Invoke();
        }
    }

    // -------------------------------------------------------------------------
    // Invocation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Also send each JSON result's <c>structuredContent</c> copy on the wire. Off by default: a
    /// client that receives it ignores the text block, which carries the summary and warnings,
    /// and no tool declares an output schema that would need it.
    /// </summary>
    public bool EmitStructuredContent { get; set; }

    /// <summary>
    /// Runs a main-thread tool on the calling thread, with no undo snapshot, no activity entry and
    /// no thread hop: what a batch tool uses for each of its operations, so the whole batch is one
    /// undo step and one flush. The caller must already be on the game thread.
    /// </summary>
    public McpToolResult InvokeInline(string name, JsonElement? arguments, McpCallContext context)
    {
        var descriptor = Find(name);
        if (descriptor == null)
            return McpToolResult.Error($"Unknown tool '{name}'.");
        if (!descriptor.MainThread)
            return McpToolResult.Error($"'{name}' cannot run inside a batch.");

        try
        {
            var args     = McpSchema.Bind(descriptor.Method, arguments, context);
            var returned = descriptor.Method.Invoke(descriptor.Target, args);
            if (returned is Task)
                return McpToolResult.Error($"'{name}' is asynchronous and cannot run inside a batch.");
            return Normalise(returned);
        }
        catch (McpToolException ex)
        {
            return McpToolResult.Error(ex.FullMessage);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is McpToolException inner)
        {
            return McpToolResult.Error(inner.FullMessage);
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            return McpToolResult.Error($"{name} failed: {ex.InnerException.Message}");
        }
        catch (Exception ex)
        {
            return McpToolResult.Error($"{name} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Runs a tool. Never throws: unknown tools, bad arguments, cancellation and handler
    /// exceptions all come back as an <c>isError</c> result Claude can read and act on.
    /// </summary>
    public async Task<McpToolResult> InvokeAsync(string name, JsonElement? arguments, McpCallContext context)
    {
        var descriptor = Find(name);
        if (descriptor == null)
            return McpToolResult.Error($"Unknown tool '{name}'. Call tools/list for the available tools.");

        long seq = Activity?.Begin(name, FormatLabel(descriptor.LabelTemplate, name, arguments),
                                   SummariseArguments(arguments), descriptor.Mutating, context.ClientName) ?? -1;

        McpToolResult result;
        try
        {
            result = await RunAsync(descriptor, arguments, context).ConfigureAwait(false);
        }
        catch (McpToolException ex)
        {
            result = McpToolResult.Error(ex.FullMessage);
        }
        catch (OperationCanceledException)
        {
            result = McpToolResult.Error("Cancelled.");
            Activity?.Complete(seq, ActivityState.Cancelled, "cancelled");
            return result;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
            if (inner is McpToolException mte)
            {
                result = McpToolResult.Error(mte.FullMessage);
            }
            else if (inner is OperationCanceledException)
            {
                result = McpToolResult.Error("Cancelled.");
                Activity?.Complete(seq, ActivityState.Cancelled, "cancelled");
                return result;
            }
            else
            {
                result = McpToolResult.Error($"{name} failed: {inner.GetType().Name}: {inner.Message}");
                Log?.Invoke($"[mcp] {name} threw {inner.GetType().Name}: {inner.Message}\n{inner.StackTrace}", true);
            }
        }

        Activity?.Complete(seq, result.IsError ? ActivityState.Failed : ActivityState.Succeeded,
                           Truncate(result.FirstText, 200), ResultSize(result));
        return result;
    }

    /// <summary>Text characters plus decoded image bytes — the size the model reads, not the panel's truncated copy.</summary>
    private static long ResultSize(McpToolResult result)
        => result.Content.Sum(c => c switch
        {
            McpTextContent  text  => (long)text.Text.Length,
            McpImageContent image => image.Base64Data.Length * 3L / 4,
            _                     => 0L,
        });

    private async Task<McpToolResult> RunAsync(McpToolDescriptor descriptor, JsonElement? arguments, McpCallContext context)
    {
        var args = McpSchema.Bind(descriptor.Method, arguments, context);

        if (!descriptor.MainThread)
        {
            var value = await AwaitIfTask(descriptor.Method.Invoke(descriptor.Target, args)).ConfigureAwait(false);
            var result = Normalise(value);
            if (AfterInvoke != null)
                await Dispatcher.InvokeAsync(() => AfterInvoke(descriptor, context, result), context.Cancellation).ConfigureAwait(false);
            return result;
        }

        // One hop for a synchronous tool: snapshot, run and flush all land in the same frame,
        // so no other tool can interleave and the change is drawn immediately.
        var outcome = await Dispatcher.InvokeAsync(() =>
        {
            if (descriptor.Mutating) BeforeMutation?.Invoke(descriptor, arguments, context);

            object? returned = descriptor.Method.Invoke(descriptor.Target, args);
            if (returned is Task pending) return (object?)pending;

            var completed = Normalise(returned);
            AfterInvoke?.Invoke(descriptor, context, completed);
            return completed;
        }, context.Cancellation).ConfigureAwait(false);

        if (outcome is McpToolResult done) return done;

        // An async handler that started on the main thread finishes off it.
        var asyncValue = await AwaitIfTask(outcome).ConfigureAwait(false);
        var asyncResult = Normalise(asyncValue);
        if (AfterInvoke != null)
            await Dispatcher.InvokeAsync(() => AfterInvoke(descriptor, context, asyncResult), context.Cancellation).ConfigureAwait(false);
        return asyncResult;
    }

    private static async Task<object?> AwaitIfTask(object? value)
    {
        if (value is not Task task) return value;

        await task.ConfigureAwait(false);

        var type = task.GetType();
        if (!type.IsGenericType) return null;

        var result = type.GetProperty("Result")?.GetValue(task);
        return result?.GetType().Name == "VoidTaskResult" ? null : result;
    }

    private static McpToolResult Normalise(object? value) => value switch
    {
        McpToolResult r => r,
        null            => McpToolResult.Text("OK"),
        string s        => McpToolResult.Text(s),
        JsonNode n      => McpToolResult.Text(McpJson.ToText(n, indented: false)),
        _               => McpToolResult.Json(value),
    };

    // -------------------------------------------------------------------------
    // Descriptions
    // -------------------------------------------------------------------------

    /// <summary>
    /// The <c>tools/list</c> payload. Every byte of it sits in the model's context for the whole
    /// session, so annotations are emitted only when they say something: a read-only hint lets
    /// the client run calls in parallel, a destructive hint gates them; the rest is the default.
    /// </summary>
    public JsonArray DescribeForToolsList()
    {
        var array = new JsonArray();
        foreach (var tool in Tools)
        {
            var entry = new JsonObject
            {
                ["name"]        = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            };

            var annotations = new JsonObject();
            if (!tool.Mutating)   annotations["readOnlyHint"]    = true;
            if (tool.Destructive) annotations["destructiveHint"] = true;
            if (annotations.Count > 0) entry["annotations"] = annotations;

            array.Add(entry);
        }
        return array;
    }

    /// <summary>A markdown table of every tool, for the wiki.</summary>
    public string DescribeMarkdown()
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Tool | Description | Parameters |");
        sb.AppendLine("|---|---|---|");

        foreach (var tool in Tools)
        {
            var parameters = new List<string>();
            foreach (var p in tool.Method.GetParameters())
            {
                if (McpSchema.IsInjected(p)) continue;

                string entry = $"`{p.Name}`: {ValueConverter.Describe(p.ParameterType)}";
                if (p.HasDefaultValue) entry += p.DefaultValue is null ? " (optional)" : $" (default {FormatDefault(p.DefaultValue)})";
                parameters.Add(entry);
            }

            sb.Append("| `").Append(tool.Name).Append("` | ")
              .Append(tool.Description.Replace("|", "\\|").Replace("\n", " "))
              .Append(" | ")
              .Append(parameters.Count == 0 ? "—" : string.Join("<br>", parameters))
              .AppendLine(" |");
        }

        return sb.ToString();
    }

    private static string FormatDefault(object value) => value switch
    {
        string s => $"\"{s}\"",
        bool b   => b ? "true" : "false",
        _        => value.ToString() ?? "",
    };

    /// <summary>
    /// Fills a label template such as <c>"Spawn actor {name}"</c> from the arguments; without a
    /// template the tool name is humanised (<c>spawn_actor</c> → <c>spawn actor</c>).
    /// </summary>
    public static string FormatLabel(string? template, string toolName, JsonElement? arguments)
    {
        if (string.IsNullOrEmpty(template)) return toolName.Replace('_', ' ');

        return Regex.Replace(template, @"\{([A-Za-z0-9_]+)\}", m =>
        {
            if (arguments is not { ValueKind: JsonValueKind.Object } args) return "?";

            foreach (var prop in args.EnumerateObject())
            {
                if (!string.Equals(prop.Name, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) continue;
                return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : prop.Value.GetRawText();
            }

            return "?";
        });
    }

    private static string SummariseArguments(JsonElement? arguments)
    {
        if (arguments is not { } a || a.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "";
        return Truncate(a.GetRawText(), 160);
    }

    private static string Truncate(string text, int max)
    {
        text = text.Replace('\n', ' ');
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
