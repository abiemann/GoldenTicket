using System.Text.Json.Serialization;
using GoldenTicket.Domain.Json;

namespace GoldenTicket.Domain;

/// <summary>Marker for an identifier whose wire form is a single string.</summary>
public interface IStringId
{
    string Value { get; }
}

/// <summary>A city on the supported board, e.g. <c>saint-louis</c>.</summary>
[JsonConverter(typeof(StringIdConverterFactory))]
public readonly record struct CityId(string Value) : IStringId
{
    public override string ToString() => Value;
}

/// <summary>
/// One claimable lane. DESIGN 6.2: a pair of city ids is not sufficient to identify a claim,
/// so parallel lanes carry distinct route ids sharing a <see cref="RouteDefinition.ParallelGroupId"/>.
/// </summary>
[JsonConverter(typeof(StringIdConverterFactory))]
public readonly record struct RouteId(string Value) : IStringId
{
    public override string ToString() => Value;
}

/// <summary>A destination ticket definition id.</summary>
[JsonConverter(typeof(StringIdConverterFactory))]
public readonly record struct TicketId(string Value) : IStringId
{
    public override string ToString() => Value;
}

/// <summary>
/// DESIGN 5.1: identical train cards still need distinct instance ids for conservation checks,
/// replay and deduplication.
/// </summary>
[JsonConverter(typeof(CardIdConverter))]
public readonly record struct CardId(int Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>A seat at the table. Stable for the life of the match.</summary>
[JsonConverter(typeof(SeatIdConverter))]
public readonly record struct SeatId(int Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>Client-supplied command identity used for idempotent replay (DESIGN 8.1).</summary>
[JsonConverter(typeof(StringIdConverterFactory))]
public readonly record struct CommandId(string Value) : IStringId
{
    public static CommandId New() => new(Guid.NewGuid().ToString("n"));

    public override string ToString() => Value;
}

/// <summary>Identity of a pending physical or digital operation (DESIGN 7.1).</summary>
[JsonConverter(typeof(StringIdConverterFactory))]
public readonly record struct OperationId(string Value) : IStringId
{
    public static OperationId New() => new(Guid.NewGuid().ToString("n"));

    public override string ToString() => Value;
}

/// <summary>Identity of a named pack-away checkpoint (DESIGN 7.1).</summary>
[JsonConverter(typeof(StringIdConverterFactory))]
public readonly record struct CheckpointId(string Value) : IStringId
{
    public static CheckpointId New() => new(Guid.NewGuid().ToString("n"));

    public override string ToString() => Value;
}

/// <summary>Identity of a saved match.</summary>
[JsonConverter(typeof(StringIdConverterFactory))]
public readonly record struct SessionId(string Value) : IStringId
{
    public static SessionId New() => new(Guid.NewGuid().ToString("n"));

    public override string ToString() => Value;
}
