namespace GoldenTicket.Domain.Model;

/// <summary>
/// One seat at the table. DESIGN 1.1 keeps the referee and the opponents separate: this record
/// says who operates the seat, never what that seat knows.
/// </summary>
public sealed record Seat(
    SeatId SeatId,
    string DisplayName,
    PlayerColor Color,
    SeatKind Kind,
    AiDifficulty Difficulty)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsComputer => Kind == SeatKind.Computer;

    /// <summary>
    /// A short redundant symbol shown next to the colour. DESIGN 4.5 / 4.8 require player identity
    /// to be readable without relying on colour alone.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Symbol => Color switch
    {
        PlayerColor.Blue => "◆",    // diamond
        PlayerColor.Red => "●",     // circle
        PlayerColor.Green => "▲",   // triangle
        PlayerColor.Yellow => "■",  // square
        PlayerColor.Black => "★",   // star
        _ => "?",
    };
}
