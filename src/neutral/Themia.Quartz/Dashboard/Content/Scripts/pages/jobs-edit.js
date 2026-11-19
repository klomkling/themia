(function () {
    'use strict';
    const pd = readPageData();
    $(function () {
        const
            $jobGroup = $('#jobGroup').val(),
            $jobName = $('#jobName').val(),
            $jobType = $('#jobType').val();

        // inits
        $('#job-properties .ui.dropdown, #header .ui.dropdown').each(function () {
            $(this).dropdown({
                allowAdditions: $(this).hasClass('allow-additions')
            });
        });

        $('#save-dropdown').dropdown('setting', 'action', 'hide');
        $('#job-properties .ui.checkbox').checkbox();

        $('#jobTypeDropdown').dropdown('set selected', $jobType);

        initDimmer();

        // event handlers
        $('#btn-delete').click(function () {
            $('#delete-dialog .confirm-item').text($jobGroup + '.' + $jobName);
            deleteItem({ name: $jobName, group: $jobGroup }, $('#job-properties'),
                pd.urlDelete, pd.urlHome);
        });

        function save(triggerNow) {
            if ($('#job-data-map .type-col .ui.dropdown.disabled').length > 0) return; // type change in progress

            $('#dimmer').dimmer('show');

            $('#job-data-map').jobDataMapPrepareForm();

            var formData = new FormData($('#form')[0]);

            $.ajax({
                type: 'POST', enctype: 'multipart/form-data', url: pd.urlSave + '?trigger=' + triggerNow,
                data: formData, processData: false, contentType: false, dataType: "json", cache: false,
                success: function (data) {
                    if (processValidationResponse(data)) {
                        document.location = pd.urlHome;
                    } else {
                        $('#dimmer').dimmer('hide');
                    }
                },
                error: function (e) {
                    $('#dimmer').dimmer('hide');
                    prependErrorMessage(e, $('#job-properties'));
                }
            });
        }

        $('#btn-save').click(function () { save(false); });
        $('#btn-trigger').click(function () { save(true); });

    });
})();
