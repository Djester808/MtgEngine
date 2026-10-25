// QA-only transport delay: keep the real invocation pending until the test releases it.
(() => {
  const NativeSocket = window.WebSocket;
  const probe = window.libraryBusyProbe = { commands: [], held: false, released: false };
  if (window.libraryBusyOnceKey && sessionStorage.getItem(window.libraryBusyOnceKey)) {
    probe.held = true;
    probe.released = true;
  }
  window.WebSocket = class extends NativeSocket {
    send(data) {
      if (typeof data === 'string') {
        const commands = data.split('\x1e').filter(Boolean).flatMap(frame => {
          try {
            const message = JSON.parse(frame);
            return message.target === 'Command' && (window.libraryBusyTarget
              ? message.arguments?.[1]?.kind === window.libraryBusyTarget
              : message.arguments?.[1]?.kind?.startsWith('manualLibrary'))
              ? [message.arguments[1]] : [];
          } catch { return []; }
        });
        probe.commands.push(...commands);
        if (commands.length && !probe.held) {
          probe.held = true;
          probe.release = () => {
            if (probe.released) return;
            probe.released = true;
            if (window.libraryBusyOnceKey) sessionStorage.setItem(window.libraryBusyOnceKey, 'released');
            NativeSocket.prototype.send.call(this, data);
          };
          return;
        }
      }
      return super.send(data);
    }
  };
})();
