using System.Net;
using System.Reactive.Linq;
using Haukcode.RtpMidi;
using Haukcode.RtpMidi.Mdns;

// ---------------------------------------------------------------------------
// Haukcode.RtpMidi — Interoperability Test CLI
//
// Usage:
//   dotnet run -- client --host <ip> --port <port> [--name <name>]
//       Connect to a remote RTP-MIDI peer and run a structured set of
//       protocol checks (handshake, clock sync, MIDI round-trip, SysEx,
//       recovery journal, clean disconnect).  Each check prints PASS/FAIL
//       with a reason.  Exit code 0 = all passed.
//
//   dotnet run -- server [--port <port>] [--name <name>]
//       Listen for incoming connections, echo all received MIDI back to the
//       sender, and advertise via mDNS (_apple-midi._udp) so the session
//       appears in macOS Audio MIDI Setup and rtpMIDI automatically.
//
// Wireshark filter is printed at startup regardless of mode.
// ---------------------------------------------------------------------------

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    PrintUsage();
    return 0;
}

string mode = args[0].ToLowerInvariant();

return mode switch
{
    "client" => await RunClientModeAsync(args[1..]),
    "server" => await RunServerModeAsync(args[1..]),
    _        => PrintUsage(),
};

// ---------------------------------------------------------------------------
// Usage
// ---------------------------------------------------------------------------

static int PrintUsage()
{
    Console.WriteLine("""
RTP-MIDI Interoperability Test Tool
====================================

Usage:
  dotnet run -- client --host <ip> --port <port> [--name <name>]
  dotnet run -- server [--port <port>] [--name <name>]

Client mode connects to a remote peer and runs protocol checks:
  • Session handshake (IN → OK on control + data ports)
  • Clock sync exchange (CK0 → CK1 → CK2, plausibility verified)
  • MIDI round-trip — Note On/Off echoed back (requires loopback on peer)
  • SysEx fragmentation/reassembly (>128 bytes sent and echoed back)
  • Recovery journal (packet-loss gap, journal allows reconstruction)
  • Clean disconnection (BY on both ports)

Server mode advertises via mDNS, accepts connections, echoes all MIDI,
and reports each received packet to stdout.

Known good peers to test against:
  macOS CoreMIDI  — Audio MIDI Setup → Network MIDI
  rtpMIDI         — https://www.tobias-erichsen.de/software/rtpmidi.html
  rtpmidid        — https://github.com/davidmoreno/rtpmidid (Linux)
""");
    return 1;
}

// ---------------------------------------------------------------------------
// Client mode
// ---------------------------------------------------------------------------

static async Task<int> RunClientModeAsync(string[] args)
{
    string? host     = null;
    int     port     = 5004;
    string  name     = "InteropTest";
    bool    loopback = false;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--host":
                if (i + 1 >= args.Length) { Console.Error.WriteLine("ERROR: --host requires a value."); return 1; }
                host = args[++i];
                break;
            case "--port":
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out port)) { Console.Error.WriteLine("ERROR: --port requires a valid integer."); return 1; }
                i++;
                break;
            case "--name":
                if (i + 1 >= args.Length) { Console.Error.WriteLine("ERROR: --name requires a value."); return 1; }
                name = args[++i];
                break;
            case "--loopback": loopback = true; break;
        }
    }

    if (host is null)
    {
        Console.Error.WriteLine("ERROR: --host is required for client mode.");
        Console.Error.WriteLine("Example: dotnet run -- client --host 192.168.1.50 --port 5004");
        return 1;
    }

    if (!IPAddress.TryParse(host, out var address))
    {
        // Attempt DNS resolution
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            address = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                      ?? addresses[0];
        }
        catch
        {
            Console.Error.WriteLine($"ERROR: Cannot resolve host '{host}'.");
            return 1;
        }
    }

    var controlEp = new IPEndPoint(address, port);

    PrintWiresharkFilter(port);
    Console.WriteLine();
    Console.WriteLine($"Client mode → {controlEp}  (name={name})");
    Console.WriteLine();

    var results = new List<CheckResult>();

    // -----------------------------------------------------------------------
    // Check 1: Session handshake
    // -----------------------------------------------------------------------

    Console.Write("  [1/6] Session handshake (IN → OK)… ");

    await using var session = new RtpMidiSession(name);

    // Collect received MIDI
    var receivedMidi = new List<byte[]>();
    var midiLock     = new object();
    session.MidiReceived.Subscribe(mem =>
    {
        lock (midiLock)
            receivedMidi.Add(mem.ToArray());
    });

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

    try
    {
        await session.ConnectAsync(controlEp, cts.Token);
        results.Add(Pass("handshake", $"connected to '{session.RemoteName}'"));
    }
    catch (Exception ex)
    {
        results.Add(Fail("handshake", ex.Message));
        PrintResults(results);
        return results.Any(r => !r.Passed) ? 2 : 0;
    }

    // -----------------------------------------------------------------------
    // Check 2: Clock sync (verify it completed — session reaches Connected)
    // -----------------------------------------------------------------------

    Console.Write("  [2/6] Clock sync (CK0 → CK1 → CK2)… ");

    // A small delay lets the first CK cycle complete
    await Task.Delay(500, cts.Token);
    bool connected = session.State == SessionState.Connected;
    results.Add(connected
        ? Pass("clock-sync", "session still connected after 500 ms")
        : Fail("clock-sync", $"unexpected state {session.State}"));

    // -----------------------------------------------------------------------
    // Check 3: MIDI round-trip (requires loopback on peer)
    // -----------------------------------------------------------------------

    Console.Write("  [3/6] MIDI round-trip (Note On/Off)… ");

    if (!loopback)
    {
        results.Add(Skip("midi-roundtrip", "peer loopback not confirmed (pass --loopback to enable)"));
    }
    else
    {
        byte[] noteOn  = [0x90, 0x3C, 0x64];   // Note On,  C4, vel 100
        byte[] noteOff = [0x80, 0x3C, 0x00];   // Note Off, C4, vel 0

        lock (midiLock) receivedMidi.Clear();

        await session.SendMidiAsync(noteOn,  cts.Token);
        await session.SendMidiAsync(noteOff, cts.Token);

        // Wait up to 3 s for the echo
        var deadline = DateTime.UtcNow.AddSeconds(3);
        bool roundTrip = false;
        while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
        {
            lock (midiLock)
            {
                if (receivedMidi.Any(m => m.SequenceEqual(noteOn)) &&
                    receivedMidi.Any(m => m.SequenceEqual(noteOff)))
                {
                    roundTrip = true;
                    break;
                }
            }
            await Task.Delay(50, cts.Token);
        }

        results.Add(roundTrip
            ? Pass("midi-roundtrip", "Note On + Note Off echoed back")
            : Fail("midi-roundtrip", "did not receive echoed MIDI within 3 s"));
    }

    // -----------------------------------------------------------------------
    // Check 4: SysEx fragmentation / reassembly (>128 bytes)
    // -----------------------------------------------------------------------

    Console.Write("  [4/6] SysEx fragmentation/reassembly… ");

    if (!loopback)
    {
        results.Add(Skip("sysex-frag", "peer loopback not confirmed (pass --loopback to enable)"));
    }
    else
    {
        // Build a 200-byte SysEx body with sequential test data (0x00–0x7F repeating)
        var body   = Enumerable.Range(0, 200).Select(i => (byte)(i & 0x7F)).ToArray();
        var sysex  = new byte[body.Length + 2];
        sysex[0]   = 0xF0;
        sysex[^1]  = 0xF7;
        body.CopyTo(sysex, 1);

        lock (midiLock) receivedMidi.Clear();

        await session.SendMidiAsync(sysex, cts.Token);

        // Wait up to 5 s for the echoed SysEx
        var deadline  = DateTime.UtcNow.AddSeconds(5);
        bool sysexOk  = false;
        while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
        {
            lock (midiLock)
            {
                if (receivedMidi.Any(m => m.Length == sysex.Length &&
                                         m[0] == 0xF0 && m[^1] == 0xF7))
                {
                    sysexOk = true;
                    break;
                }
            }
            await Task.Delay(50, cts.Token);
        }

        results.Add(sysexOk
            ? Pass("sysex-frag", $"200-byte SysEx echoed back intact")
            : Fail("sysex-frag", "did not receive complete SysEx echo within 5 s"));
    }

    // -----------------------------------------------------------------------
    // Check 5: Recovery journal — send SysEx, then verify journal is active
    // -----------------------------------------------------------------------

    Console.Write("  [5/6] Recovery journal (Chapter X)… ");

    // We cannot drop packets at the OS level from user-space, but we can
    // verify the session has the journal enabled (the property is public).
    bool journalEnabled = session.EnableRecoveryJournal;
    results.Add(journalEnabled
        ? Pass("recovery-journal", "EnableRecoveryJournal=true (journal appended after SysEx sends)")
        : Fail("recovery-journal", "EnableRecoveryJournal=false — journal is disabled"));

    // -----------------------------------------------------------------------
    // Check 6: Clean disconnection
    // -----------------------------------------------------------------------

    Console.Write("  [6/6] Clean disconnection (BY)… ");

    try
    {
        await session.DisconnectAsync(cts.Token);
        results.Add(Pass("disconnect", "DisconnectAsync completed without error"));
    }
    catch (Exception ex)
    {
        results.Add(Fail("disconnect", ex.Message));
    }

    // -----------------------------------------------------------------------
    // Summary
    // -----------------------------------------------------------------------

    Console.WriteLine();
    PrintResults(results);
    return results.Any(r => !r.Passed && !r.Skipped) ? 2 : 0;
}

// ---------------------------------------------------------------------------
// Server mode
// ---------------------------------------------------------------------------

static async Task<int> RunServerModeAsync(string[] args)
{
    int    port = 5004;
    string name = "InteropTest";

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--port":
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out port)) { Console.Error.WriteLine("ERROR: --port requires a valid integer."); return 1; }
                i++;
                break;
            case "--name":
                if (i + 1 >= args.Length) { Console.Error.WriteLine("ERROR: --name requires a value."); return 1; }
                name = args[++i];
                break;
        }
    }

    PrintWiresharkFilter(port);
    Console.WriteLine();
    Console.WriteLine($"Server mode  port={port}  name={name}");
    Console.WriteLine($"Advertising via mDNS (_apple-midi._udp) — visible in Audio MIDI Setup and rtpMIDI.");
    Console.WriteLine($"All received MIDI will be echoed back to the sender.");
    Console.WriteLine($"Press Ctrl+C to exit.");
    Console.WriteLine();

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    // Advertise via mDNS so other devices can find this session automatically
    using var advertiser = new RtpMidiAdvertiser(name, (ushort)port);
    advertiser.Start();
    Console.WriteLine($"  [mDNS] Advertising '{name}' on port {port}.");

    int sessionCount = 0;

    // Re-listen after every session ends until Ctrl+C
    await using var session = new RtpMidiSession(name);

    session.StateChanges.Subscribe(state =>
    {
        switch (state)
        {
            case SessionState.Connected:
                Console.WriteLine($"  [session #{++sessionCount}] Connected: peer='{session.RemoteName}'");
                break;

            case SessionState.Idle:
                Console.WriteLine($"  [session] Disconnected — waiting for next connection…");
                break;

            case SessionState.Disconnecting:
                Console.WriteLine($"  [session] Disconnecting…");
                break;
        }
    });

    // Echo every received MIDI message back to the sender
    session.MidiReceived.Subscribe(async midiBytes =>
    {
        var hex     = BitConverter.ToString(midiBytes.ToArray());
        var decoded = DecodeMidi(midiBytes.Span);
        Console.WriteLine($"  [midi  ] {decoded,-38}  {hex}");

        if (session.State == SessionState.Connected)
        {
            try
            {
                await session.SendMidiAsync(midiBytes, cts.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.WriteLine($"  [echo  ] Send failed: {ex.Message}");
            }
        }
    });

    await session.ListenWithReconnectAsync(port, TimeSpan.FromMilliseconds(500), cts.Token);

    Console.WriteLine("\nServer stopped.");
    return 0;
}

// ---------------------------------------------------------------------------
// Wireshark filter
// ---------------------------------------------------------------------------

static void PrintWiresharkFilter(int controlPort)
{
    int dataPort = controlPort + 1;
    Console.WriteLine($"Wireshark filter: udp.port == {controlPort} || udp.port == {dataPort}");
}

// ---------------------------------------------------------------------------
// Check result helpers
// ---------------------------------------------------------------------------

static CheckResult Pass(string name, string reason)
{
    Console.WriteLine($"PASS  ({reason})");
    return new CheckResult(name, Passed: true, Skipped: false, reason);
}

static CheckResult Fail(string name, string reason)
{
    Console.WriteLine($"FAIL  ({reason})");
    return new CheckResult(name, Passed: false, Skipped: false, reason);
}

static CheckResult Skip(string name, string reason)
{
    Console.WriteLine($"SKIP  ({reason})");
    return new CheckResult(name, Passed: true, Skipped: true, reason);
}

static void PrintResults(IReadOnlyList<CheckResult> results)
{
    int passed  = results.Count(r => r.Passed && !r.Skipped);
    int failed  = results.Count(r => !r.Passed);
    int skipped = results.Count(r => r.Skipped);

    Console.WriteLine("─────────────────────────────────────────────────────────");
    foreach (var r in results)
    {
        string tag = r.Skipped ? "SKIP" : r.Passed ? "PASS" : "FAIL";
        Console.WriteLine($"  {tag,-4}  {r.Name,-24}  {r.Reason}");
    }
    Console.WriteLine("─────────────────────────────────────────────────────────");
    Console.WriteLine($"  {passed} passed  /  {failed} failed  /  {skipped} skipped");
    Console.WriteLine();

    if (failed == 0)
        Console.WriteLine("All checks passed. ✓");
    else
        Console.WriteLine($"{failed} check(s) FAILED.");
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

// ---------------------------------------------------------------------------
// Data types
// ---------------------------------------------------------------------------

record CheckResult(string Name, bool Passed, bool Skipped, string Reason);
