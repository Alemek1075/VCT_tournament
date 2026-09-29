// F2: every <select data-autocomplete="/api/lookup/..."> becomes a Select2 box that asks the server
// after 3 typed characters and a short pause. The <select> keeps only the chosen option, so the
// page never ships the full list (the ranked ladder has a million rows).
$(function () {
    const esc = s => $('<div>').text(s ?? '').html();

    $('select[data-autocomplete]').each(function () {
        const $el = $(this);
        $el.select2({
            width: '100%',
            allowClear: !$el.prop('required'),
            placeholder: $el.data('placeholder') || 'Type 3+ letters…',
            minimumInputLength: 3,
            ajax: {
                url: $el.data('autocomplete'),
                delay: 300,           // wait for the user to stop typing
                dataType: 'json',
                cache: true,
                data: p => ({ term: p.term, page: p.page || 1 }),
            },
            templateResult: item => {
                if (item.loading) return 'Searching…';
                if (!item.id) return item.text;
                return $(`<div class="ac-item">
                    ${item.image ? `<img src="${esc(item.image)}" alt="">` : ''}
                    <div><div>${esc(item.text)}</div>${item.sub ? `<small>${esc(item.sub)}</small>` : ''}</div>
                </div>`);
            },
            templateSelection: item => item.text,
            language: {
                inputTooShort: a => `Type ${a.minimum - a.input.length} more character${a.minimum - a.input.length === 1 ? '' : 's'}`,
                noResults: () => 'Nothing found',
            },
        });
    });
});
