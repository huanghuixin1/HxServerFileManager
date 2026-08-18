param(
  [Parameter(Mandatory = $true)][string]$Png,
  [Parameter(Mandatory = $true)][string]$Out
)
# 用 System.Drawing 把 PNG 缩放到 icns 所需的各尺寸并打包成 .icns（macOS 应用图标）。
# 用法：powershell -NoProfile -ExecutionPolicy Bypass -File make-icns.ps1 logo.png out.icns
# 背景：build.sh 在 Windows 上组包 .app 时没有 macOS 的 iconutil，只能手工构造 icns 二进制。
# 注意：PowerShell 5.1 里函数返回/参数传递 byte[] 会被逐字节经管道展开，几十万字节会撑爆时间，
# 因此图标生成整段用 C# 内联（Add-Type）完成，只把结果路径交给 PowerShell。

$cs = @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

public static class IcnsBuilder {
    public static void Build(string pngPath, string outPath) {
        var src = Image.FromFile(pngPath);
        // icns chunk: OSType(4B) + length(4B big-endian, incl 8B header) + PNG data
        var sizes = new[] {
            new { Id = "icp4", Size = 16 },
            new { Id = "icp5", Size = 32 },
            new { Id = "icp6", Size = 64 },
            new { Id = "ic07", Size = 128 },
            new { Id = "ic08", Size = 256 },
            new { Id = "ic09", Size = 512 },
            new { Id = "ic10", Size = 1024 }
        };

        using (var fs = File.Create(outPath)) {
            fs.Write(Encoding.ASCII.GetBytes("icns"), 0, 4);
            fs.Write(new byte[4], 0, 4);

            foreach (var t in sizes) {
                byte[] pngBytes;
                using (var bmp = new Bitmap(t.Size, t.Size)) {
                    using (var g = Graphics.FromImage(bmp)) {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.Clear(Color.Transparent);
                        g.DrawImage(src, 0, 0, t.Size, t.Size);
                    }
                    using (var ms = new MemoryStream()) {
                        bmp.Save(ms, ImageFormat.Png);
                        pngBytes = ms.ToArray();
                    }
                }
                fs.Write(Encoding.ASCII.GetBytes(t.Id), 0, 4);
                var len = BitConverter.GetBytes(pngBytes.Length + 8);
                Array.Reverse(len);
                fs.Write(len, 0, 4);
                fs.Write(pngBytes, 0, pngBytes.Length);
            }

            // patch total length (big-endian)
            var total = (int)fs.Length;
            var totalBytes = BitConverter.GetBytes(total);
            Array.Reverse(totalBytes);
            fs.Seek(4, SeekOrigin.Begin);
            fs.Write(totalBytes, 0, 4);
        }
        src.Dispose();
        Console.WriteLine("OK: " + outPath);
    }
}
'@

Add-Type -TypeDefinition $cs -ReferencedAssemblies @('System.Drawing') -ErrorAction Stop
[IcnsBuilder]::Build($Png, $Out)