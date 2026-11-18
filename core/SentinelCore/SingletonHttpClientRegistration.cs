using Microsoft.Extensions.DependencyInjection;

namespace SentinelCore;

public static class SingletonHttpClientRegistration
{
    // AddHttpClient<TClient, TImplementation>() looks like the right call for a service that needs
    // an HttpClient, but it registers the service as *transient*. For the services that download and
    // hold an ONNX model (FastLocalDetector, ImageEmbeddingService) that is wrong twice over:
    //  - every consumer got its own instance with its own init lock, so on a fresh install each alert
    //    arriving during the ~100MB detector download started another download into the same temp
    //    file, truncating the first - with a busy camera it could keep restarting indefinitely;
    //  - every resolution loaded its own copy of the model, and in the dashboard, where the embedding
    //    service is an endpoint parameter, each visual search loaded (and threw away) ~577MB of CLIP.
    // This keeps the factory-managed HttpClient but registers one shared instance.
    public static IServiceCollection AddSingletonWithHttpClient<TService, TImplementation>(this IServiceCollection services)
        where TService : class
        where TImplementation : class, TService
    {
        var clientName = typeof(TImplementation).Name;
        services.AddHttpClient(clientName);
        services.AddSingleton<TService>(sp => ActivatorUtilities.CreateInstance<TImplementation>(
            sp, sp.GetRequiredService<IHttpClientFactory>().CreateClient(clientName)));
        return services;
    }
}
