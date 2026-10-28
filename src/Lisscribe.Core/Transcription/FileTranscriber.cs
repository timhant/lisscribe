using System.Diagnostics;
using Lisscribe.Core.Abstractions;
using Lisscribe.Core.PostProcess;
using Microsoft.Extensions.Logging;

namespace Lisscribe.Core.Transcription;

/// <summary>
/// 把一个音频文件转成逐行「[hh:mm:ss] 文字」的 txt：解码、切段、逐段识别、复读重试、写文件、报进度。
/// 设计见 docs/plans/2026-10-28-file-transcription-design.md §4。
/// </summary>
/// <remarks>
/// <para>
/// 线程：解码器的 Open、每一次 Read 与 Dispose 必须在同一条线程上。Media Foundation 真正读文件的 COM reader
/// 建在第一次 Read 所在的线程上，换一条线程释放它没有证据能行。故整个转写是 Task.Run 里的一段同步代码，
/// 识别调用在其中同步等待，不让 await 的续体把之后的 Read 与 Dispose 带到别的线程上。
/// </para>
/// <para>
/// 取消：在两段之间检查。令牌也传给引擎，但引擎只在进门前看它、不打断原生解码
/// （见 OfflineRecognizerEngine.RecognizeAsync），故取消最多等当前这一段识别完。引擎因令牌抛出的取消按取消处理，不算识别失败。
/// </para>
/// </remarks>
public sealed class FileTranscriber
{
    /// <summary>每段样本数上限：20 秒。</summary>
    private const int MaxChunkSamples = TranscriptChunker.MaxChunkSeconds * TranscriptChunker.SampleRate;

    /// <summary>切点搜索起点：10 秒，每段不短于上限的一半。</summary>
    private const int MinChunkSamples = MaxChunkSamples / 2;

    /// <summary>一个样本折合的 TimeSpan 刻度数，16kHz 下恰为 625，换算没有舍入。</summary>
    private const long TicksPerSample = TimeSpan.TicksPerSecond / TranscriptChunker.SampleRate;

    private readonly IAsrEngine _engine;

    private readonly IAudioFileDecoder _decoder;

    private readonly bool _applyChineseNumberItn;

    private readonly ILogger<FileTranscriber> _logger;

    /// <param name="applyChineseNumberItn">是否做中文数字转换，取运行中那份 SessionOptions.ApplyChineseNumberItn。</param>
    public FileTranscriber(
        IAsrEngine engine, IAudioFileDecoder decoder, bool applyChineseNumberItn, ILogger<FileTranscriber> logger)
    {
        _engine = engine;
        _decoder = decoder;
        _applyChineseNumberItn = applyChineseNumberItn;
        _logger = logger;
    }

    /// <summary>
    /// 转写 sourcePath，结果写到 outputPath（调用方已选好的最终路径）。失败返回带原因的结局、不抛；
    /// 取消抛 <see cref="OperationCanceledException"/>。两种情形都不留 .part。
    /// </summary>
    public Task<TranscriptionOutcome> RunAsync(
        string sourcePath, string outputPath, IProgress<int> progress, CancellationToken cancellationToken) =>
        Task.Run(() => Run(sourcePath, outputPath, progress, cancellationToken), CancellationToken.None);

    private TranscriptionOutcome Run(
        string sourcePath, string outputPath, IProgress<int> progress, CancellationToken cancellationToken)
    {
        var fileName = Path.GetFileName(sourcePath);

        IDecodedAudio audio;

        // 先开解码器再建 .part：文件打不开就不建文件。
        try
        {
            audio = _decoder.Open(sourcePath);
        }
        catch (Exception ex)
        {
            // 解码器的异常消息本就是写给用户看的。
            _logger.LogWarning(ex, "打不开音频文件 {File}", fileName);

            return TranscriptionOutcome.Failure(ex.Message);
        }

        // Dispose 与 Open、Read 同在这条线程上，见类注释。
        using (audio)
        {
            TranscriptWriter writer;

            try
            {
                writer = TranscriptWriter.Create(outputPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var directory = Path.GetDirectoryName(outputPath);

                _logger.LogWarning(ex, "转写结果的临时文件建不了，目录 {Directory}", directory);

                return TranscriptionOutcome.Failure($"无法在该目录写入转写结果：{directory}（{ex.Message}）");
            }

            // 未 Commit 就释放即删 .part：失败、取消、意外异常都从这里收尾。
            using (writer)
            {
                try
                {
                    return Transcribe(audio, writer, fileName, progress, cancellationToken);
                }
                catch (TranscriptionFailedException ex)
                {
                    _logger.LogError(ex.InnerException, "转写 {File} 失败：{Reason}", fileName, ex.Message);

                    return TranscriptionOutcome.Failure(ex.Message);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("转写 {File} 已取消", fileName);

                    throw;
                }
            }
        }
    }

    private TranscriptionOutcome Transcribe(
        IDecodedAudio audio, TranscriptWriter writer, string fileName, IProgress<int> progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // 多留一个样本的位置：读满 20 秒后还能再读到一个，才说明剩余超过 20 秒、需要切。
        var buffer = new float[MaxChunkSamples + 1];
        var filled = 0;
        var endOfFile = false;

        // buffer[0] 在原音频里的样本下标，也是已处理的样本数。
        var bufferStart = 0L;

        var segments = 0;
        var characters = 0;
        var lastPercent = -1;

        while (true)
        {
            // 两段之间检查取消。
            cancellationToken.ThrowIfCancellationRequested();

            // 读满缓冲或读到文件尾，解码器一次可能给不满。
            while (!endOfFile && filled < buffer.Length)
            {
                var read = ReadSamples(audio, buffer.AsSpan(filled), fileName);

                if (read == 0)
                {
                    endOfFile = true;
                }
                else
                {
                    filled += read;
                }
            }

            if (filled == 0)
            {
                break;
            }

            // 缓冲没满必是到了文件尾：剩余不超过 20 秒就整段收尾，否则在 [10s, 20s] 里挑最安静处切。
            var length = filled <= MaxChunkSamples
                ? filled
                : TranscriptChunker.FindCut(buffer, MinChunkSamples, MaxChunkSamples);

            var segmentStopwatch = Stopwatch.StartNew();
            var written = TranscribeSegment(writer, buffer.AsMemory(0, length), bufferStart, cancellationToken);

            segments++;
            characters += written;

            _logger.LogDebug(
                "第 {Index} 段：起点 {Start}，时长 {Seconds:F2}s，{Characters} 字，耗时 {Elapsed}ms",
                segments, ToTime(bufferStart), (double)length / TranscriptChunker.SampleRate, written,
                segmentStopwatch.ElapsedMilliseconds);

            // 切点之后的样本挪到缓冲开头，接着读。
            Array.Copy(buffer, length, buffer, 0, filled - length);
            filled -= length;
            bufferStart += length;

            lastPercent = ReportProgress(progress, bufferStart, audio.Duration, lastPercent);
        }

        if (segments == 0)
        {
            throw new TranscriptionFailedException("文件里没有可识别的音频", null);
        }

        var outputPath = writer.Commit();

        // 不论上一次报的是多少，落地之后都补报一次 100。
        progress.Report(100);

        _logger.LogInformation(
            "转写完成：{File}，音频 {Audio}，{Segments} 段，{Characters} 字，耗时 {Elapsed}",
            fileName, ToTime(bufferStart), segments, characters, stopwatch.Elapsed);

        return TranscriptionOutcome.Success(outputPath);
    }

    /// <summary>识别一段并写行，判为复读循环时在中段一分为二各识别一次，只重试一层。返回写下的字数。</summary>
    private int TranscribeSegment(
        TranscriptWriter writer, ReadOnlyMemory<float> segment, long start, CancellationToken cancellationToken)
    {
        var text = Recognize(segment, cancellationToken);

        if (!RepetitionDetector.IsLooping(text))
        {
            return WriteLine(writer, start, text);
        }

        var searchStart = segment.Length / 3;
        var searchEnd = segment.Length * 2 / 3;

        // 太短的段在 [1/3, 2/3] 里放不下一个能量窗口，切不开，只能原样写。
        if (searchEnd - searchStart < TranscriptChunker.WindowSamples)
        {
            _logger.LogWarning(
                "起点 {Start} 的一段判为复读循环，但只有 {Seconds:F2}s，切不开，原样写入",
                ToTime(start), (double)segment.Length / TranscriptChunker.SampleRate);

            return WriteLine(writer, start, text);
        }

        var cut = TranscriptChunker.FindCut(segment.Span, searchStart, searchEnd);
        var first = Recognize(segment[..cut], cancellationToken);
        var second = Recognize(segment[cut..], cancellationToken);

        // 两半拼起来仍循环就照写：宁可留下看得见的垃圾，也不静默删字。
        if (RepetitionDetector.IsLooping(first + second))
        {
            _logger.LogWarning("起点 {Start} 的一段一分为二重识别后仍判为复读循环，原样写入", ToTime(start));
        }

        // 两半各占一行，行首各是自己的起点。
        return WriteLine(writer, start, first) + WriteLine(writer, start + cut, second);
    }

    /// <summary>同步等待识别结果：续体若换了线程，之后的 Read 与 Dispose 就跟着换了线程，见类注释。</summary>
    private string Recognize(ReadOnlyMemory<float> samples, CancellationToken cancellationToken)
    {
        try
        {
            return _engine.RecognizeAsync(samples, cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 引擎看见令牌而退出，这是取消，不是识别失败。
            throw;
        }
        catch (Exception ex)
        {
            throw new TranscriptionFailedException($"识别失败：{ex.Message}", ex);
        }
    }

    /// <summary>中文数字转换后写一行，空白不写。返回写下的字数。</summary>
    private int WriteLine(TranscriptWriter writer, long startSample, string text)
    {
        var converted = ChineseNumberItn.Convert(text, _applyChineseNumberItn);
        var line = TranscriptLine.Format(ToTime(startSample), converted);

        if (line is null)
        {
            return 0;
        }

        writer.AppendLine(line);

        return converted.Trim().Length;
    }

    /// <summary>读一批样本。真实解码器此处抛的多是英文的系统异常，不宜示人，换成一句人话，原异常留作日志。</summary>
    private static int ReadSamples(IDecodedAudio audio, Span<float> buffer, string fileName)
    {
        try
        {
            return audio.Read(buffer);
        }
        catch (Exception ex)
        {
            throw new TranscriptionFailedException($"读取音频时中断：{fileName}", ex);
        }
    }

    /// <summary>按已处理样本占文件头总时长的比例报进度，向下取整、夹在 0–100，变了才报。返回这次算出的百分比。</summary>
    private static int ReportProgress(IProgress<int> progress, long processedSamples, TimeSpan duration, int lastPercent)
    {
        // 文件头没写时长时无从算比例，一直报 0，收尾时统一补 100。实测解出的样本可能比时长长，故夹住。
        var percent = duration <= TimeSpan.Zero
            ? 0
            : (int)Math.Clamp(processedSamples * TicksPerSample * 100 / duration.Ticks, 0, 100);

        if (percent != lastPercent)
        {
            progress.Report(percent);
        }

        return percent;
    }

    /// <summary>样本下标换成时间，整数刻度换算，不经浮点，正好落在整秒上的起点不会被舍到前一秒。</summary>
    private static TimeSpan ToTime(long sampleIndex) => TimeSpan.FromTicks(sampleIndex * TicksPerSample);

    /// <summary>转写途中出的失败，消息已是给用户看的话，由 Run 统一收尾。</summary>
    private sealed class TranscriptionFailedException(string message, Exception? innerException)
        : Exception(message, innerException);
}
