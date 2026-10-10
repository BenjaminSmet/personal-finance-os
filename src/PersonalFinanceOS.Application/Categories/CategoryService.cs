using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Categories;

namespace PersonalFinanceOS.Application.Categories;

public sealed record DefaultCategory(string Name, CategoryKind Kind);

/// <summary>Editable categories (spec §13). Merging and reordering arrive with budgets in phase 5.</summary>
public sealed class CategoryService(ICategoryRepository categories, ISettingsStore settings, IAuditLog audit, IClock clock)
{
    /// <summary>Set once the starter categories were offered, so archiving them all never brings them back.</summary>
    public const string DefaultsCreatedKey = "categories.defaultsCreated";

    public event EventHandler? Changed;

    public Task<IReadOnlyList<Category>> ListAsync(bool includeArchived = false, CancellationToken ct = default) =>
        categories.ListAsync(includeArchived, ct);

    public async Task<Category> CreateAsync(string name, CategoryKind kind, CancellationToken ct = default)
    {
        var existing = await categories.ListAsync(includeArchived: true, ct);
        RequireUniqueName(existing, name, kind, exceptId: null);
        var category = Category.Create(name, kind, existing.Count == 0 ? 0 : existing.Max(c => c.SortOrder) + 1, clock.Now);
        await categories.AddAsync([category], ct);
        await audit.RecordAsync("Category", category.Id, "Created", ct);
        Changed?.Invoke(this, EventArgs.Empty);
        return category;
    }

    public async Task RenameAsync(Guid id, string name, CancellationToken ct = default)
    {
        var category = await GetRequiredAsync(id, ct);
        RequireUniqueName(await categories.ListAsync(includeArchived: true, ct), name, category.Kind, exceptId: id);
        category.Rename(name, clock.Now);
        await categories.UpdateAsync(category, ct);
        await audit.RecordAsync("Category", id, "Renamed", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Hides the category for new entries. Existing transactions keep it.</summary>
    public async Task ArchiveAsync(Guid id, CancellationToken ct = default)
    {
        var category = await GetRequiredAsync(id, ct);
        category.Archive(clock.Now);
        await categories.UpdateAsync(category, ct);
        await audit.RecordAsync("Category", id, "Archived", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Creates the starter categories once, in the language the user has at that moment (spec §13 "default
    /// editable categories"). Does nothing when categories already exist or were created before.
    /// </summary>
    public async Task EnsureDefaultsAsync(IReadOnlyList<DefaultCategory> defaults, CancellationToken ct = default)
    {
        if (await settings.GetAsync(DefaultsCreatedKey, ct) is not null)
        {
            return;
        }

        if ((await categories.ListAsync(includeArchived: true, ct)).Count == 0)
        {
            var now = clock.Now;
            await categories.AddAsync(defaults.Select((d, i) => Category.Create(d.Name, d.Kind, i, now)).ToList(), ct);
            await audit.RecordAsync("Category", null, "DefaultsCreated", ct);
        }

        await settings.SetAsync(DefaultsCreatedKey, "1", ct);
    }

    private static void RequireUniqueName(IReadOnlyList<Category> existing, string name, CategoryKind kind, Guid? exceptId)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (existing.Any(c => c.Id != exceptId && c.IsActive && c.Kind == kind &&
                              string.Equals(c.Name, trimmed, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new DomainValidationException("category.name.duplicate");
        }
    }

    private async Task<Category> GetRequiredAsync(Guid id, CancellationToken ct) =>
        await categories.FindAsync(id, ct) ?? throw new DomainValidationException("category.notFound");
}
