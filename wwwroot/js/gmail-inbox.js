var gmCurrent = null;

// ── Toast notification ────────────────────────────────────────────
function gmToast(message, type) {
    var existing = document.getElementById('gmToastEl');
    if (existing) { clearTimeout(existing._timer); existing.remove(); }

    var colors = {
        success: { border: '#4BB543', icon: '✓' },
        error: { border: '#EA4335', icon: '✕' },
        info: { border: '#4285F4', icon: 'i' }
    };
    var c = colors[type || 'info'];

    var toast = document.createElement('div');
    toast.id = 'gmToastEl';
    toast.style.cssText = 'position:fixed;bottom:28px;right:80px;z-index:9999;display:flex;align-items:center;gap:10px;padding:12px 18px;background:#1a2332;border:1px solid ' + c.border + ';border-left:3px solid ' + c.border + ';border-radius:10px;box-shadow:0 8px 28px rgba(0,0,0,.25);font-family:DM Sans,sans-serif;font-size:13px;color:#fff;min-width:240px;max-width:380px;animation:gmToastIn .2s ease;';

    var icon = document.createElement('div');
    icon.style.cssText = 'width:20px;height:20px;border-radius:50%;background:' + c.border + ';display:flex;align-items:center;justify-content:center;font-size:11px;font-weight:700;color:#fff;flex-shrink:0;';
    icon.textContent = c.icon;

    var txt = document.createElement('span');
    txt.style.cssText = 'flex:1;line-height:1.4;';
    txt.textContent = message;

    var cls = document.createElement('button');
    cls.style.cssText = 'background:none;border:none;cursor:pointer;color:rgba(255,255,255,.5);font-size:18px;padding:0;line-height:1;flex-shrink:0;';
    cls.innerHTML = '&times;';
    cls.onclick = function () { _dismissToast(toast); };

    toast.appendChild(icon);
    toast.appendChild(txt);
    toast.appendChild(cls);
    document.body.appendChild(toast);

    if (!document.getElementById('gmToastStyle')) {
        var s = document.createElement('style');
        s.id = 'gmToastStyle';
        s.textContent = '@keyframes gmToastIn{from{opacity:0;transform:translateY(12px)}to{opacity:1;transform:translateY(0)}}';
        document.head.appendChild(s);
    }

    toast._timer = setTimeout(function () { _dismissToast(toast); }, 3500);
}

function _dismissToast(toast) {
    if (!toast || !toast.parentNode) return;
    clearTimeout(toast._timer);
    toast.style.transition = 'opacity .2s,transform .2s';
    toast.style.opacity = '0';
    toast.style.transform = 'translateY(8px)';
    setTimeout(function () { if (toast.parentNode) toast.remove(); }, 200);
}
// ─────────────────────────────────────────────────────────────────

function openEmailFromRow(btn) {
    openEmail(
        btn.dataset.msgid,
        btn.dataset.threadid,
        btn.dataset.from || '',
        btn.dataset.to || '',
        btn.dataset.subject || '',
        btn.dataset.date || ''
    );
}

function filterEmails(q, btn) {
    if (btn) {
        document.querySelectorAll('.gm-nav-btn').forEach(function (b) { b.classList.remove('active'); });
        btn.classList.add('active');
    }
    window.location.href = '/Gmail/Inbox?q=' + encodeURIComponent(q || 'in:inbox');
}

function openEmail(msgId, threadId, from, to, subject, date) {
    document.querySelectorAll('.gm-row').forEach(function (r) { r.classList.remove('active'); });
    var row = document.getElementById('row-' + msgId);
    if (row) row.classList.add('active');

    gmCurrent = { msgId: msgId, threadId: threadId, from: from, subject: subject };

    document.getElementById('gmReadEmpty').style.display = 'none';
    document.getElementById('gmReadCard').classList.add('open');
    document.getElementById('gmSubject').textContent = subject || '(no subject)';
    document.getElementById('gmFrom').textContent = from || '';
    document.getElementById('gmTo').textContent = to || '';
    document.getElementById('gmDate').textContent = date || '';
    document.getElementById('gmBody').innerHTML = '<div class="gm-read-loading">Loading\u2026</div>';
    document.getElementById('gmReplyArea').style.display = 'none';
    document.getElementById('gmReplyText').value = '';
    document.getElementById('gmReplySend').disabled = true;

    fetch('/Gmail/Read/' + encodeURIComponent(msgId), {
        headers: { 'X-Requested-With': 'XMLHttpRequest' }
    })
        .then(function (r) {
            if (!r.ok) throw new Error('Server error ' + r.status);
            return r.json();
        })
        .then(function (data) {
            var bodyEl = document.getElementById('gmBody');
            if (data.from) document.getElementById('gmFrom').textContent = data.from;
            if (data.to) document.getElementById('gmTo').textContent = data.to;
            if (data.date) document.getElementById('gmDate').textContent = data.date;
            if (data.subject) document.getElementById('gmSubject').textContent = data.subject;

            if (!data.success) {
                bodyEl.innerHTML = '<p style="color:#c62828;padding:12px;font-size:13px;">\u26a0\ufe0f ' + (data.message || 'Could not load email.') + '</p>';
                return;
            }
            var html = (data.body || '').trim();
            if (!html) {
                bodyEl.innerHTML = '<p style="color:#8a97a8;padding:12px;font-size:13px;">(No message body)</p>';
                return;
            }
            bodyEl.innerHTML = '';
            var iframe = document.createElement('iframe');
            iframe.setAttribute('sandbox', 'allow-same-origin allow-popups allow-scripts');
            iframe.style.cssText = 'width:100%;border:none;min-height:200px;display:block;';
            bodyEl.appendChild(iframe);
            try {
                var doc = iframe.contentDocument || iframe.contentWindow.document;
                doc.open();
                doc.write('<!DOCTYPE html><html><head><style>body{margin:0;padding:12px;font-family:Arial,sans-serif;font-size:14px;word-wrap:break-word;line-height:1.5;}img{max-width:100%;}</style></head><body>' + html + '</body></html>');
                doc.close();
                iframe.onload = function () {
                    try {
                        var h = doc.documentElement.scrollHeight || doc.body.scrollHeight;
                        iframe.style.height = Math.max(h + 16, 200) + 'px';
                    } catch (e) { iframe.style.height = '400px'; }
                };
            } catch (e) {
                iframe.srcdoc = html;
                iframe.style.height = '400px';
            }
            setTimeout(function () {
                if (!iframe.style.height || parseInt(iframe.style.height) < 50)
                    iframe.style.height = '400px';
            }, 2000);
        })
        .catch(function (err) {
            document.getElementById('gmBody').innerHTML = '<p style="color:#c62828;padding:12px;font-size:13px;">\u26a0\ufe0f ' + err.message + '</p>';
        });
}

function closeEmail() {
    document.getElementById('gmReadCard').classList.remove('open');
    document.getElementById('gmReadEmpty').style.display = '';
    document.querySelectorAll('.gm-row').forEach(function (r) { r.classList.remove('active'); });
    gmCurrent = null;
}

function toggleReply() {
    var area = document.getElementById('gmReplyArea');
    var isHidden = area.style.display === 'none' || area.style.display === '';
    area.style.display = isHidden ? 'block' : 'none';
    if (isHidden) setTimeout(function () { document.getElementById('gmReplyText').focus(); }, 50);
}

function sendReply() {
    if (!gmCurrent) return;
    var text = document.getElementById('gmReplyText').value.trim();
    if (!text) return;
    var btn = document.getElementById('gmReplySend');
    btn.disabled = true;
    btn.textContent = 'Sending\u2026';

    fetch('/Gmail/Reply', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
            threadId: gmCurrent.threadId,
            messageId: gmCurrent.msgId,
            to: gmCurrent.from,
            subject: gmCurrent.subject,
            replyBody: '<p>' + text.replace(/\n/g, '<br>') + '</p>'
        })
    })
        .then(function (r) { return r.json(); })
        .then(function (d) {
            if (d.success) {
                document.getElementById('gmReplyArea').style.display = 'none';
                document.getElementById('gmReplyText').value = '';
                gmToast('Reply sent successfully', 'success');
            } else {
                gmToast(d.message || 'Reply failed.', 'error');
            }
            btn.disabled = false;
            btn.textContent = 'Send';
        })
        .catch(function (e) {
            gmToast('Send failed: ' + e.message, 'error');
            btn.disabled = false;
            btn.textContent = 'Send';
        });
}

function openCompose() {
    document.getElementById('gmComposeModal').classList.add('open');
    document.getElementById('cTo').focus();
}

function closeCompose() {
    document.getElementById('gmComposeModal').classList.remove('open');
    ['cTo', 'cSubject', 'cBody'].forEach(function (id) {
        document.getElementById(id).value = '';
    });
}

function sendCompose() {
    var to = document.getElementById('cTo').value.trim();
    var subject = document.getElementById('cSubject').value.trim();
    var body = document.getElementById('cBody').value.trim();
    if (!to || !subject || !body) {
        gmToast('Please fill all fields \u2014 To, Subject and Message.', 'info');
        return;
    }
    var sendBtn = document.querySelector('.gm-compose-send');
    if (sendBtn) { sendBtn.disabled = true; sendBtn.textContent = 'Sending\u2026'; }

    fetch('/Gmail/Compose', {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: new URLSearchParams({
            To: to,
            Subject: subject,
            BodyHtml: '<p>' + body.replace(/\n/g, '<br>') + '</p>'
        })
    })
        .then(function (r) {
            if (!r.ok) throw new Error('Server returned ' + r.status);
            return r.json();
        })
        .then(function (d) {
            if (d.success) {
                closeCompose();
                gmToast('Email sent to ' + to, 'success');
            } else {
                gmToast(d.message || 'Send failed.', 'error');
            }
            if (sendBtn) { sendBtn.disabled = false; sendBtn.textContent = 'Send'; }
        })
        .catch(function (e) {
            gmToast('Send failed: ' + e.message, 'error');
            if (sendBtn) { sendBtn.disabled = false; sendBtn.textContent = 'Send'; }
        });
}

function disconnectGmail() {
    // Custom confirm instead of browser confirm()
    var toast = document.createElement('div');
    toast.style.cssText = 'position:fixed;bottom:28px;right:80px;z-index:9999;display:flex;flex-direction:column;gap:10px;padding:16px 18px;background:#1a2332;border:1px solid #EA4335;border-left:3px solid #EA4335;border-radius:10px;box-shadow:0 8px 28px rgba(0,0,0,.25);font-family:DM Sans,sans-serif;font-size:13px;color:#fff;min-width:260px;';
    toast.innerHTML = '<div style="font-weight:600;margin-bottom:4px;">Disconnect Gmail?</div><div style="color:rgba(255,255,255,.6);font-size:12px;margin-bottom:10px;">You will need to reconnect to access emails.</div><div style="display:flex;gap:8px;"><button id="gmDiscYes" style="flex:1;padding:7px;background:#EA4335;border:none;border-radius:7px;color:#fff;font-size:12px;font-weight:700;cursor:pointer;font-family:DM Sans,sans-serif;">Disconnect</button><button id="gmDiscNo" style="flex:1;padding:7px;background:rgba(255,255,255,.1);border:1px solid rgba(255,255,255,.2);border-radius:7px;color:#fff;font-size:12px;font-weight:600;cursor:pointer;font-family:DM Sans,sans-serif;">Cancel</button></div>';
    document.body.appendChild(toast);

    document.getElementById('gmDiscNo').onclick = function () { toast.remove(); };
    document.getElementById('gmDiscYes').onclick = function () {
        toast.remove();
        fetch('/Gmail/Disconnect', { method: 'POST' })
            .then(function (r) { return r.json(); })
            .then(function (d) {
                if (d.success) {
                    gmToast('Gmail disconnected.', 'info');
                    setTimeout(function () { window.location.href = '/Dashboard'; }, 1200);
                } else {
                    gmToast(d.message || 'Disconnect failed.', 'error');
                }
            });
    };
}