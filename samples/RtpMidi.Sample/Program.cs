using System.Net;
using System.Reactive.Linq;
using Haukcode.RtpMidi;
using Haukcode.RtpMidi.Mdns;

// ---------------------------------------------------------------------------
// Haukcode.RtpMidi — sample console client
//
// Usage:
//   dotnet run                         Discover peers via mDNS, pick one interactively
//   dotnet run -- <host> <port>        Connect directly (e.g.  192.168.1.50 5004)
//   dotnet run -- --listen <port>      Listen for an incoming connection
//
// Once connected the sample:
//   • Prints every incoming MIDI message decoded to human-readable form
//   • Lets you type simple commands to send MIDI back (for LED feedback testing)
//   • Automatically reconnects if the session drops (Ctrl+C to quit)
// ---------------------------------------------------------------------------

const string LocalName = "RtpMidi-Sample";

if (args.Length >= 1 && args[0] == "--listen")
{
    int listenPort = args.Length >= 2 ? int.Parse(args[1]) : 5004;
    await RunListenerAsync(listenPort);
}
else if (args.Length >= 2)
{
    var host = args[0];
    int port = int.Parse(args[1]);
    await RunClientAsync(new IPEndPoint(IPAddress.Parse(host), port));
}
else
{
    await RunDiscoveryAndConnectAsync();
}

// ---------------------------------------------------------------------------
// Discovery → interactive peer selection → connect
// ---------------------------------------------------------------------------

static async Task RunDiscoveryAndConnectAsync()
{
    Console.WriteLine("Scanning for RTP-MIDI peers (2 s)…");

    var peers = await RtpMidiDiscovery.ResolveAsync();

    if (peers.Count == 0)
    {
        Console.WriteLine("No peers found. Try: dotnet run -- <host> <port>");
        return;
    }

    Console.WriteLine($"\nFound {peers.Count} peer(s):\n");
    for (int i = 0; i < peers.Count; i++)
        Console.WriteLine($"  [{i + 1}] {peers[i].Name}  ({peers[i].ControlEndPoint})");

    Console.Write("\nSelect peer number (or 0 to exit): ");
    if (!int.TryParse(Console.ReadLine(), out int choice) || choice < 1 || choice > peers.Count)
        return;

    await RunClientAsync(peers[choice - 1].ControlEndPoint);
}

// ---------------------------------------------------------------------------
// Connect as initiator, with auto-reconnect
// ---------------------------------------------------------------------------

static async Task RunClientAsync(IPEndPoint controlEp)
{
    Console.WriteLine($"\nConnecting to {controlEp} (reconnects every 5 s on drop)…");

    await using var session = new RtpMidiSession(LocalName);

    SubscribeToSession(session);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    // Reconnect loop runs in the background; command loop runs in the foreground.
    var connectTask = session.ConnectWithReconnectAsync(controlEp, TimeSpan.FromSeconds(5), cts.Token);

    await RunCommandLoopAsync(session, cts.Token);
    await connectTask;
}

// ---------------------------------------------------------------------------
// Listen for incoming connection, with auto-reconnect
// ---------------------------------------------------------------------------

static async Task RunListenerAsync(int controlPort)
{
    Console.WriteLine($"\nListening on control port {controlPort} (data port {controlPort + 1})…");
    Console.WriteLine($"Will re-listen after each session ends. Press Ctrl+C to quit.\n");

    await using var session = new RtpMidiSession(LocalName);

    SubscribeToSession(session);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var listenTask = session.ListenWithReconnectAsync(controlPort, TimeSpan.FromMilliseconds(500), cts.Token);

    await RunCommandLoopAsync(session, cts.Token);
    await listenTask;
}

// ---------------------------------------------------------------------------
// Shared: subscribe to observables
// ---------------------------------------------------------------------------

static void SubscribeToSession(IRtpMidiSession session)
{
    session.StateChanges.Subscribe(state =>
    {
        var label = state switch
        {
            SessionState.Connected     => $"Connected to '{session.RemoteName}'",
            SessionState.Disconnecting => "Disconnecting…",
            SessionState.Idle          => "Disconnected — waiting to reconnect…",
            _                          => state.ToString(),
        };
        Console.WriteLine($"[state] {label}");
    });

    session.MidiReceived.Subscribe(midiBytes =>
    {
        var decoded = DecodeMidi(midiBytes.Span);
        var hex     = BitConverter.ToString(midiBytes.ToArray());
        Console.WriteLine($"[midi ] {decoded,-32}  {hex}");
    });
}

// ---------------------------------------------------------------------------
// Interactive command loop — send MIDI back to the peer
// ---------------------------------------------------------------------------

static async Task RunCommandLoopAsync(IRtpMidiSession session, CancellationToken ct)
{
    PrintHelp();

    while (!ct.IsCancellationRequested)
    {
        Console.Write("> ");

        string? line;
        try { line = await Task.Run(() => Console.ReadLine(), ct); }
        catch (OperationCanceledException) { break; }

        if (string.IsNullOrWhiteSpace(line)) continue;

        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cmd   = parts[0].ToLowerInvariant();

        try
        {
            switch (cmd)
            {
                case "note":
                    // note <channel 1-16> <pitch 0-127> <velocity 0-127>
                    if (parts.Length < 4) { Console.WriteLine("Usage: note <ch> <pitch> <vel>"); break; }
                    byte ch   = (byte)(int.Parse(parts[1]) - 1);
                    byte note = byte.Parse(parts[2]);
                    byte vel  = byte.Parse(parts[3]);
                    await session.SendMidiAsync(new byte[] { (byte)(0x90 | ch), note, vel }, ct);
                    Console.WriteLine($"  → Note On  ch={ch + 1} pitch={note} vel={vel}");
                    break;

                case "off":
                    // off <channel 1-16> <pitch 0-127>
                    if (parts.Length < 3) { Console.WriteLine("Usage: off <ch> <pitch>"); break; }
                    byte chOff   = (byte)(int.Parse(parts[1]) - 1);
                    byte noteOff = byte.Parse(parts[2]);
                    await session.SendMidiAsync(new byte[] { (byte)(0x80 | chOff), noteOff, 0 }, ct);
                    Console.WriteLine($"  → Note Off ch={chOff + 1} pitch={noteOff}");
                    break;

                case "cc":
                    // cc <channel 1-16> <controller 0-127> <value 0-127>
                    if (parts.Length < 4) { Console.WriteLine("Usage: cc <ch> <ctrl> <val>"); break; }
                    byte chCc  = (byte)(int.Parse(parts[1]) - 1);
                    byte ctrl  = byte.Parse(parts[2]);
                    byte ccVal = byte.Parse(parts[3]);
                    await session.SendMidiAsync(new byte[] { (byte)(0xB0 | chCc), ctrl, ccVal }, ct);
                    Console.WriteLine($"  → CC       ch={chCc + 1} ctrl={ctrl} val={ccVal}");
                    break;

                case "pc":
                    // pc <channel 1-16> <program 0-127>
                    if (parts.Length < 3) { Console.WriteLine("Usage: pc <ch> <program>"); break; }
                    byte chPc = (byte)(int.Parse(parts[1]) - 1);
                    byte prog = byte.Parse(parts[2]);
                    await session.SendMidiAsync(new byte[] { (byte)(0xC0 | chPc), prog }, ct);
                    Console.WriteLine($"  → Prog Chg ch={chPc + 1} program={prog}");
                    break;

                case "sysex":
                    // sysex <hex bytes, space-separated, without F0/F7>
                    if (parts.Length < 2) { Console.WriteLine("Usage: sysex <hex bytes>"); break; }
                    var bodyBytes = parts[1..].Select(h => Convert.ToByte(h, 16)).ToArray();
                    var sysex = new byte[bodyBytes.Length + 2];
                    sysex[0] = 0xF0;
                    bodyBytes.CopyTo(sysex, 1);
                    sysex[^1] = 0xF7;
                    await session.SendMidiAsync(sysex, ct);
                    Console.WriteLine($"  → SysEx    {BitConverter.ToString(sysex)}");
                    break;

                case "raw":
                    // raw <hex bytes, space-separated>
                    if (parts.Length < 2) { Console.WriteLine("Usage: raw <hex bytes>"); break; }
                    var rawBytes = parts[1..].Select(h => Convert.ToByte(h, 16)).ToArray();
                    await session.SendMidiAsync(rawBytes, ct);
                    Console.WriteLine($"  → Raw      {BitConverter.ToString(rawBytes)}");
                    break;

                case "help":
                    PrintHelp();
                    break;

                case "quit":
                case "exit":
                    return;

                default:
                    Console.WriteLine($"Unknown command '{cmd}'. Type 'help' for list.");
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine($"  (not connected — {ex.Message})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
        }
    }

    Console.WriteLine("\nDisconnecting…");
}

// ---------------------------------------------------------------------------
// MIDI decoder — human-readable summary of common message types
// ---------------------------------------------------------------------------

static string DecodeMidi(ReadOnlySpan<byte> data)
{
    if (data.IsEmpty) return "(empty)";

    byte status = data[0];

    if (status >= 0xF0)
    {
        return status switch
        {
            0xF0 => $"SysEx ({data.Length} bytes)",
            0xF8 => "Timing Clock",
            0xFA => "Start",
            0xFB => "Continue",
            0xFC => "Stop",
            0xFE => "Active Sensing",
            0xFF => "System Reset",
            _    => $"System 0x{status:X2}",
        };
    }

    byte msgType = (byte)(status & 0xF0);
    int  ch      = (status & 0x0F) + 1;
    byte d1      = data.Length > 1 ? data[1] : (byte)0;
    byte d2      = data.Length > 2 ? data[2] : (byte)0;

    return msgType switch
    {
        0x80 => $"Note Off  ch={ch} pitch={d1} vel={d2}",
        0x90 => d2 == 0
                    ? $"Note Off  ch={ch} pitch={d1} (vel=0)"
                    : $"Note On   ch={ch} pitch={d1} vel={d2}",
        0xA0 => $"Poly AT   ch={ch} pitch={d1} pres={d2}",
        0xB0 => $"CC        ch={ch} ctrl={d1} val={d2}",
        0xC0 => $"Prog Chg  ch={ch} program={d1}",
        0xD0 => $"Chan AT   ch={ch} pres={d1}",
        0xE0 => $"Pitch Bnd ch={ch} val={d1 | (d2 << 7)}",
        _    => $"Unknown 0x{status:X2}",
    };
}

static void PrintHelp()
{
    Console.WriteLine("""

  Commands:
    note <ch> <pitch> <vel>       Send Note On  (ch 1-16, pitch/vel 0-127)
    off  <ch> <pitch>             Send Note Off
    cc   <ch> <ctrl> <val>        Send Control Change
    pc   <ch> <program>           Send Program Change
    sysex <hex bytes...>          Send SysEx (F0/F7 added automatically)
    raw   <hex bytes...>          Send raw MIDI bytes
    help                          Show this list
    quit / exit                   Disconnect and exit

  Incoming MIDI is printed automatically as it arrives.
  Commands while disconnected are silently discarded.
""");
}
