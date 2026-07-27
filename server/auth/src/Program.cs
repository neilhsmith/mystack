using MyStack.Auth.Data;
using MyStack.Auth.Health;
using MyStack.Auth.Security;

var builder = WebApplication.CreateBuilder(args);

// `Server: Kestrel` names the target for whoever is scanning and buys nothing back.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.AddServerHeader = false);

builder.Services.AddAuthDatabase(builder.Configuration);
builder.Services.AddAuthIdentity();
builder.Services.AddAuthHealthChecks();
builder.Services.AddAuthorization();

builder.Services.AddHsts(hsts =>
{
    hsts.MaxAge = TimeSpan.FromDays(365);
    hsts.IncludeSubDomains = true;
    // Preload is deliberately off: it is a one-way door that puts the domain on a list shipped
    // inside browsers, which is an operator's decision rather than a framework default.
});

var app = builder.Build();

app.UseSecurityHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapAuthHealthChecks();

await app.RunAsync();

// WebApplicationFactory resolves the host through this type; top-level statements alone don't
// produce one the test project can name.
public partial class Program;
