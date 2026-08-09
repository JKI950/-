using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace KBrowser
{
    internal sealed class BlockedSourceRule
    {
        public string Host { get; set; }
        public string Selector { get; set; }
        public string PageUrl { get; set; }
        public string CreatedAt { get; set; }
        public bool Enabled { get; set; } = true;

        public string Key => (Host ?? "") + "\n" + (Selector ?? "");
    }

    internal static class FeaturePatch
    {
        private static readonly HashSet<Form> Patched = new HashSet<Form>();
        private static readonly Timer Watcher = new Timer { Interval = 500 };
        private static Image _fallbackIcon;

        public static void EnableGlobal()
        {
            try
            {
                _fallbackIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)?.ToBitmap();
            }
            catch { }

            Watcher.Tick += (s, e) =>
            {
                foreach (Form form in Application.OpenForms.Cast<Form>().ToArray())
                {
                    if (form.GetType().Name != "MainForm" || Patched.Contains(form)) continue;
                    Patched.Add(form);
                    try { new MainFormFeaturePatch(form, _fallbackIcon).Attach(); } catch { }
                }
            };
            Watcher.Start();
        }
    }

    internal sealed class MainFormFeaturePatch
    {
        private readonly Form _form;
        private readonly Image _fallbackIcon;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();
        private readonly List<BlockedSourceRule> _rules = new List<BlockedSourceRule>();
        private readonly HashSet<CoreWebView2> _wiredCores = new HashSet<CoreWebView2>();
        private readonly Timer _syncTimer = new Timer { Interval = 700 };

        private Panel _content;
        private Panel _devHost;
        private Panel _devHeader;
        private CheckBox _detach;
        private Button _closeDev;
        private Label _devCaption;
        private IntPtr _devWindow = IntPtr.Zero;
        private IntPtr _oldParent = IntPtr.Zero;
        private long _oldStyle;
        private bool _devDocked;
        private bool _closingDev;
        private bool _sessionOnly;
        private IList _mainRules;
        private Type _mainRuleType;
        private ToolStripMenuItem _blockListItem;

        private string RulesFile => Path.Combine(AppPaths.DataDirectory, "blocked-sources.json");

        private const string PreciseHideScript = @"
(function(){
  if (window.__kbPreciseHideInstalled) return;
  window.__kbPreciseHideInstalled = true;
  function esc(v){ return String(v||'').replace(/\\/g,'\\\\').replace(/\"/g,'\\\"'); }
  function cssEsc(v){
    if (window.CSS && CSS.escape) return CSS.escape(String(v));
    return String(v).replace(/([^a-zA-Z0-9_-])/g,'\\$1');
  }
  function unique(sel){ try { return !!sel && document.querySelectorAll(sel).length === 1; } catch(e){ return false; } }
  function selectorFor(el){
    if (!el || el.nodeType !== 1) return '';
    var tag=(el.tagName||'').toLowerCase();
    if (el.id) { var id='#'+cssEsc(el.id); if(unique(id)) return id; }
    var attrs=['src','href','data-id','data-ad','data-ad-id','data-banner-id','name'];
    for(var i=0;i<attrs.length;i++){
      var a=attrs[i], v=el.getAttribute && el.getAttribute(a);
      if(v){ var s=tag+'['+a+'=\"'+esc(v)+'\"]'; if(unique(s)) return s; }
    }
    var classes=[];
    try { classes=Array.prototype.slice.call(el.classList||[]).filter(function(x){return x && x.length<80;}).slice(0,4); } catch(e){}
    if(classes.length){ var cs=tag+classes.map(function(x){return '.'+cssEsc(x);}).join(''); if(unique(cs)) return cs; }
    var parts=[], cur=el, depth=0;
    while(cur && cur.nodeType===1 && cur!==document.documentElement && depth<7){
      var p=(cur.tagName||'').toLowerCase();
      if(cur.id){ p='#'+cssEsc(cur.id); parts.unshift(p); break; }
      var parent=cur.parentElement;
      if(parent){
        var same=Array.prototype.filter.call(parent.children,function(c){return c.tagName===cur.tagName;});
        if(same.length>1) p += ':nth-of-type('+(same.indexOf(cur)+1)+')';
      }
      parts.unshift(p); cur=parent; depth++;
    }
    return parts.join(' > ');
  }
  window.__kbHideLast=function(){
    var el=window.__kbLastTarget || window.__kbContextTarget || null;
    if(!el) return null;
    var selector=selectorFor(el);
    if(!selector) return null;
    return {Host:location.hostname,Selector:selector,PageUrl:location.href,CreatedAt:(new Date()).toISOString()};
  };
})();";

        public MainFormFeaturePatch(Form form, Image fallbackIcon)
        {
            _form = form;
            _fallbackIcon = fallbackIcon;
        }

        public void Attach()
        {
            _sessionOnly = ReadField<bool>("_sessionOnly");
            _content = ReadField<Panel>("_content");
            TryGetMainRuleList();
            LoadRules();
            BuildBottomDevTools();
            HookDevToolsTriggers();
            AddBlockListMenu();
            if (_sessionOnly) ApplyPrivateDarkTheme();
            ApplyFallbackAppIcon();

            _syncTimer.Tick += async (s, e) => await SynchronizeAsync();
            _syncTimer.Start();
            _form.FormClosed += (s, e) =>
            {
                _syncTimer.Stop();
                CloseDevTools();
            };
            _form.Resize += (s, e) => ResizeDockedDevTools();
            _ = SynchronizeAsync();
        }

        private T ReadField<T>(string name)
        {
            try
            {
                var f = _form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f == null) return default(T);
                var v = f.GetValue(_form);
                return v is T ? (T)v : default(T);
            }
            catch { return default(T); }
        }

        private object ReadFieldObject(string name)
        {
            try
            {
                var f = _form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                return f?.GetValue(_form);
            }
            catch { return null; }
        }

        private void TryGetMainRuleList()
        {
            try
            {
                var field = _form.GetType().GetField("_hiddenRules", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field == null) return;
                _mainRules = field.GetValue(_form) as IList;
                if (field.FieldType.IsGenericType) _mainRuleType = field.FieldType.GetGenericArguments()[0];
            }
            catch { }
        }

        private void ApplyFallbackAppIcon()
        {
            try
            {
                var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (icon != null) _form.Icon = icon;
            }
            catch { }
        }

        private void BuildBottomDevTools()
        {
            if (_content == null) return;
            _devHost = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 290,
                Visible = false,
                BackColor = Color.FromArgb(245, 245, 245),
                BorderStyle = BorderStyle.FixedSingle
            };
            _devHeader = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = Color.FromArgb(235, 235, 235) };
            _devCaption = new Label { Text = "개발자 도구", AutoSize = true, Left = 8, Top = 6 };
            _detach = new CheckBox { Text = "분리", AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right, Top = 5, Left = 100 };
            _closeDev = new Button { Text = "×", Width = 28, Height = 24, Anchor = AnchorStyles.Top | AnchorStyles.Right, FlatStyle = FlatStyle.Flat };
            _closeDev.FlatAppearance.BorderSize = 0;
            _devHeader.Controls.Add(_devCaption);
            _devHeader.Controls.Add(_detach);
            _devHeader.Controls.Add(_closeDev);
            _devHost.Controls.Add(_devHeader);
            _content.Controls.Add(_devHost);
            _devHost.BringToFront();

            Action layoutHeader = () =>
            {
                if (_devHeader == null) return;
                _closeDev.Left = Math.Max(0, _devHeader.ClientSize.Width - 31);
                _closeDev.Top = 2;
                _detach.Left = Math.Max(0, _closeDev.Left - 65);
            };
            _devHeader.Resize += (s, e) => layoutHeader();
            layoutHeader();
            _devHost.Resize += (s, e) => ResizeDockedDevTools();
            _detach.CheckedChanged += (s, e) =>
            {
                if (_devWindow == IntPtr.Zero || !NativeMethods.IsWindow(_devWindow)) return;
                if (_detach.Checked) DetachDevTools(); else DockDevTools(_devWindow);
            };
            _closeDev.Click += (s, e) => CloseDevTools();
        }

        private void HookDevToolsTriggers()
        {
            Button devButton = ReadField<Button>("_devButton");
            if (devButton == null)
            {
                var toolbar = ReadFieldObject("_toolbar") as Control;
                if (toolbar != null)
                    devButton = FindControls<Button>(toolbar).FirstOrDefault(b => b.Text == "<>" || b.Text == "F12");
            }
            if (devButton != null) devButton.Click += async (s, e) => await ToggleOrDockAfterBuiltInAsync();

            _form.KeyUp += async (s, e) =>
            {
                if (e.KeyCode == Keys.F12) await ToggleOrDockAfterBuiltInAsync();
            };

            try
            {
                var menu = ReadFieldObject("_menu") as ContextMenuStrip;
                if (menu != null)
                {
                    foreach (ToolStripItem item in menu.Items)
                    {
                        if (item.Text != null && item.Text.Contains("개발자 도구"))
                            item.Click += async (s, e) => await ToggleOrDockAfterBuiltInAsync();
                    }
                }
            }
            catch { }
        }

        private async Task ToggleOrDockAfterBuiltInAsync()
        {
            if (_closingDev) return;
            if (_devWindow != IntPtr.Zero && NativeMethods.IsWindow(_devWindow))
            {
                CloseDevTools();
                return;
            }

            for (int i = 0; i < 25; i++)
            {
                await Task.Delay(80);
                var hwnd = FindDevToolsWindow();
                if (hwnd == IntPtr.Zero) continue;
                _devWindow = hwnd;
                if (_detach != null && _detach.Checked) DetachDevTools(); else DockDevTools(hwnd);
                return;
            }
        }

        private IntPtr FindDevToolsWindow()
        {
            IntPtr found = IntPtr.Zero;
            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                try
                {
                    if (!NativeMethods.IsWindowVisible(hWnd) || NativeMethods.GetParent(hWnd) != IntPtr.Zero) return true;
                    var title = NativeMethods.GetWindowTitle(hWnd);
                    if (string.IsNullOrWhiteSpace(title)) return true;
                    var lt = title.ToLowerInvariant();
                    if (!(lt.Contains("devtools") || lt.Contains("developer tools") || title.Contains("개발자 도구"))) return true;
                    uint pid;
                    NativeMethods.GetWindowThreadProcessId(hWnd, out pid);
                    if (pid == 0) return true;
                    try
                    {
                        var p = Process.GetProcessById((int)pid);
                        if (!p.ProcessName.Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase) &&
                            !p.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase)) return true;
                    }
                    catch { return true; }
                    found = hWnd;
                    return false;
                }
                catch { return true; }
            }, IntPtr.Zero);
            return found;
        }

        private void DockDevTools(IntPtr hwnd)
        {
            if (_devHost == null || hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) return;
            try
            {
                _devHost.Visible = true;
                _devHost.BringToFront();
                _oldParent = NativeMethods.GetParent(hwnd);
                _oldStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
                long style = _oldStyle;
                style &= ~(NativeMethods.WS_POPUP | NativeMethods.WS_CAPTION | NativeMethods.WS_THICKFRAME |
                           NativeMethods.WS_MINIMIZEBOX | NativeMethods.WS_MAXIMIZEBOX | NativeMethods.WS_SYSMENU);
                style |= NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE;
                NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, new IntPtr(style));
                NativeMethods.SetParent(hwnd, _devHost.Handle);
                _devWindow = hwnd;
                _devDocked = true;
                if (_detach != null) _detach.Checked = false;
                ResizeDockedDevTools();
                _form.BeginInvoke((Action)(() => _content?.PerformLayout()));
            }
            catch { }
        }

        private void ResizeDockedDevTools()
        {
            if (!_devDocked || _devHost == null || _devWindow == IntPtr.Zero || !NativeMethods.IsWindow(_devWindow)) return;
            try
            {
                int top = _devHeader?.Height ?? 0;
                NativeMethods.MoveWindow(_devWindow, 0, top, Math.Max(20, _devHost.ClientSize.Width), Math.Max(20, _devHost.ClientSize.Height - top), true);
            }
            catch { }
        }

        private void DetachDevTools()
        {
            if (_devWindow == IntPtr.Zero || !NativeMethods.IsWindow(_devWindow)) return;
            try
            {
                NativeMethods.SetParent(_devWindow, _oldParent);
                long style = _oldStyle != 0 ? _oldStyle : NativeMethods.WS_OVERLAPPEDWINDOW | NativeMethods.WS_VISIBLE;
                style &= ~NativeMethods.WS_CHILD;
                style |= NativeMethods.WS_OVERLAPPEDWINDOW | NativeMethods.WS_VISIBLE;
                NativeMethods.SetWindowLongPtr(_devWindow, NativeMethods.GWL_STYLE, new IntPtr(style));
                NativeMethods.SetWindowPos(_devWindow, IntPtr.Zero, _form.Left + 80, _form.Top + 80, 1000, 700,
                    NativeMethods.SWP_FRAMECHANGED | NativeMethods.SWP_SHOWWINDOW);
                _devDocked = false;
                if (_devHost != null) _devHost.Visible = false;
            }
            catch { }
        }

        private void CloseDevTools()
        {
            if (_closingDev) return;
            _closingDev = true;
            try
            {
                if (_devWindow != IntPtr.Zero && NativeMethods.IsWindow(_devWindow))
                {
                    if (_devDocked)
                    {
                        NativeMethods.SetParent(_devWindow, IntPtr.Zero);
                        NativeMethods.SetWindowLongPtr(_devWindow, NativeMethods.GWL_STYLE,
                            new IntPtr((_oldStyle != 0 ? _oldStyle : NativeMethods.WS_OVERLAPPEDWINDOW) | NativeMethods.WS_VISIBLE));
                    }
                    NativeMethods.SendMessage(_devWindow, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
            }
            catch { }
            finally
            {
                _devWindow = IntPtr.Zero;
                _devDocked = false;
                if (_devHost != null) _devHost.Visible = false;
                _closingDev = false;
            }
        }

        private void AddBlockListMenu()
        {
            try
            {
                var menu = ReadFieldObject("_menu") as ContextMenuStrip;
                if (menu == null) return;
                _blockListItem = new ToolStripMenuItem("차단 소스 목록...");
                _blockListItem.Click += (s, e) => ShowBlockList();
                int insertAt = Math.Max(0, menu.Items.Count - 2);
                menu.Items.Insert(insertAt, _blockListItem);
            }
            catch { }
        }

        private void ShowBlockList()
        {
            MergeFromMainRules();
            using (var dlg = new Form())
            using (var list = new CheckedListBox())
            using (var delete = new Button())
            using (var close = new Button())
            using (var help = new Label())
            {
                dlg.Text = "KBrowser - 차단 소스 목록";
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ClientSize = new Size(760, 440);
                dlg.MinimumSize = new Size(620, 360);
                dlg.Font = _form.Font;

                help.Text = "체크 = 해당 소스 숨김   /   체크 해제 = 다시 보이기";
                help.Dock = DockStyle.Top;
                help.Height = 30;
                help.Padding = new Padding(8, 7, 0, 0);

                list.Dock = DockStyle.Fill;
                list.CheckOnClick = true;
                for (int i = 0; i < _rules.Count; i++)
                {
                    var r = _rules[i];
                    int n = list.Items.Add((r.Host ?? "") + "   " + (r.Selector ?? ""));
                    list.SetItemChecked(n, r.Enabled);
                }

                var bottom = new Panel { Dock = DockStyle.Bottom, Height = 42 };
                delete.Text = "선택 삭제";
                delete.SetBounds(8, 7, 92, 28);
                close.Text = "닫기";
                close.SetBounds(658, 7, 92, 28);
                close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                bottom.Controls.Add(delete);
                bottom.Controls.Add(close);

                list.ItemCheck += (s, e) =>
                {
                    if (e.Index < 0 || e.Index >= _rules.Count) return;
                    _rules[e.Index].Enabled = e.NewValue == CheckState.Checked;
                    dlg.BeginInvoke((Action)(async () =>
                    {
                        SaveRules();
                        SyncMainRulesToEnabled();
                        await ApplyRulesToAllTabsAsync();
                    }));
                };
                delete.Click += (s, e) =>
                {
                    int idx = list.SelectedIndex;
                    if (idx < 0 || idx >= _rules.Count) return;
                    _rules.RemoveAt(idx);
                    list.Items.RemoveAt(idx);
                    SaveRules();
                    SyncMainRulesToEnabled();
                    _ = ApplyRulesToAllTabsAsync();
                };
                close.Click += (s, e) => dlg.Close();

                dlg.Controls.Add(list);
                dlg.Controls.Add(bottom);
                dlg.Controls.Add(help);
                if (_sessionOnly) ApplyDarkRecursive(dlg);
                dlg.ShowDialog(_form);
            }
        }

        private void LoadRules()
        {
            try
            {
                if (!File.Exists(RulesFile)) return;
                var loaded = _json.Deserialize<List<BlockedSourceRule>>(File.ReadAllText(RulesFile));
                if (loaded == null) return;
                foreach (var r in loaded)
                {
                    if (string.IsNullOrWhiteSpace(r?.Selector)) continue;
                    _rules.Add(r);
                }
            }
            catch { }
        }

        private void SaveRules()
        {
            if (_sessionOnly) return;
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                File.WriteAllText(RulesFile, _json.Serialize(_rules));
            }
            catch { }
        }

        private void MergeFromMainRules()
        {
            if (_mainRules == null) TryGetMainRuleList();
            if (_mainRules == null) return;
            bool changed = false;
            try
            {
                foreach (var item in _mainRules.Cast<object>().ToArray())
                {
                    var host = GetProp(item, "Host");
                    var selector = GetProp(item, "Selector");
                    if (string.IsNullOrWhiteSpace(selector)) continue;
                    if (_rules.Any(r => Same(r.Host, host) && Same(r.Selector, selector))) continue;
                    _rules.Add(new BlockedSourceRule
                    {
                        Host = host,
                        Selector = selector,
                        PageUrl = GetProp(item, "PageUrl"),
                        CreatedAt = DateTime.UtcNow.ToString("o"),
                        Enabled = true
                    });
                    changed = true;
                }
            }
            catch { }
            if (changed) SaveRules();
        }

        private static bool Same(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

        private static string GetProp(object obj, string name)
        {
            try { return obj?.GetType().GetProperty(name)?.GetValue(obj, null)?.ToString() ?? ""; }
            catch { return ""; }
        }

        private void SyncMainRulesToEnabled()
        {
            if (_mainRules == null || _mainRuleType == null) return;
            try
            {
                _mainRules.Clear();
                foreach (var r in _rules.Where(x => x.Enabled))
                {
                    var obj = Activator.CreateInstance(_mainRuleType);
                    SetProp(obj, "Host", r.Host);
                    SetProp(obj, "Selector", r.Selector);
                    SetProp(obj, "PageUrl", r.PageUrl);
                    _mainRules.Add(obj);
                }
                var save = typeof(Store).GetMethod("SaveHiddenRules", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                save?.Invoke(null, new object[] { _mainRules });
            }
            catch { }
        }

        private static void SetProp(object obj, string name, object value)
        {
            try
            {
                var p = obj?.GetType().GetProperty(name);
                if (p != null && p.CanWrite) p.SetValue(obj, value, null);
            }
            catch { }
        }

        private async Task SynchronizeAsync()
        {
            try
            {
                MergeFromMainRules();
                SyncMainRulesToEnabled();
                await WireTabsAsync();
                ApplyFallbackFavicons();
                if (_devWindow != IntPtr.Zero && !NativeMethods.IsWindow(_devWindow))
                {
                    _devWindow = IntPtr.Zero;
                    _devDocked = false;
                    if (_devHost != null) _devHost.Visible = false;
                }
            }
            catch { }
        }

        private async Task WireTabsAsync()
        {
            var tabs = ReadFieldObject("_tabs") as IEnumerable;
            if (tabs == null) return;
            foreach (var tab in tabs.Cast<object>().ToArray())
            {
                WebView2 view = null;
                try
                {
                    var f = tab.GetType().GetField("View", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    view = f?.GetValue(tab) as WebView2;
                }
                catch { }
                var core = view?.CoreWebView2;
                if (core == null) continue;
                if (!_wiredCores.Contains(core))
                {
                    _wiredCores.Add(core);
                    try { await core.AddScriptToExecuteOnDocumentCreatedAsync(PreciseHideScript); } catch { }
                    core.NavigationCompleted += async (s, e) =>
                    {
                        try { await core.ExecuteScriptAsync(PreciseHideScript); } catch { }
                        await ApplyRulesAsync(core);
                    };
                    try { await core.ExecuteScriptAsync(PreciseHideScript); } catch { }
                }
                await ApplyRulesAsync(core);
            }
        }

        private async Task ApplyRulesToAllTabsAsync()
        {
            var tabs = ReadFieldObject("_tabs") as IEnumerable;
            if (tabs == null) return;
            foreach (var tab in tabs.Cast<object>().ToArray())
            {
                try
                {
                    var f = tab.GetType().GetField("View", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var v = f?.GetValue(tab) as WebView2;
                    if (v?.CoreWebView2 != null) await ApplyRulesAsync(v.CoreWebView2);
                }
                catch { }
            }
        }

        private async Task ApplyRulesAsync(CoreWebView2 core)
        {
            if (core == null) return;
            string host = "";
            try { host = new Uri(core.Source ?? "about:blank").Host; } catch { }
            var active = _rules.Where(r => r.Enabled && (string.IsNullOrWhiteSpace(r.Host) || Same(r.Host, host)))
                               .Select(r => r.Selector).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            var all = _rules.Where(r => string.IsNullOrWhiteSpace(r.Host) || Same(r.Host, host))
                            .Select(r => r.Selector).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
            string activeJson = _json.Serialize(active);
            string allJson = _json.Serialize(all);
            string js = "(function(){try{" +
                "var old=document.getElementById('__kb_blocked_sources_style');if(old)old.remove();" +
                "var all=" + allJson + ";all.forEach(function(s){try{document.querySelectorAll(s).forEach(function(el){if(el.dataset&&el.dataset.kbHidden==='1'){el.style.removeProperty('display');delete el.dataset.kbHidden;}});}catch(e){}});" +
                "var a=" + activeJson + ";if(a.length){var st=document.createElement('style');st.id='__kb_blocked_sources_style';st.textContent=a.map(function(s){return s+'{display:none !important;}';}).join('\\n');(document.head||document.documentElement).appendChild(st);}" +
                "}catch(e){}})();";
            try { await core.ExecuteScriptAsync(js); } catch { }
        }

        private void ApplyFallbackFavicons()
        {
            if (_fallbackIcon == null) return;
            try
            {
                foreach (var pic in FindControls<PictureBox>(_form))
                {
                    if (pic.IsDisposed || pic.Image != null) continue;
                    pic.Image = new Bitmap(_fallbackIcon);
                    pic.SizeMode = PictureBoxSizeMode.Zoom;
                }
            }
            catch { }
        }

        private void ApplyPrivateDarkTheme()
        {
            _form.BackColor = Color.FromArgb(32, 33, 36);
            _form.ForeColor = Color.Gainsboro;
            ApplyDarkRecursive(_form);
            try
            {
                var address = ReadField<TextBox>("_address");
                if (address != null)
                {
                    address.BackColor = Color.FromArgb(48, 49, 52);
                    address.ForeColor = Color.WhiteSmoke;
                    address.BorderStyle = BorderStyle.FixedSingle;
                }
                var menu = ReadFieldObject("_menu") as ContextMenuStrip;
                if (menu != null) ApplyDarkMenu(menu);
                var addrMenu = ReadFieldObject("_addressMenu") as ContextMenuStrip;
                if (addrMenu != null) ApplyDarkMenu(addrMenu);
            }
            catch { }
        }

        private static void ApplyDarkRecursive(Control root)
        {
            if (root == null) return;
            foreach (Control c in root.Controls)
            {
                if (c is WebView2) continue;
                if (c is TextBoxBase)
                {
                    c.BackColor = Color.FromArgb(48, 49, 52);
                    c.ForeColor = Color.WhiteSmoke;
                }
                else if (c is Button)
                {
                    c.BackColor = Color.FromArgb(48, 49, 52);
                    c.ForeColor = Color.Gainsboro;
                }
                else
                {
                    c.BackColor = Color.FromArgb(38, 39, 42);
                    c.ForeColor = Color.Gainsboro;
                }
                ApplyDarkRecursive(c);
            }
        }

        private static void ApplyDarkMenu(ContextMenuStrip menu)
        {
            menu.BackColor = Color.FromArgb(45, 46, 48);
            menu.ForeColor = Color.Gainsboro;
            foreach (ToolStripItem item in menu.Items)
            {
                item.BackColor = menu.BackColor;
                item.ForeColor = menu.ForeColor;
            }
        }

        private static IEnumerable<T> FindControls<T>(Control root) where T : Control
        {
            if (root == null) yield break;
            foreach (Control c in root.Controls)
            {
                var typed = c as T;
                if (typed != null) yield return typed;
                foreach (var child in FindControls<T>(c)) yield return child;
            }
        }
    }

    internal static class NativeMethods
    {
        internal const int GWL_STYLE = -16;
        internal const long WS_CHILD = 0x40000000L;
        internal const long WS_VISIBLE = 0x10000000L;
        internal const long WS_POPUP = 0x80000000L;
        internal const long WS_CAPTION = 0x00C00000L;
        internal const long WS_THICKFRAME = 0x00040000L;
        internal const long WS_MINIMIZEBOX = 0x00020000L;
        internal const long WS_MAXIMIZEBOX = 0x00010000L;
        internal const long WS_SYSMENU = 0x00080000L;
        internal const long WS_OVERLAPPEDWINDOW = 0x00CF0000L;
        internal const uint SWP_FRAMECHANGED = 0x0020;
        internal const uint SWP_SHOWWINDOW = 0x0040;
        internal const uint WM_CLOSE = 0x0010;

        internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        internal static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) => GetWindowLongPtrW(hWnd, nIndex);
        internal static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value) => SetWindowLongPtrW(hWnd, nIndex, value);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        internal static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        internal static string GetWindowTitle(IntPtr hWnd)
        {
            int len = GetWindowTextLength(hWnd);
            if (len <= 0) return "";
            var sb = new StringBuilder(len + 1);
            GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
