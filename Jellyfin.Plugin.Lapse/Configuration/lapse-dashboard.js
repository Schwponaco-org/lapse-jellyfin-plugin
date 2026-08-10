// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

(function () {
    'use strict';

    var queuePollHandle = null;
    var allItems = [];
    var allEngines = [];
    var allLibraries = [];
    var allProviders = [];
    var currentSettings = null;

    var OUTPUT_MODES = [
        {
            value: 'OverwriteWithBackup',
            label: 'Overwrite, keep a backup',
            note: 'Replaces the subtitle and keeps the old one as a .bak next to it.'
        },
        {
            value: 'OverwriteNoBackup',
            label: 'Overwrite, no backup',
            note: 'Replaces the subtitle and keeps nothing.'
        },
        {
            value: 'SidecarOnly',
            label: 'Write a new file',
            note: 'Leaves the original alone. Jellyfin picks the new file up as an extra subtitle track on its next scan.'
        },
        {
            value: 'SidecarWithBackup',
            label: 'Write a new file, keep a backup',
            note: 'Same, but an earlier result at that name is kept as a .bak instead of being replaced.'
        }
    ];

    var LOW_CONFIDENCE_MODES = [
        {
            value: 'KeepOriginal',
            label: 'Keep original',
            note: 'Throws the result away and leaves the file exactly as it was. The skip is logged.'
        },
        {
            value: 'OverwriteAnyway',
            label: 'Overwrite anyway',
            note: 'Writes the result as usual, whatever the confidence was.'
        },
        {
            value: 'Sidecar',
            label: 'Sidecar',
            note: 'Writes the doubtful result to a new file and leaves the original where it is.'
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
        BackgroundEnabled: true
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

    function findEngine(id) {
        for (var i = 0; i < allEngines.length; i++) {
            if (allEngines[i].Id === id) {
                return allEngines[i];
            }
        }

        return null;
    }

    function defaultEngine() {
        for (var i = 0; i < allEngines.length; i++) {
            if (allEngines[i].IsDefault) {
                return allEngines[i];
            }
        }

        return allEngines[0] || null;
    }

    // --- sidebar navigation ---

    var NAV_STORAGE_KEY = 'lapse-active-panel';

    // One panel at a time, picked from the sidebar. The last panel someone was on is
    // remembered, so coming back to the page lands where they left off rather than
    // always at the top.
    function setUpNavigation(view) {
        var items = Array.prototype.slice.call(view.querySelectorAll('.lapseNavItem'));
        if (items.length === 0) {
            return;
        }

        function show(panelId) {
            items.forEach(function (item) {
                var active = item.getAttribute('data-panel') === panelId;
                item.classList.toggle('lapseNavItem-active', active);
                item.setAttribute('aria-current', active ? 'page' : 'false');
            });

            view.querySelectorAll('.lapsePanel').forEach(function (panel) {
                panel.classList.toggle('lapsePanel-active', panel.id === panelId);
            });

            try {
                window.localStorage.setItem(NAV_STORAGE_KEY, panelId);
            } catch (e) {
                // private browsing and friends, the panel still switches
            }
        }

        items.forEach(function (item) {
            item.addEventListener('click', function () {
                show(item.getAttribute('data-panel'));
            });
        });

        var saved = null;
        try {
            saved = window.localStorage.getItem(NAV_STORAGE_KEY);
        } catch (e) {
            // fall through to the first panel
        }

        var exists = saved && view.querySelector('#' + saved);
        show(exists ? saved : items[0].getAttribute('data-panel'));
    }

    // --- diagnostics ---

    // The single most common "the plugin doesn't work" report is the context menu entries
    // not appearing, which has nothing to do with the plugin loading and everything to do
    // with whether its script is reaching the web client. Say so up front instead of
    // leaving it to the server log.
    function refreshDiagnostics(view) {
        return lapseGet('Lapse/Diagnostics').then(function (diagnostics) {
            var strip = view.querySelector('#lapseInjectionWarning');
            var broken = diagnostics.InjectionMethod === 'None';

            strip.classList.toggle('hide', !broken);

            if (broken) {
                strip.textContent = 'The LAPSE entries can\'t be added to the item menus: ' +
                    (diagnostics.Problem || 'the web client\'s index.html could not be read from ' +
                        (diagnostics.WebPath || 'the configured web path') + '.') +
                    ' Everything on this page still works.';
            }
        }).catch(function () {
            // diagnostics failing is not itself worth an error banner
        });
    }

    // --- engines ---

    function engineStateText(engine) {
        if (!engine.Installed) {
            return engine.DownloadSupported ? 'not installed' : 'no build for this server';
        }

        return engine.RunCheckError ? 'not working' : 'installed';
    }

    function engineName(engine) {
        return engine.DisplayName + (engine.Experimental ? ' (EXPERIMENTAL)' : '');
    }

    // The version that goes next to the name. Two sources can answer: the release tag
    // recorded when the plugin installed the engine, and whatever the binary says about
    // itself. The server has already picked between them, so anything installed has
    // something to show here unless neither source knew.
    function engineVersionBadge(engine) {
        if (!engine.Installed) {
            return '';
        }

        if (engine.InstalledVersion) {
            return '<span class="lapseVersionBadge">' + escapeHtml(engine.InstalledVersion) + '</span>';
        }

        return '<span class="lapseVersionBadge lapseVersionBadge-unknown">version unknown</span>';
    }

    function engineUpdateNote(engine) {
        if (!engine.Installed) {
            return engine.LatestVersion ? ('Latest release ' + engine.LatestVersion) : '';
        }

        if (engine.UpdateAvailable && engine.LatestVersion) {
            return engine.VersionUnknown
                ? (engine.LatestVersion + ' is the latest release - update to be sure of what you are running')
                : (engine.LatestVersion + ' available');
        }

        return '';
    }

    function renderEngineStates(view) {
        view.querySelector('#lapseEngineStates').innerHTML = allEngines.map(function (engine) {
            var cls = 'lapseEngineState';
            if (engine.Installed && engine.RunCheckError) {
                cls += ' lapseEngineState-broken';
            }

            return '<span class="' + cls + '">' +
                escapeHtml(engine.DisplayName) + ' ' +
                '<span class="lapseMuted">' + escapeHtml(engineStateText(engine)) + '</span>' +
                '</span>';
        }).join('');
    }

    function capabilityChips(engine) {
        var chips = [
            { label: 'Standard', on: engine.SupportsStandard },
            { label: 'OLS', on: engine.SupportsOls },
            { label: 'Split', on: engine.SupportsSplit }
        ];

        return chips.map(function (c) {
            return '<span class="lapseChip ' + (c.on ? 'lapseChip-on' : '') + '">' + c.label + '</span>';
        }).join('');
    }

    // What the binary itself said it understands. ffsubsync alone lists about fifty flags,
    // which told nobody anything useful and swamped the card, so this is a tooltip rather
    // than a wall of text.
    function discoveredFlagsTooltip(engine) {
        if (!engine.Installed || !engine.DiscoveredFlags || !engine.DiscoveredFlags.length) {
            return '';
        }

        var source = engine.CapabilitySource === 'capabilities'
            ? 'Reported by --capabilities'
            : 'Read from the engine\'s usage text';

        return source + ': ' + engine.DiscoveredFlags.join(' ');
    }

    function renderEngineCards(view) {
        var container = view.querySelector('#lapseEngineCards');

        container.innerHTML = allEngines.map(function (engine) {
            var cardClasses = 'lapseEngineCard';
            if (engine.IsDefault) {
                cardClasses += ' lapseEngineCard-default';
            }

            if (!engine.Installed && !engine.DownloadSupported) {
                cardClasses += ' lapseEngineCard-unavailable';
            }

            var actions = '';
            if (!engine.IsDefault && engine.Installed && !engine.RunCheckError) {
                actions += '<button is="emby-button" type="button" class="raised lapseSmallButton lapseBtnSetDefault">Use by default</button>';
            }

            if (engine.DownloadSupported) {
                actions += '<button is="emby-button" type="button" class="raised lapseSmallButton lapseBtnInstall">' +
                    (engine.Installed ? 'Reinstall' : 'Install') + '</button>';
            }

            if (engine.Installed && engine.DownloadSupported) {
                actions += '<button is="emby-button" type="button" class="raised lapseSmallButton lapseBtnUpdate' +
                    (engine.UpdateAvailable ? ' button-submit' : '') + '">' +
                    (engine.UpdateAvailable ? 'Update' : 'Check + update') + '</button>';
            }

            var problem = '';
            if (!engine.DownloadSupported && !engine.Installed) {
                problem = '<div class="lapseEngineNotice">' + escapeHtml(engine.NoDownloadReason || '') +
                    (engine.BuildGuideUrl
                        ? ' <a is="emby-linkbutton" class="button-link" href="' + escapeHtml(engine.BuildGuideUrl) +
                          '" target="_blank" rel="noopener">Build instructions</a>'
                        : '') +
                    '</div>';
            } else if (engine.RunCheckError) {
                problem = '<div class="lapseEngineError">' + escapeHtml(engine.RunCheckError) + '</div>';
            }

            var updateNote = engineUpdateNote(engine);

            return '' +
                '<div class="' + cardClasses + '" data-id="' + escapeHtml(engine.Id) + '">' +
                '  <div class="lapseEngineCardTop">' +
                '    <span class="lapseEngineName" title="' + escapeHtml(discoveredFlagsTooltip(engine)) + '">' +
                escapeHtml(engineName(engine)) + engineVersionBadge(engine) + '</span>' +
                '    <span class="lapseMuted lapseEngineStateLabel">' + escapeHtml(engineStateText(engine)) + '</span>' +
                '  </div>' +
                '  <div class="lapseEngineDesc">' + escapeHtml(engine.Description) + '</div>' +
                (updateNote ? '<div class="lapseEngineVersion">' + escapeHtml(updateNote) + '</div>' : '') +
                '  <div class="lapseEngineChips">' + capabilityChips(engine) + '</div>' +
                problem +
                (actions ? '<div class="lapseEngineActions">' + actions + '</div>' : '') +
                '</div>';
        }).join('');

        container.querySelectorAll('.lapseEngineCard').forEach(function (card) {
            var id = card.getAttribute('data-id');

            var setDefault = card.querySelector('.lapseBtnSetDefault');
            if (setDefault) {
                setDefault.addEventListener('click', function () {
                    lapsePost('Lapse/Engines/' + id + '/Default').then(function () {
                        return refreshEngines(view);
                    }).catch(function (err) {
                        Dashboard.alert('Could not set the default engine: ' + err.message);
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
        });

        var installed = allEngines.filter(function (e) { return e.Installed && !e.RunCheckError; }).length;
        view.querySelector('#lapseEnginesHint').textContent = installed + ' of ' + allEngines.length + ' installed';
    }

    function installEngine(view, id, button) {
        button.disabled = true;
        button.textContent = 'Installing...';
        Dashboard.showLoadingMsg();

        lapsePost('Lapse/Engines/' + id + '/Install').then(function () {
            Dashboard.hideLoadingMsg();
            return refreshEngines(view);
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Could not install that engine: ' + err.message);
            return refreshEngines(view);
        });
    }

    function updateEngine(view, id, button) {
        button.disabled = true;
        Dashboard.showLoadingMsg();

        lapsePost('Lapse/Engines/' + id + '/Update').then(function (result) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert(id + ': ' + ((result && result.Outcome) || 'done'));
            return refreshEngines(view);
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Could not update that engine: ' + err.message);
            return refreshEngines(view);
        });
    }

    function installAllEngines(view) {
        Dashboard.showLoadingMsg();
        lapsePost('Lapse/Engines/InstallAll').then(function (results) {
            Dashboard.hideLoadingMsg();

            var lines = Object.keys(results || {}).map(function (id) {
                return id + ': ' + results[id];
            });
            Dashboard.alert(lines.join('\n') || 'Nothing to install.');
            return refreshEngines(view);
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Could not install the engines: ' + err.message);
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

            Dashboard.alert(lines.join('\n'));
            return refreshEngines(view);
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Could not check for updates: ' + err.message);
        });
    }

    function refreshEngines(view) {
        return lapseGet('Lapse/Engines').then(function (engines) {
            allEngines = engines;
            renderEngineStates(view);
            renderEngineCards(view);
            renderEngineSettings(view);
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
            container.innerHTML = '<div class="fieldDescription" style="padding:.8em;">No libraries found.</div>';
            return;
        }

        container.innerHTML = allLibraries.map(function (library) {
            var frequency = library.ScheduleFrequency || 'Daily';

            return '' +
                '<div class="lapseLibraryRow" data-id="' + library.ItemId + '">' +
                '  <div class="lapseLibraryMain">' +
                '    <label class="emby-checkbox-label lapseInlineCheck">' +
                '      <input type="checkbox" is="emby-checkbox" class="lapseChkLibraryEnabled"' + (library.Enabled ? ' checked' : '') + ' />' +
                '      <span class="lapseLibraryName">' + escapeHtml(library.Name) + '</span>' +
                '    </label>' +
                '    <span class="lapseMuted lapseLibraryType">' + escapeHtml(library.CollectionType || 'mixed') + '</span>' +
                '  </div>' +
                '  <div class="lapseLibrarySchedule">' +
                '    <label class="emby-checkbox-label lapseInlineCheck">' +
                '      <input type="checkbox" is="emby-checkbox" class="lapseChkSchedule"' + (library.ScheduleEnabled ? ' checked' : '') + ' />' +
                '      <span>Schedule</span>' +
                '    </label>' +
                '    <select is="emby-select" class="emby-select-withcolor emby-select lapseScheduleFrequency">' +
                frequencyOptions(frequency) + '</select>' +
                '    <select is="emby-select" class="emby-select-withcolor emby-select lapseScheduleDay">' +
                dayOptions(library.ScheduleDay || 'Sunday') + '</select>' +
                '    <input is="emby-input" type="time" class="lapseScheduleTime" value="' + escapeHtml(library.ScheduleTime || '03:00') + '" />' +
                '  </div>' +
                '</div>';
        }).join('');

        container.querySelectorAll('.lapseLibraryRow').forEach(function (row) {
            var scheduleCheck = row.querySelector('.lapseChkSchedule');
            var enabledCheck = row.querySelector('.lapseChkLibraryEnabled');
            var frequencySelect = row.querySelector('.lapseScheduleFrequency');
            var daySelect = row.querySelector('.lapseScheduleDay');
            var timeInput = row.querySelector('.lapseScheduleTime');

            // Nothing about the schedule is shown until there is one, and the day only
            // means anything for the frequencies that repeat on one - "every day" has no
            // day to pick, so it never gets offered one.
            function syncRowState() {
                var scheduled = scheduleCheck.checked && enabledCheck.checked;
                var needsDay = scheduled && frequencySelect.value !== 'Daily';

                scheduleCheck.disabled = !enabledCheck.checked;

                frequencySelect.classList.toggle('hide', !scheduled);
                timeInput.classList.toggle('hide', !scheduled);
                daySelect.classList.toggle('hide', !needsDay);

                frequencySelect.disabled = !scheduled;
                timeInput.disabled = !scheduled;
                daySelect.disabled = !needsDay;
            }

            scheduleCheck.addEventListener('change', syncRowState);
            enabledCheck.addEventListener('change', syncRowState);
            frequencySelect.addEventListener('change', syncRowState);
            syncRowState();
        });

        var enabled = allLibraries.filter(function (l) { return l.Enabled; }).length;
        var scheduled = allLibraries.filter(function (l) { return l.Enabled && l.ScheduleEnabled; }).length;
        view.querySelector('#lapseLibrariesHint').textContent =
            enabled + ' of ' + allLibraries.length + ' enabled' + (scheduled ? (', ' + scheduled + ' scheduled') : '');
    }

    function refreshLibraries(view) {
        return lapseGet('Lapse/Libraries').then(function (libraries) {
            allLibraries = libraries;
            renderLibraries(view);
            renderLibraryFilter(view);
            renderShowLibraries(view);
        });
    }

    function saveLibraries(view) {
        var payload = { Libraries: [] };

        view.querySelectorAll('.lapseLibraryRow').forEach(function (row) {
            var frequency = row.querySelector('.lapseScheduleFrequency').value;

            payload.Libraries.push({
                ItemId: row.getAttribute('data-id'),
                Enabled: row.querySelector('.lapseChkLibraryEnabled').checked,
                ScheduleEnabled: row.querySelector('.lapseChkSchedule').checked,
                ScheduleFrequency: frequency,
                ScheduleDay: frequency === 'Daily' ? null : row.querySelector('.lapseScheduleDay').value,
                ScheduleTime: row.querySelector('.lapseScheduleTime').value || '03:00'
            });
        });

        Dashboard.showLoadingMsg();
        lapsePost('Lapse/Libraries', payload).then(function () {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Library settings saved.');

            // read them straight back rather than trusting the form: if anything didn't
            // stick, the toggles snapping back is the honest answer
            return refreshLibraries(view).then(function () {
                return refreshItemList(view);
            });
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Could not save the library settings: ' + err.message);
        });
    }

    function renderLibraryFilter(view) {
        var select = view.querySelector('#lapseItemLibraryFilter');
        var previous = select.value;

        select.innerHTML = '<option value="">All libraries</option>' + allLibraries.map(function (library) {
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
                '<div class="lapseShowLibrary" data-id="' + library.ItemId + '">' +
                '  <div class="lapseShowLibraryName">' + escapeHtml(library.Name) + '</div>' +
                '  <div class="lapseButtonRow">' +
                '    <button is="emby-button" type="button" class="raised lapseSmallButton lapseBtnSyncAllSeries">' +
                '      <span>Sync all series</span></button>' +
                '    <button is="emby-button" type="button" class="raised lapseSmallButton lapseBtnSyncSeason">' +
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
                    Dashboard.alert('Could not start the sync: ' + err.message);
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
                Dashboard.alert('No series found in that library.');
                return;
            }

            var overlay = openOverlay(
                '<h3>Sync a season</h3>' +
                '<div class="selectContainer">' +
                '  <label class="selectLabel">Series</label>' +
                '  <select is="emby-select" id="lapseSeasonSeries" class="emby-select-withcolor emby-select">' +
                series.map(function (s) {
                    return '<option value="' + s.ItemId + '">' + escapeHtml(s.Name) + '</option>';
                }).join('') +
                '  </select>' +
                '</div>' +
                '<div class="selectContainer">' +
                '  <label class="selectLabel">Season</label>' +
                '  <select is="emby-select" id="lapseSeasonSeason" class="emby-select-withcolor emby-select"></select>' +
                '</div>' +
                '<div class="lapseDialogButtons">' +
                '  <button is="emby-button" type="button" class="raised" id="lapseSeasonCancel"><span>Cancel</span></button>' +
                '  <button is="emby-button" type="button" class="raised button-submit" id="lapseSeasonSync"><span>Sync</span></button>' +
                '</div>');

            var seriesSelect = overlay.querySelector('#lapseSeasonSeries');
            var seasonSelect = overlay.querySelector('#lapseSeasonSeason');
            var syncButton = overlay.querySelector('#lapseSeasonSync');

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
                    Dashboard.alert('Could not load seasons: ' + err.message);
                });
            }

            seriesSelect.addEventListener('change', loadSeasons);
            loadSeasons();

            overlay.querySelector('#lapseSeasonCancel').addEventListener('click', function () {
                overlay.remove();
            });

            syncButton.addEventListener('click', function () {
                var seasonId = seasonSelect.value;
                if (!seasonId) {
                    return;
                }

                overlay.remove();
                Dashboard.showLoadingMsg();

                lapsePost('Lapse/Series/Sync', { ItemId: seasonId }).then(function (result) {
                    Dashboard.hideLoadingMsg();
                    Dashboard.alert('Queued ' + ((result && result.Queued) || 0) + ' episodes.');
                    startQueuePolling(view);
                    refreshQueue(view);
                }).catch(function (err) {
                    Dashboard.hideLoadingMsg();
                    Dashboard.alert('Could not start the sync: ' + err.message);
                });
            });
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Could not load the series list: ' + err.message);
        });
    }

    // --- sync queue ---

    function refreshQueue(view) {
        return lapseGet('Lapse/Queue').then(function (snapshot) {
            var section = view.querySelector('#lapseQueueSection');
            section.classList.toggle('hide', !snapshot.Running);

            if (snapshot.Running) {
                var pct = snapshot.Total === 0 ? 0 : Math.round((snapshot.Completed / snapshot.Total) * 100);
                var unit = snapshot.UnitName || 'item';
                var plural = snapshot.Total === 1 ? unit : unit + 's';

                view.querySelector('#lapseQueueBar').value = pct;
                view.querySelector('#lapseQueueText').textContent =
                    (snapshot.JobName ? snapshot.JobName + ': ' : '') +
                    snapshot.Completed + ' / ' + snapshot.Total + ' ' + plural + ' processed' +
                    (snapshot.CurrentItemName ? (' - ' + snapshot.CurrentItemName) : '');
            } else if (queuePollHandle) {
                refreshItemList(view);
            }
        });
    }

    function startQueuePolling(view) {
        if (queuePollHandle) {
            return;
        }

        queuePollHandle = setInterval(function () {
            refreshQueue(view);
        }, 2000);
    }

    function stopQueuePolling() {
        if (queuePollHandle) {
            clearInterval(queuePollHandle);
            queuePollHandle = null;
        }
    }

    // --- item status list ---

    function statusLabel(item) {
        switch (item.Status) {
            case 'Synced': return 'Synced';
            case 'PartiallySynced': return item.SyncedSubtitleCount + ' of ' + item.SubtitleCount + ' synced';
            case 'Skipped': return 'Skipped';
            case 'Failed': return 'Failed';
            default: return 'Not synced';
        }
    }

    // Status comes back in PascalCase and the pill classes are lower case, so the two are
    // mapped rather than lowercased blindly - "partiallysynced" would read as a typo in
    // the stylesheet.
    function statusPillClass(status) {
        switch (status) {
            case 'Synced': return 'lapseStatusPill-synced';
            case 'PartiallySynced': return 'lapseStatusPill-partial';
            case 'Skipped': return 'lapseStatusPill-skipped';
            case 'Failed': return 'lapseStatusPill-failed';
            default: return 'lapseStatusPill-pending';
        }
    }

    function renderItemList(view, items) {
        var container = view.querySelector('#lapseItemList');

        allItems = items;

        // libraries that scan in unrelated video files (phone backups, personal clips,
        // whatever) fill up with items that have no external subtitle at all, so hide
        // those unless the user asks for everything
        var includeAll = view.querySelector('#lapseIncludeAll').checked;
        var shown = includeAll ? items : items.filter(function (i) { return i.HasExternalSubtitle; });

        var libraryId = view.querySelector('#lapseItemLibraryFilter').value;
        if (libraryId) {
            shown = shown.filter(function (i) { return i.LibraryId === libraryId; });
        }

        var search = (view.querySelector('#lapseItemSearch').value || '').trim().toLowerCase();
        if (search) {
            shown = shown.filter(function (i) {
                return i.Name.toLowerCase().indexOf(search) !== -1;
            });
        }

        view.querySelector('#lapseItemsHint').textContent = shown.length + ' of ' + items.length;

        if (shown.length === 0) {
            if (search || libraryId) {
                container.innerHTML = '<div class="fieldDescription" style="padding:.8em;">Nothing matches that filter.</div>';
            } else if (!includeAll) {
                container.innerHTML = '<div class="fieldDescription" style="padding:.8em;">No items with an external subtitle. Turn on "Include all" to see everything.</div>';
            } else {
                container.innerHTML = '<div class="fieldDescription" style="padding:.8em;">No items found. Check that at least one library is turned on above.</div>';
            }

            return;
        }

        // a big TV library can be thousands of episodes, and rendering all of them makes
        // the page crawl for no benefit
        var capped = shown.slice(0, 500);

        container.innerHTML = capped.map(function (item) {
            var pillClass = statusPillClass(item.Status);
            var skipLabel = item.Status === 'Skipped' ? 'Un-skip' : 'Skip';
            var errorLine = item.LastError
                ? ('<div class="listItemBodyText secondary lapseItemError">' + escapeHtml(item.LastError) + '</div>')
                : '';

            return '' +
                '<div class="listItem lapseItemRow" data-id="' + item.ItemId + '" data-name="' + escapeHtml(item.Name) + '">' +
                '  <div class="listItemBody">' +
                '    <div class="listItemBodyText">' + escapeHtml(item.Name) +
                '      <span class="lapseStatusPill ' + pillClass + '">' + escapeHtml(statusLabel(item)) + '</span></div>' +
                '    <div class="listItemBodyText secondary lapseItemMeta">' +
                escapeHtml(item.LibraryName || 'unknown library') + ' &middot; ' + escapeHtml(item.ItemType) +
                ' &middot; ' + item.SubtitleCount + ' subtitle' + (item.SubtitleCount === 1 ? '' : 's') + '</div>' +
                errorLine +
                '  </div>' +
                '  <div class="lapseItemRowActions">' +
                '    <button is="emby-button" type="button" class="raised lapseBtnSync">Sync</button>' +
                '    <button is="emby-button" type="button" class="raised lapseBtnAdvanced">Advanced</button>' +
                '    <button is="emby-button" type="button" class="raised lapseBtnSkip">' + skipLabel + '</button>' +
                '  </div>' +
                '</div>';
        }).join('');

        if (shown.length > capped.length) {
            container.innerHTML += '<div class="fieldDescription" style="padding:.8em;">' +
                'Showing the first ' + capped.length + ' of ' + shown.length + '. Narrow it down with the search or the library filter.</div>';
        }

        container.querySelectorAll('.lapseItemRow').forEach(function (row) {
            var itemId = row.getAttribute('data-id');
            var name = row.getAttribute('data-name');

            row.querySelector('.lapseBtnSync').addEventListener('click', function () {
                quickSync(view, itemId, name);
            });
            row.querySelector('.lapseBtnAdvanced').addEventListener('click', function () {
                openAdvancedDialog(view, itemId, name);
            });
            row.querySelector('.lapseBtnSkip').addEventListener('click', function () {
                var isSkipping = !row.querySelector('.lapseBtnSkip').textContent.trim().startsWith('Un-skip');
                lapsePost('Lapse/Skip', { ItemId: itemId, Skip: isSkipping }).then(function () {
                    refreshItemList(view);
                });
            });
        });
    }

    function refreshItemList(view) {
        return lapseGet('Lapse/Status').then(function (items) {
            renderItemList(view, items);
        });
    }

    function quickSync(view, itemId, name) {
        // the engine only takes one subtitle at a time, so ask which one if there are several
        lapseGet('Lapse/Items/' + itemId + '/Subtitles').then(function (subtitles) {
            if (subtitles.length === 0) {
                Dashboard.alert('No external subtitle found for ' + name + '.');
                return;
            }

            if (subtitles.length === 1) {
                runSync(view, itemId, name, subtitles[0].Path);
                return;
            }

            openSubtitlePickerDialog(view, itemId, name, subtitles);
        }).catch(function (err) {
            Dashboard.alert('Could not check subtitles for ' + name + ': ' + err.message);
        });
    }

    function runSync(view, itemId, name, subtitlePath) {
        Dashboard.showLoadingMsg();
        lapsePost('Lapse/Sync', { ItemId: itemId, Mode: 'Standard', SubtitlePath: subtitlePath }).then(function (result) {
            Dashboard.hideLoadingMsg();
            showSyncResultAlert(name, result);
            refreshItemList(view);
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Sync failed for ' + name + ': ' + err.message);
        });
    }

    function subtitleOptionsHtml(subtitles) {
        return subtitles.map(function (s) {
            return '<option value="' + escapeHtml(s.Path) + '">' + escapeHtml(s.DisplayName) + '</option>';
        }).join('');
    }

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

    function openSubtitlePickerDialog(view, itemId, name, subtitles) {
        var overlay = openOverlay(
            '<h3>Pick a subtitle</h3>' +
            '<div class="selectContainer">' +
            '  <select is="emby-select" id="lapseQuickPickerSelect" class="emby-select-withcolor emby-select">' +
            subtitleOptionsHtml(subtitles) +
            '  </select>' +
            '</div>' +
            '<div class="lapseDialogButtons">' +
            '  <button is="emby-button" type="button" class="raised" id="lapseQuickPickerCancel"><span>Cancel</span></button>' +
            '  <button is="emby-button" type="button" class="raised button-submit" id="lapseQuickPickerSync"><span>Sync</span></button>' +
            '</div>');

        overlay.querySelector('#lapseQuickPickerCancel').addEventListener('click', function () {
            overlay.remove();
        });

        overlay.querySelector('#lapseQuickPickerSync').addEventListener('click', function () {
            var path = overlay.querySelector('#lapseQuickPickerSelect').value;
            overlay.remove();
            runSync(view, itemId, name, path);
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
            // engines we don't have a documented output format for just report what they said
            parts.push(result.EngineOutput);
        }

        if (result.Confidence != null) {
            parts.push('confidence ' + Math.round(result.Confidence * 100) + '%');
        }

        return parts.join(', ') || 'done';
    }

    function showSyncResultAlert(name, result) {
        if (!result.Success) {
            Dashboard.alert(name + ': sync failed - ' + result.Error);
            return;
        }

        if (result.Skipped) {
            Dashboard.alert(name + ': left the original alone (' + describeResult(result) + ').\n\n' +
                'That is under the confidence threshold, and File output is set to keep the original ' +
                'when that happens.');
            return;
        }

        var engine = findEngine(result.EngineId);
        var engineLabel = engine ? engine.DisplayName : (result.EngineId || 'engine');
        var written = result.OutputPath ? ('\nWrote ' + result.OutputPath) : '';
        Dashboard.alert(name + ': synced with ' + engineLabel + ' (' + describeResult(result) + ')' + written);
    }

    // --- advanced sync dialog ---

    function openAdvancedDialog(view, itemId, name) {
        lapseGet('Lapse/Items/' + itemId + '/Subtitles').then(function (subtitles) {
            showAdvancedDialog(view, itemId, name, subtitles);
        }).catch(function (err) {
            Dashboard.alert('Could not open advanced sync for ' + name + ': ' + err.message);
        });
    }

    // Build the mode list for one engine. Unsupported modes stay in the list but are
    // disabled and say why, so it's obvious the engine is the reason rather than the
    // option having silently vanished.
    function modeOptionsFor(engine, selected) {
        var modes = [
            { value: 'Standard', label: 'Standard', supported: engine.SupportsStandard },
            { value: 'Ols', label: 'Standard OLS', supported: engine.SupportsOls },
            { value: 'Split', label: 'Split', supported: engine.SupportsSplit }
        ];

        return modes.map(function (m) {
            var label = m.supported ? m.label : m.label + ' (not supported by ' + engine.DisplayName + ')';
            return '<option value="' + m.value + '"' +
                (m.supported ? '' : ' disabled') +
                (m.value === selected && m.supported ? ' selected' : '') +
                '>' + escapeHtml(label) + '</option>';
        }).join('');
    }

    function firstSupportedMode(engine) {
        if (engine.SupportsStandard) {
            return 'Standard';
        }

        if (engine.SupportsOls) {
            return 'Ols';
        }

        return 'Split';
    }

    function configuredProviders() {
        return allProviders.filter(function (p) { return p.Configured; });
    }

    function showAdvancedDialog(view, itemId, name, subtitles) {
        var startEngine = defaultEngine();
        if (!startEngine) {
            Dashboard.alert('No engines are available.');
            return;
        }

        var subtitlePickerHtml = '';
        if (subtitles.length === 0) {
            subtitlePickerHtml = '<p class="fieldDescription">No external subtitle found for this item.</p>';
        } else if (subtitles.length > 1) {
            subtitlePickerHtml = '' +
                '<div class="selectContainer">' +
                '  <label class="selectLabel">Subtitle</label>' +
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
            ? '<hr class="lapseDialogRule" />' +
              '<div class="selectContainer">' +
              '  <label class="selectLabel">Sync all subtitles to this one</label>' +
              '  <select is="emby-select" id="lapseAdvReference" class="emby-select-withcolor emby-select">' +
              subtitleOptionsHtml(subtitles) +
              '  </select>' +
              '</div>' +
              '<div class="fieldDescription lapseTightNote">Lines the other ' + (subtitles.length - 1) +
              ' subtitle' + (subtitles.length === 2 ? '' : 's') + ' up against this one instead of against the audio. Faster and usually more accurate, as long as this one is right.</div>' +
              '<button is="emby-button" type="button" class="raised lapseSmallButton" id="lapseAdvSyncAll"><span>Sync all to reference</span></button>'
            : '';

        var providers = configuredProviders();
        var threshold = (currentSettings && currentSettings.TranslationConfidenceThreshold) || 70;

        var translationHtml = (subtitles.length > 0 && providers.length > 0)
            ? '<hr class="lapseDialogRule" />' +
              '<h4 class="lapseSubHeading">Translate</h4>' +
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
              providers.map(function (p) {
                  return '<option value="' + escapeHtml(p.Id) + '"' + (p.IsDefault ? ' selected' : '') + '>' +
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
              ((currentSettings && currentSettings.TranslationIncludeMetadataHeader) ? ' checked' : '') + ' />' +
              '  <span>Add a metadata comment block at the top</span>' +
              '</label>' +
              '<div class="fieldDescription lapseTightNote">Writes a new file next to the original, never over it.</div>' +
              '<button is="emby-button" type="button" class="raised lapseSmallButton" id="lapseAdvTranslate"><span>Translate</span></button>'
            : '';

        var overlay = openOverlay(
            '  <h3>' + escapeHtml(name) + '</h3>' +
            '  <div class="selectContainer">' +
            '    <label class="selectLabel">Engine</label>' +
            '    <select is="emby-select" id="lapseAdvEngine" class="emby-select-withcolor emby-select">' + engineOptions + '</select>' +
            '  </div>' +
            '  <div class="selectContainer">' +
            '    <label class="selectLabel">Mode</label>' +
            '    <select is="emby-select" id="lapseAdvMode" class="emby-select-withcolor emby-select">' +
            modeOptionsFor(startEngine, 'Standard') +
            '    </select>' +
            '  </div>' +
            '  <div class="inputContainer hide" id="lapseAdvPenaltyContainer">' +
            '    <label class="inputLabel inputLabelUnfocused" for="lapseAdvPenalty">Penalty</label>' +
            '    <input is="emby-input" id="lapseAdvPenalty" type="number" value="' + startEngine.Penalty + '" />' +
            '    <div class="fieldDescription" id="lapseAdvPenaltyNote">Higher values = fewer splits.</div>' +
            '  </div>' +
            subtitlePickerHtml +
            '  <div class="lapseDialogButtons">' +
            '    <button is="emby-button" type="button" class="raised" id="lapseAdvCancel"><span>Cancel</span></button>' +
            '    <button is="emby-button" type="button" class="raised button-submit" id="lapseAdvSync" ' + (subtitles.length === 0 ? 'disabled' : '') + '><span>Sync</span></button>' +
            '  </div>' +
            multiTrackHtml +
            translationHtml +
            '  <hr class="lapseDialogRule" />' +
            '  <div class="inputContainer">' +
            '    <label class="inputLabel inputLabelUnfocused" for="lapseAdvNudge">Fine tune (seconds)</label>' +
            '    <div class="lapseNudgeRow">' +
            '      <input is="emby-input" id="lapseAdvNudge" type="number" step="0.1" value="0" />' +
            '      <button is="emby-button" type="button" class="raised lapseSmallButton" id="lapseAdvNudgeApply" ' + (subtitles.length === 0 ? 'disabled' : '') + '><span>Shift</span></button>' +
            '    </div>' +
            '    <div class="fieldDescription">Still slightly off? Minus makes subtitles show up earlier, plus later. Works on .srt and .vtt, and does not use an engine.</div>' +
            '  </div>');

        var engineSelect = overlay.querySelector('#lapseAdvEngine');
        var modeSelect = overlay.querySelector('#lapseAdvMode');
        var penaltyContainer = overlay.querySelector('#lapseAdvPenaltyContainer');
        var penaltyInput = overlay.querySelector('#lapseAdvPenalty');
        var penaltyNote = overlay.querySelector('#lapseAdvPenaltyNote');

        function currentEngine() {
            return findEngine(engineSelect.value) || startEngine;
        }

        function syncPenaltyVisibility() {
            var engine = currentEngine();
            var isSplit = modeSelect.value === 'Split';
            penaltyContainer.classList.toggle('hide', !(isSplit && engine.SupportsPenalty));
            penaltyNote.textContent = 'Higher values = fewer splits. ' + engine.DisplayName +
                ' takes ' + engine.MinPenalty + ' to ' + engine.MaxPenalty + ', default ' + engine.Penalty + '.';
        }

        engineSelect.addEventListener('change', function () {
            var engine = currentEngine();
            var wanted = modeSelect.value;

            // rebuild the modes for the newly picked engine, and if the mode that was
            // selected isn't something this engine can do, drop back to one it can
            modeSelect.innerHTML = modeOptionsFor(engine, wanted);
            if (modeSelect.selectedIndex === -1 || modeSelect.options[modeSelect.selectedIndex].disabled) {
                modeSelect.value = firstSupportedMode(engine);
            }

            penaltyInput.value = engine.Penalty;
            penaltyInput.min = engine.MinPenalty;
            penaltyInput.max = engine.MaxPenalty;
            syncPenaltyVisibility();
        });

        modeSelect.addEventListener('change', syncPenaltyVisibility);
        syncPenaltyVisibility();

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

        overlay.querySelector('#lapseAdvCancel').addEventListener('click', function () {
            overlay.remove();
        });

        overlay.querySelector('#lapseAdvSync').addEventListener('click', function () {
            Dashboard.showLoadingMsg();
            lapsePost('Lapse/Sync', {
                ItemId: itemId,
                EngineId: currentEngine().Id,
                Mode: modeSelect.value,
                Penalty: currentPenalty(),
                SubtitlePath: selectedSubtitlePath()
            }).then(function (result) {
                Dashboard.hideLoadingMsg();
                overlay.remove();
                showSyncResultAlert(name, result);
                refreshItemList(view);
            }).catch(function (err) {
                Dashboard.hideLoadingMsg();
                Dashboard.alert('Sync failed for ' + name + ': ' + err.message);
            });
        });

        var syncAllButton = overlay.querySelector('#lapseAdvSyncAll');
        if (syncAllButton) {
            syncAllButton.addEventListener('click', function () {
                Dashboard.showLoadingMsg();
                lapsePost('Lapse/SyncAllSubtitles', {
                    ItemId: itemId,
                    ReferencePath: overlay.querySelector('#lapseAdvReference').value,
                    EngineId: currentEngine().Id,
                    Mode: modeSelect.value,
                    Penalty: currentPenalty()
                }).then(function (result) {
                    Dashboard.hideLoadingMsg();
                    overlay.remove();
                    showMultiSyncAlert(name, result);
                    refreshItemList(view);
                }).catch(function (err) {
                    Dashboard.hideLoadingMsg();
                    Dashboard.alert('Could not sync the other subtitles: ' + err.message);
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
                    Dashboard.alert('Enter the language code to translate into first, e.g. es for Spanish.');
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
                    Dashboard.alert('Translation failed: ' + err.message);
                });
            });
        }

        var nudgeButton = overlay.querySelector('#lapseAdvNudgeApply');
        if (nudgeButton) {
            nudgeButton.addEventListener('click', function () {
                var offset = parseFloat(overlay.querySelector('#lapseAdvNudge').value);
                if (!offset) {
                    Dashboard.alert('Enter how many seconds to move the subtitle first.');
                    return;
                }

                Dashboard.showLoadingMsg();
                lapsePost('Lapse/Shift', {
                    ItemId: itemId,
                    SubtitlePath: selectedSubtitlePath(),
                    OffsetSeconds: offset
                }).then(function (result) {
                    Dashboard.hideLoadingMsg();
                    Dashboard.alert('Moved ' + result.Shifted + ' timestamps by ' + offset + 's.\nWrote ' + result.OutputPath);
                }).catch(function (err) {
                    Dashboard.hideLoadingMsg();
                    Dashboard.alert('Could not shift the subtitle: ' + err.message);
                });
            });
        }
    }

    function showMultiSyncAlert(name, result) {
        var lines = result.Results.map(function (outcome) {
            var r = outcome.Result || {};
            return outcome.DisplayName + ': ' + (r.Success ? describeResult(r) : ('failed - ' + r.Error));
        });

        Dashboard.alert(name + ': ' + result.SucceededCount + ' of ' + result.Results.length +
            ' subtitles synced to the reference.\n\n' + lines.join('\n'));
    }

    function showTranslationAlert(name, result) {
        if (!result.Success) {
            Dashboard.alert('Translation failed: ' + result.Error);
            return;
        }

        Dashboard.alert(name + ': translated ' + result.TranslatedCount + ' of ' + result.LineCount +
            ' lines with ' + result.Provider + '.\n' +
            'Average confidence ' + result.AverageConfidence + '%, ' + result.LowConfidenceCount + ' below the threshold.\n' +
            'Wrote ' + result.OutputPath);
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
            Dashboard.alert('Could not start sync: ' + err.message);
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
            refreshFolders(view);
            refreshItemList(view);
        });
    }

    // --- subtitle to subtitle sync ---

    // Suggest a name for the third file: the input's own name with the configured sidecar
    // suffix dropped in, which is the same shape the sidecar output modes produce. Only
    // ever a suggestion - the field is free text and an edited one is left alone.
    function suggestSubToSubOutput(inputPath) {
        if (!inputPath) {
            return '';
        }

        var suffix = (currentSettings && currentSettings.SidecarSuffix) || '.shifted';
        if (suffix.charAt(0) !== '.') {
            suffix = '.' + suffix;
        }

        var dot = inputPath.lastIndexOf('.');
        if (dot <= 0) {
            return inputPath + suffix;
        }

        var stem = inputPath.substring(0, dot);
        var extension = inputPath.substring(dot);

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
    }

    function syncSubtitles(view) {
        var referencePath = view.querySelector('#lapseRefSubPath').value;
        var inputPath = view.querySelector('#lapseInputSubPath').value;

        if (!referencePath || !inputPath) {
            Dashboard.alert('Pick both a reference and an input subtitle first.');
            return;
        }

        var body = { ReferencePath: referencePath, InputPath: inputPath };

        if (view.querySelector('#lapseSubToSubNewFile').checked) {
            var outputPath = (view.querySelector('#lapseSubToSubOutput').value || '').trim();
            if (!outputPath) {
                Dashboard.alert('Give the new subtitle file a name, or untick the box to overwrite the input.');
                return;
            }

            body.OutputPath = outputPath;
        }

        Dashboard.showLoadingMsg();
        lapsePost('Lapse/SyncSubtitles', body).then(function (result) {
            Dashboard.hideLoadingMsg();
            if (!result.Success) {
                Dashboard.alert('Sync failed: ' + result.Error);
            } else if (result.Skipped) {
                Dashboard.alert('Left the input alone (' + describeResult(result) + '), which is under the confidence threshold.');
            } else {
                Dashboard.alert('Synced! (' + describeResult(result) + ')\nWrote ' + result.OutputPath);
            }
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Sync failed: ' + err.message);
        });
    }

    // --- settings: output, translation, appearance, engines ---

    function renderRadioGroup(container, name, modes, selected, onChange) {
        container.innerHTML = modes.map(function (mode) {
            return '' +
                '<label class="lapseRadioRow">' +
                '  <input type="radio" name="' + name + '" value="' + mode.value + '"' +
                (selected === mode.value ? ' checked' : '') + ' />' +
                '  <span class="lapseRadioLabel">' + escapeHtml(mode.label) +
                '    <span class="fieldDescription">' + escapeHtml(mode.note) + '</span>' +
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
        return selectedRadio(view, 'lapseOutputMode', 'OverwriteWithBackup');
    }

    function updateSidecarPreview(view) {
        var mode = selectedOutputMode(view);
        var suffix = view.querySelector('#lapseSidecarSuffix').value || '.shifted';
        if (suffix.charAt(0) !== '.') {
            suffix = '.' + suffix;
        }

        var preview = mode.indexOf('Sidecar') === 0
            ? 'Movie.en.srt becomes Movie.en' + suffix + '.srt'
            : 'Only used by the two "write a new file" modes.';

        view.querySelector('#lapseSidecarPreview').textContent = preview;
        view.querySelector('#lapseOutputHint').textContent =
            (OUTPUT_MODES.filter(function (m) { return m.value === mode; })[0] || {}).label || '';
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

        var syncConfidence = view.querySelector('#lapseSyncConfidence');
        syncConfidence.value = currentSettings.SyncConfidenceThreshold;
        view.querySelector('#lapseSyncConfidenceValue').textContent = syncConfidence.value;

        view.querySelector('#lapseConfidence').value = currentSettings.TranslationConfidenceThreshold;
        view.querySelector('#lapseKeepLowConfidence').checked = !!currentSettings.TranslationKeepLowConfidenceOriginal;
        view.querySelector('#lapseMetadataHeader').checked = !!currentSettings.TranslationIncludeMetadataHeader;

        renderAppearance(view);
        updateSidecarPreview(view);

        // the suggested name for a subtitle-to-subtitle output uses the sidecar suffix, so
        // it can only be right once the settings have actually arrived
        updateSubToSubOutput(view, false);
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
            var fields = providerFieldsHtml(provider);

            return '' +
                '<div class="lapseProviderCard' + (provider.Configured ? ' lapseProviderCard-on' : '') + '">' +
                '  <div class="lapseProviderTop">' +
                '    <span class="lapseProviderName">' + escapeHtml(provider.DisplayName) + '</span>' +
                '    <span class="lapseChip lapseChip-tier">' + escapeHtml(tierLabel(provider.Tier)) + '</span>' +
                '    <span class="lapseMuted lapseProviderState">' +
                (provider.Configured ? 'active' : 'not configured') + '</span>' +
                '  </div>' +
                '  <div class="fieldDescription lapseTightNote">' + escapeHtml(provider.Summary) + '</div>' +
                fields +
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
        view.querySelector('#lapseTranslationHint').textContent =
            active + ' of ' + allProviders.length + ' providers ready';
    }

    function setInputValue(view, selector, value) {
        var input = view.querySelector(selector);
        if (input) {
            input.value = value || '';
        }
    }

    // The provider fields are rendered per provider, so they only exist once the provider
    // list has loaded. Saving any other part of the page before then must not read them
    // as empty and wipe someone's API keys, so fall back to what the server last told us.
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

    function currentAppearance(view) {
        return {
            Enabled: view.querySelector('#lapseAppearanceEnabled').checked,
            FontSizePx: parseInt(view.querySelector('#lapseAppearanceFontSize').value, 10) || APPEARANCE_DEFAULTS.FontSizePx,
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
        view.querySelector('#lapseAppearanceTextColor').value = APPEARANCE_DEFAULTS.TextColor;
        view.querySelector('#lapseAppearanceBgColor').value = background.rgb;
        view.querySelector('#lapseAppearanceBgOpacity').value = background.opacity;
        view.querySelector('#lapseAppearanceBgEnabled').checked = APPEARANCE_DEFAULTS.BackgroundEnabled;

        updateAppearancePreview(view);
    }

    // Everything on this page saves through the same endpoint, so each save reads the
    // current form state for all of it rather than sending a partial object that would
    // blank out whatever the user didn't touch.
    function collectSettings(view) {
        var saved = currentSettings || {};

        var payload = {
            OutputMode: selectedOutputMode(view),
            SidecarSuffix: view.querySelector('#lapseSidecarSuffix').value,
            LowConfidenceAction: selectedRadio(view, 'lapseLowConfidence', 'KeepOriginal'),
            SyncConfidenceThreshold: parseInt(view.querySelector('#lapseSyncConfidence').value, 10) || 0,
            AutoUpdateEngines: view.querySelector('#lapseAutoUpdateEngines').checked,
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
            SubtitleAppearance: currentAppearance(view),
            Engines: []
        };

        view.querySelectorAll('.lapseEngineSettingBlock').forEach(function (block) {
            var penaltyInput = block.querySelector('.lapseSettingPenalty');
            var pathInput = block.querySelector('.lapseSettingPath');

            payload.Engines.push({
                EngineId: block.getAttribute('data-id'),
                PathOverride: (pathInput.value || '').trim() || null,
                Penalty: penaltyInput ? (parseInt(penaltyInput.value, 10) || null) : null
            });
        });

        return payload;
    }

    function saveSettings(view, message) {
        Dashboard.showLoadingMsg();

        lapsePost('Lapse/Settings', collectSettings(view)).then(function () {
            Dashboard.hideLoadingMsg();
            Dashboard.alert(message);
            return refreshSettings(view).then(function () {
                return Promise.all([refreshEngines(view), refreshProviders(view)]);
            });
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Could not save: ' + err.message);
        });
    }

    function renderEngineSettings(view) {
        var container = view.querySelector('#lapseEngineSettings');

        container.innerHTML = allEngines.map(function (engine) {
            var penaltyField = '';
            if (engine.SupportsPenalty) {
                penaltyField = '' +
                    '<div class="inputContainer">' +
                    '  <label class="inputLabel inputLabelUnfocused">Split penalty (' + engine.MinPenalty + ' to ' + engine.MaxPenalty + ')</label>' +
                    '  <input is="emby-input" type="number" class="lapseSettingPenalty" min="' + engine.MinPenalty + '" max="' + engine.MaxPenalty + '" value="' + engine.Penalty + '" />' +
                    '</div>';
            }

            return '' +
                '<div class="lapseEngineSettingBlock" data-id="' + escapeHtml(engine.Id) + '">' +
                '  <div class="lapseEngineName lapseEngineSettingName">' + escapeHtml(engine.DisplayName) + '</div>' +
                penaltyField +
                '  <div class="inputContainer">' +
                '    <label class="inputLabel inputLabelUnfocused">Binary path override</label>' +
                '    <div class="lapsePathRow">' +
                '      <input is="emby-input" type="text" class="lapseSettingPath" value="' + escapeHtml(engine.PathOverride || '') + '" />' +
                '      <button is="paper-icon-button-light" type="button" class="lapseBtnBrowsePath" title="Browse">' +
                '        <span class="material-icons search" aria-hidden="true"></span>' +
                '      </button>' +
                '    </div>' +
                '    <div class="fieldDescription lapseTightNote">Leave empty to use the installed copy. Point this at a binary you built yourself when there is no download for this server.</div>' +
                '  </div>' +
                '</div>';
        }).join('');

        container.querySelectorAll('.lapseEngineSettingBlock').forEach(function (block) {
            block.querySelector('.lapseBtnBrowsePath').addEventListener('click', function () {
                browseForPath(block.querySelector('.lapseSettingPath'), true, 'Select the engine binary');
            });
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

    // The context menu opens its own dialog now rather than dragging this page along, but
    // a browser tab left open on the old URL still lands here, so the deep link keeps
    // working.
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

    document.querySelector('#LapseConfigPage').addEventListener('pageshow', function (e) {
        var view = e.target;

        setUpNavigation(view);
        Dashboard.showLoadingMsg();

        // engines and settings first, the advanced dialog and result messages both need those
        Promise.all([refreshEngines(view), refreshSettings(view), refreshLibraries(view)]).then(function () {
            return Promise.all([
                refreshProviders(view),
                refreshItemList(view),
                refreshFolders(view),
                refreshQueue(view),
                refreshPlatform(view),
                refreshDiagnostics(view)
            ]);
        }).then(function () {
            Dashboard.hideLoadingMsg();
            openDeepLinkedAdvancedDialogIfNeeded(view);
        }).catch(function () {
            Dashboard.hideLoadingMsg();
        });

        startQueuePolling(view);

        view.querySelector('#lapseItemSearch').addEventListener('input', function () {
            renderItemList(view, allItems);
        });
        view.querySelector('#lapseItemLibraryFilter').addEventListener('change', function () {
            renderItemList(view, allItems);
        });
        view.querySelector('#lapseIncludeAll').addEventListener('change', function () {
            renderItemList(view, allItems);
        });
        view.querySelector('#btnInstallAllEngines').addEventListener('click', function () {
            installAllEngines(view);
        });
        view.querySelector('#btnCheckEngineUpdates').addEventListener('click', function () {
            checkEngineUpdates(view);
        });
        view.querySelector('#lapseAutoUpdateEngines').addEventListener('change', function () {
            var enabled = view.querySelector('#lapseAutoUpdateEngines').checked;
            lapsePost('Lapse/Engines/AutoUpdate?enabled=' + enabled).catch(function (err) {
                Dashboard.alert('Could not change auto-update: ' + err.message);
            });
        });
        view.querySelector('#btnSaveLibraries').addEventListener('click', function () {
            saveLibraries(view);
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
            browseForPath(view.querySelector('#lapseRefSubPath'), true, 'Select the reference subtitle');
        });
        view.querySelector('#btnBrowseInputSub').addEventListener('click', function () {
            browseForPath(view.querySelector('#lapseInputSubPath'), true, 'Select the input subtitle', function () {
                updateSubToSubOutput(view, false);
            });
        });
        view.querySelector('#lapseSubToSubNewFile').addEventListener('change', function () {
            updateSubToSubOutput(view, false);
        });
        view.querySelector('#btnSyncSubtitles').addEventListener('click', function () {
            syncSubtitles(view);
        });
        updateSubToSubOutput(view, false);
        view.querySelector('#lapseSidecarSuffix').addEventListener('input', function () {
            updateSidecarPreview(view);
        });
        view.querySelector('#lapseSyncConfidence').addEventListener('input', function () {
            view.querySelector('#lapseSyncConfidenceValue').textContent =
                view.querySelector('#lapseSyncConfidence').value;
        });
        view.querySelector('#btnSaveOutput').addEventListener('click', function () {
            saveSettings(view, 'Output settings saved.');
        });
        view.querySelector('#btnSaveTranslation').addEventListener('click', function () {
            saveSettings(view, 'Translation settings saved.');
        });
        view.querySelector('#btnSaveSettings').addEventListener('click', function () {
            saveSettings(view, 'Engine settings saved.');
        });
        view.querySelector('#btnSaveAppearance').addEventListener('click', function () {
            saveSettings(view, 'Subtitle appearance saved.');
        });
        view.querySelector('#btnResetAppearance').addEventListener('click', function () {
            resetAppearance(view);
        });

        ['#lapseAppearanceEnabled', '#lapseAppearanceFontSize', '#lapseAppearanceTextColor',
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
