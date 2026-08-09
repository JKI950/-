using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System;
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
        public string Key { get { return (Host ?? "").ToLowerInvariant() + "\n" + (Selector ?? ""); } }
        public override string ToString()
        {
            var selector = Selector ?? "";
            if (selector.Length > 92) selector = selector.Substring(0, 92) + "...";
            return (Enabled ? "차단  " : "허용  ") + (Host ?? "") + "  |  " + selector;
        }
    }

    internal static class FeaturePatch
    {
        private static readonly HashSet<Form> Patched = new HashSet<Form>();
        private static readonly Timer Watcher = new Timer { Interval = 350 };

        public static void EnableGlobal()
        {
            Watcher.Tick += delegate
            {
                foreach (Form form in Application.OpenForms.Cast<Form>().ToArray())
                {
                    if (form.GetType().Name != "MainForm" || Patched.Contains(form)) continue;
                    Patched.Add(form);
                    try { new MainFormFeaturePatch(form).Attach(); } catch { }
                }
            };
            Watcher.Start();
        }
    }

    internal sealed class MainFormFeaturePatch
    {
        private readonly Form _form;
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private readonly List<BlockedSourceRule> _rules = new List<BlockedSourceRule>();
        private readonly HashSet<CoreWebView2> _wired = new HashSet<CoreWebView2>();
        private readonly Timer _sync = new Timer { Interval = 700 };
        private Panel _devHost;
        private Panel _devViewport;
        private CheckBox _detach;
        private IntPtr _devWindow = IntPtr.Zero;
        private IntPtr _devOldParent = IntPtr.Zero;
        private IntPtr _devOldStyle = IntPtr.Zero;
        private bool _sessionOnly;
        private ToolStripMenuItem _blockListItem;

        private string RulesFile { get { return Path.Combine(AppPaths.DataDirectory, "blocked-sources.json"); } }

        private const string PreciseHideScript = @"
(function(){
 if(window.__kbPreciseHideInstalled)return;
 window.__kbPreciseHideInstalled=true;
 function cssEsc(v){
   if(window.CSS&&CSS.escape)return CSS.escape(String(v));
   return String(v).replace(/([^a-zA-Z0-9_-])/g,'\\$1');
 }
 function unique(sel){try{return !!sel&&document.querySelectorAll(sel).length===1;}catch(e){return false;}}
 function sourceSelector(el){
   if(!el||el.nodeType!==1)return '';
   if(el.tagName==='IMG'&&el.parentElement&&el.parentElement.tagName==='A')el=el.parentElement;
   var cur=el;
   for(var z=0;z<3&&cur&&cur.nodeType===1;z++,cur=cur.parentElement){
     var tag=(cur.tagName||'').toLowerCase();
     if(cur.id){var id='#'+cssEsc(cur.id);if(unique(id))return id;}
     var attrs=['href','src','data-id','data-ad','data-ad-id','data-banner-id','name'];
     for(var i=0;i<attrs.length;i++){
       var a=attrs[i],v=cur.getAttribute&&cur.getAttribute(a);
       if(v){var s=tag+'['+a+'='+JSON.stringify(v)+']';if(unique(s))return s;}
     }
   }
   var parts=[],node=el,depth=0;
   while(node&&node.nodeType===1&&node!==document.documentElement&&depth<7){
     var p=(node.tagName||'').toLowerCase();
     if(node.id){p='#'+cssEsc(node.id);parts.unshift(p);break;}
     var parent=node.parentElement;
     if(parent){
       var same=Array.prototype.filter.call(parent.children,function(c){return c.tagName===node.tagName;});
       if(same.length>1)p+=':nth-of-type('+(same.indexOf(node)+1)+')';
     }
     parts.unshift(p);node=parent;depth++;
   }
   return parts.join(' > ');
 }
 window.__kbHideLast=function(){
   var el=window.__kbLastContext||null;
   if(!el)return null;
   var selector=sourceSelector(el);
   if(!selector)return null;
   try{
     var st=document.getElementById('__kb_hidden_rules_live');
     if(!st){st=document.createElement('style');st.id='__kb_hidden_rules_live';(document.head||document.documentElement).appendChild(st);}
     st.textContent+=(st.textContent?'\n':'')+selector+'{display:none!important;visibility:hidden!important;}';
     var hit=document.querySelector(selector);if(hit)hit.style.setProperty('display','none','important');
   }catch(e){}
   return {Host:location.hostname,Selector:selector,PageUrl:location.href};
 };
})();";

        public MainFormFeaturePatch(Form form)
        {
            _form = form;
        }

        public void Attach()
        {
            _sessionOnly = ReadField<bool>("_sessionOnly");
            LoadRules();
            SyncRulesFromMain();
            ApplyRuleState(false);
            BuildDevToolsHost();
            HookDevTools();
            AddBlockListMenu();
            if (_sessionOnly) ApplyPrivateDarkTheme();
            _sync.Tick += async delegate { await SynchronizeAsync(); };
            _sync.Start();
            _form.Resize += delegate { ResizeDockedDevTools(); };
            _form.FormClosed += delegate { _sync.Stop(); DetachDevTools(true); };
            _ = SynchronizeAsync();
        }

        private T ReadField<T>(string name)
        {
            try
            {
                var field = _form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field == null) return default(T);
                var value = field.GetValue(_form);
                return value is T ? (T)value : default(T);
            }
            catch { return default(T); }
        }

        private async Task SynchronizeAsync()
        {
            SyncRulesFromMain();
            var tabs = ReadField<List<BrowserTab>>("_tabs");
            if (tabs == null) return;
            foreach (var tab in tabs.ToArray())
            {
                var core = tab != null && tab.View != null ? tab.View.CoreWebView2 : null;
                if (core == null || _wired.Contains(core)) continue;
                _wired.Add(core);
                core.NavigationCompleted += async delegate { await InjectPreciseHideAsync(core); };
                await InjectPreciseHideAsync(core);
            }
        }

        private async Task InjectPreciseHideAsync(CoreWebView2 core)
        {
            try { await core.ExecuteScriptAsync(PreciseHideScript); } catch { }
        }

        private void LoadRules()
        {
            try
            {
                if (!File.Exists(RulesFile)) return;
                var loaded = _json.Deserialize<List<BlockedSourceRule>>(File.ReadAllText(RulesFile));
                if (loaded != null) _rules.AddRange(loaded.Where(x => x != null));
            }
            catch { }
        }

        private void SaveRules()
        {
            if (_sessionOnly) return;
            try
            {
                Directory.CreateDirectory(AppPaths.DataDirectory);
                File.WriteAllText(RulesFile, _json.Serialize(_rules), new UTF8Encoding(false));
            }
            catch { }
        }

        private void SyncRulesFromMain()
        {
            var main = ReadField<List<HiddenRule>>("_hiddenRules");
            if (main == null) return;
            var changed = false;
            foreach (var h in main.ToArray())
            {
                if (h == null || string.IsNullOrWhiteSpace(h.Host) || string.IsNullOrWhiteSpace(h.Selector)) continue;
                var key = h.Host.ToLowerInvariant() + "\n" + h.Selector;
                if (_rules.Any(r => r.Key == key)) continue;
                _rules.Add(new BlockedSourceRule
                {
                    Host = h.Host,
                    Selector = h.Selector,
                    PageUrl = h.PageUrl,
                    CreatedAt = string.IsNullOrWhiteSpace(h.CreatedAt) ? DateTime.Now.ToString("o") : h.CreatedAt,
                    Enabled = true
                });
                changed = true;
            }
            if (changed) SaveRules();
        }

        private void ApplyRuleState(bool reload)
        {
            var main = ReadField<List<HiddenRule>>("_hiddenRules");
            if (main == null) return;
            var desired = _rules.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Host) && !string.IsNullOrWhiteSpace(r.Selector)).ToList();
            main.Clear();
            foreach (var r in desired)
            {
                main.Add(new HiddenRule { Host = r.Host, Selector = r.Selector, PageUrl = r.PageUrl, CreatedAt = r.CreatedAt });
            }
            if (!_sessionOnly) Store.SaveHiddenRules(main);
            SaveRules();
            if (reload)
            {
                var active = ReadField<BrowserTab>("_active");
                try { if (active != null && active.View != null) active.View.Reload(); } catch { }
            }
        }

        private void AddBlockListMenu()
        {
            var menu = ReadField<ContextMenuStrip>("_menu");
            if (menu == null || _blockListItem != null) return;
            _blockListItem = new ToolStripMenuItem("차단 소스 목록...");
            _blockListItem.Click += delegate { ShowBlockList(); };
            var insertAt = Math.Max(0, menu.Items.Count - 2);
            menu.Items.Insert(insertAt, _blockListItem);
            if (_sessionOnly) DarkenMenu(menu);
        }

        private void ShowBlockList()
        {
            SyncRulesFromMain();
            using (var dlg = new Form())
            using (var list = new CheckedListBox())
            using (var ok = new Button())
            using (var cancel = new Button())
            {
                dlg.Text = "KBrowser - 차단 소스 목록";
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ClientSize = new Size(840, 430);
                dlg.MinimumSize = new Size(620, 340);
                list.Dock = DockStyle.Fill;
                list.CheckOnClick = true;
                list.HorizontalScrollbar = true;
                foreach (var rule in _rules)
                {
                    var index = list.Items.Add(rule);
                    list.SetItemChecked(index, rule.Enabled);
                }
                var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44 };
                ok.Text = "적용"; ok.Width = 90; ok.Height = 28; ok.Left = 638; ok.Top = 8; ok.Anchor = AnchorStyles.Top | AnchorStyles.Right; ok.DialogResult = DialogResult.OK;
                cancel.Text = "취소"; cancel.Width = 90; cancel.Height = 28; cancel.Left = 736; cancel.Top = 8; cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right; cancel.DialogResult = DialogResult.Cancel;
                bottom.Controls.Add(ok); bottom.Controls.Add(cancel);
                dlg.Controls.Add(list); dlg.Controls.Add(bottom); dlg.AcceptButton = ok; dlg.CancelButton = cancel;
                if (_sessionOnly) ApplyDarkThemeToControl(dlg);
                if (dlg.ShowDialog(_form) != DialogResult.OK) return;
                for (var i = 0; i < list.Items.Count && i < _rules.Count; i++) _rules[i].Enabled = list.GetItemChecked(i);
                ApplyRuleState(true);
            }
        }

        private void BuildDevToolsHost()
        {
            _devHost = new Panel { Dock = DockStyle.Bottom, Height = 320, Visible = false, BackColor = Color.FromArgb(36, 39, 43) };
            var header = new Panel { Dock = DockStyle.Top, Height = 28, BackColor = Color.FromArgb(45, 48, 53) };
            var caption = new Label { Text = "개발자 도구", ForeColor = Color.WhiteSmoke, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, Left = 8, Top = 0, Width = 130, Height = 28 };
            _detach = new CheckBox { Text = "분리", ForeColor = Color.WhiteSmoke, AutoSize = true, Left = 145, Top = 5 };
            var close = new Button { Text = "×", Width = 34, Height = 24, Top = 2, FlatStyle = FlatStyle.Flat, ForeColor = Color.WhiteSmoke, BackColor = Color.FromArgb(45, 48, 53), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            close.FlatAppearance.BorderSize = 0;
            close.Left = _devHost.Width - 40;
            _devHost.Resize += delegate { close.Left = _devHost.Width - 40; ResizeDockedDevTools(); };
            _detach.CheckedChanged += delegate
            {
                if (_detach.Checked) DetachDevTools(false);
                else _ = DockDevToolsSoonAsync();
            };
            close.Click += delegate
            {
                if (_devWindow != IntPtr.Zero) PostMessage(_devWindow, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                _devWindow = IntPtr.Zero;
                _devHost.Visible = false;
            };
            header.Controls.Add(caption); header.Controls.Add(_detach); header.Controls.Add(close);
            _devViewport = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(32, 34, 37) };
            _devHost.Controls.Add(_devViewport); _devHost.Controls.Add(header);
            _form.Controls.Add(_devHost);
            _devHost.BringToFront();
        }

        private void HookDevTools()
        {
            var button = ReadField<Button>("_devButton");
            if (button != null) button.Click += async delegate { await DockDevToolsSoonAsync(); };
            _form.KeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.F12) await DockDevToolsSoonAsync();
            };
        }

        private async Task DockDevToolsSoonAsync()
        {
            if (_detach != null && _detach.Checked) return;
            var active = ReadField<BrowserTab>("_active");
            try { if (active != null && active.View != null && active.View.CoreWebView2 != null) active.View.CoreWebView2.OpenDevToolsWindow(); } catch { }
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(120);
                var win = FindDevToolsWindow();
                if (win == IntPtr.Zero) continue;
                DockDevTools(win);
                return;
            }
        }

        private IntPtr FindDevToolsWindow()
        {
            var pid = Process.GetCurrentProcess().Id;
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                uint windowPid;
                GetWindowThreadProcessId(hWnd, out windowPid);
                if (windowPid != pid || !IsWindowVisible(hWnd) || hWnd == _form.Handle) return true;
                var sb = new StringBuilder(512);
                GetWindowText(hWnd, sb, sb.Capacity);
                var title = sb.ToString();
                if (title.IndexOf("DevTools", StringComparison.OrdinalIgnoreCase) >= 0 || title.IndexOf("개발자 도구", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    found = hWnd;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private void DockDevTools(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || _devViewport == null) return;
            if (_devWindow != hwnd)
            {
                _devWindow = hwnd;
                _devOldParent = GetParent(hwnd);
                _devOldStyle = GetWindowLongPtr(hwnd, GWL_STYLE);
            }
            _devHost.Visible = true;
            _devHost.BringToFront();
            var style = _devOldStyle.ToInt64();
            style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
            style |= WS_CHILD | WS_VISIBLE;
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
            SetParent(hwnd, _devViewport.Handle);
            ResizeDockedDevTools();
        }

        private void ResizeDockedDevTools()
        {
            if (_devWindow == IntPtr.Zero || _devViewport == null || _detach == null || _detach.Checked) return;
            try { MoveWindow(_devWindow, 0, 0, Math.Max(10, _devViewport.ClientSize.Width), Math.Max(10, _devViewport.ClientSize.Height), true); } catch { }
        }

        private void DetachDevTools(bool closing)
        {
            if (_devWindow == IntPtr.Zero) { if (closing && _devHost != null) _devHost.Visible = false; return; }
            try
            {
                SetParent(_devWindow, _devOldParent);
                if (_devOldStyle != IntPtr.Zero) SetWindowLongPtr(_devWindow, GWL_STYLE, _devOldStyle);
                SetWindowPos(_devWindow, IntPtr.Zero, _form.Left + 90, _form.Top + 90, 900, 650, SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            }
            catch { }
            if (!closing && _devHost != null) _devHost.Visible = false;
            if (closing) _devWindow = IntPtr.Zero;
        }

        private void ApplyPrivateDarkTheme()
        {
            _form.BackColor = Color.FromArgb(30, 32, 36);
            _form.ForeColor = Color.Gainsboro;
            ApplyDarkThemeToControl(_form);
            DarkenMenu(ReadField<ContextMenuStrip>("_menu"));
            DarkenMenu(ReadField<ContextMenuStrip>("_favoritesMenu"));
            DarkenMenu(ReadField<ContextMenuStrip>("_addressMenu"));
        }

        private static void ApplyDarkThemeToControl(Control root)
        {
            if (root == null) return;
            if (!(root is WebView2))
            {
                if (root is TextBox)
                {
                    root.BackColor = Color.FromArgb(50, 53, 58);
                    root.ForeColor = Color.WhiteSmoke;
                }
                else if (root is Button)
                {
                    var b = (Button)root;
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = Color.FromArgb(78, 82, 89);
                    b.BackColor = Color.FromArgb(48, 51, 56);
                    b.ForeColor = Color.WhiteSmoke;
                }
                else if (root is Label)
                {
                    root.BackColor = Color.Transparent;
                    root.ForeColor = Color.WhiteSmoke;
                }
                else
                {
                    root.BackColor = Color.FromArgb(36, 39, 43);
                    root.ForeColor = Color.WhiteSmoke;
                }
            }
            foreach (Control child in root.Controls) ApplyDarkThemeToControl(child);
        }

        private static void DarkenMenu(ContextMenuStrip menu)
        {
            if (menu == null) return;
            menu.BackColor = Color.FromArgb(42, 45, 49);
            menu.ForeColor = Color.WhiteSmoke;
            foreach (ToolStripItem item in menu.Items) DarkenItem(item);
        }

        private static void DarkenItem(ToolStripItem item)
        {
            if (item == null) return;
            item.BackColor = Color.FromArgb(42, 45, 49);
            item.ForeColor = Color.WhiteSmoke;
            var drop = item as ToolStripMenuItem;
            if (drop == null) return;
            foreach (ToolStripItem child in drop.DropDownItems) DarkenItem(child);
        }

        private const int GWL_STYLE = -16;
        private const long WS_CHILD = 0x40000000L;
        private const long WS_VISIBLE = 0x10000000L;
        private const long WS_CAPTION = 0x00C00000L;
        private const long WS_THICKFRAME = 0x00040000L;
        private const long WS_SYSMENU = 0x00080000L;
        private const long WS_MINIMIZEBOX = 0x00020000L;
        private const long WS_MAXIMIZEBOX = 0x00010000L;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint WM_CLOSE = 0x0010;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
