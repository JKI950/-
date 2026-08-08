'use strict';
const { app, BrowserWindow, Menu } = require('electron');
const path = require('path');

app.setName('KBrowser');

function configureGuest(contents) {
  contents.setWindowOpenHandler(({ url }) => {
    const w = BrowserWindow.getFocusedWindow();
    if (w && !w.isDestroyed()) w.webContents.send('open-url-in-tab', url);
    return { action: 'deny' };
  });
}

function createWindow() {
  const win = new BrowserWindow({
    width: 1280,
    height: 820,
    minWidth: 760,
    minHeight: 520,
    title: 'KBrowser',
    autoHideMenuBar: true,
    backgroundColor: '#ffffff',
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
      webviewTag: true
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
