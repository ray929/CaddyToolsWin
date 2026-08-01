using System;
using System.IO;
using System.Text;
using System.Windows.Forms;
using System.Drawing;
using Microsoft.Win32;
using System.ServiceProcess;
using System.Diagnostics;
using FastColoredTextBoxNS;

namespace CaddyToolsWin
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // Persistent memory stored in %USERPROFILE%\.caddy-tools-win (small JSON file).
    internal static class ConfigStore
    {
        private static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".caddy-tools-win"); }
        }

        public static AppConfig Load()
        {
            if (!File.Exists(FilePath)) return null;
            try
            {
                var json = File.ReadAllText(FilePath, Encoding.UTF8);
                var cfg = new AppConfig
                {
                    CaddyDir = ReadValue(json, "caddyDir"),
                    ExeName = ReadValue(json, "exeName"),
                    ServiceName = ReadValue(json, "serviceName") ?? ""
                };
                if (string.IsNullOrEmpty(cfg.CaddyDir) || string.IsNullOrEmpty(cfg.ExeName))
                    return null;
                return cfg;
            }
            catch
            {
                return null;
            }
        }

        public static void Save(AppConfig cfg)
        {
            var json = "{\n" +
                       "  \"caddyDir\": \"" + Escape(cfg.CaddyDir) + "\",\n" +
                       "  \"exeName\": \"" + Escape(cfg.ExeName) + "\",\n" +
                       "  \"serviceName\": \"" + Escape(cfg.ServiceName ?? "") + "\"\n" +
                       "}";
            File.WriteAllText(FilePath, json, Encoding.UTF8);
        }

        private static string Escape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string ReadValue(string json, string key)
        {
            var marker = "\"" + key + "\"";
            var i = json.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return null;
            var colon = json.IndexOf(':', i);
            if (colon < 0) return null;
            var q1 = json.IndexOf('"', colon);
            if (q1 < 0) return null;
            var q2 = json.IndexOf('"', q1 + 1);
            if (q2 < 0) return null;
            return json.Substring(q1 + 1, q2 - q1 - 1)
                       .Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }

    internal sealed class AppConfig
    {
        public string CaddyDir;
        public string ExeName;
        public string ServiceName;
    }

    // Find a Windows service whose ImagePath (including its arguments) references the
    // given Caddy / FrankenPHP directory, e.g. a WinSW service whose -config points at
    // "d:\Tools\frankenphp\frankenphp.xml".
    internal static class ServiceFinder
    {
        public static string FindByDir(string dir)
        {
            var needle = dir.TrimEnd('\\').ToLowerInvariant() + "\\";
            using (var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
            {
                if (root == null) return "";
                foreach (var name in root.GetSubKeyNames())
                {
                    using (var svc = root.OpenSubKey(name))
                    {
                        if (svc == null) continue;
                        var imagePath = svc.GetValue("ImagePath") as string;
                        if (string.IsNullOrEmpty(imagePath)) continue;
                        if (imagePath.ToLowerInvariant().Contains(needle))
                            return name;
                    }
                }
            }
            return "";
        }
    }

    internal sealed class MainForm : Form
    {
        private MenuStrip menu;
        private ToolStripMenuItem saveItem;
        private ToolStripMenuItem openItem;
        private ToolStripMenuItem validateItem;
        private ToolStripMenuItem formatItem;
        private ToolStripMenuItem reloadItem;
        private FastColoredTextBox editor;

        private AppConfig config;
        private string caddyFilePath;
        private bool dirty;
        private bool loading;

        public MainForm()
        {
            InitializeComponent();
            LoadIcon();
        }

        private void InitializeComponent()
        {
            menu = new MenuStrip();
            saveItem = new ToolStripMenuItem("Save", null, (s, e) => Save(), Keys.Control | Keys.S);
            openItem = new ToolStripMenuItem("Open", null, (s, e) => OpenDirectory());
            validateItem = new ToolStripMenuItem("Validate", null, (s, e) => ValidateConfig());
            formatItem = new ToolStripMenuItem("Format", null, (s, e) => FormatConfig());
            reloadItem = new ToolStripMenuItem("Reload", null, (s, e) => Reload());
            menu.Items.AddRange(new ToolStripItem[] { saveItem, openItem, validateItem, formatItem, reloadItem });

            editor = new FastColoredTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 10.5F, FontStyle.Regular, GraphicsUnit.Point),
                Language = Language.Custom,
                ShowLineNumbers = true,
                WordWrap = false,
                TabLength = 4,
                Text = ""
            };
            SetupCaddySyntax();
            editor.TextChanged += Editor_TextChanged;

            Controls.Add(editor);
            Controls.Add(menu);
            MainMenuStrip = menu;

            Text = "Caddyfile - Caddy Tools Win";
            ClientSize = new Size(900, 600);
            StartPosition = FormStartPosition.CenterScreen;

            Shown += (s, e) =>
            {
                if (!EnsureConfig()) { Close(); return; }
                LoadFile();
            };

            FormClosing += (s, e) =>
            {
                if (!CheckUnsaved("exiting")) e.Cancel = true;
            };
        }

        private void LoadIcon()
        {
            try
            {
                using (var stream = GetType().Assembly.GetManifestResourceStream("caddy.ico"))
                {
                    if (stream != null) Icon = new Icon(stream);
                }
            }
            catch { /* keep default icon */ }
        }

        // --- Caddyfile syntax highlighting (FastColoredTextBox) ----------------

        private TextStyle styleComment;
        private TextStyle styleDirective;
        private TextStyle styleAddr;
        private TextStyle styleString;
        private TextStyle stylePlaceholder;

        private void SetupCaddySyntax()
        {
            styleComment = new TextStyle(Brushes.Gray, null, FontStyle.Italic);
            styleDirective = new TextStyle(Brushes.Navy, null, FontStyle.Bold);
            styleAddr = new TextStyle(Brushes.Teal, null, FontStyle.Regular);
            styleString = new TextStyle(Brushes.Maroon, null, FontStyle.Regular);
            stylePlaceholder = new TextStyle(Brushes.Purple, null, FontStyle.Regular);

            editor.TextChanged += (s, e) => HighlightCaddy(e.ChangedRange);
        }

        private void HighlightCaddy(Range range)
        {
            range.ClearStyle(StyleIndex.All);
            // Comments and strings first, then directives, addresses, placeholders.
            range.SetStyle(styleComment, @"#.*");
            range.SetStyle(styleString, @"""""""|'[^']*'|`[^`]*`");
            range.SetStyle(styleDirective,
                @"\b(reverse_proxy|file_server|try_files|rewrite|redirect|respond|root|templates|encode|php_fastcgi|handle|handle_path|route|log|header|basicauth|tls|bind|import|global_options|admin|auto_https|email|listen|experimental_http3|servers|metrics|status|abort|error|method|uri|match|vars|map|sort|group|push|copy_response|copy_response_headers|file_match|path|not|expression|acos|asin|atan|base64decode|base64encode|bcrypt|bool|capitalize|capitalize_all|ceil|coalesce|contains|cookie|div|div_rem|eq|exp|file_exists|float|floor|hash|host|http_filter|humanize|int|is_cert_request|len|lower|max|md5|min|mod|mod_rewrite|mul|neq|pow|querify|replace|replace_all|round|sha256|sha512|shift|sign|sin|split|sqrt|sub|tan|truncate|unique|upper|url_decode|url_encode|uuid|write|names|pki|client_ip|remote_ip)\b");
            range.SetStyle(styleAddr, @"(?m)^[ \t]*[*\w.\-]+(:[0-9]+)?[ \t]*(?={|$)");
            range.SetStyle(stylePlaceholder, @"\{[\w.]+\}");
        }

        // --- config acquisition ------------------------------------------------

        private bool EnsureConfig()
        {
            config = ConfigStore.Load();
            while (config == null)
            {
                string dir;
                if (!PromptForDirectory(out dir))
                    return false; // user canceled -> exit application
                config = ValidateDirectory(dir);
                if (config != null) ConfigStore.Save(config);
            }
            return true;
        }

        private bool PromptForDirectory(out string dir)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Select the Caddy / FrankenPHP directory (it must contain a Caddyfile)";
                dlg.ShowNewFolderButton = false;
                var ok = dlg.ShowDialog() == DialogResult.OK;
                dir = ok ? dlg.SelectedPath : null;
                return ok;
            }
        }

        // Returns a validated config, or null (with an error dialog) if invalid.
        private AppConfig ValidateDirectory(string dir)
        {
            dir = dir.TrimEnd('\\');
            var cf = Path.Combine(dir, "Caddyfile");
            if (!File.Exists(cf))
            {
                MessageBox.Show(this,
                    "No Caddyfile found in this directory.\r\nPlease choose the correct Caddy / FrankenPHP directory.",
                    "Invalid directory", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }

            string exe = null;
            if (File.Exists(Path.Combine(dir, "caddy.exe"))) exe = "caddy.exe";
            else if (File.Exists(Path.Combine(dir, "frankenphp.exe"))) exe = "frankenphp.exe";
            if (exe == null)
            {
                MessageBox.Show(this,
                    "Neither caddy.exe nor frankenphp.exe was found in this directory.\r\nPlease choose the correct Caddy / FrankenPHP directory.",
                    "Invalid directory", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }

            var svc = ServiceFinder.FindByDir(dir); // may be empty; not an error
            return new AppConfig { CaddyDir = dir, ExeName = exe, ServiceName = svc };
        }

        private void OpenDirectory()
        {
            AppConfig next = null;
            while (next == null)
            {
                string dir;
                if (!PromptForDirectory(out dir)) return; // canceled -> keep current
                next = ValidateDirectory(dir);
                if (next != null)
                {
                    config = next;
                    ConfigStore.Save(config);
                    caddyFilePath = Path.Combine(config.CaddyDir, "Caddyfile");
                    LoadFile();
                }
            }
        }

        // --- editor -------------------------------------------------------------

        private void LoadFile()
        {
            caddyFilePath = Path.Combine(config.CaddyDir, "Caddyfile");
            loading = true;
            try
            {
                var raw = File.Exists(caddyFilePath)
                    ? File.ReadAllText(caddyFilePath, Encoding.UTF8)
                    : "";
                // FastColoredTextBox stores text with \n line endings; it renders them fine.
                editor.Text = raw.Replace("\r\n", "\n");
                editor.IsChanged = false;
            }
            finally
            {
                loading = false;
            }
            dirty = false;
            saveItem.Enabled = false;
            UpdateTitle();
        }

        private void Editor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (loading) return;
            dirty = true;
            saveItem.Enabled = true;
            UpdateTitle();
        }

        private void UpdateTitle()
        {
            Text = (dirty ? "* " : "") + "Caddyfile - Caddy Tools Win";
        }

        // Returns true if there is nothing to save or the save succeeded.
        private bool Save()
        {
            if (!dirty) return true;
            try
            {
                // Caddyfile uses \n line endings; FCTB holds text as \n already.
                File.WriteAllText(caddyFilePath, editor.Text, new UTF8Encoding(false));
                editor.IsChanged = false;
                dirty = false;
                saveItem.Enabled = false;
                UpdateTitle();
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to save:\r\n" + ex.Message, "Save",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        // If there are unsaved changes, ask the user to save or cancel the action.
        // Returns true to proceed, false to abort the operation.
        private bool CheckUnsaved(string action)
        {
            if (!dirty) return true;
            var r = MessageBox.Show(this,
                "You have unsaved changes.\r\nSave before " + action + "?",
                "Unsaved changes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r == DialogResult.Yes)
                return Save(); // proceed only if the save succeeded
            return false;      // No -> cancel the operation
        }

        // --- caddy commands -----------------------------------------------------

        private string ExePath
        {
            get { return Path.Combine(config.CaddyDir, config.ExeName); }
        }

        // Both caddy.exe and frankenphp.exe expose the caddy subcommands directly
        // (validate / fmt / reload); frankenphp does NOT use a "caddy" prefix.

        private int RunCaptured(string args, out string output)
        {
            var psi = new ProcessStartInfo(ExePath, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = config.CaddyDir,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (var p = Process.Start(psi))
            {
                var stdout = p.StandardOutput.ReadToEnd();
                var stderr = p.StandardError.ReadToEnd();
                p.WaitForExit();
                var combined = stdout + (string.IsNullOrEmpty(stderr) ? "" : "\n" + stderr);
                output = combined.Replace("\r\n", "\n").Replace("\n", "\r\n").Trim();
                return p.ExitCode;
            }
        }

        private void ValidateConfig()
        {
            if (!CheckUnsaved("validating")) return;
            var args = "validate --config \"" + caddyFilePath + "\"";
            string output;
            var code = RunCaptured(args, out output);
            MessageBox.Show(this, (code == 0 ? "Configuration is valid.\r\n\r\n" : "Validation failed.\r\n\r\n") + output,
                "Validate", MessageBoxButtons.OK, code == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        }

        private void FormatConfig()
        {
            if (!CheckUnsaved("formatting")) return;
            // caddy fmt takes the path positionally; frankenphp fmt uses --config.
            var configFlag = config.ExeName.ToLowerInvariant().Contains("frankenphp") ? " --config" : "";
            var args = "fmt --overwrite" + configFlag + " \"" + caddyFilePath + "\"";
            string output;
            var code = RunCaptured(args, out output);
            if (code == 0)
            {
                LoadFile(); // refresh editor with the formatted content
            }
            else
            {
                MessageBox.Show(this, "Format failed.\r\n\r\n" + output, "Format",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Reload()
        {
            if (!CheckUnsaved("reloading")) return;
            if (!string.IsNullOrEmpty(config.ServiceName))
            {
                ReloadViaService();
            }
            else
            {
            var args = "reload --config \"" + caddyFilePath + "\"";
            string output;
            var code = RunCaptured(args, out output);
                MessageBox.Show(this, (code == 0 ? "Reload triggered.\r\n\r\n" : "Reload failed.\r\n\r\n") + output,
                    "Reload", MessageBoxButtons.OK, code == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            }
        }

        // Restart the Windows service, elevating (UAC) when required.
        private void ReloadViaService()
        {
            var tmp = Path.GetTempFileName();
            var bat = Path.ChangeExtension(tmp, ".bat");
            try
            {
                File.WriteAllText(bat,
                    "net stop \"" + config.ServiceName + "\" > \"" + tmp + "\" 2>&1\r\n" +
                    "net start \"" + config.ServiceName + "\" >> \"" + tmp + "\" 2>&1\r\n",
                    Encoding.ASCII);

                var psi = new ProcessStartInfo(bat)
                {
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                }

                var output = File.Exists(tmp) ? File.ReadAllText(tmp, Encoding.Default).Trim() : "(no output)";
                MessageBox.Show(this, output, "Reload Service", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                MessageBox.Show(this, "Reload canceled (administrator permission was not granted).",
                    "Reload", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Reload failed:\r\n" + ex.Message, "Reload",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                try { File.Delete(bat); } catch { }
                try { File.Delete(tmp); } catch { }
            }
        }
    }
}
