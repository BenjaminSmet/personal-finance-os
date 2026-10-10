namespace PersonalFinanceOS.Domain;

/// <summary>
/// A business-rule violation. <see cref="Code"/> is a stable, language-neutral key that the UI
/// translates, so validation stays independent of the interface language (spec §20).
/// </summary>
public sealed class DomainValidationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
