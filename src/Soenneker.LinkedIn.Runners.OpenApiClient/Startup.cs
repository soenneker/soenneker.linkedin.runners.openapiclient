using Microsoft.Extensions.DependencyInjection;
using Soenneker.Kiota.Util.Registrars;
using Soenneker.Managers.Runners.Registrars;
using Soenneker.OpenApi.Fixer.Registrars;
using Soenneker.LinkedIn.Runners.OpenApiClient.Utils;
using Soenneker.LinkedIn.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.LinkedIn.Runners.OpenApiClient.Documentation;
using Soenneker.Playwrights.Installation.Registrars;

namespace Soenneker.LinkedIn.Runners.OpenApiClient;

/// <summary>
/// Console type startup
/// </summary>
public static class Startup
{
    // This method gets called by the runtime. Use this method to add services to the container.
    public static void ConfigureServices(IServiceCollection services)
    {
        services.SetupIoC();
    }

    public static IServiceCollection SetupIoC(this IServiceCollection services)
    {
        services.AddHostedService<ConsoleHostedService>()
                .AddSingleton<IFileOperationsUtil, FileOperationsUtil>()
                .AddSingleton<ILearnDocumentationSource, LearnDocumentationSource>()
                .AddSingleton<IDocumentationSpecGenerator, DocumentationSpecGenerator>()
                .AddPlaywrightInstallationUtilAsSingleton()
                .AddRunnersManagerAsSingleton()
                .AddOpenApiFixerAsSingleton()
                .AddKiotaUtilAsSingleton();

        return services;
    }
}
