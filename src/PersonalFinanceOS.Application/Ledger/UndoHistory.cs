using PersonalFinanceOS.Domain.Transactions;

namespace PersonalFinanceOS.Application.Ledger;

/// <summary>One transaction as it was before and after an action. <c>Before == null</c>: it was created.</summary>
public sealed record UndoChange(Guid TransactionId, TransactionSnapshot? Before, TransactionSnapshot After);

/// <summary>A recent ledger action that can be reversed (spec §7 "undo for recent actions").</summary>
public sealed record UndoEntry(string ActionKey, IReadOnlyList<UndoChange> Changes);

/// <summary>
/// The recent actions of this session, newest on top. Lives in memory only: undo is for "oops, that was
/// wrong" moments; the audit log keeps the lasting record.
/// </summary>
public sealed class UndoHistory
{
    public const int Capacity = 20;

    private readonly LinkedList<UndoEntry> _entries = new();
    private readonly Lock _gate = new();

    public event EventHandler? Changed;

    public bool CanUndo
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count > 0;
            }
        }
    }

    /// <summary>Stable key of the action that would be undone, e.g. "created", for the UI to translate.</summary>
    public string? NextActionKey
    {
        get
        {
            lock (_gate)
            {
                return _entries.First?.Value.ActionKey;
            }
        }
    }

    public void Push(UndoEntry entry)
    {
        lock (_gate)
        {
            _entries.AddFirst(entry);
            while (_entries.Count > Capacity)
            {
                _entries.RemoveLast();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public UndoEntry? Peek()
    {
        lock (_gate)
        {
            return _entries.First?.Value;
        }
    }

    public void Remove(UndoEntry entry)
    {
        lock (_gate)
        {
            _entries.Remove(entry);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
