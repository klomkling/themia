(function () {
    'use strict';
    const pd = readPageData();

    initDimmer();

    // search handling

    var fGroup = undefined;
    function applyFilter() {
        const fStr = $('#search-job').val().toLowerCase();
        $('#job-list > tbody > tr').each(function () {
            const $_ = $(this), jobGroup = $_.data('job-group'), txt = $_.data('fulltext');
            if (txt.indexOf(fStr) !== -1 && (!fGroup || jobGroup === fGroup)) $_.show(); else $_.hide();
        });
    }

    var searchTimer;
    $('#search-job').on('input', function (e) {
        window.clearTimeout(searchTimer);
        searchTimer = window.setTimeout(function () {
            searchTimer = null;
            applyFilter();
        }, 100);
    });

    $('#job-group-filter').dropdown({
        onChange: function (v, t, elm) {
            if (elm.data('show-all')) fGroup = undefined; else fGroup = t;
            applyFilter();
        }
    });

    // histogram
    initHistogramTooltips($('.histogram > .bar'));

    // prepare job table
    const rowIndex = {};
    $('#job-list > tbody > tr').each(function () {
        const
            $_ = $(this),
            jobNameElm = $_.find('>.job-name'),
            jobName = jobNameElm.find('a').text(),
            jobGroup = $_.find('>.job-group').text(),
            jobType = $_.find('>.job-type').text();

        rowIndex[jobGroup + '.' + jobName] = $_;

        $_.data('fulltext', (jobGroup + '.' + jobName + ';' + jobType).toLowerCase());
        $_.data('job-group', jobGroup);

        jobNameElm.append('<button class="ui icon mini detail button"><i class="dots icon"></i></button>');
    });

    // menu handling

    function bindContextMenu(menu, row, jobName, jobGroup) {

        menu.find('>.menu-edit').attr('href', row.find('>.job-name a').attr('href'));
        menu.find('>.menu-copy').attr('href', pd.urlDuplicate + '?group=' + encodeURIComponent(jobGroup) + '&name=' + encodeURIComponent(jobName));
        menu.find('>.menu-trigger').attr('href', pd.urlTrigger + '?group=' + encodeURIComponent(jobGroup) + '&name=' + encodeURIComponent(jobName));
        menu.find('>.menu-delete').click(function () {
            $('#delete-dialog .confirm-item').text(jobGroup +'.' + jobName);
            deleteItem({ name: jobName, group: jobGroup }, $('#msg-panel'),
                pd.urlDelete, pd.urlHome);
        });

        return menu;
    }

    $('#job-list > tbody').on('click', '> tr > td > .detail.button', function () {
        const $button = $(this), $_ = $(this).closest('tr'), jobName = $_.find('>.job-name a').text(), jobGroup = $_.find('>.job-group').text();
        $_.addClass('active-menu'); // to preserve highlighted row
        $button.popup({
            transition: 'slide', position: 'bottom left', variation: 'basic', on: 'click', arrowPixelsFromEdge:0,
            onCreate: function () { $(this).addClass('context-menu').click(function () { $button.popup('hide'); }); },
            onHide: function () { $_.removeClass('active-menu'); },
            html: bindContextMenu($('#job-context-menu').clone(), $_, jobName, jobGroup)
        }).off('click').popup('show');
    });

    // lazy loading
    loadAdditionalData(rowIndex, pd.urlAdditionalData);
})();
