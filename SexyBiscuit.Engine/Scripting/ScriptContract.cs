using System.Text.Json;

namespace SexyBiscuit.Engine.Scripting;

/// <summary>
/// The scripting contract, read from the copy of <c>html5/src/scripting/bridge-api.json</c>
/// embedded in this assembly, and the TypeScript declarations generated from it.
/// </summary>
/// <remarks>
/// The browser bridge and the Jint bridge implement one list. This is that list on the C# side,
/// so the hooks the runtime dispatches and the API an agent is shown cannot drift from it. The
/// declarations are written by <c>html5/tools/gen-dts.js</c> (<c>npm run gen</c>); never edit
/// them by hand.
/// </remarks>
public static class ScriptContract
{
    private const string ContractResource     = "SexyBiscuit.Engine.bridge-api.json";
    private const string DeclarationsResource = "SexyBiscuit.Engine.sb-engine.d.ts";

    private static readonly Lazy<string> ContractJson     = new(() => Read(ContractResource));
    private static readonly Lazy<string> DeclarationsText = new(() => Read(DeclarationsResource));

    /// <summary>The contract file, verbatim.</summary>
    public static string Json => ContractJson.Value;

    /// <summary>The generated <c>.d.ts</c>, verbatim.</summary>
    public static string Declarations => DeclarationsText.Value;

    /// <summary>The lifecycle hooks a script may define, in the contract's order.</summary>
    public static string[] HookNames()
    {
        using var document = JsonDocument.Parse(Json);
        return document.RootElement.GetProperty("hooks").EnumerateObject().Select(p => p.Name).ToArray();
    }

    private static string Read(string name)
    {
        using var stream = typeof(ScriptContract).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"{name} is not embedded in the engine; run `npm run gen` in html5/ and rebuild.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
