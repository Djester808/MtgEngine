using MtgEngine.Cards.Pipeline.Agree;

namespace MtgEngine.Cards.Pipeline.Tests;

/// <summary>
/// The wave-29 join P28 asked for: a verification run is the one caller entitled to change a
/// verification, so its verdict lands even when it demotes — a re-translation of the same text
/// is still kept out. Every definition here is made up and nothing touches the corpus.
/// </summary>
public sealed partial class CardFileWriterTests
{
    private static Verification RunTwo() => new("engine:def", "run-2", 0, 1, DateTimeOffset.UnixEpoch.AddDays(1));

    [Fact]
    public void A_later_verification_run_lowers_a_verified_file()
    {
        var writer = Writer();
        Assert.Equal(CardWrite.Written, writer.Write(FileOf(Clean(), ImplementationStatus.Verified)));

        var failed = FileOf(Clean(), ImplementationStatus.Translated) with { Verification = RunTwo() };
        Assert.Equal(CardWrite.Written, writer.Write(failed));

        var onDisk = writer.Load(OracleId)!;
        Assert.Equal(ImplementationStatus.Translated, onDisk.Status);
        Assert.Equal("run-2", onDisk.Verification!.RunId);
        Assert.Equal("engine:def", onDisk.Verification.EngineHash);
    }

    [Fact]
    public void A_retranslation_carrying_no_verification_is_still_kept_out()
    {
        var writer = Writer();
        Assert.Equal(CardWrite.Written, writer.Write(FileOf(Clean(), ImplementationStatus.Verified)));

        Assert.Equal(CardWrite.KeptVerified, writer.Write(FileOf(Retranslated(), ImplementationStatus.Translated)));
        Assert.Equal("run-1", writer.Load(OracleId)!.Verification!.RunId);
    }

    [Fact]
    public void The_same_run_written_again_is_not_a_later_verdict()
    {
        var writer = Writer();
        Assert.Equal(CardWrite.Written, writer.Write(FileOf(Clean(), ImplementationStatus.Verified)));

        var sameRun = FileOf(Clean(), ImplementationStatus.Translated) with { Verification = new Verification("engine:abc", "run-1", 0, 1, DateTimeOffset.UnixEpoch) };
        Assert.Equal(CardWrite.KeptVerified, writer.Write(sameRun));
        Assert.Equal(ImplementationStatus.Verified, writer.Load(OracleId)!.Status);
    }
}
