 
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Media;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CryptoFaucetApp;

public partial class Form1 : Form
{
    private readonly TabControl tabs = new();
    private readonly Dictionary<TabPage, WebView2> browsers = new();
    private readonly HashSet<string> knownPayoutIds = new(StringComparer.Ordinal);
    private readonly HashSet<WebView2> thirdTabAllowedBrowsers = new();
    private readonly HashSet<WebView2> btcFaucetBrowsers = new();
    private CoreWebView2Environment? btcFaucetEnvironment;
    private string btcFaucetEmail = "";
    private static readonly HttpClient httpClient = new();

    private WebView2 browser = new();
    private TabPage homeTab = null!;
    private WebView2? faucetPayBrowser;
    private Panel? faucetPayHost;

    private WebView2? balanceBrowser;
    private Panel? balanceHost;
    private bool balanceUpdateInProgress;
    private bool balanceManualRefreshInProgress;

    private HttpListener? listener;
    private bool serverStarted;
    private bool soundEnabled = true;
    private bool premiumAlertSoundEnabled = true;
    private bool restoreOnPremiumAlert = true;
    private bool exitRequested;
    private NotifyIcon? trayIcon;
    private readonly ContextMenuStrip trayMenu = new();
    private ToolStripMenuItem traySettingsItem = null!;
    private ToolStripMenuItem premiumSoundItem = null!;
    private ToolStripMenuItem premiumRestoreItem = null!;
    private ToolStripMenuItem trayExitItem = null!;
    private bool monitorRunning;
    private bool monitorCheckInProgress;
    private bool payoutsInitialized;
    private int newPayoutCount;

    private readonly System.Windows.Forms.Timer monitorTimer =
        new() { Interval = 30000 };

    private readonly System.Windows.Forms.Timer balanceTimer =
        new() { Interval = 900000 };

    private readonly System.Windows.Forms.Timer premiumAlertTimer =
        new() { Interval = 3000 };

    private bool premiumAlertInitialized;
    private bool premiumCheckInProgress;
    private readonly HashSet<string> knownPremiumMarkers = new(StringComparer.Ordinal);

    private Label balanceLabel = null!;
    private Label balanceTitle = null!;
    private Label statusLabel = null!;
    private Label payoutsTitle = null!;
    private Label monitorTitle = null!;

    private ListBox payoutsList = null!;

    private Button soundButton = null!;
    private Button balanceRefreshButton = null!;
    private Button languageButton = null!;
    private Button startButton = null!;
    private Button stopButton = null!;
    private Button feesButton = null!;
    private Button swapButton = null!;
    private Button withdrawButton = null!;
    private Button cacheButton = null!;

    private readonly ContextMenuStrip languageMenu = new();
    private string currentLanguage = "en";

    private const string BtcFaucetHost = "btcfaucet.in";

    private const string AllowedOrigin =
        "https://cryptofaucet.github.io";

    private const string FaucetPayUrl =
        "https://faucetpay.io/wallet/history";

    private const string FaucetPayBalanceUrl =
        "https://faucetpay.io/wallet";

    private const string PayoutsUrl =
        "http://127.0.0.1:8080/payouts";

    private SoundPlayer? payoutSoundPlayer;
    private SoundPlayer? premiumSoundPlayer;
    private Stream? payoutSoundStream;
    private Stream? premiumSoundStream;

    public Form1()
    {
        InitializeComponent();

        Text = "CryptoFaucet Hub";

        var screen = Screen.PrimaryScreen!.WorkingArea;

        Width = Math.Min(1500, screen.Width);
        Height = Math.Min(900, screen.Height);

        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1000, 600);
        KeyPreview = true;

        LoadAppSettings();
        BuildInterface();
        BuildTrayIcon();

        FormClosing += HandleFormClosing;

        monitorTimer.Tick += async (_, _) =>
            await CheckPayoutsAsync();

        balanceTimer.Tick += async (_, _) =>
            await UpdateBalanceAsync();

        premiumAlertTimer.Tick += async (_, _) =>
            await CheckPremiumAlertAsync();

        FormClosed += (_, _) =>
        {
            monitorTimer.Stop();
            balanceTimer.Stop();
            premiumAlertTimer.Stop();
            trayIcon?.Dispose();
            trayMenu.Dispose();

            try
            {
                payoutSoundPlayer?.Stop();
                payoutSoundPlayer?.Dispose();
                payoutSoundPlayer = null;
                payoutSoundStream?.Dispose();
                payoutSoundStream = null;

                premiumSoundPlayer?.Stop();
                premiumSoundPlayer?.Dispose();
                premiumSoundPlayer = null;
                premiumSoundStream?.Dispose();
                premiumSoundStream = null;

                listener?.Stop();
                listener?.Close();
            }
            catch
            {
            }
        };

        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5)
            {
                browser.CoreWebView2?.Reload();
                e.Handled = true;
            }

            if (e.Control && e.KeyCode == Keys.W)
            {
                if (tabs.SelectedTab != null &&
                    tabs.SelectedTab != homeTab)
                {
                    CloseTab(tabs.SelectedTab);
                }

                e.Handled = true;
            }

            if (e.Control && e.KeyCode == Keys.Tab)
            {
                if (tabs.TabPages.Count > 1)
                {
                    tabs.SelectedIndex =
                        (tabs.SelectedIndex + 1) % tabs.TabPages.Count;
                }

                e.Handled = true;
            }

            if (e.Control && e.Shift && e.KeyCode == Keys.F)
            {
                _ = OpenTabAsync(new Uri(FaucetPayUrl));
                e.Handled = true;
            }
        };

        Shown += async (_, _) =>
        {
            try
            {
                try
                {
                    _ = CoreWebView2Environment
                        .GetAvailableBrowserVersionString();
                }
                catch (WebView2RuntimeNotFoundException)
                {
                    var answer = MessageBox.Show(
                        "Microsoft Edge WebView2 Runtime is not installed.\n\n" +
                        "Download and install it now?",
                        "WebView2 Runtime Required",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);

                    if (answer == DialogResult.Yes)
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName =
                                "https://developer.microsoft.com/en-us/microsoft-edge/webview2/",
                            UseShellExecute = true
                        });
                    }

                    exitRequested = true;
                    Close();
                    return;
                }

                await browser.EnsureCoreWebView2Async();

                ConfigureBrowser(browser);
                StartLocalServer();

                var mainBrowser = browser;

                mainBrowser.CoreWebView2.NavigationCompleted += async (_, e) =>
                {
                    if (!e.IsSuccess)
                        return;

                    premiumAlertInitialized = false;
                    knownPremiumMarkers.Clear();

                    try
                    {
                        // В приложении звук Premium Alert воспроизводит сама программа.
                        // Отключаем дублирующий HTML-звук нового index.html.
                        await mainBrowser.CoreWebView2.ExecuteScriptAsync(
                            "localStorage.setItem('premiumAlertSoundEnabled', 'false');");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("Disable webpage Premium sound error: " + ex.Message);
                    }

                    premiumAlertTimer.Start();
                };

                browser.CoreWebView2.Navigate(
                    "https://cryptofaucet.github.io/faucets/?apprefresh=" +
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                await EnsureFaucetPayTabAsync();
                await EnsureBalanceBrowserAsync();

                balanceTimer.Start();
                _ = UpdateBalanceAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Не удалось запустить приложение.\n\n" + ex.Message,
                    "CryptoFaucet Hub",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        };
    }

    private void BuildInterface()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };

        layout.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, 235));

        layout.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100));

        layout.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100));

        Controls.Add(layout);

        BuildSidebar(layout);

        tabs.Dock = DockStyle.Fill;
        tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        tabs.Padding = new Point(18, 4);
        tabs.ItemSize = new Size(180, 28);

        homeTab = new TabPage("CryptoFaucet Hub");

        browser = new WebView2
        {
            Dock = DockStyle.Fill
        };

        homeTab.Controls.Add(browser);
        tabs.TabPages.Add(homeTab);
        browsers[homeTab] = browser;

        layout.Controls.Add(tabs, 1, 0);

        tabs.DrawItem += (_, e) =>
        {
            var tab = tabs.TabPages[e.Index];
            var rect = e.Bounds;

            TextRenderer.DrawText(
                e.Graphics,
                tab.Text,
                tabs.Font,
                new Rectangle(
                    rect.X + 7,
                    rect.Y + 2,
                    rect.Width - 28,
                    rect.Height - 4),
                SystemColors.ControlText,
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.Left |
                TextFormatFlags.EndEllipsis);

            if (tab != homeTab)
            {
                TextRenderer.DrawText(
                    e.Graphics,
                    "×",
                    new Font(tabs.Font.FontFamily, 12),
                    new Rectangle(
                        rect.Right - 20,
                        rect.Y + 5,
                        16,
                        16),
                    Color.Firebrick,
                    TextFormatFlags.HorizontalCenter |
                    TextFormatFlags.VerticalCenter);
            }
        };

        tabs.MouseDown += (_, e) =>
        {
            for (int i = 1; i < tabs.TabPages.Count; i++)
            {
                var rect = tabs.GetTabRect(i);

                if (new Rectangle(
                    rect.Right - 23,
                    rect.Y + 2,
                    22,
                    rect.Height - 4).Contains(e.Location))
                {
                    CloseTab(tabs.TabPages[i]);
                    return;
                }
            }
        };

        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedTab != null &&
                browsers.TryGetValue(tabs.SelectedTab, out var selected))
            {
                browser = selected;
            }
        };
    }

    private void BuildSidebar(TableLayoutPanel parent)
    {
        var sidebar = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(22, 29, 42),
            Padding = new Padding(7, 8, 7, 7),
            Margin = Padding.Empty
        };

        parent.Controls.Add(sidebar, 0, 0);

        var sideLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = sidebar.BackColor
        };

        sideLayout.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100));

        sideLayout.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 78));

        sideLayout.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 150));

        sideLayout.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 112));

        sideLayout.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100));

        sidebar.Controls.Add(sideLayout);

        // HEADER
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = sidebar.BackColor
        };

        header.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100));

        header.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, 28));

        header.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, 28));

        header.ColumnStyles.Add(
            new ColumnStyle(SizeType.Absolute, 65));

        header.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 22));

        header.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100));

        balanceTitle = MakeLabel(
            "TOTAL BALANCE", 8, Color.Silver, FontStyle.Bold);

        balanceTitle.Dock = DockStyle.Fill;

        header.Controls.Add(balanceTitle, 0, 0);
        header.SetColumnSpan(balanceTitle, 4);

        balanceLabel = MakeLabel(
            "$0.00", 18, Color.White, FontStyle.Bold);

        balanceLabel.Dock = DockStyle.Fill;
        balanceLabel.TextAlign = ContentAlignment.MiddleLeft;

        header.Controls.Add(balanceLabel, 0, 1);

        // Кнопка ручного обновления баланса.
        balanceRefreshButton = MakeButton("↻");
        balanceRefreshButton.Font =
            new Font("Segoe UI Symbol", 12, FontStyle.Bold);

        balanceRefreshButton.Margin = new Padding(1);
        balanceRefreshButton.BackColor =
            Color.FromArgb(43, 48, 59);

        balanceRefreshButton.Click += async (_, _) =>
            await RefreshBalanceAsync();

        header.Controls.Add(balanceRefreshButton, 1, 1);

        // Переключатель теперь управляет только звуком WebView2.
        soundButton = MakeButton("♪");
        soundButton.Font = new Font("Segoe UI Symbol", 12);
        soundButton.Margin = new Padding(1);

        soundButton.Click += (_, _) =>
        {
            soundEnabled = !soundEnabled;

            ApplyBrowserMute();
            ApplyLanguage();
        };

        header.Controls.Add(soundButton, 2, 1);

        languageButton = MakeButton("🌐 EN ▼");
        languageButton.Font = new Font("Segoe UI", 8);
        languageButton.Margin = new Padding(1);

        languageMenu.Items.Add(
            "English", null, (_, _) => SetLanguage("en"));

        languageMenu.Items.Add(
            "Русский", null, (_, _) => SetLanguage("ru"));

        languageButton.Click += (_, _) =>
            languageMenu.Show(
                languageButton,
                new Point(0, languageButton.Height));

        header.Controls.Add(languageButton, 3, 1);

        sideLayout.Controls.Add(header, 0, 0);

        // NAVIGATION
        var navigation = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = new Padding(0, 5, 0, 3),
            Padding = Padding.Empty,
            BackColor = sidebar.BackColor
        };

        navigation.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100));

        for (int i = 0; i < 4; i++)
        {
            navigation.RowStyles.Add(
                new RowStyle(SizeType.Percent, 25));
        }

        feesButton = MakeButton("Limits");
        swapButton = MakeButton("Currency Exchange");
        withdrawButton = MakeButton("Withdraw Funds");
        cacheButton = MakeButton("Clear Cache");
        cacheButton.BackColor = Color.FromArgb(55, 75, 98);

        foreach (var button in new[]
        {
            feesButton,
            swapButton,
            withdrawButton,
            cacheButton
        })
        {
            button.Font = new Font("Segoe UI", 8);
            button.Margin = new Padding(1, 3, 1, 3);
            button.Padding = new Padding(1);
        }

        feesButton.Click += (_, _) =>
            _ = OpenTabAsync(new Uri("https://faucetpay.io/fees"));

        swapButton.Click += (_, _) =>
            _ = OpenTabAsync(new Uri("https://faucetpay.io/wallet/swap"));

        withdrawButton.Click += (_, _) =>
            _ = OpenTabAsync(
                new Uri("https://faucetpay.io/wallet?action=withdraw"));

        cacheButton.Click += async (_, _) =>
            await ClearCacheAsync();

        navigation.Controls.Add(feesButton, 0, 0);
        navigation.Controls.Add(swapButton, 0, 1);
        navigation.Controls.Add(withdrawButton, 0, 2);
        navigation.Controls.Add(cacheButton, 0, 3);

        sideLayout.Controls.Add(navigation, 0, 1);

        // MONITOR
        var monitorPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 2, 0, 2),
            Padding = new Padding(4, 2, 4, 2),
            BackColor = Color.FromArgb(31, 42, 57)
        };

        monitorPanel.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100));

        monitorPanel.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 23));

        monitorPanel.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100));

        monitorTitle = MakeLabel(
            "PAYMENT MONITOR",
            8,
            Color.FromArgb(65, 210, 200),
            FontStyle.Bold);

        monitorTitle.Dock = DockStyle.Fill;

        monitorPanel.Controls.Add(monitorTitle, 0, 0);

        var monitorButtons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = monitorPanel.BackColor
        };

        monitorButtons.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 50));

        monitorButtons.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 50));

        startButton = MakeButton("▶ Start");
        startButton.BackColor = Color.FromArgb(27, 125, 91);
        startButton.Margin = new Padding(1, 2, 2, 2);
        startButton.Font = new Font("Segoe UI", 8);

        stopButton = MakeButton("■ Stop");
        stopButton.BackColor = Color.FromArgb(155, 57, 65);
        stopButton.Margin = new Padding(2, 2, 1, 2);
        stopButton.Font = new Font("Segoe UI", 8);

        startButton.Click += async (_, _) =>
            await StartMonitorAsync();

        stopButton.Click += (_, _) =>
            StopMonitor();

        monitorButtons.Controls.Add(startButton, 0, 0);
        monitorButtons.Controls.Add(stopButton, 1, 0);

        monitorPanel.Controls.Add(monitorButtons, 0, 1);

        sideLayout.Controls.Add(monitorPanel, 0, 2);

        // PAYOUTS
        var payoutsPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = new Padding(0, 5, 0, 0),
            BackColor = sidebar.BackColor
        };

        payoutsPanel.ColumnStyles.Add(
            new ColumnStyle(SizeType.Percent, 100));

        payoutsPanel.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 27));

        payoutsPanel.RowStyles.Add(
            new RowStyle(SizeType.Absolute, 54));

        payoutsPanel.RowStyles.Add(
            new RowStyle(SizeType.Percent, 100));

        payoutsTitle = MakeLabel(
            "Recent Payouts", 10, Color.White, FontStyle.Bold);

        payoutsTitle.Dock = DockStyle.Fill;
        payoutsPanel.Controls.Add(payoutsTitle, 0, 0);

        statusLabel = MakeLabel(
            "Monitoring stopped", 7, Color.LightGray, FontStyle.Regular);

        statusLabel.Dock = DockStyle.Fill;
        statusLabel.AutoEllipsis = false;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;

        payoutsPanel.Controls.Add(statusLabel, 0, 1);

        payoutsList = new ListBox
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(22, 29, 42),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 8),
            IntegralHeight = false
        };

        payoutsPanel.Controls.Add(payoutsList, 0, 2);

        sideLayout.Controls.Add(payoutsPanel, 0, 3);

        ApplyLanguage();
    }

    // =========================================================
    // SYSTEM TRAY AND APP SETTINGS
    // =========================================================

    private string SettingsFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CryptoFaucetHub",
            "settings.json");

    private sealed class AppSettings
    {
        public bool PremiumAlertSoundEnabled { get; set; } = true;
        public bool RestoreOnPremiumAlert { get; set; } = true;
    }

    private void LoadAppSettings()
    {
        try
        {
            string path = SettingsFilePath;
            if (!File.Exists(path))
                return;

            var settings = JsonSerializer.Deserialize<AppSettings>(
                File.ReadAllText(path));

            if (settings == null)
                return;

            premiumAlertSoundEnabled = settings.PremiumAlertSoundEnabled;
            restoreOnPremiumAlert = settings.RestoreOnPremiumAlert;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Settings load error: " + ex);
        }
    }

    private void SaveAppSettings()
    {
        try
        {
            string path = SettingsFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var settings = new AppSettings
            {
                PremiumAlertSoundEnabled = premiumAlertSoundEnabled,
                RestoreOnPremiumAlert = restoreOnPremiumAlert
            };

            File.WriteAllText(
                path,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Settings save error: " + ex);
        }
    }

    private void BuildTrayIcon()
    {
        traySettingsItem = new ToolStripMenuItem();

        premiumSoundItem = new ToolStripMenuItem
        {
            CheckOnClick = true,
            Checked = premiumAlertSoundEnabled
        };

        premiumRestoreItem = new ToolStripMenuItem
        {
            CheckOnClick = true,
            Checked = restoreOnPremiumAlert
        };

        trayExitItem = new ToolStripMenuItem();

        premiumSoundItem.CheckedChanged += (_, _) =>
        {
            premiumAlertSoundEnabled = premiumSoundItem.Checked;
            SaveAppSettings();
        };

        premiumRestoreItem.CheckedChanged += (_, _) =>
        {
            restoreOnPremiumAlert = premiumRestoreItem.Checked;
            SaveAppSettings();
        };

        traySettingsItem.DropDownItems.Add(premiumSoundItem);
        traySettingsItem.DropDownItems.Add(premiumRestoreItem);

        trayMenu.Items.Add(traySettingsItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(trayExitItem);

        trayExitItem.Click += (_, _) => ExitApplication();

        trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "CryptoFaucet Hub",
            ContextMenuStrip = trayMenu,
            Visible = true
        };

        trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        UpdateTrayLanguage();
    }

    private void UpdateTrayLanguage()
    {
        if (traySettingsItem == null)
            return;

        bool ru = currentLanguage == "ru";

        traySettingsItem.Text = ru ? "Настройки" : "Settings";

        premiumSoundItem.Text = ru
            ? "Звук Premium Alert"
            : "Premium Alert sound";

        premiumRestoreItem.Text = ru
            ? "Разворачивать окно при Premium Alert"
            : "Restore window on Premium Alert";

        trayExitItem.Text = ru ? "Выход" : "Exit";
    }

    private void HandleFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (exitRequested || e.CloseReason != CloseReason.UserClosing)
            return;

        e.Cancel = true;
        Hide();
        ShowInTaskbar = false;

        if (trayIcon != null)
            trayIcon.Visible = true;
    }

    private void RestoreFromTray()
    {
        if (IsDisposed)
            return;

        ShowInTaskbar = true;
        Show();

        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;

        Activate();
        BringToFront();
    }

    private void ExitApplication()
    {
        exitRequested = true;
        trayIcon.Visible = false;
        Close();
    }

    // =========================================================
    // PREMIUM BOX ALERT
    // =========================================================

    private async Task CheckPremiumAlertAsync()
    {
        if (premiumCheckInProgress)
            return;

        if (!browsers.TryGetValue(homeTab, out var web) ||
            web.IsDisposed ||
            web.CoreWebView2 == null)
            return;

        premiumCheckInProgress = true;

        try
        {
            // Читаем те же готовые краны и RUB-порог, которые использует
            // новый index.html. Старый data-current-best больше не нужен.
            const string script = """
            (() => {
                const threshold = Number(
                    localStorage.getItem('premiumAlertLimitRUB')
                );

                const limit = Number.isFinite(threshold) && threshold >= 0
                    ? threshold
                    : 0.10;

                const rows = Array.from(
                    document.querySelectorAll('tr[id^="faucet"]')
                ).filter(row => {
                    const cell = row.querySelector('.timer-cell');

                    if (!cell || !cell.classList.contains('ready'))
                        return false;

                    if (window.isFaucetGloballyDisabled &&
                        window.isFaucetGloballyDisabled(row))
                        return false;

                    if (window.isLocalModeEnabled &&
                        window.isLocalModeEnabled() &&
                        row.classList.contains('out-of-limit'))
                        return false;

                    const reward = Number.parseFloat(
                        row.getAttribute('data-rub-value')
                    ) || 0;

                    return reward >= limit;
                }).map(row => ({
                    id: row.id,
                    reward: Number.parseFloat(
                        row.getAttribute('data-rub-value')
                    ) || 0
                }));

                return JSON.stringify({
                    threshold: limit,
                    soundEnabled: document.querySelector('#premium-sound-toggle')?.checked !== false,
                    rows: rows
                });
            })();
            """;

            string result =
                await web.CoreWebView2.ExecuteScriptAsync(script);

            string json =
                JsonSerializer.Deserialize<string>(result) ?? "";

            using var doc = JsonDocument.Parse(json);

            var currentMarkers = new HashSet<string>(StringComparer.Ordinal);

            foreach (var row in doc.RootElement.GetProperty("rows").EnumerateArray())
            {
                string id = row.GetProperty("id").GetString() ?? "";
                double reward = row.GetProperty("reward").GetDouble();

                if (string.IsNullOrWhiteSpace(id))
                    continue;

                string marker = id + "_" +
                    reward.ToString(
                        "0.########",
                        System.Globalization.CultureInfo.InvariantCulture);

                currentMarkers.Add(marker);
            }

            // Первый опрос после загрузки только запоминает текущее состояние,
            // чтобы не выдавать ложные уведомления за уже готовые краны.
            if (!premiumAlertInitialized)
            {
                knownPremiumMarkers.Clear();

                foreach (string marker in currentMarkers)
                    knownPremiumMarkers.Add(marker);

                premiumAlertInitialized = true;
                return;
            }

            bool hasNewPremiumFaucet = false;

            foreach (string marker in currentMarkers)
            {
                if (!knownPremiumMarkers.Contains(marker))
                {
                    hasNewPremiumFaucet = true;
                    break;
                }
            }

            knownPremiumMarkers.Clear();

            foreach (string marker in currentMarkers)
                knownPremiumMarkers.Add(marker);

            if (!hasNewPremiumFaucet)
                return;

            bool pageSoundEnabled =
                doc.RootElement.TryGetProperty("soundEnabled", out var soundElement) &&
                soundElement.ValueKind == JsonValueKind.True;

            if (premiumAlertSoundEnabled && pageSoundEnabled)
                _ = PlayPremiumAlertSoundAsync();

            if (restoreOnPremiumAlert)
                RestoreFromTray();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Premium Alert error: " + ex);
        }
        finally
        {
            premiumCheckInProgress = false;
        }
    }

    // =========================================================
    // FAUCETPAY BALANCE
    // =========================================================

    private async Task EnsureBalanceBrowserAsync()
    {
        if (balanceBrowser != null &&
            !balanceBrowser.IsDisposed &&
            balanceBrowser.CoreWebView2 != null)
        {
            return;
        }

        if (balanceHost == null || balanceHost.IsDisposed)
        {
            balanceHost = new Panel
            {
                Visible = false,
                Size = new Size(1, 1),
                Location = new Point(-2000, -2000),
                TabStop = false
            };

            Controls.Add(balanceHost);
            balanceHost.SendToBack();
        }

        var web = new WebView2
        {
            Dock = DockStyle.Fill
        };

        balanceHost.Controls.Clear();
        balanceHost.Controls.Add(web);

        balanceBrowser = web;

        await web.EnsureCoreWebView2Async(
            faucetPayBrowser!.CoreWebView2!.Environment);

        ConfigureBrowser(web);

        web.CoreWebView2.NavigationCompleted += async (_, e) =>
        {
            if (balanceBrowser != web || web.IsDisposed)
                return;

            try
            {
                if (e.IsSuccess)
                    await UpdateBalanceAsync();
            }
            finally
            {
                if (balanceManualRefreshInProgress)
                {
                    balanceManualRefreshInProgress = false;

                    if (!IsDisposed &&
                        balanceRefreshButton != null &&
                        !balanceRefreshButton.IsDisposed)
                    {
                        balanceRefreshButton.Enabled = true;
                        balanceRefreshButton.Text = "↻";
                    }
                }
            }
        };

        web.CoreWebView2.Navigate(FaucetPayBalanceUrl);
    }

    // Ручное обновление баланса.
    private async Task RefreshBalanceAsync()
    {
        if (balanceManualRefreshInProgress)
            return;

        try
        {
            await EnsureBalanceBrowserAsync();

            var web = balanceBrowser;

            if (web == null ||
                web.IsDisposed ||
                web.CoreWebView2 == null)
            {
                return;
            }

            balanceManualRefreshInProgress = true;

            balanceRefreshButton.Enabled = false;
            balanceRefreshButton.Text = "…";

            web.CoreWebView2.Reload();
        }
        catch (Exception ex)
        {
            balanceManualRefreshInProgress = false;

            if (!IsDisposed && balanceRefreshButton != null)
            {
                balanceRefreshButton.Enabled = true;
                balanceRefreshButton.Text = "↻";
            }

            Debug.WriteLine("Manual balance refresh error: " + ex);
        }
    }

    private async Task UpdateBalanceAsync()
    {
        if (balanceUpdateInProgress)
            return;

        var web = balanceBrowser;

        if (web == null ||
            web.IsDisposed ||
            web.CoreWebView2 == null)
            return;

        balanceUpdateInProgress = true;

        try
        {
            const string script = """
            (() => {
                const labels = [...document.querySelectorAll('label')];
                const label = labels.find(e =>
                    e.textContent.trim().toUpperCase() === 'TOTAL BALANCE');

                let text = '';

                if (label && label.parentElement)
                    text = label.parentElement.innerText || '';

                const match = text.match(/\$\s*[\d,]+(?:\.[\d]+)?/);

                return JSON.stringify({
                    balance: match ? match[0].replace(/\s/g, '') : ''
                });
            })();
            """;

            for (int attempt = 0; attempt < 30; attempt++)
            {
                if (web.IsDisposed || web.CoreWebView2 == null)
                    return;

                string result =
                    await web.CoreWebView2.ExecuteScriptAsync(script);

                string json =
                    JsonSerializer.Deserialize<string>(result) ?? "";

                using var doc = JsonDocument.Parse(json);

                string balance =
                    doc.RootElement.GetProperty("balance").GetString() ?? "";

                if (!string.IsNullOrWhiteSpace(balance))
                {
                    if (!IsDisposed &&
                        balanceLabel != null &&
                        !balanceLabel.IsDisposed)
                    {
                        balanceLabel.Text = balance;
                    }

                    return;
                }

                if (attempt < 29)
                    await Task.Delay(1000);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Balance update error: " + ex);
        }
        finally
        {
            balanceUpdateInProgress = false;
        }
    }

    // =========================================================
    // FAUCETPAY PAYOUT MONITOR
    // =========================================================

    private async Task EnsureFaucetPayTabAsync()
    {
        if (faucetPayBrowser != null &&
            !faucetPayBrowser.IsDisposed &&
            faucetPayBrowser.CoreWebView2 != null)
        {
            return;
        }

        if (faucetPayHost == null || faucetPayHost.IsDisposed)
        {
            faucetPayHost = new Panel
            {
                Visible = false,
                Size = new Size(1, 1),
                Location = new Point(-2000, -2000),
                TabStop = false
            };

            Controls.Add(faucetPayHost);
            faucetPayHost.SendToBack();
        }

        var web = new WebView2
        {
            Dock = DockStyle.Fill
        };

        faucetPayHost.Controls.Clear();
        faucetPayHost.Controls.Add(web);

        faucetPayBrowser = web;

        await web.EnsureCoreWebView2Async();

        ConfigureBrowser(web);

        web.CoreWebView2.Navigate(FaucetPayUrl);
    }

    private async Task StartMonitorAsync()
    {
        if (monitorRunning)
            return;

        startButton.Enabled = false;

        try
        {
            await EnsureFaucetPayTabAsync();

            monitorRunning = true;
            payoutsInitialized = false;
            knownPayoutIds.Clear();
            newPayoutCount = 0;

            UpdateMonitorStatus();

            monitorTimer.Start();

            await CheckPayoutsAsync();
        }
        catch (Exception ex)
        {
            monitorRunning = false;
            monitorTimer.Stop();

            UpdateMonitorStatus(
                (currentLanguage == "ru"
                    ? "Ошибка запуска: "
                    : "Start error: ") + ex.Message);
        }
        finally
        {
            startButton.Enabled = true;
        }
    }

    private void StopMonitor()
    {
        monitorRunning = false;
        monitorTimer.Stop();

        UpdateMonitorStatus();
    }

    private async Task CheckPayoutsAsync()
    {
        if (!monitorRunning || monitorCheckInProgress)
            return;

        monitorCheckInProgress = true;

        try
        {
            using var response = await httpClient.GetAsync(PayoutsUrl);

            string json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                string error = TryGetError(json)
                    ?? $"HTTP {(int)response.StatusCode}";

                UpdateMonitorStatus(
                    (currentLanguage == "ru" ? "Ошибка: " : "Error: ") +
                    error);

                return;
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorElement))
            {
                UpdateMonitorStatus(
                    (currentLanguage == "ru" ? "Ошибка: " : "Error: ") +
                    errorElement.ToString());

                return;
            }

            if (!root.TryGetProperty("ok", out var okElement) ||
                okElement.ValueKind != JsonValueKind.True ||
                !root.TryGetProperty(
                    "transactions", out var transactionsElement) ||
                transactionsElement.ValueKind != JsonValueKind.Array)
            {
                UpdateMonitorStatus(
                    currentLanguage == "ru"
                        ? "Неверный ответ FaucetPay. Проверь вход в аккаунт."
                        : "Invalid FaucetPay response. Check account login.");

                return;
            }

            var transactions = new List<JsonElement>();

            foreach (var item in transactionsElement.EnumerateArray())
                transactions.Add(item.Clone());

            if (!payoutsInitialized)
            {
                payoutsInitialized = true;

                foreach (var t in transactions)
                {
                    string? id = GetString(t, "id");

                    if (!string.IsNullOrWhiteSpace(id))
                        knownPayoutIds.Add(id);
                }

                payoutsList.Items.Clear();

                for (int i = 0; i < Math.Min(10, transactions.Count); i++)
                    AddPayoutToList(transactions[i], false);

                UpdateMonitorStatus(
                    currentLanguage == "ru"
                        ? $"Монитор работает · история загружена ({transactions.Count})"
                        : $"Monitor running · history loaded ({transactions.Count})");

                return;
            }

            var fresh = new List<JsonElement>();

            foreach (var t in transactions)
            {
                string? id = GetString(t, "id");

                if (!string.IsNullOrWhiteSpace(id) &&
                    knownPayoutIds.Add(id))
                {
                    fresh.Add(t);
                }
            }

            for (int i = fresh.Count - 1; i >= 0; i--)
                AddPayoutToList(fresh[i], true);

            if (fresh.Count > 0)
            {
                newPayoutCount += fresh.Count;

                // Звук выплат больше не зависит от переключателя WebView2.
                _ = PlayPayoutSoundAsync();

                UpdateMonitorStatus(
                    currentLanguage == "ru"
                        ? $"Новые выплаты: +{fresh.Count} · всего {newPayoutCount}"
                        : $"New payouts: +{fresh.Count} · total {newPayoutCount}");
            }
            else
            {
                UpdateMonitorStatus(
                    currentLanguage == "ru"
                        ? "Монитор работает · новых выплат нет"
                        : "Monitor running · no new payouts");
            }
        }
        catch (Exception ex)
        {
            UpdateMonitorStatus(
                (currentLanguage == "ru"
                    ? "Ошибка подключения: "
                    : "Connection error: ") + ex.Message);
        }
        finally
        {
            monitorCheckInProgress = false;
        }
    }

    private Task PlayPayoutSoundAsync()
    {
        try
        {
            payoutSoundPlayer?.Stop();
            payoutSoundPlayer?.Dispose();
            payoutSoundStream?.Dispose();

            payoutSoundStream = typeof(Form1).Assembly
                .GetManifestResourceStream("CryptoFaucetApp.money.wav");

            if (payoutSoundStream == null)
                return Task.CompletedTask;

            payoutSoundPlayer = new SoundPlayer(payoutSoundStream);
            payoutSoundPlayer.Load();
            payoutSoundPlayer.Play();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Payout sound error: " + ex.Message);
        }

        return Task.CompletedTask;
    }

    private Task PlayPremiumAlertSoundAsync()
    {
        try
        {
            premiumSoundPlayer?.Stop();
            premiumSoundPlayer?.Dispose();
            premiumSoundStream?.Dispose();

            premiumSoundStream = typeof(Form1).Assembly
                .GetManifestResourceStream("CryptoFaucetApp.notification.wav");

            if (premiumSoundStream == null)
                return Task.CompletedTask;

            premiumSoundPlayer = new SoundPlayer(premiumSoundStream);
            premiumSoundPlayer.Load();
            premiumSoundPlayer.Play();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Premium Alert sound error: " + ex.Message);
        }

        return Task.CompletedTask;
    }

    private void AddPayoutToList(JsonElement t, bool isNew)
    {
        string id = GetString(t, "id") ?? "—";
        string faucet = GetString(t, "faucet_name") ?? "—";
        string coin = GetString(t, "coin") ?? "—";
        string amount = GetString(t, "amount_bitcoin") ?? "—";
        string time = "—";

        if (t.TryGetProperty("date", out var dateValue) &&
            long.TryParse(dateValue.ToString(), out long unix))
        {
            try
            {
                time = DateTimeOffset
                    .FromUnixTimeSeconds(unix)
                    .LocalDateTime
                    .ToString("HH:mm:ss");
            }
            catch
            {
            }
        }

        string line =
            $"{(isNew ? "★ " : "")}{time} | {faucet} | {coin} {amount}";

        payoutsList.Items.Insert(0, line);

        while (payoutsList.Items.Count > 50)
            payoutsList.Items.RemoveAt(payoutsList.Items.Count - 1);
    }

    private static string? GetString(
        JsonElement element,
        string property)
    {
        if (!element.TryGetProperty(property, out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return value.ToString();
    }

    private static string? TryGetError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("error", out var error))
                return error.ToString();
        }
        catch
        {
        }

        return null;
    }

    // =========================================================
    // LANGUAGE AND INTERFACE HELPERS
    // =========================================================

    private void SetLanguage(string language)
    {
        currentLanguage = language == "ru" ? "ru" : "en";
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        bool ru = currentLanguage == "ru";

        soundButton.Text = soundEnabled ? "♪" : "🔇";

        soundButton.BackColor = soundEnabled
            ? Color.FromArgb(43, 48, 59)
            : Color.FromArgb(100, 55, 60);

        balanceTitle.Text = ru
            ? "ОБЩИЙ БАЛАНС"
            : "TOTAL BALANCE";

        languageButton.Text = ru
            ? "🌐 RU ▼"
            : "🌐 EN ▼";

        feesButton.Text = ru ? "Лимиты" : "Limits";
        swapButton.Text = ru ? "Обмен валюты" : "Currency Exchange";
        withdrawButton.Text = ru ? "Вывод средств" : "Withdraw Funds";
        cacheButton.Text = ru ? "Очистить кэш" : "Clear Cache";

        monitorTitle.Text = ru
            ? "МОНИТОР ВЫПЛАТ"
            : "PAYMENT MONITOR";

        startButton.Text = ru ? "▶ Старт" : "▶ Start";
        stopButton.Text = ru ? "■ Стоп" : "■ Stop";

        payoutsTitle.Text = ru
            ? "Последние выплаты"
            : "Recent Payouts";

        UpdateMonitorStatus();
        UpdateTrayLanguage();
    }

    private void UpdateMonitorStatus(string? customText = null)
    {
        if (statusLabel == null)
            return;

        bool ru = currentLanguage == "ru";

        statusLabel.Text = customText ??
            (monitorRunning
                ? (ru ? "Монитор работает" : "Monitor running")
                : (ru ? "Мониторинг остановлен" : "Monitoring stopped"));

        if (startButton != null)
            startButton.Enabled = !monitorRunning;

        if (stopButton != null)
            stopButton.Enabled = monitorRunning;
    }

    private static Label MakeLabel(
        string text,
        float fontSize,
        Color color,
        FontStyle style)
    {
        return new Label
        {
            Text = text,
            ForeColor = color,
            Font = new Font("Segoe UI", fontSize, style),
            AutoEllipsis = true,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
    }

    private static Button MakeButton(string text)
    {
        return new Button
        {
            Text = text,
            Dock = DockStyle.Fill,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(43, 48, 59),
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 9),
            Cursor = Cursors.Hand,
            Margin = new Padding(1, 3, 1, 3),
            Padding = new Padding(2),
            UseVisualStyleBackColor = false
        };
    }

    // =========================================================
    // WEBVIEW2 CACHE
    // =========================================================

    private async Task ClearCacheAsync()
    {
        bool ru = currentLanguage == "ru";

        var answer = MessageBox.Show(
            ru
                ? "Очистить только временный кэш сайтов?\n\n" +
                  "Логины, пароли, cookies, сессии и настройки сайтов останутся."
                : "Clear only temporary website cache?\n\n" +
                  "Logins, passwords, cookies, sessions and website settings will be kept.",
            ru ? "Очистка кэша" : "Clear Cache",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question);

        if (answer != DialogResult.Yes)
            return;

        cacheButton.Enabled = false;
        cacheButton.Text = ru ? "Очистка..." : "Clearing...";

        try
        {
            var profiles = new HashSet<CoreWebView2Profile>();

            void AddProfile(WebView2? web)
            {
                try
                {
                    if (web != null &&
                        !web.IsDisposed &&
                        web.CoreWebView2 != null)
                    {
                        profiles.Add(web.CoreWebView2.Profile);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Get WebView2 profile error: " + ex.Message);
                }
            }

            foreach (var web in browsers.Values)
                AddProfile(web);

            AddProfile(faucetPayBrowser);
            AddProfile(balanceBrowser);

            if (profiles.Count == 0)
                throw new InvalidOperationException(
                    ru
                        ? "Браузерный профиль ещё не запущен."
                        : "The browser profile is not initialized yet.");

            foreach (var profile in profiles)
            {
                await profile.ClearBrowsingDataAsync(
                    CoreWebView2BrowsingDataKinds.DiskCache);
            }

            MessageBox.Show(
                ru
                    ? "Кэш сайтов успешно очищен.\n\n" +
                      "Данные входа и настройки сайтов сохранены."
                    : "Website cache cleared successfully.\n\n" +
                      "Login data and website settings were preserved.",
                ru ? "Готово" : "Done",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                (ru
                    ? "Не удалось очистить кэш:\n\n"
                    : "Could not clear cache:\n\n") + ex.Message,
                ru ? "Ошибка" : "Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            cacheButton.Enabled = true;
            ApplyLanguage();
        }
    }

    // =========================================================
    // WEBVIEW2 CONFIGURATION AND TABS
    // =========================================================

    private void ConfigureBrowser(
        WebView2 web,
        bool allowThirdTab = false)
    {
        if (allowThirdTab)
            thirdTabAllowedBrowsers.Add(web);

        web.CoreWebView2.Settings.AreDevToolsEnabled = false;
        web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;

        // Отключаем или включаем только звук браузера.
        // SoundPlayer выплат и Premium Alert работает независимо.
        web.CoreWebView2.IsMuted = !soundEnabled;

        web.CoreWebView2.NewWindowRequested += async (_, e) =>
        {
            e.Handled = true;

            if (!Uri.TryCreate(
                    e.Uri,
                    UriKind.Absolute,
                    out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp &&
                 uri.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }

            bool sourceIsBtcFaucet = btcFaucetBrowsers.Contains(web);

            if (sourceIsBtcFaucet || IsBtcFaucetUri(uri))
            {
                await OpenBtcFaucetTabAsync(uri);
                return;
            }

            bool allowThirdTab = thirdTabAllowedBrowsers.Contains(web);

            await OpenTabAsync(uri, allowThirdTab);
        };
    }

    private void ApplyBrowserMute()
    {
        var allBrowsers = new HashSet<WebView2>(browsers.Values);

        if (faucetPayBrowser != null)
            allBrowsers.Add(faucetPayBrowser);

        if (balanceBrowser != null)
            allBrowsers.Add(balanceBrowser);

        foreach (var web in allBrowsers)
        {
            try
            {
                if (!web.IsDisposed &&
                    web.CoreWebView2 != null)
                {
                    web.CoreWebView2.IsMuted = !soundEnabled;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Browser mute error: " + ex.Message);
            }
        }
    }

    private static bool IsBtcFaucetUri(Uri uri)
    {
        return uri.Host.Equals(
                   BtcFaucetHost,
                   StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(
                   "." + BtcFaucetHost,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string GetBtcFaucetEmailPath()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CryptoFaucetHub",
            "btcfaucet-email.txt");
    }

    private async Task<CoreWebView2Environment> EnsureBtcFaucetEnvironmentAsync()
    {
        if (btcFaucetEnvironment != null)
            return btcFaucetEnvironment;

        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CryptoFaucetHub",
            "BtcFaucet");

        Directory.CreateDirectory(root);

        string emailPath = GetBtcFaucetEmailPath();

        try
        {
            if (File.Exists(emailPath))
                btcFaucetEmail = File.ReadAllText(emailPath, Encoding.UTF8).Trim();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Read BTC faucet email error: " + ex.Message);
        }

        string profilePath = Path.Combine(root, "WebView2Profile");
        Directory.CreateDirectory(profilePath);

        btcFaucetEnvironment = await CoreWebView2Environment.CreateAsync(
            null,
            profilePath,
            null);

        return btcFaucetEnvironment;
    }

    private async Task RestoreAndWatchBtcFaucetEmailAsync(WebView2 web)
    {
        if (web.IsDisposed || web.CoreWebView2 == null)
            return;

        string emailJson = JsonSerializer.Serialize(btcFaucetEmail);

        string script = """
            (() => {
                const field = document.querySelector('input[name="sydxaCLSFGe"]');
                if (!field || !window.chrome || !window.chrome.webview) return "no-email-field";

                const savedEmail = EMAIL_PLACEHOLDER;
                if (savedEmail) {
                    field.value = savedEmail;
                    field.dispatchEvent(new Event("input", { bubbles: true }));
                    field.dispatchEvent(new Event("change", { bubbles: true }));
                }

                const saveEmail = () => {
                    try { window.chrome.webview.postMessage(field.value); } catch (_) {}
                };

                if (!field.dataset.btcEmailWatcher) {
                    field.dataset.btcEmailWatcher = "1";
                    field.addEventListener("input", saveEmail);
                    field.addEventListener("change", saveEmail);
                    field.addEventListener("blur", saveEmail);
                }

                saveEmail();
                return "email-ready";
            })();
            """.Replace("EMAIL_PLACEHOLDER", emailJson);

        try
        {
            await web.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Restore BTC faucet email error: " + ex.Message);
        }
    }

    private async Task OpenBtcFaucetTabAsync(Uri uri)
    {
        const int maximumTabs = 3;

        if (tabs.TabPages.Count >= maximumTabs)
            return;

        bool newVisit = btcFaucetBrowsers.Count == 0;

        var newTab = new TabPage("Loading...");
        var newBrowser = new WebView2
        {
            Dock = DockStyle.Fill
        };

        newTab.Controls.Add(newBrowser);
        tabs.TabPages.Add(newTab);
        browsers[newTab] = newBrowser;
        btcFaucetBrowsers.Add(newBrowser);
        tabs.SelectedTab = newTab;

        try
        {
            var environment = await EnsureBtcFaucetEnvironmentAsync();
            await newBrowser.EnsureCoreWebView2Async(environment);

            if (newVisit)
            {
                await newBrowser.CoreWebView2.Profile.ClearBrowsingDataAsync(
                    CoreWebView2BrowsingDataKinds.All);
            }

            ConfigureBrowser(newBrowser, allowThirdTab: true);

            newBrowser.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                try
                {
                    string email = e.TryGetWebMessageAsString();
                    if (email.Length > 320)
                        return;

                    btcFaucetEmail = email;
                    string emailPath = GetBtcFaucetEmailPath();
                    Directory.CreateDirectory(Path.GetDirectoryName(emailPath)!);
                    File.WriteAllText(emailPath, email, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Save BTC faucet email error: " + ex.Message);
                }
            };

            newBrowser.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (e.IsSuccess)
                    await RestoreAndWatchBtcFaucetEmailAsync(newBrowser);
            };

            newBrowser.CoreWebView2.DocumentTitleChanged += (_, _) =>
            {
                if (newBrowser.IsDisposed || newBrowser.CoreWebView2 == null)
                    return;

                var title = newBrowser.CoreWebView2.DocumentTitle;
                if (!string.IsNullOrWhiteSpace(title))
                {
                    newTab.Text = title.Length > 22
                        ? title[..22] + "…"
                        : title;
                }
            };

            newBrowser.CoreWebView2.Navigate(uri.ToString());
        }
        catch (Exception ex)
        {
            btcFaucetBrowsers.Remove(newBrowser);
            newTab.Text = currentLanguage == "ru" ? "Ошибка" : "Error";

            MessageBox.Show(
                (currentLanguage == "ru"
                    ? "Не удалось открыть BTC Faucet:\n"
                    : "Could not open BTC Faucet:\n") + ex.Message,
                "CryptoFaucet Hub");
        }
    }

    private async Task OpenTabAsync(
        Uri uri,
        bool allowThirdTab = false)
    {
        if (IsBtcFaucetUri(uri))
        {
            await OpenBtcFaucetTabAsync(uri);
            return;
        }

        int maximumTabs = allowThirdTab ? 3 : 2;

        if (tabs.TabPages.Count >= maximumTabs)
            return;

        var newTab = new TabPage("Loading...");

        var newBrowser = new WebView2
        {
            Dock = DockStyle.Fill
        };

        newTab.Controls.Add(newBrowser);

        tabs.TabPages.Add(newTab);
        browsers[newTab] = newBrowser;

        tabs.SelectedTab = newTab;

        try
        {
            await newBrowser.EnsureCoreWebView2Async();

            ConfigureBrowser(newBrowser, allowThirdTab);

            newBrowser.CoreWebView2.DocumentTitleChanged += (_, _) =>
            {
                if (newBrowser.IsDisposed ||
                    newBrowser.CoreWebView2 == null)
                    return;

                var title = newBrowser.CoreWebView2.DocumentTitle;

                if (!string.IsNullOrWhiteSpace(title))
                {
                    newTab.Text = title.Length > 22
                        ? title[..22] + "…"
                        : title;
                }
            };

            newBrowser.CoreWebView2.Navigate(uri.ToString());
        }
        catch (Exception ex)
        {
            newTab.Text = currentLanguage == "ru"
                ? "Ошибка"
                : "Error";

            MessageBox.Show(
                (currentLanguage == "ru"
                    ? "Не удалось открыть сайт:\n"
                    : "Could not open website:\n") + ex.Message,
                "CryptoFaucet Hub");
        }
    }

    // =========================================================
    // LOCAL SERVER FOR PAYOUT MONITOR
    // =========================================================

    private void StartLocalServer()
    {
        if (serverStarted)
            return;

        listener = new HttpListener();
        listener.Prefixes.Add("http://127.0.0.1:8080/");
        listener.Start();

        serverStarted = true;

        _ = ListenLoopAsync();
    }

    private async Task ListenLoopAsync()
    {
        while (listener != null && listener.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleRequestAsync(context));
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        try
        {
            string origin = request.Headers["Origin"] ?? "";

            bool allowedOrigin = string.Equals(
                origin,
                AllowedOrigin,
                StringComparison.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(origin) && !allowedOrigin)
            {
                await WriteResponseAsync(
                    response,
                    403,
                    "{\"error\":\"Origin not allowed\"}",
                    null);

                return;
            }

            if (allowedOrigin)
            {
                response.Headers["Access-Control-Allow-Origin"] =
                    AllowedOrigin;

                response.Headers["Vary"] = "Origin";

                response.Headers["Access-Control-Allow-Methods"] =
                    "GET, OPTIONS";

                response.Headers["Access-Control-Allow-Headers"] =
                    "Content-Type";

                response.Headers["Access-Control-Allow-Private-Network"] =
                    "true";
            }

            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 204;
                response.Close();
                return;
            }

            if (request.HttpMethod != "GET" ||
                request.Url?.AbsolutePath != "/payouts")
            {
                await WriteResponseAsync(
                    response,
                    404,
                    "{\"error\":\"Not found\"}",
                    origin);

                return;
            }

            var web = await OnUiAsync(() =>
            {
                try
                {
                    if (faucetPayBrowser != null &&
                        !faucetPayBrowser.IsDisposed &&
                        faucetPayBrowser.CoreWebView2 != null &&
                        faucetPayBrowser.Source?.Host.EndsWith(
                            "faucetpay.io",
                            StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return Task.FromResult<WebView2?>(faucetPayBrowser);
                    }
                }
                catch
                {
                }

                foreach (var item in browsers.Values)
                {
                    try
                    {
                        if (item.IsDisposed ||
                            item.CoreWebView2 == null)
                            continue;

                        if (item.Source?.Host.EndsWith(
                            "faucetpay.io",
                            StringComparison.OrdinalIgnoreCase) == true)
                        {
                            return Task.FromResult<WebView2?>(item);
                        }
                    }
                    catch
                    {
                    }
                }

                return Task.FromResult<WebView2?>(null);
            });

            if (web == null)
            {
                await WriteResponseAsync(
                    response,
                    503,
                    "{\"error\":\"FaucetPay is not available.\"}",
                    origin);

                return;
            }

            string token = Guid.NewGuid().ToString("N");

            string startScript = """
                window.__fpFetchResults = window.__fpFetchResults || {};
                window.__fpFetchResults["TOKEN_PLACEHOLDER"] = "pending";
                (async () => {
                  try {
                    const r = await fetch('/app-api/wallet/faucet-transactions', {
                      method: 'POST',
                      headers: {
                        'accept': '*/*',
                        'content-type': 'application/json'
                      },
                      credentials: 'include',
                      cache: 'no-store',
                      body: JSON.stringify({ page: 1 })
                    });

                    window.__fpFetchResults["TOKEN_PLACEHOLDER"] = {
                      status: r.status,
                      body: await r.text()
                    };
                  } catch (e) {
                    window.__fpFetchResults["TOKEN_PLACEHOLDER"] = {
                      error: String(e)
                    };
                  }
                })();
                "started";
                """.Replace("TOKEN_PLACEHOLDER", token);

            await OnUiAsync(
                () => web.CoreWebView2.ExecuteScriptAsync(startScript));

            string scriptResult = "null";
            string serializedToken = JsonSerializer.Serialize(token);

            string readScript =
                "window.__fpFetchResults && window.__fpFetchResults[" +
                serializedToken +
                "] !== undefined ? window.__fpFetchResults[" +
                serializedToken +
                "] : null";

            for (int i = 0; i < 150; i++)
            {
                await Task.Delay(100);

                scriptResult = await OnUiAsync(
                    () => web.CoreWebView2.ExecuteScriptAsync(readScript));

                if (scriptResult != "\"pending\"" &&
                    scriptResult != "null")
                    break;
            }

            if (scriptResult == "\"pending\"" ||
                scriptResult == "null")
            {
                await WriteResponseAsync(
                    response,
                    504,
                    "{\"error\":\"FaucetPay response timeout\"}",
                    origin);

                return;
            }

            using var result = JsonDocument.Parse(scriptResult);
            var root = result.RootElement;

            if (root.TryGetProperty("error", out var errorElement))
            {
                await WriteResponseAsync(
                    response,
                    502,
                    JsonSerializer.Serialize(new
                    {
                        error = errorElement.GetString()
                            ?? "FaucetPay request failed"
                    }),
                    origin);

                return;
            }

            int status = root.GetProperty("status").GetInt32();

            string body = root.GetProperty("body").GetString() ?? "{}";

            if (status < 200 || status >= 300)
            {
                await WriteResponseAsync(
                    response,
                    502,
                    JsonSerializer.Serialize(new
                    {
                        error = "FaucetPay returned HTTP " + status
                    }),
                    origin);

                return;
            }

            using var validation = JsonDocument.Parse(body);

            await WriteResponseAsync(
                response,
                200,
                body,
                origin);
        }
        catch (Exception ex)
        {
            try
            {
                await WriteResponseAsync(
                    response,
                    500,
                    JsonSerializer.Serialize(new { error = ex.Message }),
                    request.Headers["Origin"]);
            }
            catch
            {
            }
        }
    }

    private static async Task WriteResponseAsync(
        HttpListenerResponse response,
        int status,
        string body,
        string? origin)
    {
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";

        if (string.Equals(
                origin,
                AllowedOrigin,
                StringComparison.OrdinalIgnoreCase))
        {
            response.Headers["Access-Control-Allow-Origin"] =
                AllowedOrigin;

            response.Headers["Vary"] = "Origin";

            response.Headers["Access-Control-Allow-Private-Network"] =
                "true";
        }

        byte[] bytes = Encoding.UTF8.GetBytes(body);

        response.ContentLength64 = bytes.Length;

        await response.OutputStream.WriteAsync(bytes);

        response.Close();
    }

    private Task<T> OnUiAsync<T>(Func<Task<T>> action)
    {
        var tcs = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        if (IsDisposed || !IsHandleCreated)
        {
            tcs.TrySetException(
                new InvalidOperationException(
                    "Application window is closed."));

            return tcs.Task;
        }

        try
        {
            BeginInvoke(new Action(async () =>
            {
                try
                {
                    T result = await action();
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }));
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }

        return tcs.Task;
    }

    private void CloseTab(TabPage tab)
    {
        if (tab == homeTab)
            return;

        if (browsers.TryGetValue(tab, out var web))
        {
            browsers.Remove(tab);
            thirdTabAllowedBrowsers.Remove(web);
            btcFaucetBrowsers.Remove(web);
            tabs.TabPages.Remove(tab);
            web.Dispose();
            tab.Dispose();
        }
    }
}
