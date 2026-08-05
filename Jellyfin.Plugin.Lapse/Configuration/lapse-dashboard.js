// LAPSE Jellyfin Plugin
// Copyright (C) 2026 Rasmus Stisen Jensen (rs-jensen)
// Licensed under GPL v3 - see LICENSE for details

(function () {
    'use strict';

    var pluginId = '486090e1-ca92-46e1-8549-9f6bb914a1d0';
    var queuePollHandle = null;
    var allMovies = [];

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

    // --- engine download section ---

    function refreshEngineStatus(view) {
        return lapseGet('Lapse/Engine/Status').then(function (status) {
            var badge = view.querySelector('#lapseEngineBadge');
            var text = view.querySelector('#lapseEngineStatusText');
            var button = view.querySelector('#btnDownloadEngine');

            if (status.Downloaded && !status.RunCheckError) {
                badge.textContent = 'Ready';
                badge.className = 'lapseStatusPill lapseStatusPill-synced';
                text.textContent = 'Engine found at ' + status.Path + ' (' + status.OsArch + ')';
                button.classList.remove('hide');
                button.querySelector('span').textContent = 'Re-download engine';
            } else if (status.Downloaded && status.RunCheckError) {
                // file's there but it won't actually start - most likely a missing shared
                // library on this system, downloading again won't fix that by itself
                badge.textContent = 'Not working';
                badge.className = 'lapseStatusPill lapseStatusPill-failed';
                text.textContent = 'Engine is downloaded at ' + status.Path + ' but will not start: ' + status.RunCheckError;
                button.classList.remove('hide');
                button.querySelector('span').textContent = 'Re-download engine';
            } else if (!status.DownloadSupported) {
                badge.textContent = 'Not downloaded';
                badge.className = 'lapseStatusPill lapseStatusPill-pending';
                text.textContent = 'No engine found. LAPSE only publishes Linux binaries, and this server is running ' + status.OsArch + '. Build the engine yourself and set the binary path override below.';
                button.classList.add('hide');
            } else {
                badge.textContent = 'Not downloaded';
                badge.className = 'lapseStatusPill lapseStatusPill-pending';
                text.textContent = 'No engine found yet. Download it for ' + status.OsArch + '.';
                button.classList.remove('hide');
                button.querySelector('span').textContent = 'Download engine';
            }
        });
    }

    function downloadEngine(view) {
        var button = view.querySelector('#btnDownloadEngine');
        button.disabled = true;
        Dashboard.showLoadingMsg();

        lapsePost('Lapse/Engine/Download').then(function () {
            Dashboard.hideLoadingMsg();
            button.disabled = false;
            return refreshEngineStatus(view);
        }).catch(function (err) {
            Dashboard.hideLoadingMsg();
            button.disabled = false;
            Dashboard.alert('Could not download the engine: ' + err.message);
        });
    }

    // --- sync queue section ---

    function refreshQueue(view) {
        return lapseGet('Lapse/Queue').then(function (snapshot) {
            var section = view.querySelector('#lapseQueueSection');
            section.classList.toggle('hide', !snapshot.Running);

            if (snapshot.Running) {
                var pct = snapshot.Total === 0 ? 0 : Math.round((snapshot.Completed / snapshot.Total) * 100);
                view.querySelector('#lapseQueueBar').value = pct;
                view.querySelector('#lapseQueueText').textContent =
                    snapshot.Completed + ' / ' + snapshot.Total + (snapshot.CurrentItemName ? (' - syncing ' + snapshot.CurrentItemName) : '');
            } else if (queuePollHandle) {
                // job just finished, get the movie list caught up with the final statuses
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

        // hang on to the full list so the search box and the include-all checkbox can
        // both filter locally without refetching from the server
        allMovies = movies;

        // libraries that scan in unrelated video files (phone backups, personal clips,
        // whatever) tend to fill up with items that have no external subtitle at all -
        // hide those by default so the list stays useful, unless the user asks for everything
        var includeAll = view.querySelector('#lapseIncludeAll').checked;
        if (!includeAll) {
            movies = movies.filter(function (m) {
                return m.HasExternalSubtitle;
            });
        }

        var search = (view.querySelector('#lapseMovieSearch').value || '').trim().toLowerCase();
        if (search) {
            movies = movies.filter(function (m) {
                return m.Name.toLowerCase().indexOf(search) !== -1;
            });
        }

        if (movies.length === 0) {
            if (search) {
                container.innerHTML = '<div class="fieldDescription">No movies match that search.</div>';
            } else if (!includeAll) {
                container.innerHTML = '<div class="fieldDescription">No movies with an external subtitle found. Turn on "Include all" above to see every movie in the library.</div>';
            } else {
                container.innerHTML = '<div class="fieldDescription">No movies found in the library yet.</div>';
            }

            return;
        }

        var html = movies.map(function (movie) {
            var pillClass = 'lapseStatusPill-' + movie.Status.toLowerCase();
            var skipLabel = movie.Status === 'Skipped' ? 'Un-skip' : 'Skip';
            var errorLine = movie.LastError ? ('<div class="listItemBodyText secondary">' + escapeHtml(movie.LastError) + '</div>') : '';

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

        container.innerHTML = html;

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
        // the engine only takes one subtitle at a time, so if there's more than one
        // external subtitle on this movie we need to ask which one before syncing -
        // same check the injected context menu popup does
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
        lapsePost('Lapse/Sync', { ItemId: itemId, Mode: 'Standard', Penalty: 0, SubtitlePath: subtitlePath }).then(function (result) {
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

    function showSyncResultAlert(name, result) {
        if (!result.Success) {
            Dashboard.alert(name + ': sync failed - ' + result.Error);
            return;
        }

        if (result.Mode === 'Standard') {
            Dashboard.alert(name + ': synced (offset=' + result.OffsetMs + 'ms)');
        } else if (result.Mode === 'Ols') {
            Dashboard.alert(name + ': synced (slope=' + result.Slope.toFixed(4) + ', intercept=' + result.Intercept.toFixed(2) + 's)');
        } else {
            Dashboard.alert(name + ': synced (split, penalty=' + result.Penalty + ')');
        }
    }

    // --- advanced sync dialog, shared between the per-row "Advanced" button and the ---
    // --- deep link the context menu overlay opens (?movieId=...&autoAdvanced=1)     ---

    function openAdvancedDialog(view, itemId, name) {
        Promise.all([
            lapseGet('Lapse/Movies/' + itemId + '/Subtitles'),
            ApiClient.getPluginConfiguration(pluginId)
        ]).then(function (results) {
            showAdvancedDialog(view, itemId, name, results[0], results[1].DefaultPenalty);
        }).catch(function (err) {
            Dashboard.alert('Could not open advanced sync for ' + name + ': ' + err.message);
        });
    }

    function showAdvancedDialog(view, itemId, name, subtitles, defaultPenalty) {
        var overlay = document.createElement('div');
        overlay.className = 'lapseOverlay';

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

        overlay.innerHTML = '' +
            '<div class="lapseDialogCard">' +
            '  <h3>' + escapeHtml(name) + '</h3>' +
            '  <div class="selectContainer">' +
            '    <label class="selectLabel">Mode</label>' +
            '    <select is="emby-select" id="lapseAdvMode" class="emby-select-withcolor emby-select">' +
            '      <option value="Standard">Standard</option>' +
            '      <option value="Ols">Standard OLS</option>' +
            '      <option value="Split">Split</option>' +
            '    </select>' +
            '  </div>' +
            '  <div class="inputContainer hide" id="lapseAdvPenaltyContainer">' +
            '    <label class="inputLabel inputLabelUnfocused" for="lapseAdvPenalty">Penalty</label>' +
            '    <input is="emby-input" id="lapseAdvPenalty" type="number" min="1" value="' + defaultPenalty + '" />' +
            '    <div class="fieldDescription">Higher values = fewer splits. Default 6 works well for most cases. See the LAPSE dashboard page for more info.</div>' +
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
            '      <button is="emby-button" type="button" class="raised" id="lapseAdvNudgeApply" ' + (subtitles.length === 0 ? 'disabled' : '') + '><span>Shift</span></button>' +
            '    </div>' +
            '    <div class="fieldDescription">Still slightly off after a sync? Use minus to make subtitles show up earlier, plus for later. Works on .srt and .vtt.</div>' +
            '  </div>' +
            '</div>';

        document.body.appendChild(overlay);

        var modeSelect = overlay.querySelector('#lapseAdvMode');
        var penaltyContainer = overlay.querySelector('#lapseAdvPenaltyContainer');

        modeSelect.addEventListener('change', function () {
            penaltyContainer.classList.toggle('hide', modeSelect.value !== 'Split');
        });

        function selectedSubtitlePath() {
            if (subtitles.length === 0) {
                return null;
            }

            return subtitles.length > 1 ? overlay.querySelector('#lapseAdvSubtitle').value : subtitles[0].Path;
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
                lapsePost('Lapse/Shift', { ItemId: itemId, SubtitlePath: selectedSubtitlePath(), OffsetSeconds: offset }).then(function () {
                    Dashboard.hideLoadingMsg();
                    Dashboard.alert('Moved the subtitle by ' + offset + 's.');
                }).catch(function (err) {
                    Dashboard.hideLoadingMsg();
                    Dashboard.alert('Could not shift the subtitle: ' + err.message);
                });
            });
        }

        overlay.querySelector('#lapseAdvCancel').addEventListener('click', function () {
            overlay.remove();
        });

        overlay.querySelector('#lapseAdvSync').addEventListener('click', function () {
            var mode = modeSelect.value;
            var penalty = mode === 'Split' ? (parseInt(overlay.querySelector('#lapseAdvPenalty').value, 10) || defaultPenalty) : 0;
            var subtitlePath = selectedSubtitlePath();

            Dashboard.showLoadingMsg();
            lapsePost('Lapse/Sync', { ItemId: itemId, Mode: mode, Penalty: penalty, SubtitlePath: subtitlePath }).then(function (result) {
                Dashboard.hideLoadingMsg();
                overlay.remove();
                showSyncResultAlert(name, result);
                refreshMovieList(view);
            }).catch(function (err) {
                Dashboard.hideLoadingMsg();
                Dashboard.alert('Sync failed for ' + name + ': ' + err.message);
            });
        });
    }

    // --- bulk sync + folders ---

    function refreshFolders(view) {
        return lapseGet('Lapse/Folders').then(function (folders) {
            var select = view.querySelector('#lapseFolderSelect');
            select.innerHTML = folders.map(function (folder) {
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

    // --- settings ---

    function loadSettings(view) {
        return ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            view.querySelector('#lapseDefaultPenalty').value = config.DefaultPenalty;
            view.querySelector('#lapseBinaryPath').value = config.EngineBinaryPathOverride || '';
        });
    }

    function saveSettings(view) {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            config.DefaultPenalty = parseInt(view.querySelector('#lapseDefaultPenalty').value, 10) || 6;
            config.EngineBinaryPathOverride = view.querySelector('#lapseBinaryPath').value || null;

            ApiClient.updatePluginConfiguration(pluginId, config).then(function (result) {
                Dashboard.processPluginConfigurationUpdateResult(result);
                refreshEngineStatus(view);
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
                Dashboard.alert('Synced! Output written to ' + result.OutputPath);
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
        // sitting inside location.hash, not location.search - but check search too in
        // case this page ever gets loaded some other way
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

        Dashboard.showLoadingMsg();
        Promise.all([
            refreshEngineStatus(view),
            refreshMovieList(view),
            refreshFolders(view),
            loadSettings(view),
            refreshQueue(view)
        ]).then(function () {
            Dashboard.hideLoadingMsg();
            openDeepLinkedAdvancedDialogIfNeeded(view);
        }).catch(function () {
            Dashboard.hideLoadingMsg();
        });

        startQueuePolling(view);

        view.querySelector('#lapseMovieSearch').addEventListener('input', function () {
            // re-render straight from the list we already have, no server round trip
            renderMovieList(view, allMovies);
        });
        view.querySelector('#lapseIncludeAll').addEventListener('change', function () {
            renderMovieList(view, allMovies);
        });
        view.querySelector('#btnDownloadEngine').addEventListener('click', function () {
            downloadEngine(view);
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
        view.querySelector('#btnBrowseBinaryPath').addEventListener('click', function () {
            browseForPath(view.querySelector('#lapseBinaryPath'), true, 'Select the LAPSE engine binary');
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
        view.querySelector('#lapseSettingsForm').addEventListener('submit', function (e2) {
            e2.preventDefault();
            saveSettings(view);
            return false;
        });
    });

    document.querySelector('#LapseConfigPage').addEventListener('pagehide', function () {
        stopQueuePolling();
    });
})();
