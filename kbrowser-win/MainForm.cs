using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KBrowser
{
    internal sealed class BrowserTab
    {
        public Panel Header;
        public Label Title;
        public WebView2 View;
        public string LastUrl = "about:blank";
    }

    internal sealed class MainForm : Form
    {
        private const string GoogleHome = "https://www.google.com/";
        private readonly bool _sessionOnly;
        private readonly string _sessionDirectory;
        private readonly BrowserSettings _settings;
        private readonly List<BookmarkItem> _bookmarks;
        private readonly List<BrowserTab> _tabs = new List<BrowserTab>();
        private CoreWebView2Environment _environment;
        private BrowserTab _active;

        private readonly FlowLayoutPanel _tabsBar = new FlowLayoutPanel();
        private readonly Button _newTab = new Button();
        private readonly TableLayoutPanel _toolbar = new TableLayoutPanel();
        private readonly FlowLayoutPanel _bookmarksBar = new FlowLayoutPanel();
        private readonly Panel _content = new Panel();
        private readonly TextBox _address = new TextBox();
        private readonly Button _back = ButtonOf("◀");
        private readonly Button _forward = ButtonOf("▶");
        private readonly Button _reload = ButtonOf("↻");
        private readonly Button _home = ButtonOf("⌂");
        private readonly Button _go = ButtonOf("이동");
        private readonly Button _star = ButtonOf("☆");
        private readonly Button _menuButton = ButtonOf("⋮");
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly ContextMenuStrip _addressMenu = new ContextMenuStrip();

        public MainForm(bool sessionOnly)
        {
            _sessionOnly = sessionOnly;
            _sessionDirectory = sessionOnly ? AppPaths.CreateSessionDirectory() : null;
            _settings = Store.LoadSettings();
            _bookmarks = Store.LoadBookmarks();

            Text = sessionOnly ? "KBrowser - 사생활 보호" : "KBrowser";
            Width = 1280;
            Height = 820;
            MinimumSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Malgun Gothic", 9.75F);
            KeyPreview = true;

            BuildUi();
            BuildMenu();
            Load += async (s, e) => await StartEngineAsync();
            KeyDown += MainForm_KeyDown;
            FormClosed += MainForm_FormClosed;
        }

        private static Button ButtonOf(string text)
        {
            return new Button { Text = text, Dock = DockStyle.Fill, Margin = new Padding(1), FlatStyle = FlatStyle.System, Font = new Font("Malgun Gothic", 9.75F) };
        }

        private string HomeUrl
        {
            get
            {
                var url = NormalizeAddress(_settings.HomeUrl);
                return string.IsNullOrWhiteSpace(url) || url == "about:blank" ? GoogleHome : url;
            }
        }

        private void BuildUi()
        {
            _tabsBar.Dock = DockStyle.Top;
            _tabsBar.Height = 30;
            _tabsBar.WrapContents = false;
            _tabsBar.AutoScroll = true;
            _tabsBar.Padding = new Padding(2, 2, 2, 0);
            _tabsBar.BackColor = Color.FromArgb(224, 232, 240);

            _newTab.Text = "+";
            _newTab.Width = 30;
            _newTab.Height = 25;
            _newTab.Margin = new Padding(1, 1, 1, 0);
            _newTab.Click += async (s, e) => await AddTabAsync(HomeUrl, true);
            _tabsBar.Controls.Add(_newTab);

            _toolbar.Dock = DockStyle.Top;
            _toolbar.Height = 37;
            _toolbar.Padding = new Padding(2);
            _toolbar.ColumnCount = 9;
            _toolbar.RowCount = 1;
            _toolbar.BackColor = Color.FromArgb(242, 246, 249);
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));

            _address.Dock = DockStyle.Fill;
            _address.Margin = new Padding(2, 5, 2, 3);
            _address.BorderStyle = BorderStyle.FixedSingle;
            _address.Font = new Font("Malgun Gothic", 9.75F);
            _address.ShortcutsEnabled = true;
            _address.KeyDown += Address_KeyDown;

            _toolbar.Controls.Add(_back, 0, 0);
            _toolbar.Controls.Add(_forward, 1, 0);
            _toolbar.Controls.Add(_reload, 2, 0);
            _toolbar.Controls.Add(_home, 3, 0);
            _toolbar.Controls.Add(_address, 4, 0);
            _toolbar.Controls.Add(_go, 5, 0);
            _toolbar.Controls.Add(_star, 6, 0);
            _toolbar.Controls.Add(ButtonOf("F12"), 7, 0);
            _toolbar.Controls.Add(_menuButton, 8, 0);
            var devButton = _toolbar.GetControlFromPosition(7, 0) as Button;

            _back.Click += (s, e) => { if (_active != null && _active.View.CanGoBack) _active.View.GoBack(); };
            _forward.Click += (s, e) => { if (_active != null && _active.View.CanGoForward) _active.View.GoForward(); };
            _reload.Click += (s, e) => _active?.View.Reload();
            _home.Click += async (s, e) => await NavigateCurrentAsync(HomeUrl);
            _go.Click += async (s, e) => await NavigateCurrentAsync(_address.Text);
            _star.Click += (s, e) => ToggleBookmark();
            if (devButton != null) devButton.Click += (s, e) => OpenDevTools();
            _menuButton.Click += (s, e) => _menu.Show(_menuButton, new Point(0, _menuButton.Height));

            _bookmarksBar.Dock = DockStyle.Top;
            _bookmarksBar.Height = 27;
            _bookmarksBar.Padding = new Padding(2, 1, 2, 1);
            _bookmarksBar.WrapContents = false;
            _bookmarksBar.AutoScroll = true;
            _bookmarksBar.BackColor = Color.FromArgb(248, 250, 252);

            _content.Dock = DockStyle.Fill;
            _content.BackColor = Color.White;

            Controls.Add(_content);
            Controls.Add(_bookmarksBar);
            Controls.Add(_toolbar);
            Controls.Add(_tabsBar);

            ConfigureAddressMenu();
            RefreshBookmarks();
        }

        private void ConfigureAddressMenu()
        {
            _addressMenu.Items.Add("실행 취소", null, (s, e) => { if (_address.CanUndo) _address.Undo(); });
            _addressMenu.Items.Add("잘라내기", null, (s, e) => _address.Cut());
            _addressMenu.Items.Add("복사", null, (s, e) => _address.Copy());
            _addressMenu.Items.Add("붙여넣기", null, (s, e) => _address.Paste());
            _addressMenu.Items.Add("삭제", null, (s, e) => { if (_address.SelectionLength > 0) _address.SelectedText = ""; });
            _addressMenu.Items.Add("전체 선택", null, (s, e) => _address.SelectAll());
            _address.ContextMenuStrip = _addressMenu;
        }

        private void BuildMenu()
        {
            _menu.Items.Add("새 탭", null, async (s, e) => await AddTabAsync(HomeUrl, true));
            _menu.Items.Add("사생활 보호창", null, (s, e) => new MainForm(true).Show());

            var home = new ToolStripMenuItem("기본 홈페이지");
            home.DropDownItems.Add("Google", null, (s, e) => SetHome("https://www.google.com/"));
            home.DropDownItems.Add("Naver", null, (s, e) => SetHome("https://www.naver.com/"));
            home.DropDownItems.Add("Daum", null, (s, e) => SetHome("https://www.daum.net/"));
            home.DropDownItems.Add("현재 사이트", null, (s, e) => { if (_active != null) SetHome(_active.LastUrl); });
            home.DropDownItems.Add("주소 직접 입력...", null, (s, e) => SetCustomHome());
            _menu.Items.Add(home);

            var bookmarkBar = new ToolStripMenuItem("즐겨찾기 모음 표시") { CheckOnClick = true, Checked = _settings.ShowBookmarksBar };
            bookmarkBar.CheckedChanged += (s, e) =>
            {
                _settings.ShowBookmarksBar = bookmarkBar.Checked;
                _bookmarksBar.Visible = bookmarkBar.Checked;
                if (!_sessionOnly) Store.SaveSettings(_settings);
            };
            _menu.Items.Add(bookmarkBar);
            _menu.Items.Add("다운로드 폴더 설정...", null, (s, e) => ChooseDownloadFolder());
            _menu.Items.Add("개발자 도구 (F12)", null, (s, e) => OpenDevTools());
            _menu.Items.Add("현재 탭 닫기", null, (s, e) => CloseTab(_active));
            _menu.Items.Add("종료", null, (s, e) => Close());
        }

        private async Task StartEngineAsync()
        {
            try
            {
                var userData = _sessionOnly ? _sessionDirectory : AppPaths.UserDataDirectory;
                _environment = await CoreWebView2Environment.CreateAsync(null, userData);
                await AddTabAsync(HomeUrl, true);
            }
            catch (WebView2RuntimeNotFoundException)
            {
                var answer = MessageBox.Show(this,
                    "Microsoft Edge WebView2 Runtime이 필요합니다. Windows 11에는 기본 포함되어 있고 대부분의 Windows 10에도 설치되어 있습니다.\r\n\r\nMicrosoft 다운로드 페이지를 열까요?",
                    "KBrowser", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes)
                {
                    try { Process.Start("https://developer.microsoft.com/microsoft-edge/webview2/"); } catch { }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "브라우저 엔진 시작 실패\r\n\r\n" + ex.Message, "KBrowser", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task<BrowserTab> AddTabAsync(string initialUrl, bool activate)
        {
            if (_environment == null) return null;
            var tab = new BrowserTab();
            tab.Header = CreateTabHeader(tab);
            tab.View = new WebView2 { Dock = DockStyle.Fill, Visible = false, DefaultBackgroundColor = Color.White };
            _tabs.Add(tab);
            _content.Controls.Add(tab.View);
            _tabsBar.Controls.Add(tab.Header);
            _tabsBar.Controls.SetChildIndex(_newTab, _tabsBar.Controls.Count - 1);

            try
            {
                await tab.View.EnsureCoreWebView2Async(_environment);
                ConfigureCore(tab);
                if (activate) ActivateTab(tab);
                var target = NormalizeAddress(initialUrl);
                tab.View.CoreWebView2.Navigate(string.IsNullOrWhiteSpace(target) ? HomeUrl : target);
            }
            catch (Exception ex)
            {
                tab.Title.Text = "오류";
                MessageBox.Show(this, "탭을 열지 못했습니다.\r\n" + ex.Message, "KBrowser", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return tab;
        }

        private Panel CreateTabHeader(BrowserTab tab)
        {
            var panel = new Panel { Width = 184, Height = 25, Margin = new Padding(1, 1, 0, 0), BackColor = Color.FromArgb(207, 218, 228), Cursor = Cursors.Hand };
            var title = new Label { Text = "새 탭", AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Left = 6, Top = 2, Width = 146, Height = 21, Cursor = Cursors.Hand };
            var close = new Button { Text = "×", Left = 156, Top = 1, Width = 26, Height = 23, FlatStyle = FlatStyle.Flat };
            close.FlatAppearance.BorderSize = 0;
            panel.Controls.Add(title);
            panel.Controls.Add(close);
            tab.Title = title;
            panel.Click += (s, e) => ActivateTab(tab);
            title.Click += (s, e) => ActivateTab(tab);
            close.Click += (s, e) => CloseTab(tab);
            panel.MouseUp += (s, e) => { if (e.Button == MouseButtons.Middle) CloseTab(tab); };
            title.MouseUp += (s, e) => { if (e.Button == MouseButtons.Middle) CloseTab(tab); };
            return panel;
        }

        private void ConfigureCore(BrowserTab tab)
        {
            var core = tab.View.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.AreBrowserAcceleratorKeysEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            try
            {
                core.Profile.IsPasswordAutosaveEnabled = true;
                core.Profile.IsGeneralAutofillEnabled = true;
            }
            catch { }

            core.NavigationStarting += (s, e) =>
            {
                tab.LastUrl = e.Uri ?? tab.LastUrl;
                if (_active == tab) _address.Text = tab.LastUrl;
            };
            core.SourceChanged += (s, e) =>
            {
                tab.LastUrl = core.Source ?? tab.LastUrl;
                if (_active == tab) _address.Text = tab.LastUrl;
            };
            core.DocumentTitleChanged += (s, e) =>
            {
                var t = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "새 탭" : core.DocumentTitle;
                tab.Title.Text = t;
                if (_active == tab) Text = (_sessionOnly ? "KBrowser - 사생활 보호 - " : "KBrowser - ") + t;
            };
            core.HistoryChanged += (s, e) => UpdateNav();
            core.NavigationCompleted += (s, e) =>
            {
                if (_active == tab)
                {
                    _address.Text = core.Source ?? tab.LastUrl;
                    UpdateNav();
                    UpdateStar();
                }
            };
            core.NewWindowRequested += async (s, e) => await HandleNewWindowAsync(e);
            core.DownloadStarting += Core_DownloadStarting;
        }

        private async Task HandleNewWindowAsync(CoreWebView2NewWindowRequestedEventArgs e)
        {
            var deferral = e.GetDeferral();
            try
            {
                // 실제 새 WebView2를 새 탭으로 연결해 target=_blank, window.open,
                // about:blank 후 location 변경 방식 모두 사이트가 직접 처리하게 합니다.
                var child = await AddTabAsync("about:blank", true);
                if (child != null && child.View.CoreWebView2 != null)
                {
                    e.NewWindow = child.View.CoreWebView2;
                    e.Handled = true;
                }
            }
            catch { }
            finally { deferral.Complete(); }
        }

        private void Core_DownloadStarting(object sender, CoreWebView2DownloadStartingEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_settings.DownloadFolder) || !Directory.Exists(_settings.DownloadFolder)) return;
                var name = Path.GetFileName(e.ResultFilePath);
                if (string.IsNullOrWhiteSpace(name)) name = "download";
                e.ResultFilePath = UniquePath(_settings.DownloadFolder, name);
            }
            catch { }
        }

        private static string UniquePath(string folder, string fileName)
        {
            var candidate = Path.Combine(folder, fileName);
            if (!File.Exists(candidate)) return candidate;
            var name = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            for (var i = 1; i < 10000; i++)
            {
                candidate = Path.Combine(folder, name + " (" + i + ")" + ext);
                if (!File.Exists(candidate)) return candidate;
            }
            return Path.Combine(folder, Guid.NewGuid().ToString("N") + ext);
        }

        private void ActivateTab(BrowserTab tab)
        {
            if (tab == null || !_tabs.Contains(tab)) return;
            _active = tab;
            foreach (var item in _tabs)
            {
                item.View.Visible = item == tab;
                item.Header.BackColor = item == tab ? Color.White : Color.FromArgb(207, 218, 228);
            }
            tab.View.BringToFront();
            _address.Text = tab.View.CoreWebView2?.Source ?? tab.LastUrl;
            UpdateNav();
            UpdateStar();
        }

        private void CloseTab(BrowserTab tab)
        {
            if (tab == null || !_tabs.Contains(tab)) return;
            var index = _tabs.IndexOf(tab);
            var active = tab == _active;
            _tabs.Remove(tab);
            try { _content.Controls.Remove(tab.View); tab.View.Dispose(); } catch { }
            try { _tabsBar.Controls.Remove(tab.Header); tab.Header.Dispose(); } catch { }
            if (_tabs.Count == 0) { _ = AddTabAsync(HomeUrl, true); return; }
            if (active) ActivateTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
        }

        private async Task NavigateCurrentAsync(string text)
        {
            var target = NormalizeAddress(text);
            if (string.IsNullOrWhiteSpace(target)) return;
            if (_active == null) { await AddTabAsync(target, true); return; }
            try
            {
                _active.LastUrl = target;
                _address.Text = target;
                _active.View.CoreWebView2.Navigate(target);
                _active.View.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "주소로 이동하지 못했습니다.\r\n\r\n" + target + "\r\n\r\n" + ex.Message, "KBrowser", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static string NormalizeAddress(string value)
        {
            value = (value ?? "").Trim().Replace("\u200B", "");
            if (value.Equals("about:blank", StringComparison.OrdinalIgnoreCase)) return "about:blank";
            if (string.IsNullOrWhiteSpace(value)) return GoogleHome;
            Uri uri;
            if (Uri.TryCreate(value, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return uri.AbsoluteUri;
            if (!value.Contains(" ") && value.Contains(".") && Uri.TryCreate("https://" + value, UriKind.Absolute, out uri)) return uri.AbsoluteUri;
            return "https://www.google.com/search?q=" + Uri.EscapeDataString(value);
        }

        private void Address_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            e.Handled = true;
            _ = NavigateCurrentAsync(_address.Text);
        }

        private void UpdateNav()
        {
            _back.Enabled = _active != null && _active.View.CanGoBack;
            _forward.Enabled = _active != null && _active.View.CanGoForward;
        }

        private void ToggleBookmark()
        {
            if (_active == null || string.IsNullOrWhiteSpace(_active.LastUrl) || _active.LastUrl == "about:blank") return;
            var old = _bookmarks.FirstOrDefault(x => string.Equals(x.Url, _active.LastUrl, StringComparison.OrdinalIgnoreCase));
            if (old != null) _bookmarks.Remove(old);
            else
            {
                var title = _active.View.CoreWebView2?.DocumentTitle;
                _bookmarks.Insert(0, new BookmarkItem { Name = string.IsNullOrWhiteSpace(title) ? _active.LastUrl : title, Url = _active.LastUrl });
            }
            if (!_sessionOnly) Store.SaveBookmarks(_bookmarks);
            RefreshBookmarks();
            UpdateStar();
        }

        private void UpdateStar()
        {
            var url = _active?.LastUrl ?? "";
            _star.Text = _bookmarks.Any(x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase)) ? "★" : "☆";
        }

        private void RefreshBookmarks()
        {
            _bookmarksBar.SuspendLayout();
            _bookmarksBar.Controls.Clear();
            foreach (var item in _bookmarks.ToList())
            {
                var b = new Button { AutoSize = true, Height = 23, Text = item.Name, Tag = item, Margin = new Padding(1), Padding = new Padding(2, 0, 2, 0), FlatStyle = FlatStyle.System };
                b.Click += async (s, e) => await NavigateCurrentAsync(((BookmarkItem)((Control)s).Tag).Url);
                var c = new ContextMenuStrip();
                c.Items.Add("열기", null, async (s, e) => await NavigateCurrentAsync(item.Url));
                c.Items.Add("삭제", null, (s, e) => { _bookmarks.Remove(item); if (!_sessionOnly) Store.SaveBookmarks(_bookmarks); RefreshBookmarks(); });
                b.ContextMenuStrip = c;
                _bookmarksBar.Controls.Add(b);
            }
            _bookmarksBar.Visible = _settings.ShowBookmarksBar;
            _bookmarksBar.ResumeLayout(true);
        }

        private void SetHome(string value)
        {
            var url = NormalizeAddress(value);
            if (string.IsNullOrWhiteSpace(url) || url == "about:blank") url = GoogleHome;
            _settings.HomeUrl = url;
            if (!_sessionOnly) Store.SaveSettings(_settings);
            MessageBox.Show(this, "기본 홈페이지를 저장했습니다.\r\n" + url, "KBrowser", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void SetCustomHome()
        {
            using (var f = new Form())
            using (var box = new TextBox())
            using (var ok = new Button())
            using (var cancel = new Button())
            {
                f.Text = "기본 홈페이지 주소";
                f.StartPosition = FormStartPosition.CenterParent;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.ClientSize = new Size(520, 86);
                f.MinimizeBox = false; f.MaximizeBox = false;
                box.SetBounds(10, 10, 500, 26); box.Text = HomeUrl;
                ok.Text = "저장"; ok.SetBounds(344, 48, 80, 27); ok.DialogResult = DialogResult.OK;
                cancel.Text = "취소"; cancel.SetBounds(430, 48, 80, 27); cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(box); f.Controls.Add(ok); f.Controls.Add(cancel); f.AcceptButton = ok; f.CancelButton = cancel;
                if (f.ShowDialog(this) == DialogResult.OK) SetHome(box.Text);
            }
        }

        private void ChooseDownloadFolder()
        {
            using (var f = new FolderBrowserDialog())
            {
                f.Description = "KBrowser 다운로드 저장 폴더";
                if (Directory.Exists(_settings.DownloadFolder)) f.SelectedPath = _settings.DownloadFolder;
                if (f.ShowDialog(this) != DialogResult.OK) return;
                _settings.DownloadFolder = f.SelectedPath;
                if (!_sessionOnly) Store.SaveSettings(_settings);
            }
        }

        private void OpenDevTools()
        {
            try { _active?.View.CoreWebView2?.OpenDevToolsWindow(); } catch { }
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F12) { e.SuppressKeyPress = true; OpenDevTools(); return; }
            if (e.Control && e.KeyCode == Keys.L) { e.SuppressKeyPress = true; _address.Focus(); _address.SelectAll(); return; }
            if (e.Control && e.KeyCode == Keys.T) { e.SuppressKeyPress = true; _ = AddTabAsync(HomeUrl, true); return; }
            if (e.Control && e.KeyCode == Keys.W) { e.SuppressKeyPress = true; CloseTab(_active); }
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            if (!_sessionOnly || string.IsNullOrWhiteSpace(_sessionDirectory)) return;
            try { Directory.Delete(_sessionDirectory, true); } catch { }
        }
    }
}
