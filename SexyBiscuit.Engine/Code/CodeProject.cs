using System.Xml.Linq;

namespace SexyBiscuit.Engine.Code;

/// <summary>The C# project inside a SexyBiscuit project folder, and where its pieces are.</summary>
public sealed class CodeProject
{
    private CodeProject(string root, string csprojPath, string rootNamespace, string assemblyName)
    {
        Root          = root;
        CsprojPath    = csprojPath;
        RootNamespace = rootNamespace;
        AssemblyName  = assemblyName;
    }

    public string Root          { get; }
    public string CsprojPath    { get; }
    public string Name          => Path.GetFileNameWithoutExtension(CsprojPath);
    public string RootNamespace { get; }
    public string AssemblyName  { get; }

    public string SourceDirectory => Path.Combine(Root, "Source");
    public string PropsPath       => Path.Combine(Root, CodeProjectGenerator.PropsFileName);
    public string ScratchDirectory => Path.Combine(Root, ".sexybiscuit");

    /// <summary>The conventional output path; the build runner asks MSBuild for the real one.</summary>
    public string OutputAssemblyPath(string configuration)
        => Path.Combine(Root, "bin", configuration, "net8.0", AssemblyName + ".dll");

    public string DocumentationPath(string configuration)
        => Path.ChangeExtension(OutputAssemblyPath(configuration), ".xml");

    public IReadOnlyList<string> SourceFiles()
        => Directory.Exists(SourceDirectory)
            ? Directory.EnumerateFiles(SourceDirectory, "*.cs", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();

    /// <summary>
    /// The project's csproj at the root of the project folder. With several, the one named
    /// after the folder wins, then the first alphabetically. Null when there is none.
    /// </summary>
    public static CodeProject? Find(string projectRoot)
    {
        if (!Directory.Exists(projectRoot)) return null;

        var candidates = Directory.EnumerateFiles(projectRoot, "*.csproj", SearchOption.TopDirectoryOnly)
                                  .OrderBy(f => f, StringComparer.Ordinal)
                                  .ToList();
        if (candidates.Count == 0) return null;

        string folderName = Path.GetFileName(Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar));
        string csproj = candidates.FirstOrDefault(c => string.Equals(Path.GetFileNameWithoutExtension(c), folderName, StringComparison.OrdinalIgnoreCase))
                     ?? candidates[0];

        return Load(csproj);
    }

    public static CodeProject Load(string csprojPath)
    {
        string full = Path.GetFullPath(csprojPath);
        string name = Path.GetFileNameWithoutExtension(full);
        string rootNamespace = name, assemblyName = name;

        try
        {
            var doc = XDocument.Load(full);
            rootNamespace = doc.Descendants("RootNamespace").FirstOrDefault()?.Value.Trim() is { Length: > 0 } ns ? ns : name;
            assemblyName  = doc.Descendants("AssemblyName").FirstOrDefault()?.Value.Trim() is { Length: > 0 } an ? an : name;
        }
        catch (Exception)
        {
            // A csproj that does not parse still names a project; MSBuild will report why.
        }

        return new CodeProject(Path.GetDirectoryName(full)!, full, rootNamespace, assemblyName);
    }
}
