// QA-only: retain the REST screen while delaying its first authoritative hub view.
(() => {
  const NativeSocket = window.WebSocket;
  const held = [];
  let latestSocket;
  let blocking = sessionStorage.getItem('qa-library-hold-ready') === '1';
  window.libraryReadyProbe = {
    held: 0,
    reconnect() {
      blocking = true;
      window.libraryCastReady = false;
      latestSocket.close();
    },
    release() {
      blocking = false;
      sessionStorage.removeItem('qa-library-hold-ready');
      for (const [socket, data] of held.splice(0)) socket.dispatchEvent(new MessageEvent('message', { data }));
    },
  };
  window.WebSocket = class extends NativeSocket {
    constructor(...args) {
      super(...args);
      latestSocket = this;
      this.addEventListener('message', event => {
        if (!blocking || typeof event.data !== 'string') return;
        const messages = event.data.split('\x1e').filter(Boolean).map(frame => {
          try { return JSON.parse(frame); } catch { return {}; }
        });
        if (messages.some(message => message.target === 'ViewUpdated')) {
          event.stopImmediatePropagation();
          held.push([this, event.data]);
          window.libraryReadyProbe.held++;
        }
      });
    }
  };
})();
