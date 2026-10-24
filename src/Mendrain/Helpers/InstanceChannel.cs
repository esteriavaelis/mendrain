using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace Mendrain.Helpers;

/// <summary>
/// Lets later Mendrain processes hand their command line to the first one. A named mutex decides which process
/// is the main instance; requests travel over a named pipe that only the same Windows user can open.
/// Explorer starts one process per selected file, all at once: clients wait for the pipe, so nothing is lost
/// while the main instance is still starting.
/// </summary>
public static class InstanceChannel
{
    // Per user and per logon session.
    private static readonly string Name = $"Mendrain.{WindowsIdentity.GetCurrent().User?.Value}.{GetSessionId()}";
    private static Mutex? _mutex;

    /// <summary>True if this process is (now) the main instance. Call on the main thread; ownership lasts until exit.</summary>
    public static bool TryBecomeMain()
    {
        _mutex ??= new Mutex(false, @"Local\" + Name);
        try
        {
            return _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            return true;   // the previous main instance ended without releasing it; this process owns it now
        }
    }

    /// <summary>Listens for requests on a background thread for the life of the process.</summary>
    public static void StartServer(Action<string[]> onRequest)
    {
        var thread = new Thread(() => Serve(onRequest)) { IsBackground = true, Name = "Mendrain requests" };
        thread.Start();
    }

    private static void Serve(Action<string[]> onRequest)
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                pipe.WaitForConnection();
                var args = JsonSerializer.Deserialize<string[]>(ReadMessage(pipe)) ?? [];
                onRequest(args);
                WriteMessage(pipe, BitConverter.GetBytes(Environment.ProcessId));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // A client went away or sent something unreadable; keep serving the others.
            }
        }
    }

    /// <summary>Sends <paramref name="args"/> to the main instance. Returns false if it could not be reached.</summary>
    public static bool TrySend(IReadOnlyList<string> args, TimeSpan timeout)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            pipe.Connect((int)timeout.TotalMilliseconds);
            WriteMessage(pipe, JsonSerializer.SerializeToUtf8Bytes(args));
            var reply = ReadMessage(pipe);
            // This process was started by Explorer and may pass on its right to bring a window to the front.
            if (reply.Length == 4)
                AllowSetForegroundWindow(BitConverter.ToInt32(reply));
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Length-prefixed messages: a 4-byte length, then the bytes.
    private static void WriteMessage(Stream stream, byte[] data)
    {
        stream.Write(BitConverter.GetBytes(data.Length));
        stream.Write(data);
        stream.Flush();
    }

    private static byte[] ReadMessage(Stream stream)
    {
        var header = new byte[4];
        stream.ReadExactly(header);
        var length = BitConverter.ToInt32(header);
        if (length is < 0 or > 1 << 20)
            throw new IOException("Invalid request size.");
        var data = new byte[length];
        stream.ReadExactly(data);
        return data;
    }

    private static uint GetSessionId() => ProcessIdToSessionId((uint)Environment.ProcessId, out var session) ? session : 0;

    [DllImport("kernel32.dll")]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
