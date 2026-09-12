using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GoldenTicket.Domain.Events;

namespace GoldenTicket.Persistence;

/// <summary>
/// Turns durable events into bytes and back. The shape is stable and versioned per event; nothing
/// here reflects over arbitrary objects, so a payload cannot quietly grow to include state the
/// event was never meant to carry (DESIGN 21.2 applies the same rule to logging).
/// </summary>
public static class EventSerializer
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static byte[] Serialize(GameEvent domainEvent) =>
        JsonSerializer.SerializeToUtf8Bytes(domainEvent, Options);

    public static GameEvent Deserialize(byte[] payload) =>
        JsonSerializer.Deserialize<GameEvent>(payload, Options)
        ?? throw new InvalidDataException("A journal row contained no event.");

    /// <summary>Readable form for diagnostics. Callers must respect the event's visibility.</summary>
    public static string ToJson(GameEvent domainEvent) =>
        Encoding.UTF8.GetString(Serialize(domainEvent));
}
