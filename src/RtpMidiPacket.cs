namespace Haukcode.RtpMidi;

/// <summary>
/// Parsed representation of an RTP-MIDI data packet (RFC 6295).
///
/// Wire format:
///   [RTP header: 12 bytes]
///   [MIDI command section header: 1-2 bytes]
///   [MIDI commands: variable]
///   [Recovery journal: variable, present only when J=1]
///
/// RTP header layout (network byte order):
///   Byte 0:  V=2 P=0 X=0 CC=0  → 0x80
///   Byte 1:  M(1) PT=97(7)     → 0x61 (or 0xE1 with marker bit)
///   Bytes 2-3:  sequence number
///   Bytes 4-7:  timestamp (100 µs units per Apple MIDI convention)
///   Bytes 8-11: SSRC
///
/// MIDI command section header:
///   If B=0 (short): 1 byte — [B=0][J=0][Z=0][P=0][len: 4 bits]
///   If B=1 (long):  2 bytes — [B=1][J=0][Z=0][P=0][len hi: 4 bits][len lo: 8 bits]
///
/// When J=1 a recovery journal (RFC 6295 §5) immediately follows the MIDI commands.
/// </summary>
public sealed class RtpMidiPacket
{
    private const byte RtpVersion    = 0x80; // V=2, P=0, X=0, CC=0
    private const byte PayloadType   = 97;
    private const int  RtpHeaderSize = 12;

    // RFC 6295 §3.2 — MIDI command section header flag bits
    private const byte FlagBig     = 0x80; // B: big-header (1 = 12-bit length, 2-byte header)
    private const byte FlagJournal = 0x40; // J: journal present (recovery journal follows MIDI list)
    private const byte FlagDelta   = 0x20; // Z: first MIDI command has a delta-time
    private const byte FlagPhantom = 0x10; // P: phantom status (re-uses running status from previous packet)

    public ushort SequenceNumber { get; init; }
    public uint Timestamp { get; init; }
    public uint Ssrc { get; init; }

    /// <summary>Raw MIDI bytes extracted from the MIDI command section.</summary>
    public ReadOnlyMemory<byte> MidiBytes { get; init; }

    /// <summary>
    /// Raw recovery journal bytes following the MIDI command section.
    /// Empty when J=0 (no journal present in this packet).
    /// </summary>
    public ReadOnlyMemory<byte> JournalBytes { get; init; }

    // -------------------------------------------------------------------------
    // Parse
    // -------------------------------------------------------------------------

    public static bool TryParse(ReadOnlySpan<byte> data, out RtpMidiPacket? packet)
    {
        packet = null;

        if (data.Length < RtpHeaderSize + 1)
            return false;

        // Validate RTP header basics
        if ((data[0] & 0xC0) != 0x80) // V must be 2
            return false;

        var pt = data[1] & 0x7F;
        if (pt != PayloadType)
            return false;

        var seq  = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        var ts   = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        var ssrc = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);

        // Parse MIDI command section header
        var section = data[RtpHeaderSize..];
        if (section.IsEmpty)
            return false;

        bool longHeader     = (section[0] & 0x80) != 0;
        bool journalPresent = (section[0] & 0x40) != 0;
        int midiLen;
        int headerBytes;

        if (longHeader)
        {
            if (section.Length < 2)
                return false;
            midiLen = ((section[0] & 0x0F) << 8) | section[1];
            headerBytes = 2;
        }
        else
        {
            midiLen = section[0] & 0x0F;
            headerBytes = 1;
        }

        if (section.Length < headerBytes + midiLen)
            return false;

        var midiBytes = section.Slice(headerBytes, midiLen).ToArray();

        // Extract journal bytes when J=1 (RFC 6295 §5)
        var journalBytes = journalPresent
            ? section.Slice(headerBytes + midiLen).ToArray()
            : Array.Empty<byte>();

        packet = new RtpMidiPacket
        {
            SequenceNumber = seq,
            Timestamp = ts,
            Ssrc = ssrc,
            MidiBytes = midiBytes,
            JournalBytes = journalBytes,
        };

        return true;
    }

    // -------------------------------------------------------------------------
    // Encode
    // -------------------------------------------------------------------------

    /// <summary>Encodes an RTP-MIDI packet without a recovery journal (J=0).</summary>
    public static byte[] Encode(uint ssrc, ushort sequenceNumber, uint timestamp, ReadOnlySpan<byte> midiBytes)
        => Encode(ssrc, sequenceNumber, timestamp, midiBytes, ReadOnlySpan<byte>.Empty);

    /// <summary>
    /// Encodes an RTP-MIDI packet with an optional recovery journal (J=1 when journal is non-empty).
    /// </summary>
    /// <param name="journalBytes">
    /// Pre-encoded journal bytes (see <see cref="RtpMidiJournal"/>).
    /// Pass <see cref="ReadOnlySpan{T}.Empty"/> to omit the journal (J=0).
    /// </param>
    public static byte[] Encode(uint ssrc, ushort sequenceNumber, uint timestamp, ReadOnlySpan<byte> midiBytes, ReadOnlySpan<byte> journalBytes)
    {
        bool longHeader     = midiBytes.Length > 15;
        bool journalPresent = !journalBytes.IsEmpty;
        int  headerBytes    = longHeader ? 2 : 1;
        var  buf            = new byte[RtpHeaderSize + headerBytes + midiBytes.Length + journalBytes.Length];

        // RTP header
        buf[0] = RtpVersion;
        buf[1] = PayloadType;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), sequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8), ssrc);

        // MIDI command section header (RFC 6295 §3.2)
        byte flags = 0;
        if (journalPresent) flags |= FlagJournal;

        if (longHeader)
        {
            flags |= FlagBig;
            buf[12] = (byte)(flags | ((midiBytes.Length >> 8) & 0x0F));
            buf[13] = (byte)(midiBytes.Length & 0xFF);
        }
        else
        {
            buf[12] = (byte)(flags | (midiBytes.Length & 0x0F));
        }

        midiBytes.CopyTo(buf.AsSpan(RtpHeaderSize + headerBytes));

        if (journalPresent)
            journalBytes.CopyTo(buf.AsSpan(RtpHeaderSize + headerBytes + midiBytes.Length));

        return buf;
    }

    // -------------------------------------------------------------------------
    // Timestamp helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Converts a <see cref="TimeSpan"/> to RTP-MIDI timestamp units (100 µs ticks).
    /// </summary>
    public static uint ToRtpTimestamp(TimeSpan elapsed)
        => (uint)(elapsed.TotalMilliseconds * 10);

    /// <summary>
    /// Returns the current RTP timestamp relative to <paramref name="sessionStart"/>.
    /// </summary>
    public static uint CurrentTimestamp(DateTime sessionStart)
        => ToRtpTimestamp(DateTime.UtcNow - sessionStart);
}
