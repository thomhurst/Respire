using System.Globalization;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Respire.Streaming.Dashboard;

/// <summary>Registers and maps the authorized stream dashboard.</summary>
public static class RespireDashboardExtensions
{
    /// <summary>Registers dashboard configuration and antiforgery services. Register an IRespireClient separately.</summary>
    public static IServiceCollection AddRespireStreamingDashboard(this IServiceCollection services,
        Action<RespireDashboardOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new RespireDashboardOptions();
        configure(options);
        // Freeze the allowlist when registration completes, before any request can observe it.
        var frozen = new RespireDashboardOptions { EntryLimit = options.EntryLimit, MetricsHistoryMinutes = options.MetricsHistoryMinutes };
        foreach (var stream in options.Streams)
            frozen.Streams.Add(new() { Name = stream.Name, Stream = stream.Stream, Group = stream.Group,
                DeadLetterStream = stream.DeadLetterStream, IsJobQueue = stream.IsJobQueue });
        services.AddAntiforgery();
        services.AddSingleton(provider => new DashboardState(provider.GetRequiredService<IRespireClient>(), frozen));
        return services;
    }

    /// <summary>Maps HTML inspection and antiforgery-protected POST actions under an explicit named authorization policy.</summary>
    /// <remarks>Configure authentication, the named authorization policy, and authentication/authorization middleware.
    /// All endpoints require this policy. The dashboard reads only registered keys and never accepts a key from a request.</remarks>
    public static IEndpointConventionBuilder MapRespireStreamingDashboard(this IEndpointRouteBuilder endpoints,
        string pattern, string authorizationPolicy)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(authorizationPolicy);
        if (!pattern.StartsWith('/') || pattern.Contains('{') || pattern.Contains('}') || pattern.Contains('?') || pattern.Contains('#'))
            throw new ArgumentException("Use an absolute literal route prefix.", nameof(pattern));
        var path = pattern.TrimEnd('/');
        var state = endpoints.ServiceProvider.GetRequiredService<DashboardState>();
        var group = endpoints.MapGroup(path).RequireAuthorization(authorizationPolicy);
        group.MapGet("/", async (HttpContext context) =>
        {
            ProtectResponse(context);
            var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
            var tokens = antiforgery.GetAndStoreTokens(context);
            var snapshot = await state.ReadAsync(context.RequestAborted).ConfigureAwait(false);
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(DashboardPage.Render(snapshot,
                DashboardPath(context, false), tokens.FormFieldName, tokens.RequestToken!), context.RequestAborted).ConfigureAwait(false);
        });
        group.MapGet("/{index:int}/logs", async (HttpContext context) =>
        {
            ProtectResponse(context);
            var returnPath = DashboardPath(context, true);
            var ids = context.Request.Query["jobId"];
            if (ids.Count != 1 || string.IsNullOrWhiteSpace(ids[0]) || ids[0]!.Length > 4096)
            {
                await WriteErrorAsync(context, returnPath, 400, "Enter one job ID of at most 4096 characters.").ConfigureAwait(false);
                return;
            }
            var index = int.Parse(context.Request.RouteValues["index"]!.ToString()!, CultureInfo.InvariantCulture);
            try
            {
                var logs = await state.ReadLogsAsync(index, ids[0]!, context.RequestAborted).ConfigureAwait(false);
                if (logs is null)
                {
                    await WriteErrorAsync(context, returnPath, 404, "This job queue is not configured.").ConfigureAwait(false);
                    return;
                }
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(DashboardPage.Render(new([]), returnPath, "", "", logs),
                    context.RequestAborted).ConfigureAwait(false);
            }
            catch (RespireException)
            {
                await WriteErrorAsync(context, returnPath, 503,
                    "Job logs are unavailable. Check Redis permissions and the configured queue.").ConfigureAwait(false);
            }
        });
        group.MapPost("/{index:int}/{action}", async (HttpContext context) =>
        {
            ProtectResponse(context);
            var returnPath = DashboardPath(context, true);
            var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
            try { await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false); }
            catch (AntiforgeryValidationException)
            {
                await WriteErrorAsync(context, returnPath, 400, "The form expired or could not be verified. Refresh the dashboard and try again.").ConfigureAwait(false);
                return;
            }
            var action = context.Request.RouteValues["action"]?.ToString();
            if (action is not ("retry" or "delete"))
            {
                await WriteErrorAsync(context, returnPath, 404, "This dashboard action does not exist.").ConfigureAwait(false);
                return;
            }
            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            var ids = form["id"];
            if (ids.Count != 1 || !IsEntryId(ids[0]))
            {
                await WriteErrorAsync(context, returnPath, 400, "The entry ID is invalid.").ConfigureAwait(false);
                return;
            }
            var index = int.Parse(context.Request.RouteValues["index"]!.ToString()!, CultureInfo.InvariantCulture);
            try
            {
                var result = await state.MutateAsync(index, ids[0]!, action == "retry", context.RequestAborted).ConfigureAwait(false);
                if (result <= 0)
                {
                    await WriteErrorAsync(context, returnPath, result == -1 ? 409 : 404, result == -1
                        ? "The entry metadata does not match this stream and group. No entry was changed."
                        : "The entry or configured dead-letter stream was not found. Another operator may have handled it.").ConfigureAwait(false);
                    return;
                }
                context.Response.StatusCode = StatusCodes.Status303SeeOther;
                context.Response.Headers.Location = returnPath.TrimEnd('/') + "/";
            }
            catch (RespireServerException exception) when (exception.Code == "NOPERM")
            {
                await WriteErrorAsync(context, returnPath, 403,
                    "Redis permissions do not allow this action. Ask an administrator to update the dashboard Redis user's permissions.").ConfigureAwait(false);
            }
            catch (RespireException)
            {
                await WriteErrorAsync(context, returnPath, 503,
                    "Redis could not confirm the action. Refresh the dashboard to inspect the entry before trying again.").ConfigureAwait(false);
            }
        });
        return group;
    }

    private static Task WriteErrorAsync(HttpContext context, string path, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        var href = HtmlEncoder.Default.Encode(path.TrimEnd('/') + "/");
        return context.Response.WriteAsync("<!doctype html><html lang=\"en\"><meta charset=\"utf-8\">"
            + "<title>Dashboard action</title><h1>Dashboard action</h1><p>" + HtmlEncoder.Default.Encode(message)
            + "</p><a href=\"" + href + "\">Return to dashboard</a></html>", context.RequestAborted);
    }

    private static string DashboardPath(HttpContext context, bool action)
    {
        var requestPath = context.Request.Path.Value!.TrimEnd('/');
        if (action)
        {
            requestPath = requestPath[..requestPath.LastIndexOf('/')];
            requestPath = requestPath[..requestPath.LastIndexOf('/')];
        }
        var path = context.Request.PathBase.Add(new PathString(requestPath)).ToUriComponent();
        return path.Length == 0 ? "/" : path;
    }

    private static bool IsEntryId(string? value)
    {
        if (value is null || value.Length > 41) return false;
        var separator = value.IndexOf('-');
        return separator > 0
            && ulong.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && ulong.TryParse(value.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static void ProtectResponse(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
        context.Response.Headers.XContentTypeOptions = "nosniff";
    }
}
