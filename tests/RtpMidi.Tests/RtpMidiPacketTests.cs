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
}
