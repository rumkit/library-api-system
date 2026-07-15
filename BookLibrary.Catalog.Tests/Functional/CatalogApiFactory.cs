using BookLibrary.Catalog.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;

namespace BookLibrary.Catalog.Tests.Functional;

/// <summary>
/// Boots the real Catalog gRPC host in memory, but points its <see cref="LibraryDb"/> at a
/// caller-chosen database on the Testcontainers Mongo instance. Everything else — DI, the gRPC
/// pipeline, validation, mapping, the index initializer — runs exactly as in production.
/// </summary>
public sealed class CatalogApiFactory(string connectionString, string databaseName)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Satisfy AddMongoDBClient's configuration lookup; the override below is what actually gets used.
        builder.UseSetting("ConnectionStrings:library", connectionString);

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<LibraryDb>();
            services.AddSingleton(new LibraryDb(new MongoClient(connectionString).GetDatabase(databaseName)));
        });
    }
}
