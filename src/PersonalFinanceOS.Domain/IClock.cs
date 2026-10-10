namespace PersonalFinanceOS.Domain;

/// <summary>Time source, injectable so date-based rules (VISA periods, forecasts) are testable.</summary>
public interface IClock
{
    DateTimeOffset Now { get; }

    DateOnly Today => DateOnly.FromDateTime(Now.LocalDateTime);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}
