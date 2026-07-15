using BookLibrary.Api;
using BookLibrary.Contracts;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<RpcExceptionHandler>();
builder.Services.AddOpenApi();

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
