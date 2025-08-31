// main.js
const { app, BrowserWindow, ipcMain, screen } = require('electron');
const path = require('path');
const net = require('net');
const { spawn } = require('child_process');

const PIPE_PATH = '\\\\.\\pipe\\ai_selection_pipe'; // must match C# PIPE_NAME
const HELPER_REL_PATH = path.join(__dirname, 'native-helper', 'SelectionWatcher', 'bin', 'Debug', 'net8.0-windows', 'SelectionWatcher.exe'); 

// Popup size
const POPUP_W = 140;
const POPUP_H = 46;

let pipeSocket = null;
let popupWindow = null;
let lastSelectionId = 0;

function createPopupWindow() {
  if (popupWindow && !popupWindow.isDestroyed()) return popupWindow;
  popupWindow = new BrowserWindow({
    width: POPUP_W,
    height: POPUP_H,
    frame: false,
    transparent: true,
    alwaysOnTop: true,
    skipTaskbar: true,
    resizable: false,
    focusable: true,
    show: false,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      nodeIntegration: false,
      contextIsolation: true
    }
  });

  popupWindow.loadFile(path.join(__dirname, 'popup.html'));
  // Close when it loses focus
  popupWindow.on('blur', () => {
    try { if (popupWindow && !popupWindow.isDestroyed()) popupWindow.hide(); } catch(e){}
  });
  return popupWindow;
}

function showPopupAt(rect, selectionText) {
  // compute popup position - place above selection if possible
  const displays = screen.getAllDisplays();
  // clamp to primary if needed
  let display = screen.getDisplayNearestPoint({ x: Math.round(rect.x || 0), y: Math.round(rect.y || 0) });
  let popupX = Math.round((rect.x || 0) + (rect.width || 0) - POPUP_W);
  let popupY = Math.round((rect.y || 0) - POPUP_H - 8); // 8px gap

  // fallback to near cursor if no rect
  if (!rect || (!rect.width && !rect.height)) {
    const cursor = screen.getCursorScreenPoint();
    popupX = cursor.x;
    popupY = cursor.y - POPUP_H - 8;
  }

  // clamp inside display bounds
  const bounds = display.workArea;
  if (popupX < bounds.x) popupX = bounds.x + 8;
  if (popupX + POPUP_W > bounds.x + bounds.width) popupX = bounds.x + bounds.width - POPUP_W - 8;
  if (popupY < bounds.y) popupY = bounds.y + 8;

  const win = createPopupWindow();
  win.setBounds({ x: popupX, y: popupY, width: POPUP_W, height: POPUP_H });
  win.showInactive(); // show without stealing focus
  // Send selection data to popup renderer
  win.webContents.send('selection', { text: selectionText });
}

function launchHelperExecutable() {
  // spawn the C# helper (adjust path as needed)
  try {
    const exePath = path.resolve(HELPER_REL_PATH);
    console.log('Launching helper at', exePath);
    
    // Check if file exists
    const fs = require('fs');
    if (!fs.existsSync(exePath)) {
      console.error('Helper executable not found at:', exePath);
      console.log('Please build the C# project first using: dotnet build');
      return;
    }
    
    const child = spawn(exePath, [], { detached: true, stdio: 'pipe' });
    
    // Log helper output for debugging
    child.stdout.on('data', (data) => {
      console.log('Helper stdout:', data.toString());
    });
    
    child.stderr.on('data', (data) => {
      console.log('Helper stderr:', data.toString());
    });
    
    child.on('error', (err) => {
      console.error('Failed to start helper:', err);
    });
    
    child.unref();
  } catch (err) {
    console.warn('Failed to spawn helper automatically. Make sure you compiled the C# helper and set HELPER_REL_PATH correctly.', err);
  }
}

function connectPipe() {
  // For named pipes on Windows, we need to connect differently
  const tryConnect = () => {
    if (pipeSocket && !pipeSocket.destroyed) return;
    
    // Connect to named pipe
    pipeSocket = net.createConnection(PIPE_PATH, () => {
      console.log('Connected to selection pipe.');
    });

    let buffer = '';
    pipeSocket.on('data', (chunk) => {
      buffer += chunk.toString('utf8');
      let lines = buffer.split(/\r?\n/);
      buffer = lines.pop(); // remainder
      for (const line of lines) {
        if (!line.trim()) continue;
        try {
          const msg = JSON.parse(line);
          if (msg && msg.type === 'selection' && msg.text) {
            // show popup near msg.rect
            showPopupAt(msg.rect || {}, msg.text);
          }
        } catch (err) {
          console.warn('Failed to parse pipe JSON:', err, line);
        }
      }
    });

    pipeSocket.on('error', (err) => {
      // try reconnect after delay
      console.log('Pipe error:', err.message);
      setTimeout(tryConnect, 2000); // Increased delay
    });

    pipeSocket.on('close', () => {
      console.log('Pipe closed, reconnecting...');
      setTimeout(tryConnect, 1000);
    });
  };

  // Wait a bit before first connection attempt to let helper start
  setTimeout(tryConnect, 2000);
}

// Handle Ask AI request from popup renderer
ipcMain.handle('ask-ai', async (event, text) => {
  // For demo: open a small response window with the selected text.
  // Replace this with your backend call (fetch to your API) and show the response.
  const responseWin = new BrowserWindow({
    width: 560,
    height: 400,
    show: true,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true
    }
  });
  // pass text via query param or IPC; simplest: load response.html then send via IPC
  responseWin.loadFile(path.join(__dirname, 'response.html'));
  responseWin.webContents.on('did-finish-load', () => {
    responseWin.webContents.send('ai-response', { query: text, reply: `Demo reply for: "${text.slice(0,200)}"` });
  });
  return { ok: true };
});

app.whenReady().then(() => {
  // Optionally launch helper automatically (best if you set HELPER_REL_PATH correctly)
  launchHelperExecutable();

  // attempt connect to pipe (we will reconnect if helper not ready yet)
  connectPipe();

  app.on('activate', () => {});
});

app.on('window-all-closed', () => {
  // keep app alive as background helper — if you want to quit completely uncomment:
  // app.quit();
});