// claude-watch dev reload client. Served by the watcher; only loaded when the app
// was launched by claude-watch (CLAUDE_WATCH_PORT env var gates the script include).
(function () {
  "use strict";
  var origin = new URL(document.currentScript.src).origin;
  var retryMs = 1000;

  function connect() {
    var source = new EventSource(origin + "/events");
    source.addEventListener("reload", function () {
      location.reload();
    });
    source.onopen = function () {
      retryMs = 1000;
    };
    source.onerror = function () {
      // watcher restarting or gone — back off and retry
      source.close();
      setTimeout(connect, retryMs);
      retryMs = Math.min(retryMs * 2, 10000);
    };
  }

  connect();
})();
