using PersonalFinanceOS.Application.Cards;
using PersonalFinanceOS.Application.Planning;
using PersonalFinanceOS.Domain;
using PersonalFinanceOS.Domain.Accounts;
using PersonalFinanceOS.Domain.Wishlist;

namespace PersonalFinanceOS.Application.Wishlist;

/// <summary>Spec §14: "technically affordable" is not the same as "financially prudent".</summary>
public enum AffordabilityVerdict
{
    /// <summary>The balance stays above the minimum buffer through every expected bill, card statement and income in the look-ahead.</summary>
    Prudent,

    /// <summary>The money is there (on the account, or as card credit) but bills or the buffer would come under pressure.</summary>
    TechnicallyOnly,

    /// <summary>Not enough money or credit for it now.</summary>
    NotAffordable,

    /// <summary>Information is missing, so no verdict is given rather than a guess (spec §23).</summary>
    CannotCalculate,
}

public enum PaymentMethod
{
    /// <summary>Paid now from the spendable accounts.</summary>
    CurrentAccount,

    /// <summary>Paid by credit card: the money leaves when the statement is settled.</summary>
    CreditCard,

    /// <summary>Wait until it is comfortably affordable.</summary>
    Postpone,
}

/// <summary>Why a payment option has the verdict it has, when that needs saying.</summary>
public enum PaymentNote
{
    None,
    CardNotSetUp,
    SettlementAccountUnknown,

    /// <summary>The statement is paid from an account the forecast does not count (for example the joint account).</summary>
    SettlementAccountNotCounted,
    OverLimit,

    /// <summary>No day within the scanned year: usually because no income is planned.</summary>
    NoDateFound,
    BeforeTargetDate,
    AfterTargetDate,
}

/// <summary>One way to pay, with its own verdict (spec §14: compare current account, VISA and postponing).</summary>
public sealed record PaymentOption(
    PaymentMethod Method,
    AffordabilityVerdict Verdict,
    PaymentNote Note,

    /// <summary>The card, for <see cref="PaymentMethod.CreditCard"/>.</summary>
    Account? Card = null,

    /// <summary>When the money leaves the spendable accounts: today, the statement debit day, or the earliest prudent day.</summary>
    DateOnly? PaidOn = null,

    /// <summary>The lowest expected balance in the look-ahead with this option.</summary>
    Money? LowestBalance = null,
    Money? AvailableCreditAfter = null,

    /// <summary>The card's current statement including this purchase.</summary>
    Money? StatementAfter = null);

/// <summary>The short answer for one item in the list.</summary>
public sealed record AffordabilitySummary(AffordabilityVerdict Verdict, DateOnly? EarliestPrudent);

/// <summary>How buying this item changes the earliest comfortable date of another open item.</summary>
public sealed record WishlistEffect(WishlistItem Item, DateOnly? Before, DateOnly? After)
{
    public bool IsDelayed => Before != After;
}

/// <summary>The full affordability answer for one price (spec §14). A forecast: it never changes data.</summary>
public sealed record Affordability(
    Money Price,
    bool CanCalculate,
    DateOnly Today,

    /// <summary>Balance of the spendable accounts now, as recorded (bills due today are in the lowest balance, not here).</summary>
    Money CashNow,

    /// <summary>Balance of the spendable accounts right after paying now.</summary>
    Money CashAfter,
    Money Buffer,

    /// <summary>The lowest expected balance in the look-ahead when bought today from the current account.</summary>
    Money LowestAfter,
    DateOnly LowestOn,
    DateOnly LookAheadUntil,
    DateOnly? NextIncome,

    /// <summary>The minimum buffer stays intact.</summary>
    bool BufferIntact,

    /// <summary>Every expected bill and card statement in the look-ahead can still be paid (the balance stays at or above zero).</summary>
    bool BillsCovered,
    AffordabilityVerdict Verdict,

    /// <summary>The first day from today on which buying it is prudent, within a year. Null when there is none.</summary>
    DateOnly? EarliestPrudent,
    IReadOnlyList<PaymentOption> Options,
    IReadOnlyList<WishlistEffect> Effects,
    IReadOnlyList<ForecastCaveat> Caveats)
{
    public static Affordability Unknown(Money price, DateOnly today) =>
        new(price, false, today, Money.Zero, Money.Zero, Money.Zero, Money.Zero, today, today, null, false, false,
            AffordabilityVerdict.CannotCalculate, null, [], [], []);
}

/// <summary>
/// Can the user afford a purchase, and when (spec §14, §23 wishlist)? Built on the same forecast as safe-to-spend:
/// recorded transactions, open planned items, card statements, expected income and the minimum buffer. Money on
/// savings, the joint account or as card credit is never treated as spendable cash.
/// </summary>
public sealed class AffordabilityService(ForecastService forecast, CreditCardService cards)
{
    /// <summary>How far ahead a purchase must keep the buffer intact when income is planned: about three pay cycles.</summary>
    public const int LookAheadDays = 90;

    /// <summary>Without planned income the balance only goes down, so the look-ahead is the safe-to-spend horizon.</summary>
    public const int LookAheadWithoutIncomeDays = ForecastService.DefaultHorizonDays;

    /// <summary>How far the earliest comfortable date is searched.</summary>
    public const int ScanDays = 365;

    public async Task<Affordability> CheckAsync(Money price, WishlistItem? item = null, IReadOnlyList<WishlistItem>? others = null, CancellationToken ct = default)
    {
        var projection = await forecast.ProjectAsync(ScanDays + LookAheadDays, ct);
        if (!projection.CanCalculate || !price.IsPositive)
        {
            return Affordability.Unknown(price, projection.Today);
        }

        var today = projection.Today;
        var look = LookAhead(projection);
        var cashNow = projection.Start;
        var (verdict, lowestAfter, lowestOn) = BuyToday(projection, price);
        var bufferIntact = lowestAfter >= projection.Buffer;
        var earliest = EarliestPrudent(projection, price);

        var options = new List<PaymentOption>
        {
            new(PaymentMethod.CurrentAccount, verdict, PaymentNote.None, PaidOn: today, LowestBalance: lowestAfter),
        };
        options.AddRange(await CardOptionsAsync(projection, price, look, ct));
        var postponeNote = earliest is null ? PaymentNote.NoDateFound
            : item?.TargetDate is { } target ? (earliest <= target ? PaymentNote.BeforeTargetDate : PaymentNote.AfterTargetDate)
            : PaymentNote.None;
        options.Add(new PaymentOption(
            PaymentMethod.Postpone,
            earliest is null ? AffordabilityVerdict.NotAffordable : AffordabilityVerdict.Prudent,
            postponeNote,
            PaidOn: earliest));

        // What this purchase does to the other plans: bought today when the money is there, else on its earliest day.
        var effects = new List<WishlistEffect>();
        DateOnly? boughtOn = verdict != AffordabilityVerdict.NotAffordable ? today : earliest;
        if (boughtOn is { } on)
        {
            var after = projection.With(new CashEvent(on, price.Negate(), CashEventKind.Purchase, item?.Name));
            foreach (var other in (others ?? []).Where(o => o.IsOpen && o.Id != item?.Id))
            {
                effects.Add(new WishlistEffect(other, EarliestPrudent(projection, other.ExpectedPrice), EarliestPrudent(after, other.ExpectedPrice)));
            }
        }

        return new Affordability(
            price, true, today, cashNow, cashNow - price, projection.Buffer, lowestAfter, lowestOn, today.AddDays(look),
            projection.NextIncomeAfter(today), bufferIntact, lowestAfter >= Money.Zero, verdict, earliest, options, effects, projection.Caveats);
    }

    /// <summary>The verdict and earliest comfortable day of several items at once, from one forecast (for the list).</summary>
    public async Task<IReadOnlyDictionary<Guid, AffordabilitySummary>> SummariesAsync(IEnumerable<WishlistItem> items, CancellationToken ct = default)
    {
        var projection = await forecast.ProjectAsync(ScanDays + LookAheadDays, ct);
        return items.ToDictionary(
            i => i.Id,
            i => projection.CanCalculate
                ? new AffordabilitySummary(BuyToday(projection, i.ExpectedPrice).Verdict, EarliestPrudent(projection, i.ExpectedPrice))
                : new AffordabilitySummary(AffordabilityVerdict.CannotCalculate, null));
    }

    /// <summary>Buying today from the spendable accounts: the verdict and the lowest balance it leaves in the look-ahead.</summary>
    private static (AffordabilityVerdict Verdict, Money LowestAfter, DateOnly LowestOn) BuyToday(CashProjection projection, Money price)
    {
        var today = projection.Today;
        var (lowest, lowestOn) = projection.Lowest(today, today.AddDays(LookAhead(projection)));
        var lowestAfter = lowest - price;
        var verdict = lowestAfter >= projection.Buffer ? AffordabilityVerdict.Prudent
            : projection.Start >= price ? AffordabilityVerdict.TechnicallyOnly
            : AffordabilityVerdict.NotAffordable;
        return (verdict, lowestAfter, lowestOn);
    }

    /// <summary>The first day, from today, on which buying at <paramref name="price"/> keeps the buffer through the look-ahead.</summary>
    public static DateOnly? EarliestPrudent(CashProjection projection, Money price)
    {
        var look = LookAhead(projection);
        for (var day = 0; day <= ScanDays; day++)
        {
            var date = projection.Today.AddDays(day);
            if (projection.Lowest(date, date.AddDays(look)).Balance - price >= projection.Buffer)
            {
                return date;
            }
        }

        return null;
    }

    private static int LookAhead(CashProjection projection) => projection.HasIncome ? LookAheadDays : LookAheadWithoutIncomeDays;

    private async Task<IEnumerable<PaymentOption>> CardOptionsAsync(CashProjection projection, Money price, int look, CancellationToken ct)
    {
        var today = projection.Today;
        var counted = projection.Accounts.Select(a => a.Id).ToHashSet();
        var result = new List<PaymentOption>();
        foreach (var status in await cards.ListStatusesAsync(ct))
        {
            if (status.Profile is not { } profile)
            {
                result.Add(new PaymentOption(PaymentMethod.CreditCard, AffordabilityVerdict.CannotCalculate, PaymentNote.CardNotSetUp, status.Card));
                continue;
            }

            var creditAfter = status.AvailableCredit - price;
            var statementAfter = status.EstimatedStatement + price;
            var due = CardMath.Due(profile, status.CurrentPeriod, today);
            if (creditAfter.IsNegative)
            {
                result.Add(new PaymentOption(PaymentMethod.CreditCard, AffordabilityVerdict.NotAffordable, PaymentNote.OverLimit, status.Card, due, null, creditAfter, statementAfter));
                continue;
            }

            if (profile.SettlementAccountId is not { } from)
            {
                result.Add(new PaymentOption(PaymentMethod.CreditCard, AffordabilityVerdict.CannotCalculate, PaymentNote.SettlementAccountUnknown, status.Card, due, null, creditAfter, statementAfter));
                continue;
            }

            if (!counted.Contains(from))
            {
                result.Add(new PaymentOption(PaymentMethod.CreditCard, AffordabilityVerdict.CannotCalculate, PaymentNote.SettlementAccountNotCounted, status.Card, due, null, creditAfter, statementAfter));
                continue;
            }

            // The purchase joins this period's statement and leaves the paying account on its debit day.
            var withCard = projection.With(new CashEvent(due, price.Negate(), CashEventKind.Purchase, status.Card.Name));
            var lowest = withCard.Lowest(today, due.AddDays(look)).Balance;
            var verdict = lowest >= projection.Buffer ? AffordabilityVerdict.Prudent : AffordabilityVerdict.TechnicallyOnly;
            result.Add(new PaymentOption(PaymentMethod.CreditCard, verdict, PaymentNote.None, status.Card, due, lowest, creditAfter, statementAfter));
        }

        return result;
    }
}
