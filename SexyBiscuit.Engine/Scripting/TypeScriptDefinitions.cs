namespace SexyBiscuit.Engine.Scripting;

/// <summary>
/// The TypeScript declaration file (<c>.d.ts</c>) describing the script API exposed by
/// <see cref="ScriptBridge"/>, generated from the scripting contract and embedded in this
/// assembly (see <see cref="ScriptContract"/>).
///
/// Drop the file into a scripts workspace and point VS Code at it via a <c>jsconfig.json</c>
/// or <c>tsconfig.json</c> <c>typeRoots</c> entry to get completion, hover types and parameter
/// hints with zero build steps. The editor's MCP server serves the same text as the
/// <c>sexybiscuit://scripting/api.d.ts</c> resource.
///
/// Usage:
/// <code>
///   // At engine startup (editor mode):
///   TypeScriptDefinitions.WriteToFile("Scripts/sb-engine.d.ts");
/// </code>
/// </summary>
public static class TypeScriptDefinitions
{
    /// <summary>
    /// Returns the complete <c>.d.ts</c> content: every global, member, hook and helper type
    /// the contract names, as <c>html5/tools/gen-dts.js</c> wrote it.
    /// </summary>
    public static string Generate() => ScriptContract.Declarations;

    /// <summary>
    /// Writes the <c>.d.ts</c> content to <paramref name="outputPath"/>, creating any
    /// intermediate directories as needed. Overwrites any existing file at that path.
    /// </summary>
    /// <param name="outputPath">Destination file path, e.g. <c>"Scripts/sb-engine.d.ts"</c>.</param>
    public static void WriteToFile(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("outputPath must not be empty.", nameof(outputPath));

        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(outputPath, Generate(), System.Text.Encoding.UTF8);
            System.Diagnostics.Debug.WriteLine($"[TypeScriptDefinitions] Written to: {outputPath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TypeScriptDefinitions] Failed to write '{outputPath}': {ex.Message}");
            throw;
        }
    }
}
