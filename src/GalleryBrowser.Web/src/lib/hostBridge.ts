export function postHostMessage(message: unknown): void {
  if (window.chrome?.webview) {
    window.chrome.webview.postMessage(message);
  }
}

