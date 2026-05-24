var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/hello", () => new { message = "hello from mystack" });
app.MapGet("/health", () => new { status = "ok" });

app.Run();
