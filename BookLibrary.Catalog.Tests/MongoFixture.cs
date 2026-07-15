using BookLibrary.Catalog.Data;
using MongoDB.Driver;
using Testcontainers.MongoDb;
using TUnit.Core.Interfaces;

namespace BookLibrary.Catalog.Tests;

/// <summary>
/// Spins up a single throwaway MongoDB container for the whole test assembly (shared via TUnit's
/// <c>ClassDataSource(PerAssembly)</c>). Each test asks for its own uniquely-named database so the
/// aggregation tests stay isolated without paying for a container per test. Requires Docker.
/// </summary>
public sealed class MongoFixture : IAsyncInitializer, IAsyncDisposable
{
    private readonly MongoDbContainer _container = new MongoDbBuilder("mongo:8.0").Build();

    private IMongoClient _client = null!;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        _client = new MongoClient(_container.GetConnectionString());
    }

    /// <summary>A <see cref="LibraryDb"/> over a fresh, empty database for a single test.</summary>
    public LibraryDb NewDatabase() =>
        new(_client.GetDatabase("test_" + Guid.NewGuid().ToString("N")));

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}
