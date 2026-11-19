(function () {
    'use strict';
    const pd = readPageData();
    initHistogramTooltips($('.histogram > .bar'));
    initDimmer();

    function doAction(content) {
        $('#dimmer').dimmer('show');

        $.ajax({
            type: 'POST', url: pd.urlAction,
            data: JSON.stringify(content),
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

    function confirmDialog(dlg, action) {
        dlg.modal({ duration: 250, onApprove: function () { doAction({ action: action }); } })
        .modal('show');
    }

    // event handlers
    $('#btn-shutdown').click(function () { confirmDialog($('#shutdown-dialog'), 'shutdown'); });
    $('#btn-pause').click(function () { doAction({ action: 'pause' }); });
    $('#btn-resume').click(function () { doAction({ action: 'resume' }); });
    $('#btn-standby').click(function () { doAction({ action: 'standby' }); });
    $('#btn-start').click(function () { doAction({ action: 'start' }); });

    $(document).on('click', '#job-groups.group-actions button, #trigger-groups.group-actions button', function () {
        doAction({ action: $(this).data('action'), name: $(this).data('name'), groups: $(this).closest('.group-actions').attr('id') });
    });
})();
