'use strict';
const { app, BrowserWindow, Menu, clipboard, ipcMain, dialog, webContents, safeStorage, session } = require('electron');
const fs = require('fs');
const path = require('path');
const { execFile } = require('child_process');

app.setName('KBrowser');
app.commandLine.appendSwitch('no-first-run');
app.commandLine.appendSwitch('no-default-browser-check');
app.commandLine.appendSwitch('disable-component-update');
app.commandLine.appendSwitch('disable-default-apps');

const defaultUserData = app.getPath('userData');
const programDir = app.isPackaged ? path.dirname(process.execPath) : __dirname;
const portableDataDir = path.join(programDir, '.kbrowser-data');
try {
  fs.mkdirSync(portableDataDir, { recursive: true });
  app.setPath('userData', portableDataDir);
  app.setPath('sessionData', path.join(portableDataDir, 'session'));
} catch {
  app.setPath('userData', defaultUserData);
}

const captures = new Map();
let downloadDirectory = '';
let mainWindow = null;

function iconPath() {
  const p = path.join(__dirname, 'icon.ico');
  return fs.existsSync(p) ? p : undefined;
}
function settingsFile() { return path.join(app.getPath('userData'), 'browser-settings.json'); }
function loadAppSettings() {
  try { return JSON.parse(fs.readFileSync(settingsFile(), 'utf8')); } catch { return {}; }
}
function saveAppSettings(v) {
  try {
    fs.mkdirSync(path.dirname(settingsFile()), { recursive: true });
    fs.writeFileSync(settingsFile(), JSON.stringify(v, null, 2), 'utf8');
  } catch {}
}
function blockedFile() { return path.join(app.getPath('userData'), 'blocked-elements.json'); }
function loadBlocked() {
  try { return JSON.parse(fs.readFileSync(blockedFile(), 'utf8')); } catch { return {}; }
}
function saveBlocked(v) {
  try {
    fs.mkdirSync(path.dirname(blockedFile()), { recursive: true });
    fs.writeFileSync(blockedFile(), JSON.stringify(v, null, 2), 'utf8');
  } catch {}
}
function hidePortableDataFolder() {
  if (process.platform !== 'win32') return;
  try { fs.mkdirSync(app.getPath('userData'), { recursive: true }); } catch {}
  execFile('attrib', ['+H', app.getPath('userData')], () => {});
}
function safeHttpUrl(value) {
  try {
    const u = new URL(value);
    return /^https?:$/.test(u.protocol) ? u.href : '';
  } catch { return ''; }
}
function sendOpenUrl(url) {
  const target = safeHttpUrl(url);
  if (!target || !mainWindow || mainWindow.isDestroyed()) return;
  mainWindow.webContents.send('open-url-in-tab', target);
}

function editableContextTemplate() {
  return [
    { label: '실행 취소', role: 'undo' },
    { label: '다시 실행', role: 'redo' },
    { label: '잘라내기', role: 'cut' },
    { label: '복사', role: 'copy' },
    { label: '붙여넣기', role: 'paste' },
    { label: '삭제', role: 'delete' },
    { label: '전체 선택', role: 'selectAll' }
  ];
}

function createLinkPreview(url) {
  const target = safeHttpUrl(url);
  if (!target) return;
  const win = new BrowserWindow({
    width: 920,
    height: 680,
    minWidth: 480,
    minHeight: 320,
    title: 'KBrowser 링크 미리보기',
    icon: iconPath(),
    autoHideMenuBar: true,
    backgroundColor: '#ffffff',
    show: false,
    webPreferences: {
      partition: 'persist:kbrowser',
      nodeIntegration: false,
      contextIsolation: true,
      sandbox: true
    }
  });
  Menu.setApplicationMenu(null);
  win.once('ready-to-show', () => { if (!win.isDestroyed()) win.show(); });
  win.webContents.setWindowOpenHandler(({ url: childUrl }) => {
    const u = safeHttpUrl(childUrl);
    if (u) createLinkPreview(u);
    return { action: 'deny' };
  });
  win.loadURL(target);
}

function createPrivateWindow(url) {
  const target = safeHttpUrl(url);
  if (!target) return;
  const partition = `private-${Date.now()}-${Math.random().toString(36).slice(2)}`;
  const win = new BrowserWindow({
    width: 1100,
    height: 800,
    minWidth: 640,
    minHeight: 420,
    title: 'KBrowser 사생활 보호',
    icon: iconPath(),
    autoHideMenuBar: true,
    backgroundColor: '#ffffff',
    webPreferences: {
      partition,
      nodeIntegration: false,
      contextIsolation: true,
      sandbox: true
    }
  });
  Menu.setApplicationMenu(null);
  win.webContents.setWindowOpenHandler(({ url: childUrl }) => {
    const u = safeHttpUrl(childUrl);
    if (!u) return { action: 'deny' };
    return {
      action: 'allow',
      overrideBrowserWindowOptions: {
        icon: iconPath(),
        autoHideMenuBar: true,
        webPreferences: { partition, nodeIntegration: false, contextIsolation: true, sandbox: true }
      }
    };
  });
  win.loadURL(target);
}

async function inspectElementAt(contents, x, y) {
  const code = `(() => {
    const root = document.elementFromPoint(${Number(x) || 0}, ${Number(y) || 0});
    if (!root) return null;
    const esc = v => (globalThis.CSS && CSS.escape) ? CSS.escape(String(v)) : String(v).replace(/[^a-zA-Z0-9_-]/g, '\\$&');
    const selectorFor = el => {
      if (!(el instanceof Element)) return '';
      if (el.id) return '#' + esc(el.id);
      const parts = [];
      let cur = el;
      while (cur && cur.nodeType === 1 && cur !== document.documentElement) {
        let part = cur.tagName.toLowerCase();
        const cls = [...cur.classList].filter(Boolean).slice(0, 2);
        if (cls.length) part += '.' + cls.map(esc).join('.');
        const parent = cur.parentElement;
        if (parent) {
          const same = [...parent.children].filter(n => n.tagName === cur.tagName);
          if (same.length > 1) part += ':nth-of-type(' + (same.indexOf(cur) + 1) + ')';
        }
        parts.unshift(part);
        const candidate = parts.join(' > ');
        try { if (document.querySelectorAll(candidate).length === 1) return candidate; } catch {}
        cur = parent;
      }
      return parts.join(' > ');
    };
    const clone = root.cloneNode(true);
    const originals = [root, ...root.querySelectorAll('*')];
    const clones = [clone, ...clone.querySelectorAll('*')];
    const computedRules = [];
    originals.forEach((el, i) => {
      const id = 'e' + i;
      clones[i]?.setAttribute('data-kb-effect-id', id);
      const cs = getComputedStyle(el);
      let body = '';
      for (const prop of cs) body += prop + ':' + cs.getPropertyValue(prop) + ';';
      computedRules.push('[data-kb-effect-id="' + id + '"]{' + body + '}');
    });
    const matched = [];
    const keyframes = [];
    const seen = new Set();
    const visitRules = rules => {
      for (const rule of rules || []) {
        try {
          if (rule.type === CSSRule.KEYFRAMES_RULE) {
            if (!seen.has(rule.cssText)) { seen.add(rule.cssText); keyframes.push(rule.cssText); }
          } else if (rule.type === CSSRule.STYLE_RULE) {
            let ok = false;
            for (const el of originals) { try { if (el.matches(rule.selectorText)) { ok = true; break; } } catch {} }
            if (ok && !seen.has(rule.cssText)) { seen.add(rule.cssText); matched.push(rule.cssText); }
          } else if (rule.cssRules) visitRules(rule.cssRules);
        } catch {}
      }
    };
    for (const sheet of document.styleSheets) { try { visitRules(sheet.cssRules); } catch {} }
    const resources = [];
    for (const el of originals) {
      for (const attr of ['src','href','poster']) {
        const v = el.getAttribute?.(attr);
        if (v) { try { resources.push(new URL(v, location.href).href); } catch {} }
      }
      try {
        const bg = getComputedStyle(el).backgroundImage || '';
        for (const m of bg.matchAll(/url\\(["']?(.*?)["']?\\)/g)) { try { resources.push(new URL(m[1], location.href).href); } catch {} }
      } catch {}
    }
    return {
      selector: selectorFor(root),
      tagName: root.tagName,
      pageUrl: location.href,
      title: document.title,
      html: clone.outerHTML,
      css: matched.join('\\n') + '\\n' + keyframes.join('\\n') + '\\n' + computedRules.join('\\n'),
      resources: [...new Set(resources)].slice(0, 500)
    };
  })()`;
  try { return await contents.executeJavaScript(code, true); } catch { return null; }
}

async function saveElementEffect(contents, x, y) {
  const data = await inspectElementAt(contents, x, y);
  if (!data?.html) return;
  const dir = await chooseDirectory('효과 저장 폴더 선택');
  if (!dir) return;
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const out = path.join(dir, `KBrowser_Effect_${stamp}`);
  fs.mkdirSync(out, { recursive: true });
  const html = `<!doctype html>\n<html lang="ko"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>KBrowser Effect</title><link rel="stylesheet" href="effect.css"></head><body><div class="kb-effect-root">${data.html}</div></body></html>`;
  fs.writeFileSync(path.join(out, 'effect.html'), html, 'utf8');
  fs.writeFileSync(path.join(out, 'effect.css'), data.css || '', 'utf8');
  fs.writeFileSync(path.join(out, 'source.json'), JSON.stringify({ selector: data.selector, tagName: data.tagName, pageUrl: data.pageUrl, title: data.title, resources: data.resources }, null, 2), 'utf8');
  fs.writeFileSync(path.join(out, 'README.txt'), 'KBrowser 효과 저장\r\n\r\n선택한 요소의 렌더링 HTML, 적용 CSS, CSS 애니메이션/키프레임 및 관련 리소스 URL을 저장합니다.\r\n서버 내부 PHP 원본 소스는 브라우저에 전달되지 않으므로 저장할 수 없습니다. PHP가 실행된 뒤 브라우저가 받은 HTML/CSS 결과만 저장됩니다.\r\n', 'utf8');
}

async function hideElementForSite(contents, x, y) {
  const data = await inspectElementAt(contents, x, y);
  if (!data?.selector || !data?.pageUrl) return;
  let origin = '';
  try { origin = new URL(data.pageUrl).origin; } catch {}
  if (!origin) return;
  const all = loadBlocked();
  const list = Array.isArray(all[origin]) ? all[origin] : [];
  if (!list.includes(data.selector)) list.push(data.selector);
  all[origin] = list;
  saveBlocked(all);
  try { await contents.insertCSS(`${data.selector}{display:none!important;visibility:hidden!important;pointer-events:none!important}`); } catch {}
}

async function applyBlockedRules(contents) {
  if (!contents || contents.isDestroyed()) return;
  let origin = '';
  try { origin = new URL(contents.getURL()).origin; } catch {}
  if (!origin) return;
  const list = loadBlocked()[origin];
  if (!Array.isArray(list) || !list.length) return;
  const css = list.map(s => `${s}{display:none!important;visibility:hidden!important;pointer-events:none!important}`).join('\n');
  try { await contents.insertCSS(css); } catch {}
}

function popupContextMenu(contents, params) {
  const template = [];
  const link = safeHttpUrl(params.linkURL);
  if (link) {
    template.push(
      { label: '새 탭에서 링크 열기', click: () => sendOpenUrl(link) },
      { label: '링크 미리보기', click: () => createLinkPreview(link) },
      { label: '사생활 보호창에서 링크 열기', click: () => createPrivateWindow(link) },
      { label: '링크 주소 복사', click: () => clipboard.writeText(link) }
    );
  }
  if (params.srcURL && params.mediaType === 'image') {
    template.push(
      { label: '이미지를 새 탭에서 열기', click: () => sendOpenUrl(params.srcURL) },
      { label: '이미지 주소 복사', click: () => clipboard.writeText(params.srcURL) }
    );
  }
  if (params.dictionarySuggestions?.length) {
    for (const word of params.dictionarySuggestions.slice(0, 5)) template.push({ label: word, click: () => contents.replaceMisspelling(word) });
  }
  if (params.isEditable) template.push(...editableContextTemplate());
  else if (params.selectionText) template.push({ label: '복사', role: 'copy' });
  template.push(
    { label: '효과 저장하기', click: () => saveElementEffect(contents, params.x, params.y) },
    { label: '안보이게 하기', click: () => hideElementForSite(contents, params.x, params.y) },
    { label: '뒤로', enabled: contents.canGoBack(), click: () => contents.goBack() },
    { label: '앞으로', enabled: contents.canGoForward(), click: () => contents.goForward() },
    { label: '새로고침', click: () => contents.reload() },
    { label: '페이지 저장...', click: () => saveCompletePage(contents.id) },
    { label: '검사', click: () => contents.inspectElement(params.x, params.y) }
  );
  Menu.buildFromTemplate(template).popup({ window: mainWindow && !mainWindow.isDestroyed() ? mainWindow : undefined });
}

function configureGuest(contents) {
  contents.setWindowOpenHandler((details) => {
    const direct = safeHttpUrl(details.url);
    if (direct) {
      sendOpenUrl(direct);
      return { action: 'deny' };
    }
    if (details.url === 'about:blank' || !details.url) {
      return {
        action: 'allow',
        overrideBrowserWindowOptions: {
          show: false,
          frame: false,
          skipTaskbar: true,
          x: -10000,
          y: -10000,
          width: 2,
          height: 2,
          webPreferences: {
            partition: 'persist:kbrowser',
            nodeIntegration: false,
            contextIsolation: true,
            sandbox: true
          }
        }
      };
    }
    return { action: 'deny' };
  });

  contents.on('did-create-window', (child, details) => {
    try { child.hide(); } catch {}
    let forwarded = false;
    const forward = value => {
      if (forwarded) return;
      const u = safeHttpUrl(value);
      if (!u) return;
      forwarded = true;
      sendOpenUrl(u);
      setTimeout(() => { try { if (!child.isDestroyed()) child.close(); } catch {} }, 10);
    };
    forward(details?.url);
    child.webContents.on('will-navigate', (_event, url) => forward(url));
    child.webContents.on('did-navigate', (_event, url) => forward(url));
    child.webContents.on('did-navigate-in-page', (_event, url) => forward(url));
    setTimeout(() => { try { if (!child.isDestroyed()) child.close(); } catch {} }, 15000);
  });

  contents.on('context-menu', (_event, params) => popupContextMenu(contents, params));
  contents.on('did-finish-load', () => applyBlockedRules(contents));
}

function credentialsFile() { return path.join(app.getPath('userData'), 'credentials.json'); }
function loadCredentials() {
  try { return JSON.parse(fs.readFileSync(credentialsFile(), 'utf8')); } catch { return {}; }
}
function saveCredentials(data) {
  fs.mkdirSync(path.dirname(credentialsFile()), { recursive: true });
  fs.writeFileSync(credentialsFile(), JSON.stringify(data, null, 2), 'utf8');
}
function normalizeOrigin(value) {
  try { return new URL(value).origin; } catch { return ''; }
}
ipcMain.handle('credential-get', (_event, value) => {
  const origin = normalizeOrigin(value);
  if (!origin || !safeStorage.isEncryptionAvailable()) return null;
  const row = loadCredentials()[origin];
  if (!row?.password) return null;
  try { return { username: row.username || '', password: safeStorage.decryptString(Buffer.from(row.password, 'base64')) }; } catch { return null; }
});
ipcMain.handle('credential-save', (_event, value, username, password) => {
  const origin = normalizeOrigin(value);
  if (!origin || !password || !safeStorage.isEncryptionAvailable()) return { ok: false, reason: 'Windows 암호화를 사용할 수 없습니다.' };
  const all = loadCredentials();
  all[origin] = { username: String(username || ''), password: safeStorage.encryptString(String(password)).toString('base64'), updatedAt: new Date().toISOString() };
  saveCredentials(all);
  return { ok: true };
});

async function chooseDirectory(title) {
  const win = BrowserWindow.getFocusedWindow() || mainWindow;
  const result = await dialog.showOpenDialog(win && !win.isDestroyed() ? win : undefined, { title, properties: ['openDirectory', 'createDirectory'] });
  return result.canceled ? null : result.filePaths[0];
}
ipcMain.handle('download-directory-get', () => downloadDirectory || '');
ipcMain.handle('download-directory-choose', async () => {
  const dir = await chooseDirectory('다운로드 저장 폴더 선택');
  if (!dir) return { ok: false, canceled: true, path: downloadDirectory || '' };
  downloadDirectory = dir;
  const s = loadAppSettings();
  s.downloadDirectory = dir;
  saveAppSettings(s);
  return { ok: true, path: dir };
});
function uniqueDownloadPath(dir, name) {
  const parsed = path.parse(name || 'download');
  let candidate = path.join(dir, name || 'download');
  let n = 1;
  while (fs.existsSync(candidate)) candidate = path.join(dir, `${parsed.name} (${n++})${parsed.ext}`);
  return candidate;
}
function installDownloadHandler() {
  const ses = session.fromPartition('persist:kbrowser');
  ses.on('will-download', (_event, item) => {
    if (!downloadDirectory) return;
    try { item.setSavePath(uniqueDownloadPath(downloadDirectory, item.getFilename())); } catch {}
  });
}

async function saveCompletePage(contentsId) {
  const contents = webContents.fromId(Number(contentsId));
  if (!contents || contents.isDestroyed()) return { ok: false, reason: '페이지를 찾을 수 없습니다.' };
  const dir = await chooseDirectory('페이지 저장 폴더 선택');
  if (!dir) return { ok: false, canceled: true };
  const target = path.join(dir, 'index.html');
  try { await contents.savePage(target, 'HTMLComplete'); return { ok: true, path: target }; }
  catch (error) { return { ok: false, reason: String(error?.message || error) }; }
}
ipcMain.handle('save-complete-page', (_event, contentsId) => saveCompletePage(contentsId));

function mimeExt(mime = '') {
  const m = mime.toLowerCase();
  if (m.includes('text/html')) return '.html';
  if (m.includes('text/css')) return '.css';
  if (m.includes('javascript')) return '.js';
  if (m.includes('application/json')) return '.json';
  if (m.includes('image/png')) return '.png';
  if (m.includes('image/jpeg')) return '.jpg';
  if (m.includes('image/gif')) return '.gif';
  if (m.includes('image/webp')) return '.webp';
  if (m.includes('image/svg')) return '.svg';
  if (m.includes('audio/mpeg')) return '.mp3';
  if (m.includes('audio/ogg')) return '.ogg';
  if (m.includes('video/mp4')) return '.mp4';
  if (m.includes('video/webm')) return '.webm';
  if (m.includes('font/woff2')) return '.woff2';
  if (m.includes('font/woff')) return '.woff';
  return '.bin';
}
function safeFileName(url, mime, index) {
  let name = '';
  try { name = decodeURIComponent(path.basename(new URL(url).pathname || '')); } catch {}
  name = name.replace(/[<>:"/\\|?*\x00-\x1F]/g, '_').slice(0, 120);
  if (!name || name === '.' || name === '..') name = `resource_${index}`;
  if (!path.extname(name)) name += mimeExt(mime);
  return `${String(index).padStart(4, '0')}_${name}`;
}
async function startCapture(contentsId) {
  const contents = webContents.fromId(Number(contentsId));
  if (!contents || contents.isDestroyed()) return { ok: false, reason: '페이지를 찾을 수 없습니다.' };
  if (captures.has(contents.id)) return { ok: true, recording: true };
  try {
    if (!contents.debugger.isAttached()) contents.debugger.attach('1.3');
    await contents.debugger.sendCommand('Network.enable', { maxTotalBufferSize: 100000000, maxResourceBufferSize: 25000000 });
    await contents.debugger.sendCommand('Network.setCacheDisabled', { cacheDisabled: true });
  } catch (error) { return { ok: false, reason: `Network 기록 시작 실패: ${error?.message || error}` }; }
  const state = { contents, rows: new Map(), startedAt: new Date().toISOString(), bytes: 0, handler: null };
  state.handler = async (_event, method, params) => {
    try {
      if (method === 'Network.requestWillBeSent') {
        state.rows.set(params.requestId, { requestId: params.requestId, url: params.request?.url || '', method: params.request?.method || '', requestHeaders: params.request?.headers || {}, postData: params.request?.postData || null, type: params.type || '', startedAt: params.timestamp || null });
      } else if (method === 'Network.responseReceived') {
        const row = state.rows.get(params.requestId) || { requestId: params.requestId };
        Object.assign(row, { url: row.url || params.response?.url || '', status: params.response?.status, statusText: params.response?.statusText, mimeType: params.response?.mimeType || '', responseHeaders: params.response?.headers || {}, fromDiskCache: !!params.response?.fromDiskCache, fromServiceWorker: !!params.response?.fromServiceWorker, type: row.type || params.type || '' });
        state.rows.set(params.requestId, row);
      } else if (method === 'Network.loadingFinished') {
        const row = state.rows.get(params.requestId);
        if (!row || state.bytes > 200 * 1024 * 1024) return;
        row.encodedDataLength = params.encodedDataLength || 0;
        try {
          const body = await contents.debugger.sendCommand('Network.getResponseBody', { requestId: params.requestId });
          const estimated = body.base64Encoded ? Math.floor(body.body.length * 0.75) : Buffer.byteLength(body.body, 'utf8');
          if (estimated <= 25 * 1024 * 1024 && state.bytes + estimated <= 200 * 1024 * 1024) { row.body = body.body; row.base64Encoded = !!body.base64Encoded; state.bytes += estimated; }
          else row.bodySkipped = 'too-large';
        } catch (error) { row.bodySkipped = String(error?.message || error); }
      } else if (method === 'Network.loadingFailed') {
        const row = state.rows.get(params.requestId) || { requestId: params.requestId };
        row.failed = true; row.errorText = params.errorText || ''; state.rows.set(params.requestId, row);
      }
    } catch {}
  };
  contents.debugger.on('message', state.handler);
  captures.set(contents.id, state);
  try { contents.reload(); } catch {}
  return { ok: true, recording: true };
}
async function stopCapture(contentsId) {
  const id = Number(contentsId), state = captures.get(id);
  if (!state) return { ok: false, reason: '기록 중이 아닙니다.' };
  captures.delete(id);
  try { state.contents.debugger.removeListener('message', state.handler); } catch {}
  try { await state.contents.debugger.sendCommand('Network.setCacheDisabled', { cacheDisabled: false }); } catch {}
  try { if (state.contents.debugger.isAttached()) state.contents.debugger.detach(); } catch {}
  const dir = await chooseDirectory('네트워크 기록 저장 폴더 선택');
  if (!dir) return { ok: false, canceled: true };
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  const out = path.join(dir, `KBrowser_Network_${stamp}`), resDir = path.join(out, 'resources');
  fs.mkdirSync(resDir, { recursive: true });
  let snapshot = '';
  try { snapshot = await state.contents.executeJavaScript('document.documentElement.outerHTML'); } catch {}
  if (snapshot) fs.writeFileSync(path.join(out, 'snapshot.html'), snapshot, 'utf8');
  const manifest = []; let index = 1;
  for (const row of state.rows.values()) {
    const copy = { ...row }; delete copy.body;
    if (row.body != null) {
      const fileName = safeFileName(row.url, row.mimeType, index++);
      const buffer = row.base64Encoded ? Buffer.from(row.body, 'base64') : Buffer.from(row.body, 'utf8');
      fs.writeFileSync(path.join(resDir, fileName), buffer); copy.savedFile = `resources/${fileName}`;
    }
    manifest.push(copy);
  }
  fs.writeFileSync(path.join(out, 'network.json'), JSON.stringify({ startedAt: state.startedAt, stoppedAt: new Date().toISOString(), items: manifest }, null, 2), 'utf8');
  fs.writeFileSync(path.join(out, 'README.txt'), 'KBrowser Network 기록\r\n\r\nHTML/CSS/JS/이미지/미디어 등 브라우저가 실제로 받은 응답을 저장합니다.\r\n.php URL도 서버가 브라우저로 보낸 응답만 저장되며 서버 내부 PHP 소스는 가져올 수 없습니다.\r\n', 'utf8');
  return { ok: true, path: out, count: manifest.length };
}
ipcMain.handle('capture-start', (_event, contentsId) => startCapture(contentsId));
ipcMain.handle('capture-stop', (_event, contentsId) => stopCapture(contentsId));
ipcMain.handle('capture-is-recording', (_event, contentsId) => captures.has(Number(contentsId)));

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1280,
    height: 820,
    minWidth: 760,
    minHeight: 520,
    title: 'KBrowser',
    icon: iconPath(),
    autoHideMenuBar: true,
    backgroundColor: '#ffffff',
    show: true,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
      webviewTag: true,
      backgroundThrottling: false
    }
  });
  Menu.setApplicationMenu(null);
  mainWindow.webContents.on('context-menu', (_event, params) => {
    if (params.isEditable) Menu.buildFromTemplate(editableContextTemplate()).popup({ window: mainWindow });
  });
  mainWindow.webContents.on('did-attach-webview', (_event, guest) => configureGuest(guest));
  mainWindow.on('closed', () => { mainWindow = null; });
  mainWindow.loadFile(path.join(__dirname, 'index.html'));
}

app.whenReady().then(() => {
  hidePortableDataFolder();
  downloadDirectory = loadAppSettings().downloadDirectory || '';
  installDownloadHandler();
  createWindow();
  app.on('activate', () => { if (BrowserWindow.getAllWindows().length === 0) createWindow(); });
});
app.on('window-all-closed', () => { if (process.platform !== 'darwin') app.quit(); });
