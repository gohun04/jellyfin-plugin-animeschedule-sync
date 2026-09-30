/* Standalone page: deliberately does not depend on Jellyfin's admin-only web router. */
(function () {
    'use strict';
    const root = new URL('../', document.currentScript.src);
    const storageKey = 'AnimeSchedule:' + root.pathname + ':session';
    const q = id => document.getElementById(id);
    let token = '';
    let userId = '';
    let busy = false;
    const idleLimit = 5 * 60 * 1000;
    const sessionLimit = 30 * 60 * 1000;
    let started = 0, lastActivity = 0;
    function saveActivity() {
        try { sessionStorage.setItem(storageKey + ':times', JSON.stringify({ started, lastActivity })); } catch (_) { }
    }
    function expired() { return token && (!started || !lastActivity || Date.now() - lastActivity >= idleLimit || Date.now() - started >= sessionLimit); }
    function revoke(sessionToken) {
        if (!sessionToken) return;
        fetch(new URL('Sessions/Logout', root), { method: 'POST', headers: { Authorization: 'MediaBrowser Token="' + sessionToken.replace(/["\\]/g, '') + '"' }, keepalive: true }).catch(() => {});
    }
    function checkSession() {
        if (!expired()) return false;
        const previous = token;
        clearSession(); revoke(previous);
        message('For your privacy, you were signed out. Sign in again to manage your connection.');
        return true;
    }
    let deviceId;
    try {
        token = sessionStorage.getItem(storageKey) || '';
        const times = JSON.parse(sessionStorage.getItem(storageKey + ':times') || '{}');
        started = Number(times.started) || 0; lastActivity = Number(times.lastActivity) || 0;
        deviceId = sessionStorage.getItem(storageKey + ':device');
        if (!deviceId) { deviceId = crypto.randomUUID(); sessionStorage.setItem(storageKey + ':device', deviceId); }
    } catch (_) { deviceId = 'AnimeSchedule-' + Date.now(); }
    function message(text, error = false) { q('message').textContent = text; q('message').className = error ? 'error' : ''; }
    function clearSession() {
        token = ''; userId = '';
        try { sessionStorage.removeItem(storageKey); sessionStorage.removeItem(storageKey + ':times'); } catch (_) { /* In-memory sign-in also works. */ }
        q('account-panel').hidden = true; q('login-panel').hidden = false;
        q('name').textContent = ''; q('status').textContent = '';
        q('password').value = '';
    }
    function authorization() {
        const info = 'MediaBrowser Client="AnimeSchedule Connections", Device="Browser", DeviceId="' + deviceId + '", Version="0.5.3"';
        return token ? info + ', Token="' + token.replace(/["\\]/g, '') + '"' : info;
    }
    async function request(path, method = 'GET', body) {
        if (path !== 'Users/AuthenticateByName' && checkSession()) throw new Error('Your settings session expired. Sign in again.');
        const headers = { Authorization: authorization(), Accept: 'application/json' };
        if (body !== undefined) headers['Content-Type'] = 'application/json';
        const response = await fetch(new URL(path, root), {
            method, headers, credentials: 'same-origin', cache: 'no-store',
            body: body === undefined ? undefined : JSON.stringify(body)
        });
        if (response.status === 401) {
            clearSession();
            throw new Error(path === 'Users/AuthenticateByName' ? 'Sign-in failed. Check your Jellyfin username and password.' : 'Your Jellyfin session expired. Sign in again.');
        }
        if (response.status === 403) throw new Error('Your administrator does not allow this change. Refresh to see your current permissions.');
        if (!response.ok) {
            if (path.includes('authStart') && response.status === 400) throw new Error('Ask your administrator to finish the AnimeSchedule application settings first.');
            throw new Error('The request failed. Check your connection and try again.');
        }
        return response.status === 204 ? null : response.json();
    }
    async function load() {
        if (checkSession()) return;
        if (!token) { clearSession(); return; }
        const data = await request('AnimeSchedule/connections/me');
        if (checkSession() || !token) return;
        const user = data.users[0];
        if (!user) { clearSession(); throw new Error('Your Jellyfin user could not be found. Sign in again.'); }
        userId = user.userId;
        q('login-panel').hidden = true; q('account-panel').hidden = false;
        q('name').textContent = user.name;
        q('status').textContent = user.status;
        q('mode').value = user.mode;
        const editable = data.isAdmin || data.allowUsersToChangeSyncMode;
        q('mode').disabled = !editable; q('save').disabled = !editable;
        q('permission').textContent = editable ? 'Choose Personal, Server / Shared, or None.' : 'Your administrator controls your sync mode.';
        q('personal-status').textContent = user.personalConnected ? 'Your personal anime account is connected.' : 'No personal anime account connected.';
        q('connect').textContent = user.personalConnected ? 'Reconnect personal account' : 'Connect personal account';
        q('disconnect').hidden = !user.personalConnected;
    }
    async function action(button, work) {
        if (busy) return;
        busy = true; button.disabled = true; message('');
        try { await work(); }
        catch (error) { message(error.message || 'Could not complete the request.', true); }
        finally { busy = false; if (button.id !== 'save') button.disabled = false; else if (token) button.disabled = q('mode').disabled; }
    }
    q('login-form').addEventListener('submit', event => {
        event.preventDefault();
        action(q('login'), async () => {
            const password = q('password').value;
            q('password').value = '';
            const data = await request('Users/AuthenticateByName', 'POST', { Username: q('username').value.trim(), Pw: password });
            if (!data || !data.AccessToken) throw new Error('Jellyfin did not return a sign-in session.');
            token = data.AccessToken;
            started = lastActivity = Date.now(); saveActivity();
            try { sessionStorage.setItem(storageKey, token); } catch (_) { /* Keep token in memory. */ }
            await load();
        });
    });
    q('save').addEventListener('click', () => action(q('save'), async () => {
        try {
            await request('AnimeSchedule/connections/' + encodeURIComponent(userId) + '/mode', 'POST', { mode: q('mode').value });
            await load(); message('Sync mode saved.');
        } catch (error) { if (token) await load(); throw error; }
    }));
    q('connect').addEventListener('click', () => action(q('connect'), async () => {
        const data = await request('AnimeSchedule/connections/authStart?userId=' + encodeURIComponent(userId), 'POST');
        const destination = new URL(data.url);
        if (destination.origin !== 'https://animeschedule.net') throw new Error('Unexpected connection address.');
        window.location.assign(destination.href);
    }));
    q('disconnect').addEventListener('click', () => action(q('disconnect'), async () => {
        if (!window.confirm('Disconnect your personal anime account? Your saved progress on AnimeSchedule will remain.')) return;
        await request('AnimeSchedule/connections/disconnect?userId=' + encodeURIComponent(userId), 'POST');
        await load(); message('Personal account disconnected.');
    }));
    q('refresh').addEventListener('click', () => action(q('refresh'), load));
    q('logout').addEventListener('click', () => action(q('logout'), async () => {
        try { await request('Sessions/Logout', 'POST'); }
        finally { clearSession(); }
        message('Signed out.');
    }));
    // Check expiry before recording activity, including after a suspended/background tab.
    for (const eventName of ['pointerdown', 'keydown', 'touchstart']) {
        document.addEventListener(eventName, event => {
            if (!event.isTrusted || checkSession() || !token) return;
            lastActivity = Date.now(); saveActivity();
        }, { passive: true });
    }
    setInterval(checkSession, 1000);
    document.addEventListener('visibilitychange', checkSession);
    window.addEventListener('focus', checkSession);
    // Reload after OAuth/back navigation rather than showing a stale account state.
    window.addEventListener('pageshow', () => { if (!busy) load().catch(error => message(error.message, true)); });
})();
