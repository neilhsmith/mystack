using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace MyStack.Auth.Pages;

internal static class FormMarkup
{
    /// <summary>
    /// "true" when the field carries a model error, otherwise null — Razor omits the attribute
    /// entirely, which is what aria-invalid's tri-state expects.
    /// </summary>
    public static string? AriaInvalid(this ModelStateDictionary modelState, string field) =>
        modelState.TryGetValue(field, out var entry) && entry.Errors.Count > 0 ? "true" : null;
}
