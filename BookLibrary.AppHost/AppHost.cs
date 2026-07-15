var builder = DistributedApplication.CreateBuilder(args);

// MongoDB container + the "library" database used by the Catalog backend and the seeder.
var mongo = builder.AddMongoDB("mongo")
    .WithMongoExpress();
var library = mongo.AddDatabase("library");

// Domain/persistence backend: the gRPC server. Waits for Mongo to be ready.
var catalog = builder.AddProject<Projects.BookLibrary_Catalog>("catalog")
    .WithReference(library)
    .WaitFor(library);

// One-shot seeder: populates sample data once, then exits. Independent of the services.
builder.AddProject<Projects.BookLibrary_Seeder>("seeder")
    .WithReference(library)
    .WaitFor(library);

// REST edge: HTTP facade + Scalar docs, gRPC client of the catalog backend.
builder.AddProject<Projects.BookLibrary_Api>("api")
    .WithReference(catalog)
    .WaitFor(catalog);

builder.Build().Run();
