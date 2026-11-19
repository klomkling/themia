(function () {
    'use strict';
    const pd = readPageData();
    $(function () {
        initDimmer();

        $('#btn-trigger').click(function () {

            if ($('#job-data-map .type-col .ui.dropdown.disabled').length > 0) return; // type change in progress

            $('#dimmer').dimmer('show');

            $('#job-data-map').jobDataMapPrepareForm();

            const formData = new FormData($('#form')[0]);
            const msgPanel = $('#job-data-map').parent();

            $.ajax({
                type: 'POST', enctype: 'multipart/form-data', url: pd.urlTrigger,
                data: formData, processData: false, contentType: false, dataType: "json", cache: false,
                success: function (data) {
                    $('#dimmer').dimmer('hide');

                    if (processValidationResponse(data)) {
                        const msg = $('<div class="ui positive inline message"><p>Job triggered successfully.</p><i class="close icon"></i></div>');
                        msg.transition('fade in', '500ms')
                            .find('.close').on('click', function () { $(this).closest('.message').transition('fade'); });

                        msgPanel.prepend(msg);
                    }
                },
                error: function (e) {
                    $('#dimmer').dimmer('hide');
                    prependErrorMessage(e, msgPanel);
                }
            });
        });

    });
})();
