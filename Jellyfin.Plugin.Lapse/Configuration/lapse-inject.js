// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

// Runs on every page of the web client (added to index.html by ScriptInjectionMiddleware).
// Unlike the dashboard script it can't rely on the "Dashboard" helper, only ApiClient.

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

    // What the signed in user may do. Asked once a minute at most, and only to decide
    // which menu entries to show: the server checks every call again regardless.
    var accessInfo = null;
    var accessCheckedAt = 0;

    function loadAccess() {
        if (accessInfo && Date.now() - accessCheckedAt < 60000) {
            return Promise.resolve(accessInfo);
        }

        return lapseGet('Lapse/Access').then(function (access) {
            accessInfo = access || {};
            accessCheckedAt = Date.now();
            return accessInfo;
        }).catch(function (err) {
            // Can't tell, so show the entries and let the server say no, which is how
            // this behaved before it could ask.
            log('could not check what this user may do: ' + err);
            return { CanEditSubtitles: true };
        });
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
                    throw new Error(errorMessage(text, res.status));
                });
            }

            var contentType = res.headers.get('content-type') || '';
            if (contentType.indexOf('application/json') !== -1) {
                return res.json();
            }

            return null;
        });
    }

    // Errors come back as JSON, a quoted string or an ASP.NET problem details object. Show the
    // sentence, not the quotes and braces.
    function errorMessage(text, status) {
        if (!text) {
            return 'Request failed with status ' + status;
        }

        try {
            var parsed = JSON.parse(text);

            if (typeof parsed === 'string') {
                return parsed;
            }

            if (parsed && (parsed.detail || parsed.title || parsed.message)) {
                return parsed.detail || parsed.title || parsed.message;
            }
        } catch (e) {
            // not JSON, so it is the sentence already
        }

        return text;
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
    // Text tracks only. PGS/VobSub are images, and ASS styles each line inline which wins anyway.
    //
    // Two parts: the stylesheet covers existing cue elements and ::cue (CSS only). The observer
    // stamps the same values inline on each new subtitle element, because Jellyfin's own
    // appearance helper writes inline styles and an inline !important is the only thing that wins.
    var SUBTITLE_SELECTORS = [
        '.videoSubtitles',
        '.videoSubtitlesInner',
        '.videoOsdSubtitles',
        '.subtitleAppearanceContainer',
        '.htmlvideoplayer-subtitles'
    ];

    var appearanceSettings = null;
    var appearanceFetchedAt = 0;
    var clientObserver = null;

    // Called on every navigation and whenever a video shows up. Navigation reuses the last
    // answer for 30s (no point asking the server on every page change), a video starting
    // passes force so a change made in the dashboard shows up, but still at most every 5s
    // since the player can add more than one video element.
    function refreshSubtitleAppearance(force) {
        // no ApiClient on the login screen, and touching it there throws
        if (typeof ApiClient === 'undefined' || !ApiClient.accessToken()) {
            return;
        }

        var maxAge = force === true ? 5000 : 30000;
        if (Date.now() - appearanceFetchedAt < maxAge) {
            return;
        }

        appearanceFetchedAt = Date.now();

        lapseGet('Lapse/Appearance').then(function (appearance) {
            appearanceSettings = appearance;
            applySubtitleAppearance();
        }).catch(function (err) {
            appearanceFetchedAt = 0;
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
        ensureChosenFontIsLoadable(appearance.FontFamily);
    }

    // The fonts installed on the server, read once and kept. Only needed when somebody has
    // picked one, which most people won't have.
    var installedFonts = null;

    function loadInstalledFonts() {
        if (installedFonts) {
            return Promise.resolve(installedFonts);
        }

        return lapseGet('Lapse/Fonts').then(function (status) {
            installedFonts = status || { Fonts: [] };
            return installedFonts;
        }).catch(function () {
            return { Fonts: [] };
        });
    }

    function ensureChosenFontIsLoadable(family) {
        if (!family) {
            return;
        }

        loadInstalledFonts().then(function (status) {
            var match = (status.Fonts || []).filter(function (file) {
                return familyNameFor(file).toLowerCase() === String(family).toLowerCase();
            });

            // nothing matching = a system font someone typed in, the stylesheet just asks for it by name
            if (match.length) {
                ensureFontFaceLoaded(preferRegular(match));
            }
        });
    }

    // register the upright file, the browser fakes bold/italic from it (registering bold makes everything bold)
    function preferRegular(files) {
        for (var i = 0; i < files.length; i++) {
            if (/[-_]Regular\./i.test(files[i])) {
                return files[i];
            }
        }

        return files[0];
    }

    function backgroundOf(appearance) {
        return appearance.BackgroundEnabled ? (appearance.BackgroundColor || '#00000080') : 'transparent';
    }

    function fontStackOf(appearance) {
        // always keep a fallback so missing glyphs (Arabic in OpenDyslexic) still draw instead of boxes
        return appearance.FontFamily
            ? '"' + appearance.FontFamily.replace(/["\\]/g, '') + '", sans-serif'
            : '';
    }

    function spacingOf(appearance) {
        return (appearance.LetterSpacingPx || 0) + 'px';
    }

    // The player anchors subtitles to the bottom, so lifting means moving that strip up. HTML
    // subtitles only, libass draws ASS on a canvas with its own positioning.
    function positionCssFor(appearance) {
        var percent = appearance.VerticalPositionPercent || 0;

        if (percent <= 0) {
            return '';
        }

        return '.videoSubtitles {' +
            '  bottom: ' + percent + '% !important;' +
            '}';
    }

    function buildAppearanceCss(appearance) {
        var fontSize = (appearance.FontSizePx || 48) + 'px';
        var color = appearance.TextColor || '#FFFFFF';
        var background = backgroundOf(appearance);
        var fontStack = fontStackOf(appearance);
        var spacing = spacingOf(appearance);
        var family = fontStack ? '  font-family: ' + fontStack + ' !important;' : '';

        return positionCssFor(appearance) +
            SUBTITLE_SELECTORS.join(', ') + ' {' +
            '  font-size: ' + fontSize + ' !important;' +
            '  color: ' + color + ' !important;' +
            '  letter-spacing: ' + spacing + ' !important;' +
            family +
            '}' +
            '.videoSubtitlesInner, .videoSubtitles .videoSubtitlesInner {' +
            '  background-color: ' + background + ' !important;' +
            '}' +

            // Native track rendering, which no amount of element styling can reach.
            'video::cue {' +
            '  font-size: ' + fontSize + ';' +
            '  color: ' + color + ';' +
            '  background-color: ' + background + ';' +
            '  letter-spacing: ' + spacing + ';' +
            (fontStack ? '  font-family: ' + fontStack + ';' : '') +
            '}';
    }

    // The font lives on the server, so the browser needs a URL for it. Jellyfin already serves the
    // fallback folder for burn-in, we just link to it.
    function ensureFontFaceLoaded(fileName) {
        if (!fileName) {
            return;
        }

        var id = 'lapseSubtitleFontFace';
        var style = document.getElementById(id);

        if (style && style.getAttribute('data-lapse-font') === fileName) {
            return;
        }

        if (!style) {
            style = document.createElement('style');
            style.id = id;
            document.head.appendChild(style);
        }

        var family = familyNameFor(fileName);

        // url() can't send an Authorization header, so the token goes in the query string (the web
        // client's own ASS renderer does the same)
        var url = ApiClient.getUrl('FallbackFont/Fonts/' + encodeURIComponent(fileName), {
            api_key: ApiClient.accessToken()
        });

        style.setAttribute('data-lapse-font', fileName);
        style.textContent =
            '@font-face {' +
            '  font-family: "' + family + '";' +
            '  src: url("' + url + '");' +
            '  font-display: swap;' +
            '}';
    }

    // OpenDyslexic-Regular.otf -> "OpenDyslexic", so regular/bold/italic files share one family
    function familyNameFor(fileName) {
        return String(fileName)
            .replace(/\.(otf|ttf|woff2?|woff)$/i, '')
            .replace(/[-_](Regular|Bold|Italic|BoldItalic|Medium|Light)$/i, '');
    }

    function stampSubtitleElement(element) {
        var appearance = appearanceSettings;
        if (!appearance || !appearance.Enabled) {
            return;
        }

        element.setAttribute('data-lapse-styled', '1');
        element.style.setProperty('font-size', (appearance.FontSizePx || 48) + 'px', 'important');
        element.style.setProperty('color', appearance.TextColor || '#FFFFFF', 'important');
        element.style.setProperty('letter-spacing', spacingOf(appearance), 'important');

        var fontStack = fontStackOf(appearance);
        if (fontStack) {
            element.style.setProperty('font-family', fontStack, 'important');
        } else {
            element.style.removeProperty('font-family');
        }

        // background on the inner element, on the outer one it paints the whole video width
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
            element.style.removeProperty('letter-spacing');
            element.style.removeProperty('font-family');
            element.removeAttribute('data-lapse-styled');
        });
    }

    var SUBTITLE_SELECTOR = SUBTITLE_SELECTORS.join(', ');

    // Runs for every element added anywhere, so keep it cheap. Subtitle elements are only looked
    // for while there's a style to apply.
    function inspectAddedElement(node, styling) {
        if (styling) {
            if (node.matches && node.matches(SUBTITLE_SELECTOR)) {
                stampSubtitleElement(node);
            }

            if (node.querySelectorAll) {
                node.querySelectorAll(SUBTITLE_SELECTOR).forEach(stampSubtitleElement);
            }
        }

        // playback starting, make sure the settings are current
        if (node.nodeName === 'VIDEO' || (node.querySelector && node.querySelector('video'))) {
            refreshSubtitleAppearance(true);
        }

        var sheet = node.classList && node.classList.contains('actionSheet')
            ? node
            : (node.querySelector ? node.querySelector('.actionSheet') : null);

        if (sheet) {
            handleActionSheetOpened(sheet);
        }
    }

    function startWatchingTheClient() {
        if (clientObserver) {
            return;
        }

        clientObserver = new MutationObserver(function (mutations) {
            var styling = !!(appearanceSettings && appearanceSettings.Enabled);

            mutations.forEach(function (mutation) {
                mutation.addedNodes.forEach(function (node) {
                    if (node.nodeType === 1) {
                        inspectAddedElement(node, styling);
                    }
                });
            });
        });

        clientObserver.observe(document.body, { childList: true, subtree: true });
        log('watching for action sheets and subtitles');
    }

    // --- figure out which item the open action sheet belongs to ---

    // jellyfin-web has changed routing between versions (#!/details?id=x, real query strings,
    // /details/<id>), so try all of them
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

    // Set when the CC button on the player bar is pressed, so the sheet that opens right after can
    // be told apart from the other sheets that bar opens.
    var pendingSubtitleMenu = 0;

    function rememberSubtitleMenuFromClick(e) {
        if (!e.target.closest) {
            return;
        }

        var button = e.target.closest('.btnSubtitles, [data-action="subtitles"]');

        // Older and newer jellyfin-web builds disagree about the class, but the icon has
        // been the same throughout, so fall back to that.
        if (!button) {
            var icon = e.target.closest('.closed_caption');
            button = icon ? icon.closest('button') : null;
        }

        if (button) {
            pendingSubtitleMenu = Date.now();
        }
    }

    function isSubtitleMenuOpening() {
        return (Date.now() - pendingSubtitleMenu) < 1500;
    }

    function rememberCardContextFromClick(e) {
        // the card's "more" button is [data-action="menu"], class names weren't reliable
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
        // both throw synchronously between sessions, so do it all inside the promise
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

        // Stop the click here. Otherwise it bubbles to the sheet's own handler, which reads our data-id
        // as a track index and Jellyfin turns the subtitle OFF ("No available track, cannot apply
        // offset"). Opening this menu used to silently switch subtitles off because of that.
        button.addEventListener('click', function (e) {
            e.stopPropagation();

            // Close before onClick: some of these open an overlay that closes on Escape, and the sheet's
            // close would land on that instead.
            closeActionSheet(button);
            onClick(e);
        });
        return button;
    }

    // We stopped the click, so close the sheet ourselves. A synthetic Escape is ignored by
    // jellyfin-web 10.11. "_close" is what its dialog helper fires after closing, and its handler
    // does all the cleanup (backdrop, history, focus).
    function closeActionSheet(withinButton) {
        var sheet = withinButton.closest('.actionSheet');
        if (sheet && document.body.contains(sheet)) {
            sheet.dispatchEvent(new CustomEvent('_close', { bubbles: false, cancelable: false }));
        }
    }

    function addMenuButtons(sheet, context) {
        // the scroller holds the entries, but the wrapper markup moves around between versions
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

            if (accessInfo && accessInfo.OpenSubtitlesEnabled) {
                scroller.appendChild(makeMenuButton('lapse-find-subtitles', 'Find Subtitles Online', 'travel_explore', function () {
                    openOnlineSubtitlePicker(context.id, context.name);
                }));
            }

            addChooseSubtitleButton(scroller, context.id);
        }

        keepSheetOnScreen(scroller.closest('.actionSheet') || scroller);
        log('added the LAPSE buttons for ' + context.type + ' ' + context.id);
    }

    // Only shown when this item actually has answers waiting, so the menu doesn't grow an
    // entry that would only ever say "nothing to choose".
    function addChooseSubtitleButton(scroller, itemId) {
        lapseGet('Lapse/Items/' + itemId + '/Candidates').then(function (status) {
            var sets = (status && status.Sets) || [];

            if (!sets.length || !document.body.contains(scroller) || scroller.querySelector('.lapseChooseButton')) {
                return;
            }

            var count = sets.reduce(function (total, set) {
                return total + ((set.Candidates || []).length);
            }, 0);

            var button = makeMenuButton(
                'lapse-choose-subtitles',
                'Choose the right subtitle (' + count + ' waiting)',
                'compare_arrows',
                function () {
                    openCandidatePicker(itemId);
                });

            button.classList.add('lapseChooseButton');
            scroller.appendChild(button);
            keepSheetOnScreen(scroller.closest('.actionSheet') || scroller);
        }).catch(function (err) {
            log('could not check for waiting subtitles on ' + itemId + ': ' + err);
        });
    }

    function handleActionSheetOpened(sheet) {
        if (sheet.querySelector('.lapseSyncButton')) {
            return;
        }

        // The subtitle list from the player's CC button. The appearance entry goes here because that's
        // where people notice the subtitles are too small, not three menus deep in the dashboard.
        if (isSubtitleMenuOpening()) {
            pendingSubtitleMenu = 0;
            addSubtitleSettingsButton(sheet);
            return;
        }

        Promise.all([resolveItemContext(), loadAccess()]).then(function (answers) {
            var context = answers[0];
            var access = answers[1];

            if (!context) {
                log('could not figure out which item this menu belongs to, not adding the buttons');
                return;
            }

            if (!access.CanEditSubtitles) {
                log('this user can\'t edit subtitles, not adding the buttons');
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
            // a menu without our entries is a nuisance, an unhandled rejection in a MutationObserver is console spam on every menu
            log('could not add the buttons to this menu: ' + err);
        });
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

            // Supported = the engine can do something with it (LAPSE can sync PGS/VobSub timing)
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
                    // only lowercase the first letter, the whole sentence would mangle the path and engine name
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

        if (result.CandidateCount > 0) {
            return 'LAPSE was not sure, so the other engines had a go too. ' + result.CandidateCount +
                ' answers are now extra subtitle tracks. Play the item, try them, and keep the one that lines up ' +
                'from Subtitle tools in the player.';
        }

        if (result.Forced && (result.Skipped || result.Unconfirmed)) {
            return 'This subtitle has too few lines for LAPSE to check its answer, so the original was left alone' +
                (result.Unconfirmed && result.OutputPath ? ' and the forced guess went to ' + baseName(result.OutputPath) : '') +
                '. A short track like this is best lined up against the full subtitle, with Sync Subtitles to Reference.';
        }

        if (result.Verdict === 'nothing' && (result.Skipped || result.Unconfirmed)) {
            return 'LAPSE could not match this subtitle to the audio at all. That nearly always means it was made ' +
                'for a different release or a different film, so the original was left alone' +
                (result.Unconfirmed && result.OutputPath ? ' and its guess went to ' + baseName(result.OutputPath) : '') +
                '. A subtitle made for this release is the fix.';
        }

        if (result.Skipped) {
            return 'Left the original alone: ' + describeResult(result) +
                ', which is under the confidence threshold. Change what happens then under ' +
                'File output in the LAPSE dashboard, or, if you are sure the subtitle belongs ' +
                'to this video, turn on "Sync even when the engine is unsure" under Engines, Advanced.';
        }

        if (result.Unconfirmed) {
            return 'LAPSE was not sure about this one (' + describeResult(result) + '), so the original was left ' +
                'alone and its answer was written beside it as ' + baseName(result.OutputPath || '') +
                '. Play it and see if it lines up.';
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

        // no EngineId or Mode on purpose, the server uses the default engine and its default mode
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

        // srt is what people want almost every time, so it's the default selection
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

    // converting carries on into a sync by default, so the toast has to say how that went too
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

    // --- "Subtitle settings", in the player's own subtitle menu ---
    //
    // Saved per user on the server, so it follows them across devices and doesn't change anything
    // for anyone else. The dashboard's appearance is just the starting point.

    var DYSLEXIC_PRESET = {
        Enabled: true,
        FontSizePx: 60,
        LetterSpacingPx: 2,
        FontFamily: 'OpenDyslexic',
        TextColor: '#FFFFFF',
        BackgroundColor: '#000000B3',
        BackgroundEnabled: true
    };

    // ------------------------------------------------------------- the in-player panel
    //
    // In-player tools: fix subtitles while the film keeps playing instead of stopping, finding the
    // item, fixing it and starting over.
    //
    // The live delay is Jellyfin's own (it already handles libass, text tracks and its cue list).
    // It just doesn't remember it or touch the file, which is what "Save to file" is for.

    // The player is a webpack module, not a global. Pushing an empty chunk gets us
    // __webpack_require__, and we find the module by what it does since ids change every build.
    var webpackRequire = null;
    var cachedPlaybackManager = null;
    var bridgeProbes = 0;

    // When the player can't be found, when that was. Looking means turning every module
    // in the client into text, and the delay slider asks on every tick of a drag.
    var playbackManagerMissedAt = 0;
    var PLAYBACK_MANAGER_RETRY_MS = 5000;

    function getWebpackRequire() {
        if (webpackRequire) {
            return webpackRequire;
        }

        try {
            var chunks = self.webpackChunk;
            if (!chunks || typeof chunks.push !== 'function') {
                return null;
            }

            // Needs an id webpack hasn't seen (it skips chunks it thinks are loaded and marks every id it
            // gets). Counter, not timestamp, two probes in one ms would collide.
            bridgeProbes++;

            chunks.push([['lapse-bridge-' + bridgeProbes], {}, function (req) {
                webpackRequire = req;
            }]);
        } catch (err) {
            log('could not reach the webpack runtime: ' + err);
        }

        return webpackRequire;
    }

    function looksLikePlaybackManager(value) {
        return !!value
            && typeof value === 'object'
            && typeof value.setSubtitleOffset === 'function'
            && typeof value.getPlayerSubtitleOffset === 'function'
            && typeof value.getCurrentPlayer === 'function';
    }

    // could be the module's exports or a named export of it, and reading properties can run getters, so nothing may throw
    function findManagerIn(exports) {
        if (!exports || typeof exports !== 'object') {
            return null;
        }

        if (looksLikePlaybackManager(exports)) {
            return exports;
        }

        for (var key in exports) {
            var value;

            try {
                value = exports[key];
            } catch (err) {
                continue;
            }

            if (looksLikePlaybackManager(value)) {
                return value;
            }
        }

        return null;
    }

    // found by its source, not its id. Checked against the 10.11 and 12.0 bundles, exactly one match each.
    var MANAGER_SOURCE_MARKER = /getPlayerSubtitleOffset\s*[:=]\s*function/;

    function playbackManager() {
        if (cachedPlaybackManager) {
            return cachedPlaybackManager;
        }

        if (Date.now() - playbackManagerMissedAt < PLAYBACK_MANAGER_RETRY_MS) {
            return null;
        }

        var found = findPlaybackManager();
        if (!found) {
            playbackManagerMissedAt = Date.now();
        }

        return found;
    }

    function findPlaybackManager() {
        var req = getWebpackRequire();
        if (!req) {
            return null;
        }

        // a build that keeps its module cache is the easy case. Jellyfin's don't, hence the rest
        if (req.c) {
            for (var cached in req.c) {
                var entry;

                try {
                    entry = req.c[cached] && req.c[cached].exports;
                } catch (err) {
                    continue;
                }

                var hit = findManagerIn(entry);
                if (hit) {
                    cachedPlaybackManager = hit;
                    return hit;
                }
            }
        }

        // Otherwise go through the factories, but only require ones whose source mentions the method.
        // Something is already playing, so that module has run and requiring it just returns it.
        var factories = req.m;
        if (!factories) {
            return null;
        }

        for (var id in factories) {
            var source;

            try {
                source = String(factories[id]);
            } catch (err) {
                continue;
            }

            if (!MANAGER_SOURCE_MARKER.test(source)) {
                continue;
            }

            var exports;

            try {
                exports = req(id);
            } catch (err) {
                log('could not load module ' + id + ' while looking for the player: ' + err);
                continue;
            }

            var found = findManagerIn(exports);
            if (found) {
                cachedPlaybackManager = found;
                return found;
            }
        }

        return null;
    }

    // The panel speaks delay like every other player (positive = later). Jellyfin's
    // setSubtitleOffset is the other way round, so the sign flips here and only here.
    function delayToJellyfinOffset(delaySeconds) {
        return -delaySeconds;
    }

    function jellyfinOffsetToDelay(offsetSeconds) {
        return -offsetSeconds;
    }

    // what the file needs in ms, positive delay = cues later, so this keeps the panel's sign
    function delayToShiftMs(delaySeconds) {
        return Math.round(delaySeconds * 1000);
    }

    // Don't pass a player. playbackManager's offset methods fall back to their own _currentPlayer
    // (checked in the 10.11.11 and 12.0 sources) and the native slider doesn't pass one either.
    // Passing ours used to move the number but nothing on screen.
    function readCurrentDelay() {
        var manager = playbackManager();

        if (manager && manager.getCurrentPlayer && manager.getCurrentPlayer()) {
            try {
                var offset = parseFloat(manager.getPlayerSubtitleOffset());
                if (!isNaN(offset)) {
                    return jellyfinOffsetToDelay(offset);
                }
            } catch (err) {
                log('could not read the current subtitle offset: ' + err);
            }
        }

        return fallbackDelaySeconds;
    }

    // Jellyfin debounces setSubtitleOffset by 100ms (10.11.11 and 12.0 source). Reading it back right
    // away gives the old value, so the old check thought the call failed and fell back to the text
    // track path, which had nothing to move. Now we only verify after the debounce, and only log.
    var DEBOUNCE_SETTLE_MS = 150;

    function applyDelay(delaySeconds) {
        var manager = playbackManager();

        if (manager && manager.getCurrentPlayer && manager.getCurrentPlayer()) {
            try {
                // Without this the player treats the offset as something it is not
                // currently showing and puts it back to zero on the next track change.
                if (typeof manager.enableShowingSubtitleOffset === 'function') {
                    manager.enableShowingSubtitleOffset();
                }

                manager.setSubtitleOffset(delayToJellyfinOffset(delaySeconds));

                // diagnostic only, logged and never acted on (see above)
                var wanted = delayToJellyfinOffset(delaySeconds);
                setTimeout(function () {
                    try {
                        var confirmed = parseFloat(manager.getPlayerSubtitleOffset());
                        if (isNaN(confirmed) || Math.abs(confirmed - wanted) >= 0.01) {
                            log('the player did not settle on the offset it was given - got ' +
                                confirmed + ', wanted ' + wanted);
                        }
                    } catch (err) {
                        log('could not confirm the subtitle offset after the fact: ' + err);
                    }
                }, DEBOUNCE_SETTLE_MS);

                return true;
            } catch (err) {
                log('the player refused the subtitle offset: ' + err);
            }
        }

        return applyDelayToTextTracks(delaySeconds);
    }

    // Fallback when the player can't be reached: moves the browser's text track cues. Does nothing
    // for ASS, libass draws that on a canvas.
    var fallbackDelaySeconds = 0;

    function applyDelayToTextTracks(delaySeconds) {
        var video = document.querySelector('video');
        if (!video || !video.textTracks) {
            return false;
        }

        var moved = false;

        for (var i = 0; i < video.textTracks.length; i++) {
            var track = video.textTracks[i];

            if (track.mode === 'disabled' || !track.cues || !track.cues.length) {
                continue;
            }

            // Keep the untouched timings, so dragging the slider moves everything from
            // where it started rather than compounding one nudge onto the last.
            if (!track.lapseBaseline || track.lapseBaseline.length !== track.cues.length) {
                track.lapseBaseline = [];
                for (var b = 0; b < track.cues.length; b++) {
                    track.lapseBaseline.push([track.cues[b].startTime, track.cues[b].endTime]);
                }
            }

            for (var c = 0; c < track.cues.length; c++) {
                var base = track.lapseBaseline[c];

                try {
                    track.cues[c].startTime = Math.max(0, base[0] + delaySeconds);
                    track.cues[c].endTime = Math.max(0, base[1] + delaySeconds);
                } catch (err) {
                    // a cue that is on screen right now can refuse to be moved
                }
            }

            moved = true;
        }

        if (moved) {
            fallbackDelaySeconds = delaySeconds;
        }

        return moved;
    }

    var PANEL_ID = 'lapsePlayerPanel';
    var panelContext = null;
    var panelDelay = 0;

    function panelIsOpen() {
        return !!document.getElementById(PANEL_ID);
    }

    var panelWatchdog = null;

    function closePlayerPanel() {
        var panel = document.getElementById(PANEL_ID);
        if (panel) {
            panel.remove();
        }

        document.removeEventListener('keydown', panelKeyHandler, true);

        if (panelWatchdog) {
            clearInterval(panelWatchdog);
            panelWatchdog = null;
        }
    }

    // Capture, so this runs before the client's own shortcut handling. Without it, typing
    // in the panel plays and pauses the film and the arrow keys seek it.
    function panelKeyHandler(e) {
        var panel = document.getElementById(PANEL_ID);
        if (!panel) {
            return;
        }

        if (e.key === 'Escape') {
            closePlayerPanel();
            e.stopPropagation();
            e.preventDefault();
            return;
        }

        if (panel.contains(e.target)) {
            e.stopPropagation();
        }
    }

    // close the panel when playback ends, otherwise it floats over whatever page comes next
    function watchForPlaybackEnding() {
        if (panelWatchdog) {
            clearInterval(panelWatchdog);
        }

        panelWatchdog = setInterval(function () {
            if (panelIsOpen() && !document.querySelector('video')) {
                closePlayerPanel();
            }
        }, 1000);
    }

    function openPlayerPanel() {
        if (panelIsOpen()) {
            closePlayerPanel();
            return;
        }

        var panel = document.createElement('div');
        panel.id = PANEL_ID;
        panel.className = 'lapseTools';
        panel.innerHTML =
            '<div class="lapseToolsHead">' +
            '  <span class="lapseToolsTitleText">Subtitles</span>' +
            '  <button type="button" class="lapseToolsClose" id="lapseToolsClose" aria-label="Close">&times;</button>' +
            '</div>' +
            '<div class="lapseToolsBody" id="lapseToolsBody">' +
            '  <div class="lapseToolsLoading">Reading what is playing...</div>' +
            '</div>';

        document.body.appendChild(panel);
        document.addEventListener('keydown', panelKeyHandler, true);
        watchForPlaybackEnding();

        panel.querySelector('#lapseToolsClose').addEventListener('click', closePlayerPanel);

        loadPanel();
    }

    function loadPanel() {
        lapseGet('Lapse/Player/Context').then(function (context) {
            panelContext = context;

            if (!context || !context.Playing) {
                setPanelBody('<div class="lapseToolsNote">Nothing is playing that LAPSE can work on. ' +
                    'Start a film or an episode and open this again.</div>');
                return;
            }

            panelDelay = readCurrentDelay();
            renderPanel();
        }).catch(function (err) {
            setPanelBody('<div class="lapseToolsNote">Could not read what is playing: ' +
                escapeHtml(err.message) + '</div>');
        });
    }

    function setPanelBody(html) {
        var body = document.getElementById('lapseToolsBody');
        if (body) {
            body.innerHTML = html;
        }
    }

    function formatDelay(seconds) {
        var value = (Math.round(seconds * 1000) / 1000).toFixed(3);
        return (seconds > 0 ? '+' : '') + value + 's';
    }

    function renderPanel() {
        var context = panelContext;
        var playing = context.PlayingPath;
        var reachable = !!playbackManager();

        // Someone without subtitle access can still move the subtitles for themselves and
        // change how they look; writing to the files is what needs the permission.
        var canEdit = context.CanEdit !== false;

        setPanelBody(
            '<div class="lapseToolsItem">' + escapeHtml(context.ItemName || '') + '</div>' +
            delaySectionHtml(canEdit ? playing : null, reachable, canEdit) +
            (canEdit ? syncSectionHtml(context, playing) : '') +
            tracksSectionHtml(context, playing, canEdit) +
            appearanceSectionHtml());

        wireDelaySection();
        wireSyncSection();
        wireTracksSection();
        wireAppearanceSection();
    }

    function delaySectionHtml(playing, reachable, canEdit) {
        var note = reachable
            ? 'Moves the subtitles while the film keeps playing, so you can see when it lines up.'
            : 'The player would not let LAPSE in, so this falls back to moving the cues directly. ' +
              'That works for plain text subtitles and not for ASS tracks.';

        return '<div class="lapseToolsSection">' +
            '  <div class="lapseToolsLabel">Delay</div>' +
            '  <div class="lapseToolsNudge">' +
            '    <button type="button" data-nudge="-1">-1s</button>' +
            '    <button type="button" data-nudge="-0.1">-100</button>' +
            '    <button type="button" data-nudge="-0.025">-25</button>' +
            '    <span class="lapseToolsValue" id="lapseDelayValue">' + formatDelay(panelDelay) + '</span>' +
            '    <button type="button" data-nudge="0.025">+25</button>' +
            '    <button type="button" data-nudge="0.1">+100</button>' +
            '    <button type="button" data-nudge="1">+1s</button>' +
            '  </div>' +
            '  <input type="range" class="lapseToolsSlider" id="lapseDelaySlider" min="-30" max="30" step="0.025" value="' + panelDelay + '" />' +
            '  <div class="lapseToolsHint">' + note + ' Positive means the subtitles show up later.</div>' +
            '  <div class="lapseToolsButtons">' +
            '    <button type="button" class="lapseToolsButton" id="lapseDelayReset">Reset</button>' +
            '    <button type="button" class="lapseToolsButton lapseToolsButton-primary" id="lapseDelaySave"' +
            (playing ? '' : ' disabled') + '>Save to file</button>' +
            '  </div>' +
            '  <div class="lapseToolsHint">' +
            (!canEdit
                ? 'This moves the subtitles for you while you watch. Saving the delay into the file needs ' +
                  'subtitle access, which an admin can give you in the LAPSE settings.'
                : playing
                ? 'Saving writes the delay into ' + escapeHtml(baseName(playing)) +
                  ' so it is still right next time. Where it lands follows your File output setting.'
                : 'The subtitle on screen is not a file LAPSE can edit, so it can only be moved for this session. ' +
                  'A track still inside the video or a burnt-in one cannot be saved.') +
            '  </div>' +
            '  <div class="lapseToolsResult" id="lapseDelayResult"></div>' +
            '</div>';
    }

    function wireDelaySection() {
        var slider = document.getElementById('lapseDelaySlider');
        var value = document.getElementById('lapseDelayValue');

        function setDelay(next) {
            // Matches Jellyfin's own native Subtitle Offset slider range, so a delay set
            // there and read back here doesn't get clamped to something smaller.
            panelDelay = Math.max(-30, Math.min(30, Math.round(next * 1000) / 1000));
            slider.value = panelDelay;
            value.textContent = formatDelay(panelDelay);
            applyDelay(panelDelay);
        }

        slider.addEventListener('input', function () {
            setDelay(parseFloat(slider.value) || 0);
        });

        document.querySelectorAll('#' + PANEL_ID + ' [data-nudge]').forEach(function (button) {
            button.addEventListener('click', function () {
                setDelay(panelDelay + parseFloat(button.getAttribute('data-nudge')));
            });
        });

        document.getElementById('lapseDelayReset').addEventListener('click', function () {
            setDelay(0);
        });

        var save = document.getElementById('lapseDelaySave');
        if (save && !save.disabled) {
            save.addEventListener('click', saveDelayToFile);
        }
    }

    function saveDelayToFile() {
        var result = document.getElementById('lapseDelayResult');

        if (!panelDelay) {
            result.textContent = 'The delay is zero, so there is nothing to save.';
            return;
        }

        var offsetMs = delayToShiftMs(panelDelay);
        result.textContent = 'Writing ' + offsetMs + 'ms into the file...';

        lapsePost('Lapse/Shift', {
            ItemId: panelContext.ItemId,
            SubtitlePath: panelContext.PlayingPath,
            OffsetMs: offsetMs
        }).then(function (shift) {
            var moved = shift && shift.Shifted ? shift.Shifted + ' timings moved. ' : '';
            var written = shift && shift.OutputPath;

            // With a "new file" output mode the delay lands in a new file and the one on screen still has the
            // old timings, so keep the live delay until the new file is the one playing.
            if (written && written !== panelContext.PlayingPath) {
                result.textContent = 'Saved into ' + baseName(written) + '. ' + moved +
                    'It becomes a track of its own once Jellyfin picks it up; until you switch to it, ' +
                    'the live delay stays on so what you are watching stays in sync.';
                return;
            }

            // The file now carries the delay, so the live one has to come off or the
            // subtitle would be moved twice over.
            panelDelay = 0;
            applyDelay(0);

            var slider = document.getElementById('lapseDelaySlider');
            var value = document.getElementById('lapseDelayValue');
            if (slider) { slider.value = 0; }
            if (value) { value.textContent = formatDelay(0); }

            result.textContent = 'Saved. ' + moved +
                'The live delay is back to zero because the file carries it now.';
        }).catch(function (err) {
            result.textContent = 'Could not save it: ' + err.message;
        });
    }

    function syncSectionHtml(context, playing) {
        if (!context.CanSync) {
            return '<div class="lapseToolsSection">' +
                '  <div class="lapseToolsLabel">Sync</div>' +
                '  <div class="lapseToolsHint">No engine is installed, so there is nothing to sync with. ' +
                'An admin can install one from the LAPSE dashboard.</div>' +
                '</div>';
        }

        return '<div class="lapseToolsSection">' +
            '  <div class="lapseToolsLabel">Sync</div>' +
            '  <div class="lapseToolsHint">Lines the subtitle up against the speech in the audio. ' +
            'Takes a moment, and the film keeps playing while it runs.</div>' +
            '  <div class="lapseToolsButtons">' +
            '    <button type="button" class="lapseToolsButton lapseToolsButton-primary" id="lapseToolsSync"' +
            (playing ? '' : ' disabled') + '>Sync now</button>' +
            '    <button type="button" class="lapseToolsButton" id="lapseToolsAdvanced">Advanced</button>' +
            '  </div>' +
            (playing ? '' : '<div class="lapseToolsHint">The subtitle on screen is not a file, so there is nothing to sync.</div>') +
            '  <div class="lapseToolsResult" id="lapseSyncResult"></div>' +
            '</div>';
    }

    function wireSyncSection() {
        var sync = document.getElementById('lapseToolsSync');
        var advanced = document.getElementById('lapseToolsAdvanced');

        if (sync && !sync.disabled) {
            sync.addEventListener('click', function () {
                var result = document.getElementById('lapseSyncResult');
                result.textContent = 'Syncing...';
                sync.disabled = true;

                lapsePost('Lapse/Sync', {
                    ItemId: panelContext.ItemId,
                    SubtitlePath: panelContext.PlayingPath
                }).then(function (outcome) {
                    result.textContent = describeSyncOutcome(outcome);
                    sync.disabled = false;
                    loadPanel();
                }).catch(function (err) {
                    result.textContent = 'Sync failed: ' + err.message;
                    sync.disabled = false;
                });
            });
        }

        if (advanced) {
            advanced.addEventListener('click', function () {
                closePlayerPanel();
                openAdvancedDialog({ id: panelContext.ItemId, name: panelContext.ItemName, type: 'Movie' });
            });
        }
    }

    function tracksSectionHtml(context, playing, canEdit) {
        var subtitles = context.Subtitles || [];

        var rows = subtitles.map(function (s) {
            var isPlaying = playing && s.Path === playing;
            var switchable = s.StreamIndex !== null && s.StreamIndex !== undefined;

            return '<div class="lapseToolsTrack' + (isPlaying ? ' lapseToolsTrack-on' : '') + '">' +
                '  <div class="lapseToolsTrackName">' + escapeHtml(s.DisplayName || baseName(s.Path)) +
                (isPlaying ? ' <span class="lapseToolsTag">playing</span>' : '') + '</div>' +
                (switchable
                    ? '  <button type="button" class="lapseToolsButton lapseToolsButton-small lapseTrackPick"' +
                      ' data-index="' + s.StreamIndex + '"' + (isPlaying ? ' disabled' : '') + '>Use</button>'
                    : '  <span class="lapseToolsTag lapseToolsTag-quiet">not loaded</span>') +
                '</div>';
        }).join('');

        var anyMissing = subtitles.some(function (s) {
            return s.StreamIndex === null || s.StreamIndex === undefined;
        });

        return '<div class="lapseToolsSection">' +
            '  <div class="lapseToolsLabel">Subtitles on this item</div>' +
            (rows || '<div class="lapseToolsHint">LAPSE cannot see any subtitle files for this item.</div>') +
            (anyMissing && canEdit
                ? '<div class="lapseToolsHint">"Not loaded" means the file is on disk but the player has not ' +
                  'been told about it yet, usually because it was written after the last scan. Picking it up ' +
                  'takes a moment, and then it appears in the normal subtitle list.</div>' +
                  '<div class="lapseToolsButtons"><button type="button" class="lapseToolsButton" id="lapseTrackRescan">' +
                  'Pick up new files</button></div>'
                : '') +
            (context.CanFetch && canEdit
                ? '<div class="lapseToolsButtons"><button type="button" class="lapseToolsButton" id="lapseTrackFetch">' +
                  'Find one online</button></div>'
                : '') +
            '  <div class="lapseToolsResult" id="lapseTrackResult"></div>' +
            '</div>';
    }

    function wireTracksSection() {
        document.querySelectorAll('#' + PANEL_ID + ' .lapseTrackPick').forEach(function (button) {
            button.addEventListener('click', function () {
                var result = document.getElementById('lapseTrackResult');
                var manager = playbackManager();

                if (!manager || !manager.getCurrentPlayer || !manager.getCurrentPlayer()
                    || typeof manager.setSubtitleStreamIndex !== 'function') {
                    result.textContent = 'LAPSE cannot switch the track from here. Use the normal subtitle list instead.';
                    return;
                }

                try {
                    // no player passed here either, see applyDelay
                    manager.setSubtitleStreamIndex(parseInt(button.getAttribute('data-index'), 10));

                    // Switching tracks clears the player's offset, so the panel has to
                    // stop claiming a delay that is no longer applied.
                    panelDelay = 0;
                    fallbackDelaySeconds = 0;
                    result.textContent = 'Switched. The delay is back to zero for the new track.';
                    setTimeout(loadPanel, 400);
                } catch (err) {
                    result.textContent = 'Could not switch to it: ' + err;
                }
            });
        });

        var rescan = document.getElementById('lapseTrackRescan');
        if (rescan) {
            rescan.addEventListener('click', function () {
                var result = document.getElementById('lapseTrackResult');
                result.textContent = 'Asking Jellyfin to look again...';

                lapsePost('Lapse/Items/' + panelContext.ItemId + '/Rescan').then(function () {
                    result.textContent = 'Asked. Give it a few seconds, then reopen this panel.';
                }).catch(function (err) {
                    result.textContent = 'Could not ask for a rescan: ' + err.message;
                });
            });
        }

        var fetchButton = document.getElementById('lapseTrackFetch');
        if (fetchButton) {
            fetchButton.addEventListener('click', function () {
                openOnlineSubtitlePicker(panelContext.ItemId, panelContext.ItemName, function (outcome) {
                    var result = document.getElementById('lapseTrackResult');
                    if (result) {
                        result.textContent = outcome;
                    }

                    setTimeout(loadPanel, 1500);
                });
            });
        }
    }

    // --- "Find Subtitles Online" ---

    // OpenSubtitles results to pick from. Radio rows because this gets used with a TV remote a lot.
    function openOnlineSubtitlePicker(itemId, name, done) {
        var language = (accessInfo && accessInfo.OpenSubtitlesLanguage) || '';

        var overlay = openOverlay(
            '<h3>Find Subtitles Online</h3>' +
            '<div class="fieldDescription">Searches OpenSubtitles for ' + escapeHtml(name || 'this item') +
            ', by the file itself first and then by its title. Downloads come out of the account\'s daily allowance.</div>' +
            '<div class="inputContainer">' +
            '  <label class="inputLabel inputLabelUnfocused" for="lapseOsLanguage">Languages, most wanted first</label>' +
            '  <input is="emby-input" id="lapseOsLanguage" type="text" value="' + escapeHtml(language) + '" placeholder="en" />' +
            '</div>' +
            '<div class="lapseDialogButtons">' +
            '  <button is="emby-button" type="button" class="raised" id="lapseOsSearch"><span>Search</span></button>' +
            '</div>' +
            '<div id="lapseOsResults" class="fieldDescription">Searching...</div>' +
            '<label class="emby-checkbox-label" id="lapseOsSyncRow" style="display:none">' +
            '  <input id="lapseOsSync" type="checkbox" is="emby-checkbox" checked />' +
            '  <span>Sync it to this video straight after</span>' +
            '</label>' +
            '<div class="lapseDialogButtons">' +
            '  <button is="emby-button" type="button" class="raised" id="lapseOsCancel"><span>Close</span></button>' +
            '  <button is="emby-button" type="button" class="raised button-submit" id="lapseOsGet" disabled><span>Download</span></button>' +
            '</div>', true);

        var results = overlay.querySelector('#lapseOsResults');
        var getButton = overlay.querySelector('#lapseOsGet');
        var found = [];

        function search() {
            var wanted = overlay.querySelector('#lapseOsLanguage').value.trim();
            results.textContent = 'Searching...';
            getButton.disabled = true;

            lapseGet('Lapse/Items/' + itemId + '/OpenSubtitles' + (wanted ? '?language=' + encodeURIComponent(wanted) : ''))
                .then(function (answer) {
                    found = (answer && answer.Candidates) || [];

                    if (answer && answer.Error) {
                        results.textContent = answer.Error;
                        return;
                    }

                    if (found.length === 0) {
                        results.textContent = 'OpenSubtitles has nothing for this in ' + (wanted || 'that language') + '.';
                        return;
                    }

                    results.innerHTML = '<div>Found by ' + escapeHtml(answer.SearchedBy || 'search') + '.</div>' +
                        found.map(function (c, i) {
                            var tags = [];
                            if (c.HashMatch) { tags.push('made for this file'); }
                            if (c.HearingImpaired) { tags.push('hearing impaired'); }
                            if (c.ForeignPartsOnly) { tags.push('forced'); }
                            if (c.Translated) { tags.push('machine translated'); }
                            if (c.FromTrusted) { tags.push('trusted uploader'); }
                            tags.push(c.DownloadCount + ' downloads');

                            return '<label class="lapseCandidateRow">' +
                                '<input type="radio" name="lapseOsPick" value="' + i + '"' + (i === 0 ? ' checked' : '') + ' />' +
                                '<div><div>' + escapeHtml(c.Language.toUpperCase()) + ' &middot; ' +
                                escapeHtml(c.Release || c.FileName || 'untitled') + '</div>' +
                                '<div class="fieldDescription">' + escapeHtml((c.Title ? c.Title + ', ' : '') + tags.join(', ')) + '</div></div>' +
                                '</label>';
                        }).join('');

                    overlay.querySelector('#lapseOsSyncRow').style.display = '';
                    getButton.disabled = false;
                }).catch(function (err) {
                    results.textContent = 'Could not search: ' + err.message;
                });
        }

        overlay.querySelector('#lapseOsSearch').addEventListener('click', search);
        overlay.querySelector('#lapseOsCancel').addEventListener('click', function () {
            overlay.remove();
        });

        getButton.addEventListener('click', function () {
            var picked = overlay.querySelector('input[name="lapseOsPick"]:checked');
            var candidate = picked ? found[parseInt(picked.value, 10)] : null;
            if (!candidate) {
                return;
            }

            var sync = overlay.querySelector('#lapseOsSync').checked;
            overlay.remove();
            showLapseToast(sync ? 'Downloading and syncing...' : 'Downloading...');

            lapsePost('Lapse/Items/' + itemId + '/OpenSubtitles/Download', {
                FileId: candidate.FileId,
                Language: candidate.Language,
                FileName: candidate.FileName,
                HearingImpaired: candidate.HearingImpaired,
                ForeignPartsOnly: candidate.ForeignPartsOnly,
                Sync: sync
            }).then(function (outcome) {
                var message = (outcome && outcome.Message) || 'Done.';

                if (outcome && outcome.Success && outcome.Sync) {
                    message += ' ' + describeSyncOutcome(outcome.Sync);
                }

                showLapseToast(message);

                if (done) {
                    done(message);
                }
            }).catch(function (err) {
                showLapseToast('Could not download it: ' + err.message);
            });
        });

        search();
    }

    function appearanceSectionHtml() {
        var a = appearanceSettings || {};

        return '<div class="lapseToolsSection">' +
            '  <div class="lapseToolsLabel">Appearance</div>' +
            '  <label class="lapseToolsCheck">' +
            '    <input type="checkbox" id="lapseLookEnabled"' + (a.Enabled ? ' checked' : '') + ' />' +
            '    <span>Let LAPSE style the subtitles</span>' +
            '  </label>' +
            '  <div class="lapseToolsRow"><span>Size</span>' +
            '    <input type="range" id="lapseLookSize" min="16" max="120" step="1" value="' + (a.FontSizePx || 48) + '" />' +
            '    <span class="lapseToolsValue" id="lapseLookSizeValue">' + (a.FontSizePx || 48) + '</span></div>' +
            '  <div class="lapseToolsRow"><span>Height</span>' +
            '    <input type="range" id="lapseLookPosition" min="0" max="85" step="1" value="' + (a.VerticalPositionPercent || 0) + '" />' +
            '    <span class="lapseToolsValue" id="lapseLookPositionValue">' + (a.VerticalPositionPercent || 0) + '%</span></div>' +
            '  <div class="lapseToolsHint">Height lifts the subtitles off the bottom of the picture. ' +
            'It does not move an ASS track, which carries its own positioning.</div>' +
            '  <div class="lapseToolsRow"><span>Text</span>' +
            '    <input type="color" id="lapseLookColor" value="' + (a.TextColor || '#FFFFFF') + '" /></div>' +
            '  <label class="lapseToolsCheck">' +
            '    <input type="checkbox" id="lapseLookBackground"' + (a.BackgroundEnabled ? ' checked' : '') + ' />' +
            '    <span>Box behind the text</span>' +
            '  </label>' +
            '  <div class="lapseToolsHint">Saved against your account, so it follows you to the TV and the ' +
            'phone and changes nothing for anyone else.</div>' +
            '  <div class="lapseToolsButtons">' +
            '    <button type="button" class="lapseToolsButton" id="lapseLookReset">Back to the default</button>' +
            '    <button type="button" class="lapseToolsButton lapseToolsButton-primary" id="lapseLookSave">Save</button>' +
            '  </div>' +
            '  <div class="lapseToolsButtons">' +
            '    <button type="button" class="lapseToolsButton" id="lapseLookMore">Font and spacing</button>' +
            '  </div>' +
            '  <div class="lapseToolsResult" id="lapseLookResult"></div>' +
            '</div>';
    }

    function wireAppearanceSection() {
        var size = document.getElementById('lapseLookSize');
        var position = document.getElementById('lapseLookPosition');

        // Everything previews straight away. The whole reason this is in the player is so
        // you can see the change on the picture rather than imagining it.
        function preview() {
            appearanceSettings = collectPanelAppearance();
            applySubtitleAppearance();
        }

        size.addEventListener('input', function () {
            document.getElementById('lapseLookSizeValue').textContent = size.value;
            preview();
        });

        position.addEventListener('input', function () {
            document.getElementById('lapseLookPositionValue').textContent = position.value + '%';
            preview();
        });

        document.getElementById('lapseLookColor').addEventListener('input', preview);
        document.getElementById('lapseLookEnabled').addEventListener('change', preview);
        document.getElementById('lapseLookBackground').addEventListener('change', preview);

        document.getElementById('lapseLookSave').addEventListener('click', function () {
            var result = document.getElementById('lapseLookResult');
            result.textContent = 'Saving...';

            saveMyAppearance(collectPanelAppearance(), 'Subtitle look saved against your account.').then(function (saved) {
                if (result.isConnected) {
                    result.textContent = saved ? 'Saved.' : 'Could not save it.';
                }
            });
        });

        // font and letter spacing live in the bigger dialog, set once and left alone
        document.getElementById('lapseLookMore').addEventListener('click', function () {
            closePlayerPanel();
            openSubtitleSettingsDialog();
        });

        document.getElementById('lapseLookReset').addEventListener('click', function () {
            var result = document.getElementById('lapseLookResult');
            result.textContent = 'Putting it back...';

            lapsePost('Lapse/Appearance/Mine/Reset').then(function (appearance) {
                appearanceSettings = appearance;
                applySubtitleAppearance();
                renderPanel();
            }).catch(function (err) {
                result.textContent = 'Could not reset it: ' + err.message;
            });
        });
    }

    function collectPanelAppearance() {
        var current = appearanceSettings || {};

        return {
            Enabled: document.getElementById('lapseLookEnabled').checked,
            FontSizePx: parseInt(document.getElementById('lapseLookSize').value, 10) || 48,
            VerticalPositionPercent: parseInt(document.getElementById('lapseLookPosition').value, 10) || 0,
            TextColor: document.getElementById('lapseLookColor').value.toUpperCase(),
            BackgroundEnabled: document.getElementById('lapseLookBackground').checked,

            // Not on the panel: too fiddly for a remote, and already in the dashboard.
            BackgroundColor: current.BackgroundColor || '#00000080',
            FontFamily: current.FontFamily || null,
            LetterSpacingPx: current.LetterSpacingPx || 0
        };
    }

    function addSubtitleSettingsButton(sheet) {
        var scroller = sheet.querySelector('.actionSheetScroller') || sheet;

        // first in the list, people open this menu because the subtitles look wrong
        var entry = makeMenuButton('lapse-player-panel', 'Subtitle tools', 'tune', function () {
            openPlayerPanel();
        });

        if (scroller.firstChild) {
            scroller.insertBefore(entry, scroller.firstChild);
        } else {
            scroller.appendChild(entry);
        }

        log('added the subtitle tools entry to the player menu');

        addKeepCandidateButton(sheet, scroller);
        keepSheetOnScreen(sheet);
    }

    // Jellyfin sizes the sheet before we add entries, so they can run off screen. Some sheets have
    // an inline top, others (like the CC track picker) are centred dialogs with none, so nudging
    // the position only helps some of them.
    //
    // so cap the height and scroll, which works however it's positioned. The nudge stays as a second
    // step for anchored sheets where it looks nicer.
    function keepSheetOnScreen(sheet) {
        // Let the browser lay the new entries out before measuring.
        requestAnimationFrame(function () {
            if (!document.body.contains(sheet)) {
                return;
            }

            var margin = 8;

            // The CC sheet sits mid-window (positionTo centres near the button), so a budget from the window
            // height is too generous and never kicks in. Measure from this sheet's own top.
            var box = sheet.getBoundingClientRect();
            var budget = window.innerHeight - box.top - margin;

            // if it sits so low there's barely room, pull it up first
            var minBudget = 220;
            if (budget < minBudget) {
                var top = parseFloat(sheet.style.top);
                if (!isNaN(top)) {
                    var shift = Math.min(top - margin, minBudget - budget);
                    if (shift > 0) {
                        sheet.style.top = (top - shift) + 'px';
                        box = sheet.getBoundingClientRect();
                        budget = window.innerHeight - box.top - margin;
                        log('moved the action sheet up ' + Math.round(shift) + 'px to make room');
                    }
                }
            }

            var scroller = sheet.querySelector('.actionSheetScroller');

            if (scroller && scroller !== sheet) {
                // cap the inner scroller too, or the header and buttons get pushed off instead
                var chrome = box.height - scroller.getBoundingClientRect().height;
                sheet.style.maxHeight = budget + 'px';
                sheet.style.overflowY = 'hidden';
                scroller.style.maxHeight = Math.max(80, budget - chrome) + 'px';
                scroller.style.overflowY = 'auto';
            } else {
                sheet.style.maxHeight = budget + 'px';
                sheet.style.overflowY = 'auto';
            }
        });
    }

    // Multi engine answers get compared by switching tracks in the player, so "keep this one" lives
    // in the track list. The server works out which track is on from the session.
    function addKeepCandidateButton(sheet, scroller) {
        lapseGet('Lapse/Candidates/Playing').then(function (status) {
            if (!status || !status.Playing || !status.Sets || !status.Sets.length) {
                return;
            }

            if (!document.body.contains(sheet) || scroller.querySelector('.lapseKeepButton')) {
                return;
            }

            var button;

            if (status.IsCandidate) {
                button = makeMenuButton(
                    'lapse-keep-subtitle',
                    'Keep this subtitle (' + status.EngineName + ')',
                    'check_circle',
                    function () {
                        keepPlayingSubtitle(status.ItemId);
                    });
            } else {
                // They are on the original, or on some other track. Pointing at the picker
                // is more use than a button that would refuse.
                button = makeMenuButton(
                    'lapse-choose-subtitle',
                    'Choose between the synced subtitles',
                    'compare_arrows',
                    function () {
                        openCandidatePicker(status.ItemId);
                    });
            }

            button.classList.add('lapseKeepButton');
            scroller.appendChild(button);
            keepSheetOnScreen(sheet);
            log('added the multi engine entry to the player subtitle menu');
        }).catch(function (err) {
            log('could not check for waiting subtitles: ' + err);
        });
    }

    function keepPlayingSubtitle(itemId) {
        showLapseToast('Keeping this one...');

        lapsePost('Lapse/Candidates/Keep', { ItemId: itemId }).then(function (decision) {
            showLapseToast((decision && decision.Message) || 'Kept it.', true);
        }).catch(function (err) {
            showLapseToast('Could not keep it: ' + err.message, true);
        });
    }

    // The way in when nobody is watching, or when the track on screen isn't one of the
    // answers. Same decision, just made from a list instead of from the picture.
    function openCandidatePicker(itemId) {
        lapseGet('Lapse/Items/' + itemId + '/Candidates').then(function (status) {
            var sets = (status && status.Sets) || [];

            if (!sets.length) {
                showLapseToast('There are no synced subtitles waiting to be chosen for this item.');
                return;
            }

            showCandidateDialog(itemId, sets, status.PlayingPath);
        }).catch(function (err) {
            showLapseToast('Could not read the waiting subtitles: ' + err.message);
        });
    }

    function showCandidateDialog(itemId, sets, playingPath) {
        var body = sets.map(function (set) {
            var options = (set.Candidates || []).map(function (c) {
                var playing = playingPath && c.Path === playingPath ? ' (playing now)' : '';
                return '<label class="lapseCandidateRow">' +
                    '<input type="radio" name="lapseCandidate" value="' + escapeHtml(c.Path) + '" />' +
                    '<span><strong>' + escapeHtml(c.EngineName) + '</strong>' + playing +
                    '<br /><span class="fieldDescription">' + escapeHtml(c.Detail || 'no details') +
                    '</span></span></label>';
            }).join('');

            return '<div class="fieldDescription">' + escapeHtml(baseName(set.OriginalPath)) +
                ' has not been touched. Pick the answer that lines up:</div>' + options;
        }).join('');

        var overlay = openOverlay(
            '<h3>Choose the right subtitle</h3>' +
            '<div class="fieldDescription">Play the item and switch between these tracks to compare them. ' +
            'Keeping one writes it out using your File output setting and deletes the rest.</div>' +
            body +
            '<div class="lapseDialogButtons">' +
            '  <button is="emby-button" type="button" class="raised" id="lapseCandidateCancel"><span>Close</span></button>' +
            '  <button is="emby-button" type="button" class="raised" id="lapseCandidateDiscard"><span>Throw all away</span></button>' +
            '  <button is="emby-button" type="button" class="raised button-submit" id="lapseCandidateKeep"><span>Keep</span></button>' +
            '</div>',
            true);

        overlay.querySelector('#lapseCandidateCancel').addEventListener('click', function () {
            overlay.remove();
        });

        overlay.querySelector('#lapseCandidateDiscard').addEventListener('click', function () {
            overlay.remove();
            showLapseToast('Removing them...');

            lapsePost('Lapse/Candidates/Discard', { ItemId: itemId }).then(function (decision) {
                showLapseToast((decision && decision.Message) || 'Removed.', true);
            }).catch(function (err) {
                showLapseToast('Could not remove them: ' + err.message, true);
            });
        });

        overlay.querySelector('#lapseCandidateKeep').addEventListener('click', function () {
            var picked = overlay.querySelector('input[name="lapseCandidate"]:checked');

            if (!picked) {
                showLapseToast('Pick one of them first.');
                return;
            }

            overlay.remove();
            showLapseToast('Keeping it...');

            lapsePost('Lapse/Candidates/Keep', { ItemId: itemId, Path: picked.value }).then(function (decision) {
                showLapseToast((decision && decision.Message) || 'Kept it.', true);
            }).catch(function (err) {
                showLapseToast('Could not keep it: ' + err.message, true);
            });
        });
    }

    function baseName(path) {
        if (!path) {
            return '';
        }

        var parts = path.split(/[\\/]/);
        return parts[parts.length - 1];
    }

    function openSubtitleSettingsDialog() {
        Promise.all([
            lapseGet('Lapse/Appearance'),
            loadInstalledFonts()
        ]).then(function (results) {
            showSubtitleSettingsDialog(results[0] || {}, results[1] || { Fonts: [] });
        }).catch(function (err) {
            showLapseToast('Could not read the subtitle settings: ' + err.message);
        });
    }

    function fontOptionsHtml(status, chosen) {
        var families = [];

        (status.Fonts || []).forEach(function (file) {
            var family = familyNameFor(file);
            if (families.indexOf(family) === -1) {
                families.push(family);
            }
        });

        // Whatever is already set stays in the list even if the font has since been
        // removed from the server, so opening the dialog can't silently change it.
        if (chosen && families.indexOf(chosen) === -1) {
            families.push(chosen);
        }

        var options = '<option value="">The client\'s own font</option>';

        families.forEach(function (family) {
            options += '<option value="' + escapeHtml(family) + '"' +
                (family === chosen ? ' selected' : '') + '>' + escapeHtml(family) + '</option>';
        });

        return options;
    }

    function showSubtitleSettingsDialog(appearance, fonts) {
        var size = appearance.FontSizePx || 48;
        var spacing = appearance.LetterSpacingPx || 0;
        var note = (fonts.Fonts || []).length
            ? ''
            : '<div class="fieldDescription">No font has been installed on this server yet, so the list only offers the ' +
              'client\'s own. An admin can add OpenDyslexic from the LAPSE dashboard under Subtitle appearance.</div>';

        var overlay = openOverlay(
            '<h3>Subtitle settings</h3>' +
            '<div class="fieldDescription">Yours alone. These follow your account onto every device you sign in on, ' +
            'and change nothing for anybody else using this server.</div>' +
            '<label class="emby-checkbox-label lapseStackedCheck">' +
            '  <input type="checkbox" is="emby-checkbox" id="lapseAppearanceEnabled"' + (appearance.Enabled ? ' checked' : '') + ' />' +
            '  <span>Style subtitles during playback</span>' +
            '</label>' +
            '<div class="selectContainer">' +
            '  <label class="selectLabel">Font</label>' +
            '  <select is="emby-select" id="lapseAppearanceFont" class="emby-select-withcolor emby-select">' +
            fontOptionsHtml(fonts, appearance.FontFamily || '') +
            '  </select>' +
            '</div>' +
            note +
            '<div class="inputContainer">' +
            '  <label class="inputLabel inputLabelUnfocused">Text size: <span id="lapseAppearanceSizeValue">' + size + 'px</span></label>' +
            '  <input type="range" id="lapseAppearanceSize" min="16" max="120" value="' + size + '" class="lapseRange" />' +
            '</div>' +
            '<div class="inputContainer">' +
            '  <label class="inputLabel inputLabelUnfocused">Letter spacing: <span id="lapseAppearanceSpacingValue">' + spacing + 'px</span></label>' +
            '  <input type="range" id="lapseAppearanceSpacing" min="0" max="10" step="0.5" value="' + spacing + '" class="lapseRange" />' +
            '</div>' +
            '<div class="inputContainer">' +
            '  <label class="inputLabel inputLabelUnfocused" for="lapseAppearanceColor">Text colour</label>' +
            '  <input type="color" id="lapseAppearanceColor" value="' + escapeHtml(appearance.TextColor || '#FFFFFF') + '" />' +
            '</div>' +
            '<label class="emby-checkbox-label lapseStackedCheck">' +
            '  <input type="checkbox" is="emby-checkbox" id="lapseAppearanceBackground"' +
            (appearance.BackgroundEnabled === false ? '' : ' checked') + ' />' +
            '  <span>Draw a box behind the text</span>' +
            '</label>' +
            '<div class="lapseDialogButtons">' +
            '  <button is="emby-button" type="button" class="raised" id="lapseAppearanceDyslexic"><span>Dyslexia-friendly</span></button>' +
            '  <button is="emby-button" type="button" class="raised" id="lapseAppearanceReset"><span>Reset</span></button>' +
            '  <button is="emby-button" type="button" class="raised button-submit" id="lapseAppearanceSave"><span>Save</span></button>' +
            '</div>');

        var sizeInput = overlay.querySelector('#lapseAppearanceSize');
        var spacingInput = overlay.querySelector('#lapseAppearanceSpacing');

        sizeInput.addEventListener('input', function () {
            overlay.querySelector('#lapseAppearanceSizeValue').textContent = sizeInput.value + 'px';
        });

        spacingInput.addEventListener('input', function () {
            overlay.querySelector('#lapseAppearanceSpacingValue').textContent = spacingInput.value + 'px';
        });

        function collect() {
            return {
                Enabled: overlay.querySelector('#lapseAppearanceEnabled').checked,
                FontSizePx: parseInt(sizeInput.value, 10),
                LetterSpacingPx: parseFloat(spacingInput.value),
                FontFamily: overlay.querySelector('#lapseAppearanceFont').value,
                TextColor: overlay.querySelector('#lapseAppearanceColor').value,
                BackgroundColor: appearance.BackgroundColor || '#00000080',
                BackgroundEnabled: overlay.querySelector('#lapseAppearanceBackground').checked
            };
        }

        overlay.querySelector('#lapseAppearanceDyslexic').addEventListener('click', function () {
            overlay.remove();
            saveMyAppearance(DYSLEXIC_PRESET, 'Subtitles are now dyslexia-friendly, for you.');
        });

        overlay.querySelector('#lapseAppearanceReset').addEventListener('click', function () {
            overlay.remove();
            showLapseToast('Putting your subtitle settings back...');

            lapsePost('Lapse/Appearance/Mine/Reset').then(function (applied) {
                appearanceSettings = applied;
                applySubtitleAppearance();
                showLapseToast('Back to this server\'s own subtitle settings.');
            }).catch(function (err) {
                showLapseToast('Could not reset: ' + err.message);
            });
        });

        overlay.querySelector('#lapseAppearanceSave').addEventListener('click', function () {
            var wanted = collect();
            overlay.remove();
            saveMyAppearance(wanted, 'Subtitle settings saved.');
        });
    }

    // Resolves with true once it is saved, false when it wasn't.
    function saveMyAppearance(wanted, message) {
        return lapsePost('Lapse/Appearance/Mine', wanted).then(function (saved) {
            // Applied straight away rather than on the next page load, so the effect of a
            // change is visible on the film that is playing right now.
            appearanceSettings = saved;
            applySubtitleAppearance();
            showLapseToast(message);
            return true;
        }).catch(function (err) {
            showLapseToast('Could not save the subtitle settings: ' + err.message);
            return false;
        });
    }

    // --- "Readable Subtitles" ---
    //
    // Writes a copy with the font, size and spacing baked into the file, the only way to get a
    // dyslexia font on every device since most clients have no font picker.

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

    // A readable subtitle is recognisable by its name, which is what lets the dialog offer
    // to undo one without asking the server first.
    function isReadablePath(path) {
        return /\.readable\.ass$/i.test(String(path || ''));
    }

    function restyleSubtitleChecksHtml(subtitles) {
        return subtitles.map(function (s, index) {
            var readable = isReadablePath(s.Path);

            return '<label class="emby-checkbox-label lapseStackedCheck">' +
                '  <input type="checkbox" is="emby-checkbox" class="lapseRestyleTrack" ' +
                'value="' + escapeHtml(s.Path) + '"' + (index === 0 && !readable ? ' checked' : '') + ' />' +
                '  <span>' + escapeHtml(s.DisplayName) + (readable ? ' (already readable)' : '') + '</span>' +
                '</label>';
        }).join('');
    }

    function showRestyleDialog(context, subtitles, fonts) {
        var ready = fonts && fonts.DyslexicInstalled && fonts.FallbackFontEnabled;
        var anyReadable = subtitles.some(function (s) { return isReadablePath(s.Path); });

        var fontNote = ready
            ? '<div class="fieldDescription">OpenDyslexic is installed on this server, so the styled subtitle will render in it. ' +
              'A subtitle that isn\'t in the Latin alphabet - Arabic, Hebrew, Thai, Chinese - keeps the larger text, the ' +
              'heavier outline and the margin, but gets a font that has those letters in it, because OpenDyslexic hasn\'t.</div>'
            : '<div class="fieldDescription"><strong>The font isn\'t installed yet.</strong> The styled file will still be ' +
              'written, and the larger text and wider letter spacing will apply, but it will render in the player\'s ' +
              'normal font until an admin installs OpenDyslexic from the LAPSE dashboard under Subtitle appearance.</div>';

        var overlay = openOverlay(
            '<h3>Readable Subtitles</h3>' +
            '<div class="fieldDescription">Writes a copy of the subtitles you tick, with a dyslexia-friendly font, larger ' +
            'text and wider letter spacing set inside the file. Because the styling is in the file rather than in a client ' +
            'setting, every client that plays it honours it - phone, TV and browser alike, with nothing to set up on each one.</div>' +
            fontNote +
            '<div class="selectContainer"><label class="selectLabel">Subtitles</label></div>' +
            restyleSubtitleChecksHtml(subtitles) +
            '<div class="fieldDescription">Only the ones you tick are touched. The rest are left exactly as they are, which ' +
            'is what you want on a library other people read the other languages of.</div>' +
            '<div class="selectContainer">' +
            '  <label class="selectLabel">What to do with the original</label>' +
            '  <select is="emby-select" id="lapseRestyleMode" class="emby-select-withcolor emby-select">' +
            '    <option value="copy" selected>Keep it, and add the readable one beside it</option>' +
            '    <option value="replace">Replace it (the original is kept as a backup)</option>' +
            '  </select>' +
            '</div>' +
            '<div class="fieldDescription">Keeping it means both are offered as separate tracks in the player, so the styled ' +
            'one can be picked by whoever wants it and everyone else keeps the original. Either way this can be undone.</div>' +
            '<div class="lapseDialogButtons">' +
            '  <button is="emby-button" type="button" class="raised" id="lapseRestyleCancel"><span>Cancel</span></button>' +
            (anyReadable
                ? '  <button is="emby-button" type="button" class="raised" id="lapseRestyleRevert"><span>Back to normal</span></button>'
                : '') +
            '  <button is="emby-button" type="button" class="raised button-submit" id="lapseRestyleApply"><span>Write it</span></button>' +
            '</div>');

        overlay.querySelector('#lapseRestyleCancel').addEventListener('click', function () {
            overlay.remove();
        });

        if (anyReadable) {
            overlay.querySelector('#lapseRestyleRevert').addEventListener('click', function () {
                overlay.remove();
                revertReadable(context, null);
            });
        }

        overlay.querySelector('#lapseRestyleApply').addEventListener('click', function () {
            var picked = Array.prototype.slice
                .call(overlay.querySelectorAll('.lapseRestyleTrack:checked'))
                .map(function (box) { return box.value; });

            if (picked.length === 0) {
                showLapseToast('Tick at least one subtitle first.');
                return;
            }

            var replace = overlay.querySelector('#lapseRestyleMode').value === 'replace';

            overlay.remove();
            showLapseToast('Writing ' + (picked.length === 1 ? 'a readable copy' : picked.length + ' readable copies') + '...');

            lapsePost('Lapse/Restyle', {
                ItemId: context.id,
                SubtitlePaths: picked,
                ReplaceOriginal: replace
            }).then(function (response) {
                showLapseToast(describeRestyle(response));
            }).catch(function (err) {
                showLapseToast('Could not restyle: ' + err.message);
            });
        });
    }

    function describeRestyle(response) {
        var files = response.Files || [];
        var written = files.filter(function (f) { return f.Success; });
        var failed = files.filter(function (f) { return !f.Success; });

        if (written.length === 0) {
            return 'Nothing was written. ' + (failed[0] ? failed[0].Error : '');
        }

        var parts = [];

        written.forEach(function (file) {
            var line = file.OutputPath + ' (' + file.Cues + ' cues';

            // the ways a non-Latin subtitle differs from what was asked for, so nobody thinks the font setting is broken
            if (file.FontSwapped) {
                line += ', ' + file.Script + ', so it uses ' + file.FontName + ' instead';
            } else if (file.RightToLeft) {
                line += ', ' + file.Script + ', written right to left';
            }

            parts.push(line + ')');
        });

        var message = 'Wrote ' + parts.join('; ');

        if (written.some(function (f) { return !f.FontAvailable; })) {
            message += '. The font isn\'t installed on this server yet, so it will render in the player\'s normal one until it is';
        }

        if (failed.length) {
            message += '. ' + failed.length + ' could not be done: ' + failed[0].Error;
        }

        return message + '. Run a library scan to pick the new files up.';
    }

    function revertReadable(context, subtitlePath) {
        showLapseToast('Putting the subtitles back...');

        lapsePost('Lapse/Restyle/Revert', {
            ItemId: context.id,
            SubtitlePath: subtitlePath
        }).then(function (response) {
            var files = response.Files || [];
            var done = files.filter(function (f) { return f.Success; });

            if (done.length === 0) {
                showLapseToast('Nothing to put back' + (files[0] && files[0].Error ? ': ' + files[0].Error : '.'));
                return;
            }

            var restored = done.filter(function (f) { return f.RestoredPath; }).length;
            var message = 'Put ' + done.length + ' subtitle' + (done.length === 1 ? '' : 's') + ' back to normal';

            if (restored) {
                message += ', restoring ' + restored + ' original' + (restored === 1 ? '' : 's') + ' from backup';
            }

            showLapseToast(message + '. Run a library scan so the player stops offering the removed ones.');
        }).catch(function (err) {
            showLapseToast('Could not put them back: ' + err.message);
        });
    }

    // --- "Extract Embedded Subtitles" ---
    //
    // Extract embedded tracks. Removing them from the video rewrites a library file, so that's its
    // own checkbox.

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
            // slider to find it roughly, box to type it exactly, they move together
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
            // wide enough for a 29 char timing line without a scrollbar
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

            // keep the widths, ass uses centiseconds and players reject ms there
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

        // Stop button in the toast so a wrong job can be stopped from where it started
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

    // A normal dialog now (it used to load the whole dashboard in an iframe and navigate away).
    // Everything here is a plain API call.
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

    // Reads top to bottom: which subtitle, against what, how to write it, translate or not. All the
    // action buttons sit in the bottom row.
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

        // syncing to another subtitle is just a different reference, so it's a dropdown, not a second button
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

        // Overwrite on a track inside the mkv can't replace that track, say so up front
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
        document.addEventListener('click', rememberSubtitleMenuFromClick, true);
        startWatchingTheClient();

        // ApiClient isn't necessarily ready the instant the DOM is, and there's no event
        // for it, so give it a moment before the first authenticated call.
        setTimeout(refreshSubtitleAppearance, 2000);

        // SPA, page changes don't rerun this. Re-read the settings on navigation (throttled) so dashboard
        // changes show up without a reload.
        window.addEventListener('hashchange', refreshSubtitleAppearance);
        window.addEventListener('popstate', refreshSubtitleAppearance);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', start);
    } else {
        start();
    }
})();
