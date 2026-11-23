using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
while (Console.ReadLine() is { } line)
{
    using JsonDocument message = JsonDocument.Parse(line);
    JsonElement request = message.RootElement;
    long id = request.GetProperty("id").GetInt64();
    object reply = request.GetProperty("op").GetString() switch
    {
        "hello" => new { id, ok = request.GetProperty("protocol").GetInt32() == 1 &&
            request.GetProperty("slot").GetString() == "fx", protocol = 1 },
        "probe" or "reset" => new { id, ok = true },
        "effect" => new { id, ok = true, audio = Soften(request.GetProperty("audio").GetBytesFromBase64()) },
        _ => new { id, ok = false }
    };
    Console.WriteLine(JsonSerializer.Serialize(reply));
}

static byte[] Soften(byte[] pcm)
{
    for (int i = 0; i + 1 < pcm.Length; i += sizeof(short))
    {
        short sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i));
        BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i), (short)(sample * 0.55));
    }
    return pcm;
}
