(function () {
    'use strict';
    const pd = readPageData();
    initDimmer();

    // search handling

    var fGroup = undefined,
        enableEdit = String(pd.enableEdit).toLowerCase() === 'true';
    function applyFilter() {
        const fStr = $('#search-trigger').val().toLowerCase();
        $('#trigger-list > tbody > tr.job-key').hide();
        var lastJobKeyHeader = null;
        $('#trigger-list > tbody > tr').each(function () {
            const $_ = $(this), group = $_.data('trigger-group'), txt = $_.data('fulltext');
            if ($_.hasClass('job-key')) {
                lastJobKeyHeader = $_;
            } else {
                if (txt.indexOf(fStr) !== -1 && (!fGroup || group === fGroup)) { $_.show(); lastJobKeyHeader.show(); } else $_.hide();
            }
        });
    }

    var searchTimer;
    $('#search-trigger').on('input', function (e) {
        window.clearTimeout(searchTimer);
        searchTimer = window.setTimeout(function () {
            searchTimer = null;
            applyFilter();
        }, 100);
    });

    $('#trigger-group-filter').dropdown({
        onChange: function (v, t, elm) {
            if (elm.data('show-all')) fGroup = undefined; else fGroup = t;
            applyFilter();
        }
    });

    // histogram
    initHistogramTooltips($('.histogram > .bar'));

    // schedule tooltip
    $('.schedule-desc').popup({ distanceAway: -12, variation: 'very wide' });

    // prepare table
    const rowIndex = {};
    $('#trigger-list > tbody > tr').each(function () {
        const
            $_ = $(this),
            trgNameElm = $_.find('>.trigger-name'),
            trgName = trgNameElm.find('a').text(),
            trgGroup = $_.find('>.trigger-group').text();

        if (trgName) {
            rowIndex[trgGroup + '.' + trgName] = $_;

            $_.data('fulltext', (trgGroup + '.' + trgName).toLowerCase());
            $_.data('trigger-group', trgGroup);

            if (enableEdit)
                trgNameElm.append('<button class="ui icon mini detail button"><i class="dots icon"></i></button>');
        }
    });

    function doAction(url, key) {
        $('#dimmer').dimmer('show');
        $.ajax({
            type: 'POST', url: url,
            data: JSON.stringify(key),
            contentType: 'application/json', cache: false,
            success: function () {
                document.location = pd.urlHome;
            },
            error: function (e) {
                $('#dimmer').dimmer('hide');
                prependErrorMessage(e, $('#msg-panel'));
            }
        });
    }

    // menu handling

    function bindContextMenu(menu, row, triggerName, triggerGroup) {

        const btn = row.find('.detail.button');

        const mnuPause = menu.find('>.menu-pause');
        const mnuResume = menu.find('>.menu-resume');

        if (row.find('i.pause.icon').length > 0) {
            mnuPause.hide();
            mnuResume.show();
        } else {
            mnuResume.hide();
            mnuPause.show();
        }

        mnuPause.attr('href', pd.urlPause);
        mnuResume.attr('href', pd.urlResume);

        const pauseResumeFunc = function () {
            btn.popup('hide');
            doAction($(this).attr('href'), { name: triggerName, group: triggerGroup });
            return false;
        };

        mnuPause.click(pauseResumeFunc);
        mnuResume.click(pauseResumeFunc);

        menu.find('>.menu-edit').attr('href', row.find('>.trigger-name a').attr('href'));
        menu.find('>.menu-copy').attr('href', pd.urlDuplicate + '?group=' + encodeURIComponent(triggerGroup) + '&name=' + encodeURIComponent(triggerName));
        menu.find('>.menu-delete').click(function () {
            $('#delete-dialog .confirm-item').text(triggerGroup + '.' + triggerName);
            deleteItem({ name: triggerName, group: triggerGroup }, $('#msg-panel'),
                pd.urlDelete, pd.urlHome);
        });

        return menu;
    }

    $('#trigger-list > tbody').on('click', '> tr > td > .detail.button', function () {
        const $button = $(this), $_ = $(this).closest('tr'), triggerName = $_.find('>.trigger-name a').text(), triggerGroup = $_.find('>.trigger-group').text();
        $_.addClass('active-menu'); // to preserve highlighted row
        $button.popup({
            transition: 'slide', position: 'bottom left', variation: 'basic', on: 'click', arrowPixelsFromEdge: 0,
            onCreate: function () { $(this).addClass('context-menu').click(function () { $button.popup('hide'); }); },
            onHide: function () { $_.removeClass('active-menu'); },
            html: bindContextMenu($('#trigger-context-menu').clone(), $_, triggerName, triggerGroup)
        }).off('click').popup('show');
    });

    $('#trigger-list > tbody').on('click', '.btn-pause-all, .btn-resume-all', function () {
        const $_ = $(this).closest('tr'), jobName = $_.find('>.job-name a').text(), jobGroup = $_.find('>.job-group').text();
        doAction($(this).attr('href'), { name: jobName, group: jobGroup });
        return false;
    });

    // lazy loading
    loadAdditionalData(rowIndex, pd.urlAdditionalData);
})();
