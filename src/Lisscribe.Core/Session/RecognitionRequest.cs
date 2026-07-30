namespace Lisscribe.Core.Session;

/// <summary>
/// 串行队列中的一项。TargetWindow 与 CapturedAt 承重：上屏前须校验目标窗口未变、结果未过期，
/// 否则文本会落进用户在识别期间切过去的那个窗口（浏览器地址栏、聊天框…）。
/// </summary>
public sealed record RecognitionRequest(
    float[] Samples, nint TargetWindow, DateTimeOffset CapturedAt);
