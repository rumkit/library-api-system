using BookLibrary.Catalog.Data;
using BookLibrary.Catalog.Mapping;
using BookLibrary.Catalog.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Mongo client + database, wired from the Aspire "library" connection string.
builder.AddMongoDBClient(connectionName: "library");
builder.Services.AddSingleton<LibraryDb>();
builder.Services.AddHostedService<MongoIndexInitializer>();

builder.Services.AddSingleton<CatalogMapper>();
builder.Services.AddScoped<IBookRepository, BookRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IInsightRepository, InsightRepository>();

builder.Services.AddGrpc();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapGrpcService<CatalogGrpcService>();
app.MapGet("/", () =>
    "BookLibrary.Catalog gRPC backend. Use the CatalogService gRPC contract (see BookLibrary.Api for the REST facade).");

app.Run();

// Exposed so the functional/system test hosts can reference the entry point.
public partial class Program;
