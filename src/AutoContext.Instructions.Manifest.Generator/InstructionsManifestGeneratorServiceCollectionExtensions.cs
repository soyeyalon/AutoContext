namespace AutoContext.Instructions.Manifest.Generator;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the instructions-manifest generator and its collaborators on a
/// host's service collection.
/// </summary>
internal static class InstructionsManifestGeneratorServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="InstructionsManifestGenerator"/>,
    /// <see cref="ICorpusParser"/>,
    /// <see cref="IInstructionsCatalogReader"/>,
    /// <see cref="IMcpToolsRegistryReader"/>,
    /// <see cref="IInstructionsManifestBuilder"/>,
    /// <see cref="IInstructionsManifestSerializer"/>,
    /// <see cref="IInstructionsReferenceValidator"/>, and
    /// <see cref="IInstructionsToolObligationValidator"/> to
    /// <paramref name="services"/>.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is
    /// <see langword="null"/>.</exception>
    public static IServiceCollection AddInstructionsManifestGenerator(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<ICorpusParser, CorpusParser>();
        services.AddSingleton<IInstructionsCatalogReader, InstructionsCatalogReader>();
        services.AddSingleton<IMcpToolsRegistryReader, McpToolsRegistryReader>();
        services.AddSingleton<IInstructionsManifestBuilder, InstructionsManifestBuilder>();
        services.AddSingleton<IInstructionsManifestSerializer, InstructionsManifestSerializer>();
        services.AddSingleton<IInstructionsReferenceValidator, InstructionsReferenceValidator>();
        services.AddSingleton<IInstructionsToolObligationValidator, InstructionsToolObligationValidator>();
        services.AddSingleton<InstructionsManifestGenerator>();

        return services;
    }
}
