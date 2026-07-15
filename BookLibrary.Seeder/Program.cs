using BookLibrary.Catalog.Data;
using BookLibrary.Seeder;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddMongoDBClient(connectionName: "library");
builder.Services.AddSingleton<LibraryDb>();

var host = builder.Build();

// Run once and exit — this is a one-shot job, not a long-lived service.
var db = host.Services.GetRequiredService<LibraryDb>();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("BookLibrary.Seeder");
await SeedRunner.RunAsync(db, logger, CancellationToken.None);
