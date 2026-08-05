/* ═══════════════════════════════════════════════════
   TrackSystem MVC  —  layout.js
   ═══════════════════════════════════════════════════ */
(function () {
    'use strict';

    var sidebar   = document.getElementById('sidebar');
    var toggleBtn = document.getElementById('toggleBtn');
    var closeBtn  = document.getElementById('closeBtn');
    var overlay   = document.getElementById('overlay');
    var themeBtn  = document.getElementById('themeBtn');
    var themeIcon = document.getElementById('themeIcon');
    var notifBtn  = document.getElementById('notifBtn');
    var notifPanel= document.getElementById('notifPanel');
    var clearBtn  = document.getElementById('clearNotif');
    var badge     = document.getElementById('notifBadge');

    function isDesktop() { return window.innerWidth > 768; }

    // ── Sidebar ──────────────────────────────────────
    function getCollapsed() { return localStorage.getItem('sc') === '1'; }
    function setCollapsed(v) {
        sidebar.classList.toggle('collapsed', v);
        document.body.classList.toggle('sidebar-collapsed', v);
        localStorage.setItem('sc', v ? '1' : '0');
    }

    if (isDesktop()) setCollapsed(getCollapsed());

    if (toggleBtn) {
        toggleBtn.addEventListener('click', function () {
            if (isDesktop()) {
                setCollapsed(!sidebar.classList.contains('collapsed'));
            } else {
                var open = sidebar.classList.toggle('open');
                overlay.classList.toggle('show', open);
            }
        });
    }

    if (closeBtn) {
        closeBtn.addEventListener('click', function () {
            sidebar.classList.remove('open');
            overlay.classList.remove('show');
        });
    }

    if (overlay) {
        overlay.addEventListener('click', function () {
            sidebar.classList.remove('open');
            overlay.classList.remove('show');
        });
    }

    document.addEventListener('keydown', function (e) {
        if (e.key === 'Escape') {
            sidebar.classList.remove('open');
            overlay.classList.remove('show');
            if (notifPanel) notifPanel.classList.remove('show');
        }
    });

    window.addEventListener('resize', function () {
        if (isDesktop()) {
            sidebar.classList.remove('open');
            overlay.classList.remove('show');
            setCollapsed(getCollapsed());
        } else {
            sidebar.classList.remove('collapsed');
            document.body.classList.remove('sidebar-collapsed');
        }
    });

    // ── Theme ────────────────────────────────────────
    function applyTheme(dark) {
        document.body.classList.toggle('theme-dark', dark);
        if (themeIcon) themeIcon.className = dark ? 'fa-solid fa-sun' : 'fa-solid fa-moon';
        localStorage.setItem('td', dark ? '1' : '0');
    }

    var saved = localStorage.getItem('td');
    var preferDark = window.matchMedia && window.matchMedia('(prefers-color-scheme:dark)').matches;
    applyTheme(saved === null ? preferDark : saved === '1');

    if (themeBtn) {
        themeBtn.addEventListener('click', function () {
            applyTheme(!document.body.classList.contains('theme-dark'));
        });
    }

    // ── Notifications ────────────────────────────────
    if (notifBtn && notifPanel) {
        notifBtn.addEventListener('click', function (e) {
            e.stopPropagation();
            notifPanel.classList.toggle('show');
            if (badge) badge.style.display = 'none';
        });
        document.addEventListener('click', function (e) {
            if (!notifPanel.contains(e.target) && e.target !== notifBtn)
                notifPanel.classList.remove('show');
        });
    }

    if (clearBtn) {
        clearBtn.addEventListener('click', function () {
            document.querySelectorAll('.notif-item').forEach(function (el) { el.remove(); });
            if (badge) badge.style.display = 'none';
        });
    }

}());
