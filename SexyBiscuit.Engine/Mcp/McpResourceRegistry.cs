using System.Reflection;
using System.Text.Json.Nodes;

namespace SexyBiscuit.Engine.Mcp;

public sealed record McpResourceDescriptor(
    string Uri,
    string Name,
    string MimeType,
    string? Description,
    Func<McpCallContext, Task<string>> Read);

public sealed record McpPromptArgument(string Name, string Description, bool Required);

public sealed record McpPromptMessage(string Role, string Text);

public sealed record McpPromptDescriptor(
    string Name,
    string Description,
    IReadOnlyList<McpPromptArgument> Arguments,
    Func<IReadOnlyDictionary<string, string>, McpCallContext, Task<IReadOnlyList<McpPromptMessage>>> Get);

/// <summary>
/// Discovers <c>[McpResource]</c> and <c>[McpPrompt]</c> methods and serves them. Reads run
/// through the dispatcher because most resources describe the live scene.
/// </summary>
public sealed class McpResourceRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, McpResourceDescriptor> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, McpPromptDescriptor>   _prompts   = new(StringComparer.Ordinal);

    public McpResourceRegistry(IMcpDispatcher dispatcher) => Dispatcher = dispatcher;

    public IMcpDispatcher Dispatcher { get; }

    public IReadOnlyList<McpResourceDescriptor> Resources
    {
        get { lock (_lock) return _resources.Values.OrderBy(r => r.Uri, StringComparer.Ordinal).ToArray(); }
    }

    public IReadOnlyList<McpPromptDescriptor> Prompts
    {
        get { lock (_lock) return _prompts.Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToArray(); }
    }

    public event Action? Changed;

    /// <summary>
    /// Registers the object's public <c>[McpResource]</c> methods (returning <c>string</c> or
    /// <c>Task&lt;string&gt;</c>, optionally taking an <see cref="McpCallContext"/>) and
    /// <c>[McpPrompt]</c> methods (string parameters, returning a user message or a message list).
    /// </summary>
    public McpRegistration RegisterInstance(object instance)
    {
        var names = new List<string>();

        foreach (var method in instance.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            object? target = method.IsStatic ? null : instance;

            if (method.GetCustomAttribute<McpResourceAttribute>() is { } resource)
            {
                var descriptor = new McpResourceDescriptor(resource.Uri, resource.Name, resource.MimeType, resource.Description,
                    ctx => InvokeResource(method, target, ctx));

                lock (_lock) _resources[resource.Uri] = descriptor;
                names.Add(resource.Uri);
            }

            if (method.GetCustomAttribute<McpPromptAttribute>() is { } prompt)
            {
                var arguments = method.GetParameters()
                    .Where(p => !McpSchema.IsInjected(p))
                    .Select(p => new McpPromptArgument(
                        p.Name!,
                        p.GetCustomAttribute<McpParamAttribute>()?.Description ?? "",
                        !p.HasDefaultValue))
                    .ToArray();

                var descriptor = new McpPromptDescriptor(prompt.Name, prompt.Description, arguments,
                    (args, ctx) => InvokePrompt(method, target, args, ctx));

                lock (_lock) _prompts[prompt.Name] = descriptor;
                names.Add(prompt.Name);
            }
        }

        if (names.Count > 0) Changed?.Invoke();
        return new McpRegistration(names, "resources");
    }

    public McpResourceDescriptor? FindResource(string uri)
    {
        lock (_lock) return _resources.GetValueOrDefault(uri);
    }

    public McpPromptDescriptor? FindPrompt(string name)
    {
        lock (_lock) return _prompts.GetValueOrDefault(name);
    }

    /// <summary>Reads a resource, or returns null when the URI is unknown.</summary>
    public async Task<string?> ReadAsync(string uri, McpCallContext context)
    {
        var resource = FindResource(uri);
        if (resource == null) return null;
        return await resource.Read(context).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpPromptMessage>?> GetPromptAsync(string name, IReadOnlyDictionary<string, string> arguments, McpCallContext context)
    {
        var prompt = FindPrompt(name);
        if (prompt == null) return null;
        return await prompt.Get(arguments, context).ConfigureAwait(false);
    }

    private async Task<string> InvokeResource(MethodInfo method, object? target, McpCallContext context)
    {
        var parameters = method.GetParameters();
        var args = parameters.Select(p => p.ParameterType == typeof(McpCallContext) ? (object)context
                                        : p.ParameterType == typeof(CancellationToken) ? context.Cancellation
                                        : null).ToArray();

        object? value = await Dispatcher.InvokeAsync(() => method.Invoke(target, args), context.Cancellation).ConfigureAwait(false);
        if (value is Task task)
        {
            await task.ConfigureAwait(false);
            value = task.GetType().GetProperty("Result")?.GetValue(task);
        }

        return value switch
        {
            null       => "",
            string s   => s,
            JsonNode n => McpJson.ToText(n),
            _          => McpJson.Serialize(value, indented: true),
        };
    }

    private async Task<IReadOnlyList<McpPromptMessage>> InvokePrompt(MethodInfo method, object? target,
        IReadOnlyDictionary<string, string> arguments, McpCallContext context)
    {
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (p.ParameterType == typeof(McpCallContext))    { args[i] = context; continue; }
            if (p.ParameterType == typeof(CancellationToken)) { args[i] = context.Cancellation; continue; }

            var match = arguments.FirstOrDefault(kv => string.Equals(kv.Key, p.Name, StringComparison.OrdinalIgnoreCase));
            if (match.Key != null)
                args[i] = match.Value;
            else if (p.HasDefaultValue)
                args[i] = p.DefaultValue;
            else
                throw new McpToolException($"Prompt argument '{p.Name}' is required.");
        }

        object? value = await Dispatcher.InvokeAsync(() => method.Invoke(target, args), context.Cancellation).ConfigureAwait(false);
        if (value is Task task)
        {
            await task.ConfigureAwait(false);
            value = task.GetType().GetProperty("Result")?.GetValue(task);
        }

        return value switch
        {
            IReadOnlyList<McpPromptMessage> list => list,
            IEnumerable<McpPromptMessage> seq    => seq.ToArray(),
            McpPromptMessage single              => new[] { single },
            string text                          => new[] { new McpPromptMessage("user", text) },
            null                                 => Array.Empty<McpPromptMessage>(),
            _                                    => new[] { new McpPromptMessage("user", value.ToString() ?? "") },
        };
    }
}
