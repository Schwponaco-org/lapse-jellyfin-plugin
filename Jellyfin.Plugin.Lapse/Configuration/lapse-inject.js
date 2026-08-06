// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

// This runs on every page of the web client (it gets injected into index.html by
// Plugin.cs), so unlike the dashboard script it can't assume the "Dashboard" helper
// object is loaded - that's dashboard-only. Only ApiClient is safe to use everywhere.
// Same reasoning the old intro-skipper inject.js used.

(function () {
    'use strict';

    // action ids the real jellyfin-web item context menu uses, just for debug logging -
    // this used to be a hard gate but different jellyfin-web versions use different ids,
    // so now it's only informational. Log to the console with a "[lapse]" prefix so this
    // whole thing is debuggable from the browser devtools without a build step.
    var KNOWN_CONTEXT_MENU_IDS = [
        'resume', 'playallfromhere', 'queue', 'queuenext', 'shuffle', 'instantmix',
        'multiSelect', 'addtocollection', 'edit', 'identify', 'refresh', 'delete',
        'download', 'moveup', 'movedown', 'open', 'openalbum'
    ];

    // Everything with a video file worth lining subtitles up against. Used to be Movie
    // only; libraries of any type can be turned on in the dashboard now, so the menu has
    // to offer the same thing for episodes and loose videos.
    var SYNCABLE_TYPES = ['Movie', 'Episode', 'Video', 'MusicVideo'];

    var pendingCardContext = null;

    function log(message) {
        console.log('[lapse] ' + message);
    }

    function lapseFetch(path, options) {
        options = options || {};
        var headers = options.headers || {};
        headers.Authorization = 'MediaBrowser Token=' + ApiClient.accessToken();
        if (options.body && !headers['Content-Type']) {
            headers['Content-Type'] = 'application/json';
        }

        options.headers = headers;

        return fetch(ApiClient.getUrl(path), options).then(function (res) {
            if (!res.ok) {
                return res.text().then(function (text) {
                    throw new Error(text || ('Request failed with status ' + res.status));
                });
            }

            var contentType = res.headers.get('content-type') || '';
            if (contentType.indexOf('application/json') !== -1) {
                return res.json();
            }

            return null;
        });
    }

    function lapseGet(path) {
        return lapseFetch(path, { method: 'GET' });
    }

    function lapsePost(path, body) {
        return lapseFetch(path, { method: 'POST', body: JSON.stringify(body || {}) });
    }

    function escapeHtml(text) {
        var div = document.createElement('div');
        div.textContent = text == null ? '' : text;
        return div.innerHTML;
    }

    function ensureStylesheetLoaded() {
        if (document.querySelector('link[data-lapse-inject-css]')) {
            return;
        }

        var link = document.createElement('link');
        link.rel = 'stylesheet';
        link.href = 'configurationpage?name=lapse-inject.css';
        link.setAttribute('data-lapse-inject-css', '1');
        document.head.appendChild(link);
    }

    function showLapseToast(message) {
        var existing = document.querySelector('.lapseToast');
        if (existing) {
            existing.remove();
        }

        var toast = document.createElement('div');
        toast.className = 'lapseToast';
        toast.textContent = message;
        document.body.appendChild(toast);

        setTimeout(function () {
            toast.remove();
        }, 6000);
    }

    // --- figure out which item the open action sheet belongs to ---

    // jellyfin-web has changed how it routes between versions (old hash-based routing
    // like #!/details?id=xxx, newer versions using a real path/query string instead),
    // so try every shape we know about rather than betting on just one.
    function getIdFromLocation() {
        var hash = window.location.hash || '';
        var hashQueryIndex = hash.indexOf('?');
        if (hashQueryIndex !== -1) {
            var hashId = new URLSearchParams(hash.substring(hashQueryIndex + 1)).get('id');
            if (hashId) {
                return hashId;
            }
        }

        var searchId = new URLSearchParams(window.location.search || '').get('id');
        if (searchId) {
            return searchId;
        }

        // some routes put the id straight in the path, e.g. /details/<id>
        var pathMatch = window.location.pathname.match(/details\/([a-f0-9-]{32,36})/i);
        if (pathMatch) {
            return pathMatch[1];
        }

        return null;
    }

    function rememberCardContextFromClick(e) {
        // the "more" button on a card is a paper-icon-button-light with data-action="menu",
        // not a specific class - confirmed by inspecting the real rendered markup, class
        // names alone weren't reliable enough here
        var moreButton = e.target.closest ? e.target.closest('[data-action="menu"]') : null;
        if (!moreButton) {
            return;
        }

        var card = moreButton.closest('.card[data-id]');
        if (!card) {
            log('clicked a [data-action="menu"] button but found no ancestor .card[data-id]');
            return;
        }

        pendingCardContext = {
            id: card.getAttribute('data-id'),
            type: card.getAttribute('data-type'),
            timestamp: Date.now()
        };
        log('remembered card context: id=' + pendingCardContext.id + ' type=' + pendingCardContext.type);
    }

    function resolveItemContext() {
        // grid/list card menus: we already know the id and type from the click itself
        if (pendingCardContext && (Date.now() - pendingCardContext.timestamp) < 1500) {
            log('using remembered card context');
            return Promise.resolve(pendingCardContext);
        }

        // details page menu: no card involved, but the id is somewhere in the url
        var locationId = getIdFromLocation();
        if (!locationId) {
            log('no card context and no id found in the url (' + window.location.href + ')');
            return Promise.resolve(null);
        }

        log('found id ' + locationId + ' in the url, looking up its type');
        return ApiClient.getItem(ApiClient.getCurrentUserId(), locationId).then(function (item) {
            log('item ' + locationId + ' has type ' + item.Type);
            return { id: item.Id, type: item.Type };
        }).catch(function (err) {
            log('could not look up item ' + locationId + ': ' + err);
            return null;
        });
    }

    // --- detect the action sheet and add our buttons to it ---

    // just for the console log, doesn't gate anything anymore - different jellyfin-web
    // versions use different action ids so this isn't reliable enough to block on.
    function looksLikeItemContextMenu(sheet) {
        var buttons = sheet.querySelectorAll('.actionSheetMenuItem[data-id]');
        for (var i = 0; i < buttons.length; i++) {
            if (KNOWN_CONTEXT_MENU_IDS.indexOf(buttons[i].getAttribute('data-id')) !== -1) {
                return true;
            }
        }

        return false;
    }

    function makeMenuButton(dataId, label, onClick) {
        var button = document.createElement('button');
        button.setAttribute('is', 'emby-button');
        button.type = 'button';
        button.className = 'listItem listItem-button actionSheetMenuItem lapseSyncButton';
        button.setAttribute('data-id', dataId);
        button.innerHTML =
            '<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons subtitles" aria-hidden="true"></span>' +
            '<div class="listItemBody actionsheetListItemBody">' +
            '<div class="listItemBodyText actionSheetItemText">' + escapeHtml(label) + '</div>' +
            '</div>';

        // no stopPropagation here on purpose: let the sheet's own click handler close
        // the dialog like normal, it just won't recognize our data-id and will no-op
        button.addEventListener('click', onClick);
        return button;
    }

    function addSyncButtons(sheet, itemId) {
        var scroller = sheet.querySelector('.actionSheetScroller');
        if (!scroller) {
            log('sheet has no .actionSheetScroller, cannot add the buttons (jellyfin-web markup may have changed)');
            return;
        }

        if (scroller.querySelector('.lapseSyncButton')) {
            return;
        }

        scroller.appendChild(makeMenuButton('lapse-sync-subtitles', 'Sync Subtitles', function () {
            openSyncPopup(itemId);
        }));

        scroller.appendChild(makeMenuButton('lapse-sync-all-subtitles', 'Sync All Subtitles to Reference', function () {
            openReferencePopup(itemId);
        }));

        log('added the LAPSE buttons for item ' + itemId);
    }

    function handleActionSheetOpened(sheet) {
        if (sheet.querySelector('.lapseSyncButton')) {
            return;
        }

        log('action sheet opened, looks like an item menu: ' + looksLikeItemContextMenu(sheet));

        resolveItemContext().then(function (context) {
            if (!context) {
                log('could not figure out which item this menu belongs to, not adding the buttons');
                return;
            }

            if (SYNCABLE_TYPES.indexOf(context.type) === -1) {
                log('item is a ' + context.type + ', which LAPSE has nothing to do with, not adding the buttons');
                return;
            }

            // the sheet may already be gone by the time an async lookup resolves
            if (document.body.contains(sheet)) {
                addSyncButtons(sheet, context.id);
            } else {
                log('sheet closed before we could add the buttons');
            }
        });
    }

    function startWatchingForActionSheets() {
        var observer = new MutationObserver(function (mutations) {
            mutations.forEach(function (mutation) {
                mutation.addedNodes.forEach(function (node) {
                    if (node.nodeType !== 1) {
                        return;
                    }

                    var sheet = node.classList && node.classList.contains('actionSheet')
                        ? node
                        : (node.querySelector ? node.querySelector('.actionSheet') : null);

                    if (sheet) {
                        handleActionSheetOpened(sheet);
                    }
                });
            });
        });

        observer.observe(document.body, { childList: true, subtree: true });
        log('watching for action sheets');
    }

    // --- the small "Sync" / "Advanced" popup ---

    function openOverlay(innerHtml) {
        var overlay = document.createElement('div');
        overlay.className = 'lapseOverlay';
        overlay.innerHTML = '<div class="lapseDialogCard">' + innerHtml + '</div>';

        overlay.addEventListener('click', function (e) {
            if (e.target === overlay) {
                overlay.remove();
            }
        });

        document.body.appendChild(overlay);
        return overlay;
    }

    function subtitleOptionsHtml(subtitles) {
        return subtitles.map(function (s) {
            return '<option value="' + escapeHtml(s.Path) + '">' + escapeHtml(s.DisplayName) + '</option>';
        }).join('');
    }

    function openSyncPopup(itemId) {
        var overlay = openOverlay(
            '<h3>Sync Subtitles</h3>' +
            '<div class="lapseDialogButtons">' +
            '<button is="emby-button" type="button" class="raised button-submit" id="lapsePopupSync"><span>Sync</span></button>' +
            '<button is="emby-button" type="button" class="raised" id="lapsePopupAdvanced"><span>Advanced</span></button>' +
            '</div>');

        overlay.querySelector('#lapsePopupSync').addEventListener('click', function () {
            overlay.remove();
            runQuickSync(itemId);
        });

        overlay.querySelector('#lapsePopupAdvanced').addEventListener('click', function () {
            overlay.remove();
            openAdvancedOverlay(itemId);
        });
    }

    function runQuickSync(itemId) {
        showLapseToast('Checking subtitles...');

        lapseGet('Lapse/Items/' + itemId + '/Subtitles').then(function (subtitles) {
            if (subtitles.length === 0) {
                showLapseToast('No external subtitle found for this item.');
                return;
            }

            if (subtitles.length === 1) {
                doSync(itemId, subtitles[0].Path);
                return;
            }

            openSubtitlePickerPopup(itemId, subtitles);
        }).catch(function (err) {
            showLapseToast('Could not check subtitles: ' + err.message);
        });
    }

    function openSubtitlePickerPopup(itemId, subtitles) {
        var overlay = openOverlay(
            '<h3>Pick a subtitle</h3>' +
            '<div class="selectContainer">' +
            '<select is="emby-select" id="lapsePickerSelect" class="emby-select-withcolor emby-select">' +
            subtitleOptionsHtml(subtitles) +
            '</select>' +
            '</div>' +
            '<div class="lapseDialogButtons">' +
            '<button is="emby-button" type="button" class="raised" id="lapsePickerCancel"><span>Cancel</span></button>' +
            '<button is="emby-button" type="button" class="raised button-submit" id="lapsePickerSync"><span>Sync</span></button>' +
            '</div>');

        overlay.querySelector('#lapsePickerCancel').addEventListener('click', function () {
            overlay.remove();
        });

        overlay.querySelector('#lapsePickerSync').addEventListener('click', function () {
            var path = overlay.querySelector('#lapsePickerSelect').value;
            overlay.remove();
            doSync(itemId, path);
        });
    }

    function describeResult(result) {
        var parts = [];

        if (result.Mode === 'Standard' && result.OffsetMs != null) {
            parts.push('offset ' + result.OffsetMs + 'ms');
        } else if (result.Mode === 'Ols' && result.Slope != null) {
            parts.push('slope ' + result.Slope.toFixed(4) + ', intercept ' + result.Intercept.toFixed(2) + 's');
        } else if (result.Mode === 'Split' && result.Penalty != null) {
            parts.push('split, penalty ' + result.Penalty);
        } else if (result.EngineOutput) {
            parts.push(result.EngineOutput);
        }

        if (result.Confidence != null) {
            parts.push('confidence ' + Math.round(result.Confidence * 100) + '%');
        }

        return parts.join(', ') || 'done';
    }

    function doSync(itemId, subtitlePath) {
        showLapseToast('Syncing...');

        // no EngineId here on purpose - the server picks whichever engine is set as the
        // default, so the quick button stays a one press job
        lapsePost('Lapse/Sync', { ItemId: itemId, Mode: 'Standard', SubtitlePath: subtitlePath }).then(function (result) {
            if (!result.Success) {
                showLapseToast('Sync failed: ' + result.Error);
                return;
            }

            showLapseToast('Synced! ' + describeResult(result));
        }).catch(function (err) {
            showLapseToast('Sync failed: ' + err.message);
        });
    }

    // --- "Sync All Subtitles to Reference" ---

    function openReferencePopup(itemId) {
        showLapseToast('Checking subtitles...');

        lapseGet('Lapse/Items/' + itemId + '/Subtitles').then(function (subtitles) {
            if (subtitles.length < 2) {
                showLapseToast('This item needs at least two external subtitles for that.');
                return;
            }

            var overlay = openOverlay(
                '<h3>Sync All Subtitles to Reference</h3>' +
                '<p class="fieldDescription">Pick the subtitle that is already correct. Every other subtitle on this item gets lined up against it.</p>' +
                '<div class="selectContainer">' +
                '<select is="emby-select" id="lapseRefSelect" class="emby-select-withcolor emby-select">' +
                subtitleOptionsHtml(subtitles) +
                '</select>' +
                '</div>' +
                '<div class="lapseDialogButtons">' +
                '<button is="emby-button" type="button" class="raised" id="lapseRefCancel"><span>Cancel</span></button>' +
                '<button is="emby-button" type="button" class="raised button-submit" id="lapseRefSync"><span>Sync</span></button>' +
                '</div>');

            overlay.querySelector('#lapseRefCancel').addEventListener('click', function () {
                overlay.remove();
            });

            overlay.querySelector('#lapseRefSync').addEventListener('click', function () {
                var referencePath = overlay.querySelector('#lapseRefSelect').value;
                overlay.remove();
                doSyncAll(itemId, referencePath, subtitles.length - 1);
            });
        }).catch(function (err) {
            showLapseToast('Could not check subtitles: ' + err.message);
        });
    }

    function doSyncAll(itemId, referencePath, count) {
        showLapseToast('Syncing ' + count + ' subtitle' + (count === 1 ? '' : 's') + ' to the reference...');

        lapsePost('Lapse/SyncAllSubtitles', {
            ItemId: itemId,
            ReferencePath: referencePath,
            Mode: 'Standard'
        }).then(function (result) {
            var failed = result.Results.length - result.SucceededCount;
            showLapseToast(failed === 0
                ? ('Synced all ' + result.SucceededCount + ' subtitles to the reference.')
                : (result.SucceededCount + ' of ' + result.Results.length + ' synced, ' + failed + ' failed. See the LAPSE dashboard for details.'));
        }).catch(function (err) {
            showLapseToast('Sync failed: ' + err.message);
        });
    }

    function openAdvancedOverlay(itemId) {
        // has to be the SPA route (/web/#/configurationpage?...), not the bare
        // configurationpage?name=... resource - loaded on its own like that, the config
        // page has no ApiClient/Dashboard at all, those only exist once jellyfin-web's
        // own app shell has booted, which is what the #/ route inside the iframe gets us
        var iframeSrc = '/web/#/configurationpage?name=LAPSE&itemId=' + encodeURIComponent(itemId) + '&autoAdvanced=1';

        var overlay = document.createElement('div');
        overlay.className = 'lapseIframeOverlay';
        overlay.innerHTML =
            '<div class="lapseIframeCard">' +
            '<button is="paper-icon-button-light" type="button" class="lapseIframeCloseButton" title="Close">' +
            '<span class="material-icons close" aria-hidden="true"></span>' +
            '</button>' +
            '<iframe src="' + iframeSrc + '"></iframe>' +
            '</div>';

        overlay.querySelector('.lapseIframeCloseButton').addEventListener('click', function () {
            overlay.remove();
        });

        document.body.appendChild(overlay);
    }

    // --- go ---

    // This script sits at the end of <head>, so when it first runs the <body> tag hasn't
    // been parsed yet and document.body is still null. Waiting for DOMContentLoaded before
    // touching body is the whole ballgame here - without it the observer setup throws and
    // takes the rest of the script down with it.
    function start() {
        log('inject.js starting up');
        ensureStylesheetLoaded();
        document.addEventListener('click', rememberCardContextFromClick, true);
        startWatchingForActionSheets();
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
