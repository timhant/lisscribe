using System.Text;

namespace Lisscribe.Core.Transcription;

/// <summary>
/// 转写结果的落盘：逐行写进「正式名.part」，全部完成后改名为正式名，中途放弃则删掉 .part，不留半截文件冒充完整结果。
/// 设计见 docs/plans/2026-10-28-file-transcription-design.md §4.4。
/// </summary>
public sealed class TranscriptWriter : IDisposable
{
    /// <summary>临时文件后缀。</summary>
    public const string PartSuffix = ".part";

    private readonly string _finalPath;

    private readonly string _partPath;

    // Commit 或 Abort 之后置 null，此后不能再写。
    private StreamWriter? _writer;

    private bool _committed;

    private TranscriptWriter(string finalPath, string partPath, StreamWriter writer)
    {
        _finalPath = finalPath;
        _partPath = partPath;
        _writer = writer;
    }

    /// <summary>立即建好「finalPath.part」，建不了就把原异常抛出去，好在开工前就发现目录写不进。</summary>
    public static TranscriptWriter Create(string finalPath)
    {
        var partPath = finalPath + PartSuffix;

        // Create 覆盖上次崩溃残留的同名 .part；共享读，转写途中可以打开 .part 看已转好的部分。
        var stream = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.Read);

        // 带 BOM 让任何 Windows 程序都按 UTF-8 打开，换行固定 CRLF。BOM 在第一次 Flush 时落盘，零行也有。
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
        {
            NewLine = "\r\n",
        };

        return new TranscriptWriter(finalPath, partPath, writer);
    }

    /// <summary>写一行并立即落盘，中途崩溃时 .part 里留有已转好的部分，便于排障。</summary>
    public void AppendLine(string line)
    {
        var writer = _writer ?? throw new InvalidOperationException("写入器已收尾，不能再写。");

        writer.WriteLine(line);
        writer.Flush();
    }

    /// <summary>
    /// 关闭并改名为正式名，返回实际落地的完整路径。正式名已被占时按 <see cref="TranscriptFileNamer"/> 的「-1、-2」规则另选。
    /// </summary>
    public string Commit()
    {
        var writer = _writer ?? throw new InvalidOperationException("写入器已收尾，不能再提交。");

        // 先关文件再改名：Windows 不许移动仍开着的文件。
        writer.Dispose();
        _writer = null;

        var target = TranscriptFileNamer.ChooseFreePath(_finalPath, Path.Exists);

        // 不覆盖：选名与改名之间若被别人抢先占了，宁可报错也不吞掉别人的文件。
        File.Move(_partPath, target, overwrite: false);
        _committed = true;

        return target;
    }

    /// <summary>关闭并删掉 .part。已 Commit 时什么也不做，重复调用无害。</summary>
    public void Abort()
    {
        if (_committed)
        {
            return;
        }

        _writer?.Dispose();
        _writer = null;

        // 文件不在时 File.Delete 不抛，故重复 Abort、Commit 改名失败后再 Abort 都安全。
        File.Delete(_partPath);
    }

    /// <summary>未 Commit 即等同 Abort。</summary>
    public void Dispose() => Abort();
}
