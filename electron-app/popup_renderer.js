// popup_renderer.js (runs in renderer; preload exposes electronAPI)
const askBtn = document.getElementById('askBtn');
const preview = document.getElementById('preview');
let currentText = '';

if (window.electronAPI) {
  window.electronAPI.onSelection((data) => {
    if (!data || !data.text) {
      preview.textContent = 'Ask AI';
      currentText = '';
      return;
    }
    currentText = data.text;
    // truncate preview to 80 chars
    const short = data.text.length > 80 ? data.text.slice(0, 80) + '…' : data.text;
    preview.textContent = short;
  });
}

askBtn.addEventListener('click', async () => {
  if (!currentText) return;
  // call main IPC handler 'ask-ai'
  try {
    await window.electronAPI.askAI(currentText);
    // hide the popup after clicking
    window.setTimeout(() => {
      window.close?.();
    }, 120);
  } catch (e) {
    console.error(e);
  }
});
