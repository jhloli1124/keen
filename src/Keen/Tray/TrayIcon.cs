using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Keen.Tray;

// 托盘图标:刻痕意象(一道斜划 + 短 nick)。idle=深底浅痕,error=红底告警。
// 动态绘制,无需 .ico 资源。
internal static class TrayIcon
{
    private static Icon? _idle;
    private static Icon? _error;

    public static Icon Idle =>
        _idle ??= Make(Color.FromArgb(38, 39, 45), Color.FromArgb(232, 234, 240));
    public static Icon Error =>
        _error ??= Make(Color.FromArgb(150, 28, 28), Color.FromArgb(255, 232, 232));

    private static Icon Make(Color bg, Color fg)
    {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var b = new SolidBrush(bg))
                g.FillRectangle(b, 0, 0, 32, 32);
            // 主刻痕:粗斜划
            using var pen = new Pen(fg, 3.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 7, 25, 25, 7);
            // 短 nick:横向小划
            using var pen2 = new Pen(fg, 3.0f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen2, 8, 14, 19, 14);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
