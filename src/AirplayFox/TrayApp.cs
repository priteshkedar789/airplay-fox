using System.Drawing.Imaging;
using AirplayFox.Protocol;

namespace AirplayFox;

/// <summary>The whole UI: a fox in the tray. Colour = streaming, grey = anything else (the menu header says what).</summary>
public sealed class TrayApp : ApplicationContext
{
    private readonly Settings _settings = Settings.Load();
    private readonly Supervisor _supervisor;
    private readonly Discovery _discovery = new();
    private readonly Dictionary<string, AirplayDevice> _devices = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    private readonly Icon _foxColor;
    private readonly Icon _foxGrey;

    public TrayApp()
    {
        LocalMute.Restore(); // a previous crash must not leave the PC muted
        (_foxColor, _foxGrey) = LoadIcons();

        _supervisor = new Supervisor(_settings);
        _supervisor.Changed += () => _ui.Post(_ => Refresh(), null);

        _tray = new NotifyIcon { Icon = _foxGrey, Text = "AirplayFox - looking for speakers", ContextMenuStrip = _menu, Visible = true };
        _menu.Opening += (_, _) => BuildMenu();
        _tray.DoubleClick += (_, _) => { if (_supervisor.State != LinkState.Idle) _supervisor.Stop(); else ConnectLast(); };

        _discovery.DeviceFound += d => _ui.Post(_ => _devices[d.Id] = d, null);
        _discovery.Start();
        BuildMenu();
    }

    private static (Icon color, Icon grey) LoadIcons()
    {
        try
        {
            using var stream = typeof(TrayApp).Assembly.GetManifestResourceStream("fox.ico")!;
            var color = new Icon(stream, SystemInformation.SmallIconSize);
            using var bmp = color.ToBitmap();
            using var greyBmp = new Bitmap(bmp.Width, bmp.Height);
            using (var g = Graphics.FromImage(greyBmp))
            {
                var m = new ColorMatrix(new[]
                {
                    new[] { .30f, .30f, .30f, 0, 0 }, new[] { .55f, .55f, .55f, 0, 0 }, new[] { .15f, .15f, .15f, 0, 0 },
                    new[] { 0f, 0, 0, .65f, 0 }, new[] { 0f, 0, 0, 0, 1 },
                });
                using var attrs = new ImageAttributes();
                attrs.SetColorMatrix(m);
                g.DrawImage(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, attrs);
            }
            return (color, Icon.FromHandle(greyBmp.GetHicon())); // handle lives as long as the app
        }
        catch (Exception ex)
        {
            Log.Warn($"icon load failed: {ex.Message}");
            return (SystemIcons.Application, SystemIcons.Application);
        }
    }

    private void ConnectLast()
    {
        var d = _devices.Values.FirstOrDefault(x => x.Name == _settings.LastDevice) ?? _devices.Values.FirstOrDefault();
        if (d != null) Connect(d);
    }

    private void Connect(AirplayDevice device)
    {
        _settings.LastDevice = device.Name;
        _settings.Save();
        _supervisor.Start(device);
    }

    private void Refresh()
    {
        var s = _supervisor.State;
        _tray.Icon = s == LinkState.Streaming ? _foxColor : _foxGrey;
        var text = s switch
        {
            LinkState.Idle => "AirplayFox - not connected",
            LinkState.Streaming => $"AirplayFox - {_supervisor.Device?.Name}, {_supervisor.CurrentLatencyMs} ms",
            _ => $"AirplayFox - {s.ToString().ToLowerInvariant()}...",
        };
        _tray.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon limit
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var status = _supervisor.State switch
        {
            LinkState.Idle => "Not connected",
            LinkState.Tuning => "Measuring network...",
            LinkState.Connecting => $"Connecting to {_supervisor.Device?.Name}...",
            LinkState.Reconnecting => $"Reconnecting ({_supervisor.Detail})",
            _ => $"Streaming to {_supervisor.Device?.Name} ({_supervisor.CurrentLatencyMs} ms)",
        };
        _menu.Items.Add(new ToolStripMenuItem(status) { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());

        var speakers = new ToolStripMenuItem("Speakers");
        foreach (var d in _devices.Values.OrderBy(x => x.Name))
        {
            var current = _supervisor.Device?.Id == d.Id;
            speakers.DropDownItems.Add(new ToolStripMenuItem(d.Name, null, (_, _) => { if (current) _supervisor.Stop(); else Connect(d); }) { Checked = current });
        }
        if (speakers.DropDownItems.Count == 0) speakers.DropDownItems.Add(new ToolStripMenuItem("Searching...") { Enabled = false });
        _menu.Items.Add(speakers);

        if (_supervisor.State != LinkState.Idle)
            _menu.Items.Add(new ToolStripMenuItem("Disconnect", null, (_, _) => _supervisor.Stop()));

        var latency = new ToolStripMenuItem("Latency");
        var chosen = _settings.LatencySeconds;
        bool Is(double? v) => chosen is null ? v is null : v is not null && Math.Abs(chosen.Value - v.Value) < 0.0005;
        latency.DropDownItems.Add(new ToolStripMenuItem("Auto (measure at connect)", null, (_, _) => SetLatency(null)) { Checked = chosen is null });
        latency.DropDownItems.Add(new ToolStripSeparator());
        foreach (var sec in new[] { 0.0, 0.10, 0.25, 0.50, 0.75, 1.00 })
        {
            var v = sec;
            latency.DropDownItems.Add(new ToolStripMenuItem($"{v:0.00} s", null, (_, _) => SetLatency(v)) { Checked = Is(v) });
        }
        var presets = new[] { 0.0, 0.10, 0.25, 0.50, 0.75, 1.00 };
        var customActive = chosen is { } c && !presets.Any(x => Math.Abs(x - c) < 0.0005);
        latency.DropDownItems.Add(new ToolStripMenuItem(customActive ? $"Custom... ({chosen:0.00} s)" : "Custom...", null, (_, _) => AskCustomLatency()) { Checked = customActive });
        _menu.Items.Add(latency);

        var volume = new ToolStripMenuItem("Speaker volume");
        foreach (var pct in new[] { 15, 25, 33, 50, 70, 100 })
        {
            var p = pct;
            volume.DropDownItems.Add(new ToolStripMenuItem($"{p}%", null, async (_, _) =>
            {
                _settings.VolumePercent = p;
                _settings.Save();
                await _supervisor.SetVolumeAsync(p);
            }) { Checked = Math.Abs(_settings.VolumePercent - p) < 0.5 });
        }
        _menu.Items.Add(volume);

        _menu.Items.Add(new ToolStripMenuItem("Mute this PC's speakers while streaming", null, (_, _) =>
        {
            _settings.MuteLocal = !_settings.MuteLocal;
            _settings.Save();
        }) { Checked = _settings.MuteLocal });

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Open log", null, (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Log.FilePath) { UseShellExecute = true }); } catch { }
        }));
        _menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => Quit()));
    }

    private void SetLatency(double? seconds)
    {
        _settings.LatencySeconds = seconds;
        _settings.Save();
        var device = _supervisor.Device;
        if (device != null) _supervisor.Start(device); // reconnect so the new latency takes effect
    }

    private void AskCustomLatency()
    {
        using var form = new Form
        {
            Text = "Custom latency", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false, MinimizeBox = false, ClientSize = new Size(300, 110), TopMost = true, ShowInTaskbar = false,
        };
        var label = new Label { Left = 12, Top = 12, Width = 276, Text = $"Latency in seconds (0.00 - {Settings.MaxLatencySeconds:0.00}):" };
        var box = new TextBox { Left = 12, Top = 38, Width = 100, Text = (_settings.LatencySeconds ?? 0.5).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) };
        var hint = new Label { Left = 120, Top = 41, Width = 170, ForeColor = SystemColors.GrayText, Text = "lower = snappier, less margin" };
        var ok = new Button { Text = "OK", Left = 132, Top = 74, Width = 75, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 213, Top = 74, Width = 75, DialogResult = DialogResult.Cancel };
        form.Controls.AddRange(new Control[] { label, box, hint, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) => { box.Focus(); box.SelectAll(); };

        while (form.ShowDialog() == DialogResult.OK)
        {
            var text = box.Text.Trim().Replace(',', '.');
            if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec)
                && sec >= 0 && sec <= Settings.MaxLatencySeconds)
            {
                SetLatency(Math.Round(sec, 2));
                return;
            }
            MessageBox.Show(form, $"Enter a number between 0 and {Settings.MaxLatencySeconds:0.00}, e.g. 0.35", "AirplayFox", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            box.Focus();
            box.SelectAll();
        }
    }

    private void Quit()
    {
        _tray.Visible = false;
        _supervisor.Dispose();
        _discovery.Dispose();
        LocalMute.Restore();
        ExitThread();
    }
}
