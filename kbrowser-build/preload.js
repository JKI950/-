'use strict';
const { contextBridge, ipcRenderer } = require('electron');
contextBridge.exposeInMainWorld('kbrowserHost', {
  onOpenUrl: (callback) => ipcRenderer.on('open-url-in-tab', (_event, url) => callback(url)),
  getCredential: (url) => ipcRenderer.invoke('credential-get', url),
  saveCredential: (url, username, password) => ipcRenderer.invoke('credential-save', url, username, password),
  saveCompletePage: (contentsId) => ipcRenderer.invoke('save-complete-page', contentsId),
  startCapture: (contentsId) => ipcRenderer.invoke('capture-start', contentsId),
  stopCapture: (contentsId) => ipcRenderer.invoke('capture-stop', contentsId),
  isRecording: (contentsId) => ipcRenderer.invoke('capture-is-recording', contentsId)
});
