// claude-watch dev reload client. Served by the watcher; only loaded when the app
// was launched by claude-watch (CLAUDE_WATCH_PORT env var gates the script include).
(function () {
  "use strict";
  if (window.__claudeWatchReload) return; // guard against double inclusion
  window.__claudeWatchReload = true;

  var origin = new URL(document.currentScript.src).origin;
  var retryMs = 1000;

  function connect() {
    var source = new EventSource(origin + "/events");

    source.addEventListener("reload", function () {
      location.reload();
    });

    // css-only round: swap the matching <link> to the watcher-served fresh copy —
    // no page reload, Blazor circuit stays alive. Uses the clone-then-remove-on-load
    // technique (from dotnet watch) so there is no unstyled flash.
    source.addEventListener("update-css", function (e) {
      var data;
      try { data = JSON.parse(e.data); } catch { return; }
      if (!data || !data.path || !data.url) return;

      var links = document.querySelectorAll('link[rel="stylesheet"]');
      var target = null;
      for (var i = 0; i < links.length; i++) {
        var pathname = new URL(links[i].href, document.baseURI).pathname.replace(/^\//, "");
        // match the app-served route or a previous watcher-served override of it
        if (pathname === data.path || pathname === "asset/" + data.path) { target = links[i]; break; }
      }
      if (!target || target.__claudeWatchLoading) {
        if (!target) console.debug("claude-watch: no stylesheet matches", data.path);
        return;
      }

      var fresh = target.cloneNode();
      fresh.href = data.url + "?nonce=" + Date.now();
      target.__claudeWatchLoading = true;
      fresh.addEventListener("load", function () {
        target.remove();
      });
      fresh.addEventListener("error", function () {
        target.__claudeWatchLoading = false;
        fresh.remove();
      });
      target.parentNode.insertBefore(fresh, target.nextSibling);
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
