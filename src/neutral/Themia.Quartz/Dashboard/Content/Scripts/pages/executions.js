(function () {
    'use strict';
    const pd = readPageData();

    initDimmer();

    $(document).on('click', '.btn-interrupt', function () {

        $('#dimmer').dimmer('show');
        const id = $(this).data('id');
        const btn = $(this);
        $.ajax({
            type: 'POST', url: pd.urlInterrupt,
            data: JSON.stringify({ id: id}),
            contentType: 'application/json', cache: false,
            success: function () {
                $('#dimmer').dimmer('hide');

                const msg = $('<div class="ui positive inline message"><p>Interruption of "'+id+'" requested successfully.</p><i class="close icon"></i></div>');
                msg.transition('fade in', '500ms')
                    .find('.close').on('click', function () { $(this).closest('.message').transition('fade'); });

                $('#msg-panel').prepend(msg);

                btn.addClass('disabled');
            },
            error: function (e) {
                $('#dimmer').dimmer('hide');
                prependErrorMessage(e, $('#msg-panel'));
            }
        });

    });
})();
