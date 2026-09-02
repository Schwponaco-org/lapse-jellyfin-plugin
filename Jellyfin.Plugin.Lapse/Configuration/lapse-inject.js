// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

// This runs on every page of the web client (the server adds it to index.html as that
// page is served, see ScriptInjectionMiddleware), so unlike the dashboard script it can't
// assume the "Dashboard" helper object is loaded - that's dashboard-only. Only ApiClient
// is safe to use everywhere.

(function () {
    'use strict';

    // Everything with a video file worth lining subtitles up against.
    var SYNCABLE_TYPES = ['Movie', 'Episode', 'Video', 'MusicVideo'];

    // Containers that expand into episodes rather than having subtitles of their own.
    var CONTAINER_TYPES = ['Series', 'Season'];

    var pendingCardContext = null;
    var progressPollHandle = null;

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

    function showLapseToast(message, keepOpen) {
        var existing = document.querySelector('.lapseToast');
        if (existing) {
            existing.remove();
        }

        var toast = document.createElement('div');
        toast.className = 'lapseToast';
        toast.textContent = message;
        document.body.appendChild(toast);

        if (!keepOpen) {
            setTimeout(function () {
                if (toast.parentNode) {
                    toast.remove();
                }
            }, 6000);
        }

        return toast;
    }

    // --- subtitle appearance ---

    // Restyles what the player already renders. Nothing on disk is touched, and turning
    // the setting off puts everything straight back.
    //
    // Only text based tracks go through these elements at all: PGS and VOBSUB are drawn
    // as images and never match, and ASS/SSA carries per-line style tags that the player
    // applies inline, which beats anything set here.
    //
    // Two mechanisms, because one isn't enough on its own. The stylesheet covers the cue
    // elements that already exist and the native ::cue pseudo element, which can only be
    // reached from CSS. The observer then stamps the same values straight onto each
    // subtitle element as the player creates it, because Jellyfin's own subtitle
    // appearance helper writes inline styles onto those elements, and matching it inline
    // with a priority is the only thing that reliably wins.
    var SUBTITLE_SELECTORS = [
        '.videoSubtitles',
        '.videoSubtitlesInner',
        '.videoOsdSubtitles',
        '.subtitleAppearanceContainer',
        '.htmlvideoplayer-subtitles'
    ];

    var appearanceSettings = null;
    var appearanceObserver = null;

    function refreshSubtitleAppearance() {
        // Runs on every page including the login screen, where there is no ApiClient and
        // nobody to have settings for. Touching it there would throw outside any promise
        // chain, so check first and let the next page load try again.
        if (typeof ApiClient === 'undefined' || !ApiClient.accessToken()) {
            return;
        }

        lapseGet('Lapse/Appearance').then(function (appearance) {
            appearanceSettings = appearance;
            applySubtitleAppearance();
        }).catch(function (err) {
            log('could not read the subtitle appearance settings: ' + err.message);
        });
    }

    function applySubtitleAppearance() {
        var style = document.getElementById('lapseSubtitleAppearance');
        var appearance = appearanceSettings;

        if (!appearance || !appearance.Enabled) {
            if (style) {
                style.remove();
            }

            clearStampedSubtitles();
            return;
        }

        if (!style) {
            style = document.createElement('style');
            style.id = 'lapseSubtitleAppearance';
            document.head.appendChild(style);
        }

        style.textContent = buildAppearanceCss(appearance);
        stampAllSubtitleElements();
    }

    function backgroundOf(appearance) {
        return appearance.BackgroundEnabled ? (appearance.BackgroundColor || '#00000080') : 'transparent';
    }

    function buildAppearanceCss(appearance) {
        var fontSize = (appearance.FontSizePx || 48) + 'px';
        var color = appearance.TextColor || '#FFFFFF';
        var background = backgroundOf(appearance);

        return '' +
            SUBTITLE_SELECTORS.join(', ') + ' {' +
            '  font-size: ' + fontSize + ' !important;' +
            '  color: ' + color + ' !important;' +
            '}' +
            '.videoSubtitlesInner, .videoSubtitles .videoSubtitlesInner {' +
            '  background-color: ' + background + ' !important;' +
            '}' +

            // Native track rendering, which no amount of element styling can reach.
            'video::cue {' +
            '  font-size: ' + fontSize + ';' +
            '  color: ' + color + ';' +
            '  background-color: ' + background + ';' +
            '}';
    }

    function stampSubtitleElement(element) {
        var appearance = appearanceSettings;
        if (!appearance || !appearance.Enabled) {
            return;
        }

        element.setAttribute('data-lapse-styled', '1');
        element.style.setProperty('font-size', (appearance.FontSizePx || 48) + 'px', 'important');
        element.style.setProperty('color', appearance.TextColor || '#FFFFFF', 'important');

        // The background belongs on the inner element - putting it on the outer one paints
        // the whole width of the video rather than a box around the words.
        if (element.classList.contains('videoSubtitlesInner')) {
            element.style.setProperty('background-color', backgroundOf(appearance), 'important');
        }
    }

    function stampAllSubtitleElements() {
        document.querySelectorAll(SUBTITLE_SELECTORS.join(', ')).forEach(stampSubtitleElement);
    }

    function clearStampedSubtitles() {
        document.querySelectorAll('[data-lapse-styled]').forEach(function (element) {
            element.style.removeProperty('font-size');
            element.style.removeProperty('color');
            element.style.removeProperty('background-color');
            element.removeAttribute('data-lapse-styled');
        });
    }

    function startWatchingForSubtitles() {
        if (appearanceObserver) {
            return;
        }

        var selector = SUBTITLE_SELECTORS.join(', ');

        appearanceObserver = new MutationObserver(function (mutations) {
            mutations.forEach(function (mutation) {
                mutation.addedNodes.forEach(function (node) {
                    if (node.nodeType !== 1) {
                        return;
                    }

                    if (node.matches && node.matches(selector)) {
                        stampSubtitleElement(node);
                    }

                    if (node.querySelectorAll) {
                        node.querySelectorAll(selector).forEach(stampSubtitleElement);
                    }

                    // A video appearing means playback is starting, which is the moment
                    // the settings need to be current - someone who just changed them in
                    // the dashboard shouldn't have to reload the whole client first.
                    if (node.nodeName === 'VIDEO' || (node.querySelector && node.querySelector('video'))) {
                        refreshSubtitleAppearance();
                    }
                });
            });
        });

        appearanceObserver.observe(document.body, { childList: true, subtree: true });
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

        var card = moreButton.closest('.card[data-id], .listItem[data-id]');
        if (!card) {
            log('clicked a [data-action="menu"] button but found no ancestor with a data-id');
            return;
        }

        pendingCardContext = {
            id: card.getAttribute('data-id'),
            type: card.getAttribute('data-type'),
            name: card.getAttribute('data-name'),
            timestamp: Date.now()
        };
        log('remembered card context: id=' + pendingCardContext.id + ' type=' + pendingCardContext.type);
    }

    function resolveItemContext() {
        // grid/list card menus: we already know the id from the click itself, but the
        // card's data-type is missing often enough that it's worth confirming
        if (pendingCardContext && (Date.now() - pendingCardContext.timestamp) < 1500) {
            if (pendingCardContext.type && pendingCardContext.name) {
                return Promise.resolve(pendingCardContext);
            }

            return lookUpItem(pendingCardContext.id);
        }

        // details page menu: no card involved, but the id is somewhere in the url
        var locationId = getIdFromLocation();
        if (!locationId) {
            log('no card context and no id found in the url (' + window.location.href + ')');
            return Promise.resolve(null);
        }

        return lookUpItem(locationId);
    }

    function lookUpItem(id) {
        // getCurrentUserId and getItem both throw synchronously on a client that is
        // between sessions, so the whole call goes inside the promise rather than only
        // its result.
        return Promise.resolve().then(function () {
            return ApiClient.getItem(ApiClient.getCurrentUserId(), id);
        }).then(function (item) {
            return { id: item.Id, type: item.Type, name: item.Name };
        }).catch(function (err) {
            log('could not look up item ' + id + ': ' + err);
            return null;
        });
    }

    // --- detect the action sheet and add our buttons to it ---

    function makeMenuButton(dataId, label, icon, onClick) {
        var button = document.createElement('button');
        button.setAttribute('is', 'emby-button');
        button.type = 'button';
        button.className = 'listItem listItem-button actionSheetMenuItem lapseSyncButton';
        button.setAttribute('data-id', dataId);
        button.innerHTML =
            '<span class="actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons ' + icon + '" aria-hidden="true"></span>' +
            '<div class="listItemBody actionsheetListItemBody">' +
            '<div class="listItemBodyText actionSheetItemText">' + escapeHtml(label) + '</div>' +
            '</div>';

        // no stopPropagation here on purpose: let the sheet's own click handler close
        // the dialog like normal, it just won't recognize our data-id and will no-op
        button.addEventListener('click', onClick);
        return button;
    }

    function addMenuButtons(sheet, context) {
        // The scroller is where the real entries live. Different jellyfin-web versions
        // have shuffled the wrapper markup around, so fall back to the sheet itself
        // rather than giving up and showing nothing.
        var scroller = sheet.querySelector('.actionSheetScroller') || sheet;

        if (scroller.querySelector('.lapseSyncButton')) {
            return;
        }

        var isContainer = CONTAINER_TYPES.indexOf(context.type) !== -1;

        if (isContainer) {
            scroller.appendChild(makeMenuButton('lapse-sync-all', 'Sync All Subtitles', 'subtitles', function () {
                startSeriesSync(context, null);
            }));

            scroller.appendChild(makeMenuButton('lapse-sync-all-reference', 'Sync All to Reference', 'compare_arrows', function () {
                openSeriesReferencePopup(context);
            }));
        } else {
            scroller.appendChild(makeMenuButton('lapse-sync-subtitles', 'Sync Subtitles', 'subtitles', function () {
                openSyncPopup(context);
            }));

            scroller.appendChild(makeMenuButton('lapse-shift-subtitles', 'Shift Subtitles', 'schedule', function () {
                openShiftPopup(context);
            }));

            scroller.appendChild(makeMenuButton('lapse-convert-subtitles', 'Convert Subtitles', 'swap_horiz', function () {
                openConvertPopup(context);
            }));

            scroller.appendChild(makeMenuButton('lapse-sync-all-subtitles', 'Sync Subtitles to Reference', 'compare_arrows', function () {
                openReferencePopup(context);
            }));

            scroller.appendChild(makeMenuButton('lapse-extract-embedded', 'Extract Embedded Subtitles', 'file_download', function () {
                openExtractPopup(context);
            }));

            scroller.appendChild(makeMenuButton('lapse-readable-subtitles', 'Readable Subtitles', 'accessibility_new', function () {
                openRestylePopup(context);
            }));
        }

        log('added the LAPSE buttons for ' + context.type + ' ' + context.id);
    }

    function handleActionSheetOpened(sheet) {
        if (sheet.querySelector('.lapseSyncButton')) {
            return;
        }

        resolveItemContext().then(function (context) {
            if (!context) {
                log('could not figure out which item this menu belongs to, not adding the buttons');
                return;
            }

            if (SYNCABLE_TYPES.indexOf(context.type) === -1 && CONTAINER_TYPES.indexOf(context.type) === -1) {
                log('item is a ' + context.type + ', which LAPSE has nothing to do with, not adding the buttons');
                return;
            }

            // the sheet may already be gone by the time an async lookup resolves
            if (document.body.contains(sheet)) {
                addMenuButtons(sheet, context);
            } else {
                log('sheet closed before we could add the buttons');
            }
        }).catch(function (err) {
            // A menu that came up without the LAPSE entries is a nuisance; an unhandled
            // rejection out of a MutationObserver is a console full of noise on every
            // menu, in a client that is not ours to break.
            log('could not add the buttons to this menu: ' + err);
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

    // --- dialogs ---

    function openOverlay(innerHtml, wide) {
        var overlay = document.createElement('div');
        overlay.className = 'lapseOverlay';
        overlay.innerHTML = '<div class="lapseDialogCard' + (wide ? ' lapseDialogCard-wide' : '') + '">' + innerHtml + '</div>';

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

    function openSyncPopup(context) {
        var overlay = openOverlay(
            '<h3>Sync Subtitles</h3>' +
            '<div class="lapseDialogButtons">' +
            '<button is="emby-button" type="button" class="raised button-submit" id="lapsePopupSync"><span>Sync</span></button>' +
            '<button is="emby-button" type="button" class="raised" id="lapsePopupAdvanced"><span>Advanced</span></button>' +
            '</div>');

        overlay.querySelector('#lapsePopupSync').addEventListener('click', function () {
            overlay.remove();
            runQuickSync(context.id);
        });

        overlay.querySelector('#lapsePopupAdvanced').addEventListener('click', function () {
            overlay.remove();
            openAdvancedDialog(context);
        });
    }

    function runQuickSync(itemId) {
        showLapseToast('Checking subtitles...');

        lapseGet('Lapse/Items/' + itemId + '/Subtitles').then(function (subtitles) {
            if (subtitles.length === 0) {
                showLapseToast('No subtitle found for this item, in a file or in the video.');
                return;
            }

            // Supported means the engine in use can do something with the file. LAPSE
            // reads PGS and VobSub and rewrites their timing, so those are only turned
            // away when the engine cannot read them.
            var usable = subtitles.filter(function (s) { return s.Supported !== false; });

            if (usable.length === 0) {
                showLapseToast('The only subtitles on this item are picture based (PGS or VobSub), and the engine you are using cannot read those. LAPSE can sync them, or run OCR over them with something like Subtitle Edit first.');
                return;
            }

            // Everything left is in a format the engine does not read, but the plugin can
            // convert it into one that it does. Worth offering rather than refusing.
            var syncable = usable.filter(function (s) { return !s.NeedsConversion; });

            if (syncable.length === 0) {
                offerConversion(itemId, usable);
                return;
            }

            if (syncable.length === 1) {
                doSync(itemId, syncable[0].Path);
                return;
            }

            openSubtitlePickerPopup(itemId, syncable);
        }).catch(function (err) {
            showLapseToast('Could not check subtitles: ' + err.message);
        });
    }

    function uncapitalize(text) {
        return text ? text.charAt(0).toLowerCase() + text.slice(1) : '';
    }

    // Shown when everything on the item is in a format the engines cannot read. Converting
    // it is one press, and the setting decides whether the sync follows on its own.
    function offerConversion(itemId, subtitles) {
        var formats = subtitles.map(function (s) { return '.' + s.Format; })
            .filter(function (f, i, all) { return all.indexOf(f) === i; })
            .join(', ');

        var overlay = openOverlay(
            '<h3>Format not supported by the engines</h3>' +
            '<div class="fieldDescription">This item only has ' + escapeHtml(formats) +
            ', which no sync engine reads. The plugin can convert it first, and then sync the result.</div>' +
            (subtitles.length > 1
                ? '<div class="selectContainer">' +
                  '  <label class="selectLabel">Subtitle</label>' +
                  '  <select is="emby-select" id="lapseConvertPick" class="emby-select-withcolor emby-select">' +
                  subtitleOptionsHtml(subtitles) +
                  '  </select>' +
                  '</div>'
                : '') +
            '<div class="lapseDialogButtons">' +
            '  <button is="emby-button" type="button" class="raised" id="lapseOfferCancel"><span>Cancel</span></button>' +
            '  <button is="emby-button" type="button" class="raised button-submit" id="lapseOfferConvert"><span>Convert</span></button>' +
            '</div>');

        overlay.querySelector('#lapseOfferCancel').addEventListener('click', function () {
            overlay.remove();
        });

        overlay.querySelector('#lapseOfferConvert').addEventListener('click', function () {
            var picker = overlay.querySelector('#lapseConvertPick');
            var path = picker ? picker.value : subtitles[0].Path;

            overlay.remove();
            showLapseToast('Converting...');

            // No TargetFormat and no ReplaceOriginal: both come from the Conversion
            // settings, which is where that decision belongs.
            lapsePost('Lapse/Convert', { ItemId: itemId, SubtitlePath: path }).then(function (result) {
                if (result.SyncedAfter && result.Sync) {
                    // Only the first letter comes down - lowercasing the whole sentence
                    // used to mangle the file path and the engine's name along with it.
                    showLapseToast('Converted to ' + result.Format + ' and ' +
                        uncapitalize(describeSyncOutcome(result.Sync)));
                    return;
                }

                showLapseToast('Converted to ' + result.Format + ': ' + result.OutputPath);
            }).catch(function (err) {
                showLapseToast('Could not convert: ' + err.message);
            });
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

        if (result.OffsetMs != null && result.OffsetMs !== 0) {
            parts.push('offset ' + result.OffsetMs + 'ms');
        }

        if (result.Slope != null) {
            parts.push('stretched by ' + (result.Slope * 100).toFixed(3) + '%');
        }

        if (result.Mode === 'Split' && result.Penalty != null) {
            parts.push('split into ' + result.Penalty + ' parts');
        }

        if (parts.length === 0 && result.EngineOutput) {
            parts.push(result.EngineOutput);
        }

        // LAPSE says what it made of its own answer, which beats a percentage nobody has
        // a feel for. Engines that report nothing fall back to whatever they did give us.
        if (result.Verdict) {
            parts.push(result.Verdict);
        } else if (result.Confidence != null) {
            parts.push('confidence ' + Math.round(result.Confidence * 100) + '%');
        }

        return parts.join(', ') || 'done';
    }

    function describeSyncOutcome(result) {
        if (!result.Success) {
            return 'Sync failed: ' + result.Error;
        }

        if (result.AlreadyInSync) {
            return 'Already in sync, so nothing was changed. The engine would have moved it ' +
                (result.OffsetMs || 0) + 'ms, which is inside the tolerance set under File output.';
        }

        if (result.Skipped) {
            return 'Left the original alone - ' + describeResult(result) +
                ', which is under the confidence threshold. Change what happens then under ' +
                'File output in the LAPSE dashboard, or, if you are sure the subtitle belongs ' +
                'to this video, turn on "Sync even when the engine is unsure" under Engines - Advanced.';
        }

        var converted = result.ConvertedFrom
            ? ' Converted from .' + result.ConvertedFrom + ' first, so the result is ' +
              (result.OutputPath || '').split('.').pop() + '.'
            : '';

        return 'Synced! ' + describeResult(result) + converted;
    }

    function describePipelineOutcome(result) {
        if (result.Error) {
            return 'Stopped: ' + result.Error;
        }

        var parts = [];

        if (result.Extracted) {
            parts.push('Pulled the track out of the video');
        }

        if (result.ConvertedFrom) {
            parts.push('converted it from .' + result.ConvertedFrom);
        }

        if (result.Sync) {
            parts.push(describeSyncOutcome(result.Sync).replace(/^Synced! /, 'synced: '));
        }

        if (result.Translation) {
            parts.push(result.Translation.Success
                ? ('translated ' + result.Translation.TranslatedCount + ' of ' + result.Translation.LineCount + ' lines to ' + result.Translation.OutputPath)
                : ('translation failed: ' + result.Translation.Error));
        }

        return parts.length ? parts.join(', ') + '.' : 'Nothing to do.';
    }

    function doSync(itemId, subtitlePath) {
        showLapseToast('Syncing...');

        // No EngineId and no Mode here on purpose. The server picks whichever engine is set
        // as the default and runs it in that engine's configured default sync mode, so this
        // button stays a one press job and what it does is set in one place.
        lapsePost('Lapse/Sync', { ItemId: itemId, SubtitlePath: subtitlePath }).then(function (result) {
            showLapseToast(describeSyncOutcome(result));
        }).catch(function (err) {
            showLapseToast('Sync failed: ' + err.message);
        });
    }

    // --- "Convert Subtitles" ---

    var CONVERT_FORMATS = ['srt', 'vtt', 'ass', 'ssa'];

    function openConvertPopup(context) {
        showLapseToast('Checking subtitles...');

        lapseGet('Lapse/Items/' + context.id + '/Subtitles').then(function (subtitles) {
            // Converting needs text, which a picture based subtitle has none of, whatever
            // the engine can do with its timings.
            var convertible = subtitles.filter(function (s) { return s.TextBased !== false; });

            if (convertible.length === 0) {
                showLapseToast(subtitles.length === 0
                    ? 'No external subtitle found for this item.'
                    : 'The subtitles on this item are picture based (PGS or VobSub). There is no text in those to convert - they need OCR first.');
                return;
            }

            showConvertDialog(context, convertible);
        }).catch(function (err) {
            showLapseToast('Could not check subtitles: ' + err.message);
        });
    }

    function showConvertDialog(context, subtitles) {
        var overlay = openOverlay(
            '<h3>Convert Subtitles</h3>' +
            '<div class="fieldDescription">Writes a new file in the format you pick. The original is left ' +
            'alone unless you choose to delete it. Use this to turn formats no engine can sync, like ' +
            'MicroDVD .sub, into plain srt first.</div>' +
            '<div class="selectContainer">' +
            '  <label class="selectLabel">Subtitle</label>' +
            '  <select is="emby-select" id="lapseConvertSubtitle" class="emby-select-withcolor emby-select">' +
            subtitleOptionsHtml(subtitles) +
            '  </select>' +
            '</div>' +
            '<div class="selectContainer">' +
            '  <label class="selectLabel">Convert to</label>' +
            '  <select is="emby-select" id="lapseConvertFormat" class="emby-select-withcolor emby-select">' +
            CONVERT_FORMATS.map(function (f) {
                return '<option value="' + f + '">.' + f + '</option>';
            }).join('') +
            '  </select>' +
            '</div>' +
            '<label class="emby-checkbox-label lapseStackedCheck">' +
            '  <input type="checkbox" is="emby-checkbox" id="lapseConvertReplace" />' +
            '  <span>Delete the original once the new file is written</span>' +
            '</label>' +
            '<div class="lapseDialogButtons">' +
            '  <button is="emby-button" type="button" class="raised" id="lapseConvertCancel"><span>Cancel</span></button>' +
            '  <button is="emby-button" type="button" class="raised button-submit" id="lapseConvertApply"><span>Convert</span></button>' +
            '</div>');

        var select = overlay.querySelector('#lapseConvertSubtitle');
        var formatSelect = overlay.querySelector('#lapseConvertFormat');

        function currentFormat() {
            var match = /\.([a-z0-9]+)$/i.exec(select.value || '');
            return match ? match[1].toLowerCase() : '';
        }

        // srt is what people convert to almost every time, so it is the standing default
        // and stays selected unless it is deliberately changed. This used to pick whatever
        // the file was not, which meant the common case - an srt that wants tidying up -
        // opened on .vtt.
        formatSelect.value = 'srt';

        overlay.querySelector('#lapseConvertCancel').addEventListener('click', function () {
            overlay.remove();
        });

        overlay.querySelector('#lapseConvertApply').addEventListener('click', function () {
            var target = formatSelect.value;

            if (target === currentFormat()) {
                showLapseToast('That subtitle is already ' + target + '.');
                return;
            }

            var replace = overlay.querySelector('#lapseConvertReplace').checked;

            // Read everything off the dialog before it goes, rather than reaching into
            // detached nodes from the callback.
            var subtitlePath = select.value;

            overlay.remove();
            showLapseToast('Converting to ' + target + '...');

            lapsePost('Lapse/Convert', {
                ItemId: context.id,
                SubtitlePath: subtitlePath,
                TargetFormat: target,
                ReplaceOriginal: replace
            }).then(function (result) {
                showLapseToast(describeConvertOutcome(result));
            }).catch(function (err) {
                showLapseToast('Could not convert: ' + err.message);
            });
        });
    }

    // Conversion is set to carry on into a sync by default, so a toast that only mentions
    // the file that was written leaves out the half of the job people actually care about
    // - including the case where the sync then refused to touch anything.
    function describeConvertOutcome(result) {
        var written = 'Wrote ' + result.Cues + ' cues to ' + result.OutputPath;

        if (result.RemovedOriginal) {
            written += ', and deleted the original';
        }

        if (!result.SyncedAfter || !result.Sync) {
            return written + '.';
        }

        return written + '. ' + describeSyncOutcome(result.Sync);
    }

    // --- "Readable Subtitles" ---
    //
    // Writes a copy of the subtitle with the font, size and letter spacing set in the file
    // itself. Jellyfin's own subtitle appearance settings are per client and per device,
    // and most of its clients have no font picker at all, so this is the only way to set a
    // dyslexia-friendly font once and have the TV, the phone and the browser all honour it.

    function openRestylePopup(context) {
        showLapseToast('Checking subtitles...');

        Promise.all([
            lapseGet('Lapse/Items/' + context.id + '/Subtitles'),
            lapseGet('Lapse/Fonts').catch(function () { return null; })
        ]).then(function (results) {
            var subtitles = results[0].filter(function (s) { return s.TextBased !== false; });

            if (subtitles.length === 0) {
                showLapseToast('No subtitle on this item has text in it to restyle.');
                return;
            }

            showRestyleDialog(context, subtitles, results[1]);
        }).catch(function (err) {
            showLapseToast('Could not check subtitles: ' + err.message);
        });
    }

    function showRestyleDialog(context, subtitles, fonts) {
        var ready = fonts && fonts.DyslexicInstalled && fonts.FallbackFontEnabled;

        var fontNote = ready
            ? '<div class="fieldDescription">OpenDyslexic is installed on this server, so the styled subtitle will render in it.</div>'
            : '<div class="fieldDescription"><strong>The font isn\'t installed yet.</strong> The styled file will still be ' +
              'written, and the larger text and wider letter spacing will apply, but it will render in the player\'s ' +
              'normal font until an admin installs OpenDyslexic from the LAPSE dashboard under Subtitle appearance.</div>';

        var overlay = openOverlay(
            '<h3>Readable Subtitles</h3>' +
            '<div class="fieldDescription">Writes a copy of the subtitle with a dyslexia-friendly font, larger text ' +
            'and wider letter spacing set inside the file. Because the styling is in the file rather than in a client ' +
            'setting, every client that plays it honours it - phone, TV and browser alike, with nothing to set up on each one.</div>' +
            fontNote +
            '<div class="selectContainer">' +
            '  <label class="selectLabel">Subtitle</label>' +
            '  <select is="emby-select" id="lapseRestyleSubtitle" class="emby-select-withcolor emby-select">' +
            subtitleOptionsHtml(subtitles) +
            '  </select>' +
            '</div>' +
            '<label class="emby-checkbox-label lapseStackedCheck">' +
            '  <input type="checkbox" is="emby-checkbox" id="lapseRestyleReplace" />' +
            '  <span>Delete the original once the styled copy is written</span>' +
            '</label>' +
            '<div class="fieldDescription">Leave this off and both are offered as separate tracks in the player, so the ' +
            'styled one can be picked by whoever wants it and everyone else keeps the original.</div>' +
            '<div class="lapseDialogButtons">' +
            '  <button is="emby-button" type="button" class="raised" id="lapseRestyleCancel"><span>Cancel</span></button>' +
            '  <button is="emby-button" type="button" class="raised button-submit" id="lapseRestyleApply"><span>Write it</span></button>' +
            '</div>');

        var select = overlay.querySelector('#lapseRestyleSubtitle');

        overlay.querySelector('#lapseRestyleCancel').addEventListener('click', function () {
            overlay.remove();
        });

        overlay.querySelector('#lapseRestyleApply').addEventListener('click', function () {
            var subtitlePath = select.value;
            var replace = overlay.querySelector('#lapseRestyleReplace').checked;

            overlay.remove();
            showLapseToast('Writing a readable copy...');

            lapsePost('Lapse/Restyle', {
                ItemId: context.id,
                SubtitlePath: subtitlePath,
                ReplaceOriginal: replace
            }).then(function (result) {
                var message = 'Wrote ' + result.Cues + ' cues to ' + result.OutputPath;

                if (result.RemovedOriginal) {
                    message += ', and deleted the original';
                }

                if (!result.FontAvailable) {
                    message += '. ' + result.FontName + ' is not installed on this server yet, so it will render in the ' +
                        'player\'s normal font until it is';
                }

                showLapseToast(message + '. Run a library scan to pick the new file up.');
            }).catch(function (err) {
                showLapseToast('Could not restyle: ' + err.message);
            });
        });
    }

    // --- "Extract Embedded Subtitles" ---
    //
    // Pulls the tracks inside the video out as files beside it. Removing them from the
    // video afterwards is the part people want for direct play, and it is the part that
    // rewrites a library file, so it is two deliberate checkboxes rather than one.

    function openExtractPopup(context) {
        showLapseToast('Checking subtitles...');

        lapseGet('Lapse/Items/' + context.id + '/Subtitles').then(function (subtitles) {
            var embedded = subtitles.filter(function (s) { return s.IsEmbedded; });

            if (embedded.length === 0) {
                showLapseToast('There are no subtitle tracks inside this video file.');
                return;
            }

            showExtractDialog(context, embedded);
        }).catch(function (err) {
            showLapseToast('Could not check subtitles: ' + err.message);
        });
    }

    function showExtractDialog(context, embedded) {
        var textual = embedded.filter(function (s) { return s.TextBased !== false; });
        var pictures = embedded.length - textual.length;

        var trackList = embedded.map(function (s) {
            return '<li>' + escapeHtml(s.DisplayName) +
                (s.TextBased === false ? ' <em>&mdash; picture based, stays in the video</em>' : '') +
                '</li>';
        }).join('');

        var overlay = openOverlay(
            '<h3>Extract Embedded Subtitles</h3>' +
            '<div class="fieldDescription">Writes every text subtitle track inside this video out as a file ' +
            'beside it. A subtitle as a file plays back on every client without the video having to be ' +
            'transcoded, which a track inside the video often does need.</div>' +
            '<ul class="lapseTrackList">' + trackList + '</ul>' +
            (pictures > 0
                ? '<div class="fieldDescription">' + pictures + ' picture based track' + (pictures === 1 ? '' : 's') +
                  ' (PGS or VobSub) cannot be turned into text and will be left in the video whatever you pick here.</div>'
                : '') +
            (textual.length === 0
                ? '<div class="fieldDescription"><strong>None of these tracks can be extracted.</strong> They need OCR first, with something like Subtitle Edit.</div>'
                : '<label class="emby-checkbox-label lapseStackedCheck">' +
                  '  <input type="checkbox" is="emby-checkbox" id="lapseExtractRemove" />' +
                  '  <span>Also remove those tracks from the video file</span>' +
                  '</label>' +
                  '<div class="fieldDescription">Rebuilds the video without them. Nothing is re-encoded, so the ' +
                  'picture and sound come out identical, but it does write a fresh copy of the whole file.</div>' +
                  '<label class="emby-checkbox-label lapseStackedCheck hide" id="lapseExtractReplaceRow">' +
                  '  <input type="checkbox" is="emby-checkbox" id="lapseExtractReplace" />' +
                  '  <span>Replace the original video file</span>' +
                  '</label>' +
                  '<div class="fieldDescription hide" id="lapseExtractReplaceNote">Leave this off and the rebuilt video ' +
                  'is written beside the original as a .nosubs file, so you can check it before deleting anything. ' +
                  'Turn it on and the original is replaced once the rebuild has finished cleanly.</div>') +
            '<div class="lapseDialogButtons">' +
            '  <button is="emby-button" type="button" class="raised" id="lapseExtractCancel"><span>Cancel</span></button>' +
            '  <button is="emby-button" type="button" class="raised button-submit" id="lapseExtractApply"' +
            (textual.length === 0 ? ' disabled' : '') + '><span>Extract</span></button>' +
            '</div>');

        var removeCheck = overlay.querySelector('#lapseExtractRemove');
        var replaceRow = overlay.querySelector('#lapseExtractReplaceRow');
        var replaceNote = overlay.querySelector('#lapseExtractReplaceNote');
        var replaceCheck = overlay.querySelector('#lapseExtractReplace');

        // Replacing the original only means anything once removal is on, and showing it
        // before then invites someone to tick it without reading what it replaces.
        if (removeCheck) {
            removeCheck.addEventListener('change', function () {
                var removing = removeCheck.checked;
                replaceRow.classList.toggle('hide', !removing);
                replaceNote.classList.toggle('hide', !removing);

                if (!removing) {
                    replaceCheck.checked = false;
                }
            });
        }

        overlay.querySelector('#lapseExtractCancel').addEventListener('click', function () {
            overlay.remove();
        });

        overlay.querySelector('#lapseExtractApply').addEventListener('click', function () {
            var remove = !!(removeCheck && removeCheck.checked);
            var replace = !!(replaceCheck && replaceCheck.checked);

            if (replace && !window.confirm(
                'This replaces the original video file with a rebuilt copy that has no subtitle tracks in it. ' +
                'The subtitles are written out as files first, and nothing is re-encoded. Carry on?')) {
                return;
            }

            overlay.remove();
            showLapseToast(remove
                ? 'Extracting subtitles and rebuilding the video. This can take a while on a large file...'
                : 'Extracting subtitles...');

            lapsePost('Lapse/ExtractEmbedded', {
                ItemId: context.id,
                RemoveFromVideo: remove,
                ReplaceOriginal: replace
            }).then(function (result) {
                showLapseToast(describeExtractOutcome(result));
            }).catch(function (err) {
                showLapseToast('Could not extract: ' + err.message);
            });
        });
    }

    function describeExtractOutcome(result) {
        if (!result.Success) {
            return result.Error || 'Nothing was extracted.';
        }

        var count = (result.ExtractedPaths || []).length;
        var message = 'Extracted ' + count + ' subtitle' + (count === 1 ? '' : 's') + ' to files';

        if (result.RemovedCount > 0) {
            message += result.ReplacedOriginal
                ? ' and rebuilt the video without ' + (result.RemovedCount === 1 ? 'that track' : 'those tracks')
                : ' and wrote ' + result.VideoPath + ' without ' + (result.RemovedCount === 1 ? 'that track' : 'those tracks');
        }

        if ((result.KeptTracks || []).length > 0) {
            message += '. ' + result.KeptTracks.length + ' picture based track' +
                (result.KeptTracks.length === 1 ? ' was' : 's were') + ' left in the video';
        }

        return message + '. Run a library scan to pick the new files up.';
    }

    // --- "Shift Subtitles" ---

    function openShiftPopup(context) {
        showLapseToast('Checking subtitles...');

        lapseGet('Lapse/Items/' + context.id + '/Subtitles').then(function (subtitles) {
            var shiftable = subtitles.filter(function (s) {
                return /\.(srt|vtt|ass|ssa)$/i.test(s.Path);
            });

            if (shiftable.length === 0) {
                showLapseToast(subtitles.length === 0
                    ? 'No external subtitle found for this item.'
                    : 'Shifting works on .srt, .vtt, .ass and .ssa files, and this item has none of those. Convert it first.');
                return;
            }

            showShiftDialog(context, shiftable);
        }).catch(function (err) {
            showLapseToast('Could not check subtitles: ' + err.message);
        });
    }

    function showShiftDialog(context, subtitles) {
        var overlay = openOverlay(
            '<h3>Shift Subtitles</h3>' +
            '<div class="selectContainer">' +
            '  <label class="selectLabel">Subtitle</label>' +
            '  <select is="emby-select" id="lapseShiftSubtitle" class="emby-select-withcolor emby-select">' +
            subtitleOptionsHtml(subtitles) +
            '  </select>' +
            '</div>' +
            '<div class="lapsePreviewBox" id="lapseShiftPreview">Loading an example line...</div>' +
            // Slider first, then the number. Dragging is how you find roughly the right
            // offset; the box is for typing the exact one once you have. The two are the
            // same value, so whichever you touch moves the other.
            '<div class="lapseShiftControls">' +
            '  <input type="range" id="lapseShiftSlider" class="lapseShiftSlider" min="-10000" max="10000" step="50" value="0" />' +
            '  <div class="lapseShiftScale">' +
            '    <span>-10s</span><span>0</span><span>+10s</span>' +
            '  </div>' +
            '  <div class="lapseStepperRow">' +
            '    <button is="emby-button" type="button" class="raised lapseStepButton" id="lapseShiftMinus"><span>&minus;</span></button>' +
            '    <input is="emby-input" id="lapseShiftOffset" class="lapseShiftNumber" type="number" step="100" value="0" />' +
            '    <span class="lapseShiftUnit">ms</span>' +
            '    <button is="emby-button" type="button" class="raised lapseStepButton" id="lapseShiftPlus"><span>+</span></button>' +
            '  </div>' +
            '  <div class="fieldDescription">Minus makes subtitles appear earlier, plus later. The buttons move in 100ms steps. ' +
            'The slider covers 10 seconds either way; type a bigger number if you need one.</div>' +
            '</div>' +
            '<div class="lapseDialogButtons">' +
            '  <button is="emby-button" type="button" class="raised" id="lapseShiftCancel"><span>Cancel</span></button>' +
            '  <button is="emby-button" type="button" class="raised button-submit" id="lapseShiftApply"><span>Apply</span></button>' +
            '</div>',
            // Wide, because a timing line is 29 monospace characters and at the size this
            // now renders it would otherwise sit in a scrollbox, which defeats the point
            // of being able to read it at a glance.
            true);

        var select = overlay.querySelector('#lapseShiftSubtitle');
        var offsetInput = overlay.querySelector('#lapseShiftOffset');
        var slider = overlay.querySelector('#lapseShiftSlider');
        var previewBox = overlay.querySelector('#lapseShiftPreview');
        var currentCue = null;

        function currentOffset() {
            return parseInt(offsetInput.value, 10) || 0;
        }

        // Typing a value outside the slider's range is allowed, so the slider parks at
        // whichever end it can reach rather than dragging the typed number back into range.
        function syncSliderFromInput() {
            var offset = currentOffset();
            slider.value = Math.max(-10000, Math.min(10000, offset));
        }

        function renderPreview() {
            if (!currentCue) {
                previewBox.textContent = 'No example line available for this subtitle.';
                return;
            }

            var shifted = shiftTimingLine(currentCue.TimingLine, currentOffset());

            previewBox.innerHTML =
                (currentCue.Text ? '<div class="lapsePreviewText">' + escapeHtml(currentCue.Text) + '</div>' : '') +
                '<div class="lapsePreviewRow"><span class="lapsePreviewLabel">now</span>' +
                '<span>' + escapeHtml(currentCue.TimingLine) + '</span></div>' +
                '<div class="lapsePreviewRow"><span class="lapsePreviewLabel">after</span>' +
                '<span class="lapsePreviewAfter">' + escapeHtml(shifted) + '</span></div>';
        }

        function loadCue() {
            previewBox.textContent = 'Loading an example line...';
            currentCue = null;

            lapseGet('Lapse/Items/' + context.id + '/Subtitles/FirstCue?path=' + encodeURIComponent(select.value))
                .then(function (cue) {
                    currentCue = cue;
                    renderPreview();
                })
                .catch(function () {
                    previewBox.textContent = 'No example line available for this subtitle.';
                });
        }

        function step(delta) {
            offsetInput.value = currentOffset() + delta;
            syncSliderFromInput();
            renderPreview();
        }

        select.addEventListener('change', loadCue);
        offsetInput.addEventListener('input', function () {
            syncSliderFromInput();
            renderPreview();
        });
        slider.addEventListener('input', function () {
            offsetInput.value = slider.value;
            renderPreview();
        });
        overlay.querySelector('#lapseShiftMinus').addEventListener('click', function () { step(-100); });
        overlay.querySelector('#lapseShiftPlus').addEventListener('click', function () { step(100); });

        overlay.querySelector('#lapseShiftCancel').addEventListener('click', function () {
            overlay.remove();
        });

        overlay.querySelector('#lapseShiftApply').addEventListener('click', function () {
            var offset = currentOffset();
            if (!offset) {
                showLapseToast('Set an offset first - zero would not change anything.');
                return;
            }

            overlay.remove();
            showLapseToast('Shifting by ' + offset + 'ms...');

            // no OutputMode here: the server falls back to the configured one, which is
            // the whole point of "output follows the configured mode"
            lapsePost('Lapse/Shift', {
                ItemId: context.id,
                SubtitlePath: select.value,
                OffsetMs: offset
            }).then(function (result) {
                showLapseToast('Moved ' + result.Shifted + ' timestamps by ' + offset + 'ms. Wrote ' + result.OutputPath);
            }).catch(function (err) {
                showLapseToast('Could not shift the subtitle: ' + err.message);
            });
        });

        loadCue();
    }

    // Same arithmetic the server does, so the preview matches what Apply will produce
    // without a round trip on every keystroke.
    function shiftTimingLine(line, offsetMs) {
        return line.replace(/(\d{1,3}):(\d{2}):(\d{2})([,.])(\d{1,3})/g, function (all, h, m, s, sep, frac) {
            var total = (parseInt(h, 10) * 3600000) + (parseInt(m, 10) * 60000) +
                (parseInt(s, 10) * 1000) + parseInt(frac.padEnd(3, '0'), 10) + offsetMs;

            if (total < 0) {
                total = 0;
            }

            // Written back with the same widths it came in with. ass and ssa count in
            // centiseconds behind a single digit hour, and pushing srt's milliseconds
            // into one of those would make a file no player will read.
            var ms = total % 1000;
            var fraction = frac.length === 2 ? Math.floor(ms / 10)
                : frac.length === 1 ? Math.floor(ms / 100)
                : ms;

            return pad(Math.floor(total / 3600000), h.length) + ':' +
                pad(Math.floor(total / 60000) % 60, 2) + ':' +
                pad(Math.floor(total / 1000) % 60, 2) + sep +
                pad(fraction, frac.length);
        });
    }

    function pad(value, width) {
        return String(value).padStart(width, '0');
    }

    // --- "Sync Subtitles to Reference" on a single item ---

    function openReferencePopup(context) {
        showLapseToast('Checking subtitles...');

        lapseGet('Lapse/Items/' + context.id + '/Subtitles').then(function (subtitles) {
            if (subtitles.length < 2) {
                showLapseToast('This item needs at least two external subtitles for that.');
                return;
            }

            var overlay = openOverlay(
                '<h3>Sync Subtitles to Reference</h3>' +
                '<p class="fieldDescription">Pick the subtitle that is already correct, then say what to line up against it.</p>' +
                '<div class="selectContainer">' +
                '<label class="selectLabel">Reference (the correct one)</label>' +
                '<select is="emby-select" id="lapseRefSelect" class="emby-select-withcolor emby-select">' +
                subtitleOptionsHtml(subtitles) +
                '</select>' +
                '</div>' +
                '<div class="selectContainer">' +
                '<label class="selectLabel">Sync</label>' +
                '<select is="emby-select" id="lapseRefTarget" class="emby-select-withcolor emby-select">' +
                '<option value="">Every other subtitle</option>' +
                subtitleOptionsHtml(subtitles) +
                '</select>' +
                '</div>' +
                '<div class="lapseDialogButtons">' +
                '<button is="emby-button" type="button" class="raised" id="lapseRefCancel"><span>Cancel</span></button>' +
                '<button is="emby-button" type="button" class="raised button-submit" id="lapseRefSync"><span>Sync</span></button>' +
                '</div>');

            var referenceSelect = overlay.querySelector('#lapseRefSelect');
            var targetSelect = overlay.querySelector('#lapseRefTarget');

            overlay.querySelector('#lapseRefCancel').addEventListener('click', function () {
                overlay.remove();
            });

            overlay.querySelector('#lapseRefSync').addEventListener('click', function () {
                var referencePath = referenceSelect.value;
                var targetPath = targetSelect.value;

                if (targetPath && targetPath === referencePath) {
                    showLapseToast('That is the reference itself. Pick another subtitle, or sync every other one.');
                    return;
                }

                overlay.remove();
                doSyncAll(context.id, referencePath, targetPath ? 1 : subtitles.length - 1,
                    targetPath ? [targetPath] : null);
            });
        }).catch(function (err) {
            showLapseToast('Could not check subtitles: ' + err.message);
        });
    }

    // subtitlePaths names the tracks to sync, or null for every track except the
    // reference.
    function doSyncAll(itemId, referencePath, count, subtitlePaths) {
        showLapseToast('Syncing ' + count + ' subtitle' + (count === 1 ? '' : 's') + ' to the reference...');

        lapsePost('Lapse/SyncAllSubtitles', {
            ItemId: itemId,
            ReferencePath: referencePath,
            SubtitlePaths: subtitlePaths || null
        }).then(function (result) {
            var failed = result.Results.length - result.SucceededCount;
            showLapseToast(failed === 0
                ? ('Synced ' + result.SucceededCount + ' subtitle' + (result.SucceededCount === 1 ? '' : 's') + ' to the reference.')
                : (result.SucceededCount + ' of ' + result.Results.length + ' synced, ' + failed + ' failed. See the LAPSE dashboard for details.'));
        }).catch(function (err) {
            showLapseToast('Sync failed: ' + err.message);
        });
    }

    // --- series and season sync ---

    function openSeriesReferencePopup(context) {
        showLapseToast('Looking at the subtitles across these episodes...');

        lapseGet('Lapse/Series/' + context.id + '/ReferenceOptions').then(function (options) {
            if (!options || options.length === 0) {
                showLapseToast('No subtitle track here is named consistently enough to use as a reference across episodes.');
                return;
            }

            var optionsHtml = options.map(function (o) {
                return '<option value="' + escapeHtml(o.Key) + '">' + escapeHtml(o.Key) +
                    ' (on ' + o.EpisodeCount + ' of ' + o.TotalEpisodes + ' episodes)</option>';
            }).join('');

            var overlay = openOverlay(
                '<h3>Sync All to Reference</h3>' +
                '<p class="fieldDescription">Pick the subtitle track that is already correct. On every episode, the other subtitles get lined up against that episode\'s copy of it.</p>' +
                '<div class="selectContainer">' +
                '<select is="emby-select" id="lapseSeriesRefSelect" class="emby-select-withcolor emby-select">' +
                optionsHtml +
                '</select>' +
                '</div>' +
                '<div class="lapseDialogButtons">' +
                '<button is="emby-button" type="button" class="raised" id="lapseSeriesRefCancel"><span>Cancel</span></button>' +
                '<button is="emby-button" type="button" class="raised button-submit" id="lapseSeriesRefSync"><span>Sync</span></button>' +
                '</div>');

            overlay.querySelector('#lapseSeriesRefCancel').addEventListener('click', function () {
                overlay.remove();
            });

            overlay.querySelector('#lapseSeriesRefSync').addEventListener('click', function () {
                var key = overlay.querySelector('#lapseSeriesRefSelect').value;
                overlay.remove();
                startSeriesSync(context, key);
            });
        }).catch(function (err) {
            showLapseToast('Could not read the subtitle tracks: ' + err.message);
        });
    }

    function startSeriesSync(context, referenceKey) {
        showLapseToast('Queuing episodes...');

        lapsePost('Lapse/Series/Sync', {
            ItemId: context.id,
            ReferenceKey: referenceKey
        }).then(function () {
            // Progress goes in the notification toast rather than a modal, so the page
            // stays usable while a whole show works through in the background.
            startProgressPolling();
        }).catch(function (err) {
            showLapseToast('Could not start the sync: ' + err.message);
        });
    }

    function startProgressPolling() {
        if (progressPollHandle) {
            return;
        }

        var toast = showLapseToast('', true);

        // The toast carries the Stop button, so a job that turns out to be the wrong one
        // can be called off from wherever it was started rather than only from the
        // dashboard.
        toast.innerHTML = '<span class="lapseToastText">Starting...</span>' +
            '<button is="emby-button" type="button" class="raised lapseToastButton" id="lapseProgressStop">' +
            '<span>Stop</span></button>';

        var text = toast.querySelector('.lapseToastText');
        var stopButton = toast.querySelector('#lapseProgressStop');

        stopButton.addEventListener('click', function () {
            stopButton.disabled = true;
            text.textContent = 'Stopping...';

            lapsePost('Lapse/Queue/Cancel').catch(function (err) {
                stopButton.disabled = false;
                text.textContent = 'Could not stop the job: ' + err.message;
            });
        });

        function poll() {
            lapseGet('Lapse/Queue').then(function (snapshot) {
                var unit = snapshot.UnitName || 'item';
                var plural = snapshot.Total === 1 ? unit : unit + 's';

                if (snapshot.Running) {
                    text.textContent = snapshot.Cancelling
                        ? 'Stopping after the item that is running now...'
                        : ((snapshot.JobName ? snapshot.JobName + ': ' : '') +
                            snapshot.Completed + ' / ' + snapshot.Total + ' ' + plural + ' processed' +
                            (snapshot.CurrentItemName ? ' - ' + snapshot.CurrentItemName : ''));

                    stopButton.disabled = !!snapshot.Cancelling;
                    return;
                }

                stopProgressPolling();
                stopButton.remove();
                text.textContent = (snapshot.JobName ? snapshot.JobName + ': ' : '') +
                    'finished, ' + snapshot.Completed + ' / ' + snapshot.Total + ' ' + plural + ' processed.';

                setTimeout(function () {
                    if (toast.parentNode) {
                        toast.remove();
                    }
                }, 8000);
            }).catch(function (err) {
                stopProgressPolling();
                text.textContent = 'Lost track of the sync job: ' + err.message;
            });
        }

        progressPollHandle = setInterval(poll, 2000);
        poll();
    }

    function stopProgressPolling() {
        if (progressPollHandle) {
            clearInterval(progressPollHandle);
            progressPollHandle = null;
        }
    }

    // --- the Advanced dialog ---

    // This used to load the whole plugin dashboard in an iframe, which navigated the
    // page out from under whoever pressed it. It's a normal dialog now: everything it
    // offers is an API call the injected script can make directly, so there was never a
    // reason to drag the dashboard along.
    function openAdvancedDialog(context) {
        showLapseToast('Loading...');

        Promise.all([
            lapseGet('Lapse/Items/' + context.id + '/Subtitles'),
            lapseGet('Lapse/Engines'),
            lapseGet('Lapse/Translate/Providers'),
            lapseGet('Lapse/Translate/Defaults')
        ]).then(function (results) {
            var toast = document.querySelector('.lapseToast');
            if (toast) {
                toast.remove();
            }

            showAdvancedDialog(context, results[0], results[1], results[2], results[3] || {});
        }).catch(function (err) {
            showLapseToast('Could not open the advanced options: ' + err.message);
        });
    }

    // The dialog reads top to bottom as one run: which subtitle, what to line it up
    // against, how to write the result, and whether to translate what comes out. Every
    // button that starts something is in the row at the bottom, so no action is hiding
    // underneath a setting.
    function showAdvancedDialog(context, subtitles, engines, providers, defaults) {
        var usableEngines = engines.filter(function (e) { return e.Installed && !e.RunCheckError; });
        if (usableEngines.length === 0) {
            showLapseToast('No engine is installed and working. Install one from the LAPSE dashboard first.');
            return;
        }

        var configuredProviders = (providers || []).filter(function (p) { return p.Configured; });
        var canTranslate = subtitles.length > 0 && configuredProviders.length > 0;
        var startEngine = usableEngines.filter(function (e) { return e.IsDefault; })[0] || usableEngines[0];
        var threshold = typeof defaults.ConfidenceThreshold === 'number' ? defaults.ConfidenceThreshold : 70;

        var engineOptions = usableEngines.map(function (e) {
            return '<option value="' + escapeHtml(e.Id) + '"' + (e.Id === startEngine.Id ? ' selected' : '') + '>' +
                escapeHtml(e.DisplayName) + '</option>';
        }).join('');

        var subtitleSection = subtitles.length === 0
            ? '<p class="fieldDescription">No subtitle found for this item.</p>'
            : '<div class="selectContainer">' +
              '  <label class="selectLabel">Subtitle</label>' +
              '  <select is="emby-select" id="lapseAdvSubtitle" class="emby-select-withcolor emby-select">' +
              subtitleOptionsHtml(subtitles) +
              '  </select>' +
              '  <div class="fieldDescription">Tracks still inside the video file are pulled out first, so an embedded one can be picked here too.</div>' +
              '</div>';

        // Syncing against another subtitle used to be a second button further down the
        // dialog. It's the same run with a different thing to line up against, so it's a
        // dropdown here and the Sync button does both.
        var referenceSection = subtitles.length > 1
            ? '<div class="selectContainer">' +
              '  <label class="selectLabel">Line it up against</label>' +
              '  <select is="emby-select" id="lapseAdvReference" class="emby-select-withcolor emby-select">' +
              '    <option value="">The audio in the video</option>' +
              subtitleOptionsHtml(subtitles) +
              '  </select>' +
              '  <div class="fieldDescription">Another subtitle skips the audio entirely. Faster and usually more accurate, as long as the one you pick is correct.</div>' +
              '</div>' +
              '<label class="emby-checkbox-label lapseStackedCheck hide" id="lapseAdvSyncAllRow">' +
              '  <input type="checkbox" is="emby-checkbox" id="lapseAdvSyncAll" />' +
              '  <span>Sync every other subtitle to it, not just the one above</span>' +
              '</label>'
            : '';

        var translationSection = canTranslate
            ? '<hr class="lapseDialogRule" />' +
              '<h4 class="lapseDialogSubhead">Translate</h4>' +
              '<label class="emby-checkbox-label lapseStackedCheck">' +
              '  <input type="checkbox" is="emby-checkbox" id="lapseAdvAlsoTranslate" />' +
              '  <span>Translate as well when Sync runs</span>' +
              '</label>' +
              '<div class="fieldDescription">Ticked, Sync does the lot in one go: converts the format if you picked one, ' +
              'lines the subtitle up, then translates the result. Translate on its own only translates.</div>' +
              '<div class="lapseFieldPair">' +
              '  <div class="inputContainer">' +
              '    <label class="inputLabel inputLabelUnfocused">From</label>' +
              '    <input is="emby-input" id="lapseAdvSourceLang" type="text" placeholder="auto" value="' +
              escapeHtml(defaults.SourceLanguage || '') + '" />' +
              '  </div>' +
              '  <div class="inputContainer">' +
              '    <label class="inputLabel inputLabelUnfocused">To</label>' +
              '    <input is="emby-input" id="lapseAdvTargetLang" type="text" placeholder="es" value="' +
              escapeHtml(defaults.TargetLanguage || '') + '" />' +
              '  </div>' +
              '</div>' +
              '<div class="selectContainer">' +
              '  <label class="selectLabel">Provider</label>' +
              '  <select is="emby-select" id="lapseAdvProvider" class="emby-select-withcolor emby-select">' +
              configuredProviders.map(function (p) {
                  var selected = defaults.Provider ? p.Id === defaults.Provider : p.IsDefault;
                  return '<option value="' + escapeHtml(p.Id) + '"' + (selected ? ' selected' : '') + '>' +
                      escapeHtml(p.DisplayName) + '</option>';
              }).join('') +
              '  </select>' +
              '</div>' +
              '<div class="inputContainer">' +
              '  <label class="inputLabel inputLabelUnfocused">Confidence threshold: <span id="lapseAdvConfidenceValue">' +
              threshold + '</span>%</label>' +
              '  <input type="range" id="lapseAdvConfidence" min="0" max="100" value="' + threshold + '" class="lapseRange" />' +
              '</div>' +
              '<label class="emby-checkbox-label lapseStackedCheck">' +
              '  <input id="lapseAdvMetadataHeader" type="checkbox" is="emby-checkbox"' +
              (defaults.IncludeMetadataHeader ? ' checked' : '') + ' />' +
              '  <span>Add a metadata comment block at the top</span>' +
              '</label>' +
              '<div class="fieldDescription">Skipped for .srt, which has no comment syntax to put one in. ' +
              'A translation always writes a new file next to the original, never over it.</div>'
            : '';

        var overlay = openOverlay(
            '<h3>' + escapeHtml(context.name || 'Advanced') + '</h3>' +
            '<h4 class="lapseDialogSubhead">What to sync</h4>' +
            subtitleSection +
            referenceSection +
            '<hr class="lapseDialogRule" />' +
            '<h4 class="lapseDialogSubhead">How</h4>' +
            '<div class="selectContainer">' +
            '  <label class="selectLabel">Engine</label>' +
            '  <select is="emby-select" id="lapseAdvEngine" class="emby-select-withcolor emby-select">' + engineOptions + '</select>' +
            '</div>' +
            '<div class="selectContainer">' +
            '  <label class="selectLabel">Mode</label>' +
            '  <select is="emby-select" id="lapseAdvMode" class="emby-select-withcolor emby-select">' +
            modeOptionsFor(startEngine) +
            '  </select>' +
            '</div>' +
            '<div class="inputContainer hide" id="lapseAdvPenaltyContainer">' +
            '  <label class="inputLabel inputLabelUnfocused">Penalty</label>' +
            '  <input is="emby-input" id="lapseAdvPenalty" type="number" value="' + startEngine.Penalty + '" />' +
            '  <div class="fieldDescription" id="lapseAdvPenaltyNote"></div>' +
            '</div>' +
            '<hr class="lapseDialogRule" />' +
            '<h4 class="lapseDialogSubhead">Where it goes</h4>' +
            '<div class="selectContainer">' +
            '  <label class="selectLabel">Write the result as</label>' +
            '  <select is="emby-select" id="lapseAdvFormat" class="emby-select-withcolor emby-select">' +
            '    <option value="">Same format as the subtitle</option>' +
            CONVERT_FORMATS.map(function (f) {
                return '<option value="' + f + '">.' + f + '</option>';
            }).join('') +
            '  </select>' +
            '  <div class="fieldDescription">Picking a different format writes the synced result as that ' +
            'format and leaves the original file where it is.</div>' +
            '</div>' +
            '<div class="selectContainer">' +
            '  <label class="selectLabel">File output</label>' +
            '  <select is="emby-select" id="lapseAdvOutputMode" class="emby-select-withcolor emby-select">' +
            '    <option value="">Whatever the settings say</option>' +
            '    <option value="SidecarOnly">Write a new file</option>' +
            '    <option value="SidecarWithBackup">Write a new file, keep a backup</option>' +
            '    <option value="OverwriteWithBackup">Overwrite, keep a backup</option>' +
            '    <option value="OverwriteNoBackup">Overwrite, no backup</option>' +
            '  </select>' +
            '  <div class="fieldDescription">Just for this run. The default is on the File output page.</div>' +
            '  <div class="fieldDescription hide" id="lapseAdvEmbeddedNote">The subtitle you picked is still inside the video file. ' +
            'LAPSE cannot write back into the video, so it pulls the track out to a file beside it and the result goes there ' +
            'whichever option you choose here. The track stays in the video as well.</div>' +
            '</div>' +
            translationSection +
            '<div class="lapseDialogButtons">' +
            '  <button is="emby-button" type="button" class="raised" id="lapseAdvClose"><span>Close</span></button>' +
            (canTranslate
                ? '  <button is="emby-button" type="button" class="raised" id="lapseAdvTranslate"><span>Translate only</span></button>'
                : '') +
            '  <button is="emby-button" type="button" class="raised button-submit" id="lapseAdvSync"' +
            (subtitles.length === 0 ? ' disabled' : '') + '><span>Sync</span></button>' +
            '</div>',
            true);

        var engineSelect = overlay.querySelector('#lapseAdvEngine');
        var modeSelect = overlay.querySelector('#lapseAdvMode');
        var penaltyContainer = overlay.querySelector('#lapseAdvPenaltyContainer');
        var penaltyInput = overlay.querySelector('#lapseAdvPenalty');
        var penaltyNote = overlay.querySelector('#lapseAdvPenaltyNote');
        var referenceSelect = overlay.querySelector('#lapseAdvReference');
        var syncAllRow = overlay.querySelector('#lapseAdvSyncAllRow');
        var syncAllCheck = overlay.querySelector('#lapseAdvSyncAll');
        var confidenceSlider = overlay.querySelector('#lapseAdvConfidence');

        function currentEngine() {
            return usableEngines.filter(function (e) { return e.Id === engineSelect.value; })[0] || startEngine;
        }

        function syncPenaltyVisibility() {
            var engine = currentEngine();
            var isSplit = modeSelect.value === 'Split';
            penaltyContainer.classList.toggle('hide', !(isSplit && engine.SupportsPenalty));
            penaltyNote.textContent = 'Higher values mean fewer splits. ' + engine.DisplayName +
                ' takes ' + engine.MinPenalty + ' to ' + engine.MaxPenalty + ', default ' + engine.Penalty + '.';
        }

        engineSelect.addEventListener('change', function () {
            var engine = currentEngine();
            modeSelect.innerHTML = modeOptionsFor(engine);
            penaltyInput.value = engine.Penalty;
            syncPenaltyVisibility();
        });

        modeSelect.addEventListener('change', syncPenaltyVisibility);
        syncPenaltyVisibility();

        // Picking Overwrite on a track that lives inside the mkv reads as "replace that
        // track", which is not something any of these output modes can do. Saying so up
        // front is the difference between one extracted file and a folder full of them.
        var subtitleSelect = overlay.querySelector('#lapseAdvSubtitle');
        var embeddedNote = overlay.querySelector('#lapseAdvEmbeddedNote');

        function syncEmbeddedNote() {
            if (!embeddedNote) {
                return;
            }

            var picked = subtitleSelect ? subtitleSelect.value : '';
            embeddedNote.classList.toggle('hide', picked.indexOf('embedded://') !== 0);
        }

        if (subtitleSelect) {
            subtitleSelect.addEventListener('change', syncEmbeddedNote);
        }

        syncEmbeddedNote();

        // "Sync every other subtitle to it" only means anything once a reference track is
        // picked, so it stays out of the way until then.
        function syncReferenceState() {
            var usingReference = !!(referenceSelect && referenceSelect.value);
            if (syncAllRow) {
                syncAllRow.classList.toggle('hide', !usingReference);
            }
        }

        if (referenceSelect) {
            referenceSelect.addEventListener('change', syncReferenceState);
            syncReferenceState();
        }

        function selectedSubtitlePath() {
            var select = overlay.querySelector('#lapseAdvSubtitle');
            return select ? select.value : null;
        }

        function currentPenalty() {
            var engine = currentEngine();
            return modeSelect.value === 'Split' && engine.SupportsPenalty
                ? (parseInt(penaltyInput.value, 10) || engine.Penalty)
                : 0;
        }

        overlay.querySelector('#lapseAdvClose').addEventListener('click', function () {
            overlay.remove();
        });

        if (confidenceSlider) {
            confidenceSlider.addEventListener('input', function () {
                overlay.querySelector('#lapseAdvConfidenceValue').textContent = confidenceSlider.value;
            });
        }

        // The translation boxes are part of the same dialog, so a combined run reads them
        // without asking twice.
        function collectTranslationRequest() {
            var targetInput = overlay.querySelector('#lapseAdvTargetLang');
            var target = targetInput ? (targetInput.value || '').trim() : '';

            if (!target) {
                return null;
            }

            return {
                ItemId: context.id,
                SubtitlePath: selectedSubtitlePath(),
                SourceLanguage: (overlay.querySelector('#lapseAdvSourceLang').value || '').trim() || null,
                TargetLanguage: target,
                Provider: overlay.querySelector('#lapseAdvProvider').value,
                ConfidenceThreshold: confidenceSlider ? parseInt(confidenceSlider.value, 10) : threshold,
                IncludeMetadataHeader: overlay.querySelector('#lapseAdvMetadataHeader').checked
            };
        }

        function runTranslation(job, prefix) {
            showLapseToast(prefix + 'translating into ' + job.TargetLanguage + '...');

            return lapsePost('Lapse/Translate', job).then(function (result) {
                showLapseToast(result.Success
                    ? ('Translated ' + result.TranslatedCount + ' of ' + result.LineCount + ' lines. Wrote ' + result.OutputPath)
                    : ('Translation failed: ' + result.Error));
            }).catch(function (err) {
                showLapseToast('Translation failed: ' + err.message);
            });
        }

        overlay.querySelector('#lapseAdvSync').addEventListener('click', function () {
            var alsoTranslateBox = overlay.querySelector('#lapseAdvAlsoTranslate');
            var alsoTranslate = !!(alsoTranslateBox && alsoTranslateBox.checked);
            var translation = alsoTranslate ? collectTranslationRequest() : null;

            if (alsoTranslate && !translation) {
                showLapseToast('Fill in the language to translate into before asking for a translation too.');
                return;
            }

            var subtitlePath = selectedSubtitlePath();
            var referencePath = referenceSelect ? referenceSelect.value : '';
            var syncAll = !!(syncAllCheck && syncAllCheck.checked);
            var outputFormat = overlay.querySelector('#lapseAdvFormat').value || null;
            var outputMode = overlay.querySelector('#lapseAdvOutputMode').value || null;
            var engineId = currentEngine().Id;
            var mode = modeSelect.value;
            var penalty = currentPenalty();

            if (referencePath && !syncAll && referencePath === subtitlePath) {
                showLapseToast('That subtitle is the reference. Pick another one, or tick the box to sync every other subtitle to it.');
                return;
            }

            overlay.remove();

            if (referencePath) {
                var count = syncAll ? subtitles.length - 1 : 1;
                showLapseToast('Syncing ' + count + ' subtitle' + (count === 1 ? '' : 's') + ' to the reference...');

                lapsePost('Lapse/SyncAllSubtitles', {
                    ItemId: context.id,
                    ReferencePath: referencePath,
                    SubtitlePaths: syncAll ? null : [subtitlePath],
                    EngineId: engineId,
                    Mode: mode,
                    Penalty: penalty,
                    OutputMode: outputMode
                }).then(function (result) {
                    var failed = result.Results.length - result.SucceededCount;
                    showLapseToast(failed === 0
                        ? ('Synced ' + result.SucceededCount + ' subtitle' + (result.SucceededCount === 1 ? '' : 's') + ' to the reference.')
                        : (result.SucceededCount + ' of ' + result.Results.length + ' synced, ' + failed + ' failed. See the LAPSE dashboard for details.'));

                    if (translation && result.SucceededCount > 0) {
                        return runTranslation(translation, 'Synced, ');
                    }

                    return null;
                }).catch(function (err) {
                    showLapseToast('Sync failed: ' + err.message);
                });

                return;
            }

            showLapseToast(alsoTranslate ? 'Syncing and translating...' : 'Syncing...');

            lapsePost('Lapse/Pipeline', {
                ItemId: context.id,
                SubtitlePath: subtitlePath,
                EngineId: engineId,
                Mode: mode,
                Penalty: penalty,
                Sync: true,
                OutputFormat: outputFormat,
                OutputMode: outputMode,
                Translation: translation
            }).then(function (result) {
                showLapseToast(describePipelineOutcome(result));
            }).catch(function (err) {
                showLapseToast('Sync failed: ' + err.message);
            });
        });

        var translateButton = overlay.querySelector('#lapseAdvTranslate');
        if (translateButton) {
            translateButton.addEventListener('click', function () {
                var job = collectTranslationRequest();
                if (!job) {
                    showLapseToast('Enter the language code to translate into first, e.g. es for Spanish.');
                    return;
                }

                overlay.remove();
                runTranslation(job, '');
            });
        }
    }

    // The modes come from the engine itself, so this lists exactly what that engine can do
    // and starts on whatever its default sync mode is set to.
    function modeOptionsFor(engine) {
        return (engine.Modes || []).map(function (m) {
            return '<option value="' + escapeHtml(m.Value) + '"' +
                (m.Value === engine.DefaultMode ? ' selected' : '') + '>' + escapeHtml(m.Label) + '</option>';
        }).join('');
    }

    // --- go ---

    function start() {
        log('inject.js starting up');
        ensureStylesheetLoaded();
        document.addEventListener('click', rememberCardContextFromClick, true);
        startWatchingForActionSheets();
        startWatchingForSubtitles();

        // ApiClient isn't necessarily ready the instant the DOM is, and there's no event
        // for it, so give it a moment before the first authenticated call.
        setTimeout(refreshSubtitleAppearance, 2000);

        // The client is a single page app, so moving between pages doesn't re-run any of
        // this. Re-reading the settings on navigation is what makes a change made in the
        // dashboard show up on the next thing played without a full reload.
        window.addEventListener('hashchange', refreshSubtitleAppearance);
        window.addEventListener('popstate', refreshSubtitleAppearance);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
