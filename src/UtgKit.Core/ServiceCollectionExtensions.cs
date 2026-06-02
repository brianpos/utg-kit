using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UtgKit.Core.Services;

namespace UtgKit.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers UtgKit.Core services and binds the <see cref="ThoRepoSettings"/>
    /// configuration section.
    /// </summary>
    public static IServiceCollection AddUtgKitCore(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ThoRepoSettings>(configuration.GetSection(ThoRepoSettings.SectionName));
        services.AddSingleton<ThoFileService>();
        services.AddSingleton<ThoResourceResolver>();
        services.AddSingleton<ValueSetExpansionService>();
        services.AddHttpClient<ImportCodeSystemService>(ConfigureClient);
        services.AddHttpClient<ImportValueSetService>(ConfigureClient);
        services.AddHostedService<ThoFileWatcher>();
        return services;
    }

	static internal void ConfigureClient(HttpClient client)
	{
		client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("UtgKit", "0.10.0"));
	}
}
