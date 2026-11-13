/*
 * The colour theme, applied before the page paints so it never flashes the wrong one.
 * Loaded in <head> without defer. A person chooses System (the default), Light or Graphite; the
 * choice is kept in this browser. What's applied is always a concrete theme on <html>:
 * data-theme="light" or data-theme="graphite", and data-theme-choice holds what was chosen.
 */
(function () {
    var KEY = 'cabinet_theme';
    var media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : null;

    function stored() {
        try {
            var value = window.localStorage.getItem(KEY);
            // 'dark' was the old dark theme's name
            if (value === 'dark') return 'graphite';
            return value === 'light' || value === 'graphite' ? value : 'system';
        } catch (e) {
            return 'system';
        }
    }

    function apply(choice) {
        var root = document.documentElement;
        var effective = choice === 'system' ? (media && media.matches ? 'graphite' : 'light') : choice;
        root.setAttribute('data-theme', effective);
        root.setAttribute('data-theme-choice', choice);
        root.style.colorScheme = effective === 'graphite' ? 'dark' : 'light';
        return effective;
    }

    function set(choice) {
        if (choice !== 'light' && choice !== 'graphite') choice = 'system';
        try {
            if (choice === 'system') window.localStorage.removeItem(KEY);
            else window.localStorage.setItem(KEY, choice);
        } catch (e) {
            // Private windows can refuse storage; the theme still changes for this visit
        }
        return apply(choice);
    }

    if (media) {
        var follow = function () { if (stored() === 'system') apply('system'); };
        if (media.addEventListener) media.addEventListener('change', follow);
        else if (media.addListener) media.addListener(follow);
    }

    window.CabinetTheme = { choice: stored, set: set, apply: apply };
    apply(stored());
})();
