using System.IO;

namespace OAIRouter;

/// <summary>
/// ICO 文件的小工具。Windows 的图标是「一个文件多张图」，
/// System.Drawing.Icon 默认只取其中一张，需要按需挑帧。
/// </summary>
public static class IconUtil
{
    /// <summary>返回 ico 里最大一帧的边长；解析失败返回 64。</summary>
    public static int LargestSize(Stream stream)
    {
        if (stream is null || !stream.CanSeek) return 64;

        var origin = stream.Position;
        try
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            stream.Position = origin;

            if (reader.ReadUInt16() != 0) return 64;   // reserved
            if (reader.ReadUInt16() != 1) return 64;   // type: 1 = icon

            int count = reader.ReadUInt16();
            var best = 0;

            for (var i = 0; i < count; i++)
            {
                int width = reader.ReadByte();
                reader.ReadByte();                      // height，与 width 相同
                reader.ReadByte();                      // colorCount
                reader.ReadByte();                      // reserved
                reader.ReadUInt16();                    // planes
                reader.ReadUInt16();                    // bitCount
                reader.ReadUInt32();                    // bytesInRes
                reader.ReadUInt32();                    // imageOffset

                if (width == 0) width = 256;            // 0 表示 256
                if (width > best) best = width;
            }

            return best > 0 ? best : 64;
        }
        catch
        {
            return 64;
        }
        finally
        {
            stream.Position = origin;
        }
    }
}
