/*
 * Dark Souls - Open Server (Galidar fork)
 *
 * Setup Wizard — guides the user through:
 *   1. Welcome / pick game type
 *   2. Download the latest Server release (with progress bar)
 *   3. Apply Windows Firewall rules (UAC prompt)
 *   4. Configure Network (auto-detect or manual WAN/LAN)
 *   5. Configure Server settings (name, description, password)
 *   6. Done — start server
 *
 * The wizard is intentionally code-only (no .Designer.cs) so the page logic
 * lives in one place. Each page is a Panel made visible/hidden as the user
 * navigates through Back / Next.
 */

using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using Loader.LocalServer;

namespace Loader.Forms
{
    public class SetupWizardDialog : Form
    {
        // ---------------- Layout constants ----------------
        private const int FormWidth = 720;
        private const int FormHeight = 520;
        private const int HeaderHeight = 70;
        private const int FooterHeight = 60;
        private const int Pad = 16;

        // ---------------- Wizard state ----------------
        private enum Page { Welcome, Download, Firewall, Network, Settings, Done }
        private Page Current = Page.Welcome;

        // ---------------- Top-level controls ----------------
        private Label TitleLabel;
        private Label StepLabel;
        private Panel HeaderPanel, FooterPanel;
        private Panel WelcomePanel, DownloadPanel, FirewallPanel, NetworkPanel, SettingsPanel, DonePanel;
        private Button BackButton, NextButton, CancelBtn;

        // ---------------- Welcome page ----------------
        private ComboBox GameTypeCombo;

        // ---------------- Download page ----------------
        private Label DownloadStatusLabel;
        private ProgressBar DownloadProgress;
        private Label DownloadDetailLabel;
        private Button DownloadButton;
        private bool DownloadComplete;
        private string ResolvedReleaseTag = "";
        private CancellationTokenSource DownloadCts;

        // ---------------- Firewall page ----------------
        private Label FirewallStatusLabel;
        private Button ApplyFirewallButton;
        private Button SkipFirewallLink;
        private bool FirewallReady;

        // ---------------- Network page ----------------
        private RadioButton AutoDetectRadio;
        private RadioButton ManualRadio;
        private TextBox PublicIpTextBox;
        private TextBox PrivateIpTextBox;
        private Label NetworkStatusLabel;
        private Button RedetectButton;

        // ---------------- Settings page ----------------
        private TextBox NameTextBox;
        private TextBox DescriptionTextBox;
        private TextBox PasswordTextBox;
        private CheckBox AdvertiseCheckBox;

        // ---------------- Done page ----------------
        private Label DoneSummaryLabel;
        private Button StartServerButton;
        private Label ServerStatusLabel;

        // ---------------- Wizard config (in-memory until applied) ----------------
        private string GameType = "DarkSouls2";
        private string PublicIp = "";
        private string PrivateIp = "";
        private bool ManualNetwork = false;
        private string ServerName = "My DS3OS Server";
        private string ServerDescription = "A custom Dark Souls server.";
        private string Password = "";
        private bool Advertise = true;

        // GitHub repo to fetch releases from. The fork.
        private const string ReleaseRepo = "Galidar/DSSeamlessCoop";

        public SetupWizardDialog()
        {
            BuildUi();
            LoadExistingConfig();
            ShowPage(Page.Welcome);
        }

        // ============================================================
        //  UI construction
        // ============================================================

        private void BuildUi()
        {
            Text = "DSSeamlessCoop — Server Setup";
            ClientSize = new Size(FormWidth, FormHeight);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9F);

            // ---- Header ----
            HeaderPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = HeaderHeight,
                BackColor = Color.FromArgb(30, 30, 30),
            };
            TitleLabel = new Label
            {
                Text = "Welcome",
                Font = new Font("Segoe UI Semibold", 16F),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(Pad, 12),
            };
            StepLabel = new Label
            {
                Text = "Step 1 of 6",
                Font = new Font("Segoe UI", 9F),
                ForeColor = Color.LightGray,
                AutoSize = true,
                Location = new Point(Pad, 42),
            };
            HeaderPanel.Controls.Add(TitleLabel);
            HeaderPanel.Controls.Add(StepLabel);

            // ---- Footer ----
            FooterPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = FooterHeight,
                BackColor = Color.FromArgb(245, 245, 245),
            };
            CancelBtn = new Button
            {
                Text = "Cancel",
                Width = 100, Height = 32,
                Location = new Point(Pad, 14),
            };
            CancelBtn.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            BackButton = new Button
            {
                Text = "< Back",
                Width = 100, Height = 32,
                Location = new Point(FormWidth - 230, 14),
            };
            BackButton.Click += OnBackClicked;

            NextButton = new Button
            {
                Text = "Next >",
                Width = 110, Height = 32,
                Location = new Point(FormWidth - 120, 14),
            };
            NextButton.Click += OnNextClicked;

            FooterPanel.Controls.Add(CancelBtn);
            FooterPanel.Controls.Add(BackButton);
            FooterPanel.Controls.Add(NextButton);

            // ---- Page panels (all share the same area) ----
            var pageBounds = new Rectangle(0, HeaderHeight, FormWidth, FormHeight - HeaderHeight - FooterHeight);
            WelcomePanel  = MakePagePanel(pageBounds);
            DownloadPanel = MakePagePanel(pageBounds);
            FirewallPanel = MakePagePanel(pageBounds);
            NetworkPanel  = MakePagePanel(pageBounds);
            SettingsPanel = MakePagePanel(pageBounds);
            DonePanel     = MakePagePanel(pageBounds);

            BuildWelcomePage();
            BuildDownloadPage();
            BuildFirewallPage();
            BuildNetworkPage();
            BuildSettingsPage();
            BuildDonePage();

            Controls.Add(WelcomePanel);
            Controls.Add(DownloadPanel);
            Controls.Add(FirewallPanel);
            Controls.Add(NetworkPanel);
            Controls.Add(SettingsPanel);
            Controls.Add(DonePanel);
            Controls.Add(HeaderPanel);
            Controls.Add(FooterPanel);
        }

        private Panel MakePagePanel(Rectangle bounds)
        {
            return new Panel
            {
                Bounds = bounds,
                BackColor = Color.White,
                Visible = false,
            };
        }

        // ---------------- Welcome ----------------

        private void BuildWelcomePage()
        {
            int y = Pad;

            var intro = new Label
            {
                Text = "This wizard installs and configures a private Dark Souls Open Server\n" +
                       "on your machine. We'll download the latest server build, set up the\n" +
                       "Windows Firewall, configure your network, and start the server.\n\n" +
                       "Click Next to begin.",
                AutoSize = true,
                Location = new Point(Pad, y),
                Font = new Font("Segoe UI", 10F),
            };
            WelcomePanel.Controls.Add(intro);
            y += intro.Height + 24;

            var gameLabel = new Label
            {
                Text = "Game type:",
                AutoSize = true,
                Location = new Point(Pad, y),
                Font = new Font("Segoe UI Semibold", 9.5F),
            };
            WelcomePanel.Controls.Add(gameLabel);

            GameTypeCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 220,
                Location = new Point(Pad + 100, y - 3),
            };
            GameTypeCombo.Items.AddRange(new object[] { "DarkSouls2", "DarkSouls3" });
            GameTypeCombo.SelectedIndexChanged += (s, e) => GameType = (string)GameTypeCombo.SelectedItem;
            WelcomePanel.Controls.Add(GameTypeCombo);
            y += 40;

            var note = new Label
            {
                Text = "(Dark Souls II SOTFS is the focus of this fork — the bundled config is\n" +
                       "preconfigured for it. You can change later in Server Settings.)",
                AutoSize = true,
                Location = new Point(Pad, y),
                ForeColor = Color.Gray,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            };
            WelcomePanel.Controls.Add(note);
        }

        // ---------------- Download ----------------

        private void BuildDownloadPage()
        {
            int y = Pad;

            DownloadStatusLabel = new Label
            {
                Text = "Checking for the latest server build…",
                AutoSize = true,
                Location = new Point(Pad, y),
                Font = new Font("Segoe UI", 10F),
            };
            DownloadPanel.Controls.Add(DownloadStatusLabel);
            y += 36;

            DownloadProgress = new ProgressBar
            {
                Bounds = new Rectangle(Pad, y, FormWidth - Pad * 2, 24),
                Style = ProgressBarStyle.Continuous,
                Minimum = 0, Maximum = 100, Value = 0,
            };
            DownloadPanel.Controls.Add(DownloadProgress);
            y += DownloadProgress.Height + 8;

            DownloadDetailLabel = new Label
            {
                Text = "",
                AutoSize = true,
                Location = new Point(Pad, y),
                ForeColor = Color.DimGray,
                Font = new Font("Segoe UI", 9F),
            };
            DownloadPanel.Controls.Add(DownloadDetailLabel);
            y += 32;

            DownloadButton = new Button
            {
                Text = "Download && Install",
                Width = 180, Height = 34,
                Location = new Point(Pad, y),
            };
            DownloadButton.Click += async (s, e) => await StartDownload();
            DownloadPanel.Controls.Add(DownloadButton);
        }

        // ---------------- Firewall ----------------

        private void BuildFirewallPage()
        {
            int y = Pad;

            var hint = new Label
            {
                Text = "We'll create Windows Firewall rules so that other players can reach\n" +
                       "your server. This requires administrator privileges — Windows will\n" +
                       "show a UAC prompt; click Yes when it appears.",
                AutoSize = true,
                Location = new Point(Pad, y),
                Font = new Font("Segoe UI", 10F),
            };
            FirewallPanel.Controls.Add(hint);
            y += hint.Height + 16;

            FirewallStatusLabel = new Label
            {
                Text = "Status: checking…",
                AutoSize = true,
                Location = new Point(Pad, y),
                Font = new Font("Segoe UI Semibold", 9.5F),
            };
            FirewallPanel.Controls.Add(FirewallStatusLabel);
            y += 30;

            ApplyFirewallButton = new Button
            {
                Text = "Apply Firewall Rules",
                Width = 200, Height = 34,
                Location = new Point(Pad, y),
            };
            ApplyFirewallButton.Click += async (s, e) => await ApplyFirewall();
            FirewallPanel.Controls.Add(ApplyFirewallButton);
            y += 44;

            SkipFirewallLink = new Button
            {
                Text = "Skip for now",
                Width = 120, Height = 24,
                FlatStyle = FlatStyle.Flat,
                Location = new Point(Pad, y),
            };
            SkipFirewallLink.FlatAppearance.BorderSize = 0;
            SkipFirewallLink.ForeColor = Color.SteelBlue;
            SkipFirewallLink.Click += (s, e) =>
            {
                // Allow advancing without firewall — useful when running on a
                // host that already has rules in place.
                FirewallReady = true;
                ShowPage(Page.Network);
            };
            FirewallPanel.Controls.Add(SkipFirewallLink);
        }

        // ---------------- Network ----------------

        private void BuildNetworkPage()
        {
            int y = Pad;

            AutoDetectRadio = new RadioButton
            {
                Text = "Auto-detect IPs (recommended for home networks)",
                AutoSize = true,
                Checked = true,
                Location = new Point(Pad, y),
                Font = new Font("Segoe UI", 9.5F),
            };
            AutoDetectRadio.CheckedChanged += (s, e) =>
            {
                if (AutoDetectRadio.Checked) { ManualNetwork = false; UpdateNetworkUi(); }
            };
            NetworkPanel.Controls.Add(AutoDetectRadio);
            y += 28;

            ManualRadio = new RadioButton
            {
                Text = "Manual override (for paid hosting / VPN)",
                AutoSize = true,
                Location = new Point(Pad, y),
                Font = new Font("Segoe UI", 9.5F),
            };
            ManualRadio.CheckedChanged += (s, e) =>
            {
                if (ManualRadio.Checked) { ManualNetwork = true; UpdateNetworkUi(); }
            };
            NetworkPanel.Controls.Add(ManualRadio);
            y += 36;

            var pubLabel = new Label
            {
                Text = "Public IP (WAN):",
                AutoSize = true,
                Location = new Point(Pad, y + 4),
            };
            NetworkPanel.Controls.Add(pubLabel);

            PublicIpTextBox = new TextBox
            {
                Width = 240,
                Location = new Point(Pad + 130, y),
                Font = new Font("Consolas", 10F),
            };
            PublicIpTextBox.TextChanged += (s, e) => { if (ManualNetwork) PublicIp = PublicIpTextBox.Text.Trim(); };
            NetworkPanel.Controls.Add(PublicIpTextBox);
            y += 32;

            var privLabel = new Label
            {
                Text = "Private IP (LAN):",
                AutoSize = true,
                Location = new Point(Pad, y + 4),
            };
            NetworkPanel.Controls.Add(privLabel);

            PrivateIpTextBox = new TextBox
            {
                Width = 240,
                Location = new Point(Pad + 130, y),
                Font = new Font("Consolas", 10F),
            };
            PrivateIpTextBox.TextChanged += (s, e) => { if (ManualNetwork) PrivateIp = PrivateIpTextBox.Text.Trim(); };
            NetworkPanel.Controls.Add(PrivateIpTextBox);
            y += 36;

            RedetectButton = new Button
            {
                Text = "Re-detect",
                Width = 110, Height = 28,
                Location = new Point(Pad + 130, y),
            };
            RedetectButton.Click += async (s, e) => await DetectIps();
            NetworkPanel.Controls.Add(RedetectButton);
            y += 40;

            NetworkStatusLabel = new Label
            {
                Text = "",
                AutoSize = true,
                Location = new Point(Pad, y),
                ForeColor = Color.DimGray,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            };
            NetworkPanel.Controls.Add(NetworkStatusLabel);
        }

        private void UpdateNetworkUi()
        {
            PublicIpTextBox.ReadOnly = !ManualNetwork;
            PrivateIpTextBox.ReadOnly = !ManualNetwork;
            PublicIpTextBox.BackColor = ManualNetwork ? SystemColors.Window : SystemColors.Control;
            PrivateIpTextBox.BackColor = ManualNetwork ? SystemColors.Window : SystemColors.Control;
            RedetectButton.Enabled = !ManualNetwork;
        }

        // ---------------- Settings ----------------

        private void BuildSettingsPage()
        {
            int y = Pad;

            void AddRow(string label, Control control)
            {
                var l = new Label { Text = label, AutoSize = true, Location = new Point(Pad, y + 4) };
                SettingsPanel.Controls.Add(l);
                control.Location = new Point(Pad + 140, y);
                control.Width = FormWidth - Pad * 2 - 140;
                SettingsPanel.Controls.Add(control);
                y += 34;
            }

            NameTextBox = new TextBox { Font = new Font("Segoe UI", 10F) };
            NameTextBox.TextChanged += (s, e) => ServerName = NameTextBox.Text;
            AddRow("Server Name:", NameTextBox);

            DescriptionTextBox = new TextBox { Font = new Font("Segoe UI", 10F) };
            DescriptionTextBox.TextChanged += (s, e) => ServerDescription = DescriptionTextBox.Text;
            AddRow("Description:", DescriptionTextBox);

            PasswordTextBox = new TextBox { Font = new Font("Segoe UI", 10F), UseSystemPasswordChar = false };
            PasswordTextBox.TextChanged += (s, e) => Password = PasswordTextBox.Text;
            AddRow("Password (optional):", PasswordTextBox);

            AdvertiseCheckBox = new CheckBox
            {
                Text = "List my server publicly on the master server",
                AutoSize = true,
                Location = new Point(Pad, y),
                Checked = true,
                Font = new Font("Segoe UI", 9.5F),
            };
            AdvertiseCheckBox.CheckedChanged += (s, e) => Advertise = AdvertiseCheckBox.Checked;
            SettingsPanel.Controls.Add(AdvertiseCheckBox);
            y += 28;

            var note = new Label
            {
                Text = "Uncheck if you only want to play with friends who know your IP\n" +
                       "and password (the server stays reachable, just hidden from the list).",
                AutoSize = true,
                Location = new Point(Pad + 24, y),
                ForeColor = Color.DimGray,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Italic),
            };
            SettingsPanel.Controls.Add(note);
        }

        // ---------------- Done ----------------

        private void BuildDonePage()
        {
            int y = Pad;

            var bigLabel = new Label
            {
                Text = "Setup complete!",
                Font = new Font("Segoe UI Semibold", 14F),
                AutoSize = true,
                Location = new Point(Pad, y),
                ForeColor = Color.FromArgb(60, 110, 60),
            };
            DonePanel.Controls.Add(bigLabel);
            y += 40;

            DoneSummaryLabel = new Label
            {
                AutoSize = true,
                Location = new Point(Pad, y),
                Font = new Font("Segoe UI", 9.5F),
                Text = "",
            };
            DonePanel.Controls.Add(DoneSummaryLabel);
            y += 130;

            StartServerButton = new Button
            {
                Text = "Start Server",
                Width = 180, Height = 36,
                Location = new Point(Pad, y),
                Font = new Font("Segoe UI Semibold", 10F),
            };
            StartServerButton.Click += (s, e) => StartLocalServer();
            DonePanel.Controls.Add(StartServerButton);

            ServerStatusLabel = new Label
            {
                AutoSize = true,
                Location = new Point(Pad + 200, y + 8),
                Font = new Font("Segoe UI Semibold", 10F),
                Text = "",
            };
            DonePanel.Controls.Add(ServerStatusLabel);
        }

        // ============================================================
        //  Page navigation / lifecycle
        // ============================================================

        private void ShowPage(Page page)
        {
            Current = page;

            WelcomePanel.Visible  = page == Page.Welcome;
            DownloadPanel.Visible = page == Page.Download;
            FirewallPanel.Visible = page == Page.Firewall;
            NetworkPanel.Visible  = page == Page.Network;
            SettingsPanel.Visible = page == Page.Settings;
            DonePanel.Visible     = page == Page.Done;

            BackButton.Enabled = page != Page.Welcome;
            NextButton.Text = page == Page.Done ? "Finish" : "Next >";

            switch (page)
            {
                case Page.Welcome:
                    TitleLabel.Text = "Welcome";
                    StepLabel.Text = "Step 1 of 6";
                    GameTypeCombo.SelectedItem = GameType;
                    break;
                case Page.Download:
                    TitleLabel.Text = "Download Server";
                    StepLabel.Text = "Step 2 of 6";
                    OnEnterDownloadPage();
                    break;
                case Page.Firewall:
                    TitleLabel.Text = "Windows Firewall";
                    StepLabel.Text = "Step 3 of 6";
                    OnEnterFirewallPage();
                    break;
                case Page.Network:
                    TitleLabel.Text = "Network Configuration";
                    StepLabel.Text = "Step 4 of 6";
                    OnEnterNetworkPage();
                    break;
                case Page.Settings:
                    TitleLabel.Text = "Server Settings";
                    StepLabel.Text = "Step 5 of 6";
                    OnEnterSettingsPage();
                    break;
                case Page.Done:
                    TitleLabel.Text = "Finish";
                    StepLabel.Text = "Step 6 of 6";
                    OnEnterDonePage();
                    break;
            }
        }

        private void OnNextClicked(object sender, EventArgs e)
        {
            switch (Current)
            {
                case Page.Welcome:
                    ShowPage(Page.Download);
                    break;
                case Page.Download:
                    if (!DownloadComplete && !LocalServerPaths.ServerInstalled)
                    {
                        MessageBox.Show("Please download the server first, or click 'Skip' if it's already installed.",
                            "Download required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    ShowPage(Page.Firewall);
                    break;
                case Page.Firewall:
                    ShowPage(Page.Network);
                    break;
                case Page.Network:
                    if (!ValidateNetwork()) return;
                    ShowPage(Page.Settings);
                    break;
                case Page.Settings:
                    if (!ApplyConfig()) return;
                    ShowPage(Page.Done);
                    break;
                case Page.Done:
                    DialogResult = DialogResult.OK;
                    Close();
                    break;
            }
        }

        private void OnBackClicked(object sender, EventArgs e)
        {
            switch (Current)
            {
                case Page.Download: ShowPage(Page.Welcome); break;
                case Page.Firewall: ShowPage(Page.Download); break;
                case Page.Network:  ShowPage(Page.Firewall); break;
                case Page.Settings: ShowPage(Page.Network); break;
                case Page.Done:     ShowPage(Page.Settings); break;
            }
        }

        // ============================================================
        //  Page entry hooks
        // ============================================================

        private void OnEnterDownloadPage()
        {
            // Already installed?
            if (LocalServerPaths.ServerInstalled)
            {
                DownloadStatusLabel.Text = "Server is already installed. You can re-download to update, or skip ahead.";
                DownloadProgress.Value = 100;
                DownloadDetailLabel.Text = "Installed at: " + LocalServerPaths.ServerDirectory;
                DownloadButton.Text = "Re-download Latest";
                DownloadButton.Enabled = true;
                DownloadComplete = true; // allows Next without forcing a download
                return;
            }

            DownloadStatusLabel.Text = "The server has not been installed yet. Click Download to fetch the latest build (~115 MB).";
            DownloadProgress.Value = 0;
            DownloadDetailLabel.Text = "";
            DownloadButton.Text = "Download && Install";
            DownloadButton.Enabled = true;
            DownloadComplete = false;
        }

        private async Task StartDownload()
        {
            DownloadButton.Enabled = false;
            BackButton.Enabled = false;
            NextButton.Enabled = false;
            CancelBtn.Enabled = false;

            try
            {
                // Stop server if running so we can overwrite files.
                LocalServerProcess.Stop();

                DownloadStatusLabel.Text = "Resolving latest release from " + ReleaseRepo + "…";
                DownloadDetailLabel.Text = "";
                DownloadProgress.Value = 0;

                var dl = new ReleaseDownloader(ReleaseRepo, "windows.zip");
                ReleaseDownloader.ReleaseInfo info = null;
                await Task.Run(() => info = dl.QueryLatest());

                if (info == null || string.IsNullOrEmpty(info.AssetUrl))
                {
                    DownloadStatusLabel.Text = "Could not resolve the latest release. Check your connection or that " +
                                               ReleaseRepo + " has a public release with windows.zip.";
                    DownloadButton.Enabled = true;
                    BackButton.Enabled = true;
                    NextButton.Enabled = true;
                    CancelBtn.Enabled = true;
                    return;
                }

                ResolvedReleaseTag = info.TagName ?? "";
                DownloadStatusLabel.Text = "Downloading release " + ResolvedReleaseTag + "…";

                var zipPath = Path.Combine(LocalServerPaths.InstallRoot, "_release.zip");

                DownloadCts = new CancellationTokenSource();
                bool ok = await dl.DownloadAsync(info.AssetUrl, zipPath, (pct, recv, total) =>
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        DownloadProgress.Value = Math.Max(0, Math.Min(100, pct));
                        DownloadDetailLabel.Text = $"{recv / (1024 * 1024)} / {total / (1024 * 1024)} MB";
                    });
                }, DownloadCts.Token);

                if (!ok)
                {
                    DownloadStatusLabel.Text = "Download failed.";
                    DownloadButton.Enabled = true;
                    BackButton.Enabled = true;
                    NextButton.Enabled = true;
                    CancelBtn.Enabled = true;
                    return;
                }

                DownloadStatusLabel.Text = "Extracting…";
                DownloadDetailLabel.Text = "";
                DownloadProgress.Style = ProgressBarStyle.Marquee;
                bool extracted = await Task.Run(() => ReleaseDownloader.Extract(zipPath, LocalServerPaths.InstallRoot));
                DownloadProgress.Style = ProgressBarStyle.Continuous;
                DownloadProgress.Value = 100;

                try { File.Delete(zipPath); } catch { }

                if (!extracted)
                {
                    DownloadStatusLabel.Text = "Extraction failed.";
                    DownloadButton.Enabled = true;
                }
                else
                {
                    DownloadStatusLabel.Text = "Done — release " + ResolvedReleaseTag + " installed.";
                    DownloadDetailLabel.Text = "Installed at: " + LocalServerPaths.ServerDirectory;
                    DownloadComplete = true;
                    DownloadButton.Text = "Re-download Latest";
                    DownloadButton.Enabled = true;
                }
            }
            catch (Exception ex)
            {
                DownloadStatusLabel.Text = "Error: " + ex.Message;
                DownloadButton.Enabled = true;
            }
            finally
            {
                BackButton.Enabled = true;
                NextButton.Enabled = true;
                CancelBtn.Enabled = true;
            }
        }

        // ---------------- Firewall ----------------

        private void OnEnterFirewallPage()
        {
            RefreshFirewallStatus();
        }

        private void RefreshFirewallStatus()
        {
            bool installed = FirewallManager.AllRulesInstalled();
            FirewallReady = installed;
            FirewallStatusLabel.Text = installed
                ? "Status: ✅ Firewall rules are installed."
                : "Status: ⚠️ Firewall rules are not installed yet.";
            FirewallStatusLabel.ForeColor = installed
                ? Color.FromArgb(60, 110, 60)
                : Color.DarkOrange;
            ApplyFirewallButton.Text = installed ? "Reapply Rules" : "Apply Firewall Rules";
        }

        private async Task ApplyFirewall()
        {
            ApplyFirewallButton.Enabled = false;
            FirewallStatusLabel.Text = "Status: applying rules… Approve the UAC prompt.";
            FirewallStatusLabel.ForeColor = Color.DimGray;

            string serverExe = LocalServerPaths.ServerExecutable;
            string loaderExe = Application.ExecutablePath;

            bool ok = await Task.Run(() => FirewallManager.ApplyRulesElevated(serverExe, loaderExe));
            if (!ok)
            {
                FirewallStatusLabel.Text = "Status: ❌ Failed (UAC denied or netsh error).";
                FirewallStatusLabel.ForeColor = Color.IndianRed;
                ApplyFirewallButton.Enabled = true;
                return;
            }

            RefreshFirewallStatus();
            ApplyFirewallButton.Enabled = true;
        }

        // ---------------- Network ----------------

        private void OnEnterNetworkPage()
        {
            UpdateNetworkUi();
            if (!ManualNetwork && string.IsNullOrEmpty(PublicIp))
            {
                _ = DetectIps();
            }
            else
            {
                PublicIpTextBox.Text = PublicIp;
                PrivateIpTextBox.Text = PrivateIp;
            }
        }

        private async Task DetectIps()
        {
            NetworkStatusLabel.Text = "Detecting…";
            RedetectButton.Enabled = false;

            string wan = "";
            string lan = "";
            await Task.Run(() =>
            {
                wan = NetUtils.GetMachineIPv4(true);
                lan = NetUtils.GetMachineIPv4(false);
            });

            PublicIp = wan ?? "";
            PrivateIp = lan ?? "";
            PublicIpTextBox.Text = PublicIp;
            PrivateIpTextBox.Text = PrivateIp;

            if (string.IsNullOrEmpty(PublicIp) || string.IsNullOrEmpty(PrivateIp))
            {
                NetworkStatusLabel.Text = "Could not detect one or both IPs. Switch to Manual override and enter them.";
                NetworkStatusLabel.ForeColor = Color.DarkOrange;
            }
            else
            {
                NetworkStatusLabel.Text = "Detected automatically.";
                NetworkStatusLabel.ForeColor = Color.DimGray;
            }
            RedetectButton.Enabled = !ManualNetwork;
        }

        private bool ValidateNetwork()
        {
            if (string.IsNullOrWhiteSpace(PublicIp) || string.IsNullOrWhiteSpace(PrivateIp))
            {
                MessageBox.Show("Both Public and Private IPs are required.",
                    "Missing IP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (!IPAddress.TryParse(PublicIp, out _) || !IPAddress.TryParse(PrivateIp, out _))
            {
                MessageBox.Show("One of the IPs has an invalid format.",
                    "Invalid IP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }

        // ---------------- Settings ----------------

        private void OnEnterSettingsPage()
        {
            NameTextBox.Text = ServerName;
            DescriptionTextBox.Text = ServerDescription;
            PasswordTextBox.Text = Password;
            AdvertiseCheckBox.Checked = Advertise;
        }

        // ---------------- Done ----------------

        private void OnEnterDonePage()
        {
            DoneSummaryLabel.Text =
                "Game type:    " + GameType + "\n" +
                "Server name:  " + ServerName + "\n" +
                "Public IP:    " + PublicIp + "\n" +
                "Private IP:   " + PrivateIp + "\n" +
                "Advertise:    " + (Advertise ? "Yes (visible in master list)" : "No (hidden)") + "\n" +
                "Password:     " + (string.IsNullOrEmpty(Password) ? "(none)" : "***") + "\n" +
                "Release:      " + (string.IsNullOrEmpty(ResolvedReleaseTag) ? "(existing install)" : ResolvedReleaseTag);

            UpdateServerStatusLabel();
        }

        private void UpdateServerStatusLabel()
        {
            var st = LocalServerProcess.QueryStatus();
            if (st.Running)
            {
                ServerStatusLabel.Text = "🟢 Running (PID " + st.Pid + ")";
                ServerStatusLabel.ForeColor = Color.FromArgb(40, 130, 40);
                StartServerButton.Text = "Stop Server";
            }
            else
            {
                ServerStatusLabel.Text = "🔴 Not running";
                ServerStatusLabel.ForeColor = Color.IndianRed;
                StartServerButton.Text = "Start Server";
            }
        }

        private void StartLocalServer()
        {
            var st = LocalServerProcess.QueryStatus();
            if (st.Running)
            {
                LocalServerProcess.Stop();
            }
            else
            {
                if (!LocalServerProcess.Start(out string err))
                {
                    MessageBox.Show("Could not start server:\n\n" + err,
                        "Start failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            UpdateServerStatusLabel();
        }

        // ============================================================
        //  Apply user inputs to disk
        // ============================================================

        private void LoadExistingConfig()
        {
            if (!LocalServerPaths.ConfigExists) return;
            var cfg = LocalServerConfig.Load(LocalServerPaths.ConfigFile);
            ServerName = cfg.ServerName;
            ServerDescription = cfg.ServerDescription;
            Password = cfg.Password;
            GameType = cfg.GameType;
            Advertise = cfg.Advertise;
            PublicIp = cfg.ServerHostname;
            PrivateIp = cfg.ServerPrivateHostname;
        }

        /// <summary>
        /// Writes the wizard's in-memory settings into Server\Saved\default\config.json.
        /// If the file doesn't exist yet, runs Server.exe once to generate the default
        /// (then patches it). Returns true on success.
        /// </summary>
        private bool ApplyConfig()
        {
            if (!LocalServerPaths.ServerInstalled)
            {
                MessageBox.Show("Server is not installed. Go back to Step 2 and download it first.",
                    "Server missing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            // Make sure config.json exists. If not, run Server.exe briefly
            // so it generates a default — then immediately stop it.
            if (!LocalServerPaths.ConfigExists)
            {
                var ok = LocalServerProcess.Start(out string err);
                if (!ok)
                {
                    MessageBox.Show("Could not generate default config:\n\n" + err,
                        "Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
                // Wait briefly for the default to be written.
                for (int i = 0; i < 20 && !LocalServerPaths.ConfigExists; i++)
                {
                    Thread.Sleep(150);
                }
                LocalServerProcess.Stop();
            }

            var cfg = new LocalServerConfig
            {
                ServerName = ServerName,
                ServerDescription = ServerDescription,
                Password = Password,
                GameType = GameType,
                ServerHostname = PublicIp,
                ServerPrivateHostname = PrivateIp,
                Advertise = Advertise,
            };
            if (!cfg.SaveOver(LocalServerPaths.ConfigFile))
            {
                MessageBox.Show("Could not write config.json at " + LocalServerPaths.ConfigFile,
                    "Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            return true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Don't allow closing mid-download — cancel it cleanly first.
            try { DownloadCts?.Cancel(); } catch { }
            base.OnFormClosing(e);
        }
    }
}
