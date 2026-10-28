namespace Lisscribe.Core.Transcription;

/// <summary>
/// 一次转写的结局：成功带实际落地的完整路径，失败带一句给用户看的话。取消不在此列，以异常表达。
/// </summary>
public sealed record TranscriptionOutcome
{
    private TranscriptionOutcome(string? outputPath, string? failureMessage)
    {
        OutputPath = outputPath;
        FailureMessage = failureMessage;
    }

    /// <summary>成功时为 txt 实际落地的完整路径，失败时为 null。</summary>
    public string? OutputPath { get; }

    /// <summary>失败时为一句可以直接示人的话，成功时为 null。</summary>
    public string? FailureMessage { get; }

    public bool IsSuccess => FailureMessage is null;

    public static TranscriptionOutcome Success(string outputPath) => new(outputPath, null);

    public static TranscriptionOutcome Failure(string message) => new(null, message);
}
