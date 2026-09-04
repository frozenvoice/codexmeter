using System.IO;
using System.IO.Pipes;
using System.Text;
using ProMeter.Providers.ChatGpt;
using ProMeter.Services;

namespace ProMeter.Companion;

public static class NativeMessagingHost
{
    public const string PipeName = "ProMeterCompanion";

    public static void Run()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(2000);
            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();
            while (TryReadMessage(stdin, out var message))
            {
                WriteMessage(pipe, AttachPairingToken(message));
                if (!TryReadMessage(pipe, out var reply))
                {
                    break;
                }

                WriteMessage(stdout, StripPairingToken(reply));
            }
        }
        catch
        {
            // Native host must not write secrets; a failed connect is a clean exit.
        }
    }

    public static string AttachPairingToken(string message)
    {
        try
        {
            if (JsonNode.Parse(message) is not JsonObject node)
            {
                return message;
            }

            var existing = ChatGptJson.GetString(node, "pairingToken", "pairing_token");
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return message;
            }

            node["pairingToken"] = CompanionPairingStore.LoadOrCreate().Token;
            return node.ToJsonString();
        }
        catch
        {
            return message;
        }
    }

    public static string StripPairingToken(string message)
    {
        try
        {
            if (JsonNode.Parse(message) is not JsonObject node)
            {
                return message;
            }

            node.Remove("pairingToken");
            node.Remove("pairing_token");
            return node.ToJsonString();
        }
        catch
        {
            return message;
        }
    }

    public static bool TryReadMessage(Stream stream, out string message)
    {
        message = "";
        var header = new byte[4];
        if (!ReadExact(stream, header, 4))
        {
            return false;
        }

        var length = BitConverter.ToInt32(header, 0);
        if (length is <= 0 or > 5_000_000)
        {
            return false;
        }

        var payload = new byte[length];
        if (!ReadExact(stream, payload, length))
        {
            return false;
        }

        message = Encoding.UTF8.GetString(payload);
        return true;
    }

    public static void WriteMessage(Stream stream, string message)
    {
        var payload = Encoding.UTF8.GetBytes(message);
        stream.Write(BitConverter.GetBytes(payload.Length));
        stream.Write(payload);
        stream.Flush();
    }

    private static bool ReadExact(Stream stream, byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n <= 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }
}
