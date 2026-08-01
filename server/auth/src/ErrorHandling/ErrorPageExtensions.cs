using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Diagnostics;

namespace MyStack.Auth.ErrorHandling;

internal static class ErrorPageExtensions
{
    // The error surface splits on the Accept header. Browsers get the error page — re-executed
    // in place, so the status survives; a redirect would launder it into a 200. API callers get
    // ProblemDetails. Anything that already wrote a body — a rendered page, a token endpoint's
    // OAuth error JSON — passes through untouched.
    //
    // Two middlewares, and their order matters: the exception handler sits inside the
    // status-code shaping and writes ProblemDetails for API accepts only, leaving a browser's
    // 500 empty for the shaping outside it to render. It can't be the parameterless handler:
    // browsers say */*, which the default ProblemDetails writer takes as an invitation to serve
    // them JSON.
    public static IApplicationBuilder UseAuthErrorHandling(this IApplicationBuilder app)
    {
        app.UseStatusCodePages(async statusContext =>
        {
            var context = statusContext.HttpContext;

            if (!AcceptsHtml(context.Request))
            {
                await WriteProblemDetailsAsync(context);
                return;
            }

            var originalPath = context.Request.Path;
            var originalQueryString = context.Request.QueryString;

            context.Features.Set<IStatusCodeReExecuteFeature>(
                new StatusCodeReExecuteFeature
                {
                    OriginalPathBase = context.Request.PathBase.Value ?? string.Empty,
                    OriginalPath = originalPath.Value ?? string.Empty,
                    OriginalQueryString = originalQueryString.Value,
                }
            );

            // The endpoint and route values were already resolved for the original path; the
            // re-executed request has to be matched afresh.
            context.SetEndpoint(endpoint: null);
            context.Request.RouteValues.Clear();
            context.Request.Path = $"/error/{context.Response.StatusCode}";
            context.Request.QueryString = QueryString.Empty;

            try
            {
                await statusContext.Next(context);
            }
            finally
            {
                context.Request.Path = originalPath;
                context.Request.QueryString = originalQueryString;
                context.Features.Set<IStatusCodeReExecuteFeature?>(null);
            }
        });

        app.UseExceptionHandler(
            new ExceptionHandlerOptions
            {
                ExceptionHandler = async context =>
                {
                    if (!AcceptsHtml(context.Request))
                    {
                        await WriteProblemDetailsAsync(context);
                    }

                    // For browsers nothing is written: the middleware already set the bare 500,
                    // which the status-code shaping outside turns into the error page.
                },
            }
        );

        return app;
    }

    private static async Task WriteProblemDetailsAsync(HttpContext context)
    {
        var problem = new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Status = context.Response.StatusCode },
        };

        // A rejected OIDC request carries the protocol's own error; losing it would leave an
        // API caller a bare status where the spec promises a code and a description.
        if (context.GetOpenIddictServerResponse() is { Error: not null } oidc)
        {
            problem.ProblemDetails.Extensions["error"] = oidc.Error;
            problem.ProblemDetails.Detail = oidc.ErrorDescription;
        }

        await context
            .RequestServices.GetRequiredService<IProblemDetailsService>()
            .TryWriteAsync(problem);
    }

    // A navigating browser always says text/html; API clients say JSON or nothing.
    private static bool AcceptsHtml(HttpRequest request)
    {
        var accept = request.Headers.Accept.ToString();

        return accept.Contains("text/html", StringComparison.OrdinalIgnoreCase)
            || accept.Contains("application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
    }
}
