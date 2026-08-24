// Protocol client for /embed/chat. Same idiom as site.js (plain function declarations, one
// DOMContentLoaded entry point, data-* attributes for config) but a separate file on purpose:
// this page opts out of the shared layout that makes site.js's "one script on every page" design
// necessary, and a CSP with no 'unsafe-inline' applies here, so nothing in this file may rely on
// inline <script>/style attributes.
(function () {
    'use strict';

    var INIT_TIMEOUT_MS = 3000;
    var TOKEN_TIMEOUT_MS = 5000;
    var INTENT_TIMEOUT_MS = 10000;
    var REQUEST_CEILING_MS = 120000;
    var PROACTIVE_REFRESH_LEAD_MS = 120000;
    var MAX_ACK_JOURNAL_ENTRIES = 3;

    var SURFACE_LABELS = {
        picsearch: 'Carrier Part Search',
        orderentry: 'Order Entry',
        overlay: 'Overlay'
    };

    function wireEmbedChat() {
        var root = document.querySelector('[data-embed-chat]');
        if (!root) {
            return;
        }

        var expectedOrigin = root.dataset.parentOrigin || '';
        var maxMessageLength = parseInt(root.dataset.maxMessageLength, 10) || 4000;
        var preferredWidth = parseInt(root.dataset.preferredWidth, 10) || 380;

        var transcript = root.querySelector('[data-transcript]');
        var composerForm = root.querySelector('[data-composer-form]');
        var composerInput = root.querySelector('[data-composer-input]');
        var composerButton = root.querySelector('[data-composer-button]');
        var statusBanner = root.querySelector('[data-status-banner]');
        var contextChip = root.querySelector('[data-context-chip]');
        var surfacePills = root.querySelector('[data-surface-pills]');

        // ---- state ----
        var bridgeState = 'awaiting-init'; // awaiting-init | linked | unauthenticated | awaiting-token
        var turnState = 'idle'; // idle | sending | deferred
        var visible = true;
        var sessionToken = null;
        var tokenGeneration = 0;
        var turnEpoch = 0;
        var contextKey = null;
        var currentPageContext = null;
        var currentSurfaces = [];
        var pendingMessage = null;
        var resizeQueued = false;
        var initTimeoutHandle = null;
        var tokenTimeoutHandle = null;
        var proactiveRefreshHandle = null;
        var pendingIntents = new Map();
        var ackJournal = [];
        var thinkingElement = null;
        var thinkingTickHandle = null;
        var thinkingStartedAtMs = null;

        // ---- origin-validated bridge plumbing ----

        function canPostToParent() {
            return !!expectedOrigin && window.parent !== window;
        }

        // The one and only postMessage call site in this file — every outgoing message funnels
        // through here, always with the exact expected origin, never "*".
        function postToParent(payload) {
            if (!canPostToParent()) {
                return;
            }
            window.parent.postMessage(payload, expectedOrigin);
        }

        function handleIncoming(event) {
            if (event.origin !== expectedOrigin) {
                return;
            }
            if (event.source !== window.parent) {
                return;
            }
            var data = event.data;
            if (!data || typeof data !== 'object') {
                return;
            }
            if (typeof data.type !== 'string' || data.type.indexOf('aitoolbox:') !== 0) {
                return;
            }
            if (data.version !== 1) {
                console.warn('[embed-chat] ignoring unsupported protocol version', data);
                return;
            }

            switch (data.type) {
                case 'aitoolbox:init':
                    onInit(data);
                    break;
                case 'aitoolbox:context':
                    onContext(data);
                    break;
                case 'aitoolbox:ack':
                    onAck(data);
                    break;
                case 'aitoolbox:token':
                    onToken(data);
                    break;
                case 'aitoolbox:visibility':
                    onVisibility(data);
                    break;
                default:
                    // Forward-compatible: an unrecognized message type from a newer broker is
                    // ignored, not an error.
                    break;
            }
        }

        // ---- protocol state transitions ----

        function onInit(data) {
            clearInitTimeout();
            contextKey = data.contextKey || null;
            currentPageContext = data.pageContext || null;
            currentSurfaces = Array.isArray(data.surfaces) ? data.surfaces : [];
            renderContextChip();
            renderSurfacePills();

            if (data.sessionToken) {
                linkWithToken(data.sessionToken);
            } else {
                enterUnauthenticated('This chat requires you to be signed in on this page.');
            }

            requestResize(preferredWidth);
        }

        function onContext(data) {
            var newContextKey = data.contextKey || null;
            currentPageContext = data.pageContext || currentPageContext;
            currentSurfaces = Array.isArray(data.surfaces) ? data.surfaces : currentSurfaces;
            renderSurfacePills();
            renderContextChip();

            if (newContextKey !== contextKey) {
                // A message held while awaiting a fresh token (bridgeState === 'awaiting-token')
                // belongs to the OLD context/conversation. contextKey is about to be overwritten
                // below, so if that message were left in pendingMessage it would get sent under
                // the NEW context once a token arrives — silently mixing two contexts, exactly
                // what the context-binding design exists to prevent. Discard it instead.
                var droppedPendingMessage = pendingMessage !== null;
                pendingMessage = null;

                contextKey = newContextKey;
                turnEpoch += 1;
                ackJournal = [];
                pendingIntents.clear();
                clearTranscript();
                renderSystemNotice('Starting a new conversation for ' + describeContext() + '.');
                if (droppedPendingMessage) {
                    renderSystemNotice("Your last message wasn't sent — the context changed before it could go through.");
                }
            }
        }

        function onAck(data) {
            if (!Array.isArray(data.acks)) {
                return;
            }
            data.acks.forEach(function (ack) {
                var pending = pendingIntents.get(ack.intentId);
                if (!pending) {
                    // Unknown id — never trusted to create transcript content; an untracked id
                    // could otherwise let the parent frame inject arbitrary text.
                    console.warn('[embed-chat] ack for unknown intentId ignored', ack.intentId);
                    return;
                }
                pendingIntents.delete(ack.intentId);
                updateIntentChip(pending, ack.status, ack.detail);
                pushAckJournal(pending.name + ': ' + (ack.status || 'updated') + (ack.detail ? ' (' + ack.detail + ')' : ''));
            });
        }

        function onToken(data) {
            if (!data.sessionToken) {
                return;
            }
            clearTokenTimeout();
            linkWithToken(data.sessionToken);
            if (pendingMessage !== null) {
                var toSend = pendingMessage;
                pendingMessage = null;
                turnState = 'sending';
                setComposerEnabled(false);
                sendTurn(toSend);
            }
        }

        function onVisibility(data) {
            visible = !!data.visible;
            if (visible) {
                if (thinkingElement) {
                    startThinkingTicker();
                }
                flushQueuedResize();
            } else {
                stopThinkingTicker();
            }
        }

        function linkWithToken(token) {
            sessionToken = token;
            tokenGeneration += 1;
            scheduleProactiveRefresh(token);
            bridgeState = 'linked';
            setComposerEnabled(turnState === 'idle');
            hideBanner();
        }

        function enterUnauthenticated(message) {
            bridgeState = 'unauthenticated';
            sessionToken = null;
            setComposerEnabled(false);
            showBanner(message);
        }

        function enterAwaitingToken() {
            bridgeState = 'awaiting-token';
            setComposerEnabled(false);
            var myGeneration = tokenGeneration;
            postToParent({ type: 'aitoolbox:token-expired', version: 1 });
            tokenTimeoutHandle = window.setTimeout(function () {
                tokenTimeoutHandle = null;
                if (tokenGeneration !== myGeneration) {
                    return; // a fresh token already arrived — stale timeout, ignore
                }
                var hadPending = pendingMessage !== null;
                pendingMessage = null;
                turnState = 'idle';
                enterUnauthenticated("Your session expired and a new one didn't arrive in time.");
                if (hadPending) {
                    renderSystemNotice("Your last message wasn't sent — please try again once reconnected.");
                }
            }, TOKEN_TIMEOUT_MS);
        }

        // ---- timeouts ----

        function armInitTimeout() {
            initTimeoutHandle = window.setTimeout(function () {
                initTimeoutHandle = null;
                if (bridgeState === 'awaiting-init') {
                    enterUnauthenticated("Couldn't connect to the host page. Chat isn't available right now.");
                }
            }, INIT_TIMEOUT_MS);
        }

        function clearInitTimeout() {
            if (initTimeoutHandle !== null) {
                window.clearTimeout(initTimeoutHandle);
                initTimeoutHandle = null;
            }
        }

        function clearTokenTimeout() {
            if (tokenTimeoutHandle !== null) {
                window.clearTimeout(tokenTimeoutHandle);
                tokenTimeoutHandle = null;
            }
        }

        function scheduleProactiveRefresh(token) {
            if (proactiveRefreshHandle !== null) {
                window.clearTimeout(proactiveRefreshHandle);
                proactiveRefreshHandle = null;
            }
            var exp = decodeJwtExpiry(token);
            if (exp === null) {
                return;
            }
            var msUntilRefresh = (exp * 1000) - Date.now() - PROACTIVE_REFRESH_LEAD_MS;
            if (msUntilRefresh <= 0) {
                return;
            }
            var myGeneration = tokenGeneration;
            proactiveRefreshHandle = window.setTimeout(function () {
                proactiveRefreshHandle = null;
                if (tokenGeneration === myGeneration && turnState === 'idle' && bridgeState === 'linked') {
                    enterAwaitingToken();
                }
            }, msUntilRefresh);
        }

        // Advisory only — never a trust decision. Server-side validation stays authoritative;
        // this only lets the panel refresh proactively before the server ever rejects anything.
        function decodeJwtExpiry(token) {
            try {
                var parts = token.split('.');
                if (parts.length < 2) {
                    return null;
                }
                var payload = JSON.parse(base64UrlDecode(parts[1]));
                return typeof payload.exp === 'number' ? payload.exp : null;
            } catch (e) {
                return null;
            }
        }

        function base64UrlDecode(value) {
            var padded = value.replace(/-/g, '+').replace(/_/g, '/');
            var remainder = padded.length % 4;
            var withPadding = remainder === 0 ? padded : padded + '='.repeat(4 - remainder);
            return decodeURIComponent(escape(window.atob(withPadding)));
        }

        // ---- sending a turn ----

        function submitMessage(text) {
            if (bridgeState !== 'linked' || turnState !== 'idle') {
                return;
            }

            renderUserMessage(text);
            var textToSend = prependAckJournal(text, maxMessageLength);

            turnState = 'sending';
            setComposerEnabled(false);
            sendTurn(textToSend);
        }

        function prependAckJournal(text, limit) {
            if (ackJournal.length === 0) {
                return text;
            }
            var block = '[Host page updates: ' + ackJournal.join('; ') + ']';
            renderSystemNotice(block);
            ackJournal = [];
            var combined = block + '\n' + text;
            return combined.length > limit ? combined.slice(0, limit) : combined;
        }

        function sendTurn(text) {
            var epochAtSend = turnEpoch;
            showThinking();

            var controller = ('AbortController' in window) ? new AbortController() : null;
            var ceilingHandle = window.setTimeout(function () {
                if (controller) {
                    controller.abort();
                }
            }, REQUEST_CEILING_MS);

            function stillCurrent() {
                return epochAtSend === turnEpoch;
            }

            function onEvent(message) {
                if (!stillCurrent()) {
                    return;
                }
                switch (message.type) {
                    case 'started':
                        // Thinking indicator is already showing; nothing further to do.
                        break;
                    case 'conversationReset':
                        renderSystemNotice(message.message);
                        break;
                    case 'intent':
                        // The local closure function, not window.AIToolboxEmbed.sendIntent — that
                        // indirection exists for external/console use, not for code already
                        // inside this same closure.
                        sendIntent({
                            name: message.name,
                            kind: message.kind,
                            surface: message.surface,
                            arguments: message.arguments
                        });
                        break;
                    case 'complete':
                        hideThinking();
                        renderAssistantMessage(message.message, message.toolsUsed);
                        finishTurn();
                        break;
                    case 'error':
                        hideThinking();
                        onTurnError(message, text);
                        break;
                    default:
                        // Unknown event type — ignored silently so a future contract addition
                        // needs no coordinated deploy.
                        break;
                }
            }

            function onTransportFailure() {
                if (!stillCurrent()) {
                    return;
                }
                hideThinking();
                renderAssistantError('The assistant could not be reached. Please try again.');
                finishTurn();
            }

            fetch('/api/picsearch/ask', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'X-P21-Session-Token': sessionToken || ''
                },
                body: JSON.stringify({
                    contextKey: contextKey,
                    message: text,
                    uiSurfaces: currentSurfaces,
                    pageContext: currentPageContext
                }),
                signal: controller ? controller.signal : undefined
            }).then(function (response) {
                if (!stillCurrent()) {
                    return;
                }
                var contentType = response.headers.get('content-type') || '';
                if (contentType.indexOf('application/x-ndjson') !== 0) {
                    return response.text().then(function () {
                        if (!stillCurrent()) {
                            return;
                        }
                        hideThinking();
                        renderAssistantError('The assistant returned an unexpected response (' + response.status + ').');
                        finishTurn();
                    });
                }
                return readNdjsonStream(response.body, onEvent);
            }).catch(onTransportFailure).finally(function () {
                window.clearTimeout(ceilingHandle);
            });

            function onTurnError(message, originalText) {
                if (message.code === 'session_token_rejected' || message.code === 'session_token_required') {
                    pendingMessage = originalText;
                    turnState = 'deferred';
                    enterAwaitingToken();
                    return;
                }

                renderAssistantError(message.message || 'Something went wrong.');
                finishTurn();
            }
        }

        function readNdjsonStream(body, onEvent) {
            var reader = body.getReader();
            var decoder = new TextDecoder();
            var buffer = '';

            function handleLine(line) {
                var trimmed = line.trim();
                if (!trimmed) {
                    return;
                }
                var message;
                try {
                    message = JSON.parse(trimmed);
                } catch (e) {
                    return;
                }
                onEvent(message);
            }

            function pump() {
                return reader.read().then(function (result) {
                    if (result.done) {
                        var remainder = buffer.trim();
                        if (remainder) {
                            handleLine(remainder);
                        }
                        return;
                    }
                    buffer += decoder.decode(result.value, { stream: true });
                    var lines = buffer.split('\n');
                    buffer = lines.pop();
                    lines.forEach(handleLine);
                    return pump();
                });
            }

            return pump();
        }

        function finishTurn() {
            turnState = 'idle';
            setComposerEnabled(bridgeState === 'linked');
        }

        // ---- intents (transport only — nothing calls this yet; exposed for the broker/console) ----

        function sendIntent(descriptor) {
            var intentId = (window.crypto && window.crypto.randomUUID)
                ? window.crypto.randomUUID()
                : String(Date.now()) + '-' + Math.random().toString(36).slice(2);

            var entry = {
                intentId: intentId,
                name: descriptor.name,
                kind: descriptor.kind,
                surface: descriptor.surface,
                turnEpoch: turnEpoch,
                chipElement: null
            };
            pendingIntents.set(intentId, entry);
            entry.chipElement = renderIntentChip(entry);

            postToParent({
                type: 'aitoolbox:intent',
                version: 1,
                intentId: intentId,
                surface: descriptor.surface,
                name: descriptor.name,
                kind: descriptor.kind,
                arguments: descriptor.arguments
            });

            window.setTimeout(function () {
                if (pendingIntents.has(intentId)) {
                    pendingIntents.delete(intentId);
                    updateIntentChip(entry, 'no-response', 'the host page did not respond');
                }
            }, INTENT_TIMEOUT_MS);

            return intentId;
        }

        function pushAckJournal(summary) {
            ackJournal.push(summary);
            if (ackJournal.length > MAX_ACK_JOURNAL_ENTRIES) {
                ackJournal.shift();
            }
        }

        // ---- rendering ----

        function escapeHtml(value) {
            return String(value)
                .replace(/&/g, '&amp;')
                .replace(/</g, '&lt;')
                .replace(/>/g, '&gt;')
                .replace(/"/g, '&quot;')
                .replace(/'/g, '&#39;');
        }

        function textToHtml(text) {
            return escapeHtml(text).replace(/\n/g, '<br>');
        }

        function appendBubble(cssClass, innerHtml) {
            var el = document.createElement('div');
            el.className = 'embed-message ' + cssClass;
            el.innerHTML = innerHtml;
            transcript.appendChild(el);
            transcript.scrollTop = transcript.scrollHeight;
            return el;
        }

        function renderUserMessage(text) {
            appendBubble('embed-message-user', textToHtml(text));
        }

        function renderAssistantMessage(text, toolsUsed) {
            var html = textToHtml(text || '');
            if (Array.isArray(toolsUsed) && toolsUsed.length > 0) {
                html += '<span class="embed-message-footnote">used: ' + escapeHtml(toolsUsed.join(', ')) + '</span>';
            }
            appendBubble('embed-message-assistant', html);
        }

        function renderAssistantError(text) {
            appendBubble('embed-message-error', textToHtml(text));
        }

        function renderSystemNotice(text) {
            appendBubble('embed-message-system', textToHtml(text));
        }

        function showThinking() {
            thinkingStartedAtMs = Date.now();
            thinkingElement = appendBubble('embed-message-thinking', 'Working&hellip;');
            if (visible) {
                startThinkingTicker();
            }
        }

        function startThinkingTicker() {
            stopThinkingTicker();
            if (!thinkingElement) {
                return;
            }
            thinkingTickHandle = window.setInterval(updateThinkingCopy, 1000);
        }

        function stopThinkingTicker() {
            if (thinkingTickHandle !== null) {
                window.clearInterval(thinkingTickHandle);
                thinkingTickHandle = null;
            }
        }

        function updateThinkingCopy() {
            if (!thinkingElement || thinkingStartedAtMs === null) {
                return;
            }
            var elapsedSeconds = Math.floor((Date.now() - thinkingStartedAtMs) / 1000);
            var copy = 'Working&hellip;';
            if (elapsedSeconds >= 15) {
                copy = 'Complex lookups can take a while&hellip;';
            } else if (elapsedSeconds >= 5) {
                copy = 'Still working&hellip;';
            }
            thinkingElement.innerHTML = copy;
        }

        function hideThinking() {
            stopThinkingTicker();
            if (thinkingElement && thinkingElement.parentNode) {
                thinkingElement.parentNode.removeChild(thinkingElement);
            }
            thinkingElement = null;
            thinkingStartedAtMs = null;
        }

        function clearTranscript() {
            transcript.innerHTML = '';
        }

        function describeContext() {
            if (currentPageContext && typeof currentPageContext.title === 'string' && currentPageContext.title.length > 0 && currentPageContext.title.length <= 80) {
                return currentPageContext.title;
            }
            if (currentPageContext && typeof currentPageContext.label === 'string' && currentPageContext.label.length > 0 && currentPageContext.label.length <= 80) {
                return currentPageContext.label;
            }
            return contextKey || 'this context';
        }

        function renderContextChip() {
            var surfaceLabels = currentSurfaces.map(function (surface) {
                return SURFACE_LABELS[surface] || surface;
            });
            var text = describeContext();
            if (surfaceLabels.length > 0) {
                text += ' · ' + surfaceLabels.join(', ');
            }
            contextChip.textContent = text;
        }

        function renderSurfacePills() {
            surfacePills.innerHTML = '';
            currentSurfaces.forEach(function (surface) {
                var pill = document.createElement('span');
                pill.className = 'embed-surface-pill';
                // Unknown surface ids render as-is rather than being dropped, so protocol drift
                // is visible instead of silently hidden.
                pill.textContent = SURFACE_LABELS[surface] || surface;
                surfacePills.appendChild(pill);
            });
        }

        function renderIntentChip(entry) {
            return appendBubble('embed-intent-chip', 'Requested: ' + escapeHtml(entry.name));
        }

        function updateIntentChip(entry, status, detail) {
            if (!entry.chipElement) {
                return;
            }
            var text = 'Requested: ' + entry.name + ' — ' + status;
            if (detail) {
                text += ' (' + detail + ')';
            }
            entry.chipElement.textContent = text;
        }

        function setComposerEnabled(enabled) {
            composerInput.disabled = !enabled;
            composerButton.disabled = !enabled;
        }

        function showBanner(text) {
            statusBanner.textContent = text;
            statusBanner.hidden = false;
        }

        function hideBanner() {
            statusBanner.hidden = true;
            statusBanner.textContent = '';
        }

        function requestResize(width) {
            if (!visible) {
                resizeQueued = true;
                return;
            }
            postToParent({ type: 'aitoolbox:resize', version: 1, width: width });
        }

        function flushQueuedResize() {
            if (resizeQueued) {
                resizeQueued = false;
                postToParent({ type: 'aitoolbox:resize', version: 1, width: preferredWidth });
            }
        }

        // ---- boot ----

        window.addEventListener('message', handleIncoming);

        composerForm.addEventListener('submit', function (event) {
            event.preventDefault();
            var text = (composerInput.value || '').trim();
            if (!text) {
                return;
            }
            if (text.length > maxMessageLength) {
                text = text.slice(0, maxMessageLength);
            }
            composerInput.value = '';
            submitMessage(text);
        });

        setComposerEnabled(false);
        renderContextChip();

        if (!canPostToParent()) {
            enterUnauthenticated('This page was opened directly and has no host page to connect to.');
        } else {
            armInitTimeout();
            postToParent({ type: 'aitoolbox:ready', version: 1 });
        }

        window.AIToolboxEmbed = window.AIToolboxEmbed || {};
        window.AIToolboxEmbed.sendIntent = sendIntent;
    }

    document.addEventListener('DOMContentLoaded', wireEmbedChat);
})();
