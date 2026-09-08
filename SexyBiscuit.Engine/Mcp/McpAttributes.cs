namespace SexyBiscuit.Engine.Mcp;

/// <summary>
/// Marks a method as an MCP tool. The registry builds the JSON schema from the parameters and
/// their <see cref="McpParamAttribute"/>s, so the method signature is the whole contract.
/// </summary>
/// <remarks>
/// Handlers may be instance or static, synchronous or <c>Task</c>-returning, and may return
/// <see cref="McpToolResult"/>, <c>string</c>, any serialisable object (sent as JSON text plus
/// structured content), or nothing. Parameters of type <see cref="CancellationToken"/> and
/// <see cref="McpCallContext"/> are injected and hidden from the schema.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class McpToolAttribute : Attribute
{
    public McpToolAttribute(string name, string description)
    {
        Name        = name;
        Description = description;
    }

    /// <summary>Tool name: letters, digits, underscores and hyphens, 1–64 characters.</summary>
    public string Name { get; }

    /// <summary>What Claude reads to decide when to call it. Imperative, with units.</summary>
    public string Description { get; }

    /// <summary>
    /// Run the handler through the dispatcher on the game thread. Default true: almost every
    /// tool touches the scene, and the scene has no locks.
    /// </summary>
    public bool MainThread { get; init; } = true;

    /// <summary>
    /// The tool changes the scene. Drives the undo snapshot, the post-call flush, the dirty flag
    /// and the console echo.
    /// </summary>
    public bool Mutating { get; init; }

    /// <summary>Advertised as <c>destructiveHint</c> so a cautious client can confirm first.</summary>
    public bool Destructive { get; init; }

    /// <summary>
    /// The tool changes nothing: not the scene, not a file, not a process, not a remote service.
    /// Advertised as <c>readOnlyHint</c>, which is what a client reads to decide whether a call
    /// needs confirming, so it is opt-in and false by default.
    /// </summary>
    /// <remarks>
    /// This used to be inferred from <see cref="Mutating"/>, which means something narrower — the
    /// tool edits the scene graph, so snapshot undo first. Everything that wrote a file, spawned a
    /// process or published a build was therefore advertised as read-only: 65 of the 88 tools,
    /// <c>publish_build</c> and <c>uninstall_cookie</c> among them, the latter claiming to be
    /// read-only and destructive at once.
    /// </remarks>
    public bool ReadOnly { get; init; }

    /// <summary>
    /// Optional activity-log label with <c>{argument}</c> placeholders, e.g.
    /// <c>"Spawn actor {name}"</c>. Defaults to the tool name with underscores as spaces.
    /// </summary>
    public string? Label { get; init; }
}

/// <summary>Describes a tool parameter. Appears in the JSON schema Claude reads.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class McpParamAttribute : Attribute
{
    public McpParamAttribute(string description) => Description = description;

    public string  Description { get; }

    /// <summary>Appended to the description as "e.g. …".</summary>
    public string? Example     { get; init; }
}

/// <summary>
/// Marks a method returning <c>string</c> (or <c>Task&lt;string&gt;</c>) as an MCP resource.
/// Resources are documents Claude can read on demand: the component catalogue, the current
/// scene, the scripting API.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class McpResourceAttribute : Attribute
{
    public McpResourceAttribute(string uri, string name, string mimeType = "text/plain")
    {
        Uri      = uri;
        Name     = name;
        MimeType = mimeType;
    }

    public string  Uri         { get; }
    public string  Name        { get; }
    public string  MimeType    { get; }
    public string? Description { get; init; }
}

/// <summary>
/// Marks a method as an MCP prompt template. Its string parameters become the prompt's
/// arguments; it returns the user message text (or a list of <see cref="McpPromptMessage"/>).
/// Claude Code exposes prompts as slash commands.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class McpPromptAttribute : Attribute
{
    public McpPromptAttribute(string name, string description)
    {
        Name        = name;
        Description = description;
    }

    public string Name        { get; }
    public string Description { get; }
}
