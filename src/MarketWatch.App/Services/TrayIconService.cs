using System.Drawing;
using System.Drawing.Drawing2D;
using WinForms = System.Windows.Forms;

namespace MarketWatch.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly Icon _icon;

    public event Action? OpenMainRequested;
    public event Action? ToggleMiniWindowRequested;
    public event Action? TestNormalReminderRequested;
    public event Action? TestStrongReminderRequested;
    public event Action? OpenUpdatesRequested;
    public event Action? OpenSettingsRequested;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        _icon = CreateCrispAppIcon();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开 MarketWatch", null, (_, _) => OpenMainRequested?.Invoke());
        menu.Items.Add("悬浮迷你窗口", null, (_, _) => ToggleMiniWindowRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("静音测试提醒（普通）", null, (_, _) => TestNormalReminderRequested?.Invoke());
        menu.Items.Add("静音测试提醒（强提醒）", null, (_, _) => TestStrongReminderRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("检查更新...", null, (_, _) => OpenUpdatesRequested?.Invoke());
        menu.Items.Add("设置", null, (_, _) => OpenSettingsRequested?.Invoke());
        menu.Items.Add("退出程序", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _icon,
            Text = "MarketWatch 桌面盯盘助手",
            ContextMenuStrip = menu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => OpenMainRequested?.Invoke();
    }

    public void UpdateTooltip(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        // NotifyIcon.Text max length is 63 chars in WinForms
        _notifyIcon.Text = text.Length <= 63 ? text : text[..63];
    }

    private static Icon CreateCrispAppIcon()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/MarketWatch;component/Assets/vegetable.ico"));
            if (res?.Stream is not null)
            {
                using (res.Stream)
                {
                    return new Icon(res.Stream, new Size(32, 32));
                }
            }
        }
        catch
        {
            // Fall back to drawn icon if resource stream is unavailable
        }

        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Rounded dark slate badge
        using var bgBrush = new LinearGradientBrush(
            new Rectangle(0, 0, 32, 32),
            Color.FromArgb(30, 64, 175),
            Color.FromArgb(15, 23, 42),
            LinearGradientMode.ForwardDiagonal);
        using var path = new GraphicsPath();
        path.AddArc(1, 1, 10, 10, 180, 90);
        path.AddArc(21, 1, 10, 10, 270, 90);
        path.AddArc(21, 21, 10, 10, 0, 90);
        path.AddArc(1, 21, 10, 10, 90, 90);
        path.CloseFigure();
        g.FillPath(bgBrush, path);

        // Subtle chart sparkline rising
        using var linePen = new Pen(Color.FromArgb(248, 113, 113), 2.6f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        g.DrawLines(linePen,
        [
            new PointF(6, 22),
            new PointF(12, 16),
            new PointF(17, 19),
            new PointF(25, 9)
        ]);

        // Small accent dot at tip
        using var dotBrush = new SolidBrush(Color.FromArgb(254, 202, 202));
        g.FillEllipse(dotBrush, 23, 7, 4.5f, 4.5f);

        var hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }
}
