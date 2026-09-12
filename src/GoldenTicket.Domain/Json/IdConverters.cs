using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoldenTicket.Domain.Json;

/// <summary>
/// Serialises every <see cref="IStringId"/> as a bare JSON string so the durable journal stays
/// readable during diagnosis instead of nesting <c>{"Value":"..."}</c> around every identifier.
/// </summary>
public sealed class StringIdConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeof(IStringId).IsAssignableFrom(typeToConvert);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(StringIdConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }

    private sealed class StringIdConverter<T> : JsonConverter<T>
        where T : struct, IStringId
    {
        private static readonly ConstructorInfo Constructor =
            typeof(T).GetConstructor([typeof(string)])
            ?? throw new InvalidOperationException($"{typeof(T).Name} needs a single-string constructor.");

        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString()
                        ?? throw new JsonException($"{typeof(T).Name} cannot be null.");
            return (T)Constructor.Invoke([value]);
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value);

        public override T ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => (T)Constructor.Invoke([reader.GetString()!]);

        public override void WriteAsPropertyName(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => writer.WritePropertyName(value.Value);
    }
}

/// <summary>Serialises <see cref="CardId"/> as a bare number, including as a dictionary key.</summary>
public sealed class CardIdConverter : JsonConverter<CardId>
{
    public override CardId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetInt32());

    public override void Write(Utf8JsonWriter writer, CardId value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Value);

    public override CardId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(int.Parse(reader.GetString()!));

    public override void WriteAsPropertyName(Utf8JsonWriter writer, CardId value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Value.ToString());
}

/// <summary>Serialises <see cref="SeatId"/> as a bare number, including as a dictionary key.</summary>
public sealed class SeatIdConverter : JsonConverter<SeatId>
{
    public override SeatId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(reader.GetInt32());

    public override void Write(Utf8JsonWriter writer, SeatId value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value.Value);

    public override SeatId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(int.Parse(reader.GetString()!));

    public override void WriteAsPropertyName(Utf8JsonWriter writer, SeatId value, JsonSerializerOptions options)
        => writer.WritePropertyName(value.Value.ToString());
}
