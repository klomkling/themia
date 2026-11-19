(function () {
    'use strict';
    const pd = readPageData();

    initDimmer();

    // search handling

    function applyFilter() {
        const fStr = $('#search-calendar').val().toLowerCase();
        $('#calendar-list > tbody > tr').each(function () {
            const $_ = $(this), txt = $_.data('fulltext');
            if (txt.indexOf(fStr) !== -1) $_.show(); else $_.hide();
        });
    }

    var searchTimer;
    $('#search-calendar').on('input', function (e) {
        window.clearTimeout(searchTimer);
        searchTimer = window.setTimeout(function () {
            searchTimer = null;
            applyFilter();
        }, 100);
    });

    // prepare calendar table
    $('#calendar-list > tbody > tr').each(function () {
        const
            $_ = $(this),
            nameElm = $_.find('>.cal-name').text();

        $_.data('fulltext', nameElm.toLowerCase());
    });
})();
