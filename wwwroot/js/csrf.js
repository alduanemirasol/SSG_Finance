// ─────────────────────────────────────────────────────────────
// CSRF protection for all fetch() calls.
// Automatically attaches the anti-forgery token to every mutating
// (POST/PUT/DELETE/PATCH), same-origin request so the server's
// AutoValidateAntiforgeryToken filter accepts it. Must load in <head>
// BEFORE any other script that may call fetch().
// ─────────────────────────────────────────────────────────────
(function () {
    function getToken() {
        // Primary source: the JS-readable cookie issued by the server on page load.
        var m = document.cookie.match(/(?:^|;\s*)XSRF-TOKEN=([^;]+)/);
        if (m) return decodeURIComponent(m[1]);
        // Fallback: hidden field rendered by @Html.AntiForgeryToken().
        var input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    }

    var SAFE = /^(GET|HEAD|OPTIONS|TRACE)$/i;

    function isSameOrigin(url) {
        try {
            return new URL(url, window.location.href).origin === window.location.origin;
        } catch (e) {
            return true; // relative URLs are always same-origin
        }
    }

    var originalFetch = window.fetch.bind(window);

    window.fetch = function (input, init) {
        init = init || {};
        var method = init.method ||
            (typeof input === 'object' && input ? input.method : null) ||
            'GET';
        var url = (typeof input === 'string') ? input : (input && input.url) || '';

        if (!SAFE.test(method) && isSameOrigin(url)) {
            var headers = new Headers(
                init.headers ||
                (typeof input === 'object' && input ? input.headers : null) ||
                {}
            );
            if (!headers.has('RequestVerificationToken')) {
                headers.set('RequestVerificationToken', getToken());
            }
            init.headers = headers;
        }

        return originalFetch(input, init);
    };
})();
