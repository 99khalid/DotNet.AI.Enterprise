using DotNet.AI.Enterprise.Application;
using DotNet.AI.Enterprise.Infrastructure;
using DotNet.AI.Enterprise.WebApi.Conversations;
using DotNet.AI.Enterprise.WebApi.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// Same registrations as the ConsoleApp — only the presentation layer differs.
builder.Services
    .AddApplication()
    .AddInfrastructure(builder.Configuration);

builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ConversationStore>();
builder.Services.AddProblemDetails();

var app = builder.Build();

app.UseExceptionHandler();
app.UseDefaultFiles();   // "/" → wwwroot/index.html
app.UseStaticFiles();    // serves the test UI from wwwroot

app.MapHrEndpoints();

app.Run();
