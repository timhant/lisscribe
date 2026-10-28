namespace Lisscribe.App.Tests;

/// <summary>
/// 文件夹选择器的假件：只决定输入（选中谁、取消、抛异常），并记下收到的起始目录。
/// Avalonia 的 IStorageProvider 带内部成员，用户代码无法实现，故 ViewModel 收的是一个委托。
/// </summary>
internal sealed class FakePicker
{
    /// <summary>要返回的本地路径；<c>null</c> 表示用户取消。</summary>
    public string? PickedPath { get; init; }

    /// <summary>非空则选择时抛它。</summary>
    public Exception? Throws { get; init; }

    public bool WasOpened { get; private set; }

    /// <summary>收到的起始目录；<c>null</c> 表示没有给起始位置。</summary>
    public string? ReceivedStartDir { get; private set; }

    public Task<string?> PickAsync(string? startDir)
    {
        WasOpened = true;
        ReceivedStartDir = startDir;

        if (Throws is not null)
        {
            throw Throws;
        }

        return Task.FromResult(PickedPath);
    }
}