using System.Text;

namespace GoldenTicket.Vision;

/// <summary>
/// A small bounded reader for the ONNX fields needed by this fixed experiment. ONNX Runtime performs
/// the full model/type validation. We reject external tensors, custom operators and nested programs
/// before giving the verified in-memory model to its native runtime.
/// </summary>
internal static class PieceModelGraphValidator
{
    private static readonly HashSet<string> Operators = new(StringComparer.Ordinal)
    {
        "Add", "BatchNormalization", "Cast", "Concat", "Constant", "ConstantOfShape", "Conv", "Div",
        "Exp", "Expand", "Flatten", "Gather", "Identity", "MaxPool", "Mul", "Pad", "Pow", "Range",
        "Relu", "Reshape", "Resize", "Shape", "Sigmoid", "Slice", "Split", "Squeeze", "Sub",
        "Transpose", "Unsqueeze"
    };

    internal static void Validate(ReadOnlySpan<byte> model, int expectedOpset)
    {
        var reader = new Fields(model);
        var graphs = 0;
        var imports = 0;
        while (reader.Next(out var field, out _, out var bytes, out _))
        {
            if (field == 7) { graphs++; ValidateGraph(bytes); }
            if (field == 8)
            {
                imports++;
                var import = new Fields(bytes);
                var domain = "";
                ulong version = 0;
                while (import.Next(out var key, out _, out var value, out var integer))
                {
                    if (key == 1) domain = Text(value);
                    if (key == 2) version = integer;
                }
                if (domain is not "" and not "ai.onnx" || version != (ulong)expectedOpset)
                    throw new InvalidDataException("The piece model uses an unsupported operator domain or opset.");
            }
            if (field is 20 or 25) throw new InvalidDataException("Training graphs and model functions are not supported by this experiment.");
        }
        if (graphs != 1 || imports != 1) throw new InvalidDataException("The piece model must contain one standard ONNX graph and opset.");
    }

    private static void ValidateGraph(ReadOnlySpan<byte> graph)
    {
        var reader = new Fields(graph);
        var nodes = 0;
        while (reader.Next(out var field, out _, out var bytes, out _))
        {
            if (field == 1)
            {
                if (++nodes > 10000) throw new InvalidDataException("The piece model exceeds its operator count limit.");
                ValidateNode(bytes);
            }
            if (field == 5) ValidateTensor(bytes);
            if (field == 15) throw new InvalidDataException("Sparse external tensors are not supported by this experiment.");
        }
        if (nodes == 0) throw new InvalidDataException("The piece-model graph is empty.");
    }

    private static void ValidateNode(ReadOnlySpan<byte> node)
    {
        var reader = new Fields(node);
        var op = "";
        var domain = "";
        while (reader.Next(out var field, out _, out var bytes, out _))
        {
            if (field == 4) op = Text(bytes);
            if (field == 7) domain = Text(bytes);
            if (field == 5)
            {
                var attribute = new Fields(bytes);
                while (attribute.Next(out var key, out _, out var value, out _))
                {
                    if (key is 5 or 10) ValidateTensor(value);
                    if (key is 6 or 11 or 22 or 23)
                        throw new InvalidDataException("Nested model graphs and sparse attributes are not supported by this experiment.");
                }
            }
        }
        if (domain is not "" and not "ai.onnx" || !Operators.Contains(op))
            throw new InvalidDataException($"The piece model uses an unsupported operator: {domain}:{op}.");
    }

    private static void ValidateTensor(ReadOnlySpan<byte> tensor)
    {
        var reader = new Fields(tensor);
        while (reader.Next(out var field, out _, out _, out var integer))
            if (field == 13 || field == 14 && integer != 0)
                throw new InvalidDataException("The piece model must embed every tensor; external files are not allowed.");
    }

    private static string Text(ReadOnlySpan<byte> bytes) => bytes.Length <= 128
        ? Encoding.UTF8.GetString(bytes) : throw new InvalidDataException("An ONNX operator identifier is too long.");

    private ref struct Fields(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _remaining = bytes;

        internal bool Next(out int field, out int wire, out ReadOnlySpan<byte> value, out ulong integer)
        {
            field = wire = 0;
            value = default;
            integer = 0;
            if (_remaining.IsEmpty) return false;
            var tag = Varint();
            if (tag == 0 || tag >> 3 > int.MaxValue) throw new InvalidDataException("Invalid ONNX field.");
            field = (int)(tag >> 3);
            wire = (int)(tag & 7);
            switch (wire)
            {
                case 0: integer = Varint(); break;
                case 1: value = Take(8); break;
                case 2:
                    var length = Varint();
                    if (length > int.MaxValue) throw new InvalidDataException("Invalid ONNX field size.");
                    value = Take((int)length); break;
                case 5: value = Take(4); break;
                default: throw new InvalidDataException("Unsupported ONNX wire encoding.");
            }
            return true;
        }

        private ulong Varint()
        {
            ulong result = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                var b = Take(1)[0];
                if (shift == 63 && b > 1) throw new InvalidDataException("Invalid ONNX integer.");
                result |= (ulong)(b & 127) << shift;
                if ((b & 128) == 0) return result;
            }
            throw new InvalidDataException("Invalid ONNX integer.");
        }

        private ReadOnlySpan<byte> Take(int length)
        {
            if (length < 0 || length > _remaining.Length) throw new InvalidDataException("The ONNX model is truncated.");
            var value = _remaining[..length];
            _remaining = _remaining[length..];
            return value;
        }
    }
}
