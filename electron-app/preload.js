// preload.js
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('electronAPI', {
  onSelection: (cb) => ipcRenderer.on('selection', (event, data) => cb(data)),
  askAI: (text) => ipcRenderer.invoke('ask-ai', text),
  onAIResponse: (cb) => ipcRenderer.on('ai-response', (event, data) => cb(data))
});