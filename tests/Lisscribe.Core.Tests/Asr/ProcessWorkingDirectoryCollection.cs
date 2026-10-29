using Xunit;

namespace Lisscribe.Core.Tests.Asr;

/// <summary>
/// 会改进程级当前工作目录的测试类都归入此集合：它不与任何别的集合并行，免得互相踩。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessWorkingDirectoryCollection
{
    public const string Name = "改进程当前目录（进程级全局状态，不可并行）";
}
