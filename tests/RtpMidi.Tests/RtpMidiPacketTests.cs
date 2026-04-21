using Haukcode.RtpMidi;

namespace RtpMidi.Tests;

public class RtpMidiPacketTests
{
    private static readonly byte[] SimpleMidi = [0x90, 0x3C, 0x7F]; // Note On, C4, vel 127

    // -------------------------------------------------------------------------
    // Encode → Parse round-trips
    // -------------------------------------------------------------------------

    [Fact]
    public void ShortHeader_RoundTrip()
    {
        var encoded = RtpMidiPacket.Encode(
            ssrc: 0xAABBCCDD,
            sequenceNumber: 42,
            timestamp: 1000,
            midiBytes: SimpleMidi);

        Assert.True(RtpMidiPacket.TryParse(encoded, out var pkt));
        Assert.NotNull(pkt);
        Assert.Equal(42, pkt.SequenceNumber);
        Assert.Equal(1000u, pkt.Timestamp);
        Assert.Equal(0xAABBCCDDu, pkt.Ssrc);
        Assert.Equal(SimpleMidi, pkt.MidiBytes.ToArray());
    }

    [Fact]
    public void LongHeader_UsedWhenMidiExceeds15Bytes()
    {
        // 16 bytes of MIDI forces long header
        var midi = new byte[16];
        for (int i = 0; i < midi.Length; i++) midi[i] = (byte)(0xB0 + i % 16);

        var encoded = RtpMidiPacket.Encode(0x01020304, 1, 0, midi);

        // Verify long-header bit is set (byte at offset 12, bit 7)
        Assert.True((encoded[12] & 0x80) != 0, "Long header B-bit should be set");

        Assert.True(RtpMidiPacket.TryParse(encoded, out var pkt));
        Assert.Equal(midi, pkt!.MidiBytes.ToArray());
    }

    [Fact]
    public void ShortHeader_ExactByteValue_10ByteMessage()
    {
        // 10-byte MIDI payload → short header, buf[12] must be 0x0A (length only, no flags set)
        var midi = new byte[10];
        var encoded = RtpMidiPacket.Encode(1, 1, 0, midi);

        Assert.Equal(0x0A, encoded[12]);
    }

    [Fact]
    public void LongHeader_ExactByteValues_200ByteMessage()
    {
        // 200-byte MIDI payload → long header
        // buf[12]: B=1 (0x80) | length-high nibble = (200 >> 8) & 0x0F = 0 → 0x80
        // buf[13]: length-low byte = 200 & 0xFF = 0xC8
        var midi = new byte[200];
        var encoded = RtpMidiPacket.Encode(1, 1, 0, midi);

        Assert.Equal(0x80, encoded[12]);
        Assert.Equal(0xC8, encoded[13]);
    }


    [Fact]
    public void SysEx_RoundTrip()
    {
        // Typical SysEx: F0 ... F7
        var sysex = new byte[] { 0xF0, 0x47, 0x7F, 0x30, 0x2C, 0x01, 0x00, 0xF7 };
        var encoded = RtpMidiPacket.Encode(0x12345678, 100, 5000, sysex);

        Assert.True(RtpMidiPacket.TryParse(encoded, out var pkt));
        Assert.Equal(sysex, pkt!.MidiBytes.ToArray());
    }

    [Fact]
    public void SequenceNumber_Wraps_At_UshortMax()
    {
        var encoded = RtpMidiPacket.Encode(1, ushort.MaxValue, 0, SimpleMidi);
        Assert.True(RtpMidiPacket.TryParse(encoded, out var pkt));
        Assert.Equal(ushort.MaxValue, pkt!.SequenceNumber);
    }

    // -------------------------------------------------------------------------
    // Parse rejection
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_RejectsTooShort()
    {
        Assert.False(RtpMidiPacket.TryParse(new byte[5], out _));
    }

    [Fact]
    public void Parse_RejectsWrongRtpVersion()
    {
        var buf = RtpMidiPacket.Encode(1, 1, 0, SimpleMidi);
        buf[0] = 0x40; // V=1, not V=2
        Assert.False(RtpMidiPacket.TryParse(buf, out _));
    }

    [Fact]
    public void Parse_RejectsWrongPayloadType()
    {
        var buf = RtpMidiPacket.Encode(1, 1, 0, SimpleMidi);
        buf[1] = 0x00; // PT=0
        Assert.False(RtpMidiPacket.TryParse(buf, out _));
    }

    // -------------------------------------------------------------------------
    // Timestamp helpers
    // -------------------------------------------------------------------------

    [Fact]
    public void ToRtpTimestamp_OneMsEqualstenTicks()
    {
        var ts = RtpMidiPacket.ToRtpTimestamp(TimeSpan.FromMilliseconds(1));
        Assert.Equal(10u, ts);
    }

    [Fact]
    public void CurrentTimestamp_IncreasesOverTime()
    {
        var start = DateTime.UtcNow;
        var ts1   = RtpMidiPacket.CurrentTimestamp(start);
        System.Threading.Thread.Sleep(50);
        var ts2   = RtpMidiPacket.CurrentTimestamp(start);
        Assert.True(ts2 > ts1, "Timestamp should increase over time");
    }

    // -------------------------------------------------------------------------
    // RFC 6295 §4.1 — CC > 0 and X=1 header extension handling
    // -------------------------------------------------------------------------

    [Fact]
    public void Parse_SkipsCSRCList_WhenCCNonZero()
    {
        // Build a valid packet and inject 2 CSRC entries (CC=2, 8 extra bytes)
        var encoded = RtpMidiPacket.Encode(0xAABBCCDDu, 99, 500, SimpleMidi);

        // Expand buffer to add 2 CSRC entries after the 12-byte fixed header
        var withCsrc = new byte[encoded.Length + 2 * 4];
        encoded[0] = (byte)((encoded[0] & 0xF0) | 2); // CC=2

        // Copy fixed header (12 bytes)
        encoded.AsSpan(0, 12).CopyTo(withCsrc);
        // Insert two dummy CSRC words at offset 12
        withCsrc[12] = 0x11; withCsrc[13] = 0x22; withCsrc[14] = 0x33; withCsrc[15] = 0x44;
        withCsrc[16] = 0x55; withCsrc[17] = 0x66; withCsrc[18] = 0x77; withCsrc[19] = 0x88;
        // Copy MIDI command section after the CSRC list
        encoded.AsSpan(12).CopyTo(withCsrc.AsSpan(20));

        Assert.True(RtpMidiPacket.TryParse(withCsrc, out var pkt));
        Assert.NotNull(pkt);
        Assert.Equal(SimpleMidi, pkt!.MidiBytes.ToArray());
        Assert.Equal(99, pkt.SequenceNumber);
    }

    [Fact]
    public void Parse_SkipsHeaderExtension_WhenXBitSet()
    {
        // Build a valid packet, then inject a 4-byte extension header (X=1, extLen=0 words)
        var encoded = RtpMidiPacket.Encode(0x11223344u, 7, 100, SimpleMidi);

        var withExt = new byte[encoded.Length + 4];
        encoded[0] = (byte)(encoded[0] | 0x10); // X=1, CC=0

        // Copy fixed RTP header (12 bytes)
        encoded.AsSpan(0, 12).CopyTo(withExt);
        // Insert minimal extension header: profile=0x0000, length=0 (0 extra 32-bit words)
        withExt[12] = 0x00; withExt[13] = 0x00; // extension profile
        withExt[14] = 0x00; withExt[15] = 0x00; // extension length in 32-bit words = 0
        // Copy MIDI command section
        encoded.AsSpan(12).CopyTo(withExt.AsSpan(16));

        Assert.True(RtpMidiPacket.TryParse(withExt, out var pkt));
        Assert.NotNull(pkt);
        Assert.Equal(SimpleMidi, pkt!.MidiBytes.ToArray());
    }

    // -------------------------------------------------------------------------
    // RFC 4695 §3.4 — Marker bit (M=1)
    // -------------------------------------------------------------------------

    [Fact]
    public void Encode_SetsMarkerBit_OnEveryPacket()
    {
        // RFC 4695 §3.4: M=1 signals a phrase start.  Apple CoreMIDI and many
        // hardware bridges set M=1 on every packet.  Byte[1] of the RTP header
        // is 0x80 | PT(97) = 0xE1.
        var encoded = RtpMidiPacket.Encode(0x01, 1, 0, SimpleMidi);

        Assert.Equal(0xE1, encoded[1]); // M=1(0x80) | PT=97(0x61)
    }

    [Fact]
    public void Parse_AcceptsPacketWithMarkerBit_Set()
    {
        // TryParse already strips the M bit via (data[1] & 0x7F); this confirms
        // that a packet with M=1 still round-trips correctly.
        var encoded = RtpMidiPacket.Encode(0xAABBCCDD, 77, 5000, SimpleMidi);
        Assert.Equal(0xE1, encoded[1]);

        Assert.True(RtpMidiPacket.TryParse(encoded, out var pkt));
        Assert.NotNull(pkt);
        Assert.Equal(77, pkt!.SequenceNumber);
        Assert.Equal(SimpleMidi, pkt.MidiBytes.ToArray());
    }

    [Fact]
    public void Parse_RejectsPacket_WhenCCMakesBufferTooShort()
    {
        // CC=3 requires 12 extra bytes, but we only have the base 12-byte header
        var encoded = RtpMidiPacket.Encode(1u, 1, 0, SimpleMidi);
        encoded[0] = (byte)((encoded[0] & 0xF0) | 3); // CC=3 but no room for CSRCs
        // Truncate to just the fixed header (no CSRC room)
        var truncated = encoded[..12];
        Assert.False(RtpMidiPacket.TryParse(truncated, out _));
    }
}
