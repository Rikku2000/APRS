using System;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace APRSForwarder
{
    public class APRSGateWayGUI : Form
    {
        private string _iniPath;
        private APRSGateWay _gw = new APRSGateWay();
        private TextBoxWriter _consoleWriter;
        private FileSystemWatcher _fsw;
        private bool _autoApply = false;

        private TabControl tabs;
        private RichTextBox txtLog;
        private Button btnLoad, btnSave, btnStart, btnStop, btnApply;
        private CheckBox cbAuto;

        public APRSGateWayGUI()
        {
            this.Text = "APRSGateway - Control Panel";
            this.Width = 700; this.Height = 700;
            this.StartPosition = FormStartPosition.CenterScreen;
			this.MaximizeBox = false;
			this.FormBorderStyle = FormBorderStyle.FixedSingle;
			this.SizeGripStyle = SizeGripStyle.Hide;
			LoadAppIcon();

            _iniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "aprsgateway.ini");

            tabs = new TabControl();
            tabs.Dock = DockStyle.Top; tabs.Height = 410;

            TabPage pgAprs = new TabPage("APRSGateway");
            TabPage pgMesh = new TabPage("Meshtastic");
            TabPage pgFA   = new TabPage("FlightAware");
            TabPage pgD1090= new TabPage("Dump1090");
            TabPage pgVF   = new TabPage("VesselFinder");
            tabs.TabPages.Add(pgAprs);
            tabs.TabPages.Add(pgMesh);
            tabs.TabPages.Add(pgFA);
            tabs.TabPages.Add(pgD1090);
            tabs.TabPages.Add(pgVF);
            this.Controls.Add(tabs);

            int rowHeight = 24;
            int labelLeft = 10;
            int tbLeft = 180;
            int tbWidth = 485;

            CreatePair(pgAprs, "Server IN URL", "aprs_server_in_url", 0, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgAprs, "Server IN Port", "aprs_server_in_port", 1, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgAprs, "Server OUT URL", "aprs_server_out_url", 2, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgAprs, "Server OUT Port", "aprs_server_out_port", 3, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgAprs, "Callsign", "aprs_callsign", 4, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgAprs, "Passcode", "aprs_passcode", 5, rowHeight, labelLeft, tbLeft, tbWidth);

            CreatePair(pgMesh, "Enabled (true/false)", "mesh_enabled", 0, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgMesh, "MQTT Host", "mesh_host", 1, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgMesh, "MQTT Port", "mesh_port", 2, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgMesh, "Use TLS (true/false)", "mesh_tls", 3, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgMesh, "TLS ignore errors (true/false)", "mesh_tls_ign", 4, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgMesh, "MQTT User", "mesh_user", 5, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgMesh, "MQTT Password", "mesh_pass", 6, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgMesh, "MQTT Topic", "mesh_topic", 7, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgMesh, "Keepalive", "mesh_keepalive", 8, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgMesh, "Node Callsign Prefix", "mesh_prefix", 9, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgMesh, "Default Symbol", "mesh_symbol", 10, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgMesh, "Comment Suffix", "mesh_cmt", 11, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgMesh, "Threadsleep (ms)", "mesh_threadsleep", 12, rowHeight, labelLeft, tbLeft, tbWidth);

            CreatePair(pgFA, "Enabled (true/false)", "fa_enabled", 0, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgFA, "Aircraft URL", "fa_url", 1, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgFA, "Poll Sec.", "fa_poll", 2, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgFA, "Default Symbol", "fa_symbol", 3, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgFA, "Comment Suffix", "fa_cmt", 4, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgFA, "Node Callsign Prefix", "fa_prefix", 5, rowHeight, labelLeft, tbLeft, tbWidth);

            CreatePair(pgD1090, "Enabled (true/false)", "d1090_enabled", 0, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgD1090, "JSON Url", "d1090_json", 1, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgD1090, "Poll Sec.", "d1090_poll", 2, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgD1090, "SBS Host", "d1090_sbs_host", 3, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgD1090, "SBS Port", "d1090_sbs_port", 4, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgD1090, "Default Symbol", "d1090_symbol", 5, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgD1090, "Comment Suffix", "d1090_cmt", 6, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgD1090, "Node Callsign Prefix", "d1090_prefix", 7, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgD1090, "Min TX Interval Sec.", "d1090_min_tx", 8, rowHeight, labelLeft, tbLeft, tbWidth);

            CreatePair(pgVF, "Enabled (true/false)", "vf_enabled", 0, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgVF, "JSON Url", "vf_json", 1, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgVF, "Poll Sec.", "vf_poll", 2, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgVF, "Default Symbol", "vf_symbol", 3, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgVF, "Comment Suffix", "vf_cmt", 4, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgVF, "Node Callsign Prefix", "vf_prefix", 5, rowHeight, labelLeft, tbLeft, tbWidth);
            CreatePair(pgVF, "Min TX Interval Sec.", "vf_min_tx", 6, rowHeight, labelLeft, tbLeft, tbWidth);

            btnLoad  = new Button(); btnLoad.Text = "Load"; btnLoad.Left = 10;  btnLoad.Top = tabs.Bottom + 10; btnLoad.Width = 90;
            btnSave  = new Button(); btnSave.Text = "Save"; btnSave.Left = 110; btnSave.Top = tabs.Bottom + 10; btnSave.Width = 90;
            btnApply = new Button(); btnApply.Text = "Apply"; btnApply.Left = 210; btnApply.Top = tabs.Bottom + 10; btnApply.Width = 90;
            cbAuto   = new CheckBox(); cbAuto.Text = "Auto Save"; cbAuto.Left = 310; cbAuto.Top = tabs.Bottom + 10; cbAuto.Width = 110;
            btnStart = new Button(); btnStart.Text = "Start"; btnStart.Left = this.Width - 110; btnStart.Top = tabs.Bottom + 10; btnStart.Width = 80;
            btnStop  = new Button(); btnStop.Text = "Stop"; btnStop.Left = this.Width - 200; btnStop.Top = tabs.Bottom + 10; btnStop.Width = 80;

            this.Controls.Add(btnLoad);
            this.Controls.Add(btnSave);
            this.Controls.Add(btnApply);
            this.Controls.Add(cbAuto);
            this.Controls.Add(btnStart);
            this.Controls.Add(btnStop);

			CheckBox cbAutostart = new CheckBox();
			cbAutostart.Name = "cbAutostart";
			cbAutostart.Text = "Autostart";
			cbAutostart.Left = 420; cbAutostart.Top = tabs.Bottom + 10; cbAutostart.Width = 110;
			this.Controls.Add(cbAutostart);

			txtLog = new RichTextBox();
			txtLog.DetectUrls = false; txtLog.ReadOnly = true; txtLog.HideSelection = false;
			txtLog.Multiline = true; txtLog.ScrollBars = RichTextBoxScrollBars.None;
			txtLog.WordWrap = true; txtLog.ReadOnly = true;
			txtLog.Left = 10; txtLog.Top = btnLoad.Bottom + 10; txtLog.Width = this.ClientSize.Width - 25; txtLog.Height = this.ClientSize.Height - (btnLoad.Bottom + 20);
			txtLog.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
			this.Controls.Add(txtLog);

            _consoleWriter = new TextBoxWriter((RichTextBox)txtLog, this);
            Console.SetOut(_consoleWriter);
            Console.SetError(_consoleWriter);

            btnLoad.Click += new EventHandler(OnLoadIni);
            btnSave.Click += new EventHandler(OnSaveIni);
            btnApply.Click += new EventHandler(OnApply);
            cbAuto.CheckedChanged += new EventHandler(OnAutoChanged);
            btnStart.Click += new EventHandler(OnStart);
            btnStop.Click += new EventHandler(OnStop);

            this.FormClosing += new FormClosingEventHandler(OnFormClosingClean);
            this.FormClosed += new FormClosedEventHandler(OnFormClosedClean);
            Application.ApplicationExit += new EventHandler(OnAppExitClean);

            LoadIniToUi();

			Control[] ca2 = this.Controls.Find("cbAutostart", true);
			bool doAutoStart = (ca2 != null && ca2.Length > 0 && ca2[0] is CheckBox) ? ((CheckBox)ca2[0]).Checked : false;
			if (doAutoStart) {
				try { _gw.Start(); } catch (Exception ex) { Console.WriteLine("[GUI] " + ex.Message); }
			}
        }

        private void CreatePair(TabPage pg, string label, string name, int row, int rowHeight, int labelLeft, int tbLeft, int tbWidth)
        {
            int y = 15 + 28 * row;
            Label lbl = new Label(); lbl.Text = label; lbl.AutoSize = true; lbl.Left = labelLeft; lbl.Top = y;
            TextBox tb = new TextBox(); tb.Name = "tb_" + name; tb.Width = tbWidth; tb.Left = tbLeft; tb.Top = y - 3;
            pg.Controls.Add(lbl); pg.Controls.Add(tb);
        }

        private void EnsureWatcher(bool enable)
        {
            try
            {
                if (!enable)
                {
                    if (_fsw != null) { _fsw.EnableRaisingEvents = false; _fsw.Dispose(); _fsw = null; }
                    return;
                }
                if (!File.Exists(_iniPath)) return;

                if (_fsw == null)
                {
                    _fsw = new FileSystemWatcher(Path.GetDirectoryName(_iniPath), Path.GetFileName(_iniPath));
                    _fsw.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes;
                    _fsw.Changed += new FileSystemEventHandler(OnIniChanged);
                }
                _fsw.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[GUI] Watcher error: " + ex.Message);
            }
        }

        private void OnIniChanged(object sender, FileSystemEventArgs e)
        {
            Thread.Sleep(150);
            try
            {
                if (_autoApply)
                {
                    if (this.IsHandleCreated)
                    {
                        this.BeginInvoke(new MethodInvoker(delegate()
                        {
                            Console.WriteLine("[GUI] Detected INI change on disk -> reloading...");
                            LoadIniToUi();
                            try { _gw.ReloadFromIni(); } catch (Exception ex) { Console.WriteLine("[GUI] " + ex.Message); }
                        }));
                    }
                }
            }
            catch { }
        }

        private void OnAutoChanged(object sender, EventArgs e)
        {
            _autoApply = cbAuto.Checked;
            EnsureWatcher(_autoApply);
        }

		private void OnFormClosingClean(object sender, FormClosingEventArgs e)
		{
			try { if (_gw != null) _gw.Stop(); } catch { }
			try {
				if (_fsw != null) { _fsw.EnableRaisingEvents = false; _fsw.Dispose(); _fsw = null; }
			} catch { }
		}

		private void OnFormClosedClean(object sender, FormClosedEventArgs e)
		{
			try { System.Environment.Exit(0); } catch { }
		}

		private void OnAppExitClean(object sender, EventArgs e)
		{
			try { if (_gw != null) _gw.Stop(); } catch { }
			try {
				if (_fsw != null) { _fsw.EnableRaisingEvents = false; _fsw.Dispose(); _fsw = null; }
			} catch { }
		}

        private void OnLoadIni(object sender, EventArgs e)
        {
            LoadIniToUi();
        }
        private void OnSaveIni(object sender, EventArgs e)
        {
            SaveUiToIni();
        }
        private void OnStart(object sender, EventArgs e)
        {
            SafeInvoke(new MethodInvoker(delegate() { _gw.Start(); }));
        }
        private void OnStop(object sender, EventArgs e)
        {
            SafeInvoke(new MethodInvoker(delegate() { _gw.Stop(); }));
        }
        private void OnApply(object sender, EventArgs e)
        {
            SafeInvoke(new MethodInvoker(delegate()
            {
                SaveUiToIni();
                _gw.ReloadFromIni();
            }));
        }

        private void SafeInvoke(MethodInvoker act)
        {
            try { act(); }
            catch (Exception ex) { Console.WriteLine("[GUI] " + ex.Message); }
        }

        private void LoadIniToUi()
        {
            SimpleIni ini = new SimpleIni(_iniPath);

			string autostart = ini.Read("autostart", "APRSGateWayGUI");
			Control[] ca = this.Controls.Find("cbAutostart", true);
			if (ca != null && ca.Length > 0 && ca[0] is CheckBox)
				((CheckBox)ca[0]).Checked = (autostart == "true" || autostart == "1" || autostart == "True");

            SetTB("tb_aprs_server_in_url",  ini.Read("server_in_url", "APRSGateway"));
            SetTB("tb_aprs_server_in_port", ini.Read("server_in_port", "APRSGateway"));
            SetTB("tb_aprs_server_out_url", ini.Read("server_out_url", "APRSGateway"));
            SetTB("tb_aprs_server_out_port",ini.Read("server_out_port", "APRSGateway"));
            SetTB("tb_aprs_callsign",       ini.Read("callsign", "APRSGateway"));
            SetTB("tb_aprs_passcode",       ini.Read("passcode", "APRSGateway"));

            SetTB("tb_mesh_enabled",        ini.Read("enabled", "MeshGateway"));
            SetTB("tb_mesh_host",           ini.Read("mqtt_host", "MeshGateway"));
            SetTB("tb_mesh_port",           ini.Read("mqtt_port", "MeshGateway"));
            SetTB("tb_mesh_tls",            ini.Read("use_tls", "MeshGateway"));
            SetTB("tb_mesh_tls_ign",        ini.Read("tls_ignore_errors", "MeshGateway"));
            SetTB("tb_mesh_user",           ini.Read("mqtt_user", "MeshGateway"));
            SetTB("tb_mesh_pass",           ini.Read("mqtt_pass", "MeshGateway"));
            SetTB("tb_mesh_topic",          ini.Read("mqtt_topic", "MeshGateway"));
            SetTB("tb_mesh_keepalive",      ini.Read("keepalive", "MeshGateway"));
            SetTB("tb_mesh_prefix",         ini.Read("node_callsign_prefix", "MeshGateway"));
            SetTB("tb_mesh_symbol",         ini.Read("default_symbol", "MeshGateway"));
            SetTB("tb_mesh_cmt",            ini.Read("comment_suffix", "MeshGateway"));
            SetTB("tb_mesh_threadsleep",    ini.Read("threadsleep", "MeshGateway"));

            SetTB("tb_fa_enabled",          ini.Read("enabled", "FlightAware"));
            SetTB("tb_fa_url",              ini.Read("aircraft_url", "FlightAware"));
            SetTB("tb_fa_poll",             ini.Read("poll_secs", "FlightAware"));
            SetTB("tb_fa_symbol",           ini.Read("default_symbol", "FlightAware"));
            SetTB("tb_fa_cmt",              ini.Read("comment_suffix", "FlightAware"));
            SetTB("tb_fa_prefix",           ini.Read("node_callsign_prefix", "FlightAware"));

            SetTB("tb_d1090_enabled",       ini.Read("enabled", "Dump1090"));
            SetTB("tb_d1090_json",          ini.Read("json_url", "Dump1090"));
            SetTB("tb_d1090_poll",          ini.Read("poll_secs", "Dump1090"));
            SetTB("tb_d1090_sbs_host",      ini.Read("sbs_host", "Dump1090"));
            SetTB("tb_d1090_sbs_port",      ini.Read("sbs_port", "Dump1090"));
            SetTB("tb_d1090_symbol",        ini.Read("default_symbol", "Dump1090"));
            SetTB("tb_d1090_cmt",           ini.Read("comment_suffix", "Dump1090"));
            SetTB("tb_d1090_prefix",        ini.Read("node_callsign_prefix", "Dump1090"));
            SetTB("tb_d1090_min_tx",        ini.Read("min_tx_interval_secs", "Dump1090"));

            SetTB("tb_vf_enabled",          ini.Read("enabled", "VesselFinder"));
            SetTB("tb_vf_json",             ini.Read("json_url", "VesselFinder"));
            SetTB("tb_vf_poll",             ini.Read("poll_secs", "VesselFinder"));
            SetTB("tb_vf_symbol",           ini.Read("default_symbol", "VesselFinder"));
            SetTB("tb_vf_cmt",              ini.Read("comment_suffix", "VesselFinder"));
            SetTB("tb_vf_prefix",           ini.Read("node_callsign_prefix", "VesselFinder"));
            SetTB("tb_vf_min_tx",           ini.Read("min_tx_interval_secs", "VesselFinder"));

            Console.WriteLine("[GUI] INI loaded from " + _iniPath);
        }

        private void SaveUiToIni()
        {
            SimpleIni ini = new SimpleIni(_iniPath);

			Control[] ca = this.Controls.Find("cbAutostart", true);
			bool autostartChecked = (ca != null && ca.Length > 0 && ca[0] is CheckBox) ? ((CheckBox)ca[0]).Checked : false;
			ini.Write("autostart", autostartChecked ? "true" : "false", "APRSGateWayGUI");

            ini.Write("server_in_url",     GetTB("tb_aprs_server_in_url"),  "APRSGateway");
            ini.Write("server_in_port",    GetTB("tb_aprs_server_in_port"), "APRSGateway");
            ini.Write("server_out_url",    GetTB("tb_aprs_server_out_url"), "APRSGateway");
            ini.Write("server_out_port",   GetTB("tb_aprs_server_out_port"),"APRSGateway");
            ini.Write("callsign",          GetTB("tb_aprs_callsign"),       "APRSGateway");
            ini.Write("passcode",          GetTB("tb_aprs_passcode"),       "APRSGateway");

            ini.Write("enabled",            GetTB("tb_mesh_enabled"),     "MeshGateway");
            ini.Write("mqtt_host",          GetTB("tb_mesh_host"),        "MeshGateway");
            ini.Write("mqtt_port",          GetTB("tb_mesh_port"),        "MeshGateway");
            ini.Write("use_tls",            GetTB("tb_mesh_tls"),         "MeshGateway");
            ini.Write("tls_ignore_errors",  GetTB("tb_mesh_tls_ign"),     "MeshGateway");
            ini.Write("mqtt_user",          GetTB("tb_mesh_user"),        "MeshGateway");
            ini.Write("mqtt_pass",          GetTB("tb_mesh_pass"),        "MeshGateway");
            ini.Write("mqtt_topic",         GetTB("tb_mesh_topic"),       "MeshGateway");
            ini.Write("keepalive",          GetTB("tb_mesh_keepalive"),   "MeshGateway");
            ini.Write("node_callsign_prefix",GetTB("tb_mesh_prefix"),     "MeshGateway");
            ini.Write("default_symbol",     GetTB("tb_mesh_symbol"),      "MeshGateway");
            ini.Write("comment_suffix",     GetTB("tb_mesh_cmt"),         "MeshGateway");
            ini.Write("threadsleep",        GetTB("tb_mesh_threadsleep"), "MeshGateway");

            ini.Write("enabled",            GetTB("tb_fa_enabled"),       "FlightAware");
            ini.Write("aircraft_url",       GetTB("tb_fa_url"),           "FlightAware");
            ini.Write("poll_secs",          GetTB("tb_fa_poll"),          "FlightAware");
            ini.Write("default_symbol",     GetTB("tb_fa_symbol"),        "FlightAware");
            ini.Write("comment_suffix",     GetTB("tb_fa_cmt"),           "FlightAware");
            ini.Write("node_callsign_prefix",GetTB("tb_fa_prefix"),       "FlightAware");

            ini.Write("enabled",            GetTB("tb_d1090_enabled"),    "Dump1090");
            ini.Write("json_url",           GetTB("tb_d1090_json"),       "Dump1090");
            ini.Write("poll_secs",          GetTB("tb_d1090_poll"),       "Dump1090");
            ini.Write("sbs_host",           GetTB("tb_d1090_sbs_host"),   "Dump1090");
            ini.Write("sbs_port",           GetTB("tb_d1090_sbs_port"),   "Dump1090");
            ini.Write("default_symbol",     GetTB("tb_d1090_symbol"),     "Dump1090");
            ini.Write("comment_suffix",     GetTB("tb_d1090_cmt"),        "Dump1090");
            ini.Write("node_callsign_prefix",GetTB("tb_d1090_prefix"),    "Dump1090");
            ini.Write("min_tx_interval_secs",GetTB("tb_d1090_min_tx"),    "Dump1090");

            ini.Write("enabled",            GetTB("tb_vf_enabled"),       "VesselFinder");
            ini.Write("json_url",           GetTB("tb_vf_json"),          "VesselFinder");
            ini.Write("poll_secs",          GetTB("tb_vf_poll"),          "VesselFinder");
            ini.Write("default_symbol",     GetTB("tb_vf_symbol"),        "VesselFinder");
            ini.Write("comment_suffix",     GetTB("tb_vf_cmt"),           "VesselFinder");
            ini.Write("node_callsign_prefix",GetTB("tb_vf_prefix"),       "VesselFinder");
            ini.Write("min_tx_interval_secs",GetTB("tb_vf_min_tx"),       "VesselFinder");

            ini.Save();
            Console.WriteLine("[GUI] INI saved to " + _iniPath);
        }

        private void SetTB(string name, string val)
        {
            Control[] c = this.Controls.Find(name, true);
            if (c != null && c.Length > 0)
            {
                TextBox tb = c[0] as TextBox;
                if (tb != null) tb.Text = (val == null) ? string.Empty : val;
            }
        }
        private string GetTB(string name)
        {
            Control[] c = this.Controls.Find(name, true);
            if (c != null && c.Length > 0)
            {
                TextBox tb = c[0] as TextBox;
                if (tb != null) return (tb.Text == null) ? string.Empty : tb.Text.Trim();
            }
            return string.Empty;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
		private static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

		public System.Drawing.Color ColorForLine(string line)
		{
			if (line == null) return System.Drawing.SystemColors.ControlText;

			string s = line;
			if (s.IndexOf("[APRS]", StringComparison.OrdinalIgnoreCase) >= 0) return System.Drawing.Color.FromArgb(0, 200, 120);
			if (s.IndexOf("[MQTT]", StringComparison.OrdinalIgnoreCase) >= 0) return System.Drawing.Color.FromArgb(80, 160, 255);
			if (s.IndexOf("[FA]", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("[FlightAware]", StringComparison.OrdinalIgnoreCase) >= 0) return System.Drawing.Color.FromArgb(255, 200, 80);
			if (s.IndexOf("[DUMP1090]", StringComparison.OrdinalIgnoreCase) >= 0) return System.Drawing.Color.FromArgb(200, 120, 255);
			if (s.IndexOf("[AIS]", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("[VesselFinder]", StringComparison.OrdinalIgnoreCase) >= 0) return System.Drawing.Color.FromArgb(80, 220, 220);
			if (s.IndexOf("[GUI]", StringComparison.OrdinalIgnoreCase) >= 0) return System.Drawing.Color.FromArgb(170, 170, 170);
			if (s.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) >= 0) return System.Drawing.Color.FromArgb(255, 90, 90);
			if (s.IndexOf("WARN", StringComparison.OrdinalIgnoreCase) >= 0) return System.Drawing.Color.FromArgb(255, 210, 90);

			return System.Drawing.SystemColors.ControlText;
		}

        private class TextBoxWriter : System.IO.TextWriter
        {
            private RichTextBox _tb;
            private APRSGateWayGUI _owner;
            public TextBoxWriter(RichTextBox tb, APRSGateWayGUI owner) { _tb = tb; _owner = owner; }
            public override Encoding Encoding { get { return Encoding.UTF8; } }
            public override void WriteLine(string value)
            {
                if (_tb.IsDisposed) return;
                if (_tb.InvokeRequired)
                {
                    _tb.BeginInvoke(new MethodInvoker(delegate() { Append(value); }));
                }
                else Append(value);
            }
            public override void Write(char value) { }
            private void Append(string line)
            {
                System.Drawing.Color col = _owner != null ? _owner.ColorForLine(line) : _tb.ForeColor;

                int start = _tb.TextLength;
                string text = line + Environment.NewLine;
                _tb.SelectionStart = start;
                _tb.SelectionLength = 0;
                _tb.SelectionColor = col;
                _tb.AppendText(text);
                _tb.SelectionColor = _tb.ForeColor;
                _tb.SelectionStart = _tb.TextLength;
                _tb.ScrollToCaret();

                if (_tb.Lines != null && _tb.Lines.Length > 10000)
                {
                    string[] lines = _tb.Lines;
                    int keep = 8000;
                    if (lines.Length > keep)
                    {
                        string[] trimmed = new string[keep];
                        Array.Copy(lines, lines.Length - keep, trimmed, 0, keep);
                        _tb.Lines = trimmed;
                    }
                }
            }
        }

		private void LoadAppIcon() {
			try {
				System.Drawing.Icon exeIco = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath);
				if (exeIco != null) { this.Icon = exeIco; return; }
			} catch { }
		}
    }

    internal sealed class SimpleIni
    {
        private readonly string _path;
        private readonly Dictionary<string, Dictionary<string, string>> _map =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public SimpleIni(string path)
        {
            _path = path;
            if (File.Exists(path))
            {
                string cur = "DEFAULT";
                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string raw = lines[i];
                    if (raw == null) continue;
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        cur = line.Substring(1, line.Length - 2).Trim();
                        if (!_map.ContainsKey(cur)) _map[cur] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        continue;
                    }
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                    {
                        string k = line.Substring(0, eq).Trim();
                        string v = line.Substring(eq + 1).Trim();
                        if (!_map.ContainsKey(cur)) _map[cur] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        _map[cur][k] = v;
                    }
                }
            }
        }

        public string Read(string key, string section)
        {
            if (section == null) section = "DEFAULT";
            Dictionary<string, string> sec;
            string v;
            if (_map.TryGetValue(section, out sec) && sec.TryGetValue(key, out v)) return v;
            return string.Empty;
        }

        public void Write(string key, string value, string section)
        {
            if (!_map.ContainsKey(section)) _map[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _map[section][key] = (value == null) ? string.Empty : value;
        }

        public void Save()
        {
            using (StreamWriter sw = new StreamWriter(_path, false, new UTF8Encoding(false)))
            {
                foreach (string sec in _map.Keys)
                {
                    sw.WriteLine("[" + sec + "]");
                    Dictionary<string, string> kvs = _map[sec];
                    foreach (KeyValuePair<string, string> kv in kvs)
                        sw.WriteLine(kv.Key + "=" + kv.Value);
                    sw.WriteLine();
                }
            }
        }
    }
}
