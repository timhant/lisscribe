using System.Text;
using Lisscribe.Core.Tests.Fakes;
using Lisscribe.Core.Transcription;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lisscribe.Core.Tests.Transcription;

/// <summary>
/// 启动时清理上次残留的 .part。文件名与目录全部手写，不经被测的正则与 TranscriptFileNamer 生成；时间取固定截止时刻前后，不看真时钟。
/// </summary>
public sealed class LeftoverPartCleanerTests : IDisposable
{
    private static readonly DateTimeOffset Cutoff = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTime BeforeCutoff = new(2026, 10, 29, 9, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime AfterCutoff = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>阳性对照：一个该删的残留。「保留」类测试都放一个，清理根本没跑时它们也会红。</summary>
    private const string ControlName = "对照-20261029080000.txt.part";

    private readonly string _base = Directory.CreateTempSubdirectory("lisscribe-leftover").FullName;

    private readonly RecordingLogger<LeftoverPartCleaner> _logger = new();

    public void Dispose() => Directory.Delete(_base, recursive: true);

    /// <summary>默认目录，手写而不经 ResolveDirectory。</summary>
    private string DefaultDir => Path.Combine(_base, "transcripts");

    private int Clean(string? configured = null) =>
        new LeftoverPartCleaner(_logger).Clean(configured, _base, Cutoff);

    /// <summary>建一个文件并把最后写入时间设成 lastWriteUtc，目录不在就先建。</summary>
    private static string Make(string directory, string name, DateTime lastWriteUtc, string content = "[00:00:00] 半截")
    {
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, name);
        File.WriteAllText(path, content, Encoding.UTF8);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);

        return path;
    }

    // ───────────── 删与留 ─────────────

    [Fact]
    public void AMatchingPartFromBeforeTheCutoff_IsDeleted()
    {
        var leftover = Make(DefaultDir, "会议-20261029120000.txt.part", BeforeCutoff);

        var deleted = Clean();

        Assert.False(File.Exists(leftover));
        Assert.Equal(1, deleted);
    }

    [Theory]
    [InlineData("会议-20261029120000-1.txt.part")]
    [InlineData("会议-20261029120000-12.txt.part")]
    [InlineData("会议-20261029120000.TXT.PART")]
    [InlineData("a.b-20261029120000.txt.part")]
    [InlineData("-20261029120000.txt.part")]     // 源文件名只有扩展名时主名为空
    public void MatchingVariants_AreDeleted(string name)
    {
        var leftover = Make(DefaultDir, name, BeforeCutoff);

        Clean();

        Assert.False(File.Exists(leftover));
    }

    [Theory]
    [InlineData(0)]     // 恰在截止时刻：不算「之前」
    [InlineData(1)]
    public void AMatchingPartWrittenAtOrAfterTheCutoff_IsKept(int daysAfterCutoff)
    {
        var fresh = Make(DefaultDir, "会议-20261030090000.txt.part", Cutoff.UtcDateTime.AddDays(daysAfterCutoff));
        var control = Make(DefaultDir, ControlName, BeforeCutoff);

        Clean();

        Assert.True(File.Exists(fresh));
        Assert.False(File.Exists(control));
    }

    [Theory]
    [InlineData("会议.txt.part")]
    [InlineData("会议-2026.txt.part")]
    [InlineData("会议-20261029120000.txt")]
    [InlineData("其它程序的.part")]
    [InlineData("会议-2026102912000.txt.part")]       // 13 位
    [InlineData("会议-202610291200000.txt.part")]     // 15 位
    [InlineData("会议-２０２６１０２９１２００００.txt.part")]   // 全角数字
    [InlineData("会议20261029120000.txt.part")]       // 少了连字符
    [InlineData("会议-20261029120000-.txt.part")]     // 序号为空
    [InlineData("会议-20261029120000-a.txt.part")]
    [InlineData("会议-20261029120000.md.part")]
    [InlineData("会议-20261029120000.txt.part.bak")]
    [InlineData("会议-20261029120000.txt.partial")]
    public void NamesThatAreNotLeftoverParts_AreKept(string name)
    {
        var other = Make(DefaultDir, name, BeforeCutoff);
        var control = Make(DefaultDir, ControlName, BeforeCutoff);

        Clean();

        Assert.True(File.Exists(other));
        Assert.False(File.Exists(control));
    }

    [Fact]
    public void MatchesInSubdirectories_AreKept()
    {
        var nested = Make(Path.Combine(DefaultDir, "子目录"), "会议-20261029120000.txt.part", BeforeCutoff);
        var control = Make(DefaultDir, ControlName, BeforeCutoff);

        Clean();

        Assert.True(File.Exists(nested));
        Assert.False(File.Exists(control));
    }

    [Fact]
    public void ADirectoryNamedLikeALeftover_IsKept()
    {
        var directory = Path.Combine(DefaultDir, "会议-20261029120000.txt.part");
        Directory.CreateDirectory(directory);
        Directory.SetLastWriteTimeUtc(directory, BeforeCutoff);
        var control = Make(DefaultDir, ControlName, BeforeCutoff);

        Clean();

        Assert.True(Directory.Exists(directory));
        Assert.False(File.Exists(control));
    }

    [Fact]
    public void MissingDirectories_AreSkipped_AndNotCreated()
    {
        var configured = Path.Combine(_base, "别处");

        var deleted = Clean(configured);

        Assert.Equal(0, deleted);
        Assert.False(Directory.Exists(DefaultDir));
        Assert.False(Directory.Exists(configured));
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Information);
    }

    [Fact]
    public void TheConfiguredDirectory_IsCleanedToo_RelativeToTheBase()
    {
        var inDefault = Make(DefaultDir, "会议-20261029120000.txt.part", BeforeCutoff);
        var inConfigured = Make(Path.Combine(_base, "设定"), "访谈-20261029120000.txt.part", BeforeCutoff);

        var deleted = Clean("设定");

        Assert.False(File.Exists(inDefault));
        Assert.False(File.Exists(inConfigured));
        Assert.Equal(2, deleted);
    }

    // ───────────── 扫哪几个目录 ─────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("transcripts")]
    [InlineData(@"transcripts\")]
    [InlineData(@".\TRANSCRIPTS")]
    public void DirectoriesToScan_WhenTheConfiguredOneIsTheDefault_HasOnlyTheDefault(string? configured)
    {
        Assert.Equal([DefaultDir], LeftoverPartCleaner.DirectoriesToScan(configured, _base));
    }

    [Fact]
    public void DirectoriesToScan_TheDefaultUnderAnotherCase_IsTheSameDirectory()
    {
        Assert.Equal([DefaultDir], LeftoverPartCleaner.DirectoriesToScan(DefaultDir.ToUpperInvariant(), _base));
    }

    [Fact]
    public void DirectoriesToScan_WhenTheConfiguredOneDiffers_HasBoth_DefaultFirst()
    {
        var configured = Path.Combine(_base, "设定");

        Assert.Equal([DefaultDir, configured], LeftoverPartCleaner.DirectoriesToScan(configured, _base));
    }

    [Fact]
    public void DirectoriesToScan_WhenTheConfiguredOneIsNotAValidPath_HasOnlyTheDefault()
    {
        Assert.Equal([DefaultDir], LeftoverPartCleaner.DirectoriesToScan("设定\0目录", _base));
    }

    [Fact]
    public void TheSameDirectoryUnderAnotherCase_IsScannedOnce()
    {
        // 扫两遍的话，第二遍对同一个删不掉的文件再记一条 Warning。
        var locked = Make(DefaultDir, "会议-20261029120000.txt.part", BeforeCutoff);
        using var holder = new FileStream(locked, FileMode.Open, FileAccess.Write, FileShare.Read);

        Clean(DefaultDir.ToUpperInvariant());

        Assert.Equal(1, _logger.CountOf(LogLevel.Warning));
    }

    // ───────────── 删不掉 ─────────────

    [Fact]
    public void ALeftoverStillBeingWritten_IsSkippedWithAWarning_AndTheOthersAreStillDeleted()
    {
        // 被占的文件两侧各放一个：NTFS 按名字顺序列出，去掉逐文件的接住后，排在它后面的那个就删不到。
        var first = Make(DefaultDir, "a-20261029120000.txt.part", BeforeCutoff);
        var locked = Make(DefaultDir, "m-20261029120000.txt.part", BeforeCutoff);
        var last = Make(DefaultDir, "z-20261029120000.txt.part", BeforeCutoff);

        // 与 TranscriptWriter 开 .part 的方式相同：只写、共享读，不许别人删。
        using var holder = new FileStream(locked, FileMode.Open, FileAccess.Write, FileShare.Read);

        var deleted = Clean();

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(last));

        var warning = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.IsAssignableFrom<IOException>(warning.Error);
        Assert.Contains(locked, warning.Message, StringComparison.Ordinal);
    }

    // ───────────── 日志 ─────────────

    [Fact]
    public void EachDeletion_LogsItsFullPath_ThenOneSummary_AndNeverTheContent()
    {
        // 「鸮鹬」不在任何日志模板与本文件别处：日志里一旦出现，只能是把文件内容写了进去。
        var one = Make(DefaultDir, "会议-20261029120000.txt.part", BeforeCutoff, "[00:00:00] 鸮鹬残稿");
        var two = Make(DefaultDir, "访谈-20261029120000-1.txt.part", BeforeCutoff, "[00:00:00] 鸮鹬残稿");

        Clean();

        var information = _logger.Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message).ToList();

        Assert.Equal(3, information.Count);
        Assert.Single(information, m => m.Contains(one, StringComparison.Ordinal));
        Assert.Single(information, m => m.Contains(two, StringComparison.Ordinal));

        // 汇总那条不含任一文件的完整路径，故按此认出它，再看目录与个数。
        var summary = Assert.Single(
            information,
            m => !m.Contains(one, StringComparison.Ordinal) && !m.Contains(two, StringComparison.Ordinal));
        Assert.Contains(DefaultDir, summary, StringComparison.Ordinal);
        Assert.Contains("2 个", summary, StringComparison.Ordinal);

        Assert.DoesNotContain(_logger.Entries, e => e.Message.Contains("鸮鹬", StringComparison.Ordinal));
    }

    [Fact]
    public void NothingToClean_LogsNothingAboveDebug()
    {
        var result = Make(DefaultDir, "会议-20261029120000.txt", BeforeCutoff);
        var fresh = Make(DefaultDir, "会议-20261030090000.txt.part", AfterCutoff);

        var deleted = Clean();

        Assert.Equal(0, deleted);
        Assert.True(File.Exists(result));
        Assert.True(File.Exists(fresh));
        Assert.DoesNotContain(_logger.Entries, e => e.Level >= LogLevel.Information);
    }

    // ───────────── 后台 ─────────────

    [Fact]
    public async Task CleanInBackground_ReturnsBeforeTheWorkIsDone()
    {
        var leftover = Make(DefaultDir, "会议-20261029120000.txt.part", BeforeCutoff);
        using var gate = new ManualResetEventSlim();
        var logger = new HookedLogger(level =>
        {
            if (level == LogLevel.Information)
            {
                gate.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
        });

        var task = new LeftoverPartCleaner(logger).CleanInBackground(null, _base, Cutoff);

        // 清理卡在删后那条日志上，此刻不可能已完成。若在调用线程上同步做，上一行要等满 10 秒才返回，返回时已完成。
        Assert.False(task.IsCompleted);

        gate.Set();
        await task;

        Assert.False(File.Exists(leftover));
    }

    [Fact]
    public async Task CleanInBackground_AnUnexpectedError_IsLogged_AndTheTaskStillSucceeds()
    {
        Make(DefaultDir, "会议-20261029120000.txt.part", BeforeCutoff);
        var unexpected = new InvalidOperationException("日志写不进");
        var logger = new HookedLogger(level =>
        {
            if (level == LogLevel.Information)
            {
                throw unexpected;
            }
        });

        await new LeftoverPartCleaner(logger).CleanInBackground(null, _base, Cutoff);

        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Same(unexpected, error.Error);
    }

    /// <summary>每写一条日志先调一次 onLog 再记下，供「卡住」「抛出」两种情形用。</summary>
    private sealed class HookedLogger(Action<LogLevel> onLog) : ILogger<LeftoverPartCleaner>
    {
        private readonly RecordingLogger<LeftoverPartCleaner> _inner = new();

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Error)> Entries => _inner.Entries;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            onLog(logLevel);
            _inner.Log(logLevel, eventId, state, exception, formatter);
        }
    }
}
