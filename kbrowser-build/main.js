'use strict';
const { app, BrowserWindow, Menu, clipboard } = require('electron');
const path = require('path');

app.setName('KBrowser');
app.commandLine.appendSwitch('disable-component-update');
app.commandLine.appendSwitch('disable-default-apps');

function popupContextMenu(contents, params) {
  const template = [];
  if (params.linkURL) {
    template.push(
      { label: '새 탭에서 링크 열기', click: () => {
        const w = BrowserWindow.getFocusedWindow();
        if (w && !w.isDestroyed()) w.webContents.send('open-url-in-tab', params.linkURL);
      }},
      { label: '링크 주소 복사', click: () => clipboard.writeText(params.linkURL) },
      { type: 'separator' }
    );
  }
  if (params.srcURL && params.mediaType === 'image') {
    template.push(
      { label: '이미지 새 탭에서 열기', click: () => {
        const w = BrowserWindow.getFocusedWindow();
        if (w && !w.isDestroyed()) w.webContents.send('open-url-in-tab', params.srcURL);
      }},
      { label: '이미지 주소 복사', click: () => clipboard.writeText(params.srcURL) },
      { type: 'separator' }
    );
  }
  if (params.isEditable) {
    template.push(
      { label: '실행 취소', role: 'undo', enabled: !!params.editFlags.canUndo },
      { label: '다시 실행', role: 'redo', enabled: !!params.editFlags.canRedo },
      { type: 'separator' },
      { label: '잘라내기', role: 'cut', enabled: !!params.editFlags.canCut },
      { label: '복사', role: 'copy', enabled: !!params.editFlags.canCopy },
      { label: '붙여넣기', role: 'paste', enabled: !!params.editFlags.canPaste },
      { label: '전체 선택', role: 'selectAll', enabled: !!params.editFlags.canSelectAll },
      { type: 'separator' }
    );
  } else if (params.selectionText) {
    template.push({ label: '복사', role: 'copy' }, { type: 'separator' });
  }
  template.push(
    { label: '뒤로', enabled: contents.canGoBack(), click: () => contents.goBack() },
    { label: '앞으로', enabled: contents.canGoForward(), click: () => contents.goForward() },
    { label: '새로고침', click: () => contents.reload() },
    { type: 'separator' },
    { label: '페이지 소스/개발자 도구', click: () => contents.openDevTools({ mode: 'detach' }) }
  );
  Menu.buildFromTemplate(template).popup({ window: BrowserWindow.getFocusedWindow() || undefined });
}

function configureGuest(contents) {
  contents.setWindowOpenHandler(({ url }) => {
    const w = BrowserWindow.getFocusedWindow();
    if (w && !w.isDestroyed()) w.webContents.send('open-url-in-tab', url);
    return { action: 'deny' };
  });
  contents.on('context-menu', (_event, params) => popupContextMenu(contents, params));
}

function createWindow() {
  const win = new BrowserWindow({
    width: 1280,
    height: 820,
    minWidth: 760,
    minHeight: 520,
    title: 'KBrowser',
    icon: path.join(__dirname, 'icon.ico'),
    autoHideMenuBar: true,
    backgroundColor: '#ffffff',
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
  win.webContents.on('did-attach-webview', (_event, guest) => configureGuest(guest));
  win.loadFile(path.join(__dirname, 'index.html'));
}

app.whenReady().then(() => {
  createWindow();
  app.on('activate', () => { if (BrowserWindow.getAllWindows().length === 0) createWindow(); });
});
app.on('window-all-closed', () => { if (process.platform !== 'darwin') app.quit(); });
