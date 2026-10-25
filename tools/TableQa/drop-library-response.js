// Runs before the application loads. Lose one completion after the server has
// processed the command, while retaining normal broadcasts and reconnect behavior.
(() => {
  const NativeSocket = window.WebSocket;
  window.libraryDeliveryProbe = JSON.parse(sessionStorage.getItem('qa-library-delivery-probe') || 'null')
    || { requests: [], dropped: false, joined: 0 };
  const probe = window.libraryDeliveryProbe;
  const save = () => sessionStorage.setItem('qa-library-delivery-probe', JSON.stringify(probe));
  window.WebSocket = class extends NativeSocket {
    constructor(...args) {
      super(...args);
      this.addEventListener('message', event => {
        if (typeof event.data !== 'string') return;
        for (const frame of event.data.split('\x1e').filter(Boolean)) {
          let message;
          try { message = JSON.parse(frame); } catch { continue; }
          if (message.type === 3 && message.invocationId === this.lostInvocation && !probe.dropped) {
            probe.dropped = true;
            save();
            event.stopImmediatePropagation();
            this.close();
          }
        }
      });
    }
    send(data) {
      if (typeof data === 'string') {
        for (const frame of data.split('\x1e').filter(Boolean)) {
          let message;
          try { message = JSON.parse(frame); } catch { continue; }
          if (message.target === 'Join') probe.joined++;
          if (message.target === 'Command' && ['manualLibraryDraw', 'manualLibraryShuffle', 'manualLibrarySearch', 'manualLibraryLook', 'manualLibraryStopLooking', 'manualLibraryReposition', 'manualLibraryArrange'].includes(message.arguments?.[1]?.kind)) {
            probe.requests.push(message.arguments[1].requestId);
            (probe.commands ??= []).push(message.arguments[1].kind);
            (probe.payloads ??= []).push(message.arguments[1]);
            if (!probe.dropped && (!window.libraryDeliveryTarget || window.libraryDeliveryTarget === message.arguments[1].kind))
              this.lostInvocation = message.invocationId;
          }
          save();
        }
      }
      return super.send(data);
    }
  };
})();
