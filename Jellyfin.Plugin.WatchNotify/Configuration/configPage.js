const pluginId = '295982d0-fc94-4c98-9bf4-54bc7cf05b30';

// Jellyfin renders user ids with or without dashes depending on the endpoint, so
// ids are compared in a canonical form rather than literally.
function normalizeId(id) {
    return String(id || '').replace(/-/g, '').toLowerCase();
}

function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, (c) => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[c]);
}

const tabs = [
    { href: 'configurationpage?name=WatchNotify', name: 'Settings' },
    { href: 'configurationpage?name=WatchNotifyLog', name: 'Log' }
];

// Navigation is a nice-to-have, so every failure here is swallowed: an exception
// escaping this would abort the viewshow handler and leave the form unpopulated.
function setupTabs(view, selectedIndex) {
    try {
        if (window.LibraryMenu && typeof window.LibraryMenu.setTabs === 'function') {
            window.LibraryMenu.setTabs('watchnotify', selectedIndex, () => tabs);
            return;
        }
    } catch (err) {
        console.error('[WatchNotify] LibraryMenu.setTabs failed', err);
    }

    const container = view.querySelector('.content-primary');
    if (!container || container.querySelector('.watchNotifyTabFallback')) {
        return;
    }

    const bar = document.createElement('div');
    bar.className = 'watchNotifyTabFallback';
    bar.style.marginBottom = '1em';
    bar.innerHTML = tabs.map((tab, index) => index === selectedIndex
        ? '<strong style="margin-right: 1em;">' + escapeHtml(tab.name) + '</strong>'
        : '<a style="margin-right: 1em;" href="' + escapeHtml(tab.href) + '">' + escapeHtml(tab.name) + '</a>').join('');
    container.insertBefore(bar, container.firstChild);
}

function numberOr(element, fallback) {
    const value = Number(element.value);
    return element.value.trim() === '' || Number.isNaN(value) ? fallback : value;
}

function renderUserList(container, users, selectedIds) {
    const selected = new Set((selectedIds || []).map(normalizeId));
    container.innerHTML = users.map((user) =>
        '<label class="checkboxContainer">'
        + '<input is="emby-checkbox" type="checkbox" class="chkWatchNotifyUser" data-userid="' + escapeHtml(user.Id) + '" />'
        + '<span>' + escapeHtml(user.Name) + '</span>'
        + '</label>').join('');

    container.querySelectorAll('.chkWatchNotifyUser').forEach((el) => {
        el.checked = selected.has(normalizeId(el.getAttribute('data-userid')));
    });
}

function readUserList(container) {
    return Array.from(container.querySelectorAll('.chkWatchNotifyUser'))
        .filter((el) => el.checked)
        .map((el) => el.getAttribute('data-userid'));
}

export default function (view) {
    const form = view.querySelector('#watchNotifyConfigForm');
    const scnUserList = view.querySelector('#ScnUserList');
    const joplinUserList = view.querySelector('#JoplinUserList');
    const warnings = view.querySelector('#watchNotifyWarnings');

    function refreshStatus() {
        return ApiClient.ajax({
            type: 'GET',
            url: ApiClient.getUrl('WatchNotify/Status'),
            dataType: 'json'
        }).then((status) => {
            const messages = (status.Warnings || []).slice();
            if (status.LastError) {
                messages.push('Last delivery error: ' + status.LastError);
            }
            if (status.QueueDepth > 0) {
                messages.push(status.QueueDepth + ' notification(s) still queued for delivery.');
            }

            warnings.innerHTML = messages.map((m) => '<div>' + escapeHtml(m) + '</div>').join('');
            warnings.classList.toggle('hide', messages.length === 0);
        }).catch(() => {
            warnings.classList.add('hide');
        });
    }

    function runTest(target) {
        Dashboard.showLoadingMsg();
        ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl('WatchNotify/Test', { target: target }),
            dataType: 'json'
        }).then((result) => {
            Dashboard.hideLoadingMsg();
            Dashboard.alert({ title: 'WatchNotify', message: result.Message });
            refreshStatus();
        }).catch(() => {
            Dashboard.hideLoadingMsg();
            Dashboard.alert({ title: 'WatchNotify', message: 'The test request failed.' });
        });
    }

    function syncUserListVisibility() {
        scnUserList.classList.toggle('hide', view.querySelector('#ScnAllUsers').checked);
        joplinUserList.classList.toggle('hide', view.querySelector('#JoplinAllUsers').checked);
    }

    function load() {
        Dashboard.showLoadingMsg();
        Promise.all([ApiClient.getPluginConfiguration(pluginId), ApiClient.getUsers()])
            .then(([config, users]) => {
                view.querySelector('#DisplayTimeZone').value = config.DisplayTimeZone;
                view.querySelector('#WatchedThreshold').value = config.WatchedThreshold;
                view.querySelector('#DedupMinutes').value = config.DedupMinutes;
                view.querySelector('#StartCoalesceMinutes').value = config.StartCoalesceMinutes;
                view.querySelector('#RetrySeconds').value = config.RetrySeconds;
                view.querySelector('#NotifyOnManualMarkWatched').checked = config.NotifyOnManualMarkWatched;

                view.querySelector('#ScnEnabled').checked = config.ScnEnabled;
                view.querySelector('#ScnBaseUrl').value = config.ScnBaseUrl;
                view.querySelector('#ScnUserId').value = config.ScnUserId;
                view.querySelector('#ScnKey').value = config.ScnKey;
                view.querySelector('#ScnChannel').value = config.ScnChannel;
                view.querySelector('#ScnPriority').value = config.ScnPriority;
                view.querySelector('#ScnAllUsers').checked = config.ScnAllUsers;
                renderUserList(scnUserList, users, config.ScnUserIds);

                view.querySelector('#JoplinEnabled').checked = config.JoplinEnabled;
                view.querySelector('#JoplinBaseUrl').value = config.JoplinBaseUrl;
                view.querySelector('#JoplinToken').value = config.JoplinToken;
                view.querySelector('#JoplinNoteId').value = config.JoplinNoteId;
                view.querySelector('#JoplinAnchor').value = config.JoplinAnchor;
                view.querySelector('#JoplinPosition').value = config.JoplinPosition;
                view.querySelector('#JoplinEmptylineGap').value = config.JoplinEmptylineGap;
                view.querySelector('#JoplinAllUsers').checked = config.JoplinAllUsers;
                renderUserList(joplinUserList, users, config.JoplinUserIds);

                view.querySelector('#EventLogSize').value = config.EventLogSize;
                view.querySelector('#WriteToActivityLog').checked = config.WriteToActivityLog;

                syncUserListVisibility();
                refreshStatus();
                Dashboard.hideLoadingMsg();
            })
            .catch((err) => {
                console.error('[WatchNotify] could not load the configuration', err);
                Dashboard.hideLoadingMsg();
            });
    }

    function save() {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then((config) => {
            config.DisplayTimeZone = view.querySelector('#DisplayTimeZone').value.trim();
            config.WatchedThreshold = numberOr(view.querySelector('#WatchedThreshold'), 0.9);
            config.DedupMinutes = numberOr(view.querySelector('#DedupMinutes'), 360);
            config.StartCoalesceMinutes = numberOr(view.querySelector('#StartCoalesceMinutes'), 10);
            config.RetrySeconds = numberOr(view.querySelector('#RetrySeconds'), 30);
            config.NotifyOnManualMarkWatched = view.querySelector('#NotifyOnManualMarkWatched').checked;

            config.ScnEnabled = view.querySelector('#ScnEnabled').checked;
            config.ScnBaseUrl = view.querySelector('#ScnBaseUrl').value.trim();
            config.ScnUserId = view.querySelector('#ScnUserId').value.trim();
            config.ScnKey = view.querySelector('#ScnKey').value.trim();
            config.ScnChannel = view.querySelector('#ScnChannel').value.trim();
            config.ScnPriority = numberOr(view.querySelector('#ScnPriority'), 1);
            config.ScnAllUsers = view.querySelector('#ScnAllUsers').checked;
            config.ScnUserIds = readUserList(scnUserList);

            config.JoplinEnabled = view.querySelector('#JoplinEnabled').checked;
            config.JoplinBaseUrl = view.querySelector('#JoplinBaseUrl').value.trim();
            config.JoplinToken = view.querySelector('#JoplinToken').value.trim();
            config.JoplinNoteId = view.querySelector('#JoplinNoteId').value.trim();
            config.JoplinAnchor = view.querySelector('#JoplinAnchor').value;
            config.JoplinPosition = view.querySelector('#JoplinPosition').value;
            config.JoplinEmptylineGap = numberOr(view.querySelector('#JoplinEmptylineGap'), 0);
            config.JoplinAllUsers = view.querySelector('#JoplinAllUsers').checked;
            config.JoplinUserIds = readUserList(joplinUserList);

            config.EventLogSize = numberOr(view.querySelector('#EventLogSize'), 500);
            config.WriteToActivityLog = view.querySelector('#WriteToActivityLog').checked;

            ApiClient.updatePluginConfiguration(pluginId, config).then((result) => {
                Dashboard.processPluginConfigurationUpdateResult(result);
                refreshStatus();
            });
        });
    }

    view.addEventListener('viewshow', function () {
        load();
        setupTabs(view, 0);
    });

    load();

    view.querySelector('#TestScn').addEventListener('click', () => runTest('scn'));
    view.querySelector('#TestJoplin').addEventListener('click', () => runTest('joplin'));

    view.querySelector('#ScnAllUsers').addEventListener('change', syncUserListVisibility);
    view.querySelector('#JoplinAllUsers').addEventListener('change', syncUserListVisibility);

    form.addEventListener('submit', (e) => {
        e.preventDefault();
        save();
        return false;
    });
}
