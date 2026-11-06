using UrlShortener.Api;
using UrlShortener.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddUrlShortener(builder.Configuration);

var app = builder.Build();
app.UseUrlShortenerPipeline();

app.MapHealthEndpoints();
app.MapLinkEndpoints();
app.MapRedirectEndpoints();

app.Run();

/// <summary>Exposed for WebApplicationFactory integration tests.</summary>
public partial class Program;
