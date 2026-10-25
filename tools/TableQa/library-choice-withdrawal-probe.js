// QA-only simulated authoritative-view updates; never mutates the server table.
(() => {
  const NativeSocket = window.WebSocket;
  let socket;
  let original;
  let injecting = false;
  function deliver(message) {
    injecting = true;
    try {
      socket.dispatchEvent(new MessageEvent('message', { data: JSON.stringify(message) + '\x1e' }));
    } finally { injecting = false; }
  }
  window.libraryWithdrawalProbe = {
    restore() { if (!original) throw Error('No search view captured'); deliver(original); },
    withdraw(required) {
      if (!original?.arguments[0].pending?.options.length) throw Error('No offered search options');
      const revised = structuredClone(original);
      const pending = revised.arguments[0].pending;
      const removed = pending.options.shift().id;
      pending.min = required ? 1 : 0;
      deliver(revised);
      return { removed, choiceId: pending.id, remaining: pending.options.length };
    },
  };
  window.WebSocket = class extends NativeSocket {
    constructor(...args) {
      super(...args);
      socket = this;
      this.addEventListener('message', event => {
        if (injecting || typeof event.data !== 'string') return;
        for (const frame of event.data.split('\x1e')) {
          try {
            const message = JSON.parse(frame);
            if (message.target === 'ViewUpdated' && message.arguments[0].pending?.kind === 'search')
              original = message;
          } catch { /* SignalR handshake or empty frame. */ }
        }
      });
    }
  };
})();
