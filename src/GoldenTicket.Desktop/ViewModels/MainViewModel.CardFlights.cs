using GoldenTicket.Domain;

namespace GoldenTicket.Desktop.ViewModels;

public enum CardFlightSource { FaceUpTrain, TrainPile, DestinationPile }

/// <summary>Public card artwork only. Blind cards and destination tickets travel face down.</summary>
public sealed class CardFlightEventArgs(SeatId seatId, CardFlightSource source,
    int? marketSlot = null, TrainCardKind? visibleKind = null, int count = 1) : EventArgs
{
    private readonly List<Task> _presentations = [];
    public SeatId SeatId { get; } = seatId;
    public CardFlightSource Source { get; } = source;
    public int? MarketSlot { get; } = marketSlot;
    public TrainCardKind? VisibleKind { get; } = visibleKind;
    public int Count { get; } = count;

    /// <summary>Register a flight while handling CardDrawn; complete it on landing or removal.</summary>
    public void TrackPresentation(Task completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        _presentations.Add(completion);
    }

    internal Task PresentationCompletion => Task.WhenAll(_presentations);
}

public sealed partial class MainViewModel
{
    public event EventHandler<CardFlightEventArgs>? CardDrawn;

    internal bool CanPresentCardFlights => !IsGameInputPaused && _windowActive && _systemAvailable && !_toolsDisposed;

    private Task OnLocalTrainCardAccepted(SeatId seatId, int? sourceSlot, TrainCardKind kind) =>
        NotifyCardDrawn(new(seatId, sourceSlot is null ? CardFlightSource.TrainPile : CardFlightSource.FaceUpTrain,
            sourceSlot, sourceSlot is null ? null : kind));

    private Task OnLocalDestinationCardsAccepted(SeatId seatId, int count) =>
        NotifyCardDrawn(new(seatId, CardFlightSource.DestinationPile, count: count));

    private Task NotifyCardDrawn(CardFlightEventArgs flight)
    {
        // Drawing is already durable. A cosmetic failure must never turn an awarded card into
        // a reported failed action or prevent the subsequent board check.
        try { CardDrawn?.Invoke(this, flight); }
        catch (Exception ex)
        {
            BoardInteractionLog.Write("card-flight.presentation-failed", new { error = ex.GetType().Name });
        }
        return WaitForCardPresentationAsync(flight.PresentationCompletion);
    }

    private static async Task WaitForCardPresentationAsync(Task completion)
    {
        try
        {
            // Normal flights signal their actual landing. A removed or broken view must not
            // strand an already committed turn; no animation means no delay at all.
            await completion.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            BoardInteractionLog.Write("card-flight.completion-unavailable", new { error = ex.GetType().Name });
        }
    }
}
