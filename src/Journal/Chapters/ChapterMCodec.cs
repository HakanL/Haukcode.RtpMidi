using Haukcode.RtpMidi.Journal.State;

namespace Haukcode.RtpMidi.Journal.Chapters;

/// <summary>
/// Chapter M — Parameter System: RPN/NRPN (RFC 6295 §A.4).
///
/// Wire layout:
///   2-byte header: S(last) | P(pending MSB) | E(0) | U(all-RPN) | W(all-NRPN) | Z(0)
///                  | 10-bit total chapter length
///   Optional pending byte (when P=1): Q(NRPN) | PNUM_MSB[6:0]
///   Log items (most-recent-selected first), each:
///     PNUM_LSB byte: S(last item) | PNUM_LSB[6:0]
///     PNUM_MSB byte: Q(NRPN)     | PNUM_MSB[6:0]
///     flags byte:    J(data MSB) | K(data LSB) | L | M | N | T | V | R
///     optional: data-entry MSB (CC6) when J=1
///     optional: data-entry LSB (CC38) when K=1
///     optional: 2 bytes when L=1 (a-button), 2 bytes when M=1 (c-button),
///               1 byte when N=1 (count). These are skipped by the decoder;
///               the encoder never emits them.
///
/// Z optimization: when Z=1 and either U=1 or W=1, PNUM_MSB is omitted from
/// each log item (inferred from chapter-level hint). Parsed defensively for
/// interop; the encoder always sets Z=0.
/// </summary>
internal sealed class ChapterMCodec : IChapterCodec
{
    private const ushort HdrBitS = 0x8000;
    private const ushort HdrBitP = 0x4000;
    private const ushort HdrBitU = 0x1000;
    private const ushort HdrBitW = 0x0800;
    private const ushort HdrBitZ = 0x0400;
    private const ushort HdrLenMask = 0x03FF;

    private readonly ParameterLog _log;

    public ChapterMCodec(ParameterLog log) => _log = log;

    public char ChapterId => 'M';
    public bool HasData   => _log.HasAnyData;

    public byte[] Encode(bool isLastChapterInJournal)
    {
        // Compute total size for the 10-bit LENGTH field.
        int pendingSize = _log.HasPendingMsb ? 1 : 0;
        int itemsSize = 0;
        var entries = _log.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            itemsSize += 3 + (e.HasDataMsb ? 1 : 0) + (e.HasDataLsb ? 1 : 0);
        }
        int totalSize = 2 + pendingSize + itemsSize;

        // U/W are chapter-level hints (used with Z optimisation to omit
        // PNUM_MSB from each item). We set them accurately even though Z=0.
        bool allRpn  = false;
        bool allNrpn = false;
        if (entries.Count > 0)
        {
            allRpn  = true;
            allNrpn = true;
            for (int i = 0; i < entries.Count; i++)
            {
                if (entries[i].IsNrpn) allRpn  = false;
                else                   allNrpn = false;
            }
        }

        var buf = new byte[totalSize];

        ushort hdr = (ushort)(
            (isLastChapterInJournal ? HdrBitS : 0)
            | (_log.HasPendingMsb   ? HdrBitP : 0)
            | (allRpn               ? HdrBitU : 0)
            | (allNrpn              ? HdrBitW : 0)
            | (totalSize & HdrLenMask));
        buf[0] = (byte)(hdr >> 8);
        buf[1] = (byte)(hdr & 0xFF);

        int off = 2;

        if (_log.HasPendingMsb)
            buf[off++] = (byte)((_log.PendingIsNrpn ? 0x80 : 0) | (_log.PendingMsb & 0x7F));

        for (int i = 0; i < entries.Count; i++)
        {
            var  e        = entries[i];
            bool lastItem = i == entries.Count - 1;

            buf[off++] = (byte)((lastItem ? 0x80 : 0) | (e.ParamLsb & 0x7F)); // PNUM_LSB
            buf[off++] = (byte)((e.IsNrpn ? 0x80 : 0) | (e.ParamMsb & 0x7F)); // PNUM_MSB (Q=NRPN)

            byte flags = 0;
            if (e.HasDataMsb) flags |= 0x80; // J
            if (e.HasDataLsb) flags |= 0x40; // K
            buf[off++] = flags;

            if (e.HasDataMsb) buf[off++] = (byte)(e.DataMsb & 0x7F);
            if (e.HasDataLsb) buf[off++] = (byte)(e.DataLsb & 0x7F);
        }

        return buf;
    }

    public int Decode(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
        => DecodeStatic(data, channel, recovered);

    public static int DecodeStatic(ReadOnlySpan<byte> data, byte channel, List<byte[]> recovered)
    {
        if (data.Length < 2) return -1;

        ushort header  = (ushort)((data[0] << 8) | data[1]);
        bool   pending = (header & HdrBitP) != 0;
        bool   hasZ    = (header & HdrBitZ) != 0;
        bool   hasU    = (header & HdrBitU) != 0; // all-RPN hint
        bool   hasW    = (header & HdrBitW) != 0; // all-NRPN hint
        int    length  = header & HdrLenMask;

        if (length < 2 || data.Length < length) return -1;

        int pos = 2;

        // Skip the optional pending byte
        if (pending)
        {
            if (pos >= length) return length;
            pos++;
        }

        // Z optimization: PNUM_MSB is omitted from each log item when all entries
        // share the same MSB (hint given by U or W). Our encoder always sets Z=0
        // so we never generate this format, but parse it defensively.
        bool noPnumMsb = hasZ && (hasU || hasW);

        byte status = (byte)(0xB0 | (channel & 0x0F));

        // Walk the log list
        while (pos < length)
        {
            // PNUM_LSB byte (bit 7 = S "last item")
            byte pnumLsb = (byte)(data[pos] & 0x7F);
            pos++;

            // PNUM_MSB byte (bit 7 = Q "NRPN")
            byte pnumMsb    = 0;
            bool itemIsNrpn = hasW; // default from chapter-level W hint
            if (!noPnumMsb)
            {
                if (pos >= length) return -1;
                itemIsNrpn = (data[pos] & 0x80) != 0;
                pnumMsb    = (byte)(data[pos] & 0x7F);
                pos++;
            }

            // Flags byte (J=0x80, K=0x40, L=0x20, M=0x10, N=0x08, T=0x04, V=0x02, R=0x01)
            if (pos >= length) return -1;
            byte flags = data[pos++];
            bool flagJ = (flags & 0x80) != 0;
            bool flagK = (flags & 0x40) != 0;
            bool flagL = (flags & 0x20) != 0;
            bool flagM = (flags & 0x10) != 0;
            bool flagN = (flags & 0x08) != 0;

            byte entryMsb = 0;
            if (flagJ)
            {
                if (pos >= length) return -1;
                entryMsb = (byte)(data[pos++] & 0x7F);
            }

            byte entryLsb = 0;
            if (flagK)
            {
                if (pos >= length) return -1;
                entryLsb = (byte)(data[pos++] & 0x7F);
            }

            // Skip optional sections we don't emit.
            if (flagL) pos += 2; // a-button
            if (flagM) pos += 2; // c-button
            if (flagN) pos += 1; // count

            // Reconstruct the original CC sequence:
            //   CC99+CC98 (NRPN) or CC101+CC100 (RPN), then optional CC6 / CC38.
            if (itemIsNrpn)
            {
                recovered.Add([status, 99, pnumMsb]);  // NRPN MSB
                recovered.Add([status, 98, pnumLsb]);  // NRPN LSB
            }
            else
            {
                recovered.Add([status, 101, pnumMsb]); // RPN MSB
                recovered.Add([status, 100, pnumLsb]); // RPN LSB
            }

            if (flagJ) recovered.Add([status,  6, entryMsb]); // Data Entry MSB
            if (flagK) recovered.Add([status, 38, entryLsb]); // Data Entry LSB
        }

        return length;
    }

    public void Reset() { }
}
