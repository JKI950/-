using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace KBrowser
{
    internal sealed class BrowserTab
    {
        public Panel Header;
        public PictureBox Favicon;
        public Label Title;
        public WebView2 View;
        public string LastUrl = "about:blank";
    }

    internal sealed class MainForm : Form
    {
        private const string GoogleHome = "https://www.google.com/";
        private readonly bool _sessionOnly;
        private readonly string _sessionDirectory;
        private readonly string _initialUrl;
        private readonly BrowserSettings _settings;
        private readonly List<BookmarkItem> _bookmarks;
        private readonly List<HiddenRule> _hiddenRules;
        private readonly List<BrowserTab> _tabs = new List<BrowserTab>();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private CoreWebView2Environment _environment;
        private BrowserTab _active;

        private readonly FlowLayoutPanel _tabsBar = new FlowLayoutPanel();
        private readonly Button _newTab = new Button();
        private readonly TableLayoutPanel _toolbar = new TableLayoutPanel();
        private readonly FlowLayoutPanel _bookmarksBar = new FlowLayoutPanel();
        private readonly Panel _content = new Panel();
        private readonly Panel _addressHost = new Panel();
        private readonly TextBox _address = new TextBox();
        private readonly Button _back = ButtonOf("◀");
        private readonly Button _forward = ButtonOf("▶");
        private readonly Button _reload = ButtonOf("↻");
        private readonly Button _home = ButtonOf("⌂");
        private readonly Button _go = new Button();
        private readonly Button _star = ButtonOf("☆");
        private readonly Button _favoritesButton = ButtonOf("🔖");
        private readonly Button _unlockButton = ButtonOf("🖱");
        private readonly Button _printButton = ButtonOf("🖨");
        private readonly Button _devButton = ButtonOf("<>");
        private readonly Button _menuButton = ButtonOf("⋮");
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly ContextMenuStrip _addressMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip _favoritesMenu = new ContextMenuStrip();

        private Point _bookmarkDragStart;
        private BookmarkItem _bookmarkDragItem;
        private bool _bookmarkDragging;

        public MainForm(bool sessionOnly, string initialUrl = null)
        {
            _sessionOnly = sessionOnly;
            _initialUrl = string.IsNullOrWhiteSpace(initialUrl) ? null : initialUrl;
            _sessionDirectory = sessionOnly ? AppPaths.CreateSessionDirectory() : null;
            _settings = Store.LoadSettings();
            if (_settings.PopupAllowedHosts == null) _settings.PopupAllowedHosts = new List<string>();
            _bookmarks = Store.LoadBookmarks();
            _hiddenRules = Store.LoadHiddenRules();

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
            return new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                Margin = new Padding(1),
                FlatStyle = FlatStyle.System,
                Font = new Font("Malgun Gothic", 9.75F)
            };
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
            _toolbar.ColumnCount = 11;
            _toolbar.RowCount = 1;
            _toolbar.BackColor = Color.FromArgb(242, 246, 249);
            for (var i = 0; i < 4; i++) _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 40));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));

            BuildAddressBox();

            _favoritesButton.Font = new Font("Segoe UI Emoji", 11F);
            _favoritesButton.Text = "★";
            _favoritesButton.AccessibleName = "즐겨찾기";
            _unlockButton.Font = new Font("Segoe UI Emoji", 10F);
            _unlockButton.Text = "🖱";
            _unlockButton.AccessibleName = "제한 해제";
            _printButton.Font = new Font("Segoe UI Symbol", 11F);
            _printButton.Text = "▣";
            _printButton.AccessibleName = "인쇄";
            _devButton.Font = new Font("Consolas", 9F, FontStyle.Bold);

            _toolbar.Controls.Add(_back, 0, 0);
            _toolbar.Controls.Add(_forward, 1, 0);
            _toolbar.Controls.Add(_reload, 2, 0);
            _toolbar.Controls.Add(_home, 3, 0);
            _toolbar.Controls.Add(_addressHost, 4, 0);
            _toolbar.Controls.Add(_star, 5, 0);
            _toolbar.Controls.Add(_favoritesButton, 6, 0);
            _toolbar.Controls.Add(_unlockButton, 7, 0);
            _toolbar.Controls.Add(_printButton, 8, 0);
            _toolbar.Controls.Add(_devButton, 9, 0);
            _toolbar.Controls.Add(_menuButton, 10, 0);

            _back.Click += (s, e) => { if (_active != null && _active.View.CanGoBack) _active.View.GoBack(); };
            _forward.Click += (s, e) => { if (_active != null && _active.View.CanGoForward) _active.View.GoForward(); };
            _reload.Click += (s, e) => _active?.View.Reload();
            _home.Click += async (s, e) => await NavigateCurrentAsync(HomeUrl);
            _star.Click += (s, e) => ToggleBookmark();
            _favoritesButton.Click += (s, e) => ShowFavoritesMenu();
            _unlockButton.Click += async (s, e) => await UnlockPageAsync();
            _printButton.Click += async (s, e) => await PrintPageAsync();
            _devButton.Click += (s, e) => OpenDevTools();
            _menuButton.Click += (s, e) => _menu.Show(_menuButton, new Point(0, _menuButton.Height));

            _bookmarksBar.Dock = DockStyle.Top;
            _bookmarksBar.Height = 27;
            _bookmarksBar.Padding = new Padding(2, 1, 2, 1);
            _bookmarksBar.WrapContents = false;
            _bookmarksBar.AutoScroll = true;
            _bookmarksBar.BackColor = Color.FromArgb(248, 250, 252);
            _bookmarksBar.AllowDrop = true;
            _bookmarksBar.DragEnter += BookmarkBar_DragEnter;
            _bookmarksBar.DragOver += BookmarkBar_DragEnter;
            _bookmarksBar.DragDrop += BookmarkBar_DragDrop;

            _content.Dock = DockStyle.Fill;
            _content.BackColor = Color.White;

            Controls.Add(_content);
            Controls.Add(_bookmarksBar);
            Controls.Add(_toolbar);
            Controls.Add(_tabsBar);

            ConfigureAddressMenu();
            RefreshBookmarks();
        }

        private void BuildAddressBox()
        {
            _addressHost.Dock = DockStyle.Fill;
            _addressHost.Margin = new Padding(2, 4, 2, 3);
            _addressHost.BackColor = Color.White;
            _addressHost.BorderStyle = BorderStyle.FixedSingle;

            _go.Dock = DockStyle.Right;
            _go.Width = 30;
            _go.FlatStyle = FlatStyle.Flat;
            _go.FlatAppearance.BorderSize = 0;
            _go.BackColor = Color.White;
            _go.Image = CreateGoGlyph();
            _go.ImageAlign = ContentAlignment.MiddleCenter;
            _go.TabStop = false;
            _go.AccessibleName = "이동";
            _go.Cursor = Cursors.Hand;
            _go.Click += async (s, e) => await NavigateCurrentAsync(_address.Text);

            _address.Dock = DockStyle.Fill;
            _address.Margin = new Padding(0);
            _address.BorderStyle = BorderStyle.None;
            _address.Font = new Font("Malgun Gothic", 10F);
            _address.ShortcutsEnabled = true;
            _address.KeyDown += Address_KeyDown;

            var inset = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 5, 2, 2), BackColor = Color.White };
            inset.Controls.Add(_address);
            _addressHost.Controls.Add(inset);
            _addressHost.Controls.Add(_go);
            _go.BringToFront();
        }

        private static Bitmap CreateGoGlyph()
        {
            var bmp = new Bitmap(22, 18);
            using (var g = Graphics.FromImage(bmp))
            using (var pen = new Pen(Color.FromArgb(95, 105, 115), 2.2F))
            using (var brush = new SolidBrush(Color.FromArgb(95, 105, 115)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                g.DrawLine(pen, 3, 9, 15, 9);
                g.FillPolygon(brush, new[] { new PointF(12, 4), new PointF(19, 9), new PointF(12, 14) });
            }
            return bmp;
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
            _menu.Items.Clear();
            _menu.Items.Add("새 탭", null, async (s, e) => await AddTabAsync(HomeUrl, true));
            _menu.Items.Add("사생활 보호창", null, (s, e) => new MainForm(true).Show());

            var home = new ToolStripMenuItem("기본 홈페이지");
            home.DropDownItems.Add("Google", null, (s, e) => SetHome("https://www.google.com/"));
            home.DropDownItems.Add("Naver", null, (s, e) => SetHome("https://www.naver.com/"));
            home.DropDownItems.Add("Daum", null, (s, e) => SetHome("https://www.daum.net/"));
            home.DropDownItems.Add("현재 사이트", null, (s, e) => { if (_active != null) SetHome(_active.LastUrl); });
            home.DropDownItems.Add("주소 직접 입력...", null, (s, e) => SetCustomHome());
            _menu.Items.Add(home);

            _menu.Items.Add("코드 - 현재 페이지 HTML 저장", null, async (s, e) => await SavePageCodeAsync());
            _menu.Items.Add("애니메 - 우클릭 선택 효과 저장", null, async (s, e) => await SaveEffectAsync(_active));

            var bookmarkBar = new ToolStripMenuItem("즐겨찾기 모음 표시") { CheckOnClick = true, Checked = _settings.ShowBookmarksBar };
            bookmarkBar.CheckedChanged += (s, e) =>
            {
                _settings.ShowBookmarksBar = bookmarkBar.Checked;
                _bookmarksBar.Visible = bookmarkBar.Checked;
                SaveSettings();
            };
            _menu.Items.Add(bookmarkBar);

            var bookmarkMode = new ToolStripMenuItem("북마크 표시 방식");
            AddBookmarkModeItem(bookmarkMode, "파비콘 + 이름", "IconName");
            AddBookmarkModeItem(bookmarkMode, "파비콘 + 이름 3자", "IconShort");
            AddBookmarkModeItem(bookmarkMode, "파비콘만", "IconOnly");
            _menu.Items.Add(bookmarkMode);

            var popupBlock = new ToolStripMenuItem("팝업 차단") { CheckOnClick = true, Checked = _settings.PopupBlockEnabled };
            popupBlock.CheckedChanged += (s, e) => { _settings.PopupBlockEnabled = popupBlock.Checked; SaveSettings(); };
            _menu.Items.Add(popupBlock);
            _menu.Items.Add("현재 사이트 팝업 허용/차단", null, (s, e) => ToggleCurrentSitePopupPermission());

            _menu.Items.Add("다운로드 폴더 설정...", null, (s, e) => ChooseDownloadFolder());
            _menu.Items.Add("개발자 도구 (F12)", null, (s, e) => OpenDevTools());
            _menu.Items.Add("현재 탭 닫기", null, (s, e) => CloseTab(_active));
            _menu.Items.Add("종료", null, (s, e) => Close());
        }

        private void AddBookmarkModeItem(ToolStripMenuItem parent, string text, string mode)
        {
            var item = new ToolStripMenuItem(text) { Checked = string.Equals(_settings.BookmarkDisplayMode, mode, StringComparison.OrdinalIgnoreCase) };
            item.Click += (s, e) =>
            {
                _settings.BookmarkDisplayMode = mode;
                SaveSettings();
                RefreshBookmarks();
                BuildMenu();
            };
            parent.DropDownItems.Add(item);
        }

        private void ShowFavoritesMenu()
        {
            _favoritesMenu.Items.Clear();
            var show = new ToolStripMenuItem("즐겨찾기 모음 표시") { Checked = _settings.ShowBookmarksBar, CheckOnClick = true };
            show.CheckedChanged += (s, e) =>
            {
                _settings.ShowBookmarksBar = show.Checked;
                _bookmarksBar.Visible = show.Checked;
                SaveSettings();
            };
            _favoritesMenu.Items.Add(show);

            var modes = new ToolStripMenuItem("표시 방식");
            AddFavoriteMenuMode(modes, "파비콘 + 이름", "IconName");
            AddFavoriteMenuMode(modes, "파비콘 + 이름 3자", "IconShort");
            AddFavoriteMenuMode(modes, "파비콘만", "IconOnly");
            _favoritesMenu.Items.Add(modes);

            foreach (var b in _bookmarks.ToList())
            {
                var mi = new ToolStripMenuItem(b.Name);
                mi.Click += async (s, e) => await NavigateCurrentAsync(b.Url);
                _favoritesMenu.Items.Add(mi);
            }
            _favoritesMenu.Show(_favoritesButton, new Point(0, _favoritesButton.Height));
        }

        private void AddFavoriteMenuMode(ToolStripMenuItem parent, string text, string mode)
        {
            var mi = new ToolStripMenuItem(text) { Checked = string.Equals(_settings.BookmarkDisplayMode, mode, StringComparison.OrdinalIgnoreCase) };
            mi.Click += (s, e) =>
            {
                _settings.BookmarkDisplayMode = mode;
                SaveSettings();
                RefreshBookmarks();
            };
            parent.DropDownItems.Add(mi);
        }

        private void SaveSettings()
        {
            if (!_sessionOnly) Store.SaveSettings(_settings);
        }

        private async Task StartEngineAsync()
        {
            try
            {
                var userData = _sessionOnly ? _sessionDirectory : AppPaths.UserDataDirectory;
                _environment = await CoreWebView2Environment.CreateAsync(null, userData);
                await AddTabAsync(_initialUrl ?? HomeUrl, true);
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
                await ConfigureCoreAsync(tab);
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
            var panel = new Panel { Width = 190, Height = 25, Margin = new Padding(1, 1, 0, 0), BackColor = Color.FromArgb(207, 218, 228), Cursor = Cursors.Hand };
            var icon = new PictureBox { Left = 5, Top = 5, Width = 16, Height = 16, SizeMode = PictureBoxSizeMode.Zoom, Cursor = Cursors.Hand };
            var title = new Label { Text = "새 탭", AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Left = 24, Top = 2, Width = 134, Height = 21, Cursor = Cursors.Hand };
            var close = new Button { Text = "×", Left = 162, Top = 1, Width = 26, Height = 23, FlatStyle = FlatStyle.Flat };
            close.FlatAppearance.BorderSize = 0;
            panel.Controls.Add(icon);
            panel.Controls.Add(title);
            panel.Controls.Add(close);
            tab.Favicon = icon;
            tab.Title = title;
            panel.Click += (s, e) => ActivateTab(tab);
            icon.Click += (s, e) => ActivateTab(tab);
            title.Click += (s, e) => ActivateTab(tab);
            close.Click += (s, e) => CloseTab(tab);
            panel.MouseUp += (s, e) => { if (e.Button == MouseButtons.Middle) CloseTab(tab); };
            title.MouseUp += (s, e) => { if (e.Button == MouseButtons.Middle) CloseTab(tab); };
            return panel;
        }

        private async Task ConfigureCoreAsync(BrowserTab tab)
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

            await core.AddScriptToExecuteOnDocumentCreatedAsync(GetPageHelperScript());

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
            core.NavigationCompleted += async (s, e) =>
            {
                if (_active == tab)
                {
                    _address.Text = core.Source ?? tab.LastUrl;
                    UpdateNav();
                    UpdateStar();
                }
                await ApplyHiddenRulesAsync(tab);
                await UpdateTabFaviconAsync(tab);
            };
            core.NewWindowRequested += async (s, e) => await HandleNewWindowAsync(tab, e);
            core.DownloadStarting += Core_DownloadStarting;
            core.ContextMenuRequested += (s, e) => HandleContextMenuRequested(tab, e);
        }

        private async Task HandleNewWindowAsync(BrowserTab sourceTab, CoreWebView2NewWindowRequestedEventArgs e)
        {
            var sourceHost = HostOf(sourceTab?.LastUrl);
            var allowPopup = IsPopupAllowed(sourceHost);
            if (_settings.PopupBlockEnabled && !allowPopup && !e.IsUserInitiated)
            {
                e.Handled = true;
                return;
            }

            var deferral = e.GetDeferral();
            try
            {
                var child = await AddTabAsync("about:blank", true);
                if (child != null && child.View.CoreWebView2 != null)
                {
                    e.NewWindow = child.View.CoreWebView2;
                    e.Handled = true;
                }
            }
            catch
            {
                if (!string.IsNullOrWhiteSpace(e.Uri))
                {
                    e.Handled = true;
                    await AddTabAsync(e.Uri, true);
                }
            }
            finally { deferral.Complete(); }
        }

        private void HandleContextMenuRequested(BrowserTab tab, CoreWebView2ContextMenuRequestedEventArgs e)
        {
            var target = e.ContextMenuTarget;
            var location = e.Location;
            var linkUri = target != null && target.HasLinkUri ? target.LinkUri : null;
            var sourceUri = target != null && target.HasSourceUri ? target.SourceUri : null;
            var selection = target != null && target.HasSelection ? target.SelectionText : null;
            var editable = target != null && target.IsEditable;

            e.Handled = true;
            BeginInvoke((Action)(() =>
            {
                var menu = new ContextMenuStrip();

                if (!string.IsNullOrWhiteSpace(linkUri))
                {
                    menu.Items.Add("새 탭에서 링크 열기", null, async (s, a) => await AddTabAsync(linkUri, true));
                    menu.Items.Add("링크 미리보기", null, async (s, a) => await ShowLinkPreviewAsync(linkUri));
                    menu.Items.Add("사생활 보호창에서 링크 열기", null, (s, a) => new MainForm(true, linkUri).Show());
                    menu.Items.Add("링크 주소 복사", null, (s, a) => SafeClipboard(linkUri));
                }

                if (!string.IsNullOrWhiteSpace(sourceUri))
                    menu.Items.Add("이미지/미디어 주소 복사", null, (s, a) => SafeClipboard(sourceUri));

                if (!string.IsNullOrWhiteSpace(selection))
                    menu.Items.Add("선택 내용 복사", null, (s, a) => SafeClipboard(selection));

                if (editable)
                {
                    menu.Items.Add("붙여넣기", null, (s, a) =>
                    {
                        tab.View.Focus();
                        SendKeys.Send("^v");
                    });
                    menu.Items.Add("전체 선택", null, (s, a) =>
                    {
                        tab.View.Focus();
                        SendKeys.Send("^a");
                    });
                }

                menu.Items.Add("효과 저장하기", null, async (s, a) => await SaveEffectAsync(tab));
                menu.Items.Add("안보이게 하기", null, async (s, a) => await HideSelectedElementAsync(tab));
                menu.Items.Add("페이지 새로고침", null, (s, a) => tab.View.Reload());

                menu.Show(tab.View, new Point(location.X, location.Y));
            }));
        }

        private async Task ShowLinkPreviewAsync(string url)
        {
            if (_environment == null || string.IsNullOrWhiteSpace(url)) return;
            var preview = new Form
            {
                Text = "KBrowser - 링크 미리보기",
                Width = 920,
                Height = 650,
                StartPosition = FormStartPosition.CenterParent,
                ShowInTaskbar = false
            };
            var view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.White };
            preview.Controls.Add(view);
            preview.Shown += async (s, e) =>
            {
                try
                {
                    await view.EnsureCoreWebView2Async(_environment);
                    view.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    view.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                    view.CoreWebView2.Navigate(NormalizeAddress(url));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(preview, "링크 미리보기를 열지 못했습니다.\r\n" + ex.Message, "KBrowser", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    preview.Close();
                }
            };
            preview.Show(this);
            await Task.CompletedTask;
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
            try
            {
                if (tab.Favicon?.Image != null) tab.Favicon.Image.Dispose();
                _tabsBar.Controls.Remove(tab.Header);
                tab.Header.Dispose();
            }
            catch { }
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

        private async void ToggleBookmark()
        {
            if (_active == null || string.IsNullOrWhiteSpace(_active.LastUrl) || _active.LastUrl == "about:blank") return;
            var old = _bookmarks.FirstOrDefault(x => string.Equals(x.Url, _active.LastUrl, StringComparison.OrdinalIgnoreCase));
            if (old != null) _bookmarks.Remove(old);
            else
            {
                var title = _active.View.CoreWebView2?.DocumentTitle;
                var favicon = await GetCurrentFaviconUrlAsync(_active);
                _bookmarks.Insert(0, new BookmarkItem
                {
                    Name = string.IsNullOrWhiteSpace(title) ? _active.LastUrl : title,
                    Url = _active.LastUrl,
                    FaviconUrl = favicon
                });
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
            foreach (var c in _bookmarksBar.Controls.Cast<Control>().ToList())
            {
                var pb = c.Controls.OfType<PictureBox>().FirstOrDefault();
                if (pb?.Image != null) pb.Image.Dispose();
                c.Dispose();
            }
            _bookmarksBar.Controls.Clear();

            foreach (var item in _bookmarks.ToList())
            {
                var control = CreateBookmarkControl(item);
                _bookmarksBar.Controls.Add(control);
            }
            _bookmarksBar.Visible = _settings.ShowBookmarksBar;
            _bookmarksBar.ResumeLayout(true);
        }

        private Control CreateBookmarkControl(BookmarkItem item)
        {
            var mode = _settings.BookmarkDisplayMode ?? "IconShort";
            var iconOnly = string.Equals(mode, "IconOnly", StringComparison.OrdinalIgnoreCase);
            var shortName = string.Equals(mode, "IconShort", StringComparison.OrdinalIgnoreCase);
            var text = shortName ? FirstChars(item.Name, 3) : (item.Name ?? "");
            var width = iconOnly ? 25 : Math.Max(48, Math.Min(150, 26 + TextRenderer.MeasureText(text, Font).Width));

            var p = new Panel
            {
                Width = width,
                Height = 23,
                Margin = new Padding(1),
                BackColor = Color.Transparent,
                Cursor = Cursors.Hand,
                Tag = item
            };
            var pic = new PictureBox
            {
                Left = 3,
                Top = 3,
                Width = 16,
                Height = 16,
                SizeMode = PictureBoxSizeMode.Zoom,
                Cursor = Cursors.Hand,
                Tag = item
            };
            p.Controls.Add(pic);

            Label label = null;
            if (!iconOnly)
            {
                label = new Label
                {
                    Left = 22,
                    Top = 1,
                    Width = width - 24,
                    Height = 21,
                    Text = text,
                    AutoEllipsis = true,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Cursor = Cursors.Hand,
                    Tag = item
                };
                p.Controls.Add(label);
            }

            var c = new ContextMenuStrip();
            c.Items.Add("열기", null, async (s, e) => await NavigateCurrentAsync(item.Url));
            c.Items.Add("편집", null, (s, e) => EditBookmark(item));
            c.Items.Add("삭제", null, (s, e) =>
            {
                _bookmarks.Remove(item);
                if (!_sessionOnly) Store.SaveBookmarks(_bookmarks);
                RefreshBookmarks();
            });
            p.ContextMenuStrip = c;
            pic.ContextMenuStrip = c;
            if (label != null) label.ContextMenuStrip = c;

            AttachBookmarkMouse(p, item);
            AttachBookmarkMouse(pic, item);
            if (label != null) AttachBookmarkMouse(label, item);
            _ = LoadBookmarkFaviconAsync(pic, item);
            return p;
        }

        private void AttachBookmarkMouse(Control control, BookmarkItem item)
        {
            control.MouseDown += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                _bookmarkDragStart = e.Location;
                _bookmarkDragItem = item;
                _bookmarkDragging = false;
            };
            control.MouseMove += (s, e) =>
            {
                if (e.Button != MouseButtons.Left || _bookmarkDragItem != item || _bookmarkDragging) return;
                if (Math.Abs(e.X - _bookmarkDragStart.X) < SystemInformation.DragSize.Width / 2 &&
                    Math.Abs(e.Y - _bookmarkDragStart.Y) < SystemInformation.DragSize.Height / 2) return;
                _bookmarkDragging = true;
                control.DoDragDrop(item, DragDropEffects.Move);
            };
            control.MouseUp += async (s, e) =>
            {
                if (e.Button == MouseButtons.Left && !_bookmarkDragging)
                    await NavigateCurrentAsync(item.Url);
                _bookmarkDragging = false;
                _bookmarkDragItem = null;
            };
        }

        private void BookmarkBar_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(typeof(BookmarkItem))) e.Effect = DragDropEffects.Move;
        }

        private void BookmarkBar_DragDrop(object sender, DragEventArgs e)
        {
            var item = e.Data.GetData(typeof(BookmarkItem)) as BookmarkItem;
            if (item == null) return;
            var oldIndex = _bookmarks.IndexOf(item);
            if (oldIndex < 0) return;

            var client = _bookmarksBar.PointToClient(new Point(e.X, e.Y));
            var newIndex = _bookmarks.Count - 1;
            for (var i = 0; i < _bookmarksBar.Controls.Count; i++)
            {
                var c = _bookmarksBar.Controls[i];
                if (client.X < c.Left + c.Width / 2) { newIndex = i; break; }
                newIndex = i + 1;
            }
            if (newIndex > oldIndex) newIndex--;
            newIndex = Math.Max(0, Math.Min(newIndex, _bookmarks.Count - 1));

            _bookmarks.RemoveAt(oldIndex);
            _bookmarks.Insert(newIndex, item);
            if (!_sessionOnly) Store.SaveBookmarks(_bookmarks);
            RefreshBookmarks();
        }

        private async Task LoadBookmarkFaviconAsync(PictureBox pic, BookmarkItem item)
        {
            var url = string.IsNullOrWhiteSpace(item.FaviconUrl) ? FallbackFavicon(item.Url) : item.FaviconUrl;
            var img = await DownloadImageAsync(url);
            if (img == null || pic.IsDisposed) { img?.Dispose(); return; }
            if (pic.Image != null) pic.Image.Dispose();
            pic.Image = img;
        }

        private async Task UpdateTabFaviconAsync(BrowserTab tab)
        {
            if (tab?.Favicon == null || tab.View?.CoreWebView2 == null) return;
            var url = await GetCurrentFaviconUrlAsync(tab);
            var img = await DownloadImageAsync(url);
            if (img == null || tab.Favicon.IsDisposed) { img?.Dispose(); return; }
            if (tab.Favicon.Image != null) tab.Favicon.Image.Dispose();
            tab.Favicon.Image = img;
        }

        private async Task<string> GetCurrentFaviconUrlAsync(BrowserTab tab)
        {
            try
            {
                var result = await tab.View.CoreWebView2.ExecuteScriptAsync(
                    "(()=>{var x=document.querySelector('link[rel~=\"icon\"],link[rel*=\"icon\"]');return x?x.href:(location.origin+'/favicon.ico')})()");
                var value = _json.Deserialize<string>(result);
                return string.IsNullOrWhiteSpace(value) ? FallbackFavicon(tab.LastUrl) : value;
            }
            catch { return FallbackFavicon(tab?.LastUrl); }
        }

        private static string FallbackFavicon(string url)
        {
            try
            {
                var u = new Uri(url);
                return u.Scheme + "://" + u.Authority + "/favicon.ico";
            }
            catch { return ""; }
        }

        private static async Task<Image> DownloadImageAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "KBrowser";
                    var bytes = await wc.DownloadDataTaskAsync(url);
                    using (var ms = new MemoryStream(bytes))
                    using (var temp = Image.FromStream(ms))
                        return new Bitmap(temp);
                }
            }
            catch { return null; }
        }

        private static string FirstChars(string text, int count)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= count) return text ?? "";
            return text.Substring(0, count);
        }

        private void EditBookmark(BookmarkItem item)
        {
            using (var f = new Form())
            using (var name = new TextBox())
            using (var url = new TextBox())
            using (var ok = new Button())
            using (var cancel = new Button())
            {
                f.Text = "즐겨찾기 편집";
                f.StartPosition = FormStartPosition.CenterParent;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.ClientSize = new Size(560, 125);
                f.MinimizeBox = false;
                f.MaximizeBox = false;

                var l1 = new Label { Text = "이름", Left = 10, Top = 15, Width = 55 };
                var l2 = new Label { Text = "주소", Left = 10, Top = 50, Width = 55 };
                name.SetBounds(68, 10, 480, 26);
                url.SetBounds(68, 45, 480, 26);
                name.Text = item.Name ?? "";
                url.Text = item.Url ?? "";
                ok.Text = "저장"; ok.SetBounds(382, 87, 80, 27); ok.DialogResult = DialogResult.OK;
                cancel.Text = "취소"; cancel.SetBounds(468, 87, 80, 27); cancel.DialogResult = DialogResult.Cancel;
                f.Controls.AddRange(new Control[] { l1, l2, name, url, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;

                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    item.Name = string.IsNullOrWhiteSpace(name.Text) ? item.Name : name.Text.Trim();
                    item.Url = NormalizeAddress(url.Text);
                    if (!_sessionOnly) Store.SaveBookmarks(_bookmarks);
                    RefreshBookmarks();
                }
            }
        }

        private void SetHome(string value)
        {
            var url = NormalizeAddress(value);
            if (string.IsNullOrWhiteSpace(url) || url == "about:blank") url = GoogleHome;
            _settings.HomeUrl = url;
            SaveSettings();
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
                SaveSettings();
            }
        }

        private async Task UnlockPageAsync()
        {
            if (_active?.View?.CoreWebView2 == null) return;
            try
            {
                await _active.View.CoreWebView2.ExecuteScriptAsync(
                    "window.__kbUnlock?window.__kbUnlock():(()=>{document.documentElement.style.userSelect='text';return true})()");
            }
            catch { }
        }

        private async Task PrintPageAsync()
        {
            if (_active?.View?.CoreWebView2 == null) return;
            try { await _active.View.CoreWebView2.ExecuteScriptAsync("window.print()"); } catch { }
        }

        private void OpenDevTools()
        {
            try { _active?.View.CoreWebView2?.OpenDevToolsWindow(); } catch { }
        }

        private async Task SavePageCodeAsync()
        {
            if (_active?.View?.CoreWebView2 == null) return;
            try
            {
                var raw = await _active.View.CoreWebView2.ExecuteScriptAsync("document.documentElement?document.documentElement.outerHTML:''");
                var html = _json.Deserialize<string>(raw) ?? "";
                using (var dlg = new SaveFileDialog())
                {
                    dlg.Title = "현재 페이지 HTML 저장";
                    dlg.Filter = "HTML 파일 (*.html)|*.html|모든 파일 (*.*)|*.*";
                    dlg.FileName = "page.html";
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    File.WriteAllText(dlg.FileName, html, new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "페이지 HTML 저장 실패\r\n" + ex.Message, "KBrowser", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async Task SaveEffectAsync(BrowserTab tab)
        {
            if (tab?.View?.CoreWebView2 == null) return;
            try
            {
                var raw = await tab.View.CoreWebView2.ExecuteScriptAsync("window.__kbCaptureEffect?window.__kbCaptureEffect():null");
                if (string.IsNullOrWhiteSpace(raw) || raw == "null")
                {
                    MessageBox.Show(this, "먼저 저장할 광고 박스, 카드, 버튼 또는 애니메이션 요소에서 오른쪽 마우스를 클릭해 주세요.", "KBrowser", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var effect = _json.Deserialize<EffectCapture>(raw);
                if (effect == null || string.IsNullOrWhiteSpace(effect.Html))
                {
                    MessageBox.Show(this, "선택한 요소의 정보를 가져오지 못했습니다.", "KBrowser", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.Description = "효과 파일을 저장할 폴더를 선택하세요.";
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    var folder = Path.Combine(dlg.SelectedPath, "KBrowser_Effect_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                    Directory.CreateDirectory(folder);

                    var css = (effect.Css ?? "") + Environment.NewLine +
                              (effect.ComputedCss ?? "") + Environment.NewLine +
                              (effect.Keyframes ?? "");
                    var baseHref = HtmlEscape(effect.PageUrl ?? "");
                    var html = "<!doctype html>\r\n<html><head><meta charset=\"utf-8\">" +
                               "<base href=\"" + baseHref + "\"><link rel=\"stylesheet\" href=\"effect.css\"></head><body>\r\n" +
                               (effect.Html ?? "") + "\r\n</body></html>";
                    File.WriteAllText(Path.Combine(folder, "effect.html"), html, new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(folder, "effect.css"), css, new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(folder, "source.json"), _json.Serialize(effect), new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(folder, "README.txt"),
                        "KBrowser 효과 저장 파일\r\n\r\n" +
                        "effect.html : 선택한 HTML 요소\r\n" +
                        "effect.css : 적용 CSS, 계산된 스타일, @keyframes\r\n" +
                        "source.json : 원본 페이지, CSS selector, 이미지/미디어 URL 등\r\n\r\n" +
                        "주의: PHP는 서버에서 실행되므로 브라우저가 서버의 PHP 원본 소스 파일을 받을 수 없습니다.\r\n" +
                        "따라서 PHP 실행 결과로 브라우저에 전달된 HTML/CSS/리소스 정보만 저장됩니다.\r\n",
                        new UTF8Encoding(false));

                    MessageBox.Show(this, "효과를 저장했습니다.\r\n" + folder, "KBrowser", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "효과 저장 실패\r\n" + ex.Message, "KBrowser", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async Task HideSelectedElementAsync(BrowserTab tab)
        {
            if (tab?.View?.CoreWebView2 == null) return;
            try
            {
                var raw = await tab.View.CoreWebView2.ExecuteScriptAsync("window.__kbHideLast?window.__kbHideLast():null");
                if (string.IsNullOrWhiteSpace(raw) || raw == "null")
                {
                    MessageBox.Show(this, "숨길 요소에서 오른쪽 마우스를 다시 클릭해 주세요.", "KBrowser", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                var hidden = _json.Deserialize<HideCapture>(raw);
                if (hidden == null || string.IsNullOrWhiteSpace(hidden.Host) || string.IsNullOrWhiteSpace(hidden.Selector)) return;

                if (!_hiddenRules.Any(x =>
                    string.Equals(x.Host, hidden.Host, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.Selector, hidden.Selector, StringComparison.Ordinal)))
                {
                    _hiddenRules.Add(new HiddenRule
                    {
                        Host = hidden.Host.ToLowerInvariant(),
                        Selector = hidden.Selector,
                        PageUrl = hidden.PageUrl,
                        CreatedAt = DateTime.Now.ToString("o")
                    });
                    if (!_sessionOnly) Store.SaveHiddenRules(_hiddenRules);
                }
                await ApplyHiddenRulesAsync(tab);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "선택 요소 차단 실패\r\n" + ex.Message, "KBrowser", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async Task ApplyHiddenRulesAsync(BrowserTab tab)
        {
            if (tab?.View?.CoreWebView2 == null) return;
            var host = HostOf(tab.LastUrl);
            if (string.IsNullOrWhiteSpace(host)) return;
            var selectors = _hiddenRules
                .Where(x => string.Equals(x.Host, host, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Selector)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct()
                .ToArray();
            if (selectors.Length == 0) return;
            try
            {
                await tab.View.CoreWebView2.ExecuteScriptAsync("window.__kbApplyRules&&window.__kbApplyRules(" + _json.Serialize(selectors) + ")");
            }
            catch { }
        }

        private void ToggleCurrentSitePopupPermission()
        {
            var host = HostOf(_active?.LastUrl);
            if (string.IsNullOrWhiteSpace(host))
            {
                MessageBox.Show(this, "현재 사이트 주소를 확인할 수 없습니다.", "KBrowser", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var old = _settings.PopupAllowedHosts.FirstOrDefault(x => string.Equals(x, host, StringComparison.OrdinalIgnoreCase));
            if (old != null)
            {
                _settings.PopupAllowedHosts.Remove(old);
                MessageBox.Show(this, host + "\r\n팝업 허용을 해제했습니다.", "KBrowser", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                _settings.PopupAllowedHosts.Add(host.ToLowerInvariant());
                MessageBox.Show(this, host + "\r\n이 사이트의 팝업을 허용했습니다.", "KBrowser", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            SaveSettings();
        }

        private bool IsPopupAllowed(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            return _settings.PopupAllowedHosts != null &&
                   _settings.PopupAllowedHosts.Any(x => string.Equals(x, host, StringComparison.OrdinalIgnoreCase));
        }

        private static string HostOf(string url)
        {
            try { return new Uri(url).Host.ToLowerInvariant(); } catch { return ""; }
        }

        private static void SafeClipboard(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try { Clipboard.SetText(text); } catch { }
        }

        private static string HtmlEscape(string text)
        {
            return (text ?? "").Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        private static string GetPageHelperScript()
        {
            return @"
(function(){
 if(window.__kbInstalled)return;
 window.__kbInstalled=true;
 window.__kbLastContext=null;
 window.__kbUnlockContext=false;

 function esc(s){return String(s||'').replace(/([ #;?%&,.+*~\':!^$\[\]()=>|\/@])/g,'\\$1');}
 function selector(el){
   if(!el||el.nodeType!==1)return '';
   if(el.id)return '#'+esc(el.id);
   var parts=[],cur=el,depth=0;
   while(cur&&cur.nodeType===1&&cur!==document.documentElement&&depth<6){
     var part=cur.tagName.toLowerCase();
     var cls=Array.prototype.slice.call(cur.classList||[]).filter(function(x){return x&&x.length<80;}).slice(0,3);
     if(cls.length)part+='.'+cls.map(esc).join('.');
     var parent=cur.parentElement;
     if(parent){
       try{
         var same=parent.querySelectorAll(':scope > '+part);
         if(same.length>1){
           var siblings=Array.prototype.filter.call(parent.children,function(x){return x.tagName===cur.tagName;});
           part+=':nth-of-type('+(siblings.indexOf(cur)+1)+')';
         }
       }catch(_){}
     }
     parts.unshift(part);
     try{if(document.querySelectorAll(parts.join(' > ')).length===1)break;}catch(_){}
     cur=parent;depth++;
   }
   return parts.join(' > ');
 }

 document.addEventListener('contextmenu',function(e){
   window.__kbLastContext=e.target;
   if(window.__kbUnlockContext)e.stopImmediatePropagation();
 },true);

 window.__kbUnlock=function(){
   window.__kbUnlockContext=true;
   try{
     var st=document.getElementById('__kb_unlock_style');
     if(!st){st=document.createElement('style');st.id='__kb_unlock_style';st.textContent='*{user-select:text!important;-webkit-user-select:text!important}';document.documentElement.appendChild(st);}
   }catch(_){}
   document.oncontextmenu=null;document.onselectstart=null;document.ondragstart=null;
   return true;
 };

 window.__kbApplyRules=function(list){
   if(!Array.isArray(list)||!list.length)return false;
   var st=document.getElementById('__kb_hidden_rules');
   if(!st){st=document.createElement('style');st.id='__kb_hidden_rules';(document.head||document.documentElement).appendChild(st);}
   st.textContent=list.join(',\n')+'{display:none!important;visibility:hidden!important;}';
   return true;
 };

 window.__kbHideLast=function(){
   var el=window.__kbLastContext;
   if(!el||el.nodeType!==1)return null;
   var sel=selector(el);
   if(!sel)return null;
   try{
     var st=document.getElementById('__kb_hidden_rules_live');
     if(!st){st=document.createElement('style');st.id='__kb_hidden_rules_live';(document.head||document.documentElement).appendChild(st);}
     st.textContent+=(st.textContent?'\n':'')+sel+'{display:none!important;visibility:hidden!important;}';
     el.style.setProperty('display','none','important');
   }catch(_){}
   return {Host:location.hostname,Selector:sel,PageUrl:location.href};
 };

 function walkRules(rules,el,out,names){
   for(var i=0;i<rules.length;i++){
     var r=rules[i];
     try{
       if(r.cssRules){walkRules(r.cssRules,el,out,names);continue;}
       if(r.type===CSSRule.KEYFRAMES_RULE){
         if(names.indexOf(r.name)>=0)out.keys.push(r.cssText);
         continue;
       }
       if(r.selectorText){
         var matched=false;
         try{matched=el.matches(r.selectorText)||!!el.querySelector(r.selectorText);}catch(_){}
         if(matched)out.rules.push(r.cssText);
       }
     }catch(_){}
   }
 }

 window.__kbCaptureEffect=function(){
   var el=window.__kbLastContext;
   if(!el||el.nodeType!==1)return null;
   var sel=selector(el);
   var cs=getComputedStyle(el);
   var computed=[];
   for(var i=0;i<cs.length;i++){
     var p=cs[i];
     computed.push(p+':'+cs.getPropertyValue(p)+';');
   }
   var names=String(cs.animationName||'').split(',').map(function(x){return x.trim();}).filter(function(x){return x&&x!=='none';});
   var out={rules:[],keys:[]};
   for(var s=0;s<document.styleSheets.length;s++){
     try{walkRules(document.styleSheets[s].cssRules||[],el,out,names);}catch(_){}
   }
   var media=[];
   var nodes=[el].concat(Array.prototype.slice.call(el.querySelectorAll('[src],[poster],source,img,video,audio')));
   nodes.forEach(function(n){
     var u=n.currentSrc||n.src||n.poster;
     if(u&&media.indexOf(u)<0)media.push(u);
     try{
       var bg=getComputedStyle(n).backgroundImage||'';
       var re=/url\([""']?([^""')]+)[""']?\)/g,m;
       while((m=re.exec(bg))){var a=new URL(m[1],location.href).href;if(media.indexOf(a)<0)media.push(a);}
     }catch(_){}
   });
   return {
     Html:el.outerHTML,
     Css:out.rules.join('\n\n'),
     ComputedCss:(sel||el.tagName.toLowerCase())+'{\n'+computed.join('\n')+'\n}',
     Keyframes:out.keys.join('\n\n'),
     MediaUrls:media,
     PageUrl:location.href,
     Selector:sel,
     TagName:el.tagName,
     Title:document.title
   };
 };
})();";
        }

        private void MainForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F12)
            {
                e.SuppressKeyPress = true;
                OpenDevTools();
                return;
            }
            if (e.Control && e.KeyCode == Keys.L)
            {
                e.SuppressKeyPress = true;
                _address.Focus();
                _address.SelectAll();
                return;
            }
            if (e.Control && e.KeyCode == Keys.T)
            {
                e.SuppressKeyPress = true;
                _ = AddTabAsync(HomeUrl, true);
                return;
            }
            if (e.Control && e.KeyCode == Keys.W)
            {
                e.SuppressKeyPress = true;
                CloseTab(_active);
            }
        }

        private void MainForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            if (!_sessionOnly || string.IsNullOrWhiteSpace(_sessionDirectory)) return;
            try { Directory.Delete(_sessionDirectory, true); } catch { }
        }
    }
}
