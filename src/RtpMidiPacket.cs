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

    /// <summary>
    /// <c>true</c> when the Z flag is set: every MIDI command in the list is preceded
    /// by a variable-length quantity (VLQ) delta-time (RFC 6295 §3.1).
    /// </summary>
    public bool HasDeltaTime { get; init; }

    /// <summary>
    /// <c>true</c> when the P flag is set: the first MIDI command in the list has no
    /// status byte and instead re-uses the running status from the previous packet
    /// (RFC 6295 §3.2).
    /// </summary>
    public bool HasPhantomStatus { get; init; }

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

        // RFC 6295 §4.1 — skip past CSRC list (CC entries × 4 bytes each)
        int cc = data[0] & 0x0F;
        bool extensionBit = (data[0] & 0x10) != 0;
        int payloadOffset = RtpHeaderSize + cc * 4;

        // RFC 6295 §4.1 — skip the RTP header extension (X=1): 4-byte extension header
        // whose last two bytes contain the extension length (in 32-bit words).
        if (extensionBit)
        {
            if (data.Length < payloadOffset + 4)
                return false;
            int extWords = BinaryPrimitives.ReadUInt16BigEndian(data[(payloadOffset + 2)..]);
            payloadOffset += 4 + extWords * 4;
        }

        if (data.Length < payloadOffset + 1)
            return false;

        // Parse MIDI command section header
        var section = data[payloadOffset..];
        if (section.IsEmpty)
            return false;

        bool longHeader       = (section[0] & FlagBig)     != 0;
        bool journalPresent   = (section[0] & FlagJournal) != 0;
        bool hasDeltaTime     = (section[0] & FlagDelta)   != 0;
        bool hasPhantomStatus = (section[0] & FlagPhantom) != 0;
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
            SequenceNumber  = seq,
            Timestamp       = ts,
            Ssrc            = ssrc,
            MidiBytes       = midiBytes,
            JournalBytes    = journalBytes,
            HasDeltaTime    = hasDeltaTime,
            HasPhantomStatus = hasPhantomStatus,
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
        // M=1 (marker bit, RFC 4695 §3.4): marks phrase start. Apple CoreMIDI sets this on
        // every packet; many hardware receivers depend on it for phrase boundary detection.
        buf[1] = (byte)(0x80 | PayloadType);
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

    /// <summary>
    /// Encodes an RTP-MIDI packet from a list of MIDI commands with optional delta-times
    /// and cross-packet running status (phantom status).
    ///
    /// Sets the Z flag (delta-time) when multiple commands are present or any command has a
    /// non-zero delta-time, prefixing each command with a VLQ delta-time (RFC 6295 §3.1).
    ///
    /// Applies within-packet running status compression: consecutive commands sharing the
    /// same channel status byte have the status byte omitted.
    ///
    /// Sets the P flag (phantom status) when <paramref name="phantomStatus"/> matches the
    /// first command's status byte, omitting that status byte and signalling the receiver
    /// to supply it from its cross-packet running status (RFC 6295 §3.2).
    /// </summary>
    /// <param name="commands">MIDI commands to encode; each must include the status byte.</param>
    /// <param name="phantomStatus">
    /// Running status byte carried over from the previous packet.
    /// When non-null and matching the first command's status, enables the P flag.
    /// Pass <c>null</c> to suppress phantom-status optimisation.
    /// </param>
    public static byte[] Encode(uint ssrc, ushort sequenceNumber, uint timestamp,
        IReadOnlyList<MidiCommand> commands, byte? phantomStatus = null)
        => Encode(ssrc, sequenceNumber, timestamp, commands, phantomStatus, ReadOnlySpan<byte>.Empty);

    /// <summary>
    /// Encodes an RTP-MIDI packet from a list of MIDI commands with delta-times, optional
    /// phantom status, and an optional recovery journal.
    /// </summary>
    public static byte[] Encode(uint ssrc, ushort sequenceNumber, uint timestamp,
        IReadOnlyList<MidiCommand> commands, byte? phantomStatus, ReadOnlySpan<byte> journalBytes)
    {
        var (midiSection, hasDelta, hasPhantom) = EncodeCommandSection(commands, phantomStatus);
        ReadOnlySpan<byte> midiSpan = midiSection;

        bool longHeader     = midiSpan.Length > 15;
        bool journalPresent = !journalBytes.IsEmpty;
        int  headerBytes    = longHeader ? 2 : 1;
        var  buf            = new byte[RtpHeaderSize + headerBytes + midiSpan.Length + journalBytes.Length];

        // RTP header
        buf[0] = RtpVersion;
        buf[1] = (byte)(0x80 | PayloadType); // M=1: phrase-start marker (RFC 4695 §3.4)
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2), sequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), timestamp);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8), ssrc);

        // MIDI command section header (RFC 6295 §3.2)
        byte flags = 0;
        if (journalPresent) flags |= FlagJournal;
        if (hasDelta)       flags |= FlagDelta;
        if (hasPhantom)     flags |= FlagPhantom;

        if (longHeader)
        {
            flags |= FlagBig;
            buf[12] = (byte)(flags | ((midiSpan.Length >> 8) & 0x0F));
            buf[13] = (byte)(midiSpan.Length & 0xFF);
        }
        else
        {
            buf[12] = (byte)(flags | (midiSpan.Length & 0x0F));
        }

        midiSpan.CopyTo(buf.AsSpan(RtpHeaderSize + headerBytes));

        if (journalPresent)
            journalBytes.CopyTo(buf.AsSpan(RtpHeaderSize + headerBytes + midiSpan.Length));

        return buf;
    }

    /// <summary>
    /// Decodes the MIDI command list from this packet's <see cref="MidiBytes"/>, restoring
    /// VLQ delta-times (when <see cref="HasDeltaTime"/> is set) and expanding omitted status
    /// bytes from within-packet running status and cross-packet phantom status.
    /// </summary>
    /// <param name="phantomStatus">
    /// The running status byte from the previous packet (required when
    /// <see cref="HasPhantomStatus"/> is <c>true</c> to reconstruct the first command's
    /// missing status byte). Pass <c>null</c> if not tracking cross-packet running status.
    /// </param>
    /// <returns>
    /// An ordered list of <see cref="MidiCommand"/> values, each with its delta-time and
    /// full MIDI data (status byte always present at index 0).
    /// </returns>
    public IReadOnlyList<MidiCommand> DecodeCommands(byte? phantomStatus = null)
        => DecodeCommandSection(MidiBytes.Span, HasDeltaTime, HasPhantomStatus, phantomStatus);

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

    // -------------------------------------------------------------------------
    // MIDI command section helpers (RFC 6295 §3.1–3.2)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Encodes a list of <see cref="MidiCommand"/> values into raw MIDI command section bytes,
    /// applying VLQ delta-time prefixes and running-status compression.
    ///
    /// Returns the encoded byte array together with the Z (hasDelta) and P (hasPhantom)
    /// flag values that should be written into the MIDI command section header.
    /// </summary>
    internal static (byte[] encoded, bool hasDelta, bool hasPhantom) EncodeCommandSection(
        IReadOnlyList<MidiCommand> commands, byte? phantomStatus)
    {
        if (commands.Count == 0)
            return (Array.Empty<byte>(), false, false);

        // Z flag: prefix all commands with VLQ delta-times when there are multiple commands
        // or the single command carries a non-zero delta-time.
        bool hasDelta   = commands.Count > 1 || commands[0].DeltaTime > 0;
        bool hasPhantom = false;

        var buf = new List<byte>();
        byte? runningStatus = null;
        Span<byte> vlqBuf = stackalloc byte[4];

        for (int i = 0; i < commands.Count; i++)
        {
            var  cmd  = commands[i];
            var  data = cmd.Data.Span;

            if (data.IsEmpty)
                continue;

            // Write VLQ delta-time when Z=1.
            if (hasDelta)
            {
                int vlqLen = WriteVlq(cmd.DeltaTime, vlqBuf);
                for (int j = 0; j < vlqLen; j++) buf.Add(vlqBuf[j]);
            }

            byte status           = data[0];
            bool isChannelMessage = status >= 0x80 && status < 0xF0; // Only channel voice messages participate in running status

            if (i == 0 && phantomStatus.HasValue && status == phantomStatus.Value && isChannelMessage)
            {
                // Cross-packet running status (P flag): omit first command's status byte.
                hasPhantom    = true;
                runningStatus = status;
                for (int j = 1; j < data.Length; j++) buf.Add(data[j]);
            }
            else if (i > 0 && runningStatus.HasValue && status == runningStatus.Value && isChannelMessage)
            {
                // Within-packet running status: omit redundant channel-status byte.
                for (int j = 1; j < data.Length; j++) buf.Add(data[j]);
            }
            else
            {
                // Include the full command.
                for (int j = 0; j < data.Length; j++) buf.Add(data[j]);
                // Update running status: channel messages (0x80–0xEF) establish it;
                // SysEx and system common (0xF0–0xF7) cancel it;
                // real-time (0xF8–0xFF) leave it unchanged.
                if (status >= 0x80 && status < 0xF0)
                    runningStatus = status;
                else if (status < 0xF8)
                    runningStatus = null;
            }
        }

        return (buf.ToArray(), hasDelta, hasPhantom);
    }

    /// <summary>
    /// Decodes raw MIDI command section bytes into a list of <see cref="MidiCommand"/> values,
    /// parsing VLQ delta-times and restoring omitted status bytes from running status.
    /// </summary>
    internal static IReadOnlyList<MidiCommand> DecodeCommandSection(
        ReadOnlySpan<byte> bytes, bool hasDelta, bool hasPhantom, byte? phantomStatus)
    {
        var  commands     = new List<MidiCommand>();
        int  pos          = 0;
        byte? runningStatus = null;
        bool isFirst      = true;

        while (pos < bytes.Length)
        {
            // 1. Read VLQ delta-time (Z=1 only).
            uint deltaTime = 0;
            if (hasDelta)
            {
                if (!TryReadVlq(bytes[pos..], out deltaTime, out int vlqLen))
                    break;
                pos += vlqLen;
                if (pos >= bytes.Length)
                    break;
            }

            // 2. Determine the effective status byte.
            bool statusInStream; // whether the status byte is physically present at [pos]

            byte status;
            if (isFirst && hasPhantom && phantomStatus.HasValue)
            {
                // P=1: first command has no status byte; use phantom status.
                status         = phantomStatus.Value;
                statusInStream = false;
                // Phantom status is always a channel message; establish running status.
                if (status < 0xF0)
                    runningStatus = status;
            }
            else
            {
                byte b = bytes[pos];
                if (b >= 0x80)
                {
                    // New status byte present in the stream.
                    status         = b;
                    statusInStream = true;
                    // Channel messages (0x80–0xEF) establish running status;
                    // system common (0xF0–0xF7) cancel it;
                    // real-time (0xF8–0xFF) leave it unchanged.
                    if (b < 0xF0)
                        runningStatus = b;
                    else if (b < 0xF8)
                        runningStatus = null;
                }
                else if (runningStatus.HasValue)
                {
                    // Within-packet running status: data byte with no status byte.
                    status         = runningStatus.Value;
                    statusInStream = false;
                }
                else
                {
                    break; // Malformed: data byte without any running status — stop parsing.
                }
            }

            isFirst = false;

            // 3. Build the full MIDI command (always with status byte at index 0).
            ReadOnlyMemory<byte> cmdData;
            int dataStart = statusInStream ? pos + 1 : pos;

            if (status == 0xF0 || status == 0xF7)
            {
                // SysEx (variable length): scan forward for the 0xF7 terminator.
                int f7Pos = dataStart;
                while (f7Pos < bytes.Length && bytes[f7Pos] != 0xF7) f7Pos++;
                if (f7Pos < bytes.Length) f7Pos++; // include the 0xF7 byte

                int dataLen = f7Pos - dataStart;
                var full    = new byte[1 + dataLen];
                full[0]     = status;
                bytes.Slice(dataStart, dataLen).CopyTo(full.AsSpan(1));
                cmdData       = full;
                pos           = f7Pos;
                runningStatus = null; // SysEx resets running status
            }
            else
            {
                int dataLen   = GetStatusDataLength(status);
                if (dataLen < 0)
                    break; // Unknown status byte — stop parsing.

                int available = Math.Min(dataLen, bytes.Length - dataStart);
                var full      = new byte[1 + dataLen];
                full[0]       = status;
                bytes.Slice(dataStart, available).CopyTo(full.AsSpan(1));
                cmdData = full;
                pos     = dataStart + dataLen;
            }

            commands.Add(new MidiCommand(deltaTime, cmdData));
        }

        return commands;
    }

    /// <summary>
    /// Encodes <paramref name="value"/> as a MIDI variable-length quantity (VLQ) into
    /// <paramref name="buf"/> and returns the number of bytes written (1–4).
    /// </summary>
    internal static int WriteVlq(uint value, Span<byte> buf)
    {
        // Collect 7-bit groups from LSB to MSB.
        Span<byte> temp = stackalloc byte[4];
        int len = 0;
        do
        {
            temp[len++] = (byte)(value & 0x7F);
            value >>= 7;
        } while (value > 0);

        // Write groups in MSB-first order; all but the last byte have bit 7 set.
        for (int i = 0; i < len; i++)
        {
            byte b = temp[len - 1 - i];
            if (i < len - 1) b |= 0x80;
            buf[i] = b;
        }

        return len;
    }

    /// <summary>
    /// Reads one MIDI VLQ from <paramref name="buf"/>.
    /// Returns <c>false</c> when the span is empty or the VLQ is malformed.
    /// </summary>
    internal static bool TryReadVlq(ReadOnlySpan<byte> buf, out uint value, out int consumed)
    {
        value    = 0;
        consumed = 0;

        for (int i = 0; i < 4 && i < buf.Length; i++)
        {
            value = (value << 7) | (uint)(buf[i] & 0x7F);
            consumed++;
            if ((buf[i] & 0x80) == 0)
                return true;
        }

        return false; // Malformed or incomplete VLQ
    }

    /// <summary>
    /// Returns the number of data bytes that follow a given MIDI status byte
    /// (not counting the status byte itself).
    /// Returns <c>-1</c> for SysEx (variable length, requires special handling).
    /// </summary>
    internal static int GetStatusDataLength(byte status) => status switch
    {
        >= 0xF8            => 0,  // Real-time: status only
        0xF6               => 0,  // Tune Request: status only
        0xF4 or 0xF5       => 0,  // Undefined system common
        0xF3               => 1,  // Song Select
        0xF2               => 2,  // Song Position Pointer
        0xF1               => 1,  // MTC Quarter Frame
        0xF0 or 0xF7       => -1, // SysEx: variable length
        >= 0xE0 and < 0xF0 => 2,  // Pitch Bend
        >= 0xC0 and < 0xE0 => 1,  // Program Change, Channel Pressure
        >= 0x80            => 2,  // Note Off/On, Poly Pressure, CC
        _                  => -1, // Data byte / unknown
    };
}
