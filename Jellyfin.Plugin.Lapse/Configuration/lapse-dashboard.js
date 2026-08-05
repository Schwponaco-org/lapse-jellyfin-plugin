// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

(function () {
    'use strict';

    var pluginId = '486090e1-ca92-46e1-8549-9f6bb914a1d0';
    var queuePollHandle = null;
    var allMovies = [];
    var allEngines = [];

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

    // --- remember which sections were open between visits ---

    function setUpSections(view) {
        view.querySelectorAll('.lapseSection').forEach(function (section) {
            var key = 'lapse-section-' + section.id;
            var saved = null;

            try {
                saved = window.localStorage.getItem(key);
            } catch (e) {
                // private browsing and friends, just fall back to the markup default
            }

            if (saved === 'open') {
                section.open = true;
            } else if (saved === 'closed') {
                section.open = false;
            }

            section.addEventListener('toggle', function () {
                try {
                    window.localStorage.setItem(key, section.open ? 'open' : 'closed');
                } catch (e) {
                    // nothing we can do, not worth bothering the user about
                }
            });
        });
    }

    // --- engines ---

    function engineStateClass(engine) {
        if (!engine.Installed) {
            return '';
        }

        return engine.RunCheckError ? 'lapseEngineDot-broken' : 'lapseEngineDot-ready';
    }

    function engineStateText(engine) {
        if (!engine.Installed) {
            return engine.DownloadSupported ? 'not installed' : 'unavailable';
        }

        return engine.RunCheckError ? 'not working' : 'ready';
    }

    function renderEngineDots(view) {
        view.querySelector('#lapseEngineDots').innerHTML = allEngines.map(function (engine) {
            return '<span class="lapseEngineDot ' + engineStateClass(engine) + '">' +
                escapeHtml(engine.DisplayName) + ' ' +
                '<span style="opacity:.6">' + escapeHtml(engineStateText(engine)) + '</span>' +
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

            var problem = '';
            if (!engine.DownloadSupported && !engine.Installed) {
                problem = '<div class="lapseEngineError">No build published for this server\'s architecture. Build it yourself and set a path override in Settings.</div>';
            } else if (engine.RunCheckError) {
                problem = '<div class="lapseEngineError">' + escapeHtml(engine.RunCheckError) + '</div>';
            }

            return '' +
                '<div class="' + cardClasses + '" data-id="' + escapeHtml(engine.Id) + '">' +
                '  <div class="lapseEngineCardTop">' +
                '    <span class="lapseEngineName">' + escapeHtml(engine.DisplayName) + '</span>' +
                '    <span class="lapseEngineDot ' + engineStateClass(engine) + '" style="font-size:.78em;opacity:.7">' + escapeHtml(engineStateText(engine)) + '</span>' +
                '  </div>' +
                '  <div class="lapseEngineDesc">' + escapeHtml(engine.Description) + '</div>' +
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
        });

        var installed = allEngines.filter(function (e) { return e.Installed && !e.RunCheckError; }).length;
        view.querySelector('#lapseEnginesHint').textContent = installed + ' of ' + allEngines.length + ' ready';
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

    function refreshEngines(view) {
        return lapseGet('Lapse/Engines').then(function (engines) {
            allEngines = engines;
            renderEngineDots(view);
            renderEngineCards(view);
            renderEngineSettings(view);
        });
    }

    // --- per-engine settings ---

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
                '<div class="lapseEngineSettingBlock" data-id="' + escapeHtml(engine.Id) + '" style="margin-bottom:1.2em;">' +
                '  <div class="lapseEngineName" style="margin-bottom:.3em;">' + escapeHtml(engine.DisplayName) + '</div>' +
                penaltyField +
                '  <div class="inputContainer">' +
                '    <label class="inputLabel inputLabelUnfocused">Binary path override</label>' +
                '    <div class="lapsePathRow">' +
                '      <input is="emby-input" type="text" class="lapseSettingPath" value="' + escapeHtml(engine.PathOverride || '') + '" />' +
                '      <button is="paper-icon-button-light" type="button" class="lapseBtnBrowsePath" title="Browse">' +
                '        <span class="material-icons search" aria-hidden="true"></span>' +
                '      </button>' +
                '    </div>' +
                '    <div class="fieldDescription lapseTightNote">Leave empty to use the installed copy.</div>' +
                '  </div>' +
                '</div>';
        }).join('');

        container.querySelectorAll('.lapseEngineSettingBlock').forEach(function (block) {
            block.querySelector('.lapseBtnBrowsePath').addEventListener('click', function () {
                browseForPath(block.querySelector('.lapseSettingPath'), true, 'Select the engine binary');
            });
        });
    }

    function saveSettings(view) {
        Dashboard.showLoadingMsg();

        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.Engines = config.Engines || [];

            view.querySelectorAll('.lapseEngineSettingBlock').forEach(function (block) {
                var id = block.getAttribute('data-id');
                var penaltyInput = block.querySelector('.lapseSettingPenalty');
                var pathInput = block.querySelector('.lapseSettingPath');

                var entry = null;
                for (var i = 0; i < config.Engines.length; i++) {
                    if (config.Engines[i].EngineId === id) {
                        entry = config.Engines[i];
                        break;
                    }
                }

                if (!entry) {
                    entry = { EngineId: id };
                    config.Engines.push(entry);
                }

                entry.PathOverride = (pathInput.value || '').trim() || null;
                entry.Penalty = penaltyInput ? (parseInt(penaltyInput.value, 10) || null) : null;
            });

            ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
                refreshEngines(view);
            });
        });
    }

    function browseForPath(input, includeFiles, header) {
        var picker = new Dashboard.DirectoryBrowser();
        picker.show({
            includeFiles: includeFiles,
            header: header,
            callback: function (path) {
                if (path) {
                    input.value = path;
                }

                picker.close();
            }
        });
    }

    // --- sync queue ---

    function refreshQueue(view) {
        return lapseGet('Lapse/Queue').then(function (snapshot) {
            var section = view.querySelector('#lapseQueueSection');
            section.classList.toggle('hide', !snapshot.Running);

            if (snapshot.Running) {
                var pct = snapshot.Total === 0 ? 0 : Math.round((snapshot.Completed / snapshot.Total) * 100);
                view.querySelector('#lapseQueueBar').value = pct;
                view.querySelector('#lapseQueueText').textContent =
                    'Syncing ' + snapshot.Completed + ' / ' + snapshot.Total +
                    (snapshot.CurrentItemName ? (' - ' + snapshot.CurrentItemName) : '');
            } else if (queuePollHandle) {
                refreshMovieList(view);
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

    // --- movie status list ---

    function statusLabel(status) {
        switch (status) {
            case 'Synced': return 'Synced';
            case 'Skipped': return 'Skipped';
            case 'Failed': return 'Failed';
            default: return 'Not synced';
        }
    }

    function renderMovieList(view, movies) {
        var container = view.querySelector('#lapseMovieList');

        allMovies = movies;

        // libraries that scan in unrelated video files (phone backups, personal clips,
        // whatever) fill up with items that have no external subtitle at all, so hide
        // those unless the user asks for everything
        var includeAll = view.querySelector('#lapseIncludeAll').checked;
        var shown = includeAll ? movies : movies.filter(function (m) { return m.HasExternalSubtitle; });

        var search = (view.querySelector('#lapseMovieSearch').value || '').trim().toLowerCase();
        if (search) {
            shown = shown.filter(function (m) {
                return m.Name.toLowerCase().indexOf(search) !== -1;
            });
        }

        view.querySelector('#lapseMoviesHint').textContent = shown.length + ' of ' + movies.length;

        if (shown.length === 0) {
            if (search) {
                container.innerHTML = '<div class="fieldDescription" style="padding:.8em;">No movies match that search.</div>';
            } else if (!includeAll) {
                container.innerHTML = '<div class="fieldDescription" style="padding:.8em;">No movies with an external subtitle. Turn on "Include all" to see everything.</div>';
            } else {
                container.innerHTML = '<div class="fieldDescription" style="padding:.8em;">No movies found in the library yet.</div>';
            }

            return;
        }

        container.innerHTML = shown.map(function (movie) {
            var pillClass = 'lapseStatusPill-' + movie.Status.toLowerCase();
            var skipLabel = movie.Status === 'Skipped' ? 'Un-skip' : 'Skip';
            var errorLine = movie.LastError
                ? ('<div class="listItemBodyText secondary" style="font-size:.78em;opacity:.7">' + escapeHtml(movie.LastError) + '</div>')
                : '';

            return '' +
                '<div class="listItem lapseMovieRow" data-id="' + movie.ItemId + '" data-name="' + escapeHtml(movie.Name) + '">' +
                '  <div class="listItemBody">' +
                '    <div class="listItemBodyText">' + escapeHtml(movie.Name) + '<span class="lapseStatusPill ' + pillClass + '">' + statusLabel(movie.Status) + '</span></div>' +
                errorLine +
                '  </div>' +
                '  <div class="lapseMovieRowActions">' +
                '    <button is="emby-button" type="button" class="raised lapseBtnSync">Sync</button>' +
                '    <button is="emby-button" type="button" class="raised lapseBtnAdvanced">Advanced</button>' +
                '    <button is="emby-button" type="button" class="raised lapseBtnSkip">' + skipLabel + '</button>' +
                '  </div>' +
                '</div>';
        }).join('');

        container.querySelectorAll('.lapseMovieRow').forEach(function (row) {
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
                    refreshMovieList(view);
                });
            });
        });
    }

    function refreshMovieList(view) {
        return lapseGet('Lapse/Status').then(function (movies) {
            renderMovieList(view, movies);
        });
    }

    function quickSync(view, itemId, name) {
        // the engine only takes one subtitle at a time, so ask which one if there are several
        lapseGet('Lapse/Movies/' + itemId + '/Subtitles').then(function (subtitles) {
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
            refreshMovieList(view);
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Sync failed for ' + name + ': ' + err.message);
        });
    }

    function openSubtitlePickerDialog(view, itemId, name, subtitles) {
        var overlay = document.createElement('div');
        overlay.className = 'lapseOverlay';
        overlay.innerHTML = '' +
            '<div class="lapseDialogCard">' +
            '  <h3>Pick a subtitle</h3>' +
            '  <div class="selectContainer">' +
            '    <select is="emby-select" id="lapseQuickPickerSelect" class="emby-select-withcolor emby-select">' +
            subtitles.map(function (s) { return '<option value="' + escapeHtml(s.Path) + '">' + escapeHtml(s.DisplayName) + '</option>'; }).join('') +
            '    </select>' +
            '  </div>' +
            '  <div class="lapseDialogButtons">' +
            '    <button is="emby-button" type="button" class="raised" id="lapseQuickPickerCancel"><span>Cancel</span></button>' +
            '    <button is="emby-button" type="button" class="raised button-submit" id="lapseQuickPickerSync"><span>Sync</span></button>' +
            '  </div>' +
            '</div>';

        document.body.appendChild(overlay);

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
        if (result.Mode === 'Standard' && result.OffsetMs != null) {
            return 'offset ' + result.OffsetMs + 'ms';
        }

        if (result.Mode === 'Ols' && result.Slope != null) {
            return 'slope ' + result.Slope.toFixed(4) + ', intercept ' + result.Intercept.toFixed(2) + 's';
        }

        if (result.Mode === 'Split' && result.Penalty != null) {
            return 'split, penalty ' + result.Penalty;
        }

        // engines we don't have a documented output format for just report what they said
        return result.EngineOutput || 'done';
    }

    function showSyncResultAlert(name, result) {
        if (!result.Success) {
            Dashboard.alert(name + ': sync failed - ' + result.Error);
            return;
        }

        var engine = findEngine(result.EngineId);
        var engineName = engine ? engine.DisplayName : (result.EngineId || 'engine');
        Dashboard.alert(name + ': synced with ' + engineName + ' (' + describeResult(result) + ')');
    }

    // --- advanced sync dialog ---

    function openAdvancedDialog(view, itemId, name) {
        lapseGet('Lapse/Movies/' + itemId + '/Subtitles').then(function (subtitles) {
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

    function showAdvancedDialog(view, itemId, name, subtitles) {
        var overlay = document.createElement('div');
        overlay.className = 'lapseOverlay';

        var startEngine = defaultEngine();
        if (!startEngine) {
            Dashboard.alert('No engines are available.');
            return;
        }

        var subtitlePickerHtml = '';
        if (subtitles.length === 0) {
            subtitlePickerHtml = '<p class="fieldDescription">No external subtitle found for this movie.</p>';
        } else if (subtitles.length > 1) {
            subtitlePickerHtml = '' +
                '<div class="selectContainer">' +
                '  <label class="selectLabel">Subtitle</label>' +
                '  <select is="emby-select" id="lapseAdvSubtitle" class="emby-select-withcolor emby-select">' +
                subtitles.map(function (s) { return '<option value="' + escapeHtml(s.Path) + '">' + escapeHtml(s.DisplayName) + '</option>'; }).join('') +
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

        overlay.innerHTML = '' +
            '<div class="lapseDialogCard">' +
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
            '  <hr style="margin:1.2em 0; opacity:.2;" />' +
            '  <div class="inputContainer">' +
            '    <label class="inputLabel inputLabelUnfocused" for="lapseAdvNudge">Fine tune (seconds)</label>' +
            '    <div style="display:flex; gap:.5em; align-items:center;">' +
            '      <input is="emby-input" id="lapseAdvNudge" type="number" step="0.1" value="0" style="flex-grow:1;" />' +
            '      <button is="emby-button" type="button" class="raised lapseSmallButton" id="lapseAdvNudgeApply" ' + (subtitles.length === 0 ? 'disabled' : '') + '><span>Shift</span></button>' +
            '    </div>' +
            '    <div class="fieldDescription">Still slightly off? Minus makes subtitles show up earlier, plus later. Works on .srt and .vtt, and does not use an engine.</div>' +
            '  </div>' +
            '</div>';

        document.body.appendChild(overlay);

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

        overlay.querySelector('#lapseAdvCancel').addEventListener('click', function () {
            overlay.remove();
        });

        overlay.querySelector('#lapseAdvSync').addEventListener('click', function () {
            var engine = currentEngine();
            var mode = modeSelect.value;
            var penalty = mode === 'Split' && engine.SupportsPenalty
                ? (parseInt(penaltyInput.value, 10) || engine.Penalty)
                : 0;

            Dashboard.showLoadingMsg();
            lapsePost('Lapse/Sync', {
                ItemId: itemId,
                EngineId: engine.Id,
                Mode: mode,
                Penalty: penalty,
                SubtitlePath: selectedSubtitlePath()
            }).then(function (result) {
                Dashboard.hideLoadingMsg();
                overlay.remove();
                showSyncResultAlert(name, result);
                refreshMovieList(view);
            }).catch(function (err) {
                Dashboard.hideLoadingMsg();
                Dashboard.alert('Sync failed for ' + name + ': ' + err.message);
            });
        });

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
                }).then(function () {
                    Dashboard.hideLoadingMsg();
                    Dashboard.alert('Moved the subtitle by ' + offset + 's.');
                }).catch(function (err) {
                    Dashboard.hideLoadingMsg();
                    Dashboard.alert('Could not shift the subtitle: ' + err.message);
                });
            });
        }
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
            refreshMovieList(view);
        });
    }

    // --- subtitle to subtitle sync ---

    function syncSubtitles(view) {
        var referencePath = view.querySelector('#lapseRefSubPath').value;
        var inputPath = view.querySelector('#lapseInputSubPath').value;

        if (!referencePath || !inputPath) {
            Dashboard.alert('Pick both a reference and an input subtitle first.');
            return;
        }

        Dashboard.showLoadingMsg();
        lapsePost('Lapse/SyncSubtitles', { ReferencePath: referencePath, InputPath: inputPath }).then(function (result) {
            Dashboard.hideLoadingMsg();
            if (result.Success) {
                Dashboard.alert('Synced! (' + describeResult(result) + ')');
            } else {
                Dashboard.alert('Sync failed: ' + result.Error);
            }
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            Dashboard.alert('Sync failed: ' + err.message);
        });
    }

    // --- deep link from the context menu's "Advanced" button ---

    function getDeepLinkParams() {
        // the context menu overlay loads this page through the SPA route
        // (/web/#/configurationpage?name=LAPSE&movieId=...), so the params we want are
        // inside location.hash, not location.search
        var hash = window.location.hash || '';
        var hashQueryIndex = hash.indexOf('?');
        if (hashQueryIndex !== -1) {
            return new URLSearchParams(hash.substring(hashQueryIndex + 1));
        }

        return new URLSearchParams(window.location.search || '');
    }

    function openDeepLinkedAdvancedDialogIfNeeded(view) {
        var params = getDeepLinkParams();
        var movieId = params.get('movieId');

        if (!movieId || params.get('autoAdvanced') !== '1') {
            return;
        }

        ApiClient.getItem(ApiClient.getCurrentUserId(), movieId).then(function (item) {
            openAdvancedDialog(view, movieId, item.Name);
        });
    }

    // --- wire everything up ---

    document.querySelector('#LapseConfigPage').addEventListener('pageshow', function (e) {
        var view = e.target;

        setUpSections(view);
        Dashboard.showLoadingMsg();

        // engines first, the advanced dialog and result messages both need that list
        refreshEngines(view).then(function () {
            return Promise.all([
                refreshMovieList(view),
                refreshFolders(view),
                refreshQueue(view)
            ]);
        }).then(function () {
            Dashboard.hideLoadingMsg();
            openDeepLinkedAdvancedDialogIfNeeded(view);
        }).catch(function () {
            Dashboard.hideLoadingMsg();
        });

        startQueuePolling(view);

        view.querySelector('#lapseMovieSearch').addEventListener('input', function () {
            renderMovieList(view, allMovies);
        });
        view.querySelector('#lapseIncludeAll').addEventListener('change', function () {
            renderMovieList(view, allMovies);
        });
        view.querySelector('#btnInstallAllEngines').addEventListener('click', function () {
            installAllEngines(view);
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
            browseForPath(view.querySelector('#lapseInputSubPath'), true, 'Select the input subtitle');
        });
        view.querySelector('#btnSyncSubtitles').addEventListener('click', function () {
            syncSubtitles(view);
        });
        view.querySelector('#btnSaveSettings').addEventListener('click', function () {
            saveSettings(view);
        });
    });

    document.querySelector('#LapseConfigPage').addEventListener('pagehide', function () {
        stopQueuePolling();
    });
})();
