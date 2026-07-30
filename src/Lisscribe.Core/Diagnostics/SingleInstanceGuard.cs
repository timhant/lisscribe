namespace Lisscribe.Core.Diagnostics;

/// <summary>
/// 进程级单实例互斥。
/// 必要性：explorer.exe 重启后托盘图标会消失（Avalonia 不处理 TaskbarCreated 广播），用户以为
/// 程序退了就再启一个——于是两个进程抢同一个热键、同一份 config.json，两份低层键盘钩子都会收到
/// 同一次按键：一次说话录两遍、上屏两遍。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>
    /// 互斥体只在同一登录会话内有效。远程桌面 / 快速用户切换下多个用户各跑各的一份，互不干扰；
    /// 换成 Global\ 就成了「甲用户开着程序，乙用户起不来」。
    /// </summary>
    private const string SessionPrefix = "Local\\";

    private Mutex? _mutex;

    private SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// 尝试成为本登录会话内唯一的实例。取到返回 true 且 <paramref name="guard"/> 非空，
    /// 调用方须持有它直到进程退出；没取到返回 false 且 <paramref name="guard"/> 为 null。
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> 为空、只含空白、含反斜杠，或长到无法用作内核对象名。
    /// 这些都是程序员错误（调用方传的是编译期常量），故不做返回值处理。
    /// </exception>
    /// <exception cref="WaitHandleCannotBeOpenedException">
    /// 这个名字已被系统中另一个非互斥内核对象占用。这不等于「已有实例在跑」，处置方式也不同，
    /// 故不在此吞掉——由 <see cref="StartupPreflight"/> 接住。
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// 这个名字已被占用，且那个对象的 DACL 拒绝本进程访问（最常见的成因是另一份以管理员身份
    /// 跑着）。同样不在此吞掉。
    /// </exception>
    public static bool TryAcquire(string name, out SingleInstanceGuard? guard)
    {
        // 空名字会退化成匿名互斥体：次次都能取到，守卫形同虚设却毫无征兆，等到用户开出第二个
        // 实例才发作。宁可当场炸给开发者看。
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        // 反斜杠会把拼好的名字变成内核对象命名空间路径：Local\a\b 里的 a 被当成对象目录去查，
        // 抛的是一句谈论「路径」的 DirectoryNotFoundException。前缀是本方法偷偷拼上去的，
        // 调用方对着那句话根本无从下手，所以这笔账得在这里认下来。
        if (name.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"实例名不能含反斜杠（本方法会拼成 \"{SessionPrefix}\" + 名字作为内核对象名，" +
                $"反斜杠会被当成对象命名空间分隔符）。收到的是：{name}",
                nameof(name));
        }

        var mutex = CreateMutex(name);

        bool acquired;

        try
        {
            acquired = mutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            // 上一个持有者没释放就没了（崩溃 / 被结束进程 / 断电）。这不是「已有实例在跑」，而是
            // 「互斥体现在归我们了」——判反的话，崩一次之后程序永远起不来，用户唯一的出路是
            // 重启电脑，而他不会想到这一层。
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();

            guard = null;

            return false;
        }

        guard = new SingleInstanceGuard(mutex);

        return true;
    }

    /// <summary>
    /// 只负责把「名字本身不合法」的失败翻成 <see cref="ArgumentException"/>。
    /// </summary>
    /// <remarks>
    /// 长度：流传的 260 字符上限是 .NET Framework 的，.NET 10 上不存在——实测名字至多 32731 字符，
    /// 再多一个才抛 PathTooLongException。故此处不写死任何长度常量：写死必然是错的（真上限还随
    /// 会话号位数浮动），而且会把一批合法名字拒之门外。
    /// 不原样冒泡的理由是那条消息本身：它把整个名字抄了进去，40000 字符的名字换来 40080 字符的
    /// 异常消息，落进日志就是一整屏 'a'；而且它管这叫「path」，可这里根本没有路径。细节不丢，
    /// 挂在 InnerException 上。
    /// <para>
    /// 只接 <see cref="PathTooLongException"/> 这一个具体类型，不接 IOException 全家：兜底分支会把
    /// 一切未特判的错误码翻成裸 IOException（句柄耗尽等），那类失败与「名字不合法」毫无关系，
    /// 误报成 ArgumentException 之后还会撞上 <see cref="StartupPreflight"/>「ArgumentException 不接」
    /// 的既定契约，于是又变回一次静默崩溃。
    /// 同理，<see cref="WaitHandleCannotBeOpenedException"/> 与
    /// <see cref="UnauthorizedAccessException"/> 也不在此列，都由 <see cref="StartupPreflight"/>
    /// 接住并翻成人话。
    /// </para>
    /// </remarks>
    private static Mutex CreateMutex(string name)
    {
        try
        {
            return new Mutex(initiallyOwned: false, $"{SessionPrefix}{name}");
        }
        catch (PathTooLongException ex)
        {
            throw new ArgumentException(
                $"实例名无法用作内核对象名（长度 {name.Length}，本方法还会在前面拼上 " +
                $"\"{SessionPrefix}\"）。详细原因见 InnerException。",
                nameof(name),
                ex);
        }
    }

    public void Dispose()
    {
        // 关机路径可能与启动路径不在同一线程，也可能被重复调用：先原子摘走再动手，保证只释放
        // 自己确实持有过的那一次。
        var mutex = Interlocked.Exchange(ref _mutex, null);

        if (mutex is null)
        {
            return;
        }

        try
        {
            mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // ReleaseMutex 只允许持有线程调用，别的线程会吃到 ApplicationException。组合根多半在
            // 关机路径上释放，而那未必是启动时取锁的线程——让它冒出去就等于「每次退出崩一下」。
            // 此处只能放弃显式释放：句柄随即关闭，进程一退操作系统就把互斥体标记为 abandoned，
            // 下次启动走上面那个 catch 照常放行。
        }
        finally
        {
            mutex.Dispose();
        }
    }
}
