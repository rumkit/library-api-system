using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core.Interfaces;

namespace BookLibrary.SystemTests;

/// <summary>
/// Boots the entire distributed application (Mongo container, Catalog, Seeder, Api) once for the
/// whole test assembly, then exposes an HTTP client for the REST edge. Waits for the seeder to
/// finish so the flow tests observe a fully populated catalog. Requires Docker.
/// </summary>
public sealed class AppHostFixture : IAsyncInitializer, IAsyncDisposable
{
    private DistributedApplication? _app;

    public HttpClient ApiClient { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.BookLibrary_AppHost>();
        builder.Services.ConfigureHttpClientDefaults(client => client.AddStandardResilienceHandler());

        _app = await builder.BuildAsync();
        var notifications = _app.Services.GetRequiredService<ResourceNotificationService>();

        await _app.StartAsync();

        // The seeder is a one-shot job; wait for it to complete before the catalog is queried.
        await notifications
            .WaitForResourceAsync("seeder", KnownResourceStates.Finished)
            .WaitAsync(TimeSpan.FromMinutes(3));
        await notifications
            .WaitForResourceAsync("api", KnownResourceStates.Running)
            .WaitAsync(TimeSpan.FromMinutes(2));

        // Use the plain HTTP endpoint — the dev TLS cert isn't trusted inside the test host.
        ApiClient = _app.CreateHttpClient("api", "http");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }
}
