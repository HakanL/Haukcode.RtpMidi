using Haukcode.RtpMidi;

namespace RtpMidi.Tests;

public class ClockSyncTests
{
    private static ClockSync MakeSync(ulong fixedTimestamp)
    {
        var now = DateTime.UtcNow;
        return new ClockSync(now, getTimestamp: () => fixedTimestamp);
    }

    [Fact]
    public void Ck0_Sets_Timestamp1()
    {
        var sync = MakeSync(fixedTimestamp: 50_000);
        var ck0  = sync.BuildCk0(ssrc: 1);

        Assert.Equal(0, ck0.Count);
        Assert.Equal(50_000ul, ck0.Timestamp1);
        Assert.Equal(0ul, ck0.Timestamp2);
        Assert.Equal(0ul, ck0.Timestamp3);
    }

    [Fact]
    public void Responder_Ck1_EchoesTimestamp1()
    {
        var sync = MakeSync(fixedTimestamp: 60_000);
        var ck0  = new ClockSyncPacket(Ssrc: 2, Count: 0, Timestamp1: 55_000, Timestamp2: 0, Timestamp3: 0);
        var ck1  = sync.HandleCk0AndBuildCk1(ssrc: 1, ck0);

        Assert.Equal(1, ck1.Count);
        Assert.Equal(55_000ul, ck1.Timestamp1);  // echoed
        Assert.Equal(60_000ul, ck1.Timestamp2);  // our current time
    }

    [Fact]
    public void Initiator_Ck2_ComputesLatencyAndOffset()
    {
        // Simulate: ts1=100, ts2=150 (remote time), ts3=200
        // Latency = (ts3 - ts1) / 2 = 50
        // Offset  = ts2 - (ts1 + latency) = 150 - 150 = 0
        var sync = MakeSync(fixedTimestamp: 200);
        var ck1  = new ClockSyncPacket(Ssrc: 2, Count: 1, Timestamp1: 100, Timestamp2: 150, Timestamp3: 0);
        var ck2  = sync.HandleCk1AndBuildCk2(ssrc: 1, ck1);

        Assert.Equal(2, ck2.Count);
        Assert.Equal(50ul,  sync.LatencyTicks);
        Assert.Equal(0L,    sync.OffsetTicks);
    }

    [Fact]
    public void Initiator_Ck2_NonZeroOffset()
    {
        // ts1=100, ts2=200 (remote clock 50 ticks ahead), ts3=200
        // Latency = (200 - 100) / 2 = 50
        // Offset  = 200 - (100 + 50) = 50
        var sync = MakeSync(fixedTimestamp: 200);
        var ck1  = new ClockSyncPacket(Ssrc: 2, Count: 1, Timestamp1: 100, Timestamp2: 200, Timestamp3: 0);
        sync.HandleCk1AndBuildCk2(ssrc: 1, ck1);

        Assert.Equal(50ul, sync.LatencyTicks);
        Assert.Equal(50L,  sync.OffsetTicks);
    }

    [Fact]
    public void SessionTimestamp_IncreasesOverTime()
    {
        var start = DateTime.UtcNow;
        System.Threading.Thread.Sleep(20);
        var ts = ClockSync.SessionTimestamp(start);
        Assert.True(ts > 0, "Session timestamp should be positive after sleep");
    }
}
