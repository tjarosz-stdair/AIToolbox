// Manual broker simulator for /embed/chat (Admin/EmbedTestHarness). Mirror image of
// embed-chat.js's own bridge plumbing: same message shapes, opposite direction — this posts
// broker-to-panel messages by hand and logs whatever the panel posts back.
(function () {
    'use strict';

    function wireEmbedHarness() {
        var root = document.querySelector('[data-embed-harness]');
        if (!root) {
            return;
        }

        var ownOrigin = root.dataset.ownOrigin;
        var iframe = root.querySelector('[data-harness-iframe]');
        var log = root.querySelector('[data-log]');
        var visible = true;

        function field(name) {
            var el = root.querySelector('[data-field="' + name + '"]');
            return el ? el.value : '';
        }

        function appendLog(direction, payload) {
            var line = document.createElement('div');
            line.textContent = new Date().toLocaleTimeString() + ' ' + direction + ' ' + JSON.stringify(payload);
            log.appendChild(line);
            log.scrollTop = log.scrollHeight;
        }

        // The only postMessage call site here, always targeted at this page's own origin — the
        // iframe validates event.origin against exactly that, so anything else would be silently
        // discarded by the panel anyway.
        function postToPanel(payload) {
            appendLog('→ panel', payload);
            iframe.contentWindow.postMessage(payload, ownOrigin);
        }

        function surfacesList() {
            return field('surfaces')
                .split(',')
                .map(function (s) { return s.trim(); })
                .filter(function (s) { return s.length > 0; });
        }

        function pageContext() {
            var title = field('pageContextTitle');
            return title ? { title: title } : null;
        }

        root.querySelector('[data-action="send-init"]').addEventListener('click', function () {
            postToPanel({
                type: 'aitoolbox:init',
                version: 1,
                sessionToken: field('sessionToken') || null,
                surfaces: surfacesList(),
                contextKey: field('contextKey'),
                pageContext: pageContext()
            });
        });

        root.querySelector('[data-action="send-context"]').addEventListener('click', function () {
            postToPanel({
                type: 'aitoolbox:context',
                version: 1,
                surfaces: surfacesList(),
                contextKey: field('contextKey'),
                pageContext: pageContext()
            });
        });

        root.querySelector('[data-action="send-token"]').addEventListener('click', function () {
            postToPanel({
                type: 'aitoolbox:token',
                version: 1,
                sessionToken: field('sessionToken') || null
            });
        });

        root.querySelector('[data-action="toggle-visibility"]').addEventListener('click', function () {
            visible = !visible;
            postToPanel({ type: 'aitoolbox:visibility', version: 1, visible: visible });
        });

        window.addEventListener('message', function (event) {
            if (event.origin !== ownOrigin) {
                return;
            }
            if (event.source !== iframe.contentWindow) {
                return;
            }
            var data = event.data;
            if (!data || typeof data !== 'object' || typeof data.type !== 'string' || data.type.indexOf('aitoolbox:') !== 0) {
                return;
            }
            appendLog('← panel', data);

            // A real broker would also respond to intent/token-expired here (e.g. mint a fresh
            // token and send it back). This harness only logs them — driving the reply is a
            // manual "click Send token" step, matching its purpose as a hand-operated tool.
        });
    }

    document.addEventListener('DOMContentLoaded', wireEmbedHarness);
})();
