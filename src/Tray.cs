using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Timer = System.Windows.Forms.Timer;

[assembly: System.Reflection.AssemblyTitle("Battery and Fans")]
[assembly: System.Reflection.AssemblyProduct("Battery and Fans")]
[assembly: System.Reflection.AssemblyFileVersion("1.6.0.0")]
static class Program
{
    [DllImport("user32")] static extern bool SetProcessDPIAware();
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--diagnostics")
        {
            using (var hardware = new Hardware())
            {
                Hardware.Power status;
                bool ok = Hardware.GetSystemPowerStatus(out status);
                var battery = hardware.ReadBattery(ok && (status.Flags & 128) == 0, ok && status.AC == 0, ok && status.AC == 1);
                hardware.Cpu();
                hardware.Gpu();
                Thread.Sleep(1200);
                var memory = Hardware.ReadMemory();
                File.WriteAllLines(args[1], new[] {
                    "Power API=" + ok, "Battery=" + status.Percent, "AC=" + status.AC, "WindowsSeconds=" + status.Seconds,
                    "RAM bytes=" + memory.Total, "CPU=" + hardware.Cpu(), "GPU=" + hardware.Gpu(),
                    "RemainingWh=" + battery.RemainingWh, "FullWh=" + battery.FullWh, "DesignWh=" + battery.DesignWh,
                    "Wear=" + battery.Wear, "Watts=" + battery.Watts, "ChargeWatts=" + battery.ChargeWatts
                });
            }
            return;
        }
        bool created;
        using (var instance = new Mutex(true, @"Local\PinVaHieuNang", out created))
        {
            if (!created) return;
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var app = new TrayMonitor(Array.IndexOf(args, "--show") >= 0)) Application.Run(app);
        }
    }
}

sealed class TrayMonitor : ApplicationContext
{
    const string AppName = "Battery and Fans";
    const string StartupKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string StartupValue = "PinVaHieuNang";
    const int PopupWidth = 250, DefaultHeight = 348, MinimumHeight = 334;
    const string PanelKey = @"Software\PinVaHieuNang";
    static readonly CultureInfo Vietnamese = CultureInfo.GetCultureInfo("vi-VN");
    static readonly Color Background = Color.FromArgb(23, 29, 42);
    static readonly Color Muted = Color.FromArgb(174, 186, 204);
    static readonly Color Accent = Color.FromArgb(108, 231, 189);
    readonly Hardware hardware = new Hardware();
    readonly NotifyIcon tray = new NotifyIcon();
    readonly ContextMenuStrip menu = new ContextMenuStrip();
    readonly Timer timer = new Timer();
    readonly Form popup, widget;
    readonly Label title, batteryPercent, state, compact;
    readonly Label[] metrics = new Label[3];
    readonly Label[] values = new Label[5];
    readonly Label[] captions = new Label[5];
    readonly Label note, grip;
    readonly Button pinButton;
    readonly ToolStripMenuItem pinItem;
    readonly ToolStripMenuItem startupItem, widgetItem;
    Icon icon;
    string iconKey;
    DateTime nextDetail, lastRate;
    Hardware.Battery battery = new Hardware.Battery();
    byte previousAc = 255, previousPercent = 255;
    double? averageWatts;
    bool previousCharging;
    string cpuText = "—", ramText = "—", gpuText = "—";
    bool opening, disposingApp;
    bool pinned;
    Point? savedPosition;
    Point gestureStart, panelStart;
    int startHeight, preferredHeight = DefaultHeight;

    public TrayMonitor(bool show)
    {
        LoadPanelSettings();
        popup = CreatePanel(PopupWidth, DefaultHeight);
        popup.Text = AppName;
        using (var executableIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
            if (executableIcon != null) popup.Icon = (Icon)executableIcon.Clone();
        title = AddLabel(popup, "Pin còn lại", 12, 8, 118, 31, 12, Muted, true);
        batteryPercent = AddLabel(popup, "—", 12, 42, 226, 63, 34, Accent, true);
        title.Cursor = Cursors.SizeAll;
        title.MouseDown += BeginMove;
        title.MouseMove += MovePanel;
        title.MouseUp += delegate { title.Capture = false; };
        title.MouseCaptureChanged += delegate { if (!title.Capture && pinned && popup.Visible) SavePanelSettings(); };
        pinButton = new Button { AccessibleName = "Ghim bảng", Location = new Point(133, 8),
            Size = new Size(78, 32), FlatStyle = FlatStyle.Flat, ForeColor = Accent, TabStop = false };
        pinButton.FlatAppearance.BorderColor = Color.FromArgb(85, 111, 133);
        pinButton.Click += delegate { SetPinned(!pinned); };
        popup.Controls.Add(pinButton);
        var close = new Button { Text = "×", AccessibleName = "Đóng chi tiết", Location = new Point(213, 8),
            Size = new Size(30, 32), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, TabStop = false };
        close.FlatAppearance.BorderSize = 0;
        close.Click += delegate { popup.Hide(); };
        popup.Controls.Add(close);
        state = AddLabel(popup, "", 12, 105, 226, 24, 11, Muted, false);
        string[] names = { "Công suất xả", "Độ chai pin", "Dung lượng", "Thời gian còn", "Dự kiến cạn pin" };
        for (int i = 0; i < names.Length; i++)
        {
            captions[i] = AddLabel(popup, names[i], 12, 129 + i * 30, 114, 27, 11, Muted, false);
            values[i] = AddLabel(popup, "—", 128, 129 + i * 30, 110, 27, 10, Color.White, true);
            values[i].TextAlign = ContentAlignment.MiddleRight;
        }
        for (int i = 0; i < metrics.Length; i++)
        {
            metrics[i] = AddLabel(popup, "", 12 + i * 76, 280, 74, 26, 9.5f, Accent, true);
            metrics[i].TextAlign = i == 0 ? ContentAlignment.MiddleLeft :
                i == 1 ? ContentAlignment.MiddleCenter : ContentAlignment.MiddleRight;
        }
        note = AddLabel(popup, "Máy có thể ngủ hoặc tắt sớm.", 12, 307, 226, 20, 9, Muted, false);
        grip = AddLabel(popup, "━━━━", 0, 329, PopupWidth, 18, 9, Muted, true);
        grip.TextAlign = ContentAlignment.MiddleCenter;
        grip.AccessibleName = "Kéo để chỉnh chiều cao";
        grip.Cursor = Cursors.SizeNS;
        grip.MouseDown += BeginResize;
        grip.MouseMove += ResizePanel;
        grip.MouseUp += delegate { grip.Capture = false; };
        grip.MouseCaptureChanged += delegate { if (!grip.Capture && popup.Visible) SavePanelSettings(); };
        ScalePanel(popup, PopupWidth, DefaultHeight);
        popup.Resize += delegate { LayoutPopup(); };
        popup.Deactivate += delegate { if (!opening && !pinned) popup.Hide(); };

        popup.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) popup.Hide(); };
        popup.KeyPreview = true;

        widget = CreatePanel(PopupWidth, 34);
        widget.Text = "Chỉ số hiệu năng";
        compact = AddLabel(widget, "", 5, 1, 240, 31, 9, Accent, true);
        compact.TextAlign = ContentAlignment.MiddleCenter;
        compact.Click += delegate { TogglePopup(); };
        ScalePanel(widget, PopupWidth, 34);
        widget.Click += delegate { TogglePopup(); };
        popup.VisibleChanged += delegate { if (!popup.Visible && !disposingApp) RefreshSampling(); };

        menu.Items.Add("Mở chi tiết", null, delegate { ShowPopup(); });
        pinItem = new ToolStripMenuItem("Ghim bảng trên màn hình");
        pinItem.Click += delegate { SetPinned(!pinned); if (pinned) ShowPopup(); };
        menu.Items.Add(pinItem);
        UpdatePinControl();
        widgetItem = new ToolStripMenuItem("Hiện chỉ số cạnh thanh tác vụ");
        widgetItem.Click += delegate { SetWidget(!widget.Visible); };
        menu.Items.Add(widgetItem);
        startupItem = new ToolStripMenuItem("Tự chạy khi đăng nhập Windows");
        startupItem.Click += delegate { ToggleStartup(); };
        menu.Items.Add(startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Thoát", null, delegate { ExitThread(); });
        menu.Opening += delegate { startupItem.Checked = StartupEnabled(); };
        tray.ContextMenuStrip = menu;
        tray.MouseClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) TogglePopup(); };
        timer.Tick += delegate { Sample(); };
        timer.Interval = 15000;
        Sample();
        tray.Visible = true;
        timer.Start();
        if (show || pinned)
        {
            EventHandler display = null;
            display = delegate { Application.Idle -= display; ShowPopup(); };
            Application.Idle += display;
        }
    }
    static Form CreatePanel(int width, int height)
    {
        float scale;
        using (var graphics = Graphics.FromHwnd(IntPtr.Zero)) scale = graphics.DpiX / 96f;
        return new Form {
            AutoScaleMode = AutoScaleMode.None,
            BackColor = Background, ClientSize = new Size(width, height), FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false, TopMost = true, StartPosition = FormStartPosition.Manual, Opacity = 0.88,
            Tag = new Size((int)Math.Round(width * scale), (int)Math.Round(height * scale))
        };
    }
    static void ScalePanel(Form form, int width, int height)
    {
        Size size = (Size)form.Tag;
        float x = size.Width / (float)width, y = size.Height / (float)height;
        foreach (Control control in form.Controls)
        {
            Rectangle bounds = control.Bounds;
            control.SetBounds((int)Math.Round(bounds.X * x), (int)Math.Round(bounds.Y * y),
                (int)Math.Round(bounds.Width * x), (int)Math.Round(bounds.Height * y));
            Font font = control.Font;
            control.Font = new Font(font.FontFamily, (font.Unit == GraphicsUnit.Pixel ? font.Size : font.SizeInPoints * 96 / 72) * y, font.Style, GraphicsUnit.Pixel);
        }
        form.ClientSize = size;
    }
    static Label AddLabel(Form parent, string text, int x, int y, int width, int height, float points, Color color, bool bold)
    {
        var label = new Label {
            Text = text, Location = new Point(x, y), Size = new Size(width, height),
            Font = new Font("Segoe UI", points * 96 / 72, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel),
            ForeColor = color, TextAlign = ContentAlignment.MiddleLeft
        };
        parent.Controls.Add(label);
        return label;
    }
    void Position(Form form, Screen screen, int offset)
    {
        Rectangle area = screen.WorkingArea;
        Size size = (Size)form.Tag;
        form.Location = new Point(Math.Max(area.Left, area.Right - size.Width - 10),
            Math.Max(area.Top, area.Bottom - size.Height - 8 - offset));
    }
    void SetWidget(bool visible)
    {
        widgetItem.Checked = visible;
        if (visible)
        {
            Position(widget, Screen.FromPoint(Cursor.Position), 0);
            widget.Show();
        }
        else widget.Hide();
        RefreshSampling();
        Sample();
    }
    void TogglePopup() { if (popup.Visible && !pinned) popup.Hide(); else ShowPopup(); }
    void ShowPopup()
    {
        opening = true;
        try
        {
            if (!popup.Visible)
            {
                Screen screen = pinned && savedPosition.HasValue ? Screen.FromPoint(savedPosition.Value) :
                    widget.Visible ? Screen.FromControl(widget) : Screen.FromPoint(Cursor.Position);
                popup.CreateControl();
                Rectangle area = screen.WorkingArea;
                popup.Height = Math.Min(Scale(preferredHeight), area.Height);
                var defaultPosition = new Point(area.Right - popup.Width - 10,
                    area.Bottom - popup.Height - 8 - (widget.Visible ? widget.Height + 5 : 0));
                popup.Location = KeepOnScreen(pinned && savedPosition.HasValue ? savedPosition.Value : defaultPosition, area);
                LayoutPopup();
            }
            popup.Show();
            popup.Activate();
            RefreshSampling();
            Sample();
        }
        finally { opening = false; }
    }
    int Scale(int pixels) { return (int)Math.Round(pixels * popup.ClientSize.Width / (double)PopupWidth); }
    void LayoutPopup()
    {
        int bottom = popup.ClientSize.Height;
        int rowsTop = Scale(132);
        int rowHeight = Math.Max(Scale(1), (bottom - Scale(71) - rowsTop) / captions.Length);
        for (int i = 0; i < captions.Length; i++)
        {
            captions[i].Top = values[i].Top = rowsTop + i * rowHeight;
            captions[i].Height = values[i].Height = Math.Max(1, rowHeight - Scale(5));
        }
        foreach (Label metric in metrics) metric.Top = bottom - Scale(68);
        note.Top = bottom - Scale(41);
        grip.SetBounds(0, bottom - Scale(19), popup.ClientSize.Width, Scale(18));
    }
    Point KeepOnScreen(Point point, Rectangle area)
    {
        return new Point(Math.Max(area.Left, Math.Min(area.Right - popup.Width, point.X)),
            Math.Max(area.Top, Math.Min(area.Bottom - popup.Height, point.Y)));
    }
    void BeginMove(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (!pinned) SetPinned(true);
        gestureStart = Cursor.Position;
        panelStart = popup.Location;
        title.Capture = true;
    }
    void MovePanel(object sender, MouseEventArgs e)
    {
        if (!title.Capture || e.Button != MouseButtons.Left) return;
        Point cursor = Cursor.Position;
        popup.Location = KeepOnScreen(new Point(panelStart.X + cursor.X - gestureStart.X,
            panelStart.Y + cursor.Y - gestureStart.Y), Screen.FromPoint(cursor).WorkingArea);
    }
    void BeginResize(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        gestureStart = Cursor.Position;
        panelStart = popup.Location;
        startHeight = popup.Height;
        grip.Capture = true;
    }
    void ResizePanel(object sender, MouseEventArgs e)
    {
        if (!grip.Capture || e.Button != MouseButtons.Left) return;
        Rectangle area = Screen.FromControl(popup).WorkingArea;
        int min = Math.Min(Scale(MinimumHeight), area.Height);
        popup.Height = Math.Max(min, Math.Min(area.Height, startHeight + Cursor.Position.Y - gestureStart.Y));
        popup.Location = KeepOnScreen(panelStart, area);
        preferredHeight = (int)Math.Round(popup.Height * (double)PopupWidth / popup.ClientSize.Width);
    }
    void SetPinned(bool value)
    {
        pinned = value;
        UpdatePinControl();
        SavePanelSettings();
    }
    void UpdatePinControl()
    {
        pinButton.Text = pinned ? "Bỏ ghim" : "Ghim";
        pinButton.AccessibleName = pinButton.Text;
        pinItem.Checked = pinned;
    }
    void LoadPanelSettings()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(PanelKey))
            {
                if (key == null) return;
                pinned = Equals(key.GetValue("Pinned"), 1);
                int height = (int)key.GetValue("CompactHeight", DefaultHeight);
                preferredHeight = Math.Max(MinimumHeight, Math.Min(4320, height));
                object x = key.GetValue("X"), y = key.GetValue("Y");
                if (x is int && y is int) savedPosition = new Point((int)x, (int)y);
            }
        }
        catch (Exception ex)
        {
            if (!(ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException || ex is InvalidCastException)) throw;
        }
    }
    void SavePanelSettings()
    {
        if (pinned && popup.Visible) savedPosition = popup.Location;
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(PanelKey))
            {
                key.SetValue("Pinned", pinned ? 1 : 0);
                key.SetValue("CompactHeight", preferredHeight);
                if (savedPosition.HasValue)
                {
                    key.SetValue("X", savedPosition.Value.X);
                    key.SetValue("Y", savedPosition.Value.Y);
                }
            }
        }
        catch (Exception ex)
        {
            if (!(ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException)) throw;
        }
    }
    bool StartupEnabled()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(StartupKey))
            return key != null && string.Equals(key.GetValue(StartupValue) as string, StartupCommand(), StringComparison.OrdinalIgnoreCase);
    }
    static string StartupCommand() { return "\"" + Application.ExecutablePath + "\""; }
    void ToggleStartup()
    {
        try
        {
            bool enabled = StartupEnabled();
            using (var key = Registry.CurrentUser.CreateSubKey(StartupKey))
            {
                if (enabled) key.DeleteValue(StartupValue, false);
                else key.SetValue(StartupValue, StartupCommand());
            }
            startupItem.Checked = !enabled;
        }
        catch (Exception ex)
        {
            if (!(ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException)) throw;
            MessageBox.Show("Windows không cho thay đổi tùy chọn tự chạy.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
    void RefreshSampling()
    {
        nextDetail = DateTime.MinValue;
        timer.Interval = popup.Visible ? 2500 : widget.Visible ? 5000 : 15000;
        if (!popup.Visible && !widget.Visible) hardware.StopGpu();
    }
    static string Number(double? value, string format, string unit)
    {
        return value.HasValue ? value.Value.ToString(format, Vietnamese) + unit : "—";
    }
    static double? ChargeSeconds(double? fullWh, double? remainingWh, double? watts)
    {
        if (!fullWh.HasValue || !remainingWh.HasValue || !watts.HasValue ||
            watts.Value <= 0 || fullWh.Value <= remainingWh.Value) return null;
        return (fullWh.Value - remainingWh.Value) * 3600 / watts.Value;
    }
    void Sample()
    {
        DateTime now = DateTime.UtcNow;
        Hardware.Power power;
        bool ok = Hardware.GetSystemPowerStatus(out power);
        bool hasBattery = ok && (power.Flags == 255 ? power.Percent <= 100 : (power.Flags & 128) == 0);
        bool discharging = hasBattery && power.AC == 0;
        bool plugged = hasBattery && power.AC == 1;
        int percent = hasBattery && power.Percent <= 100 ? power.Percent : -1;
        bool active = popup.Visible || widget.Visible;
        bool powerChanged = power.AC != previousAc;
        if (powerChanged || power.Percent != previousPercent || now >= nextDetail)
        {
            battery = hardware.ReadBattery(hasBattery, discharging, plugged);
            bool chargingNow = plugged && percent != 100 &&
                (battery.ChargeWatts.HasValue || (power.Flags != 255 && (power.Flags & 8) != 0));
            if (powerChanged || chargingNow != previousCharging) { averageWatts = null; lastRate = default(DateTime); }
            previousCharging = chargingNow;
            double? sampleWatts = chargingNow ? battery.ChargeWatts : battery.Watts;
            if (sampleWatts.HasValue)
            {
                double elapsed = (now - lastRate).TotalSeconds;
                double weight = 1 - Math.Exp(-elapsed / 45);
                averageWatts = averageWatts.HasValue && elapsed < 90
                    ? averageWatts.Value + weight * (sampleWatts.Value - averageWatts.Value) : sampleWatts;
                lastRate = now;
            }
            if ((now - lastRate).TotalSeconds > 45) averageWatts = null;
            nextDetail = now.AddSeconds(active ? 2 : 15);
            previousAc = power.AC;
            previousPercent = power.Percent;
        }
        if (active)
        {
            var memory = Hardware.ReadMemory();
            cpuText = Number(hardware.Cpu(), "0", "%");
            ramText = memory.Total == 0 ? "—" : Number(memory.Load, "0", "%");
            gpuText = Number(hardware.Gpu(), "0", "%");
        }
        else
        {
            hardware.Cpu();
        }
        double? seconds = null;
        bool fromWindows = false;
        bool charging = plugged && previousCharging && percent != 100;
        if (charging && battery.ChargeWatts.HasValue)
            seconds = ChargeSeconds(battery.FullWh, battery.RemainingWh, averageWatts);
        if (discharging)
        {
            if (averageWatts.HasValue && battery.RemainingWh.HasValue)
                seconds = battery.RemainingWh.Value / averageWatts.Value * 3600;
            else if (power.Seconds != uint.MaxValue && power.Seconds <= 7 * 86400)
            { seconds = power.Seconds; fromWindows = true; }
        }
        if (seconds.HasValue && seconds.Value > 7 * 86400) seconds = null;
        string duration = "—", end = "—";
        if (seconds.HasValue)
        {
            int minutes = (int)Math.Ceiling(seconds.Value / 60);
            duration = string.Format(Vietnamese, "{0} giờ {1:00} phút", minutes / 60, minutes % 60);
            DateTime empty = DateTime.Now.AddSeconds(seconds.Value);
            end = empty.ToString(empty.Date == DateTime.Today ? "HH:mm" : "HH:mm dd/MM", Vietnamese);
        }
        string pct = percent < 0 ? "—" : percent + "%";
        string status = !ok ? "Windows chưa cung cấp trạng thái pin" : !hasBattery ? "Windows chưa nhận diện pin" :
            plugged ? (percent == 100 ? "Đã sạc đầy" : charging ? "Đang sạc" : "Cắm nguồn · Chưa sạc") :
            discharging ? (fromWindows ? "Đang dùng pin · Dự báo từ Windows" : "Đang dùng pin") : "Trạng thái nguồn chưa rõ";
        string summary = AppName + " · " + pct + " · " + (seconds.HasValue ? duration : status);
        if (tray.Text != summary) tray.Text = summary.Length > 63 ? summary.Substring(0, 63) : summary;
        UpdateIcon(percent, power.AC == 1);
        if (active)
        {
            batteryPercent.Text = pct;
            state.Text = status;
            captions[0].Text = plugged ? "Công suất sạc" : "Công suất xả";
            captions[3].Text = plugged ? "Còn đến đầy" : "Thời gian còn";
            captions[4].Text = plugged ? "Dự kiến sạc đầy" : "Dự kiến cạn pin";
            values[0].Text = Number(plugged ? battery.ChargeWatts : battery.Watts, "0.0", " W");
            values[1].Text = Number(battery.Wear, "0.0", "%");
            values[2].Text = Number(battery.RemainingWh, "0.0", " Wh");
            values[3].Text = plugged && percent == 100 ? "Đã đầy" : duration;
            values[4].Text = end;
            metrics[0].Text = "CPU " + cpuText;
            metrics[1].Text = "RAM " + ramText;
            metrics[2].Text = "GPU " + gpuText;
            compact.Text = "Pin " + pct + "  " + metrics[0].Text + "  " + metrics[1].Text + "  " + metrics[2].Text;
        }
    }
    [DllImport("user32")] static extern bool DestroyIcon(IntPtr handle);
    void UpdateIcon(int percent, bool plugged)
    {
        string key = percent + "/" + plugged;
        if (key == iconKey) return;
        iconKey = key;
        using (var bmp = new Bitmap(32, 32))
        using (var gfx = Graphics.FromImage(bmp))
        using (var background = new SolidBrush(percent >= 0 && percent <= 15 && !plugged
            ? Color.FromArgb(167, 43, 53) : plugged ? Color.FromArgb(18, 106, 77) : Color.FromArgb(35, 54, 85)))
        using (var font = new Font("Segoe UI", percent == 100 ? 11 : 15, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            gfx.SmoothingMode = SmoothingMode.AntiAlias;
            gfx.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            gfx.FillRectangle(background, 0, 6, 29, 21);
            gfx.FillRectangle(background, 29, 12, 3, 9);
            gfx.DrawString(percent < 0 ? "–" : percent.ToString() + "%", font, Brushes.White, new RectangleF(0, 1, 29, 31), format);
            IntPtr handle = bmp.GetHicon();
            try
            {
                var fresh = (Icon)Icon.FromHandle(handle).Clone();
                tray.Icon = fresh;
                if (icon != null) icon.Dispose();
                icon = fresh;
            }
            finally { DestroyIcon(handle); }
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            disposingApp = true;
            timer.Dispose();
            tray.Visible = false;
            tray.Dispose();
            if (icon != null) icon.Dispose();
            menu.Dispose();
            popup.Dispose();
            widget.Dispose();
            hardware.Dispose();
        }
        base.Dispose(disposing);
    }
}
