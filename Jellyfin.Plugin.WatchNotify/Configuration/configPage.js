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
                Dashboard.hideLoadingMsg();
            })
            .catch(() => Dashboard.hideLoadingMsg());
    }

    function save() {
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginId).then((config) => {
            config.DisplayTimeZone = view.querySelector('#DisplayTimeZone').value.trim();
            config.WatchedThreshold = parseFloat(view.querySelector('#WatchedThreshold').value);
            config.DedupMinutes = parseInt(view.querySelector('#DedupMinutes').value, 10);
            config.StartCoalesceMinutes = parseInt(view.querySelector('#StartCoalesceMinutes').value, 10);
            config.RetrySeconds = parseInt(view.querySelector('#RetrySeconds').value, 10);
            config.NotifyOnManualMarkWatched = view.querySelector('#NotifyOnManualMarkWatched').checked;

            config.ScnEnabled = view.querySelector('#ScnEnabled').checked;
            config.ScnBaseUrl = view.querySelector('#ScnBaseUrl').value.trim();
            config.ScnUserId = view.querySelector('#ScnUserId').value.trim();
            config.ScnKey = view.querySelector('#ScnKey').value.trim();
            config.ScnChannel = view.querySelector('#ScnChannel').value.trim();
            config.ScnPriority = parseInt(view.querySelector('#ScnPriority').value, 10);
            config.ScnAllUsers = view.querySelector('#ScnAllUsers').checked;
            config.ScnUserIds = readUserList(scnUserList);

            config.JoplinEnabled = view.querySelector('#JoplinEnabled').checked;
            config.JoplinBaseUrl = view.querySelector('#JoplinBaseUrl').value.trim();
            config.JoplinToken = view.querySelector('#JoplinToken').value.trim();
            config.JoplinNoteId = view.querySelector('#JoplinNoteId').value.trim();
            config.JoplinAnchor = view.querySelector('#JoplinAnchor').value;
            config.JoplinPosition = view.querySelector('#JoplinPosition').value;
            config.JoplinEmptylineGap = parseInt(view.querySelector('#JoplinEmptylineGap').value, 10);
            config.JoplinAllUsers = view.querySelector('#JoplinAllUsers').checked;
            config.JoplinUserIds = readUserList(joplinUserList);

            config.EventLogSize = parseInt(view.querySelector('#EventLogSize').value, 10);
            config.WriteToActivityLog = view.querySelector('#WriteToActivityLog').checked;

            ApiClient.updatePluginConfiguration(pluginId, config)
                .then((result) => Dashboard.processPluginConfigurationUpdateResult(result));
        });
    }

    view.addEventListener('viewshow', load);

    view.querySelector('#ScnAllUsers').addEventListener('change', syncUserListVisibility);
    view.querySelector('#JoplinAllUsers').addEventListener('change', syncUserListVisibility);

    form.addEventListener('submit', (e) => {
        e.preventDefault();
        save();
        return false;
    });
}
