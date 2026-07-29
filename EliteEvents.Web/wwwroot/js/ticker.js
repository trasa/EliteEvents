// The only client-side script on the site. htmx does the swapping; this just keeps the ticker
// from growing without bound and shows whether the SSE stream is actually connected.
(function () {
    "use strict";

    var MAX_ROWS = 40;

    function trim() {
        var ticker = document.getElementById("ticker");
        if (!ticker) {
            return;
        }
        while (ticker.children.length > MAX_ROWS) {
            ticker.removeChild(ticker.lastElementChild);
        }
        var empty = document.getElementById("ticker-empty");
        if (empty && ticker.children.length > 0) {
            empty.remove();
        }
    }

    function status(connected) {
        var dot = document.getElementById("sse-status");
        var text = document.getElementById("sse-status-text");
        if (dot) {
            dot.className = "dot " + (connected ? "on" : "off");
        }
        if (text) {
            text.textContent = connected ? "connected" : "offline";
        }
    }

    // sse.js swaps first and fires htmx:sseMessage after, so trimming here runs on the new list.
    document.body.addEventListener("htmx:sseMessage", trim);
    document.body.addEventListener("htmx:sseOpen", function () { status(true); });
    document.body.addEventListener("htmx:sseError", function () { status(false); });
})();
