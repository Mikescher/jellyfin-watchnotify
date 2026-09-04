const refreshIntervalMs = 10000;

const kinds = [
    'playback-start', 'playback-stop', 'watched', 'not-watched', 'dedup-skipped',
    'scn-sent', 'scn-failed', 'joplin-appended', 'joplin-failed',
    'item-added', 'config-changed'
];

function escapeHtml(s) {
    return String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[c]);
}

function formatTimestamp(value) {
    const date = new Date(value);
    return isNaN(date.getTime()) ? String(value) : date.toLocaleString();
}

// jellyfin-web exposes LibraryMenu globally for plugin pages; without it the
// page still works, so fall back to a plain link bar rather than failing.
function setupTabs(view, selectedIndex) {
    const tabs = [
        { href: Dashboard.getConfigurationPageUrl('WatchNotify'), name: 'Settings' },
        { href: Dashboard.getConfigurationPageUrl('WatchNotifyLog'), name: 'Log' }
    ];

    if (window.LibraryMenu && typeof window.LibraryMenu.setTabs === 'function') {
        window.LibraryMenu.setTabs('watchnotify', selectedIndex, () => tabs);
        return;
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

export default function (view) {
    const rows = view.querySelector('#LogRows');
    const empty = view.querySelector('#LogEmpty');
    const kindFilter = view.querySelector('#KindFilter');
    let timer = null;

    kindFilter.innerHTML = '<option value="">All kinds</option>'
        + kinds.map((k) => '<option value="' + k + '">' + k + '</option>').join('');

    function render(entries) {
        rows.innerHTML = entries.map((entry) => {
            const color = entry.Success ? '' : ' style="color: #cc3333;"';
            return '<tr class="detailTableBodyRow detailTableBodyRow-shaded">'
                + '<td class="detailTableBodyCell">' + escapeHtml(formatTimestamp(entry.Timestamp)) + '</td>'
                + '<td class="detailTableBodyCell"' + color + '>' + escapeHtml(entry.Kind) + '</td>'
                + '<td class="detailTableBodyCell">' + escapeHtml(entry.User) + '</td>'
                + '<td class="detailTableBodyCell">' + escapeHtml(entry.Item) + '</td>'
                + '<td class="detailTableBodyCell">' + escapeHtml(entry.Detail) + '</td>'
                + '</tr>';
        }).join('');

        empty.classList.toggle('hide', entries.length > 0);
    }

    function load() {
        const query = {};
        if (kindFilter.value) {
            query.kind = kindFilter.value;
        }

        return ApiClient.ajax({
            type: 'GET',
            url: ApiClient.getUrl('WatchNotify/Events', query),
            dataType: 'json'
        }).then(render).catch(() => render([]));
    }

    function stopTimer() {
        if (timer !== null) {
            clearInterval(timer);
            timer = null;
        }
    }

    function syncTimer() {
        stopTimer();
        if (view.querySelector('#AutoRefresh').checked) {
            timer = setInterval(load, refreshIntervalMs);
        }
    }

    view.addEventListener('viewshow', function () {
        setupTabs(view, 1);
        load();
        syncTimer();
    });

    view.addEventListener('viewhide', stopTimer);
    view.addEventListener('viewdestroy', stopTimer);

    view.querySelector('#AutoRefresh').addEventListener('change', syncTimer);
    kindFilter.addEventListener('change', load);
    view.querySelector('#RefreshLog').addEventListener('click', load);

    view.querySelector('#ClearLog').addEventListener('click', function () {
        ApiClient.ajax({
            type: 'DELETE',
            url: ApiClient.getUrl('WatchNotify/Events')
        }).then(load);
    });
}
