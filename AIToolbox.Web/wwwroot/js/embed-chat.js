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
        var resumeBanner = root.querySelector('[data-resume-banner]');
        var resumeBannerText = root.querySelector('[data-resume-banner-text]');
        var resumeBannerResetButton = root.querySelector('[data-resume-banner-reset]');
        var contextChip = root.querySelector('[data-context-chip]');
        var surfacePills = root.querySelector('[data-surface-pills]');
        var autoNavigateRow = root.querySelector('[data-auto-navigate-row]');
        var autoNavigateCheckbox = root.querySelector('[data-auto-navigate-checkbox]');
        var resetButton = root.querySelector('[data-reset-button]');
        var micButton = root.querySelector('[data-mic-button]');
        var speechEnabled = root.dataset.speechEnabled === 'true';

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
        // Most recent live tool-step label (from the "toolStep" NDJSON event) -- shown by
        // updateThinkingCopy() instead of the time-tiered copy while it's recent, so a multi-tool
        // turn reads as "Looking up part specs..." rather than just "Still working...".
        var latestToolStepLabel = null;
        var latestToolStepAtMs = null;
        var mediaRecorder = null;
        var recordedChunks = [];
        var micStream = null;
        var micSafetyTimeoutHandle = null;
        var MIC_MAX_RECORDING_MS = 60000;
        // Set from aitoolbox:init — true only for the Chrome extension side panel broker, which
        // lacks the UX affordance to show a getUserMedia permission prompt at all (Chrome
        // resolves the request as denied instantly, no dialog ever shown). When true, recording
        // is delegated up to the broker's own offscreen document instead of calling
        // getUserMedia locally. Standalone PIC Search and the admin test harness never set this.
        var micDelegated = false;
        var micRecording = false;

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
                case 'aitoolbox:mic-started':
                    onMicStarted();
                    break;
                case 'aitoolbox:mic-audio':
                    onMicAudio(data);
                    break;
                case 'aitoolbox:mic-error':
                    onMicError(data);
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
            micDelegated = !!data.micDelegated;
            renderContextChip();
            renderSurfacePills();
            updateAutoNavigateVisibility();

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
            updateAutoNavigateVisibility();

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
                hideResumeBanner();
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
            fetchActiveConversationHistory();
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

        // ---- manual reset ----

        // A direct browser session (no P21 broker driving context changes) never trips the
        // automatic reset triggers (context change, turn cap, inactivity) — this is the only way
        // to start a fresh conversation for the current context without waiting one out.
        function resetConversation() {
            if (bridgeState !== 'linked' || turnState !== 'idle' || !contextKey) {
                return;
            }
            setComposerEnabled(false);
            fetch('/api/picsearch/reset', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'X-P21-Session-Token': sessionToken || ''
                },
                body: JSON.stringify({ contextKey: contextKey })
            }).then(function (response) {
                return response.json().catch(function () { return null; });
            }).then(function (data) {
                setComposerEnabled(bridgeState === 'linked');
                if (!data || !data.success) {
                    renderSystemNotice('Could not reset the conversation. Please try again.');
                    return;
                }
                turnEpoch += 1;
                ackJournal = [];
                pendingIntents.clear();
                clearTranscript();
                hideResumeBanner();
                renderSystemNotice('Conversation reset for ' + describeContext() + '.');
            }).catch(function () {
                setComposerEnabled(bridgeState === 'linked');
                renderSystemNotice('Could not reset the conversation. Please try again.');
            });
        }

        // ---- resuming an already-active conversation ----
        //
        // SubmitTurnAsync already finds and continues an Active conversation server-side for this
        // sub+surface+contextKey (see PicSearchChatService) -- this just lets the browser reflect
        // that on load instead of always starting from a blank transcript. Fired once contextKey
        // and sessionToken are both known (right after linkWithToken), well before the user could
        // have sent anything themselves.

        function fetchActiveConversationHistory() {
            if (!contextKey || !sessionToken) {
                return;
            }
            var contextKeyAtFetch = contextKey;
            var turnEpochAtFetch = turnEpoch;
            fetch('/api/picsearch/history', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'X-P21-Session-Token': sessionToken
                },
                body: JSON.stringify({ contextKey: contextKey })
            }).then(function (response) {
                return response.json().catch(function () { return null; });
            }).then(function (data) {
                if (!data || !data.success || !data.hasActiveConversation) {
                    return;
                }
                // Stale by the time it arrived -- context changed, a reset happened, or the user
                // already sent/received a live message while this was in flight. Any of those
                // must win over a late history response; never clobber real, current content.
                if (contextKeyAtFetch !== contextKey || turnEpochAtFetch !== turnEpoch || transcript.children.length > 0) {
                    return;
                }
                (data.messages || []).forEach(function (item) {
                    if (item.role === 'assistant') {
                        renderAssistantMessage(item.html, item.text, null, null);
                    } else {
                        renderUserMessage(item.text);
                    }
                });
                showResumeBanner(data.turnCount);
            }).catch(function () {
                // Best effort only -- resuming history is a nice-to-have, never blocks the chat
                // from working.
            });
        }

        // ---- voice input ----
        //
        // Voice-to-text only, never read-back: a transcript populates the composer input for the
        // user to review and edit, exactly like typed text — it never auto-submits. The mic button
        // itself is only ever shown when data-speech-enabled is true (decided server-side, see
        // EmbedController) AND the browser actually supports MediaRecorder/getUserMedia; Chrome,
        // the only real target here, has both.

        function micSupported() {
            if (!speechEnabled) {
                return false;
            }
            if (micDelegated) {
                return true; // capture happens in the broker's own offscreen document, not here
            }
            return typeof window.MediaRecorder === 'function'
                && !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia);
        }

        function toggleRecording() {
            if (micDelegated) {
                if (micRecording) {
                    stopRecording();
                } else {
                    startRecording();
                }
                return;
            }
            if (mediaRecorder && mediaRecorder.state === 'recording') {
                stopRecording();
                return;
            }
            startRecording();
        }

        function startRecording() {
            if (bridgeState !== 'linked' || turnState !== 'idle') {
                return;
            }

            if (micDelegated) {
                micRecording = true;
                // Not is-recording yet -- the side panel still has to receive this message,
                // acquire the mic fresh (see sidepanel.ts), and actually start MediaRecorder.
                // Turning the icon red now would invite the user to talk before capture has
                // begun. onMicStarted() flips this to is-recording once that's confirmed.
                micButton.classList.add('is-connecting');
                micSafetyTimeoutHandle = window.setTimeout(function () {
                    if (micRecording) {
                        stopRecording();
                    }
                }, MIC_MAX_RECORDING_MS);
                postToParent({ type: 'aitoolbox:mic-start', version: 1 });
                return;
            }

            navigator.mediaDevices.getUserMedia({ audio: true }).then(function (stream) {
                micStream = stream;
                recordedChunks = [];

                try {
                    mediaRecorder = new MediaRecorder(stream);
                } catch (e) {
                    releaseMicStream();
                    renderSystemNotice('Voice input could not start on this browser.');
                    return;
                }

                mediaRecorder.addEventListener('dataavailable', function (event) {
                    if (event.data && event.data.size > 0) {
                        recordedChunks.push(event.data);
                    }
                });
                mediaRecorder.addEventListener('stop', onRecordingStopped);

                mediaRecorder.start();
                micButton.classList.add('is-recording');
                micSafetyTimeoutHandle = window.setTimeout(function () {
                    if (mediaRecorder && mediaRecorder.state === 'recording') {
                        stopRecording();
                    }
                }, MIC_MAX_RECORDING_MS);
            }).catch(function () {
                renderSystemNotice('Microphone access is blocked for this page.');
            });
        }

        function stopRecording() {
            if (micSafetyTimeoutHandle !== null) {
                window.clearTimeout(micSafetyTimeoutHandle);
                micSafetyTimeoutHandle = null;
            }

            if (micDelegated) {
                if (!micRecording) {
                    return;
                }
                micRecording = false;
                micButton.disabled = true;
                postToParent({ type: 'aitoolbox:mic-stop', version: 1 });
                return;
            }

            if (mediaRecorder && mediaRecorder.state === 'recording') {
                mediaRecorder.stop();
            }
        }

        function releaseMicStream() {
            if (micStream) {
                micStream.getTracks().forEach(function (track) { track.stop(); });
                micStream = null;
            }
            micButton.classList.remove('is-recording');
        }

        function onRecordingStopped() {
            var chunks = recordedChunks;
            var mimeType = mediaRecorder ? (mediaRecorder.mimeType || 'audio/webm') : 'audio/webm';
            recordedChunks = [];
            mediaRecorder = null;
            releaseMicStream();

            if (chunks.length === 0) {
                return;
            }

            var blob = new Blob(chunks, { type: mimeType });
            sendSpeechToText(blob);
        }

        // Broker (side panel) recorded the audio directly and is handing the raw bytes back down
        // — same fetch as the local-capture path, just a different source for the Blob. Never a
        // separate copy of the request logic.
        function onMicStarted() {
            if (!micRecording) {
                return; // stopped or errored before this confirmation arrived -- stale, ignore
            }
            micButton.classList.remove('is-connecting');
            micButton.classList.add('is-recording');
        }

        function onMicAudio(data) {
            micButton.classList.remove('is-connecting');
            micButton.classList.remove('is-recording');
            if (!data || !data.arrayBuffer) {
                micButton.disabled = bridgeState !== 'linked';
                renderSystemNotice("Didn't catch that — try again.");
                return;
            }
            var blob = new Blob([data.arrayBuffer], { type: data.mimeType || 'audio/webm' });
            sendSpeechToText(blob);
        }

        function onMicError(data) {
            micRecording = false;
            micButton.classList.remove('is-connecting');
            micButton.classList.remove('is-recording');
            micButton.disabled = bridgeState !== 'linked';
            renderSystemNotice((data && data.message) || 'Voice input failed. Please try again.');
        }

        function sendSpeechToText(blob) {
            micButton.disabled = true;
            // The one client-side fact that answers "did it actually record anything" without
            // needing the server log at all — check this in DevTools before anything else. A
            // suspiciously tiny size (well under a KB for what should be a several-second clip)
            // means the capture itself came up empty; a normal size means the bytes are fine and
            // whatever's wrong is downstream (the network call or Azure's own recognition).
            console.log('[embed-chat] sending speech-to-text request', { size: blob.size, type: blob.type });
            fetch('/api/picsearch/speech-to-text', {
                method: 'POST',
                headers: {
                    'Content-Type': blob.type || 'audio/webm',
                    'X-P21-Session-Token': sessionToken || ''
                },
                body: blob
            }).then(function (response) {
                return response.json().catch(function () { return null; });
            }).then(function (data) {
                console.log('[embed-chat] speech-to-text response', data);
                micButton.disabled = bridgeState !== 'linked';
                if (!data || !data.success || !data.text) {
                    renderSystemNotice((data && data.message) || "Didn't catch that — try again.");
                    return;
                }
                var existing = composerInput.value || '';
                composerInput.value = existing && !/\s$/.test(existing) ? existing + ' ' + data.text : existing + data.text;
                composerInput.focus();
            }).catch(function (error) {
                console.error('[embed-chat] speech-to-text request failed', error);
                micButton.disabled = bridgeState !== 'linked';
                renderSystemNotice('Voice input failed. Please try again.');
            });
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
                    case 'toolStep':
                        latestToolStepLabel = message.description || message.name;
                        latestToolStepAtMs = Date.now();
                        updateThinkingCopy();
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
                        var completeDurationMs = elapsedSinceThinkingMs();
                        hideThinking();
                        renderAssistantMessage(message.messageHtml, message.message, message.toolsUsed, completeDurationMs);
                        finishTurn();
                        break;
                    case 'error':
                        var errorDurationMs = elapsedSinceThinkingMs();
                        hideThinking();
                        onTurnError(message, text, errorDurationMs);
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
                var failureDurationMs = elapsedSinceThinkingMs();
                hideThinking();
                renderAssistantError('The assistant could not be reached. Please try again.', failureDurationMs);
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
                    pageContext: currentPageContext,
                    autoNavigate: !autoNavigateRow.hidden && autoNavigateCheckbox.checked
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
                        var unexpectedDurationMs = elapsedSinceThinkingMs();
                        hideThinking();
                        renderAssistantError('The assistant returned an unexpected response (' + response.status + ').', unexpectedDurationMs);
                        finishTurn();
                    });
                }
                return readNdjsonStream(response.body, onEvent);
            }).catch(onTransportFailure).finally(function () {
                window.clearTimeout(ceilingHandle);
            });

            function onTurnError(message, originalText, durationMs) {
                if (message.code === 'session_token_rejected' || message.code === 'session_token_required') {
                    pendingMessage = originalText;
                    turnState = 'deferred';
                    enterAwaitingToken();
                    return;
                }

                renderAssistantError(message.message || 'Something went wrong.', durationMs);
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

        // Anchored to showThinking's timestamp (send time) rather than a separate clock, so this
        // is genuinely "how long the round trip took" — network + server processing together, not
        // just the server's own reported duration (which the NDJSON contract doesn't carry anyway).
        function elapsedSinceThinkingMs() {
            return thinkingStartedAtMs !== null ? Date.now() - thinkingStartedAtMs : null;
        }

        function formatDuration(ms) {
            if (typeof ms !== 'number' || ms < 0) {
                return null;
            }
            return ms < 1000 ? Math.round(ms) + 'ms' : (ms / 1000).toFixed(1) + 's';
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

        function renderAssistantMessage(messageHtml, plainText, toolsUsed, durationMs) {
            // messageHtml is rendered server-side by IMarkdownRenderer (Markdig with HTML
            // disabled and a link-scheme allowlist) — the same pipeline already used for AI
            // Compare's responses. Fall back to escaped plain text only if it's ever missing
            // (e.g. an older cached response shape), never render plainText as HTML directly.
            var html = messageHtml || textToHtml(plainText || '');
            var footnoteParts = [];
            var duration = formatDuration(durationMs);
            if (duration) {
                footnoteParts.push(duration);
            }
            if (Array.isArray(toolsUsed) && toolsUsed.length > 0) {
                footnoteParts.push('used: ' + escapeHtml(toolsUsed.join(', ')));
            }
            if (footnoteParts.length > 0) {
                html += '<span class="embed-message-footnote">' + footnoteParts.join(' · ') + '</span>';
            }
            appendBubble('embed-message-assistant', html);
        }

        function renderAssistantError(text, durationMs) {
            var html = textToHtml(text);
            var duration = formatDuration(durationMs);
            if (duration) {
                html += '<span class="embed-message-footnote">' + duration + '</span>';
            }
            appendBubble('embed-message-error', html);
        }

        function renderSystemNotice(text) {
            appendBubble('embed-message-system', textToHtml(text));
        }

        function showThinking() {
            thinkingStartedAtMs = Date.now();
            latestToolStepLabel = null;
            latestToolStepAtMs = null;
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

            if (latestToolStepLabel && latestToolStepAtMs !== null && Date.now() - latestToolStepAtMs < 5000) {
                thinkingElement.innerHTML = escapeHtml(latestToolStepLabel) + '&hellip;';
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

        function updateAutoNavigateVisibility() {
            autoNavigateRow.hidden = currentSurfaces.indexOf('picsearch') === -1;
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
            resetButton.disabled = !enabled;
            micButton.disabled = !enabled;
            if (resumeBannerResetButton) {
                resumeBannerResetButton.disabled = !enabled;
            }
        }

        function showBanner(text) {
            statusBanner.textContent = text;
            statusBanner.hidden = false;
        }

        function hideBanner() {
            statusBanner.hidden = true;
            statusBanner.textContent = '';
        }

        function showResumeBanner(turnCount) {
            if (!resumeBanner || !resumeBannerText) {
                return;
            }
            var suffix = typeof turnCount === 'number' && turnCount > 0
                ? ' (' + turnCount + (turnCount === 1 ? ' message' : ' messages') + ')'
                : '';
            resumeBannerText.textContent = 'Continuing a previous conversation' + suffix + '.';
            resumeBanner.hidden = false;
        }

        function hideResumeBanner() {
            if (resumeBanner) {
                resumeBanner.hidden = true;
            }
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

        resetButton.addEventListener('click', resetConversation);
        if (resumeBannerResetButton) {
            resumeBannerResetButton.addEventListener('click', resetConversation);
        }

        if (micSupported()) {
            micButton.hidden = false;
            micButton.addEventListener('click', toggleRecording);
        }

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
