using System.Net.Http.Json;
using System.Text.Json;

namespace MyStack.Auth.Tests;

internal sealed record HealthPayload(
    string Status,
    double DurationMs,
    IReadOnlyList<HealthPayloadCheck> Checks
)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(
        JsonSerializerDefaults.Web
    );

    public static async Task<HealthPayload> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<HealthPayload>(SerializerOptions)
        ?? throw new InvalidOperationException("The health endpoint returned no body.");

    public HealthPayloadCheck Check(string name) =>
        Checks.SingleOrDefault(check => check.Name == name)
        ?? throw new InvalidOperationException(
            $"No check named '{name}'. Present: {string.Join(", ", Checks.Select(check => check.Name))}."
        );
}

internal sealed record HealthPayloadCheck(
    string Name,
    string Status,
    double DurationMs,
    string? Description
);
