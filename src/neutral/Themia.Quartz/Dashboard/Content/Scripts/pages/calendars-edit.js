(function () {
    'use strict';
    const pd = readPageData();
    $(function () {
        initDimmer();

        const model = pd.model;

        initQuartzCalendarEditor.call($('#seg-root'),
            {
                model: model,
                timezones: pd.timezones,
                defaultTimezone: pd.defaultTimezone,
                cronUrl: pd.urlCron,
                dateFormat: pd.dateFormat,
                isNew: String(pd.isNew).toLowerCase() === 'true'
            }
        );

        const calName = model[0].Name;
        $('#btn-delete').click(function () {
            $('#delete-dialog .confirm-item').text(calName);
            deleteItem({ name: calName }, $('#msg-panel'),
                pd.urlDelete, pd.urlHome);
        });

        $('#btn-save').click(function () {
            const model = $('#seg-root').data('getModel')();

            $('#dimmer').dimmer('show');

            $.ajax({
                type: 'POST', url: pd.urlSave,
                data: JSON.stringify(model), contentType: 'application/json', dataType: "json", cache: false,
                success: function (data) {
                    if (processValidationResponse(data, $('#seg-root'))) {
                        document.location = pd.urlHome;
                    } else {
                        $('#dimmer').dimmer('hide');
                    }
                },
                error: function (e) {
                    $('#dimmer').dimmer('hide');
                    prependErrorMessage(e, $('#seg-root>.segment:first'));
                }
            });

        });

    });
})();
