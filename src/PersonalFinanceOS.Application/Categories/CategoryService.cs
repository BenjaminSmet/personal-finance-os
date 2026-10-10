using PersonalFinanceOS.Application.Abstractions;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Budgets;
using PersonalFinanceOS.Domain.Categories;

namespace PersonalFinanceOS.Application.Categories;

public sealed record DefaultCategory(string Name, CategoryKind Kind);

/// <summary>Editable categories (spec §13): create, rename, merge, reorder, archive and bring back.</summary>
public sealed class CategoryService(ICategoryRepository categories, IBudgetRepository budgets, ISettingsStore settings, IAuditLog audit, IClock clock)
{
    /// <summary>Raised after categories change, so lists and budgets elsewhere refresh.</summary>
    public event EventHandler? Changed;

    /// <summary>Set once the starter categories were offered, so archiving them all never brings them back.</summary>
    public const string DefaultsCreatedKey = "categories.defaultsCreated";

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

    /// <summary>Brings an archived category back for new entries, at the end of its list.</summary>
    public async Task ReactivateAsync(Guid id, CancellationToken ct = default)
    {
        var category = await GetRequiredAsync(id, ct);
        var all = await categories.ListAsync(includeArchived: true, ct);
        RequireUniqueName(all, category.Name, category.Kind, exceptId: id);
        category.Reactivate(clock.Now);
        category.MoveTo(all.Max(c => c.SortOrder) + 1, clock.Now);
        await categories.UpdateAsync(category, ct);
        await audit.RecordAsync("Category", id, "Reactivated", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Moves a category one place up (<paramref name="offset"/> −1) or down (+1) among the active categories of
    /// its kind. The order is used in lists, budgets and reports.
    /// </summary>
    public async Task MoveAsync(Guid id, int offset, CancellationToken ct = default)
    {
        var list = (await categories.ListAsync(includeArchived: false, ct)).ToList();
        var category = list.FirstOrDefault(c => c.Id == id) ?? throw new DomainValidationException("category.notFound");
        var sameKind = list.Where(c => c.Kind == category.Kind).ToList();
        var index = sameKind.IndexOf(category);
        var target = index + Math.Sign(offset);
        if (offset == 0 || target < 0 || target >= sameKind.Count)
        {
            return;
        }

        sameKind.RemoveAt(index);
        sameKind.Insert(target, category);

        // Renumber the whole kind so equal or missing positions from earlier versions never cause a jump.
        var now = clock.Now;
        var changed = new List<Category>();
        for (var i = 0; i < sameKind.Count; i++)
        {
            if (sameKind[i].SortOrder != i)
            {
                sameKind[i].MoveTo(i, now);
                changed.Add(sameKind[i]);
            }
        }

        await categories.UpdateAsync(changed, ct);
        await audit.RecordAsync("Category", id, "Moved", ct);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Merges <paramref name="sourceId"/> into <paramref name="targetId"/> (same kind): every transaction, payee
    /// rule and recurring rule of the source moves to the target, and the source is archived. The target keeps its
    /// own budgets; only when it has none, the source's budgets move with it. One database transaction.
    /// </summary>
    public async Task MergeAsync(Guid sourceId, Guid targetId, CancellationToken ct = default)
    {
        var source = await GetRequiredAsync(sourceId, ct);
        var target = await GetRequiredAsync(targetId, ct);
        if (sourceId == targetId || source.Kind != target.Kind || !target.IsActive)
        {
            throw new DomainValidationException("category.merge.invalid");
        }

        var now = clock.Now;
        var all = await budgets.ListAsync(ct);
        var fromSource = all.Where(b => b.CategoryId == sourceId).ToList();
        var targetHasBudget = all.Any(b => b.CategoryId == targetId);
        if (!targetHasBudget)
        {
            fromSource.ForEach(b => b.MoveTo(targetId, now));
        }

        source.Archive(now);
        await categories.MergeAsync(source, targetId, targetHasBudget ? [] : fromSource, targetHasBudget ? fromSource : [], ct);
        await audit.RecordAsync("Category", sourceId, "Merged", ct);
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
