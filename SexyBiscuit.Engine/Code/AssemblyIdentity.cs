using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace SexyBiscuit.Engine.Code;

/// <summary>
/// Tells one build of an assembly from another. SDK builds are deterministic, so two identical
/// builds share a module version id and any change produces a new one — which makes the MVID
/// a reliable "was this game compiled against the engine I am running?" test.
/// </summary>
public static class AssemblyIdentity
{
    /// <summary>The MVID of the engine assembly this process has loaded.</summary>
    public static Guid RunningEngineMvid => typeof(AssemblyIdentity).Assembly.ManifestModule.ModuleVersionId;

    /// <summary>Reads an assembly's MVID from disk without loading it. Null for a missing or unreadable file.</summary>
    public static Guid? TryReadMvid(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe     = new PEReader(stream);
            if (!pe.HasMetadata) return null;

            var metadata = pe.GetMetadataReader();
            return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
