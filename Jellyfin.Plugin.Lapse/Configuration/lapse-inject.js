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

            scroller.appendChild(makeMenuButton('lapse-sync-all-subtitles', 'Sync All Subtitles to Reference', 'compare_arrows', function () {
                openReferencePopup(context);
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

            // Picture based tracks hold no text, so nothing here can line them up.
            var usable = subtitles.filter(function (s) { return s.Supported !== false; });

            if (usable.length === 0) {
                showLapseToast('The only subtitles on this item are picture based (PGS or VobSub). Those are images of text, so they need OCR before anything can sync them.');
                return;
            }

            // Everything left is in a format no engine reads, but the plugin can convert
            // it into one that they do. Worth offering rather than refusing.
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
            var convertible = subtitles.filter(function (s) { return s.Supported !== false; });

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
            'alone unless you say otherwise. Formats no engine reads, like MicroDVD .sub, become plain ' +
            'srt that everything else here works on.</div>' +
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

        // Default to something that isn't what the file already is, so the first press
        // does something.
        function pickDefaultFormat() {
            var current = currentFormat();
            formatSelect.value = current === 'srt' ? 'vtt' : 'srt';
        }

        function currentFormat() {
            var match = /\.([a-z0-9]+)$/i.exec(select.value || '');
            return match ? match[1].toLowerCase() : '';
        }

        select.addEventListener('change', pickDefaultFormat);
        pickDefaultFormat();

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

    // --- "Sync All Subtitles to Reference" on a single item ---

    function openReferencePopup(context) {
        showLapseToast('Checking subtitles...');

        lapseGet('Lapse/Items/' + context.id + '/Subtitles').then(function (subtitles) {
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
                doSyncAll(context.id, referencePath, subtitles.length - 1);
            });
        }).catch(function (err) {
            showLapseToast('Could not check subtitles: ' + err.message);
        });
    }

    function doSyncAll(itemId, referencePath, count) {
        showLapseToast('Syncing ' + count + ' subtitle' + (count === 1 ? '' : 's') + ' to the reference...');

        lapsePost('Lapse/SyncAllSubtitles', {
            ItemId: itemId,
            ReferencePath: referencePath
        }).then(function (result) {
            var failed = result.Results.length - result.SucceededCount;
            showLapseToast(failed === 0
                ? ('Synced all ' + result.SucceededCount + ' subtitles to the reference.')
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

        var toast = showLapseToast('Starting...', true);

        function poll() {
            lapseGet('Lapse/Queue').then(function (snapshot) {
                var unit = snapshot.UnitName || 'item';
                var plural = snapshot.Total === 1 ? unit : unit + 's';

                if (snapshot.Running) {
                    toast.textContent = (snapshot.JobName ? snapshot.JobName + ': ' : '') +
                        snapshot.Completed + ' / ' + snapshot.Total + ' ' + plural + ' processed' +
                        (snapshot.CurrentItemName ? ' - ' + snapshot.CurrentItemName : '');
                    return;
                }

                stopProgressPolling();
                toast.textContent = (snapshot.JobName ? snapshot.JobName + ': ' : '') +
                    'finished, ' + snapshot.Completed + ' / ' + snapshot.Total + ' ' + plural + ' processed.';

                setTimeout(function () {
                    if (toast.parentNode) {
                        toast.remove();
                    }
                }, 8000);
            }).catch(function (err) {
                stopProgressPolling();
                toast.textContent = 'Lost track of the sync job: ' + err.message;
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
            lapseGet('Lapse/Translate/Providers')
        ]).then(function (results) {
            var toast = document.querySelector('.lapseToast');
            if (toast) {
                toast.remove();
            }

            showAdvancedDialog(context, results[0], results[1], results[2]);
        }).catch(function (err) {
            showLapseToast('Could not open the advanced options: ' + err.message);
        });
    }

    function showAdvancedDialog(context, subtitles, engines, providers) {
        var usableEngines = engines.filter(function (e) { return e.Installed && !e.RunCheckError; });
        if (usableEngines.length === 0) {
            showLapseToast('No engine is installed and working. Install one from the LAPSE dashboard first.');
            return;
        }

        var configuredProviders = (providers || []).filter(function (p) { return p.Configured; });

        var startEngine = usableEngines.filter(function (e) { return e.IsDefault; })[0] || usableEngines[0];

        var engineOptions = usableEngines.map(function (e) {
            return '<option value="' + escapeHtml(e.Id) + '"' + (e.Id === startEngine.Id ? ' selected' : '') + '>' +
                escapeHtml(e.DisplayName) + '</option>';
        }).join('');

        var subtitleSection = subtitles.length === 0
            ? '<p class="fieldDescription">No external subtitle found for this item.</p>'
            : '<div class="selectContainer">' +
              '  <label class="selectLabel">Subtitle</label>' +
              '  <select is="emby-select" id="lapseAdvSubtitle" class="emby-select-withcolor emby-select">' +
              subtitleOptionsHtml(subtitles) +
              '  </select>' +
              '</div>';

        var referenceSection = subtitles.length > 1
            ? '<hr class="lapseDialogRule" />' +
              '<div class="selectContainer">' +
              '  <label class="selectLabel">Sync all subtitles to this one</label>' +
              '  <select is="emby-select" id="lapseAdvReference" class="emby-select-withcolor emby-select">' +
              subtitleOptionsHtml(subtitles) +
              '  </select>' +
              '</div>' +
              '<button is="emby-button" type="button" class="raised lapseSmallButton" id="lapseAdvSyncAll"><span>Sync all to reference</span></button>'
            : '';

        var translationSection = (subtitles.length > 0 && configuredProviders.length > 0)
            ? '<hr class="lapseDialogRule" />' +
              '<h4 class="lapseDialogSubhead">Translate</h4>' +
              '<div class="lapseFieldPair">' +
              '  <div class="inputContainer">' +
              '    <label class="inputLabel inputLabelUnfocused">From</label>' +
              '    <input is="emby-input" id="lapseAdvSourceLang" type="text" placeholder="auto" />' +
              '  </div>' +
              '  <div class="inputContainer">' +
              '    <label class="inputLabel inputLabelUnfocused">To</label>' +
              '    <input is="emby-input" id="lapseAdvTargetLang" type="text" placeholder="es" />' +
              '  </div>' +
              '</div>' +
              '<div class="selectContainer">' +
              '  <label class="selectLabel">Provider</label>' +
              '  <select is="emby-select" id="lapseAdvProvider" class="emby-select-withcolor emby-select">' +
              configuredProviders.map(function (p) {
                  return '<option value="' + escapeHtml(p.Id) + '"' + (p.IsDefault ? ' selected' : '') + '>' +
                      escapeHtml(p.DisplayName) + '</option>';
              }).join('') +
              '  </select>' +
              '</div>' +
              '<div class="inputContainer">' +
              '  <label class="inputLabel inputLabelUnfocused">Confidence threshold: <span id="lapseAdvConfidenceValue">70</span>%</label>' +
              '  <input type="range" id="lapseAdvConfidence" min="0" max="100" value="70" class="lapseRange" />' +
              '</div>' +
              '<label class="emby-checkbox-label lapseStackedCheck">' +
              '  <input id="lapseAdvMetadataHeader" type="checkbox" is="emby-checkbox" checked />' +
              '  <span>Add a metadata comment block at the top</span>' +
              '</label>' +
              '<div class="fieldDescription">Writes a new file next to the original, never over it.</div>' +
              '<button is="emby-button" type="button" class="raised lapseSmallButton" id="lapseAdvTranslate"><span>Translate</span></button>'
            : '';

        var overlay = openOverlay(
            '<h3>' + escapeHtml(context.name || 'Advanced') + '</h3>' +
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
            subtitleSection +
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
            '</div>' +
            '<label class="emby-checkbox-label lapseStackedCheck">' +
            '  <input type="checkbox" is="emby-checkbox" id="lapseAdvAlsoTranslate" />' +
            '  <span>Translate it as well, using the boxes further down</span>' +
            '</label>' +
            '<div class="fieldDescription">Ticked, Sync does all of it in one go: converts the format if ' +
            'you picked one, lines it up, then translates what came out. Left alone, Sync only syncs.</div>' +
            '<div class="lapseDialogButtons">' +
            '  <button is="emby-button" type="button" class="raised" id="lapseAdvClose"><span>Close</span></button>' +
            '  <button is="emby-button" type="button" class="raised button-submit" id="lapseAdvSync"' +
            (subtitles.length === 0 ? ' disabled' : '') + '><span>Sync</span></button>' +
            '</div>' +
            referenceSection +
            translationSection,
            true);

        var engineSelect = overlay.querySelector('#lapseAdvEngine');
        var modeSelect = overlay.querySelector('#lapseAdvMode');
        var penaltyContainer = overlay.querySelector('#lapseAdvPenaltyContainer');
        var penaltyInput = overlay.querySelector('#lapseAdvPenalty');
        var penaltyNote = overlay.querySelector('#lapseAdvPenaltyNote');

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

        overlay.querySelector('#lapseAdvSync').addEventListener('click', function () {
            var alsoTranslate = overlay.querySelector('#lapseAdvAlsoTranslate').checked;
            var translation = alsoTranslate ? collectTranslationRequest() : null;

            if (alsoTranslate && !translation) {
                showLapseToast('Fill in the target language further down before asking for a translation too.');
                return;
            }

            overlay.remove();
            showLapseToast(alsoTranslate ? 'Syncing and translating...' : 'Syncing...');

            lapsePost('Lapse/Pipeline', {
                ItemId: context.id,
                SubtitlePath: selectedSubtitlePath(),
                EngineId: currentEngine().Id,
                Mode: modeSelect.value,
                Penalty: currentPenalty(),
                Sync: true,
                OutputFormat: overlay.querySelector('#lapseAdvFormat').value || null,
                OutputMode: overlay.querySelector('#lapseAdvOutputMode').value || null,
                Translation: translation
            }).then(function (result) {
                showLapseToast(describePipelineOutcome(result));
            }).catch(function (err) {
                showLapseToast('Sync failed: ' + err.message);
            });
        });

        var syncAllButton = overlay.querySelector('#lapseAdvSyncAll');
        if (syncAllButton) {
            syncAllButton.addEventListener('click', function () {
                var referencePath = overlay.querySelector('#lapseAdvReference').value;
                overlay.remove();
                doSyncAll(context.id, referencePath, subtitles.length - 1);
            });
        }

        var confidenceSlider = overlay.querySelector('#lapseAdvConfidence');
        if (confidenceSlider) {
            confidenceSlider.addEventListener('input', function () {
                overlay.querySelector('#lapseAdvConfidenceValue').textContent = confidenceSlider.value;
            });
        }

        // The translation boxes are further down the same dialog, so a combined run can
        // read them without asking twice.
        function collectTranslationRequest() {
            var targetInput = overlay.querySelector('#lapseAdvTargetLang');
            var target = targetInput ? (targetInput.value || '').trim() : '';

            if (!target) {
                return null;
            }

            var slider = overlay.querySelector('#lapseAdvConfidence');

            return {
                ItemId: context.id,
                SubtitlePath: selectedSubtitlePath(),
                SourceLanguage: (overlay.querySelector('#lapseAdvSourceLang').value || '').trim() || null,
                TargetLanguage: target,
                Provider: overlay.querySelector('#lapseAdvProvider').value,
                ConfidenceThreshold: slider ? parseInt(slider.value, 10) : 70,
                IncludeMetadataHeader: overlay.querySelector('#lapseAdvMetadataHeader').checked
            };
        }

        var translateButton = overlay.querySelector('#lapseAdvTranslate');
        if (translateButton) {
            translateButton.addEventListener('click', function () {
                var target = (overlay.querySelector('#lapseAdvTargetLang').value || '').trim();
                if (!target) {
                    showLapseToast('Enter the language code to translate into first, e.g. es for Spanish.');
                    return;
                }

                var job = {
                    ItemId: context.id,
                    SubtitlePath: selectedSubtitlePath(),
                    SourceLanguage: (overlay.querySelector('#lapseAdvSourceLang').value || '').trim() || null,
                    TargetLanguage: target,
                    Provider: overlay.querySelector('#lapseAdvProvider').value,
                    ConfidenceThreshold: parseInt(confidenceSlider.value, 10),
                    IncludeMetadataHeader: overlay.querySelector('#lapseAdvMetadataHeader').checked
                };

                overlay.remove();
                showLapseToast('Translating into ' + target + '...');

                lapsePost('Lapse/Translate', job).then(function (result) {
                    showLapseToast(result.Success
                        ? ('Translated ' + result.TranslatedCount + ' of ' + result.LineCount + ' lines. Wrote ' + result.OutputPath)
                        : ('Translation failed: ' + result.Error));
                }).catch(function (err) {
                    showLapseToast('Translation failed: ' + err.message);
                });
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
