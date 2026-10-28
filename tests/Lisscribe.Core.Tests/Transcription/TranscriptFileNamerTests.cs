using System.Globalization;
using Xunit;
using Lisscribe.Core.Transcription;

namespace Lisscribe.Core.Tests.Transcription;

public sealed class TranscriptFileNamerTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "lisscribe-namer-base");

    private static readonly DateTime Started = new(2026, 10, 28, 21, 30, 45);

    [Fact]
    public void DefaultDirectoryName_IsTranscripts()
    {
        Assert.Equal("transcripts", TranscriptFileNamer.DefaultDirectoryName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveDirectory_BlankFallsBackToDefaultUnderBase(string? configured)
    {
        Assert.Equal(Path.Combine(Base, "transcripts"), TranscriptFileNamer.ResolveDirectory(configured, Base));
    }

    [Fact]
    public void ResolveDirectory_RelativePathIsExpandedAgainstBase_NotAgainstCwd()
    {
        var original = Directory.GetCurrentDirectory();
        var elsewhere = Path.GetTempPath();

        try
        {
            Directory.SetCurrentDirectory(elsewhere);

            var resolved = TranscriptFileNamer.ResolveDirectory(@"out\会议", Base);

            Assert.Equal(Path.Combine(Base, "out", "会议"), resolved);
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
        }
    }

    [Fact]
    public void ResolveDirectory_AbsolutePathIsKept()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "别处 目录");

        Assert.Equal(absolute, TranscriptFileNamer.ResolveDirectory(absolute, Base));
    }

    [Fact]
    public void ChoosePath_UsesSourceNameAndInvariantTimestamp()
    {
        var path = TranscriptFileNamer.ChoosePath(Base, @"D:\录音\会议.m4a", Started, _ => false);

        Assert.Equal(Path.Combine(Base, "会议-20261028213045.txt"), path);
    }

    [Fact]
    public void ChoosePath_IgnoresCurrentCulture()
    {
        var original = CultureInfo.CurrentCulture;

        try
        {
            // 泰语文化用佛历，年份会变成 2569。
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            Assert.Equal("2569", Started.ToString("yyyy"));   // 前提：此文化下年份确实不同

            var path = TranscriptFileNamer.ChoosePath(Base, "a.m4a", Started, _ => false);

            Assert.Equal(Path.Combine(Base, "a-20261028213045.txt"), path);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void ChoosePath_SourceNameWithDots_KeepsAllButTheLastExtension()
    {
        var path = TranscriptFileNamer.ChoosePath(Base, "a.b.m4a", Started, _ => false);

        Assert.Equal(Path.Combine(Base, "a.b-20261028213045.txt"), path);
    }

    [Fact]
    public void ChoosePath_WhenTakenTwice_ReturnsSuffixTwo()
    {
        var taken = new HashSet<string>
        {
            Path.Combine(Base, "x-20261028213045.txt"),
            Path.Combine(Base, "x-20261028213045-1.txt"),
        };

        var path = TranscriptFileNamer.ChoosePath(Base, "x.wav", Started, taken.Contains);

        Assert.Equal(Path.Combine(Base, "x-20261028213045-2.txt"), path);
    }

    [Fact]
    public void ChoosePath_WhenOnlyThePlainNameIsTaken_ReturnsSuffixOne()
    {
        var plain = Path.Combine(Base, "x-20261028213045.txt");

        var path = TranscriptFileNamer.ChoosePath(Base, "x.wav", Started, p => p == plain);

        Assert.Equal(Path.Combine(Base, "x-20261028213045-1.txt"), path);
    }

    [Fact]
    public void ChoosePath_WhenFirstIsFree_HasNoSuffix()
    {
        var path = TranscriptFileNamer.ChoosePath(Base, "x.wav", Started, p => p.EndsWith("-1.txt", StringComparison.Ordinal));

        Assert.Equal(Path.Combine(Base, "x-20261028213045.txt"), path);
    }
}