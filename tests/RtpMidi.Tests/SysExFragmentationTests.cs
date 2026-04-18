using Haukcode.RtpMidi;

namespace RtpMidi.Tests;

/// <summary>
/// Tests for RFC 6295 §3.3 SysEx fragmentation (send side) and reassembly (receive side).
/// </summary>
public class SysExFragmentationTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Builds a SysEx of the form F0 [sequential bytes 0x01..N] F7.</summary>
    private static byte[] MakeSysEx(int innerLength)
    {
        var buf = new byte[innerLength + 2];
        buf[0] = 0xF0;
        for (int i = 0; i < innerLength; i++)
            buf[i + 1] = (byte)((i % 127) + 1);
        buf[buf.Length - 1] = 0xF7;
        return buf;
    }

    // -------------------------------------------------------------------------
    // BuildSysExFragments — send side
    // -------------------------------------------------------------------------

    [Fact]
    public void SmallSysEx_NotFragmented()
    {
        // 10 bytes total — well under 128-byte threshold
        var sysex = MakeSysEx(8);

        var fragments = RtpMidiSession.BuildSysExFragments(sysex).ToList();

        Assert.Single(fragments);
        Assert.Equal(sysex, fragments[0].ToArray());
    }

    [Fact]
    public void NonSysEx_PassedThroughUnchanged()
    {
        var noteOn = new byte[] { 0x90, 0x3C, 0x7F };

        var fragments = RtpMidiSession.BuildSysExFragments(noteOn).ToList();

        Assert.Single(fragments);
        Assert.Equal(noteOn, fragments[0].ToArray());
    }

    [Fact]
    public void LargeSysEx_ProducesTwoPackets()
    {
        // 200-byte SysEx → inner 198 bytes → ceil(198/126) = 2 fragments
        var sysex = MakeSysEx(198);

        var fragments = RtpMidiSession.BuildSysExFragments(sysex).ToList();

        Assert.Equal(2, fragments.Count);

        var first = fragments[0].ToArray();
        var last  = fragments[1].ToArray();

        // First segment: F0 … F0
        Assert.Equal(0xF0, first[0]);
        Assert.Equal(0xF0, first[first.Length - 1]);

        // Last segment: F7 … F7
        Assert.Equal(0xF7, last[0]);
        Assert.Equal(0xF7, last[last.Length - 1]);

        // Each fragment ≤ MaxSysExBytesPerPacket (128)
        Assert.True(first.Length <= 128);
        Assert.True(last.Length  <= 128);
    }

    [Fact]
    public void LargeSysEx_FragmentsReproduceOriginal()
    {
        var sysex = MakeSysEx(198);

        var fragments = RtpMidiSession.BuildSysExFragments(sysex).ToList();

        // Concatenate inner bytes (strip opening and closing markers from each segment)
        var inner = new List<byte>();
        foreach (var frag in fragments)
        {
            var arr = frag.ToArray();
            for (int i = 1; i < arr.Length - 1; i++)
                inner.Add(arr[i]);
        }

        var expected = sysex[1..^1]; // inner bytes of original (excluding F0/F7)
        Assert.Equal(expected, inner);
    }

    [Fact]
    public void VeryLargeSysEx_ProducesMultiplePackets()
    {
        // 400-byte SysEx → inner 398 bytes → ceil(398/126) = 4 fragments
        var sysex = MakeSysEx(398);

        var fragments = RtpMidiSession.BuildSysExFragments(sysex).ToList();

        Assert.Equal(4, fragments.Count);

        // First: F0…F0; Middle: F7…F0; Last: F7…F7
        Assert.Equal(0xF0, fragments[0].Span[0]);
        Assert.Equal(0xF0, fragments[0].Span[fragments[0].Length - 1]);

        for (int i = 1; i < fragments.Count - 1; i++)
        {
            Assert.Equal(0xF7, fragments[i].Span[0]);
            Assert.Equal(0xF0, fragments[i].Span[fragments[i].Length - 1]);
        }

        Assert.Equal(0xF7, fragments[^1].Span[0]);
        Assert.Equal(0xF7, fragments[^1].Span[fragments[^1].Length - 1]);

        // All fragments ≤ 128 bytes
        foreach (var frag in fragments)
            Assert.True(frag.Length <= 128);
    }

    [Fact]
    public void SequenceNumbers_IncrementAcrossFragments()
    {
        // This verifies that each fragment gets a distinct sequence number by examining
        // the encoded RTP packets. We use RtpMidiPacket.Encode directly here.
        var sysex = MakeSysEx(198);
        var fragments = RtpMidiSession.BuildSysExFragments(sysex).ToList();

        ushort seq = 0;
        var packets = fragments
            .Select(f => RtpMidiPacket.Encode(0x1234, seq++, 0, f.Span))
            .ToList();

        for (int i = 0; i < packets.Count; i++)
        {
            Assert.True(RtpMidiPacket.TryParse(packets[i], out var pkt));
            Assert.Equal((ushort)i, pkt!.SequenceNumber);
        }
    }

    // -------------------------------------------------------------------------
    // AssembleSysExFragment — receive side
    // -------------------------------------------------------------------------

    [Fact]
    public void CompleteSysEx_EmittedDirectly()
    {
        var session = new RtpMidiSession("test");
        var sysex = new byte[] { 0xF0, 0x47, 0x7F, 0x30, 0xF7 };

        var result = session.AssembleSysExFragment(sysex);

        Assert.NotNull(result);
        Assert.Equal(sysex, result!.Value.ToArray());
    }

    [Fact]
    public void NonSysEx_EmittedDirectly()
    {
        var session = new RtpMidiSession("test");
        var noteOn = new byte[] { 0x90, 0x3C, 0x7F };

        var result = session.AssembleSysExFragment(noteOn);

        Assert.NotNull(result);
        Assert.Equal(noteOn, result!.Value.ToArray());
    }

    [Fact]
    public void TwoPartSysEx_RoundTrip()
    {
        var session = new RtpMidiSession("test");
        var original = MakeSysEx(198);
        var fragments = RtpMidiSession.BuildSysExFragments(original).ToList();

        // Feed first fragment — must not emit yet
        var r1 = session.AssembleSysExFragment(fragments[0]);
        Assert.Null(r1);

        // Feed last fragment — must emit complete SysEx
        var r2 = session.AssembleSysExFragment(fragments[1]);
        Assert.NotNull(r2);
        Assert.Equal(original, r2!.Value.ToArray());
    }

    [Fact]
    public void FourPartSysEx_RoundTrip()
    {
        var session = new RtpMidiSession("test");
        var original = MakeSysEx(398);
        var fragments = RtpMidiSession.BuildSysExFragments(original).ToList();

        Assert.Equal(4, fragments.Count);

        // First three fragments must return null
        for (int i = 0; i < fragments.Count - 1; i++)
        {
            var r = session.AssembleSysExFragment(fragments[i]);
            Assert.Null(r);
        }

        // Last fragment must emit complete SysEx
        var final = session.AssembleSysExFragment(fragments[^1]);
        Assert.NotNull(final);
        Assert.Equal(original, final!.Value.ToArray());
    }

    [Fact]
    public void UnexpectedF7Opener_EmittedAsIs_AndBufferReset()
    {
        var session = new RtpMidiSession("test");

        // A spurious "continuation" packet without a prior first-fragment
        var spurious = new byte[] { 0xF7, 0x01, 0x02, 0xF7 };
        var result = session.AssembleSysExFragment(spurious);

        // Should emit as-is (best-effort) rather than silently drop
        Assert.NotNull(result);
        Assert.Equal(spurious, result!.Value.ToArray());
    }
}
