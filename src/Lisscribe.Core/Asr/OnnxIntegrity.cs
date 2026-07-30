namespace Lisscribe.Core.Asr;

/// <summary>ONNX 文件的廉价完整性自检，返回 null 表示「看起来没问题」。两档引擎共用。</summary>
/// <remarks>
/// 非做不可：把损坏的 onnx 递给 sherpa-onnx，原生层的 <c>Ort::Exception</c> 不转成托管异常，进程
/// 当场 SIGABRT——无窗口、无日志、无退出信息，用户看到的是「点了没反应」，于是再点一次再死一次。
/// 而权重要用户自己下（229MB / 982MB 两档），下载中断、磁盘写满、拷到一半，产出的都是这种文件。
/// <para>
/// 做法：ONNX 没有魔数，它就是裸 protobuf，而最常见的下载中断截的是尾巴、文件头完好，查头白查。
/// 故遍历顶层字段并要求最后一个字段恰好终止于 EOF——变长字段只读它声明的长度然后 Seek 跳过，绝不
/// 读净荷，代价是二十来次 seek 且与文件多大无关，也不必做哈希（哈希比它守护的那次加载还贵）。
/// graph 是一个巨型变长字段，少 0.1% 的尾巴都会被抓出来；下载成 HTML 错误页则撞上非法 wire type。
/// </para>
/// </remarks>
public static class OnnxIntegrity
{
    /// <param name="path">要检查的文件全路径。</param>
    /// <param name="displayName">写进错误信息里的文件名，用户认得的那个。</param>
    public static string? DescribeDamage(string path, string displayName)
    {
        using var stream = File.OpenRead(path);

        var length = stream.Length;

        if (length == 0)
        {
            return $"{displayName} 是空文件";
        }

        var fieldIndex = 0;

        while (stream.Position < length)
        {
            fieldIndex++;

            if (!TryReadVarint(stream, out var tag))
            {
                return $"{displayName} 第 {fieldIndex} 个顶层字段的 tag 不完整，文件在此截断";
            }

            var wireType = (int)(tag & 0x7);
            var fieldNumber = tag >> 3;

            if (fieldNumber == 0)
            {
                // 字段号 0 不合法。下载到 HTML 错误页时常在此或 wire type 上翻车。
                return $"{displayName} 不是 ONNX（protobuf）格式：出现非法字段号 0";
            }

            switch (wireType)
            {
                case 0: // varint
                    if (!TryReadVarint(stream, out _))
                    {
                        return $"{displayName} 在字段 {fieldNumber} 的 varint 中途截断";
                    }

                    break;

                case 1: // 64-bit
                case 5: // 32-bit
                    var fixedSize = wireType == 1 ? 8 : 4;
                    if (length - stream.Position < fixedSize)
                    {
                        return $"{displayName} 在字段 {fieldNumber} 的定长净荷中途截断";
                    }

                    stream.Seek(fixedSize, SeekOrigin.Current);

                    break;

                case 2: // 变长（ONNX 的 graph、initializer 等大块都走这里）
                    if (!TryReadVarint(stream, out var payloadLength))
                    {
                        return $"{displayName} 在字段 {fieldNumber} 的长度前缀中途截断";
                    }

                    var remaining = (ulong)(length - stream.Position);

                    if (payloadLength > remaining)
                    {
                        return $"{displayName} 被截断：字段 {fieldNumber} 声明 {payloadLength} 字节，" +
                               $"文件里只剩 {remaining} 字节";
                    }

                    stream.Seek((long)payloadLength, SeekOrigin.Current);

                    break;

                default:
                    // 3/4 是已废弃的 group，6/7 从未定义过。真 ONNX 里不会出现。
                    return $"{displayName} 不是 ONNX（protobuf）格式：字段 {fieldNumber} 的 " +
                           $"wire type {wireType} 非法";
            }
        }

        return null;
    }

    /// <summary>读一个 protobuf varint。读到文件尾或长度超过 10 字节即视为损坏。</summary>
    private static bool TryReadVarint(Stream stream, out ulong value)
    {
        value = 0;

        for (var shift = 0; shift < 64; shift += 7)
        {
            var b = stream.ReadByte();

            if (b < 0)
            {
                return false;
            }

            value |= (ulong)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }
}
