using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new RawMeterForm());
    }
}

internal sealed class RawMeterForm : Form
{
    private const int WmInput = 0x00FF;
    private const int WmInputDeviceChange = 0x00FE;
    private const int RidInput = 0x10000003;
    private const int RidDeviceInfo = 0x20000007;
    private const int RidPreparsedData = 0x20000005;
    private const int RimTypeHid = 2;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevDevNotify = 0x00002000;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WmHotKey = 0x0312;
    private const int HotkeyId = 0x515;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const int KeyM = 0x4D;
    private const int HidpInput = 0;

    private readonly ComboBox stickBox = new ComboBox();
    private readonly Label inputLabel = new Label();
    private readonly Label statusLabel = new Label();
    private readonly Label valuesLabel = new Label();
    private readonly Label deviceLabel = new Label();
    private readonly Button clickButton = new Button();
    private readonly Button resetButton = new Button();
    private readonly MeterSurface surface = new MeterSurface();
    private readonly NotifyIcon trayIcon = new NotifyIcon();
    private readonly ContextMenuStrip meterMenu = new ContextMenuStrip();
    private readonly Dictionary<IntPtr, HidDevice> devices = new Dictionary<IntPtr, HidDevice>();
    private bool clickThrough;
    private Color accentColor = Color.FromArgb(214, 255, 88);
    private Color faceColor = Color.FromArgb(21, 25, 28);
    private Image backgroundImage;
    private double x;
    private double y;
    private DateTime lastReport = DateTime.MinValue;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidRawData
    {
        public uint Size;
        public uint Count;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint numberDevices, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, int key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr handle, int id);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr handle, int index, int value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr handle, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage, out uint value, IntPtr preparsedData, IntPtr report, uint reportLength);

    public RawMeterForm()
    {
        Text = "Manual Meter";
        ClientSize = new Size(280, 235);
        FormBorderStyle = FormBorderStyle.None;
        BackColor = Color.Fuchsia;
        TransparencyKey = Color.Fuchsia;
        ForeColor = Color.FromArgb(244, 246, 246);
        Font = new Font("Segoe UI", 9F);
        TopMost = true;
        ShowInTaskbar = false;
        DoubleBuffered = true;

        Rectangle work = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(work.Right - Width - 24, work.Top + 65);
        BuildUi();
        ConfigureMeterMenu();
        ConfigureTrayIcon();
        ShapeWindowToMeter();
    }

    private void BuildUi()
    {
        stickBox.DropDownStyle = ComboBoxStyle.DropDownList;
        stickBox.Items.Add("Left stick · DualSense");
        stickBox.Items.Add("Right stick · DualSense");
        stickBox.SelectedIndex = 0;
        surface.Location = new Point(40, 15);
        surface.Size = new Size(200, 200);
        surface.BackColor = Color.Fuchsia;
        surface.Paint += PaintSurface;
        Controls.Add(surface);
    }

    private void ShapeWindowToMeter()
    {
        using (GraphicsPath circle = new GraphicsPath())
        {
            circle.AddEllipse(surface.Left + 16, surface.Top + 16, 168, 168);
            Region = new Region(circle);
        }
    }

    private void ConfigureMeterMenu()
    {
        ToolStripMenuItem colours = new ToolStripMenuItem("Meter colour");
        colours.DropDownItems.Add(CreateColourItem("Lime", Color.FromArgb(214, 255, 88)));
        colours.DropDownItems.Add(CreateColourItem("Cyan", Color.FromArgb(78, 224, 255)));
        colours.DropDownItems.Add(CreateColourItem("Orange", Color.FromArgb(255, 174, 73)));
        colours.DropDownItems.Add(CreateColourItem("Pink", Color.FromArgb(255, 106, 191)));
        ToolStripMenuItem pickColour = new ToolStripMenuItem("Pick a colour...");
        pickColour.Click += delegate
        {
            using (ColorDialog dialog = new ColorDialog())
            {
                dialog.Color = accentColor;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    accentColor = dialog.Color;
                    Draw();
                }
            }
        };
        colours.DropDownItems.Add(new ToolStripSeparator());
        colours.DropDownItems.Add(pickColour);

        ToolStripMenuItem background = new ToolStripMenuItem("Circle background");
        ToolStripMenuItem chooseImage = new ToolStripMenuItem("Choose image...");
        chooseImage.Click += delegate { ChooseBackgroundImage(); };
        ToolStripMenuItem clearImage = new ToolStripMenuItem("Remove image");
        clearImage.Click += delegate
        {
            if (backgroundImage != null)
            {
                backgroundImage.Dispose();
                backgroundImage = null;
                Draw();
            }
        };
        ToolStripMenuItem pickFace = new ToolStripMenuItem("Choose solid colour...");
        pickFace.Click += delegate
        {
            using (ColorDialog dialog = new ColorDialog())
            {
                dialog.Color = faceColor;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    faceColor = dialog.Color;
                    Draw();
                }
            }
        };
        background.DropDownItems.Add(chooseImage);
        background.DropDownItems.Add(clearImage);
        background.DropDownItems.Add(new ToolStripSeparator());
        background.DropDownItems.Add(pickFace);

        ToolStripMenuItem left = new ToolStripMenuItem("Read left stick");
        left.Click += delegate { stickBox.SelectedIndex = 0; };
        ToolStripMenuItem right = new ToolStripMenuItem("Read right stick");
        right.Click += delegate { stickBox.SelectedIndex = 1; };
        ToolStripMenuItem passThrough = new ToolStripMenuItem("Mouse pass-through");
        passThrough.Click += delegate { SetClickThrough(!clickThrough); };
        ToolStripMenuItem close = new ToolStripMenuItem("Close meter");
        close.Click += delegate { Close(); };

        meterMenu.Items.Add(colours);
        meterMenu.Items.Add(background);
        meterMenu.Items.Add(new ToolStripSeparator());
        meterMenu.Items.Add(left);
        meterMenu.Items.Add(right);
        meterMenu.Items.Add(passThrough);
        meterMenu.Items.Add(new ToolStripSeparator());
        meterMenu.Items.Add(close);
        surface.ContextMenuStrip = meterMenu;
    }

    private ToolStripMenuItem CreateColourItem(string name, Color colour)
    {
        ToolStripMenuItem item = new ToolStripMenuItem(name);
        item.Click += delegate
        {
            accentColor = colour;
            Draw();
        };
        return item;
    }

    private void ChooseBackgroundImage()
    {
        using (OpenFileDialog dialog = new OpenFileDialog())
        {
            dialog.Title = "Choose a meter background image";
            dialog.Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*";
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                using (Image source = Image.FromFile(dialog.FileName))
                {
                    Image replacement = new Bitmap(source);
                    if (backgroundImage != null) backgroundImage.Dispose();
                    backgroundImage = replacement;
                }
                Draw();
            }
            catch
            {
                MessageBox.Show(this, "That image could not be used.", "Manual Meter", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }

    private void ConfigureTrayIcon()
    {
        ContextMenuStrip menu = new ContextMenuStrip();
        ToolStripMenuItem left = new ToolStripMenuItem("Read left stick");
        left.Click += delegate { stickBox.SelectedIndex = 0; };
        ToolStripMenuItem right = new ToolStripMenuItem("Read right stick");
        right.Click += delegate { stickBox.SelectedIndex = 1; };
        ToolStripMenuItem close = new ToolStripMenuItem("Close meter");
        close.Click += delegate { Close(); };
        menu.Items.Add(left);
        menu.Items.Add(right);
        ToolStripMenuItem customise = new ToolStripMenuItem("Customise meter");
        customise.Click += delegate { meterMenu.Show(Cursor.Position); };
        menu.Items.Add(customise);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(close);
        trayIcon.Icon = SystemIcons.Application;
        trayIcon.Text = "Manual Meter";
        trayIcon.ContextMenuStrip = menu;
        trayIcon.Visible = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RawInputDevice[] requested = new RawInputDevice[]
        {
            new RawInputDevice { UsagePage = 0x01, Usage = 0x04, Flags = RidevInputSink | RidevDevNotify, Target = Handle },
            new RawInputDevice { UsagePage = 0x01, Usage = 0x05, Flags = RidevInputSink | RidevDevNotify, Target = Handle }
        };
        if (!RegisterRawInputDevices(requested, (uint)requested.Length, (uint)Marshal.SizeOf(typeof(RawInputDevice))))
            SetStatus("Windows could not register controller input. Restart the app once.");
        RegisterHotKey(Handle, HotkeyId, ModControl | ModShift, KeyM);
        SetClickThrough(false);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmInput)
        {
            ReadRawInput(message.LParam);
            return;
        }
        if (message.Msg == WmInputDeviceChange)
        {
            SetStatus("Controller connection changed. Move a stick to refresh it.");
        }
        if (message.Msg == WmHotKey && message.WParam.ToInt32() == HotkeyId)
        {
            SetClickThrough(!clickThrough);
            return;
        }
        base.WndProc(ref message);
    }

    private void ReadRawInput(IntPtr rawInput)
    {
        uint size = 0;
        uint headerSize = (uint)Marshal.SizeOf(typeof(RawInputHeader));
        GetRawInputData(rawInput, RidInput, IntPtr.Zero, ref size, headerSize);
        if (size < headerSize) return;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(rawInput, RidInput, buffer, ref size, headerSize) != size) return;
            RawInputHeader header = (RawInputHeader)Marshal.PtrToStructure(buffer, typeof(RawInputHeader));
            if (header.Type != RimTypeHid || header.Device == IntPtr.Zero) return;
            IntPtr hidHeader = IntPtr.Add(buffer, (int)headerSize);
            HidRawData hid = (HidRawData)Marshal.PtrToStructure(hidHeader, typeof(HidRawData));
            if (hid.Size == 0 || hid.Count == 0) return;
            int start = (int)headerSize + Marshal.SizeOf(typeof(HidRawData));
            int reportLength = (int)hid.Size;
            for (int i = 0; i < hid.Count; i++)
            {
                int offset = start + i * reportLength;
                if (offset + reportLength > size) break;
                ParseHidReport(header.Device, IntPtr.Add(buffer, offset), (uint)reportLength);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void ParseHidReport(IntPtr deviceHandle, IntPtr report, uint reportLength)
    {
        HidDevice device;
        if (!devices.TryGetValue(deviceHandle, out device))
        {
            device = CreateDevice(deviceHandle);
            if (device == null) return;
            devices[deviceHandle] = device;
        }

        uint leftX, leftY, rightX, rightY;
        bool gotLX = ReadUsage(device, report, reportLength, 0x30, out leftX);
        bool gotLY = ReadUsage(device, report, reportLength, 0x31, out leftY);
        bool gotRX = ReadUsage(device, report, reportLength, 0x32, out rightX);
        bool gotRY = ReadUsage(device, report, reportLength, 0x35, out rightY);
        if (!gotLX && !gotLY && !gotRX && !gotRY) return;

        uint horizontal = stickBox.SelectedIndex == 1 ? rightX : leftX;
        uint vertical = stickBox.SelectedIndex == 1 ? rightY : leftY;
        bool gotHorizontal = stickBox.SelectedIndex == 1 ? gotRX : gotLX;
        bool gotVertical = stickBox.SelectedIndex == 1 ? gotRY : gotLY;
        if (!gotHorizontal || !gotVertical) return;

        x = NormalizeAxis(horizontal);
        y = NormalizeAxis(vertical);
        lastReport = DateTime.Now;
        deviceLabel.Text = "Receiving controller input: " + device.Name;
        inputLabel.Text = "LX " + leftX + "  LY " + leftY + "  RX " + rightX + "  RY " + rightY;
        valuesLabel.Text = "X " + x.ToString("+0.00;-0.00;0.00") + "    Y " + y.ToString("+0.00;-0.00;0.00");
        statusLabel.Text = "Direct HID input is live. Ctrl+Shift+M lets clicks pass through to Roblox.";
        Draw();
    }

    private HidDevice CreateDevice(IntPtr deviceHandle)
    {
        uint size = 0;
        GetRawInputDeviceInfo(deviceHandle, RidPreparsedData, IntPtr.Zero, ref size);
        if (size == 0) return null;
        IntPtr preparsed = Marshal.AllocHGlobal((int)size);
        try
        {
            uint actual = size;
            if (GetRawInputDeviceInfo(deviceHandle, RidPreparsedData, preparsed, ref actual) == 0) return null;
            byte[] data = new byte[actual];
            Marshal.Copy(preparsed, data, 0, data.Length);
            return new HidDevice { Preparsed = data, Name = GetDeviceName(deviceHandle) };
        }
        finally
        {
            Marshal.FreeHGlobal(preparsed);
        }
    }

    private bool ReadUsage(HidDevice device, IntPtr report, uint reportLength, ushort usage, out uint value)
    {
        value = 0;
        GCHandle pinned = GCHandle.Alloc(device.Preparsed, GCHandleType.Pinned);
        try
        {
            int status = HidP_GetUsageValue(HidpInput, 0x01, 0, usage, out value, pinned.AddrOfPinnedObject(), report, reportLength);
            return status >= 0;
        }
        finally
        {
            pinned.Free();
        }
    }

    private static double NormalizeAxis(uint raw)
    {
        if (raw <= 255) return Clamp((raw - 127.5) / 127.5);
        if (raw <= 1023) return Clamp((raw - 511.5) / 511.5);
        if (raw <= 4095) return Clamp((raw - 2047.5) / 2047.5);
        if (raw <= 65535) return Clamp((raw - 32767.5) / 32767.5);
        return 0;
    }

    private static double Clamp(double value)
    {
        return Math.Max(-1.0, Math.Min(1.0, value));
    }

    private static string GetDeviceName(IntPtr handle)
    {
        uint chars = 0;
        GetRawInputDeviceInfo(handle, RidDeviceInfo, IntPtr.Zero, ref chars);
        if (chars == 0) return "controller";
        IntPtr pointer = Marshal.AllocHGlobal(((int)chars + 1) * 2);
        try
        {
            uint requested = chars;
            if (GetRawInputDeviceInfo(handle, RidDeviceInfo, pointer, ref requested) == 0) return "controller";
            string path = Marshal.PtrToStringUni(pointer);
            if (String.IsNullOrEmpty(path)) return "controller";
            if (path.IndexOf("VID_054C", StringComparison.OrdinalIgnoreCase) >= 0) return "DualSense";
            return "game controller";
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }

    private void PaintSurface(object sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float cx = surface.Width / 2F;
        float cy = surface.Height / 2F;
        const float outerRadius = 84F;
        const float innerRadius = 30F;
        RectangleF faceBounds = new RectangleF(cx - outerRadius, cy - outerRadius, outerRadius * 2F, outerRadius * 2F);

        // Everything outside this disc stays the form's transparency-key colour.
        using (Brush face = new SolidBrush(faceColor)) g.FillEllipse(face, faceBounds);
        if (backgroundImage != null)
        {
            GraphicsState saved = g.Save();
            using (GraphicsPath clip = new GraphicsPath())
            {
                clip.AddEllipse(faceBounds);
                g.SetClip(clip);
                g.DrawImage(backgroundImage, faceBounds);
            }
            g.Restore(saved);
        }
        using (Pen outer = new Pen(accentColor, 2.2F)) g.DrawEllipse(outer, faceBounds);
        using (Pen guide = new Pen(Color.FromArgb(123, 145, 151), 1.2F))
        {
            g.DrawLine(guide, cx, cy - outerRadius + 11, cx, cy + outerRadius - 11);
            g.DrawLine(guide, cx - outerRadius + 11, cy, cx + outerRadius - 11, cy);
            g.DrawEllipse(guide, cx - 57, cy - 57, 114, 114);
            g.DrawEllipse(guide, cx - innerRadius, cy - innerRadius, innerRadius * 2F, innerRadius * 2F);
        }

        double markerX = x;
        double markerY = y;
        double length = Math.Sqrt(markerX * markerX + markerY * markerY);
        if (length > 1.0)
        {
            markerX /= length;
            markerY /= length;
        }
        float px = cx + (float)markerX * 67F;
        float py = cy + (float)markerY * 67F;
        using (Brush glow = new SolidBrush(Color.FromArgb(55, accentColor.R, accentColor.G, accentColor.B))) g.FillEllipse(glow, px - 18, py - 18, 36, 36);
        using (Brush dot = new SolidBrush(accentColor)) g.FillEllipse(dot, px - 9, py - 9, 18, 18);
        using (Pen edge = new Pen(Color.FromArgb(248, 255, 226), 2F)) g.DrawEllipse(edge, px - 9, py - 9, 18, 18);
    }

    private void Draw()
    {
        surface.Invalidate();
    }

    private void SetStatus(string text)
    {
        statusLabel.Text = text;
    }

    private void DragWindow(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
    }

    private void SetClickThrough(bool enabled)
    {
        clickThrough = enabled;
        long style = IntPtr.Size == 8 ? GetWindowLongPtr64(Handle, GwlExStyle).ToInt64() : GetWindowLong32(Handle, GwlExStyle);
        if (enabled) style |= WsExTransparent;
        else style &= ~((long)WsExTransparent);
        if (IntPtr.Size == 8) SetWindowLongPtr64(Handle, GwlExStyle, new IntPtr(style));
        else SetWindowLong32(Handle, GwlExStyle, (int)style);
        clickButton.Text = enabled ? "Mouse pass-through: ON  ·  Ctrl+Shift+M" : "Mouse pass-through: OFF  ·  Ctrl+Shift+M";
        clickButton.BackColor = enabled ? Color.FromArgb(214, 255, 88) : Color.FromArgb(37, 42, 46);
        clickButton.ForeColor = enabled ? Color.FromArgb(21, 25, 18) : Color.FromArgb(240, 244, 244);
    }

    private static void StyleButton(Button button, bool accent)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = accent ? Color.FromArgb(214, 255, 88) : Color.FromArgb(62, 70, 74);
        button.BackColor = accent ? Color.FromArgb(214, 255, 88) : Color.FromArgb(37, 42, 46);
        button.ForeColor = accent ? Color.FromArgb(21, 25, 18) : Color.FromArgb(240, 244, 244);
        button.Cursor = Cursors.Hand;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UnregisterHotKey(Handle, HotkeyId);
        if (backgroundImage != null) backgroundImage.Dispose();
        trayIcon.Visible = false;
        trayIcon.Dispose();
        base.OnFormClosed(e);
    }
}

internal sealed class HidDevice
{
    public byte[] Preparsed;
    public string Name;
}

internal sealed class MeterSurface : Panel
{
    public MeterSurface()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
    }
}
