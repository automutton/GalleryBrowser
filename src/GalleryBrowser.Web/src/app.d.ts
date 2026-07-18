export {};

declare global {
  interface Window {
    galleryBrowserFlushCreatorTracking?: () => void;
    chrome?: {
      webview?: {
        postMessage: (message: unknown) => void;
        addEventListener: (event: 'message', callback: (event: MessageEvent) => void) => void;
      };
    };
  }
}
