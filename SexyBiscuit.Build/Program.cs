using SexyBiscuit.Engine.Build;

namespace SexyBiscuit.Build;

/// <summary>
/// <c>sbengine</c>: exports, publishes, packages and uploads a game from the command line.
/// </summary>
/// <remarks>
/// The pipeline lives in the engine so the editor can run it too; this project exists because
/// the engine is a class library and a CI job or an agent needs something to invoke. Run it as
/// <c>dotnet run --project SexyBiscuit.Build -- --project Games/Foo --all</c>, or build it once
/// and call <c>sbengine</c>.
/// </remarks>
internal static class Program
{
    private static int Main(string[] args) => ExportPipeline.RunCli(args);
}
