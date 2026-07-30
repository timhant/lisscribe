namespace Lisscribe.Core.Abstractions;

public interface IAsrEngine : IAsyncDisposable
{
    bool IsLoaded { get; }

    /// <summary>供 UI 显示「当前在跑哪个模型」。</summary>
    string EngineName { get; }

    /// <summary>下一次 <see cref="LoadAsync"/> 会去找模型的目录。组合根拿它跟新配置比对。</summary>
    string ModelDir { get; }

    /// <summary>
    /// 换模型目录，**只对尚未加载的引擎有效**，已加载的实现须抛
    /// <see cref="InvalidOperationException"/>。
    ///
    /// <para>
    /// 它撑的是唯一一条自救路径：引擎因模型缺失加载失败 → 用户在配置窗把目录改对 → 保存 →
    /// 重新加载。已加载还允许换址只能换出「配置说的是 A、跑着的是 B」这种查无可查的状态。
    /// </para>
    /// </summary>
    void RepointModelDir(string modelDir);

    Task LoadAsync(CancellationToken cancellationToken);

    Task<string> RecognizeAsync(ReadOnlyMemory<float> samples, CancellationToken cancellationToken);
}
