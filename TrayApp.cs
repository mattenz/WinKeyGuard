using System.Diagnostics;
using HidSharp;
using Microsoft.Win32;

namespace WinKeyGuard;

sealed class TrayApp : ApplicationContext
{
    const int RetryMs = 10000;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    readonly object _lock = new();                 // serialises all keyboard (HID) operations
    readonly SynchronizationContext _ui;
    readonly FocusMonitor _monitor = new();
    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _debounce = new();
    readonly System.Windows.Forms.Timer _retry = new() { Interval = RetryMs };
    readonly ToolStripMenuItem _statusItem = new("Starting...") { Enabled = false };
    readonly ToolStripMenuItem _startupItem = new("Start with Windows");
    readonly Icon _iconEnabled, _iconDisabled, _iconWarn;

    Config _cfg;
    KeyboardController _kb;

    volatile bool _desired;            // should the Win keys be disabled right now?
    bool _pendingTarget;               // latest focus state, before debouncing
    ApplyResult? _lastResult;
    bool _mayBeDisabled = true;        // Win keys might currently be disabled on the keyboard
    bool _confirmedDisabled;           // we verified a disable and have not yet verified a restore
    bool _warned;                      // one notification per focus change
    volatile bool _shutDown;
    int _shutdownDone;
    SettingsForm? _settings;

    public TrayApp()
    {
        Log.Init();
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _cfg = Config.Load();
        _kb = new KeyboardController(_cfg);

        var iconSize = SystemInformation.SmallIconSize;
        _iconEnabled = Assets.Load("tray-enabled.ico", iconSize);
        _iconDisabled = Assets.Load("tray-disabled.ico", iconSize);
        _iconWarn = Assets.Load("tray-warn.ico", iconSize);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Settings...", null, (_, _) => OpenSettings())
        {
            Font = new Font(menu.Font, FontStyle.Bold)
        });
        menu.Items.Add("Restore Win keys now", null, (_, _) => ForceRestore());
        menu.Items.Add("Reload config file", null, (_, _) => ReloadConfig());
        menu.Items.Add("Open data folder", null, (_, _) => Process.Start("explorer.exe", AppPaths.Dir));
        menu.Items.Add("Refresh keymap backup from keyboard...", null, (_, _) => RefreshBackup());
        _startupItem.CheckOnClick = true;
        _startupItem.Checked = IsStartupEnabled();
        _startupItem.Click += (_, _) => SetStartup(_startupItem.Checked);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _tray = new NotifyIcon
        {
            Icon = _iconEnabled,
            Text = "WinKeyGuard",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => OpenSettings();

        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            SetDesired(_pendingTarget);
        };

        _retry.Tick += (_, _) =>
        {
            _retry.Interval = RetryMs;
            RequestReconcile();
        };

        _monitor.ForegroundExeChanged += exe => OnForeground(exe);
        _monitor.Start();

        DeviceList.Local.Changed += (_, _) => _ui.Post(_ => OnDevicesChanged(), null);

        // Last-chance restores so the Win keys are never left disabled.
        SystemEvents.SessionEnding += (_, _) => ShutdownRestore();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownRestore();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => ShutdownRestore();

        _pendingTarget = _cfg.IsTarget(_monitor.CurrentExe());
        _desired = _pendingTarget;
        Log.Write($"Started. Targets: {string.Join(", ", _cfg.Targets)}. Initial desired-disabled={_desired}");
        RequestReconcile();
    }

    // ---- Focus handling ----------------------------------------------------------------

    void OnForeground(string? exe)
    {
        if (exe == null) return;

        bool isTarget = _cfg.IsTarget(exe);
        if (isTarget == _pendingTarget) return;

        _pendingTarget = isTarget;
        _debounce.Stop();                       // cancels any change still waiting out its delay
        if (isTarget == _desired) return;       // already in the right state

        _debounce.Interval = Math.Max(1, isTarget ? _cfg.DisableDelayMs : _cfg.RestoreDelayMs);
        _debounce.Start();
    }

    void SetDesired(bool disable)
    {
        _desired = disable;
        _warned = false;
        RequestReconcile();
    }

    // ---- Talking to the keyboard (off the UI thread) ------------------------------------

    void RequestReconcile()
    {
        if (_shutDown) return;

        Task.Run(() =>
        {
            ApplyResult result;
            string message;
            bool wanted;

            lock (_lock)
            {
                if (_shutDown) return;
                wanted = _desired;
                result = _kb.Apply(wanted, out message);
            }

            _ui.Post(_ => OnResult(wanted, result, message), null);
        });
    }

    void OnResult(bool wanted, ApplyResult result, string message)
    {
        _lastResult = result;
        Log.Write($"Apply(disable={wanted}) -> {result}: {message}");

        switch (result)
        {
            case ApplyResult.Ok:
                _mayBeDisabled = wanted;
                _confirmedDisabled = wanted;
                _retry.Stop();
                SetStatus(wanted ? "Win keys DISABLED (game focused)" : "Win keys enabled",
                          wanted ? _iconDisabled : _iconEnabled);
                break;

            case ApplyResult.NotConnected:
                SetStatus("Keyboard not reachable over USB", _iconWarn);
                if (wanted && !_warned)
                {
                    _warned = true;
                    Balloon("WinKeyGuard",
                        "Q6 Max isn't connected by USB, so the Windows key was left enabled.",
                        ToolTipIcon.Warning);
                }
                else if (!wanted && _confirmedDisabled && !_warned)
                {
                    _warned = true;
                    Balloon("WinKeyGuard",
                        "Can't reach the keyboard. The Windows key may still be disabled; it will be restored when USB reconnects.",
                        ToolTipIcon.Warning);
                }
                if (wanted || _mayBeDisabled) _retry.Start();   // keep trying quietly
                break;

            case ApplyResult.Mismatch:
            case ApplyResult.NoBackup:
                SetStatus(message, _iconWarn);
                _retry.Stop();
                if (!_warned)
                {
                    _warned = true;
                    Balloon("WinKeyGuard", message, ToolTipIcon.Warning);
                }
                break;

            default:   // Error
                SetStatus("Error: " + message, _iconWarn);
                if (wanted || _mayBeDisabled) _retry.Start();
                break;
        }
    }

    void OnDevicesChanged()
    {
        // A USB device came or went. If we are waiting on the keyboard, retry sooner.
        bool waiting = _lastResult is ApplyResult.NotConnected or ApplyResult.Error;
        if (waiting && _retry.Enabled)
        {
            _retry.Stop();
            _retry.Interval = 1500;
            _retry.Start();
        }
    }

    // ---- Menu actions --------------------------------------------------------------------

    void ForceRestore()
    {
        _debounce.Stop();
        _pendingTarget = false;   // the next time a game gains focus it will disable again
        SetDesired(false);
    }

    void ReloadConfig() => ApplyConfig(Config.Load(), "Config reloaded.");

    void ApplyConfig(Config config, string notice)
    {
        lock (_lock)
        {
            _cfg = config;
            _kb = new KeyboardController(_cfg);
        }

        _debounce.Stop();
        _pendingTarget = _cfg.IsTarget(_monitor.CurrentExe());
        Log.Write($"Config applied. Targets: {string.Join(", ", _cfg.Targets)}; " +
                  $"disable delay {_cfg.DisableDelayMs} ms, restore delay {_cfg.RestoreDelayMs} ms");
        SetDesired(_pendingTarget);
        Balloon("WinKeyGuard", notice, ToolTipIcon.Info);
    }

    void OpenSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            _settings.WindowState = FormWindowState.Normal;
            _settings.Activate();
            return;
        }

        // The form edits a copy; nothing changes until Save is clicked.
        _settings = new SettingsForm(_cfg.Clone(), edited =>
        {
            edited.Save();
            ApplyConfig(edited, "Settings saved.");
        });
        _settings.FormClosed += (_, _) => _settings = null;
        _settings.Show();
    }

    void RefreshBackup()
    {
        var answer = MessageBox.Show(
            "Save the keyboard's current keymap as the new backup?\n\n" +
            "Do this only while the Win keys are enabled and your keymap is the way you want it.",
            "WinKeyGuard", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        Task.Run(() =>
        {
            ApplyResult result;
            string message;
            lock (_lock)
            {
                result = _kb.CreateBackup(overwrite: true, out message);
            }

            _ui.Post(_ =>
            {
                Log.Write($"Refresh backup -> {result}: {message}");
                Balloon("WinKeyGuard", message,
                    result == ApplyResult.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
                if (result == ApplyResult.Ok) RequestReconcile();
            }, null);
        });
    }

    void ExitApp()
    {
        _settings?.Close();
        ShutdownRestore();
        _monitor.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }

    // Restores the Win keys synchronously. Safe to call more than once.
    void ShutdownRestore()
    {
        if (Interlocked.Exchange(ref _shutdownDone, 1) == 1) return;
        _shutDown = true;

        try
        {
            lock (_lock)
            {
                _kb.Apply(false, out string message);
                Log.Write("Shutdown restore: " + message);
            }
        }
        catch { }
    }

    // ---- UI helpers ----------------------------------------------------------------------

    void SetStatus(string text, Icon icon)
    {
        _statusItem.Text = text;
        _tray.Icon = icon;
        string tip = "WinKeyGuard: " + text;
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    void Balloon(string title, string text, ToolTipIcon icon) =>
        _tray.ShowBalloonTip(6000, title, text, icon);

    static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("WinKeyGuard") != null;
    }

    static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key == null) return;

        if (enabled) key.SetValue("WinKeyGuard", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("WinKeyGuard", throwOnMissingValue: false);
    }

}
