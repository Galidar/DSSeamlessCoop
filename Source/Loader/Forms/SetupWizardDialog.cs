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
 * Visual style intentionally matches the main Loader (dark masthead, white
 * body, firekeeper-amber accents). The page state machine and the layout are
 * both code-only — easier to keep aligned across DPI scales than the WinForms
 * Designer.
 */

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        // ============================================================
        //  Theme
        // ============================================================
        private static readonly Color HeaderBg     = Color.FromArgb(20, 20, 20);
        private static readonly Color HeaderText   = Color.White;
        private static readonly Color BodyBg       = Color.White;
        private static readonly Color FooterBg     = Color.FromArgb(245, 244, 240);
        private static readonly Color BorderLine   = Color.FromArgb(220, 218, 210);
        private static readonly Color TextPrimary  = Color.FromArgb(35, 32, 28);
        private static readonly Color TextSubtle   = Color.FromArgb(110, 105, 95);
        private static readonly Color Accent       = Color.FromArgb(200, 134, 47); // firekeeper amber
        private static readonly Color AccentHover  = Color.FromArgb(225, 152, 60);
        private static readonly Color AccentText   = Color.White;
        private static readonly Color OkGreen      = Color.FromArgb(46, 130, 60);
        private static readonly Color WarnAmber    = Color.FromArgb(190, 120, 30);
        private static readonly Color ErrRed       = Color.FromArgb(180, 60, 60);
        private static readonly Color StepActive   = Accent;
        private static readonly Color StepDone     = Color.FromArgb(60, 60, 60);
        private static readonly Color StepFuture   = Color.FromArgb(160, 160, 160);

        // ============================================================
        //  Layout
        // ============================================================
        private const int FormW = 820;
        private const int FormH = 620;
        private const int HeaderH = 140;
        private const int FooterH = 72;
        private const int PadX = 36;
        private const int PadY = 28;

        // ============================================================
        //  State
        // ============================================================
        private enum Page { Welcome, Download, Firewall, Network, Settings, Done }
        private Page Current = Page.Welcome;
        private static readonly string[] StepTitles =
        {
            "Welcome", "Download", "Firewall", "Network", "Settings", "Finish",
        };

        // GitHub repo to fetch releases from. The fork.
        private const string ReleaseRepo = "Galidar/DSSeamlessCoop";

        // ----- top-level controls -----
        private Panel HeaderPanel, BodyPanel, FooterPanel;
        private Label TitleLabel;
        private StepIndicator Stepper;
        private FlatButton NextButton, BackButton, CancelBtn;

        // ----- page panels -----
        private Panel WelcomePanel, DownloadPanel, FirewallPanel, NetworkPanel, SettingsPanel, DonePanel;

        // ----- welcome -----
        private ComboBox GameTypeCombo;

        // ----- download -----
        private Label DownloadStatusLabel;
        private ProgressBar DownloadProgress;
        private Label DownloadDetailLabel;
        private FlatButton DownloadButton;
        private bool DownloadComplete;
        private string ResolvedReleaseTag = "";
        private CancellationTokenSource DownloadCts;

        // ----- firewall -----
        private Label FirewallStatusLabel;
        private FlatButton ApplyFirewallButton;
        private LinkLabel SkipFirewallLink;
        private bool FirewallReady;

        // ----- network -----
        private RadioButton AutoDetectRadio, ManualRadio;
        private TextBox PublicIpTextBox, PrivateIpTextBox;
        private Label NetworkStatusLabel;
        private FlatButton RedetectButton;

        // ----- settings -----
        private TextBox NameTextBox, DescriptionTextBox, PasswordTextBox;
        private CheckBox AdvertiseCheckBox;

        // ----- done -----
        private Label DoneSummaryLabel;
        private FlatButton StartServerButton;
        private Label ServerStatusLabel;

        // ----- model (in-memory until applied) -----
        private string GameType = "DarkSouls2";
        private string PublicIp = "", PrivateIp = "";
        private bool ManualNetwork = false;
        private string ServerName = "My DS3OS Server";
        private string ServerDescription = "A custom Dark Souls server.";
        private string Password = "";
        private bool Advertise = true;

        // ============================================================
        //  Construction
        // ============================================================
        public SetupWizardDialog()
        {
            BuildUi();
            LoadExistingConfig();
            ShowPage(Page.Welcome);
        }

        private void BuildUi()
        {
            Text = "DSSeamlessCoop — Server Setup";
            ClientSize = new Size(FormW, FormH);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9.5F);
            BackColor = BodyBg;
            DoubleBuffered = true;
            // Per-monitor DPI: scale based on font, which propagates correctly
            // to child controls regardless of the user's display scaling.
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            KeyPreview = true;

            BuildHeader();
            BuildFooter();
            BuildBody();

            // Z-order: footer & header drawn over body. Body fills remaining.
            Controls.Add(BodyPanel);
            Controls.Add(HeaderPanel);
            Controls.Add(FooterPanel);
        }

        private void BuildHeader()
        {
            HeaderPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = HeaderH,
                BackColor = HeaderBg,
            };

            // Title row — single line. 48px tall ensures descenders ("g")
            // are never clipped at any DPI scale, with breathing room above.
            TitleLabel = new Label
            {
                Text = "Welcome",
                Font = new Font("Segoe UI Semibold", 18F),
                ForeColor = HeaderText,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Bounds = new Rectangle(PadX, 14, FormW - PadX * 2, 48),
                BackColor = HeaderBg,
                UseCompatibleTextRendering = false,
            };

            // Stepper occupies the lower half of the header. 70px is enough
            // for 24px circles, the connecting line, and the labels below.
            Stepper = new StepIndicator(StepTitles)
            {
                Bounds = new Rectangle(PadX, 66, FormW - PadX * 2, 68),
                BackColor = HeaderBg,
                ActiveColor = StepActive,
                DoneColor = StepDone,
                FutureColor = StepFuture,
                LabelColor = Color.FromArgb(190, 188, 180),
                ActiveLabelColor = Color.White,
            };

            HeaderPanel.Controls.Add(TitleLabel);
            HeaderPanel.Controls.Add(Stepper);

            // 1px hairline at the bottom of the header for separation.
            var hairline = new Panel
            {
                BackColor = Color.FromArgb(40, 40, 40),
                Height = 1,
                Dock = DockStyle.Bottom,
            };
            HeaderPanel.Controls.Add(hairline);

        }

        private void BuildFooter()
        {
            FooterPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = FooterH,
                BackColor = FooterBg,
            };

            // 1px hairline at the top of the footer.
            var hairline = new Panel
            {
                BackColor = BorderLine,
                Height = 1,
                Dock = DockStyle.Top,
            };
            FooterPanel.Controls.Add(hairline);

            CancelBtn = new FlatButton("Cancel")
            {
                Bounds = new Rectangle(PadX, 18, 100, 36),
                Style = FlatButton.ButtonStyle.Secondary,
            };
            CancelBtn.Clicked += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            BackButton = new FlatButton("Back")
            {
                Bounds = new Rectangle(FormW - PadX - 100 - 8 - 130, 18, 100, 36),
                Style = FlatButton.ButtonStyle.Secondary,
            };
            BackButton.Clicked += OnBackClicked;

            NextButton = new FlatButton("Next")
            {
                Bounds = new Rectangle(FormW - PadX - 130, 18, 130, 36),
                Style = FlatButton.ButtonStyle.Primary,
            };
            NextButton.Clicked += OnNextClicked;

            FooterPanel.Controls.Add(CancelBtn);
            FooterPanel.Controls.Add(BackButton);
            FooterPanel.Controls.Add(NextButton);
        }

        private void BuildBody()
        {
            BodyPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = BodyBg,
                Padding = new Padding(0),
            };

            int innerW = FormW - PadX * 2;
            // Bounds inside BodyPanel — body height computed at runtime.
            int innerH = FormH - HeaderH - FooterH;
            var bounds = new Rectangle(PadX, PadY, innerW, innerH - PadY * 2);

            WelcomePanel  = MakePagePanel(bounds);
            DownloadPanel = MakePagePanel(bounds);
            FirewallPanel = MakePagePanel(bounds);
            NetworkPanel  = MakePagePanel(bounds);
            SettingsPanel = MakePagePanel(bounds);
            DonePanel     = MakePagePanel(bounds);

            BuildWelcomePage();
            BuildDownloadPage();
            BuildFirewallPage();
            BuildNetworkPage();
            BuildSettingsPage();
            BuildDonePage();

            BodyPanel.Controls.Add(WelcomePanel);
            BodyPanel.Controls.Add(DownloadPanel);
            BodyPanel.Controls.Add(FirewallPanel);
            BodyPanel.Controls.Add(NetworkPanel);
            BodyPanel.Controls.Add(SettingsPanel);
            BodyPanel.Controls.Add(DonePanel);
        }

        private Panel MakePagePanel(Rectangle bounds)
        {
            return new Panel
            {
                Bounds = bounds,
                BackColor = BodyBg,
                Visible = false,
            };
        }

        // ============================================================
        //  Page builders
        // ============================================================

        private void BuildWelcomePage()
        {
            int y = 0;

            var heading = MakeHeading("Let's set up your server");
            heading.Location = new Point(0, y);
            WelcomePanel.Controls.Add(heading);
            y += heading.Height + 8;

            var body = MakeBody(
                "This wizard installs the latest server build, configures the\n" +
                "Windows Firewall, sets up your network (auto-detected by default,\n" +
                "or manual override for paid hosting / VPN), and starts the server\n" +
                "for you.\n\n" +
                "It takes about a minute. Click Next to begin.");
            body.Location = new Point(0, y);
            WelcomePanel.Controls.Add(body);
            y += body.Height + 28;

            var gameLabel = MakeLabel("Game type", semibold: true);
            gameLabel.Location = new Point(0, y + 5);
            WelcomePanel.Controls.Add(gameLabel);

            GameTypeCombo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Bounds = new Rectangle(110, y, 260, 28),
                Font = new Font("Segoe UI", 10F),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                TabStop = false, // don't grab focus on form load
            };
            GameTypeCombo.Items.AddRange(new object[] { "DarkSouls2", "DarkSouls3" });
            GameTypeCombo.SelectedIndexChanged += (s, e) => GameType = (string)GameTypeCombo.SelectedItem;
            WelcomePanel.Controls.Add(GameTypeCombo);
            y += 40;

            var note = MakeNote(
                "Dark Souls II SOTFS is the focus of this fork — the bundled config is\n" +
                "preconfigured for it. You can change the game type later in Settings.");
            note.Location = new Point(0, y);
            WelcomePanel.Controls.Add(note);
        }

        private void BuildDownloadPage()
        {
            int y = 0;

            var heading = MakeHeading("Download the server build");
            heading.Location = new Point(0, y);
            DownloadPanel.Controls.Add(heading);
            y += heading.Height + 8;

            // Status label — auto-sizes vertically based on text. We keep a
            // reference to its initial Y; the rest of the page is positioned
            // dynamically in LayoutDownloadPage() because the status text
            // changes between 1 and 2 lines depending on install state.
            DownloadStatusLabel = MakeBody("Checking for the latest release…");
            DownloadStatusLabel.Location = new Point(0, y);
            DownloadStatusLabel.MaximumSize = new Size(DownloadPanel.Width, 0);
            DownloadPanel.Controls.Add(DownloadStatusLabel);

            DownloadProgress = new ProgressBar
            {
                Width = DownloadPanel.Width,
                Height = 22,
                Style = ProgressBarStyle.Continuous,
                Minimum = 0, Maximum = 100, Value = 0,
            };
            DownloadPanel.Controls.Add(DownloadProgress);

            DownloadDetailLabel = MakeNote("");
            DownloadDetailLabel.MaximumSize = new Size(DownloadPanel.Width, 0);
            DownloadPanel.Controls.Add(DownloadDetailLabel);

            DownloadButton = new FlatButton("Download && Install")
            {
                Size = new Size(220, 36),
                Style = FlatButton.ButtonStyle.Secondary,
            };
            DownloadButton.Clicked += async (s, e) => await StartDownload();
            DownloadPanel.Controls.Add(DownloadButton);

            LayoutDownloadPage();
        }

        // Reposition every control under the status label according to the
        // status label's current actual height. Called after construction
        // and any time DownloadStatusLabel.Text changes.
        private void LayoutDownloadPage()
        {
            int y = DownloadStatusLabel.Bottom + 14;
            DownloadProgress.Location = new Point(0, y);
            y += DownloadProgress.Height + 10;

            DownloadDetailLabel.Location = new Point(0, y);
            // detail label is autosize — measure after positioning
            y += Math.Max(DownloadDetailLabel.Height, 18) + 14;

            DownloadButton.Location = new Point(0, y);
        }

        private void SetDownloadStatus(string text, Color? color = null)
        {
            DownloadStatusLabel.Text = text;
            if (color.HasValue) DownloadStatusLabel.ForeColor = color.Value;
            // AutoSize updates Height synchronously when Text changes,
            // so subsequent controls can be repositioned right away.
            LayoutDownloadPage();
        }

        private void BuildFirewallPage()
        {
            int y = 0;

            var heading = MakeHeading("Configure Windows Firewall");
            heading.Location = new Point(0, y);
            FirewallPanel.Controls.Add(heading);
            y += heading.Height + 8;

            var body = MakeBody(
                "We'll create the Windows Firewall rules so other players can reach your\n" +
                "server. This requires administrator privileges — Windows will show a UAC\n" +
                "prompt; click Yes when it appears.");
            body.Location = new Point(0, y);
            FirewallPanel.Controls.Add(body);
            y += body.Height + 24;

            FirewallStatusLabel = MakeLabel("Status: checking…", semibold: true);
            FirewallStatusLabel.Location = new Point(0, y);
            FirewallStatusLabel.Width = WelcomePanel.Width;
            FirewallPanel.Controls.Add(FirewallStatusLabel);
            y += 36;

            ApplyFirewallButton = new FlatButton("Apply Firewall Rules")
            {
                Bounds = new Rectangle(0, y, 220, 38),
                Style = FlatButton.ButtonStyle.Secondary,
            };
            ApplyFirewallButton.Clicked += async (s, e) => await ApplyFirewall();
            FirewallPanel.Controls.Add(ApplyFirewallButton);
            y += 56;

            SkipFirewallLink = new LinkLabel
            {
                Text = "Skip firewall step (use if rules already exist)",
                AutoSize = true,
                Location = new Point(0, y),
                Font = new Font("Segoe UI", 9F),
                LinkColor = Accent,
                ActiveLinkColor = AccentHover,
                LinkBehavior = LinkBehavior.HoverUnderline,
            };
            SkipFirewallLink.LinkClicked += (s, e) =>
            {
                FirewallReady = true;
                ShowPage(Page.Network);
            };
            FirewallPanel.Controls.Add(SkipFirewallLink);
        }

        private void BuildNetworkPage()
        {
            int y = 0;

            var heading = MakeHeading("Configure your network");
            heading.Location = new Point(0, y);
            NetworkPanel.Controls.Add(heading);
            y += heading.Height + 8;

            var body = MakeBody(
                "We need your public (WAN) IP and your local (LAN) IP. We can detect\n" +
                "them automatically; switch to Manual override only if you're hosting\n" +
                "on a paid server or behind a VPN.");
            body.Location = new Point(0, y);
            NetworkPanel.Controls.Add(body);
            y += body.Height + 18;

            AutoDetectRadio = new RadioButton
            {
                Text = "Auto-detect IPs (recommended)",
                AutoSize = true,
                Checked = true,
                Location = new Point(0, y),
                Font = new Font("Segoe UI", 10F),
                ForeColor = TextPrimary,
                BackColor = BodyBg,
            };
            AutoDetectRadio.CheckedChanged += (s, e) =>
            {
                if (AutoDetectRadio.Checked) { ManualNetwork = false; UpdateNetworkUi(); }
            };
            NetworkPanel.Controls.Add(AutoDetectRadio);
            y += 26;

            ManualRadio = new RadioButton
            {
                Text = "Manual override (paid hosting / VPN)",
                AutoSize = true,
                Location = new Point(0, y),
                Font = new Font("Segoe UI", 10F),
                ForeColor = TextPrimary,
                BackColor = BodyBg,
            };
            ManualRadio.CheckedChanged += (s, e) =>
            {
                if (ManualRadio.Checked) { ManualNetwork = true; UpdateNetworkUi(); }
            };
            NetworkPanel.Controls.Add(ManualRadio);
            y += 36;

            var pubLabel = MakeLabel("Public IP (WAN)", semibold: true);
            pubLabel.Location = new Point(0, y + 6);
            NetworkPanel.Controls.Add(pubLabel);

            PublicIpTextBox = new TextBox
            {
                Bounds = new Rectangle(160, y, 320, 28),
                Font = new Font("Consolas", 11F),
                BorderStyle = BorderStyle.FixedSingle,
            };
            PublicIpTextBox.TextChanged += (s, e) =>
            {
                if (ManualNetwork) PublicIp = PublicIpTextBox.Text.Trim();
            };
            NetworkPanel.Controls.Add(PublicIpTextBox);
            y += 36;

            var privLabel = MakeLabel("Private IP (LAN)", semibold: true);
            privLabel.Location = new Point(0, y + 6);
            NetworkPanel.Controls.Add(privLabel);

            PrivateIpTextBox = new TextBox
            {
                Bounds = new Rectangle(160, y, 320, 28),
                Font = new Font("Consolas", 11F),
                BorderStyle = BorderStyle.FixedSingle,
            };
            PrivateIpTextBox.TextChanged += (s, e) =>
            {
                if (ManualNetwork) PrivateIp = PrivateIpTextBox.Text.Trim();
            };
            NetworkPanel.Controls.Add(PrivateIpTextBox);
            y += 40;

            RedetectButton = new FlatButton("Re-detect")
            {
                Bounds = new Rectangle(160, y, 120, 32),
                Style = FlatButton.ButtonStyle.Secondary,
            };
            RedetectButton.Clicked += async (s, e) => await DetectIps();
            NetworkPanel.Controls.Add(RedetectButton);
            y += 42;

            NetworkStatusLabel = MakeNote("");
            NetworkStatusLabel.Location = new Point(0, y);
            NetworkStatusLabel.Width = WelcomePanel.Width;
            NetworkPanel.Controls.Add(NetworkStatusLabel);
        }

        private void BuildSettingsPage()
        {
            int y = 0;

            var heading = MakeHeading("Server settings");
            heading.Location = new Point(0, y);
            SettingsPanel.Controls.Add(heading);
            y += heading.Height + 8;

            var body = MakeBody(
                "Choose how your server identifies itself in the public list (or hide it\n" +
                "entirely and only share the IP/password with friends).");
            body.Location = new Point(0, y);
            SettingsPanel.Controls.Add(body);
            y += body.Height + 18;

            void AddRow(string label, Control control)
            {
                var l = MakeLabel(label, semibold: true);
                l.Location = new Point(0, y + 6);
                SettingsPanel.Controls.Add(l);
                control.Bounds = new Rectangle(180, y, FormW - PadX * 2 - 180, 28);
                SettingsPanel.Controls.Add(control);
                y += 38;
            }

            NameTextBox = new TextBox { Font = new Font("Segoe UI", 10F), BorderStyle = BorderStyle.FixedSingle };
            NameTextBox.TextChanged += (s, e) => ServerName = NameTextBox.Text;
            AddRow("Server Name", NameTextBox);

            DescriptionTextBox = new TextBox { Font = new Font("Segoe UI", 10F), BorderStyle = BorderStyle.FixedSingle };
            DescriptionTextBox.TextChanged += (s, e) => ServerDescription = DescriptionTextBox.Text;
            AddRow("Description", DescriptionTextBox);

            PasswordTextBox = new TextBox { Font = new Font("Segoe UI", 10F), BorderStyle = BorderStyle.FixedSingle };
            PasswordTextBox.TextChanged += (s, e) => Password = PasswordTextBox.Text;
            AddRow("Password (optional)", PasswordTextBox);

            y += 6;
            AdvertiseCheckBox = new CheckBox
            {
                Text = "List my server publicly on the master server",
                AutoSize = true,
                Location = new Point(0, y),
                Checked = true,
                Font = new Font("Segoe UI", 10F),
                ForeColor = TextPrimary,
                BackColor = BodyBg,
            };
            AdvertiseCheckBox.CheckedChanged += (s, e) => Advertise = AdvertiseCheckBox.Checked;
            SettingsPanel.Controls.Add(AdvertiseCheckBox);
            y += 26;

            var note = MakeNote(
                "Uncheck to keep the server unlisted. It stays reachable, just hidden\n" +
                "from the public master list — friends can join with your IP and password.");
            note.Location = new Point(22, y);
            SettingsPanel.Controls.Add(note);
        }

        private void BuildDonePage()
        {
            int y = 0;

            var heading = MakeHeading("Setup complete");
            heading.ForeColor = OkGreen;
            heading.Location = new Point(0, y);
            DonePanel.Controls.Add(heading);
            y += heading.Height + 8;

            var body = MakeBody(
                "Everything is configured. Click Start Server below, then close this\n" +
                "wizard and use the main Loader window to launch the game.");
            body.Location = new Point(0, y);
            DonePanel.Controls.Add(body);
            y += body.Height + 18;

            DoneSummaryLabel = new Label
            {
                AutoSize = false,
                Bounds = new Rectangle(0, y, WelcomePanel.Width, 140),
                Font = new Font("Consolas", 9.5F),
                ForeColor = TextPrimary,
                BackColor = Color.FromArgb(248, 246, 240),
                Text = "",
                Padding = new Padding(12),
                BorderStyle = BorderStyle.FixedSingle,
            };
            DonePanel.Controls.Add(DoneSummaryLabel);
            y += DoneSummaryLabel.Height + 18;

            StartServerButton = new FlatButton("Start Server")
            {
                Bounds = new Rectangle(0, y, 180, 40),
                Style = FlatButton.ButtonStyle.Primary,
            };
            StartServerButton.Clicked += (s, e) => ToggleLocalServer();
            DonePanel.Controls.Add(StartServerButton);

            ServerStatusLabel = new Label
            {
                AutoSize = true,
                Location = new Point(200, y + 10),
                Font = new Font("Segoe UI Semibold", 10F),
                Text = "",
                BackColor = BodyBg,
            };
            DonePanel.Controls.Add(ServerStatusLabel);
        }

        // ============================================================
        //  Page navigation
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
            NextButton.Text = page == Page.Done ? "Finish" : "Next";

            int idx = (int)page;
            TitleLabel.Text = StepTitles[idx];
            Stepper.SetActiveStep(idx);

            switch (page)
            {
                case Page.Welcome:  GameTypeCombo.SelectedItem = GameType; break;
                case Page.Download: OnEnterDownloadPage(); break;
                case Page.Firewall: OnEnterFirewallPage(); break;
                case Page.Network:  OnEnterNetworkPage(); break;
                case Page.Settings: OnEnterSettingsPage(); break;
                case Page.Done:     OnEnterDonePage(); break;
            }
        }

        private void OnNextClicked(object sender, EventArgs e)
        {
            switch (Current)
            {
                case Page.Welcome: ShowPage(Page.Download); break;
                case Page.Download:
                    if (!DownloadComplete && !LocalServerPaths.ServerInstalled)
                    {
                        ShowToast("Please download the server first, or skip if it's already installed.");
                        return;
                    }
                    ShowPage(Page.Firewall);
                    break;
                case Page.Firewall: ShowPage(Page.Network); break;
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
            if (LocalServerPaths.ServerInstalled)
            {
                DownloadProgress.Value = 100;
                DownloadDetailLabel.Text = "Installed at: " + LocalServerPaths.ServerDirectory;
                DownloadButton.Text = "Re-download Latest";
                DownloadButton.Enabled = true;
                DownloadComplete = true;
                SetDownloadStatus("The server is already installed. Re-download to update or skip ahead.");
                return;
            }

            DownloadProgress.Value = 0;
            DownloadDetailLabel.Text = "";
            DownloadButton.Text = "Download && Install";
            DownloadButton.Enabled = true;
            DownloadComplete = false;
            SetDownloadStatus("The server is not installed yet. Click Download to fetch the latest build (~115 MB).");
        }

        private async Task StartDownload()
        {
            DownloadButton.Enabled = false;
            BackButton.Enabled = false;
            NextButton.Enabled = false;
            CancelBtn.Enabled = false;

            try
            {
                LocalServerProcess.Stop();

                SetDownloadStatus("Resolving the latest release from " + ReleaseRepo + "…", TextPrimary);
                DownloadDetailLabel.Text = "";
                DownloadProgress.Value = 0;

                var dl = new ReleaseDownloader(ReleaseRepo, "windows.zip");
                ReleaseDownloader.ReleaseInfo info = null;
                await Task.Run(() => info = dl.QueryLatest());

                if (info == null || string.IsNullOrEmpty(info.AssetUrl))
                {
                    SetDownloadStatus(
                        "Could not resolve the latest release. Check your internet connection.",
                        ErrRed);
                    DownloadButton.Enabled = true;
                    BackButton.Enabled = true;
                    NextButton.Enabled = true;
                    CancelBtn.Enabled = true;
                    return;
                }

                ResolvedReleaseTag = info.TagName ?? "";
                SetDownloadStatus("Downloading release " + ResolvedReleaseTag + "…", TextPrimary);

                var zipPath = Path.Combine(LocalServerPaths.InstallRoot, "_release.zip");

                DownloadCts = new CancellationTokenSource();
                bool ok = await dl.DownloadAsync(info.AssetUrl, zipPath, (pct, recv, total) =>
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        DownloadProgress.Value = Math.Max(0, Math.Min(100, pct));
                        DownloadDetailLabel.Text = (recv / (1024 * 1024)) + " / " + (total / (1024 * 1024)) + " MB";
                    });
                }, DownloadCts.Token);

                if (!ok)
                {
                    SetDownloadStatus("Download failed.", ErrRed);
                    DownloadButton.Enabled = true;
                    BackButton.Enabled = true;
                    NextButton.Enabled = true;
                    CancelBtn.Enabled = true;
                    return;
                }

                SetDownloadStatus("Extracting…", TextPrimary);
                DownloadDetailLabel.Text = "";
                DownloadProgress.Style = ProgressBarStyle.Marquee;
                bool extracted = await Task.Run(() => ReleaseDownloader.Extract(zipPath, LocalServerPaths.InstallRoot));
                DownloadProgress.Style = ProgressBarStyle.Continuous;
                DownloadProgress.Value = 100;

                try { File.Delete(zipPath); } catch { }

                if (!extracted)
                {
                    SetDownloadStatus("Extraction failed.", ErrRed);
                    DownloadButton.Enabled = true;
                }
                else
                {
                    DownloadDetailLabel.Text = "Installed at: " + LocalServerPaths.ServerDirectory;
                    DownloadComplete = true;
                    DownloadButton.Text = "Re-download Latest";
                    DownloadButton.Enabled = true;
                    SetDownloadStatus("Done — release " + ResolvedReleaseTag + " installed.", OkGreen);
                }
            }
            catch (Exception ex)
            {
                SetDownloadStatus("Error: " + ex.Message, ErrRed);
                DownloadButton.Enabled = true;
            }
            finally
            {
                BackButton.Enabled = true;
                NextButton.Enabled = true;
                CancelBtn.Enabled = true;
            }
        }

        // ----- firewall -----

        private void OnEnterFirewallPage()
        {
            RefreshFirewallStatus();
        }

        private void RefreshFirewallStatus()
        {
            bool installed = FirewallManager.AllRulesInstalled();
            FirewallReady = installed;
            FirewallStatusLabel.Text = installed
                ? "Status: Firewall rules are installed."
                : "Status: Firewall rules are not installed yet.";
            FirewallStatusLabel.ForeColor = installed ? OkGreen : WarnAmber;
            ApplyFirewallButton.Text = installed ? "Reapply Rules" : "Apply Firewall Rules";
        }

        private async Task ApplyFirewall()
        {
            ApplyFirewallButton.Enabled = false;
            FirewallStatusLabel.Text = "Status: applying rules… approve the UAC prompt that appears.";
            FirewallStatusLabel.ForeColor = TextSubtle;

            string serverExe = LocalServerPaths.ServerExecutable;
            string loaderExe = Application.ExecutablePath;

            bool ok = await Task.Run(() => FirewallManager.ApplyRulesElevated(serverExe, loaderExe));
            if (!ok)
            {
                FirewallStatusLabel.Text = "Status: failed (UAC denied or netsh error).";
                FirewallStatusLabel.ForeColor = ErrRed;
                ApplyFirewallButton.Enabled = true;
                return;
            }

            RefreshFirewallStatus();
            ApplyFirewallButton.Enabled = true;
        }

        // ----- network -----

        private void OnEnterNetworkPage()
        {
            UpdateNetworkUi();
            if (!ManualNetwork && string.IsNullOrEmpty(PublicIp))
            {
                _ = DetectIps();
            }
            else
            {
                SetIpText(PublicIpTextBox, PublicIp);
                SetIpText(PrivateIpTextBox, PrivateIp);
            }
        }

        private void UpdateNetworkUi()
        {
            PublicIpTextBox.ReadOnly = !ManualNetwork;
            PrivateIpTextBox.ReadOnly = !ManualNetwork;
            PublicIpTextBox.BackColor = ManualNetwork ? Color.White : Color.FromArgb(245, 244, 240);
            PrivateIpTextBox.BackColor = ManualNetwork ? Color.White : Color.FromArgb(245, 244, 240);
            RedetectButton.Enabled = !ManualNetwork;
        }

        private async Task DetectIps()
        {
            NetworkStatusLabel.Text = "Detecting…";
            NetworkStatusLabel.ForeColor = TextSubtle;
            RedetectButton.Enabled = false;

            string wan = "", lan = "";
            await Task.Run(() =>
            {
                wan = NetUtils.GetMachineIPv4(true);
                lan = NetUtils.GetMachineIPv4(false);
            });

            PublicIp = wan ?? "";
            PrivateIp = lan ?? "";
            SetIpText(PublicIpTextBox, PublicIp);
            SetIpText(PrivateIpTextBox, PrivateIp);

            if (string.IsNullOrEmpty(PublicIp) || string.IsNullOrEmpty(PrivateIp))
            {
                NetworkStatusLabel.Text = "Could not detect one or both IPs. Switch to Manual override and enter them yourself.";
                NetworkStatusLabel.ForeColor = WarnAmber;
            }
            else
            {
                NetworkStatusLabel.Text = "Detected automatically.";
                NetworkStatusLabel.ForeColor = OkGreen;
            }
            RedetectButton.Enabled = !ManualNetwork;
        }

        // Set text in a textbox and reset cursor to start so the leading
        // characters are always visible (otherwise the textbox scrolls right).
        private static void SetIpText(TextBox tb, string value)
        {
            tb.Text = value;
            tb.SelectionStart = 0;
            tb.SelectionLength = 0;
        }

        private bool ValidateNetwork()
        {
            if (string.IsNullOrWhiteSpace(PublicIp) || string.IsNullOrWhiteSpace(PrivateIp))
            {
                ShowToast("Both Public and Private IPs are required.");
                return false;
            }
            if (!IPAddress.TryParse(PublicIp, out _) || !IPAddress.TryParse(PrivateIp, out _))
            {
                ShowToast("One of the IPs has an invalid format.");
                return false;
            }
            return true;
        }

        // ----- settings -----

        private void OnEnterSettingsPage()
        {
            NameTextBox.Text = ServerName;
            DescriptionTextBox.Text = ServerDescription;
            PasswordTextBox.Text = Password;
            AdvertiseCheckBox.Checked = Advertise;
        }

        // ----- done -----

        private void OnEnterDonePage()
        {
            DoneSummaryLabel.Text =
                "Game type    " + GameType + "\r\n" +
                "Server name  " + ServerName + "\r\n" +
                "Public IP    " + PublicIp + "\r\n" +
                "Private IP   " + PrivateIp + "\r\n" +
                "Advertise    " + (Advertise ? "Yes (visible in master list)" : "No (hidden)") + "\r\n" +
                "Password     " + (string.IsNullOrEmpty(Password) ? "(none)" : "***") + "\r\n" +
                "Release      " + (string.IsNullOrEmpty(ResolvedReleaseTag) ? "(existing install)" : ResolvedReleaseTag);

            UpdateServerStatusLabel();
        }

        private void UpdateServerStatusLabel()
        {
            var st = LocalServerProcess.QueryStatus();
            if (st.Running)
            {
                ServerStatusLabel.Text = "● Running (PID " + st.Pid + ")";
                ServerStatusLabel.ForeColor = OkGreen;
                StartServerButton.Text = "Stop Server";
            }
            else
            {
                ServerStatusLabel.Text = "● Not running";
                ServerStatusLabel.ForeColor = ErrRed;
                StartServerButton.Text = "Start Server";
            }
        }

        private void ToggleLocalServer()
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

        private bool ApplyConfig()
        {
            if (!LocalServerPaths.ServerInstalled)
            {
                ShowToast("Server is not installed. Go back to step 2 and download it.");
                return false;
            }

            if (!LocalServerPaths.ConfigExists)
            {
                if (!LocalServerProcess.Start(out string err))
                {
                    MessageBox.Show("Could not generate default config:\n\n" + err,
                        "Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
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

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Move focus off the first input control so the combobox doesn't
            // open with the selection highlighted in blue and so the Welcome
            // page reads as a clean "press Next" prompt.
            ActiveControl = NextButton;
            NextButton.Focus();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { DownloadCts?.Cancel(); } catch { }
            base.OnFormClosing(e);
        }

        // ============================================================
        //  Visual helpers
        // ============================================================

        private static Label MakeHeading(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = false,
                Bounds = new Rectangle(0, 0, FormW - PadX * 2, 38),
                Font = new Font("Segoe UI Semibold", 13F),
                ForeColor = TextPrimary,
                BackColor = BodyBg,
                TextAlign = ContentAlignment.MiddleLeft,
                UseCompatibleTextRendering = false,
            };
        }

        private static Label MakeBody(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(FormW - PadX * 2, 0),
                Font = new Font("Segoe UI", 10F),
                ForeColor = TextPrimary,
                BackColor = BodyBg,
            };
        }

        private static Label MakeLabel(string text, bool semibold = false)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Font = new Font("Segoe UI" + (semibold ? " Semibold" : ""), 10F),
                ForeColor = TextPrimary,
                BackColor = BodyBg,
            };
        }

        private static Label MakeNote(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(FormW - PadX * 2, 0),
                Font = new Font("Segoe UI", 9F, FontStyle.Italic),
                ForeColor = TextSubtle,
                BackColor = BodyBg,
            };
        }

        private void ShowToast(string text)
        {
            // Quick non-blocking message; could be a custom popup later.
            MessageBox.Show(this, text, "Setup", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ============================================================
        //  StepIndicator — horizontal numbered circles connected by a line
        // ============================================================
        private class StepIndicator : Control
        {
            public Color ActiveColor = Color.Orange;
            public Color DoneColor = Color.DimGray;
            public Color FutureColor = Color.LightGray;
            public Color LabelColor = Color.LightGray;
            public Color ActiveLabelColor = Color.White;

            private readonly string[] Labels;
            private int ActiveIndex = 0;

            public StepIndicator(string[] labels)
            {
                Labels = labels ?? new string[0];
                DoubleBuffered = true;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            }

            public void SetActiveStep(int idx)
            {
                ActiveIndex = Math.Max(0, Math.Min(Labels.Length - 1, idx));
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                g.Clear(BackColor);

                if (Labels.Length == 0) return;

                // Layout zones: top zone has the circles+line; bottom zone has labels.
                // Both adapt to the control's actual height so we never clip text.
                int W = ClientSize.Width;
                int H = ClientSize.Height;
                int circleD = 24;
                int circleR = circleD / 2;
                float circleY = circleR + 4;          // circle centre Y
                float labelTop = circleY + circleR + 8; // start of label band

                // Reserve some side margin so labels don't run off the edge.
                int sideMargin = 40;
                int usableW = Math.Max(circleD, W - sideMargin * 2);

                float[] cx = new float[Labels.Length];
                for (int i = 0; i < Labels.Length; i++)
                {
                    if (Labels.Length == 1) cx[i] = W / 2f;
                    else cx[i] = sideMargin + i * (float)usableW / (Labels.Length - 1);
                }

                // Connector segments (line between consecutive circles).
                using (var donePen = new Pen(DoneColor, 2))
                using (var futurePen = new Pen(FutureColor, 2))
                {
                    for (int i = 0; i < Labels.Length - 1; i++)
                    {
                        var pen = i < ActiveIndex ? donePen : futurePen;
                        g.DrawLine(pen, cx[i] + circleR, circleY, cx[i + 1] - circleR, circleY);
                    }
                }

                // Circles + numbers + labels.
                using (var numFont = new Font("Segoe UI Semibold", 9F))
                using (var labelFont = new Font("Segoe UI Semibold", 8.25F))
                using (var fmt = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Near,
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap,
                })
                {
                    for (int i = 0; i < Labels.Length; i++)
                    {
                        Color fill = i < ActiveIndex ? DoneColor
                                   : i == ActiveIndex ? ActiveColor
                                   : FutureColor;

                        // Filled circle.
                        var rect = new RectangleF(cx[i] - circleR, circleY - circleR, circleD, circleD);
                        using (var brush = new SolidBrush(fill))
                            g.FillEllipse(brush, rect);

                        // Step number inside the circle.
                        using (var numBrush = new SolidBrush(Color.White))
                        {
                            string num = (i + 1).ToString();
                            var size = g.MeasureString(num, numFont);
                            g.DrawString(num, numFont, numBrush,
                                cx[i] - size.Width / 2f, circleY - size.Height / 2f);
                        }

                        // Label below — bold for active, regular tone for others.
                        Color tc = (i == ActiveIndex) ? ActiveLabelColor : LabelColor;
                        using (var labelBrush = new SolidBrush(tc))
                        {
                            var labelRect = new RectangleF(cx[i] - 56, labelTop, 112, H - labelTop);
                            g.DrawString(Labels[i], labelFont, labelBrush, labelRect, fmt);
                        }
                    }
                }
            }
        }

        // ============================================================
        //  FlatButton — owner-drawn button with primary/secondary/ghost styles
        // ============================================================
        private class FlatButton : Control
        {
            public enum ButtonStyle { Primary, Secondary, Ghost }

            public event EventHandler Clicked;
            public ButtonStyle Style { get; set; } = ButtonStyle.Secondary;

            private bool Hover, Down;

            public FlatButton(string text)
            {
                Text = text;
                Font = new Font("Segoe UI Semibold", 10F);
                Cursor = Cursors.Hand;
                DoubleBuffered = true;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
                MouseEnter += (s, e) => { Hover = true; Invalidate(); };
                MouseLeave += (s, e) => { Hover = false; Down = false; Invalidate(); };
                MouseDown += (s, e) => { Down = true; Invalidate(); };
                MouseUp += (s, e) =>
                {
                    bool wasDown = Down;
                    Down = false;
                    Invalidate();
                    if (wasDown && Enabled && ClientRectangle.Contains(e.Location))
                    {
                        Clicked?.Invoke(this, EventArgs.Empty);
                    }
                };
            }

            protected override void OnEnabledChanged(EventArgs e)
            {
                base.OnEnabledChanged(e);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

                Color bg, fg, border;
                switch (Style)
                {
                    case ButtonStyle.Primary:
                        bg = Enabled ? (Down ? Color.FromArgb(170, 110, 35) : (Hover ? AccentHover : Accent))
                                     : Color.FromArgb(220, 200, 175);
                        fg = AccentText;
                        border = bg;
                        break;
                    case ButtonStyle.Ghost:
                        bg = Hover ? Color.FromArgb(238, 235, 226) : Color.Transparent;
                        fg = Enabled ? TextPrimary : Color.Gray;
                        border = Color.Transparent;
                        break;
                    default: // Secondary
                        bg = Hover ? Color.FromArgb(245, 243, 235) : Color.White;
                        fg = Enabled ? TextPrimary : Color.Gray;
                        border = Color.FromArgb(200, 195, 185);
                        break;
                }

                var rect = ClientRectangle;
                var bgRect = new Rectangle(0, 0, rect.Width - 1, rect.Height - 1);

                using (var path = RoundedRect(bgRect, 4))
                {
                    using (var brush = new SolidBrush(bg))
                        g.FillPath(brush, path);
                    if (border.A > 0)
                    {
                        using (var pen = new Pen(border, 1))
                            g.DrawPath(pen, path);
                    }
                }

                TextRenderer.DrawText(g, Text, Font, rect, fg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }

            private static GraphicsPath RoundedRect(Rectangle r, int radius)
            {
                var path = new GraphicsPath();
                int d = radius * 2;
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }
        }
    }
}
