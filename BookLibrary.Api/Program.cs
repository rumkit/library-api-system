using BookLibrary.Api;
using BookLibrary.Api.Caching;
using BookLibrary.Contracts;
using Microsoft.Extensions.Caching.Hybrid;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<RpcExceptionHandler>();
builder.Services.AddOpenApi();

// Cache the expensive insight aggregations at the REST edge. HybridCache runs an in-memory tier
// today; adding a distributed L2 (e.g. Redis) later is a registration-only change with no call-site
// edits. Per-endpoint TTLs come from CatalogCacheOptions; the default here is a backstop.
builder.Services.Configure<CatalogCacheOptions>(builder.Configuration.GetSection("CatalogCache"));
builder.Services.AddHybridCache(options =>
    options.DefaultEntryOptions = new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(5) });
builder.Services.AddSingleton<InsightCacheInvalidator>();

// Typed gRPC client for the Catalog backend. The host "catalog" is rewritten to the real
// endpoint by the Aspire service-discovery handler (added via ServiceDefaults); the scheme must
// be a plain one GrpcChannel understands (not the "https+http" discovery scheme). We use http/2
// cleartext (h2c) to the Catalog's HTTP/2 endpoint, which keeps internal comms free of TLS setup.
builder.Services
    .AddGrpcClient<CatalogService.CatalogServiceClient>(options =>
        options.Address = new Uri("http://catalog"));

var app = builder.Build();

app.UseExceptionHandler();

app.MapDefaultEndpoints();
app.MapOpenApi();
app.MapScalarApiReference(options => options.WithTitle("BookLibrary API"));
app.MapGet("/", () => Results.Redirect("/scalar/v1"));

app.MapCatalogEndpoints();

app.Run();

// Exposed so the system test host can reference the entry point.
public partial class Program;
