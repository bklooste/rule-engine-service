using System.Text.RegularExpressions;

using Microsoft.Extensions.Options;

namespace Templates.Data;

/// <summary>
/// Bound from the "Templates" config section. Every property MUST appear in the README config table
/// (<c>Templates__PropertyName</c>) — <c>ReadmeConfigTableTests</c> fails otherwise.
/// </summary>
public sealed class TemplatesOptions
{
    public const string SectionName = "Templates";

    /// <summary>
    /// Prefix for every Redis key the service writes, so several environments can share one Redis.
    /// Empty (default) means <c>{ENVIRONMENT}:plat-templates:</c>, the shape plat-templates always used.
    /// </summary>
    public string KeyPrefix { get; set; } = "";

    /// <summary>Redis key for one template id; <c>{tenant}</c> is replaced by the template id.</summary>
    public string TenantKeyFormat { get; set; } = "{tenant}-templates";

    /// <summary>Name of the request header the gateway sets to the caller's identity; recorded as ChangedBy on writes.</summary>
    public string ChangedByHeader { get; set; } = "userid";
}

/// <summary>Fail-fast validation, written by hand because DataAnnotations is not trim/AOT safe.</summary>
public sealed partial class TemplatesOptionsValidator : IValidateOptions<TemplatesOptions>
{
    [GeneratedRegex("^[A-Za-z0-9:_.-]*$")]
    private static partial Regex PrefixPattern();

    public ValidateOptionsResult Validate(string? name, TemplatesOptions o)
    {
        var errors = new List<string>();
        if (!PrefixPattern().IsMatch(o.KeyPrefix)) errors.Add("Templates:KeyPrefix may contain only letters, digits and : _ . -");
        if (!o.TenantKeyFormat.Contains("{tenant}", StringComparison.Ordinal)) errors.Add("Templates:TenantKeyFormat must contain {tenant}");
        if (string.IsNullOrWhiteSpace(o.ChangedByHeader)) errors.Add("Templates:ChangedByHeader must not be empty");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
