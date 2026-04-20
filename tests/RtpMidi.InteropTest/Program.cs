using System.Net;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using Haukcode.RtpMidi;
using Haukcode.RtpMidi.Mdns;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Multimedia;

// ---------------------------------------------------------------------------
// Haukcode.RtpMidi — Interoperability Test CLI
//
// Usage:
//   dotnet run -- client --host <ip> --port <port> [--name <name>] [--loopback] [--alsa-verify]
//       Connect to a remote RTP-MIDI peer and run a structured set of
//       protocol checks.  Each check prints PASS/FAIL/SKIP with a reason.
//       Exit code 0 = all non-skipped checks passed.
//
//   dotnet run -- server [--port <port>] [--name <name>]
//       Listen for incoming connections, echo all received MIDI back to the
//       sender, and advertise via mDNS (_apple-midi._udp).
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
  dotnet run -- client --host <ip> --port <port> [--name <name>] [--loopback] [--alsa-verify]
  dotnet run -- server [--port <port>] [--name <name>]

Client mode connects to a remote peer and runs protocol checks:
  Always:
    • Session handshake (IN → OK on control + data ports)
    • Clock sync (CK0 → CK1 → CK2, plausibility verified)
    • Recovery journal enabled (EnableRecoveryJournal property)
    • Clean disconnection (BY on both ports)

  With --loopback (peer echoes MIDI back):
    • MIDI round-trip — Note On/Off echoed back
    • SysEx fragmentation/reassembly (>128 bytes echoed intact)
    • Control Change (CC #7 Vol, CC #10 Pan)
    • Program Change
    • Pitch Wheel
    • Polyphony + Note Off (vel=0 variant)
    • Channel Aftertouch
    • Poly Key Pressure
    • MTC Quarter Frame (System Common)
    • Sequence-number gap + journal recovery

  With --alsa-verify (Linux only, for rtpmidid interop):
    • MIDI → RTP-MIDI → rtpmidid → ALSA → DryWetMidi verification

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
// MIDI helpers (DryWetMidi-based)
// ---------------------------------------------------------------------------

/// <summary>Converts a DryWetMidi MIDI event to a raw byte array for RTP-MIDI transmission.</summary>
static byte[] ToRawBytes(MidiEvent evt)
{
    byte ch = evt is ChannelEvent ce ? (byte)ce.Channel : (byte)0;
    return evt switch
    {
        NoteOnEvent e            => [(byte)(0x90 | ch), (byte)e.NoteNumber, (byte)e.Velocity],
        NoteOffEvent e           => [(byte)(0x80 | ch), (byte)e.NoteNumber, (byte)e.Velocity],
        ControlChangeEvent e     => [(byte)(0xB0 | ch), (byte)e.ControlNumber, (byte)e.ControlValue],
        ProgramChangeEvent e     => [(byte)(0xC0 | ch), (byte)e.ProgramNumber],
        PitchBendEvent e         => [(byte)(0xE0 | ch), (byte)(e.PitchValue & 0x7F), (byte)((e.PitchValue >> 7) & 0x7F)],
        ChannelAftertouchEvent e => [(byte)(0xD0 | ch), (byte)e.AftertouchValue],
        NoteAftertouchEvent e    => [(byte)(0xA0 | ch), (byte)e.NoteNumber, (byte)e.AftertouchValue],
        MidiTimeCodeEvent e      => [0xF1, (byte)(((byte)e.Component << 4) | (byte)e.ComponentValue)],
        _ => throw new NotSupportedException($"Cannot convert {evt.GetType().Name} to raw bytes")
    };
}

// ---------------------------------------------------------------------------
// Client mode
// ---------------------------------------------------------------------------

static async Task<int> RunClientModeAsync(string[] args)
{
    string? host       = null;
    int     port       = 5004;
    string  name       = "InteropTest";
    bool    loopback   = false;
    bool    alsaVerify = false;

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
            case "--loopback":    loopback    = true; break;
            case "--alsa-verify": alsaVerify  = true; break;
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

    int totalChecks = 14 + (alsaVerify ? 1 : 0);
    int checkNum    = 0;
    string NextLabel(string title) => $"  [{++checkNum}/{totalChecks}] {title}";

    // MIDI channel 0 (MIDI channel 1 in 1-indexed notation)
    var ch = (FourBitNumber)0;

    PrintWiresharkFilter(port);
    Console.WriteLine();
    Console.WriteLine($"Client mode → {controlEp}  (name={name}, loopback={loopback}, alsa-verify={alsaVerify})");
    Console.WriteLine();

    var results = new List<CheckResult>();

    // -----------------------------------------------------------------------
    // Check 1: Session handshake
    // -----------------------------------------------------------------------

    Console.Write(NextLabel("Session handshake (IN → OK)… "));

    await using var session = new RtpMidiSession(name);

    // Collect received MIDI
    var receivedMidi = new List<byte[]>();
    var midiLock     = new object();
    session.MidiReceived.Subscribe(mem =>
    {
        lock (midiLock)
            receivedMidi.Add(mem.ToArray());
    });

    // 90 s total budget — loopback has 13 checks each with up to ~3–5 s waits
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

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
    // Check 2: Clock sync
    // -----------------------------------------------------------------------

    Console.Write(NextLabel("Clock sync (CK0 → CK1 → CK2)… "));

    await Task.Delay(500, cts.Token);
    bool connected = session.State == SessionState.Connected;
    results.Add(connected
        ? Pass("clock-sync", "session still Connected after 500 ms")
        : Fail("clock-sync", $"unexpected state {session.State}"));

    // -----------------------------------------------------------------------
    // Check 3: MIDI round-trip (Note On / Note Off)
    // -----------------------------------------------------------------------

    Console.Write(NextLabel("MIDI round-trip (Note On/Off)… "));

    if (!loopback)
    {
        results.Add(Skip("midi-roundtrip", "pass --loopback to enable"));
    }
    else
    {
        byte[] noteOn  = ToRawBytes(new NoteOnEvent ((SevenBitNumber)0x3C, (SevenBitNumber)0x64) { Channel = ch });
        byte[] noteOff = ToRawBytes(new NoteOffEvent((SevenBitNumber)0x3C, (SevenBitNumber)0x00) { Channel = ch });

        lock (midiLock) receivedMidi.Clear();
        await session.SendMidiAsync(noteOn,  cts.Token);
        await session.SendMidiAsync(noteOff, cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(3);
        bool ok = false;
        while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
        {
            lock (midiLock)
                if (receivedMidi.Any(m => m.SequenceEqual(noteOn)) &&
                    receivedMidi.Any(m => m.SequenceEqual(noteOff)))
                { ok = true; break; }
            await Task.Delay(50, cts.Token);
        }

        results.Add(ok
            ? Pass("midi-roundtrip", "Note On + Note Off echoed back")
            : Fail("midi-roundtrip", "did not receive echoed MIDI within 3 s"));
    }

    // -----------------------------------------------------------------------
    // Check 4: SysEx fragmentation / reassembly (>128 bytes)
    // -----------------------------------------------------------------------

    Console.Write(NextLabel("SysEx fragmentation/reassembly… "));

    if (!loopback)
    {
        results.Add(Skip("sysex-frag", "pass --loopback to enable"));
    }
    else
    {
        // 200-byte SysEx body with sequential test data (0x00–0x7F repeating)
        var body  = Enumerable.Range(0, 200).Select(i => (byte)(i & 0x7F)).ToArray();
        var sysex = new byte[body.Length + 2];
        sysex[0]  = 0xF0;
        sysex[^1] = 0xF7;
        body.CopyTo(sysex, 1);

        lock (midiLock) receivedMidi.Clear();
        await session.SendMidiAsync(sysex, cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        bool ok = false;
        while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
        {
            lock (midiLock)
                if (receivedMidi.Any(m => m.Length == sysex.Length && m[0] == 0xF0 && m[^1] == 0xF7))
                { ok = true; break; }
            await Task.Delay(50, cts.Token);
        }

        results.Add(ok
            ? Pass("sysex-frag", "200-byte SysEx echoed back intact")
            : Fail("sysex-frag", "did not receive complete SysEx echo within 5 s"));
    }

    // -----------------------------------------------------------------------
    // Check 5: Recovery journal enabled
    // -----------------------------------------------------------------------

    Console.Write(NextLabel("Recovery journal (Chapter X)… "));

    results.Add(session.EnableRecoveryJournal
        ? Pass("recovery-journal", "EnableRecoveryJournal=true")
        : Fail("recovery-journal", "EnableRecoveryJournal=false — journal is disabled"));

    // -----------------------------------------------------------------------
    // Check 6: Control Change (CC #7 Volume, CC #10 Pan)
    // -----------------------------------------------------------------------

    Console.Write(NextLabel("Control Change (CC #7 Vol, #10 Pan)… "));

    if (!loopback)
    {
        results.Add(Skip("cc", "pass --loopback to enable"));
    }
    else
    {
        byte[] cc7  = ToRawBytes(new ControlChangeEvent((SevenBitNumber)7,  (SevenBitNumber)100) { Channel = ch });
        byte[] cc10 = ToRawBytes(new ControlChangeEvent((SevenBitNumber)10, (SevenBitNumber)64)  { Channel = ch });

        lock (midiLock) receivedMidi.Clear();
        await session.SendMidiAsync(cc7,  cts.Token);
        await session.SendMidiAsync(cc10, cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        bool ok = false;
        while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
        {
            lock (midiLock)
                if (receivedMidi.Any(m => m.SequenceEqual(cc7)) &&
                    receivedMidi.Any(m => m.SequenceEqual(cc10)))
                { ok = true; break; }
            await Task.Delay(50, cts.Token);
        }

        results.Add(ok
            ? Pass("cc", "CC #7 and #10 echoed back")
            : Fail("cc", "CC messages not echoed within 2 s"));
    }

    // -----------------------------------------------------------------------
    // Check 7: Program Change
    // -----------------------------------------------------------------------

    Console.Write(NextLabel("Program Change… "));

    if (!loopback)
    {
        results.Add(Skip("program-change", "pass --loopback to enable"));
    }
    else
    {
        byte[] pc = ToRawBytes(new ProgramChangeEvent((SevenBitNumber)25) { Channel = ch });

        lock (midiLock) receivedMidi.Clear();
        await session.SendMidiAsync(pc, cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        bool ok = false;
        while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
        {
            lock (midiLock)
                if (receivedMidi.Any(m => m.SequenceEqual(pc)))
                { ok = true; break; }
            await Task.Delay(50, cts.Token);
        }

        results.Add(ok
            ? Pass("program-change", "Program Change #25 echoed back")
            : Fail("program-change", "Program Change not echoed within 2 s"));
    }

    // -----------------------------------------------------------------------
    // Check 8: Pitch Wheel
    // -----------------------------------------------------------------------

    Console.Write(NextLabel("Pitch Wheel… "));

    if (!loopback)
    {
        results.Add(Skip("pitch-wheel", "pass --loopback to enable"));
    }
    else
    {
        // Non-centre value (10 000 → lsb=0x10 msb=0x4E)
        byte[] pb = ToRawBytes(new PitchBendEvent(10_000) { Channel = ch });

        lock (midiLock) receivedMidi.Clear();
        await session.SendMidiAsync(pb, cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        bool ok = false;
        while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
        {
            lock (midiLock)
                if (receivedMidi.Any(m => m.SequenceEqual(pb)))
                { ok = true; break; }
            await Task.Delay(50, cts.Token);
        }

        results.Add(ok
            ? Pass("pitch-wheel", "Pitch Bend value=10000 echoed back")
            : Fail("pitch-wheel", "Pitch Bend not echoed within 2 s"));
    }

    // -----------------------------------------------------------------------
    // Check 9: Polyphony + Note Off (vel=0 variant)
    // -----------------------------------------------------------------------

    Console.Write(NextLabel("Polyphony + Note Off (vel=0)… "));

    if (!loopback)
    {
        results.Add(Skip("polyphony", "pass --loopback to enable"));
    }
    else
    {
        // Two simultaneous notes; release C4 via Note On vel=0
        byte[] noteC = ToRawBytes(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)90) { Channel = ch });
        byte[] noteE = ToRawBytes(new NoteOnEvent((SevenBitNumber)64, (SevenBitNumber)80) { Channel = ch });
        byte[] relC  = ToRawBytes(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)0)  { Channel = ch }); // vel=0 = Note Off

        lock (midiLock) receivedMidi.Clear();
        await session.SendMidiAsync(noteC, cts.Token);
        await session.SendMidiAsync(noteE, cts.Token);
        await session.SendMidiAsync(relC,  cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        bool ok = false;
        while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
        {
            lock (midiLock)
                if (receivedMidi.Any(m => m.SequenceEqual(noteC)) &&
                    receivedMidi.Any(m => m.SequenceEqual(noteE)) &&
                    receivedMidi.Any(m => m.SequenceEqual(relC)))
                { ok = true; break; }
            await Task.Delay(50, cts.Token);
        }

        results.Add(ok
            ? Pass("polyphony", "C4+E4 poly on, C4 released via vel=0 — all echoed back")
            : Fail("polyphony", "polyphonic notes not all echoed within 2 s"));
    }

    // -----------------------------------------------------------------------
    // Check 10: Channel Aftertouch
    // -----------------------------------------------------------------------

    Console.Write(NextLabel("Channel Aftertouch… "));

    if (!loopback)
    {
        results.Add(Skip("channel-at", "pass --loopback to enable"));
    }
    else
    {
        byte[] at = ToRawBytes(new ChannelAftertouchEvent((SevenBitNumber)64) { Channel = ch });

        lock (midiLock) receivedMidi.Clear();
        await session.SendMidiAsync(at, cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        bool ok = false;
        while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
        {
            lock (midiLock)
                if (receivedMidi.Any(m => m.SequenceEqual(at)))
                { ok = true; break; }
            await Task.Delay(50, cts.Token);
        }

        results.Add(ok
            ? Pass("channel-at", "Channel Aftertouch pressure=64 echoed back")
            : Fail("channel-at", "Channel Aftertouch not echoed within 2 s"));
    }

    // -----------------------------------------------------------------------
    // Check 11: Poly Key Pressure (Note Aftertouch)
    // -----------------------------------------------------------------------

    Console.Write(NextLabel("Poly Key Pressure… "));

    if (!loopback)
    {
        results.Add(Skip("poly-kp", "pass --loopback to enable"));
    }
    else
    {
        byte[] pkp = ToRawBytes(new NoteAftertouchEvent((SevenBitNumber)60, (SevenBitNumber)40) { Channel = ch });

        lock (midiLock) receivedMidi.Clear();
        await session.SendMidiAsync(pkp, cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        bool ok = false;
        while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
        {
            lock (midiLock)
                if (receivedMidi.Any(m => m.SequenceEqual(pkp)))
                { ok = true; break; }
            await Task.Delay(50, cts.Token);
        }

        results.Add(ok
            ? Pass("poly-kp", "Poly Key Pressure note=60 pres=40 echoed back")
            : Fail("poly-kp", "Poly Key Pressure not echoed within 2 s"));
    }

    // -----------------------------------------------------------------------
    // Check 12: MTC Quarter Frame (System Common)
    // -----------------------------------------------------------------------

    Console.Write(NextLabel("MTC Quarter Frame (System Common)… "));

    if (!loopback)
    {
        results.Add(Skip("mtc-qf", "pass --loopback to enable"));
    }
    else
    {
        byte[] mtc = ToRawBytes(new MidiTimeCodeEvent(MidiTimeCodeComponent.FramesLsb, (FourBitNumber)5));

        lock (midiLock) receivedMidi.Clear();
        await session.SendMidiAsync(mtc, cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        bool ok = false;
        while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
        {
            lock (midiLock)
                if (receivedMidi.Any(m => m.SequenceEqual(mtc)))
                { ok = true; break; }
            await Task.Delay(50, cts.Token);
        }

        results.Add(ok
            ? Pass("mtc-qf", "MTC Quarter Frame (F1 05) echoed back")
            : Fail("mtc-qf", "MTC Quarter Frame not echoed within 2 s"));
    }

    // -----------------------------------------------------------------------
    // Check 13: Sequence-number gap + recovery journal
    // -----------------------------------------------------------------------

    Console.Write(NextLabel("Seq-number gap + journal recovery… "));

    if (!loopback)
    {
        results.Add(Skip("seq-gap-recovery", "pass --loopback to enable"));
    }
    else
    {
        // Step 1: Send a note so it lands in the recovery journal.
        // Use A4 (note 69) — unlikely to appear in any other check.
        byte[] journalNote = ToRawBytes(new NoteOnEvent((SevenBitNumber)69, (SevenBitNumber)64) { Channel = ch });

        lock (midiLock) receivedMidi.Clear();
        await session.SendMidiAsync(journalNote, cts.Token);

        // Wait for the echo so we know the server is in sync
        var syncDeadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < syncDeadline && !cts.IsCancellationRequested)
        {
            lock (midiLock)
                if (receivedMidi.Any(m => m.SequenceEqual(journalNote))) break;
            await Task.Delay(50, cts.Token);
        }

        // Step 2: Skip one outgoing sequence number (simulates a dropped packet).
        // The server will see a gap and use the journal from the next packet to
        // recover journalNote.
        lock (midiLock) receivedMidi.Clear();
        session.SimulatePacketLoss(1);

        // Step 3: Send a different note (B4 = 71) to carry the journal.
        byte[] triggerNote = ToRawBytes(new NoteOnEvent((SevenBitNumber)71, (SevenBitNumber)64) { Channel = ch });
        await session.SendMidiAsync(triggerNote, cts.Token);

        // Step 4: The server should echo back the journal-recovered A4 AND the actual B4.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        bool recovered = false;
        while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
        {
            lock (midiLock)
                if (receivedMidi.Any(m => m.SequenceEqual(journalNote)) &&
                    receivedMidi.Any(m => m.SequenceEqual(triggerNote)))
                { recovered = true; break; }
            await Task.Delay(50, cts.Token);
        }

        results.Add(recovered
            ? Pass("seq-gap-recovery", "A4 recovered from journal across 1-packet gap; B4 echoed directly")
            : Fail("seq-gap-recovery", "journal recovery did not produce expected echo within 3 s"));
    }

    // -----------------------------------------------------------------------
    // Optional check: ALSA verification via DryWetMidi (Linux, --alsa-verify)
    // -----------------------------------------------------------------------

    if (alsaVerify)
    {
        Console.Write(NextLabel("MIDI → rtpmidid → ALSA (DryWetMidi)… "));

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            results.Add(Skip("alsa-verify", "ALSA verification is only supported on Linux"));
        }
        else
        {
            try
            {
                // Give rtpmidid a moment to create its ALSA port after the RTP-MIDI connection
                await Task.Delay(500, cts.Token);

                ICollection<InputDevice> allDevices = InputDevice.GetAll();
                var rtpmididIn = allDevices.FirstOrDefault(d =>
                    d.Name.Contains("rtpmidid", StringComparison.OrdinalIgnoreCase) ||
                    d.Name.Contains("Network",  StringComparison.OrdinalIgnoreCase));

                if (rtpmididIn == null)
                {
                    var names = string.Join(", ", allDevices.Select(d => $"'{d.Name}'"));
                    results.Add(Skip("alsa-verify",
                        $"no rtpmidid ALSA port found (available: {(names.Length > 0 ? names : "none")})"));
                }
                else
                {
                    // Use a note not seen in any other check (F#4 = 66)
                    byte[] alsaTestNote = ToRawBytes(
                        new NoteOnEvent((SevenBitNumber)66, (SevenBitNumber)80) { Channel = ch });

                    var alsaReceived = new List<MidiEvent>();
                    rtpmididIn.EventReceived += (_, e) => { lock (alsaReceived) alsaReceived.Add(e.Event); };
                    rtpmididIn.StartEventsListening();

                    await session.SendMidiAsync(alsaTestNote, cts.Token);

                    // Wait up to 2 s for the event to appear on the ALSA port
                    var deadline = DateTime.UtcNow.AddSeconds(2);
                    bool alsaOk = false;
                    while (DateTime.UtcNow < deadline && !cts.IsCancellationRequested)
                    {
                        lock (alsaReceived)
                            if (alsaReceived.OfType<NoteOnEvent>()
                                            .Any(e => (byte)e.NoteNumber == 66))
                            { alsaOk = true; break; }
                        await Task.Delay(50, cts.Token);
                    }

                    rtpmididIn.StopEventsListening();
                    rtpmididIn.Dispose();

                    results.Add(alsaOk
                        ? Pass("alsa-verify", $"Note On F#4 arrived on ALSA port '{rtpmididIn.Name}' via rtpmidid")
                        : Fail("alsa-verify", $"Note On not received on ALSA port '{rtpmididIn.Name}' within 2 s"));
                }
            }
            catch (Exception ex)
            {
                results.Add(Skip("alsa-verify", $"MIDI device enumeration failed: {ex.Message}"));
            }
        }
    }

    // -----------------------------------------------------------------------
    // Final check: Clean disconnection
    // -----------------------------------------------------------------------

    Console.Write(NextLabel("Clean disconnection (BY)… "));

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

    using var advertiser = new RtpMidiAdvertiser(name, (ushort)port);
    advertiser.Start();
    Console.WriteLine($"  [mDNS] Advertising '{name}' on port {port}.");

    int sessionCount = 0;

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
            0xF1 => $"MTC Quarter Frame 0x{(data.Length > 1 ? data[1] : 0):X2}",
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
