using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using Microsoft.Win32;
using System.ServiceProcess;
using System.Diagnostics;
using WF = System.Windows.Forms; // FolderBrowserDialog (alias to avoid WPF/WinForms name clashes)
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using System.Xml;

namespace CaddyToolsWin
{
    internal static class Program
    {
        private static readonly string MutexName = "CaddyToolsWin-SingleInstance-{8F3E1A2B-5C7D-4E9F-8A1C-3B6D2E5F0A9D}";

        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (var mutex = new System.Threading.Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // Another instance is already running; bail out silently.
                    return;
                }
                var app = new System.Windows.Application();
                app.Run(new MainWindow());
            }
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

    internal sealed class MainWindow : Window
    {
        private Menu menu;
        private MenuItem saveItem;
        private MenuItem openItem;
        private MenuItem validateItem;
        private MenuItem formatItem;
        private MenuItem reloadItem;
        private TextEditor editor;

        private AppConfig config;
        private string caddyFilePath;
        private bool dirty;
        private bool loading;

        public MainWindow()
        {
            InitializeComponent();
            LoadIcon();
            Loaded += (s, e) =>
            {
                if (!EnsureConfig()) { Close(); return; }
                LoadFile();
            };
            Closing += (s, e) =>
            {
                if (!CheckUnsaved("exiting")) e.Cancel = true;
            };
        }

        private void InitializeComponent()
        {
            // Menu
            saveItem = new MenuItem { Header = "_Save" };
            saveItem.InputGestureText = "Ctrl+S";
            saveItem.Click += (s, e) => Save();
            openItem = new MenuItem { Header = "_Open" };
            openItem.Click += (s, e) => OpenDirectory();
            validateItem = new MenuItem { Header = "_Validate" };
            validateItem.Click += (s, e) => ValidateConfig();
            formatItem = new MenuItem { Header = "_Format" };
            formatItem.Click += (s, e) => FormatConfig();
            reloadItem = new MenuItem { Header = "_Reload" };
            reloadItem.Click += (s, e) => Reload();

            menu = new Menu();
            menu.Items.Add(saveItem);
            menu.Items.Add(openItem);
            menu.Items.Add(validateItem);
            menu.Items.Add(formatItem);
            menu.Items.Add(reloadItem);

            // Editor (AvalonEdit)
            editor = new TextEditor
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 13.0,
                ShowLineNumbers = true,
                WordWrap = false,
                SyntaxHighlighting = CaddyHighlighting.Instance,
                Background = Brushes.White,
                Foreground = Brushes.Black
            };
            editor.Options.IndentationSize = 4;
            editor.Options.ConvertTabsToSpaces = false;
            editor.Options.EnableHyperlinks = false;
            editor.Options.EnableEmailHyperlinks = false;
            editor.TextChanged += Editor_TextChanged;
            editor.PreviewKeyDown += Editor_PreviewKeyDown;

            var dock = new DockPanel();
            DockPanel.SetDock(menu, Dock.Top);
            dock.Children.Add(menu);
            dock.Children.Add(editor);

            Content = dock;
            Title = "Caddyfile - Caddy Tools Win";
            Width = 900;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        private void LoadIcon()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "caddy.ico");
                if (File.Exists(path))
                    Icon = new BitmapImage(new Uri(path));
            }
            catch { /* keep default icon */ }
        }

        private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyboardDevice.Modifiers == ModifierKeys.Control && e.Key == Key.S)
            {
                Save();
                e.Handled = true;
            }
        }

        // --- Caddyfile syntax highlighting (AvalonEdit) ----------------------

        private static class CaddyHighlighting
        {
            private static IHighlightingDefinition _instance;
            public static IHighlightingDefinition Instance
            {
                get { return _instance ?? (_instance = Load()); }
            }

            private static IHighlightingDefinition Load()
            {
                var xshd =
@"<SyntaxDefinition name=""Caddyfile"" xmlns=""http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008"">
  <Color name=""Comment"" foreground=""#FF6A9955"" />
  <Color name=""String"" foreground=""#FFA31515"" />
  <Color name=""Placeholder"" foreground=""#FF800080"" />
  <Color name=""Directive"" foreground=""#FF0000FF"" fontWeight=""bold"" />
  <RuleSet>
    <Span color=""Comment"">
      <Begin>\#</Begin>
    </Span>
    <Span color=""String"">
      <Begin>&quot;</Begin>
      <End>&quot;</End>
    </Span>
    <Span color=""String"">
      <Begin>'</Begin>
      <End>'</End>
    </Span>
    <Span color=""String"">
      <Begin>`</Begin>
      <End>`</End>
    </Span>
    <Rule color=""Placeholder"">{[\w.]+}</Rule>
    <Rule color=""Directive"">(?&lt;=^|[ \t])(abort|acme_server|basic_auth|bind|encode|error|file|file_server|forward_auth|fs|handle|handle_errors|handle_path|header|host|import|intercept|invoke|log|log_append|log_skip|log_name|map|method|metrics|php_fastcgi|push|redir|request_body|request_header|respond|reverse_proxy|rewrite|root|route|templates|tls|tracing|try_files|uri|vars|debug|http_port|https_port|default_bind|order|storage|storage_clean_interval|admin|persist_config|grace_period|shutdown_delay|auto_https|email|default_sni|fallback_sni|local_certs|skip_install_trust|acme_ca|acme_ca_root|acme_eab|acme_dns|dns|ech|on_demand_tls|key_type|cert_issuer|renew_interval|cert_lifetime|ocsp_interval|ocsp_stapling|renewal_window_ratio|preferred_chains|servers|filesystem|pki|events|frankenphp|num_threads|max_threads|max_wait_time|max_idle_time|max_requests|php_ini|worker|php_server|split_path|resolve_root_symlink|env|match|watch|name|num|enable_full_duplex)\b</Rule>
  </RuleSet>
</SyntaxDefinition>";
                using (var reader = new StringReader(xshd))
                using (var xml = XmlReader.Create(reader))
                {
                    return HighlightingLoader.Load(xml, null);
                }
            }
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
            using (var dlg = new WF.FolderBrowserDialog())
            {
                dlg.Description = "Select the Caddy / FrankenPHP directory (it must contain a Caddyfile)";
                dlg.ShowNewFolderButton = false;
                var owner = new WindowInteropHelper(this).Handle;
                var result = owner != IntPtr.Zero ? dlg.ShowDialog(new Win32Window(owner)) : dlg.ShowDialog();
                var ok = result == WF.DialogResult.OK;
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
                System.Windows.MessageBox.Show(this,
                    "No Caddyfile found in this directory.\r\nPlease choose the correct Caddy / FrankenPHP directory.",
                    "Invalid directory", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }

            string exe = null;
            if (File.Exists(Path.Combine(dir, "caddy.exe"))) exe = "caddy.exe";
            else if (File.Exists(Path.Combine(dir, "frankenphp.exe"))) exe = "frankenphp.exe";
            if (exe == null)
            {
                System.Windows.MessageBox.Show(this,
                    "Neither caddy.exe nor frankenphp.exe was found in this directory.\r\nPlease choose the correct Caddy / FrankenPHP directory.",
                    "Invalid directory", MessageBoxButton.OK, MessageBoxImage.Error);
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
                // AvalonEdit preserves CRLF; normalize to LF for consistency with caddy.
                editor.Text = raw.Replace("\r\n", "\n");
            }
            finally
            {
                loading = false;
            }
            dirty = false;
            saveItem.IsEnabled = false;
            UpdateTitle();
        }

        private void Editor_TextChanged(object sender, EventArgs e)
        {
            if (loading) return;
            dirty = true;
            saveItem.IsEnabled = true;
            UpdateTitle();
        }

        private void UpdateTitle()
        {
            Title = (dirty ? "* " : "") + "Caddyfile - Caddy Tools Win";
        }

        // Returns true if there is nothing to save or the save succeeded.
        private bool Save()
        {
            if (!dirty) return true;
            try
            {
                File.WriteAllText(caddyFilePath, editor.Text, new UTF8Encoding(false));
                dirty = false;
                saveItem.IsEnabled = false;
                UpdateTitle();
                return true;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this, "Failed to save:\r\n" + ex.Message, "Save",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // If there are unsaved changes, ask the user to save or cancel the action.
        // Returns true to proceed, false to abort the operation.
        private bool CheckUnsaved(string action)
        {
            if (!dirty) return true;
            var r = System.Windows.MessageBox.Show(this,
                "You have unsaved changes.\r\nSave before " + action + "?",
                "Unsaved changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r == MessageBoxResult.Yes)
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
            System.Windows.MessageBox.Show(this, (code == 0 ? "Configuration is valid.\r\n\r\n" : "Validation failed.\r\n\r\n") + output,
                "Validate", MessageBoxButton.OK, code == 0 ? MessageBoxImage.Information : MessageBoxImage.Error);
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
                System.Windows.MessageBox.Show(this, "Format failed.\r\n\r\n" + output, "Format",
                    MessageBoxButton.OK, MessageBoxImage.Error);
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
                System.Windows.MessageBox.Show(this, (code == 0 ? "Reload triggered.\r\n\r\n" : "Reload failed.\r\n\r\n") + output,
                    "Reload", MessageBoxButton.OK, code == 0 ? MessageBoxImage.Information : MessageBoxImage.Error);
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
                System.Windows.MessageBox.Show(this, output, "Reload Service", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                System.Windows.MessageBox.Show(this, "Reload canceled (administrator permission was not granted).",
                    "Reload", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(this, "Reload failed:\r\n" + ex.Message, "Reload",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try { File.Delete(bat); } catch { }
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    // Adapter so System.Windows.Forms.FolderBrowserDialog can be parented to a WPF window.
    internal sealed class Win32Window : WF.IWin32Window
    {
        private readonly IntPtr _handle;
        public Win32Window(IntPtr handle) { _handle = handle; }
        public IntPtr Handle { get { return _handle; } }
    }
}
