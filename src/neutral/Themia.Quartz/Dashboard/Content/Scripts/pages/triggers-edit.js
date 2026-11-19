(function () {
    'use strict';
    const pd = readPageData();
    const $misfireInstructionsMap = JSON.parse(pd.misfireInstructionsJson);

    $(function () {
        const
            $triggerGroup = $('#triggerGroup').val(),
            $triggerName = $('#triggerName').val();

        $('#trigger-properties .ui.dropdown, #header .ui.dropdown').each(function () {
            $(this).dropdown({
                allowAdditions: $(this).hasClass('allow-additions'),
                placeholder: $(this).hasClass('allow-empty') ? 'false' : 'auto',
                fullTextSearch: $(this).hasClass('fulltext') ? 'exact' : 'false'
            });
        });

        $('#trigger-properties .ui.checkbox').checkbox();

        function misfireInstrForDropdown(triggerType) {

            const map = $misfireInstructionsMap[triggerType];
            const keys = Object.keys(map);

            const result = [];
            for (let i = 0; i < keys.length; i++) { // prepare suitable object for dropdown
                result.push({ name: map[keys[i]], value: keys[i] });
            }

            return result;
        }

        function triggerTypeChanged() {
            const checkedInput = $('#trigger-type-radios').find(':checked');
            $('.trigger-type').hide();
            $('.' + checkedInput.val() + '.trigger-type').show();
            $('#specificTriggerHeader').text(checkedInput.closest('.radio').find('label').text() + ' Trigger Properties');

            $('#misfireInstructions').dropdown('change values', misfireInstrForDropdown(checkedInput.val()));
            $('#misfireInstructions').dropdown('set selected', '0'); // reset misfire instruction to default value
        }

        $('#misfireInstructions').dropdown('setting', 'onChange', function (value) {
            $('#selectedMisfireInstruction').val(value); // value is mirrored to hidden input because dynamically changed dropdown's value is not propagated properly during form submit
        });

        $('#trigger-type-radios .radio').checkbox('setting', 'onChange', triggerTypeChanged);
        $('#trigger-type-radios input[value=' + String(pd.triggerType).toLowerCase() + ']').closest('.radio').checkbox('check');

        $('#misfireInstructions').dropdown('set selected', String(pd.misfireInstruction)); // because misfire dropdown values were changed in triggerTypeChanged
        $('#triggerPriority').dropdown('set selected', String(pd.priority)); // in case priority is out of range [1-10]

        $('#trigger-properties .ui.checkbox input[data-toggle-input]')
            .closest('.ui.checkbox')
            .checkbox('setting', 'onChange', function () {
                $('#' + $(this).data('toggle-input')).prop('disabled', this.checked); // search for input's ID specified with attribute data-toggle-input and set accessibility for that input based on checkbox state
            });

        initDimmer();

        // cron describe
        initCronLiveDescription(pd.urlCron, $('#cron-expression'), $('#cron-desc'), $('#next-cron-dates'));

        function initCalendars(elements) {
            const
                dateFmt = String(pd.dateFormat).toUpperCase(),
                timeFmt = pd.timeFormat,
                fmt = dateFmt + ' ' + timeFmt;

            elements.each(function () {

                const $cal = $(this);
                var calType = 'datetime';
                if ($cal.hasClass('time-only')) { calType = 'time'; }

                $cal.calendar({
                    type: calType,
                    ampm: false,
                    formatInput: calType === 'datetime',
                    formatter: {
                        date: function (date, settings) {
                            return moment(date).format(dateFmt);
                        },
                        time: function (date, settings) {
                            return moment(date).format(timeFmt);
                        }
                    },
                    parser: {
                        date: function (text, settings) {
                            return moment(text, fmt).toDate();
                        }
                    }
                });
            });
        }

        initCalendars($('#trigger-properties .ui.calendar'));

        $('#btn-delete').click(function () {
            $('#delete-dialog .confirm-item').text($triggerGroup + '.' + $triggerName);
            deleteItem({ name: $triggerName, group: $triggerGroup }, $('#trigger-properties'),
                pd.urlDelete, pd.urlHome);
        });

        $('#btn-save').click(function () {

            if ($('#job-data-map .type-col .ui.dropdown.disabled').length > 0) return; // type change in progress

            $('#dimmer').dimmer('show');

            $('#job-data-map').jobDataMapPrepareForm();

            const formData = new FormData($('#form')[0]);

            $.ajax({
                type: 'POST', enctype: 'multipart/form-data', url: pd.urlSave,
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
                    prependErrorMessage(e, $('#trigger-properties'));
                }
            });
        });

        $('#next-cron-dates').click(function () {

        });

    });
})();
