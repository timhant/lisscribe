using System.Text;
using Xunit;
using Lisscribe.Core.Transcription;

namespace Lisscribe.Core.Tests.Transcription;

/// <summary>
/// 转写结果的落盘：先写 .part，Commit 改名，Abort 删除。期望字节全部手写，不经被测代码的编码器。
/// </summary>
public sealed class TranscriptWriterTests : IDisposable
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private readonly string _dir = Directory.CreateTempSubdirectory("lisscribe-writer").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Final => Path.Combine(_dir, "会议-20261028213045.txt");

    private string Part => Final + ".part";

    [Fact]
    public void Create_ImmediatelyCreatesThePartFile_AndNotTheFinalOne()
    {
        using var writer = TranscriptWriter.Create(Final);

        Assert.True(File.Exists(Part));
        Assert.False(File.Exists(Final));
    }

    [Fact]
    public void Commit_LeavesOnlyTheFinalFile_WithBomAndCrlf()
    {
        string committed;

        using (var writer = TranscriptWriter.Create(Final))
        {
            writer.AppendLine("[00:00:00] 你好");
            writer.AppendLine("[00:00:13] 世界");

            committed = writer.Commit();
        }

        byte[] expected = [.. Bom, .. Encoding.UTF8.GetBytes("[00:00:00] 你好\r\n[00:00:13] 世界\r\n")];

        Assert.Equal(Final, committed);
        Assert.Equal([Final], Directory.GetFileSystemEntries(_dir));
        Assert.Equal(expected, File.ReadAllBytes(Final));
    }

    [Fact]
    public void Commit_WithNoLines_WritesJustTheBom()
    {
        // 整条录音都没人说话时就走这条路，结果仍须是一个合法的 UTF-8 文件。
        using (var writer = TranscriptWriter.Create(Final))
        {
            writer.Commit();
        }

        Assert.Equal(Bom, File.ReadAllBytes(Final));
    }

    [Fact]
    public void AppendLine_IsOnDiskBeforeCommit()
    {
        using var writer = TranscriptWriter.Create(Final);

        writer.AppendLine("[00:00:00] 第一行");

        // 写入器开着文件时旁人只能以共享读写的方式打开，这本身也钉住了「途中可以打开 .part 看」。
        using var reader = new FileStream(Part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new MemoryStream();
        reader.CopyTo(buffer);

        byte[] expected = [.. Bom, .. Encoding.UTF8.GetBytes("[00:00:00] 第一行\r\n")];

        Assert.Equal(expected, buffer.ToArray());
    }

    [Fact]
    public void Abort_RemovesThePartFile()
    {
        using var writer = TranscriptWriter.Create(Final);
        writer.AppendLine("[00:00:00] 半截");

        writer.Abort();

        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void Abort_Twice_IsHarmless()
    {
        using var writer = TranscriptWriter.Create(Final);

        writer.Abort();
        writer.Abort();

        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void Dispose_WithoutCommit_RemovesThePartFile()
    {
        using (var writer = TranscriptWriter.Create(Final))
        {
            writer.AppendLine("[00:00:00] 半截");
        }

        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void Dispose_AfterCommit_KeepsTheFinalFile()
    {
        var writer = TranscriptWriter.Create(Final);
        writer.AppendLine("[00:00:00] 完整");
        writer.Commit();

        writer.Dispose();

        Assert.Equal([Final], Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void AppendLine_AfterCommit_Throws()
    {
        using var writer = TranscriptWriter.Create(Final);
        writer.Commit();

        Assert.Throws<InvalidOperationException>(() => writer.AppendLine("[00:00:00] 迟到"));
    }

    [Fact]
    public void Create_InAMissingDirectory_ThrowsTheOriginalException()
    {
        var missing = Path.Combine(_dir, "不存在", "x.txt");

        // 原异常原样抛出，不包一层也不吞。
        Assert.Throws<DirectoryNotFoundException>(() => TranscriptWriter.Create(missing));
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void Commit_WhenTheFinalNameWasTakenMeanwhile_MovesToSuffixOne_AndLeavesTheOtherFileAlone()
    {
        byte[] theirs = [1, 2, 3];
        var suffixOne = Path.Combine(_dir, "会议-20261028213045-1.txt");

        using var writer = TranscriptWriter.Create(Final);
        writer.AppendLine("[00:00:00] 我的");

        // 转写途中别人占了正式名。
        File.WriteAllBytes(Final, theirs);

        var committed = writer.Commit();

        byte[] mine = [.. Bom, .. Encoding.UTF8.GetBytes("[00:00:00] 我的\r\n")];

        Assert.Equal(suffixOne, committed);
        Assert.Equal(theirs, File.ReadAllBytes(Final));
        Assert.Equal(mine, File.ReadAllBytes(suffixOne));
        Assert.False(File.Exists(Part));
    }
}
