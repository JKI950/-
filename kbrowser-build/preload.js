'use strict';
const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('kbrowserHost', {
  onOpenUrl: (callback) => ipcRenderer.on('open-url-in-tab', (_event, url) => callback(url))
});
