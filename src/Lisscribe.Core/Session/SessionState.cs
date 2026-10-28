namespace Lisscribe.Core.Session;

public enum SessionState
{
    Idle,
    Recording,
    Recognizing,
    Failed,
    Paused,

    /// <summary>音频文件转写在跑，热键不开录。加在末尾，不改动已有成员的值。</summary>
    Transcribing,
}

public sealed class SessionStatusEventArgs(SessionState state, string? message) : EventArgs
{
    public SessionState State { get; } = state;
    public string? Message { get; } = message;
}
