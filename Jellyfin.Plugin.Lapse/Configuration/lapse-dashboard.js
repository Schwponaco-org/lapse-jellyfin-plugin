// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

(function () {
    'use strict';

    var queuePollTimer = null;
    var queuePolling = false;
    var queueWasRunning = false;
    var allItems = [];
    var allEngines = [];
    var allLibraries = [];
    var allProviders = [];
    var allIgnoreRules = [];
    var currentSettings = null;
    var currentOverview = null;
    var currentDiagnostics = null;
    var currentFontStatus = null;
    var currentUsers = [];
    var currentReview = [];
    var ignoreSearchPending = false;
    var ignoreSearchFetched = false;

    // fallback text when there's no engine id to build the one-click prompt (confirmForceRetry) from
    var FORCE_HINT = 'If you are sure the subtitle belongs to this video, turn on ' +
        '"Sync even when the engine is unsure" under Settings, Engines, and run it again.';

    // Tests what's typed in, not only what's saved, so a key can be checked before it's
    // relied on. Blank boxes fall back to the saved values on the server.
    function testOpenSubtitles(view) {
        var result = view.querySelector('#lapseOpenSubtitlesTestResult');
        var button = view.querySelector('#btnTestOpenSubtitles');

        result.textContent = 'Asking OpenSubtitles...';
        button.disabled = true;

        lapsePost('Lapse/OpenSubtitles/Test', {
            ApiKey: view.querySelector('#lapseOpenSubtitlesApiKey').value || null,
            Username: view.querySelector('#lapseOpenSubtitlesUsername').value || null,
            Password: view.querySelector('#lapseOpenSubtitlesPassword').value || null
        }).then(function (status) {
            result.textContent = (status && status.Message) || 'No answer.';
        }).catch(function (err) {
            result.textContent = 'Could not test it: ' + err.message;
        }).then(function () {
            button.disabled = false;
        });
    }

    // Offer "sync even when unsure" right where the skip happened, it's the only way to get a doubtful result written.
    function confirmForceRetry(message, engineId, view, retry) {
        if (!engineId) {
            tell('Left the original alone', message + ' ' + FORCE_HINT);
            return;
        }

        confirmAction('Not sure enough to write it', message + ' Turn on "Sync even when the engine is unsure" and try again?',
            'Turn it on and retry', false).then(function (yes) {
            if (yes) {
                forceAndRetry(engineId, view, retry);
            }
        });
    }

    function forceAndRetry(engineId, view, retry) {
        lapsePost('Lapse/Engines/' + engineId + '/Parameter', { Key: 'force', Value: 'true' }).then(function () {
            if (view) {
                refreshEngines(view);
            }

            if (retry) {
                retry();
            }
        }).catch(function (err) {
            tell('Could not turn it on', err.message);
        });
    }

    var OUTPUT_MODES = [
        {
            value: 'SidecarOnly',
            label: 'Write a new file',
            note: 'The original stays as it is and the result becomes an extra subtitle track. Nothing can be lost.'
        },
        {
            value: 'SidecarWithBackup',
            label: 'Write a new file, keep a backup',
            note: 'The same, and an earlier result under that name is kept as a .bak.'
        },
        {
            value: 'OverwriteWithBackup',
            label: 'Overwrite, keep a backup',
            note: 'Replaces the subtitle and keeps the old one as a .bak beside it.'
        },
        {
            value: 'OverwriteNoBackup',
            label: 'Overwrite, no backup',
            note: 'Replaces the subtitle and keeps nothing.'
        }
    ];

    var CONVERSION_FORMATS = [
        {
            value: 'srt',
            label: '.srt',
            recommended: true,
            note: 'Read by everything, and the easiest to edit by hand.'
        },
        { value: 'vtt', label: '.vtt', note: 'What browsers use.' },
        { value: 'ass', label: '.ass', note: 'Carries position and style, where the original had any.' },
        { value: 'ssa', label: '.ssa', note: 'The older form of .ass, for players that need it.' }
    ];

    var CONVERSION_ORIGINAL_MODES = [
        {
            value: 'keep',
            label: 'Keep it beside the new one',
            recommended: true,
            note: 'Nothing is lost if the conversion is not what you wanted.'
        },
        {
            value: 'replace',
            label: 'Delete it once the new file is written',
            note: 'There is no undo for this.'
        }
    ];

    var CONVERSION_SYNC_MODES = [
        {
            value: 'sync',
            label: 'Convert it, then sync it',
            recommended: true,
            note: 'The conversion was only needed so the engine could read it.'
        },
        {
            value: 'convert',
            label: 'Just convert it',
            note: 'Stops after the conversion.'
        }
    ];

    var CONVERT_BEFORE_SYNC_MODES = [
        {
            value: 'off',
            label: 'Only when the engine cannot read the format',
            recommended: true,
            note: 'The file keeps the format it came in.'
        },
        {
            value: 'on',
            label: 'Always convert first',
            note: 'For one format across the library. The original is left where it is.'
        }
    ];

    var AUTOMATION_ACTIONS = [
        {
            value: 'Sync',
            label: 'Sync them',
            recommended: true,
            note: 'Leaves their format alone.'
        },
        {
            value: 'ConvertThenSync',
            label: 'Convert them, then sync them',
            note: 'Writes each one in the format set under Conversion first.'
        },
        {
            value: 'Convert',
            label: 'Only convert them',
            note: 'Nothing is synced, so items stay not synced.'
        }
    ];

    var ACCESS_MODES = [
        {
            value: 'AdminsOnly',
            label: 'Administrators only',
            note: 'Nobody else sees the LAPSE entries in an item\'s menu.'
        },
        {
            value: 'SubtitleManagers',
            label: 'Anyone allowed to manage subtitles',
            note: 'Users with Jellyfin\'s own permission to manage subtitles.'
        },
        {
            value: 'SelectedUsers',
            label: 'Only the users I pick',
            note: 'Pick them below.'
        },
        {
            value: 'Everyone',
            label: 'Every signed in user',
            note: 'Anyone with an account can rewrite subtitle files in your library.'
        }
    ];

    var LOW_CONFIDENCE_MODES = [
        {
            value: 'Sidecar',
            label: 'Write it beside the original for review',
            recommended: true,
            note: 'The original stays as it is until you keep or discard the result under Review.'
        },
        {
            value: 'KeepOriginal',
            label: 'Discard it',
            note: 'The file is left as it was.'
        },
        {
            value: 'OverwriteAnyway',
            label: 'Write it anyway',
            note: 'Writes it the way File output says, sure or not.'
        }
    ];

    var FREQUENCIES = [
        { value: 'Daily', label: 'Every day' },
        { value: 'Weekly', label: 'Every week' },
        { value: 'BiWeekly', label: 'Every 2 weeks' },
        { value: 'Monthly', label: 'Every month' }
    ];

    var DAYS = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];

    var APPEARANCE_DEFAULTS = {
        Enabled: false,
        FontSizePx: 48,
        TextColor: '#FFFFFF',
        BackgroundColor: '#00000080',
        BackgroundEnabled: true,
        VerticalPositionPercent: 0
    };

    // Plain fetch with the auth header jellyfin wants, instead of guessing at
    // ApiClient.ajax's exact behavior for every endpoint shape we hit.
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

    // Errors come back as JSON (quoted string, or ASP.NET problem details). Show the sentence, not the quotes and braces.
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

    function lapseDelete(path) {
        return lapseFetch(path, { method: 'DELETE' });
    }

    function escapeHtml(text) {
        var div = document.createElement('div');
        div.textContent = text == null ? '' : text;
        return div.innerHTML;
    }

    // guids come back dashed from some endpoints and undashed from others, === between them never matches
    function sameId(a, b) {
        if (!a || !b) {
            return false;
        }

        return String(a).replace(/-/g, '').toLowerCase() === String(b).replace(/-/g, '').toLowerCase();
    }

    function findEngine(id) {
        for (var i = 0; i < allEngines.length; i++) {
            if (allEngines[i].Id === id) {
                return allEngines[i];
            }
        }

        return null;
    }

    function engineName(id) {
        var engine = findEngine(id);
        return engine ? engine.DisplayName : id;
    }

    function defaultEngine() {
        for (var i = 0; i < allEngines.length; i++) {
            if (allEngines[i].IsDefault) {
                return allEngines[i];
            }
        }

        return allEngines[0] || null;
    }

    function usableEngines() {
        return allEngines.filter(function (e) { return e.Installed && !e.RunCheckError; });
    }

    // --- sidebar navigation ---

    var showPanel = null;

    // One panel at a time from the sidebar, Settings folds out so the first view isn't eleven config
    // screens. On a phone the same list is a drop-down. A fresh visit lands on the overview.
    function setUpNavigation(view) {
        var items = Array.prototype.slice.call(view.querySelectorAll('.lapseNavItem'));
        if (items.length === 0) {
            return;
        }

        var settingsGroup = view.querySelector('#lapseSettingsGroup');
        var settingsToggle = view.querySelector('#lapseSettingsToggle');
        var select = view.querySelector('#lapseNavSelect');

        function label(item) {
            return item.querySelector('.lapseNavLabel').textContent;
        }

        function option(item) {
            return '<option value="' + item.getAttribute('data-panel') + '">' + escapeHtml(label(item)) + '</option>';
        }

        select.innerHTML = items.filter(function (item) { return !settingsGroup.contains(item); }).map(option).join('') +
            '<optgroup label="Settings">' +
            items.filter(function (item) { return settingsGroup.contains(item); }).map(option).join('') +
            '</optgroup>';

        function openSettings(open) {
            settingsGroup.classList.toggle('hide', !open);
            settingsToggle.setAttribute('aria-expanded', open ? 'true' : 'false');
        }

        showPanel = function (panelId) {
            items.forEach(function (item) {
                var active = item.getAttribute('data-panel') === panelId;
                item.classList.toggle('lapseNavItem-active', active);

                if (active) {
                    item.setAttribute('aria-current', 'page');
                } else {
                    item.removeAttribute('aria-current');
                }
            });

            view.querySelectorAll('.lapsePanel').forEach(function (panel) {
                panel.classList.toggle('lapsePanel-active', panel.id === panelId);
            });

            if (settingsGroup.querySelector('[data-panel="' + panelId + '"]')) {
                openSettings(true);
            }

            select.value = panelId;
        };

        items.forEach(function (item) {
            item.addEventListener('click', function () {
                showPanel(item.getAttribute('data-panel'));
            });
        });

        select.addEventListener('change', function () {
            showPanel(select.value);
        });

        settingsToggle.addEventListener('click', function () {
            openSettings(settingsGroup.classList.contains('hide'));
        });

        showPanel('lapsePanelDashboard');
    }

    // --- diagnostics ---

    // Missing context menu entries is the #1 "doesn't work" report. Only warn when it really won't
    // work, the server checks whether injection CAN happen, not whether it already did.
    function refreshDiagnostics(view) {
        return lapseGet('Lapse/Diagnostics').then(function (diagnostics) {
            currentDiagnostics = diagnostics;

            var strip = view.querySelector('#lapseInjectionWarning');
            var broken = diagnostics.Working === false;

            strip.classList.toggle('hide', !broken);

            if (broken) {
                strip.textContent = 'The LAPSE entries can\'t be added to the item menus: ' +
                    (diagnostics.Problem || 'the web client\'s index.html could not be read from ' +
                        (diagnostics.WebPath || 'the configured web path') + '.') +
                    ' Everything on this page still works.';
            }

            renderAbout(view);
        }).catch(function () {
            // diagnostics failing is not itself worth an error banner
        });
    }

    // --- dashboard overview ---

    var STATUS_ORDER = [
        { key: 'Synced', label: 'synced' },
        { key: 'PartiallySynced', label: 'partly synced' },
        { key: 'Pending', label: 'not synced' },
        { key: 'Failed', label: 'failed' },
        { key: 'Skipped', label: 'skipped' },
        { key: 'Ignored', label: 'ignored' }
    ];

    // Each count is a link to the status list filtered to it, which is what anyone who just
    // read "14 failed" wants next. Zeroes are left out.
    function statusLinks(counts) {
        return STATUS_ORDER.filter(function (part) {
            return counts[part.key] > 0;
        }).map(function (part) {
            return '<a is="emby-linkbutton" href="#" class="button-link lapseStatusLink" data-status="' + part.key + '">' +
                counts[part.key] + ' ' + part.label + '</a>';
        }).join('<span aria-hidden="true"> &middot; </span>');
    }

    // proportions bar, anything above zero gets at least a sliver
    function progressBar(counts) {
        var total = STATUS_ORDER.reduce(function (sum, part) { return sum + counts[part.key]; }, 0);
        if (total === 0) {
            return '';
        }

        return '<div class="lapseProgressBar" aria-hidden="true">' + STATUS_ORDER.map(function (part) {
            var value = counts[part.key];
            return value === 0 ? '' : '<span class="lapseProgress-' + part.key + '" style="flex-grow:' + value + '"></span>';
        }).join('') + '</div>';
    }

    function timeAgo(iso) {
        if (!iso) {
            return '';
        }

        var then = new Date(iso).getTime();
        var minutes = Math.round((Date.now() - then) / 60000);

        if (minutes < 1) { return 'just now'; }
        if (minutes < 60) { return minutes + ' min ago'; }
        if (minutes < 60 * 24) { return Math.round(minutes / 60) + ' h ago'; }
        return Math.round(minutes / (60 * 24)) + ' d ago';
    }

    // counted from the status list we already have, not another trip over the whole library
    function countStatuses() {
        var counts = {
            Synced: 0, PartiallySynced: 0, Pending: 0, Failed: 0,
            Skipped: 0, Ignored: 0, WithoutSubtitles: 0, Total: allItems.length
        };

        allItems.forEach(function (item) {
            if (counts[item.Status] != null) {
                counts[item.Status]++;
            }

            if (!item.HasExternalSubtitle) {
                counts.WithoutSubtitles++;
            }
        });

        return counts;
    }

    function activityRow(entry) {
        var actions = '';
        var state = '';

        if (entry.Unsure && !entry.Reverted && !entry.Superseded && entry.InReview) {
            actions += '<button is="emby-button" type="button" class="raised lapseBtnGoReview"><span>Review</span></button>';
        }

        if (entry.CanRevert) {
            actions += '<button is="emby-button" type="button" class="raised lapseBtnRevert" data-id="' + escapeHtml(entry.Id) + '">' +
                '<span>Undo</span></button>';
        } else if (entry.Reverted) {
            state = 'Undone. ';
        } else if (entry.Superseded) {
            state = 'Replaced by a later sync. ';
        }

        return '<div class="listItem listItem-border lapseActivityRow">' +
            '<div class="listItemBody two-line">' +
            '<div class="listItemBodyText" title="' + escapeHtml(entry.OutputPath || '') + '">' + escapeHtml(entry.Name) + '</div>' +
            '<div class="listItemBodyText secondary">' + escapeHtml(state + (entry.Detail || '')) + ' &middot; ' +
            escapeHtml(timeAgo(entry.WhenUtc)) + '</div>' +
            '</div>' +
            (actions ? '<div class="lapseRowActions">' + actions + '</div>' : '') +
            '</div>';
    }

    function renderOverview(view) {
        var container = view.querySelector('#lapseOverview');
        var overview = currentOverview;

        if (!overview) {
            container.innerHTML = '';
            return;
        }

        // nothing installed: one button for LAPSE instead of a page of zeroes
        if (!overview.AnyEngineInstalled) {
            container.innerHTML = '' +
                '<div class="verticalSection">' +
                '  <p>LAPSE needs its sync engine before it can do anything. It is one download, for Linux, macOS and Windows.</p>' +
                '  <button is="emby-button" type="button" class="raised button-submit" id="lapseInstallLapse"><span>Install LAPSE</span></button>' +
                '  <div class="fieldDescription">Other engines are under Settings, Engines.</div>' +
                '</div>';

            container.querySelector('#lapseInstallLapse').addEventListener('click', function (e) {
                installEngine(view, 'lapse', e.currentTarget);
            });

            return;
        }

        var engine = findEngine(overview.ActiveEngineId);
        var engineNote = '';

        if (!overview.ActiveEngineReady) {
            engineNote = 'It is not installed. Install it under Settings, Engines.';
        } else if (engine && engine.RunCheckError) {
            engineNote = engine.RunCheckError;
        }

        // offer the update right here instead of sending people to Settings > Engines
        var updateAvailable = !!(engine && overview.ActiveEngineReady && !engine.RunCheckError && engine.UpdateAvailable);

        var counts = countStatuses();
        var modeLabel = overview.ActiveEngineMode || '';
        if (engine) {
            engine.Modes.forEach(function (m) {
                if (m.Value === overview.ActiveEngineMode) {
                    modeLabel = m.Label;
                }
            });
        }

        var eligible = counts.Synced + counts.PartiallySynced + counts.Pending + counts.Failed;
        var unsure = currentReview.length;

        // the same answer can turn up in Review only while its file is still there
        var waiting = {};
        currentReview.forEach(function (set) {
            (set.Candidates || []).forEach(function (c) {
                waiting[c.Path] = true;
            });
        });

        var recent = (overview.Recent || []);
        recent.forEach(function (entry) {
            entry.InReview = !!waiting[entry.OutputPath];
        });

        container.innerHTML = '' +
            '<div class="verticalSection">' +
            '  <h2 class="sectionTitle">' + counts.Synced + ' of ' + eligible + ' items synced</h2>' +
            progressBar(counts) +
            '  <div class="lapseStatusLinks">' + statusLinks(counts) + '</div>' +
            (counts.WithoutSubtitles
                ? '  <div class="fieldDescription">' + counts.WithoutSubtitles + ' of the ' + counts.Total +
                  ' items in the turned on libraries have no subtitle file.</div>'
                : '') +
            '  <div class="lapseButtonRow">' +
            '    <button is="emby-button" type="button" class="raised button-submit" id="lapseGoBulk"><span>Sync everything</span></button>' +
            (updateAvailable
                ? '    <button is="emby-button" type="button" class="raised" id="lapseUpdateEngine"><span>Update ' +
                  escapeHtml(engine.DisplayName) + ' to ' + escapeHtml(engine.LatestVersion || 'the latest') + '</span></button>'
                : '') +
            '  </div>' +
            '  <div class="fieldDescription">Using ' + escapeHtml(overview.ActiveEngineName || '') +
            (overview.ActiveEngineVersion ? ' ' + escapeHtml(overview.ActiveEngineVersion) : '') +
            ' in ' + escapeHtml(modeLabel) + ' mode. ' +
            '<a is="emby-linkbutton" href="#" class="button-link" id="lapseGoEngines">Change engine</a></div>' +
            (engineNote ? '  <div class="fieldDescription lapseProblem">' + escapeHtml(engineNote) + '</div>' : '') +
            '</div>' +

            (unsure
                ? '<div class="verticalSection">' +
                  '  <h2 class="sectionTitle">Waiting for review</h2>' +
                  '  <p>LAPSE was unsure about ' + unsure + (unsure === 1 ? ' subtitle' : ' subtitles') +
                  ' and kept the originals. Check the results and keep or discard them.</p>' +
                  '  <button is="emby-button" type="button" class="raised" id="lapseGoReview"><span>Review</span></button>' +
                  '</div>'
                : '') +

            '<div class="verticalSection">' +
            '  <h2 class="sectionTitle">Recent activity</h2>' +
            (recent.length === 0
                ? '  <p class="fieldDescription">Nothing yet. Every sync and shift shows up here, with a way to undo it.</p>'
                : '  <div class="paperList">' + recent.map(activityRow).join('') + '</div>') +
            '</div>';

        var updateNow = container.querySelector('#lapseUpdateEngine');
        if (updateNow) {
            updateNow.addEventListener('click', function () {
                updateEngine(view, engine.Id, updateNow);
            });
        }

        container.querySelector('#lapseGoBulk').addEventListener('click', function () {
            showPanel('lapsePanelBulk');
        });
        container.querySelector('#lapseGoEngines').addEventListener('click', function (e) {
            e.preventDefault();
            showPanel('lapsePanelEngines');
        });

        container.querySelectorAll('#lapseGoReview, .lapseBtnGoReview').forEach(function (button) {
            button.addEventListener('click', function () {
                showPanel('lapsePanelReview');
            });
        });

        container.querySelectorAll('.lapseStatusLink').forEach(function (link) {
            link.addEventListener('click', function (e) {
                e.preventDefault();
                view.querySelector('#lapseItemStatusFilter').value = link.getAttribute('data-status');
                renderItemList(view, allItems);
                showPanel('lapsePanelItems');
            });
        });

        container.querySelectorAll('.lapseBtnRevert').forEach(function (button) {
            button.addEventListener('click', function () {
                confirmAction('Undo this', 'The subtitle is put back the way it was before.', 'Undo').then(function (yes) {
                    if (!yes) {
                        return;
                    }

                    button.disabled = true;
                    lapsePost('Lapse/History/' + button.getAttribute('data-id') + '/Revert').then(function (result) {
                        tell('Undo', (result && result.Outcome) || 'Put back.');
                        return Promise.all([refreshOverview(view), refreshItemList(view), refreshReview(view)]);
                    }).catch(function (err) {
                        button.disabled = false;
                        tell('Could not undo that', err.message);
                    });
                });
            });
        });
    }

    function refreshOverview(view) {
        return lapseGet('Lapse/Overview').then(function (overview) {
            currentOverview = overview;
            renderOverview(view);
        });
    }

    // --- about ---

    function renderAbout(view) {
        var body = view.querySelector('#lapseAboutBody');
        if (!body) {
            return;
        }

        var d = currentDiagnostics || {};
        var rows = [
            ['Server', d.Platform],
            ['Engine builds chosen for', d.TargetArchitecture],
            ['This process is running as', d.ProcessArchitecture],
            ['.NET', d.Framework],
            ['Web client folder', d.WebPath],
            ['Context menu entries', d.Working === false ? 'not working' : 'working (' + (d.InjectionMethod || '') + ')']
        ];

        if (d.InContainer) {
            rows.splice(1, 0, ['Running in', 'a container']);
        }

        body.innerHTML = '' +
            '<div class="fieldDescription">Read from the running server. If the two architecture lines differ, ' +
            'it runs under emulation, which can stop an engine from starting.</div>' +
            '<div class="lapseInfoTable">' +
            rows.filter(function (r) { return r[1]; }).map(function (r) {
                return '<div class="lapseInfoRow">' +
                    '<span class="lapseInfoKey">' + escapeHtml(r[0]) + '</span>' +
                    '<span class="lapseInfoValue">' + escapeHtml(r[1]) + '</span>' +
                    '</div>';
            }).join('') +
            '</div>' +
            '<div class="lapseButtonRow">' +
            '<a is="emby-linkbutton" class="button-link" href="https://github.com/Schwponaco-org/lapse-jellyfin-plugin" target="_blank" rel="noopener">Plugin on GitHub</a>' +
            '<a is="emby-linkbutton" class="button-link" href="https://github.com/Schwponaco-org/lapse" target="_blank" rel="noopener">Engine on GitHub</a>' +
            '</div>';
    }

    // --- engines ---

    function engineStateText(engine) {
        if (!engine.Installed) {
            return engine.DownloadSupported ? 'not installed' : 'no build for this server';
        }

        return engine.RunCheckError ? 'not working' : 'installed';
    }

    // LAPSE silently falls back to a weaker voice detector when the Silero files are missing, this is the only place that says so
    function vadNotice(engine) {
        if (engine.Id !== 'lapse' || !engine.Installed || engine.VadBackend !== 'libfvad') {
            return '';
        }

        return '<div class="fieldDescription">Running on libfvad, the weaker fallback voice ' +
            'detector, instead of Silero. The onnxruntime library and silero_vad.onnx that ship ' +
            'in the same release archive were not found next to the binary, so audio-based sync ' +
            'will be less accurate than it should be. Reinstall to fetch them.</div>';
    }

    // A whisper model that's set but can't run is silently left off, so the card says why
    function whisperNotice(engine) {
        if (!engine.Installed || !engine.WhisperProblem) {
            return '';
        }

        return '<div class="fieldDescription">The whisper settings are not being used. ' +
            escapeHtml(engine.WhisperProblem) + ' Syncs carry on with voice detection alone until that is sorted.</div>';
    }

    // "sync even when unsure" used to be buried in Advanced where nobody found it. Still off by default.
    function forceControl(engine) {
        if (!engine.Installed) {
            return '';
        }

        var parameter = (engine.Parameters || []).filter(function (p) { return p.Key === 'force'; })[0];
        return parameter ? parameterControl(parameter, engine.Id) : '';
    }

    function engineUpdateNote(engine) {
        if (!engine.Installed) {
            return engine.LatestVersion ? ('Latest release ' + engine.LatestVersion) : '';
        }

        if (engine.UpdateAvailable && engine.LatestVersion) {
            return engine.VersionUnknown
                ? (engine.LatestVersion + ' is the latest release, and the copy on disk can\'t say what it is. Update to be sure.')
                : (engine.LatestVersion + ' available');
        }

        if (engine.LatestVersion) {
            return 'Up to date (' + engine.LatestVersion + ')';
        }

        return '';
    }

    // what the binary says it understands, as a tooltip (ffsubsync alone lists ~50 flags)
    function discoveredFlagsTooltip(engine) {
        if (!engine.Installed || !engine.DiscoveredFlags || !engine.DiscoveredFlags.length) {
            return '';
        }

        var source = engine.CapabilitySource === 'capabilities'
            ? 'Reported by --capabilities'
            : 'Read from the engine\'s usage text';

        return source + ': ' + engine.DiscoveredFlags.join(' ');
    }

    function parameterControl(parameter, engineId) {
        var id = 'lapseParam-' + engineId + '-' + parameter.Key;
        var flagNote = parameter.Flag ? ' <code class="lapseFlag">' + escapeHtml(parameter.Flag) + '</code>' : '';

        // the setting is kept, the runner just leaves the flag off until the engine is updated
        var versionNote = parameter.Supported === false
            ? '<div class="fieldDescription">Needs LAPSE ' +
              escapeHtml(parameter.Since || 'a newer build') + ' or newer. The installed build leaves this off until you update it.</div>'
            : '';

        if (parameter.Kind === 'Boolean') {
            return '' +
                '<div class="checkboxContainer checkboxContainer-withDescription">' +
                '<label class="emby-checkbox-label">' +
                '  <input type="checkbox" is="emby-checkbox" class="lapseParamInput" data-key="' + escapeHtml(parameter.Key) + '"' +
                '   data-kind="Boolean"' + (parameter.Value === 'true' ? ' checked' : '') + ' />' +
                '  <span>' + escapeHtml(parameter.Label) + flagNote + '</span>' +
                '</label>' +
                '<div class="fieldDescription checkboxFieldDescription">' + escapeHtml(parameter.Description) + '</div>' +
                versionNote +
                '</div>';
        }

        if (parameter.Kind === 'Select') {
            return '' +
                '<div class="selectContainer">' +
                '  <label class="selectLabel" for="' + id + '">' + escapeHtml(parameter.Label) + flagNote + '</label>' +
                '  <select is="emby-select" id="' + id + '" class="emby-select-withcolor emby-select lapseParamInput"' +
                '   data-key="' + escapeHtml(parameter.Key) + '" data-kind="Select">' +
                parameter.Options.map(function (o) {
                    return '<option value="' + escapeHtml(o.Value) + '"' +
                        (o.Value === parameter.Value ? ' selected' : '') + '>' + escapeHtml(o.Label) + '</option>';
                }).join('') +
                '  </select>' +
                '  <div class="fieldDescription">' + escapeHtml(parameter.Description) + '</div>' +
                versionNote +
                '</div>';
        }

        var attributes = '';
        if (parameter.Kind === 'Number') {
            if (parameter.Minimum != null) { attributes += ' min="' + parameter.Minimum + '"'; }
            if (parameter.Maximum != null) { attributes += ' max="' + parameter.Maximum + '"'; }
            attributes += ' step="' + (parameter.Step || 1) + '"';
        }

        var placeholder = parameter.BlankMeansUnset
            ? ' placeholder="leave blank to let the engine choose"'
            : (parameter.DefaultValue ? ' placeholder="' + escapeHtml(parameter.DefaultValue) + '"' : '');

        var input = '<input is="emby-input" id="' + id + '" type="' + (parameter.Kind === 'Number' ? 'number' : 'text') + '"' +
            ' class="lapseParamInput" data-key="' + escapeHtml(parameter.Key) + '" data-kind="' + parameter.Kind + '"' +
            attributes + (parameter.IsPath ? ' placeholder="off"' : placeholder) + ' value="' + escapeHtml(parameter.Value || '') + '" />';

        // a file on the server, same browse button as the binary path override
        if (parameter.IsPath) {
            input = '<div class="lapsePathRow">' + input +
                '<button is="paper-icon-button-light" type="button" class="lapseBtnBrowseParam" data-for="' + id + '"' +
                ' data-label="' + escapeHtml(parameter.Label) + '" title="Browse">' +
                '<span class="material-icons search" aria-hidden="true"></span></button></div>';
        }

        return '' +
            '<div class="inputContainer">' +
            '  <label class="inputLabel inputLabelUnfocused" for="' + id + '">' + escapeHtml(parameter.Label) + flagNote + '</label>' +
            input +
            '  <div class="fieldDescription">' + escapeHtml(parameter.Description) + '</div>' +
            versionNote +
            '</div>';
    }

    function engineAdvancedHtml(engine) {
        var modeOptions = engine.Modes.map(function (m) {
            return '<option value="' + escapeHtml(m.Value) + '"' +
                (m.Value === engine.DefaultMode ? ' selected' : '') + '>' + escapeHtml(m.Label) + '</option>';
        }).join('');

        var modeNotes = engine.Modes.map(function (m) {
            return '<div class="lapseModeNote"><strong>' + escapeHtml(m.Label) + '</strong> ' +
                escapeHtml(m.Description) + '</div>';
        }).join('');

        var penaltyField = '';
        if (engine.SupportsPenalty) {
            var penaltyDefault = engine.DefaultPenalty;

            penaltyField = '' +
                '<div class="inputContainer">' +
                '  <label class="inputLabel inputLabelUnfocused" for="lapsePenalty-' + escapeHtml(engine.Id) + '">Split penalty' +
                ' <code class="lapseFlag">' + (engine.Id === 'lapse' ? 'positional' : '--split-penalty') + '</code></label>' +
                '  <input is="emby-input" type="number" id="lapsePenalty-' + escapeHtml(engine.Id) + '" class="lapseSettingPenalty" min="' + engine.MinPenalty +
                '" max="' + engine.MaxPenalty + '" value="' + engine.Penalty + '" />' +
                '  <div class="fieldDescription">Higher means fewer splits. ' +
                penaltyDefault + ' is the standard value. Range ' + engine.MinPenalty + ' to ' + engine.MaxPenalty +
                ', split mode only.' +
                (engine.Id === 'lapse'
                    ? ' In Auto mode the engine works this out from the size of the file and ignores it.'
                    : '') +
                '</div>' +
                '</div>';
        }

        var note = engine.AdvancedNote
            ? '<div class="fieldDescription">' + escapeHtml(engine.AdvancedNote) + '</div>'
            : '';

        return '' +
            '<details class="lapseDetails">' +
            '<summary>Advanced</summary>' +
            note +
            '  <div class="selectContainer">' +
            '    <label class="selectLabel" for="lapseMode-' + escapeHtml(engine.Id) + '">Default sync mode</label>' +
            '    <select is="emby-select" id="lapseMode-' + escapeHtml(engine.Id) + '" class="emby-select-withcolor emby-select lapseSettingMode">' + modeOptions + '</select>' +
            '    <div class="fieldDescription">What Sync does from an item\'s menu or the Sync status list.</div>' +
            '  </div>' +
            '  <div class="lapseModeNotes">' + modeNotes + '</div>' +
            penaltyField +
            engine.Parameters.filter(function (p) { return p.Key !== 'force'; }).map(function (p) {
                return parameterControl(p, engine.Id);
            }).join('') +
            '  <div class="inputContainer">' +
            '    <label class="inputLabel inputLabelUnfocused" for="lapsePath-' + escapeHtml(engine.Id) + '">Binary path override</label>' +
            '    <div class="lapsePathRow">' +
            '      <input is="emby-input" type="text" id="lapsePath-' + escapeHtml(engine.Id) + '" class="lapseSettingPath" value="' + escapeHtml(engine.PathOverride || '') + '" />' +
            '      <button is="paper-icon-button-light" type="button" class="lapseBtnBrowsePath" title="Browse" aria-label="Browse for the binary">' +
            '        <span class="material-icons search" aria-hidden="true"></span>' +
            '      </button>' +
            '    </div>' +
            '    <div class="fieldDescription">Leave empty to use the copy the plugin installed. ' +
            'Point this at a binary you built yourself when there is no download for this server. ' +
            'The plugin never replaces a binary behind an override.</div>' +
            '  </div>' +
            '  <a is="emby-linkbutton" class="button-link" href="' + escapeHtml(engine.ProjectUrl) +
            '" target="_blank" rel="noopener">' + escapeHtml(engine.DisplayName) + ' on GitHub</a>' +
            '</details>';
    }

    // one box comparing all engines' formats side by side, that's the actual question people have
    function renderEngineFormats(view) {
        var box = view.querySelector('#lapseEngineFormats');
        if (!box) {
            return;
        }

        var withFormats = allEngines.filter(function (e) {
            return (e.SubtitleExtensions || []).length > 0;
        });

        if (withFormats.length === 0) {
            box.classList.add('hide');
            return;
        }

        box.classList.remove('hide');
        box.innerHTML =

            '<div class="lapseFormatTable">' +
            withFormats.map(function (engine) {
                return '<div class="lapseFormatName">' + escapeHtml(engine.DisplayName) +
                    (engine.IsDefault ? ' (in use)' : '') + '</div>' +
                    '<div class="lapseFormatList">' +
                    (engine.SubtitleExtensions || []).map(function (f) {
                        return '<code>' + escapeHtml(f) + '</code>';
                    }).join(' ') +
                    '</div>';
            }).join('') +
            '</div>' +
            '<p>Each engine writes a file back in the format it read it in. Anything the engine ' +
            'in use cannot read is converted to .srt first and synced as that, so nothing is ' +
            'turned away for its format alone.</p>' +
            '<p>.sup (PGS) and .idx (VobSub, point it at the .idx file) are pictures of text ' +
            'rather than text. LAPSE can still move their timing, because timing is timing, and from ' +
            '2.5.0 shift them by hand too, but converting and translating need words and cannot touch them. ' +
            'Those need OCR first, with something like Subtitle Edit.</p>' +
            (broadcastFormats(withFormats)
                ? '<p>.stl (EBU STL, or the text STL from DVD Studio Pro) and .scc (Scenarist) are broadcast formats. The engine syncs them fine, but ' +
                  'Jellyfin cannot show either of them, so the plugin leaves them out of its subtitle lists. For these ' +
                  'files use <a href="https://schwponaco.org/lapse/" target="_blank" rel="noopener">LAPSE in the browser</a> ' +
                  'or the engine\'s <a href="https://github.com/Schwponaco-org/lapse#docker" target="_blank" rel="noopener">Docker image</a>, ' +
                  'which take them as they are, or convert them to .srt first to watch them in Jellyfin.</p>'
                : '') +
            '<p>Whatever a file is read as, it is written back in the encoding it arrived in, so a ' +
            'Windows-1252 subtitle stays Windows-1252. To force a mixed library onto one encoding ' +
            'instead, use Write every result in one encoding under the engine\'s advanced settings.</p>' +
            subFormatsHtml();
    }

    function broadcastFormats(engines) {
        return engines.some(function (e) {
            return (e.SubtitleExtensions || []).some(function (f) { return f === '.stl' || f === '.scc'; });
        });
    }

    // MicroDVD, MPL2 and SubViewer 2 all use .sub, LAPSE sniffs which. Only MicroDVD needs a frame rate.
    function subFormatsHtml() {
        var kinds = [
            ['MicroDVD', '{450}{487}text', 'Yes, frames mean nothing without one'],
            ['MPL2', '[180][195]text', 'No, the numbers are tenths of a second'],
            ['SubViewer 2', '00:03:00.00,00:03:01.50 on its own line', 'No']
        ];

        return '<p>Three formats share .sub and the name says nothing about which one you have, ' +
            'so LAPSE reads the file instead:</p>' +
            '<div class="lapseSubKinds">' +
            '<div class="lapseSubKindHead"></div>' +
            '<div class="lapseSubKindHead">Looks like</div>' +
            '<div class="lapseSubKindHead">Needs a frame rate</div>' +
            kinds.map(function (kind) {
                return '<div class="lapseSubKindName">' + escapeHtml(kind[0]) + '</div>' +
                    '<div class="lapseSubKindShape"><code>' + escapeHtml(kind[1]) + '</code></div>' +
                    '<div class="lapseSubKindFps">' + escapeHtml(kind[2]) + '</div>';
            }).join('') +
            '</div>';
    }

    // Anything worth knowing about running an engine outside Jellyfin. Only LAPSE has one
    // of these, but it is driven off the data rather than hardcoded to it.
    function renderEngineDeployment(view) {
        var box = view.querySelector('#lapseEngineDeployment');
        if (!box) {
            return;
        }

        var withNotes = allEngines.filter(function (e) { return e.DeploymentNote; });

        if (withNotes.length === 0) {
            box.classList.add('hide');
            return;
        }

        box.classList.remove('hide');
        box.innerHTML =
            '<h3>Running it outside Jellyfin</h3>' +
            withNotes.map(function (engine) {
                return '<p>' + escapeHtml(engine.DeploymentNote) +
                    ' <a is="emby-linkbutton" class="button-link" href="' + escapeHtml(engine.ProjectUrl) +
                    '" target="_blank" rel="noopener">' + escapeHtml(engine.DisplayName) +
                    ' on GitHub</a></p>';
            }).join('');
    }

    function renderEngineCards(view) {
        var container = view.querySelector('#lapseEngineCards');

        container.innerHTML = allEngines.map(function (engine) {
            var actions = '';
            if (!engine.IsDefault && engine.Installed && !engine.RunCheckError) {
                actions += '<button is="emby-button" type="button" class="raised lapseBtnSetDefault"><span>Use this one</span></button>';
            }

            if (engine.Installed && engine.DownloadSupported && engine.UpdateAvailable) {
                actions += '<button is="emby-button" type="button" class="raised button-submit lapseBtnUpdate"><span>Update</span></button>';
            }

            if (engine.DownloadSupported) {
                actions += '<button is="emby-button" type="button" class="raised lapseBtnInstall"><span>' +
                    (engine.Installed ? 'Reinstall' : 'Install') + '</span></button>';
            }

            // Only offered for a copy the plugin put there. A binary behind a path
            // override isn't the plugin's to delete, and the server refuses anyway.
            if (engine.Installed && !engine.PathOverride) {
                actions += '<button is="emby-button" type="button" class="raised lapseBtnUninstall"><span>Uninstall</span></button>';
            }

            var problem = '';
            if (!engine.DownloadSupported && !engine.Installed) {
                problem = '<div class="fieldDescription">' + escapeHtml(engine.NoDownloadReason || '') +
                    (engine.BuildGuideUrl
                        ? ' <a is="emby-linkbutton" class="button-link" href="' + escapeHtml(engine.BuildGuideUrl) +
                          '" target="_blank" rel="noopener">Build instructions</a>'
                        : '') +
                    '</div>';
            } else if (engine.RunCheckError) {
                problem = '<div class="fieldDescription lapseProblem">' + escapeHtml(engine.RunCheckError) + '</div>';
            }

            // Only LAPSE carries a "why" link, the one claim on this page that needs backing up
            var whyLink = engine.WhyUrl
                ? ' <a is="emby-linkbutton" class="button-link" href="' + escapeHtml(engine.WhyUrl) + '" target="_blank" rel="noopener">' +
                  escapeHtml(engine.WhyLabel || 'Read more on GitHub') + '</a>'
                : '';

            var state = capitalize(engineUpdateNote(engine) || engineStateText(engine)) + '.' +
                (engine.Installed && !engine.InstalledVersion ? ' The installed copy does not say which version it is.' : '');

            return '' +
                '<div class="verticalSection lapseEngineCard" data-id="' + escapeHtml(engine.Id) + '">' +
                '  <h3 class="sectionTitle" title="' + escapeHtml(discoveredFlagsTooltip(engine)) + '">' +
                escapeHtml(engine.DisplayName) + (engine.InstalledVersion ? ' ' + escapeHtml(engine.InstalledVersion) : '') +
                (engine.IsDefault ? ' (in use)' : '') + '</h3>' +
                '  <div class="fieldDescription">' + escapeHtml(state) + ' ' + escapeHtml(engine.Tier) + '.' + whyLink + '</div>' +
                problem +
                vadNotice(engine) +
                whisperNotice(engine) +
                forceControl(engine) +
                (actions ? '  <div class="lapseButtonRow">' + actions + '</div>' : '') +
                engineAdvancedHtml(engine) +
                '</div>';
        }).join('');

        container.querySelectorAll('.lapseEngineCard').forEach(function (card) {
            var id = card.getAttribute('data-id');

            var setDefault = card.querySelector('.lapseBtnSetDefault');
            if (setDefault) {
                setDefault.addEventListener('click', function () {
                    lapsePost('Lapse/Engines/' + id + '/Default').then(function () {
                        return refreshEngines(view).then(function () { return refreshOverview(view); });
                    }).catch(function (err) {
                        tell('Could not set the default engine', err.message);
                    });
                });
            }

            var install = card.querySelector('.lapseBtnInstall');
            if (install) {
                install.addEventListener('click', function () {
                    installEngine(view, id, install);
                });
            }

            var update = card.querySelector('.lapseBtnUpdate');
            if (update) {
                update.addEventListener('click', function () {
                    updateEngine(view, id, update);
                });
            }

            card.querySelector('.lapseBtnBrowsePath').addEventListener('click', function () {
                browseForPath(card.querySelector('.lapseSettingPath'), true, 'Select the engine binary');
            });

            card.querySelectorAll('.lapseBtnBrowseParam').forEach(function (button) {
                button.addEventListener('click', function () {
                    browseForPath(card.querySelector('#' + button.getAttribute('data-for')), true, button.getAttribute('data-label'));
                });
            });

            var uninstall = card.querySelector('.lapseBtnUninstall');
            if (uninstall) {
                uninstall.addEventListener('click', function () {
                    confirmAction('Uninstall ' + engineName(id),
                        'Your settings for it are kept, and it can be installed again at any time.', 'Uninstall').then(function (yes) {
                        if (!yes) {
                            return;
                        }

                        uninstall.disabled = true;
                        Dashboard.showLoadingMsg();

                        lapsePost('Lapse/Engines/' + id + '/Uninstall').then(function () {
                            Dashboard.hideLoadingMsg();
                            return refreshEngines(view).then(function () { return refreshOverview(view); });
                        }).catch(function (err) {
                            Dashboard.hideLoadingMsg();
                            tell('Could not remove that engine', err.message);
                            return refreshEngines(view);
                        });
                    });
                });
            }
        });

        var installed = usableEngines().length;
        view.querySelector('#lapseEnginesHint').textContent = installed + ' of ' + allEngines.length + ' installed';
    }

    function installEngine(view, id, button) {
        button.disabled = true;
        Dashboard.showLoadingMsg();

        lapsePost('Lapse/Engines/' + id + '/Install').then(function () {
            Dashboard.hideLoadingMsg();
            return refreshEngines(view).then(function () { return refreshOverview(view); });
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            tell('Could not install that engine', err.message);
            return refreshEngines(view);
        });
    }

    function updateEngine(view, id, button) {
        button.disabled = true;
        Dashboard.showLoadingMsg();

        lapsePost('Lapse/Engines/' + id + '/Update').then(function (result) {
            Dashboard.hideLoadingMsg();
            tell(engineName(id), (result && result.Outcome) || 'Updated.');
            return refreshEngines(view).then(function () { return refreshOverview(view); });
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            tell('Could not update that engine', err.message);
            return refreshEngines(view);
        });
    }

    function checkEngineUpdates(view) {
        Dashboard.showLoadingMsg();
        lapseGet('Lapse/Engines/Updates').then(function (statuses) {
            Dashboard.hideLoadingMsg();

            var lines = (statuses || []).map(function (s) {
                if (!s.LatestVersion) {
                    return s.EngineId + ': could not reach GitHub';
                }

                if (s.UpdateAvailable) {
                    return s.EngineId + ': ' + (s.InstalledVersion || 'version unknown') + ' -> ' + s.LatestVersion + ' available';
                }

                return s.EngineId + ': up to date (' + s.LatestVersion + ')';
            });

            tell('Engine updates', lines.join('\n'));
            return refreshEngines(view);
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            tell('Could not check for updates', err.message);
        });
    }

    function refreshEngines(view) {
        return lapseGet('Lapse/Engines').then(function (engines) {
            allEngines = engines;
            renderEngineCards(view);
            renderEngineFormats(view);
            renderEngineDeployment(view);
            updateSubToSubEngineNote(view);
            renderConversionEngineNote(view);
            view.querySelector('#lapseAutoUpdateEngines').checked =
                allEngines.length > 0 ? !!allEngines[0].AutoUpdate : true;
        });
    }

    function refreshPlatform(view) {
        return lapseGet('Lapse/Platform').then(function (platform) {
            view.querySelector('#lapsePlatformNote').textContent =
                'This server is running ' + platform.Description + '.';
        }).catch(function () {
            // purely informational, not worth an error message
        });
    }

    function refreshEngineCache(view) {
        return lapseGet('Lapse/Engines/Cache').then(function (bytes) {
            view.querySelector('#lapseEngineCacheNote').textContent =
                'LAPSE keeps the speech it found in each film, so the next subtitle for it takes a second. The cache holds ' +
                (bytes < 1048576 ? Math.ceil(bytes / 1024) + ' KB' : (bytes / 1048576).toFixed(1) + ' MB') + '.';
        }).catch(function () {
            // purely informational, not worth an error message
        });
    }

    function clearEngineCache(view) {
        confirmAction('Clear the cache', 'Nothing is lost, but the next sync of each film takes longer while LAPSE listens to it again.',
            'Clear').then(function (yes) {
            if (yes) {
                clearEngineCacheConfirmed(view);
            }
        });
    }

    function clearEngineCacheConfirmed(view) {
        lapseDelete('Lapse/Engines/Cache').then(function () {
            Dashboard.alert('The cache is cleared.');
            return refreshEngineCache(view);
        }).catch(function (err) {
            tell('Could not clear the cache', err.message);
        });
    }

    // --- libraries ---

    function frequencyOptions(selected) {
        return FREQUENCIES.map(function (f) {
            return '<option value="' + f.value + '"' + (f.value === selected ? ' selected' : '') + '>' +
                f.label + '</option>';
        }).join('');
    }

    function dayOptions(selected) {
        return DAYS.map(function (day) {
            return '<option value="' + day + '"' + (day === selected ? ' selected' : '') + '>' + day + '</option>';
        }).join('');
    }

    function renderLibraries(view) {
        var container = view.querySelector('#lapseLibraryList');

        if (allLibraries.length === 0) {
            container.innerHTML = '<p class="fieldDescription">Jellyfin has no libraries yet.</p>';
            return;
        }

        container.innerHTML = allLibraries.map(function (library) {
            var frequency = library.ScheduleFrequency || 'Daily';

            var name = escapeHtml(library.Name);

            return '' +
                '<div class="listItem listItem-border lapseLibraryRow" data-id="' + escapeHtml(library.ItemId) + '">' +
                '  <div class="listItemBody">' +
                '    <div class="checkboxContainer"><label class="emby-checkbox-label">' +
                '      <input type="checkbox" is="emby-checkbox" class="lapseChkLibraryEnabled"' + (library.Enabled ? ' checked' : '') + ' />' +
                '      <span>' + name + '</span>' +
                '    </label></div>' +
                '    <div class="lapseLibrarySchedule">' +
                '      <div class="checkboxContainer"><label class="emby-checkbox-label">' +
                '        <input type="checkbox" is="emby-checkbox" class="lapseChkAutoSync"' + (library.AutoSyncEnabled ? ' checked' : '') + ' />' +
                '        <span>New items</span>' +
                '      </label></div>' +
                '      <div class="checkboxContainer"><label class="emby-checkbox-label">' +
                '        <input type="checkbox" is="emby-checkbox" class="lapseChkSchedule"' + (library.ScheduleEnabled ? ' checked' : '') + ' />' +
                '        <span>Schedule</span>' +
                '      </label></div>' +
                '      <select is="emby-select" class="emby-select-withcolor emby-select lapseScheduleFrequency" aria-label="How often, ' + name + '">' +
                frequencyOptions(frequency) + '</select>' +
                '      <select is="emby-select" class="emby-select-withcolor emby-select lapseScheduleDay" aria-label="Day, ' + name + '">' +
                dayOptions(library.ScheduleDay || 'Sunday') + '</select>' +
                '      <input is="emby-input" type="time" class="lapseScheduleTime" aria-label="Time, ' + name + '" value="' + escapeHtml(library.ScheduleTime || '03:00') + '" />' +
                '    </div>' +
                '  </div>' +
                '</div>';
        }).join('');

        container.querySelectorAll('.lapseLibraryRow').forEach(function (row) {
            var scheduleCheck = row.querySelector('.lapseChkSchedule');
            var autoSyncCheck = row.querySelector('.lapseChkAutoSync');
            var enabledCheck = row.querySelector('.lapseChkLibraryEnabled');
            var frequencySelect = row.querySelector('.lapseScheduleFrequency');
            var daySelect = row.querySelector('.lapseScheduleDay');
            var timeInput = row.querySelector('.lapseScheduleTime');

            // new items OR schedule, ticking one unticks the other
            autoSyncCheck.addEventListener('change', function () {
                if (autoSyncCheck.checked) {
                    scheduleCheck.checked = false;
                }

                syncRowState();
            });

            scheduleCheck.addEventListener('change', function () {
                if (scheduleCheck.checked) {
                    autoSyncCheck.checked = false;
                }

                syncRowState();
            });

            // schedule fields only when there's a schedule, and no day picker for daily
            function syncRowState() {
                var scheduled = scheduleCheck.checked && enabledCheck.checked;
                var needsDay = scheduled && frequencySelect.value !== 'Daily';

                scheduleCheck.disabled = !enabledCheck.checked;
                autoSyncCheck.disabled = !enabledCheck.checked;

                frequencySelect.classList.toggle('hide', !scheduled);
                timeInput.classList.toggle('hide', !scheduled);
                daySelect.classList.toggle('hide', !needsDay);

                frequencySelect.disabled = !scheduled;
                timeInput.disabled = !scheduled;
                daySelect.disabled = !needsDay;

                // A row nobody can act on reads as inert, not just as a row with two
                // greyed out boxes in it.
                row.querySelector('.lapseLibrarySchedule').classList.toggle('lapseDimmed', !enabledCheck.checked);
            }

            // turning a library off clears new-item and schedule too, so turning it back on doesn't bring them back unasked
            enabledCheck.addEventListener('change', function () {
                if (!enabledCheck.checked) {
                    autoSyncCheck.checked = false;
                    scheduleCheck.checked = false;
                }

                syncRowState();
            });
            frequencySelect.addEventListener('change', syncRowState);
            syncRowState();
        });

        var enabled = allLibraries.filter(function (l) { return l.Enabled; }).length;
        var scheduled = allLibraries.filter(function (l) { return l.Enabled && l.ScheduleEnabled; }).length;
        var automatic = allLibraries.filter(function (l) { return l.Enabled && l.AutoSyncEnabled; }).length;

        var extra = [];
        if (automatic) {
            extra.push(automatic + ' on new items');
        }

        if (scheduled) {
            extra.push(scheduled + ' scheduled');
        }

        view.querySelector('#lapseLibrariesHint').textContent =
            enabled + ' of ' + allLibraries.length + ' on' + (extra.length ? (', ' + extra.join(', ')) : '');
    }

    function refreshLibraries(view) {
        return lapseGet('Lapse/Libraries').then(function (libraries) {
            allLibraries = libraries;
            renderLibraries(view);
            renderLibraryFilter(view);
            renderShowLibraries(view);
            renderExtractLibraries(view, true);
        });
    }

    function saveLibraries(view, button) {
        var payload = { Libraries: [] };

        view.querySelectorAll('.lapseLibraryRow').forEach(function (row) {
            var frequency = row.querySelector('.lapseScheduleFrequency').value;

            payload.Libraries.push({
                ItemId: row.getAttribute('data-id'),
                Enabled: row.querySelector('.lapseChkLibraryEnabled').checked,
                AutoSyncEnabled: row.querySelector('.lapseChkAutoSync').checked,
                ScheduleEnabled: row.querySelector('.lapseChkSchedule').checked,
                ScheduleFrequency: frequency,
                ScheduleDay: frequency === 'Daily' ? null : row.querySelector('.lapseScheduleDay').value,
                ScheduleTime: row.querySelector('.lapseScheduleTime').value || '03:00'
            });
        });

        button.disabled = true;
        Dashboard.showLoadingMsg();
        lapsePost('Lapse/Libraries', payload).then(function () {
            Dashboard.hideLoadingMsg();
            button.disabled = false;
            Dashboard.alert('Settings saved.');

            // read them straight back rather than trusting the form: if anything didn't
            // stick, the toggles snapping back is the honest answer
            return refreshLibraries(view).then(function () {
                return Promise.all([refreshItemList(view), refreshOverview(view)]);
            });
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            button.disabled = false;
            tell('Could not save the library settings', err.message);
        });
    }

    // enabled libraries only, the status list has nothing from the others
    function renderLibraryFilter(view) {
        var select = view.querySelector('#lapseItemLibraryFilter');
        var previous = select.value;

        select.innerHTML = '<option value="">All libraries</option>' + allLibraries.filter(function (library) {
            return library.Enabled;
        }).map(function (library) {
            return '<option value="' + library.ItemId + '">' + escapeHtml(library.Name) + '</option>';
        }).join('');

        if (previous) {
            select.value = previous;
        }
    }

    // --- series and season sync, per show library ---

    function renderShowLibraries(view) {
        var container = view.querySelector('#lapseShowLibraries');
        var showLibraries = allLibraries.filter(function (l) { return l.IsShowLibrary && l.Enabled; });

        if (showLibraries.length === 0) {
            container.innerHTML = '';
            return;
        }

        container.innerHTML = showLibraries.map(function (library) {
            return '' +
                '<div class="verticalSection lapseShowLibrary" data-id="' + escapeHtml(library.ItemId) + '">' +
                '  <h3 class="sectionTitle">' + escapeHtml(library.Name) + '</h3>' +
                '  <div class="lapseButtonRow">' +
                '    <button is="emby-button" type="button" class="raised lapseBtnSyncAllSeries">' +
                '      <span>Sync all series</span></button>' +
                '    <button is="emby-button" type="button" class="raised lapseBtnSyncSeason">' +
                '      <span>Sync season...</span></button>' +
                '  </div>' +
                '</div>';
        }).join('');

        container.querySelectorAll('.lapseShowLibrary').forEach(function (row) {
            var libraryId = row.getAttribute('data-id');

            row.querySelector('.lapseBtnSyncAllSeries').addEventListener('click', function () {
                Dashboard.showLoadingMsg();
                lapsePost('Lapse/Libraries/' + libraryId + '/SyncAllSeries').then(function (result) {
                    Dashboard.hideLoadingMsg();
                    Dashboard.alert('Queued ' + ((result && result.Queued) || 0) + ' episodes.');
                    startQueuePolling(view);
                    refreshQueue(view);
                }).catch(function (err) {
                    Dashboard.hideLoadingMsg();
                    tell('Could not start the sync', err.message);
                });
            });

            row.querySelector('.lapseBtnSyncSeason').addEventListener('click', function () {
                openSeasonPicker(view, libraryId);
            });
        });
    }

    // Series first, then that series' seasons, then sync. Two steps rather than one huge
    // flat list, because a TV library can hold hundreds of series.
    function openSeasonPicker(view, libraryId) {
        Dashboard.showLoadingMsg();

        lapseGet('Lapse/Libraries/' + libraryId + '/Series').then(function (series) {
            Dashboard.hideLoadingMsg();

            if (!series || series.length === 0) {
                tell('Sync a season', 'There are no series in that library yet.');
                return;
            }

            var overlay = openDialog('Sync a season',
                '<div class="selectContainer">' +
                '  <label class="selectLabel" for="lapseSeasonSeries">Series</label>' +
                '  <select is="emby-select" id="lapseSeasonSeries" class="emby-select-withcolor emby-select">' +
                series.map(function (s) {
                    return '<option value="' + s.ItemId + '">' + escapeHtml(s.Name) + '</option>';
                }).join('') +
                '  </select>' +
                '</div>' +
                '<div class="selectContainer">' +
                '  <label class="selectLabel" for="lapseSeasonSeason">Season</label>' +
                '  <select is="emby-select" id="lapseSeasonSeason" class="emby-select-withcolor emby-select"></select>' +
                '</div>',
                'Sync');

            var seriesSelect = overlay.querySelector('#lapseSeasonSeries');
            var seasonSelect = overlay.querySelector('#lapseSeasonSeason');
            var syncButton = overlay.querySelector('.btnSubmit');

            function loadSeasons() {
                seasonSelect.innerHTML = '<option value="">Loading...</option>';
                syncButton.disabled = true;

                lapseGet('Lapse/Series/' + seriesSelect.value + '/Seasons').then(function (seasons) {
                    if (!seasons || seasons.length === 0) {
                        seasonSelect.innerHTML = '<option value="">No seasons found</option>';
                        return;
                    }

                    seasonSelect.innerHTML = seasons.map(function (s) {
                        return '<option value="' + s.ItemId + '">' + escapeHtml(s.Name) + '</option>';
                    }).join('');
                    syncButton.disabled = false;
                }).catch(function (err) {
                    seasonSelect.innerHTML = '<option value="">Could not load seasons</option>';
                    tell('Could not load seasons', err.message);
                });
            }

            seriesSelect.addEventListener('change', loadSeasons);
            loadSeasons();

            syncButton.addEventListener('click', function () {
                var seasonId = seasonSelect.value;
                if (!seasonId) {
                    return;
                }

                closeDialog(overlay);
                Dashboard.showLoadingMsg();

                lapsePost('Lapse/Series/Sync', { ItemId: seasonId }).then(function (result) {
                    Dashboard.hideLoadingMsg();
                    Dashboard.alert('Queued ' + ((result && result.Queued) || 0) + ' episodes.');
                    startQueuePolling(view);
                    refreshQueue(view);
                }).catch(function (err) {
                    Dashboard.hideLoadingMsg();
                    tell('Could not start the sync', err.message);
                });
            });
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            tell('Could not load the series list', err.message);
        });
    }

    // --- sync queue ---

    function refreshQueue(view) {
        return lapseGet('Lapse/Queue').then(function (snapshot) {
            snapshot = snapshot || {};

            var section = view.querySelector('#lapseQueueSection');
            var running = !!snapshot.Running;
            section.classList.toggle('hide', !running);

            if (running) {
                var pct = snapshot.Total === 0 ? 0 : Math.round((snapshot.Completed / snapshot.Total) * 100);
                var unit = snapshot.UnitName || 'item';
                var plural = snapshot.Total === 1 ? unit : unit + 's';

                view.querySelector('#lapseQueueBar').value = pct;
                view.querySelector('#lapseQueueText').textContent = snapshot.Cancelling
                    ? 'Stopping after the item that is running now...'
                    : ((snapshot.JobName ? snapshot.JobName + ': ' : '') +
                        snapshot.Completed + ' / ' + snapshot.Total + ' ' + plural + ' processed' +
                        (snapshot.CurrentItemName ? (' - ' + snapshot.CurrentItemName) : ''));

                var stopButton = view.querySelector('#lapseQueueStop');
                if (stopButton) {
                    stopButton.disabled = !!snapshot.Cancelling;
                }
            } else if (queueWasRunning) {
                // only when a job just finished. Doing it every idle tick ran Lapse/Status (whole library) every 2s.
                refreshItemList(view);
                refreshOverview(view);
                refreshRuns(view);
            }

            queueWasRunning = running;
        }).catch(function (err) {
            // This runs on a timer, so a server that is restarting or briefly unreachable
            // must not turn into a rejection every two seconds.
            console.warn('LAPSE: could not read the sync queue', err);
        });
    }

    // 2s while a job runs, 10s idle. Idle polling only exists to notice jobs started elsewhere.
    function scheduleQueuePoll(view, delay) {
        clearTimeout(queuePollTimer);

        queuePollTimer = setTimeout(function () {
            refreshQueue(view).then(function () {
                if (queuePolling) {
                    scheduleQueuePoll(view, queueWasRunning ? 2000 : 10000);
                }
            });
        }, delay);
    }

    // Called when the page opens and whenever a job has just been started, so the next
    // look comes round quickly rather than whenever the idle poll would have.
    function startQueuePolling(view) {
        queuePolling = true;
        scheduleQueuePoll(view, 2000);
    }

    function stopQueuePolling() {
        queuePolling = false;
        clearTimeout(queuePollTimer);
        queuePollTimer = null;
    }

    // --- item status list ---

    function statusLabel(item) {
        switch (item.Status) {
            case 'Synced': return 'Synced';
            case 'PartiallySynced': return item.SyncedSubtitleCount + ' of ' + item.SubtitleCount + ' synced';
            case 'Skipped': return 'Skipped';
            case 'Ignored': return 'Ignored';
            case 'Failed': return 'Failed';
            default: return 'Not synced';
        }
    }

    function renderItemList(view, items) {
        var container = view.querySelector('#lapseItemList');

        allItems = items || [];
        items = allItems;

        // libraries with phone clips etc fill up with items without subtitles, hide those unless asked
        var includeAll = view.querySelector('#lapseIncludeAll').checked;
        var shown = includeAll ? items : items.filter(function (i) { return i.HasExternalSubtitle; });

        var libraryId = view.querySelector('#lapseItemLibraryFilter').value;
        if (libraryId) {
            shown = shown.filter(function (i) { return sameId(i.LibraryId, libraryId); });
        }

        var status = view.querySelector('#lapseItemStatusFilter').value;
        if (status) {
            shown = shown.filter(function (i) { return i.Status === status; });
        }

        var search = (view.querySelector('#lapseItemSearch').value || '').trim().toLowerCase();
        if (search) {
            shown = shown.filter(function (i) {
                return (i.Name || '').toLowerCase().indexOf(search) !== -1;
            });
        }

        view.querySelector('#lapseItemsHint').textContent = shown.length + ' of ' + items.length;

        if (shown.length === 0) {
            if (search || libraryId || status) {
                container.innerHTML = '<p class="fieldDescription">Nothing matches that filter.</p>';
            } else if (!includeAll) {
                container.innerHTML = '<p class="fieldDescription">No items with an external subtitle. Turn on "Include all" to see everything.</p>';
            } else {
                container.innerHTML = '<p class="fieldDescription">No items found. Check that at least one library is turned on under Settings, Libraries.</p>';
            }

            return;
        }

        // a big TV library can be thousands of episodes, and rendering all of them makes
        // the page crawl for no benefit
        var capped = shown.slice(0, 500);

        container.innerHTML = capped.map(function (item) {
            var ignored = item.Status === 'Ignored';
            var skipLabel = item.Status === 'Skipped' ? 'Un-skip' : 'Skip';
            var errorLine = item.LastError
                ? ('<div class="listItemBodyText secondary">' + escapeHtml(item.LastError) + '</div>')
                : '';

            return '' +
                '<div class="listItem listItem-border lapseItemRow' + (ignored ? ' lapseItemRow-ignored' : '') + '"' +
                ' data-id="' + escapeHtml(item.ItemId) + '" data-name="' + escapeHtml(item.Name) + '">' +
                '  <div class="listItemBody' + (errorLine ? ' three-line' : ' two-line') + '">' +
                '    <div class="listItemBodyText">' + escapeHtml(item.Name) + '</div>' +
                '    <div class="listItemBodyText secondary">' + escapeHtml(statusLabel(item)) + ' &middot; ' +
                escapeHtml(item.LibraryName || 'unknown library') + ' &middot; ' +
                item.SubtitleCount + ' subtitle' + (item.SubtitleCount === 1 ? '' : 's') + '</div>' +
                errorLine +
                '  </div>' +
                '  <div class="lapseRowActions">' +
                '    <button is="emby-button" type="button" class="raised button-submit lapseBtnSync"><span>Sync</span></button>' +
                '    <button is="emby-button" type="button" class="raised lapseBtnAdvanced"><span>Advanced</span></button>' +
                '    <button is="emby-button" type="button" class="raised lapseBtnSkip"><span>' + skipLabel + '</span></button>' +
                '    <button is="emby-button" type="button" class="raised lapseBtnIgnore"><span>' +
                (ignored ? 'Un-ignore' : 'Ignore') + '</span></button>' +
                '  </div>' +
                '</div>';
        }).join('');

        if (shown.length > capped.length) {
            container.innerHTML += '<p class="fieldDescription">' +
                'Showing the first ' + capped.length + ' of ' + shown.length + '. Narrow it down with the search or the filters.</p>';
        }

    }

    // redrawn on every keystroke (up to 500 rows), so one delegated listener instead of four per row
    function wireItemListActions(view) {
        view.querySelector('#lapseItemList').addEventListener('click', function (e) {
            var button = e.target.closest ? e.target.closest('button') : null;
            var row = button ? button.closest('.lapseItemRow') : null;

            if (!row) {
                return;
            }

            var itemId = row.getAttribute('data-id');
            var name = row.getAttribute('data-name');

            if (button.classList.contains('lapseBtnSync')) {
                quickSync(view, itemId, name);
            } else if (button.classList.contains('lapseBtnAdvanced')) {
                openAdvancedDialog(view, itemId, name);
            } else if (button.classList.contains('lapseBtnSkip')) {
                toggleSkipItem(view, itemId, button.textContent.trim().indexOf('Un-skip') !== 0);
            } else if (button.classList.contains('lapseBtnIgnore')) {
                toggleIgnoreItem(view, itemId, row.classList.contains('lapseItemRow-ignored'));
            }
        });
    }

    function toggleSkipItem(view, itemId, skip) {
        lapsePost('Lapse/Skip', { ItemId: itemId, Skip: skip }).then(function () {
            return Promise.all([refreshItemList(view), refreshOverview(view)]);
        }).catch(function (err) {
            tell('Could not change that', err.message);
        });
    }

    function toggleIgnoreItem(view, itemId, isIgnored) {
        var request = isIgnored
            ? lapseDelete('Lapse/Ignore?itemId=' + encodeURIComponent(itemId))
                .then(function (result) {
                    // Nothing came off, so this item is covered by a rule on its series or
                    // its folder and the row will still be greyed out.
                    if (result && result.Removed === 0) {
                        tell('Still ignored', 'This item is covered by a rule on its series or its folder. ' +
                            'Remove that rule under Settings, Ignore list.');
                    }
                })
            : lapsePost('Lapse/Ignore', { ItemId: itemId });

        request.then(function () {
            return Promise.all([refreshItemList(view), refreshIgnoreRules(view), refreshOverview(view)]);
        }).catch(function (err) {
            tell('Could not change the ignore list', err.message);
        });
    }

    function refreshItemList(view) {
        return lapseGet('Lapse/Status').then(function (items) {
            renderItemList(view, items);

            // the counts come from this list, so redraw them here, not in renderItemList (that runs per keystroke)
            renderOverview(view);
        });
    }

    function quickSync(view, itemId, name) {
        // the engine only takes one subtitle at a time, so ask which one if there are several
        lapseGet('Lapse/Items/' + itemId + '/Subtitles').then(function (subtitles) {
            if (subtitles.length === 0) {
                if (currentSettings && currentSettings.OpenSubtitlesEnabled) {
                    runSync(view, itemId, name, null);
                    return;
                }

                tell(name, 'There is no subtitle file to sync. Add one, or turn on fetching from OpenSubtitles under Experimental.');
                return;
            }

            if (subtitles.length === 1) {
                runSync(view, itemId, name, subtitles[0].Path);
                return;
            }

            openSubtitlePickerDialog(view, itemId, name, subtitles);
        }).catch(function (err) {
            tell(name, 'Could not read its subtitles. ' + err.message);
        });
    }

    // Shared by the quick Sync button and the advanced dialog, so that both get the same
    // retry-after-turning-on-force offer instead of it being wired up twice.
    function attemptSync(view, name, body) {
        Dashboard.showLoadingMsg();
        lapsePost('Lapse/Sync', body).then(function (result) {
            Dashboard.hideLoadingMsg();
            showSyncResultAlert(name, result, view, function () {
                attemptSync(view, name, body);
            });
            refreshItemList(view);
            refreshOverview(view);
            refreshReview(view);
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            tell(name, 'The sync failed. ' + err.message);
        });
    }

    // no Mode, the server uses the engine's default, same as the context menu button
    function runSync(view, itemId, name, subtitlePath) {
        attemptSync(view, name, { ItemId: itemId, SubtitlePath: subtitlePath });
    }

    function subtitleOptionsHtml(subtitles) {
        return subtitles.map(function (s) {
            return '<option value="' + escapeHtml(s.Path) + '">' + escapeHtml(s.DisplayName) + '</option>';
        }).join('');
    }

    // Jellyfin's own dialog, so it sizes, scrolls, keeps focus and closes on Back like every other one
    function openDialog(title, bodyHtml, submitLabel) {
        var dlg = Dashboard.dialogHelper.createDialog({ size: 'small', removeOnClose: true, scrollY: false });

        dlg.classList.add('formDialog');
        dlg.setAttribute('aria-label', title);
        dlg.innerHTML =
            '<div class="formDialogHeader">' +
            '  <button is="paper-icon-button-light" type="button" class="btnCancel autoSize" title="Close" aria-label="Close">' +
            '    <span class="material-icons arrow_back" aria-hidden="true"></span></button>' +
            '  <h3 class="formDialogHeaderTitle"></h3>' +
            '</div>' +
            '<div class="formDialogContent smoothScrollY">' +
            '  <div class="dialogContentInner dialog-content-centered">' + bodyHtml + '</div>' +
            '</div>' +
            (submitLabel
                ? '<div class="formDialogFooter">' +
                  '  <button is="emby-button" type="button" class="raised button-submit block formDialogFooterItem btnSubmit">' +
                  '<span>' + escapeHtml(submitLabel) + '</span></button>' +
                  '</div>'
                : '');

        dlg.querySelector('.formDialogHeaderTitle').textContent = title;
        dlg.querySelector('.btnCancel').addEventListener('click', function () {
            closeDialog(dlg);
        });

        Dashboard.dialogHelper.open(dlg);
        return dlg;
    }

    function closeDialog(dlg) {
        Dashboard.dialogHelper.close(dlg);
    }

    // A toast is gone before anything longer than "Saved" has been read, so results stay up until
    // closed. Jellyfin reads the message as HTML, hence the escaping.
    function tell(title, message) {
        Dashboard.alert({ title: title, message: escapeHtml(message).replace(/\n/g, '<br>') });
    }

    // Jellyfin's confirm, with the action named on the button instead of OK. Resolves true or false.
    function confirmAction(title, text, confirmText, destructive) {
        return new Promise(function (resolve) {
            Dashboard.confirm({
                title: title,
                text: escapeHtml(text),
                confirmText: confirmText,
                primary: destructive === false ? null : 'delete'
            }, title, resolve);
        });
    }

    function openSubtitlePickerDialog(view, itemId, name, subtitles) {
        var overlay = openDialog(name,
            '<div class="selectContainer">' +
            '  <label class="selectLabel" for="lapseQuickPickerSelect">Subtitle</label>' +
            '  <select is="emby-select" id="lapseQuickPickerSelect" class="emby-select-withcolor emby-select">' +
            subtitleOptionsHtml(subtitles) +
            '  </select>' +
            '</div>',
            'Sync');

        overlay.querySelector('.btnSubmit').addEventListener('click', function () {
            var path = overlay.querySelector('#lapseQuickPickerSelect').value;
            closeDialog(overlay);
            runSync(view, itemId, name, path);
        });
    }

    // "8.7 s later", "250 ms earlier", the same way the history puts it
    function distance(ms) {
        var size = Math.abs(ms);
        var text = size < 1000 ? size + ' ms' : (size / 1000).toFixed(size < 10000 ? 1 : 0) + ' s';
        return text + (ms < 0 ? ' earlier' : ' later');
    }

    function describeResult(result) {
        var parts = [];
        var split = result.PartOffsetsMs && result.PartOffsetsMs.length > 1 ? result.PartOffsetsMs : null;

        // LAPSE 2.3.0 says where each part went, older builds give one number for the lot
        if (split) {
            parts.push('moved in ' + split.length + ' parts, ' + split.map(distance).join(' then '));
        } else if (result.OffsetMs != null && result.OffsetMs !== 0) {
            parts.push('moved ' + distance(result.OffsetMs));
        }

        if (result.Slope != null) {
            parts.push('stretched to fit');
        }

        if (result.Snapped) {
            parts.push(result.Snapped + (result.Snapped === 1 ? ' line' : ' lines') + ' snapped onto picture cuts');
        }

        if (result.Reference === 'vad+whisper') {
            parts.push('checked against the words with whisper');
        }

        if (parts.length === 0 && result.EngineOutput) {
            // engines we don't have a documented output format for just report what they said
            parts.push(result.EngineOutput);
        }

        return parts.join(', ') || 'lined up';
    }

    function capitalize(text) {
        return text.charAt(0).toUpperCase() + text.slice(1);
    }

    function showSyncResultAlert(name, result, view, retry) {
        if (!result.Success) {
            tell(name, 'The sync failed. ' + result.Error);
            return;
        }

        if (result.AlreadyInSync) {
            tell(name, 'Already in sync, so the file was left as it was. LAPSE would have ' + describeResult(result) +
                ', which is inside the tolerance under File output.');
            return;
        }

        if (result.CandidateCount > 0) {
            tell(name, 'LAPSE was not sure, so the other engines had a go too. Their ' + result.CandidateCount +
                ' answers are waiting under Multi engine sync. Nothing was overwritten.');
            return;
        }

        if (result.Forced && (result.Skipped || result.Unconfirmed)) {
            tell(name, 'This subtitle has too few lines for LAPSE to check its answer, so the original was left alone.' +
                (result.Unconfirmed ? ' Its guess is waiting in Review.' : '') +
                ' A short track like this is best synced to the full subtitle with Sync to reference.');
            return;
        }

        // "nothing" means the subtitle doesn't belong to this video at all. Forcing it
        // through would only write a wrong subtitle, so that offer isn't made for it.
        if (result.Verdict === 'nothing' && (result.Skipped || result.Unconfirmed)) {
            tell(name, 'LAPSE could not match this subtitle to the audio, which nearly always means it was made for ' +
                'a different release. The original was left alone.' +
                (result.Unconfirmed ? ' Its guess is waiting in Review.' : ''));
            return;
        }

        if (result.Skipped) {
            confirmForceRetry(
                name + ': LAPSE ' + describeResult(result) + ' but was not sure, and File output is set to keep the original then.',
                result.EngineId, view, retry);
            return;
        }

        if (result.Unconfirmed || result.LowConfidence) {
            tell(name, capitalize(describeResult(result)) + ', but LAPSE was not sure, so the original was left alone ' +
                'and the result written beside it. It is waiting in Review.');
            return;
        }

        tell(name, 'Synced: ' + describeResult(result) + '.' +
            (result.OutputPath ? ' Written to ' + fileNameOf(result.OutputPath) + '.' : ''));
    }

    // --- advanced sync dialog ---

    function openAdvancedDialog(view, itemId, name) {
        lapseGet('Lapse/Items/' + itemId + '/Subtitles').then(function (subtitles) {
            showAdvancedDialog(view, itemId, name, subtitles);
        }).catch(function (err) {
            tell(name, 'Could not read its subtitles. ' + err.message);
        });
    }

    // The mode list comes from the engine itself now, so it only ever offers what that
    // engine can actually do rather than listing three fixed options and greying two out.
    function modeOptionsFor(engine, selected) {
        return engine.Modes.map(function (m) {
            return '<option value="' + escapeHtml(m.Value) + '"' +
                (m.Value === selected ? ' selected' : '') + '>' + escapeHtml(m.Label) + '</option>';
        }).join('');
    }

    function configuredProviders() {
        return allProviders.filter(function (p) { return p.Configured; });
    }

    // Only shown when the item has another copy to take a subtitle from. Loaded after the
    // dialog opens, so a slow lookup never holds the rest of it up.
    function fillCopySection(view, overlay, itemId, name) {
        var box = overlay.querySelector('#lapseAdvCopies');

        lapseGet('Lapse/Items/' + itemId + '/Copies').then(function (list) {
            var copies = (list && list.Copies) || [];
            if (!copies.length || !document.body.contains(box)) {
                return;
            }

            copies.sort(function (a, b) {
                return (b.SubtitleCount > 0) - (a.SubtitleCount > 0);
            });

            box.innerHTML =
                '<h3 class="lapseDialogSection">From another copy</h3>' +
                (list.CanMove
                    ? '<div class="selectContainer">' +
                      '  <label class="selectLabel" for="lapseAdvCopySource">The copy the subtitle fits</label>' +
                      '  <select is="emby-select" id="lapseAdvCopySource" class="emby-select-withcolor emby-select">' +
                      copies.map(function (c) {
                          return '<option value="' + escapeHtml(c.ItemId) + '">' + escapeHtml(c.Name) + '</option>';
                      }).join('') +
                      '  </select>' +
                      '  <div class="fieldDescription" id="lapseAdvCopyDetail"></div>' +
                      '</div>' +
                      '<div class="selectContainer">' +
                      '  <label class="selectLabel" for="lapseAdvCopySubtitle">Subtitle</label>' +
                      '  <select is="emby-select" id="lapseAdvCopySubtitle" class="emby-select-withcolor emby-select"></select>' +
                      '</div>' +
                      '<div class="fieldDescription">For a subtitle that fits the other copy but not this one. ' +
                      'The result is a new file named after this video.</div>' +
                      '<button is="emby-button" type="button" class="raised" id="lapseAdvMoveCopy" disabled><span>Move it across</span></button>'
                    : '<div class="fieldDescription">' + escapeHtml(list.Problem || '') + '</div>');

            box.classList.remove('hide');

            if (!list.CanMove) {
                return;
            }

            var sourceSelect = box.querySelector('#lapseAdvCopySource');
            var subtitleSelect = box.querySelector('#lapseAdvCopySubtitle');
            var moveButton = box.querySelector('#lapseAdvMoveCopy');

            function loadSubtitles() {
                var wanted = sourceSelect.value;
                var copy = copies.filter(function (c) { return c.ItemId === wanted; })[0];
                box.querySelector('#lapseAdvCopyDetail').textContent = copy
                    ? copy.Detail + ', ' + copy.SubtitleCount + (copy.SubtitleCount === 1 ? ' subtitle' : ' subtitles')
                    : '';

                subtitleSelect.innerHTML = '<option value="">Loading...</option>';
                moveButton.disabled = true;

                lapseGet('Lapse/Items/' + wanted + '/Subtitles').then(function (subtitles) {
                    if (sourceSelect.value !== wanted) {
                        return;
                    }

                    var usable = subtitles.filter(function (s) { return s.Supported !== false; });
                    subtitleSelect.innerHTML = usable.length
                        ? subtitleOptionsHtml(usable)
                        : '<option value="">That copy has no subtitle LAPSE can read</option>';
                    moveButton.disabled = usable.length === 0;
                }).catch(function (err) {
                    subtitleSelect.innerHTML = '<option value="">Could not list its subtitles: ' + escapeHtml(err.message) + '</option>';
                });
            }

            sourceSelect.addEventListener('change', loadSubtitles);
            loadSubtitles();

            moveButton.addEventListener('click', function () {
                var request = {
                    ItemId: itemId,
                    SourceItemId: sourceSelect.value,
                    SubtitlePath: subtitleSelect.value
                };

                closeDialog(overlay);
                Dashboard.showLoadingMsg();

                lapsePost('Lapse/SyncFromCopy', request).then(function (result) {
                    Dashboard.hideLoadingMsg();
                    tell(name, !result.Success
                        ? 'Could not move it. ' + result.Error
                        : result.Skipped
                            ? 'LAPSE could not line the two copies up well enough, so nothing was written. Check that both really are the same film.'
                            : result.Unconfirmed
                                ? 'LAPSE was not sure, so it was written as ' + fileNameOf(result.OutputPath || '') + ' and is waiting in Review.'
                                : 'Moved across as ' + fileNameOf(result.OutputPath || '') + '.');
                    return Promise.all([refreshItemList(view), refreshOverview(view)]);
                }).catch(function (err) {
                    Dashboard.hideLoadingMsg();
                    tell('Could not move it', err.message);
                });
            });
        }).catch(function () {
            // nothing to offer then, the rest of the dialog works without it
        });
    }

    function showAdvancedDialog(view, itemId, name, subtitles) {
        var startEngine = defaultEngine();
        if (!startEngine) {
            tell(name, 'No engine is installed. Install one under Settings, Engines.');
            return;
        }

        var subtitlePickerHtml = '';
        if (subtitles.length === 0) {
            subtitlePickerHtml = '<p class="fieldDescription">No external subtitle found for this item.</p>';
        } else if (subtitles.length > 1) {
            subtitlePickerHtml = '' +
                '<div class="selectContainer">' +
                '  <label class="selectLabel" for="lapseAdvSubtitle">Subtitle</label>' +
                '  <select is="emby-select" id="lapseAdvSubtitle" class="emby-select-withcolor emby-select">' +
                subtitleOptionsHtml(subtitles) +
                '  </select>' +
                '</div>';
        }

        var engineOptions = allEngines.map(function (e) {
            var usable = e.Installed && !e.RunCheckError;
            var label = usable ? e.DisplayName : e.DisplayName + ' (not installed)';
            return '<option value="' + escapeHtml(e.Id) + '"' +
                (usable ? '' : ' disabled') +
                (e.Id === startEngine.Id ? ' selected' : '') +
                '>' + escapeHtml(label) + '</option>';
        }).join('');

        var multiTrackHtml = subtitles.length > 1
            ? '<h3 class="lapseDialogSection">Sync to another subtitle</h3>' +
              '<div class="selectContainer">' +
              '  <label class="selectLabel" for="lapseAdvReference">Reference (the subtitle that is already correct)</label>' +
              '  <select is="emby-select" id="lapseAdvReference" class="emby-select-withcolor emby-select">' +
              subtitleOptionsHtml(subtitles) +
              '  </select>' +
              '</div>' +
              '<div class="selectContainer">' +
              '  <label class="selectLabel" for="lapseAdvReferenceTarget">Sync to it</label>' +
              '  <select is="emby-select" id="lapseAdvReferenceTarget" class="emby-select-withcolor emby-select">' +
              '    <option value="">Every other subtitle</option>' +
              subtitleOptionsHtml(subtitles) +
              '  </select>' +
              '</div>' +
              '<div class="fieldDescription">Faster than the audio and usually more accurate, as long as the reference is right.</div>' +
              '<button is="emby-button" type="button" class="raised" id="lapseAdvSyncAll"><span>Sync to reference</span></button>'
            : '';

        var providers = configuredProviders();
        var threshold = (currentSettings && currentSettings.TranslationConfidenceThreshold) || 70;

        // The languages set under Translation fill the boxes in, so a manual run starts
        // where the settings say rather than empty every time.
        var defaultTarget = (currentSettings &&
            (currentSettings.TranslationDefaultTargetLanguage || currentSettings.AutoTranslateLanguage)) || '';
        var defaultSource = (currentSettings && currentSettings.TranslationDefaultSourceLanguage) || '';

        var translationHtml = (subtitles.length > 0 && providers.length > 0)
            ? '<h3 class="lapseDialogSection">Translate</h3>' +
              '<div class="lapseFieldPair">' +
              '  <div class="inputContainer">' +
              '    <label class="inputLabel inputLabelUnfocused" for="lapseAdvSourceLang">From</label>' +
              '    <input is="emby-input" id="lapseAdvSourceLang" type="text" placeholder="auto" value="' +
              escapeHtml(defaultSource) + '" />' +
              '  </div>' +
              '  <div class="inputContainer">' +
              '    <label class="inputLabel inputLabelUnfocused" for="lapseAdvTargetLang">To</label>' +
              '    <input is="emby-input" id="lapseAdvTargetLang" type="text" placeholder="es" value="' +
              escapeHtml(defaultTarget) + '" />' +
              '  </div>' +
              '</div>' +
              '<div class="selectContainer">' +
              '  <label class="selectLabel" for="lapseAdvProvider">Provider</label>' +
              '  <select is="emby-select" id="lapseAdvProvider" class="emby-select-withcolor emby-select">' +
              providers.map(function (p) {
                  return '<option value="' + escapeHtml(p.Id) + '"' + (p.IsDefault ? ' selected' : '') + '>' +
                      escapeHtml(p.DisplayName) + '</option>';
              }).join('') +
              '  </select>' +
              '</div>' +
              '<div class="inputContainer">' +
              '  <label class="inputLabel inputLabelUnfocused" for="lapseAdvConfidence">Confidence threshold: <span id="lapseAdvConfidenceValue">' +
              threshold + '</span>%</label>' +
              '  <input type="range" id="lapseAdvConfidence" min="0" max="100" value="' + threshold + '" class="lapseRange" />' +
              '</div>' +
              '<div class="checkboxContainer"><label class="emby-checkbox-label">' +
              '  <input id="lapseAdvMetadataHeader" type="checkbox" is="emby-checkbox"' +
              ((currentSettings && currentSettings.TranslationIncludeMetadataHeader) ? ' checked' : '') + ' />' +
              '  <span>Add a comment at the top saying how it was translated</span>' +
              '</label></div>' +
              '<div class="fieldDescription">Writes a new file next to the original. .srt has no room for the comment block.</div>' +
              '<button is="emby-button" type="button" class="raised" id="lapseAdvTranslate"><span>Translate</span></button>'
            : '';

        var overlay = openDialog(name,
            '  <div class="selectContainer">' +
            '    <label class="selectLabel" for="lapseAdvEngine">Engine</label>' +
            '    <select is="emby-select" id="lapseAdvEngine" class="emby-select-withcolor emby-select">' + engineOptions + '</select>' +
            '  </div>' +
            '  <div class="selectContainer">' +
            '    <label class="selectLabel" for="lapseAdvMode">Mode</label>' +
            '    <select is="emby-select" id="lapseAdvMode" class="emby-select-withcolor emby-select">' +
            modeOptionsFor(startEngine, startEngine.DefaultMode) +
            '    </select>' +
            '    <div class="fieldDescription" id="lapseAdvModeNote"></div>' +
            '  </div>' +
            '  <div class="inputContainer hide" id="lapseAdvPenaltyContainer">' +
            '    <label class="inputLabel inputLabelUnfocused" for="lapseAdvPenalty">Penalty</label>' +
            '    <input is="emby-input" id="lapseAdvPenalty" type="number" value="' + startEngine.Penalty + '" />' +
            '    <div class="fieldDescription" id="lapseAdvPenaltyNote">Higher values = fewer splits.</div>' +
            '  </div>' +
            subtitlePickerHtml +
            '  <div class="selectContainer">' +
            '    <label class="selectLabel" for="lapseAdvFormat">Write the result as</label>' +
            '    <select is="emby-select" id="lapseAdvFormat" class="emby-select-withcolor emby-select">' +
            '      <option value="">Same format as the subtitle</option>' +
            '      <option value="srt">.srt</option>' +
            '      <option value="vtt">.vtt</option>' +
            '      <option value="ass">.ass</option>' +
            '      <option value="ssa">.ssa</option>' +
            '    </select>' +
            '    <div class="fieldDescription">Converts on the way out. The original file stays where it is.</div>' +
            '  </div>' +
            '  <button is="emby-button" type="button" class="raised button-submit" id="lapseAdvSync"' + (subtitles.length === 0 ? ' disabled' : '') + '><span>Sync</span></button>' +
            multiTrackHtml +
            '  <div id="lapseAdvCopies" class="hide"></div>' +
            translationHtml +
            '  <h3 class="lapseDialogSection">Shift by hand</h3>' +
            '  <div class="inputContainer">' +
            '    <label class="inputLabel inputLabelUnfocused" for="lapseAdvNudge">Seconds</label>' +
            '    <div class="lapseNudgeRow">' +
            '      <input is="emby-input" id="lapseAdvNudge" type="number" step="0.1" value="0" />' +
            '      <button is="emby-button" type="button" class="raised" id="lapseAdvNudgeApply"' + (subtitles.length === 0 ? ' disabled' : '') + '><span>Shift</span></button>' +
            '    </div>' +
            '    <div class="fieldDescription">Minus shows the subtitles earlier, plus later.</div>' +
            '  </div>');

        var engineSelect = overlay.querySelector('#lapseAdvEngine');
        var modeSelect = overlay.querySelector('#lapseAdvMode');
        var modeNote = overlay.querySelector('#lapseAdvModeNote');
        var penaltyContainer = overlay.querySelector('#lapseAdvPenaltyContainer');
        var penaltyInput = overlay.querySelector('#lapseAdvPenalty');
        var penaltyNote = overlay.querySelector('#lapseAdvPenaltyNote');

        fillCopySection(view, overlay, itemId, name);

        function currentEngine() {
            return findEngine(engineSelect.value) || startEngine;
        }

        function syncModeState() {
            var engine = currentEngine();
            var isSplit = modeSelect.value === 'Split';

            penaltyContainer.classList.toggle('hide', !(isSplit && engine.SupportsPenalty));
            penaltyNote.textContent = 'Higher values = fewer splits. ' + engine.DisplayName +
                ' takes ' + engine.MinPenalty + ' to ' + engine.MaxPenalty + ', default ' + engine.Penalty + '.';

            modeNote.textContent = '';
            engine.Modes.forEach(function (m) {
                if (m.Value === modeSelect.value) {
                    modeNote.textContent = m.Description;
                }
            });
        }

        engineSelect.addEventListener('change', function () {
            var engine = currentEngine();
            var wanted = modeSelect.value;

            // rebuild the modes for the newly picked engine, and if the mode that was
            // selected isn't something this engine can do, drop back to its own default
            modeSelect.innerHTML = modeOptionsFor(engine, wanted);
            if (modeSelect.selectedIndex === -1) {
                modeSelect.value = engine.DefaultMode;
            }

            penaltyInput.value = engine.Penalty;
            penaltyInput.min = engine.MinPenalty;
            penaltyInput.max = engine.MaxPenalty;
            syncModeState();
        });

        modeSelect.addEventListener('change', syncModeState);
        syncModeState();

        function selectedSubtitlePath() {
            if (subtitles.length === 0) {
                return null;
            }

            return subtitles.length > 1 ? overlay.querySelector('#lapseAdvSubtitle').value : subtitles[0].Path;
        }

        function currentPenalty() {
            var engine = currentEngine();
            return modeSelect.value === 'Split' && engine.SupportsPenalty
                ? (parseInt(penaltyInput.value, 10) || engine.Penalty)
                : 0;
        }

        overlay.querySelector('#lapseAdvSync').addEventListener('click', function () {
            var body = {
                ItemId: itemId,
                EngineId: currentEngine().Id,
                Mode: modeSelect.value,
                Penalty: currentPenalty(),
                SubtitlePath: selectedSubtitlePath(),
                OutputFormat: overlay.querySelector('#lapseAdvFormat').value || null
            };
            closeDialog(overlay);
            attemptSync(view, name, body);
        });

        var syncAllButton = overlay.querySelector('#lapseAdvSyncAll');
        if (syncAllButton) {
            syncAllButton.addEventListener('click', function () {
                var referencePath = overlay.querySelector('#lapseAdvReference').value;
                var targetPath = overlay.querySelector('#lapseAdvReferenceTarget').value;

                if (targetPath && targetPath === referencePath) {
                    tell('Sync to reference', 'That is the reference itself. Pick another subtitle, or sync every other one.');
                    return;
                }

                Dashboard.showLoadingMsg();
                lapsePost('Lapse/SyncAllSubtitles', {
                    ItemId: itemId,
                    ReferencePath: referencePath,
                    SubtitlePaths: targetPath ? [targetPath] : null,
                    EngineId: currentEngine().Id,
                    Mode: modeSelect.value,
                    Penalty: currentPenalty()
                }).then(function (result) {
                    Dashboard.hideLoadingMsg();
                    closeDialog(overlay);
                    showMultiSyncAlert(name, result);
                    refreshItemList(view);
                    refreshOverview(view);
                }).catch(function (err) {
                    Dashboard.hideLoadingMsg();
                    tell('Could not sync the other subtitles', err.message);
                });
            });
        }

        var confidenceSlider = overlay.querySelector('#lapseAdvConfidence');
        if (confidenceSlider) {
            confidenceSlider.addEventListener('input', function () {
                overlay.querySelector('#lapseAdvConfidenceValue').textContent = confidenceSlider.value;
            });
        }

        var translateButton = overlay.querySelector('#lapseAdvTranslate');
        if (translateButton) {
            translateButton.addEventListener('click', function () {
                var target = (overlay.querySelector('#lapseAdvTargetLang').value || '').trim();
                if (!target) {
                    tell('Translate', 'Enter the language to translate into first, for example es for Spanish.');
                    return;
                }

                Dashboard.showLoadingMsg();
                lapsePost('Lapse/Translate', {
                    ItemId: itemId,
                    SubtitlePath: selectedSubtitlePath(),
                    SourceLanguage: (overlay.querySelector('#lapseAdvSourceLang').value || '').trim() || null,
                    TargetLanguage: target,
                    Provider: overlay.querySelector('#lapseAdvProvider').value,
                    ConfidenceThreshold: parseInt(confidenceSlider.value, 10),
                    IncludeMetadataHeader: overlay.querySelector('#lapseAdvMetadataHeader').checked
                }).then(function (result) {
                    Dashboard.hideLoadingMsg();
                    showTranslationAlert(name, result);
                }).catch(function (err) {
                    Dashboard.hideLoadingMsg();
                    tell('Translation failed', err.message);
                });
            });
        }

        var nudgeButton = overlay.querySelector('#lapseAdvNudgeApply');
        if (nudgeButton) {
            nudgeButton.addEventListener('click', function () {
                var offset = parseFloat(overlay.querySelector('#lapseAdvNudge').value);
                if (!offset) {
                    tell('Shift by hand', 'Enter how many seconds to move the subtitle first.');
                    return;
                }

                Dashboard.showLoadingMsg();
                lapsePost('Lapse/Shift', {
                    ItemId: itemId,
                    SubtitlePath: selectedSubtitlePath(),
                    OffsetSeconds: offset
                }).then(function (result) {
                    Dashboard.hideLoadingMsg();
                    tell(name, 'Shifted ' + distance(Math.round(offset * 1000)) + '. Written to ' + fileNameOf(result.OutputPath) +
                        ', and it can be undone from Recent activity.');
                    refreshOverview(view);
                }).catch(function (err) {
                    Dashboard.hideLoadingMsg();
                    tell('Could not shift the subtitle', err.message);
                });
            });
        }
    }

    function showMultiSyncAlert(name, result) {
        var lines = result.Results.map(function (outcome) {
            var r = outcome.Result || {};
            return outcome.DisplayName + ': ' + (r.Success ? describeResult(r) : ('failed - ' + r.Error));
        });

        tell(name, result.SucceededCount + ' of ' + result.Results.length +
            ' subtitles synced to the reference.\n\n' + lines.join('\n'));
    }

    function showTranslationAlert(name, result) {
        if (!result.Success) {
            tell(name, 'The translation failed. ' + result.Error);
            return;
        }

        tell(name, 'Translated ' + result.TranslatedCount + ' of ' + result.LineCount + ' lines with ' + result.Provider +
            ', ' + result.LowConfidenceCount + ' of them below the confidence threshold. Written to ' + fileNameOf(result.OutputPath) + '.');
    }

    // --- bulk sync + folders ---

    function refreshFolders(view) {
        return lapseGet('Lapse/Folders').then(function (folders) {
            view.querySelector('#lapseFolderSelect').innerHTML = folders.map(function (folder) {
                return '<option value="' + folder.ItemId + '">' + escapeHtml(folder.Name) + (folder.Skipped ? ' (skipped)' : '') + '</option>';
            }).join('');
        });
    }

    function startBulkSync(view, scope, folderId) {
        var body = { Scope: scope };
        if (folderId) {
            body.FolderId = folderId;
        }

        lapsePost('Lapse/BulkSync', body).then(function () {
            startQueuePolling(view);
            refreshQueue(view);
        }).catch(function (err) {
            tell('Could not start sync', err.message);
        });
    }

    function refreshRuns(view) {
        return lapseGet('Lapse/Runs').then(function (runs) {
            var container = view.querySelector('#lapseRuns');

            container.innerHTML = runs.length === 0
                ? '<p class="fieldDescription">No bulk run has written anything yet. Each run shows up here with a way to undo it.</p>'
                : '<div class="paperList">' + runs.map(function (run) {
                    var undone = run.Undone >= run.Files;
                    var action = undone
                        ? ''
                        : '<div class="lapseRowActions"><button is="emby-button" type="button" class="raised lapseBtnUndoRun"' +
                          ' data-id="' + escapeHtml(run.Id) + '"><span>Undo run</span></button></div>';

                    return '<div class="listItem listItem-border">' +
                        '<div class="listItemBody two-line">' +
                        '<div class="listItemBodyText">' + escapeHtml(run.Name) + ', ' + escapeHtml(timeAgo(run.FinishedUtc)) + '</div>' +
                        '<div class="listItemBodyText secondary">' + (undone ? 'Undone. ' : '') + run.Synced + ' synced, ' + run.Unsure + ' unsure, ' +
                        run.Failed + ' failed, ' + run.Skipped + ' skipped, ' + run.Files + (run.Files === 1 ? ' file' : ' files') + ' written.</div>' +
                        '</div>' + action + '</div>';
                }).join('') + '</div>';

            container.querySelectorAll('.lapseBtnUndoRun').forEach(function (button) {
                button.addEventListener('click', function () {
                    undoRun(view, button);
                });
            });
        });
    }

    function undoRun(view, button) {
        confirmAction('Undo this run', 'Every file the run wrote is put back the way it was. ' +
            'A file written again since the run is left alone.', 'Undo run').then(function (yes) {
            if (yes) {
                undoRunConfirmed(view, button);
            }
        });
    }

    function undoRunConfirmed(view, button) {
        button.disabled = true;
        lapsePost('Lapse/Runs/' + button.getAttribute('data-id') + '/Undo').then(function (files) {
            var left = files.filter(function (file) { return !file.Reverted; });
            var lines = ['Put back ' + (files.length - left.length) + ' of ' + files.length + ' files.'];

            // one line per file that's still as the run left it, a whole library of them won't fit
            left.slice(0, 20).forEach(function (file) {
                lines.push(file.OutputPath + ': ' + file.Outcome);
            });
            if (left.length > 20) {
                lines.push('...and ' + (left.length - 20) + ' more.');
            }

            tell('Undo run', lines.join('\n'));
            return Promise.all([refreshRuns(view), refreshItemList(view), refreshOverview(view)]);
        }).catch(function (err) {
            button.disabled = false;
            tell('Could not undo that run', err.message);
        });
    }

    function toggleSkipFolder(view) {
        var select = view.querySelector('#lapseFolderSelect');
        var option = select.options[select.selectedIndex];
        if (!option) {
            return;
        }

        var isSkipped = option.textContent.indexOf('(skipped)') !== -1;
        lapsePost('Lapse/Skip', { ItemId: option.value, Skip: !isSkipped }).then(function () {
            return Promise.all([refreshFolders(view), refreshItemList(view)]);
        }).catch(function (err) {
            tell('Could not change that', err.message);
        });
    }

    // --- ignore list ---

    function renderIgnoreList(view) {
        var container = view.querySelector('#lapseIgnoreList');

        view.querySelector('#lapseIgnoreHint').textContent =
            allIgnoreRules.length === 0 ? '' : (allIgnoreRules.length + ' rules');

        if (allIgnoreRules.length === 0) {
            container.innerHTML = '<p class="fieldDescription">Nothing is ignored.</p>';
            return;
        }

        container.innerHTML = allIgnoreRules.map(function (rule) {
            return '' +
                '<div class="listItem listItem-border lapseIgnoreRow" data-item="' + escapeHtml(rule.ItemId || '') +
                '" data-path="' + escapeHtml(rule.Path || '') + '">' +
                '  <div class="listItemBody two-line">' +
                '    <div class="listItemBodyText">' + escapeHtml(rule.DisplayName) + '</div>' +
                '    <div class="listItemBodyText secondary">' + escapeHtml(rule.Kind || '') + '</div>' +
                '  </div>' +
                '  <div class="lapseRowActions">' +
                '    <button is="emby-button" type="button" class="raised lapseBtnUnignore"><span>Remove</span></button>' +
                '  </div>' +
                '</div>';
        }).join('');

        container.querySelectorAll('.lapseIgnoreRow').forEach(function (row) {
            row.querySelector('.lapseBtnUnignore').addEventListener('click', function () {
                var itemId = row.getAttribute('data-item');
                var path = row.getAttribute('data-path');
                var query = itemId
                    ? 'itemId=' + encodeURIComponent(itemId)
                    : 'path=' + encodeURIComponent(path);

                lapseDelete('Lapse/Ignore?' + query).then(function () {
                    return Promise.all([refreshIgnoreRules(view), refreshItemList(view), refreshOverview(view)]);
                }).catch(function (err) {
                    tell('Could not remove that rule', err.message);
                });
            });
        });
    }

    function refreshIgnoreRules(view) {
        return lapseGet('Lapse/Ignore').then(function (rules) {
            allIgnoreRules = rules || [];
            renderIgnoreList(view);
        });
    }

    // Searches the sync status list, which covers films, episodes and loose videos.
    // Adding a whole series is done by ignoring its folder.
    //
    // The list may still be loading on a big library (or failed), and searching an empty array said
    // "nothing matches", which looked like a dead button. Fetch it first if it's empty.
    function searchForIgnore(view) {
        var container = view.querySelector('#lapseIgnoreSearchResults');
        var term = (view.querySelector('#lapseIgnoreSearch').value || '').trim().toLowerCase();

        if (!term) {
            container.innerHTML = '';
            return;
        }

        // Only worth one attempt: a library that really is empty must not send this round
        // again on the next keystroke, or on the call the fetch itself makes below.
        if (allItems.length === 0 && !ignoreSearchFetched) {
            if (ignoreSearchPending) {
                return;
            }

            ignoreSearchPending = true;
            ignoreSearchFetched = true;
            container.innerHTML = '<div class="fieldDescription">Looking through the library...</div>';

            refreshItemList(view).then(function () {
                ignoreSearchPending = false;

                // Whatever is in the box now, not what was in it when the fetch started.
                searchForIgnore(view);
            }).catch(function (err) {
                ignoreSearchPending = false;

                // A failed read is worth retrying, unlike an empty one.
                ignoreSearchFetched = false;
                container.innerHTML = '<div class="fieldDescription">Could not read the library: ' +
                    escapeHtml(err.message || 'unknown error') + '</div>';
            });

            return;
        }

        renderIgnoreMatches(view, container, term);
    }

    function renderIgnoreMatches(view, container, term) {
        var matches = allItems.filter(function (i) {
            return (i.Name || '').toLowerCase().indexOf(term) !== -1 && i.Status !== 'Ignored';
        }).slice(0, 25);

        if (matches.length === 0) {
            container.innerHTML = '<div class="fieldDescription">' +
                'Nothing matches that. Only items in a library that is turned on under Settings, ' +
                'Libraries can be searched here - use the path box below for anything else.</div>';
            return;
        }

        container.innerHTML = matches.map(function (item) {
            return '<div class="listItem listItem-border lapseIgnoreCandidate" data-id="' + escapeHtml(item.ItemId) + '">' +
                '<div class="listItemBody two-line">' +
                '<div class="listItemBodyText">' + escapeHtml(item.Name) + '</div>' +
                '<div class="listItemBodyText secondary">' + escapeHtml(item.ItemType) + '</div>' +
                '</div>' +
                '<div class="lapseRowActions"><button is="emby-button" type="button" class="raised lapseBtnIgnoreAdd"><span>Ignore</span></button></div>' +
                '</div>';
        }).join('');

        container.querySelectorAll('.lapseIgnoreCandidate').forEach(function (row) {
            row.querySelector('.lapseBtnIgnoreAdd').addEventListener('click', function () {
                lapsePost('Lapse/Ignore', { ItemId: row.getAttribute('data-id') }).then(function () {
                    return Promise.all([refreshIgnoreRules(view), refreshItemList(view), refreshOverview(view)]);
                }).then(function () {
                    searchForIgnore(view);
                }).catch(function (err) {
                    tell('Could not add that', err.message);
                });
            });
        });
    }

    // --- subtitle to subtitle sync ---

    // suggest input name + sidecar suffix, only a suggestion, an edited name is left alone
    function suggestSubToSubOutput(inputPath) {
        if (!inputPath) {
            return '';
        }

        var suffix = (currentSettings && currentSettings.SidecarSuffix) || '.shifted';
        if (suffix.charAt(0) !== '.') {
            suffix = '.' + suffix;
        }

        var name = inputPath.split(/[\\/]/).pop();
        var dot = name.lastIndexOf('.');

        if (dot <= 0) {
            return name + suffix;
        }

        var stem = name.substring(0, dot);
        var extension = name.substring(dot);

        if (stem.toLowerCase().endsWith(suffix.toLowerCase())) {
            stem = stem.substring(0, stem.length - suffix.length);
        }

        return stem + suffix + extension;
    }

    function updateSubToSubOutput(view, force) {
        var output = view.querySelector('#lapseSubToSubOutput');
        var suggestion = suggestSubToSubOutput(view.querySelector('#lapseInputSubPath').value);

        // don't stomp on a name the user typed themselves
        if (force || !output.value || output.value === output.getAttribute('data-suggested')) {
            output.value = suggestion;
        }

        output.setAttribute('data-suggested', suggestion);

        var wantsNewFile = view.querySelector('#lapseSubToSubNewFile').checked;
        view.querySelector('#lapseSubToSubOutputContainer').classList.toggle('hide', !wantsNewFile);

        updateSubToSubPlacement(view);
    }

    function updateSubToSubPlacement(view) {
        var placement = view.querySelector('#lapseSubToSubPlacement').value;
        var referenceFolder = folderOf(view.querySelector('#lapseRefSubPath').value);
        var inputFolder = folderOf(view.querySelector('#lapseInputSubPath').value);

        view.querySelector('#lapseSubToSubCustomContainer').classList.toggle('hide', placement !== 'CustomFolder');

        var note;
        if (placement === 'InputFolder') {
            note = inputFolder ? ('Goes in ' + inputFolder) : 'Goes wherever the input subtitle is.';
        } else if (placement === 'CustomFolder') {
            note = 'Goes in the folder below.';
        } else {
            note = referenceFolder
                ? ('Goes in ' + referenceFolder)
                : 'Goes wherever the reference subtitle is. That is the file already sitting next to its video, ' +
                  'so a result there is one Jellyfin will pick up as another track.';
        }

        view.querySelector('#lapseSubToSubPlacementNote').textContent = note;
    }

    function folderOf(path) {
        if (!path) {
            return '';
        }

        var cut = Math.max(path.lastIndexOf('/'), path.lastIndexOf('\\'));
        return cut > 0 ? path.substring(0, cut) : '';
    }

    function updateSubToSubEngineNote(view) {
        var engine = defaultEngine();
        var note = view.querySelector('#lapseSubToSubEngineNote');

        if (!engine) {
            note.textContent = '';
            return;
        }

        note.textContent = 'Runs ' + engine.DisplayName + ' in single offset mode. Matching a subtitle ' +
            'against another subtitle skips the audio entirely, so it is quick.';
    }

    function syncSubtitles(view) {
        var referencePath = view.querySelector('#lapseRefSubPath').value;
        var inputPath = view.querySelector('#lapseInputSubPath').value;

        if (!referencePath || !inputPath) {
            tell('Subtitle to subtitle', 'Pick both a reference and an input subtitle first.');
            return;
        }

        var body = { ReferencePath: referencePath, InputPath: inputPath };

        if (view.querySelector('#lapseSubToSubNewFile').checked) {
            var outputPath = (view.querySelector('#lapseSubToSubOutput').value || '').trim();
            if (!outputPath) {
                tell('Subtitle to subtitle', 'Give the new subtitle file a name, or untick the box to overwrite the input.');
                return;
            }

            body.OutputPath = outputPath;
            body.Placement = view.querySelector('#lapseSubToSubPlacement').value;
        }

        attemptSubToSubSync(view, body);
    }

    function attemptSubToSubSync(view, body) {
        Dashboard.showLoadingMsg();
        lapsePost('Lapse/SyncSubtitles', body).then(function (result) {
            Dashboard.hideLoadingMsg();
            if (!result.Success) {
                tell('Subtitle to subtitle', 'The sync failed. ' + result.Error);
            } else if (result.AlreadyInSync) {
                tell('Subtitle to subtitle', 'Already in sync, so the input was left as it was. LAPSE would have ' +
                    describeResult(result) + ', which is inside the tolerance under File output.');
            } else if (result.Skipped) {
                confirmForceRetry(
                    'Left the input alone (' + describeResult(result) + '), which is under the confidence threshold.',
                    result.EngineId, view, function () {
                        attemptSubToSubSync(view, body);
                    });
            } else if (result.Unconfirmed) {
                tell('Subtitle to subtitle', capitalize(describeResult(result)) + ', but LAPSE was not sure. ' +
                    'Written to ' + result.OutputPath + ' for you to check, without touching any file already there.');
            } else {
                tell('Subtitle to subtitle', 'Synced: ' + describeResult(result) + '. Written to ' + result.OutputPath + '.');
            }
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            tell('Sync failed', err.message);
        });
    }

    // --- settings: output, translation, appearance, engines ---

    // `options.hideRecommended` leaves the word out but keeps the default picked
    function renderRadioGroup(container, name, modes, selected, onChange, options) {
        var showRecommended = !(options && options.hideRecommended);

        // no match = value gone or never set, use the recommended one so a dot always shows
        var hasSelection = modes.some(function (mode) { return mode.value === selected; });
        if (!hasSelection) {
            var fallback = modes.filter(function (mode) { return mode.recommended; })[0] || modes[0];
            selected = fallback && fallback.value;
        }

        container.innerHTML = modes.map(function (mode) {
            return '' +
                '<label class="lapseRadioRow">' +
                '  <input type="radio" name="' + name + '" value="' + escapeHtml(mode.value) + '"' +
                (selected === mode.value ? ' checked' : '') + ' />' +
                '  <span><span>' + escapeHtml(mode.label) + '</span>' +
                '    <span class="fieldDescription">' + (mode.recommended && showRecommended ? 'Recommended. ' : '') +
                escapeHtml(mode.note) + '</span>' +
                '  </span>' +
                '</label>';
        }).join('');

        if (onChange) {
            container.querySelectorAll('input[name="' + name + '"]').forEach(function (radio) {
                radio.addEventListener('change', onChange);
            });
        }
    }

    function selectedRadio(view, name, fallback) {
        var checked = view.querySelector('input[name="' + name + '"]:checked');
        return checked ? checked.value : fallback;
    }

    function selectedOutputMode(view) {
        return selectedRadio(view, 'lapseOutputMode', 'SidecarOnly');
    }

    function updateSidecarPreview(view) {
        var mode = selectedOutputMode(view);
        var suffix = view.querySelector('#lapseSidecarSuffix').value || '.shifted';
        if (suffix.charAt(0) !== '.') {
            suffix = '.' + suffix;
        }

        var preview = mode.indexOf('Sidecar') === 0
            ? 'Movie.en.srt becomes Movie.en' + suffix + '.srt'
            : 'Only used when a new file is written.';

        view.querySelector('#lapseSidecarPreview').textContent = preview;
        view.querySelector('#lapseOutputHint').textContent =
            (OUTPUT_MODES.filter(function (m) { return m.value === mode; })[0] || {}).label || '';
    }

    function updateConfidenceNote(view) {
        var value = parseFloat(view.querySelector('#lapseConfidenceSigma').value);
        view.querySelector('#lapseConfidenceSigmaValue').textContent = value;

        var note;
        if (value >= 12) {
            note = 'Strict: more results go to Review. LAPSE only, 8 is its own default.';
        } else if (value >= 6) {
            note = 'How clearly an answer has to stand out before LAPSE writes it. LAPSE only, 8 is its own default.';
        } else {
            note = 'Loose: fewer results go to Review, and some of those written are wrong. LAPSE only, 8 is its own default.';
        }

        view.querySelector('#lapseConfidenceSigmaNote').textContent = note;
    }

    function updateAlreadyInSyncNote(view) {
        var on = view.querySelector('#lapseSkipAlreadyInSync').checked;
        var row = view.querySelector('#lapseAlreadyInSyncToleranceRow');
        var value = parseInt(view.querySelector('#lapseAlreadyInSyncTolerance').value, 10) || 0;

        // zero still means something (only an exact 0 offset is left alone), so the slider stays visible
        row.classList.toggle('lapseDimmed', !on);
        view.querySelector('#lapseAlreadyInSyncTolerance').disabled = !on;
        view.querySelector('#lapseAlreadyInSyncToleranceValue').textContent = value + ' ms';
    }

    // No Recommended badges, the right format depends on what reads the file. srt is still pre-picked
    // on a fresh install or when the stored value is blank/unknown (the generic fallback would
    // just pick the first entry).
    function conversionFormat(stored) {
        var value = String(stored || '').trim().replace(/^\./, '').toLowerCase();
        var known = CONVERSION_FORMATS.some(function (f) { return f.value === value; });
        return known ? value : 'srt';
    }

    function renderConversion(view) {
        var settings = currentSettings || {};
        var plain = { hideRecommended: true };

        renderRadioGroup(
            view.querySelector('#lapseConversionFormats'),
            'lapseConversionFormat',
            CONVERSION_FORMATS,
            conversionFormat(settings.ConversionFormat),
            function () { updateConversionHint(view); },
            plain);

        renderRadioGroup(
            view.querySelector('#lapseConversionOriginal'),
            'lapseConversionOriginal',
            CONVERSION_ORIGINAL_MODES,
            settings.ConversionReplaceOriginal ? 'replace' : 'keep',
            null,
            plain);

        renderRadioGroup(
            view.querySelector('#lapseConvertBeforeSync'),
            'lapseConvertBeforeSync',
            CONVERT_BEFORE_SYNC_MODES,
            settings.ConvertBeforeSync ? 'on' : 'off',
            null,
            plain);

        renderRadioGroup(
            view.querySelector('#lapseConversionSync'),
            'lapseConversionSync',
            CONVERSION_SYNC_MODES,
            settings.ConversionSyncAfter === false ? 'convert' : 'sync',
            null,
            plain);

        renderConversionEngineNote(view);
        updateConversionHint(view);
    }

    // Says which formats the engine in use actually reads, so "do I need to convert this"
    // is answered on the page rather than guessed at.
    function renderConversionEngineNote(view) {
        var note = view.querySelector('#lapseConversionEngineNote');
        if (!note) {
            return;
        }

        var engine = allEngines.filter(function (e) { return e.IsDefault; })[0];
        var formats = (engine && engine.SubtitleExtensions) || [];

        if (!engine || formats.length === 0) {
            note.classList.add('hide');
            return;
        }

        note.classList.remove('hide');
        note.textContent = engine.DisplayName + ' reads ' + formats.join(', ') + ' and writes each back in its own format, ' +
            'so converting is only needed for one format across the library.';
    }

    function updateConversionHint(view) {
        view.querySelector('#lapseConversionHint').textContent =
            '.' + selectedRadio(view, 'lapseConversionFormat', 'srt');
    }

    function renderAutomation(view) {
        var settings = currentSettings || {};

        renderRadioGroup(
            view.querySelector('#lapseAutomationActions'),
            'lapseAutomationAction',
            AUTOMATION_ACTIONS,
            settings.AutomationAction || 'Sync',
            function () { updateAutomationHint(view); },
            { hideRecommended: true });

        view.querySelector('#lapseAutoTranslateEnabled').checked = settings.AutoTranslateEnabled === true;
        view.querySelector('#lapseAutoTranslateLanguage').value = settings.AutoTranslateLanguage || '';
        view.querySelector('#lapseAutoTranslateSkipExisting').checked = settings.AutoTranslateSkipExisting !== false;
        view.querySelector('#lapseSkipSyncedInUnattendedRuns').checked = settings.SkipSyncedInUnattendedRuns !== false;
        view.querySelector('#lapseAutoSyncNewSubtitles').checked = settings.AutoSyncNewSubtitles !== false;

        updateAutomationHint(view);
    }

    function updateAutomationHint(view) {
        var action = selectedRadio(view, 'lapseAutomationAction', 'Sync');
        var label = AUTOMATION_ACTIONS.filter(function (a) { return a.value === action; })[0];
        var translating = view.querySelector('#lapseAutoTranslateEnabled').checked
            && view.querySelector('#lapseAutoTranslateLanguage').value.trim();

        view.querySelector('#lapseAutomationHint').textContent =
            (label ? label.label : 'Sync them') + (translating ? ' + translate' : '');
    }

    function renderAccess(view) {
        var mode = (currentSettings && currentSettings.SubtitleAccess) || 'AdminsOnly';

        renderRadioGroup(
            view.querySelector('#lapseAccessModes'),
            'lapseAccessMode',
            ACCESS_MODES,
            mode,
            function () { updateAccessPanel(view); });

        renderAccessUsers(view);
        updateAccessPanel(view);
    }

    function renderAccessUsers(view) {
        var container = view.querySelector('#lapseAccessUsers');
        var allowed = (currentSettings && currentSettings.SubtitleAccessUserIds) || [];

        var pickable = currentUsers.filter(function (user) { return !user.IsAdministrator; });

        if (!pickable.length) {
            container.innerHTML = '<div class="fieldDescription">This server has no non-administrator users.</div>';
            return;
        }

        container.innerHTML = pickable.map(function (user) {
            var checked = allowed.some(function (id) { return sameId(id, user.Id); });

            return '' +
                '<div class="checkboxContainer"><label class="emby-checkbox-label">' +
                '  <input type="checkbox" is="emby-checkbox" class="lapseAccessUser" data-id="' + escapeHtml(user.Id) + '"' +
                (checked ? ' checked' : '') + ' />' +
                '  <span>' + escapeHtml(user.Name) + (user.CanManageSubtitles ? ' (can manage subtitles)' : '') + '</span>' +
                '</label></div>';
        }).join('');
    }

    function updateAccessPanel(view) {
        var mode = selectedRadio(view, 'lapseAccessMode', 'AdminsOnly');
        var chosen = ACCESS_MODES.filter(function (m) { return m.value === mode; })[0];

        view.querySelector('#lapseAccessUsersContainer').classList.toggle('hide', mode !== 'SelectedUsers');
        view.querySelector('#lapseAccessHint').textContent = chosen ? chosen.label : '';
    }

    function selectedAccessUserIds(view) {
        var ids = [];

        view.querySelectorAll('.lapseAccessUser').forEach(function (input) {
            if (input.checked) {
                ids.push(input.getAttribute('data-id'));
            }
        });

        return ids;
    }

    function refreshUsers(view) {
        return lapseGet('Lapse/Users').then(function (users) {
            currentUsers = users || [];
            renderAccessUsers(view);
        }).catch(function () {
            // Not fatal: the rest of the access panel still works, the picker just can't
            // be filled in.
            currentUsers = [];
        });
    }

    function renderSettings(view) {
        renderRadioGroup(
            view.querySelector('#lapseOutputModes'),
            'lapseOutputMode',
            OUTPUT_MODES,
            currentSettings.OutputMode,
            function () { updateSidecarPreview(view); });

        renderRadioGroup(
            view.querySelector('#lapseLowConfidenceModes'),
            'lapseLowConfidence',
            LOW_CONFIDENCE_MODES,
            currentSettings.LowConfidenceAction);

        view.querySelector('#lapseSidecarSuffix').value = currentSettings.SidecarSuffix || '.shifted';
        view.querySelector('#lapseConfidenceSigma').value = currentSettings.ConfidenceSigma || 8;
        updateConfidenceNote(view);

        view.querySelector('#lapseSkipAlreadyInSync').checked = currentSettings.SkipAlreadyInSync !== false;
        view.querySelector('#lapseAlreadyInSyncTolerance').value =
            currentSettings.AlreadyInSyncToleranceMs == null ? 100 : currentSettings.AlreadyInSyncToleranceMs;
        updateAlreadyInSyncNote(view);

        view.querySelector('#lapseConfidence').value = currentSettings.TranslationConfidenceThreshold;
        view.querySelector('#lapseKeepLowConfidence').checked = !!currentSettings.TranslationKeepLowConfidenceOriginal;
        view.querySelector('#lapseMetadataHeader').checked = !!currentSettings.TranslationIncludeMetadataHeader;
        view.querySelector('#lapseCountEmbedded').checked = !!currentSettings.CountEmbeddedSubtitlesInStatus;
        view.querySelector('#lapseDefaultTargetLanguage').value = currentSettings.TranslationDefaultTargetLanguage || '';
        view.querySelector('#lapseDefaultSourceLanguage').value = currentSettings.TranslationDefaultSourceLanguage || '';

        view.querySelector('#lapseStyleFontName').value = currentSettings.SubtitleFontName || 'OpenDyslexic';
        view.querySelector('#lapseStyleNonLatinFontName').value = currentSettings.SubtitleNonLatinFontName || '';
        view.querySelector('#lapseReadableAutomation').value = currentSettings.ReadableAutomation || 'Off';
        view.querySelector('#lapseStyleFontSize').value = currentSettings.SubtitleFontSize || 78;
        view.querySelector('#lapseStyleLetterSpacing').value =
            currentSettings.SubtitleLetterSpacing === undefined ? 2 : currentSettings.SubtitleLetterSpacing;
        view.querySelector('#lapseStyleOutline').value =
            currentSettings.SubtitleOutline === undefined ? 3.5 : currentSettings.SubtitleOutline;
        view.querySelector('#lapseStyleMarginV').value =
            currentSettings.SubtitleMarginV === undefined ? 70 : currentSettings.SubtitleMarginV;
        view.querySelector('#lapseStyleBold').checked = !!currentSettings.SubtitleBold;

        updateReadableLabels(view);

        view.querySelector('#lapseSubToSubPlacement').value = currentSettings.SubToSubPlacement || 'ReferenceFolder';
        view.querySelector('#lapseSubToSubCustomFolder').value = currentSettings.SubToSubCustomFolder || '';

        view.querySelector('#lapseOpenSubtitlesEnabled').checked = !!currentSettings.OpenSubtitlesEnabled;
        view.querySelector('#lapseOpenSubtitlesLanguage').value = currentSettings.OpenSubtitlesLanguage || 'en';
        view.querySelector('#lapseOpenSubtitlesApiKey').value = currentSettings.OpenSubtitlesApiKey || '';
        view.querySelector('#lapseOpenSubtitlesUsername').value = currentSettings.OpenSubtitlesUsername || '';
        view.querySelector('#lapseOpenSubtitlesPassword').value = currentSettings.OpenSubtitlesPassword || '';
        view.querySelector('#lapseArrWebhookEnabled').checked = !!currentSettings.ArrWebhookEnabled;
        renderWebhookUrl(view);
        view.querySelector('#lapseNtfyUrl').value = currentSettings.NtfyUrl || '';
        view.querySelector('#lapseDiscordWebhookUrl').value = currentSettings.DiscordWebhookUrl || '';

        view.querySelector('#lapseExtractEnabled').checked = !!currentSettings.ExtractEmbeddedEnabled;
        view.querySelector('#lapseExtractLanguages').value = currentSettings.ExtractLanguages || '';
        view.querySelector('#lapseExtractTrackFilter').value = currentSettings.ExtractTrackFilter || 'All';
        view.querySelector('#lapseExtractSkipExisting').checked = currentSettings.ExtractSkipExisting !== false;
        view.querySelector('#lapseExtractPictures').checked = !!currentSettings.ExtractPictureSubtitles;
        view.querySelector('#lapseExtractPathFilter').value = currentSettings.ExtractPathFilter || '';
        renderExtractLibraries(view, false);

        view.querySelector('#lapseMultiEngineEnabled').checked = !!currentSettings.MultiEngineEnabled;
        view.querySelector('#lapseMultiEngineUseAlass').checked = !!currentSettings.MultiEngineUseAlass;
        view.querySelector('#lapseMultiEngineUseFfsubsync').checked = !!currentSettings.MultiEngineUseFfsubsync;
        view.querySelector('#lapseMultiEngineTrigger').value = currentSettings.MultiEngineTrigger || 'UnsureOnly';
        view.querySelector('#lapseMultiEngineCandidateFormat').value = currentSettings.MultiEngineCandidateFormat || 'MatchOriginal';
        view.querySelector('#lapseMultiEngineInBulk').checked = !!currentSettings.MultiEngineInBulk;
        refreshMultiEngine(view);

        renderAppearance(view);
        renderAccess(view);
        renderConversion(view);
        renderAutomation(view);
        updateSidecarPreview(view);

        // the suggested name for a subtitle-to-subtitle output uses the sidecar suffix, so
        // it can only be right once the settings have actually arrived
        updateSubToSubOutput(view, false);
    }

    // --- scheduled extraction of embedded subtitles ---

    // runs when the settings or the libraries arrive, whichever is second. keepTicks keeps unsaved ticks on a reload.
    function renderExtractLibraries(view, keepTicks) {
        var container = view.querySelector('#lapseExtractLibraries');
        if (!container) {
            return;
        }

        var picked = keepTicks && container.querySelector('.lapseExtractLibrary')
            ? selectedExtractLibraryIds(view)
            : ((currentSettings && currentSettings.ExtractLibraryIds) || []);

        if (!allLibraries || !allLibraries.length) {
            container.innerHTML = '<div class="fieldDescription">No libraries yet.</div>';
            return;
        }

        container.innerHTML = allLibraries.map(function (library) {
            var checked = picked.some(function (id) { return sameId(id, library.ItemId); });

            return '' +
                '<div class="checkboxContainer"><label class="emby-checkbox-label">' +
                '  <input type="checkbox" is="emby-checkbox" class="lapseExtractLibrary" data-id="' + escapeHtml(library.ItemId) + '"' +
                (checked ? ' checked' : '') + ' />' +
                '  <span>' + escapeHtml(library.Name) + '</span>' +
                '</label></div>';
        }).join('');
    }

    function selectedExtractLibraryIds(view) {
        var ids = [];

        view.querySelectorAll('.lapseExtractLibrary').forEach(function (input) {
            if (input.checked) {
                ids.push(input.getAttribute('data-id'));
            }
        });

        return ids;
    }

    function describeExtractRun(status) {
        var run = status && status.LastRun;
        if (!run) {
            return 'No extraction has run since the server started.';
        }

        if (run.Message && !run.Videos) {
            return (status.Running ? 'Running: ' : 'Last run ' + timeAgo(run.FinishedUtc) + ': ') + run.Message;
        }

        var parts = [
            run.Written + ' written',
            run.AlreadyExtracted + ' already there',
            run.CoveredByExisting + ' covered by an existing subtitle'
        ];

        if (run.Empty) {
            parts.push(run.Empty + ' empty');
        }

        if (run.Unsupported) {
            parts.push(run.Unsupported + ' picture based, left in the video');
        }

        if (run.Failed) {
            parts.push(run.Failed + ' failed');
        }

        var videos = run.Videos + (run.Videos === 1 ? ' video' : ' videos');
        var head = status.Running
            ? 'Running, ' + videos + ' so far: '
            : (run.Cancelled ? 'Stopped ' : 'Last run ') + timeAgo(run.FinishedUtc) + ', ' + videos + ': ';

        var text = head + parts.join(', ') + '.';

        if (run.Message) {
            text += ' ' + run.Message;
        }

        if (run.Failures && run.Failures.length) {
            text += ' First failures: ' + run.Failures.slice(0, 5).join('; ') + '. The server log has the rest.';
        }

        return text;
    }

    var extractPollTimer = null;
    var lastExtractStart = null;

    // waitFor = start time of the run on screen when a new one was asked for, show "starting" until a newer run appears
    function refreshExtractStatus(view, waitFor, triesLeft) {
        return lapseGet('Lapse/Extraction').then(function (status) {
            var line = view.querySelector('#lapseExtractStatus');
            var run = status && status.LastRun;
            var waiting = waitFor !== undefined && triesLeft > 0 &&
                !(status && status.Running) && (!run || run.StartedUtc === waitFor);

            if (line) {
                line.textContent = waiting ? 'Starting the extraction...' : describeExtractRun(status);
            }

            if (!waiting && run) {
                lastExtractStart = run.StartedUtc;
            }

            // Keep the line moving while a run is going or about to, and stop as soon as
            // it isn't or the page has been left.
            clearTimeout(extractPollTimer);
            if (document.body.contains(view) && (waiting || (status && status.Running))) {
                extractPollTimer = setTimeout(function () {
                    refreshExtractStatus(view, waiting ? waitFor : undefined, waiting ? triesLeft - 1 : 0);
                }, waiting ? 1000 : 3000);
            }
        }).catch(function () {
            // The line just stays as it was; nothing else on the page depends on it.
        });
    }

    function runExtraction(view) {
        Dashboard.showLoadingMsg();

        var previous = lastExtractStart;

        lapsePost('Lapse/Settings', collectSettings(view)).then(function () {
            return lapsePost('Lapse/Extraction/Run');
        }).then(function () {
            Dashboard.hideLoadingMsg();
            return refreshSettings(view).then(function () {
                return refreshExtractStatus(view, previous, 30);
            });
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            tell('Could not start the extraction', err.message);
        });
    }

    function renderWebhookUrl(view) {
        var token = currentSettings && currentSettings.ArrWebhookToken;
        var input = view.querySelector('#lapseArrWebhookUrl');

        // ApiClient.getUrl already returns an absolute URL including the server address,
        // so nothing goes in front of it.
        input.value = token
            ? (ApiClient.getUrl('Lapse/Webhook/Arr') + '?token=' + token)
            : 'Press "Generate a new URL" to create one.';
        view.querySelector('#lapseBazarrCommand').value = token
            ? 'curl -fsS -H "Content-Type: text/plain" --data-binary {{episode}} "' + ApiClient.getUrl('Lapse/Webhook/Path') + '?token=' + token + '"'
            : '';

        // A webhook that quietly does nothing looks exactly like one that was never set
        // up, so say whether anything has ever called it.
        var status = view.querySelector('#lapseArrWebhookStatus');
        var d = currentDiagnostics || {};

        if (!token) {
            status.textContent = '';
        } else if (d.LastEvent) {
            status.textContent = 'Last call: "' + d.LastEvent + '" ' + timeAgo(d.ArrWebhookLastEventUtc) +
                '. Press Test in Radarr or Sonarr to check the connection.';
        } else {
            status.textContent = 'Nothing has called this yet. Press Test in Radarr or Sonarr and this line will update.';
        }
    }

    function refreshSettings(view) {
        return lapseGet('Lapse/Settings').then(function (settings) {
            currentSettings = settings;
            renderSettings(view);
        });
    }

    // --- translation providers ---

    function tierLabel(tier) {
        if (tier === 0) {
            return 'No setup needed';
        }

        return tier === 1 ? 'Self hosted' : 'Needs an API key';
    }

    // Each provider gets the fields it actually needs, right under its own name, rather
    // than a flat pile of keys and URLs that gives no clue which goes with what.
    function providerFieldsHtml(provider) {
        switch (provider.Id) {
            case 'LibreTranslate':
                return textField('lapseLibreUrl', 'Base URL', 'text', 'http://libretranslate:5000') +
                    textField('lapseLibreKey', 'API key (only if your instance wants one)', 'password', '');
            case 'Lingarr':
                return textField('lapseLingarrUrl', 'Base URL', 'text', 'http://lingarr:9876') +
                    textField('lapseLingarrKey', 'API key (only if authentication is on)', 'password', '');
            case 'DeepL':
                return textField('lapseDeepLKey', 'API key', 'password', '');
            case 'Google':
                return textField('lapseGoogleKey', 'API key', 'password', '');
            default:
                return '';
        }
    }

    function textField(id, label, type, placeholder) {
        return '' +
            '<div class="inputContainer">' +
            '  <label class="inputLabel inputLabelUnfocused" for="' + id + '">' + escapeHtml(label) + '</label>' +
            '  <input is="emby-input" id="' + id + '" type="' + type + '"' +
            (placeholder ? ' placeholder="' + escapeHtml(placeholder) + '"' : '') + ' />' +
            '</div>';
    }

    function renderProviders(view) {
        var container = view.querySelector('#lapseProviderList');

        container.innerHTML = allProviders.map(function (provider) {
            return '' +
                '<div class="verticalSection">' +
                '  <h3 class="sectionTitle">' + escapeHtml(provider.DisplayName) + '</h3>' +
                '  <div class="fieldDescription">' + escapeHtml(tierLabel(provider.Tier)) + ', ' +
                (provider.Configured ? 'ready' : 'not set up') + '. ' + escapeHtml(provider.Summary) + '</div>' +
                providerFieldsHtml(provider) +
                '</div>';
        }).join('');

        // fill the fields in after the markup exists
        setInputValue(view, '#lapseLibreUrl', currentSettings && currentSettings.LibreTranslateBaseUrl);
        setInputValue(view, '#lapseLibreKey', currentSettings && currentSettings.LibreTranslateApiKey);
        setInputValue(view, '#lapseLingarrUrl', currentSettings && currentSettings.LingarrBaseUrl);
        setInputValue(view, '#lapseLingarrKey', currentSettings && currentSettings.LingarrApiKey);
        setInputValue(view, '#lapseDeepLKey', currentSettings && currentSettings.DeepLApiKey);
        setInputValue(view, '#lapseGoogleKey', currentSettings && currentSettings.GoogleTranslateApiKey);

        var defaultSelect = view.querySelector('#lapseDefaultProvider');
        defaultSelect.innerHTML = allProviders.map(function (p) {
            return '<option value="' + escapeHtml(p.Id) + '"' + (p.Configured ? '' : ' disabled') + '>' +
                escapeHtml(p.DisplayName) + (p.Configured ? '' : ' (not configured)') + '</option>';
        }).join('');

        if (currentSettings) {
            defaultSelect.value = currentSettings.DefaultTranslationProvider;
        }

        var active = allProviders.filter(function (p) { return p.Configured; }).length;
        view.querySelector('#lapseTranslationHint').textContent = active + ' of ' + allProviders.length + ' ready';
    }

    function setInputValue(view, selector, value) {
        var input = view.querySelector(selector);
        if (input) {
            input.value = value || '';
        }
    }

    // provider fields only exist once the provider list loaded, don't save them as empty and wipe API keys
    function inputValue(view, selector, savedValue) {
        var input = view.querySelector(selector);
        return input ? input.value : (savedValue || null);
    }

    function refreshProviders(view) {
        return lapseGet('Lapse/Translate/Providers').then(function (providers) {
            allProviders = providers;
            renderProviders(view);
        });
    }

    // --- subtitle appearance ---

    // The colour input can only do #rrggbb, so opacity rides along as its own slider and
    // the two get stitched back into the #rrggbbaa the config stores.
    function splitColor(value, fallback) {
        var color = (value || fallback || '#000000').trim();
        var rgb = color.substring(0, 7);
        var alpha = color.length === 9 ? parseInt(color.substring(7), 16) : 255;

        return { rgb: rgb, opacity: Math.round((alpha / 255) * 100) };
    }

    function joinColor(rgb, opacityPercent) {
        var alpha = Math.round((opacityPercent / 100) * 255);
        return rgb + alpha.toString(16).padStart(2, '0').toUpperCase();
    }

    // each slider shows its value, and the font section says whether the player can actually find the font
    function updateReadableLabels(view) {
        view.querySelector('#lapseStyleFontSizeValue').textContent =
            view.querySelector('#lapseStyleFontSize').value;
        view.querySelector('#lapseStyleLetterSpacingValue').textContent =
            view.querySelector('#lapseStyleLetterSpacing').value;
        view.querySelector('#lapseStyleOutlineValue').textContent =
            view.querySelector('#lapseStyleOutline').value;
        view.querySelector('#lapseStyleMarginVValue').textContent =
            view.querySelector('#lapseStyleMarginV').value;
    }

    function renderFontStatus(view, status) {
        var strip = view.querySelector('#lapseFontStatus');
        currentFontStatus = status;

        var button = view.querySelector('#btnInstallDyslexicFont');
        if (button) {
            button.querySelector('span').textContent = (status && status.DyslexicInstalled)
                ? 'Remove OpenDyslexic'
                : 'Install OpenDyslexic';
        }

        if (!status) {
            strip.textContent = 'Could not read the server\'s font settings.';
            return;
        }

        if (status.Error) {
            strip.textContent = status.Error;
            return;
        }

        var parts = [];

        if (status.DyslexicInstalled) {
            parts.push('OpenDyslexic is installed.');
        } else {
            parts.push('OpenDyslexic is not installed yet.');
        }

        if (!status.FallbackFontEnabled) {
            parts.push('The server\'s fallback font setting is off, so nothing in the font folder is used. ' +
                'Installing below turns it on.');
        }

        parts.push('Font folder: ' + status.FolderPath);

        if (status.Fonts && status.Fonts.length) {
            parts.push('Installed: ' + status.Fonts.join(', ') + '.');
        }

        strip.textContent = parts.join(' ');
    }

    // explain why multi engine can't be turned on yet, it depends on things not visible on this page
    function renderMultiEngine(view, status) {
        var strip = view.querySelector('#lapseMultiEngineStatus');
        var toggle = view.querySelector('#lapseMultiEngineEnabled');

        if (!status) {
            strip.textContent = 'Could not check which engines are installed.';
            return;
        }

        var availability = status.Availability || {};
        var parts = [];

        if (availability.Available) {
            parts.push('Ready to use.');
            parts.push('Installed and available to ask: ' +
                [availability.AlassInstalled ? 'alass' : null,
                 availability.FfsubsyncInstalled ? 'ffsubsync' : null]
                .filter(function (n) { return n; }).join(' and ') + '.');
        } else {
            parts.push(availability.Reason || 'Not available yet.');
        }

        // Overwriting on a low score means LAPSE has already replaced the file by the time
        // the other engines could have been asked, so there is nothing left to compare.
        if (availability.OverwriteAnywayConflict) {
            parts.push('File output writes an unsure result anyway, so there is nothing left to compare. ' +
                'Change When LAPSE is unsure under File output to use this.');
        }

        strip.textContent = parts.join(' ');

        var usable = !!availability.Available && !availability.OverwriteAnywayConflict;
        toggle.disabled = !usable;


        // The waiting count is the number worth seeing without opening the page, since
        // every one of them is a decision somebody still has to make.
        // a set with only LAPSE in it is an unsure answer up for review, that has its own panel
        var pending = (status.Pending || []).filter(function (set) {
            return (set.Candidates || []).some(function (c) { return c.EngineId !== 'lapse'; });
        });
        view.querySelector('#lapseMultiEngineHint').textContent = pending.length
            ? pending.length + ' waiting'
            : (availability.Enabled && usable ? 'on' : 'off');

        renderCandidateList(view, pending);
    }

    function renderCandidateList(view, sets) {
        var host = view.querySelector('#lapseCandidateList');

        if (!sets.length) {
            host.innerHTML = '<p class="fieldDescription">Nothing is waiting. When the other engines are asked, ' +
                'their answers show up here until one is kept.</p>';
            return;
        }

        host.innerHTML = sets.map(function (set) {
            var rows = (set.Candidates || []).map(function (c) {
                return '<div class="listItem listItem-border"><div class="listItemBody two-line">' +
                    '<div class="listItemBodyText">' + escapeHtml(c.EngineName) + '</div>' +
                    '<div class="listItemBodyText secondary">' + escapeHtml(fileNameOf(c.Path)) +
                    (c.Detail ? ', ' + escapeHtml(c.Detail) : '') + '</div>' +
                    '</div></div>';
            }).join('');

            return '<div class="verticalSection">' +
                '<h3>' + escapeHtml(set.ItemName || 'Unknown item') + '</h3>' +
                '<div class="fieldDescription">' + escapeHtml(fileNameOf(set.OriginalPath)) +
                ' is untouched. Keep one from the subtitle menu while it plays.</div>' +
                '<div class="paperList">' + rows + '</div>' +
                '<button is="emby-button" type="button" class="raised lapseDiscardCandidates" ' +
                'data-item="' + escapeHtml(set.ItemId) + '" data-original="' + escapeHtml(set.OriginalPath) + '">' +
                '<span>Discard all</span></button>' +
                '</div>';
        }).join('');

        host.querySelectorAll('.lapseDiscardCandidates').forEach(function (button) {
            button.addEventListener('click', function () {
                confirmAction('Discard these answers', 'Every answer file is deleted and the original stays as it is.', 'Discard').then(function (yes) {
                    if (!yes) {
                        return;
                    }

                    button.disabled = true;
                    lapsePost('Lapse/Candidates/Discard', {
                        ItemId: button.getAttribute('data-item'),
                        OriginalPath: button.getAttribute('data-original')
                    }).then(function (decision) {
                        tell('Multi engine sync', decision.Message || 'Removed.');
                        refreshMultiEngine(view);
                    }).catch(function (err) {
                        button.disabled = false;
                        tell('Could not remove them', err.message);
                    });
                });
            });
        });
    }

    function fileNameOf(path) {
        if (!path) {
            return '';
        }

        var parts = path.split(/[\\/]/);
        return parts[parts.length - 1];
    }

    function refreshMultiEngine(view) {
        return lapseGet('Lapse/MultiEngine').then(function (status) {
            renderMultiEngine(view, status);
        }).catch(function () {
            renderMultiEngine(view, null);
        });
    }

    function refreshReview(view) {
        return lapseGet('Lapse/Review').then(function (sets) {
            renderReview(view, sets);
        });
    }

    function renderReview(view, sets) {
        var host = view.querySelector('#lapseReviewList');
        currentReview = sets;
        view.querySelector('#lapseReviewHint').textContent = sets.length ? sets.length + ' waiting' : '';
        renderOverview(view);

        if (!sets.length) {
            host.innerHTML = '<p class="fieldDescription">Nothing to review. When LAPSE is unsure about a sync, ' +
                'its result is written beside the original and shows up here.</p>';
            return;
        }

        host.innerHTML = sets.map(function (set) {
            var notes = [];

            if (!set.InLibrary) {
                notes.push('The item is no longer in the library.');
            }

            if (set.OriginalChanged) {
                notes.push(fileNameOf(set.OriginalPath) + ' has changed since, so keeping this would undo that change.');
            }

            var rows = (set.Candidates || []).map(function (c) {
                var playable = c.Exists && set.InLibrary && c.StreamIndex != null;
                var note = !c.Exists ? 'The file has gone from disk.'
                    : set.InLibrary && c.StreamIndex == null ? 'Jellyfin has not picked this file up yet. Reload in a minute.'
                    : '';

                var checks = playable ? c.SpotChecks.map(function (seconds) {
                    return '<button is="emby-button" type="button" class="raised lapseReviewPlay" ' +
                        'data-item="' + escapeHtml(set.ItemId) + '" data-stream="' + c.StreamIndex + '" data-seconds="' + seconds + '"' +
                        ' aria-label="Play from ' + clockOf(seconds) + '">' +
                        '<span class="material-icons play_arrow" aria-hidden="true"></span><span>' + clockOf(seconds) + '</span></button>';
                }).join('') : '';

                var keep = c.Exists && set.InLibrary
                    ? '<button is="emby-button" type="button" class="raised button-submit lapseReviewKeep" ' +
                        'data-item="' + escapeHtml(set.ItemId) + '" data-path="' + escapeHtml(c.Path) + '"><span>Keep</span></button>'
                    : '';

                return '<div class="listItem listItem-border">' +
                    '<div class="listItemBody two-line">' +
                    '<div class="listItemBodyText">' + escapeHtml(fileNameOf(c.Path)) + '</div>' +
                    '<div class="listItemBodyText secondary">' + escapeHtml(note || c.Detail || '') + '</div>' +
                    (checks ? '<div class="lapseButtonRow">' + checks + '</div>' : '') +
                    '</div>' +
                    '<div class="lapseRowActions">' + keep + '</div>' +
                    '</div>';
            }).join('');

            return '<div class="verticalSection">' +
                '<h2 class="sectionTitle">' + escapeHtml(set.ItemName || 'Unknown item') + '</h2>' +
                '<div class="fieldDescription">' + escapeHtml(fileNameOf(set.OriginalPath)) + ' is untouched. ' +
                'Play from each time to see whether the lines fit, then keep the result or discard it.' +
                (notes.length ? ' ' + escapeHtml(notes.join(' ')) : '') + '</div>' +
                '<div class="paperList">' + rows + '</div>' +
                '<button is="emby-button" type="button" class="raised lapseReviewWrong" ' +
                'data-item="' + escapeHtml(set.ItemId) + '" data-original="' + escapeHtml(set.OriginalPath) + '">' +
                '<span>Discard</span></button>' +
                '</div>';
        }).join('');

        host.querySelectorAll('.lapseReviewPlay').forEach(function (button) {
            button.addEventListener('click', function () {
                playAt(button.getAttribute('data-item'), Number(button.getAttribute('data-stream')), Number(button.getAttribute('data-seconds')))
                    .catch(function (err) {
                        tell('Could not start the player', (err && err.message) || 'The server said no.');
                    });
            });
        });

        host.querySelectorAll('.lapseReviewKeep').forEach(function (button) {
            button.addEventListener('click', function () {
                decideReview(view, button, 'Lapse/Candidates/Keep', {
                    ItemId: button.getAttribute('data-item'),
                    Path: button.getAttribute('data-path')
                });
            });
        });

        host.querySelectorAll('.lapseReviewWrong').forEach(function (button) {
            button.addEventListener('click', function () {
                confirmAction('Discard this result', 'The result is deleted and the original stays as it is.', 'Discard').then(function (yes) {
                    if (yes) {
                        decideReview(view, button, 'Lapse/Candidates/Discard', {
                            ItemId: button.getAttribute('data-item'),
                            OriginalPath: button.getAttribute('data-original')
                        });
                    }
                });
            });
        });
    }

    // Reloads either way: when another browser got there first the server says so, and the list should catch up.
    function decideReview(view, button, path, body) {
        button.disabled = true;
        lapsePost(path, body).then(function (decision) {
            tell('Review', decision.Message || 'Done.');
        }).catch(function (err) {
            tell('Review', err.message);
        }).then(function () {
            return Promise.all([refreshReview(view), refreshItemList(view)]);
        });
    }

    // Through the web client's own player when the injected script is there, which also keeps the
    // answer's track on, otherwise Jellyfin's "play on this device" call. Starts a few seconds early
    // so the line can be seen coming in.
    function playAt(itemId, streamIndex, seconds) {
        if (window.lapsePlayWithSubtitle && window.lapsePlayWithSubtitle(itemId, streamIndex, seconds)) {
            return Promise.resolve();
        }

        return ApiClient.getSessions({ deviceId: ApiClient.deviceId() }).then(function (sessions) {
            if (!sessions.length) {
                throw new Error('this browser has no session on the server');
            }

            return ApiClient.sendPlayCommand(sessions[0].Id, {
                PlayCommand: 'PlayNow',
                ItemIds: itemId,
                StartPositionTicks: Math.round(Math.max(0, seconds - 3) * 10000000),
                SubtitleStreamIndex: streamIndex
            });
        });
    }

    function clockOf(seconds) {
        var whole = Math.floor(seconds);
        var minutes = Math.floor(whole / 60) % 60;
        var hours = Math.floor(whole / 3600);

        return (hours ? hours + ':' + (minutes < 10 ? '0' : '') : '') + minutes + ':' + (whole % 60 < 10 ? '0' : '') + (whole % 60);
    }

    function refreshFontStatus(view) {
        return lapseGet('Lapse/Fonts').then(function (status) {
            renderFontStatus(view, status);
        }).catch(function () {
            renderFontStatus(view, null);
        });
    }

    function currentAppearance(view) {
        return {
            Enabled: view.querySelector('#lapseAppearanceEnabled').checked,
            FontSizePx: parseInt(view.querySelector('#lapseAppearanceFontSize').value, 10) || APPEARANCE_DEFAULTS.FontSizePx,
            VerticalPositionPercent: parseInt(view.querySelector('#lapseAppearancePosition').value, 10) || 0,
            TextColor: view.querySelector('#lapseAppearanceTextColor').value.toUpperCase(),
            BackgroundColor: joinColor(
                view.querySelector('#lapseAppearanceBgColor').value.toUpperCase(),
                parseInt(view.querySelector('#lapseAppearanceBgOpacity').value, 10) || 0),
            BackgroundEnabled: view.querySelector('#lapseAppearanceBgEnabled').checked
        };
    }

    function renderAppearance(view) {
        var appearance = (currentSettings && currentSettings.SubtitleAppearance) || APPEARANCE_DEFAULTS;
        var background = splitColor(appearance.BackgroundColor, APPEARANCE_DEFAULTS.BackgroundColor);

        view.querySelector('#lapseAppearanceEnabled').checked = !!appearance.Enabled;
        view.querySelector('#lapseAppearanceFontSize').value = appearance.FontSizePx || APPEARANCE_DEFAULTS.FontSizePx;
        view.querySelector('#lapseAppearancePosition').value = appearance.VerticalPositionPercent || 0;
        view.querySelector('#lapseAppearanceTextColor').value =
            splitColor(appearance.TextColor, APPEARANCE_DEFAULTS.TextColor).rgb;
        view.querySelector('#lapseAppearanceBgColor').value = background.rgb;
        view.querySelector('#lapseAppearanceBgOpacity').value = background.opacity;
        view.querySelector('#lapseAppearanceBgEnabled').checked = appearance.BackgroundEnabled !== false;

        updateAppearancePreview(view);
    }

    function updateAppearancePreview(view) {
        var appearance = currentAppearance(view);
        var text = view.querySelector('#lapseAppearancePreviewText');

        view.querySelector('#lapseAppearanceFontSizeValue').textContent = appearance.FontSizePx;
        view.querySelector('#lapseAppearancePositionValue').textContent = appearance.VerticalPositionPercent || 0;
        view.querySelector('#lapseAppearanceBgOpacityValue').textContent =
            view.querySelector('#lapseAppearanceBgOpacity').value;

        // The preview scales the font down so a 48px subtitle still fits the panel while
        // staying proportionally right against the other settings.
        text.style.fontSize = Math.max(10, Math.round(appearance.FontSizePx * 0.5)) + 'px';
        text.style.color = appearance.TextColor;
        text.style.backgroundColor = appearance.BackgroundEnabled ? appearance.BackgroundColor : 'transparent';

        view.querySelector('#lapseAppearanceHint').textContent = appearance.Enabled ? 'on' : 'off';
    }

    function resetAppearance(view) {
        var background = splitColor(APPEARANCE_DEFAULTS.BackgroundColor, APPEARANCE_DEFAULTS.BackgroundColor);

        view.querySelector('#lapseAppearanceEnabled').checked = APPEARANCE_DEFAULTS.Enabled;
        view.querySelector('#lapseAppearanceFontSize').value = APPEARANCE_DEFAULTS.FontSizePx;
        view.querySelector('#lapseAppearancePosition').value = APPEARANCE_DEFAULTS.VerticalPositionPercent;
        view.querySelector('#lapseAppearanceTextColor').value = APPEARANCE_DEFAULTS.TextColor;
        view.querySelector('#lapseAppearanceBgColor').value = background.rgb;
        view.querySelector('#lapseAppearanceBgOpacity').value = background.opacity;
        view.querySelector('#lapseAppearanceBgEnabled').checked = APPEARANCE_DEFAULTS.BackgroundEnabled;

        updateAppearancePreview(view);
    }

    // one endpoint for the whole page, so always send everything (a partial object would blank the rest)
    function collectSettings(view) {
        var saved = currentSettings || {};

        var payload = {
            OutputMode: selectedOutputMode(view),
            SubtitleAccess: selectedRadio(view, 'lapseAccessMode', 'AdminsOnly'),
            ConversionFormat: selectedRadio(view, 'lapseConversionFormat', 'srt'),
            ConversionReplaceOriginal: selectedRadio(view, 'lapseConversionOriginal', 'keep') === 'replace',
            ConversionSyncAfter: selectedRadio(view, 'lapseConversionSync', 'sync') === 'sync',
            ConvertBeforeSync: selectedRadio(view, 'lapseConvertBeforeSync', 'off') === 'on',
            AutomationAction: selectedRadio(view, 'lapseAutomationAction', 'Sync'),
            AutoTranslateEnabled: view.querySelector('#lapseAutoTranslateEnabled').checked,
            AutoTranslateLanguage: view.querySelector('#lapseAutoTranslateLanguage').value.trim() || null,
            AutoTranslateSkipExisting: view.querySelector('#lapseAutoTranslateSkipExisting').checked,
            SkipSyncedInUnattendedRuns: view.querySelector('#lapseSkipSyncedInUnattendedRuns').checked,
            AutoSyncNewSubtitles: view.querySelector('#lapseAutoSyncNewSubtitles').checked,
            SubtitleAccessUserIds: selectedAccessUserIds(view),
            SidecarSuffix: view.querySelector('#lapseSidecarSuffix').value,
            LowConfidenceAction: selectedRadio(view, 'lapseLowConfidence', 'Sidecar'),
            ConfidenceSigma: parseFloat(view.querySelector('#lapseConfidenceSigma').value) || 8,
            SkipAlreadyInSync: view.querySelector('#lapseSkipAlreadyInSync').checked,
            AlreadyInSyncToleranceMs: parseInt(view.querySelector('#lapseAlreadyInSyncTolerance').value, 10) || 0,
            AutoUpdateEngines: view.querySelector('#lapseAutoUpdateEngines').checked,
            CountEmbeddedSubtitlesInStatus: view.querySelector('#lapseCountEmbedded').checked,
            SubToSubPlacement: view.querySelector('#lapseSubToSubPlacement').value,
            SubToSubCustomFolder: view.querySelector('#lapseSubToSubCustomFolder').value || null,
            OpenSubtitlesEnabled: view.querySelector('#lapseOpenSubtitlesEnabled').checked,
            OpenSubtitlesApiKey: view.querySelector('#lapseOpenSubtitlesApiKey').value || null,
            OpenSubtitlesUsername: view.querySelector('#lapseOpenSubtitlesUsername').value || null,
            OpenSubtitlesPassword: view.querySelector('#lapseOpenSubtitlesPassword').value || null,
            OpenSubtitlesLanguage: view.querySelector('#lapseOpenSubtitlesLanguage').value || 'en',
            ArrWebhookEnabled: view.querySelector('#lapseArrWebhookEnabled').checked,
            NtfyUrl: view.querySelector('#lapseNtfyUrl').value.trim() || null,
            DiscordWebhookUrl: view.querySelector('#lapseDiscordWebhookUrl').value.trim() || null,
            ExtractEmbeddedEnabled: view.querySelector('#lapseExtractEnabled').checked,
            ExtractLibraryIds: selectedExtractLibraryIds(view),
            ExtractLanguages: view.querySelector('#lapseExtractLanguages').value.trim() || null,
            ExtractTrackFilter: view.querySelector('#lapseExtractTrackFilter').value || 'All',
            ExtractSkipExisting: view.querySelector('#lapseExtractSkipExisting').checked,
            ExtractPictureSubtitles: view.querySelector('#lapseExtractPictures').checked,
            ExtractPathFilter: view.querySelector('#lapseExtractPathFilter').value.trim() || null,
            MultiEngineEnabled: view.querySelector('#lapseMultiEngineEnabled').checked,
            MultiEngineUseAlass: view.querySelector('#lapseMultiEngineUseAlass').checked,
            MultiEngineUseFfsubsync: view.querySelector('#lapseMultiEngineUseFfsubsync').checked,
            MultiEngineTrigger: view.querySelector('#lapseMultiEngineTrigger').value || 'UnsureOnly',
            MultiEngineCandidateFormat: view.querySelector('#lapseMultiEngineCandidateFormat').value || 'MatchOriginal',
            MultiEngineInBulk: view.querySelector('#lapseMultiEngineInBulk').checked,
            DefaultTranslationProvider: view.querySelector('#lapseDefaultProvider').value ||
                saved.DefaultTranslationProvider || 'MyMemory',
            GoogleTranslateApiKey: inputValue(view, '#lapseGoogleKey', saved.GoogleTranslateApiKey),
            DeepLApiKey: inputValue(view, '#lapseDeepLKey', saved.DeepLApiKey),
            LingarrBaseUrl: inputValue(view, '#lapseLingarrUrl', saved.LingarrBaseUrl),
            LingarrApiKey: inputValue(view, '#lapseLingarrKey', saved.LingarrApiKey),
            LibreTranslateBaseUrl: inputValue(view, '#lapseLibreUrl', saved.LibreTranslateBaseUrl),
            LibreTranslateApiKey: inputValue(view, '#lapseLibreKey', saved.LibreTranslateApiKey),
            TranslationConfidenceThreshold: parseInt(view.querySelector('#lapseConfidence').value, 10) || 0,
            TranslationKeepLowConfidenceOriginal: view.querySelector('#lapseKeepLowConfidence').checked,
            TranslationIncludeMetadataHeader: view.querySelector('#lapseMetadataHeader').checked,
            TranslationDefaultTargetLanguage: view.querySelector('#lapseDefaultTargetLanguage').value.trim() || null,
            TranslationDefaultSourceLanguage: view.querySelector('#lapseDefaultSourceLanguage').value.trim() || null,
            SubtitleFontName: view.querySelector('#lapseStyleFontName').value.trim() || 'OpenDyslexic',
            SubtitleNonLatinFontName: view.querySelector('#lapseStyleNonLatinFontName').value.trim() || null,
            ReadableAutomation: view.querySelector('#lapseReadableAutomation').value,
            SubtitleFontSize: parseInt(view.querySelector('#lapseStyleFontSize').value, 10) || 78,
            SubtitleLetterSpacing: parseFloat(view.querySelector('#lapseStyleLetterSpacing').value) || 0,
            SubtitleOutline: parseFloat(view.querySelector('#lapseStyleOutline').value) || 0,
            SubtitleMarginV: parseInt(view.querySelector('#lapseStyleMarginV').value, 10) || 0,
            SubtitleBold: view.querySelector('#lapseStyleBold').checked,
            SubtitleAppearance: currentAppearance(view),
            Engines: []
        };

        view.querySelectorAll('.lapseEngineCard').forEach(function (card) {
            var penaltyInput = card.querySelector('.lapseSettingPenalty');
            var pathInput = card.querySelector('.lapseSettingPath');
            var modeSelect = card.querySelector('.lapseSettingMode');
            var parameters = [];

            card.querySelectorAll('.lapseParamInput').forEach(function (input) {
                parameters.push({
                    Key: input.getAttribute('data-key'),
                    Value: input.getAttribute('data-kind') === 'Boolean'
                        ? (input.checked ? 'true' : 'false')
                        : input.value
                });
            });

            payload.Engines.push({
                EngineId: card.getAttribute('data-id'),
                PathOverride: (pathInput.value || '').trim() || null,
                Penalty: penaltyInput ? (parseInt(penaltyInput.value, 10) || null) : null,
                DefaultMode: modeSelect ? modeSelect.value : null,
                Parameters: parameters
            });
        });

        return payload;
    }

    function saveSettings(view, button) {
        button.disabled = true;
        Dashboard.showLoadingMsg();

        lapsePost('Lapse/Settings', collectSettings(view)).then(function () {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Settings saved.');
            return refreshSettings(view).then(function () {
                return Promise.all([refreshEngines(view), refreshProviders(view), refreshOverview(view)]);
            });
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            tell('Could not save', err.message);
        }).then(function () {
            button.disabled = false;
        });
    }

    function browseForPath(input, includeFiles, header, onPicked) {
        var picker = new Dashboard.DirectoryBrowser();
        picker.show({
            includeFiles: includeFiles,
            header: header,
            callback: function (path) {
                if (path) {
                    input.value = path;
                    if (onPicked) {
                        onPicked(path);
                    }
                }

                picker.close();
            }
        });
    }

    // --- deep link from the context menu's old "Advanced" button ---

    // the context menu has its own dialog now, but old bookmarked deep links still land here
    function getDeepLinkParams() {
        var hash = window.location.hash || '';
        var hashQueryIndex = hash.indexOf('?');
        if (hashQueryIndex !== -1) {
            return new URLSearchParams(hash.substring(hashQueryIndex + 1));
        }

        return new URLSearchParams(window.location.search || '');
    }

    function openDeepLinkedAdvancedDialogIfNeeded(view) {
        var params = getDeepLinkParams();

        // movieId is the old name, still accepted so a stale web client page keeps working
        var itemId = params.get('itemId') || params.get('movieId');

        if (!itemId || params.get('autoAdvanced') !== '1') {
            return;
        }

        ApiClient.getItem(ApiClient.getCurrentUserId(), itemId).then(function (item) {
            openAdvancedDialog(view, itemId, item.Name);
        });
    }

    // --- wire everything up ---

    function refreshEverything(view) {
        ignoreSearchFetched = false;
        Dashboard.showLoadingMsg();

        // engines and settings first, the advanced dialog and result messages both need those
        return Promise.all([refreshEngines(view), refreshSettings(view), refreshLibraries(view)]).then(function () {
            return Promise.all([
                refreshOverview(view),
                refreshProviders(view),
                refreshItemList(view),
                refreshReview(view),
                refreshIgnoreRules(view),
                refreshUsers(view),
                refreshFolders(view),
                refreshRuns(view),
                refreshQueue(view),
                refreshPlatform(view),
                refreshEngineCache(view),
                refreshDiagnostics(view),
                refreshExtractStatus(view)
            ]);
        }).then(function () {
            Dashboard.hideLoadingMsg();
        }).catch(function (err) {
            // Swallowing this used to leave half a dashboard with no explanation, and
            // every panel fed from the failed call looking like a broken button.
            Dashboard.hideLoadingMsg();
            console.error('LAPSE: could not load the dashboard', err);
            tell('Some of LAPSE could not be loaded', ((err && err.message) || 'The server did not answer.') +
                ' Reload the page, and check the Jellyfin log if it keeps happening.');
        });
    }

    document.querySelector('#LapseConfigPage').addEventListener('pageshow', function (e) {
        var view = e.target;

        // pageshow fires again on every visit with the same view, without this every button fires twice
        if (view.lapseWired) {
            refreshEverything(view);
            startQueuePolling(view);
            return;
        }

        view.lapseWired = true;

        setUpNavigation(view);

        refreshEverything(view).then(function () {
            openDeepLinkedAdvancedDialogIfNeeded(view);
        });

        startQueuePolling(view);

        view.querySelector('#lapseQueueStop').addEventListener('click', function () {
            var button = view.querySelector('#lapseQueueStop');
            button.disabled = true;

            lapsePost('Lapse/Queue/Cancel').then(function () {
                refreshQueue(view);
            }).catch(function (err) {
                button.disabled = false;
                tell('Could not stop the job', err.message);
            });
        });
        // A filter-like toggle sitting above the list, so it saves itself rather than
        // waiting for a Save button on a page that hasn't got one.
        view.querySelector('#lapseCountEmbedded').addEventListener('change', function () {
            lapsePost('Lapse/Settings', collectSettings(view)).then(function () {
                return refreshSettings(view);
            }).then(function () {
                return Promise.all([refreshItemList(view), refreshOverview(view)]);
            }).catch(function (err) {
                tell('Could not save', err.message);
            });
        });
        wireItemListActions(view);

        var searchTimer = null;
        view.querySelector('#lapseItemSearch').addEventListener('input', function () {
            clearTimeout(searchTimer);
            searchTimer = setTimeout(function () {
                renderItemList(view, allItems);
            }, 150);
        });
        view.querySelector('#lapseItemLibraryFilter').addEventListener('change', function () {
            renderItemList(view, allItems);
        });
        view.querySelector('#lapseItemStatusFilter').addEventListener('change', function () {
            renderItemList(view, allItems);
        });
        view.querySelector('#lapseIncludeAll').addEventListener('change', function () {
            renderItemList(view, allItems);
        });
        view.querySelector('#btnCheckEngineUpdates').addEventListener('click', function () {
            checkEngineUpdates(view);
        });
        view.querySelector('#btnClearEngineCache').addEventListener('click', function () {
            clearEngineCache(view);
        });
        view.querySelector('#btnSaveEngines').addEventListener('click', function (e) {
            saveSettings(view, e.currentTarget);
        });
        view.querySelector('#lapseAutoUpdateEngines').addEventListener('change', function () {
            var enabled = view.querySelector('#lapseAutoUpdateEngines').checked;
            lapsePost('Lapse/Engines/AutoUpdate?enabled=' + enabled).catch(function (err) {
                tell('Could not change auto-update', err.message);
            });
        });
        view.querySelector('#btnSaveLibraries').addEventListener('click', function (e) {
            saveLibraries(view, e.currentTarget);
        });
        view.querySelector('#btnSyncLibrary').addEventListener('click', function () {
            startBulkSync(view, 'Library');
        });
        view.querySelector('#btnSyncFolder').addEventListener('click', function () {
            var select = view.querySelector('#lapseFolderSelect');
            if (select.value) {
                startBulkSync(view, 'Folder', select.value);
            }
        });
        view.querySelector('#btnToggleSkipFolder').addEventListener('click', function () {
            toggleSkipFolder(view);
        });
        view.querySelector('#btnBrowseRefSub').addEventListener('click', function () {
            browseForPath(view.querySelector('#lapseRefSubPath'), true, 'Select the reference subtitle', function () {
                updateSubToSubPlacement(view);
            });
        });
        view.querySelector('#btnBrowseInputSub').addEventListener('click', function () {
            browseForPath(view.querySelector('#lapseInputSubPath'), true, 'Select the input subtitle', function () {
                updateSubToSubOutput(view, false);
            });
        });
        view.querySelector('#btnBrowseSubToSubFolder').addEventListener('click', function () {
            browseForPath(view.querySelector('#lapseSubToSubCustomFolder'), false, 'Select the output folder');
        });
        view.querySelector('#lapseSubToSubNewFile').addEventListener('change', function () {
            updateSubToSubOutput(view, false);
        });
        view.querySelector('#lapseSubToSubPlacement').addEventListener('change', function () {
            updateSubToSubPlacement(view);
        });
        view.querySelector('#btnSyncSubtitles').addEventListener('click', function () {
            syncSubtitles(view);
        });
        view.querySelector('#btnSaveSubToSub').addEventListener('click', function (e) {
            saveSettings(view, e.currentTarget);
        });
        updateSubToSubOutput(view, false);
        view.querySelector('#lapseSidecarSuffix').addEventListener('input', function () {
            updateSidecarPreview(view);
        });
        view.querySelector('#lapseConfidenceSigma').addEventListener('input', function () {
            updateConfidenceNote(view);
        });
        view.querySelector('#lapseAlreadyInSyncTolerance').addEventListener('input', function () {
            updateAlreadyInSyncNote(view);
        });
        view.querySelector('#lapseSkipAlreadyInSync').addEventListener('change', function () {
            updateAlreadyInSyncNote(view);
        });
        view.querySelector('#btnSaveConversion').addEventListener('click', function (e) {
            saveSettings(view, e.currentTarget);
        });
        view.querySelector('#btnSaveAutomation').addEventListener('click', function (e) {
            saveSettings(view, e.currentTarget);
        });
        view.querySelector('#lapseAutoTranslateEnabled').addEventListener('change', function () {
            updateAutomationHint(view);
        });
        view.querySelector('#lapseAutoTranslateLanguage').addEventListener('input', function () {
            updateAutomationHint(view);
        });
        view.querySelector('#btnSaveAccess').addEventListener('click', function (e) {
            saveSettings(view, e.currentTarget);
        });
        view.querySelector('#btnSaveOutput').addEventListener('click', function (e) {
            saveSettings(view, e.currentTarget);
        });
        view.querySelector('#btnSaveTranslation').addEventListener('click', function (e) {
            saveSettings(view, e.currentTarget);
        });
        view.querySelector('#btnSaveLabs').addEventListener('click', function (e) {
            saveSettings(view, e.currentTarget);
        });
        view.querySelector('#btnTestOpenSubtitles').addEventListener('click', function () {
            testOpenSubtitles(view);
        });
        view.querySelector('#btnSaveMultiEngine').addEventListener('click', function (e) {
            // Saving reads the settings back, and drawing those redraws this panel's
            // status strip and waiting list too.
            saveSettings(view, e.currentTarget);
        });
        view.querySelector('#btnSaveAppearance').addEventListener('click', function (e) {
            saveSettings(view, e.currentTarget);
        });
        view.querySelector('#btnResetAppearance').addEventListener('click', function () {
            resetAppearance(view);
        });
        view.querySelector('#btnSaveReadable').addEventListener('click', function (e) {
            saveSettings(view, e.currentTarget);
        });
        view.querySelector('#btnInstallDyslexicFont').addEventListener('click', function () {
            var strip = view.querySelector('#lapseFontStatus');
            var installed = currentFontStatus && currentFontStatus.DyslexicInstalled;

            if (installed) {
                strip.textContent = 'Removing OpenDyslexic...';
                lapseDelete('Lapse/Fonts/Dyslexic').then(function (status) {
                    renderFontStatus(view, status);
                }).catch(function (err) {
                    strip.textContent = 'Could not remove the font: ' + err.message;
                });
                return;
            }

            strip.textContent = 'Downloading OpenDyslexic...';
            lapsePost('Lapse/Fonts/InstallDyslexic').then(function (status) {
                renderFontStatus(view, status);
            }).catch(function (err) {
                strip.textContent = 'Could not install the font: ' + err.message;
            });
        });

        ['#lapseStyleFontSize', '#lapseStyleLetterSpacing', '#lapseStyleOutline', '#lapseStyleMarginV']
            .forEach(function (selector) {
                view.querySelector(selector).addEventListener('input', function () {
                    updateReadableLabels(view);
                });
            });

        refreshFontStatus(view);
        view.querySelector('#btnNewArrToken').addEventListener('click', function () {
            lapsePost('Lapse/Webhook/Arr/Token').then(function (result) {
                if (currentSettings) {
                    currentSettings.ArrWebhookToken = result && result.Token;
                }

                renderWebhookUrl(view);
            }).catch(function (err) {
                tell('Could not make a new URL', err.message);
            });
        });
        view.querySelector('#btnRunExtract').addEventListener('click', function () {
            runExtraction(view);
        });
        view.querySelector('#btnRefreshExtract').addEventListener('click', function () {
            refreshExtractStatus(view);
        });
        view.querySelector('#btnRefreshArrStatus').addEventListener('click', function () {
            refreshDiagnostics(view).then(function () {
                renderWebhookUrl(view);
            });
        });
        view.querySelector('#btnIgnoreSearch').addEventListener('click', function () {
            searchForIgnore(view);
        });
        view.querySelector('#lapseIgnoreSearch').addEventListener('input', function () {
            searchForIgnore(view);
        });
        view.querySelector('#btnBrowseIgnorePath').addEventListener('click', function () {
            browseForPath(view.querySelector('#lapseIgnorePath'), false, 'Select a folder to ignore');
        });
        view.querySelector('#btnAddIgnorePath').addEventListener('click', function () {
            var path = (view.querySelector('#lapseIgnorePath').value || '').trim();
            if (!path) {
                tell('Ignore list', 'Pick or type a path first.');
                return;
            }

            lapsePost('Lapse/Ignore', { Path: path }).then(function () {
                view.querySelector('#lapseIgnorePath').value = '';
                return Promise.all([refreshIgnoreRules(view), refreshItemList(view), refreshOverview(view)]);
            }).catch(function (err) {
                tell('Could not add that path', err.message);
            });
        });

        ['#lapseAppearanceEnabled', '#lapseAppearanceFontSize', '#lapseAppearancePosition',
            '#lapseAppearanceTextColor',
            '#lapseAppearanceBgColor', '#lapseAppearanceBgOpacity', '#lapseAppearanceBgEnabled']
            .forEach(function (selector) {
                view.querySelector(selector).addEventListener('input', function () {
                    updateAppearancePreview(view);
                });
                view.querySelector(selector).addEventListener('change', function () {
                    updateAppearancePreview(view);
                });
            });
    });

    document.querySelector('#LapseConfigPage').addEventListener('pagehide', function () {
        stopQueuePolling();
    });
})();
